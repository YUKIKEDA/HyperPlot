namespace HyperPlot;

/// <summary>
/// An evenly spaced column that holds no values: element <c>i</c> is <c>Start + i * Step</c>, computed in
/// <see cref="double"/> (design §3.1, requirement D-5). The most common X of a time series.
/// </summary>
public sealed class UniformColumn : Column
{
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

    /// <summary>Gets the first value.</summary>
    public double Start { get; }

    /// <summary>Gets the difference between neighbors.</summary>
    public double Step { get; }

    /// <summary>Gets the last value, or <see cref="Start"/> for an empty column.</summary>
    public double Last => Length == 0 ? Start : GetValue(Length - 1);

    /// <summary>Returns one element.</summary>
    /// <param name="index">The element, from 0 to <see cref="Column.Length"/> - 1.</param>
    /// <returns><c>Start + index * Step</c>, rounded once.</returns>
    public double GetValue(long index)
    {
        if ((ulong)index >= (ulong)Length)
        {
            ThrowHelper.ThrowIndexOutOfRange(index, Length);
        }

        return Math.FusedMultiplyAdd(index, Step, Start);
    }
}
