using System.Numerics;
using HyperPlot.Interaction;
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

    /// <summary>Returns the line's M4 points of the last extraction from its batch. Tests only.</summary>
    internal ReadOnlySpan<M4Point> Points(M4Batch batch) => _output.AsSpan(0, batch.GetResult(_series).Count);

    public override LayerReadiness Poll(LayerIndexes indexes) => indexes.XStats(Columns[0]);

    internal override void Hit(ref HitScan scan) => scan.Series(0, Columns[0], Columns[1], Rows, derived: false);

    internal override Color HitColor(int series) => Stroke(((LineLayerSpec)Spec).Style).Color;

    internal override SelectionShape? Shape => new(ShapeKind.Point, Columns[0], null, Columns[1], Rows, Sorted: true);

    public override void AddRanges(RangeSink sink, bool ofX, DataTable data)
    {
        var line = (LineLayerSpec)Spec;
        var reference = ofX ? line.X : line.Y;
        AddColumn(sink, reference.Column, data.GetName(reference.Index));
    }

    public override void Record(RecordContext context)
    {
        var points = _output.AsSpan(0, context.Batch.GetResult(_series).Count);
        _ = context.MapSeries(points, out var xs, out var ys);
        var style = Stroke(((LineLayerSpec)Spec).Style);
        context.Polyline(points, xs, ys, style);
        KeepLine(points, xs, ys, style, context.Flipped);
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

    public override LayerReadiness Poll(LayerIndexes indexes) => indexes.XStats(Columns[0]);

    internal override void Hit(ref HitScan scan) => scan.Series(0, Columns[0], Columns[1], Rows, derived: false);

    internal override Color HitColor(int series) => Stroke(((StepLayerSpec)Spec).Style).Color;

    internal override SelectionShape? Shape => new(ShapeKind.Point, Columns[0], null, Columns[1], Rows, Sorted: true);

    public override void AddRanges(RangeSink sink, bool ofX, DataTable data)
    {
        var step = (StepLayerSpec)Spec;
        var reference = ofX ? step.X : step.Y;
        AddColumn(sink, reference.Column, data.GetName(reference.Index));
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
        KeepLine(buffer[..count], Stroke(step.Style), context.Flipped);
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

    public override LayerReadiness Poll(LayerIndexes indexes) => indexes.XStats(Columns[0]);

    internal override void Hit(ref HitScan scan) => scan.Series(0, Columns[0], Columns[1], Rows, derived: false);

    internal override Color HitColor(int series) => (((AreaLayerSpec)Spec).Fill ?? SeriesColor) with { A = 255 };

    internal override SelectionShape? Shape => new(ShapeKind.Point, Columns[0], null, Columns[1], Rows, Sorted: true);

    public override void AddRanges(RangeSink sink, bool ofX, DataTable data)
    {
        var area = (AreaLayerSpec)Spec;
        var reference = ofX ? area.X : area.Y;
        AddColumn(sink, reference.Column, data.GetName(reference.Index));
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
            KeepLine(points, xs, ys, Stroke(outline), context.Flipped);
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

    public override LayerReadiness Poll(LayerIndexes indexes) => indexes.XStats(Columns[0]);

    internal override int HitWidth => 2;

    internal override Color HitColor(int series) => (((BandLayerSpec)Spec).Fill ?? SeriesColor) with { A = 255 };

    /// <summary>Answers for the band's low (series 0) and high (series 1) edges.</summary>
    internal override void Hit(ref HitScan scan)
    {
        scan.Series(0, Columns[0], Columns[1], Rows, derived: false);
        scan.Series(1, Columns[0], Columns[2], Rows, derived: false);
    }

    internal override SelectionShape? Shape => new(ShapeKind.Span, Columns[0], Columns[1], Columns[2], Rows, Sorted: true);

    public override void AddRanges(RangeSink sink, bool ofX, DataTable data)
    {
        var band = (BandLayerSpec)Spec;
        if (ofX)
        {
            AddColumn(sink, band.X.Column, data.GetName(band.X.Index));
            return;
        }

        AddColumn(sink, band.YMin.Column, data.GetName(band.YMin.Index));
        AddColumn(sink, band.YMax.Column, data.GetName(band.YMax.Index));
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
        SetColumns(AndColors(spec.X.Column, spec.Y.Column));
        (_series, _output) = build.AddSeries(spec.X.Column, spec.Y.Column, rows);
    }

    /// <summary>Gets the width the bars are drawn with, in data units: read from the X's statistics, so after the wait.</summary>
    private double Width =>
        ((BarLayerSpec)Spec).Width
        ?? (
            Columns[0] is UniformColumn uniform ? uniform.Step * 0.9
            : TryGetRange(Columns[0], out var min, out var max) ? DefaultWidth(min.Value, max.Value, RowCount(Columns[0].Length))
            : 0.9
        );

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

        return x.TryGetRange(out var min, out var max) ? DefaultWidth(min.Value, max.Value, x.Length) : 0.9;
    }

    /// <summary>Returns the default width of a stored X of a range and a number of rows: 0.9 times its mean spacing, or 0.9.</summary>
    internal static double DefaultWidth(double min, double max, long count) => count > 1 && max > min ? (max - min) / (count - 1) * 0.9 : 0.9;

    public override LayerReadiness Poll(LayerIndexes indexes) => indexes.XStats(Columns[0]);

    /// <summary>Answers as its bars are drawn (row I2c, Issue #355 Decisions 1, 2, 5): a dodged part in its place.</summary>
    internal override void Hit(ref HitScan scan)
    {
        var bar = (BarLayerSpec)Spec;
        var (shift, half) = Slot(bar);
        scan.Bars(0, Columns[0], null, Columns[1], Rows, bar.Baseline, shift, half, derived: false);
    }

    internal override Color HitColor(int series) => (((BarLayerSpec)Spec).Fill ?? SeriesColor) with { A = 255 };

    internal override double HitAlong(in GeomFrame.Axis x, in Interaction.SeriesHit hit) =>
        HitScan.PixelOfValue(x, hit.X + Slot((BarLayerSpec)Spec).Shift);

    /// <summary>Returns a bar's shift and half width in data units: a dodged part's place of the width (row G5a), else the whole.</summary>
    private (double Shift, double Half) Slot(BarLayerSpec bar) =>
        DodgeSlot is { } slot
            ? (Width * (((slot.Index + 0.5) / slot.Count) - 0.5), Width / slot.Count * (1 - DodgePadding(bar)) / 2)
            : (0.0, Width / 2);

    internal override SelectionShape? Shape => new(ShapeKind.Span, Columns[0], null, Columns[1], Rows, Sorted: true, ((BarLayerSpec)Spec).Baseline);

    public override void AddRanges(RangeSink sink, bool ofX, DataTable data)
    {
        var bar = (BarLayerSpec)Spec;
        if (!ofX)
        {
            AddColumn(sink, bar.Y.Column, data.GetName(bar.Y.Index));
            sink.AddBaseline(bar.Baseline);
            return;
        }

        AddColumn(sink, bar.X.Column, data.GetName(bar.X.Index));
        if (TryGetRange(bar.X.Column, out var min, out var max))
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
        var first = x.LowerBound(boundaries[0] - half, context.XOrigin, Rows);
        var end = x.LowerBound(boundaries[^1] + half, context.XOrigin, Rows);
        var baseline = context.BaselinePixel(bar.Baseline);
        var barFill = FillOf(bar.Fill, translucent: false);
        if (ElementColors is { } colors)
        {
            // A color per bar cannot join a pixel column (design §4.1, row G7c): each bar, up to the cap.
            if (end - first > MaxElements)
            {
                throw new InvalidOperationException(
                    $"{end - first} bars are in view, colored one by one by a number; at most {MaxElements} are drawn so (design §4.1). Row R3e draws more as a density."
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
            // A dodged part (row G5a): its place of the width, in data units, x + Width · ((k + 0.5) / n − 0.5).
            var (shift, drawn) = Slot(bar);
            for (var i = first; i < end; i++)
            {
                var d = x.DifferenceAt(i, context.XOrigin) + shift;
                var left = context.PixelX(d - drawn);
                var right = context.PixelX(d + drawn);
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
    // The envelope drawn, and the one of a newer version of the Y column that the workers make meanwhile (row W1b1).
    private CategoryEnvelope? _done;
    private CategoryEnvelope? _making;

    public CategoryBarPlan(BarLayerSpec spec, RowRange rows)
        : base(spec, rows) => SetColumns(AndColors(spec.X.Column, spec.Y.Column));

    private CategoricalColumn Categories => (CategoricalColumn)Columns[0];

    public override LayerReadiness Poll(LayerIndexes indexes) =>
        CategoryEnvelope.Step(ref _done, ref _making, Columns[1], Columns[1], Categories, Rows, indexes);

    /// <summary>
    /// Answers with a category's bar as drawn (row I2c, Issue #355 Decisions 3 and 10): the end on the cursor's side of the
    /// baseline and the row it came from; bands under a pixel, the pixel column's category of that extreme. The hit's x is the
    /// category's number.
    /// </summary>
    internal override void Hit(ref HitScan scan)
    {
        if (_done is not { } envelope || scan.X.Band is not { Count: > 0 } band)
        {
            return;
        }

        var baseline = ((BarLayerSpec)Spec).Baseline;
        var basePixel = HitScan.PixelOfValue(scan.Y, baseline);
        var above = scan.CursorValue(scan.Y) >= baseline;
        var (a, b) = CategoryHits.Line(scan.X);
        var (best, bestRow, bestCategory, bestValue) = (scan.Mode == HitMode.X ? double.PositiveInfinity : scan.Radius, -1L, -1L, double.NaN);
        var provisional = false;
        if (CategoryHits.Wide(a, band))
        {
            var (from, to) =
                scan.Mode == HitMode.X
                    ? (CategoryHits.PositionAt(scan.Along, a, b, band), CategoryHits.PositionAt(scan.Along, a, b, band))
                    : CategoryHits.Reach(scan.Along, scan.Radius, a, b, band);
            for (var j = from; j <= to; j++)
            {
                var (left, right) = Place((a * j) + b, (a * (j + band.Width)) + b);
                Consider(ref scan, band.Position(j), left, right);
            }
        }
        else
        {
            var (first, last) =
                scan.Mode == HitMode.X
                    ? (Math.Floor(scan.Along), Math.Floor(scan.Along))
                    : (Math.Floor(scan.Along - scan.Radius), Math.Floor(scan.Along + scan.Radius));
            for (var column = first; column <= last; column++)
            {
                var (from, to) = CategoryHits.InColumn(column, a, b, band);
                if (from > to)
                {
                    continue;
                }

                var start = CategoryHits.FirstCategory(from, to, band);
                var category = CategoryHits.Extreme(
                    above ? envelope.MaxPyramid! : envelope.MinPyramid!,
                    start,
                    to - from + 1,
                    above,
                    ref provisional
                );
                if (category >= 0)
                {
                    Consider(ref scan, category, column, column + 1);
                }
            }
        }

        if (bestRow >= 0)
        {
            scan.Add(0, bestRow, bestCategory, bestValue, best, derived: false, provisional);
        }

        void Consider(ref HitScan scan, long category, double left, double right)
        {
            var (value, row) = CategoryHits.Side(envelope.Low, category, above, baseline);
            if (row < 0)
            {
                return;
            }

            var distance = scan.Box(left, right, basePixel, HitScan.PixelOfValue(scan.Y, value));
            if (distance <= best && (bestRow < 0 || distance < best || scan.Mode == HitMode.X))
            {
                (best, bestRow, bestCategory, bestValue) = (distance, row, category, value);
            }
        }
    }

    internal override Color HitColor(int series) => (((BarLayerSpec)Spec).Fill ?? SeriesColor) with { A = 255 };

    internal override double HitAlong(in GeomFrame.Axis x, in Interaction.SeriesHit hit) => BandAlong(x, (int)hit.X);

    internal override SelectionShape? Shape => new(ShapeKind.Span, Columns[0], null, Columns[1], Rows, Sorted: false, ((BarLayerSpec)Spec).Baseline);

    public override void AddRanges(RangeSink sink, bool ofX, DataTable data)
    {
        // x is the band's: its domain comes from the categories (design §4.2).
        if (ofX)
        {
            return;
        }

        var bar = (BarLayerSpec)Spec;
        AddColumn(sink, bar.Y.Column, data.GetName(bar.Y.Index));
        sink.AddBaseline(bar.Baseline);
    }

    public override void Record(RecordContext context)
    {
        var bar = (BarLayerSpec)Spec;
        if (_done is not { } envelope || context.X.Band is not { } band)
        {
            return;
        }

        var extremes = envelope.Low;

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
            var (first, last) = band.Visible(a, b, context.X.Offset, context.X.Size);
            for (var category = first; category <= last; category++)
            {
                var j = band.Position(category);
                var (from, to) = Place((a * j) + b, (a * (j + band.Width)) + b);
                Bars(context, from, to, extremes.Min[category], extremes.Max[category], baseline, fill);
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
            var low = envelope.MinPyramid!.GetMinMax(start, to - from + 1);
            var high = envelope.MaxPyramid!.GetMinMax(start, to - from + 1);
            if (low.Count > 0)
            {
                Bars(context, column, column + 1, low.Min, high.Max, baseline, fill);
            }
        }
    }

    public override void Release()
    {
        _done?.Free();
        _making?.Free();
        (_done, _making) = (null, null);
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

    // The caps' half width in pixels at the last recording: the hit test measures the caps as drawn.
    private double _cap;

    public ErrorBarPlan(ErrorBarLayerSpec spec, PlanBuild build, RowRange rows)
        : base(spec, rows)
    {
        SetColumns(spec.X.Column, spec.YMin.Column, spec.YMax.Column);
        (_lowSeries, _lowOutput) = build.AddSeries(spec.X.Column, spec.YMin.Column, rows);
        (_highSeries, _highOutput) = build.AddSeries(spec.X.Column, spec.YMax.Column, rows);
    }

    public override LayerReadiness Poll(LayerIndexes indexes) => indexes.XStats(Columns[0]);

    internal override int HitWidth => 2;

    /// <summary>Answers with each row's low end (series 0) and high end (series 1), as drawn (row I2c, Issue #355 Decisions 6 and 11).</summary>
    internal override void Hit(ref HitScan scan) => scan.ErrorBars(Columns[0], Columns[1], Columns[2], Rows, _cap);

    internal override Color HitColor(int series) => Stroke(((ErrorBarLayerSpec)Spec).Style).Color;

    internal override SelectionShape? Shape => new(ShapeKind.Span, Columns[0], Columns[1], Columns[2], Rows, Sorted: true);

    public override void AddRanges(RangeSink sink, bool ofX, DataTable data)
    {
        var bars = (ErrorBarLayerSpec)Spec;
        if (ofX)
        {
            AddColumn(sink, bars.X.Column, data.GetName(bars.X.Index));
            return;
        }

        AddColumn(sink, bars.YMin.Column, data.GetName(bars.YMin.Index));
        AddColumn(sink, bars.YMax.Column, data.GetName(bars.YMax.Index));
    }

    public override void Record(RecordContext context)
    {
        var bars = (ErrorBarLayerSpec)Spec;
        var x = bars.X.Column;
        var first = x.LowerBound(context.Boundaries[0], context.XOrigin, Rows);
        var end = x.LowerBound(context.Boundaries[^1], context.XOrigin, Rows);
        _cap = 0;
        if (end - first <= 4L * context.X.Size)
        {
            var half = bars.CapWidth * context.Scale / 2;
            _cap = half;
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
    private long _checked = -1;

    public RulePlan(RuleLayerSpec spec, RowRange rows)
        : base(spec, rows) => SetColumns(Channel(spec).Column);

    private static ColumnRef Channel(RuleLayerSpec rule) => rule.X.IsDefault ? rule.Y : rule.X;

    public override LayerReadiness Poll(LayerIndexes indexes)
    {
        // The rows a rule searches, the run of a facet's panel or a split's category (row G6b), checked once a version, from
        // the statistics the workers computed: until they are there, the rule is not drawn (row W1b1).
        var rule = (RuleLayerSpec)Spec;
        var column = Columns[0];
        var version = column.Version;
        if (version == _checked)
        {
            return LayerReadiness.Ready;
        }

        bool nonDecreasing;
        if (indexes.Waits)
        {
            nonDecreasing = column.IsNonDecreasing(indexes.Pool, Rows);
        }
        else if (indexes.XStats(column) == LayerReadiness.Pending || !column.TryIsNonDecreasing(Rows, out nonDecreasing))
        {
            return LayerReadiness.Pending;
        }

        if (!nonDecreasing)
        {
            throw new InvalidOperationException(
                $"The {(rule.X.IsDefault ? "Y" : "X")} column of a rule layer is not non-decreasing without NaN; a rule's lines are found by searching it (design §5.5.3)."
            );
        }

        _checked = version;
        return LayerReadiness.Ready;
    }

    public override void AddRanges(RangeSink sink, bool ofX, DataTable data)
    {
        var rule = (RuleLayerSpec)Spec;
        var reference = ofX ? rule.X : rule.Y;
        if (!reference.IsDefault)
        {
            AddColumn(sink, reference.Column, data.GetName(reference.Index));
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
        var first = column.LowerBound(boundaries[0], origin, Rows);
        var end = column.LowerBound(boundaries[^1], origin, Rows);
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
                var next = column.LowerBound(boundaries[k + 1], origin, Rows);
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
    // The corners' pixels of the last recording, kept for the hit test (row I2c, Issue #355 Decision 7).
    private double[] _x0 = [];
    private double[] _x1 = [];
    private double[] _y0 = [];
    private double[] _y1 = [];
    private Column[] _corners = [];
    private long _mapped;

    public RectPlan(RectLayerSpec spec, RowRange rows)
        : base(spec, rows) => SetColumns(AndColors(spec.XMin.Column, spec.XMax.Column, spec.YMin.Column, spec.YMax.Column));

    /// <summary>Answers from the pixels the last recording mapped: every rectangle, up to 131,072 (row I2c).</summary>
    internal override void Hit(ref HitScan scan)
    {
        var end = Math.Min(Rows.EndIn(_mapped), _mapped);
        if (end > Rows.Start)
        {
            scan.Rects(_x0, _x1, _y0, _y1, Rows.Start, end, _corners);
        }
    }

    internal override Color HitColor(int series) => (((RectLayerSpec)Spec).Fill ?? SeriesColor) with { A = 255 };

    internal override SelectionShape? Shape => new(ShapeKind.Box, Columns[0], null, Columns[2], Rows, Sorted: false, X1: Columns[1], Y1: Columns[3]);

    public override void AddRanges(RangeSink sink, bool ofX, DataTable data)
    {
        var rect = (RectLayerSpec)Spec;
        var (a, b) = ofX ? (rect.XMin, rect.XMax) : (rect.YMin, rect.YMax);
        AddColumn(sink, a.Column, data.GetName(a.Index));
        AddColumn(sink, b.Column, data.GetName(b.Index));
    }

    public override void Record(RecordContext context)
    {
        var rect = (RectLayerSpec)Spec;
        var xs0 = context.ElementPixelsInto(rect.XMin.Column, ofX: true, ref _x0);
        var xs1 = context.ElementPixelsInto(rect.XMax.Column, ofX: true, ref _x1);
        var ys0 = context.ElementPixelsInto(rect.YMin.Column, ofX: false, ref _y0);
        var ys1 = context.ElementPixelsInto(rect.YMax.Column, ofX: false, ref _y1);
        if (_corners.Length == 0)
        {
            _corners = [rect.XMin.Column, rect.XMax.Column, rect.YMin.Column, rect.YMax.Column];
        }

        _mapped = Math.Min(Math.Min(xs0.Length, xs1.Length), Math.Min(ys0.Length, ys1.Length));
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
                else
                {
                    // Not drawn, so not hit either (design §6.3): the hit test passes a rectangle with a NaN corner.
                    _x0[i] = double.NaN;
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
        AddColumn(sink, a.Column, data.GetName(a.Index));
        AddColumn(sink, b.Column, data.GetName(b.Index));
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

/// <summary>
/// Points on the CPU path (design §5.5.2, row R3a): every point mapped in doubles each recording and recorded as a marker,
/// for a layer of at most 131,072 rows.
/// </summary>
internal sealed class PointPlan : LayerPlan
{
    // The points' pixels of the last recording, along and across, kept for the hit test (design §6.3).
    private double[] _x = [];
    private double[] _y = [];
    private long _mapped;
    private double _radius;

    // The radius of each point of the last recording in physical pixels, by row, when the layer has a SizeBy; else empty.
    private float[] _radii = [];
    private double[] _diameters = [];
    private bool _sized;

    public PointPlan(PointLayerSpec spec, RowRange rows)
        : base(spec, rows) => SetColumns(Read(spec, AndColors(spec.X.Column, spec.Y.Column)));

    /// <summary>Answers from the pixels the last recording mapped: every point, up to 131,072 (design §6.3).</summary>
    internal override void Hit(ref HitScan scan)
    {
        var end = Math.Min(Rows.EndIn(_mapped), _mapped);
        if (end > Rows.Start)
        {
            scan.Points(_x, _y, Rows.Start, end, Columns[0], Columns[1], _radius, _sized ? _radii : default);
        }
    }

    internal override Color HitColor(int series) => (((PointLayerSpec)Spec).Color ?? SeriesColor) with { A = 255 };

    public override void AddRanges(RangeSink sink, bool ofX, DataTable data)
    {
        var point = (PointLayerSpec)Spec;
        var channel = ofX ? point.X : point.Y;
        AddColumn(sink, channel.Column, data.GetName(channel.Index));
    }

    public override void Record(RecordContext context)
    {
        var point = (PointLayerSpec)Spec;
        var xs = context.ElementPixelsInto(point.X.Column, ofX: true, ref _x);
        var ys = context.ElementPixelsInto(point.Y.Column, ofX: false, ref _y);
        _mapped = Math.Min(xs.Length, ys.Length);
        _radius = point.Size * context.Scale / 2;
        var start = (int)Rows.Start;
        var end = (int)Math.Min(Rows.EndIn(_mapped), _mapped);
        if (end <= start)
        {
            return;
        }

        var centers = context.Points(end - start)[..(end - start)];
        var style = new MarkerStyle(Faded(point.Color ?? SeriesColor, point.Opacity), point.Size, point.Shape);
        _sized = point.SizeBy is not null;
        if (_sized || point.ShapeBy is not null)
        {
            // The reach of the hit test's first cut is the largest radius drawn.
            _radius = _sized ? 0 : _radius;
            RecordLooks(context, point, centers, xs, ys, start, end, style);
            return;
        }

        if (ElementColors is not { } values)
        {
            // A point that does not map is NaN, which the list passes.
            for (var i = start; i < end; i++)
            {
                centers[i - start] = new Vector2((float)xs[i], (float)ys[i]);
            }

            context.DrawMarkers(centers, default, style);
            return;
        }

        // A point whose color value is NaN draws nothing (design §4.1): it is left out.
        var colors = context.MarkerColors(end - start);
        var count = 0;
        for (var i = start; i < end; i++)
        {
            if (ElementColor(values, i) is { } color)
            {
                centers[count] = new Vector2((float)xs[i], (float)ys[i]);
                colors[count++] = Faded(color, point.Opacity);
            }
            else
            {
                // Not drawn, so not hit either (design §6.3): the hit test passes a point whose pixel is NaN.
                _x[i] = double.NaN;
            }
        }

        context.DrawMarkers(centers[..count], colors[..count], style);
    }

    /// <summary>
    /// Records the points of a layer with a SizeBy or a ShapeBy (design §5.5.2 "大きさと形のチャネル"): each with its own
    /// diameter and shape through the plot's scales, and its own color when the layer is colored by a number. A point whose
    /// size or color value is NaN draws nothing and is not hit.
    /// </summary>
    private void RecordLooks(
        RecordContext context,
        PointLayerSpec point,
        Span<Vector2> centers,
        ReadOnlySpan<double> xs,
        ReadOnlySpan<double> ys,
        int start,
        int end,
        in MarkerStyle style
    )
    {
        var length = end - start;
        var values = ElementColors;
        var sizeBy = point.SizeBy?.Column;
        var shapeBy = point.ShapeBy?.Column as CategoricalColumn;
        var colors = values is null ? default : context.MarkerColors(length);
        var sizes = sizeBy is null ? default : context.MarkerSizes(length);
        var shapes = shapeBy is null ? default : context.MarkerShapes(length);
        if (sizeBy is not null && _radii.Length < end)
        {
            _radii = new float[Math.Max(end, _x.Length)];
        }

        // The sizes read and mapped at once: one at a time through the column took 2 to 4 ms of 131,072 points (Issue #456).
        var diameters = default(ReadOnlySpan<double>);
        if (sizeBy is not null)
        {
            if (_diameters.Length < sizeBy.Length)
            {
                _diameters = new double[sizeBy.Length];
            }

            var all = _diameters.AsSpan(0, (int)sizeBy.Length);
            sizeBy.ReadDifferences(all, 0);
            Sizes!.Diameters(all, context.Scale);
            diameters = all;
        }

        var count = 0;
        for (var i = start; i < end; i++)
        {
            var color = values is null ? default(Color?) : ElementColor(values, i);
            var size = sizeBy is null ? 0 : (float)diameters[i];
            if ((values is not null && color is null) || float.IsNaN(size))
            {
                // Not drawn, so not hit either (design §6.3): the hit test passes a point whose pixel is NaN.
                _x[i] = double.NaN;
                continue;
            }

            centers[count] = new Vector2((float)xs[i], (float)ys[i]);
            if (values is not null)
            {
                colors[count] = Faded(color!.Value, point.Opacity);
            }

            if (sizeBy is not null)
            {
                (sizes[count], _radii[i]) = (size, size / 2);
                _radius = Math.Max(_radius, size / 2);
            }

            if (shapeBy is not null)
            {
                shapes[count] = Shapes!.Of(shapeBy.GetCode(i));
            }

            count++;
        }

        context.DrawMarkers(
            centers[..count],
            values is null ? default : colors[..count],
            sizeBy is null ? default : sizes[..count],
            shapeBy is null ? default : shapes[..count],
            style
        );
    }

    /// <summary>Returns the columns a point layer reads: its positions, its numeric color, and its size and shape columns.</summary>
    private static Column[] Read(PointLayerSpec spec, Column[] columns) =>
        (spec.SizeBy, spec.ShapeBy) switch
        {
            ({ } size, { } shape) => [.. columns, size.Column, shape.Column],
            ({ } size, null) => [.. columns, size.Column],
            (null, { } shape) => [.. columns, shape.Column],
            _ => columns,
        };

    /// <summary>Returns a color with its alpha multiplied by an opacity.</summary>
    private static Color Faded(Color color, double opacity) => opacity >= 1 ? color : color with { A = (byte)Math.Round(color.A * opacity) };
}
