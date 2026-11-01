using HyperPlot.Rendering;

namespace HyperPlot;

/// <summary>
/// Bars or areas over a numeric x stacked by the groups of a color column (design §5.5.3, row G5a): the workers stack the
/// groups into columns over the union of their x values (<see cref="SeriesStack"/>) when the layer is made, and each group's
/// pieces are drawn by the geoms' usual paths over those columns: an area or a band, a bar or a floating bar. A column's new
/// version makes the layer again (<see cref="Stale"/>), since the union's length may change.
/// </summary>
internal sealed class StackedSeriesPlan : LayerPlan
{
    private readonly List<Part> _parts = [];
    private readonly SeriesStack _stack;
    private readonly long[] _built;
    private readonly CategoricalColumn? _groups;
    private readonly bool _fill;

    public StackedSeriesPlan(LayerSpec spec, PlanBuild build)
        : base(spec)
    {
        var (x, y) = spec switch
        {
            BarLayerSpec bar => (bar.X.Column, bar.Y.Column),
            AreaLayerSpec area => (area.X.Column, area.Y.Column),
            _ => throw new InvalidOperationException($"A {spec.GetType().Name} is not stacked over a numeric x."),
        };
        _groups = SplitPlan.Splits(spec) ? (CategoricalColumn)(spec.FillBy ?? spec.ColorBy)!.Value.Column : null;
        _fill = PositionOf(spec) == PositionKind.Fill;
        SetColumns(_groups is null ? [x, y] : [x, y, _groups]);
        _built = new long[Columns.Length];
        for (var i = 0; i < _built.Length; i++)
        {
            _built[i] = Columns[i].Version;
        }

        // An integer x stays exact: its values are read as differences from its least one and written back as longs.
        var integer = x is UniformColumn { IsInteger: true } || (x.TryGetRange(out var low, out _) && low.IsInteger);
        var origin = integer && x.TryGetRange(out var least, out _) ? least.Integer : 0;
        var (series, categories, gathers) = Series(x, y, build.Pool);
        try
        {
            _stack = SeriesStack.Build(series, spec is AreaLayerSpec, _fill, integer, origin, build.Pool);
        }
        finally
        {
            foreach (var gather in gathers)
            {
                gather.Dispose();
            }
        }

        // Each group's pieces as layers of the usual geoms over the stack's columns.
        var table = DataTable.Create().Add("x", _stack.X);
        var names = new Dictionary<Column, int>();
        ColumnRef Ref(DataTable built, Column column) => new(built, names[column]);
        foreach (var side in (ReadOnlySpan<Column<double>?[]>)[_stack.Positive, _stack.Negative])
        {
            foreach (var column in side)
            {
                if (column is not null && names.TryAdd(column, names.Count + 1))
                {
                    _ = table.Add($"{names.Count}", column);
                }
            }
        }

        var data = table.Build();
        names[_stack.X] = 0;
        for (var k = 0; k < categories.Length; k++)
        {
            foreach (var up in (ReadOnlySpan<bool>)[true, false])
            {
                if (!(up ? _stack.HasPositive[k] : _stack.HasNegative[k]))
                {
                    continue;
                }

                var side = up ? _stack.Positive : _stack.Negative;
                var (outer, inner) = (side[k]!, side[k + 1]);
                var make = Piece(data, Ref(data, _stack.X), Ref(data, outer), inner is null ? null : Ref(data, inner), up);
                _parts.Add(new Part(categories[k], MakePlan(make(spec), inner is null ? null : Ref(data, inner), build), make));
            }
        }

        // An area's outline: the outer edge of each piece, a line of its own over the fills.
        if (spec is AreaLayerSpec { Outline: not null })
        {
            for (var k = 0; k < categories.Length; k++)
            {
                foreach (var up in (ReadOnlySpan<bool>)[true, false])
                {
                    if (up ? _stack.HasPositive[k] : _stack.HasNegative[k])
                    {
                        var (lineX, lineY) = (Ref(data, _stack.X), Ref(data, (up ? _stack.Positive : _stack.Negative)[k]!));
                        LayerSpec Line(LayerSpec layer) =>
                            new LineLayerSpec
                            {
                                X = lineX,
                                Y = lineY,
                                Style = ((AreaLayerSpec)layer).Outline!.Value,
                            };
                        _parts.Add(new Part(OutlineCategory(spec, categories[k]), Create(Line(spec), build), Line));
                    }
                }
            }
        }
    }

    public override bool Stale
    {
        get
        {
            for (var i = 0; i < _built.Length; i++)
            {
                if (Columns[i].Version != _built[i])
                {
                    return true;
                }
            }

            return false;
        }
    }

    public override void WaitForIndexes(PlotWorkerPool pool)
    {
        foreach (var part in _parts)
        {
            part.Plan.WaitForIndexes(pool);
        }
    }

    public override void AddRanges(RangeSink sink, bool ofX, DataTable data)
    {
        if (ofX)
        {
            var reference = Spec is BarLayerSpec bar ? bar.X : ((AreaLayerSpec)Spec).X;
            sink.AddColumn(reference.Column, data.GetName(reference.Index));
            if (Spec is BarLayerSpec bars && reference.Column.TryGetRange(out var min, out var max))
            {
                // Half a bar past each end, as unstacked bars (design §4.2).
                var half = Width(bars) / 2;
                sink.Add(min.Value - half, max.Value + half, data.GetName(reference.Index));
            }

            return;
        }

        if (_stack.Range is not { } range)
        {
            return;
        }

        var name = data.GetName((Spec is BarLayerSpec b ? b.Y : ((AreaLayerSpec)Spec).Y).Index);
        sink.AddBaseline(0);
        if (_fill)
        {
            sink.Add(_stack.AnyNegative ? -1 : 0, 1, name);
            return;
        }

        sink.Add(range.Min, range.Max, name);
    }

    public override void Record(RecordContext context)
    {
        foreach (var part in _parts)
        {
            part.Plan.SeriesColor = part.Category >= 0 ? Colors!.Palette.Series(part.Category) : SeriesColor;
            part.Plan.Index = Index;
            part.Plan.Colors = Colors;
            part.Plan.Record(context);
            context.Flush();
        }
    }

    public override void Release()
    {
        foreach (var part in _parts)
        {
            part.Plan.Release();
        }

        _stack.Dispose();
    }

    /// <summary>Passes a new node of the same columns on to every piece: its fill, outline, width, and label.</summary>
    protected override void OnSpecChanged(LayerSpec before, LayerSpec after)
    {
        // A new node of the same looks (a host that builds its spec each frame): making the pieces' nodes anew would allocate.
        if (ReferenceEquals(before, after) || SplitPlan.SameLooks(before, after))
        {
            return;
        }

        foreach (var part in _parts)
        {
            part.Plan.Take(part.Make(after));
        }
    }

    /// <summary>Returns a bar layer's width: the one given, or the default of its x (design §5.5.3).</summary>
    private static double Width(BarLayerSpec bar) => bar.Width ?? BarPlan.DefaultWidth(bar.X.Column);

    /// <summary>Returns the category whose color an outline takes: the group's when ColorBy splits it, else the layer's own (−1).</summary>
    private static int OutlineCategory(LayerSpec spec, int category) => spec.ColorBy is { Column: CategoricalColumn } ? category : -1;

    /// <summary>
    /// Returns how a piece's node is made from the layer's: an area or a band between the piece's ends, a bar from 0 or a
    /// floating bar, with the layer's fill and width.
    /// </summary>
    private static Func<LayerSpec, LayerSpec> Piece(DataTable data, ColumnRef x, ColumnRef outer, ColumnRef? inner, bool up) =>
        layer =>
            (layer, inner) switch
            {
                (AreaLayerSpec area, null) => new AreaLayerSpec
                {
                    X = x,
                    Y = outer,
                    Fill = area.Fill,
                },
                (AreaLayerSpec area, { } other) => new BandLayerSpec
                {
                    X = x,
                    YMin = up ? other : outer,
                    YMax = up ? outer : other,
                    Fill = area.Fill,
                },
                (BarLayerSpec bar, _) => new BarLayerSpec
                {
                    X = x,
                    Y = outer,
                    Fill = bar.Fill,
                    Width = Width(bar),
                },
                _ => throw new InvalidOperationException($"A {layer.GetType().Name} is not stacked over a numeric x."),
            };

    /// <summary>Makes a piece's plan: a floating bar from its inner end or 0, or an area's or a band's own plan.</summary>
    private static LayerPlan MakePlan(LayerSpec piece, ColumnRef? inner, PlanBuild build) =>
        piece is BarLayerSpec bar ? new FloatingBarPlan(bar, inner, build) : Create(piece, build, RowRange.All);

    /// <summary>
    /// Returns each group's rows, and its category (−1 for a layer that is not split): a category's run of the layer's own
    /// columns, or its rows gathered (design §5.5.3, row G7c), with the gathers to free once the stack is made.
    /// </summary>
    private (List<StackSeries> Series, int[] Categories, GatherChannel[] Gathers) Series(Column x, Column y, PlotWorkerPool pool)
    {
        if (_groups is null)
        {
            return ([new StackSeries(x, y, 0, Math.Min(x.Length, y.Length))], [-1], []);
        }

        var runs = CategoryRuns.Of(_groups, pool);
        var counts = new long[_groups.CategoryCount];
        for (var c = 0; c < counts.Length; c++)
        {
            counts[c] = runs.IsRun(c) ? 0 : runs.Count[c];
        }

        GatherChannel[] gathers = [];
        if (counts.AsSpan().ContainsAnyExcept(0))
        {
            gathers = [GatherChannel.For(x, counts), GatherChannel.For(y, counts)];
            long[] starts = [];
            SplitPlan.GatherRows(pool, _groups, runs, gathers, ref starts);
        }

        // Every category is a group, used or not, so that the stack's order is the categories' (design §4.1).
        var series = new List<StackSeries>();
        var categories = new int[_groups.CategoryCount];
        for (var c = 0; c < categories.Length; c++)
        {
            categories[c] = c;
            series.Add(
                runs.Count[c] == 0 ? new StackSeries(x, y, 0, 0)
                : runs.IsRun(c) ? new StackSeries(x, y, runs.First[c], runs.Last[c] + 1)
                : new StackSeries(gathers[0].Target(c)!, gathers[1].Target(c)!, 0, runs.Count[c])
            );
        }

        return (series, categories, gathers);
    }

    /// <summary>One piece: its category (−1 for the layer's own color), its plan, and how its node is made from the layer's.</summary>
    private readonly record struct Part(int Category, LayerPlan Plan, Func<LayerSpec, LayerSpec> Make);
}

/// <summary>
/// Bars between two columns (design §5.5.3, row G5a): a stacked group's pieces over a numeric x, from its inner end to its
/// outer one. A rectangle a bar up to 4W of them, else a rectangle a pixel column from the least of the lower ends to the
/// greatest of the upper ones, from M4 as error bars do.
/// </summary>
internal sealed class FloatingBarPlan : LayerPlan
{
    private readonly ColumnRef? _low;
    private readonly int _lowSeries;
    private readonly int _highSeries;
    private readonly M4Point[] _lowOutput;
    private readonly M4Point[] _highOutput;
    private readonly ColumnEnvelope _lows = new();
    private readonly ColumnEnvelope _highs = new();

    public FloatingBarPlan(BarLayerSpec spec, ColumnRef? low, PlanBuild build)
        : base(spec)
    {
        _low = low;
        SetColumns(low is { } lower ? [spec.X.Column, lower.Column, spec.Y.Column] : [spec.X.Column, spec.Y.Column]);
        (_lowSeries, _lowOutput) = low is { } from ? build.AddSeries(spec.X.Column, from.Column) : (-1, []);
        (_highSeries, _highOutput) = build.AddSeries(spec.X.Column, spec.Y.Column);
    }

    public override void WaitForIndexes(PlotWorkerPool pool) => Columns[0].PrecomputeStatsIfMissing(pool)?.GetAwaiter().GetResult();

    // The stacked layer adds the ranges (design §4.2).
    public override void AddRanges(RangeSink sink, bool ofX, DataTable data) { }

    public override void Record(RecordContext context)
    {
        var bar = (BarLayerSpec)Spec;
        var (x, low, high) = (bar.X.Column, _low?.Column, bar.Y.Column);
        var zero = context.BaselinePixel(0);
        var half = bar.Width!.Value / 2;
        var boundaries = context.Boundaries;
        var first = x.LowerBound(boundaries[0] - half, context.XOrigin);
        var end = x.LowerBound(boundaries[^1] + half, context.XOrigin);
        var fill = FillOf(bar.Fill, translucent: false);
        if (end - first <= 4L * context.X.Size)
        {
            for (var i = first; i < end; i++)
            {
                var (bottom, top) = (low is null ? zero : context.ElementY(low, i), context.ElementY(high, i));
                if (bottom != top)
                {
                    var d = x.DifferenceAt(i, context.XOrigin);
                    context.DrawRect(context.PixelX(d - half), context.PixelX(d + half), top, bottom, fill);
                }
            }

            return;
        }

        // More bars than 4W: a pixel column from the least lower end (0 under the last group) to the greatest upper end.
        if (_low is not null)
        {
            Envelope(context, _lowOutput, _lowSeries, _lows);
        }

        Envelope(context, _highOutput, _highSeries, _highs);
        for (var c = 0; c < _highs.Width; c++)
        {
            var (lowest, highest) = _low is null ? (zero, zero) : (_lows.Low[c], _lows.High[c]);
            if (double.IsNaN(lowest) || double.IsNaN(_highs.Low[c]))
            {
                continue;
            }

            var top = AreaPlan.Min(AreaPlan.Min(lowest, _highs.Low[c]), AreaPlan.Min(highest, _highs.High[c]));
            var bottom = AreaPlan.Max(AreaPlan.Max(lowest, _highs.Low[c]), AreaPlan.Max(highest, _highs.High[c]));
            if (bottom > top)
            {
                var column = _highs.Offset + c;
                context.DrawRect(column, column + 1, top, bottom, fill);
            }
        }
    }

    private static void Envelope(RecordContext context, M4Point[] output, int series, ColumnEnvelope envelope)
    {
        var points = output.AsSpan(0, context.Batch.GetResult(series).Count);
        _ = context.MapSeries(points, out var xs, out var ys);
        context.PointEnvelope(points, xs, ys, envelope);
    }
}
