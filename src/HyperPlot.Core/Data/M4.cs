using System.Numerics;

namespace HyperPlot;

/// <summary>How <see cref="M4.Extract{TX, TY}(Column{TX}, MinMaxPyramid{TY}, ReadOnlySpan{double}, Span{M4Point}, M4Options)"/> treats NaN (design §5.5.1).</summary>
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

    /// <summary>Gets the X of the element, in the units of the X column.</summary>
    public double X { get; }

    /// <summary>Gets the Y. In a provisional result, the Y of a minimum or a maximum may not be the value at <see cref="Index"/>.</summary>
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
    /// Gets a value indicating whether a pixel column touched a chunk whose pyramid is not built yet. Such a column takes the
    /// chunk's statistics, and the chunk is requested; extract again after <see cref="MinMaxPyramid{T}.Version"/> changes.
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
    /// <param name="boundaries">
    /// The W + 1 pixel-column boundaries in X units: finite and strictly increasing. Pixel column k is [b_k, b_k+1).
    /// </param>
    /// <param name="destination">Where the points go: at least <see cref="MaxPointCount(int)"/> of W.</param>
    /// <param name="options">How NaN is treated.</param>
    /// <returns>The number of points written, and whether the result is provisional.</returns>
    public static M4Result Extract<TX, TY>(
        Column<TX> x,
        MinMaxPyramid<TY> y,
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

        var source = new ColumnX<TX>(x);
        var result = Run(ref source, y, boundaries, destination, options.Gaps);
        return statsReady ? result : new M4Result(result.Count, isProvisional: true);
    }

    /// <summary>Extracts the M4 points of a series whose X is evenly spaced. Does not allocate.</summary>
    /// <typeparam name="TY">The element type of the Y column.</typeparam>
    /// <param name="x">The X column, as long as the Y column.</param>
    /// <param name="y">The pyramid over the Y column.</param>
    /// <param name="boundaries">
    /// The W + 1 pixel-column boundaries in X units: finite and strictly increasing. Pixel column k is [b_k, b_k+1).
    /// </param>
    /// <param name="destination">Where the points go: at least <see cref="MaxPointCount(int)"/> of W.</param>
    /// <param name="options">How NaN is treated.</param>
    /// <returns>The number of points written, and whether the result is provisional.</returns>
    public static M4Result Extract<TY>(
        UniformColumn x,
        MinMaxPyramid<TY> y,
        ReadOnlySpan<double> boundaries,
        Span<M4Point> destination,
        M4Options options = default
    )
        where TY : unmanaged, INumber<TY>
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        Validate(x, y, boundaries, destination);
        var source = new UniformX(x);
        return Run(ref source, y, boundaries, destination, options.Gaps);
    }

    private static void Validate<TY>(Column x, MinMaxPyramid<TY> y, ReadOnlySpan<double> boundaries, Span<M4Point> destination)
        where TY : unmanaged, INumber<TY>
    {
        y.ThrowIfDisposed();
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
        ReadOnlySpan<double> boundaries,
        Span<M4Point> destination,
        M4Gaps gaps
    )
        where TSource : struct, IXSource
        where TY : unmanaged, INumber<TY>
    {
        var column = y.Column;
        var length = column.Length;
        var writer = new Writer(destination, gaps);
        var provisional = false;
        var start = x.LowerBound(boundaries[0]);
        if (start > 0)
        {
            var before = gaps == M4Gaps.Break ? start - 1 : y.FindNumber(0, start, fromEnd: true, ref provisional);
            if (before >= 0)
            {
                writer.Emit(before, x.ValueAt(before), double.CreateTruncating(column.GetValue(before)), M4Roles.Before);
            }
        }

        Span<Candidate> candidates = stackalloc Candidate[4];
        for (var k = 1; k < boundaries.Length; k++)
        {
            var end = x.LowerBound(boundaries[k]);
            var count = end - start;
            if (count > 0 && count <= 4)
            {
                for (var i = start; i < end; i++)
                {
                    writer.Emit(i, x.ValueAt(i), double.CreateTruncating(column.GetValue(i)), M4Roles.Raw);
                }
            }
            else if (count > 4)
            {
                y.Query(start, end, wantCount: false, out var range);
                provisional |= range.IsProvisional;
                if (!range.Found)
                {
                    // Every value of the column is NaN: nothing is drawn, and with Break the line breaks there.
                    if (gaps == M4Gaps.Break)
                    {
                        writer.Break();
                    }
                }
                else
                {
                    var first = gaps == M4Gaps.Break ? start : y.FindNumber(start, end, fromEnd: false, ref provisional);
                    var last = gaps == M4Gaps.Break ? end - 1 : y.FindNumber(start, end, fromEnd: true, ref provisional);

                    // A provisional range may hold a value that the column's elements do not; the provisional points stand in.
                    first = first < 0 ? range.MinIndex : first;
                    last = last < 0 ? range.MaxIndex : last;
                    candidates[0] = new Candidate(first, double.CreateTruncating(column.GetValue(first)), M4Roles.First);
                    candidates[1] = new Candidate(range.MinIndex, double.CreateTruncating(range.Min), M4Roles.Min);
                    candidates[2] = new Candidate(range.MaxIndex, double.CreateTruncating(range.Max), M4Roles.Max);
                    candidates[3] = new Candidate(last, double.CreateTruncating(column.GetValue(last)), M4Roles.Last);
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
            var after = gaps == M4Gaps.Break ? start : y.FindNumber(start, length, fromEnd: false, ref provisional);
            if (after >= 0)
            {
                writer.Emit(after, x.ValueAt(after), double.CreateTruncating(column.GetValue(after)), M4Roles.After);
            }
        }

        return new M4Result(writer.Count, provisional);
    }

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
    private interface IXSource
    {
        /// <summary>Returns x(i) as a <see cref="double"/>.</summary>
        /// <param name="index">The element.</param>
        /// <returns>The X of the element.</returns>
        double ValueAt(long index);

        /// <summary>Returns the least i with x(i) ≥ <paramref name="boundary"/>, or the length when there is none.</summary>
        /// <param name="boundary">The boundary, in X units.</param>
        /// <returns>The position of the boundary.</returns>
        long LowerBound(double boundary);
    }

    private readonly record struct Candidate(long Index, double Y, M4Roles Roles);

    private readonly struct UniformX(UniformColumn column) : IXSource
    {
        public double ValueAt(long index) => column.GetValue(index);

        public long LowerBound(double boundary)
        {
            var length = column.Length;
            if (length == 0)
            {
                return 0;
            }

            // The division and GetValue's fused multiply-add round differently; the guess is corrected against GetValue.
            var guess = Math.Ceiling((boundary - column.Start) / column.Step);
            var index =
                guess <= 0 ? 0
                : guess >= length ? length
                : (long)guess;
            while (index > 0 && column.GetValue(index - 1) >= boundary)
            {
                index--;
            }

            while (index < length && column.GetValue(index) < boundary)
            {
                index++;
            }

            return index;
        }
    }

    private readonly struct ColumnX<TX>(Column<TX> column) : IXSource
        where TX : unmanaged, INumber<TX>
    {
        public double ValueAt(long index) => double.CreateTruncating(column.GetValue(index));

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

                if (double.CreateTruncating(stats.Last) >= boundary)
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
                if (double.CreateTruncating(values[middle]) >= boundary)
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
                if (double.CreateTruncating(column.GetValue(middle)) >= boundary)
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
