using System.Diagnostics;
using HyperPlot.Interaction;
using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>
/// Spike for Issue #656: the side effects of several plans that share one residency cache, whose frame moves on at every
/// plan's record. Each test prints its figures; the assertions state what the code does now, not what it should do.
/// </summary>
[Collection(nameof(SharedCacheSpikeTests))]
[CollectionDefinition(nameof(SharedCacheSpikeTests), DisableParallelization = true)]
public sealed class SharedCacheSpikeTests : IDisposable
{
    private const long Page = (long)PointPages.PagePoints * PointPages.PointBytes;

    private readonly PlotWorkerPool _pool = new(3);
    private readonly List<IDisposable> _columns = [];

    public void Dispose()
    {
        _pool.Dispose();
        foreach (var column in _columns)
        {
            column.Dispose();
        }
    }

    /// <summary>
    /// Side effect 1: a view whose point pages need reading, drawn in turn with another view on the same backend that records
    /// every frame, never sends its pages: each of its frames withdraws the reads of its frame before last.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Engine A: a Points layer of 3 pages, default budget, frames that do not wait. Engine B: a line of 1,000 points whose
    ///   view moves every frame. Both on one null backend, or A alone on its own.
    ///
    /// Steps:
    /// - Draw until A's index is complete, then 200 more ticks; a tick draws A, waits for the reads, draws B, waits.
    ///
    /// Expected:
    /// - Alone: A clears its provisional mark within a few frames, 3 reads. Shared: A stays provisional through the 200
    ///   ticks, and its reads grow by about one a tick.
    /// </remarks>
    [Fact]
    public void ReadCancellation_TwoEnginesInTurn()
    {
        var output = TestContext.Current.TestOutputHelper!;

        // 0: A alone; 1: B a line whose view moves; 2: B a Points layer of 3 other pages, its view still.
        foreach (var mode in new[] { 0, 1, 2 })
        {
            var shared = mode > 0;
            var table = Table(3, 3);
            var reads = 0;
            var backend = new NullBackend();
            backend.Images.Points.TestRead = (_, _) => Interlocked.Increment(ref reads);
            using var a = new PlotEngine(backend, 640, 360, 1, _pool);
            a.SetPlot(GeomTesting.Spec(table, Points(table)));
            using var b = shared ? new PlotEngine(backend, 640, 360, 1, _pool) : null;
            b?.SetPlot(mode == 1 ? Line() : GeomTesting.Spec(table, Points(table, 1)));

            var clock = Stopwatch.StartNew();
            var (tick, complete, cleared, readsAtComplete, sentAt) = (0, -1, -1, 0, -1);
            while (complete < 0 || tick - complete < 200)
            {
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), "The index did not end in 60 s.");
                backend.Images.Sent();
                a.RenderFrame();
                _ = backend.Images.Points.AwaitReads();
                if (b is not null)
                {
                    if (mode == 1 && tick > 0)
                    {
                        b.Views.SetView(new PanelId(0, 0), new PanelView2D(ViewRange.FromValues(tick % 2, 1000), ViewRange.FromValues(-1, 2)));
                    }

                    backend.Images.Sent();
                    b.RenderFrame();
                    _ = backend.Images.Points.AwaitReads();
                }

                if (sentAt < 0 && backend.PagesSent > 0)
                {
                    sentAt = tick;
                }

                if (complete < 0 && a.Plan?.Panels[0].Layers[0] is IndexedPointPlan { SpatialIndex.IsComplete: true })
                {
                    (complete, readsAtComplete) = (tick, Volatile.Read(ref reads));
                }

                if (complete >= 0 && cleared < 0 && !a.IsProvisional)
                {
                    cleared = tick;
                }

                tick++;
            }

            output.WriteLine(
                $"mode={mode}: B provisional {b?.IsProvisional}; index complete at tick {complete}, reads then {readsAtComplete}; cleared at tick {cleared}; first page sent at tick {sentAt}; "
                    + $"ticks {tick}; reads {reads}; pages in use {backend.Images.Points.PagesInUse}; cache frame {backend.Images.Frame}; "
                    + $"A provisional {a.IsProvisional}; pages sent {backend.PagesSent}"
            );
            if (shared)
            {
                Assert.True(a.IsProvisional);
                Assert.Equal(0, backend.Images.Points.PagesInUse);
            }
            else
            {
                Assert.True(cleared >= 0);
            }
        }
    }

    /// <summary>
    /// Side effect 1 with a figure: the same two plots as one engine's figure share one cache frame, and A's pages come.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - One engine, a figure of A's Points layer of 3 pages and B's line, B's view moved every frame.
    ///
    /// Steps:
    /// - Draw until A's index is complete, then up to 200 more frames, each followed by the wait for the reads.
    ///
    /// Expected:
    /// - The frame clears its provisional mark: the figure records both plots in one cache frame.
    /// </remarks>
    [Fact]
    public void ReadCancellation_FigureOfTheSamePlots()
    {
        var output = TestContext.Current.TestOutputHelper!;
        var table = Table(3);
        var reads = 0;
        var backend = new NullBackend();
        backend.Images.Points.TestRead = (_, _) => Interlocked.Increment(ref reads);
        using var engine = new PlotEngine(backend, 1280, 360, 1, _pool);
        engine.SetFigure(Figure.HConcat(GeomTesting.Spec(table, Points(table)), Line()));
        var clock = Stopwatch.StartNew();
        var (tick, complete, cleared) = (0, -1, -1);
        while (cleared < 0 && (complete < 0 || tick - complete < 200))
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), "The index did not end in 60 s.");
            if (tick > 0)
            {
                engine.Views.SetView(new PanelId(1, 0), new PanelView2D(ViewRange.FromValues(tick % 2, 1000), ViewRange.FromValues(-1, 2)));
            }

            backend.Images.Sent();
            engine.RenderFrame();
            _ = backend.Images.Points.AwaitReads();
            if (complete < 0 && engine.Figure?.Plans[0].Panels[0].Layers[0] is IndexedPointPlan { SpatialIndex.IsComplete: true })
            {
                complete = tick;
            }

            if (complete >= 0 && !engine.IsProvisional)
            {
                cleared = tick;
            }

            tick++;
        }

        output.WriteLine(
            $"figure: index complete at tick {complete}; cleared at tick {cleared}; reads {reads}; pages {backend.Images.Points.PagesInUse}"
        );
        Assert.True(cleared >= 0);
    }

    /// <summary>
    /// Side effect 2: the tiles of the last cache frame are another renderer's, so a plan's budget counts tiles it does not
    /// draw, and does not count the other renderer's pages, which it shares the cache with.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Case T: a budget of 22 pages and one face. Renderer A draws a float raster of 1,024 × 1,024; renderer B an Auto
    ///   layer of 22 pages, which B alone draws raw. Case P: a budget of 7 pages; A a Points layer of 5 pages, B an Auto
    ///   layer of 4 pages.
    ///
    /// Steps:
    /// - Render B alone; then A and B on one backend, A first.
    ///
    /// Expected:
    /// - T: B's sum holds A's tiles and B draws a density. P: B's sum is its own 4 pages, B stays raw though the cache holds
    ///   9 pages' worth of need.
    /// </remarks>
    [Fact]
    public void TileBytesLastFrame_IsAnotherRenderersTiles()
    {
        var output = TestContext.Current.TestOutputHelper!;
        var budget = (22 * Page) + ResidencyCache.FaceBytes;
        var table = Table(22);
        var auto = Points(table) with { Draw = PointDraw.Auto };
        using (var alone = new HeadlessRenderer(new NullBackend { Images = new ResidencyCache(budget) }, 640, 360, _pool))
        {
            alone.Render(GeomTesting.Spec(table, auto));
            output.WriteLine($"T alone: B density {Layer(alone).DrewDensity}, sum {alone.Plan!.Budget.LastSum}, tiles last {0}");
            Assert.False(Layer(alone).DrewDensity);
        }

        using var pixels = Column.Wrap(new float[1024 * 1024]);
        var image = Plot.Create(table).Image(Raster.Of(pixels, 1024), i => i.Extent(0, 100, 0, 100)).Build().Layers[0];
        var backend = new NullBackend { Images = new ResidencyCache(budget) };
        using var a = new HeadlessRenderer(backend, 640, 360, _pool);
        using var b = new HeadlessRenderer(backend, 640, 360, _pool);
        a.Render(GeomTesting.Spec(table, image));
        var tilesOfA = backend.Images.SlotsInUse * (long)ResidencyCache.FaceBytes;
        b.Render(GeomTesting.Spec(table, auto));
        output.WriteLine(
            $"T shared: A's tile slots {backend.Images.SlotsInUse} ({tilesOfA} B), B density {Layer(b).DrewDensity}, "
                + $"B needed {Layer(b).Switch.Needed}, B sum {b.Plan!.Budget.LastSum}, budget {budget}"
        );
        Assert.True(Layer(b).DrewDensity);

        var small = Table(5, 4);
        var backendP = new NullBackend { Images = new ResidencyCache(7 * Page) };
        using var p = new HeadlessRenderer(backendP, 640, 360, _pool);
        using var q = new HeadlessRenderer(backendP, 640, 360, _pool);
        p.Render(GeomTesting.Spec(small, Points(small, 0)));
        q.Render(GeomTesting.Spec(small, Points(small, 1) with { Draw = PointDraw.Auto }));
        output.WriteLine(
            $"P shared: Q density {((IndexedPointPlan)q.Plan!.Panels[0].Layers[0]).DrewDensity}, Q sum {q.Plan.Budget.LastSum} "
                + $"({q.Plan.Budget.LastSum / Page} pages), P sum {p.Plan!.Budget.LastSum / Page} pages, pages in use "
                + $"{backendP.Images.Points.PagesInUse}, budget 7 pages"
        );
        Assert.False(((IndexedPointPlan)q.Plan.Panels[0].Layers[0]).DrewDensity);
    }

    /// <summary>
    /// Side effect 3: the frame's upload is per cache frame, so two engines drawn in one tick send twice it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two engines of 1,280 × 1,280 on one null backend, each a float raster of 2,048 × 2,048 of its own; and for
    ///   comparison one engine of 2,560 × 1,280 whose figure holds the two plots.
    ///
    /// Steps:
    /// - Draw ticks (A then B, or the figure) until no tile is sent for 5 ticks or 400 ticks; count each draw's bytes.
    ///
    /// Expected:
    /// - Two engines: the largest tick sends more than 4 MB, up to 2 × 4 MB. The figure: no tick sends more than 4 MB.
    /// </remarks>
    [Fact]
    public void FrameUpload_PerCacheFrame()
    {
        var output = TestContext.Current.TestOutputHelper!;
        var table = Table(1);
        var (first, second) = (Raster2048(table, 1), Raster2048(table, 2));
        foreach (var figure in new[] { false, true })
        {
            long tickBytes = 0;
            var backend = new NullBackend { Images = new ResidencyCache() };
            backend.Sending = cache =>
            {
                tickBytes += cache.BytesThisFrame;
                cache.Sent();
            };
            using var a = new PlotEngine(backend, figure ? 2560 : 1280, 1280, 1, _pool);
            using var b = figure ? null : new PlotEngine(backend, 1280, 1280, 1, _pool);
            if (figure)
            {
                a.SetFigure(Figure.HConcat(first, second));
            }
            else
            {
                a.SetPlot(first);
                b!.SetPlot(second);
            }

            var (max, total, quiet, ticks, over) = (0L, 0L, 0, 0, 0);
            var clock = Stopwatch.StartNew();
            while (quiet < 20 && clock.Elapsed < TimeSpan.FromSeconds(30))
            {
                tickBytes = 0;
                a.RenderFrame();
                b?.RenderFrame();
                (max, total, ticks) = (Math.Max(max, tickBytes), total + tickBytes, ticks + 1);
                over += tickBytes > ResidencyCache.FrameUpload ? 1 : 0;
                quiet = tickBytes == 0 && total > 0 ? quiet + 1 : 0;
            }

            output.WriteLine(
                $"figure={figure}: ticks {ticks}, largest tick {max} B ({max / (double)ResidencyCache.FrameUpload:F2} x FrameUpload), "
                    + $"ticks over 4 MB {over}, total {total} B, slots {backend.Images.SlotsInUse}"
            );
            if (figure)
            {
                Assert.True(max <= ResidencyCache.FrameUpload);
            }
            else
            {
                Assert.True(max > ResidencyCache.FrameUpload);
            }
        }
    }

    private static IndexedPointPlan Layer(HeadlessRenderer renderer) => (IndexedPointPlan)renderer.Plan!.Panels[0].Layers[0];

    private static PointLayerSpec Points(DataTable table, int layer = 0) =>
        new()
        {
            X = table.Col<double>($"x{layer}"),
            Y = table.Col<double>($"y{layer}"),
            Size = 1,
            Draw = PointDraw.Points,
        };

    private PlotSpec Raster2048(DataTable table, int seed)
    {
        var values = new float[2048 * 2048];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = (i * seed) % 977;
        }

        var pixels = Column.Wrap(values);
        _columns.Add(pixels);
        return Plot.Create(table).Image(Raster.Of(pixels, 2048), i => i.Extent(0, 100, 0, 100)).Build();
    }

    private PlotSpec Line()
    {
        var x = Column.Wrap(Enumerable.Range(0, 1000).Select(i => (double)i).ToArray());
        var y = Column.Wrap(Enumerable.Range(0, 1000).Select(i => Math.Sin(i * 0.01)).ToArray());
        _columns.Add(x);
        _columns.Add(y);
        var table = DataTable.Create().Add("x", x).Add("y", y).Build();
        return Plot.Create(table).Line(table.Col<double>("x"), table.Col<double>("y")).Build();
    }

    /// <summary>Returns a table of a pair of columns (x, y) a layer, each of whole pages of seeded uniform points over [0, 100]², NaN past them.</summary>
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
                (xs[i], ys[i]) = i < count ? (random.NextDouble() * 100, random.NextDouble() * 100) : (double.NaN, double.NaN);
            }

            var (x, y) = (Column.Wrap(xs), Column.Wrap(ys));
            _columns.Add(x);
            _columns.Add(y);
            builder = builder.Add($"x{layer}", x).Add($"y{layer}", y);
        }

        return builder.Build();
    }
}
