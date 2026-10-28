using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace HyperPlot;

/// <summary>
/// The minimum, the maximum, and the count of the values that are not NaN, a vector at a time, and the first level of a
/// pyramid with or without the chunk's statistics (design §3.3).
/// </summary>
internal static class PyramidKernel
{
    /// <summary>Returns the number of values that are not NaN. <paramref name="min"/> and <paramref name="max"/> are <c>default</c> when it is 0.</summary>
    internal static int MinMax<T>(ReadOnlySpan<T> values, out T min, out T max)
        where T : unmanaged, INumber<T>
    {
        if (typeof(T) == typeof(float) || typeof(T) == typeof(double))
        {
            return MinMaxFloating(values, out min, out max);
        }

        return MinMaxInteger(values, out min, out max);
    }

    /// <summary>
    /// Writes the minimum, the maximum, and the count of the values that are not NaN of each 64-element bucket (the first
    /// level of a pyramid, design §3.3), and returns the chunk's statistics (§3.2). For <see cref="float"/> and
    /// <see cref="double"/>, both come from one pass over the values; the row B1 measurement found the one pass 0.53 to 0.72
    /// times the time of the two (docs/benchmarks/b1-data-layer.md). The result equals <see cref="ChunkStatsKernel.Compute"/> in value, and in its bits for the sum; a minimum or maximum of 0 may differ in sign, since the vector minimum and maximum keep the second operand of two zeros and the two kernels combine in another order.
    /// </summary>
    internal static ChunkStats<T> StatsAndLevel1<T>(ReadOnlySpan<T> values, Span<T> mins, Span<T> maxs, Span<byte> numbers)
        where T : unmanaged, INumber<T>
    {
        if (typeof(T) == typeof(float) || typeof(T) == typeof(double))
        {
            return StatsAndLevel1Floating(values, mins, maxs, numbers);
        }

        Level1(values, mins, maxs, numbers);
        return ChunkStatsKernel.Compute(values);
    }

    /// <summary>Writes the first level of a pyramid, as <see cref="StatsAndLevel1"/> does, without the statistics.</summary>
    internal static void Level1<T>(ReadOnlySpan<T> values, Span<T> mins, Span<T> maxs, Span<byte> numbers)
        where T : unmanaged, INumber<T>
    {
        for (var bucket = 0; bucket < numbers.Length; bucket++)
        {
            var first = bucket << PyramidLayout.FirstShift;
            var slice = values.Slice(first, Math.Min(1 << PyramidLayout.FirstShift, values.Length - first));
            numbers[bucket] = (byte)MinMax(slice, out mins[bucket], out maxs[bucket]);
        }
    }

    private static ChunkStats<T> StatsAndLevel1Floating<T>(ReadOnlySpan<T> values, Span<T> mins, Span<T> maxs, Span<byte> numbers)
        where T : unmanaged, INumber<T>
    {
        // Unchecked reads: the vector loop loads [i, i + width) only while i + width <= end <= length, the pair check loads
        // [i + 1, i + width] only while i + width < length, and the scalar loops read below end or length. A bucket of 64
        // elements is a whole number of vectors, so only the last bucket of a chunk has a scalar tail.
        ref var first = ref MemoryMarshal.GetReference(values);
        var length = values.Length;
        var width = Vector<T>.Count;
        var positiveInfinity = new Vector<T>(T.CreateTruncating(double.PositiveInfinity));
        var negativeInfinity = new Vector<T>(T.CreateTruncating(double.NegativeInfinity));
        var sumLow = Vector<double>.Zero;
        var sumHigh = Vector<double>.Zero;
        var ordered = true;

        // Every pair (j, j + 1) with j below nextPair was compared; the scalar tail starts at tail.
        var nextPair = 0;
        var tail = length;
        long count = 0;
        var min = T.CreateTruncating(double.PositiveInfinity);
        var max = T.CreateTruncating(double.NegativeInfinity);
        for (var bucket = 0; bucket < numbers.Length; bucket++)
        {
            var start = bucket << PyramidLayout.FirstShift;
            var end = Math.Min(start + (1 << PyramidLayout.FirstShift), length);
            var bucketMin = positiveInfinity;
            var bucketMax = negativeInfinity;
            var bucketCount = 0;
            var i = start;
            for (; i <= end - width; i += width)
            {
                var x = Vector.LoadUnsafe(ref first, (nuint)i);
                if (ordered && i + width < length)
                {
                    ordered = Vector.LessThanOrEqualAll(x, Vector.LoadUnsafe(ref first, (nuint)(i + 1)));
                    nextPair = i + width;
                }

                if (Vector.EqualsAll(x, x))
                {
                    bucketMin = Vector.Min(bucketMin, x);
                    bucketMax = Vector.Max(bucketMax, x);
                    AddSum(x, ref sumLow, ref sumHigh);
                    bucketCount += width;
                    continue;
                }

                var isNumber = Vector.Equals(x, x);
                bucketMin = Vector.Min(bucketMin, Vector.ConditionalSelect(isNumber, x, positiveInfinity));
                bucketMax = Vector.Max(bucketMax, Vector.ConditionalSelect(isNumber, x, negativeInfinity));
                AddSum(Vector.ConditionalSelect(isNumber, x, Vector<T>.Zero), ref sumLow, ref sumHigh);
                for (var lane = 0; lane < width; lane++)
                {
                    if (!T.IsNaN(x[lane]))
                    {
                        bucketCount++;
                    }
                }
            }

            var low = bucketMin[0];
            var high = bucketMax[0];
            for (var lane = 1; lane < width; lane++)
            {
                low = T.Min(low, bucketMin[lane]);
                high = T.Max(high, bucketMax[lane]);
            }

            // The pairs of this bucket that the vector loop did not compare: near the end of the chunk, or a short bucket.
            for (; ordered && nextPair < end && nextPair + 1 < length; nextPair++)
            {
                ordered = Unsafe.Add(ref first, nextPair) <= Unsafe.Add(ref first, nextPair + 1);
            }

            if (i < end)
            {
                tail = i;
            }

            for (; i < end; i++)
            {
                var x = Unsafe.Add(ref first, i);
                if (!T.IsNaN(x))
                {
                    low = T.Min(low, x);
                    high = T.Max(high, x);
                    bucketCount++;
                }
            }

            numbers[bucket] = (byte)bucketCount;
            (mins[bucket], maxs[bucket]) = bucketCount == 0 ? (default(T), default(T)) : (low, high);

            // A bucket of only NaN leaves low and high at +∞ and −∞, which change neither.
            min = T.Min(min, low);
            max = T.Max(max, high);
            count += bucketCount;
        }

        // The sum is added in the order of ChunkStatsKernel, so that both give the same bits: the lanes, then the tail.
        double sum = 0;
        for (var lane = 0; lane < Vector<double>.Count; lane++)
        {
            sum += sumLow[lane] + sumHigh[lane];
        }

        for (var j = tail; j < length; j++)
        {
            var x = Unsafe.Add(ref first, j);
            if (!T.IsNaN(x))
            {
                sum += double.CreateTruncating(x);
            }
        }

        var nanCount = length - count;
        if (count == 0)
        {
            (min, max) = (T.Zero, T.Zero);
        }

        return new ChunkStats<T>(min, max, values[0], values[^1], sum, count, nanCount, nanCount == 0 && ordered);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddSum<T>(Vector<T> x, ref Vector<double> low, ref Vector<double> high)
        where T : unmanaged, INumber<T>
    {
        if (typeof(T) == typeof(float))
        {
            Vector.Widen(x.As<T, float>(), out var widenedLow, out var widenedHigh);
            low += widenedLow;
            high += widenedHigh;
        }
        else
        {
            low += x.As<T, double>();
        }
    }

    private static int MinMaxFloating<T>(ReadOnlySpan<T> values, out T min, out T max)
        where T : unmanaged, INumber<T>
    {
        // Unchecked reads: the vector loop loads [i, i + width) only while i <= length - width, and the tail reads i < length.
        ref var first = ref MemoryMarshal.GetReference(values);
        var length = values.Length;
        var width = Vector<T>.Count;
        var positiveInfinity = new Vector<T>(T.CreateTruncating(double.PositiveInfinity));
        var negativeInfinity = new Vector<T>(T.CreateTruncating(double.NegativeInfinity));
        var minimum = positiveInfinity;
        var maximum = negativeInfinity;
        var numbers = 0;
        var i = 0;
        for (; i <= length - width; i += width)
        {
            var x = Vector.LoadUnsafe(ref first, (nuint)i);
            if (Vector.EqualsAll(x, x))
            {
                minimum = Vector.Min(minimum, x);
                maximum = Vector.Max(maximum, x);
                numbers += width;
                continue;
            }

            var isNumber = Vector.Equals(x, x);
            minimum = Vector.Min(minimum, Vector.ConditionalSelect(isNumber, x, positiveInfinity));
            maximum = Vector.Max(maximum, Vector.ConditionalSelect(isNumber, x, negativeInfinity));
            for (var lane = 0; lane < width; lane++)
            {
                if (!T.IsNaN(x[lane]))
                {
                    numbers++;
                }
            }
        }

        var low = minimum[0];
        var high = maximum[0];
        for (var lane = 1; lane < width; lane++)
        {
            low = T.Min(low, minimum[lane]);
            high = T.Max(high, maximum[lane]);
        }

        for (; i < length; i++)
        {
            var x = Unsafe.Add(ref first, i);
            if (!T.IsNaN(x))
            {
                low = T.Min(low, x);
                high = T.Max(high, x);
                numbers++;
            }
        }

        (min, max) = numbers == 0 ? (default(T), default(T)) : (low, high);
        return numbers;
    }

    private static int MinMaxInteger<T>(ReadOnlySpan<T> values, out T min, out T max)
        where T : unmanaged, INumber<T>
    {
        if (values.IsEmpty)
        {
            (min, max) = (default, default);
            return 0;
        }

        // Unchecked reads: the vector loop loads [i, i + width) only while i <= length - width, and the tail reads i < length.
        ref var first = ref MemoryMarshal.GetReference(values);
        var length = values.Length;
        var width = Vector<T>.Count;
        var minimum = new Vector<T>(values[0]);
        var maximum = minimum;
        var i = 0;
        for (; i <= length - width; i += width)
        {
            var x = Vector.LoadUnsafe(ref first, (nuint)i);
            minimum = Vector.Min(minimum, x);
            maximum = Vector.Max(maximum, x);
        }

        var low = minimum[0];
        var high = maximum[0];
        for (var lane = 1; lane < width; lane++)
        {
            low = T.Min(low, minimum[lane]);
            high = T.Max(high, maximum[lane]);
        }

        for (; i < length; i++)
        {
            var x = Unsafe.Add(ref first, i);
            low = T.Min(low, x);
            high = T.Max(high, x);
        }

        (min, max) = (low, high);
        return length;
    }
}
