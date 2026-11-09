using System.Buffers;
using System.Numerics;
using HyperPlot.Interaction;
using HyperPlot.Rendering;

namespace HyperPlot;

/// <summary>
/// Bars over categories stacked by the groups of a color column (design §5.5.3, row G5a): the workers sum each cell's
/// positive and negative values in one pass, a version of the Y column, then accumulate them from the last group, so that
/// group k's pieces are [P(k + 1), P(k)] above 0 and [N(k), N(k + 1)] below. A frame draws a piece a group a category, or
/// a pixel column at a time from pyramids over the accumulated arrays when bands are under a pixel.
/// </summary>
internal sealed class StackedCategoryBarPlan : LayerPlan
{
    /// <summary>The bytes the partial sums of a pass take at most: groups of chunks sum apart, then join in their order.</summary>
    private const long PartialBytes = 256L << 20;

    /// <summary>The most groups of chunks a pass sums apart: a number of the cells only, so a sum is the same on any machine.</summary>
    private const int MaxPartials = 8;

    private readonly CategoricalColumn _x;
    private readonly Column _y;
    private readonly CategoricalColumn? _groups;
    private readonly int _groupCount;
    private readonly bool _fill;
    private double[] _extremes = [];

    // The sums drawn, and those of a newer version of the Y column that the workers make meanwhile (row W1b1).
    private Sums? _done;
    private Sums? _making;

    public StackedCategoryBarPlan(BarLayerSpec spec, RowRange rows)
        : base(spec, rows)
    {
        _x = (CategoricalColumn)spec.X.Column;
        _y = spec.Y.Column;
        _groups = SplitPlan.Splits(spec) ? (CategoricalColumn)(spec.FillBy ?? spec.ColorBy)!.Value.Column : null;
        _groupCount = _groups?.CategoryCount ?? 1;
        _fill = PositionOf(spec) == PositionKind.Fill;
        SetColumns(_groups is null ? [_x, _y] : [_x, _y, _groups]);
    }

    public override LayerReadiness Poll(LayerIndexes indexes)
    {
        // The sums of the Y column's version, made once; until they are, those of the version before are drawn (row W1b1).
        var version = _y.Version;
        if (_done?.Version == version)
        {
            return LayerReadiness.Ready;
        }

        if (_making?.Version != version)
        {
            _making?.Free();
            _making = null;
            _making = new Sums(version, Sum(indexes.Pool));
        }

        if (!_making.Advance(this, indexes))
        {
            return _done is null ? LayerReadiness.Pending : LayerReadiness.Previous;
        }

        _done?.Free();
        (_done, _making) = (_making, null);
        return LayerReadiness.Ready;
    }

    public override void AddRanges(RangeSink sink, bool ofX, DataTable data)
    {
        // x is the band's: its domain comes from the categories (design §4.2, row G7b).
        if (ofX || _done is not { Range: { } range } done)
        {
            return;
        }

        var bar = (BarLayerSpec)Spec;
        var name = data.GetName(bar.Y.Index);
        sink.AddBaseline(0);
        if (_fill)
        {
            sink.Add(done.AnyNegative ? -1 : 0, 1, name);
            return;
        }

        sink.Add(range.Min, range.Max, name);
    }

    public override void Record(RecordContext context)
    {
        if (_done is not { } done || context.X.Band is not { Count: > 0 } band)
        {
            return;
        }

        var (positive, negative, pyramids, anyNegative) = (done.Positive, done.Negative, done.Pyramids, done.AnyNegative);

        var bar = (BarLayerSpec)Spec;
        var origin = context.XOrigin;
        var b = context.PixelX(0 - origin);
        var a = context.PixelX(1 - origin) - b;
        if (Math.Abs(a * band.Width) >= 1)
        {
            var (first, last) = band.Visible(a, b, context.X.Offset, context.X.Size);
            for (var k = 0; k < _groupCount; k++)
            {
                var color = ColorOf(bar, k);
                for (var category = first; category <= last; category++)
                {
                    var j = band.Position(category);
                    var (from, to) = ((a * j) + b, (a * (j + band.Width)) + b);
                    Piece(context, from, to, Lower(positive, k, category), positive[k][category], color);
                    if (anyNegative)
                    {
                        Piece(context, from, to, negative[k][category], Lower(negative, k, category), color);
                    }
                }
            }

            return;
        }

        // Bands under a pixel: the categories whose band starts in a pixel column, from the pyramids' levels (row G7b). Each
        // accumulated array is asked once a column, for both ends: a group's top and the next group's bottom come from the
        // same array. Then the pieces are drawn in the groups' order.
        var axis = context.X;
        var arrays = pyramids.Length;
        if (_extremes.Length < 2 * arrays * axis.Size)
        {
            _extremes = new double[2 * arrays * axis.Size];
        }

        for (var c = 0; c < axis.Size; c++)
        {
            var column = axis.Offset + c;
            var (first, last) =
                a > 0
                    ? (Math.Ceiling((column - b) / a), Math.Ceiling((column + 1 - b) / a) - 1)
                    : (Math.Floor((column + 1 - b) / a) + 1, Math.Floor((column - b) / a));
            var (from, to) = ((long)Math.Max(first, 0), (long)Math.Min(last, band.Count - 1));
            for (var p = 0; p < arrays; p++)
            {
                var at = 2 * ((c * arrays) + p);
                if (from > to)
                {
                    (_extremes[at], _extremes[at + 1]) = (double.NaN, double.NaN);
                    continue;
                }

                var extremes = pyramids[p].GetMinMax(band.Reverse ? band.Count - 1 - to : from, to - from + 1);
                (_extremes[at], _extremes[at + 1]) = (extremes.Min, extremes.Max);
            }
        }

        for (var k = 0; k < _groupCount; k++)
        {
            var color = ColorOf(bar, k);
            for (var c = 0; c < axis.Size; c++)
            {
                var at = 2 * c * arrays;
                if (double.IsNaN(_extremes[at]))
                {
                    continue;
                }

                var column = axis.Offset + c;
                var top = _extremes[at + (2 * k) + 1];
                var bottom = k + 1 < _groupCount ? _extremes[at + (2 * (k + 1))] : 0;
                Piece(context, column, column + 1, bottom, top, color);
                if (anyNegative)
                {
                    var low = _extremes[at + (2 * (_groupCount + k))];
                    var high = k + 1 < _groupCount ? _extremes[at + (2 * (_groupCount + k + 1)) + 1] : 0;
                    Piece(context, column, column + 1, low, high, color);
                }
            }
        }
    }

    public override void Release()
    {
        _done?.Free();
        _making?.Free();
        (_done, _making) = (null, null);
    }

    /// <summary>Returns the lower end of group k's piece in a category: the next group's sum, or 0 under the last.</summary>
    private double Lower(double[][] sums, int k, int category) => k + 1 < _groupCount ? sums[k + 1][category] : 0;

    internal override Color HitColor(int series) => ColorOf((BarLayerSpec)Spec, series / 2) with { A = 255 };

    internal override double HitAlong(in GeomFrame.Axis x, in SeriesHit hit) => BandAlong(x, (int)(hit.Row % _x.CategoryCount));

    /// <summary>
    /// Answers with each group's pieces as drawn (row I2c, Issue #355 Decision 4): its positive piece as series 2 · group, its
    /// negative one as 2 · group + 1, at the outer end; the row is the cell, group · categories + category, marked derived.
    /// Bands under a pixel answer with the pixel column's category of each array's extreme.
    /// </summary>
    internal override void Hit(ref HitScan scan)
    {
        if (_done is not { } done || scan.X.Band is not { Count: > 0 } band)
        {
            return;
        }

        var (a, b) = CategoryHits.Line(scan.X);
        var wide = CategoryHits.Wide(a, band);
        var provisional = false;
        for (var k = 0; k < _groupCount; k++)
        {
            for (var sign = 0; sign < (done.AnyNegative ? 2 : 1); sign++)
            {
                var (best, bestCategory, bestValue) = (scan.Mode == HitMode.X ? double.PositiveInfinity : scan.Radius, -1L, double.NaN);
                if (wide)
                {
                    var (from, to) =
                        scan.Mode == HitMode.X
                            ? (CategoryHits.PositionAt(scan.Along, a, b, band), CategoryHits.PositionAt(scan.Along, a, b, band))
                            : CategoryHits.Reach(scan.Along, scan.Radius, a, b, band);
                    for (var j = from; j <= to; j++)
                    {
                        var category = band.Position(j);
                        var (inner, outer) =
                            sign == 0
                                ? (Lower(done.Positive, k, category), done.Positive[k][category])
                                : (Lower(done.Negative, k, category), done.Negative[k][category]);
                        Consider(
                            ref scan,
                            (a * j) + b,
                            (a * (j + band.Width)) + b,
                            category,
                            inner,
                            outer,
                            ref best,
                            ref bestCategory,
                            ref bestValue
                        );
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

                        // The piece as the recording joins it: this group's array's extreme and the next group's other one.
                        var (start, count) = (CategoryHits.FirstCategory(from, to, band), to - from + 1);
                        var array = sign == 0 ? k : _groupCount + k;
                        var category = CategoryHits.Extreme(done.Pyramids[array], start, count, greatest: sign == 0, ref provisional);
                        if (category < 0)
                        {
                            continue;
                        }

                        var outer = sign == 0 ? done.Positive[k][category] : done.Negative[k][category];
                        var next =
                            k + 1 < _groupCount
                                ? CategoryHits.Extreme(done.Pyramids[array + 1], start, count, greatest: sign != 0, ref provisional)
                                : -1;
                        var inner =
                            next < 0 ? 0
                            : sign == 0 ? done.Positive[k + 1][next]
                            : done.Negative[k + 1][next];
                        Consider(ref scan, column, column + 1, category, inner, outer, ref best, ref bestCategory, ref bestValue);
                    }
                }

                if (bestCategory >= 0)
                {
                    scan.Add((2 * k) + sign, (k * (long)_x.CategoryCount) + bestCategory, bestCategory, bestValue, best, derived: true, provisional);
                }
            }
        }

        static void Consider(
            ref HitScan scan,
            double left,
            double right,
            long category,
            double inner,
            double outer,
            ref double best,
            ref long bestCategory,
            ref double bestValue
        )
        {
            if (double.IsNaN(outer) || outer == inner)
            {
                return;
            }

            var distance = scan.Box(left, right, HitScan.PixelOfValue(scan.Y, inner), HitScan.PixelOfValue(scan.Y, outer));
            if (distance < best || (scan.Mode == HitMode.X && bestCategory < 0))
            {
                (best, bestCategory, bestValue) = (distance, category, outer);
            }
        }
    }

    /// <summary>Records a piece from one sum to another between two x pixels, unless the two are the same.</summary>
    private static void Piece(RecordContext context, double left, double right, double lower, double upper, Color fill)
    {
        if (upper != lower)
        {
            context.DrawRect(left, right, context.PixelY(upper - context.YOrigin), context.PixelY(lower - context.YOrigin), fill);
        }
    }

    /// <summary>Returns group k's color: its category's in the color scale, or the layer's fill when it is not split.</summary>
    private Color ColorOf(BarLayerSpec bar, int k) => _groups is null ? FillOf(bar.Fill, translucent: false) : Colors!.Palette.Series(k);

    /// <summary>
    /// Starts summing each cell's positive and negative values (design §5.5.3): the chunks split into a fixed number of groups
    /// summed apart on the workers, joined in their order by <see cref="Accumulate"/>, so that the sums do not depend on
    /// which worker ends first.
    /// </summary>
    private (Task Pass, double[][] Partials) Sum(PlotWorkerPool pool)
    {
        var nx = _x.CategoryCount;
        var nk = _groupCount;
        var cells = nk * nx;
        var (firstChunk, endChunk) = StatWork.ChunksOf(_y, Rows);
        var chunks = endChunk - firstChunk;
        var partials = (int)Math.Min(Math.Clamp(PartialBytes / Math.Max(16L * cells, 1), 1, MaxPartials), Math.Max(chunks, 1));
        var sums = new double[partials][];
        var pass = pool.RunChunks(
            partials,
            part =>
            {
                var mine = sums[part] = new double[2 * cells];
                var buffer = ArrayPool<double>.Shared.Rent(Column.ChunkLength);
                try
                {
                    var (first, end) = ((int)((long)part * chunks / partials), (int)((long)(part + 1) * chunks / partials));
                    for (var chunk = firstChunk + first; chunk < firstChunk + end; chunk++)
                    {
                        var (lo, hi) = StatWork.PartOf(_y, chunk, Rows);
                        var codes = _x.GetCodes(chunk)[lo..hi];
                        var groups = _groups is null ? default : _groups.GetCodes(chunk)[lo..hi];
                        var values = buffer.AsSpan(0, codes.Length);
                        _y.ReadChunk(chunk, lo, values, 0);
                        for (var i = 0; i < codes.Length; i++)
                        {
                            var (code, group, value) = (codes[i], groups.IsEmpty ? 0 : groups[i], values[i]);
                            if (code < 0 || group < 0 || double.IsNaN(value))
                            {
                                continue;
                            }

                            var cell = (group * nx) + code;
                            if (value > 0)
                            {
                                mine[cell] += value;
                            }
                            else if (value < 0)
                            {
                                mine[cells + cell] += value;
                            }
                        }
                    }
                }
                finally
                {
                    ArrayPool<double>.Shared.Return(buffer);
                }
            }
        );
        return (pass, sums);
    }

    /// <summary>
    /// Joins a pass's partial sums in their order and accumulates them from the last group, which lies next to 0, to the
    /// first, outermost (design §4.1): bounded by the cells, not the rows.
    /// </summary>
    private void Accumulate(Sums into, double[][] sums)
    {
        var nx = _x.CategoryCount;
        var nk = _groupCount;
        var cells = nk * nx;
        var partials = sums.Length;
        var total = sums[0];
        for (var p = 1; p < partials; p++)
        {
            for (var i = 0; i < total.Length; i++)
            {
                total[i] += sums[p][i];
            }
        }

        var (positive, negative) = (new double[nk][], new double[nk][]);
        for (var k = 0; k < nk; k++)
        {
            (positive[k], negative[k]) = (new double[nx], new double[nx]);
        }

        var anyNegative = false;
        var (min, max) = (double.PositiveInfinity, double.NegativeInfinity);
        for (var i = 0; i < nx; i++)
        {
            var (up, down) = (0.0, 0.0);
            for (var k = nk - 1; k >= 0; k--)
            {
                up += total[(k * nx) + i];
                down += total[cells + (k * nx) + i];
                (positive[k][i], negative[k][i]) = (up, down);
            }

            if (_fill)
            {
                for (var k = 0; k < nk; k++)
                {
                    positive[k][i] = up == 0 ? 0 : positive[k][i] / up;
                    negative[k][i] = down == 0 ? 0 : negative[k][i] / -down;
                }
            }

            anyNegative |= down < 0;
            for (var k = 0; k < nk; k++)
            {
                foreach (var value in (ReadOnlySpan<double>)[positive[k][i], negative[k][i]])
                {
                    if (value != 0)
                    {
                        (min, max) = (Math.Min(min, value), Math.Max(max, value));
                    }
                }
            }
        }

        (into.Positive, into.Negative, into.AnyNegative, into.Range) = (positive, negative, anyNegative, min <= max ? (min, max) : null);
    }

    /// <summary>The accumulated sums of a version of the Y column and the pyramids over them, made in steps (row W1b1).</summary>
    private sealed class Sums(long version, (Task Pass, double[][] Partials) pass)
    {
        private Column<double>[]? _columns;

        public long Version => version;

        public double[][] Positive { get; set; } = [];

        public double[][] Negative { get; set; } = [];

        public bool AnyNegative { get; set; }

        public (double Min, double Max)? Range { get; set; }

        public MinMaxPyramid<double>[] Pyramids { get; private set; } = [];

        /// <summary>Moves on as far as the workers have: the pass, then the pyramids over its sums. Returns whether all are done.</summary>
        public bool Advance(StackedCategoryBarPlan plan, LayerIndexes indexes)
        {
            if (_columns is null)
            {
                if (!indexes.Done(pass.Pass))
                {
                    return false;
                }

                plan.Accumulate(this, pass.Partials);

                // A column of no category has nothing to draw: no pyramid over empty arrays (rows G7b and G5a).
                _columns = new Column<double>[
                    plan._x.CategoryCount == 0 ? 0
                    : AnyNegative ? 2 * plan._groupCount
                    : plan._groupCount
                ];
                Pyramids = new MinMaxPyramid<double>[_columns.Length];
                for (var i = 0; i < _columns.Length; i++)
                {
                    _columns[i] = Column.Wrap(i < plan._groupCount ? Positive[i] : Negative[i - plan._groupCount]);
                    Pyramids[i] = indexes.Pool.BuildPyramid(_columns[i]);
                }
            }

            var complete = true;
            foreach (var pyramid in Pyramids)
            {
                complete &= indexes.Complete(pyramid);
            }

            return complete;
        }

        /// <summary>Frees the pyramids and their columns.</summary>
        public void Free()
        {
            foreach (var pyramid in Pyramids)
            {
                pyramid.Dispose();
            }

            foreach (var column in _columns ?? [])
            {
                column.Dispose();
            }

            (Pyramids, _columns) = ([], null);
        }
    }
}

/// <summary>
/// Error bars over categories (design §5.5.3, row G5a): for each category, a line from the least YMin of its rows to the
/// greatest YMax, in the middle of its band, or of its dodged place. The extremes come from the workers, as a bar's over
/// categories do (row G7b), with pyramids over them for bands under a pixel.
/// </summary>
internal sealed class CategoryErrorBarPlan : LayerPlan
{
    // The envelope drawn, and the one of newer versions of the Y columns that the workers make meanwhile (row W1b1).
    private CategoryEnvelope? _done;
    private CategoryEnvelope? _making;

    // The caps' half width in pixels at the last recording: the hit test measures the caps as drawn.
    private double _cap;

    public CategoryErrorBarPlan(ErrorBarLayerSpec spec, RowRange rows)
        : base(spec, rows) => SetColumns(spec.X.Column, spec.YMin.Column, spec.YMax.Column);

    private CategoricalColumn Categories => (CategoricalColumn)Columns[0];

    public override LayerReadiness Poll(LayerIndexes indexes) =>
        CategoryEnvelope.Step(ref _done, ref _making, Columns[1], Columns[2], Categories, Rows, indexes);

    internal override int HitWidth => 2;

    internal override Color HitColor(int series) => Stroke(((ErrorBarLayerSpec)Spec).Style).Color;

    internal override double HitAlong(in GeomFrame.Axis x, in SeriesHit hit) => BandAlong(x, (int)hit.X);

    /// <summary>
    /// Answers with a category's line as drawn (row I2c, Issue #355 Decisions 6 and 10): its least YMin (series 0) and its
    /// greatest YMax (series 1) and the rows they came from; bands under a pixel, the pixel column's categories of those.
    /// </summary>
    internal override void Hit(ref HitScan scan)
    {
        if (_done is not { } envelope || scan.X.Band is not { Count: > 0 } band)
        {
            return;
        }

        var (low, high) = (envelope.Low, envelope.High);
        var (a, b) = CategoryHits.Line(scan.X);
        var (best, bestLow, bestHigh, bestAlong) = (scan.Mode == HitMode.X ? double.PositiveInfinity : scan.Radius, -1L, -1L, 0.0);
        var provisional = false;
        if (CategoryHits.Wide(a, band))
        {
            var (from, to) =
                scan.Mode == HitMode.X
                    ? (CategoryHits.PositionAt(scan.Along, a, b, band), CategoryHits.PositionAt(scan.Along, a, b, band))
                    : CategoryHits.Reach(scan.Along, scan.Radius + _cap, a, b, band);
            for (var j = from; j <= to; j++)
            {
                var (left, right) = Place((a * j) + b, (a * (j + band.Width)) + b);
                var category = band.Position(j);
                Consider(ref scan, category, category, (left + right) / 2, _cap);
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
                var least = CategoryHits.Extreme(envelope.MinPyramid!, start, to - from + 1, greatest: false, ref provisional);
                var greatest = CategoryHits.Extreme(envelope.MaxPyramid!, start, to - from + 1, greatest: true, ref provisional);
                if (least >= 0 && greatest >= 0)
                {
                    Consider(ref scan, least, greatest, column + 0.5, 0);
                }
            }
        }

        if (bestLow >= 0)
        {
            scan.Add(0, low.MinRow[bestLow], bestLow, low.Min[bestLow], Point(ref scan, bestAlong, low.Min[bestLow]), derived: false, provisional);
            scan.Add(
                1,
                high.MaxRow[bestHigh],
                bestHigh,
                high.Max[bestHigh],
                Point(ref scan, bestAlong, high.Max[bestHigh]),
                derived: false,
                provisional
            );
        }

        static double Point(ref HitScan scan, double along, double value) => scan.Distance(along, HitScan.PixelOfValue(scan.Y, value));

        void Consider(ref HitScan scan, long least, long greatest, double along, double cap)
        {
            var (min, max) = (low.Min[least], high.Max[greatest]);
            if (double.IsNaN(min) || double.IsNaN(max) || low.MinRow[least] < 0 || high.MaxRow[greatest] < 0)
            {
                return;
            }

            var (p0, p1) = (HitScan.PixelOfValue(scan.Y, min), HitScan.PixelOfValue(scan.Y, max));
            var distance = scan.Box(along, along, p0, p1);
            if (cap > 0)
            {
                distance = Math.Min(distance, Math.Min(scan.Box(along - cap, along + cap, p0, p0), scan.Box(along - cap, along + cap, p1, p1)));
            }

            if (distance < best || (scan.Mode == HitMode.X && bestLow < 0))
            {
                (best, bestLow, bestHigh, bestAlong) = (distance, least, greatest, along);
            }
        }
    }

    public override void AddRanges(RangeSink sink, bool ofX, DataTable data)
    {
        if (ofX)
        {
            return;
        }

        var bars = (ErrorBarLayerSpec)Spec;
        AddColumn(sink, bars.YMin.Column, data.GetName(bars.YMin.Index));
        AddColumn(sink, bars.YMax.Column, data.GetName(bars.YMax.Index));
    }

    public override void Record(RecordContext context)
    {
        if (_done is not { } envelope || context.X.Band is not { } band)
        {
            return;
        }

        var (low, high) = (envelope.Low, envelope.High);

        var bars = (ErrorBarLayerSpec)Spec;
        var origin = context.XOrigin;
        var b = context.PixelX(0 - origin);
        var a = context.PixelX(1 - origin) - b;
        _cap = 0;
        if (Math.Abs(a * band.Width) >= 1)
        {
            var half = bars.CapWidth * context.Scale / 2;
            _cap = half;
            var (first, last) = band.Visible(a, b, context.X.Offset, context.X.Size);
            var buffer = context.Points(9 * Math.Max(last - first + 1, 0));
            var count = 0;
            for (var category = first; category <= last; category++)
            {
                var j = band.Position(category);
                var (min, max) = (low.Min[category], high.Max[category]);
                if (double.IsNaN(min) || double.IsNaN(max))
                {
                    continue;
                }

                var (from, to) = Place((a * j) + b, (a * (j + band.Width)) + b);
                var px = (float)((from + to) / 2);
                var (top, bottom) = ((float)context.PixelY(max - context.YOrigin), (float)context.PixelY(min - context.YOrigin));
                count = ErrorBarPlan.Segment(buffer, count, px, bottom, px, top);
                if (half > 0)
                {
                    count = ErrorBarPlan.Segment(buffer, count, px - half, bottom, px + half, bottom);
                    count = ErrorBarPlan.Segment(buffer, count, px - half, top, px + half, top);
                }
            }

            context.DrawPolyline(buffer[..count], Stroke(bars.Style));
            return;
        }

        // Bands under a pixel: a line a pixel column, from the least YMin to the greatest YMax of its categories, without caps.
        var axis = context.X;
        var lines = context.Points(3 * axis.Size);
        var n = 0;
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

            var start = band.Reverse ? band.Count - 1 - to : from;
            var lowest = envelope.MinPyramid!.GetMinMax(start, to - from + 1);
            var highest = envelope.MaxPyramid!.GetMinMax(start, to - from + 1);
            if (lowest.Count > 0 && highest.Count > 0)
            {
                var center = column + 0.5f;
                var (top, bottom) = ((float)context.PixelY(highest.Max - context.YOrigin), (float)context.PixelY(lowest.Min - context.YOrigin));
                n = ErrorBarPlan.Segment(lines, n, center, bottom, center, top);
            }
        }

        context.DrawPolyline(lines[..n], Stroke(bars.Style));
    }

    public override void Release()
    {
        _done?.Free();
        _making?.Free();
        (_done, _making) = (null, null);
    }
}

/// <summary>
/// Vertical rules over categories (design §5.5.3, row G5a): a line across the panel in the middle of the band, or of the
/// dodged place, of each category that has a row. The rows a category has are counted once, by the workers, since a
/// categorical column never changes; a pyramid over the counts finds the pixel columns that hold one when bands are under
/// a pixel.
/// </summary>
internal sealed class CategoryRulePlan : LayerPlan
{
    private double[]? _counts;
    private Column<double>? _countColumn;
    private MinMaxPyramid<double>? _pyramid;

    // The workers' count of each category's rows, and its tallies, until it ends (row W1b1).
    private Task? _pass;
    private long[] _tallies = [];

    public CategoryRulePlan(RuleLayerSpec spec, RowRange rows)
        : base(spec, rows) => SetColumns(spec.X.Column);

    private CategoricalColumn Categories => (CategoricalColumn)Columns[0];

    public override LayerReadiness Poll(LayerIndexes indexes)
    {
        // A categorical column's codes do not change: its rows are counted once.
        if (_counts is null)
        {
            if (_pass is null)
            {
                (_pass, _tallies) = Count(indexes.Pool);
            }

            if (!indexes.Done(_pass))
            {
                return LayerReadiness.Pending;
            }

            var held = new double[_tallies.Length];
            for (var c = 0; c < _tallies.Length; c++)
            {
                held[c] = _tallies[c];
            }

            if (held.Length > 0)
            {
                _countColumn = Column.Wrap(held);
                _pyramid = indexes.Pool.BuildPyramid(_countColumn);
            }

            _counts = held;
        }

        return _pyramid is null || indexes.Complete(_pyramid) ? LayerReadiness.Ready : LayerReadiness.Pending;
    }

    /// <summary>Starts the workers' count of each category's rows in the layer's run.</summary>
    private (Task Pass, long[] Counts) Count(PlotWorkerPool pool)
    {
        var categories = Categories;
        var counts = new long[categories.CategoryCount];
        var (start, end) = (Rows.Start, Rows.EndIn(categories.Length));
        var pass = pool.RunChunks(
            categories.ChunkCount,
            chunk =>
            {
                var codes = categories.GetCodes(chunk);
                var first = (long)chunk << Column.ChunkShift;
                var (from, to) = ((int)Math.Clamp(start - first, 0, codes.Length), (int)Math.Clamp(end - first, 0, codes.Length));
                for (var i = from; i < to; i++)
                {
                    if (codes[i] >= 0)
                    {
                        _ = Interlocked.Increment(ref counts[codes[i]]);
                    }
                }
            }
        );
        return (pass, counts);
    }

    // x is the band's, and a vertical rule adds nothing to y.
    public override void AddRanges(RangeSink sink, bool ofX, DataTable data) { }

    public override void Record(RecordContext context)
    {
        if (_counts is not { } counts || context.X.Band is not { } band)
        {
            return;
        }

        var rule = (RuleLayerSpec)Spec;
        var origin = context.XOrigin;
        var b = context.PixelX(0 - origin);
        var a = context.PixelX(1 - origin) - b;
        var across = context.Y;
        var (top, bottom) = ((float)across.Offset, (float)(across.Offset + across.Size));
        var axis = context.X;
        var wide = Math.Abs(a * band.Width) >= 1;
        var (shownFirst, shownLast) = band.Visible(a, b, axis.Offset, axis.Size);
        var buffer = context.Points(3 * (wide ? Math.Max(shownLast - shownFirst + 1, 0) : axis.Size));
        var count = 0;
        if (wide)
        {
            for (var category = shownFirst; category <= shownLast; category++)
            {
                var j = band.Position(category);
                if (counts[category] > 0)
                {
                    var (from, to) = Place((a * j) + b, (a * (j + band.Width)) + b);
                    var at = (float)((from + to) / 2);
                    count = ErrorBarPlan.Segment(buffer, count, at, top, at, bottom);
                }
            }
        }
        else
        {
            for (var c = 0; c < axis.Size; c++)
            {
                var column = axis.Offset + c;
                var (first, last) =
                    a > 0
                        ? (Math.Ceiling((column - b) / a), Math.Ceiling((column + 1 - b) / a) - 1)
                        : (Math.Floor((column + 1 - b) / a) + 1, Math.Floor((column - b) / a));
                var (from, to) = ((long)Math.Max(first, 0), (long)Math.Min(last, band.Count - 1));
                if (from <= to && _pyramid!.GetMinMax(band.Reverse ? band.Count - 1 - to : from, to - from + 1).Max > 0)
                {
                    count = ErrorBarPlan.Segment(buffer, count, column + 0.5f, top, column + 0.5f, bottom);
                }
            }
        }

        context.DrawPolyline(buffer[..count], Stroke(rule.Style));
    }

    public override void Release()
    {
        _pyramid?.Dispose();
        _countColumn?.Dispose();
        (_pyramid, _countColumn, _counts, _pass, _tallies) = (null, null, null, null, []);
    }
}
