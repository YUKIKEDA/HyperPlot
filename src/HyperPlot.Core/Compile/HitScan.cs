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

    private readonly Span<SeriesHit> _results;
    private readonly PyramidStore _store;

    public HitScan(Span<SeriesHit> results, PyramidStore store, int plot, int panel, HitMode mode, float radius, double along, double across)
    {
        _results = results;
        _store = store;
        (Plot, Panel, Mode, Radius, Along, Across) = (plot, panel, mode, radius, along, across);
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
        if (start >= end)
        {
            return;
        }

        var provisional = false;
        if (Mode == HitMode.X)
        {
            var row = NearestInX(pyramid, x, y, start, end, ref provisional);
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

    /// <summary>Returns the pixel distance from the cursor to a point given along and across.</summary>
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

    /// <summary>Returns the first row of [start, end) whose x reaches the x axis's value at a pixel.</summary>
    private readonly long RowAt(Column x, double pixel, long start, long end)
    {
        var (origin, d) = AtPixel(X, pixel);
        return x.LowerBound(d, origin, new RowRange(start, end));
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
    private readonly long NearestInX(PlotPyramid pyramid, Column x, Column y, long start, long end, ref bool provisional)
    {
        var (origin, d) = AtPixel(X, Along);
        var after = x.LowerBound(d, origin, new RowRange(start, end));
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
        return Math.Abs(PixelOfRow(X, x, after) - cursor) < Math.Abs(PixelOfRow(X, x, before) - cursor) ? after : before;
    }

    /// <summary>
    /// Finds the point of [start, end) nearest the cursor, closer than <paramref name="best"/> (design §6.3: the buckets of the
    /// pyramid that hold the cursor's y are descended). A range whose extremes on the screen, with the column's gap along,
    /// cannot come closer is skipped; a short one is read row by row.
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
        if (start >= end || gap >= best || !pyramid.TryExtremes(start, end, out var min, out _, out var max, out _, ref provisional))
        {
            return;
        }

        var (p0, p1) = (PixelOfValue(Y, min), PixelOfValue(Y, max));
        var (low, high) = (Math.Min(p0, p1), Math.Max(p0, p1));
        var across =
            Across < low ? low - Across
            : Across > high ? Across - high
            : 0;
        if (Math.Sqrt((gap * gap) + (across * across)) >= best)
        {
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

/// <summary>A gather of rows by key (row I2a): a split's categories, a facet's panels; it takes a gathered row back.</summary>
internal interface ISourceRows
{
    /// <summary>Returns the row a key's gathered row was gathered from.</summary>
    /// <param name="key">The key: a category, a panel's.</param>
    /// <param name="gathered">The row among the key's gathered rows.</param>
    /// <returns>The row it was gathered from, or −1 past the key's rows.</returns>
    long SourceRow(int key, long gathered);
}
