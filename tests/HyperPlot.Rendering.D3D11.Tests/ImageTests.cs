using System.Numerics;
using TerraFX.Interop.DirectX;

namespace HyperPlot.Rendering.D3D11.Tests;

/// <summary>
/// The image pipeline on WARP (design §5.3, §10's G9 row): tile records written by hand over tiles the backend's residency
/// cache sends, against the shader's formulas computed apart on the CPU.
/// </summary>
public sealed class ImageTests : IDisposable
{
    private const int Height = 32;

    // The flag of ImageInstance.Flags that leaves a number of exactly 0 transparent (row R3b1).
    private const uint ZeroTransparent = 64;
    private static readonly Color Background = new(250, 245, 235, 255);
    private readonly PlotWorkerPool _pool = new(1);

    public void Dispose() => _pool.Dispose();

    /// <summary>
    /// A number is painted at the index ColorMap.Map takes, through each transform (design §4.2): linear, log, symlog, pow,
    /// diverging around its middle, reversed; NaN and a value that does not map are transparent.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A raster of 6 values, nearest, each texel 16 pixels wide; viridis.
    ///
    /// Steps:
    /// - Draw each case; read the middle of each texel.
    ///
    /// Expected:
    /// - viridis.Map(t) with t of the case's formula; the background where the value is NaN or does not map.
    /// </remarks>
    [Theory]
    [InlineData(0u, 0.0, 0.0, 1.0, 0.0, false, false, new[] { 0.0, 0.2, 0.5, 0.9, 1.0, 1.5 })]
    [InlineData(1u, 0.0, 0.0, 3.0, 0.0, false, false, new[] { 1.0, 3.0, 10.0, 300.0, -1.0, 2000.0 })]
    [InlineData(2u, 1.0, 0.0, 2.0, 0.0, false, false, new[] { 0.0, 5.0, 20.0, -3.0, 99.0, 50.0 })]
    [InlineData(3u, 0.5, 0.0, 3.0, 0.0, false, false, new[] { 0.0, 1.0, 4.0, 7.5, double.NaN, 9.0 })]
    [InlineData(0u, 0.0, -2.0, 6.0, 1.0, true, false, new[] { -2.0, -0.5, 1.0, 2.5, 6.0, 4.0 })]
    [InlineData(0u, 0.0, 0.0, 1.0, 0.0, false, true, new[] { 0.1, 0.3, 0.5, 0.7, 0.9, 1.0 })]
    public void Paint_AsTheColorMapIndex(
        uint kind,
        double constant,
        double low,
        double high,
        double middle,
        bool diverging,
        bool reverse,
        double[] values
    )
    {
        var map = ColorMaps.Viridis;
        var (pixels, width) = Draw(values, kind, constant, low, high, middle, diverging, reverse, map, bilinear: false, opacity: 1);
        for (var i = 0; i < values.Length; i++)
        {
            var s = Forward(values[i], kind, constant);
            var expected = !double.IsFinite(s) ? Background : map.Map(Position(s, low, high, middle, diverging, reverse));
            AssertPixel(pixels, width, (16 * i) + 8, Height / 2, expected.A == 0 ? Background : expected, 0);
        }
    }

    /// <summary>
    /// Bilinear across a tile's seam reads the neighbor's border (design §5.3): a ramp of values stays a ramp through the seam.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A raster 300 × 1 of values equal to their column; texels 250 to 262 drawn 10 pixels each, the two tiles' records
    ///   side by side; a color domain of [250, 262].
    ///
    /// Steps:
    /// - Draw bilinear; read every pixel along the middle row.
    ///
    /// Expected:
    /// - Each pixel the color of the value interpolated at its center (its texel position − 0.5), within an index of the
    ///   table: no step at the seam.
    /// </remarks>
    [Fact]
    public void Bilinear_ThroughTheSeam()
    {
        const int Columns = 300;
        var values = Enumerable.Range(0, Columns).Select(i => (double)i).ToArray();
        using var column = Column.Wrap(values.Select(v => (float)v).ToArray());
        using var mipmap = Raster.Of(column, Columns).MakePyramid();
        mipmap.Update(_pool, wait: true);
        using var backend = D3D11Backend.Create(useWarp: true);
        const int Width = 120;
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(Width, Height));
        var cache = backend.Images;
        cache.BeginFrame();
        var map = ColorMaps.Viridis;
        var list = new RenderList();
        foreach (var tile in new[] { 0, 1 })
        {
            var slot = cache.Request(new ResidencyCache.TileKey(mipmap, ResidencyCache.Format.Float, 0, tile, 0, mipmap.Stamp(0, 0), 0), mipmap);
            var (from, to) = tile == 0 ? (250f, 256f) : (0f, 6f);
            var record = Paint(0, 0, 250, 262, 0, false, false, (uint)cache.Table(map), bilinear: true, opacity: 1);
            record.Origin = new Vector2(-2500 + (tile * 2560), 0);
            record.AlongU = new Vector2(10, 0);
            record.AlongV = new Vector2(0, Height);
            record.Range = new Vector4(from, 0, to, 1);
            record.SampleScale = 1;
            record.Slice = (uint)(slot % ResidencyCache.PageSlots);
            list.DrawImage(record, ImagePlan.PageOf(false, slot));
        }

        target.Clear(Background);
        backend.Draw(target, list);
        var pixels = new byte[Width * Height * 4];
        target.ReadPixels(pixels);
        for (var x = 0; x < Width; x++)
        {
            var at = 250 + ((x + 0.5) / 10);
            var value = Math.Clamp(at - 0.5, 0, Columns - 1);
            var index = Index((value - 250) / 12);
            AssertPixel(pixels, Width, x, Height / 2, [map[Math.Max(0, index - 1)], map[index], map[Math.Min(255, index + 1)]]);
        }
    }

    /// <summary>The opacity and an RGBA image's premultiplied colors blend over the target (design §5.3).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A number at t = 0.5 drawn at opacity 0.5; an RGBA texel (200, 100, 50, 128) at opacity 0.8.
    ///
    /// Steps:
    /// - Draw each over the background; read the middle.
    ///
    /// Expected:
    /// - The premultiplied color times the opacity over the background, within 2 of 255.
    /// </remarks>
    [Fact]
    public void Opacity_AndRgba_BlendOver()
    {
        var map = ColorMaps.Viridis;
        var (pixels, width) = Draw([0.5], 0, 0, 0, 1, 0, false, false, map, bilinear: false, opacity: 0.5f);
        AssertPixel(pixels, width, 8, Height / 2, Over(map.Map(0.5), 0.5), 2);

        using var column = Column.Wrap(new byte[] { 200, 100, 50, 128 });
        using var mipmap = Raster.Rgba(column, 1).MakePyramid();
        mipmap.Update(_pool, wait: true);
        using var backend = D3D11Backend.Create(useWarp: true);
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(16, Height));
        backend.Images.BeginFrame();
        var slot = backend.Images.Request(new ResidencyCache.TileKey(mipmap, ResidencyCache.Format.Rgba, 0, 0, 0, mipmap.Stamp(0, 0), 0), mipmap);
        var record = new ImageInstance
        {
            AlongU = new Vector2(16, 0),
            AlongV = new Vector2(0, Height),
            Range = new Vector4(0, 0, 1, 1),
            SampleScale = 1,
            Slice = (uint)(slot % ResidencyCache.PageSlots),
            Flags = 2,
            Opacity = 0.8f,
        };
        var list = new RenderList();
        list.DrawImage(record, ImagePlan.PageOf(true, slot));
        target.Clear(Background);
        backend.Draw(target, list);
        var rgba = new byte[16 * Height * 4];
        target.ReadPixels(rgba);
        AssertPixel(rgba, 16, 8, Height / 2, Over(new Color(200, 100, 50, 128), 0.8), 2);
    }

    /// <summary>
    /// The flag of bit 6 leaves a number of exactly 0 transparent and paints every other number as before; without it, 0
    /// is painted as any number (design §5.5.2 "描き方", row R3b1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A raster of 6 values with 0 at the first, the third, and the last texel, nearest, each texel 16 pixels wide;
    ///   viridis over [0, 1].
    ///
    /// Steps:
    /// - Draw it with the flag and without; read the middle of each texel.
    ///
    /// Expected:
    /// - With the flag: the background at the texels of 0, viridis.Map(value) at the others.
    /// - Without: viridis.Map(value) at every texel, the first color at the texels of 0.
    /// </remarks>
    [Fact]
    public void ZeroTransparent_LeavesOnlyZeroTransparent()
    {
        var map = ColorMaps.Viridis;
        double[] values = [0, 0.5, 0, 1, 0.25, 0];
        var (flagged, width) = Draw(values, 0, 0, 0, 1, 0, false, false, map, bilinear: false, opacity: 1, zeroTransparent: true);
        var (plain, _) = Draw(values, 0, 0, 0, 1, 0, false, false, map, bilinear: false, opacity: 1);
        for (var i = 0; i < values.Length; i++)
        {
            AssertPixel(flagged, width, (16 * i) + 8, Height / 2, values[i] == 0 ? Background : map.Map(values[i]), 0);
            AssertPixel(plain, width, (16 * i) + 8, Height / 2, map.Map(values[i]), 0);
        }

        Assert.NotEqual(Background, map.Map(0));
    }

    /// <summary>
    /// A density tile's face, read nearest through the symlog of constant 1 up to its largest count, draws each bin's count
    /// at its color and the empty bins transparent (design §5.5.2 "描き方", row R3b1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A tile of 2-byte bins whose first row begins 0, 1, 5, 0, 300, 1,000 and whose other bins are 0, requested from
    ///   the backend's cache for the full snapshot of 100 points; viridis.
    ///
    /// Steps:
    /// - Draw the first 6 bins of the first row, 16 pixels each, with the flag of bit 6, symlog of constant 1, and the
    ///   domain [0, symlog(1,000)]; read the middle of each bin.
    ///
    /// Expected:
    /// - The background at the bins of 0; at the others, viridis at log10(1 + c) / log10(1,001), within an index of the
    ///   table (log10 on the GPU and on the CPU may round apart at an index's edge).
    /// </remarks>
    [Fact]
    public async Task DensityFace_DrawsTheCountsAndLeavesEmptyBinsTransparent()
    {
        ulong[] counts = [0, 1, 5, 0, 300, 1_000];
        var bins = new byte[DensityTiles.BinCount * 2];
        for (var x = 0; x < counts.Length; x++)
        {
            bins[2 * x] = (byte)counts[x];
            bins[(2 * x) + 1] = (byte)(counts[x] >> 8);
        }

        using var pool = new PlotWorkerPool(1);
        var random = new Random(37);
        using var xs = Column.Wrap(Enumerable.Range(0, 100).Select(_ => random.NextDouble()).ToArray());
        using var ys = Column.Wrap(Enumerable.Range(0, 100).Select(_ => random.NextDouble()).ToArray());
        using var index = pool.BuildSpatialIndex(xs, ys, new SpatialIndexInput { Rows = RowRange.All });
        await index.WhenComplete();
        var snapshot = index.Acquire();
        try
        {
            using var backend = D3D11Backend.Create(useWarp: true);
            var width = counts.Length * 16;
            using var target = backend.CreateRenderTarget(new RenderTargetDescription(width, Height));
            var map = ColorMaps.Viridis;
            backend.Images.BeginFrame();
            var slot = backend.Images.RequestDensity(snapshot, 0, 0, new DensityTile(bins, 2, 0));
            var high = Forward(1_000, 2, 1);
            var record = Paint(2, 1, 0, high, 0, false, false, (uint)backend.Images.Table(map), bilinear: false, opacity: 1);
            record.Flags |= ZeroTransparent;
            record.AlongU = new Vector2(16, 0);
            record.AlongV = new Vector2(0, Height);
            record.Range = new Vector4(0, 0, counts.Length, 1);
            record.SampleScale = 1;
            record.Slice = (uint)(slot % ResidencyCache.PageSlots);
            var list = new RenderList();
            list.DrawImage(record, ImagePlan.PageOf(false, slot));
            target.Clear(Background);
            backend.Draw(target, list);
            var pixels = new byte[width * Height * 4];
            target.ReadPixels(pixels);
            for (var i = 0; i < counts.Length; i++)
            {
                if (counts[i] == 0)
                {
                    AssertPixel(pixels, width, (16 * i) + 8, Height / 2, Background, 0);
                    continue;
                }

                var expected = Index(Forward(counts[i], 2, 1) / high);
                AssertPixel(
                    pixels,
                    width,
                    (16 * i) + 8,
                    Height / 2,
                    [map[Math.Max(0, expected - 1)], map[expected], map[Math.Min(255, expected + 1)]]
                );
            }
        }
        finally
        {
            snapshot.Release();
        }
    }

    /// <summary>The density cases: each norm, the layer's ramp and viridis, a tile at the chosen level and one 2 levels shallower.</summary>
    public static TheoryData<DensityNorm, bool, int> DensityCases
    {
        get
        {
            var cases = new TheoryData<DensityNorm, bool, int>();
            foreach (var norm in new[] { DensityNorm.Log, DensityNorm.Linear, DensityNorm.Cbrt })
            {
                foreach (var viridis in new[] { false, true })
                {
                    foreach (var shallower in new[] { 0, 2 })
                    {
                        cases.Add(norm, viridis, shallower);
                    }
                }
            }

            return cases;
        }
    }

    /// <summary>
    /// The image pixel shader paints a density tile's bins at the color the CPU gives the same normalized count, for each
    /// norm, with the layer's ramp and with a color map, for a tile of the chosen level and for an ancestor's tile; a bin of
    /// 0 is transparent in both (design §5.5.2 "描き方", "広さで正規化する", "祖先のタイル", §10's R3b1 row).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A top m of 1,000 points a bin of the chosen level. A tile of 2-byte bins requested from the backend's cache for the
    ///   full snapshot of 100 points. For a tile k levels shallower than the chosen level (k of 0 and 2), the bins hold raw
    ///   counts r of the ancestor's bins: a count c a bin of the chosen level is r = c · 4^k.
    /// - One row of 11 bins: 0; nine counts from about 1 to 900 points a bin of the chosen level, each raised to the next raw
    ///   count whose t · 256 lies at least 0.05 from an index's edge, so that float32 on the GPU and double on the CPU take
    ///   the same index; 0 again; and m · 4^k, the top.
    /// - At k = 0 the row is the tile's first row from bin 0, read with SampleOrigin (0, 0) and SampleScale 1. At k = 2 it is
    ///   row 128 from bin 64, read as the engine reads an ancestor: SampleOrigin (64, 128) and SampleScale 2^-2, the drawn
    ///   range 4 tile texels a face texel.
    /// - The cases: Log, Linear, and Cbrt; the ramp from transparent to (31, 119, 180) and viridis; k of 0 and 2.
    ///
    /// Steps:
    /// - Record one image instance with the transform, the constant, and the high end of DensityMath.Paint(norm, m, k), a low
    ///   end of 0, the flag of bit 6, opacity 1, and the map's table; each bin 16 pixels wide. Draw it over the background and
    ///   read the middle of each bin.
    ///
    /// Expected:
    /// - The data: every count but the zeros and the top lies at least 0.05 from an index's edge (asserted while it is made).
    /// - A bin of 0 is the background, with viridis too, whose first color is not the background.
    /// - Every other bin is map.Map(DensityMath.Position(norm, r / 4^k, m)) over the background, within 1 of 255 in each
    ///   channel: the rounding of the blend.
    /// </remarks>
    [Theory]
    [MemberData(nameof(DensityCases))]
    public async Task DensityPaint_MatchesTheCpuColor(DensityNorm norm, bool viridis, int shallower)
    {
        const double Top = 1_000;
        var area = Math.Pow(4, shallower);
        var map = viridis ? ColorMaps.Viridis : DensityMath.Ramp(new Color(31, 119, 180, 255));
        var counts = new List<ulong> { 0 };
        foreach (var count in new[] { 1.0, 3, 10, 40, 120, 300, 550, 750, 900 })
        {
            counts.Add(AwayFromEdges(norm, (ulong)Math.Ceiling(count * area), area, Top));
        }

        counts.Add(0);
        counts.Add((ulong)(Top * area));
        var (originX, originY) = shallower == 0 ? (0, 0) : (64, 128);
        var bins = new byte[DensityTiles.BinCount * 2];
        for (var i = 0; i < counts.Count; i++)
        {
            var at = 2 * ((originY * DensityTiles.Side) + originX + i);
            bins[at] = (byte)counts[i];
            bins[at + 1] = (byte)(counts[i] >> 8);
        }

        var random = new Random(41);
        using var xs = Column.Wrap(Enumerable.Range(0, 100).Select(_ => random.NextDouble()).ToArray());
        using var ys = Column.Wrap(Enumerable.Range(0, 100).Select(_ => random.NextDouble()).ToArray());
        using var index = _pool.BuildSpatialIndex(xs, ys, new SpatialIndexInput { Rows = RowRange.All });
        await index.WhenComplete();
        var snapshot = index.Acquire();
        try
        {
            using var backend = D3D11Backend.Create(useWarp: true);
            var width = counts.Count * 16;
            using var target = backend.CreateRenderTarget(new RenderTargetDescription(width, Height));
            backend.Images.BeginFrame();
            var slot = backend.Images.RequestDensity(snapshot, 0, 0, new DensityTile(bins, 2, counts.Max()));
            var (kind, constant, high) = DensityMath.Paint(norm, Top, shallower);
            var texels = Math.Pow(2, shallower);
            var record = new ImageInstance
            {
                AlongU = new Vector2((float)(16 / texels), 0),
                AlongV = new Vector2(0, (float)(Height / texels)),
                Range = new Vector4(0, 0, (float)(counts.Count * texels), (float)texels),
                SampleOrigin = new Vector2(originX, originY),
                SampleScale = (float)(1 / texels),
                Slice = (uint)(slot % ResidencyCache.PageSlots),
                Flags = (kind << 2) | ZeroTransparent,
                Table = (uint)backend.Images.Table(map),
                Constant = constant,
                Low = 0,
                High = high,
                Opacity = 1,
            };
            var list = new RenderList();
            list.DrawImage(record, ImagePlan.PageOf(false, slot));
            target.Clear(Background);
            backend.Draw(target, list);
            var pixels = new byte[width * Height * 4];
            target.ReadPixels(pixels);
            for (var i = 0; i < counts.Count; i++)
            {
                var expected = counts[i] == 0 ? Background : Over(map.Map(DensityMath.Position(norm, counts[i] / area, Top)), 1);
                AssertPixel(pixels, width, (16 * i) + 8, Height / 2, expected, 1);
            }

            Assert.NotEqual(Background, ColorMaps.Viridis.Map(0));
        }
        finally
        {
            snapshot.Release();
        }
    }

    /// <summary>After a device loss the cache forgets its tiles, and the next frame sends them again and draws the same (design §5.2).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A drawn raster of 6 values.
    ///
    /// Steps:
    /// - Lose the device through Map; request the tiles again and draw.
    ///
    /// Expected:
    /// - The loss throws GpuDeviceLostException and leaves no slot in use; the next draw reads the same pixels.
    /// </remarks>
    [Fact]
    public void DeviceLost_SendsTheTilesAgain()
    {
        double[] values = [0, 0.2, 0.5, 0.9, 1, 0.4];
        using var column = Column.Wrap(values.Select(v => (float)v).ToArray());
        using var mipmap = Raster.Of(column, values.Length).MakePyramid();
        mipmap.Update(_pool, wait: true);
        using var backend = D3D11Backend.Create(useWarp: true);
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(values.Length * 16, Height));
        var first = DrawAndRead();
        backend.CheckResult = (call, result) => call == "Map" ? DXGI.DXGI_ERROR_DEVICE_REMOVED : result;
        _ = Assert.Throws<GpuDeviceLostException>(() => target.ReadPixels(new byte[values.Length * 16 * Height * 4]));
        backend.CheckResult = null;
        Assert.Equal(0, backend.Images.SlotsInUse);
        Assert.Equal(first, DrawAndRead());

        byte[] DrawAndRead()
        {
            using var frame = backend.BeginFrame();
            var list = new RenderList();
            backend.Images.BeginFrame();
            var slot = backend.Images.Request(
                new ResidencyCache.TileKey(mipmap, ResidencyCache.Format.Float, 0, 0, 0, mipmap.Stamp(0, 0), 0),
                mipmap
            );
            var record = Paint(0, 0, 0, 1, 0, false, false, (uint)backend.Images.Table(ColorMaps.Viridis), bilinear: false, opacity: 1);
            record.AlongU = new Vector2(16, 0);
            record.AlongV = new Vector2(0, Height);
            record.Range = new Vector4(0, 0, values.Length, 1);
            record.SampleScale = 1;
            record.Slice = (uint)(slot % ResidencyCache.PageSlots);
            list.DrawImage(record, ImagePlan.PageOf(false, slot));
            target.Clear(Background);
            backend.Draw(target, list);
            var pixels = new byte[values.Length * 16 * Height * 4];
            target.ReadPixels(pixels);
            return pixels;
        }
    }

    private (byte[] Pixels, int Width) Draw(
        double[] values,
        uint kind,
        double constant,
        double low,
        double high,
        double middle,
        bool diverging,
        bool reverse,
        ColorMap map,
        bool bilinear,
        float opacity,
        bool zeroTransparent = false
    )
    {
        using var column = Column.Wrap(values.Select(v => (float)v).ToArray());
        using var mipmap = Raster.Of(column, values.Length).MakePyramid();
        mipmap.Update(_pool, wait: true);
        using var backend = D3D11Backend.Create(useWarp: true);
        var width = values.Length * 16;
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(width, Height));
        backend.Images.BeginFrame();
        var slot = backend.Images.Request(new ResidencyCache.TileKey(mipmap, ResidencyCache.Format.Float, 0, 0, 0, mipmap.Stamp(0, 0), 0), mipmap);
        var record = Paint(kind, constant, low, high, middle, diverging, reverse, (uint)backend.Images.Table(map), bilinear, opacity);
        record.Flags |= zeroTransparent ? ZeroTransparent : 0;
        record.AlongU = new Vector2(16, 0);
        record.AlongV = new Vector2(0, Height);
        record.Range = new Vector4(0, 0, values.Length, 1);
        record.SampleScale = 1;
        record.Slice = (uint)(slot % ResidencyCache.PageSlots);
        var list = new RenderList();
        list.DrawImage(record, ImagePlan.PageOf(false, slot));
        target.Clear(Background);
        backend.Draw(target, list);
        var pixels = new byte[width * Height * 4];
        target.ReadPixels(pixels);
        return (pixels, width);
    }

    private static ImageInstance Paint(
        uint kind,
        double constant,
        double low,
        double high,
        double middle,
        bool diverging,
        bool reverse,
        uint table,
        bool bilinear,
        float opacity
    ) =>
        new()
        {
            Flags = (bilinear ? 1u : 0) | (kind << 2) | (diverging ? 16u : 0) | (reverse ? 32u : 0),
            Constant = (float)constant,
            Low = (float)low,
            High = (float)high,
            Middle = (float)middle,
            Table = table,
            Opacity = opacity,
        };

    /// <summary>The scale's transform, computed apart from the shader.</summary>
    private static double Forward(double v, uint kind, double c) =>
        double.IsNaN(v)
            ? double.NaN
            : kind switch
            {
                1 => Math.Log10(v),
                2 => Math.Sign(v) * Math.Log10(1 + (Math.Abs(v) / c)),
                3 => Math.Sign(v) * Math.Pow(Math.Abs(v), c),
                _ => v,
            };

    private static double Position(double s, double low, double high, double middle, bool diverging, bool reverse)
    {
        var t = diverging
            ? (s <= middle ? 0.5 * (s - low) / (middle - low) : 0.5 + (0.5 * (s - middle) / (high - middle)))
            : (s - low) / (high - low);
        return reverse ? 1 - t : t;
    }

    /// <summary>
    /// Returns the first raw count from <paramref name="from"/> on, below the top, whose position under the top lies at least
    /// 0.05 of an index from the edges of the table's indices: float32 rounding on the GPU moves t · 256 by far less.
    /// </summary>
    private static ulong AwayFromEdges(DensityNorm norm, ulong from, double area, double top)
    {
        for (var raw = Math.Max(1UL, from); raw < top * area; raw++)
        {
            var at = DensityMath.Position(norm, raw / area, top) * 256;
            var part = at - Math.Floor(at);
            if (part is >= 0.05 and <= 0.95)
            {
                return raw;
            }
        }

        Assert.Fail($"No count from {from} below the top {top * area} lies away from an index's edge.");
        return 0;
    }

    private static int Index(double t) =>
        t < 0 ? 0
        : t >= 1 ? 255
        : Math.Min(255, (int)Math.Floor(t * 256));

    /// <summary>Returns a color drawn at an opacity over the background, as the target holds it read back.</summary>
    private static Color Over(Color color, double opacity)
    {
        var a = color.A / 255.0 * opacity;
        byte Channel(byte c, byte b) => (byte)Math.Round((c / 255.0 * a * 255) + (b * (1 - a)));
        return new Color(Channel(color.R, Background.R), Channel(color.G, Background.G), Channel(color.B, Background.B), 255);
    }

    private static void AssertPixel(byte[] pixels, int width, int x, int y, Color expected, int tolerance) =>
        AssertPixel(pixels, width, x, y, [expected], tolerance);

    private static void AssertPixel(byte[] pixels, int width, int x, int y, Color[] any, int tolerance = 0)
    {
        var i = ((y * width) + x) * 4;
        var actual = new Color(pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]);
        Assert.True(
            any.Any(e => Math.Abs(e.R - actual.R) <= tolerance && Math.Abs(e.G - actual.G) <= tolerance && Math.Abs(e.B - actual.B) <= tolerance),
            $"Pixel ({x}, {y}) is {actual}; expected one of {string.Join(", ", any)}."
        );
    }
}
