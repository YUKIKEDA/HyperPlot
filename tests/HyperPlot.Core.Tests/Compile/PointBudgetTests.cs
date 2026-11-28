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
    /// - With the image, the frames after the first count its tiles (more than one face), the layer's bytes and the tiles'
    ///   pass the budget, and the layer draws a density; the sum left holds the tiles, a face for each image record the
    ///   last list draws (the raster's tiles and the density's), as the frame before it drew the same.
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
        var layer = (IndexedPointPlan)renderer.Plan!.Panels[0].Layers[1];
        Assert.InRange(backend.Images.TileBytesLastFrame, 2L * ResidencyCache.FaceBytes, budget);
        Assert.Equal(backend.LastList!.Images.Length * (long)ResidencyCache.FaceBytes, backend.Images.TileBytesLastFrame);
        Assert.True(layer.DrewDensity);
        Assert.Equal(backend.Images.TileBytesLastFrame, renderer.Plan.Budget.LastSum);
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
}
