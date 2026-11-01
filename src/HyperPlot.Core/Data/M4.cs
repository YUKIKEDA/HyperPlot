using System.Numerics;

namespace HyperPlot;

/// <summary>How <see cref="M4.Extract{TX, TY}(Column{TX}, MinMaxPyramid{TY}, ScaleOrigin, ReadOnlySpan{double}, Span{M4Point}, M4Options)"/> treats NaN (design §5.5.1).</summary>
public enum M4Gaps
{
    /// <summary>A NaN breaks the line: the point after it does not connect to the point before it (requirement D-7).</summary>
    Break,

    /// <summary>NaN values are skipped, and the line connects the values around them.</summary>
    Connect,
}

/// <summary>What an <see cref="M4Point"/> stands for in its pixel column. One point may have several roles.</summary>
[Flags]
public enum M4Roles : byte
{
    /// <summary>No role.</summary>
    None = 0,

    /// <summary>The first element of the pixel column.</summary>
    First = 1,

    /// <summary>The first element with the column's minimum.</summary>
    Min = 2,

    /// <summary>The first element with the column's maximum.</summary>
    Max = 4,

    /// <summary>The last element of the pixel column.</summary>
    Last = 8,

    /// <summary>An element of a pixel column of at most 4 elements, written as it is.</summary>
    Raw = 16,

    /// <summary>The last element before the first boundary, so that the line reaches the left edge.</summary>
    Before = 32,

    /// <summary>The first element at or after the last boundary, so that the line reaches the right edge.</summary>
    After = 64,
}

/// <summary>Options of M4 extraction.</summary>
/// <param name="Gaps">How NaN is treated. The default breaks the line.</param>
public readonly record struct M4Options(M4Gaps Gaps);

/// <summary>One point M4 extraction writes.</summary>
public readonly struct M4Point
{
    internal M4Point(long index, double x, double y, M4Roles roles, bool breakBefore)
    {
        Index = index;
        X = x;
        Y = y;
        Roles = roles;
        BreakBefore = breakBefore;
    }

    /// <summary>Gets the position of the element in the Y column.</summary>
    public long Index { get; }

    /// <summary>Gets the X of the element: its difference from <see cref="ScaleOrigin.X"/>, in the units of the X column (design §3.6).</summary>
    public double X { get; }

    /// <summary>
    /// Gets the Y: its difference from <see cref="ScaleOrigin.Y"/> (design §3.6). In a provisional result, the Y of a minimum
    /// or a maximum may not be the value at <see cref="Index"/>.
    /// </summary>
    public double Y { get; }

    /// <summary>Gets the roles of the point.</summary>
    public M4Roles Roles { get; }

    /// <summary>Gets a value indicating whether the line does not connect this point to the one before it (a NaN lies between).</summary>
    public bool BreakBefore { get; }
}

/// <summary>The result of M4 extraction.</summary>
public readonly struct M4Result
{
    internal M4Result(int count, bool isProvisional)
    {
        Count = count;
        IsProvisional = isProvisional;
    }

    /// <summary>Gets the number of points written.</summary>
    public int Count { get; }

    /// <summary>
    /// Gets a value indicating whether the points are a stand-in (design §5.5.1). It is true when a pixel column touched a
    /// chunk whose pyramid is not built yet, or when a stored X column does not have every chunk's statistics computed yet.
    /// A column over such chunks takes the minimum and maximum of their statistics if already computed; with no statistics,
    /// it has only its first and last element. For <see cref="M4Gaps.Connect"/>, an element that would need a scan of an
    /// unbuilt chunk is replaced by the column's boundary element. The chunks are requested; extract again after
    /// <see cref="MinMaxPyramid{T}.Version"/> changes, or after <see cref="PlotWorkerPool.PrecomputeStats{T}(Column{T})"/>
    /// ends.
    /// </summary>
    public bool IsProvisional { get; }
}

/// <summary>
/// M4 extraction (design §5.5.1, Jugel et al. 2014): for each pixel column, the first, minimum, maximum, and last element,
/// in element order, so that the drawn line looks the same as the line through every element.
/// </summary>
public static class M4
{
    /// <summary>Returns the most points an extraction over <paramref name="pixelColumns"/> columns writes: 4W + 2.</summary>
    /// <param name="pixelColumns">The number of pixel columns W.</param>
    /// <returns>The size the destination needs.</returns>
    public static int MaxPointCount(int pixelColumns)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelColumns);
        return checked((4 * pixelColumns) + 2);
    }

    /// <summary>Extracts the M4 points of a series whose X is a stored column. Does not allocate.</summary>
    /// <typeparam name="TX">The element type of the X column.</typeparam>
    /// <typeparam name="TY">The element type of the Y column.</typeparam>
    /// <param name="x">
    /// The X column: non-decreasing, with no NaN, as long as the Y column. Compute its statistics first with
    /// <see cref="PlotWorkerPool.PrecomputeStats{T}(Column{T})"/>; until they are all computed, the result is provisional and
    /// the column is not checked.
    /// </param>
    /// <param name="y">The pyramid over the Y column.</param>
    /// <param name="origin">
    /// The origins that the boundaries are given in and the points are written in (design §3.6). With 0, a floating-point
    /// value is its own difference.
    /// </param>
    /// <param name="boundaries">
    /// The W + 1 pixel-column boundaries in X units, as differences from <see cref="ScaleOrigin.X"/>: finite and strictly
    /// increasing. Pixel column k is [b_k, b_k+1).
    /// </param>
    /// <param name="destination">Where the points go: at least <see cref="MaxPointCount(int)"/> of W.</param>
    /// <param name="options">How NaN is treated.</param>
    /// <returns>The number of points written, and whether the result is provisional.</returns>
    public static M4Result Extract<TX, TY>(
        Column<TX> x,
        MinMaxPyramid<TY> y,
        ScaleOrigin origin,
        ReadOnlySpan<double> boundaries,
        Span<M4Point> destination,
        M4Options options = default
    )
        where TX : unmanaged, INumber<TX>
        where TY : unmanaged, INumber<TY>
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        Validate(x, y, boundaries, destination);

        // The X statistics are read only if computed (PlotWorkerPool.PrecomputeStats): scanning X here would put O(N) on the
        // frame path (design §5.5.1, invariant 2). Without them the result is provisional and X is not checked yet.
        var statsReady = x.TryIsNonDecreasing(out var nonDecreasing);
        if (statsReady && !nonDecreasing)
        {
            throw new InvalidOperationException("The X column must be non-decreasing and hold no NaN.");
        }

        var source = new ColumnX<TX>(x, origin.X);
        var result = Run(ref source, y, origin.Y, boundaries, destination, options.Gaps, RowRange.All);
        return statsReady ? result : new M4Result(result.Count, isProvisional: true);
    }

    /// <summary>
    /// Extracts the M4 points of the rows [start, end) of a series, as if the rows outside were not there (design §5.5.1, row
    /// G7c): the series of a category whose rows lie in one run. Checks nothing the public form checks; allocates nothing.
    /// </summary>
    /// <remarks>
    /// Only the run's X need be non-decreasing (row G6b): a facet's long data restarts its times in each panel's run. The
    /// caller checks the run and gives the result, null while the statistics are not computed.
    /// </remarks>
    internal static M4Result Extract<TX, TY>(
        Column<TX> x,
        MinMaxPyramid<TY> y,
        ScaleOrigin origin,
        ReadOnlySpan<double> boundaries,
        Span<M4Point> destination,
        M4Options options,
        RowRange rows,
        bool? runNonDecreasing
    )
        where TX : unmanaged, INumber<TX>
        where TY : unmanaged, INumber<TY>
    {
        Validate(x, y, boundaries, destination);
        var statsReady = runNonDecreasing.HasValue;
        if (runNonDecreasing == false)
        {
            throw new InvalidOperationException("The X column must be non-decreasing and hold no NaN over the series' rows.");
        }

        var source = new ColumnX<TX>(x, origin.X);
        var result = Run(ref source, y, origin.Y, boundaries, destination, options.Gaps, rows);
        return statsReady ? result : new M4Result(result.Count, isProvisional: true);
    }

    /// <summary>Extracts the M4 points of the rows [start, end) of a series whose X is evenly spaced (row G7c).</summary>
    internal static M4Result Extract<TY>(
        UniformColumn x,
        MinMaxPyramid<TY> y,
        ScaleOrigin origin,
        ReadOnlySpan<double> boundaries,
        Span<M4Point> destination,
        M4Options options,
        RowRange rows
    )
        where TY : unmanaged, INumber<TY>
    {
        Validate(x, y, boundaries, destination);
        var source = new UniformX(x, origin.X);
        return Run(ref source, y, origin.Y, boundaries, destination, options.Gaps, rows);
    }

    /// <summary>Extracts the M4 points of a series whose X is evenly spaced. Does not allocate.</summary>
    /// <typeparam name="TY">The element type of the Y column.</typeparam>
    /// <param name="x">The X column, as long as the Y column.</param>
    /// <param name="y">The pyramid over the Y column.</param>
    /// <param name="origin">
    /// The origins that the boundaries are given in and the points are written in (design §3.6). With 0, a floating-point
    /// value is its own difference.
    /// </param>
    /// <param name="boundaries">
    /// The W + 1 pixel-column boundaries in X units, as differences from <see cref="ScaleOrigin.X"/>: finite and strictly
    /// increasing. Pixel column k is [b_k, b_k+1).
    /// </param>
    /// <param name="destination">Where the points go: at least <see cref="MaxPointCount(int)"/> of W.</param>
    /// <param name="options">How NaN is treated.</param>
    /// <returns>The number of points written, and whether the result is provisional.</returns>
    public static M4Result Extract<TY>(
        UniformColumn x,
        MinMaxPyramid<TY> y,
        ScaleOrigin origin,
        ReadOnlySpan<double> boundaries,
        Span<M4Point> destination,
        M4Options options = default
    )
        where TY : unmanaged, INumber<TY>
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        Validate(x, y, boundaries, destination);
        var source = new UniformX(x, origin.X);
        return Run(ref source, y, origin.Y, boundaries, destination, options.Gaps, RowRange.All);
    }

    /// <summary>Throws unless there are at least 2 boundaries, finite and strictly increasing (design §5.5.1).</summary>
    internal static void ValidateBoundaries(ReadOnlySpan<double> boundaries)
    {
        if (boundaries.Length < 2)
        {
            throw new ArgumentException("At least 2 boundaries (1 pixel column) are needed.", nameof(boundaries));
        }

        for (var i = 0; i < boundaries.Length; i++)
        {
            if (!double.IsFinite(boundaries[i]) || (i > 0 && !(boundaries[i - 1] < boundaries[i])))
            {
                throw new ArgumentException("The boundaries must be finite and strictly increasing.", nameof(boundaries));
            }
        }
    }

    private static void Validate<TY>(Column x, MinMaxPyramid<TY> y, ReadOnlySpan<double> boundaries, Span<M4Point> destination)
        where TY : unmanaged, INumber<TY>
    {
        y.ThrowIfDisposed();
        ValidateBoundaries(boundaries);
        if (destination.Length < MaxPointCount(boundaries.Length - 1))
        {
            throw new ArgumentException($"The destination needs {MaxPointCount(boundaries.Length - 1)} points.", nameof(destination));
        }

        if (x.Length != y.Column.Length)
        {
            throw new ArgumentException("The X and Y columns must have the same length.", nameof(x));
        }
    }

    private static M4Result Run<TSource, TY>(
        ref TSource x,
        MinMaxPyramid<TY> y,
        long yOrigin,
        ReadOnlySpan<double> boundaries,
        Span<M4Point> destination,
        M4Gaps gaps,
        RowRange rows
    )
        where TSource : struct, IXSource
        where TY : unmanaged, INumber<TY>
    {
        var column = y.Column;

        // The rows of the series: the whole column, or one run of it (row G7c). X is non-decreasing over the run, not
        // always over the column (row G6b): a boundary is searched inside the run, after the one before it.
        var (low, length) = (rows.Start, Math.Min(rows.End, column.Length));
        var writer = new Writer(destination, gaps);
        var provisional = false;
        var start = x.LowerBound(boundaries[0], low, length);
        if (start > low)
        {
            var before = gaps == M4Gaps.Break ? start - 1 : Known(y.FindNumber(low, start, fromEnd: true, ref provisional), start - 1);
            if (before >= low)
            {
                writer.Emit(before, x.ValueAt(before), ScaleSpace.Difference(column.GetValue(before), yOrigin), M4Roles.Before);
            }
        }

        Span<Candidate> candidates = stackalloc Candidate[4];
        for (var k = 1; k < boundaries.Length; k++)
        {
            var end = x.LowerBound(boundaries[k], start, length);
            var count = end - start;
            if (count > 0 && count <= 4)
            {
                for (var i = start; i < end; i++)
                {
                    writer.Emit(i, x.ValueAt(i), ScaleSpace.Difference(column.GetValue(i), yOrigin), M4Roles.Raw);
                }
            }
            else if (count > 4)
            {
                y.Query(start, end, wantCount: false, out var range);
                provisional |= range.IsProvisional;
                var first = gaps == M4Gaps.Break ? start : Known(y.FindNumber(start, end, fromEnd: false, ref provisional), start);
                var last = gaps == M4Gaps.Break ? end - 1 : Known(y.FindNumber(start, end, fromEnd: true, ref provisional), end - 1);
                if (!range.Found && !range.IsProvisional)
                {
                    // Every value of the column is NaN: nothing is drawn, and with Break the line breaks there.
                    if (gaps == M4Gaps.Break)
                    {
                        writer.Break();
                    }
                }
                else if (!range.Found)
                {
                    // Only chunks with no statistics yet: the minimum and maximum are unknown, and first and last stand alone.
                    if (first >= 0)
                    {
                        writer.Emit(first, x.ValueAt(first), ScaleSpace.Difference(column.GetValue(first), yOrigin), M4Roles.First);
                    }

                    if (last >= 0)
                    {
                        writer.Emit(last, x.ValueAt(last), ScaleSpace.Difference(column.GetValue(last), yOrigin), M4Roles.Last);
                    }
                }
                else
                {
                    // A provisional range may hold a value that the column's elements do not; the provisional points stand in.
                    first = first < 0 ? range.MinIndex : first;
                    last = last < 0 ? range.MaxIndex : last;
                    candidates[0] = new Candidate(first, ScaleSpace.Difference(column.GetValue(first), yOrigin), M4Roles.First);
                    candidates[1] = new Candidate(range.MinIndex, ScaleSpace.Difference(range.Min, yOrigin), M4Roles.Min);
                    candidates[2] = new Candidate(range.MaxIndex, ScaleSpace.Difference(range.Max, yOrigin), M4Roles.Max);
                    candidates[3] = new Candidate(last, ScaleSpace.Difference(column.GetValue(last), yOrigin), M4Roles.Last);
                    SortByIndex(candidates);
                    foreach (var candidate in candidates)
                    {
                        writer.Emit(candidate.Index, x.ValueAt(candidate.Index), candidate.Y, candidate.Roles);
                    }
                }
            }

            start = end;
        }

        if (start < length)
        {
            var after = gaps == M4Gaps.Break ? start : Known(y.FindNumber(start, length, fromEnd: false, ref provisional), start);
            if (after >= 0)
            {
                writer.Emit(after, x.ValueAt(after), ScaleSpace.Difference(column.GetValue(after), yOrigin), M4Roles.After);
            }
        }

        return new M4Result(writer.Count, provisional);
    }

    /// <summary>Returns the boundary element in place of a search that could not answer without a scan (design §5.5.1).</summary>
    private static long Known(long found, long boundary) => found == PyramidSearch.Unknown ? boundary : found;

    private static void SortByIndex(Span<Candidate> candidates)
    {
        for (var i = 1; i < candidates.Length; i++)
        {
            var item = candidates[i];
            var j = i - 1;
            while (j >= 0 && candidates[j].Index > item.Index)
            {
                candidates[j + 1] = candidates[j];
                j--;
            }

            candidates[j + 1] = item;
        }
    }

    /// <summary>The X of a series: its value at an element, and the position of a boundary (design §5.5.1).</summary>
    internal interface IXSource
    {
        /// <summary>Returns d(x(i)), the difference of x(i) from the X origin (design §3.6).</summary>
        /// <param name="index">The element.</param>
        /// <returns>The X of the element.</returns>
        double ValueAt(long index);

        /// <summary>Returns the least i with d(x(i)) ≥ <paramref name="boundary"/>, or the length when there is none.</summary>
        /// <param name="boundary">The boundary, as a difference from the X origin.</param>
        /// <returns>The position of the boundary.</returns>
        long LowerBound(double boundary);

        /// <summary>
        /// Returns the least i in [<paramref name="low"/>, <paramref name="high"/>) with d(x(i)) ≥ <paramref name="boundary"/>,
        /// or <paramref name="high"/> when there is none (row G6b): X need be non-decreasing over those rows only.
        /// </summary>
        /// <param name="boundary">The boundary, as a difference from the X origin.</param>
        /// <param name="low">The first row searched.</param>
        /// <param name="high">The row past the last searched.</param>
        /// <returns>The position of the boundary.</returns>
        long LowerBound(double boundary, long low, long high);
    }

    private readonly record struct Candidate(long Index, double Y, M4Roles Roles);

    internal readonly struct UniformX(UniformColumn column, long origin) : IXSource
    {
        public double ValueAt(long index) =>
            column.IsInteger ? ScaleSpace.Difference(column.IntegerAt(index), origin) : column.GetValue(index) - origin;

        // An evenly spaced column increases throughout: the column's position held to the rows.
        public long LowerBound(double boundary, long low, long high) => Math.Clamp(LowerBound(boundary), low, high);

        public long LowerBound(double boundary)
        {
            var length = column.Length;
            if (length == 0)
            {
                return 0;
            }

            // The division and the differences round differently; the guess is corrected against ValueAt.
            var guess = Math.Ceiling((boundary - ValueAt(0)) / column.Step);
            var index =
                guess <= 0 ? 0
                : guess >= length ? length
                : (long)guess;
            while (index > 0 && ValueAt(index - 1) >= boundary)
            {
                index--;
            }

            while (index < length && ValueAt(index) < boundary)
            {
                index++;
            }

            return index;
        }
    }

    internal readonly struct ColumnX<TX>(Column<TX> column, long origin) : IXSource
        where TX : unmanaged, INumber<TX>
    {
        public double ValueAt(long index) => ScaleSpace.Difference(column.GetValue(index), origin);

        public long LowerBound(double boundary, long low, long high)
        {
            // The whole column's search reads its chunks' statistics; a run's searches its elements, O(log n) reads.
            if (low <= 0 && high >= column.Length)
            {
                return Math.Clamp(LowerBound(boundary), low, high);
            }

            var (first, last) = (low, high);
            while (first < last)
            {
                var middle = first + ((last - first) >>> 1);
                if (ScaleSpace.Difference(column.GetValue(middle), origin) >= boundary)
                {
                    last = middle;
                }
                else
                {
                    first = middle + 1;
                }
            }

            return first;
        }

        public long LowerBound(double boundary)
        {
            // The chunks' last values are non-decreasing: the first chunk whose last value reaches the boundary holds the answer.
            var low = 0;
            var high = column.ChunkCount;
            while (low < high)
            {
                var middle = (low + high) >>> 1;
                if (!column.TryGetStoredStats(middle, out var stats))
                {
                    return SearchElements(boundary);
                }

                if (ScaleSpace.Difference(stats.Last, origin) >= boundary)
                {
                    high = middle;
                }
                else
                {
                    low = middle + 1;
                }
            }

            if (low == column.ChunkCount)
            {
                return column.Length;
            }

            var values = column.GetChunk(low);
            var first = 0;
            var last = values.Length;
            while (first < last)
            {
                var middle = (first + last) >>> 1;
                if (ScaleSpace.Difference(values[middle], origin) >= boundary)
                {
                    last = middle;
                }
                else
                {
                    first = middle + 1;
                }
            }

            return ((long)low << Column.ChunkShift) + first;
        }

        /// <summary>The same search over the elements, for chunks whose statistics are not computed: O(log N) reads.</summary>
        private long SearchElements(double boundary)
        {
            long first = 0;
            var last = column.Length;
            while (first < last)
            {
                var middle = first + ((last - first) >>> 1);
                if (ScaleSpace.Difference(column.GetValue(middle), origin) >= boundary)
                {
                    last = middle;
                }
                else
                {
                    first = middle + 1;
                }
            }

            return first;
        }
    }

    /// <summary>Writes points in element order, merges the roles of one element, and carries a pending break (design §5.5.1).</summary>
    private ref struct Writer(Span<M4Point> destination, M4Gaps gaps)
    {
        private readonly Span<M4Point> _destination = destination;
        private readonly M4Gaps _gaps = gaps;
        private bool _pendingBreak;

        public int Count { get; private set; }

        public void Break() => _pendingBreak = Count > 0;

        public void Emit(long index, double x, double y, M4Roles roles)
        {
            if (double.IsNaN(y))
            {
                if (_gaps == M4Gaps.Break)
                {
                    Break();
                }

                return;
            }

            if (Count > 0)
            {
                ref var previous = ref _destination[Count - 1];
                if (previous.Index == index && previous.Y.Equals(y))
                {
                    previous = new M4Point(index, x, y, previous.Roles | roles, previous.BreakBefore);
                    return;
                }
            }

            _destination[Count++] = new M4Point(index, x, y, roles, _pendingBreak);
            _pendingBreak = false;
        }
    }
}
