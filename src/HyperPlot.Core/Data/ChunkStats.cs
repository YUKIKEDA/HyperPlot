using System.Numerics;

namespace HyperPlot;

/// <summary>
/// The statistics of one chunk, or of a whole column (design §3.2). Scales, the Y auto-range, and the search for an X read
/// these before they read values.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
public readonly struct ChunkStats<T>
    where T : unmanaged, INumber<T>
{
    internal ChunkStats(T min, T max, T first, T last, double sum, long count, long nanCount, bool isNonDecreasing)
    {
        Min = min;
        Max = max;
        First = first;
        Last = last;
        Sum = sum;
        Count = count;
        NaNCount = nanCount;
        IsNonDecreasing = isNonDecreasing;
    }

    /// <summary>Gets the smallest value that is not NaN. <c>default</c> when <see cref="Count"/> is 0.</summary>
    public T Min { get; }

    /// <summary>Gets the largest value that is not NaN. <c>default</c> when <see cref="Count"/> is 0.</summary>
    public T Max { get; }

    /// <summary>Gets the first value, NaN included.</summary>
    public T First { get; }

    /// <summary>Gets the last value, NaN included.</summary>
    public T Last { get; }

    /// <summary>Gets the sum of the values that are not NaN, added in <see cref="double"/>.</summary>
    public double Sum { get; }

    /// <summary>Gets the number of values that are not NaN.</summary>
    public long Count { get; }

    /// <summary>Gets the number of NaN values. Always 0 for integer types.</summary>
    public long NaNCount { get; }

    /// <summary>Gets a value indicating whether every value is at most the next one, with no NaN.</summary>
    public bool IsNonDecreasing { get; }

    internal static ChunkStats<T> Empty => new(default, default, default, default, 0, 0, 0, true);

    /// <summary>Combines the statistics of two neighboring ranges, <paramref name="left"/> first.</summary>
    internal static ChunkStats<T> Combine(in ChunkStats<T> left, in ChunkStats<T> right)
    {
        T min;
        T max;
        if (left.Count == 0)
        {
            (min, max) = (right.Min, right.Max);
        }
        else if (right.Count == 0)
        {
            (min, max) = (left.Min, left.Max);
        }
        else
        {
            min = left.Min <= right.Min ? left.Min : right.Min;
            max = left.Max >= right.Max ? left.Max : right.Max;
        }

        // Both sides being non-decreasing means neither holds a NaN, so the comparison at the seam is well defined.
        var nonDecreasing = left.IsNonDecreasing && right.IsNonDecreasing && left.Last <= right.First;
        return new ChunkStats<T>(
            min,
            max,
            left.First,
            right.Last,
            left.Sum + right.Sum,
            left.Count + right.Count,
            left.NaNCount + right.NaNCount,
            nonDecreasing
        );
    }
}
