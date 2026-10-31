using System.Numerics;
using HyperPlot.Rendering;

namespace HyperPlot;

/// <summary>A line through M4 (rows G1, G2, design §4.8).</summary>
internal sealed class LinePlan : LayerPlan
{
    private readonly int _series;
    private readonly M4Point[] _output;

    public LinePlan(LineLayerSpec spec, PlanBuild build, RowRange rows)
        : base(spec, rows)
    {
        SetColumns(spec.X.Column, spec.Y.Column);
        (_series, _output) = build.AddSeries(spec.X.Column, spec.Y.Column, rows);
    }

    public override void WaitForIndexes(PlotWorkerPool pool) => Columns[0].PrecomputeStatsIfMissing(pool)?.GetAwaiter().GetResult();

    public override void AddRanges(RangeSink sink, bool ofX, DataTable data)
    {
        var line = (LineLayerSpec)Spec;
        var reference = ofX ? line.X : line.Y;
        sink.AddColumn(reference.Column, data.GetName(reference.Index));
    }

    public override void Record(RecordContext context)
    {
        var points = _output.AsSpan(0, context.Batch.GetResult(_series).Count);
        _ = context.MapSeries(points, out var xs, out var ys);
        context.Polyline(points, xs, ys, Stroke(((LineLayerSpec)Spec).Style));
    }
}

/// <summary>A step line: M4, then a turn between each two points (design §5.5.3).</summary>
internal sealed class StepPlan : LayerPlan
{
    private readonly int _series;
    private readonly M4Point[] _output;

    public StepPlan(StepLayerSpec spec, PlanBuild build, RowRange rows)
        : base(spec, rows)
    {
        SetColumns(spec.X.Column, spec.Y.Column);
        (_series, _output) = build.AddSeries(spec.X.Column, spec.Y.Column, rows);
    }

    public override void WaitForIndexes(PlotWorkerPool pool) => Columns[0].PrecomputeStatsIfMissing(pool)?.GetAwaiter().GetResult();

    public override void AddRanges(RangeSink sink, bool ofX, DataTable data)
    {
        var step = (StepLayerSpec)Spec;
        var reference = ofX ? step.X : step.Y;
        sink.AddColumn(reference.Column, data.GetName(reference.Index));
    }

    public override void Record(RecordContext context)
    {
        var step = (StepLayerSpec)Spec;
        var points = _output.AsSpan(0, context.Batch.GetResult(_series).Count);
        _ = context.MapSeries(points, out var xs, out var ys);
        var buffer = context.Points((4 * points.Length) + 1);
        var count = 0;
        for (var i = 0; i < points.Length; i++)
        {
            if (points[i].BreakBefore)
            {
                buffer[count++] = new Vector2(float.NaN);
            }
            else if (i > 0)
            {
                // The turn from the point before (design §5.5.3): across then up, up then across, or across to the middle.
                switch (step.Mode)
                {
                    case StepMode.After:
                        buffer[count++] = new Vector2((float)xs[i], (float)ys[i - 1]);
                        break;
                    case StepMode.Before:
                        buffer[count++] = new Vector2((float)xs[i - 1], (float)ys[i]);
                        break;
                    default:
                        var middle = (float)context.PixelX((points[i - 1].X + points[i].X) / 2);
                        buffer[count++] = new Vector2(middle, (float)ys[i - 1]);
                        buffer[count++] = new Vector2(middle, (float)ys[i]);
                        break;
                }
            }

            buffer[count++] = new Vector2((float)xs[i], (float)ys[i]);
        }

        context.DrawPolyline(buffer[..count], Stroke(step.Style));
    }
}

/// <summary>An area: one trapezoid a pixel column between the curve and the baseline (design §5.5.3).</summary>
internal sealed class AreaPlan : LayerPlan
{
    private readonly int _series;
    private readonly M4Point[] _output;
    private readonly ColumnEnvelope _envelope = new();

    public AreaPlan(AreaLayerSpec spec, PlanBuild build, RowRange rows)
        : base(spec, rows)
    {
        SetColumns(spec.X.Column, spec.Y.Column);
        (_series, _output) = build.AddSeries(spec.X.Column, spec.Y.Column, rows);
    }

    public override void WaitForIndexes(PlotWorkerPool pool) => Columns[0].PrecomputeStatsIfMissing(pool)?.GetAwaiter().GetResult();

    public override void AddRanges(RangeSink sink, bool ofX, DataTable data)
    {
        var area = (AreaLayerSpec)Spec;
        var reference = ofX ? area.X : area.Y;
        sink.AddColumn(reference.Column, data.GetName(reference.Index));
        if (!ofX)
        {
            sink.AddBaseline(area.Baseline);
        }
    }

    public override void Record(RecordContext context)
    {
        var area = (AreaLayerSpec)Spec;
        var points = _output.AsSpan(0, context.Batch.GetResult(_series).Count);
        _ = context.MapSeries(points, out var xs, out var ys);
        context.Envelope(points, xs, ys, _envelope);
        var baseline = context.BaselinePixel(area.Baseline);
        var trapezoids = context.Trapezoids(_envelope.Width);
        var count = 0;
        for (var c = 0; c < _envelope.Width; c++)
        {
            if (Column(_envelope, c, baseline, baseline, out var trapezoid))
            {
                trapezoids[count++] = trapezoid;
            }
        }

        context.DrawColumns(trapezoids[..count], FillOf(area.Fill, translucent: true));
        if (area.Outline is { } outline)
        {
            context.Polyline(points, xs, ys, Stroke(outline));
        }
    }

    /// <summary>
    /// The trapezoid of a column between a curve's envelope and a lower edge of two values (design §5.5.3): sloped where no
    /// point lies inside, flat over the extremes of the values and the points where one does.
    /// </summary>
    internal static bool Column(ColumnEnvelope top, int c, double bottomLeft, double bottomRight, out ColumnTrapezoid trapezoid)
    {
        var (from, to) = (top.From[c], top.To[c]);
        trapezoid = default;
        if (double.IsNaN(from) || !(to > from))
        {
            return false;
        }

        if (double.IsNaN(top.Low[c]) && !double.IsNaN(top.Left[c]) && !double.IsNaN(top.Right[c]))
        {
            trapezoid = new ColumnTrapezoid((float)from, (float)to, (float)top.Left[c], (float)top.Right[c], (float)bottomLeft, (float)bottomRight);
            return true;
        }

        var low = Min(Min(Min(top.Left[c], top.Right[c]), Min(top.Low[c], top.High[c])), Min(bottomLeft, bottomRight));
        var high = Max(Max(Max(top.Left[c], top.Right[c]), Max(top.Low[c], top.High[c])), Max(bottomLeft, bottomRight));
        trapezoid = new ColumnTrapezoid((float)from, (float)to, (float)low, (float)low, (float)high, (float)high);
        return true;
    }

    internal static double Min(double a, double b) =>
        double.IsNaN(a) ? b
        : double.IsNaN(b) ? a
        : Math.Min(a, b);

    internal static double Max(double a, double b) =>
        double.IsNaN(a) ? b
        : double.IsNaN(b) ? a
        : Math.Max(a, b);
}

/// <summary>A band: one trapezoid a pixel column between the envelopes of two series (design §5.5.3).</summary>
internal sealed class BandPlan : LayerPlan
{
    private readonly int _lowSeries;
    private readonly int _highSeries;
    private readonly M4Point[] _lowOutput;
    private readonly M4Point[] _highOutput;
    private readonly ColumnEnvelope _low = new();
    private readonly ColumnEnvelope _high = new();

    public BandPlan(BandLayerSpec spec, PlanBuild build, RowRange rows)
        : base(spec, rows)
    {
        SetColumns(spec.X.Column, spec.YMin.Column, spec.YMax.Column);
        (_lowSeries, _lowOutput) = build.AddSeries(spec.X.Column, spec.YMin.Column, rows);
        (_highSeries, _highOutput) = build.AddSeries(spec.X.Column, spec.YMax.Column, rows);
    }

    public override void WaitForIndexes(PlotWorkerPool pool) => Columns[0].PrecomputeStatsIfMissing(pool)?.GetAwaiter().GetResult();

    public override void AddRanges(RangeSink sink, bool ofX, DataTable data)
    {
        var band = (BandLayerSpec)Spec;
        if (ofX)
        {
            sink.AddColumn(band.X.Column, data.GetName(band.X.Index));
            return;
        }

        sink.AddColumn(band.YMin.Column, data.GetName(band.YMin.Index));
        sink.AddColumn(band.YMax.Column, data.GetName(band.YMax.Index));
    }

    public override void Record(RecordContext context)
    {
        var band = (BandLayerSpec)Spec;
        var lowPoints = _lowOutput.AsSpan(0, context.Batch.GetResult(_lowSeries).Count);
        _ = context.MapSeries(lowPoints, out var lowXs, out var lowYs);
        context.Envelope(lowPoints, lowXs, lowYs, _low);
        var highPoints = _highOutput.AsSpan(0, context.Batch.GetResult(_highSeries).Count);
        _ = context.MapSeries(highPoints, out var highXs, out var highYs);
        context.Envelope(highPoints, highXs, highYs, _high);
        var trapezoids = context.Trapezoids(_high.Width);
        var count = 0;
        for (var c = 0; c < _high.Width; c++)
        {
            var from = Math.Max(_high.From[c], _low.From[c]);
            var to = Math.Min(_high.To[c], _low.To[c]);
            if (double.IsNaN(from) || double.IsNaN(to) || !(to > from))
            {
                continue;
            }

            var sloped =
                double.IsNaN(_high.Low[c])
                && double.IsNaN(_low.Low[c])
                && !double.IsNaN(_high.Left[c] + _high.Right[c] + _low.Left[c] + _low.Right[c]);
            if (sloped)
            {
                trapezoids[count++] = new ColumnTrapezoid(
                    (float)from,
                    (float)to,
                    (float)_high.Left[c],
                    (float)_high.Right[c],
                    (float)_low.Left[c],
                    (float)_low.Right[c]
                );
                continue;
            }

            var low = AreaPlan.Min(
                AreaPlan.Min(AreaPlan.Min(_high.Left[c], _high.Right[c]), AreaPlan.Min(_high.Low[c], _high.High[c])),
                AreaPlan.Min(AreaPlan.Min(_low.Left[c], _low.Right[c]), AreaPlan.Min(_low.Low[c], _low.High[c]))
            );
            var high = AreaPlan.Max(
                AreaPlan.Max(AreaPlan.Max(_high.Left[c], _high.Right[c]), AreaPlan.Max(_high.Low[c], _high.High[c])),
                AreaPlan.Max(AreaPlan.Max(_low.Left[c], _low.Right[c]), AreaPlan.Max(_low.Low[c], _low.High[c]))
            );
            trapezoids[count++] = new ColumnTrapezoid((float)from, (float)to, (float)low, (float)low, (float)high, (float)high);
        }

        context.DrawColumns(trapezoids[..count], FillOf(band.Fill, translucent: true));
        if (band.Outline is { } outline)
        {
            // Over the fill; the mapped buffer holds one series at a time, so each is mapped again.
            _ = context.MapSeries(lowPoints, out lowXs, out lowYs);
            context.Polyline(lowPoints, lowXs, lowYs, Stroke(outline));
            _ = context.MapSeries(highPoints, out highXs, out highYs);
            context.Polyline(highPoints, highXs, highYs, Stroke(outline));
        }
    }
}

/// <summary>Bars: one rectangle a bar up to 4W of them, else one or two a pixel column from M4 (design §5.5.3).</summary>
internal sealed class BarPlan : LayerPlan
{
    private readonly int _series;
    private readonly M4Point[] _output;
    private readonly ColumnEnvelope _envelope = new();

    public BarPlan(BarLayerSpec spec, PlanBuild build, RowRange rows)
        : base(spec, rows)
    {
        SetColumns(spec.X.Column, spec.Y.Column);
        (_series, _output) = build.AddSeries(spec.X.Column, spec.Y.Column, rows);
    }

    /// <summary>Gets the width the bars are drawn with, in data units: read from the X's statistics, so after the wait.</summary>
    private double Width => ((BarLayerSpec)Spec).Width ?? DefaultWidth(Columns[0]);

    /// <summary>
    /// Returns the default width (design §5.5.3): 0.9 times a uniform X's step or a stored X's mean spacing, or 0.9 when a
    /// stored X has one element or all its values are the same.
    /// </summary>
    internal static double DefaultWidth(Column x)
    {
        if (x is UniformColumn uniform)
        {
            return uniform.Step * 0.9;
        }

        return x.Length > 1 && x.TryGetRange(out var min, out var max) && max.Value > min.Value
            ? (max.Value - min.Value) / (x.Length - 1) * 0.9
            : 0.9;
    }

    public override void WaitForIndexes(PlotWorkerPool pool) => Columns[0].PrecomputeStatsIfMissing(pool)?.GetAwaiter().GetResult();

    public override void AddRanges(RangeSink sink, bool ofX, DataTable data)
    {
        var bar = (BarLayerSpec)Spec;
        if (!ofX)
        {
            sink.AddColumn(bar.Y.Column, data.GetName(bar.Y.Index));
            sink.AddBaseline(bar.Baseline);
            return;
        }

        sink.AddColumn(bar.X.Column, data.GetName(bar.X.Index));
        if (bar.X.Column.TryGetRange(out var min, out var max))
        {
            // Half a bar past each end, so that the end bars are not cut (design §4.2).
            var half = Width / 2;
            sink.Add(min.Value - half, max.Value + half, data.GetName(bar.X.Index));
        }
    }

    public override void Record(RecordContext context)
    {
        var bar = (BarLayerSpec)Spec;
        var x = bar.X.Column;
        var y = bar.Y.Column;
        var boundaries = context.Boundaries;
        var half = Width / 2;
        var first = Rows.Hold(x.LowerBound(boundaries[0] - half, context.XOrigin), x.Length);
        var end = Rows.Hold(x.LowerBound(boundaries[^1] + half, context.XOrigin), x.Length);
        var baseline = context.BaselinePixel(bar.Baseline);
        var barFill = FillOf(bar.Fill, translucent: false);
        if (ElementColors is { } colors)
        {
            // A color per bar cannot join a pixel column (design §4.1, row G7c): each bar, up to the cap.
            if (end - first > MaxElements)
            {
                throw new InvalidOperationException(
                    $"{end - first} bars are in view, colored one by one by a number; at most {MaxElements} are drawn so (design §4.1). Row R3 draws more as a density."
                );
            }

            for (var i = first; i < end; i++)
            {
                if (ElementColor(colors, i) is { } color)
                {
                    var d = x.DifferenceAt(i, context.XOrigin);
                    context.DrawRect(context.PixelX(d - half), context.PixelX(d + half), context.ElementY(y, i), baseline, color);
                }
            }

            return;
        }

        if (end - first <= 4L * context.X.Size)
        {
            for (var i = first; i < end; i++)
            {
                var d = x.DifferenceAt(i, context.XOrigin);
                var left = context.PixelX(d - half);
                var right = context.PixelX(d + half);
                var top = context.ElementY(y, i);
                context.DrawRect(left, right, top, baseline, barFill);
            }

            return;
        }

        // More bars than 4W: the M4 points of each pixel column, from the baseline to their extreme above and below.
        var points = _output.AsSpan(0, context.Batch.GetResult(_series).Count);
        _ = context.MapSeries(points, out var xs, out var ys);
        context.PointEnvelope(points, xs, ys, _envelope);

        for (var c = 0; c < _envelope.Width; c++)
        {
            var column = _envelope.Offset + c;
            if (_envelope.Low[c] < baseline)
            {
                context.DrawRect(column, column + 1, _envelope.Low[c], baseline, barFill);
            }

            if (_envelope.High[c] > baseline)
            {
                context.DrawRect(column, column + 1, _envelope.High[c], baseline, barFill);
            }
        }
    }
}

/// <summary>
/// Bars over categories (design §5.5.3, row G7b): for each category, from the baseline to the greatest and to the least y of
/// its rows, a band wide. The extremes come from the workers, a pass a version of the Y column, with a pyramid over each
/// so that bands under a pixel join a pixel column at a time from the pyramid's levels: the frame's work is bounded by
/// the categories, and by the pixels when they are more.
/// </summary>
internal sealed class CategoryBarPlan : LayerPlan
{
    private CategoryExtremes? _extremes;
    private Column<double>? _minColumn;
    private Column<double>? _maxColumn;
    private MinMaxPyramid<double>? _minPyramid;
    private MinMaxPyramid<double>? _maxPyramid;

    public CategoryBarPlan(BarLayerSpec spec, RowRange rows)
        : base(spec, rows) => SetColumns(spec.X.Column, spec.Y.Column);

    private CategoricalColumn Categories => (CategoricalColumn)Columns[0];

    public override void WaitForIndexes(PlotWorkerPool pool)
    {
        var y = Columns[1];
        if (_extremes is { } done && done.Version == y.Version)
        {
            return;
        }

        Release();
        var extremes = y.StartCategoryExtremes(Categories, pool, Rows);
        extremes.Completion.GetAwaiter().GetResult();
        if (extremes.Min.Length > 0)
        {
            (_minColumn, _maxColumn) = (Column.Wrap(extremes.Min), Column.Wrap(extremes.Max));
            (_minPyramid, _maxPyramid) = (pool.BuildPyramid(_minColumn), pool.BuildPyramid(_maxColumn));
            _minPyramid.WhenComplete().GetAwaiter().GetResult();
            _maxPyramid.WhenComplete().GetAwaiter().GetResult();
        }

        _extremes = extremes;
    }

    public override void AddRanges(RangeSink sink, bool ofX, DataTable data)
    {
        // x is the band's: its domain comes from the categories (design §4.2).
        if (ofX)
        {
            return;
        }

        var bar = (BarLayerSpec)Spec;
        sink.AddColumn(bar.Y.Column, data.GetName(bar.Y.Index));
        sink.AddBaseline(bar.Baseline);
    }

    public override void Record(RecordContext context)
    {
        var bar = (BarLayerSpec)Spec;
        if (_extremes is not { } extremes || context.X.Band is not { } band)
        {
            return;
        }

        var baseline = context.BaselinePixel(bar.Baseline);
        var fill = FillOf(bar.Fill, translucent: false);
        var origin = context.XOrigin;

        // A band's pixels are linear in its position: pixel(j) = a · j + b.
        var b = context.PixelX(0 - origin);
        var a = context.PixelX(1 - origin) - b;
        if (ElementColors is { } colors)
        {
            // Colored by a number, a bar a row in the rows' order (design §4.1, row G7c): the compile holds them to the cap.
            var x = Categories;
            var y = Columns[1];
            for (var i = Rows.Start; i < Rows.EndIn(x.Length); i++)
            {
                var code = x.GetCode(i);
                if (code < 0 || ElementColor(colors, i) is not { } color)
                {
                    continue;
                }

                var j = band.Position(code);
                var top = context.ElementY(y, i);
                if (!double.IsNaN(top))
                {
                    context.DrawRect((a * j) + b, (a * (j + band.Width)) + b, top, baseline, color);
                }
            }

            return;
        }

        if (Math.Abs(a * band.Width) >= 1)
        {
            for (var category = 0; category < band.Count; category++)
            {
                var j = band.Position(category);
                Bars(context, (a * j) + b, (a * (j + band.Width)) + b, extremes.Min[category], extremes.Max[category], baseline, fill);
            }

            return;
        }

        // Bands under a pixel: the categories whose band starts in a pixel column, from the pyramids' levels.
        var axis = context.X;
        for (var c = 0; c < axis.Size; c++)
        {
            var column = axis.Offset + c;
            var (first, last) =
                a > 0
                    ? (Math.Ceiling((column - b) / a), Math.Ceiling((column + 1 - b) / a) - 1)
                    : (Math.Floor((column + 1 - b) / a) + 1, Math.Floor((column - b) / a));
            var (from, to) = ((long)Math.Max(first, 0), (long)Math.Min(last, band.Count - 1));
            if (from > to)
            {
                continue;
            }

            // Positions from..to are categories from..to, or the reverse, a run either way.
            var start = band.Reverse ? band.Count - 1 - to : from;
            var low = _minPyramid!.GetMinMax(start, to - from + 1);
            var high = _maxPyramid!.GetMinMax(start, to - from + 1);
            if (low.Count > 0)
            {
                Bars(context, column, column + 1, low.Min, high.Max, baseline, fill);
            }
        }
    }

    public override void Release()
    {
        _minPyramid?.Dispose();
        _maxPyramid?.Dispose();
        _minColumn?.Dispose();
        _maxColumn?.Dispose();
        (_minPyramid, _maxPyramid, _minColumn, _maxColumn, _extremes) = (null, null, null, null, null);
    }

    /// <summary>Records a category's bars between two x pixels: from the baseline to its greatest y and to its least.</summary>
    private static void Bars(RecordContext context, double left, double right, double min, double max, double baseline, Color fill)
    {
        if (double.IsNaN(min))
        {
            return;
        }

        var (one, other) = (context.PixelY(max - context.YOrigin), context.PixelY(min - context.YOrigin));
        var (top, bottom) = (Math.Min(one, other), Math.Max(one, other));
        if (top < baseline)
        {
            context.DrawRect(left, right, top, baseline, fill);
        }

        if (bottom > baseline)
        {
            context.DrawRect(left, right, bottom, baseline, fill);
        }
    }
}

/// <summary>Error bars: a bar and two caps each up to 4W of them, else a line a pixel column from M4 (design §5.5.3).</summary>
internal sealed class ErrorBarPlan : LayerPlan
{
    private readonly int _lowSeries;
    private readonly int _highSeries;
    private readonly M4Point[] _lowOutput;
    private readonly M4Point[] _highOutput;
    private readonly ColumnEnvelope _low = new();
    private readonly ColumnEnvelope _high = new();

    public ErrorBarPlan(ErrorBarLayerSpec spec, PlanBuild build, RowRange rows)
        : base(spec, rows)
    {
        SetColumns(spec.X.Column, spec.YMin.Column, spec.YMax.Column);
        (_lowSeries, _lowOutput) = build.AddSeries(spec.X.Column, spec.YMin.Column, rows);
        (_highSeries, _highOutput) = build.AddSeries(spec.X.Column, spec.YMax.Column, rows);
    }

    public override void WaitForIndexes(PlotWorkerPool pool) => Columns[0].PrecomputeStatsIfMissing(pool)?.GetAwaiter().GetResult();

    public override void AddRanges(RangeSink sink, bool ofX, DataTable data)
    {
        var bars = (ErrorBarLayerSpec)Spec;
        if (ofX)
        {
            sink.AddColumn(bars.X.Column, data.GetName(bars.X.Index));
            return;
        }

        sink.AddColumn(bars.YMin.Column, data.GetName(bars.YMin.Index));
        sink.AddColumn(bars.YMax.Column, data.GetName(bars.YMax.Index));
    }

    public override void Record(RecordContext context)
    {
        var bars = (ErrorBarLayerSpec)Spec;
        var x = bars.X.Column;
        var first = Rows.Hold(x.LowerBound(context.Boundaries[0], context.XOrigin), x.Length);
        var end = Rows.Hold(x.LowerBound(context.Boundaries[^1], context.XOrigin), x.Length);
        if (end - first <= 4L * context.X.Size)
        {
            var half = bars.CapWidth * context.Scale / 2;
            var buffer = context.Points(9 * (int)(end - first));
            var count = 0;
            for (var i = first; i < end; i++)
            {
                var px = (float)context.ElementX(x, i);
                var low = (float)context.ElementY(bars.YMin.Column, i);
                var high = (float)context.ElementY(bars.YMax.Column, i);
                count = Segment(buffer, count, px, low, px, high);
                if (half > 0)
                {
                    count = Segment(buffer, count, px - half, low, px + half, low);
                    count = Segment(buffer, count, px - half, high, px + half, high);
                }
            }

            context.DrawPolyline(buffer[..count], Stroke(bars.Style));
            return;
        }

        // More bars than 4W: a line a pixel column, from the least of YMin to the greatest of YMax, without caps.
        Envelope(context, _lowOutput, _lowSeries, _low);
        Envelope(context, _highOutput, _highSeries, _high);
        var lines = context.Points(3 * _high.Width);
        var n = 0;
        for (var c = 0; c < _high.Width; c++)
        {
            var top = AreaPlan.Min(_low.Low[c], _high.Low[c]);
            var bottom = AreaPlan.Max(_low.High[c], _high.High[c]);
            if (double.IsNaN(_low.Low[c]) || double.IsNaN(_high.Low[c]))
            {
                continue;
            }

            var center = _high.Offset + c + 0.5f;
            n = Segment(lines, n, center, (float)top, center, (float)bottom);
        }

        context.DrawPolyline(lines[..n], Stroke(bars.Style));
    }

    /// <summary>Adds a segment and a break after it to a polyline that holds many (design §5.5.3).</summary>
    internal static int Segment(Span<Vector2> buffer, int count, float x0, float y0, float x1, float y1)
    {
        buffer[count++] = new Vector2(x0, y0);
        buffer[count++] = new Vector2(x1, y1);
        buffer[count++] = new Vector2(float.NaN);
        return count;
    }

    private static void Envelope(RecordContext context, M4Point[] output, int series, ColumnEnvelope envelope)
    {
        var points = output.AsSpan(0, context.Batch.GetResult(series).Count);
        _ = context.MapSeries(points, out var xs, out var ys);
        context.PointEnvelope(points, xs, ys, envelope);
    }
}

/// <summary>
/// Rules: a line each up to 4 per pixel along the rule's data axis, else one a pixel that holds one (design §5.5.3): 4W and
/// pixel columns for x, 4H and rows for y, the other way round when flipped.
/// </summary>
internal sealed class RulePlan : LayerPlan
{
    public RulePlan(RuleLayerSpec spec, RowRange rows)
        : base(spec, rows) => SetColumns(Channel(spec).Column);

    private static ColumnRef Channel(RuleLayerSpec rule) => rule.X.IsDefault ? rule.Y : rule.X;

    public override void WaitForIndexes(PlotWorkerPool pool)
    {
        var rule = (RuleLayerSpec)Spec;
        var column = Columns[0];
        if (!column.IsNonDecreasing(pool))
        {
            throw new InvalidOperationException(
                $"The {(rule.X.IsDefault ? "Y" : "X")} column of a rule layer is not non-decreasing without NaN; a rule's lines are found by searching it (design §5.5.3)."
            );
        }
    }

    public override void AddRanges(RangeSink sink, bool ofX, DataTable data)
    {
        var rule = (RuleLayerSpec)Spec;
        var reference = ofX ? rule.X : rule.Y;
        if (!reference.IsDefault)
        {
            sink.AddColumn(reference.Column, data.GetName(reference.Index));
        }
    }

    public override void Record(RecordContext context)
    {
        var rule = (RuleLayerSpec)Spec;
        var vertical = !rule.X.IsDefault;
        var column = Columns[0];
        var axis = vertical ? context.X : context.Y;
        var across = vertical ? context.Y : context.X;
        var origin = vertical ? context.XOrigin : context.YOrigin;
        ReadOnlySpan<double> boundaries = vertical ? context.Boundaries : context.YBoundaries();
        var first = Rows.Hold(column.LowerBound(boundaries[0], origin), column.Length);
        var end = Rows.Hold(column.LowerBound(boundaries[^1], origin), column.Length);
        var (from, to) = ((float)across.Offset, (float)(across.Offset + across.Size));
        var many = end - first > 4L * axis.Size;
        var buffer = context.Points(3 * (int)(many ? axis.Size : end - first));
        var count = 0;
        if (!many)
        {
            for (var i = first; i < end; i++)
            {
                var at = (float)(vertical ? context.ElementX(column, i) : context.ElementY(column, i));
                count = vertical ? ErrorBarPlan.Segment(buffer, count, at, from, at, to) : ErrorBarPlan.Segment(buffer, count, from, at, to, at);
            }
        }
        else
        {
            // One line in each pixel column (row) that holds an element: the search finds its first element past each boundary.
            var start = first;
            for (var k = 0; k < axis.Size; k++)
            {
                var next = Rows.Hold(column.LowerBound(boundaries[k + 1], origin), column.Length);
                if (next > start)
                {
                    // The boundaries run with the data; a descending axis runs the pixels the other way.
                    var at = axis.Offset + (axis.Descending ? axis.Size - k - 0.5f : k + 0.5f);
                    count = vertical ? ErrorBarPlan.Segment(buffer, count, at, from, at, to) : ErrorBarPlan.Segment(buffer, count, from, at, to, at);
                }

                start = next;
            }
        }

        context.DrawPolyline(buffer[..count], Stroke(rule.Style));
    }
}

/// <summary>Rectangles: every element, up to 131,072 (design §5.5.3).</summary>
internal sealed class RectPlan : LayerPlan
{
    public RectPlan(RectLayerSpec spec, RowRange rows)
        : base(spec, rows) => SetColumns(spec.XMin.Column, spec.XMax.Column, spec.YMin.Column, spec.YMax.Column);

    public override void AddRanges(RangeSink sink, bool ofX, DataTable data)
    {
        var rect = (RectLayerSpec)Spec;
        var (a, b) = ofX ? (rect.XMin, rect.XMax) : (rect.YMin, rect.YMax);
        sink.AddColumn(a.Column, data.GetName(a.Index));
        sink.AddColumn(b.Column, data.GetName(b.Index));
    }

    public override void Record(RecordContext context)
    {
        var rect = (RectLayerSpec)Spec;
        var xs0 = context.ElementPixels(rect.XMin.Column, ofX: true, 0);
        var xs1 = context.ElementPixels(rect.XMax.Column, ofX: true, 1);
        var ys0 = context.ElementPixels(rect.YMin.Column, ofX: false, 2);
        var ys1 = context.ElementPixels(rect.YMax.Column, ofX: false, 3);
        for (var i = (int)Rows.Start; i < Rows.EndIn(xs0.Length); i++)
        {
            // Corners mapped one by one: the smaller is the top left whatever the scales' directions. A corner that does
            // not map draws nothing.
            if (ElementColors is { } colors)
            {
                if (ElementColor(colors, i) is { } color)
                {
                    context.DrawRect(xs0[i], xs1[i], ys0[i], ys1[i], color);
                }

                continue;
            }

            context.DrawRect(xs0[i], xs1[i], ys0[i], ys1[i], FillOf(rect.Fill, translucent: false));
        }
    }
}

/// <summary>Segments: every element, up to 131,072, as one polyline broken by NaN (design §5.5.3).</summary>
internal sealed class SegmentPlan : LayerPlan
{
    public SegmentPlan(SegmentLayerSpec spec, RowRange rows)
        : base(spec, rows) => SetColumns(spec.X.Column, spec.Y.Column, spec.XEnd.Column, spec.YEnd.Column);

    public override void AddRanges(RangeSink sink, bool ofX, DataTable data)
    {
        var segment = (SegmentLayerSpec)Spec;
        var (a, b) = ofX ? (segment.X, segment.XEnd) : (segment.Y, segment.YEnd);
        sink.AddColumn(a.Column, data.GetName(a.Index));
        sink.AddColumn(b.Column, data.GetName(b.Index));
    }

    public override void Record(RecordContext context)
    {
        var segment = (SegmentLayerSpec)Spec;
        var xs0 = context.ElementPixels(segment.X.Column, ofX: true, 0);
        var ys0 = context.ElementPixels(segment.Y.Column, ofX: false, 1);
        var xs1 = context.ElementPixels(segment.XEnd.Column, ofX: true, 2);
        var ys1 = context.ElementPixels(segment.YEnd.Column, ofX: false, 3);
        var buffer = context.Points(3 * xs0.Length);
        var count = 0;
        for (var i = (int)Rows.Start; i < Rows.EndIn(xs0.Length); i++)
        {
            // An end that does not map is NaN, which breaks the polyline on both sides: the segment draws nothing.
            count = ErrorBarPlan.Segment(buffer, count, (float)xs0[i], (float)ys0[i], (float)xs1[i], (float)ys1[i]);
        }

        context.DrawPolyline(buffer[..count], Stroke(segment.Style));
    }
}

/// <summary>A user's geom: its ranges and its recording through <see cref="IGeom"/> (design §5.5.3).</summary>
internal sealed class CustomPlan : LayerPlan
{
    private readonly GeomRanges _ranges = new();

    public CustomPlan(CustomLayerSpec spec)
        : base(spec) => SetColumns();

    /// <summary>Gets the number of calls of <see cref="IGeom.Learn"/>. Tests only.</summary>
    public int Learned { get; private set; }

    public override void BeginLearning()
    {
        // Once a learning, for both axes: a scale with a given domain does not ask for its axis's ranges.
        _ranges.Clear();
        ((CustomLayerSpec)Spec).Geom.Learn(_ranges);
        Learned++;
    }

    public override void AddRanges(RangeSink sink, bool ofX, DataTable data)
    {
        var (min, max) = ofX ? (_ranges.XMin, _ranges.XMax) : (_ranges.YMin, _ranges.YMax);
        if (min is { } low && max is { } high)
        {
            sink.Add(low, high, "a custom geom's range");
        }
    }

    public override void Record(RecordContext context)
    {
        context.Frame.Open(context.X, context.Y, context.Scale, context.Flipped, context.Panel, context.Polar ? context.PolarMap : null, SeriesColor);
        try
        {
            ((CustomLayerSpec)Spec).Geom.Record(context.Frame, context.List);
        }
        finally
        {
            context.Frame.Close();
            context.ClipToPanel();
        }
    }
}
