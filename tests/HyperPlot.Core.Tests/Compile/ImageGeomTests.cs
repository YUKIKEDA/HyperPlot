using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>The image geom of row G9 (design §4.1, §4.8, §5.2, §5.5.3, §10's G9 row).</summary>
public sealed class ImageGeomTests : IDisposable
{
    private readonly PlotWorkerPool _pool = new(2);
    private readonly List<IDisposable> _columns = [];

    public void Dispose()
    {
        foreach (var column in _columns)
        {
            column.Dispose();
        }

        _pool.Dispose();
    }

    /// <summary>The builder checks the raster and the configure's values, and the compile checks a spec made directly the same way (design §4.1).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A raster 4 × 3 and a plan of the bare theme.
    ///
    /// Steps:
    /// - Build with a null raster; extents with NaN, ∞, and equal edges; opacities −0.1, 1.1, NaN; an origin and a reading
    ///   not named. Render specs made directly with the same values.
    ///
    /// Expected:
    /// - ArgumentNullException for the raster, ArgumentOutOfRangeException for the rest, both ways. The defaults: no extent,
    ///   the upper origin, nearest, opacity 1.
    /// </remarks>
    [Fact]
    public void Checks_TheRasterAndTheLooks()
    {
        var raster = Numbers(4, 3);
        var table = DataTable.Create().Build();
        _ = Assert.Throws<ArgumentNullException>(() => Plot.Create(table).Image(null!));
        Action<ImageBuilder>[] bad =
        [
            i => i.Extent(double.NaN, 1, 0, 1),
            i => i.Extent(0, double.PositiveInfinity, 0, 1),
            i => i.Extent(0, 0, 0, 1),
            i => i.Extent(0, 1, 2, 2),
            i => i.Opacity(-0.1),
            i => i.Opacity(1.1),
            i => i.Opacity(double.NaN),
            i => i.Origin((RasterOrigin)2),
            i => i.Interpolation((RasterInterpolation)2),
        ];
        foreach (var configure in bad)
        {
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => Plot.Create(table).Image(raster, configure));
        }

        var layer = (ImageLayerSpec)Plot.Create(table).Image(raster).Build().Layers[0];
        Assert.Equal(
            ((RasterExtent?)null, RasterOrigin.Upper, RasterInterpolation.Nearest, 1.0),
            (layer.Extent, layer.Origin, layer.Interpolation, layer.Opacity)
        );

        using var plan = Plan(100, 100);
        foreach (
            var spec in new[]
            {
                layer with
                {
                    Extent = new RasterExtent(0, 0, 0, 1),
                },
                layer with
                {
                    Opacity = 2,
                },
                layer with
                {
                    Origin = (RasterOrigin)5,
                },
                layer with
                {
                    Interpolation = (RasterInterpolation)5,
                },
            }
        )
        {
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => plan.Render(Spec(spec)));
        }

        _ = Assert.Throws<ArgumentNullException>(() => plan.Render(Spec(layer with { Raster = null! })));
    }

    /// <summary>
    /// The compile rejects what an image cannot be drawn on or with (design §4.1, §4.2, §5.5.3): polar, a log x or y, a
    /// color channel, an ordinal scale for numbers, and a color scale of a user's transform.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A raster of numbers and an RGBA one.
    ///
    /// Steps:
    /// - Render each case, a time color scale over a raster of floats included. Fill the color tables; render a 65th map.
    ///
    /// Expected:
    /// - ArgumentException each, naming the layer; the RGBA image takes an ordinal color scale of other layers. The 65th map
    ///   throws InvalidOperationException naming the layer.
    /// </remarks>
    [Fact]
    public void Compile_RejectsWhatAnImageCannotBeDrawnOn()
    {
        var layer = new ImageLayerSpec { Raster = Numbers(4, 3) };
        using var plan = Plan(100, 100);
        var categories = Column.Categorical(["a"]);
        var table = DataTable.Create().Add("c", categories).Build();
        foreach (
            var spec in new[]
            {
                Spec(layer) with
                {
                    Coord = Coord.Polar(),
                },
                Spec(layer) with
                {
                    ScaleX = Scale.Log(),
                },
                Spec(layer) with
                {
                    ScaleY = Scale.Symlog(),
                },
                Spec(layer with { ColorBy = table.Cat("c").ToColumnRef() }) with
                {
                    Data = table,
                },
                Spec(layer) with
                {
                    ScaleColor = Scale.Ordinal(),
                },
                Spec(layer) with
                {
                    ScaleColor = Scale.Sequential(ColorMaps.Viridis, Scale.Transform(new Identity())),
                },
                Spec(layer) with
                {
                    ScaleColor = Scale.Sequential(ColorMaps.Viridis, Scale.Time()),
                },
            }
        )
        {
            Assert.Contains("ayer 0", Assert.Throws<ArgumentException>(() => plan.Render(spec)).Message, StringComparison.Ordinal);
        }

        _ = plan.Render(Spec(new ImageLayerSpec { Raster = Rgba(2, 2) }) with { ScaleColor = Scale.Ordinal() });

        // A 65th color map past the table's 64 rows throws rather than paint with another map's colors (PR #201's review).
        for (var i = 0; plan.Images.Table(new ColorMap(new Color((byte)i, 0, 0, 255), Colors.White)) >= 0; i++) { }

        var past = Spec(layer) with { ScaleColor = Scale.Sequential(new ColorMap(Colors.Black, Colors.Red)) };
        Assert.Contains("ayer 0", Assert.Throws<InvalidOperationException>(() => plan.Render(past)).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// x and y learn the extent, the default one and a turned-over one; the color domain learns the raster's values with
    /// the other numeric color columns (design §4.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A raster 4 × 3 of values 1 to 12; a rect colored by a column of −5 and 20.
    ///
    /// Steps:
    /// - Render on learned scales with the default extent; with (8, 2, 5, −1); with the rect; with a raster whose value is
    ///   30, then rewritten to 40 and its row invalidated.
    ///
    /// Expected:
    /// - [−0.5, 3.5] × [−0.5, 2.5]; [2, 8] × [−1, 5]; the color domain [1, 12], then [−5, 20], [1, 30], and [1, 40].
    /// </remarks>
    [Fact]
    public void Learning_TheExtentAndTheValues()
    {
        var raster = Numbers(4, 3, i => i + 1);
        using var plan = Plan(100, 100);
        var learned = Spec(new ImageLayerSpec { Raster = raster }) with { ScaleX = Scale.Linear(), ScaleY = Scale.Linear() };
        _ = plan.Render(learned);
        Assert.Equal((-0.5, 3.5), GeomTesting.Ends(plan.Guides.X.FinalDomain));
        Assert.Equal((-0.5, 2.5), GeomTesting.Ends(plan.Guides.Y.FinalDomain));
        Assert.Equal((1.0, 12.0), GeomTesting.Ends(plan.Color!.Domain));

        _ = plan.Render(learned with { Layers = [new ImageLayerSpec { Raster = raster, Extent = new RasterExtent(8, 2, 5, -1) }] });
        Assert.Equal((2.0, 8.0), GeomTesting.Ends(plan.Guides.X.FinalDomain));
        Assert.Equal((-1.0, 5.0), GeomTesting.Ends(plan.Guides.Y.FinalDomain));

        using var v = Column.Wrap(new double[] { -5, 20 });
        using var e = Column.Wrap(new double[] { 0, 1 });
        var table = DataTable.Create().Add("v", v).Add("e", e).Build();
        var rect = new RectLayerSpec
        {
            XMin = table.Col<double>("e"),
            XMax = table.Col<double>("e"),
            YMin = table.Col<double>("e"),
            YMax = table.Col<double>("e"),
            ColorBy = table.Col<double>("v"),
        };
        _ = plan.Render(learned with { Data = table, Layers = [new ImageLayerSpec { Raster = raster }, rect] });
        Assert.Equal((-5.0, 20.0), GeomTesting.Ends(plan.Color!.Domain));

        // A new version of the raster's column learns again (design §4.8): its largest value goes to 30.
        _ = plan.Render(learned);
        var values = new float[12];
        ((Raster<float>)raster).Values.GetChunk(0).CopyTo(values);
        values[5] = 30;
        var column = Column.Wrap(values);
        _columns.Add(column);
        var rewritten = Raster.Of(column, 4);
        _ = plan.Render(learned with { Layers = [new ImageLayerSpec { Raster = rewritten }] });
        Assert.Equal((1.0, 30.0), GeomTesting.Ends(plan.Color!.Domain));
        values[5] = 40;
        rewritten.Invalidate(1, 1);
        _ = plan.Render(learned with { Layers = [new ImageLayerSpec { Raster = rewritten }] });
        Assert.Equal((1.0, 40.0), GeomTesting.Ends(plan.Color!.Domain));
    }

    /// <summary>
    /// The level is ⌊log₂ max(sx, sy)⌋ held between 0 and the top (design §5.5.3): one texel of the finer direction is 1 to
    /// 2 a pixel.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A raster 4,096 × 4,096 (top level 4) on a panel of 100 × 100.
    ///
    /// Steps:
    /// - Show it whole (s = 40.96); 100 columns and all rows (sx = 1, sy = 40.96); 10 columns and 10 rows (s = 0.1); then on a
    ///   panel of 8 × 8 (s = 512); then 199 columns and rows (s = 1.99) and 200 (s = 2).
    ///
    /// Expected:
    /// - Levels 5 → held at 4, 5 → 4, 0, 4, 0, and 1.
    /// </remarks>
    [Fact]
    public void Level_FromTheFinerDirection()
    {
        var raster = Rgba(4096, 4096);
        using var plan = Plan(100, 100);
        int Level(double columns, double rows, PlotPlan p)
        {
            _ = p.Render(Spec(new ImageLayerSpec { Raster = raster, Extent = new RasterExtent(0, 4096, 0, 4096) }, columns, rows));
            return ((ImagePlan)p.Layers[0]).Level;
        }

        Assert.Equal(4, Level(4096, 4096, plan));
        Assert.Equal(4, Level(100, 4096, plan));
        Assert.Equal(0, Level(10, 10, plan));
        Assert.Equal(0, Level(199, 199, plan));
        Assert.Equal(1, Level(200, 200, plan));
        using var small = Plan(8, 8);
        Assert.Equal(4, Level(4096, 4096, small));
    }

    /// <summary>At most 160 tiles are visible on 1,920 × 1,080 (design §5.5.3), with the texels just under 2 a pixel.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A raster 4,000 × 2,200 on a panel of 1,920 × 1,080, showing 3,830 × 2,150 of it from (5, 5).
    ///
    /// Steps:
    /// - Render, the cache unbounded.
    ///
    /// Expected:
    /// - Level 0, and the visible tiles above 100 and at most 160.
    /// </remarks>
    [Fact]
    public void VisibleTiles_AtMost160()
    {
        var raster = Rgba(4000, 2200);
        using var plan = Plan(1920, 1080, unbounded: true);
        var spec = Spec(new ImageLayerSpec { Raster = raster, Extent = new RasterExtent(0, 4000, 0, 2200) }) with
        {
            ScaleX = Scale.Linear().WithDomain(5, 3835),
            ScaleY = Scale.Linear().WithDomain(5, 2155),
        };
        _ = plan.Render(spec);
        var image = (ImagePlan)plan.Layers[0];
        Assert.Equal(0, image.Level);
        Assert.InRange(image.VisibleTiles, 101, 160);
    }

    /// <summary>
    /// A tile's corners are the extent's mapped through the axes, the origin and the turned-over extent set by the texels'
    /// vectors, and flip swaps them (design §5.5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A raster 3 × 2 on the extent (10, 40, 0, 20), the plot [0, 50] × [0, 20] on 100 × 100 (2 pixels a unit across, 5 down).
    ///
    /// Steps:
    /// - Render with the upper origin; the lower; turned over by (40, 10, 0, 20); flipped.
    ///
    /// Expected:
    /// - Upper: origin (20, 0), a texel (20, 0) across and (0, 50) down; lower: origin (20, 100), down (0, −50); turned over:
    ///   origin (80, 0), across (−20, 0); flipped: the vectors swapped. The range is 3 × 2 texels and the sample the tile itself.
    /// </remarks>
    [Fact]
    public void Record_TheCornersOfTheTiles()
    {
        var raster = Rgba(3, 2);
        using var plan = Plan(100, 100, unbounded: true);
        ImageInstance Record(ImageLayerSpec layer, CoordSpec? coord = null)
        {
            var spec = Spec(layer) with { ScaleX = Scale.Linear().WithDomain(0, 50), ScaleY = Scale.Linear().WithDomain(0, 20) };
            return Assert.Single(plan.Render(coord is null ? spec : spec with { Coord = coord }).Images.ToArray());
        }

        var layer = new ImageLayerSpec { Raster = raster, Extent = new RasterExtent(10, 40, 0, 20) };
        var upper = Record(layer);
        Assert.Equal(
            (new System.Numerics.Vector2(20, 0), new System.Numerics.Vector2(20, 0), new System.Numerics.Vector2(0, 50)),
            (upper.Origin, upper.AlongU, upper.AlongV)
        );
        Assert.Equal(new System.Numerics.Vector4(0, 0, 3, 2), upper.Range);
        Assert.Equal((System.Numerics.Vector2.Zero, 1f), (upper.SampleOrigin, upper.SampleScale));

        var lower = Record(layer with { Origin = RasterOrigin.Lower });
        Assert.Equal((new System.Numerics.Vector2(20, 100), new System.Numerics.Vector2(0, -50)), (lower.Origin, lower.AlongV));

        var turned = Record(layer with { Extent = new RasterExtent(40, 10, 0, 20) });
        Assert.Equal((new System.Numerics.Vector2(80, 0), new System.Numerics.Vector2(-20, 0)), (turned.Origin, turned.AlongU));

        var flipped = Record(layer, Coord.Cartesian(flip: true));
        Assert.Equal(0, flipped.AlongU.X);
        Assert.Equal(0, flipped.AlongV.Y);
    }

    /// <summary>
    /// A frame sends at most 4 MB; the tiles not there yet are drawn from the top level's part, and the image is whole after
    /// a few frames (design §5.2, §5.5.3). A headless render sends everything at once.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A raster 3,000 × 2,000 shown whole at level 0 on 1,920 × 1,080 (over 15 tiles).
    ///
    /// Steps:
    /// - Render frames until no tile is provisional; render again with an unbounded cache.
    ///
    /// Expected:
    /// - Each frame at most 4 MB; the first provisional, with the top tile sent and every visible tile drawn; whole within
    ///   ⌈tiles / 15⌉ + 1 frames. Unbounded: whole at the first frame.
    /// </remarks>
    [Fact]
    public void Budget_SendsAFrameAtATime()
    {
        var raster = Rgba(3000, 2000);
        var spec = Spec(new ImageLayerSpec { Raster = raster }) with
        {
            ScaleX = Scale.Linear().WithDomain(0, 1900),
            ScaleY = Scale.Linear().WithDomain(0, 1000),
        };
        using var plan = new PlotPlan(_pool, 1920, 1080, new TextEngine(new FakeTextBackend()), 1, new ResidencyCache());
        var cache = plan.Images;
        var list = plan.Render(spec);
        var image = (ImagePlan)plan.Layers[0];
        Assert.True(image.Provisional);
        Assert.Equal(image.VisibleTiles, list.Images.Length);
        var frames = 1;
        while (image.Provisional)
        {
            Assert.InRange(cache.BytesThisFrame, 1, ResidencyCache.FrameUpload);
            cache.Sent();
            _ = plan.Render(spec with { Title = $"{frames}" });
            frames++;
            Assert.True(frames <= (image.VisibleTiles / 15) + 2);
        }

        var unbounded = new ResidencyCache { Unbounded = true };
        using var headless = new PlotPlan(_pool, 1920, 1080, new TextEngine(new FakeTextBackend()), 1, unbounded);
        _ = headless.Render(spec);
        Assert.False(((ImagePlan)headless.Layers[0]).Provisional);
    }

    /// <summary>
    /// The cache keeps a tile at its slot, sends it again for a new stamp or origin, lets go of the slot used longest ago
    /// but never one of this frame, and forgets all on a reset (design §5.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A mipmap of a raster 300 × 300 (4 tiles at level 0), an unbounded cache.
    ///
    /// Steps:
    /// - Request a tile twice; with another stamp; with another origin; fill the slots past the budget over two frames;
    ///   reset.
    ///
    /// Expected:
    /// - The same slot twice, new slots for the other stamp and origin; past the budget a slot of the earlier frame is taken,
    ///   and within one frame −1 once all are this frame's; nothing in use after the reset.
    /// </remarks>
    [Fact]
    public void Cache_KeysSlotsAndLetsGo()
    {
        using var column = Column.Wrap(new float[300 * 300]);
        using var mipmap = Raster.Of(column, 300).MakePyramid();
        mipmap.Update(_pool);
        var cache = new ResidencyCache { Unbounded = true };
        cache.BeginFrame();
        ResidencyCache.TileKey Key(int column, long stamp = 1, double origin = 0) =>
            new(mipmap, ResidencyCache.Format.Float, 0, column, 0, stamp, origin);
        var slot = cache.Request(Key(0), mipmap);
        Assert.Equal(slot, cache.Request(Key(0), mipmap));
        Assert.NotEqual(slot, cache.Request(Key(0, stamp: 2), mipmap));
        Assert.NotEqual(slot, cache.Request(Key(0, origin: 5), mipmap));

        // Past the budget: every slot of this frame is kept, then the next frame takes the oldest.
        var taken = 3;
        while (cache.Request(Key(taken, stamp: 9), mipmap) >= 0)
        {
            taken++;
        }

        Assert.Equal(ResidencyCache.SlotCapacity / ResidencyCache.PageSlots * ResidencyCache.PageSlots, cache.SlotsInUse);
        cache.BeginFrame();
        Assert.True(cache.Request(Key(-1, stamp: 9), mipmap) >= 0);
        cache.Reset();
        Assert.Equal(0, cache.SlotsInUse);
    }

    /// <summary>
    /// Another raster makes the layer again, another extent learns again, and the looks lay out and record only (design §4.8).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A drawn image layer.
    ///
    /// Steps:
    /// - Render another raster; another extent; then nodes of another origin, reading, opacity, and label.
    ///
    /// Expected:
    /// - A new layer plan for the raster; an extraction for the extent; 1 layout, 0 extractions, and 1 recording for each look.
    /// </remarks>
    [Fact]
    public void Diff_TheStages()
    {
        var layer = new ImageLayerSpec { Raster = Numbers(4, 3) };
        using var plan = Plan(100, 100);
        _ = plan.Render(Spec(layer));
        var before = plan.Layers[0];
        _ = plan.Render(Spec(layer with { Raster = Numbers(4, 3) }));
        Assert.NotSame(before, plan.Layers[0]);

        var extractions = plan.Extractions;
        _ = plan.Render(Spec(layer with { Extent = new RasterExtent(0, 1, 0, 1) }) with { ScaleX = Scale.Linear(), ScaleY = Scale.Linear() });
        _ = plan.Render(Spec(layer with { Extent = new RasterExtent(0, 2, 0, 1) }) with { ScaleX = Scale.Linear(), ScaleY = Scale.Linear() });
        Assert.True(plan.Extractions > extractions);

        _ = plan.Render(Spec(layer));
        foreach (
            var changed in new[]
            {
                layer with
                {
                    Origin = RasterOrigin.Lower,
                },
                layer with
                {
                    Interpolation = RasterInterpolation.Bilinear,
                },
                layer with
                {
                    Opacity = 0.5,
                },
                layer with
                {
                    Label = "L",
                },
            }
        )
        {
            var (layouts, extracted, recordings) = (plan.Layouts, plan.Extractions, plan.Recordings);
            _ = plan.Render(Spec(changed));
            Assert.Equal((layouts + 1, extracted, recordings + 1), (plan.Layouts, plan.Extractions, plan.Recordings));
        }
    }

    /// <summary>A raster of numbers shows the color bar; an RGBA image shows none; neither shows a label's row (design §4.6).</summary>
    /// <remarks>
    /// Preconditions:
    /// - The light theme; layers labeled "L".
    ///
    /// Steps:
    /// - Render a raster of numbers, then an RGBA image.
    ///
    /// Expected:
    /// - A color bar, then none; no text "L" either time.
    /// </remarks>
    [Fact]
    public void Legend_TheColorBarOnly()
    {
        using var plan = new PlotPlan(_pool, 640, 360, new TextEngine(new FakeTextBackend()), 1);
        var numbers = plan.Render(Spec(new ImageLayerSpec { Raster = Numbers(4, 3, i => i), Label = "L" }) with { Theme = Theme.Light });
        Assert.True(plan.Guides.ColorBarLength > 0);
        Assert.DoesNotContain(numbers.Texts.ToArray(), t => t.Text == "L");
        var colors = plan.Render(Spec(new ImageLayerSpec { Raster = Rgba(2, 2), Label = "L" }) with { Theme = Theme.Light });
        Assert.Equal(0, plan.Guides.ColorBarLength);
        Assert.DoesNotContain(colors.Texts.ToArray(), t => t.Text == "L");
    }

    /// <summary>Once its tiles are there, an image drawn again and panned allocates nothing (design §5.5.3).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A raster 1,000 × 1,000, of colors and of numbers (with its color bar), on 640 × 360, the cache unbounded, the specs of
    ///   4 views made beforehand.
    ///
    /// Steps:
    /// - Render the 4 views twice to warm; render them again.
    ///
    /// Expected:
    /// - 0 bytes on this thread.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Warm_AllocatesNothing(bool rgba)
    {
        Raster raster = rgba ? Rgba(1000, 1000) : Numbers(1000, 1000);
        using var plan = Plan(640, 360, unbounded: true);
        var specs = Enumerable
            .Range(0, 4)
            .Select(i =>
                Spec(new ImageLayerSpec { Raster = raster }) with
                {
                    ScaleX = Scale.Linear().WithDomain(i * 50, 600 + (i * 50)),
                    ScaleY = Scale.Linear().WithDomain(0, 300),
                }
            )
            .ToArray();
        foreach (var spec in specs.Concat(specs))
        {
            _ = plan.Render(spec);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var spec in specs)
        {
            _ = plan.Render(spec);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static PlotSpec Spec(ImageLayerSpec layer, double columns = 4, double rows = 3) =>
        new()
        {
            Theme = TestPlans.Bare,
            Data = DataTable.Create().Build(),
            Layers = [layer],
            ScaleX = Scale.Linear().WithDomain(0, columns),
            ScaleY = Scale.Linear().WithDomain(0, rows),
        };

    private PlotPlan Plan(int width, int height, bool unbounded = false) =>
        new(_pool, width, height, new TextEngine(new FakeTextBackend()), 1, unbounded ? new ResidencyCache { Unbounded = true } : null);

    private Raster<float> Numbers(int width, int height, Func<int, float>? value = null)
    {
        var column = Column.Wrap(Enumerable.Range(0, width * height).Select(i => value?.Invoke(i) ?? (float)(i % 97)).ToArray());
        _columns.Add(column);
        return Raster.Of(column, width);
    }

    private Raster<byte> Rgba(int width, int height)
    {
        var column = Column.Wrap(new byte[width * height * 4]);
        _columns.Add(column);
        return Raster.Rgba(column, width);
    }

    /// <summary>A user's transform that changes nothing.</summary>
    private sealed class Identity : IScale
    {
        public void Forward(ReadOnlySpan<double> values, Span<double> destination) => values.CopyTo(destination);

        public void Inverse(ReadOnlySpan<double> values, Span<double> destination) => values.CopyTo(destination);
    }
}
