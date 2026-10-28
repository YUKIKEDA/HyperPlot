using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace HyperPlot;

/// <summary>The minimum, the maximum, and the count of the values that are not NaN, a vector at a time (design §3.3).</summary>
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
