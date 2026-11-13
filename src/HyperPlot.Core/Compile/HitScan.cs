using HyperPlot.Interaction;

namespace HyperPlot;

/// <summary>
/// One hit test over a panel's layers (design §6.3, row I2a, Issue #337): the cursor, the radius, the mode, the axes of the
/// layer being asked, and the caller's buffer. A ref struct over the buffer, so that a hit test allocates nothing (invariant
/// 3). The along coordinate runs with the layer's x (the screen's vertical when flipped), the across one with its y.
/// </summary>
internal ref struct HitScan
{
    // A range of rows this short is read row by row; a longer one is split by its extremes from the pyramid.
    private const int ScanRows = 64;

    // How much nearer than the best found a range must be able to come to be descended, in pixels (Issue #341 Decision 5): a
    // dense series crosses the cursor's y many times in a pixel column, and finding the nearest of those crossings exactly
    // reads them all (66 ms at 1e9 points), while a hover cannot tell them apart.
    private const double Tolerance = 0.5;

    private readonly Span<SeriesHit> _results;
    private readonly PyramidStore _store;

    // A scan of statistics (row I4b): the series' summaries over an x range of the scale space, in place of hits.
    private readonly Span<SeriesStatistics> _statistics;
    private readonly bool _summing;
    private readonly ScaleRange _range;

    public HitScan(Span<SeriesHit> results, PyramidStore store, int plot, int panel, HitMode mode, float radius, double along, double across)
    {
        _results = results;
        _store = store;
        (Plot, Panel, Mode, Radius, Along, Across) = (plot, panel, mode, radius, along, across);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HitScan"/> struct for statistics (design §6.5 "計測", row I4b): each series
    /// asked adds its summary over the rows whose x lies in the range of the x scale's space; bars, error bars, and rects add
    /// none.
    /// </summary>
    public HitScan(Span<SeriesStatistics> statistics, PyramidStore store, int plot, int panel, ScaleRange range)
    {
        _statistics = statistics;
        _store = store;
        _summing = true;
        _range = range;
        (Plot, Panel) = (plot, panel);
    }

    /// <summary>Gets the number of hits found, written or not: the caller's buffer may be shorter (Issue #337 Decision 9).</summary>
    public int Found { get; private set; }

    public int Plot { get; }

    public int Panel { get; }

    public HitMode Mode { get; }

    public float Radius { get; }

    /// <summary>Gets the cursor's pixel along the layer's x axis.</summary>
    public double Along { get; }

    /// <summary>Gets the cursor's pixel along the layer's y axis.</summary>
    public double Across { get; }

    /// <summary>Gets or sets the layer being asked.</summary>
    public int Layer { get; set; }

    /// <summary>Gets or sets the layer's x axis: its pair's, a secondary one for a layer on ScaleX2 (Issue #337 Decision 8).</summary>
    public GeomFrame.Axis X { get; set; }

    /// <summary>Gets or sets the layer's y axis.</summary>
    public GeomFrame.Axis Y { get; set; }

    /// <summary>Gets or sets a value indicating whether the layer draws an older version while a newer one is made: its answers are provisional.</summary>
    public bool LayerProvisional { get; set; }

    /// <summary>
    /// Gets or sets the part of a split layer being asked (row I2a): its series are numbered from
    /// <c>SeriesBase · SeriesWidth</c>, so that each category's series keep their own numbers.
    /// </summary>
    public int SeriesBase { get; set; }

    /// <summary>Gets or sets the number of series a part of a split layer answers: 2 for a band, else 1.</summary>
    public int SeriesWidth { get; set; } = 1;

    /// <summary>Gets or sets a value indicating whether the rows asked are a derived table's: a stat's or a stack's.</summary>
    public bool Derived { get; set; }

    /// <summary>
    /// Adds a series' summary over the rows of [start, end) whose x lies in the scan's range (row I4b): the rows found by two
    /// searches of x, the y's count, mean, least, and greatest from its pyramid. Counted whether the span has room or not.
    /// </summary>
    private void Summarize(int series, PlotPyramid pyramid, Column x, long start, long end)
    {
        long count = 0;
        double sum = 0,
            min = double.NaN,
            max = double.NaN;
        var provisional = LayerProvisional;
        if (start < end)
        {
            // The range's ends as the column's values: a difference from the domain's origin, or a data value through a transform.
            var (lowOrigin, low) = ColumnValue(X, _range.Origin, _range.Lower);
            var (highOrigin, high) = ColumnValue(X, _range.Origin, _range.Upper);
            var first = x.LowerBound(low, lowOrigin, new RowRange(start, end));
            var last = x.LowerBound(Math.BitIncrement(high), highOrigin, new RowRange(start, end));
            if (first < last)
            {
                pyramid.Summarize(first, last, out count, out sum, out min, out max, ref provisional);
            }
        }

        if (Found < _statistics.Length)
        {
            var (mean, low, high) = count > 0 ? (sum / count, min, max) : (double.NaN, double.NaN, double.NaN);
            _statistics[Found] = new SeriesStatistics(Layer, (SeriesBase * SeriesWidth) + series, count, mean, low, high, provisional);
        }

        Found++;

        static (long Origin, double Difference) ColumnValue(in GeomFrame.Axis axis, long origin, double difference)
        {
            if (axis.Transform is not { } transform)
            {
                return (origin, difference);
            }

            Span<double> one = [origin + difference];
            transform.Inverse(one, one);
            return (0, one[0]);
        }
    }

    /// <summary>Writes a hit when the buffer has room, and counts it either way.</summary>
    public void Add(int series, long row, double x, double y, double distance, bool derived, bool provisional)
    {
        if (Found < _results.Length)
        {
            _results[Found] = new SeriesHit(
                Plot,
                Panel,
                Layer,
                (SeriesBase * SeriesWidth) + series,
                row,
                x,
                y,
                (float)distance,
                derived || Derived,
                provisional || LayerProvisional
            );
        }

        Found++;
    }

    /// <summary>
    /// Takes the rows of the hits written from <paramref name="from"/> back through a gather (row I2a, Issue #337 Decision 3):
    /// a split's or a facet's gathered rows to the rows they were gathered from. A derived row stays.
    /// </summary>
    public readonly void Remap(int from, ISourceRows rows, int key)
    {
        for (var i = from; i < Math.Min(Found, _results.Length); i++)
        {
            if (!_results[i].Derived)
            {
                _results[i] = _results[i] with { Row = rows.SourceRow(key, _results[i].Row) };
            }
        }
    }

    /// <summary>
    /// Answers for one series over a Y column's pyramid (design §6.3's table): the row nearest the cursor's x, or the point
    /// nearest the cursor within the radius. Reads O(log N) values and the pyramid's extremes; never waits for the workers
    /// (Issue #337 Decision 5).
    /// </summary>
    public void Series(int series, Column x, Column y, RowRange rows, bool derived)
    {
        if (_store.Find(y) is not { } pyramid)
        {
            return;
        }

        var (start, end) = (rows.Start, rows.EndIn(Math.Min(x.Length, y.Length)));
        if (_summing)
        {
            Summarize(series, pyramid, x, start, end);
            return;
        }

        if (start >= end)
        {
            return;
        }

        var provisional = false;
        if (Mode == HitMode.X)
        {
            var row = NearestInX(pyramid, x, y, start, end, 0, ref provisional);
            if (row >= 0)
            {
                var (px, py) = (PixelOfRow(X, x, row), PixelOfValue(Y, y.DifferenceAt(row, 0)));
                Add(series, row, ValueOf(X, x, row), y.DifferenceAt(row, 0), Distance(px, py), derived, provisional);
            }

            return;
        }

        var (best, bestRow) = ((double)Radius, -1L);
        for (var column = Math.Floor(Along - Radius); column <= Math.Ceiling(Along + Radius); column++)
        {
            var (low, high) = (RowAt(x, column, start, end), RowAt(x, column + 1, start, end));
            (low, high) = (Math.Min(low, high), Math.Max(low, high));
            Nearest(pyramid, x, y, low, high, ColumnGap(column), ref best, ref bestRow, ref provisional);
        }

        if (bestRow >= 0)
        {
            Add(series, bestRow, ValueOf(X, x, bestRow), y.DifferenceAt(bestRow, 0), best, derived, provisional);
        }
    }

    /// <summary>
    /// Answers for one series of bars over a numeric x (design §6.3 "要素の層", row I2c, Issue #355 Decisions 1, 2, 4, 5): in
    /// <see cref="HitMode.X"/> the bar whose x is nearest the cursor's, whatever its distance, as a series answers; in
    /// <see cref="HitMode.XY"/> the bar whose rectangle lies nearest the cursor within the radius, a pixel column's run of rows
    /// longer than <see cref="ScanRows"/> standing in by its bar that reaches furthest to the cursor's side of the baseline.
    /// A bar spans x + shift ± half, in data units, from its low end (the baseline when <paramref name="low"/> is null) to its
    /// value. Never waits for the workers.
    /// </summary>
    public void Bars(int series, Column x, Column? low, Column high, RowRange rows, double baseline, double shift, double half, bool derived)
    {
        if (_summing)
        {
            return;
        }

        if (_store.Find(high) is not { } pyramid)
        {
            return;
        }

        var (start, end) = (rows.Start, rows.EndIn(Math.Min(x.Length, high.Length)));
        if (start >= end)
        {
            return;
        }

        var provisional = false;
        var bar = new Bar(x, low, high, baseline, shift, half);
        if (Mode == HitMode.X)
        {
            var row = NearestInX(pyramid, x, high, start, end, shift, ref provisional);
            if (row >= 0 && BarDistance(bar, row) is var distance && double.IsFinite(distance))
            {
                Add(series, row, ValueOf(X, x, row), high.DifferenceAt(row, 0), distance, derived, provisional);
            }

            return;
        }

        // The columns whose bars' centers can lie within the radius and a bar's half width of the cursor.
        var (_, d) = AtPixel(X, Along);
        var reach = Math.Abs(PixelOf(X, d + half) - PixelOf(X, d)) + Radius;
        var above = CursorValue(Y) >= baseline;
        var (best, bestRow) = ((double)Radius, -1L);
        for (var column = Math.Floor(Along - reach); column <= Math.Ceiling(Along + reach); column++)
        {
            var (from, to) = (RowAt(x, column, start, end, shift), RowAt(x, column + 1, start, end, shift));
            (from, to) = (Math.Min(from, to), Math.Max(from, to));
            if (from >= to)
            {
                continue;
            }

            if (to - from <= ScanRows)
            {
                for (var row = from; row < to; row++)
                {
                    Closer(BarDistance(bar, row), row, ref best, ref bestRow);
                }
            }
            else if (pyramid.TryExtremes(from, to, out _, out var minRow, out _, out var maxRow, ref provisional))
            {
                // Rows past the pixels: the bar that reaches furthest toward the cursor stands for them (Issue #355 Decision 2).
                Closer(BarDistance(bar, above ? maxRow : minRow), above ? maxRow : minRow, ref best, ref bestRow);
            }
        }

        if (bestRow >= 0)
        {
            Add(series, bestRow, ValueOf(X, x, bestRow), high.DifferenceAt(bestRow, 0), best, derived, provisional);
        }
    }

    /// <summary>
    /// Answers for a layer of error bars over a numeric x (row I2c, Issue #355 Decisions 6 and 11): the row's low end as series
    /// 0 and its high end as series 1. <see cref="HitMode.X"/> takes the row whose x is nearest the cursor's;
    /// <see cref="HitMode.XY"/> the row whose line and caps lie nearest within the radius, a long run of a pixel column standing
    /// in by its greatest high end when the cursor is above it, its least low end when below, and its row nearest along between.
    /// </summary>
    public void ErrorBars(Column x, Column low, Column high, RowRange rows, double cap)
    {
        if (_summing)
        {
            return;
        }

        if (_store.Find(high) is not { } highs || _store.Find(low) is not { } lows)
        {
            return;
        }

        var (start, end) = (rows.Start, rows.EndIn(Math.Min(x.Length, Math.Min(low.Length, high.Length))));
        if (start >= end)
        {
            return;
        }

        var provisional = false;
        if (Mode == HitMode.X)
        {
            var row = NearestInX(highs, x, high, start, end, 0, ref provisional);
            if (row >= 0)
            {
                var along = PixelOfRow(X, x, row);
                var (lowValue, highValue) = (low.DifferenceAt(row, 0), high.DifferenceAt(row, 0));
                Add(0, row, ValueOf(X, x, row), lowValue, Distance(along, PixelOfValue(Y, lowValue)), derived: false, provisional);
                Add(1, row, ValueOf(X, x, row), highValue, Distance(along, PixelOfValue(Y, highValue)), derived: false, provisional);
            }

            return;
        }

        var value = CursorValue(Y);
        var (best, bestRow) = ((double)Radius, -1L);
        var reach = cap + Radius;
        for (var column = Math.Floor(Along - reach); column <= Math.Ceiling(Along + reach); column++)
        {
            var (from, to) = (RowAt(x, column, start, end, 0), RowAt(x, column + 1, start, end, 0));
            (from, to) = (Math.Min(from, to), Math.Max(from, to));
            if (from >= to)
            {
                continue;
            }

            if (to - from <= ScanRows)
            {
                for (var row = from; row < to; row++)
                {
                    Closer(ErrorBarDistance(x, low, high, row, cap), row, ref best, ref bestRow);
                }

                continue;
            }

            // A long run (Issue #355 Decision 11): above its greatest high end, that row; below its least low end, that row;
            // between, a line passes the cursor's y, and the row nearest along stands for the run.
            if (
                !highs.TryExtremes(from, to, out _, out _, out var max, out var maxRow, ref provisional)
                || !lows.TryExtremes(from, to, out var min, out var minRow, out _, out _, ref provisional)
            )
            {
                continue;
            }

            var row0 =
                value > max ? maxRow
                : value < min ? minRow
                : Covering(lows, highs, low, high, from, to, value, ref provisional);
            if (row0 >= 0)
            {
                Closer(ErrorBarDistance(x, low, high, row0, cap), row0, ref best, ref bestRow);
            }
        }

        if (bestRow >= 0)
        {
            var xValue = ValueOf(X, x, bestRow);
            Add(0, bestRow, xValue, low.DifferenceAt(bestRow, 0), best, derived: false, provisional);
            Add(1, bestRow, xValue, high.DifferenceAt(bestRow, 0), best, derived: false, provisional);
        }
    }

    /// <summary>
    /// Answers for a layer of rectangles from the pixels its last recording mapped (row I2c, Issue #355 Decision 7):
    /// <see cref="HitMode.X"/> takes, of the rectangles whose x span holds the cursor's, one whose y span holds it too, else
    /// the nearest across; <see cref="HitMode.XY"/> the nearest of those the radius reaches. Of equal distances the later row
    /// stays: it is drawn over. The x and y are the rectangle's center, in data units.
    /// </summary>
    public void Rects(
        ReadOnlySpan<double> x0,
        ReadOnlySpan<double> x1,
        ReadOnlySpan<double> y0,
        ReadOnlySpan<double> y1,
        long start,
        long end,
        Column[] columns
    )
    {
        if (_summing)
        {
            return;
        }

        // Squared distances and plain comparisons: 131,072 rectangles a call (design §9, P-5). A rectangle with a corner
        // that does not map (NaN) fails each "lower ≤ upper" test and is passed, as the recording draws nothing for it.
        var (along, across, xMode) = (Along, Across, Mode == HitMode.X);
        var reach = xMode ? double.PositiveInfinity : (double)Radius;
        var (best, bestRow) = (xMode ? double.PositiveInfinity : reach * reach, -1L);
        var (first, last) = ((int)start, (int)Math.Min(end, Math.Min(Math.Min(x0.Length, x1.Length), Math.Min(y0.Length, y1.Length))));

        // Most rectangles lie away from the cursor along: a vector of them at a time is passed when none comes within reach.
        var lanes = System.Numerics.Vector<double>.Count;
        var window = xMode ? 0 : reach;
        var (low, high) = (new System.Numerics.Vector<double>(along - window), new System.Numerics.Vector<double>(along + window));
        for (var i = first; i < last; i++)
        {
            if (i + lanes <= last && (i - first) % lanes == 0)
            {
                var (a, b) = (new System.Numerics.Vector<double>(x0.Slice(i, lanes)), new System.Numerics.Vector<double>(x1.Slice(i, lanes)));
                var near =
                    System.Numerics.Vector.GreaterThanOrEqual(System.Numerics.Vector.Max(a, b), low)
                    & System.Numerics.Vector.LessThanOrEqual(System.Numerics.Vector.Min(a, b), high);
                if (near == System.Numerics.Vector<long>.Zero)
                {
                    i += lanes - 1;
                    continue;
                }
            }

            var (left, right) = (x0[i], x1[i]);
            if (left > right)
            {
                (left, right) = (right, left);
            }

            if (!(left <= right))
            {
                continue;
            }

            var gap =
                along < left ? left - along
                : along > right ? along - right
                : 0;
            if (gap > reach || (xMode && gap > 0))
            {
                continue;
            }

            var (top, bottom) = (y0[i], y1[i]);
            if (top > bottom)
            {
                (top, bottom) = (bottom, top);
            }

            if (!(top <= bottom))
            {
                continue;
            }

            var height =
                across < top ? top - across
                : across > bottom ? across - bottom
                : 0;
            var squared = (gap * gap) + (height * height);
            if (squared <= best && (xMode || squared < reach * reach))
            {
                (best, bestRow) = (squared, i);
            }
        }

        if (bestRow >= 0)
        {
            var x = (ValueOf(X, columns[0], bestRow) + ValueOf(X, columns[1], bestRow)) / 2;
            var y = (columns[2].DifferenceAt(bestRow, 0) + columns[3].DifferenceAt(bestRow, 0)) / 2;
            Add(0, bestRow, x, y, Math.Sqrt(best), derived: false, provisional: false);
        }
    }

    /// <summary>
    /// Answers for a layer of points from the pixels its last recording mapped (design §6.3, row R3a):
    /// <see cref="HitMode.X"/> takes, of the points within the radius along, the nearest along, and of those as near the
    /// nearest across; <see cref="HitMode.XY"/> the point whose center is nearest among those within the radius and the
    /// marker's own. Of equal distances the later row stays: it is drawn over. The x and y are the point's, in data units.
    /// </summary>
    public void Points(
        ReadOnlySpan<double> xs,
        ReadOnlySpan<double> ys,
        long start,
        long end,
        Column x,
        Column y,
        double markerRadius,
        ReadOnlySpan<float> radii = default
    )
    {
        if (_summing)
        {
            return;
        }

        // Plain comparisons: 131,072 points a call (design §9, P-5). A point that does not map (NaN) fails each test.
        var (along, across, xMode) = (Along, Across, Mode == HitMode.X);
        var reach = xMode ? (double)Radius : Radius + markerRadius;
        var (bestAlong, bestAcross, bestRow) = (double.PositiveInfinity, double.PositiveInfinity, -1L);
        var best = reach * reach;
        var last = (int)Math.Min(end, Math.Min(xs.Length, ys.Length));
        for (var i = (int)start; i < last; i++)
        {
            var dx = Math.Abs(xs[i] - along);
            if (!(dx <= reach))
            {
                continue;
            }

            var dy = Math.Abs(ys[i] - across);
            if (!(dy >= 0))
            {
                continue;
            }

            if (xMode)
            {
                if (dx < bestAlong || (dx == bestAlong && dy <= bestAcross))
                {
                    (bestAlong, bestAcross, bestRow) = (dx, dy, i);
                }
            }
            else
            {
                var squared = (dx * dx) + (dy * dy);

                // A point with a radius of its own (row R3f) is within reach by that radius, not by the largest.
                if (!radii.IsEmpty && squared > ((double)Radius + radii[i]) * ((double)Radius + radii[i]))
                {
                    continue;
                }

                if (squared <= best)
                {
                    (best, bestAlong, bestAcross, bestRow) = (squared, dx, dy, i);
                }
            }
        }

        if (bestRow >= 0)
        {
            var distance = Math.Sqrt((bestAlong * bestAlong) + (bestAcross * bestAcross));
            Add(0, bestRow, ValueOf(X, x, bestRow), y.DifferenceAt(bestRow, 0), distance, derived: false, provisional: false);
        }
    }

    /// <summary>Returns the pixel distance from the cursor to a box given along and across: 0 inside it.</summary>
    public readonly double Box(double left, double right, double low, double high)
    {
        var (a, b) = (Math.Min(left, right), Math.Max(left, right));
        var (c, e) = (Math.Min(low, high), Math.Max(low, high));
        var along =
            Along < a ? a - Along
            : Along > b ? Along - b
            : 0;
        var across =
            Across < c ? c - Across
            : Across > e ? Across - e
            : 0;
        return Math.Sqrt((along * along) + (across * across));
    }

    /// <summary>Returns the data value of the y axis at the cursor's pixel across.</summary>
    public readonly double CursorValue(in GeomFrame.Axis axis)
    {
        var (origin, d) = AtPixel(axis, Across);
        return axis.Transform is null ? origin + d : d;
    }

    /// <summary>Returns the pixel distance from the cursor to the point given along and across.</summary>
    public readonly double Distance(double along, double across) =>
        Math.Sqrt(((along - Along) * (along - Along)) + ((across - Across) * (across - Across)));

    /// <summary>
    /// Returns an axis's value at a pixel, as a difference from the origin the column searches take: the domain's origin, or 0
    /// with a transform, whose value is the data value.
    /// </summary>
    public static (long Origin, double Difference) AtPixel(in GeomFrame.Axis axis, double pixel)
    {
        var t = (pixel - axis.Offset) / axis.Size;
        t = axis.Descending ? 1 - t : t;
        var d = axis.Domain.Lower + (t * (axis.Domain.Upper - axis.Domain.Lower));
        if (axis.Transform is not { } transform)
        {
            return (axis.Domain.Origin, d);
        }

        Span<double> one = [axis.Domain.Origin + d];
        transform.Inverse(one, one);
        return (0, one[0]);
    }

    /// <summary>Returns an axis's pixel of a value given as a difference from the origin <see cref="AtPixel"/> takes.</summary>
    public static double PixelOf(in GeomFrame.Axis axis, double difference)
    {
        var d = difference;
        if (axis.Transform is { } transform)
        {
            Span<double> one = [difference];
            transform.Forward(one, one);
            d = one[0] - axis.Domain.Origin;
        }

        var t = (d - axis.Domain.Lower) / (axis.Domain.Upper - axis.Domain.Lower);
        return axis.Offset + ((axis.Descending ? 1 - t : t) * axis.Size);
    }

    /// <summary>Returns an axis's pixel of a data value.</summary>
    public static double PixelOfValue(in GeomFrame.Axis axis, double value) =>
        PixelOf(axis, axis.Transform is null ? value - axis.Domain.Origin : value);

    /// <summary>Returns the data value of a row of a column on an axis.</summary>
    public static double ValueOf(in GeomFrame.Axis axis, Column column, long row) =>
        axis.Transform is null ? axis.Domain.Origin + column.DifferenceAt(row, axis.Domain.Origin) : column.DifferenceAt(row, 0);

    /// <summary>Returns an axis's pixel of a row of a column.</summary>
    public static double PixelOfRow(in GeomFrame.Axis axis, Column column, long row) =>
        PixelOf(axis, column.DifferenceAt(row, axis.Transform is null ? axis.Domain.Origin : 0));

    /// <summary>Returns the first row of [start, end) whose x, moved by a shift in data units, reaches the x axis's value at a pixel.</summary>
    private readonly long RowAt(Column x, double pixel, long start, long end, double shift = 0)
    {
        var (origin, d) = AtPixel(X, pixel);
        return x.LowerBound(d - shift, origin, new RowRange(start, end));
    }

    private static void Closer(double distance, long row, ref double best, ref long bestRow)
    {
        if (distance < best)
        {
            (best, bestRow) = (distance, row);
        }
    }

    /// <summary>Returns the pixel distance from the cursor to a bar's rectangle: x + shift ± half, from its low end to its value.</summary>
    private readonly double BarDistance(in Bar bar, long row)
    {
        var value = bar.High.DifferenceAt(row, 0);
        var bottom = bar.Low is { } low ? low.DifferenceAt(row, 0) : bar.Baseline;

        // A bar of no height draws nothing and answers nothing: a stack's side where the value is 0 (PR #360's review).
        if (double.IsNaN(value) || double.IsNaN(bottom) || value == bottom)
        {
            return double.PositiveInfinity;
        }

        var d = bar.X.DifferenceAt(row, X.Transform is null ? X.Domain.Origin : 0) + bar.Shift;
        return Box(PixelOf(X, d - bar.Half), PixelOf(X, d + bar.Half), PixelOfValue(Y, bottom), PixelOfValue(Y, value));
    }

    /// <summary>
    /// Returns a row of [start, end) whose line from its low end to its high end holds a value, or −1 (Issue #355 Decision 11):
    /// the pyramids' extremes pass the ranges whose least low end lies above the value or whose greatest high end below it, and
    /// a short range is read row by row.
    /// </summary>
    private static long Covering(
        PlotPyramid lows,
        PlotPyramid highs,
        Column low,
        Column high,
        long start,
        long end,
        double value,
        ref bool provisional
    )
    {
        if (start >= end)
        {
            return -1;
        }

        if (end - start <= ScanRows)
        {
            for (var row = start; row < end; row++)
            {
                if (low.DifferenceAt(row, 0) <= value && high.DifferenceAt(row, 0) >= value)
                {
                    return row;
                }
            }

            return -1;
        }

        if (
            !lows.TryExtremes(start, end, out var min, out _, out _, out _, ref provisional)
            || !highs.TryExtremes(start, end, out _, out _, out var max, out _, ref provisional)
            || min > value
            || max < value
        )
        {
            return -1;
        }

        var middle = start + ((end - start) / 2);
        var found = Covering(lows, highs, low, high, start, middle, value, ref provisional);
        return found >= 0 ? found : Covering(lows, highs, low, high, middle, end, value, ref provisional);
    }

    /// <summary>Returns the pixel distance from the cursor to an error bar's line and its caps, a cap half wide in pixels.</summary>
    private readonly double ErrorBarDistance(Column x, Column low, Column high, long row, double cap)
    {
        var (lowValue, highValue) = (low.DifferenceAt(row, 0), high.DifferenceAt(row, 0));
        if (double.IsNaN(lowValue) || double.IsNaN(highValue))
        {
            return double.PositiveInfinity;
        }

        var along = PixelOfRow(X, x, row);
        var (a, b) = (PixelOfValue(Y, lowValue), PixelOfValue(Y, highValue));
        var line = Box(along, along, a, b);
        return cap > 0 ? Math.Min(line, Math.Min(Box(along - cap, along + cap, a, a), Box(along - cap, along + cap, b, b))) : line;
    }

    /// <summary>Returns how far the cursor lies from a column of pixels [column, column + 1) along: 0 inside it.</summary>
    private readonly double ColumnGap(double column) =>
        Along < column ? column - Along
        : Along > column + 1 ? Along - column - 1
        : 0;

    /// <summary>
    /// Returns the row whose x is nearest the cursor's, skipping rows whose y is NaN (§10: "NaN の欠けを飛ばす"), or −1. The
    /// rows around the cursor's x are found by the column's search; a NaN is passed through the pyramid's search for numbers.
    /// </summary>
    private readonly long NearestInX(PlotPyramid pyramid, Column x, Column y, long start, long end, double shift, ref bool provisional)
    {
        var (origin, d) = AtPixel(X, Along);
        var after = x.LowerBound(d - shift, origin, new RowRange(start, end));
        var before = after - 1;
        if (after < end)
        {
            after = pyramid.FindNumber(after, end, fromEnd: false, ref provisional);
        }

        if (before >= start)
        {
            before = pyramid.FindNumber(start, before + 1, fromEnd: true, ref provisional);
        }

        var (hasAfter, hasBefore) = (after >= start && after < end, before >= start && before < end);
        if (!hasAfter || !hasBefore)
        {
            return hasAfter ? after
                : hasBefore ? before
                : -1;
        }

        var cursor = Along;
        var o = X.Transform is null ? X.Domain.Origin : 0;
        var (afterPixel, beforePixel) = (PixelOf(X, x.DifferenceAt(after, o) + shift), PixelOf(X, x.DifferenceAt(before, o) + shift));
        return Math.Abs(afterPixel - cursor) < Math.Abs(beforePixel - cursor) ? after : before;
    }

    /// <summary>
    /// Finds the point of [start, end) nearest the cursor, closer than <paramref name="best"/> (design §6.3: the buckets of the
    /// pyramid that hold the cursor's y are descended). A range whose extremes on the screen, with the column's gap along,
    /// cannot come closer by more than half a pixel is skipped, so the point found is within half a pixel of the nearest; a
    /// short range is read row by row.
    /// </summary>
    private readonly void Nearest(
        PlotPyramid pyramid,
        Column x,
        Column y,
        long start,
        long end,
        double gap,
        ref double best,
        ref long bestRow,
        ref bool provisional
    )
    {
        if (
            start >= end
            || gap >= best
            || !pyramid.TryExtremes(start, end, out var min, out var minRow, out var max, out var maxRow, ref provisional)
        )
        {
            return;
        }

        // The range's box on the screen: its x from its first and last rows (the x does not decrease), its y from its
        // extremes. A box that cannot come nearer than the best by more than the tolerance is skipped.
        var (x0, x1) = (PixelOfRow(X, x, start), PixelOfRow(X, x, end - 1));
        var (left, right) = (Math.Min(x0, x1), Math.Max(x0, x1));
        var (p0, p1) = (PixelOfValue(Y, min), PixelOfValue(Y, max));
        var (low, high) = (Math.Min(p0, p1), Math.Max(p0, p1));
        var inside =
            Along < left ? left - Along
            : Along > right ? Along - right
            : 0;
        var along = Math.Max(gap, inside);
        var across =
            Across < low ? low - Across
            : Across > high ? Across - high
            : 0;

        // Until a point is found, the radius bounds the search as it is: a point just inside it is still an answer.
        var bound = bestRow >= 0 ? best - Tolerance : best;
        if (Math.Sqrt((along * along) + (across * across)) >= bound)
        {
            return;
        }

        // A box whose diagonal is within the tolerance: its extremes stand for its points, as near as the tolerance.
        if (((right - left) * (right - left)) + ((high - low) * (high - low)) <= Tolerance * Tolerance)
        {
            foreach (var row in (ReadOnlySpan<long>)[minRow, maxRow])
            {
                var distance = Distance(PixelOfRow(X, x, row), PixelOfValue(Y, y.DifferenceAt(row, 0)));
                if (distance < best)
                {
                    (best, bestRow) = (distance, row);
                }
            }

            return;
        }

        if (end - start <= ScanRows)
        {
            for (var row = start; row < end; row++)
            {
                var value = y.DifferenceAt(row, 0);
                if (double.IsNaN(value))
                {
                    continue;
                }

                var distance = Distance(PixelOfRow(X, x, row), PixelOfValue(Y, value));
                if (distance < best)
                {
                    (best, bestRow) = (distance, row);
                }
            }

            return;
        }

        var middle = start + ((end - start) / 2);
        Nearest(pyramid, x, y, start, middle, gap, ref best, ref bestRow, ref provisional);
        Nearest(pyramid, x, y, middle, end, gap, ref best, ref bestRow, ref provisional);
    }
}

/// <summary>A bar series' columns and placing for a hit test (row I2c): its x, its low end or none, its value, and its span.</summary>
internal readonly record struct Bar(Column X, Column? Low, Column High, double Baseline, double Shift, double Half);

/// <summary>A gather of rows by key (row I2a): a split's categories, a facet's panels; it takes a gathered row back.</summary>
internal interface ISourceRows
{
    /// <summary>Returns the row a key's gathered row was gathered from.</summary>
    /// <param name="key">The key: a category, a panel's.</param>
    /// <param name="gathered">The row among the key's gathered rows.</param>
    /// <returns>The row it was gathered from, or −1 past the key's rows.</returns>
    long SourceRow(int key, long gathered);
}
