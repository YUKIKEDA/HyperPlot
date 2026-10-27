using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace HyperPlot;

/// <summary>
/// Computes <see cref="ChunkStats{T}"/> with <see cref="Vector{T}"/> (design §3.2). <see cref="float"/> and
/// <see cref="double"/> leave NaN out of the minimum, the maximum, the sum, and the count.
/// </summary>
internal static class ChunkStatsKernel
{
    internal static ChunkStats<T> Compute<T>(ReadOnlySpan<T> values)
        where T : unmanaged, INumber<T>
    {
        if (values.IsEmpty)
        {
            return ChunkStats<T>.Empty;
        }

        if (typeof(T) == typeof(float))
        {
            var stats = ComputeSingle(MemoryMarshal.Cast<T, float>(values));
            return Unsafe.As<ChunkStats<float>, ChunkStats<T>>(ref stats);
        }

        if (typeof(T) == typeof(double))
        {
            var stats = ComputeDouble(MemoryMarshal.Cast<T, double>(values));
            return Unsafe.As<ChunkStats<double>, ChunkStats<T>>(ref stats);
        }

        return ComputeInteger(values);
    }

    private static ChunkStats<float> ComputeSingle(ReadOnlySpan<float> values)
    {
        ref var first = ref MemoryMarshal.GetReference(values);
        var length = values.Length;
        var width = Vector<float>.Count;
        var positiveInfinity = new Vector<float>(float.PositiveInfinity);
        var negativeInfinity = new Vector<float>(float.NegativeInfinity);
        var minimum = positiveInfinity;
        var maximum = negativeInfinity;
        var sumLow = Vector<double>.Zero;
        var sumHigh = Vector<double>.Zero;
        var count = Vector<int>.Zero;
        var i = 0;
        for (; i <= length - width; i += width)
        {
            var x = Vector.LoadUnsafe(ref first, (nuint)i);
            var isNumber = Vector.Equals(x, x);
            minimum = Vector.Min(minimum, Vector.ConditionalSelect(isNumber, x, positiveInfinity));
            maximum = Vector.Max(maximum, Vector.ConditionalSelect(isNumber, x, negativeInfinity));
            Vector.Widen(Vector.ConditionalSelect(isNumber, x, Vector<float>.Zero), out var low, out var high);
            sumLow += low;
            sumHigh += high;

            // A true lane of the mask is -1.
            count -= isNumber;
        }

        var min = float.PositiveInfinity;
        var max = float.NegativeInfinity;
        double sum = 0;
        long numbers = 0;
        for (var lane = 0; lane < width; lane++)
        {
            min = Math.Min(min, minimum[lane]);
            max = Math.Max(max, maximum[lane]);
            numbers += count[lane];
        }

        for (var lane = 0; lane < Vector<double>.Count; lane++)
        {
            sum += sumLow[lane] + sumHigh[lane];
        }

        for (; i < length; i++)
        {
            var x = Unsafe.Add(ref first, i);
            if (!float.IsNaN(x))
            {
                min = Math.Min(min, x);
                max = Math.Max(max, x);
                sum += x;
                numbers++;
            }
        }

        var nanCount = length - numbers;
        if (numbers == 0)
        {
            (min, max) = (0, 0);
        }

        var nonDecreasing = nanCount == 0 && IsNonDecreasing(values);
        return new ChunkStats<float>(min, max, values[0], values[^1], sum, numbers, nanCount, nonDecreasing);
    }

    private static ChunkStats<double> ComputeDouble(ReadOnlySpan<double> values)
    {
        ref var first = ref MemoryMarshal.GetReference(values);
        var length = values.Length;
        var width = Vector<double>.Count;
        var positiveInfinity = new Vector<double>(double.PositiveInfinity);
        var negativeInfinity = new Vector<double>(double.NegativeInfinity);
        var minimum = positiveInfinity;
        var maximum = negativeInfinity;
        var sums = Vector<double>.Zero;
        var count = Vector<long>.Zero;
        var i = 0;
        for (; i <= length - width; i += width)
        {
            var x = Vector.LoadUnsafe(ref first, (nuint)i);
            var isNumber = Vector.Equals(x, x);
            minimum = Vector.Min(minimum, Vector.ConditionalSelect(isNumber, x, positiveInfinity));
            maximum = Vector.Max(maximum, Vector.ConditionalSelect(isNumber, x, negativeInfinity));
            sums += Vector.ConditionalSelect(isNumber, x, Vector<double>.Zero);
            count -= isNumber;
        }

        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        double sum = 0;
        long numbers = 0;
        for (var lane = 0; lane < width; lane++)
        {
            min = Math.Min(min, minimum[lane]);
            max = Math.Max(max, maximum[lane]);
            sum += sums[lane];
            numbers += count[lane];
        }

        for (; i < length; i++)
        {
            var x = Unsafe.Add(ref first, i);
            if (!double.IsNaN(x))
            {
                min = Math.Min(min, x);
                max = Math.Max(max, x);
                sum += x;
                numbers++;
            }
        }

        var nanCount = length - numbers;
        if (numbers == 0)
        {
            (min, max) = (0, 0);
        }

        var nonDecreasing = nanCount == 0 && IsNonDecreasing(values);
        return new ChunkStats<double>(min, max, values[0], values[^1], sum, numbers, nanCount, nonDecreasing);
    }

    private static ChunkStats<T> ComputeInteger<T>(ReadOnlySpan<T> values)
        where T : unmanaged, INumber<T>
    {
        ref var first = ref MemoryMarshal.GetReference(values);
        var length = values.Length;
        var width = Vector<T>.Count;
        var minimum = new Vector<T>(values[0]);
        var maximum = minimum;
        double sum = 0;
        var i = 0;
        for (; i <= length - width; i += width)
        {
            var x = Vector.LoadUnsafe(ref first, (nuint)i);
            minimum = Vector.Min(minimum, x);
            maximum = Vector.Max(maximum, x);
        }

        var min = minimum[0];
        var max = maximum[0];
        for (var lane = 1; lane < width; lane++)
        {
            min = T.Min(min, minimum[lane]);
            max = T.Max(max, maximum[lane]);
        }

        for (; i < length; i++)
        {
            var x = Unsafe.Add(ref first, i);
            min = T.Min(min, x);
            max = T.Max(max, x);
        }

        // The sum is not vectorized: it needs a widening per element type, and integer columns are categories and counts,
        // not the billion-point series.
        for (var j = 0; j < length; j++)
        {
            sum += double.CreateTruncating(Unsafe.Add(ref first, j));
        }

        return new ChunkStats<T>(min, max, values[0], values[^1], sum, length, 0, IsNonDecreasing(values));
    }

    /// <summary>Compares each element with the next, a vector at a time. Any NaN makes it false.</summary>
    private static bool IsNonDecreasing<T>(ReadOnlySpan<T> values)
        where T : unmanaged, INumber<T>
    {
        ref var first = ref MemoryMarshal.GetReference(values);
        var pairs = values.Length - 1;
        var width = Vector<T>.Count;
        var i = 0;
        for (; i <= pairs - width; i += width)
        {
            var current = Vector.LoadUnsafe(ref first, (nuint)i);
            var next = Vector.LoadUnsafe(ref first, (nuint)(i + 1));
            if (!Vector.LessThanOrEqualAll(current, next))
            {
                return false;
            }
        }

        for (; i < pairs; i++)
        {
            if (!(Unsafe.Add(ref first, i) <= Unsafe.Add(ref first, i + 1)))
            {
                return false;
            }
        }

        return true;
    }
}
