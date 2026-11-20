using HyperPlot.Interaction;
using HyperPlot.Rendering;
using HyperPlot.Testing;
using static HyperPlot.Tests.GeomTesting;

namespace HyperPlot.Tests;

/// <summary>
/// The blocks of a point layer's pages and the blocks in view (design §5.5.2 "行 R3b2 の重なりと予算", §10 R3b2 row
/// "ブロック（Core）" and "見えるブロック（Core、偽のバックエンド）").
/// </summary>
[Collection("Large data")]
public sealed class PointBlockTests
{
    private static readonly PanelId Panel = new(0, 0);

    /// <summary>
    /// The points of a block follow from the parts of the layer in a panel: 32,768 over the parts, rounded up to a power of
    /// two, from 512 to 32,768. A page holds each block's points in steps of 104,729 through the copy's order, each once; no
    /// block crosses a page, and a page's last block holds the rest.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 150,000 seeded points over [0, 100]², colored by a category of <paramref name="parts"/> names that take turns row
    ///   by row (no color for one part): every part has more points than a block and a page that ends in a shorter block.
    /// - An engine that waits for its indexes, which sends every page in the frame that asks for it, on the null backend.
    ///
    /// Steps:
    /// - Render the whole view. For every page of every part's snapshot, request the page with <paramref name="block"/>
    ///   and read what the frame staged for its slot; read the same part of the copy with ReadBlock.
    /// - Request a page with half and with twice the block.
    ///
    /// Expected:
    /// - No request with <paramref name="block"/> stages a page: the layer asked for the pages with that block.
    /// - Point k of each block of a staged page is point k × 104,729 modulo the block's count of the same block of the
    ///   copy, and the steps take each point of the block once; the last block of a page has the page's rest.
    /// - Some page of the layer has more points than the block. Half and twice the block each stage a page of their own.
    /// </remarks>
    /// <param name="parts">The parts of the layer.</param>
    /// <param name="block">The points of a block the design gives for the parts.</param>
    [Theory]
    [InlineData(1, 32_768)]
    [InlineData(2, 16_384)]
    [InlineData(3, 16_384)]
    [InlineData(4, 8_192)]
    [InlineData(6, 8_192)]
    [InlineData(7, 8_192)]
    [InlineData(8, 4_096)]
    [InlineData(15, 4_096)]
    [InlineData(16, 2_048)]
    [InlineData(63, 1_024)]
    [InlineData(64, 512)]
    public void Blocks_FollowThePartsOfTheLayer(int parts, int block)
    {
        const int Count = 150_000;
        using var data = new Data(Count, seed: 41, parts);
        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, 640, 360) { WaitsForIndexes = true };
        engine.SetPlot(data.Spec());
        engine.RenderFrame();
        Assert.False(engine.IsProvisional);

        var pages = backend.Images.Points;
        backend.Images.Unbounded = true;
        var plans = Parts(engine.Plan!.Panels[0]);
        Assert.Equal(parts, plans.Length);
        var staged = pages.Uploads.Length;
        var longest = 0;
        long points = 0;
        foreach (var plan in plans)
        {
            var snapshot = plan.SpatialIndex!.Acquire();
            try
            {
                points += snapshot.PointCount;
                for (var page = 0; (long)page * PointPages.PagePoints < snapshot.PointCount; page++)
                {
                    var slot = pages.Request(snapshot, page, block);
                    Assert.Equal(staged, pages.Uploads.Length);
                    var upload = Assert.Single(pages.Uploads.ToArray(), u => u.Slot == slot);
                    Assert.Equal(PointPages.PointsOf(snapshot, page), upload.Count);
                    longest = Math.Max(longest, upload.Count);
                    AssertInSteps(snapshot, page, block, pages.Staged(upload));
                }

                if (ReferenceEquals(plan, plans[0]))
                {
                    Assert.NotEqual(pages.Request(snapshot, 0, block / 2), pages.Request(snapshot, 0, block * 2));
                    Assert.Equal(staged += 2, pages.Uploads.Length);
                }
            }
            finally
            {
                snapshot.Release();
            }
        }

        Assert.Equal(Count, points);
        Assert.True(longest > block);
    }

    /// <summary>The formula gives 512 points for more than 64 parts too, which no layer has before row R3d3.</summary>
    /// <remarks>
    /// Preconditions:
    /// - None.
    ///
    /// Steps:
    /// - Ask for the points of a block of 65, 128, and 100,000 parts.
    ///
    /// Expected:
    /// - 512 each.
    /// </remarks>
    [Fact]
    public void BlockPoints_MoreThan64Parts_Is512()
    {
        Assert.Equal((512, 512, 512), (PointPages.BlockPoints(65), PointPages.BlockPoints(128), PointPages.BlockPoints(100_000)));
    }

    /// <summary>
    /// The parts that set the block are the parts that lie over one another in one panel: a facet's panels are not counted,
    /// so a layer of 8 categories has blocks of 4,096 points in 6 panels as in one.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 480,000 seeded points over [0, 100]², colored by a category of 8 names (row modulo 8) and wrapped by a category of
    ///   6 names (row / 8 modulo 6): 48 parts of 10,000 points, two whole blocks of 4,096 and a block of 1,808 each. With
    ///   the panels counted the block would be 1,024.
    /// - An engine that waits for its indexes, which sends every page in the frame that asks for it, on the null backend.
    ///
    /// Steps:
    /// - Render the whole view with the facet. For every page of every part of every panel, request the page with a block
    ///   of 4,096 and read what the frame staged.
    ///
    /// Expected:
    /// - 6 panels of 8 parts. No request stages a page, and every staged page is in steps within blocks of 4,096.
    /// </remarks>
    [Fact]
    public void Blocks_FacetPanels_AreNotCountedAsParts()
    {
        const int Count = 480_000;
        const int Block = 4_096;
        using var data = new Data(Count, seed: 43, parts: 8, panels: 6);
        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, 960, 640) { WaitsForIndexes = true };
        engine.SetPlot(data.Spec());
        engine.RenderFrame();
        Assert.False(engine.IsProvisional);

        var pages = backend.Images.Points;
        backend.Images.Unbounded = true;
        var staged = pages.Uploads.Length;
        Assert.Equal(48, staged);
        Assert.Equal(6, engine.Plan!.Panels.Count);
        foreach (var panel in engine.Plan.Panels)
        {
            var plans = Parts(panel);
            Assert.Equal(8, plans.Length);
            foreach (var plan in plans)
            {
                var snapshot = plan.SpatialIndex!.Acquire();
                try
                {
                    Assert.Equal(10_000, snapshot.PointCount);
                    var slot = pages.Request(snapshot, 0, Block);
                    Assert.Equal(staged, pages.Uploads.Length);
                    AssertInSteps(snapshot, 0, Block, pages.Staged(Assert.Single(pages.Uploads.ToArray(), u => u.Slot == slot)));
                }
                finally
                {
                    snapshot.Release();
                }
            }
        }
    }

    /// <summary>
    /// A part of no point is a part too (design §5.5.2 "ブロック"): a layer of four categories, one of them with no finite
    /// point, has the blocks of four parts, 8,192 points, not the 16,384 of three.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 150,000 seeded points over [0, 100]², colored by a category of 4 names that take turns row by row; the x of every
    ///   row of the fourth name is NaN, so that its index holds no point. The other parts hold 37,500 points each, more
    ///   than two blocks of either size.
    /// - An engine that waits for its indexes, which sends every page in the frame that asks for it, on the null backend.
    ///
    /// Steps:
    /// - Render the whole view. For the page of each part that holds points, request the page with a block of 8,192.
    ///
    /// Expected:
    /// - The layer has 4 parts, one of no point. No request stages a page, and each staged page is in steps within blocks
    ///   of 8,192.
    /// </remarks>
    [Fact]
    public void Blocks_PartOfNoPoint_IsCounted()
    {
        const int Count = 150_000;
        const int Block = 8_192;
        var random = new Random(61);
        using var xs = Column.Wrap(Enumerable.Range(0, Count).Select(i => i % 4 == 3 ? double.NaN : random.NextDouble() * 100).ToArray());
        using var ys = Column.Wrap(Enumerable.Range(0, Count).Select(_ => random.NextDouble() * 100).ToArray());
        var table = DataTable
            .Create()
            .Add("x", xs)
            .Add("y", ys)
            .Add("c", Column.Categorical(Enumerable.Range(0, Count).Select(i => (string?)$"k{i % 4}").ToArray()))
            .Build();
        var point = new PointLayerSpec
        {
            X = table.Col<double>("x"),
            Y = table.Col<double>("y"),
            ColorBy = table.Cat("c").ToColumnRef(),
        };
        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, 640, 360) { WaitsForIndexes = true };
        engine.SetPlot(GeomTesting.Spec(table, point) with { ScaleColor = Scale.Ordinal() });
        engine.RenderFrame();

        var pages = backend.Images.Points;
        backend.Images.Unbounded = true;
        var staged = pages.Uploads.Length;
        Assert.Equal(3, staged);
        var plans = Parts(engine.Plan!.Panels[0]);
        Assert.Equal(4, plans.Length);
        var empty = 0;
        foreach (var plan in plans)
        {
            var snapshot = plan.SpatialIndex!.Acquire();
            try
            {
                if (snapshot.PointCount == 0)
                {
                    empty++;
                    continue;
                }

                Assert.Equal(37_500, snapshot.PointCount);
                var slot = pages.Request(snapshot, 0, Block);
                Assert.Equal(staged, pages.Uploads.Length);
                AssertInSteps(snapshot, 0, Block, pages.Staged(Assert.Single(pages.Uploads.ToArray(), u => u.Slot == slot)));
            }
            finally
            {
                snapshot.Release();
            }
        }

        Assert.Equal(1, empty);
    }

    /// <summary>
    /// A recording draws the blocks in view only: the points of the cells that reach into the view, widened to the blocks'
    /// ends, the blocks that follow one another in a page as one run and the others as runs of their own. The whole view is
    /// one run a page. Zooming and panning change the runs and not the blocks: no page is read again.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - <paramref name="count"/> seeded points over [0, 100]² in <paramref name="parts"/> parts (blocks of
    ///   <paramref name="block"/> points), in an engine that waits for its indexes, which sends every page in the frame that asks for it, on the null backend.
    ///
    /// Steps:
    /// - Render the whole view, and say that its pages were sent. Then render each of five views: a tall strip at the left,
    ///   a small square off the middle, the strip moved right by 7, a wide strip at the bottom, and the middle half.
    /// - For each view, mark every block that holds a point of a resident cell whose box reaches into the view (its ends
    ///   included), and join the marked blocks that follow one another in a page.
    /// - Render the first view again with the cache drawing whole pages.
    ///
    /// Expected:
    /// - The whole view records one run a page, from 0, of the page's points.
    /// - Each view records exactly the joined blocks, as (part, page, first, count), and stages no page.
    /// - Over the views, some run begins after its page's first block, and each view draws fewer points than the whole
    ///   view. Some page of the 8 parts (16 blocks a page) has two runs or more; no page of the one part (2 blocks) has.
    /// - With whole pages: one run, from 0 and of the page's points, for each page that has a run without.
    /// </remarks>
    /// <param name="parts">The parts of the layer.</param>
    /// <param name="block">The points of a block of the layer.</param>
    /// <param name="count">The points of the layer.</param>
    [Theory]
    [InlineData(1, 32_768, 600_000)]
    [InlineData(8, 4_096, 2_400_000)]
    public void Recording_DrawsTheBlocksInView(int parts, int block, int count)
    {
        using var data = new Data(count, seed: 47, parts);
        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, 640, 360) { WaitsForIndexes = true };
        engine.SetPlot(data.Spec());
        engine.RenderFrame();
        var pages = backend.Images.Points;
        using var layer = new Layer(engine, backend.Images, block);

        var whole = layer.Runs(backend.LastList!);
        Assert.Equal(layer.Expected(0, 100, 0, 100, clampX: false, wholePages: false), whole);
        Assert.Equal(pages.PagesInUse, whole.Length);
        Assert.All(whole, run => Assert.Equal((0, layer.PointsOf(run.Part, run.Page)), (run.First, run.Count)));
        Assert.Equal(count, whole.Sum(r => r.Count));

        (double X0, double X1, double Y0, double Y1)[] views =
        [
            (1.3, 9.7, 2.1, 41.3),
            (61.7, 63.1, 30.2, 31.9),
            (8.3, 16.7, 2.1, 41.3),
            (3.3, 88.1, 4.9, 7.7),
            (26.1, 73.7, 27.3, 72.9),
        ];
        var (split, late) = (false, false);
        var first = Array.Empty<Run>();
        foreach (var (x0, x1, y0, y1) in views)
        {
            backend.Images.Sent();
            engine.Views.SetView(Panel, new PanelView2D(ViewRange.FromValues(x0, x1), ViewRange.FromValues(y0, y1)));
            engine.RenderFrame();
            var runs = layer.Runs(backend.LastList!);
            Assert.Equal(layer.Expected(x0, x1, y0, y1, clampX: false, wholePages: false), runs);
            Assert.Equal(0, pages.Uploads.Length);
            Assert.InRange(runs.Sum(r => r.Count), 1, count - 1);
            split |= runs.GroupBy(r => (r.Part, r.Page)).Any(g => g.Count() > 1);
            late |= runs.Any(r => r.First > 0);
            first = first.Length == 0 ? runs : first;
        }

        // A page of two blocks cannot hold two runs: only the split layer's pages, of 16 blocks, can.
        Assert.True(late);
        Assert.Equal(parts > 1, split);

        var (wx0, wx1, wy0, wy1) = views[0];
        pages.DrawsWholePages = true;
        backend.Images.Sent();
        engine.Views.SetView(Panel, new PanelView2D(ViewRange.FromValues(wx0, wx1), ViewRange.FromValues(wy0, wy1)));
        engine.RenderFrame();
        var drawnWhole = layer.Runs(backend.LastList!);
        Assert.Equal(layer.Expected(wx0, wx1, wy0, wy1, clampX: false, wholePages: true), drawnWhole);
        Assert.Equal(first.Select(r => (r.Part, r.Page)).Distinct(), drawnWhole.Select(r => (r.Part, r.Page)));
        Assert.All(drawnWhole, run => Assert.Equal((0, layer.PointsOf(run.Part, run.Page)), (run.First, run.Count)));
        Assert.True(drawnWhole.Sum(r => r.Count) > first.Sum(r => r.Count));
    }

    /// <summary>
    /// On an axis that clamps, the blocks of the cells outside the view on that axis are drawn too: their points are drawn
    /// at the view's end. The other axis still chooses the blocks.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 600,000 seeded points over [0, 100]² in one part, in an engine that waits for its indexes, on the null backend
    ///   with no frame budget. The x domain is [200, 300], past every point; the y domain is [2.1, 31.3].
    ///
    /// Steps:
    /// - Render with an x scale that does not clamp, and with one that does.
    ///
    /// Expected:
    /// - Without the clamp: no run. With it: exactly the joined blocks of the cells whose y reaches into [2.1, 31.3],
    ///   whatever their x; fewer points than the layer has, and more than none.
    /// </remarks>
    [Fact]
    public void Recording_ClampedAxis_DrawsTheBlocksOfTheCellsOutsideTheView()
    {
        const int Count = 600_000;
        using var data = new Data(Count, seed: 53, parts: 1);
        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, 640, 360) { WaitsForIndexes = true };
        var spec = data.Spec() with { ScaleX = Scale.Linear().WithDomain(200, 300), ScaleY = Scale.Linear().WithDomain(2.1, 31.3) };
        engine.SetPlot(spec);
        engine.RenderFrame();
        Assert.Equal(0, backend.LastList!.ResidentRuns.Length);

        backend.Images.Sent();
        engine.SetPlot(spec with { ScaleX = Scale.Linear().WithDomain(200, 300) with { Clamp = true } });
        engine.RenderFrame();
        using var layer = new Layer(engine, backend.Images, PointPages.MaxBlockPoints, resident: false);
        var runs = layer.Runs(backend.LastList!);
        Assert.Equal(layer.Expected(200, 300, 2.1, 31.3, clampX: true, wholePages: false), runs);
        Assert.InRange(runs.Sum(r => r.Count), 1, Count - 1);
    }

    /// <summary>
    /// Frames that zoom and pan a split layer allocate nothing once its pages are there, though the blocks in view, and so
    /// the runs, change with every frame (invariant 3, design §5.5.2 "確保").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 2,400,000 seeded points over [0, 100]² colored by 4 categories (blocks of 8,192 points), in an engine that waits for
    ///   its indexes, on the null backend. Twelve views: a zoom from the whole view to a tenth of the side around a center
    ///   that moves, and back. Every view is drawn until no page is missing, so that its tick labels are shaped too.
    ///
    /// Steps:
    /// - Draw the twelve views as a warm-up; collect; draw them once more, counting the thread's allocations.
    ///
    /// Expected:
    /// - 0 bytes.
    /// - No two views that follow one another record the same runs.
    /// </remarks>
    [Fact]
    public void ZoomAndPan_AllocateNothing()
    {
        const int Count = 2_400_000;
        using var data = new Data(Count, seed: 59, parts: 4);
        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, 640, 360) { WaitsForIndexes = true };
        engine.SetPlot(
            Plot.Create(data.Table)
                .Point(data.Table.Col<double>("x"), data.Table.Col<double>("y"), p => p.ColorBy(data.Table.Cat("c")))
                .ScaleX(Scale.Linear().WithDomain(0, 100))
                .ScaleY(Scale.Linear().WithDomain(0, 100))
                .Build()
        );
        var views = Enumerable
            .Range(0, 12)
            .Select(i =>
            {
                var span = 100 * Math.Pow(10, -Math.Sin(i / 12.0 * Math.PI));
                var (cx, cy) = (50 + (20 * Math.Sin(i / 12.0 * 2 * Math.PI)), 50 + (10 * Math.Cos(i / 12.0 * 2 * Math.PI)));
                return new PanelView2D(
                    ViewRange.FromValues(cx - (span / 2), cx + (span / 2)),
                    ViewRange.FromValues(cy - (span / 2), cy + (span / 2))
                );
            })
            .ToArray();
        var recorded = new List<(int, int, int)[]>();
        engine.RenderFrame();
        foreach (var view in views)
        {
            Draw(view);
            while (engine.IsProvisional)
            {
                Draw(view);
            }

            recorded.Add([.. backend.LastList!.ResidentRuns.ToArray().Select(r => (r.Slot, r.First, r.Count))]);
        }

        for (var i = 0; i < views.Length; i++)
        {
            Assert.NotEqual(recorded[i], recorded[(i + 1) % views.Length]);
        }

        PerformanceAssert.DoesNotAllocate(() =>
        {
            foreach (var view in views)
            {
                Draw(view);
            }
        });
        Assert.False(engine.IsProvisional);

        void Draw(PanelView2D view)
        {
            backend.Images.Sent();
            engine.Views.SetView(Panel, view);
            engine.RenderFrame();
        }
    }

    /// <summary>Returns the parts of a panel's first layer on the index path.</summary>
    private static IndexedPointPlan[] Parts(PlotPanel panel) =>
        panel.Layers[0] is SplitPlan split ? [.. split.Parts.Cast<IndexedPointPlan>()] : [(IndexedPointPlan)panel.Layers[0]];

    /// <summary>
    /// Asserts that a staged page holds each block of its part of the copy in steps of 104,729, every point of the block
    /// once, the last block being the page's rest.
    /// </summary>
    private static void AssertInSteps(SpatialSnapshot snapshot, int page, int block, ReadOnlySpan<ResidentPoint> staged)
    {
        var copy = new ResidentPoint[staged.Length];
        snapshot.ReadBlock((long)page * PointPages.PagePoints, copy);
        var taken = new bool[staged.Length];
        for (var start = 0; start < staged.Length; start += block)
        {
            var length = Math.Min(block, staged.Length - start);
            for (var k = 0; k < length; k++)
            {
                var from = start + (int)(((long)k * 104_729) % length);
                Assert.False(taken[from]);
                taken[from] = true;
                if (copy[from] != staged[start + k])
                {
                    Assert.Fail($"Point {k} of the block at {start} of page {page} is not point {from} of the copy.");
                }
            }
        }
    }

    /// <summary>A run as a test reads it: the part and the page of its slot, its first point, and its points.</summary>
    private readonly record struct Run(int Part, int Page, int First, int Count);

    /// <summary>Seeded points over [0, 100]² with a category of the parts and, when asked, one of the panels.</summary>
    private sealed class Data : IDisposable
    {
        private readonly Column<double> _x;
        private readonly Column<double> _y;
        private readonly int _parts;
        private readonly int _panels;

        public Data(int count, int seed, int parts, int panels = 1)
        {
            var random = new Random(seed);
            _x = Column.Wrap(Enumerable.Range(0, count).Select(_ => random.NextDouble() * 100).ToArray());
            _y = Column.Wrap(Enumerable.Range(0, count).Select(_ => random.NextDouble() * 100).ToArray());
            (_parts, _panels) = (parts, panels);
            var builder = DataTable.Create().Add("x", _x).Add("y", _y);
            var names = Enumerable.Range(0, Math.Max(parts, panels)).Select(i => $"k{i:D2}").ToArray();
            builder = builder.Add("c", Column.Categorical(Enumerable.Range(0, count).Select(i => (string?)names[i % parts]).ToArray()));
            builder = builder.Add("f", Column.Categorical(Enumerable.Range(0, count).Select(i => (string?)names[(i / parts) % panels]).ToArray()));
            Table = builder.Build();
        }

        public DataTable Table { get; }

        /// <summary>Returns the spec of the point layer over [0, 100]², split by the parts and wrapped by the panels.</summary>
        public PlotSpec Spec()
        {
            var point = new PointLayerSpec { X = Table.Col<double>("x"), Y = Table.Col<double>("y") };
            var spec =
                _parts > 1
                    ? GeomTesting.Spec(Table, point with { ColorBy = Table.Cat("c").ToColumnRef() }) with
                    {
                        ScaleColor = Scale.Ordinal(),
                    }
                    : GeomTesting.Spec(Table, point);
            return _panels > 1 ? spec with { Facet = new FacetWrapSpec { By = Table.Cat("f").ToColumnRef() } } : spec;
        }

        public void Dispose()
        {
            _x.Dispose();
            _y.Dispose();
        }
    }

    /// <summary>
    /// The parts of an engine's first layer with their snapshots, and the slot of each of their pages: asked for right after
    /// a frame, with the layer's block, which stages nothing after a frame of the whole view when the layer asked with the
    /// same block.
    /// </summary>
    private sealed class Layer : IDisposable
    {
        private readonly SpatialSnapshot[] _snapshots;
        private readonly Dictionary<int, (int Part, int Page)> _slots = [];
        private readonly int _block;

        public Layer(PlotEngine engine, ResidencyCache cache, int block, bool resident = true)
        {
            // The frame's own requests were without a frame budget, as a frame that waits; these are too.
            var pages = cache.Points;
            cache.Unbounded = true;
            _block = block;
            _snapshots = [.. Parts(engine.Plan!.Panels[0]).Select(p => p.SpatialIndex!.Acquire())];
            var staged = pages.Uploads.Length;
            for (var part = 0; part < _snapshots.Length; part++)
            {
                for (var page = 0; (long)page * PointPages.PagePoints < _snapshots[part].PointCount; page++)
                {
                    _slots.Add(pages.Request(_snapshots[part], page, block), (part, page));
                }
            }

            // After a frame of the whole view every page is there, when the layer asked with the same block.
            Assert.True(!resident || staged == pages.Uploads.Length);
        }

        public int PointsOf(int part, int page) => PointPages.PointsOf(_snapshots[part], page);

        /// <summary>Returns a list's runs by part, page, and first point.</summary>
        public Run[] Runs(RenderList list) =>
            [
                .. list
                    .ResidentRuns.ToArray()
                    .Select(r => new Run(_slots[r.Slot].Part, _slots[r.Slot].Page, r.First, r.Count))
                    .OrderBy(r => r.Part)
                    .ThenBy(r => r.Page)
                    .ThenBy(r => r.First),
            ];

        /// <summary>
        /// Returns the runs the design gives for a view in data units: every block that holds a point of a resident cell
        /// whose box reaches into the view, the marked blocks that follow one another in a page joined; or, for whole
        /// pages, each page that has such a block.
        /// </summary>
        public Run[] Expected(double x0, double x1, double y0, double y1, bool clampX, bool wholePages)
        {
            var runs = new List<Run>();
            for (var part = 0; part < _snapshots.Length; part++)
            {
                var snapshot = _snapshots[part];
                var marked = new bool[(snapshot.PointCount + _block - 1) / _block];
                var (lowX, highX) = (x0 - snapshot.XRange.Origin, x1 - snapshot.XRange.Origin);
                var (lowY, highY) = (y0 - snapshot.YRange.Origin, y1 - snapshot.YRange.Origin);
                foreach (var cell in snapshot.ResidentCells)
                {
                    var seesX = clampX || (cell.CornerX <= highX && cell.CornerX + cell.ExtentX >= lowX);
                    if (seesX && cell.CornerY <= highY && cell.CornerY + cell.ExtentY >= lowY)
                    {
                        for (var b = cell.First / _block; b * _block < cell.First + cell.Count; b++)
                        {
                            marked[b] = true;
                        }
                    }
                }

                var inPage = PointPages.PagePoints / _block;
                for (var page = 0; page * inPage < marked.Length; page++)
                {
                    var points = PointsOf(part, page);
                    var blocks = marked.AsSpan(page * inPage, Math.Min(inPage, marked.Length - (page * inPage)));
                    if (wholePages)
                    {
                        if (blocks.Contains(true))
                        {
                            runs.Add(new Run(part, page, 0, points));
                        }

                        continue;
                    }

                    for (var b = 0; b < blocks.Length; b++)
                    {
                        if (blocks[b] && (b == 0 || !blocks[b - 1]))
                        {
                            var end = b;
                            while (end < blocks.Length && blocks[end])
                            {
                                end++;
                            }

                            runs.Add(new Run(part, page, b * _block, Math.Min(points, end * _block) - (b * _block)));
                        }
                    }
                }
            }

            return [.. runs];
        }

        public void Dispose()
        {
            foreach (var snapshot in _snapshots)
            {
                snapshot.Release();
            }
        }
    }
}
