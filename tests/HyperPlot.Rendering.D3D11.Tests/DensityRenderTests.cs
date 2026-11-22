using System.Diagnostics;
using HyperPlot.Interaction;
using HyperPlot.Testing;
using HyperPlot.Tests;

namespace HyperPlot.Rendering.D3D11.Tests;

/// <summary>
/// Tests that build columns and spatial indexes of up to 1e7 points, hundreds of megabytes; they run apart from the others
/// (.cursor/rules/testing.mdc).
/// </summary>
[CollectionDefinition(DensityRenderTests.Collection, DisableParallelization = true)]
public sealed class LargeDataGroup;

/// <summary>
/// Row R3b1's densities of point layers on the index path drawn through the whole engine on WARP (design §5.5.2 "行 R3b1 の
/// 密度と切り替え", §10's R3b1 row), with the fake text backend.
/// </summary>
[Collection(Collection)]
public sealed class DensityRenderTests
{
    /// <summary>The name of the collection of the tests of large data.</summary>
    internal const string Collection = "Large data";

    private const int Large = 10_000_000;

    /// <summary>
    /// A density of 1e7 points spread evenly, the whole view, through log and shades of the layer's color (design §5.5.2
    /// "描き方").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 1e7 seeded float points spread evenly over [0, 100]², generated a chunk at a time into native columns; Draw is
    ///   Density, the norm Log, no map; domains [0, 100] on both axes; the fake text backend; Theme.Light.
    ///
    /// Steps:
    /// - Render at 640 × 360 with a renderer that waits for the index; compare with <c>Golden/density-uniform.png</c>.
    ///
    /// Expected:
    /// - The layer drew a density from the root's tile: the plot area is under 512 pixels high, so the tile level is 0, and
    ///   every piece is the root read from its own tile.
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void Uniform_MatchesBaseline()
    {
        using var pool = new PlotWorkerPool(4);
        using var x = Column.CreateNative<float>(Large, (first, chunk) => Fill(chunk, first, Uniform, ofX: true));
        using var y = Column.CreateNative<float>(Large, (first, chunk) => Fill(chunk, first, Uniform, ofX: false));
        var table = DataTable.Create().Add("x", x).Add("y", y).Build();
        var spec = Plot.Create(table)
            .Point(table.Col<float>("x"), table.Col<float>("y"), p => p.Draw(PointDraw.Density))
            .ScaleX(Scale.Linear().WithDomain(0, 100).WithTitle("x"))
            .ScaleY(Scale.Linear().WithDomain(0, 100).WithTitle("y"))
            .Title("Density, 1e7 points spread evenly")
            .Build();

        Check(spec, pool, 640, 360, "density-uniform.png", Drew);

        static void Drew(HeadlessRenderer renderer)
        {
            var part = (IndexedPointPlan)renderer.Plan!.Layers[0];
            Assert.True(part.DrewDensity);
            Assert.Equal(0, part.TileLevel);
            var pieces = part.Pieces.ToArray();
            Assert.NotEmpty(pieces);
            Assert.All(pieces, piece => Assert.Equal((0, 0), (piece.CellLevel, piece.TileLevel)));
        }
    }

    /// <summary>
    /// A density of 1e7 points in two clusters over a thin background, the whole view, through log and shades of the
    /// layer's color, where the cells of level 1 with too few points for a tile of their own draw their part of the root's
    /// tile normalized by 4 (design §5.5.2 "水準の選び方", "広さで正規化する", "祖先のタイル").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 1e7 seeded float points over [0, 100]²: 60 % in a normal cluster at (28, 70) of deviations (8, 6), 35 % in one at
    ///   (72, 28) of deviations (6, 8), and 5 % spread evenly; a cluster's point outside [0, 100]² is taken evenly instead.
    ///   The lower-left and upper-right quarters of the range hold about 130,000 and 155,000 points, under the density
    ///   split of 262,144, so their cells of level 1 have no tile; the other two have one.
    /// - Draw is Density, the norm Log, no map; domains [0, 100]; the fake text backend; Theme.Light.
    ///
    /// Steps:
    /// - Render at 1,280 × 720 with a renderer that waits for the index; compare with <c>Golden/density-clustered.png</c>.
    ///
    /// Expected:
    /// - The layer drew a density at tile level 1 (the plot area is at least 512 pixels and under 1,024 high): some cells of
    ///   level 1 are read from their own tile and some from the root's tile of level 0.
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void Clustered_MatchesBaseline()
    {
        using var pool = new PlotWorkerPool(4);
        using var x = Column.CreateNative<float>(Large, (first, chunk) => Fill(chunk, first, Clustered, ofX: true));
        using var y = Column.CreateNative<float>(Large, (first, chunk) => Fill(chunk, first, Clustered, ofX: false));
        var table = DataTable.Create().Add("x", x).Add("y", y).Build();
        var spec = Plot.Create(table)
            .Point(table.Col<float>("x"), table.Col<float>("y"), p => p.Draw(PointDraw.Density))
            .ScaleX(Scale.Linear().WithDomain(0, 100).WithTitle("x"))
            .ScaleY(Scale.Linear().WithDomain(0, 100).WithTitle("y"))
            .Title("Density, 1e7 points in two clusters")
            .Build();

        Check(spec, pool, 1280, 720, "density-clustered.png", Drew);

        static void Drew(HeadlessRenderer renderer)
        {
            var part = (IndexedPointPlan)renderer.Plan!.Layers[0];
            Assert.True(part.DrewDensity);
            Assert.Equal(1, part.TileLevel);
            var pieces = part.Pieces.ToArray();
            Assert.Contains(pieces, piece => piece is { CellLevel: 1, TileLevel: 1 });
            Assert.Contains(pieces, piece => piece is { CellLevel: 1, TileLevel: 0 });
        }
    }

    /// <summary>
    /// A density of the same 1e7 clustered points through a color map, viridis, in place of the layer's shades (design
    /// §5.5.2 "描き方"): the empty bins stay transparent, not viridis's first color.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The clustered points of <see cref="Clustered_MatchesBaseline"/>; Draw is Density, the norm Log, the map viridis;
    ///   domains [0, 100]; the fake text backend; Theme.Light.
    ///
    /// Steps:
    /// - Render at 640 × 360 with a renderer that waits for the index; compare with <c>Golden/density-viridis.png</c>.
    ///
    /// Expected:
    /// - The layer drew a density.
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void Viridis_MatchesBaseline()
    {
        using var pool = new PlotWorkerPool(4);
        using var x = Column.CreateNative<float>(Large, (first, chunk) => Fill(chunk, first, Clustered, ofX: true));
        using var y = Column.CreateNative<float>(Large, (first, chunk) => Fill(chunk, first, Clustered, ofX: false));
        var table = DataTable.Create().Add("x", x).Add("y", y).Build();
        var spec = Plot.Create(table)
            .Point(table.Col<float>("x"), table.Col<float>("y"), p => p.Draw(PointDraw.Density).Density(DensityNorm.Log, ColorMaps.Viridis))
            .ScaleX(Scale.Linear().WithDomain(0, 100).WithTitle("x"))
            .ScaleY(Scale.Linear().WithDomain(0, 100).WithTitle("y"))
            .Title("Density through viridis")
            .Build();

        Check(spec, pool, 640, 360, "density-viridis.png", Drew);

        static void Drew(HeadlessRenderer renderer) => Assert.True(((IndexedPointPlan)renderer.Plan!.Layers[0]).DrewDensity);
    }

    /// <summary>
    /// A layer split by a category of three drawn as densities: each part from its own index and tiles, in shades of its
    /// category's color, drawn over each other in the parts' order, under one top (design §5.5.2 "分けた層", "濃さの上端 m").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 900,000 seeded float points, the categories "alpha", "beta", and "gamma" in runs of 300,000 rows, each a normal
    ///   cluster of deviation 12 at (35, 45), (55, 60), and (62, 35), overlapping; a point outside [0, 100]² is taken evenly
    ///   instead. 300,000 is a round count just above the density split of 262,144, so that each part's root has a tile;
    ///   1e7 points would only make the test slower.
    /// - Colored by the category; Draw is Density, the norm Log, no map; domains [0, 100]; the fake text backend;
    ///   Theme.Light.
    ///
    /// Steps:
    /// - Render at 640 × 360 with a renderer that waits for the indexes; compare with <c>Golden/density-split.png</c>.
    ///
    /// Expected:
    /// - Three parts, each of which drew a density with the root's tile among its pieces.
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void Split_MatchesBaseline()
    {
        const int Run = 300_000;
        const int Count = 3 * Run;
        string[] names = ["alpha", "beta", "gamma"];
        using var pool = new PlotWorkerPool(4);
        using var x = Column.CreateNative<float>(Count, (first, chunk) => Fill(chunk, first, row => Blobs(row, Run), ofX: true));
        using var y = Column.CreateNative<float>(Count, (first, chunk) => Fill(chunk, first, row => Blobs(row, Run), ofX: false));
        var table = DataTable
            .Create()
            .Add("x", x)
            .Add("y", y)
            .Add("kind", Column.Categorical(Enumerable.Range(0, Count).Select(i => (string?)names[i / Run]).ToArray()))
            .Build();
        var spec = Plot.Create(table)
            .Point(table.Col<float>("x"), table.Col<float>("y"), p => p.Draw(PointDraw.Density).ColorBy(table.Cat("kind")))
            .ScaleX(Scale.Linear().WithDomain(0, 100).WithTitle("x"))
            .ScaleY(Scale.Linear().WithDomain(0, 100).WithTitle("y"))
            .Title("Density split by a category")
            .Build();

        Check(spec, pool, 640, 360, "density-split.png", Drew);

        static void Drew(HeadlessRenderer renderer)
        {
            var parts = ((SplitPlan)renderer.Plan!.Layers[0]).Parts.Cast<IndexedPointPlan>().ToArray();
            Assert.Equal(3, parts.Length);
            foreach (var part in parts)
            {
                Assert.True(part.DrewDensity);
                Assert.Contains(part.Pieces.ToArray(), piece => piece.TileLevel == 0);
            }
        }
    }

    /// <summary>
    /// While the index has only its coarse snapshot, a layer of Auto draws the coarse density, the level-8 grid of 256 × 256
    /// counts, as one tile over the whole range, provisional (design §5.5.2 "索引ができるまで").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 400,000 seeded double points of the two clusters of <see cref="Clustered_MatchesBaseline"/>, more than the CPU
    ///   path's 131,072 rows; the x column on a test storage that holds every worker read of chunk 0 once the index writes
    ///   its copy, so the build stands after its coarse snapshot, published before the write pass. Draw is Auto, which with
    ///   a full snapshot of 400,000 points under the threshold of 1e7 would draw raw points; the norm Log, no map; domains
    ///   [0, 100]; a pool of 1 worker; an engine on WARP that does not wait for its indexes; the fake text backend;
    ///   Theme.Light; 640 × 360.
    ///
    /// Steps:
    /// - Hold chunk 0 until the plan has started its index, then let the reads go until the index writes. Render frames
    ///   until the layer records the coarse density's face; read the pixels.
    /// - Let the reads go in a finally, and render frames until one is not provisional, so that the build ends before the
    ///   engine is disposed. Compare the pixels read with <c>Golden/density-coarse.png</c>.
    ///
    /// Expected:
    /// - At the frame read: the snapshot is Coarse, the frame is provisional, and the layer drew a density of one piece,
    ///   the root read from the coarse grid, whose face was sent.
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void Coarse_MatchesBaseline()
    {
        const int Count = 400_000;
        const int Width = 640;
        const int Height = 360;
        var points = Enumerable.Range(0, Count).Select(row => Clustered(row)).ToArray();
        using var xs = TestStorage<double>.MakeColumn(points.Select(p => p.X).ToArray(), out var storage);
        using var ys = Column.Wrap(points.Select(p => p.Y).ToArray());
        var table = DataTable.Create().Add("x", xs).Add("y", ys).Build();
        var spec = Plot.Create(table)
            .Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Draw(PointDraw.Auto))
            .ScaleX(Scale.Linear().WithDomain(0, 100).WithTitle("x"))
            .ScaleY(Scale.Linear().WithDomain(0, 100).WithTitle("y"))
            .Title("Coarse density while the index builds")
            .Build();
        using var pool = new PlotWorkerPool(1);
        using var backend = D3D11Backend.CreateForTest(useWarp: true, createDevice: null, new TextEngine(new FakeTextBackend()));
        using var engine = new PlotEngine(backend, Width, Height, 1, pool);
        var hold = new WriteHold(storage);
        storage.OnWorkerRead = hold.OnRead;
        var clock = Stopwatch.StartNew();
        var pixels = new byte[Width * Height * 4];

        // The build waits at its first read until the test knows its index: from then on, a read of the copy's writing holds.
        storage.Hold(0);
        try
        {
            engine.SetPlot(spec);
            engine.RenderFrame();
            var plan = (IndexedPointPlan)engine.Plan!.Layers[0];
            while (plan.SpatialIndex is null)
            {
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), "The index was not started in 60 s.");
                engine.RenderFrame();
            }

            var index = plan.SpatialIndex;
            hold.Watch(index);
            storage.Release(0);
            var drawn = false;
            while (!drawn)
            {
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), "The coarse density was not drawn in 60 s.");
                engine.RenderFrame();
                drawn = plan.DrewDensity && plan.Pieces.Length == 1 && !plan.MissedPages;
            }

            engine.ReadPixels(pixels);
            var snapshot = index.Acquire();
            var stage = snapshot.Stage;
            snapshot.Release();
            Assert.Equal(SpatialStage.Coarse, stage);
            Assert.True(engine.IsProvisional);
            Assert.Equal([new DensityPiece(0, 0, 0, 0, 0)], plan.Pieces.ToArray());
        }
        finally
        {
            hold.Stop();
            storage.Release(0);
        }

        // The build ends before the engine is disposed: a worker that read before the stop may hold the chunk again.
        while (engine.IsProvisional)
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), "The frames stayed provisional for 60 s.");
            storage.Release(0);
            engine.RenderFrame();
        }

        GoldenImage.AssertMatches(
            pixels,
            Width,
            Height,
            GoldenImageTests.Baseline("density-coarse.png"),
            GoldenImageTests.Tolerance,
            GoldenImageTests.Output
        );
    }

    /// <summary>
    /// A view of raw points drawn first while most of its pages were not on the GPU, and drawn again once they all are,
    /// allocates nothing the second time (design §11 invariant 3, Issue #528): a page not sent yet is recorded as a run
    /// that draws nothing, so the list and the backend's steps reach the view's size in its first frame.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 1,048,576 seeded float points spread evenly over [0, 100]², 16 pages of 65,536 points; Draw is Points; domains
    ///   [0, 100]; an engine on WARP that does not wait for its indexes, so a frame sends 4 MB at most, 5 pages; the fake
    ///   text backend; 640 × 360. The index has ended.
    ///
    /// Steps:
    /// - Render the whole view once; wait for the workers' reads it asked for (row R3b2) and render it again. Then render
    ///   each quarter of it, [0.5, 49.5] or [50.5, 99.5] on each axis, until the quarter's frame has all its pages, waiting
    ///   for the reads after each frame. Collect, then render the whole view again, counting the thread's allocations over
    ///   the view's change and the frame.
    ///
    /// Expected:
    /// - The first frame of the whole view is provisional with no page on the GPU, and records 16 runs, none with a slot.
    ///   The next is provisional with 5 pages on the GPU, and records 16 runs, 5 of them with a slot.
    /// - No quarter's frame records more than 8 runs, so that nothing but the first frame grew the list past 8 runs; after
    ///   the quarters, 16 pages are on the GPU.
    /// - The last frame is not provisional, records 16 runs with a slot each, and allocated 0 bytes.
    /// </remarks>
    [Fact]
    public void RawPoints_ViewDrawnBeforeItsPagesArrived_AllocatesNothingWhenDrawnAgain()
    {
        const int Pages = 16;
        const int Count = Pages * PointPages.PagePoints;
        using var pool = new PlotWorkerPool(4);
        using var x = Column.CreateNative<float>(Count, (first, chunk) => Fill(chunk, first, Uniform, ofX: true));
        using var y = Column.CreateNative<float>(Count, (first, chunk) => Fill(chunk, first, Uniform, ofX: false));
        var table = DataTable.Create().Add("x", x).Add("y", y).Build();
        var spec = Plot.Create(table)
            .Point(table.Col<float>("x"), table.Col<float>("y"), p => p.Draw(PointDraw.Points))
            .ScaleX(Scale.Linear().WithDomain(0, 100))
            .ScaleY(Scale.Linear().WithDomain(0, 100))
            .Build();
        using var backend = D3D11Backend.CreateForTest(useWarp: true, createDevice: null, new TextEngine(new FakeTextBackend()));
        using var engine = new PlotEngine(backend, 640, 360, 1, pool);
        var panel = new PanelId(0, 0);
        var whole = new PanelView2D(ViewRange.FromValues(0, 100), ViewRange.FromValues(0, 100));
        engine.SetPlot(spec);
        engine.RenderFrame();
        var plan = (IndexedPointPlan)engine.Plan!.Layers[0];
        Eventually.Completes(plan.SpatialIndex!.WhenComplete());

        engine.Views.SetView(panel, whole);
        engine.RenderFrame();
        var asked = engine.Plan!.List.ResidentRuns.ToArray();
        Assert.True(engine.IsProvisional);
        Assert.Equal(0, backend.Images.Points.PagesInUse);
        Assert.Equal((Pages, 0), (asked.Length, asked.Count(run => run.Slot >= 0)));
        _ = backend.Images.Points.AwaitReads();
        engine.RenderFrame();
        var first = engine.Plan!.List.ResidentRuns.ToArray();
        Assert.True(engine.IsProvisional);
        Assert.Equal(5, backend.Images.Points.PagesInUse);

        (double Low, double High)[] halves = [(0.5, 49.5), (50.5, 99.5)];
        var most = 0;
        foreach (var (lowX, highX) in halves)
        {
            foreach (var (lowY, highY) in halves)
            {
                engine.Views.SetView(panel, new PanelView2D(ViewRange.FromValues(lowX, highX), ViewRange.FromValues(lowY, highY)));
                var missed = true;
                while (missed)
                {
                    engine.RenderFrame();
                    most = Math.Max(most, engine.Plan!.List.ResidentRuns.Length);
                    missed = plan.MissedPages;
                    _ = backend.Images.Points.AwaitReads();
                }
            }
        }

        Assert.InRange(most, 1, 8);
        Assert.Equal(Pages, backend.Images.Points.PagesInUse);

        // An allocation that follows a collection is counted in every run (.cursor/rules/bench.mdc).
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        engine.Views.SetView(panel, whole);
        engine.RenderFrame();
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;

        var last = engine.Plan!.List.ResidentRuns.ToArray();
        Assert.False(engine.IsProvisional);
        Assert.Equal((Pages, Pages), (last.Length, last.Count(run => run.Slot >= 0)));
        Assert.Equal(0, bytes);
        Assert.Equal((Pages, 5), (first.Length, first.Count(run => run.Slot >= 0)));
    }

    private static void Check(PlotSpec spec, PlotWorkerPool pool, int width, int height, string baseline, Action<HeadlessRenderer> drew)
    {
        using var backend = D3D11Backend.CreateForTest(useWarp: true, createDevice: null, new TextEngine(new FakeTextBackend()));
        using var renderer = new HeadlessRenderer(backend, width, height, pool);
        renderer.Render(spec);
        drew(renderer);

        GoldenImage.AssertMatches(renderer, GoldenImageTests.Baseline(baseline), GoldenImageTests.Tolerance, GoldenImageTests.Output);
    }

    /// <summary>
    /// Writes the x or the y of the rows of a chunk from a function of the row alone, so that a chunk holds the same values
    /// whichever order the chunks are written in.
    /// </summary>
    private static void Fill(Span<float> chunk, long first, Func<long, (double X, double Y)> point, bool ofX)
    {
        for (var i = 0; i < chunk.Length; i++)
        {
            var (x, y) = point(first + i);
            chunk[i] = (float)(ofX ? x : y);
        }
    }

    /// <summary>Returns a row's point spread evenly over [0, 100]².</summary>
    private static (double X, double Y) Uniform(long row) => (100 * Unit(row, 0), 100 * Unit(row, 1));

    /// <summary>Returns a row's point of the two clusters over a thin background (<see cref="Clustered_MatchesBaseline"/>).</summary>
    private static (double X, double Y) Clustered(long row)
    {
        var pick = Unit(row, 2);
        return pick < 0.60 ? Normal(row, 28, 70, 8, 6)
            : pick < 0.95 ? Normal(row, 72, 28, 6, 8)
            : Uniform(row);
    }

    /// <summary>Returns a row's point of the three overlapping clusters, one a run of rows (<see cref="Split_MatchesBaseline"/>).</summary>
    private static (double X, double Y) Blobs(long row, int run)
    {
        return (row / run) switch
        {
            0 => Normal(row, 35, 45, 12, 12),
            1 => Normal(row, 55, 60, 12, 12),
            _ => Normal(row, 62, 35, 12, 12),
        };
    }

    /// <summary>Returns a row's point of a normal cluster (Box–Muller), or one spread evenly when it falls outside [0, 100]².</summary>
    private static (double X, double Y) Normal(long row, double x, double y, double deviationX, double deviationY)
    {
        var radius = Math.Sqrt(-2 * Math.Log(1 - Unit(row, 3)));
        var angle = 2 * Math.PI * Unit(row, 4);
        var point = (X: x + (deviationX * radius * Math.Cos(angle)), Y: y + (deviationY * radius * Math.Sin(angle)));
        return point.X is >= 0 and <= 100 && point.Y is >= 0 and <= 100 ? point : Uniform(row);
    }

    /// <summary>Returns a seeded number in [0, 1) for a row and a stream: SplitMix64's mix of the pair, its top 53 bits.</summary>
    private static double Unit(long row, int stream)
    {
        var z = ((ulong)row * 8) + (ulong)stream + 0x9E3779B97F4A7C15;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
        z ^= z >> 31;
        return (z >> 11) * (1.0 / (1UL << 53));
    }

    /// <summary>
    /// Holds chunk 0 of a storage at every worker read from the time the watched index writes its copy, until it is stopped:
    /// the build then stands after its coarse snapshot.
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
}
