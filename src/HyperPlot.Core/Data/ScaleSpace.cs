using System.Numerics;

namespace HyperPlot;

/// <summary>The difference d(v) of a value from an origin of the scale space (design §3.6).</summary>
internal static class ScaleSpace
{
    /// <summary>
    /// Returns v − origin as a <see cref="double"/>. An integer column's difference is exact before it is rounded once; a
    /// floating-point column's is <c>(double)v − (double)origin</c>. Non-decreasing in v for every type.
    /// </summary>
    /// <typeparam name="T">The element type of the column.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="origin">The origin, in the column's unit.</param>
    /// <returns>The difference.</returns>
    public static double Difference<T>(T value, long origin)
        where T : unmanaged, INumber<T>
    {
        // The type test is a constant for each T, so each instantiation keeps one branch.
        return IsInteger<T>() ? Difference(long.CreateTruncating(value), origin) : double.CreateTruncating(value) - origin;
    }

    /// <summary>Returns whether a column of <typeparamref name="T"/> holds integers: <c>long</c>, <c>int</c>, or <c>byte</c> (design §3.1).</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <returns>Whether the values are integers.</returns>
    public static bool IsInteger<T>()
        where T : unmanaged, INumber<T> => typeof(T) == typeof(long) || typeof(T) == typeof(int) || typeof(T) == typeof(byte);

    /// <summary>Returns the exact difference of two integers, rounded once to <see cref="double"/>.</summary>
    /// <param name="value">The value.</param>
    /// <param name="origin">The origin.</param>
    /// <returns>The difference.</returns>
    public static double Difference(long value, long origin)
    {
        var difference = unchecked(value - origin);

        // The subtraction overflows only when the signs differ and the result's sign is not the value's.
        return ((value ^ origin) & (value ^ difference)) < 0 ? (double)((Int128)value - origin) : difference;
    }
}
