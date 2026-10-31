namespace HyperPlot.Tests;

/// <summary>The raster and its mipmap of row G9 (design §3.7, §10's G9 row).</summary>
public sealed class RasterTests : IDisposable
{
    private readonly PlotWorkerPool _pool = new(2);

    public void Dispose() => _pool.Dispose();

    /// <summary>The factories set the width, the height, the values, and the kind, and check the column's length (design §3.7).</summary>
    /// <remarks>
    /// Preconditions:
    /// - Columns of 12 floats and 48 bytes.
    ///
    /// Steps:
    /// - Make rasters 4 wide; make ones of widths 5, 0, and −1, of a null column, and of an empty column; invalidate rows.
    ///
    /// Expected:
    /// - 4 × 3 of numbers and 4 × 3 RGBA; ArgumentException for 5 and the empty column, ArgumentOutOfRangeException for 0
    ///   and −1, ArgumentNullException for null; Invalidate moves the column's version over its rows' elements, and rows
    ///   outside throw ArgumentOutOfRangeException.
    /// </remarks>
    [Fact]
    public void Factories_CheckTheColumn()
    {
        using var values = Column.Wrap(new float[12]);
        using var bytes = Column.Wrap(new byte[48]);
        var numbers = Raster.Of(values, 4);
        var image = Raster.Rgba(bytes, 4);
        Assert.Equal((4, 3, false), (numbers.Width, numbers.Height, numbers.IsRgba));
        Assert.Equal((4, 3, true), (image.Width, image.Height, image.IsRgba));
        Assert.Same(values, numbers.Values);
        Assert.Same(bytes, ((Raster)image).Values);
        _ = Assert.Throws<ArgumentException>(() => Raster.Of(values, 5));
        _ = Assert.Throws<ArgumentException>(() => Raster.Rgba(bytes, 5));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Raster.Of(values, 0));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Raster.Of(values, -1));
        _ = Assert.Throws<ArgumentNullException>(() => Raster.Of<float>(null!, 4));
        using var empty = Column.Wrap(Array.Empty<float>());
        _ = Assert.Throws<ArgumentException>(() => Raster.Of(empty, 4));

        var version = values.Version;
        numbers.Invalidate(1, 2);
        Assert.True(values.Version > version);
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => numbers.Invalidate(2, 2));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => numbers.Invalidate(-1, 1));
    }

    /// <summary>
    /// Each level above 0 is the 2 × 2 average of the level under it, as counted on the CPU (design §3.7): floats without NaN,
    /// integers rounded a half away from 0 in 128 bits, RGBA premultiplied; the odd edges, and the top at most 256.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Seeded rasters 601 × 300 of floats with NaN, of ints, of longs near ±2^62, and an RGBA image; 2 workers.
    ///
    /// Steps:
    /// - Build each mipmap; average each level from the one under it on the CPU.
    ///
    /// Expected:
    /// - Every element equal; the level sizes ⌈half⌉ up to the first of sides at most 256; the elements above level 0 under
    ///   a third of the raster's.
    /// </remarks>
    [Fact]
    public void Levels_AreTheAverages()
    {
        const int Width = 601;
        const int Height = 300;
        var random = new Random(9);
        var floats = Enumerable.Range(0, Width * Height).Select(i => random.Next(20) == 0 ? float.NaN : (float)random.NextDouble()).ToArray();
        var ints = Enumerable.Range(0, Width * Height).Select(_ => random.Next(-7, 8)).ToArray();
        var longs = Enumerable.Range(0, Width * Height).Select(_ => (random.Next(2) == 0 ? 1 : -1) * ((1L << 62) - random.Next(4))).ToArray();
        var rgba = Enumerable.Range(0, Width * Height * 4).Select(_ => (byte)random.Next(256)).ToArray();
        using var floatColumn = Column.Wrap(floats);
        using var intColumn = Column.Wrap(ints);
        using var longColumn = Column.Wrap(longs);
        using var byteColumn = Column.Wrap(rgba);

        Check(
            Raster.Of(floatColumn, Width),
            static a => (float)a.Where(v => !float.IsNaN(v)).Select(v => (double)v).DefaultIfEmpty(double.NaN).Average()
        );
        Check(Raster.Of(intColumn, Width), static a => (int)Rounded([.. a.Select(v => (Int128)v)]));
        Check(Raster.Of(longColumn, Width), static a => (long)Rounded([.. a.Select(v => (Int128)v)]));

        using var image = (RasterPyramid<byte>)Raster.Rgba(byteColumn, Width).MakePyramid();
        image.Update(_pool);
        for (var level = 1; level < image.LevelCount; level++)
        {
            var (w, h) = image.Sizes[level];
            for (var row = 0; row < h; row++)
            {
                for (var column = 0; column < w; column++)
                {
                    var pixels = Quad(image, level, column, row).Select(p => Premultiplied(image, level - 1, p.Column, p.Row)).ToArray();
                    for (var channel = 0; channel < 4; channel++)
                    {
                        var sum = pixels.Sum(p => p[channel]);
                        Assert.Equal(((2 * sum) + pixels.Length) / (2 * pixels.Length), image.Read(level, column, row, channel));
                    }
                }
            }
        }

        static Int128 Rounded(Int128[] values)
        {
            var (total, n) = (values.Aggregate(Int128.Zero, (s, v) => s + v), values.Length);
            var magnitude = ((Int128.Abs(total) * 2) + n) / (2 * n);
            return total < 0 ? -magnitude : magnitude;
        }
    }

    /// <summary>
    /// A rewrite of some rows rebuilds the tiles of their chunks' rows and the rows above them only, and the levels then
    /// equal a mipmap built from the start (design §3.7).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A raster of floats 1,024 wide and 2,048 high (2 chunks), built once.
    ///
    /// Steps:
    /// - Rewrite a value in the second chunk's rows and invalidate its rows; update; build a new mipmap from the start.
    ///
    /// Expected:
    /// - Only the second chunk's tile rows are written (4 levels' worth, not all); the stamps of the first chunk's tiles at
    ///   level 0 stay; every element equals the new mipmap's.
    /// </remarks>
    [Fact]
    public void Invalidate_RebuildsTheChangedRowsOnly()
    {
        const int Width = 1024;
        const int Height = 2048;
        var values = Enumerable.Range(0, Width * Height).Select(i => (float)(i % 977)).ToArray();
        using var column = Column.Wrap(values);
        var raster = Raster.Of(column, Width);
        using var mipmap = (RasterPyramid<float>)raster.MakePyramid();
        mipmap.Update(_pool);
        var all = mipmap.TilesWritten;
        var firstStamp = mipmap.Stamp(0, 0);

        values[(1500 * Width) + 7] = 5000;
        raster.Invalidate(1500, 1);
        mipmap.Update(_pool);
        Assert.InRange(mipmap.TilesWritten, 1, (all / 2) + 4);
        Assert.Equal(firstStamp, mipmap.Stamp(0, 0));
        Assert.NotEqual(firstStamp, mipmap.Stamp(0, 1500 / 256));

        using var fresh = (RasterPyramid<float>)raster.MakePyramid();
        fresh.Update(_pool);
        for (var level = 1; level < mipmap.LevelCount; level++)
        {
            var (w, h) = mipmap.Sizes[level];
            for (var row = 0; row < h; row += 7)
            {
                for (var x = 0; x < w; x += 5)
                {
                    Assert.Equal(fresh.Read(level, x, row, 0), mipmap.Read(level, x, row, 0));
                }
            }
        }
    }

    /// <summary>A tile's face holds the tile and a border of a texel from its neighbors, the edge texels repeated past the raster (design §5.2).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A raster of floats 300 × 260 whose value is its index.
    ///
    /// Steps:
    /// - Copy tile (1, 0) and tile (0, 1) of level 0 with an origin of 10.
    ///
    /// Expected:
    /// - Face texel (x, y) is the raster's (256 · tile + x − 1, …) less 10, clamped to the raster.
    /// </remarks>
    [Fact]
    public void Tiles_CarryTheirNeighborsBorder()
    {
        const int Width = 300;
        const int Height = 260;
        using var column = Column.Wrap(Enumerable.Range(0, Width * Height).Select(i => (float)i).ToArray());
        using var mipmap = Raster.Of(column, Width).MakePyramid();
        mipmap.Update(_pool);
        var face = new float[258 * 258];
        foreach (var (tx, ty) in new[] { (1, 0), (0, 1) })
        {
            mipmap.CopyTile(0, tx, ty, 10, face);
            for (var y = 0; y < 258; y += 17)
            {
                for (var x = 0; x < 258; x += 13)
                {
                    var (c, r) = (Math.Clamp((tx * 256) + x - 1, 0, Width - 1), Math.Clamp((ty * 256) + y - 1, 0, Height - 1));
                    Assert.Equal(((r * Width) + c) - 10, face[(y * 258) + x]);
                }
            }
        }
    }

    private void Check<T>(Raster<T> raster, Func<T[], T> mean)
        where T : unmanaged, System.Numerics.INumber<T>
    {
        using var mipmap = (RasterPyramid<T>)raster.MakePyramid();
        mipmap.Update(_pool);
        Assert.True(mipmap.Sizes[^1].Width <= 256 && mipmap.Sizes[^1].Height <= 256);
        Assert.True(mipmap.Sizes[^2].Width > 256 || mipmap.Sizes[^2].Height > 256);
        var above = 0L;
        for (var level = 1; level < mipmap.LevelCount; level++)
        {
            var (w, h) = mipmap.Sizes[level];
            Assert.Equal(((mipmap.Sizes[level - 1].Width + 1) / 2, (mipmap.Sizes[level - 1].Height + 1) / 2), (w, h));
            above += (long)w * h;
            for (var row = 0; row < h; row++)
            {
                for (var column = 0; column < w; column++)
                {
                    var quad = Quad(mipmap, level, column, row).Select(p => mipmap.Read(level - 1, p.Column, p.Row, 0)).ToArray();
                    Assert.Equal(mean(quad), mipmap.Read(level, column, row, 0));
                }
            }
        }

        Assert.True(above < (long)raster.Width * raster.Height / 3);
    }

    private static (int Column, int Row)[] Quad(RasterPyramid mipmap, int level, int column, int row)
    {
        var (w, h) = mipmap.Sizes[level - 1];
        var columns = (2 * column) + 1 < w ? new[] { 2 * column, (2 * column) + 1 } : [2 * column];
        var rows = (2 * row) + 1 < h ? new[] { 2 * row, (2 * row) + 1 } : [2 * row];
        return [.. rows.SelectMany(r => columns.Select(c => (c, r)))];
    }

    private static uint[] Premultiplied(RasterPyramid<byte> mipmap, int level, int column, int row)
    {
        var p = Enumerable.Range(0, 4).Select(c => (uint)mipmap.Read(level, column, row, c)).ToArray();
        return level == 0 ? [((p[0] * p[3]) + 127) / 255, ((p[1] * p[3]) + 127) / 255, ((p[2] * p[3]) + 127) / 255, p[3]] : p;
    }
}
