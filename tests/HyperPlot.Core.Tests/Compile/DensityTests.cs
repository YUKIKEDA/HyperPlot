using System.Diagnostics;
using System.Numerics;
using HyperPlot.Interaction;
using HyperPlot.Rendering;
using HyperPlot.Testing;

namespace HyperPlot.Tests;

/// <summary>The density of point layers on the index path and its switch from raw points (design §5.5.2 "行 R3b1 の密度と切り替え", §10 R3b1 row).</summary>
[Collection("Large data")]
public sealed class DensityTests
{
    private const int Over = LayerPlan.MaxElements + 1;
    private static readonly PanelId Panel = new(0, 0);
    private static readonly string[] Three = ["a", "b", "c"];

    /// <summary>
    /// The tile level is the deepest whose bins are at least a physical pixel on both axes, from 0 to 24; the counting level
    /// the shallowest whose cells are at most a pixel (design §5.5.2 "水準の選び方", "画面内の点の数").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - None.
    ///
    /// Steps:
    /// - Call TileLevel with ranges of 256, 511, 512, and 2^40 pixels, a range below 256, and two axes of 1,024 and 4,096;
    ///   PixelLevel with 1, 2, 3, and 1,000 pixels.
    ///
    /// Expected:
    /// - Tile levels 0, 0, 1, 24, 0, and 2 (the narrower axis decides). Pixel levels 0, 1, 2, and 10.
    /// </remarks>
    [Fact]
    public void Levels_FollowThePixels()
    {
        Assert.Equal(0, DensityMath.TileLevel(256, 256));
        Assert.Equal(0, DensityMath.TileLevel(511, 511));
        Assert.Equal(1, DensityMath.TileLevel(512, 512));
        Assert.Equal(24, DensityMath.TileLevel(Math.Pow(2, 40), Math.Pow(2, 40)));
        Assert.Equal(0, DensityMath.TileLevel(100, 100));
        Assert.Equal(2, DensityMath.TileLevel(4096, -1024));
        Assert.Equal([0, 1, 2, 10], new[] { 1.0, 2, 3, 1000 }.Select(p => DensityMath.PixelLevel(p, p)));
    }

    /// <summary>
    /// The image command's paint of a tile whose bins cover an area on the screen gives the position of c / area under the
    /// top, for the three norms (design §5.5.2 "広さで正規化する"): the shader's formula, taken on the CPU, equals the CPU's
    /// position.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The shader's transforms as Image.hlsl writes them: none, symlog log10(1 + |v| / C), and pow |v|^C; t = (s − low) / (high − low).
    ///
    /// Steps:
    /// - For each norm, bin areas of 1, 2.5, 4, and 64 square physical pixels, and counts 0, 1, 7, and 1,000 against a top
    ///   of 50: compute t through Paint and through Position of c / area.
    ///
    /// Expected:
    /// - Equal to within 1e-6, held to [0, 1].
    /// </remarks>
    [Fact]
    public void Paint_EqualsThePositionOfTheNormalizedCount()
    {
        foreach (var norm in Enum.GetValues<DensityNorm>())
        {
            foreach (var area in new[] { 1, 2.5, 4, 64 })
            {
                foreach (var c in new[] { 0.0, 1, 7, 1000 })
                {
                    var (kind, constant, high) = DensityMath.Paint(norm, 50, area);
                    var s = kind switch
                    {
                        2 => Math.Log10(1 + (c / constant)),
                        3 => Math.Pow(c, constant),
                        _ => c,
                    };
                    var shader = Math.Clamp(s / high, 0, 1);
                    Assert.Equal(DensityMath.Position(norm, DensityMath.Normalized(c, area), 50), shader, 6);
                }
            }
        }
    }

    /// <summary>
    /// Auto draws a density when the upper count of the view passes the threshold, raw points when it falls under 0.8
    /// times it, and the last drawing between; Points and Density do not count (design §5.5.2 "入れ替わりを抑える").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 131,073 seeded points over [0, 100]² in an engine that waits for its indexes; a threshold of 10,000.
    ///
    /// Steps:
    /// - Zoom in from the whole view to a side of 2 in 200 steps, then out again, drawing each view. Draw a whole view with
    ///   Points and a side of 5 with Density.
    ///
    /// Expected:
    /// - At each view, Auto draws a density exactly when the upper count passes 10,000 after raw points, or stays at or over
    ///   8,000 after a density. The walk passes both kinds of view between 8,000 and 10,000: one drawn as a density (zooming
    ///   in) and one drawn as raw points (zooming out). The whole view is a density; a side of 2 is raw points.
    /// - Points: raw points at the whole view. Density: a density at a side of 5.
    /// </remarks>
    [Fact]
    public void Auto_SwitchesWithABand()
    {
        const long Threshold = 10_000;
        var random = new Random(3);
        using var xs = Column.Wrap(Enumerable.Range(0, Over).Select(_ => random.NextDouble() * 100).ToArray());
        using var ys = Column.Wrap(Enumerable.Range(0, Over).Select(_ => random.NextDouble() * 100).ToArray());
        var table = DataTable.Create().Add("x", xs).Add("y", ys).Build();
        PlotSpec Spec(PointDraw draw) =>
            Plot.Create(table)
                .Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Draw(draw).Density(threshold: Threshold))
                .ScaleX(Scale.Linear().WithDomain(0, 100))
                .ScaleY(Scale.Linear().WithDomain(0, 100))
                .Build();

        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, 640, 360) { WaitsForIndexes = true };
        engine.SetPlot(Spec(PointDraw.Auto));
        engine.RenderFrame();
        IndexedPointPlan Plan() => (IndexedPointPlan)engine.Plan!.Layers[0];
        bool Density(double side)
        {
            engine.Views.SetView(Panel, new PanelView2D(ViewRange.FromValues(0, side), ViewRange.FromValues(0, side)));
            backend.Images.Sent();
            engine.RenderFrame();
            return Plan().DrewDensity;
        }

        var sides = Enumerable.Range(0, 201).Select(i => 100 * Math.Pow(0.02, i / 200.0)).ToArray();
        var (last, heldDensity, heldPoints) = (false, false, false);
        foreach (var side in sides.Concat(sides.Reverse()))
        {
            var drawn = Density(side);
            var count = Plan().Switch.LastCount;
            Assert.Equal(last ? count >= Threshold * DensitySwitch.Back : count > Threshold, drawn);
            var between = count >= Threshold * DensitySwitch.Back && count <= Threshold;
            (heldDensity, heldPoints) = (heldDensity || (between && drawn), heldPoints || (between && !drawn));
            last = drawn;
        }

        Assert.True(heldDensity && heldPoints);
        Assert.True(Density(100));
        Assert.False(Density(2));

        engine.SetPlot(Spec(PointDraw.Points));
        engine.RenderFrame();
        Assert.False(Density(100));
        engine.SetPlot(Spec(PointDraw.Density));
        engine.RenderFrame();
        Assert.True(Density(5));
    }

    /// <summary>
    /// A view beside the data counts no point, so that Auto draws raw points there (design §5.5.2 "画面内の点の数", §3.4
    /// "範囲で切る", row R3c): the count no longer takes the end cells of the ranges.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 131,073 seeded points over [0, 100]² in an engine that waits for its indexes; Auto with a threshold of 10,000.
    ///
    /// Steps:
    /// - Draw the whole view, then the views [200, 300]² (beyond both ranges), [200, 300] × [0, 100] (beyond x alone),
    ///   and [0, 100] × [−300, −200] (below y alone).
    ///
    /// Expected:
    /// - The whole view draws a density.
    /// - Each view beside the data counts 0 points and draws raw points.
    /// </remarks>
    [Fact]
    public void Auto_ViewBesideTheData_CountsNothingAndDrawsRawPoints()
    {
        var random = new Random(4);
        using var xs = Column.Wrap(Enumerable.Range(0, Over).Select(_ => random.NextDouble() * 100).ToArray());
        using var ys = Column.Wrap(Enumerable.Range(0, Over).Select(_ => random.NextDouble() * 100).ToArray());
        var table = DataTable.Create().Add("x", xs).Add("y", ys).Build();
        var spec = Plot.Create(table)
            .Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Density(threshold: 10_000))
            .ScaleX(Scale.Linear().WithDomain(0, 100))
            .ScaleY(Scale.Linear().WithDomain(0, 100))
            .Build();
        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, 640, 360) { WaitsForIndexes = true };
        engine.SetPlot(spec);
        engine.RenderFrame();
        IndexedPointPlan Plan() => (IndexedPointPlan)engine.Plan!.Layers[0];
        bool Density(double x0, double x1, double y0, double y1)
        {
            engine.Views.SetView(Panel, new PanelView2D(ViewRange.FromValues(x0, x1), ViewRange.FromValues(y0, y1)));
            backend.Images.Sent();
            engine.RenderFrame();
            return Plan().DrewDensity;
        }

        Assert.True(Density(0, 100, 0, 100));
        Assert.False(Density(200, 300, 200, 300));
        Assert.Equal(0, Plan().Switch.LastCount);
        Assert.True(Density(0, 100, 0, 100));
        Assert.False(Density(200, 300, 0, 100));
        Assert.Equal(0, Plan().Switch.LastCount);
        Assert.True(Density(0, 100, 0, 100));
        Assert.False(Density(0, 100, -300, -200));
        Assert.Equal(0, Plan().Switch.LastCount);
    }

    /// <summary>
    /// A density records image instances, from the drawn tiles of the view, with a top that is the largest normalized bin
    /// and no resident run (design §5.5.2 "描き方").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 400,000 seeded points over [0, 100]² (tiles exist above the density split), Density, an engine that waits.
    ///
    /// Steps:
    /// - Draw the whole view until it is not provisional.
    ///
    /// Expected:
    /// - Image records and no resident run; the switch's top is above 0; every record's flags carry the zero-is-transparent
    ///   bit and the symlog transform (Log, the default).
    /// </remarks>
    [Fact]
    public void Density_RecordsImages()
    {
        const int Count = 400_000;
        var random = new Random(5);
        using var xs = Column.Wrap(Enumerable.Range(0, Count).Select(_ => random.NextDouble() * 100).ToArray());
        using var ys = Column.Wrap(Enumerable.Range(0, Count).Select(_ => random.NextDouble() * 100).ToArray());
        var table = DataTable.Create().Add("x", xs).Add("y", ys).Build();
        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, 640, 360) { WaitsForIndexes = true };
        engine.SetPlot(
            Plot.Create(table)
                .Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Draw(PointDraw.Density))
                .ScaleX(Scale.Linear().WithDomain(0, 100))
                .ScaleY(Scale.Linear().WithDomain(0, 100))
                .Build()
        );
        for (var i = 0; i < 50 && (i == 0 || engine.IsProvisional); i++)
        {
            backend.Images.Sent();
            engine.RenderFrame();
        }

        var list = backend.LastList!;
        Assert.Equal(0, list.ResidentRuns.Length);
        Assert.NotEqual(0, list.Images.Length);
        var plan = (IndexedPointPlan)engine.Plan!.Layers[0];
        Assert.True(plan.Switch.Top > 0);
        Assert.All(
            list.Images.ToArray(),
            image => Assert.Equal(ImageInstance.ZeroTransparent | (2u << 2), image.Flags & (ImageInstance.ZeroTransparent | (3u << 2)))
        );
    }

    /// <summary>
    /// Auto switches at the ends of its band exactly, by the sum of its parts' upper counts (design §5.5.2 "画面内の点の数",
    /// "入れ替わりを抑える"): a count equal to the threshold is not above it, a count equal to 0.8 times it is not under it,
    /// and every part of a split layer draws the layer's one drawing.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 150,000 seeded points over [0, 100]² split by a category of three names that take turns row by row: 50,000 points a
    ///   part, each on its own index. An engine that waits for its indexes.
    ///
    /// Steps:
    /// - With a threshold no count reaches, draw the whole view and 400 views (squares from 100 down to 2, around (50, 50)
    ///   and from (0, 0)), and keep each view's summed upper count.
    /// - Threshold 150,000: draw the whole view. Threshold 149,999: draw the whole view.
    /// - Threshold T = 1.25 × C, for the first view whose count C is a multiple of 4 under 120,000: draw the whole view,
    ///   the view of C, the view U of the largest count under C, the view of C, and the whole view.
    ///
    /// Expected:
    /// - The whole view counts 150,000, the sum of the parts' points; the three parts share one switch and always draw
    ///   alike.
    /// - At threshold 150,000: raw points; at 149,999: a density, though no part alone holds more than 50,000 points.
    /// - At T: a density; a density at C = 0.8 × T; raw points at U; raw points again at C, which is not above T; a
    ///   density at the whole view.
    /// </remarks>
    [Fact]
    public void Switch_AtTheThresholdAndAtFourFifths_FollowsTheSumOfTheParts()
    {
        const int Count = 150_000;
        const long Total = Count;
        var random = new Random(53);
        using var xs = Column.Wrap(Enumerable.Range(0, Count).Select(_ => random.NextDouble() * 100).ToArray());
        using var ys = Column.Wrap(Enumerable.Range(0, Count).Select(_ => random.NextDouble() * 100).ToArray());
        var table = DataTable
            .Create()
            .Add("x", xs)
            .Add("y", ys)
            .Add("c", Column.Categorical(Enumerable.Range(0, Count).Select(i => (string?)Three[i % 3]).ToArray()))
            .Build();
        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, 640, 360) { WaitsForIndexes = true };

        void Use(long threshold)
        {
            engine.SetPlot(
                Plot.Create(table)
                    .Point(table.Col<double>("x"), table.Col<double>("y"), p => p.ColorBy(table.Cat("c")).Density(threshold: threshold))
                    .ScaleX(Scale.Linear().WithDomain(0, 100))
                    .ScaleY(Scale.Linear().WithDomain(0, 100))
                    .Build()
            );
            engine.RenderFrame();
        }

        // Draws a view; returns the summed upper count and the drawing every part took.
        (long Count, bool Density) Draw(PanelView2D view)
        {
            engine.Views.SetView(Panel, view);
            backend.Images.Sent();
            engine.RenderFrame();
            var parts = ((SplitPlan)engine.Plan!.Layers[0]).Parts.Cast<IndexedPointPlan>().ToArray();
            Assert.Equal(3, parts.Length);
            Assert.All(parts, p => Assert.Same(parts[0].Switch, p.Switch));
            var drawn = Assert.Single(parts.Select(p => p.DrewDensity).Distinct());
            return (parts[0].Switch.LastCount, drawn);
        }

        static PanelView2D Square(double low, double side) => new(ViewRange.FromValues(low, low + side), ViewRange.FromValues(low, low + side));

        var whole = Square(0, 100);
        Use(long.MaxValue);
        Assert.Equal((Total, false), Draw(whole));
        var views = Enumerable
            .Range(1, 200)
            .Select(i => 100 * Math.Pow(0.02, i / 200.0))
            .SelectMany(side => new[] { Square(50 - (side / 2), side), Square(0, side) })
            .ToArray();
        var counts = views.Select(view => Draw(view).Count).ToArray();

        Use(Total);
        Assert.Equal((Total, false), Draw(whole));
        Use(Total - 1);
        Assert.Equal((Total, true), Draw(whole));

        var atBand = Array.FindIndex(counts, n => n % 4 == 0 && n < Total * 4 / 5 && counts.Any(m => m < n));
        Assert.True(atBand >= 0, "No view counts a multiple of 4 under 120,000 with a view of fewer points.");
        var under = Enumerable.Range(0, counts.Length).Where(i => counts[i] < counts[atBand]).MaxBy(i => counts[i]);
        var threshold = counts[atBand] / 4 * 5;
        Use(threshold);
        Assert.Equal((Total, true), Draw(whole));
        Assert.Equal((counts[atBand], true), Draw(views[atBand]));
        Assert.Equal((counts[under], false), Draw(views[under]));
        Assert.Equal((counts[atBand], false), Draw(views[atBand]));
        Assert.Equal((Total, true), Draw(whole));
    }

    /// <summary>
    /// A layer on the CPU path, 131,072 rows or fewer, draws raw points with Density, and one row more draws a density
    /// (design §5.5.2 "道"): the density lives on the index path only.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Seeded points over [0, 100]² in columns of 131,072 and of 131,073 rows; a plan that waits for its indexes.
    ///
    /// Steps:
    /// - Render each length with Draw set to Density.
    ///
    /// Expected:
    /// - 131,072 rows: a CPU-path plan, 131,072 markers, no image and no resident run.
    /// - 131,073 rows: an index-path plan that drew a density, and no marker or resident run.
    /// </remarks>
    [Fact]
    public void CpuPathLayer_WithDensity_DrawsRawPoints()
    {
        var random = new Random(59);
        var values = Enumerable.Range(0, Over).Select(_ => random.NextDouble() * 100).ToArray();
        var others = Enumerable.Range(0, Over).Select(_ => random.NextDouble() * 100).ToArray();
        using var shortX = Column.Wrap(values[..LayerPlan.MaxElements]);
        using var shortY = Column.Wrap(others[..LayerPlan.MaxElements]);
        using var longX = Column.Wrap(values);
        using var longY = Column.Wrap(others);
        var few = DataTable.Create().Add("x", shortX).Add("y", shortY).Build();
        var many = DataTable.Create().Add("x", longX).Add("y", longY).Build();
        using var pool = new PlotWorkerPool(2);
        using var plan = TestPlans.Create(pool, GeomTesting.Size, GeomTesting.Size);

        var mapped = plan.Render(
            GeomTesting.Spec(
                few,
                new PointLayerSpec
                {
                    X = few.Col<double>("x"),
                    Y = few.Col<double>("y"),
                    Draw = PointDraw.Density,
                }
            )
        );
        _ = Assert.IsType<PointPlan>(plan.Layers[0]);
        Assert.Equal((LayerPlan.MaxElements, 0, 0), (mapped.Markers.Length, mapped.Images.Length, mapped.ResidentRuns.Length));

        var dense = plan.Render(
            GeomTesting.Spec(
                many,
                new PointLayerSpec
                {
                    X = many.Col<double>("x"),
                    Y = many.Col<double>("y"),
                    Draw = PointDraw.Density,
                }
            )
        );
        Assert.True(Assert.IsType<IndexedPointPlan>(plan.Layers[0]).DrewDensity);
        Assert.Equal((0, 0), (dense.Markers.Length, dense.ResidentRuns.Length));
    }

    /// <summary>
    /// Density faces share a frame's upload of 4 MB with the other faces, and a frame whose face could not be sent is
    /// provisional; the next frame sends it and is not (design §5.5.2 "描き方", §5.1 "密度タイル").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A density layer of 400,000 seeded points over [0, 100]² (a root tile) in an engine that does not wait, whose index
    ///   has ended; the null backend, which sends only when told.
    /// - The frame's upload filled with faces of another snapshot of 2,000 points, until one is refused: 15 faces.
    ///
    /// Steps:
    /// - Render a frame; say the uploads were sent; render a frame.
    ///
    /// Expected:
    /// - The first frame draws a density with no image, asked for a face it did not get, is provisional, and stages nothing
    ///   past the 15 faces.
    /// - The second frame records one image, stages one face of the float format, and is not provisional.
    /// </remarks>
    [Fact]
    public void DensityFaces_ShareTheFrameUpload_AndLeaveTheFrameProvisionalUntilSent()
    {
        const int Count = 400_000;
        var random = new Random(61);
        using var xs = Column.Wrap(Enumerable.Range(0, Count).Select(_ => random.NextDouble() * 100).ToArray());
        using var ys = Column.Wrap(Enumerable.Range(0, Count).Select(_ => random.NextDouble() * 100).ToArray());
        var table = DataTable.Create().Add("x", xs).Add("y", ys).Build();
        var (otherX, otherY) = SpatialData.Uniform(2_000, seed: 62);
        using var other = SpatialBuilt.Of(otherX, otherY);
        var bins = new byte[DensityTiles.BinCount];
        var backend = new NullBackend();
        var cache = backend.Images;
        using var engine = new PlotEngine(backend, 640, 360);
        engine.SetPlot(
            Plot.Create(table)
                .Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Draw(PointDraw.Density))
                .ScaleX(Scale.Linear().WithDomain(0, 100))
                .ScaleY(Scale.Linear().WithDomain(0, 100))
                .Build()
        );
        engine.RenderFrame();
        var plan = (IndexedPointPlan)engine.Plan!.Layers[0];
        Eventually.Completes(plan.SpatialIndex!.WhenComplete());

        cache.Sent();
        var staged = 0;
        while (cache.RequestDensity(other.Snapshot, 2, (ulong)staged, new DensityTile(bins, 1, 0)) >= 0)
        {
            staged++;
        }

        Assert.Equal(ResidencyCache.FrameUpload / ResidencyCache.FaceBytes, staged);
        engine.RenderFrame();
        Assert.True(plan.DrewDensity);
        Assert.True(plan.MissedPages);
        Assert.True(engine.IsProvisional);
        Assert.Equal(0, backend.LastList!.Images.Length);
        Assert.Equal(staged, cache.Uploads.Length);

        cache.Sent();
        engine.RenderFrame();
        Assert.False(plan.MissedPages);
        Assert.False(engine.IsProvisional);
        Assert.Equal(1, backend.LastList!.Images.Length);
        Assert.Equal(ResidencyCache.Format.Float, Assert.Single(cache.Uploads.ToArray()).Format);
    }

    /// <summary>
    /// A Points layer whose pages do not fit the budget draws the pages that fit and stays provisional, and does not turn to
    /// a density (design §5.5.2 "`Points` と予算"); density faces take their share of the budget.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 960 density faces of another snapshot sent over 64 frames (15 pages of the float pool, 255,605,760 bytes), which
    ///   leaves 12,829,696 bytes of the 256 MB budget: 16 pages of 65,536 points.
    /// - A Points layer of 23 pages (1,507,328 seeded points over [0, 100]²) with a threshold of 1,000, in an engine that
    ///   does not wait, whose index has ended.
    ///
    /// Steps:
    /// - Render 46 frames at the whole view, saying before each that the last frame's uploads were sent.
    ///
    /// Expected:
    /// - 16 pages in use, and the bytes in use within the budget; the 960 faces are still in their slots.
    /// - The last frame is provisional, asked for pages it did not get, drew no density and no image, and draws 16 runs of
    ///   whole pages.
    /// </remarks>
    [Fact]
    public void PointsLayer_OverTheBudget_DrawsThePagesThatFitAndStaysProvisional()
    {
        const int Pages = 23;
        const int Count = Pages * PointPages.PagePoints;
        const int Faces = 15 * ResidencyCache.PageSlots;
        var random = new Random(67);
        using var xs = Column.Wrap(Enumerable.Range(0, Count).Select(_ => random.NextDouble() * 100).ToArray());
        using var ys = Column.Wrap(Enumerable.Range(0, Count).Select(_ => random.NextDouble() * 100).ToArray());
        var table = DataTable.Create().Add("x", xs).Add("y", ys).Build();
        var (otherX, otherY) = SpatialData.Uniform(2_000, seed: 68);
        using var other = SpatialBuilt.Of(otherX, otherY);
        var bins = new byte[DensityTiles.BinCount];
        var backend = new NullBackend();
        var cache = backend.Images;
        using var engine = new PlotEngine(backend, 640, 360);
        engine.SetPlot(
            Plot.Create(table)
                .Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Draw(PointDraw.Points).Density(threshold: 1_000))
                .ScaleX(Scale.Linear().WithDomain(0, 100))
                .ScaleY(Scale.Linear().WithDomain(0, 100))
                .Build()
        );
        engine.RenderFrame();
        var plan = (IndexedPointPlan)engine.Plan!.Layers[0];
        Eventually.Completes(plan.SpatialIndex!.WhenComplete());

        for (var face = 0; face < Faces; face++)
        {
            if (face % 15 == 0)
            {
                cache.Sent();
                cache.BeginFrame();
            }

            Assert.True(cache.RequestDensity(other.Snapshot, 2, (ulong)face, new DensityTile(bins, 1, 0)) >= 0);
        }

        Assert.Equal(Faces, cache.SlotsInUse);
        var left = ResidencyCache.DefaultBudget - ((long)Faces * ResidencyCache.FaceBytes);
        var fit = (int)(left / ((long)PointPages.PagePoints * PointPages.PointBytes));
        Assert.Equal(16, fit);
        for (var frame = 0; frame < 2 * Pages; frame++)
        {
            cache.Sent();
            engine.RenderFrame();
        }

        Assert.Equal(fit, cache.Points.PagesInUse);
        Assert.True(cache.BytesInUse <= ResidencyCache.DefaultBudget);
        Assert.Equal(Faces, cache.SlotsInUse);
        Assert.True(engine.IsProvisional);
        Assert.True(plan.MissedPages);
        Assert.False(plan.DrewDensity);
        var list = backend.LastList!;
        Assert.Equal(0, list.Images.Length);
        Assert.Equal(fit, list.ResidentRuns.Length);
        Assert.All(list.ResidentRuns.ToArray(), run => Assert.Equal(PointPages.PagePoints, run.Count));
    }

    /// <summary>
    /// While a part's index has only its coarse snapshot, the part draws the coarse density as one tile over the whole range,
    /// provisional, whatever its Draw; once the snapshot is full it draws as Draw says (design §5.5.2 "索引ができるまで").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A layer of 400,000 seeded points over [0, 100]² with the Draw of the case; its x column's storage holds every
    ///   worker read of its chunk once the index writes its copy, so the build stands after its coarse snapshot. An engine
    ///   that does not wait, on a pool of 1 worker; the view [37.5, 62.5]², which chooses a tile level of 1 or more.
    ///
    /// Steps:
    /// - Render frames until one records an image. Let the reads go, and render frames until one is not provisional.
    ///
    /// Expected:
    /// - The coarse frame: the index's snapshot is Coarse; the frame is provisional; one piece, the root cell read from
    ///   the coarse grid (0, 0, 0, 0, 0); one image that reads the grid from its corner, 2^-L of a texel a texel for the
    ///   chosen level L; no resident run.
    /// - A hit test at (50, 50) in the coarse frame: one hit of row −1 at a distance of 0, provisional, whose count is the
    ///   coarse grid's cell of level 8 that holds (50, 50) (row R3c).
    /// - The full frame, Points and Auto: raw points, no image, and no face in the cache (the coarse face let go of).
    ///   Density: more than one piece, each a cell of the chosen level that reads the root's tile; an image each; one face
    ///   in the cache, the root tile's.
    /// </remarks>
    [Theory]
    [InlineData(PointDraw.Auto)]
    [InlineData(PointDraw.Points)]
    [InlineData(PointDraw.Density)]
    public void CoarseSnapshot_DrawsTheCoarseDensityWhateverDraw_UntilFull(PointDraw draw)
    {
        const int Count = 400_000;
        var random = new Random(71);
        var xValues = Enumerable.Range(0, Count).Select(_ => random.NextDouble() * 100).ToArray();
        using var xs = TestStorage<double>.MakeColumn(xValues, out var storage);
        using var ys = Column.Wrap(Enumerable.Range(0, Count).Select(_ => random.NextDouble() * 100).ToArray());
        var table = DataTable.Create().Add("x", xs).Add("y", ys).Build();
        var backend = new NullBackend();
        using var pool = new PlotWorkerPool(1);
        using var engine = new PlotEngine(backend, 640, 360, pool: pool);
        var hold = new WriteHold(storage);
        storage.OnWorkerRead = hold.OnRead;
        var clock = Stopwatch.StartNew();
        IndexedPointPlan plan;

        // The build waits at its first read until the test knows its index: from then on, a read of the copy's writing holds.
        storage.Hold(0);
        try
        {
            engine.SetPlot(
                Plot.Create(table)
                    .Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Draw(draw))
                    .ScaleX(Scale.Linear().WithDomain(0, 100))
                    .ScaleY(Scale.Linear().WithDomain(0, 100))
                    .Build()
            );
            engine.RenderFrame();
            plan = (IndexedPointPlan)engine.Plan!.Layers[0];
            while (plan.SpatialIndex is null)
            {
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), "The index was not started in 60 s.");
                engine.RenderFrame();
            }

            var index = plan.SpatialIndex;
            hold.Watch(index);
            storage.Release(0);
            engine.Views.SetView(Panel, new PanelView2D(ViewRange.FromValues(37.5, 62.5), ViewRange.FromValues(37.5, 62.5)));
            var drawn = false;
            while (!drawn)
            {
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), "The coarse density was not drawn in 60 s.");
                backend.Images.Sent();
                engine.RenderFrame();
                drawn = backend.LastList!.Images.Length > 0;
            }

            var snapshot = index.Acquire();
            var stage = snapshot.Stage;
            snapshot.Release();
            Assert.Equal(SpatialStage.Coarse, stage);
            Assert.True(engine.IsProvisional);
            Assert.True(plan.DrewDensity);
            Assert.True(plan.TileLevel >= 1);
            Assert.Equal([new DensityPiece(0, 0, 0, 0, 0)], plan.Pieces.ToArray());
            var list = backend.LastList!;
            Assert.Equal(0, list.ResidentRuns.Length);
            var image = Assert.Single(list.Images.ToArray());
            Assert.Equal(Vector2.Zero, image.SampleOrigin);
            Assert.Equal(Math.Pow(2, -plan.TileLevel), image.SampleScale, 9);

            // A hit test answers with the coarse bin under the cursor, provisionally (row R3c, Issue #471 Decision 5).
            var coarse = index.Acquire();
            try
            {
                var (gx, gy) = coarse.GridAt(50 - coarse.XRange.Origin, 50 - coarse.YRange.Origin);
                var expected = coarse.CoarseDensity[(int)(((gy >> 24) << 8) + (gx >> 24))];
                var hits = new SeriesHit[2];
                var at = engine.ViewToPixel(Panel, ViewRange.FromValues(50.0, 50.0), ViewRange.FromValues(50.0, 50.0));
                var hit = Assert.Single(hits[..engine.HitTest(at, 8, HitMode.XY, hits)]);
                Assert.Equal((-1L, 0f, true, expected), (hit.Row, hit.Distance, hit.Provisional, hit.Count));
            }
            finally
            {
                coarse.Release();
            }
        }
        finally
        {
            hold.Stop();
            storage.Release(0);
        }

        var provisional = true;
        while (provisional)
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), "The frames stayed provisional for 60 s.");

            // A worker that read before the stop may hold the chunk again after the release above.
            storage.Release(0);
            backend.Images.Sent();
            engine.RenderFrame();
            provisional = engine.IsProvisional;
        }

        var full = backend.LastList!;
        if (draw == PointDraw.Density)
        {
            Assert.True(plan.DrewDensity);
            var pieces = plan.Pieces.ToArray();
            Assert.True(pieces.Length > 1);
            Assert.All(pieces, p => Assert.Equal((plan.TileLevel, 0UL, 0), (p.CellLevel, p.TileCode, p.TileLevel)));
            Assert.Equal(pieces.Length, full.Images.Length);
            Assert.Equal(1, backend.Images.SlotsInUse);
        }
        else
        {
            Assert.False(plan.DrewDensity);
            Assert.Equal(0, full.Images.Length);
            Assert.NotEqual(0, full.ResidentRuns.Length);
            Assert.Equal(0, backend.Images.SlotsInUse);
        }
    }

    /// <summary>
    /// A layer drawn as a density keeps its legend row's sample, and no color bar comes with it (design §5.5.2 "凡例").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A labeled point layer of 131,073 seeded points over [0, 100]² in the palette's fourth color; a plan of 400 × 300
    ///   that waits for its indexes.
    ///
    /// Steps:
    /// - Render it with Points, then with Density.
    ///
    /// Expected:
    /// - Points draws raw points and one marker, the legend's sample. Density draws a density, and the same one marker, the
    ///   same number of texts, and the same panel: no color bar's labels or room.
    /// </remarks>
    [Fact]
    public void Legend_OfADensityLayer_KeepsTheLayersSample()
    {
        var random = new Random(79);
        using var xs = Column.Wrap(Enumerable.Range(0, Over).Select(_ => random.NextDouble() * 100).ToArray());
        using var ys = Column.Wrap(Enumerable.Range(0, Over).Select(_ => random.NextDouble() * 100).ToArray());
        var table = DataTable.Create().Add("x", xs).Add("y", ys).Build();
        using var pool = new PlotWorkerPool(2);
        using var plan = TestPlans.Create(pool, 400, 300);
        var point = new PointLayerSpec
        {
            X = table.Col<double>("x"),
            Y = table.Col<double>("y"),
            Label = "points",
            Color = Palettes.Tab10[3],
            Draw = PointDraw.Points,
        };

        var raw = plan.Render(GeomTesting.Spec(table, point));
        Assert.False(((IndexedPointPlan)plan.Layers[0]).DrewDensity);
        var sample = Assert.Single(raw.Markers.ToArray());
        var texts = raw.Texts.Length;
        var guide = plan.Guides.PanelAt(0);
        var panel = (guide.Left, guide.Top, guide.Width, guide.Height);

        var density = plan.Render(GeomTesting.Spec(table, point with { Draw = PointDraw.Density }));
        Assert.True(((IndexedPointPlan)plan.Layers[0]).DrewDensity);
        Assert.Equal(sample, Assert.Single(density.Markers.ToArray()));
        Assert.Equal(texts, density.Texts.Length);
        guide = plan.Guides.PanelAt(0);
        Assert.Equal(panel, (guide.Left, guide.Top, guide.Width, guide.Height));
    }

    /// <summary>
    /// Frames of a density, zoomed and whole, and of a split layer whose drawing switches both ways allocate nothing once
    /// warm (invariant 3, design §5.5.2 "確保").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 400,000 seeded rows over [0, 100]²: a Density layer over (x, y), whose root has a tile, and an Auto layer over
    ///   (u, v) split by three categories with a threshold of 130,000. An engine that waits for its indexes.
    /// - A script of square views around (50, 50) of sides 100, 70, 20, 10, 20, 70, and 100, run 6 times.
    ///
    /// Steps:
    /// - Run the script twice more as a warm-up; collect; run it twice again, counting the thread's allocations.
    ///
    /// Expected:
    /// - In the warm-up, the Density layer draws a density at every view, and the Auto layer draws both a density and raw
    ///   points; no frame is provisional at the end.
    /// - 0 bytes.
    /// </remarks>
    [Fact]
    public void Frames_DensityAndTheSwitch_AllocateNothing()
    {
        const int Count = 400_000;
        var random = new Random(73);
        double[] Values() => [.. Enumerable.Range(0, Count).Select(_ => random.NextDouble() * 100)];
        using var xs = Column.Wrap(Values());
        using var ys = Column.Wrap(Values());
        using var us = Column.Wrap(Values());
        using var vs = Column.Wrap(Values());
        var table = DataTable
            .Create()
            .Add("x", xs)
            .Add("y", ys)
            .Add("u", us)
            .Add("v", vs)
            .Add("c", Column.Categorical(Enumerable.Range(0, Count).Select(i => (string?)Three[i % 3]).ToArray()))
            .Build();
        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, 640, 360) { WaitsForIndexes = true };
        engine.SetPlot(
            Plot.Create(table)
                .Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Draw(PointDraw.Density))
                .Point(table.Col<double>("u"), table.Col<double>("v"), p => p.ColorBy(table.Cat("c")).Density(threshold: 130_000))
                .ScaleX(Scale.Linear().WithDomain(0, 100))
                .ScaleY(Scale.Linear().WithDomain(0, 100))
                .Build()
        );
        engine.RenderFrame();
        double[] sides = [100, 70, 20, 10, 20, 70, 100];
        var views = sides
            .Select(s => new PanelView2D(ViewRange.FromValues(50 - (s / 2), 50 + (s / 2)), ViewRange.FromValues(50 - (s / 2), 50 + (s / 2))))
            .ToArray();
        var density = (IndexedPointPlan)engine.Plan!.Layers[0];
        var switching = ((SplitPlan)engine.Plan!.Layers[1]).Parts.Cast<IndexedPointPlan>().First();
        var drawn = new bool[views.Length];
        for (var round = 0; round < 6; round++)
        {
            for (var i = 0; i < views.Length; i++)
            {
                Draw(views[i]);
                Assert.True(density.DrewDensity);
                drawn[i] = switching.DrewDensity;
            }
        }

        Assert.Contains(true, drawn);
        Assert.Contains(false, drawn);
        Assert.False(engine.IsProvisional);

        PerformanceAssert.DoesNotAllocate(() =>
        {
            for (var round = 0; round < 2; round++)
            {
                foreach (var view in views)
                {
                    Draw(view);
                }
            }
        });

        void Draw(PanelView2D view)
        {
            backend.Images.Sent();
            engine.Views.SetView(Panel, view);
            engine.RenderFrame();
        }
    }

    /// <summary>
    /// Holds the workers' reads of chunk 0 once the index writes its copy, so that the build stands after its coarse
    /// snapshot (design §3.4: the coarse snapshot is published before the write pass).
    /// </summary>
    private sealed class WriteHold(TestStorage<double> storage)
    {
        private SpatialIndex? _index;
        private int _stopped;

        public void Watch(SpatialIndex index) => Volatile.Write(ref _index, index);

        public void Stop() => Volatile.Write(ref _stopped, 1);

        public void OnRead()
        {
            if (Volatile.Read(ref _stopped) == 0 && Volatile.Read(ref _index) is { State: SpatialIndexState.Writing })
            {
                storage.Hold(0);
            }
        }
    }

    /// <summary>
    /// A frame whose density layers need more color tables than the table texture's 64 rows throws, naming the layer that
    /// found no row (design §5.5.2 "描き方", PR #497's review): a full texture reuses only rows not used in the frame.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 131,073 seeded points over [0, 100]²; 65 point layers of the same columns, each drawn as a density in a color of its
    ///   own; an engine that waits for its indexes.
    ///
    /// Steps:
    /// - Draw frames until the layers draw their densities.
    ///
    /// Expected:
    /// - InvalidOperationException naming layer 64.
    /// </remarks>
    [Fact]
    public void Density_MoreLayerColorsThanTableRowsInAFrame_Throws()
    {
        var random = new Random(9);
        using var xs = Column.Wrap(Enumerable.Range(0, Over).Select(_ => random.NextDouble() * 100).ToArray());
        using var ys = Column.Wrap(Enumerable.Range(0, Over).Select(_ => random.NextDouble() * 100).ToArray());
        var table = DataTable.Create().Add("x", xs).Add("y", ys).Build();
        var builder = Plot.Create(table);
        for (var i = 0; i <= ResidencyCache.MaxColorTables; i++)
        {
            var color = new Color((byte)i, (byte)(255 - i), 128, 255);
            builder = builder.Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Draw(PointDraw.Density).Color(color));
        }

        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, 640, 360) { WaitsForIndexes = true };
        engine.SetPlot(builder.ScaleX(Scale.Linear().WithDomain(0, 100)).ScaleY(Scale.Linear().WithDomain(0, 100)).Build());
        var thrown = Assert.Throws<InvalidOperationException>(() =>
        {
            for (var frame = 0; frame < 50; frame++)
            {
                backend.Images.Sent();
                engine.RenderFrame();
            }
        });
        Assert.Contains("Layer 64", thrown.Message, StringComparison.Ordinal);
    }
}
