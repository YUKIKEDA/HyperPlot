using System.Numerics;
using HyperPlot.Rendering;

namespace HyperPlot;

/// <summary>
/// One compiled layer (design §4.8, §5.5.3): the columns it reads and their versions, its M4 series over the plan's
/// pyramids, the ranges it adds to the domains, and how it records itself. One kind a geom of rows G1 and G4, and one for a
/// user's <see cref="IGeom"/>; the plan treats them all one way.
/// </summary>
internal abstract class LayerPlan
{
    /// <summary>The most elements a segment or a rect layer draws (design §5.5.3): 2^17, 4 MB of rectangle records.</summary>
    public const int MaxElements = 131_072;

    /// <summary>The most categories a color channel splits a layer into (design §4.1, row G7c).</summary>
    public const int MaxCategories = 256;

    /// <summary>
    /// The most cells of the extremes of bars over categories split by color (design §5.5.3, row G7c): 2^24, two arrays of
    /// 128 MB.
    /// </summary>
    public const long MaxCategoryCells = 1L << 24;

    private Column[] _columns = [];
    private LayerSpec _spec;

    protected LayerPlan(LayerSpec spec)
        : this(spec, RowRange.All) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="LayerPlan"/> class over a run of its columns' rows (row G7c): the series
    /// of one category.
    /// </summary>
    protected LayerPlan(LayerSpec spec, RowRange rows) => (_spec, Rows) = (spec, rows);

    /// <summary>Gets the rows the layer draws: all of them, or the run of one category (design §5.5.3, row G7c).</summary>
    public RowRange Rows { get; }

    /// <summary>Gets the layer's node: a new node of the same columns is taken without building again.</summary>
    public LayerSpec Spec => _spec;

    /// <summary>Takes a new node of the same columns: its looks, baselines, and widths (design §4.8).</summary>
    public void Take(LayerSpec spec)
    {
        var before = _spec;
        _spec = spec;
        OnSpecChanged(before, spec);
    }

    /// <summary>Gets the columns the layer reads; their versions decide whether it is extracted again.</summary>
    public ReadOnlySpan<Column> Columns => _columns;

    /// <summary>
    /// Gets or sets the layer's series color: the theme's palette at the layer's position (design §4.1), set before each
    /// recording. A stroke or a fill the spec leaves without a color takes it.
    /// </summary>
    public Color SeriesColor { get; set; }

    /// <summary>Gets or sets the plot's color scale, set before each recording; null when no layer has a color channel (row G7c).</summary>
    public ColorScale? Colors { get; set; }

    /// <summary>Gets the versions of <see cref="Columns"/> this Render read before its wait.</summary>
    public long[] Seen { get; private set; } = [];

    /// <summary>Gets the versions of <see cref="Columns"/> at the last extraction.</summary>
    public long[] Extracted { get; private set; } = [];

    /// <summary>Makes the plan of a layer: its pyramids and series join the plan's.</summary>
    public static LayerPlan Create(LayerSpec spec, PlanBuild build) =>
        SplitPlan.Splits(spec) ? new SplitPlan(spec, build) : Create(spec, build, RowRange.All);

    /// <summary>Makes the plan of a layer over a run of its columns' rows (row G7c): the series of one category.</summary>
    public static LayerPlan Create(LayerSpec spec, PlanBuild build, RowRange rows) =>
        spec switch
        {
            LineLayerSpec line => new LinePlan(line, build, rows),
            StepLayerSpec step => new StepPlan(step, build, rows),
            AreaLayerSpec area => new AreaPlan(area, build, rows),
            BandLayerSpec band => new BandPlan(band, build, rows),
            BarLayerSpec { X.Column: CategoricalColumn } bar => new CategoryBarPlan(bar, rows),
            BarLayerSpec bar => new BarPlan(bar, build, rows),
            ErrorBarLayerSpec errorBar => new ErrorBarPlan(errorBar, build, rows),
            RuleLayerSpec rule => new RulePlan(rule, rows),
            RectLayerSpec rect => new RectPlan(rect, rows),
            SegmentLayerSpec segment => new SegmentPlan(segment, rows),
            _ => new CustomPlan((CustomLayerSpec)spec),
        };

    /// <summary>
    /// Writes the channels a layer puts on one axis into <paramref name="into"/> and returns their count (design §4.2):
    /// what the axis learns from and what a time scale checks. Allocates nothing.
    /// </summary>
    public static int AxisChannels(LayerSpec layer, bool ofX, Span<ColumnRef> into)
    {
        switch (layer)
        {
            case LineLayerSpec l:
                into[0] = ofX ? l.X : l.Y;
                return 1;
            case StepLayerSpec s:
                into[0] = ofX ? s.X : s.Y;
                return 1;
            case AreaLayerSpec a:
                into[0] = ofX ? a.X : a.Y;
                return 1;
            case BarLayerSpec b:
                into[0] = ofX ? b.X : b.Y;
                return 1;
            case BandLayerSpec b:
                return Two(ofX, b.X, b.YMin, b.YMax, into);
            case ErrorBarLayerSpec e:
                return Two(ofX, e.X, e.YMin, e.YMax, into);
            case RectLayerSpec r:
                (into[0], into[1]) = ofX ? (r.XMin, r.XMax) : (r.YMin, r.YMax);
                return 2;
            case SegmentLayerSpec s:
                (into[0], into[1]) = ofX ? (s.X, s.XEnd) : (s.Y, s.YEnd);
                return 2;
            case RuleLayerSpec r:
                var channel = ofX ? r.X : r.Y;
                into[0] = channel;
                return channel.IsDefault ? 0 : 1;
            default:
                return 0;
        }

        static int Two(bool ofX, ColumnRef x, ColumnRef low, ColumnRef high, Span<ColumnRef> into)
        {
            if (ofX)
            {
                into[0] = x;
                return 1;
            }

            (into[0], into[1]) = (low, high);
            return 2;
        }
    }

    /// <summary>
    /// Throws unless a layer is one the plan compiles and its members are in range (design §4.8). Reads no data but the
    /// lengths of a segment's and a rect's columns.
    /// </summary>
    public static void Validate(DataTable data, LayerSpec layer, int index, Span<ColumnRef> buffer)
    {
        switch (layer)
        {
            case LineLayerSpec:
            case StepLayerSpec:
            case AreaLayerSpec:
            case BandLayerSpec:
            case BarLayerSpec:
            case ErrorBarLayerSpec:
            case RectLayerSpec:
            case SegmentLayerSpec:
            case CustomLayerSpec:
                break;
            case RuleLayerSpec rule:
                if (rule.X.IsDefault == rule.Y.IsDefault)
                {
                    throw new ArgumentException(
                        $"Layer {index} is a rule with {(rule.X.IsDefault ? "neither" : "both")} X and Y; a rule has exactly one."
                    );
                }

                break;
            default:
                throw new ArgumentException(
                    $"Layer {index} is a {layer.GetType().Name}; the plot compiles the layers of rows G1 and G4 and a CustomLayerSpec.",
                    nameof(layer)
                );
        }

        foreach (var ofX in (ReadOnlySpan<bool>)[true, false])
        {
            var count = AxisChannels(layer, ofX, buffer);
            for (var c = 0; c < count; c++)
            {
                CheckReference(data, buffer[c], index, ofX ? "x channel" : "y channel");

                // Categories go on x of bars only (design §4.2, row G7b).
                if (buffer[c].Column is CategoricalColumn && !(ofX && layer is BarLayerSpec))
                {
                    throw new ArgumentException(
                        $"The {(ofX ? "x" : "y")} column \"{data.GetName(buffer[c].Index)}\" of layer {index} is a CategoricalColumn; row G7b reads categories on x of bars only (a {layer.GetType().Name} here)."
                    );
                }
            }
        }

        switch (layer)
        {
            case LineLayerSpec l:
                CheckStored(data, l.Y, index, "a line's Y");
                break;
            case StepLayerSpec s:
                CheckStored(data, s.Y, index, "a step's Y");
                if ((uint)s.Mode > (uint)StepMode.Middle)
                {
                    throw new ArgumentException($"The Mode of layer {index} is {s.Mode}, not a named value.");
                }

                break;
            case AreaLayerSpec a:
                CheckStored(data, a.Y, index, "an area's Y");
                CheckFinite(a.Baseline, index, "Baseline");
                break;
            case BandLayerSpec b:
                CheckStored(data, b.YMin, index, "a band's YMin");
                CheckStored(data, b.YMax, index, "a band's YMax");
                break;
            case BarLayerSpec b:
                CheckStored(data, b.Y, index, "a bar's Y");
                CheckFinite(b.Baseline, index, "Baseline");
                if (b.X.Column is CategoricalColumn && b.Width is not null)
                {
                    throw new ArgumentException(
                        $"The Width of layer {index} is {b.Width}; bars over categories are as wide as their band, so it is null."
                    );
                }

                if (b.Width is { } w && (!double.IsFinite(w) || w <= 0))
                {
                    throw new ArgumentException($"The Width of layer {index} is {w}; a bar's width is finite and above 0.");
                }

                break;
            case ErrorBarLayerSpec e:
                CheckStored(data, e.YMin, index, "an error bar's YMin");
                CheckStored(data, e.YMax, index, "an error bar's YMax");
                if (!float.IsFinite(e.CapWidth) || e.CapWidth < 0)
                {
                    throw new ArgumentException($"The CapWidth of layer {index} is {e.CapWidth}; a cap is finite and at least 0 wide.");
                }

                break;
            case RectLayerSpec r:
                CheckCount(r.XMin.Column.Length, index);
                break;
            case SegmentLayerSpec s:
                CheckCount(s.X.Column.Length, index);
                break;
            case CustomLayerSpec c when c.Geom is null:
                throw new ArgumentNullException(nameof(layer), $"The Geom of layer {index} is null.");
            default:
                break;
        }

        ValidateColor(data, layer, index, buffer);
    }

    /// <summary>
    /// Throws unless a layer's color channels are ones row G7c draws (design §4.1): a column of the table as long as the
    /// layer's elements; categories on any geom but a user's, at most 256 of them; numbers on bars and rects only; and no
    /// constant color beside the channel that colors the same thing.
    /// </summary>
    private static void ValidateColor(DataTable data, LayerSpec layer, int index, Span<ColumnRef> buffer)
    {
        if (layer.ColorBy is null && layer.FillBy is null)
        {
            return;
        }

        if (layer is CustomLayerSpec)
        {
            throw new ArgumentException(
                $"Layer {index} is a CustomLayerSpec with a color channel; a user's geom reads its own columns and is not split (design §4.1)."
            );
        }

        var fill = layer is AreaLayerSpec or BandLayerSpec or BarLayerSpec or RectLayerSpec;
        if (layer.FillBy is not null && !fill)
        {
            throw new ArgumentException(
                $"Layer {index} is a {layer.GetType().Name} with a FillBy; only the fill geoms (area, band, bar, rect) have a fill."
            );
        }

        var count = AxisChannels(layer, ofX: true, buffer);
        var elements = (count > 0 ? buffer[0] : buffer[AxisChannels(layer, ofX: false, buffer) - 1]).Column.Length;
        foreach (var (channel, name) in (ReadOnlySpan<(ColumnRef?, string)>)[(layer.ColorBy, "ColorBy"), (layer.FillBy, "FillBy")])
        {
            if (channel is not { } reference)
            {
                continue;
            }

            CheckReference(data, reference, index, name);
            var column = reference.Column;
            if (column.Length != elements)
            {
                throw new ArgumentException(
                    $"The {name} column \"{data.GetName(reference.Index)}\" of layer {index} has {column.Length} elements and the layer {elements}; a color column is as long as the layer's."
                );
            }

            if (column is CategoricalColumn categories)
            {
                if (categories.CategoryCount > MaxCategories)
                {
                    throw new ArgumentException(
                        $"The {name} column \"{data.GetName(reference.Index)}\" of layer {index} has {categories.CategoryCount} categories; a layer is split into at most {MaxCategories} (design §4.1). Data of more categories is drawn as a density (row R3)."
                    );
                }

                if (layer is BarLayerSpec { X.Column: CategoricalColumn x } && (long)categories.CategoryCount * x.CategoryCount > MaxCategoryCells)
                {
                    throw new ArgumentException(
                        $"Layer {index} splits {x.CategoryCount} categories of x by {categories.CategoryCount} of {name}; the extremes of bars over categories split by color hold at most {MaxCategoryCells} (2^24) of them (design §5.5.3). Data of more is drawn as a density (row R3)."
                    );
                }
            }
            else if (layer is BarLayerSpec { X.Column: CategoricalColumn } && column.Length > MaxElements)
            {
                // Bars over categories colored by numbers are a bar a row (design §4.1): their rows are counted here.
                throw new InvalidOperationException(
                    $"Layer {index} has {column.Length} rows of bars over categories colored one by one by a number; at most {MaxElements} are drawn so (design §4.1). Row R3 draws more as a density."
                );
            }
            else if (layer is not (BarLayerSpec or RectLayerSpec))
            {
                throw new ArgumentException(
                    $"The {name} column \"{data.GetName(reference.Index)}\" of layer {index} is a {column.KindName}; a {layer.GetType().Name} is colored by categories only (design §4.1). Numbers color the elements of bars and rects."
                );
            }
        }

        // A constant beside the channel that colors the same thing (design §4.1).
        var conflict = layer switch
        {
            LineLayerSpec l => l.ColorBy is not null && l.Style.Color is not null ? "Style.Color and ColorBy" : null,
            StepLayerSpec s => s.ColorBy is not null && s.Style.Color is not null ? "Style.Color and ColorBy" : null,
            RuleLayerSpec r => r.ColorBy is not null && r.Style.Color is not null ? "Style.Color and ColorBy" : null,
            SegmentLayerSpec s => s.ColorBy is not null && s.Style.Color is not null ? "Style.Color and ColorBy" : null,
            ErrorBarLayerSpec e => e.ColorBy is not null && e.Style.Color is not null ? "Style.Color and ColorBy" : null,
            AreaLayerSpec a => Fill(a.Fill, a.FillBy, a.ColorBy) ?? Outline(a.Outline, a.ColorBy),
            BandLayerSpec b => Fill(b.Fill, b.FillBy, b.ColorBy) ?? Outline(b.Outline, b.ColorBy),
            BarLayerSpec b => Fill(b.Fill, b.FillBy, b.ColorBy),
            RectLayerSpec r => Fill(r.Fill, r.FillBy, r.ColorBy),
            _ => null,
        };
        if (conflict is not null)
        {
            throw new ArgumentException(
                $"Layer {index} has both {conflict}; a constant color and a channel do not color the same thing (design §4.1)."
            );
        }

        static string? Fill(Color? fill, ColumnRef? fillBy, ColumnRef? colorBy) =>
            fill is null ? null
            : fillBy is not null ? "Fill and FillBy"
            : colorBy is not null ? "Fill and ColorBy"
            : null;

        static string? Outline(StrokeSpec? outline, ColumnRef? colorBy) =>
            outline is { Color: not null } && colorBy is not null ? "Outline's color and ColorBy" : null;
    }

    /// <summary>
    /// Returns whether a layer's node, over the same kind, names other columns or another geom (design §4.8): the
    /// "data" stage.
    /// </summary>
    public static bool ChannelsDiffer(LayerSpec before, LayerSpec after, Span<ColumnRef> a, Span<ColumnRef> b)
    {
        if (before.GetType() != after.GetType())
        {
            return true;
        }

        if (before is CustomLayerSpec custom)
        {
            return !ReferenceEquals(custom.Geom, ((CustomLayerSpec)after).Geom);
        }

        // A color column splits the layer or colors its elements (design §4.8, row G7c): another one, or a split layer's
        // outline coming or going, makes its parts again.
        if (before.ColorBy != after.ColorBy || before.FillBy != after.FillBy)
        {
            return true;
        }

        if (SplitPlan.Splits(after))
        {
            var outlineChanged = (before, after) switch
            {
                (AreaLayerSpec old, AreaLayerSpec next) => (old.Outline is null) != (next.Outline is null),
                (BandLayerSpec old, BandLayerSpec next) => (old.Outline is null) != (next.Outline is null),
                _ => false,
            };
            if (outlineChanged)
            {
                return true;
            }
        }

        foreach (var ofX in (ReadOnlySpan<bool>)[true, false])
        {
            var count = AxisChannels(before, ofX, a);
            if (count != AxisChannels(after, ofX, b))
            {
                return true;
            }

            for (var i = 0; i < count; i++)
            {
                if (a[i] != b[i])
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Returns whether a change of node changes what the layer adds to the domains (design §4.8): a bar's or an area's
    /// Baseline, a bar's Width, or any new node of a user's geom.
    /// </summary>
    public static bool RangesDiffer(LayerSpec before, LayerSpec after) =>
        (before, after) switch
        {
            (AreaLayerSpec b, AreaLayerSpec a) => !b.Baseline.Equals(a.Baseline),
            (BarLayerSpec b, BarLayerSpec a) => !b.Baseline.Equals(a.Baseline) || b.Width != a.Width,

            // A new node of the same geom is how a user says its content changed: it learns again.
            (CustomLayerSpec, CustomLayerSpec) => true,
            _ => false,
        };

    /// <summary>Remembers the columns' versions before the wait.</summary>
    public void ReadVersions()
    {
        for (var i = 0; i < _columns.Length; i++)
        {
            Seen[i] = _columns[i].Version;
        }
    }

    /// <summary>Records that the layer was extracted at the versions read.</summary>
    public void MarkExtracted() => Seen.CopyTo(Extracted, 0);

    /// <summary>Returns whether a column changed between the last extraction and this Render's read.</summary>
    public bool ValuesChanged()
    {
        for (var i = 0; i < _columns.Length; i++)
        {
            if (Seen[i] != Extracted[i])
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Takes a new node of the same columns (row G7c): a split layer passes it on to its parts.</summary>
    protected virtual void OnSpecChanged(LayerSpec before, LayerSpec after) { }

    /// <summary>Waits until the statistics the layer's searches need are there, and checks its sorted columns' order.</summary>
    public virtual void WaitForIndexes(PlotWorkerPool pool) { }

    /// <summary>Starts a learning of both domains, before the layer adds its ranges on each axis.</summary>
    public virtual void BeginLearning() { }

    /// <summary>Adds the layer's ranges on one axis (design §4.2).</summary>
    public abstract void AddRanges(RangeSink sink, bool ofX, DataTable data);

    /// <summary>Records the layer into the panel (design §5.5.3).</summary>
    public abstract void Record(RecordContext context);

    /// <summary>Frees what the layer made for itself (a pyramid over its categories' extremes); the plan's pyramids stay with the plan.</summary>
    public virtual void Release() { }

    /// <summary>Returns a spec's stroke with its color, or the series color when it has none (design §4.1).</summary>
    protected LineStyle Stroke(in StrokeSpec stroke) => stroke.Resolve(SeriesColor);

    /// <summary>Gets the numeric column that colors each element of a bar or a rect layer, or null (design §4.1, row G7c).</summary>
    protected Column? ElementColors => (Spec.FillBy ?? Spec.ColorBy) is { Column: { } column and not CategoricalColumn } ? column : null;

    /// <summary>
    /// Returns a layer's columns and, after them, its column of element colors (row G7c, PR #173's review): its versions
    /// decide too whether the layer learns and records again.
    /// </summary>
    protected Column[] AndColors(params Column[] columns) => ElementColors is { } colors ? [.. columns, colors] : columns;

    /// <summary>Returns an element's color through the plot's color scale, or null when its value is NaN (design §4.1).</summary>
    protected Color? ElementColor(Column column, long index)
    {
        var value = column.DifferenceAt(index, 0);
        return double.IsNaN(value) ? null : Colors!.Of(value);
    }

    /// <summary>
    /// Returns a spec's fill, or the series color when it has none (design §4.1): at alpha 128 for an area or a band, which
    /// lie over other layers, opaque for bars and rects.
    /// </summary>
    protected Color FillOf(Color? fill, bool translucent) => fill ?? (translucent ? SeriesColor with { A = 128 } : SeriesColor);

    protected void SetColumns(params Column[] columns)
    {
        _columns = columns;
        Seen = new long[columns.Length];
        Extracted = new long[columns.Length];
        Array.Fill(Extracted, -1);
    }

    private static void CheckReference(DataTable data, ColumnRef reference, int layer, string channel)
    {
        if (reference.IsDefault)
        {
            throw new ArgumentException($"A {channel} of layer {layer} is the default reference, which names no column.");
        }

        if (!ReferenceEquals(reference.TableOrNull, data))
        {
            throw new ArgumentException($"A {channel} column of layer {layer} belongs to another table than the spec's Data.");
        }
    }

    private static void CheckStored(DataTable data, ColumnRef reference, int layer, string what)
    {
        if (reference.Column is not IPyramidColumn)
        {
            throw new ArgumentException(
                $"The column \"{data.GetName(reference.Index)}\" of layer {layer} is a {reference.Column.KindName}; {what} is a stored column."
            );
        }
    }

    private static void CheckFinite(double value, int layer, string name)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentException($"The {name} of layer {layer} is {value}; it is finite.");
        }
    }

    private static void CheckCount(long count, int layer)
    {
        if (count > MaxElements)
        {
            throw new InvalidOperationException(
                $"Layer {layer} has {count} elements; a segment or a rect layer draws at most {MaxElements} (design §5.5.3). Row R3 draws more as a density."
            );
        }
    }
}

/// <summary>What a layer's plan needs from the plan while it is made: the shared pyramids and the M4 batch.</summary>
internal sealed class PlanBuild(Func<Column, PlotPyramid> pyramid, Func<Column, PlotPyramid?> held, M4Batch batch, int capacity, PlotWorkerPool pool)
{
    /// <summary>Gets the workers that split layers find their categories' runs and gather their rows on (row G7c).</summary>
    public PlotWorkerPool Pool => pool;

    /// <summary>Returns the pyramid the plan holds over a column, or null (row G7c).</summary>
    public PlotPyramid? Held(Column column) => held(column);

    /// <summary>Adds a series of an X column over a Y column's pyramid, and returns its number and its output.</summary>
    public (int Series, M4Point[] Output) AddSeries(Column x, Column y) => AddSeries(x, y, RowRange.All);

    /// <summary>Adds a series of a run of rows (row G7c): the series of a category whose rows lie in one run.</summary>
    public (int Series, M4Point[] Output) AddSeries(Column x, Column y, RowRange rows)
    {
        var output = new M4Point[capacity];
        return (pyramid(y).AddTo(batch, x, output, rows), output);
    }
}

/// <summary>
/// The union of the ranges an axis learns (design §4.2): the columns' statistics and extra ranges (a baseline, a bar's
/// half width, a user's geom), kept as long while every end is an integer, with the names of the columns at its ends.
/// </summary>
internal sealed class RangeSink
{
    public long IntegerMin { get; private set; } = long.MaxValue;

    public long IntegerMax { get; private set; } = long.MinValue;

    public double Min { get; private set; } = double.PositiveInfinity;

    public double Max { get; private set; } = double.NegativeInfinity;

    public string MinName { get; private set; } = string.Empty;

    public string MaxName { get; private set; } = string.Empty;

    public bool Found { get; private set; }

    public bool Integer { get; private set; } = true;

    /// <summary>Gets the least baseline added, or +∞ for none.</summary>
    public double BaselineMin { get; private set; } = double.PositiveInfinity;

    /// <summary>Gets the greatest baseline added, or −∞ for none.</summary>
    public double BaselineMax { get; private set; } = double.NegativeInfinity;

    public void Reset()
    {
        (IntegerMin, IntegerMax, Min, Max) = (long.MaxValue, long.MinValue, double.PositiveInfinity, double.NegativeInfinity);
        (MinName, MaxName, Found, Integer) = (string.Empty, string.Empty, false, true);
        (BaselineMin, BaselineMax) = (double.PositiveInfinity, double.NegativeInfinity);
    }

    /// <summary>
    /// Adds an area's or a bar's baseline, held apart until the plan knows the transform: a baseline the scale does not map
    /// to a finite value (0 on log) is the panel's edge, not a domain's end (design §4.2, §5.5.3).
    /// </summary>
    public void AddBaseline(double value) => (BaselineMin, BaselineMax) = (Math.Min(BaselineMin, value), Math.Max(BaselineMax, value));

    /// <summary>Adds a column's range of values that are not NaN, or nothing when it has none; throws for an infinite end.</summary>
    public void AddColumn(Column column, string name)
    {
        if (column.TryGetRange(out var low, out var high))
        {
            if (!double.IsFinite(low.Value) || !double.IsFinite(high.Value))
            {
                throw new InvalidOperationException(
                    $"The column \"{name}\" holds {(double.IsFinite(low.Value) ? high.Value : low.Value)}, so a learned domain would have an infinite end; give the scale a domain with WithDomain."
                );
            }

            Add(low, high, name);
        }
    }

    /// <summary>Adds a range of ends.</summary>
    public void Add(ScaleEnd low, ScaleEnd high, string name)
    {
        Integer &= low.IsInteger && high.IsInteger;
        if (low.IsInteger && high.IsInteger)
        {
            (IntegerMin, IntegerMax) = (Math.Min(IntegerMin, low.Integer), Math.Max(IntegerMax, high.Integer));
        }

        (MinName, MaxName) = (low.Value < Min ? name : MinName, high.Value > Max ? name : MaxName);
        (Min, Max, Found) = (Math.Min(Min, low.Value), Math.Max(Max, high.Value), true);
    }

    /// <summary>
    /// Adds a range of doubles: a baseline, a bar's widened ends. A whole value inside the range of long counts as an
    /// integer, which it converts to exactly, so that a whole baseline keeps a long column's domain exact.
    /// </summary>
    public void Add(double low, double high, string name) => Add(End(low), End(high), name);

    // -2^63 is a long; 2^63 is not.
    private static ScaleEnd End(double value) =>
        value >= long.MinValue && value < -(double)long.MinValue && value == Math.Floor(value) ? ScaleEnd.Of((long)value) : ScaleEnd.Of(value);
}

/// <summary>
/// What every layer records with (design §4.4, §5.5.3): the list, the batch's results, the cartesian mapping of each axis,
/// the boundaries of the pixel columns, and work buffers kept from frame to frame. A layer computes along × across: x
/// pixels along the x axis and y pixels along the y axis, whichever side each runs on; the drawing methods here swap
/// them into the target's (horizontal, vertical) when the coordinate system is flipped (row G10), and map them from the
/// stretched space to the circle when it is polar (row G11): a line cut into pieces that follow the curve, and the fills
/// of a layer as polar trapezoids of one shape.
/// </summary>
internal sealed class RecordContext(PlotGuides guides, M4Batch batch, RenderList list, GeomFrame frame)
{
    private readonly double[] _one = new double[1];
    private readonly double[] _oneMapped = new double[1];
    private readonly double[][] _elements =
    [
        [],
        [],
        [],
        [],
    ];

    private double[] _values = new double[64];
    private double[] _mapped = new double[64];
    private Vector2[] _points = new Vector2[64];
    private ColumnTrapezoid[] _trapezoids = new ColumnTrapezoid[64];
    private Vector2[] _polarPoints = new Vector2[256];
    private SectorTrapezoid[] _sectors = new SectorTrapezoid[64];
    private int _pendingSectors;
    private Color _pendingFill;
    private double[] _yBoundaries = [];
    private ScaleRange _yBoundaryDomain = ScaleRange.None;
    private IScale? _yBoundaryTransform;

    public RenderList List => list;

    public M4Batch Batch => batch;

    public PlotGuides Guides => guides;

    public GeomFrame Frame => frame;

    public GeomFrame.Axis X { get; private set; }

    public GeomFrame.Axis Y { get; private set; }

    /// <summary>Gets the origin the X columns are read in: the x domain's without a transform, 0 with one (design §4.2).</summary>
    public long XOrigin { get; private set; }

    /// <summary>Gets the origin the Y columns are read in.</summary>
    public long YOrigin { get; private set; }

    /// <summary>Gets the W + 1 boundaries of the pixel columns, as differences from <see cref="XOrigin"/>.</summary>
    public double[] Boundaries { get; private set; } = [];

    public float Scale { get; private set; }

    /// <summary>Gets a value indicating whether x runs on the vertical side and y on the horizontal one (row G10).</summary>
    public bool Flipped { get; private set; }

    /// <summary>Gets the panel's cartesian transform (design §4.4): of the stretched space when polar.</summary>
    public CartesianTransform Transform { get; private set; }

    /// <summary>Gets the panel's rectangle, in whole pixels, whatever the coordinate system.</summary>
    public (int Left, int Top, int Width, int Height) Panel { get; private set; }

    /// <summary>Gets a value indicating whether the coordinate system is polar (row G11): then <see cref="X"/> and <see cref="Y"/> are of the stretched space.</summary>
    public bool Polar { get; private set; }

    /// <summary>Gets the polar transform, when <see cref="Polar"/>.</summary>
    public PolarTransform PolarMap { get; private set; }

    public void Begin(
        in GeomFrame.Axis x,
        in GeomFrame.Axis y,
        double[] boundaries,
        float scale,
        bool flipped,
        (int Left, int Top, int Width, int Height) panel,
        PolarTransform? polar
    )
    {
        (X, Y, Boundaries, Scale, Flipped, Transform) = (x, y, boundaries, scale, flipped, CartesianTransform.Create(x, y));
        (Panel, Polar, PolarMap, _pendingSectors) = (panel, polar.HasValue, polar.GetValueOrDefault(), 0);
        XOrigin = x.Transform is null ? x.Domain.Origin : 0;
        YOrigin = y.Transform is null ? y.Domain.Origin : 0;
    }

    /// <summary>Sets the panel's clip back, after a layer that may have changed it.</summary>
    public void ClipToPanel() => list.SetClip(Panel.Left, Panel.Top, Panel.Width, Panel.Height);

    /// <summary>
    /// Records the polar trapezoids of the rectangles drawn since the last flush as one shape (design §5.1): a layer's fills
    /// are one call, so that bars that touch leave no seam. Called after each layer; a layer's rectangles share its fill, and
    /// a layer that draws rectangles draws no line.
    /// </summary>
    public void Flush()
    {
        if (_pendingSectors > 0)
        {
            list.DrawSectors(_sectors.AsSpan(0, _pendingSectors), Center, _pendingFill);
            _pendingSectors = 0;
        }
    }

    /// <summary>
    /// Records a polyline of along × across points, swapped in place when flipped, with its style's DIPs scaled. The caller
    /// does not use the points afterwards: flipped, they hold the target's coordinates.
    /// </summary>
    public void DrawPolyline(Span<Vector2> points, in LineStyle style)
    {
        if (Polar)
        {
            var count = PolarMap.Polyline(points, ref _polarPoints);
            list.DrawPolyline(_polarPoints.AsSpan(0, count), guides.Scaled(style));
            return;
        }

        if (Flipped)
        {
            foreach (ref var point in points)
            {
                point = new Vector2(point.Y, point.X);
            }
        }

        list.DrawPolyline(points, guides.Scaled(style));
    }

    /// <summary>Records a rectangle between two x pixels and two y pixels, in either order; nothing when one is not finite.</summary>
    public void DrawRect(double x0, double x1, double y0, double y1, Color fill)
    {
        if (Polar)
        {
            if (PolarMap.Rect(x0, x1, y0, y1, out var sector))
            {
                Ensure(ref _sectors, _pendingSectors + 1);
                _sectors[_pendingSectors++] = sector;
                _pendingFill = fill;
            }

            return;
        }

        if (!double.IsFinite(x0) || !double.IsFinite(x1) || !double.IsFinite(y0) || !double.IsFinite(y1))
        {
            return;
        }

        var (left, top, width, height) = ((float)Math.Min(x0, x1), (float)Math.Min(y0, y1), (float)Math.Abs(x1 - x0), (float)Math.Abs(y1 - y0));
        if (Flipped)
        {
            list.DrawRect(top, left, height, width, fill);
        }
        else
        {
            list.DrawRect(left, top, width, height, fill);
        }
    }

    /// <summary>
    /// Records trapezoids whose sides are x pixels and whose edges are y pixels: horizontal ones when flipped, and one shape of
    /// polar trapezoids when polar.
    /// </summary>
    public void DrawColumns(ReadOnlySpan<ColumnTrapezoid> columns, Color fill)
    {
        if (!Polar)
        {
            list.DrawColumns(columns, fill, Flipped ? ColumnAxis.Horizontal : ColumnAxis.Vertical);
            return;
        }

        // The buffer of the rectangles is free: an area or a band draws no rectangle.
        Ensure(ref _sectors, 6 * columns.Length);
        var count = 0;
        foreach (ref readonly var column in columns)
        {
            count += PolarMap.Columns(column, _sectors.AsSpan(count));
        }

        list.DrawSectors(_sectors.AsSpan(0, count), Center, fill);
    }

    private Vector2 Center => new((float)PolarMap.CenterX, (float)PolarMap.CenterY);

    /// <summary>Returns the x pixel of a difference from <see cref="XOrigin"/>, through the transform; NaN when it does not map.</summary>
    public double PixelX(double d) => Pixel(X, d, ofX: true);

    /// <summary>Returns the y pixel of a difference from <see cref="YOrigin"/>, through the transform; NaN when it does not map.</summary>
    public double PixelY(double d) => Pixel(Y, d, ofX: false);

    /// <summary>
    /// Returns the y pixel of a baseline in data units, at the panel's edge where y is least when it does not map, or the
    /// nearest edge when it lies outside (design §5.5.3).
    /// </summary>
    public double BaselinePixel(double value)
    {
        var pixel = PixelY(value - YOrigin);
        var bottom = Y.Offset + (Y.Descending ? Y.Size : 0);
        return double.IsFinite(pixel) ? Math.Clamp(pixel, Y.Offset, Y.Offset + Y.Size) : bottom;
    }

    /// <summary>Returns the pixel of a column's element on the x axis.</summary>
    public double ElementX(Column column, long index) => PixelX(column.DifferenceAt(index, XOrigin));

    /// <summary>Returns the pixel of a column's element on the y axis.</summary>
    public double ElementY(Column column, long index) => PixelY(column.DifferenceAt(index, YOrigin));

    /// <summary>
    /// Maps all the elements of a column to pixels on one axis into one of four buffers kept from frame to frame, a chunk
    /// read at a time and the transform applied to a span (a segment's and a rect's columns, design §5.5.3).
    /// </summary>
    public ReadOnlySpan<double> ElementPixels(Column column, bool ofX, int slot)
    {
        var length = (int)column.Length;
        Ensure(ref _elements[slot], length);
        Ensure(ref _values, length);
        var values = _values.AsSpan(0, length);
        var pixels = _elements[slot].AsSpan(0, length);
        column.ReadDifferences(values, ofX ? XOrigin : YOrigin);
        MapAxis(Transform, ofX ? X : Y, values, pixels, ofX);
        return pixels;
    }

    /// <summary>
    /// Maps a series' M4 points to pixels, both coordinates at once through the transforms' spans (design §4.2), and returns
    /// them; a point whose coordinate does not map is NaN.
    /// </summary>
    public int MapSeries(ReadOnlySpan<M4Point> points, out ReadOnlySpan<double> xs, out ReadOnlySpan<double> ys)
    {
        var n = points.Length;
        Ensure(ref _values, 2 * n);
        Ensure(ref _mapped, 2 * n);
        var x = _values.AsSpan(0, n);
        var y = _values.AsSpan(n, n);
        for (var i = 0; i < n; i++)
        {
            (x[i], y[i]) = (points[i].X, points[i].Y);
        }

        MapAxis(Transform, X, x, _mapped.AsSpan(0, n), ofX: true);
        MapAxis(Transform, Y, y, _mapped.AsSpan(n, n), ofX: false);
        xs = _mapped.AsSpan(0, n);
        ys = _mapped.AsSpan(n, n);
        return n;
    }

    /// <summary>Gets a point buffer of at least a length, kept from frame to frame.</summary>
    public Span<Vector2> Points(int length)
    {
        Ensure(ref _points, length);
        return _points;
    }

    /// <summary>Gets a trapezoid buffer of at least a length.</summary>
    public Span<ColumnTrapezoid> Trapezoids(int length)
    {
        Ensure(ref _trapezoids, length);
        return _trapezoids;
    }

    /// <summary>Records a series as a polyline, broken where M4 broke it and where a point does not map (design §5.1).</summary>
    public void Polyline(ReadOnlySpan<M4Point> points, ReadOnlySpan<double> xs, ReadOnlySpan<double> ys, in LineStyle style)
    {
        var buffer = Points((2 * points.Length) + 1);
        var count = 0;
        for (var i = 0; i < points.Length; i++)
        {
            if (points[i].BreakBefore)
            {
                buffer[count++] = new Vector2(float.NaN);
            }

            buffer[count++] = new Vector2((float)xs[i], (float)ys[i]);
        }

        DrawPolyline(buffer[..count], style);
    }

    /// <summary>
    /// Returns the H + 1 boundaries of the pixel rows over the y domain, as differences from <see cref="YOrigin"/> in data
    /// units, from the bottom (design §5.5.3, a horizontal rule). Made again when the domain, the transform, or the height changes.
    /// </summary>
    public ReadOnlySpan<double> YBoundaries()
    {
        var domain = Y.Domain;
        var height = Y.Size;
        if (_yBoundaryDomain == domain && ReferenceEquals(_yBoundaryTransform, Y.Transform) && _yBoundaries.Length == height + 1)
        {
            return _yBoundaries;
        }

        if (_yBoundaries.Length != height + 1)
        {
            _yBoundaries = new double[height + 1];
        }

        for (var k = 0; k <= height; k++)
        {
            var s = domain.Lower + ((domain.Upper - domain.Lower) * k / height);
            _yBoundaries[k] = Y.Transform is null ? s : Inverse(Y.Transform, s + domain.Origin);
        }

        (_yBoundaryDomain, _yBoundaryTransform) = (domain, Y.Transform);
        return _yBoundaries;
    }

    /// <summary>
    /// Fills per pixel column of the panel the envelope of a series (design §5.5.3): the curve's x span inside the column,
    /// its values at the column's sides where it crosses them, and the least and greatest pixel of the points inside.
    /// </summary>
    public void Envelope(ReadOnlySpan<M4Point> points, ReadOnlySpan<double> xs, ReadOnlySpan<double> ys, ColumnEnvelope envelope)
    {
        PointEnvelope(points, xs, ys, envelope);
        for (var i = 0; i < points.Length; i++)
        {
            if (
                i > 0
                && !points[i].BreakBefore
                && double.IsFinite(xs[i - 1])
                && double.IsFinite(ys[i - 1])
                && double.IsFinite(xs[i])
                && double.IsFinite(ys[i])
            )
            {
                envelope.Segment(xs[i - 1], ys[i - 1], xs[i], ys[i]);
            }
        }
    }

    /// <summary>
    /// Fills per pixel column the least and greatest pixel y of the points inside (design §5.5.3). A point's column is the
    /// one M4 put it in, found from its X against the boundaries, not from its pixel, which rounding can put across a side.
    /// The columns are along the x axis: pixel rows when flipped.
    /// </summary>
    public void PointEnvelope(ReadOnlySpan<M4Point> points, ReadOnlySpan<double> xs, ReadOnlySpan<double> ys, ColumnEnvelope envelope)
    {
        envelope.Reset(X.Offset, X.Size);
        var boundaries = Boundaries;
        var width = X.Size;
        var k = 0;
        for (var i = 0; i < points.Length; i++)
        {
            // The points run in X order, so the column only moves forward.
            var x = points[i].X;
            while (k < width && x >= boundaries[k + 1])
            {
                k++;
            }

            if (x < boundaries[0] || k == width || !double.IsFinite(xs[i]) || !double.IsFinite(ys[i]))
            {
                continue;
            }

            // The boundaries run with the data; a descending x (reversed, or flipped and not) runs the pixels the other way.
            envelope.Point(X.Descending ? width - 1 - k : k, xs[i], ys[i]);
        }
    }

    private static void Ensure<T>(ref T[] buffer, int length)
    {
        if (buffer.Length < length)
        {
            Array.Resize(ref buffer, Math.Max(length, buffer.Length * 2));
        }
    }

    private double Inverse(IScale transform, double s)
    {
        _one[0] = s;
        transform.Inverse(_one, _oneMapped);
        return _oneMapped[0];
    }

    private double Pixel(in GeomFrame.Axis axis, double d, bool ofX)
    {
        if (axis.Transform is null)
        {
            return GeomFrame.Pixel(Transform, axis, d, ofX);
        }

        _one[0] = d;
        axis.Transform.Forward(_one, _oneMapped);
        return GeomFrame.Pixel(Transform, axis, _oneMapped[0] - axis.Domain.Origin, ofX);
    }

    private static void MapAxis(in CartesianTransform transform, in GeomFrame.Axis axis, Span<double> values, Span<double> pixels, bool ofX)
    {
        if (axis.Transform is not null)
        {
            axis.Transform.Forward(values, pixels);
            transform.Map(axis, ofX, pixels, pixels, axis.Domain.Origin);
            return;
        }

        transform.Map(axis, ofX, values, pixels, 0);
    }
}

/// <summary>
/// The envelope of a curve over the pixel columns of a panel (design §5.5.3): for each column, the x span the curve
/// covers in it, its values at the column's left and right sides when it crosses them, and the least and greatest y of
/// the points inside. Reused from frame to frame.
/// </summary>
internal sealed class ColumnEnvelope
{
    private double[] _from = [];
    private double[] _to = [];
    private double[] _left = [];
    private double[] _right = [];
    private double[] _low = [];
    private double[] _high = [];

    public int Offset { get; private set; }

    public int Width { get; private set; }

    /// <summary>Gets the x where the curve starts in the column, or NaN when it is not there.</summary>
    public ReadOnlySpan<double> From => _from.AsSpan(0, Width);

    /// <summary>Gets the x where the curve ends in the column.</summary>
    public ReadOnlySpan<double> To => _to.AsSpan(0, Width);

    /// <summary>Gets the curve's y at the column's left side, or NaN when it does not reach it.</summary>
    public ReadOnlySpan<double> Left => _left.AsSpan(0, Width);

    /// <summary>Gets the curve's y at the column's right side, or NaN.</summary>
    public ReadOnlySpan<double> Right => _right.AsSpan(0, Width);

    /// <summary>Gets the least y of the points inside the column, or NaN when none is.</summary>
    public ReadOnlySpan<double> Low => _low.AsSpan(0, Width);

    /// <summary>Gets the greatest y of the points inside the column, or NaN.</summary>
    public ReadOnlySpan<double> High => _high.AsSpan(0, Width);

    public void Reset(int offset, int width)
    {
        if (_from.Length < width)
        {
            (_from, _to, _left, _right, _low, _high) = (
                new double[width],
                new double[width],
                new double[width],
                new double[width],
                new double[width],
                new double[width]
            );
        }

        (Offset, Width) = (offset, width);
        foreach (var array in (ReadOnlySpan<double[]>)[_from, _to, _left, _right, _low, _high])
        {
            array.AsSpan(0, width).Fill(double.NaN);
        }
    }

    /// <summary>Adds a point inside a column, counted from the panel's left.</summary>
    public void Point(int c, double x, double y)
    {
        _low[c] = double.IsNaN(_low[c]) ? y : Math.Min(_low[c], y);
        _high[c] = double.IsNaN(_high[c]) ? y : Math.Max(_high[c], y);
        Span(c, x, x);
    }

    /// <summary>Adds the straight segment between two points, in whichever order of x.</summary>
    public void Segment(double x0, double y0, double x1, double y1)
    {
        if (x1 < x0)
        {
            (x0, y0, x1, y1) = (x1, y1, x0, y0);
        }

        var first = Math.Max((int)Math.Floor(x0), Offset);
        var last = Math.Min((int)Math.Floor(x1), Offset + Width - 1);
        for (var column = first; column <= last; column++)
        {
            var c = column - Offset;
            var from = Math.Max(column, x0);
            var to = Math.Min(column + 1, x1);
            Span(c, from, to);
            if (x0 <= column && x1 >= column && x1 > x0)
            {
                _left[c] = y0 + ((y1 - y0) * (column - x0) / (x1 - x0));
            }

            if (x0 <= column + 1 && x1 >= column + 1 && x1 > x0)
            {
                _right[c] = y0 + ((y1 - y0) * (column + 1 - x0) / (x1 - x0));
            }
        }
    }

    private void Span(int c, double from, double to)
    {
        _from[c] = double.IsNaN(_from[c]) ? from : Math.Min(_from[c], from);
        _to[c] = double.IsNaN(_to[c]) ? to : Math.Max(_to[c], to);
    }
}
