using System.Numerics;
using System.Runtime.InteropServices;

namespace HyperPlot;

/// <summary>The mipmap of a raster of one element type (design §3.7).</summary>
/// <typeparam name="T">The element type.</typeparam>
internal sealed unsafe class RasterPyramid<T> : RasterPyramid
    where T : unmanaged, INumber<T>
{
    private readonly Raster<T> _raster;
    private readonly T*[] _levels;

    public RasterPyramid(Raster<T> raster)
        : base(raster)
    {
        _raster = raster;
        _levels = new T*[Sizes.Length];
        for (var level = 1; level < Sizes.Length; level++)
        {
            var (w, h) = Sizes[level];
            _levels[level] = (T*)NativeMemory.AlignedAlloc((nuint)((long)w * h * raster.Channels * sizeof(T)), 64);
        }
    }

    public override void Dispose()
    {
        for (var level = 1; level < _levels.Length; level++)
        {
            NativeMemory.AlignedFree(_levels[level]);
            _levels[level] = null;
        }
    }

    public override void CopyTile(int level, int tileColumn, int tileRow, double origin, Span<float> face)
    {
        const int side = TileSide + 2;
        var (w, h) = Sizes[level];
        for (var y = 0; y < side; y++)
        {
            var row = Math.Clamp((tileRow * TileSide) + y - 1, 0, h - 1);
            for (var x = 0; x < side; x++)
            {
                var column = Math.Clamp((tileColumn * TileSide) + x - 1, 0, w - 1);
                face[(y * side) + x] = (float)(double.CreateTruncating(Read(level, column, row, 0)) - origin);
            }
        }
    }

    public override void CopyTile(int level, int tileColumn, int tileRow, Span<uint> face)
    {
        const int side = TileSide + 2;
        var (w, h) = Sizes[level];
        for (var y = 0; y < side; y++)
        {
            var row = Math.Clamp((tileRow * TileSide) + y - 1, 0, h - 1);
            for (var x = 0; x < side; x++)
            {
                var column = Math.Clamp((tileColumn * TileSide) + x - 1, 0, w - 1);
                var (r, g, b, a) = Premultiplied(level, column, row);
                face[(y * side) + x] = r | (g << 8) | (b << 16) | (a << 24);
            }
        }
    }

    /// <summary>Returns a level's element at a column, a row, and a channel. Level 0 is read from the column.</summary>
    internal T Read(int level, int column, int row, int channel)
    {
        var index = ((((long)row * Sizes[level].Width) + column) * _raster.Channels) + channel;
        return level > 0
            ? _levels[level][index]
            : _raster.Values.GetChunk((int)(index >> Column.ChunkShift))[(int)(index & (Column.ChunkLength - 1))];
    }

    private protected override long GenerationOf(int chunk) => _raster.Values.GetGeneration(chunk);

    private protected override void Average(int level, int first, int last)
    {
        var (w, h) = Sizes[level];
        var (belowWidth, belowHeight) = Sizes[level - 1];
        for (var row = first; row <= last && row < h; row++)
        {
            // An edge pixel of an odd side stands alone: its twin past the edge is not read.
            var (r0, r1) = (2 * row, Math.Min((2 * row) + 1, belowHeight - 1));
            for (var column = 0; column < w; column++)
            {
                var (c0, c1) = (2 * column, Math.Min((2 * column) + 1, belowWidth - 1));
                if (_raster.IsRgba)
                {
                    AverageRgba(level, row, column, r0, r1, c0, c1);
                }
                else
                {
                    _levels[level][((long)row * w) + column] = AverageNumber(level - 1, r0, r1, c0, c1);
                }
            }
        }
    }

    /// <summary>
    /// Returns the mean of up to four elements of a level (design §3.7): a float's or a double's without its NaN, NaN when all
    /// are; an integer's rounded to the nearest, a half away from 0, its sum taken in 128 bits.
    /// </summary>
    private T AverageNumber(int below, int r0, int r1, int c0, int c1)
    {
        Span<T> values = [Read(below, c0, r0, 0), default, default, default];
        var n = 1;
        if (c1 != c0)
        {
            values[n++] = Read(below, c1, r0, 0);
        }

        if (r1 != r0)
        {
            values[n++] = Read(below, c0, r1, 0);
            if (c1 != c0)
            {
                values[n++] = Read(below, c1, r1, 0);
            }
        }

        if (typeof(T) == typeof(float) || typeof(T) == typeof(double))
        {
            var (sum, count) = (0.0, 0);
            for (var i = 0; i < n; i++)
            {
                var v = double.CreateTruncating(values[i]);
                if (!double.IsNaN(v))
                {
                    (sum, count) = (sum + v, count + 1);
                }
            }

            return count == 0 ? T.CreateTruncating(double.NaN) : T.CreateTruncating(sum / count);
        }

        var total = Int128.Zero;
        for (var i = 0; i < n; i++)
        {
            total += Int128.CreateTruncating(values[i]);
        }

        return T.CreateTruncating(RoundedQuotient(total, n));
    }

    /// <summary>Writes the mean of up to four premultiplied pixels of the level under, premultiplied, rounded a half up.</summary>
    private void AverageRgba(int level, int row, int column, int r0, int r1, int c0, int c1)
    {
        var below = level - 1;
        var (r, g, b, a) = Premultiplied(below, c0, r0);
        var n = 1u;
        void Add((uint R, uint G, uint B, uint A) p) => (r, g, b, a, n) = (r + p.R, g + p.G, b + p.B, a + p.A, n + 1);
        if (c1 != c0)
        {
            Add(Premultiplied(below, c1, r0));
        }

        if (r1 != r0)
        {
            Add(Premultiplied(below, c0, r1));
            if (c1 != c0)
            {
                Add(Premultiplied(below, c1, r1));
            }
        }

        var index = (((long)row * Sizes[level].Width) + column) * 4;
        var target = _levels[level];
        target[index] = T.CreateTruncating(((2 * r) + n) / (2 * n));
        target[index + 1] = T.CreateTruncating(((2 * g) + n) / (2 * n));
        target[index + 2] = T.CreateTruncating(((2 * b) + n) / (2 * n));
        target[index + 3] = T.CreateTruncating(((2 * a) + n) / (2 * n));
    }

    /// <summary>Returns a pixel of an RGBA level premultiplied: level 0 holds straight alpha, the levels above premultiplied.</summary>
    private (uint R, uint G, uint B, uint A) Premultiplied(int level, int column, int row)
    {
        var (r, g, b, a) = (Byte(level, column, row, 0), Byte(level, column, row, 1), Byte(level, column, row, 2), Byte(level, column, row, 3));
        return level == 0 ? (Premultiply(r, a), Premultiply(g, a), Premultiply(b, a), a) : (r, g, b, a);
    }

    private uint Byte(int level, int column, int row, int channel) => uint.CreateTruncating(Read(level, column, row, channel));

    private static uint Premultiply(uint value, uint alpha) => ((value * alpha) + 127) / 255;

    /// <summary>Returns total / n rounded to the nearest, a half away from 0.</summary>
    private static Int128 RoundedQuotient(Int128 total, int n)
    {
        var twice = (Int128.Abs(total) * 2) + n;
        var magnitude = twice / (2 * n);
        return total < 0 ? -magnitude : magnitude;
    }
}
