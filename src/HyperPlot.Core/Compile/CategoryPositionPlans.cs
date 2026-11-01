using System.Buffers;
using System.Numerics;
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
    private double[][] _positive = [];
    private double[][] _negative = [];
    private Column<double>[] _columns = [];
    private MinMaxPyramid<double>[] _pyramids = [];
    private double[] _extremes = [];
    private long _version = -1;
    private (double Min, double Max)? _range;
    private bool _anyNegative;

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

    public override void WaitForIndexes(PlotWorkerPool pool)
    {
        var version = _y.Version;
        if (_version == version)
        {
            return;
        }

        Release();
        Sum(pool);

        // A column of no category has nothing to draw: no pyramid over empty arrays (rows G7b and G5a).
        _columns = new Column<double>[
            _x.CategoryCount == 0 ? 0
            : _anyNegative ? 2 * _groupCount
            : _groupCount
        ];
        _pyramids = new MinMaxPyramid<double>[_columns.Length];
        for (var i = 0; i < _columns.Length; i++)
        {
            _columns[i] = Column.Wrap(i < _groupCount ? _positive[i] : _negative[i - _groupCount]);
            _pyramids[i] = pool.BuildPyramid(_columns[i]);
        }

        foreach (var pyramid in _pyramids)
        {
            pyramid.WhenComplete().GetAwaiter().GetResult();
        }

        _version = version;
    }

    public override void AddRanges(RangeSink sink, bool ofX, DataTable data)
    {
        // x is the band's: its domain comes from the categories (design §4.2, row G7b).
        if (ofX || _range is not { } range)
        {
            return;
        }

        var bar = (BarLayerSpec)Spec;
        var name = data.GetName(bar.Y.Index);
        sink.AddBaseline(0);
        if (_fill)
        {
            sink.Add(_anyNegative ? -1 : 0, 1, name);
            return;
        }

        sink.Add(range.Min, range.Max, name);
    }

    public override void Record(RecordContext context)
    {
        if (_version < 0 || context.X.Band is not { Count: > 0 } band)
        {
            return;
        }

        var bar = (BarLayerSpec)Spec;
        var origin = context.XOrigin;
        var b = context.PixelX(0 - origin);
        var a = context.PixelX(1 - origin) - b;
        if (Math.Abs(a * band.Width) >= 1)
        {
            for (var k = 0; k < _groupCount; k++)
            {
                var color = ColorOf(bar, k);
                for (var category = 0; category < band.Count; category++)
                {
                    var j = band.Position(category);
                    var (from, to) = ((a * j) + b, (a * (j + band.Width)) + b);
                    Piece(context, from, to, Lower(_positive, k, category), _positive[k][category], color);
                    if (_anyNegative)
                    {
                        Piece(context, from, to, _negative[k][category], Lower(_negative, k, category), color);
                    }
                }
            }

            return;
        }

        // Bands under a pixel: the categories whose band starts in a pixel column, from the pyramids' levels (row G7b). Each
        // accumulated array is asked once a column, for both ends: a group's top and the next group's bottom come from the
        // same array. Then the pieces are drawn in the groups' order.
        var axis = context.X;
        var arrays = _pyramids.Length;
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

                var extremes = _pyramids[p].GetMinMax(band.Reverse ? band.Count - 1 - to : from, to - from + 1);
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
                if (_anyNegative)
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
        foreach (var pyramid in _pyramids)
        {
            pyramid.Dispose();
        }

        foreach (var column in _columns)
        {
            column.Dispose();
        }

        (_pyramids, _columns, _version) = ([], [], -1);
    }

    /// <summary>Returns the lower end of group k's piece in a category: the next group's sum, or 0 under the last.</summary>
    private double Lower(double[][] sums, int k, int category) => k + 1 < _groupCount ? sums[k + 1][category] : 0;

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
    /// Sums each cell's positive and negative values (design §5.5.3): the chunks split into a fixed number of groups summed
    /// apart on the workers, joined in their order, so that the sums do not depend on which worker ends first.
    /// </summary>
    private void Sum(PlotWorkerPool pool)
    {
        var nx = _x.CategoryCount;
        var nk = _groupCount;
        var cells = nk * nx;
        var (firstChunk, endChunk) = StatWork.ChunksOf(_y, Rows);
        var chunks = endChunk - firstChunk;
        var partials = (int)Math.Min(Math.Clamp(PartialBytes / Math.Max(16L * cells, 1), 1, MaxPartials), Math.Max(chunks, 1));
        var sums = new double[partials][];
        pool.RunChunks(
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
            )
            .GetAwaiter()
            .GetResult();

        var total = sums[0];
        for (var p = 1; p < partials; p++)
        {
            for (var i = 0; i < total.Length; i++)
            {
                total[i] += sums[p][i];
            }
        }

        // Accumulated from the last group, which lies next to 0, to the first, outermost (design §4.1).
        (_positive, _negative) = (new double[nk][], new double[nk][]);
        for (var k = 0; k < nk; k++)
        {
            (_positive[k], _negative[k]) = (new double[nx], new double[nx]);
        }

        (_range, _anyNegative) = (null, false);
        var (min, max) = (double.PositiveInfinity, double.NegativeInfinity);
        for (var i = 0; i < nx; i++)
        {
            var (up, down) = (0.0, 0.0);
            for (var k = nk - 1; k >= 0; k--)
            {
                up += total[(k * nx) + i];
                down += total[cells + (k * nx) + i];
                (_positive[k][i], _negative[k][i]) = (up, down);
            }

            if (_fill)
            {
                for (var k = 0; k < nk; k++)
                {
                    _positive[k][i] = up == 0 ? 0 : _positive[k][i] / up;
                    _negative[k][i] = down == 0 ? 0 : _negative[k][i] / -down;
                }
            }

            _anyNegative |= down < 0;
            for (var k = 0; k < nk; k++)
            {
                foreach (var value in (ReadOnlySpan<double>)[_positive[k][i], _negative[k][i]])
                {
                    if (value != 0)
                    {
                        (min, max) = (Math.Min(min, value), Math.Max(max, value));
                    }
                }
            }
        }

        _range = min <= max ? (min, max) : null;
    }
}

/// <summary>
/// Error bars over categories (design §5.5.3, row G5a): for each category, a line from the least YMin of its rows to the
/// greatest YMax, in the middle of its band, or of its dodged place. The extremes come from the workers, as a bar's over
/// categories do (row G7b), with pyramids over them for bands under a pixel.
/// </summary>
internal sealed class CategoryErrorBarPlan : LayerPlan
{
    private CategoryExtremes? _low;
    private CategoryExtremes? _high;
    private Column<double>? _minColumn;
    private Column<double>? _maxColumn;
    private MinMaxPyramid<double>? _minPyramid;
    private MinMaxPyramid<double>? _maxPyramid;

    public CategoryErrorBarPlan(ErrorBarLayerSpec spec, RowRange rows)
        : base(spec, rows) => SetColumns(spec.X.Column, spec.YMin.Column, spec.YMax.Column);

    private CategoricalColumn Categories => (CategoricalColumn)Columns[0];

    public override void WaitForIndexes(PlotWorkerPool pool)
    {
        var (yMin, yMax) = (Columns[1], Columns[2]);
        if (_low is { } low && _high is { } high && low.Version == yMin.Version && high.Version == yMax.Version)
        {
            return;
        }

        Release();
        var lows = yMin.StartCategoryExtremes(Categories, pool, Rows);
        var highs = yMax.StartCategoryExtremes(Categories, pool, Rows);
        lows.Completion.GetAwaiter().GetResult();
        highs.Completion.GetAwaiter().GetResult();
        if (lows.Min.Length > 0)
        {
            (_minColumn, _maxColumn) = (Column.Wrap(lows.Min), Column.Wrap(highs.Max));
            (_minPyramid, _maxPyramid) = (pool.BuildPyramid(_minColumn), pool.BuildPyramid(_maxColumn));
            _minPyramid.WhenComplete().GetAwaiter().GetResult();
            _maxPyramid.WhenComplete().GetAwaiter().GetResult();
        }

        (_low, _high) = (lows, highs);
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
        if (_low is not { } low || _high is not { } high || context.X.Band is not { } band)
        {
            return;
        }

        var bars = (ErrorBarLayerSpec)Spec;
        var origin = context.XOrigin;
        var b = context.PixelX(0 - origin);
        var a = context.PixelX(1 - origin) - b;
        if (Math.Abs(a * band.Width) >= 1)
        {
            var half = bars.CapWidth * context.Scale / 2;
            var buffer = context.Points(9 * band.Count);
            var count = 0;
            for (var category = 0; category < band.Count; category++)
            {
                var (min, max) = (low.Min[category], high.Max[category]);
                if (double.IsNaN(min) || double.IsNaN(max))
                {
                    continue;
                }

                var j = band.Position(category);
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
            var lowest = _minPyramid!.GetMinMax(start, to - from + 1);
            var highest = _maxPyramid!.GetMinMax(start, to - from + 1);
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
        _minPyramid?.Dispose();
        _maxPyramid?.Dispose();
        _minColumn?.Dispose();
        _maxColumn?.Dispose();
        (_minPyramid, _maxPyramid, _minColumn, _maxColumn, _low, _high) = (null, null, null, null, null, null);
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

    public CategoryRulePlan(RuleLayerSpec spec, RowRange rows)
        : base(spec, rows) => SetColumns(spec.X.Column);

    private CategoricalColumn Categories => (CategoricalColumn)Columns[0];

    public override void WaitForIndexes(PlotWorkerPool pool)
    {
        if (_counts is not null)
        {
            return;
        }

        var categories = Categories;
        var counts = new long[categories.CategoryCount];
        var (start, end) = (Rows.Start, Rows.EndIn(categories.Length));
        pool.RunChunks(
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
            )
            .GetAwaiter()
            .GetResult();

        var held = new double[counts.Length];
        for (var c = 0; c < counts.Length; c++)
        {
            held[c] = counts[c];
        }

        if (held.Length > 0)
        {
            _countColumn = Column.Wrap(held);
            _pyramid = pool.BuildPyramid(_countColumn);
            _pyramid.WhenComplete().GetAwaiter().GetResult();
        }

        _counts = held;
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
        var buffer = context.Points(3 * (wide ? band.Count : axis.Size));
        var count = 0;
        if (wide)
        {
            for (var category = 0; category < band.Count; category++)
            {
                if (counts[category] > 0)
                {
                    var j = band.Position(category);
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
        (_pyramid, _countColumn, _counts) = (null, null, null);
    }
}
