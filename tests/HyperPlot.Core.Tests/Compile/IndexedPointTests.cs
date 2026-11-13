using System.Diagnostics;
using HyperPlot.Interaction;
using HyperPlot.Rendering;
using static HyperPlot.Tests.GeomTesting;

namespace HyperPlot.Tests;

/// <summary>The point layer on the index path: which layers take it, its checks, and its frames (design §5.5.2, §10 R3a row).</summary>
[Collection("Large data")]
public sealed class IndexedPointTests
{
    private const int Over = LayerPlan.MaxElements + 1;
    private static readonly PanelId Panel = new(0, 0);
    private static readonly string[] Three = ["a", "b", "c"];

    /// <summary>
    /// The path is chosen by the layer's rows before any split (design §5.5.2 "2 つの道"): 131,072 rows are mapped on the CPU
    /// and make no index; 131,073 rows are drawn from the index in every part, the small ones too.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Seeded points over [0, 100]² in columns of 131,072 and of 131,073 rows; a category of three names, the third on
    ///   five rows only.
    ///
    /// Steps:
    /// - Render each length plain, and the longer one split by the category, on a plan that waits for its indexes.
    ///
    /// Expected:
    /// - 131,072 rows: 131,072 markers and no resident run.
    /// - 131,073 rows: no marker; runs of three pages whose points add up to 131,073; one cell's record or more.
    /// - Split: no marker but the legend's three samples; runs in the palette's three colors, the third's of five points.
    /// </remarks>
    [Fact]
    public void Path_IsChosenByTheLayersRows()
    {
        using var pool = new PlotWorkerPool(2);
        using var plan = TestPlans.Create(pool, Size, Size);
        var random = new Random(21);
        var values = Enumerable.Range(0, Over).Select(_ => random.NextDouble() * 100).ToArray();
        var others = Enumerable.Range(0, Over).Select(_ => random.NextDouble() * 100).ToArray();
        using var shortX = Column.Wrap(values[..LayerPlan.MaxElements]);
        using var shortY = Column.Wrap(others[..LayerPlan.MaxElements]);
        using var longX = Column.Wrap(values);
        using var longY = Column.Wrap(others);
        var few = DataTable.Create().Add("x", shortX).Add("y", shortY).Build();
        var many = DataTable
            .Create()
            .Add("x", longX)
            .Add("y", longY)
            .Add("c", Column.Categorical(Enumerable.Range(0, Over).Select(i => (string?)Three[i < 5 ? 2 : i % 2]).ToArray()))
            .Build();

        var mapped = plan.Render(Spec(few, new PointLayerSpec { X = few.Col<double>("x"), Y = few.Col<double>("y") }));
        Assert.Equal((LayerPlan.MaxElements, 0), (mapped.Markers.Length, mapped.ResidentRuns.Length));

        var point = new PointLayerSpec { X = many.Col<double>("x"), Y = many.Col<double>("y") };
        var resident = plan.Render(Spec(many, point));
        Assert.Equal(0, resident.Markers.Length);
        Assert.Equal(3, resident.ResidentRuns.Length);
        Assert.Equal(Over, resident.ResidentRuns.ToArray().Sum(r => r.Count));
        Assert.True(resident.ResidentCells.Length >= 1);

        var split = plan.Render(Spec(many, point with { ColorBy = many.Cat("c").ToColumnRef() }) with { ScaleColor = Scale.Ordinal() });
        Assert.Equal(3, split.Markers.Length);
        var runs = split.ResidentRuns.ToArray();
        Assert.Equal(Enumerable.Range(0, 3).Select(i => RenderList.Premultiply(Palettes.Tab10[i])), runs.Select(r => r.Color).Distinct());
        Assert.Equal(5, runs.Where(r => r.Color == RenderList.Premultiply(Palettes.Tab10[2])).Sum(r => r.Count));
        Assert.Equal(Over, runs.Sum(r => r.Count));
    }

    /// <summary>
    /// A facet's panels take the layer's path too (design §5.5.2 "2 つの道"): the rows are counted before the facet gathers
    /// them, so a layer of 131,073 rows is drawn from an index in every panel, though each panel holds fewer than 131,072.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Seeded points over [0, 100]² in columns of 131,073 rows; a category of three names that takes turns row by row, so
    ///   that each panel's rows are gathered: 43,691, 43,691, and 43,691.
    ///
    /// Steps:
    /// - Render the point layer wrapped by the category, on a plan that waits for its indexes.
    ///
    /// Expected:
    /// - No marker, and resident runs whose points add up to 131,073.
    /// </remarks>
    [Fact]
    public void FacetPanels_TakeTheLayersPath()
    {
        using var pool = new PlotWorkerPool(2);
        using var plan = TestPlans.Create(pool, Size, Size);
        var random = new Random(31);
        using var xs = Column.Wrap(Enumerable.Range(0, Over).Select(_ => random.NextDouble() * 100).ToArray());
        using var ys = Column.Wrap(Enumerable.Range(0, Over).Select(_ => random.NextDouble() * 100).ToArray());
        var table = DataTable
            .Create()
            .Add("x", xs)
            .Add("y", ys)
            .Add("c", Column.Categorical(Enumerable.Range(0, Over).Select(i => (string?)Three[i % 3]).ToArray()))
            .Build();
        var point = new PointLayerSpec { X = table.Col<double>("x"), Y = table.Col<double>("y") };

        var list = plan.Render(Spec(table, point) with { Facet = new FacetWrapSpec { By = table.Cat("c").ToColumnRef() } });

        Assert.Equal(0, list.Markers.Length);
        Assert.Equal(Over, list.ResidentRuns.ToArray().Sum(r => r.Count));
    }

    /// <summary>
    /// An axis that clamps draws the cells outside the view too (design §5.5.2 "常駐のセルとページ"): their points are
    /// drawn at the view's end, as the CPU path draws them. Without the clamp such a cell asks for no page.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Seeded points over [0, 100]² in columns of 131,073 rows. The x domain is [200, 300], past every point; the y domain
    ///   is [0, 100].
    ///
    /// Steps:
    /// - Render the layer with an x scale that does not clamp, and with one that does, on a plan that waits.
    ///
    /// Expected:
    /// - Without the clamp: no resident run. With it: runs whose points add up to 131,073, and no marker.
    /// </remarks>
    [Fact]
    public void ClampedAxis_DrawsTheCellsOutsideTheView()
    {
        using var pool = new PlotWorkerPool(2);
        using var plan = TestPlans.Create(pool, Size, Size);
        var random = new Random(37);
        using var xs = Column.Wrap(Enumerable.Range(0, Over).Select(_ => random.NextDouble() * 100).ToArray());
        using var ys = Column.Wrap(Enumerable.Range(0, Over).Select(_ => random.NextDouble() * 100).ToArray());
        var table = DataTable.Create().Add("x", xs).Add("y", ys).Build();
        var spec = Spec(table, new PointLayerSpec { X = table.Col<double>("x"), Y = table.Col<double>("y") }) with
        {
            ScaleX = Scale.Linear().WithDomain(200, 300),
            ScaleY = Scale.Linear().WithDomain(0, 100),
        };

        Assert.Equal(0, plan.Render(spec).ResidentRuns.Length);

        var clamped = plan.Render(spec with { ScaleX = Scale.Linear().WithDomain(200, 300) with { Clamp = true } });
        Assert.Equal((0, Over), (clamped.Markers.Length, clamped.ResidentRuns.ToArray().Sum(r => r.Count)));
    }

    /// <summary>
    /// The compile refuses what the index path does not draw (design §5.5.2 "上限と例外"): a numeric color, polar
    /// coordinates, a column that is not stored, and more than 64 parts.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Columns of 131,073 rows; categories of 64 and of 65 names over the 131,073 rows.
    ///
    /// Steps:
    /// - Render 131,073 rows colored by a number; 131,073 rows in polar coordinates; 131,073 rows over an evenly spaced x;
    ///   131,073 rows split by 65 categories, and by 64.
    ///
    /// Expected:
    /// - InvalidOperationException naming row R3d, R3e, and R3d for the first two and the 65 categories; ArgumentException
    ///   naming a stored column for the evenly spaced x; the 64 categories render 64 parts' runs.
    /// </remarks>
    [Fact]
    public void Validate_RefusesWhatTheIndexPathDoesNotDraw()
    {
        using var pool = new PlotWorkerPool(2);
        using var plan = TestPlans.Create(pool, Size, Size);
        using var values = Column.Wrap(Enumerable.Range(0, Over).Select(i => (double)(i % 100)).ToArray());
        var names64 = Enumerable.Range(0, 64).Select(i => $"k{i:D2}").ToArray();
        var names65 = Enumerable.Range(0, 65).Select(i => $"k{i:D2}").ToArray();
        var table = DataTable
            .Create()
            .Add("v", values)
            .Add("u", Column.Uniform(0, 1, Over))
            .Add("c64", Column.Categorical(Enumerable.Range(0, Over).Select(i => (string?)names64[i % 64]).ToArray()))
            .Add("c65", Column.Categorical(Enumerable.Range(0, Over).Select(i => (string?)names65[i % 65]).ToArray()))
            .Build();
        var v = table.Col<double>("v");
        var point = new PointLayerSpec { X = v, Y = v };

        var numbered = Spec(table, point with { ColorBy = v }) with { ScaleColor = Scale.Sequential() };
        Assert.Contains("R3d", Assert.Throws<InvalidOperationException>(() => plan.Render(numbered)).Message, StringComparison.Ordinal);
        Assert.Contains(
            "R3e",
            Assert.Throws<InvalidOperationException>(() => plan.Render(Spec(table, point) with { Coord = Coord.Polar() })).Message,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "stored column",
            Assert.Throws<ArgumentException>(() => plan.Render(Spec(table, point with { X = table.Col<double>("u") }))).Message,
            StringComparison.Ordinal
        );
        var tooMany = Spec(table, point with { ColorBy = table.Cat("c65").ToColumnRef() }) with { ScaleColor = Scale.Ordinal() };
        Assert.Contains("R3d", Assert.Throws<InvalidOperationException>(() => plan.Render(tooMany)).Message, StringComparison.Ordinal);

        var parts = plan.Render(Spec(table, point with { ColorBy = table.Cat("c64").ToColumnRef() }) with { ScaleColor = Scale.Ordinal() });
        Assert.Equal(64, parts.ResidentRuns.ToArray().Select(r => r.Cells).Distinct().Count());
    }

    /// <summary>
    /// A point layer of more than 1e7 rows passes the compile's checks (design §4.8 "検査（行 R3b1）", §10 R3b1 row): row
    /// R3b1 draws any number of rows from the indexes, and the limit of row R3a is gone.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - One stored column of 10,000,001 floats, the layer's x and y (40 MB, so the class runs apart from the others).
    ///
    /// Steps:
    /// - Check the layer with the checks the compile runs on each layer before it makes a plan (LayerPlan.Validate), drawn
    ///   as each value of Enum.GetValues of PointDraw. A render is not needed for the check, and it would build an index
    ///   of 1e7 points.
    ///
    /// Expected:
    /// - No check throws. Row R3a threw InvalidOperationException here.
    /// </remarks>
    [Fact]
    public void Validate_MoreThan1e7Rows_Passes()
    {
        using var huge = Column.Wrap(new float[10_000_001]);
        var table = DataTable.Create().Add("v", huge).Build();
        var v = table.Col<float>("v");
        var buffer = new ColumnRef[4];
        foreach (var draw in Enum.GetValues<PointDraw>())
        {
            LayerPlan.Validate(table, new PointLayerSpec { X = v, Y = v, Draw = draw }, 0, buffer);
        }
    }

    /// <summary>
    /// A frame that does not wait draws nothing of the layer until its index is built, and then sends at most 4 MB of pages a
    /// frame; the frame is provisional until every page it draws is there (design §5.5.2 "索引ができるまで", "常駐").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A point layer of 400,000 seeded rows (7 pages) in an engine that does not wait, on the null backend.
    ///
    /// Steps:
    /// - Render frames, saying before each that the last frame's uploads were sent, until a frame is not provisional.
    ///
    /// Expected:
    /// - The first frame is provisional and records no run.
    /// - The first frame that records runs sends the 5 whole pages and the short last page that fit in 4 MB, and is
    ///   provisional; the next sends the sixth whole page.
    /// - The last frame is not provisional; 7 pages are in use, and its 7 runs are the 7 pages, each whole and once.
    ///
    /// Steps:
    /// - Zoom to [50, 50.5] × [50, 50.5] and render.
    ///
    /// Expected:
    /// - Fewer runs than 7, and each is still a whole page.
    /// </remarks>
    [Fact]
    public void Frames_DrawNothingUntilTheIndexAndSendPagesByTheBudget()
    {
        const int Count = 400_000;
        var random = new Random(23);
        using var xs = Column.Wrap(Enumerable.Range(0, Count).Select(_ => random.NextDouble() * 100).ToArray());
        using var ys = Column.Wrap(Enumerable.Range(0, Count).Select(_ => random.NextDouble() * 100).ToArray());
        var table = DataTable.Create().Add("x", xs).Add("y", ys).Build();
        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, 640, 360);
        engine.SetPlot(
            Plot.Create(table)
                .Point(table.Col<double>("x"), table.Col<double>("y"))
                .ScaleX(Scale.Linear().WithDomain(0, 100))
                .ScaleY(Scale.Linear().WithDomain(0, 100))
                .Build()
        );
        var pages = backend.Images.Points;

        engine.RenderFrame();
        Assert.True(engine.IsProvisional);
        Assert.Equal(0, backend.LastList!.ResidentRuns.Length);

        var sent = new List<long>();
        var clock = Stopwatch.StartNew();
        while (engine.IsProvisional)
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), "The index was not built in 60 s.");
            backend.Images.Sent();
            engine.RenderFrame();
            if (pages.BytesThisFrame > 0)
            {
                sent.Add(pages.BytesThisFrame);
                Assert.True(pages.BytesThisFrame <= ResidencyCache.FrameUpload);
            }
            else if (sent.Count == 0)
            {
                Thread.Sleep(2);
            }
        }

        // Five whole pages and the last page's 6,784 points fit in 4 MB; the sixth whole page waits for the next frame.
        Assert.Equal(
            [
                (5L * PointPages.PagePoints * PointPages.PointBytes) + ((Count - (6L * PointPages.PagePoints)) * PointPages.PointBytes),
                (long)PointPages.PagePoints * PointPages.PointBytes,
            ],
            sent
        );
        Assert.Equal(7, pages.PagesInUse);
        var runs = backend.LastList!.ResidentRuns.ToArray();
        Assert.Equal(7, runs.Select(r => r.Slot).Distinct().Count());
        Assert.Equal(
            [
                PointPages.PagePoints,
                PointPages.PagePoints,
                PointPages.PagePoints,
                PointPages.PagePoints,
                PointPages.PagePoints,
                PointPages.PagePoints,
                Count - (6 * PointPages.PagePoints),
            ],
            runs.Select(r => r.Count).OrderDescending()
        );

        engine.Views.SetView(Panel, new PanelView2D(ViewRange.FromValues(50, 50.5), ViewRange.FromValues(50, 50.5)));
        engine.RenderFrame();
        var zoomed = backend.LastList!.ResidentRuns.ToArray();
        Assert.InRange(zoomed.Length, 1, 6);
        Assert.Equal(zoomed.Length, zoomed.Select(r => r.Slot).Distinct().Count());
        Assert.All(zoomed, r => Assert.Contains(runs, whole => whole.Slot == r.Slot && whole.Count == r.Count));
    }

    /// <summary>
    /// A layer split by a categorical color stays provisional until the pages of every part are sent (design §5.5.2
    /// "索引ができるまでと、失敗"): a part that waits for its pages keeps the frame provisional as a layer of its own does.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A point layer of 600,000 seeded rows colored by a column of 3 categories, 200,000 rows each: 4 pages a part, 12
    ///   pages of 7,200,000 bytes, more than a frame sends. An engine that does not wait, on the null backend.
    ///
    /// Steps:
    /// - Render frames, saying before each that the last frame's uploads were sent, until a frame is not provisional.
    ///
    /// Expected:
    /// - The first frame that is not provisional has 12 pages in use, and its runs hold the 600,000 points.
    /// - Pages were sent in at least 2 frames, each within 4 MB.
    /// </remarks>
    [Fact]
    public void SplitFrames_StayProvisionalUntilEveryPartsPagesAreSent()
    {
        const int Count = 600_000;
        var random = new Random(29);
        using var xs = Column.Wrap(Enumerable.Range(0, Count).Select(_ => random.NextDouble() * 100).ToArray());
        using var ys = Column.Wrap(Enumerable.Range(0, Count).Select(_ => random.NextDouble() * 100).ToArray());
        var table = DataTable
            .Create()
            .Add("x", xs)
            .Add("y", ys)
            .Add("c", Column.Categorical(Enumerable.Range(0, Count).Select(i => (string?)Three[i % 3]).ToArray()))
            .Build();
        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, 640, 360);
        engine.SetPlot(
            Plot.Create(table)
                .Point(table.Col<double>("x"), table.Col<double>("y"), p => p.ColorBy(table.Cat("c")))
                .ScaleX(Scale.Linear().WithDomain(0, 100))
                .ScaleY(Scale.Linear().WithDomain(0, 100))
                .Build()
        );
        var pages = backend.Images.Points;
        engine.RenderFrame();
        var sending = 0;
        var clock = Stopwatch.StartNew();
        while (engine.IsProvisional)
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), "The indexes were not built in 60 s.");
            backend.Images.Sent();
            engine.RenderFrame();
            Assert.True(pages.BytesThisFrame <= ResidencyCache.FrameUpload);
            if (pages.BytesThisFrame > 0)
            {
                sending++;
            }
            else
            {
                Thread.Sleep(2);
            }
        }

        Assert.Equal(12, pages.PagesInUse);
        Assert.Equal(Count, backend.LastList!.ResidentRuns.ToArray().Sum(r => r.Count));
        Assert.True(sending >= 2);
    }

    /// <summary>
    /// A build that fails fails the layer: the engine draws nothing of it and says so once, without staying provisional, and
    /// a render that waits throws the build's exception (design §5.5.2 "構築の失敗").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A point layer of 131,073 rows on an x scale whose transform throws on any thread but the test's: the plan's own
    ///   calls pass, and the workers' calls of the build throw.
    ///
    /// Steps:
    /// - In an engine that does not wait, render frames until the failure is told. Render the spec on a plan that waits.
    ///
    /// Expected:
    /// - One WorkFailed; the frame after it is not provisional and records no run.
    /// - The waiting render throws the transform's InvalidOperationException.
    /// </remarks>
    [Fact]
    public void FailedBuild_FailsTheLayer()
    {
        using var values = Column.Wrap(Enumerable.Range(0, Over).Select(i => 1.0 + (i % 100)).ToArray());
        var table = DataTable.Create().Add("v", values).Build();
        var v = table.Col<double>("v");
        var spec = Plot.Create(table)
            .Point(v, v)
            .ScaleX(Scale.Transform(new ThrowsOffThread(Environment.CurrentManagedThreadId)).WithDomain(0, 200))
            .ScaleY(Scale.Linear().WithDomain(0, 200))
            .Build();
        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, 640, 360);
        var failures = 0;
        engine.WorkFailed += (in WorkFailure _) => failures++;
        engine.SetPlot(spec);
        var clock = Stopwatch.StartNew();
        while (failures == 0)
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), "The build did not fail in 60 s.");
            engine.RenderFrame();
            Thread.Sleep(2);
        }

        engine.RenderFrame();
        Assert.Equal(1, failures);
        Assert.False(engine.IsProvisional);
        Assert.Equal(0, backend.LastList!.ResidentRuns.Length);

        using var pool = new PlotWorkerPool(2);
        using var plan = TestPlans.Create(pool, 640, 360);
        Assert.Equal("The build's transform.", Assert.Throws<InvalidOperationException>(() => plan.Render(spec)).Message);
    }

    /// <summary>
    /// When a column's values change, the layer draws its last snapshot, provisional, until the new index is built, and then
    /// draws the new one (design §5.5.2 "索引ができるまで").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A point layer of 131,073 rows in an engine that does not wait, drawn until it is not provisional.
    ///
    /// Steps:
    /// - Invalidate the x column; render a frame; render frames until one is not provisional.
    ///
    /// Expected:
    /// - The frame after the change is provisional and still records the 131,073 points; the last frame is not provisional
    ///   and records them too.
    /// </remarks>
    [Fact]
    public void ChangedColumn_DrawsTheLastSnapshotUntilTheNext()
    {
        using var xs = Column.Wrap(Enumerable.Range(0, Over).Select(i => (double)(i % 100)).ToArray());
        using var ys = Column.Wrap(Enumerable.Range(0, Over).Select(i => (double)(i % 97)).ToArray());
        var table = DataTable.Create().Add("x", xs).Add("y", ys).Build();
        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, 640, 360);
        engine.SetPlot(
            Plot.Create(table)
                .Point(table.Col<double>("x"), table.Col<double>("y"))
                .ScaleX(Scale.Linear().WithDomain(0, 100))
                .ScaleY(Scale.Linear().WithDomain(0, 100))
                .Build()
        );
        Settle();

        xs.Invalidate();
        backend.Images.Sent();
        engine.RenderFrame();
        Assert.True(engine.IsProvisional);
        Assert.Equal(Over, backend.LastList!.ResidentRuns.ToArray().Sum(r => r.Count));

        Settle();
        Assert.Equal(Over, backend.LastList!.ResidentRuns.ToArray().Sum(r => r.Count));

        void Settle()
        {
            var clock = Stopwatch.StartNew();
            var provisional = true;
            while (provisional)
            {
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), "The frames stayed provisional for 60 s.");
                backend.Images.Sent();
                engine.RenderFrame();
                Thread.Sleep(1);
                provisional = engine.IsProvisional;
            }
        }
    }

    /// <summary>Frames of a layer on the index path allocate nothing once its pages are there (invariant 3).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A point layer of 200,000 rows split by three categories, drawn until it is not provisional; five views, each drawn once.
    ///
    /// Steps:
    /// - Draw the five views again, counting the thread's allocations.
    ///
    /// Expected:
    /// - 0 bytes.
    /// </remarks>
    [Fact]
    public void Frames_AllocateNothing()
    {
        const int Count = 200_000;
        var random = new Random(29);
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
        engine.SetPlot(
            Plot.Create(table)
                .Point(table.Col<double>("x"), table.Col<double>("y"), p => p.ColorBy(table.Cat("c")))
                .ScaleX(Scale.Linear().WithDomain(0, 100))
                .ScaleY(Scale.Linear().WithDomain(0, 100))
                .Build()
        );
        engine.RenderFrame();
        var views = Enumerable.Range(0, 5).Select(i => new PanelView2D(ViewRange.FromValues(10 + i, 90), ViewRange.FromValues(10, 90 - i))).ToArray();
        for (var round = 0; round < 2; round++)
        {
            foreach (var view in views)
            {
                Draw(view);
            }
        }

        Assert.False(engine.IsProvisional);
        var before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var view in views)
        {
            Draw(view);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(backend.LastList!.ResidentRuns.Length >= 3);

        void Draw(PanelView2D view)
        {
            backend.Images.Sent();
            engine.Views.SetView(Panel, view);
            engine.RenderFrame();
        }
    }

    /// <summary>A transform that is the identity on one thread and throws on every other: the workers' calls fail.</summary>
    private sealed class ThrowsOffThread(int thread) : IScale
    {
        public void Forward(ReadOnlySpan<double> values, Span<double> destination)
        {
            if (Environment.CurrentManagedThreadId != thread)
            {
                throw new InvalidOperationException("The build's transform.");
            }

            values.CopyTo(destination);
        }

        public void Inverse(ReadOnlySpan<double> values, Span<double> destination) => values.CopyTo(destination);
    }
}
