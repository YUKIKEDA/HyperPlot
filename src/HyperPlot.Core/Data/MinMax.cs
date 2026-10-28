using System.Numerics;

namespace HyperPlot;

/// <summary>The minimum and maximum of a range of a column, from <see cref="MinMaxPyramid{T}.GetMinMax(long, long)"/> (design §3.3).</summary>
/// <typeparam name="T">The element type.</typeparam>
public readonly struct MinMax<T>
    where T : unmanaged, INumber<T>
{
    internal MinMax(T min, T max, long count, bool hasNaN, bool isProvisional)
    {
        Min = min;
        Max = max;
        Count = count;
        HasNaN = hasNaN;
        IsProvisional = isProvisional;
    }

    /// <summary>Gets the smallest value that is not NaN. <c>default</c> when <see cref="Count"/> is 0.</summary>
    public T Min { get; }

    /// <summary>Gets the largest value that is not NaN. <c>default</c> when <see cref="Count"/> is 0.</summary>
    public T Max { get; }

    /// <summary>Gets the number of values that are not NaN.</summary>
    public long Count { get; }

    /// <summary>Gets a value indicating whether the range holds a NaN, including a range of NaN only.</summary>
    public bool HasNaN { get; }

    /// <summary>
    /// Gets a value indicating whether a chunk whose pyramid was not built yet was answered from the chunk's statistics. The
    /// range of such a result covers the whole chunk, so it may be wider than the true one; ask again after
    /// <see cref="MinMaxPyramid{T}.Version"/> changes.
    /// </summary>
    public bool IsProvisional { get; }
}
