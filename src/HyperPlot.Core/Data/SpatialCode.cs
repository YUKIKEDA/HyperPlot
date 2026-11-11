using System.Numerics;

namespace HyperPlot;

/// <summary>
/// The codes of a spatial tile index (design §3.4 "格子と符号"): the grid cells of a point's axes with their bits
/// interleaved, the last axis's bit above the others' at each step, so the upper D × L bits of a code are the point's
/// cell at level L.
/// </summary>
internal static class SpatialCode
{
    /// <summary>Returns the code of a point of two axes.</summary>
    /// <param name="x">The grid cell on x.</param>
    /// <param name="y">The grid cell on y.</param>
    /// <returns>The 64-bit code: bit 2i is bit i of <paramref name="x"/>, and bit 2i + 1 is bit i of <paramref name="y"/>.</returns>
    public static ulong Interleave(uint x, uint y) => Spread2(x) | (Spread2(y) << 1);

    /// <summary>Returns the code of a point of three axes.</summary>
    /// <param name="x">The grid cell on x, below 2^21.</param>
    /// <param name="y">The grid cell on y, below 2^21.</param>
    /// <param name="z">The grid cell on z, below 2^21.</param>
    /// <returns>The 63-bit code: bits 3i, 3i + 1, and 3i + 2 are bit i of x, y, and z.</returns>
    public static ulong Interleave(uint x, uint y, uint z) => Spread3(x) | (Spread3(y) << 1) | (Spread3(z) << 2);

    /// <summary>Returns the grid cell on one axis of a code of two axes.</summary>
    /// <param name="code">The code.</param>
    /// <param name="axis">The axis: 0 for x and 1 for y.</param>
    /// <returns>The grid cell.</returns>
    public static uint Axis2(ulong code, int axis)
    {
        var v = (code >> axis) & 0x5555_5555_5555_5555;
        v = (v | (v >> 1)) & 0x3333_3333_3333_3333;
        v = (v | (v >> 2)) & 0x0F0F_0F0F_0F0F_0F0F;
        v = (v | (v >> 4)) & 0x00FF_00FF_00FF_00FF;
        v = (v | (v >> 8)) & 0x0000_FFFF_0000_FFFF;
        v = (v | (v >> 16)) & 0x0000_0000_FFFF_FFFF;
        return (uint)v;
    }

    /// <summary>Returns the grid cell on one axis of a code of three axes.</summary>
    /// <param name="code">The code.</param>
    /// <param name="axis">The axis: 0 for x, 1 for y, and 2 for z.</param>
    /// <returns>The grid cell.</returns>
    public static uint Axis3(ulong code, int axis)
    {
        var v = (code >> axis) & 0x1249_2492_4924_9249;
        v = (v | (v >> 2)) & 0x10C3_0C30_C30C_30C3;
        v = (v | (v >> 4)) & 0x100F_00F0_0F00_F00F;
        v = (v | (v >> 8)) & 0x001F_0000_FF00_00FF;
        v = (v | (v >> 16)) & 0x001F_0000_0000_FFFF;
        v = (v | (v >> 32)) & 0x0000_0000_001F_FFFF;
        return (uint)v;
    }

    /// <summary>
    /// Writes the codes of the points of two axes whose stored values are both finite, in the order of the rows, and
    /// skips the others (design §3.4 "有限でない値").
    /// </summary>
    /// <typeparam name="TX">The type of the stored x values.</typeparam>
    /// <typeparam name="TY">The type of the stored y values.</typeparam>
    /// <param name="xs">The stored x values.</param>
    /// <param name="ys">The stored y values: as many as <paramref name="xs"/>.</param>
    /// <param name="xAxis">The quantization of x.</param>
    /// <param name="yAxis">The quantization of y.</param>
    /// <param name="codes">Where the codes go: room for as many as <paramref name="xs"/>.</param>
    /// <returns>The number of codes written.</returns>
    public static int Encode<TX, TY>(ReadOnlySpan<TX> xs, ReadOnlySpan<TY> ys, in SpatialAxis xAxis, in SpatialAxis yAxis, Span<ulong> codes)
        where TX : unmanaged, INumber<TX>
        where TY : unmanaged, INumber<TY>
    {
        // The slices let the JIT drop the bounds checks of ys and codes in the loop.
        ys = ys[..xs.Length];
        codes = codes[..xs.Length];
        var count = 0;
        for (var i = 0; i < xs.Length; i++)
        {
            var x = xs[i];
            var y = ys[i];
            if (TX.IsFinite(x) && TY.IsFinite(y))
            {
                codes[count++] = Interleave(xAxis.CellOf(x), yAxis.CellOf(y));
            }
        }

        return count;
    }

    /// <summary>
    /// Writes the codes of the points of three axes whose stored values are all finite, in the order of the rows, and
    /// skips the others.
    /// </summary>
    /// <typeparam name="TX">The type of the stored x values.</typeparam>
    /// <typeparam name="TY">The type of the stored y values.</typeparam>
    /// <typeparam name="TZ">The type of the stored z values.</typeparam>
    /// <param name="xs">The stored x values.</param>
    /// <param name="ys">The stored y values: as many as <paramref name="xs"/>.</param>
    /// <param name="zs">The stored z values: as many as <paramref name="xs"/>.</param>
    /// <param name="xAxis">The quantization of x.</param>
    /// <param name="yAxis">The quantization of y.</param>
    /// <param name="zAxis">The quantization of z.</param>
    /// <param name="codes">Where the codes go: room for as many as <paramref name="xs"/>.</param>
    /// <returns>The number of codes written.</returns>
    public static int Encode<TX, TY, TZ>(
        ReadOnlySpan<TX> xs,
        ReadOnlySpan<TY> ys,
        ReadOnlySpan<TZ> zs,
        in SpatialAxis xAxis,
        in SpatialAxis yAxis,
        in SpatialAxis zAxis,
        Span<ulong> codes
    )
        where TX : unmanaged, INumber<TX>
        where TY : unmanaged, INumber<TY>
        where TZ : unmanaged, INumber<TZ>
    {
        ys = ys[..xs.Length];
        zs = zs[..xs.Length];
        codes = codes[..xs.Length];
        var count = 0;
        for (var i = 0; i < xs.Length; i++)
        {
            var x = xs[i];
            var y = ys[i];
            var z = zs[i];
            if (TX.IsFinite(x) && TY.IsFinite(y) && TZ.IsFinite(z))
            {
                codes[count++] = Interleave(xAxis.CellOf(x), yAxis.CellOf(y), zAxis.CellOf(z));
            }
        }

        return count;
    }

    private static ulong Spread2(ulong v)
    {
        v = (v | (v << 16)) & 0x0000_FFFF_0000_FFFF;
        v = (v | (v << 8)) & 0x00FF_00FF_00FF_00FF;
        v = (v | (v << 4)) & 0x0F0F_0F0F_0F0F_0F0F;
        v = (v | (v << 2)) & 0x3333_3333_3333_3333;
        return (v | (v << 1)) & 0x5555_5555_5555_5555;
    }

    private static ulong Spread3(ulong v)
    {
        v &= 0x1F_FFFF;
        v = (v | (v << 32)) & 0x001F_0000_0000_FFFF;
        v = (v | (v << 16)) & 0x001F_0000_FF00_00FF;
        v = (v | (v << 8)) & 0x100F_00F0_0F00_F00F;
        v = (v | (v << 4)) & 0x10C3_0C30_C30C_30C3;
        return (v | (v << 2)) & 0x1249_2492_4924_9249;
    }
}
