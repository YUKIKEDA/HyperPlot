using System.Numerics;
using HyperPlot.Rendering;

namespace HyperPlot;

/// <summary>
/// A compiled plot (design §4.8): the pyramid of each Y column, the M4 series of each layer, the learned domains, the layout
/// of the guides (row G3, §4.6), and the recorded lines and guides, for a target of one size and scale. Each
/// <see cref="Render(PlotSpec)"/> compares the spec with the last one drawn and does again only the stages that changed.
/// </summary>
/// <remarks>One thread at a time uses a plan. It owns its pyramids; the columns stay with their owner, who disposes them after the plan.</remarks>
internal sealed class PlotPlan : IDisposable
{
    private readonly PlotWorkerPool _pool;
    private readonly float _scale;
    private readonly PlotGuides _guides;
    private readonly Dictionary<Column, PlotPyramid> _pyramids = [];
    private readonly M4Batch _batch = new();
    private readonly RenderList _list = new();
    private readonly RecordContext _context;
    private readonly RangeSink _sink = new();
    private readonly int _capacity;

    // The channels of a layer's node and of the one before it, for the checks and the diff (design §4.8).
    private readonly ColumnRef[] _channels = new ColumnRef[4];
    private readonly ColumnRef[] _previousChannels = new ColumnRef[4];
    private readonly (double[] Data, double[] Scale)[] _widths = new (double[], double[])[4];
    private IScale? _xTransform;
    private IScale? _yTransform;
    private LayerPlan[] _layers = [];
    private PlotSpec? _spec;

    // The boundaries of the pixel columns, W + 1 of them for the panel's width W, in data units and in the scale space
    // (design §4.8). The y labels set W, so a new y domain can change it: _widths keeps the buffers of the last four widths,
    // so that drawing domains seen before allocates nothing (§4.6).
    private double[] _boundaries = [];
    private double[] _scaleBoundaries = [];

    // The domains learned (design §4.2), before nice widens them in the layout (§4.6).
    private ScaleRange _learnedX;
    private ScaleRange _learnedY;

    // ScaleRange.None until boundaries are made for a domain: no domain equals it.
    private ScaleRange _x = ScaleRange.None;
    private ScaleRange _y;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlotPlan"/> class for a target of a size in physical pixels, measuring
    /// its text with a backend's engine, at a scale from DIPs to pixels (design §4.6).
    /// </summary>
    internal PlotPlan(PlotWorkerPool pool, int width, int height, TextEngine text, float scale)
    {
        _pool = pool;
        _scale = scale;
        _guides = new PlotGuides(text, scale, width, height);
        _context = new RecordContext(_guides, _batch, _list, new GeomFrame());

        // The panel is never wider than the target.
        _capacity = M4.MaxPointCount(width);
    }

    /// <summary>Gets the number of pyramids built, for the tests of the diff (design §4.8).</summary>
    internal int PyramidsBuilt { get; private set; }

    /// <summary>Gets the number of M4 extractions, for the tests of the diff.</summary>
    internal int Extractions { get; private set; }

    /// <summary>Gets the number of times the lines were recorded, for the tests of the diff.</summary>
    internal int Recordings { get; private set; }

    /// <summary>Gets the number of times the guides were laid out, for the tests of the diff (design §4.8).</summary>
    internal int Layouts { get; private set; }

    /// <summary>Gets the layout of the guides. Tests only.</summary>
    internal PlotGuides Guides => _guides;

    /// <summary>Gets the pyramids the plan holds, one a Y column. Tests only.</summary>
    internal IReadOnlyCollection<PlotPyramid> Pyramids => _pyramids.Values;

    /// <summary>Gets the x domain of the last extraction, as an origin and differences (design §3.6). Tests only.</summary>
    internal ScaleRange XDomain => _x;

    /// <summary>Gets the y domain of the last extraction. Tests only.</summary>
    internal ScaleRange YDomain => _y;

    /// <summary>
    /// Compiles a spec against the last one drawn, waits until its pyramids and X statistics are built for the columns'
    /// current values, lays out the guides, and returns the recorded panel, grid, lines, axes, titles, and legend (design
    /// §4.6, §4.8); an empty list when the panel is too small. Allocates nothing for the spec drawn last once warm.
    /// </summary>
    /// <exception cref="ArgumentNullException">The spec has a null member (its theme included) or layer.</exception>
    /// <exception cref="ArgumentException">
    /// The spec or its theme does not check (design §4.7, §4.8), or the x domain is too narrow for the panel's width, in
    /// the scale space or once mapped back to data units.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A learned domain has an end that is not finite, in data units or once the scale maps it (design §4.2), or an X column
    /// decreases. While ticks are chosen: a time domain outside the years 1 to 9999, a tick format that returns false or a
    /// length outside 0 to 64, or a default label past 64 characters (design §4.6). Shaping a text failed. An exception
    /// that a user's <see cref="IScale"/> or tick format throws comes out as it is.
    /// </exception>
    internal RenderList Render(PlotSpec spec)
    {
        Validate(spec);
        if (spec.Theme is null)
        {
            throw new ArgumentNullException(nameof(spec), "The spec's Theme must not be null.");
        }

        spec.Theme.Validate(_scale);
        var previous = _spec;

        // Until this spec is drawn, the next one is compiled from the start: a stage that threw leaves no state to trust.
        _spec = null;
        var data = previous is null || DataChanged(previous, spec);
        var scales = previous is null || !SameMapping(previous.ScaleX, spec.ScaleX) || !SameMapping(previous.ScaleY, spec.ScaleY);
        var ranges = data || RangesChanged(previous!, spec);
        var looks =
            data
            || NodesChanged(previous!, spec)
            || OptionsChanged(previous!.ScaleX, spec.ScaleX)
            || OptionsChanged(previous!.ScaleY, spec.ScaleY)
            || !string.Equals(previous.Title, spec.Title, StringComparison.Ordinal)
            || !previous.Theme.Equals(spec.Theme);
        if (scales)
        {
            // A new transform makes new boundaries even over the same range of the scale space.
            (_xTransform, _yTransform, _x) = (
                ((ContinuousScaleSpec)spec.ScaleX).CreateTransform(),
                ((ContinuousScaleSpec)spec.ScaleY).CreateTransform(),
                ScaleRange.None
            );
        }

        if (data)
        {
            BuildLayers(spec);
        }
        else
        {
            // The layers name the same columns; take the new nodes for their looks, baselines, and widths.
            for (var i = 0; i < _layers.Length; i++)
            {
                _layers[i].Spec = spec.Layers[i];
            }
        }

        // The versions are read before the wait and the extraction. An Invalidate that comes after this read leaves the
        // recorded versions behind the column's, so the next Render extracts again.
        foreach (var layer in _layers)
        {
            layer.ReadVersions();
        }

        WaitForIndexes();
        var extract = ranges || scales || ValuesChanged();
        if (extract)
        {
            foreach (var layer in _layers)
            {
                layer.BeginLearning();
            }

            _learnedX = Learn(spec, (ContinuousScaleSpec)spec.ScaleX, _xTransform, ofX: true);
            _learnedY = Learn(spec, (ContinuousScaleSpec)spec.ScaleY, _yTransform, ofX: false);
        }

        if (extract || looks)
        {
            // The layout chooses the ticks, which nice may widen the domains to (design §4.6, §4.8).
            _guides.Layout(spec, _xTransform, _learnedX, _yTransform, _learnedY);
            Layouts++;
            if (_guides.Empty)
            {
                // Only the background, which the renderer clears to (design §4.6).
                _list.Clear();
                _x = ScaleRange.None;
                _spec = spec;
                return _list;
            }

            var x = _guides.X.FinalDomain;
            _y = _guides.Y.FinalDomain;
            var boundaries = x != _x || _boundaries.Length != _guides.Width + 1;
            if (boundaries)
            {
                MakeBoundaries(x, _guides.Width);
            }

            if (extract || boundaries)
            {
                // A transformed axis reads its data in the origin 0; its origin is one of the scale space (design §4.2).
                _pool.Extract(_batch, new ScaleOrigin(_xTransform is null ? _x.Origin : 0, _yTransform is null ? _y.Origin : 0), _boundaries);
                Extractions++;
                foreach (var layer in _layers)
                {
                    layer.MarkExtracted();
                }
            }

            Record(spec);
            Recordings++;
        }

        _spec = spec;
        return _list;
    }

    /// <summary>Releases the pyramids.</summary>
    public void Dispose()
    {
        foreach (var pyramid in _pyramids.Values)
        {
            pyramid.Dispose();
        }

        _pyramids.Clear();
        _layers = [];
        _batch.Clear();
        _spec = null;
    }

    /// <summary>Throws unless the spec is one that rows G1 to G4 compile (design §4.8). Reads no data but column lengths.</summary>
    private void Validate(PlotSpec plot)
    {
        if (plot.Data is null || plot.Layers is null || plot.ScaleX is null || plot.ScaleY is null)
        {
            throw new ArgumentNullException(nameof(plot), "The spec's Data, Layers, ScaleX, and ScaleY must not be null.");
        }

        for (var i = 0; i < plot.Layers.Count; i++)
        {
            var layer = plot.Layers[i] ?? throw new ArgumentNullException(nameof(plot), $"Layer {i} of the spec is null.");
            LayerPlan.Validate(plot.Data, layer, i, _channels);
        }

        CheckScale(plot, plot.ScaleX, _spec?.ScaleX, "ScaleX", ofX: true);
        CheckScale(plot, plot.ScaleY, _spec?.ScaleY, "ScaleY", ofX: false);
    }

    /// <summary>
    /// Throws unless a scale is one of row G2's (design §4.8), its domain set with <c>with</c> meets its kind's conditions,
    /// and a time scale reads long date-time columns. A user cannot derive a new continuous kind: its base has internal
    /// abstract members.
    /// </summary>
    private void CheckScale(PlotSpec plot, ScaleSpec scale, ScaleSpec? previous, string name, bool ofX)
    {
        if (scale is not ContinuousScaleSpec continuous)
        {
            throw new ArgumentException($"The {name} is a {scale.GetType().Name}; row G2 compiles its position scales only.");
        }

        // A node drawn last time was checked then: it is immutable, and a user's Forward runs once a node (design §4.2).
        if (previous is null || previous.Id != scale.Id)
        {
            continuous.CheckGivenDomain();
        }

        if (continuous is not TimeScaleSpec)
        {
            return;
        }

        foreach (var layer in plot.Layers)
        {
            var count = LayerPlan.AxisChannels(layer, ofX, _channels);
            for (var c = 0; c < count; c++)
            {
                var reference = _channels[c];
                if (reference.Column is not (Column<long> or UniformColumn { IsInteger: true }))
                {
                    throw new ArgumentException(
                        $"The {name} is a time scale, and the column \"{plot.Data.GetName(reference.Index)}\" is a {reference.Column.KindName}, not a long date-time column."
                    );
                }
            }
        }
    }

    /// <summary>
    /// Returns whether a layer's columns or kind changed, or the layers were added, removed, or moved (design §4.8): the
    /// "data" stage. A new table changes every reference, since a reference names its table and the check refuses another
    /// table's.
    /// </summary>
    private bool DataChanged(PlotSpec previous, PlotSpec spec)
    {
        if (previous.Layers.Count != spec.Layers.Count)
        {
            return true;
        }

        for (var i = 0; i < spec.Layers.Count; i++)
        {
            var (before, after) = (previous.Layers[i], spec.Layers[i]);
            if (before.Id != after.Id && LayerPlan.ChannelsDiffer(before, after, _previousChannels, _channels))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns whether a layer's Baseline or bar Width changed (the "scale" stage); called when the layers name the same columns.</summary>
    private static bool RangesChanged(PlotSpec previous, PlotSpec spec)
    {
        for (var i = 0; i < spec.Layers.Count; i++)
        {
            var (before, after) = (previous.Layers[i], spec.Layers[i]);
            if (before.Id != after.Id && LayerPlan.RangesDiffer(before, after))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns whether a layer is a new node (design §4.8): over the same columns and ranges, a new node changes only its
    /// looks (a stroke, a fill, a label, a mode), which records again.
    /// </summary>
    private static bool NodesChanged(PlotSpec previous, PlotSpec spec)
    {
        for (var i = 0; i < spec.Layers.Count; i++)
        {
            if (previous.Layers[i].Id != spec.Layers[i].Id)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns whether two scales map data the same way (design §4.8): the same node, or the same kind with the same domain,
    /// zero, nice, and constants. Reverse, clamp, the title, and the format are left out: they change the layout and the
    /// recording only (design §4.8). Allocates nothing.
    /// </summary>
    private static bool SameMapping(ScaleSpec before, ScaleSpec after) =>
        before.Id == after.Id || (((ContinuousScaleSpec)before).Nice == ((ContinuousScaleSpec)after).Nice && SameKind(before, after));

    private static bool SameKind(ScaleSpec before, ScaleSpec after) =>
        (before, after) switch
        {
            (LinearScaleSpec b, LinearScaleSpec a) => b.Domain == a.Domain && b.Zero == a.Zero,
            (LogScaleSpec b, LogScaleSpec a) => b.Domain == a.Domain,
            (SymlogScaleSpec b, SymlogScaleSpec a) => b.Domain == a.Domain && b.Zero == a.Zero && b.Constant.Equals(a.Constant),
            (PowScaleSpec b, PowScaleSpec a) => b.Domain == a.Domain && b.Zero == a.Zero && b.Exponent.Equals(a.Exponent),
            (TimeScaleSpec b, TimeScaleSpec a) => b.Domain == a.Domain && b.Unit == a.Unit && b.Epoch == a.Epoch,
            (TransformScaleSpec b, TransformScaleSpec a) => b.Domain == a.Domain && ReferenceEquals(b.Transform, a.Transform),
            _ => false,
        };

    /// <summary>
    /// Returns whether a scale's reverse, clamp, title, or tick format changed, which only lays out and records again (design
    /// §4.8). Formats are compared as delegates: the same method and target are equal.
    /// </summary>
    private static bool OptionsChanged(ScaleSpec before, ScaleSpec after)
    {
        var (b, a) = ((ContinuousScaleSpec)before, (ContinuousScaleSpec)after);
        return b.Reverse != a.Reverse
            || b.Clamp != a.Clamp
            || !string.Equals(b.Title, a.Title, StringComparison.Ordinal)
            || !Equals(b.NumericFormat, a.NumericFormat)
            || (b is TimeScaleSpec bt && a is TimeScaleSpec at && !Equals(bt.TickFormat, at.TickFormat));
    }

    /// <summary>
    /// Learns a domain (design §4.2) as an origin and differences (§3.6): the one given, or the union of the ranges of every
    /// layer's column, from statistics that the wait for the indexes had the workers compute. Without a transform (linear,
    /// time) the domain is in data units; integer ends are compared as <see cref="long"/> while every column is an integer
    /// column, and as <see cref="double"/> once a floating-point one comes in. With a transform it is in the scale space: the
    /// ends are mapped, and the cases of no value and one value are taken there.
    /// </summary>
    private ScaleRange Learn(PlotSpec spec, ContinuousScaleSpec scale, IScale? transform, bool ofX)
    {
        if (scale is TimeScaleSpec { Domain: { } time })
        {
            return ScaleRange.FromInteger(time.Min, time.Max);
        }

        if (scale.GivenDomain is { } domain)
        {
            return transform is null
                ? ScaleRange.FromDouble(domain.Min, domain.Max)
                : Mapped(transform, domain.Min, domain.Max, string.Empty, string.Empty);
        }

        _sink.Reset();
        foreach (var layer in _layers)
        {
            layer.AddRanges(_sink, ofX, spec.Data);
        }

        var (integerMin, integerMax, min, max) = (_sink.IntegerMin, _sink.IntegerMax, _sink.Min, _sink.Max);
        var (minName, maxName, found, integer) = (_sink.MinName, _sink.MaxName, _sink.Found, _sink.Integer);
        if (!found)
        {
            // No value: [0, 1], in the scale space when there is a transform (log: 1 to 10).
            return ScaleRange.FromDouble(0, 1);
        }

        if (scale.ZeroOption)
        {
            // 0 first; one value that is not 0 then has a width (design §4.2).
            (integerMin, integerMax) = (Math.Min(integerMin, 0), Math.Max(integerMax, 0));
            (min, max) = (Math.Min(min, 0), Math.Max(max, 0));
        }

        if (transform is not null)
        {
            return min == max ? Single(transform, min, minName) : Mapped(transform, min, max, minName, maxName);
        }

        if (!integer)
        {
            return min == max ? ScaleRange.FromDouble(min - 0.5, max + 0.5) : ScaleRange.FromDouble(min, max);
        }

        // One value v gives [v - 0.5, v + 0.5], taken as differences from v so that a v past 2^53 stays exact.
        return integerMin == integerMax ? new ScaleRange(integerMin, -0.5, 0.5).Anchor() : ScaleRange.FromInteger(integerMin, integerMax);
    }

    /// <summary>Returns the range of the scale space between the mapped ends, or throws when an end does not map to a finite value.</summary>
    private static ScaleRange Mapped(IScale transform, double min, double max, string minName, string maxName)
    {
        Span<double> ends = [min, max];
        Span<double> mapped = stackalloc double[2];
        transform.Forward(ends, mapped);
        CheckMapped(min, mapped[0], minName);
        CheckMapped(max, mapped[1], maxName);
        return ScaleRange.FromDouble(mapped[0], mapped[1]);
    }

    /// <summary>Returns [f(v) − 0.5, f(v) + 0.5] in the scale space, for a learned domain of one value (design §4.2).</summary>
    private static ScaleRange Single(IScale transform, double value, string name)
    {
        Span<double> end = [value];
        Span<double> mapped = stackalloc double[1];
        transform.Forward(end, mapped);
        CheckMapped(value, mapped[0], name);
        return ScaleRange.FromDouble(mapped[0] - 0.5, mapped[0] + 0.5);
    }

    private static void CheckMapped(double value, double mapped, string name)
    {
        if (!double.IsFinite(mapped))
        {
            throw new InvalidOperationException(
                $"The column \"{name}\" holds {value}, which the scale maps to {mapped}, so a learned domain would have an end that is not finite; give the scale a domain with WithDomain."
            );
        }
    }

    /// <summary>
    /// Writes the W + 1 boundaries of the pixel columns over an x domain into the plan's buffer, or throws when they do not
    /// increase in double (design §4.8). Without a transform they are differences from the domain's origin; with one they are
    /// made in the scale space and mapped back to data units with <see cref="IScale.Inverse"/>, and must still increase
    /// (design §4.2). Allocates nothing, so that a new x domain costs no garbage.
    /// </summary>
    private void MakeBoundaries(ScaleRange domain, int width)
    {
        // Until the buffer holds this domain's boundaries, it holds no domain's. A new width makes new buffers: the layout
        // changed, which is not the steady state.
        _x = ScaleRange.None;
        if (_boundaries.Length != width + 1)
        {
            (_boundaries, _scaleBoundaries) = Buffers(width + 1);
        }

        var target = _xTransform is null ? _boundaries : _scaleBoundaries;
        for (var k = 0; k <= width; k++)
        {
            target[k] = domain.Lower + ((domain.Upper - domain.Lower) * k / width);
            if (k > 0 && !(target[k - 1] < target[k]))
            {
                throw new ArgumentException(
                    $"The x domain [{domain.Origin} + {domain.Lower}, {domain.Origin} + {domain.Upper}] is too narrow for {width} pixel columns: their boundaries do not increase in double."
                );
            }
        }

        if (_xTransform is not null)
        {
            for (var k = 0; k <= width; k++)
            {
                _scaleBoundaries[k] += domain.Origin;
            }

            _xTransform.Inverse(_scaleBoundaries, _boundaries);
            for (var k = 0; k <= width; k++)
            {
                if (!double.IsFinite(_boundaries[k]) || (k > 0 && !(_boundaries[k - 1] < _boundaries[k])))
                {
                    throw new ArgumentException(
                        $"The x domain [{domain.Origin} + {domain.Lower}, {domain.Origin} + {domain.Upper}] of the scale space is too narrow for {width} pixel columns: its boundaries, mapped back to data units, do not increase in double."
                    );
                }
            }
        }

        _x = domain;
    }

    /// <summary>Returns the buffers of a length: kept ones, or new ones in the place of the least recent.</summary>
    private (double[] Data, double[] Scale) Buffers(int length)
    {
        for (var i = 0; i < _widths.Length; i++)
        {
            if (_widths[i].Data?.Length == length)
            {
                var found = _widths[i];
                Array.Copy(_widths, 0, _widths, 1, i);
                _widths[0] = found;
                return found;
            }
        }

        Array.Copy(_widths, 0, _widths, 1, _widths.Length - 1);
        _widths[0] = (new double[length], new double[length]);
        return _widths[0];
    }

    /// <summary>
    /// Makes the layers again (design §4.8): a pyramid for each Y column not held yet, the layers' M4 series, and frees the
    /// pyramids no layer uses.
    /// </summary>
    private void BuildLayers(PlotSpec spec)
    {
        _batch.Clear();
        var used = new HashSet<Column>();
        var build = new PlanBuild(Pyramid, _batch, _capacity);
        var layers = new LayerPlan[spec.Layers.Count];
        for (var i = 0; i < layers.Length; i++)
        {
            layers[i] = LayerPlan.Create(spec.Layers[i], build);
        }

        foreach (var column in _pyramids.Keys.ToArray())
        {
            if (!used.Contains(column))
            {
                _pyramids[column].Dispose();
                _ = _pyramids.Remove(column);
            }
        }

        _layers = layers;

        PlotPyramid Pyramid(Column y)
        {
            _ = used.Add(y);
            if (!_pyramids.TryGetValue(y, out var pyramid))
            {
                pyramid = ((IPyramidColumn)y).BuildPlotPyramid(_pool);
                _pyramids.Add(y, pyramid);
                PyramidsBuilt++;
            }

            return pyramid;
        }
    }

    /// <summary>
    /// Waits until every pyramid and the statistics and orders the layers search are there for the columns' current values
    /// (design §4.8): a headless image is the same whenever it is drawn. A failure is thrown again as it was.
    /// </summary>
    private void WaitForIndexes()
    {
        foreach (var pyramid in _pyramids.Values)
        {
            pyramid.WhenComplete().GetAwaiter().GetResult();
        }

        foreach (var layer in _layers)
        {
            layer.WaitForIndexes(_pool);
        }
    }

    /// <summary>Returns whether a column's values changed between the last extraction and this Render's read of the versions (an Invalidate).</summary>
    private bool ValuesChanged()
    {
        foreach (var layer in _layers)
        {
            if (layer.ValuesChanged())
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Records the guides behind, each layer in order inside the panel's clip, and the guides over (design §4.6, §5.5.3). A
    /// layer maps its values to pixels in double, through the transforms a span at a time.
    /// </summary>
    private void Record(PlotSpec spec)
    {
        var (xScale, yScale) = ((ContinuousScaleSpec)spec.ScaleX, (ContinuousScaleSpec)spec.ScaleY);
        _list.Clear();
        _guides.RecordBehind(_list, spec);
        _context.Begin(
            new GeomFrame.Axis(_xTransform, _x, xScale.Reverse, xScale.Clamp, _guides.Left, _guides.Width),
            new GeomFrame.Axis(_yTransform, _y, yScale.Reverse, yScale.Clamp, _guides.Top, _guides.Height),
            _boundaries,
            _scale
        );
        _context.ClipToPanel();
        foreach (var layer in _layers)
        {
            layer.Record(_context);
        }

        _guides.RecordOver(_list, spec);
    }
}
