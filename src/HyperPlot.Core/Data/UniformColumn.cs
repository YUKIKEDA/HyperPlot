namespace HyperPlot;

/// <summary>
/// An evenly spaced column that holds no values: element <c>i</c> is <c>Start + i * Step</c> (design §3.1, requirement
/// D-5). The most common X of a time series. <see cref="Column.Uniform(double, double, long)"/> computes the values in
/// <see cref="double"/>; <see cref="Column.UniformInteger(long, long, long)"/> keeps them exact in <see cref="long"/>.
/// </summary>
public sealed class UniformColumn : Column
{
    private readonly long _integerStart;
    private readonly long _integerStep;

    internal UniformColumn(double start, double step, long length)
        : base(length)
    {
        if (!double.IsFinite(start))
        {
            throw new ArgumentOutOfRangeException(nameof(start), start, "The start must be finite.");
        }

        if (!double.IsFinite(step) || step <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(step), step, "The step must be finite and greater than 0.");
        }

        Start = start;
        Step = step;
    }

    internal UniformColumn(long start, long step, long length)
        : base(length)
    {
        if (step <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(step), step, "The step must be greater than 0.");
        }

        if (length > 0 && start + ((Int128)(length - 1) * step) > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "The last value start + (length - 1) * step must fit in a long.");
        }

        IsInteger = true;
        _integerStart = start;
        _integerStep = step;
        Start = start;
        Step = step;
    }

    /// <summary>Gets the first value. For an integer column above 2^53, it is rounded; read the exact one with <see cref="TryGetInteger"/>.</summary>
    public double Start { get; }

    /// <summary>Gets the difference between neighbors. For an integer column above 2^53, it is rounded.</summary>
    public double Step { get; }

    /// <summary>Gets the last value, or <see cref="Start"/> for an empty column.</summary>
    public double Last => Length == 0 ? Start : GetValue(Length - 1);

    /// <summary>Gets a value indicating whether the column was made by <see cref="Column.UniformInteger(long, long, long)"/>.</summary>
    public bool IsInteger { get; }

    /// <summary>Returns one element.</summary>
    /// <param name="index">The element, from 0 to <see cref="Column.Length"/> - 1.</param>
    /// <returns>
    /// <c>Start + index * Step</c>: rounded once from the exact value for an integer column, and computed with one rounding
    /// (a fused multiply-add) otherwise.
    /// </returns>
    public double GetValue(long index)
    {
        if ((ulong)index >= (ulong)Length)
        {
            ThrowHelper.ThrowIndexOutOfRange(index, Length);
        }

        return IsInteger ? IntegerAt(index) : Math.FusedMultiplyAdd(index, Step, Start);
    }

    /// <summary>
    /// Returns the exact start and step of an integer column (design §3.1), so that a caller that connects M4 itself can make
    /// its <see cref="ScaleOrigin"/> and boundaries without the rounding of <see cref="Start"/>. Element <c>i</c> is
    /// <c>start + i * step</c>, exact in <see cref="long"/>.
    /// </summary>
    /// <param name="start">The first value, or 0 for a column of doubles.</param>
    /// <param name="step">The difference between neighbors, or 0 for a column of doubles.</param>
    /// <returns><see langword="true"/> for a column made by <see cref="Column.UniformInteger(long, long, long)"/>.</returns>
    public bool TryGetInteger(out long start, out long step)
    {
        (start, step) = (_integerStart, _integerStep);
        return IsInteger;
    }

    /// <inheritdoc />
    internal override string KindName => IsInteger ? "UniformColumn (integer)" : nameof(UniformColumn);

    /// <summary>Returns element <paramref name="index"/> of an integer column, exact: the constructor made sure it fits.</summary>
    internal long IntegerAt(long index) => _integerStart + (index * _integerStep);

    /// <inheritdoc />
    internal override bool TryGetRange(out ScaleEnd min, out ScaleEnd max)
    {
        (min, max) = IsInteger
            ? (ScaleEnd.Of(_integerStart), ScaleEnd.Of(IntegerAt(Math.Max(Length - 1, 0))))
            : (ScaleEnd.Of(Start), ScaleEnd.Of(Last));
        return Length > 0;
    }

    /// <inheritdoc />
    internal override bool TryGetRange(RowRange rows, out ScaleEnd min, out ScaleEnd max)
    {
        // The step is above 0: the run's first and last rows are its ends.
        var (start, end) = (Math.Max(rows.Start, 0), rows.EndIn(Length));
        if (start >= end)
        {
            (min, max) = (default, default);
            return false;
        }

        (min, max) = IsInteger
            ? (ScaleEnd.Of(IntegerAt(start)), ScaleEnd.Of(IntegerAt(end - 1)))
            : (ScaleEnd.Of(GetValue(start)), ScaleEnd.Of(GetValue(end - 1)));
        return true;
    }

    /// <inheritdoc />
    internal override Task? PrecomputeStatsIfMissing(PlotWorkerPool pool) => null;

    /// <inheritdoc />
    internal override double DifferenceAt(long index, long origin) => new M4.UniformX(this, origin).ValueAt(index);

    /// <inheritdoc />
    internal override void ReadDifferences(Span<double> destination, long origin)
    {
        if (IsInteger)
        {
            for (var i = 0; i < destination.Length; i++)
            {
                destination[i] = ScaleSpace.Difference(IntegerAt(i), origin);
            }

            return;
        }

        // GetValue's floating-point branch, as M4's UniformX reads it.
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = Math.FusedMultiplyAdd(i, Step, Start) - origin;
        }
    }

    /// <inheritdoc />
    internal override void ReadChunk(int chunk, int offset, Span<double> destination, long origin)
    {
        var first = ((long)chunk << ChunkShift) + offset;
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = IsInteger ? ScaleSpace.Difference(IntegerAt(first + i), origin) : Math.FusedMultiplyAdd(first + i, Step, Start) - origin;
        }
    }

    /// <inheritdoc />
    internal override long LowerBound(double boundary, long origin) => new M4.UniformX(this, origin).LowerBound(boundary);

    /// <inheritdoc />
    internal override long LowerBound(double boundary, long origin, RowRange rows) =>
        new M4.UniformX(this, origin).LowerBound(boundary, Math.Clamp(rows.Start, 0, Length), rows.EndIn(Length));

    /// <inheritdoc />
    internal override bool IsNonDecreasing(PlotWorkerPool pool, RowRange rows) => true;

    /// <inheritdoc />
    /// <remarks>A uniform column's keys are a long's or a double's (row G5c): a density's quantiles select over them.</remarks>
    internal override int KeyBytes => 8;

    /// <inheritdoc />
    internal override void ReadKeys(int chunk, int offset, Span<ulong> destination)
    {
        var first = ((long)chunk << ChunkShift) + offset;
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = IsInteger ? (ulong)IntegerAt(first + i) ^ (1UL << 63) : DoubleKey(GetValue(first + i));
        }
    }

    /// <inheritdoc />
    internal override double ValueOfKey(ulong key) => IsInteger ? (long)(key ^ (1UL << 63)) : KeyDouble(key);

    /// <inheritdoc />
    internal override ulong KeyOfValue(double value) => IsInteger ? (ulong)(long)value ^ (1UL << 63) : DoubleKey(value);

    /// <inheritdoc />
    internal override bool IsNonDecreasing(PlotWorkerPool pool) => true;

    /// <inheritdoc />
    internal override int AddSeries<TY>(M4Batch batch, MinMaxPyramid<TY> y, M4Point[] destination, RowRange rows) =>
        batch.AddRun(this, y, destination, rows);

    /// <inheritdoc />
    internal override GatherChannel MakeGather(ReadOnlySpan<long> counts) =>
        IsInteger ? new GatherChannel.UniformGather<long>(this, counts) : new GatherChannel.UniformGather<double>(this, counts);
}
