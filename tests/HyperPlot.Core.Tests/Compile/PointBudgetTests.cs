using System.Diagnostics;
using HyperPlot.Interaction;
using HyperPlot.Rendering;
using HyperPlot.Testing;

namespace HyperPlot.Tests;

/// <summary>
/// The budget's condition of the point layers on the index path (design §5.5.2 "予算", "替える順", "入れ替わりを抑える", row
/// R3b2): the sum of the bytes every layer that draws raw points needs and of the tiles the last frame used, against the
/// residency cache's budget, which a test sets small through the cache's internal constructor.
/// </summary>
/// <remarks>
/// Each layer holds whole pages of 65,536 uniform points over [0, 100]², drawn whole on a 640 × 360 target without guides
/// at markers of 1 DIP, so that its predicted time (about 1.7 ms) and its count stay far under their thresholds and only
/// the budget decides. A layer of k pages needs k × 786,432 bytes.
/// </remarks>
[Collection(nameof(PointBudgetTests))]
[CollectionDefinition(nameof(PointBudgetTests), DisableParallelization = true)]
public sealed class PointBudgetTests : IDisposable
{
    private const long Page = (long)PointPages.PagePoints * PointPages.PointBytes;

    private readonly PlotWorkerPool _pool = new(3);
    private readonly List<IDisposable> _columns = [];
    private DataTable? _table;

    public void Dispose()
    {
        _pool.Dispose();
        foreach (var column in _columns)
        {
            column.Dispose();
        }
    }

    /// <summary>
    /// An Auto layer becomes a density when the sum of every raw layer's bytes passes the budget, though each layer alone is
    /// within it and its count is under the threshold; the one that needs the most bytes goes, and the other stays raw.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A budget of 7 pages. Two Auto layers: A of 5 pages, B of 4 (9 pages together).
    ///
    /// Steps:
    /// - Render once headless.
    ///
    /// Expected:
    /// - A needs 5 pages and B 4, each within the budget. A draws a density and B raw points; the sum left is B's 4 pages.
    /// </remarks>
    [Fact]
    public void Sum_OverTheBudget_MakesTheLargestAutoLayerADensity()
    {
        var table = Table(5, 4);
        using var renderer = Render(7, Spec(table, Auto(0), Auto(1)));
        var (a, b) = (Layer(renderer, 0), Layer(renderer, 1));
        Assert.Equal((5 * Page, 4 * Page), (a.Switch.Needed, b.Switch.Needed));
        Assert.True(a.Switch.LastCount < 10_000_000 && a.Switch.LastTime < PredictedTime.Threshold * DensitySwitch.Back);
        Assert.Equal((true, false), (a.DrewDensity, b.DrewDensity));
        Assert.Equal(4 * Page, renderer.Plan!.Budget.LastSum);
    }

    /// <summary>
    /// The machine's time scale does not reach the budget's condition (design §5.5.2 "ほかの機械"): at 0.5, at 2, and at a subnormal
    /// scale, whose times are nearly 0, the same layer goes as at 1, and the sum left is the same.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A budget of 7 pages. Two Auto layers: A of 5 pages, B of 4. A renderer of the time scale given.
    ///
    /// Steps:
    /// - Render once headless.
    ///
    /// Expected:
    /// - The predicted times are the scale times the reference's, under 0.8 times the threshold. A draws a density and B raw
    ///   points; the sum left is B's 4 pages.
    /// </remarks>
    [Theory]
    [InlineData(0.5)]
    [InlineData(2.0)]
    [InlineData(double.Epsilon)]
    public void Sum_TakesNoTimeScale(double scale)
    {
        var table = Table(5, 4);
        using var renderer = new HeadlessRenderer(new NullBackend { Images = new ResidencyCache(7 * Page) }, 640, 360, _pool)
        {
            GpuTimeScale = scale,
        };
        renderer.Render(Spec(table, Auto(0), Auto(1)));
        var (a, b) = (Layer(renderer, 0), Layer(renderer, 1));
        var reference = Of(b.Switch.LastCount, 1, b.Switch.LastLargestBin);
        Assert.Equal(scale * reference, b.Switch.LastTime, 1e-9);
        Assert.True(b.Switch.LastTime < PredictedTime.Threshold * DensitySwitch.Back);
        Assert.Equal((true, false), (a.DrewDensity, b.DrewDensity));
        Assert.Equal(4 * Page, renderer.Plan!.Budget.LastSum);
    }

    /// <summary>
    /// The layers are made densities one at a time, the most bytes first, and the next one stays raw once the sum is within
    /// the budget (design §5.5.2 "替える順").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A budget of 9 pages. Three Auto layers of 2, 5, and 4 pages (11 together), the largest in the middle.
    ///
    /// Steps:
    /// - Render once headless.
    ///
    /// Expected:
    /// - Only the layer of 5 pages draws a density: without it the sum is 6 pages, within the budget, so the layer of 4 pages
    ///   stays raw.
    /// </remarks>
    [Fact]
    public void Sum_WithinTheBudgetAgain_LeavesTheSmallerLayersRaw()
    {
        var table = Table(2, 5, 4);
        using var renderer = Render(9, Spec(table, Auto(0), Auto(1), Auto(2)));
        Assert.Equal([false, true, false], [Layer(renderer, 0).DrewDensity, Layer(renderer, 1).DrewDensity, Layer(renderer, 2).DrewDensity]);
    }

    /// <summary>Layers that need the same bytes are made densities in the order of their layer's number, the lower first.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A budget of 7 pages. Two Auto layers of 4 pages each.
    ///
    /// Steps:
    /// - Render once headless.
    ///
    /// Expected:
    /// - Layer 0 draws a density and layer 1 raw points.
    /// </remarks>
    [Fact]
    public void Sum_EqualNeeds_MakesTheLowerLayerADensity()
    {
        var table = Table(4, 4);
        using var renderer = Render(7, Spec(table, Auto(0), Auto(1)));
        Assert.Equal((true, false), (Layer(renderer, 0).DrewDensity, Layer(renderer, 1).DrewDensity));
    }

    /// <summary>
    /// The sum counts the raw layers of a figure's other plots, and its Points layers: a figure's plots draw with one cache
    /// in one frame; among layers of the same bytes, the lower plot's number goes first, before the layer's number.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A budget of 7 pages. Plot 0: a Points layer of 1 page and an Auto layer of 3 pages (layer 1). Plot 1: an Auto layer
    ///   of 3 pages (layer 0) and a Points layer of 1 page. A plot alone of plot 0's layers for comparison.
    ///
    /// Steps:
    /// - Render the plot alone headless; render the figure of the two plots side by side headless.
    ///
    /// Expected:
    /// - Alone, the sum (4 pages) is within the budget, and the Auto layer stays raw.
    /// - In the figure the sum is 8 pages, past the budget. Plot 0's Auto layer draws a density (plot 0 before plot 1 at
    ///   equal bytes, though its layer's number is the higher), plot 1's stays raw, the Points layers draw raw points, and
    ///   the sum left is 5 pages.
    /// </remarks>
    [Fact]
    public void Sum_CountsTheFiguresOtherPlots()
    {
        var table = Table(1, 3, 3, 1);
        var first = Spec(table, Points(0), Auto(1));
        var second = Spec(table, Auto(2), Points(3));
        using (var alone = Render(7, first))
        {
            Assert.Equal(4 * Page, alone.Plan!.Budget.LastSum);
            Assert.False(Layer(alone, 1).DrewDensity);
        }

        var backend = new NullBackend { Images = new ResidencyCache(7 * Page) };
        using var renderer = new HeadlessRenderer(backend, 640, 360, _pool);
        renderer.Render(Figure.HConcat(first, second));
        var plans = renderer.Figure!.Plans;
        IndexedPointPlan Of(int plot, int layer) => (IndexedPointPlan)plans[plot].Panels[0].Layers[layer];
        Assert.Equal((0, 1), (plans[0].Ordinal, plans[1].Ordinal));
        Assert.Equal((3 * Page, 3 * Page), (Of(0, 1).Switch.Needed, Of(1, 0).Switch.Needed));
        Assert.Equal((false, true, false, false), (Of(0, 0).DrewDensity, Of(0, 1).DrewDensity, Of(1, 0).DrewDensity, Of(1, 1).DrewDensity));
        Assert.Equal(5 * Page, plans[0].Budget.LastSum);
    }

    /// <summary>
    /// The raster tiles the last frame used take their share of the budget: an Auto layer within the budget alone draws a
    /// density once an image's tiles are counted with it (design §5.5.2 "予算", "合計").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A budget of 22 pages and one face (17,567,760 bytes; a pool of tiles needs room for 64 faces to make its first
    ///   texture array). An Auto layer of 22 pages, and a float raster of 1,024 × 1,024 over the panel drawn before it.
    ///
    /// Steps:
    /// - Render the point layer alone headless. Render the image and the point layer headless on another backend.
    ///
    /// Expected:
    /// - Alone, the layer's 22 pages are within the budget and it draws raw points.
    /// - With the image, the last frame counts the slots the frame before it used. That frame drew the layer's raw points
    ///   and the raster at level 1 (1,024 texels over about 360 pixels down, 2 to 4 a pixel): its 2 × 2 tiles, and the top
    ///   level's one tile (level 2, 256 × 256), which it asked for first and did not draw (design §5.5.3 "送る順と仮の描画":
    ///   the top level's tile is asked for first in every frame). These 5 faces and the layer's
    ///   22 pages pass the budget, so the layer draws a density, and the sum left is the 5 faces.
    /// </remarks>
    [Fact]
    public void Sum_CountsTheRasterTilesOfTheLastFrame()
    {
        var budget = (22 * Page) + ResidencyCache.FaceBytes;
        var table = Table(22);
        using (var alone = Render(budget, Spec(table, Auto(0))))
        {
            Assert.Equal(22 * Page, Layer(alone, 0).Switch.Needed);
            Assert.False(Layer(alone, 0).DrewDensity);
        }

        using var pixels = Column.Wrap(new float[1024 * 1024]);
        var image = (ImageLayerSpec)Plot.Create(table).Image(Raster.Of(pixels, 1024), i => i.Extent(0, 100, 0, 100)).Build().Layers[0];
        var backend = new NullBackend { Images = new ResidencyCache(budget) };
        using var renderer = new HeadlessRenderer(backend, 640, 360, _pool);
        renderer.Render(Spec(table, image, Auto(0)));
        var raster = (ImagePlan)renderer.Plan!.Panels[0].Layers[0];
        var layer = (IndexedPointPlan)renderer.Plan.Panels[0].Layers[1];
        Assert.Equal((1, 4), (raster.Level, raster.VisibleTiles));

        // The frame before the last: the 2 × 2 tiles of level 1 drawn and the top level's one tile asked for.
        const long Used = ((2 * 2) + 1) * (long)ResidencyCache.FaceBytes;
        Assert.Equal(Used, backend.Images.TileBytesLastFrame);
        Assert.True(layer.DrewDensity);
        Assert.Equal(Used, renderer.Plan.Budget.LastSum);
    }

    /// <summary>
    /// A layer the budget made a density goes back to raw points only while the sum with its bytes stays under 0.8 times
    /// the budget (design §5.5.2 "入れ替わりを抑える"); of two in one panel, the shorter time, here also the fewer bytes, is
    /// the panel's candidate (design §5.5.2 "重なる層の和" "戻り").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A budget of 12 pages (0.8 times is 9.6 pages). Points layers P of 7 pages and Q of 3, Auto layers S of 3 and L of 4:
    ///   17 pages, so L and then S become densities (10 pages left).
    ///
    /// Steps:
    /// - Render headless. Render again with P drawn as a density, so that the raw layers need 3 pages (Q's); the tiles' slots
    ///   are none (a budget under 64 faces makes no texture array). Render the same spec 30 more times.
    ///
    /// Expected:
    /// - First: S and L draw densities. Then S goes back (3 + 3 = 6 pages under 9.6) and L stays a density (6 + 4 = 10 pages
    ///   is not under 9.6, though it is within the budget); the sum left is 6 pages. In 30 more renders of the same spec,
    ///   no frame changes either drawing.
    /// </remarks>
    [Fact]
    public void Back_FewestBytesFirst_WhileUnderEightTenths()
    {
        var table = Table(7, 3, 3, 4);
        var backend = new NullBackend { Images = new ResidencyCache(12 * Page) };
        using var renderer = new HeadlessRenderer(backend, 640, 360, _pool);
        renderer.Render(Spec(table, Points(0), Points(1), Auto(2), Auto(3)));
        Assert.Equal((true, true), (Layer(renderer, 2).DrewDensity, Layer(renderer, 3).DrewDensity));

        renderer.Render(Spec(table, Points(0) with { Draw = PointDraw.Density }, Points(1), Auto(2), Auto(3)));
        Assert.Equal(0, backend.Images.TileBytesLastFrame);
        Assert.Equal((false, true), (Layer(renderer, 2).DrewDensity, Layer(renderer, 3).DrewDensity));
        Assert.Equal(6 * Page, renderer.Plan!.Budget.LastSum);

        // The same view for 30 more renders, each a frame or more: the drawings do not swing between frames.
        var changes = 0;
        for (var round = 0; round < 30; round++)
        {
            backend.Images.Sent();
            renderer.Render(Spec(table, Points(0) with { Draw = PointDraw.Density }, Points(1), Auto(2), Auto(3)));
            changes += Layer(renderer, 2).DrewDensity || !Layer(renderer, 3).DrewDensity ? 1 : 0;
        }

        Assert.Equal(0, changes);
    }

    /// <summary>
    /// The panels' candidates to go back go back the fewest bytes first while the sum with their bytes stays under 0.8
    /// times the budget: of a figure's two plots, the plot of the fewer bytes, though its number is the higher.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A budget of 12 pages (0.8 times is 9.6 pages). Plot 0: a Points layer P of 7 pages and an Auto layer L of 4. Plot 1:
    ///   a Points layer Q of 3 pages and an Auto layer S of 3. 17 pages, so L and then S become densities.
    ///
    /// Steps:
    /// - Render the figure headless. Render it again with P drawn as a density, so that the raw layers need 3 pages (Q's).
    ///
    /// Expected:
    /// - First: L and S draw densities. Then each plot's panel has one candidate, each within the room alone (3 + 4 and 3 + 3
    ///   pages under 9.6); S, of the fewer bytes, goes back first, and L stays a density (6 + 4 = 10 pages is not under 9.6);
    ///   the sum left is 6 pages.
    /// </remarks>
    [Fact]
    public void Back_PanelsCandidates_FewestBytesFirst()
    {
        var table = Table(7, 4, 3, 3);
        var backend = new NullBackend { Images = new ResidencyCache(12 * Page) };
        using var renderer = new HeadlessRenderer(backend, 1280, 360, _pool);
        FigureSpec Figure(PointLayerSpec p) => HyperPlot.Figure.HConcat(Spec(table, p, Auto(1)), Spec(table, Points(2), Auto(3)));
        IndexedPointPlan Of(int plot, int layer) => (IndexedPointPlan)renderer.Figure!.Plans[plot].Panels[0].Layers[layer];
        renderer.Render(Figure(Points(0)));
        Assert.Equal((true, true), (Of(0, 1).DrewDensity, Of(1, 1).DrewDensity));

        renderer.Render(Figure(Points(0) with { Draw = PointDraw.Density }));
        Assert.Equal((4 * Page, 3 * Page), (Of(0, 1).Switch.Needed, Of(1, 1).Switch.Needed));
        Assert.Equal((true, false), (Of(0, 1).DrewDensity, Of(1, 1).DrewDensity));
        Assert.Equal(6 * Page, renderer.Figure!.Plans[0].Budget.LastSum);
    }

    /// <summary>
    /// A density goes back only when the predicted time, the count, and the budget all let it (design §5.5.2 "入れ替わりを
    /// 抑える"): a layer the budget made a density stays one while its time is in the band, the budget leaving room.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A budget of 7 pages. A Points layer P of 5 pages and an Auto layer A of 4 pages: A becomes a density by the budget.
    ///
    /// Steps:
    /// - Render headless. Render with P drawn as a density and A's markers sized so that A's predicted time is 12.7 ms (in
    ///   the band of 11.28 to 14.1 ms). Render with A's markers at 1 DIP again.
    ///
    /// Expected:
    /// - A is a density by the budget alone (its time under 11.28 ms). With the time in the band it stays a density, though
    ///   the budget now has room (4 pages under 5.6). Back at 1 DIP it draws raw points.
    /// </remarks>
    [Fact]
    public void Back_NeedsTheTimeAndTheCountToo()
    {
        var table = Table(5, 4);
        var backend = new NullBackend { Images = new ResidencyCache(7 * Page) };
        using var renderer = new HeadlessRenderer(backend, 640, 360, _pool);
        renderer.Render(Spec(table, Points(0), Auto(1)));
        var layer = Layer(renderer, 1);
        Assert.True(layer.DrewDensity);
        Assert.True(layer.Switch.LastTime < PredictedTime.Threshold * DensitySwitch.Back);

        var size = Diameter(12.7, layer.Switch.LastCount, layer.Switch.LastLargestBin);
        var hidden = Points(0) with { Draw = PointDraw.Density };
        renderer.Render(Spec(table, hidden, Auto(1) with { Size = size }));
        Assert.Equal(12.7, layer.Switch.LastTime, 1e-4);
        Assert.True(layer.DrewDensity);

        renderer.Render(Spec(table, hidden, Auto(1)));
        Assert.False(layer.DrewDensity);
    }

    /// <summary>
    /// A layer's needed bytes are the whole contents of the pages that hold a block in view, not the bytes of the blocks in
    /// view (design §5.5.2 "予算").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A Points layer of 6 pages over [0, 100]² (393,216 points: cells of 24,576 points, so that the cell at the low
    ///   corner fills one block of its page and not the other), drawn at the view [1, 10] × [1, 10] on the default budget.
    ///
    /// Steps:
    /// - Render headless, which sends every page the view needs.
    ///
    /// Expected:
    /// - The layer needs the bytes of the pages the list draws from, counted by their slots (each a whole page of 786,432
    ///   bytes), which are the bytes the cache holds after the render, and more than the bytes of the blocks the list draws.
    /// </remarks>
    [Fact]
    public void Needed_IsThePagesWithABlockInView()
    {
        var table = Table(6);
        var backend = new NullBackend();
        using var renderer = new HeadlessRenderer(backend, 640, 360, _pool);
        renderer.Render(Spec(table, Points(0)) with { ScaleX = Scale.Linear().WithDomain(1, 10), ScaleY = Scale.Linear().WithDomain(1, 10) });
        var needed = Layer(renderer, 0).Switch.Needed;
        var drawn = backend.LastList!.ResidentRuns.ToArray();
        Assert.All(drawn, run => Assert.True(run.Slot >= 0));
        Assert.Equal(drawn.Select(run => run.Slot).Distinct().Count() * Page, needed);
        Assert.Equal(backend.Images.Points.BytesInUse, needed);
        Assert.InRange(needed, 1, 5 * Page);
        Assert.True(drawn.Sum(run => (long)run.Count) * PointPages.PointBytes < needed);
    }

    /// <summary>
    /// The frames of an engine over the budget allocate nothing once warm (design §5.5.2 "確保", the Acceptance of Issue
    /// #627): the budget's choice over the layers, the full frames' requests, and the frames that switch.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A budget of 7 pages. A Points layer of 9 pages, past the budget alone, and two Auto layers of 3 and 2 pages, in an
    ///   engine that does not wait, on the null backend. Two views that show every point, [−1, 101]² and [−2, 102]², which
    ///   have the same ticks.
    ///
    /// Steps:
    /// - Draw until every layer's index has ended and is scanned on its full snapshot, and the pages the budget holds are
    ///   there (the frame stays provisional: the Points layer never fits). The
    ///   measured run: 20 frames, the two views in turn, each followed by the wait for the reads. Warm up twice, collect,
    ///   and count one run.
    ///
    /// Expected:
    /// - 0 bytes. Both Auto layers draw densities, and the counted frames were full: they searched for no page.
    /// </remarks>
    [Fact]
    public void FullFrames_AllocateNothing()
    {
        var table = Table(9, 3, 2);
        var backend = new NullBackend { Images = new ResidencyCache(7 * Page) };
        using var engine = new PlotEngine(backend, 640, 360, 1, _pool);
        engine.SetPlot(Spec(table, Points(0), Auto(1), Auto(2)));
        var clock = Stopwatch.StartNew();
        bool Full(int layer) =>
            engine.Plan?.Panels[0].Layers[layer] is IndexedPointPlan { SpatialIndex.IsComplete: true } part && part.ScannedFull(backend.Images.Frame);
        while (backend.Images.Points.PagesInUse < 7 || !backend.Images.FullThisFrame || !Full(0) || !Full(1) || !Full(2))
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "The pages did not come in 30 s.");
            backend.Images.Sent();
            engine.RenderFrame();
            _ = backend.Images.Points.AwaitReads();
        }

        PanelView2D[] views =
        [
            new(ViewRange.FromValues(-1, 101), ViewRange.FromValues(-1, 101)),
            new(ViewRange.FromValues(-2, 102), ViewRange.FromValues(-2, 102)),
        ];
        var (searches, full, run) = (0, true, 0);
        PerformanceAssert.DoesNotAllocate(
            () =>
            {
                (searches, full) = (0, true);
                for (var f = 0; f < 20; f++)
                {
                    engine.Views.SetView(new PanelId(0, 0), views[run++ % 2]);
                    backend.Images.Sent();
                    engine.RenderFrame();
                    _ = backend.Images.Points.AwaitReads();
                    searches += backend.Images.Points.SearchesThisFrame;
                    full &= backend.Images.FullThisFrame;
                }
            },
            warmUps: 2
        );
        Assert.Equal((true, true), (Layer(engine, 1).DrewDensity, Layer(engine, 2).DrewDensity));
        Assert.Equal((0, true), (searches, full));
    }

    /// <summary>
    /// A figure's plot that does not change keeps its list of pages of the cache without recording (design §5.5.2 "予算",
    /// Issue #636): its pages are marked this frame's, so another plot's requests let go of other pages, its list draws the
    /// same points, and its bytes, from its last scan, are in the budget's sum.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A budget of 7 pages, on a null backend whose draws keep each page sent in its slot, as a GPU's buffers would. Plot 0:
    ///   a Points layer of 3 pages. Plot 1: a Points layer A of 4 pages, then a Points layer B of 4 other pages.
    ///
    /// Steps:
    /// - Render the figure of plot 0 and plot 1 with A headless, and take the centers plot 0's list draws, computed from the
    ///   kept pages as the markers' shader computes them. Render the figure again with plot 1 drawing B, plot 0 unchanged.
    ///
    /// Expected:
    /// - The first time, plot 0 draws its 3 pages: 196,608 centers. The second time, plot 0 did not record, its list draws the
    ///   same centers, B draws its 4 pages, and the sum left is 7 pages: plot 0's 3 with B's 4.
    /// - Without the fix, B's pages let plot 0's go first: they and A's were last used in the same frame, and the cache lets
    ///   go of the lowest slot of the oldest frame first (the tie's order of PointPages.Oldest), which plot 0, recorded
    ///   first, holds.
    /// </remarks>
    [Fact]
    public void Figure_PlotThatDoesNotChange_KeepsItsPagesAndEntersTheSum()
    {
        var table = Table(3, 4, 4);
        var (still, first, second) = (Spec(table, Points(0)), Spec(table, Points(1), Hidden(2)), Spec(table, Hidden(1), Points(2)));
        var gpu = new GpuPages();
        var backend = new NullBackend { Images = new ResidencyCache(7 * Page), Sending = gpu.Send };
        using var renderer = new HeadlessRenderer(backend, 640, 360, _pool);
        renderer.Render(Figure.HConcat(still, first));
        var plans = renderer.Figure!.Plans;
        var drawn = gpu.Centers(plans[0].List);
        Assert.Equal(3 * PointPages.PagePoints, drawn.Length);
        Assert.DoesNotContain(drawn, center => float.IsNaN(center.X));

        var recordings = plans[0].Recordings;
        renderer.Render(Figure.HConcat(still, second));
        Assert.Equal(drawn, gpu.Centers(plans[0].List));
        Assert.Equal(4 * PointPages.PagePoints, gpu.Centers(plans[1].List).Count(center => !float.IsNaN(center.X)));
        Assert.Equal(7 * Page, plans[0].Budget.LastSum);
        Assert.Equal(recordings, plans[0].Recordings);
    }

    /// <summary>
    /// A figure's plot that does not change keeps its list of raster tiles of the cache without recording (design §5.5.2
    /// "予算", "1 つの LRU", Issue #636): its tiles and its table row are marked this frame's, so another plot's pages and
    /// tiles let go of other items, and its list draws the same faces.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A budget of 64 faces and 4 pages (a pool of tiles makes its first texture array of 64 faces), on a null backend
    ///   whose draws keep each face and page sent in its slot and then count the uploads as sent, as a GPU backend does.
    ///   Plot 0: a float raster of 1,024 × 1,024 over [0, 100]² with guides. Plot 1: Points layer A of 20 pages and layer
    ///   B of 20 pages drawn as a density; then A drawn as a density and B as points; then the same with a float raster of
    ///   5,000 everywhere over [0, 100]² after them.
    ///
    /// Steps:
    /// - Render the figure headless and take the faces plot 0's image records draw. Render it again with plot 1's layers
    ///   swapped, then with plot 1's raster too, plot 0 unchanged: B's pages need room, and the raster's tiles take free
    ///   slots.
    ///
    /// Expected:
    /// - Plot 0 draws at least one tile. Each time after, it did not record and draws the same faces in the same order, and
    ///   plot 1's raster draws tiles.
    /// </remarks>
    [Fact]
    public void Figure_PlotThatDoesNotChange_KeepsItsRasterTiles()
    {
        var table = Table(20, 20);
        var values = new float[1024 * 1024];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = i % 977;
        }

        using var pixels = Column.Wrap(values);
        var still = Plot.Create(table).Image(Raster.Of(pixels, 1024), i => i.Extent(0, 100, 0, 100)).Build();
        var gpu = new GpuPages();
        var backend = new NullBackend { Images = new ResidencyCache((64L * ResidencyCache.FaceBytes) + (4 * Page)) };
        backend.Sending = cache =>
        {
            gpu.Send(cache);
            cache.Sent();
        };
        using var renderer = new HeadlessRenderer(backend, 640, 360, _pool);
        renderer.Render(Figure.HConcat(still, Spec(table, Points(0), Hidden(1))));
        var plans = renderer.Figure!.Plans;
        var drawn = gpu.Faces(plans[0].List);
        Assert.NotEmpty(drawn);
        Assert.DoesNotContain(drawn, face => face is null);

        var recordings = plans[0].Recordings;
        renderer.Render(Figure.HConcat(still, Spec(table, Hidden(0), Points(1))));
        Assert.Equal(drawn, gpu.Faces(plans[0].List));

        // Another image's tiles take the slots the pages let go of.
        using var others = Column.Wrap(Enumerable.Repeat(5_000f, 1024 * 1024).ToArray());
        var other = Plot.Create(table).Image(Raster.Of(others, 1024), i => i.Extent(0, 100, 0, 100)).Build().Layers[0];
        renderer.Render(Figure.HConcat(still, Spec(table, Hidden(0), Points(1), other)));
        Assert.NotEmpty(gpu.Faces(plans[1].List));
        Assert.Equal(drawn, gpu.Faces(plans[0].List));
        Assert.Equal(recordings, plans[0].Recordings);
    }

    /// <summary>
    /// The bytes of a figure's plot that does not change are in the sum that chooses another plot's Auto layer (design
    /// §5.5.2 "予算", Issue #636): the other plot's layer, within the budget alone, draws a density.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A budget of 7 pages. Plot 0: a Points layer of 3 pages. Plot 1: a Points layer of 1 page, then an Auto layer of 5
    ///   pages.
    ///
    /// Steps:
    /// - Render the figure headless; render it again with plot 1 drawing the Auto layer, plot 0 unchanged.
    ///
    /// Expected:
    /// - The Auto layer needs 5 pages, within the budget alone; with plot 0's 3 the sum is 8 pages, so it draws a density,
    ///   and the sum left is plot 0's 3 pages.
    /// </remarks>
    [Fact]
    public void Figure_PlotThatDoesNotChange_CountsForAnotherPlotsAutoLayer()
    {
        var table = Table(3, 1, 5);
        var still = Spec(table, Points(0));
        var backend = new NullBackend { Images = new ResidencyCache(7 * Page) };
        using var renderer = new HeadlessRenderer(backend, 640, 360, _pool);
        renderer.Render(Figure.HConcat(still, Spec(table, Points(1))));
        renderer.Render(Figure.HConcat(still, Spec(table, Auto(2))));
        var plans = renderer.Figure!.Plans;
        var auto = (IndexedPointPlan)plans[1].Panels[0].Layers[0];
        Assert.Equal(5 * Page, auto.Switch.Needed);
        Assert.True(auto.DrewDensity);
        Assert.Equal(3 * Page, plans[0].Budget.LastSum);
    }

    /// <summary>
    /// A plot alone that does not change records again when a page its list draws was let go of since, in a cache it shares
    /// with another plan (two renderers on one backend; design §5.5.2 "予算", Issue #636), and keeps its list while its
    /// pages are there: each render draws the same points.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - One null backend of a budget of 7 pages, whose draws keep each page sent in its slot. Renderer P draws a Points layer
    ///   of 3 pages; renderer Q draws Points layer A of 4 pages and B of 4 pages drawn as a density, then A as a density and
    ///   B as points.
    ///
    /// Steps:
    /// - Render P; take the centers its list draws. Render Q's first plot, then its second, whose pages need room. Render
    ///   P's plot again, unchanged; then once more. Reset the cache as a lost device does, render Q with both layers as
    ///   points (8 pages, which take all 7 slots), and render P's plot again.
    ///
    /// Expected:
    /// - P's second render records again and draws the same 196,608 centers. Its third does not record, and draws them too.
    ///   After the reset, P records again and draws them, though every slot holds a page again.
    /// </remarks>
    [Fact]
    public void SharedCache_PlotThatDoesNotChange_DrawsItsOwnPagesAgain()
    {
        var table = Table(3, 4, 4);
        var still = Spec(table, Points(0));
        var gpu = new GpuPages();
        var backend = new NullBackend { Images = new ResidencyCache(7 * Page), Sending = gpu.Send };
        using var p = new HeadlessRenderer(backend, 640, 360, _pool);
        using var q = new HeadlessRenderer(backend, 640, 360, _pool);
        p.Render(still);
        var drawn = gpu.Centers(p.Plan!.List);
        Assert.Equal(3 * PointPages.PagePoints, drawn.Length);

        q.Render(Spec(table, Points(1), Hidden(2)));
        q.Render(Spec(table, Hidden(1), Points(2)));
        Assert.Equal(4 * PointPages.PagePoints, gpu.Centers(q.Plan!.List).Length);
        var recordings = p.Plan.Recordings;
        p.Render(still);
        Assert.Equal(drawn, gpu.Centers(p.Plan.List));
        Assert.True(p.Plan.Recordings > recordings);

        recordings = p.Plan.Recordings;
        p.Render(still);
        Assert.Equal(drawn, gpu.Centers(p.Plan.List));
        Assert.Equal(recordings, p.Plan.Recordings);

        // A lost device forgets every page, and Q's 8 pages take all 7 slots again.
        backend.Images.Reset();
        q.Render(Spec(table, Points(1), Points(2)));
        Assert.Equal(7, backend.Images.Points.PagesInUse);
        p.Render(still);
        Assert.Equal(drawn, gpu.Centers(p.Plan.List));
        Assert.True(p.Plan.Recordings > recordings);
    }

    /// <summary>
    /// A plot alone that does not change records again when a tile its list draws was let go of and its slot taken by
    /// another renderer's tile, or when the device was lost and the slots were taken again, in a cache the two renderers
    /// share (design §5.5.2 "予算", Issue #636): each render draws the same faces.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - One null backend of a budget of 64 faces and 4 pages, whose draws keep each face and page sent in its slot and then
    ///   count the uploads as sent. Renderer P draws a float raster of 1,024 × 1,024 over [0, 100]²; renderer Q draws a
    ///   Points layer of 25 pages, which needs room P's tiles hold, then that layer as a density with a raster of 5,000
    ///   everywhere over [0, 100]², whose tiles take free slots.
    ///
    /// Steps:
    /// - Render P; take the faces its list draws. Render Q's first plot and its second. Render P's plot again, unchanged.
    ///   Then reset the cache as a lost device does, render Q's second plot again, which takes the slots from the first, and
    ///   render P's plot again.
    ///
    /// Expected:
    /// - Each of P's renders after the first records again and draws the same faces in the same order.
    /// </remarks>
    [Fact]
    public void SharedCache_PlotThatDoesNotChange_DrawsItsOwnTilesAgain()
    {
        var table = Table(25);
        var values = new float[1024 * 1024];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = i % 977;
        }

        using var pixels = Column.Wrap(values);
        using var others = Column.Wrap(Enumerable.Repeat(5_000f, 1024 * 1024).ToArray());
        var still = Plot.Create(table).Image(Raster.Of(pixels, 1024), i => i.Extent(0, 100, 0, 100)).Build();
        var other = Plot.Create(table).Image(Raster.Of(others, 1024), i => i.Extent(0, 100, 0, 100)).Build().Layers[0];
        var gpu = new GpuPages();
        var backend = new NullBackend { Images = new ResidencyCache((64L * ResidencyCache.FaceBytes) + (4 * Page)) };
        backend.Sending = cache =>
        {
            gpu.Send(cache);
            cache.Sent();
        };
        using var p = new HeadlessRenderer(backend, 640, 360, _pool);
        using var q = new HeadlessRenderer(backend, 640, 360, _pool);
        p.Render(still);
        var drawn = gpu.Faces(p.Plan!.List);
        Assert.NotEmpty(drawn);
        Assert.DoesNotContain(drawn, face => face is null);

        q.Render(Spec(table, Points(0)));
        q.Render(Spec(table, Hidden(0), other));
        var recordings = p.Plan.Recordings;
        p.Render(still);
        Assert.Equal(drawn, gpu.Faces(p.Plan.List));
        Assert.True(p.Plan.Recordings > recordings);

        backend.Images.Reset();
        q.Render(Spec(table, Hidden(0), other));
        recordings = p.Plan.Recordings;
        p.Render(still);
        Assert.Equal(drawn, gpu.Faces(p.Plan.List));
        Assert.True(p.Plan.Recordings > recordings);
    }

    /// <summary>
    /// A figure's plot that does not change records again when the frame's budget, counting another plot's new layer, chooses
    /// another drawing for one of its layers (design §5.5.2 "予算", Issue #636).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A budget of 7 pages. Plot 0: an Auto layer of 3 pages. Plot 1: a Points layer of 1 page, then a Points layer of 5
    ///   pages.
    ///
    /// Steps:
    /// - Render the figure headless; render it again with plot 1's layer of 5 pages, plot 0 unchanged.
    ///
    /// Expected:
    /// - First, plot 0's layer draws raw points (4 pages in all), its list drawing pages. Then the sum is 8 pages, plot 0's
    ///   layer is the only Auto layer on raw points, and plot 0 records again and draws it as a density, its list drawing no
    ///   page (under 64 faces of budget no tile is sent either); the sum left is plot 1's 5 pages.
    /// </remarks>
    [Fact]
    public void Figure_PlotThatDoesNotChange_RecordsWhenTheBudgetChangesItsDrawing()
    {
        var table = Table(3, 1, 5);
        var still = Spec(table, Auto(0));
        var backend = new NullBackend { Images = new ResidencyCache(7 * Page) };
        using var renderer = new HeadlessRenderer(backend, 640, 360, _pool);
        renderer.Render(Figure.HConcat(still, Spec(table, Points(1))));
        var plans = renderer.Figure!.Plans;
        var layer = (IndexedPointPlan)plans[0].Panels[0].Layers[0];
        Assert.False(layer.DrewDensity);
        Assert.NotEmpty(plans[0].List.ResidentRuns.ToArray());

        var recordings = plans[0].Recordings;
        renderer.Render(Figure.HConcat(still, Spec(table, Points(2))));
        Assert.True(plans[0].Recordings > recordings);
        Assert.True(layer.DrewDensity);
        Assert.Empty(plans[0].List.ResidentRuns.ToArray());
        Assert.Equal(5 * Page, plans[0].Budget.LastSum);
    }

    /// <summary>
    /// A kept plot's layer split by a category takes its parts' last scans into the budget, and the plot records again when
    /// the budget makes the layer a density (design §5.5.2 "予算", Issue #636): the split layer's carry and its check of
    /// the drawing reach every part.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A budget of 7 pages. Plot 0: an Auto layer of 196,608 seeded points over [0, 100]² colored by a category of 2
    ///   values, row by row (2 parts of 98,304 points, which need 3 pages' bytes together). Plot 1: a Points layer of 1
    ///   page, then a Points layer of 5 pages.
    ///
    /// Steps:
    /// - Render the figure headless; render it again with plot 1's layer of 5 pages, plot 0 unchanged.
    ///
    /// Expected:
    /// - First, both parts draw raw points, the layer needing 3 pages. Then the sum is 8 pages, plot 0 records again, and
    ///   both parts draw a density; the sum left is plot 1's 5 pages.
    /// </remarks>
    [Fact]
    public void Figure_PlotThatDoesNotChange_SplitLayerRecordsWhenTheBudgetChangesItsDrawing()
    {
        const int Rows = 3 * PointPages.PagePoints;
        var random = new Random(636);
        using var xs = Column.Wrap(Enumerable.Range(0, Rows).Select(_ => random.NextDouble() * 100).ToArray());
        using var ys = Column.Wrap(Enumerable.Range(0, Rows).Select(_ => random.NextDouble() * 100).ToArray());
        var split = DataTable
            .Create()
            .Add("x", xs)
            .Add("y", ys)
            .Add("c", Column.Categorical(Enumerable.Range(0, Rows).Select(i => (string?)(i % 2 == 0 ? "a" : "b")).ToArray()))
            .Build();
        var layer = Plot.Create(split).Point(split.Col<double>("x"), split.Col<double>("y"), p => p.Size(1).ColorBy(split.Cat("c"))).Build().Layers[
            0
        ];
        var still = Spec(split, layer);
        var table = Table(1, 5);
        var backend = new NullBackend { Images = new ResidencyCache(7 * Page) };
        using var renderer = new HeadlessRenderer(backend, 640, 360, _pool);
        renderer.Render(Figure.HConcat(still, Spec(table, Points(0))));
        var plans = renderer.Figure!.Plans;
        var parts = ((SplitPlan)plans[0].Panels[0].Layers[0]).Parts.Cast<IndexedPointPlan>().ToArray();
        Assert.Equal(2, parts.Length);
        Assert.Equal((false, false), (parts[0].DrewDensity, parts[1].DrewDensity));
        Assert.Equal(3 * Page, parts[0].Switch.Needed);

        var recordings = plans[0].Recordings;
        renderer.Render(Figure.HConcat(still, Spec(table, Points(1))));
        Assert.True(plans[0].Recordings > recordings);
        Assert.Equal((true, true), (parts[0].DrewDensity, parts[1].DrewDensity));
        Assert.Equal(5 * Page, plans[0].Budget.LastSum);
    }

    /// <summary>Returns a layer's predicted time alone, as the switch reads it at the reference machine's speed: the fixed term and the others.</summary>
    private static double Of(long points, double diameter, double largestBin) =>
        PredictedTime.Fixed + PredictedTime.Terms(points, diameter, largestBin);

    /// <summary>
    /// A kept plot whose layer draws nothing yet (a density with no tile sent) still enters the frame's budget with its last
    /// scan, and a kept plot records again when the machine's time scale changes (design §5.5.2 "予算", "ほかの機械", Issue
    /// #636).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A budget of 7 pages, under the 64 faces a texture array needs, so that no tile is sent. Plot 0: a layer of 3 pages
    ///   drawn as a density. Plot 1: a Points layer of 1 page, then of 5 pages.
    ///
    /// Steps:
    /// - Render the figure headless; render it with plot 1's second layer; set the renderer's time scale to 2 and render
    ///   the same figure.
    ///
    /// Expected:
    /// - Plot 0's list draws nothing of the cache. The second render does not record plot 0, and its layer took a full scan
    ///   in the frame (carried). The third records plot 0 again.
    /// </remarks>
    [Fact]
    public void Figure_PlotThatDoesNotChange_CarriesALayerThatDrawsNothingAndRecordsForATimeScale()
    {
        var table = Table(3, 1, 5);
        var still = Spec(table, Hidden(0));
        var backend = new NullBackend { Images = new ResidencyCache(7 * Page) };
        using var renderer = new HeadlessRenderer(backend, 640, 360, _pool);
        renderer.Render(Figure.HConcat(still, Spec(table, Points(1))));
        var plans = renderer.Figure!.Plans;
        var layer = (IndexedPointPlan)plans[0].Panels[0].Layers[0];
        Assert.Equal((0, 0), (plans[0].List.Images.Length, plans[0].List.ResidentRuns.Length));

        var recordings = plans[0].Recordings;
        renderer.Render(Figure.HConcat(still, Spec(table, Points(2))));
        Assert.Equal(recordings, plans[0].Recordings);
        Assert.True(layer.ScannedFull(backend.Images.Frame));

        renderer.GpuTimeScale = 2;
        renderer.Render(Figure.HConcat(still, Spec(table, Points(2))));
        Assert.True(plans[0].Recordings > recordings);
    }

    /// <summary>Returns the markers' diameter, in pixels, that makes a count and a largest bin predict a time.</summary>
    private static float Diameter(double time, long count, double largestBin)
    {
        var n = count / PredictedTime.PointsUnit;
        var term = time - PredictedTime.Fixed - (PredictedTime.PerPoints * n);
        var area = term / (PredictedTime.Overlap * n * Math.Pow(largestBin, PredictedTime.Exponent));
        return (float)Math.Sqrt(area * 4 / Math.PI);
    }

    private static IndexedPointPlan Layer(HeadlessRenderer renderer, int layer) => (IndexedPointPlan)renderer.Plan!.Panels[0].Layers[layer];

    private static IndexedPointPlan Layer(PlotEngine engine, int layer) => (IndexedPointPlan)engine.Plan!.Panels[0].Layers[layer];

    private PointLayerSpec Auto(int layer) => Points(layer) with { Draw = PointDraw.Auto };

    /// <summary>A layer drawn as a density, which draws no page: under 64 faces of budget no tile is sent, so it draws nothing.</summary>
    private PointLayerSpec Hidden(int layer) => Points(layer) with { Draw = PointDraw.Density };

    private PointLayerSpec Points(int layer) =>
        new()
        {
            X = _table!.Col<double>($"x{layer}"),
            Y = _table.Col<double>($"y{layer}"),
            Size = 1,
            Draw = PointDraw.Points,
        };

    private static PlotSpec Spec(DataTable table, params LayerSpec[] layers) => GeomTesting.Spec(table, layers);

    /// <summary>Renders a spec once on a null backend of a budget, waiting for its indexes and its pages.</summary>
    private HeadlessRenderer Render(long budget, PlotSpec spec)
    {
        var renderer = new HeadlessRenderer(new NullBackend { Images = new ResidencyCache(budget) }, 640, 360, _pool);
        renderer.Render(spec);
        return renderer;
    }

    private HeadlessRenderer Render(int pages, PlotSpec spec) => Render(pages * Page, spec);

    /// <summary>
    /// Returns a table of a pair of columns (x, y) a layer, each of whole pages of seeded uniform points over [0, 100]², and
    /// NaN in the rows past them.
    /// </summary>
    private DataTable Table(params int[] pages)
    {
        var builder = DataTable.Create();
        var rows = pages.Max() * PointPages.PagePoints;
        for (var layer = 0; layer < pages.Length; layer++)
        {
            var random = new Random(700 + layer);
            var count = pages[layer] * PointPages.PagePoints;
            var (xs, ys) = (new double[rows], new double[rows]);
            for (var i = 0; i < rows; i++)
            {
                // A table's columns have one length: a layer of fewer pages has NaN rows, which its index skips.
                (xs[i], ys[i]) = i < count ? (random.NextDouble() * 100, random.NextDouble() * 100) : (double.NaN, double.NaN);
            }

            var (x, y) = (Column.Wrap(xs), Column.Wrap(ys));
            _columns.Add(x);
            _columns.Add(y);
            builder = builder.Add($"x{layer}", x).Add($"y{layer}", y);
        }

        _table = builder.Build();
        return _table;
    }

    /// <summary>
    /// The pages and the faces a GPU would hold, slot by slot, from what the draws of a null backend send: a dropped slot's
    /// buffer goes, and a sent page's points or a sent face's texels take their slot's.
    /// </summary>
    private sealed class GpuPages
    {
        private readonly Dictionary<int, ResidentPoint[]> _pages = [];
        private readonly Dictionary<(ResidencyCache.Format, int), uint[]> _faces = [];

        public void Send(ResidencyCache cache)
        {
            foreach (var slot in cache.Points.Dropped)
            {
                _ = _pages.Remove(slot);
            }

            foreach (var upload in cache.Points.Uploads)
            {
                _pages[upload.Slot] = cache.Points.Staged(upload).ToArray();
            }

            foreach (var upload in cache.Uploads)
            {
                _faces[(upload.Format, upload.Slot)] = cache.Face(upload).ToArray();
            }
        }

        /// <summary>Returns the faces a list's image records read, in the records' order: null for a slot that holds none.</summary>
        public uint[]?[] Faces(RenderList list)
        {
            var images = list.Images;
            var calls = list.ImageCalls;
            var faces = new uint[]?[images.Length];
            for (var i = 0; i < images.Length; i++)
            {
                // A call's page is the format a 1,024 and the texture array of the slot (ImagePlan.PageOf).
                var page = calls[i].Page;
                var format = page >= 1024 ? ResidencyCache.Format.Rgba : ResidencyCache.Format.Float;
                faces[i] = _faces.GetValueOrDefault((format, ((page % 1024) * ResidencyCache.PageSlots) + (int)images[i].Slice));
            }

            return faces;
        }

        /// <summary>
        /// Returns the centers a list's resident runs draw, sorted, each computed from the kept page as Markers.hlsl computes
        /// it (the cell's pixel plus the point's units from the cell's base times its scale, clamped); NaN for a point of a
        /// slot that holds no page or whose cell is not in the list's table.
        /// </summary>
        public (float X, float Y)[] Centers(RenderList list)
        {
            var centers = new List<(float X, float Y)>();
            var cells = list.ResidentCells;
            foreach (var run in list.ResidentRuns)
            {
                if (!_pages.TryGetValue(run.Slot, out var page) || run.First + run.Count > page.Length)
                {
                    centers.AddRange(Enumerable.Repeat((float.NaN, float.NaN), run.Count));
                    continue;
                }

                for (var i = run.First; i < run.First + run.Count; i++)
                {
                    var point = page[i];
                    var at = run.Cells + (long)point.Cell;
                    if (at >= cells.Length)
                    {
                        centers.Add((float.NaN, float.NaN));
                        continue;
                    }

                    var cell = cells[(int)at];
                    var x = Math.Clamp(cell.PixelX + (Units(point.X, cell.BaseX) * cell.ScaleX), run.ClampLow.X, run.ClampHigh.X);
                    var y = Math.Clamp(cell.PixelY + (Units(point.Y, cell.BaseY) * cell.ScaleY), run.ClampLow.Y, run.ClampHigh.Y);
                    centers.Add(run.Swap ? (y, x) : (x, y));
                }
            }

            return [.. centers.Order()];

            static float Units(uint position, uint origin) => position >= origin ? position - origin : -(float)(origin - position);
        }
    }
}
