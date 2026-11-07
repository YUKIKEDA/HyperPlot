using HyperPlot.Interaction;

namespace HyperPlot.Tests;

/// <summary>
/// The engine's frames that do not wait (design §4.8 "`PlotEngine` の待たない描き方", §7.1, §10 "暫定の表示（W1b）", row W1b1):
/// what a frame draws while the workers build, that it is provisional and asks for the next, and how a layer's failed work is
/// told and tried again. The workers are held with <see cref="TestStorage{T}"/>; they take no frame job, so that M4's reads of
/// a held chunk's ends, a frame's own work that it may wait for (design §8.1), stay on the test's thread.
/// </summary>
public sealed class ProvisionalFrameTests
{
    private const int Chunk = Column.ChunkLength;
    private static readonly PanelId Panel = new(0, 0);

    /// <summary>
    /// A frame over a pyramid still building draws M4's provisional result, learns from the chunks with statistics, is
    /// provisional, and asks for the next; once the pyramid is built the full view widens to the whole column, a view the
    /// user moved stays, and the frames stop asking (design §4.8).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread; a line over a uniform x and a Y of two chunks, values in [−1, 1] in chunk 0 and [−10, 10] in
    ///   chunk 1, chunk 1 held on the worker.
    ///
    /// Steps:
    /// - Draw until the worker reads chunk 1; draw. Once with the full view, once with a view of x [0, 100] and y [−5, 5]
    ///   set: release chunk 1 and draw until a frame is not provisional; draw once more.
    ///
    /// Expected:
    /// - Held: the frame returns, is provisional, a line is drawn whose last extraction is provisional, the y view lies
    ///   within [−2, 2], ProvisionalChanged came once, and the frame asked for the next. Released: ProvisionalChanged came
    ///   a second time; the full view's y reaches past ±9, and the set view is unchanged; the frame after asks for nothing.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pyramid_Building_DrawsProvisionalAndLearnsAsItFills(bool zoomed)
    {
        using var pool = new PlotWorkerPool(1) { WorkersTakeJobs = false };
        using var y = TestStorage<float>.MakeColumn(TwoChunks(), out var storage);
        storage.Hold(1);
        var table = DataTable.Create().Add("x", Column.Uniform(0, 1, 2 * Chunk)).Add("y", y).Build();
        using var engine = new PlotEngine(new NullBackend(), 640, 360, 1, pool);
        var (requests, changes) = (0, 0);
        engine.FrameRequested += () => requests++;
        engine.ProvisionalChanged += () => changes++;
        engine.SetPlot(Plot.Create(table).Line(table.Col<double>("x"), table.Col<float>("y")).Build());

        engine.RenderFrame();
        storage.WaitForWorker(1);
        var asked = requests;
        engine.RenderFrame();

        Assert.True(engine.IsProvisional);
        Assert.Equal(1, changes);
        Assert.Equal(asked + 1, requests);
        Assert.NotEmpty(engine.Plan!.List.Polylines.ToArray());
        Assert.True(engine.Plan.Panels[0].Batches[0].GetResult(0).IsProvisional);
        var held = View(engine);
        Assert.InRange(held.Y.LowValue, -2, 2);
        Assert.InRange(held.Y.HighValue, -2, 2);

        var set = new PanelView2D(ViewRange.FromValues(0.0, 100.0), ViewRange.FromValues(-5.0, 5.0));
        if (zoomed)
        {
            engine.Views.SetView(Panel, set);
            engine.RenderFrame();
        }

        storage.Release(1);
        Eventually.True(
            () =>
            {
                engine.RenderFrame();
                return !engine.IsProvisional;
            },
            "a frame that is not provisional"
        );

        Assert.Equal(2, changes);
        var done = View(engine);
        if (zoomed)
        {
            Assert.Equal((0.0, 100.0, -5.0, 5.0), (done.X.LowValue, done.X.HighValue, done.Y.LowValue, done.Y.HighValue));
        }
        else
        {
            Assert.True(done.Y.LowValue < -9 && done.Y.HighValue > 9, $"{done.Y}");
        }

        asked = requests;
        engine.RenderFrame();
        Assert.Equal(asked, requests);
        Assert.False(engine.IsProvisional);
    }

    /// <summary>
    /// A layer whose X has no statistics yet draws nothing until the workers compute them, and draws in the frame after
    /// (design §4.8: "統計のまだない `Column&lt;T&gt;` の X").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread; a line over an increasing X column of one chunk, held on the worker, and a Y of the same length.
    ///
    /// Steps:
    /// - Draw; release the chunk; draw until a frame is not provisional.
    ///
    /// Expected:
    /// - Held: the layer is pending and not drawn, its series is left out of the extraction (no point), and the frame is
    ///   provisional. Released: the layer is drawn.
    /// </remarks>
    [Fact]
    public void XWithoutStatistics_DrawsOnceTheyAreThere()
    {
        const int Length = 1000;
        using var pool = new PlotWorkerPool(1) { WorkersTakeJobs = false };
        using var x = TestStorage<double>.MakeColumn([.. Enumerable.Range(0, Length).Select(i => (double)i)], out var storage);
        using var y = Column.Wrap(Enumerable.Range(0, Length).Select(i => MathF.Sin(i * 0.02f)).ToArray());
        storage.Hold(0);
        var table = DataTable.Create().Add("x", x).Add("y", y).Build();
        using var engine = new PlotEngine(new NullBackend(), 640, 360, 1, pool);
        engine.SetPlot(Plot.Create(table).Line(table.Col<double>("x"), table.Col<float>("y")).Build());

        engine.RenderFrame();
        Assert.Equal(LayerReadiness.Pending, engine.Plan!.Layers[0].Readiness);
        Assert.Equal(0, engine.Plan.LayersRecorded);
        Assert.Equal(0, engine.Plan.Panels[0].Batches[0].GetResult(0).Count);
        Assert.True(engine.IsProvisional);

        storage.Release(0);
        Settle(engine);
        Assert.Equal(1, engine.Plan.LayersRecorded);
    }

    /// <summary>
    /// A layer drawn from the workers' results over categories draws nothing until they are there and draws in the frame
    /// after; after an Invalidate it draws the older results while the new ones are made (design §4.8, Issue #291 Decision 4).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread; 4 categories; a Y column of one chunk held on the worker. Three plots: bars, error bars, and
    ///   bars stacked by a color column, over the categories.
    ///
    /// Steps:
    /// - For each: draw; release; draw until not provisional; hold, double the values, Invalidate, draw; release; draw until
    ///   not provisional.
    ///
    /// Expected:
    /// - Held at first: the layer is pending and not drawn, and the frame is provisional. Once done: it is drawn. After the
    ///   Invalidate: the layer draws its older results, as many elements as before, the y view is the one before (the older
    ///   statistics stand in), and the frame is provisional. At the end the layer is ready.
    /// </remarks>
    [Fact]
    public void CategoryResults_DrawOnceThereAndTheOlderWhileMade()
    {
        const int Length = 1000;
        var names = new[] { "a", "b", "c", "d" };
        var codes = Enumerable.Range(0, Length).Select(i => names[i % 4]).ToArray();
        var groups = Enumerable.Range(0, Length).Select(i => i % 2 == 0 ? "p" : "q").ToArray();
        Func<DataTable, PlotSpec>[] plots =
        [
            t => Plot.Create(t).Bar(t.Cat("c"), t.Col<double>("y")).ScaleX(Scale.Band()).Build(),
            t => Plot.Create(t).ErrorBar(t.Cat("c"), t.Col<double>("y"), t.Col<double>("y")).ScaleX(Scale.Band()).Build(),
            t =>
                Plot.Create(t)
                    .Bar(t.Cat("c"), t.Col<double>("y"), configure: b => b.ColorBy(t.Cat("g")).Position(Position.Stack))
                    .ScaleX(Scale.Band())
                    .Build(),
        ];
        foreach (var make in plots)
        {
            using var pool = new PlotWorkerPool(1) { WorkersTakeJobs = false };
            using var y = TestStorage<double>.MakeColumn([.. Enumerable.Range(0, Length).Select(i => 1.0 + (i % 7))], out var storage);
            storage.Hold(0);
            var table = DataTable
                .Create()
                .Add("c", Column.Categorical(codes, names))
                .Add("g", Column.Categorical(groups, ["p", "q"]))
                .Add("y", y)
                .Build();
            using var engine = new PlotEngine(new NullBackend(), 640, 360, 1, pool);
            engine.SetPlot(make(table));

            engine.RenderFrame();
            Assert.Equal(LayerReadiness.Pending, engine.Plan!.Layers[0].Readiness);
            Assert.Equal(0, engine.Plan.LayersRecorded);
            Assert.True(engine.IsProvisional);

            storage.Release(0);
            Settle(engine);
            Assert.Equal(1, engine.Plan.LayersRecorded);
            var (done, view) = (Elements(engine), View(engine).Y);

            storage.Hold(0);
            for (var i = 0; i < Length; i++)
            {
                storage.Values[i] *= 2;
            }

            y.Invalidate();
            engine.RenderFrame();
            Assert.Equal(LayerReadiness.Previous, engine.Plan.Layers[0].Readiness);
            Assert.True(engine.IsProvisional);
            Assert.Equal(done, Elements(engine));
            Assert.Equal(view, View(engine).Y);

            storage.Release(0);
            Settle(engine);
            Assert.Equal(LayerReadiness.Ready, engine.Plan.Layers[0].Readiness);
        }
    }

    /// <summary>
    /// A layer's failed work does not throw from the frame (design §4.8, Issue #291 Decision 2): the layer is not drawn,
    /// WorkFailed tells it once with the plot, the layer, and the exception, a frame with only it left is not provisional,
    /// and after its column is invalidated it is made again and drawn.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread. Plots of two layers of a kind, layer 0 over healthy columns, layer 1 over a column that fails:
    ///   a pyramid's chunk (line), the X statistics (line over an X column), the extremes over categories (bars), the sums
    ///   of a stack (stacked bars), and an X that decreases (line; the data's error).
    ///
    /// Steps:
    /// - For each: draw until WorkFailed came; draw until not provisional, and twice more. Make the column read again (stop
    ///   the failure, or make the X increase), Invalidate it, and draw until not provisional.
    ///
    /// Expected:
    /// - Failed: one WorkFailed, plot 0 and layer 1, with the exception of its kind (IOException, or
    ///   InvalidOperationException for the X that decreases); the frames throw nothing; layer 0 is ready; a frame is not
    ///   provisional though layer 1 is not drawn (one layer recorded); no second WorkFailed. Tried again: still one
    ///   WorkFailed, and layer 1 is ready and drawn (two layers recorded).
    /// </remarks>
    [Fact]
    public void Failures_AreToldOnceAndTriedAgain()
    {
        const int Length = 1000;
        var names = new[] { "a", "b", "c", "d" };
        var codes = Enumerable.Range(0, Length).Select(i => names[i % 4]).ToArray();
        var cases = new (string Name, Func<DataTable, PlotSpec> Make, bool Decreasing, Type Error)[]
        {
            ("pyramid", t => Lines(t, "x", "x"), false, typeof(IOException)),
            ("x statistics", t => Lines(t, "u", "bad"), false, typeof(IOException)),
            (
                "category extremes",
                t => Plot.Create(t).Bar(t.Cat("c"), t.Col<double>("y")).Bar(t.Cat("c"), t.Col<double>("bad")).ScaleX(Scale.Band()).Build(),
                false,
                typeof(IOException)
            ),
            (
                "stack sums",
                t =>
                    Plot.Create(t)
                        .Bar(t.Cat("c"), t.Col<double>("y"))
                        .Bar(t.Cat("c"), t.Col<double>("bad"), configure: b => b.ColorBy(t.Cat("c")).Position(Position.Stack))
                        .ScaleX(Scale.Band())
                        .Build(),
                false,
                typeof(IOException)
            ),
            ("decreasing x", t => Lines(t, "u", "bad"), true, typeof(InvalidOperationException)),
        };
        foreach (var (name, make, decreasing, error) in cases)
        {
            using var pool = new PlotWorkerPool(1) { WorkersTakeJobs = false };
            var values = Enumerable.Range(0, Length).Select(i => decreasing ? (double)(Length - i) : i).ToArray();
            using var bad = TestStorage<double>.MakeColumn(values, out var storage);
            using var y = Column.Wrap(Enumerable.Range(0, Length).Select(i => 1.0 + (i % 7)).ToArray());
            storage.FailOn(0, fail: !decreasing);
            var table = DataTable
                .Create()
                .Add("u", Column.Uniform(0, 1, Length))
                .Add("c", Column.Categorical(codes, names))
                .Add("y", y)
                .Add("bad", bad)
                .Build();
            using var engine = new PlotEngine(new NullBackend(), 640, 360, 1, pool);
            var failures = new List<WorkFailure>();
            engine.WorkFailed += (in WorkFailure failure) => failures.Add(failure);
            engine.SetPlot(make(table));

            Eventually.True(
                () =>
                {
                    engine.RenderFrame();
                    return failures.Count > 0;
                },
                $"the failure of the {name}"
            );
            Settle(engine);
            engine.RenderFrame();
            engine.RenderFrame();

            var failure = Assert.Single(failures);
            Assert.Equal((0, 1), (failure.Plot, failure.Layer));
            Assert.IsType(error, failure.Exception);
            Assert.Equal(LayerReadiness.Ready, engine.Plan!.Layers[0].Readiness);
            Assert.False(engine.IsProvisional, name);
            Assert.Equal(1, engine.Plan.LayersRecorded);

            storage.FailOn(0, fail: false);
            for (var i = 0; i < Length; i++)
            {
                storage.Values[i] = i;
            }

            bad.Invalidate();
            Settle(engine);
            Assert.Single(failures);
            Assert.Equal(LayerReadiness.Ready, engine.Plan.Layers[1].Readiness);
            Assert.Equal(2, engine.Plan.LayersRecorded);
        }

        static PlotSpec Lines(DataTable t, string healthyX, string failingX) =>
            failingX == "bad"
                ? Plot.Create(t).Line(t.Col<double>("u"), t.Col<double>("y")).Line(t.Col<double>("bad"), t.Col<double>("y")).Build()
                : Plot.Create(t).Line(t.Col<double>("u"), t.Col<double>("y")).Line(t.Col<double>("u"), t.Col<double>("bad")).Build();
    }

    /// <summary>A failure in a figure names the plot it is in (design §7.1).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread; a figure of two plots side by side, the second's Y failing on the worker.
    ///
    /// Steps:
    /// - Draw until WorkFailed came.
    ///
    /// Expected:
    /// - One failure, plot 1 and layer 0.
    /// </remarks>
    [Fact]
    public void Failure_InAFigure_NamesThePlot()
    {
        using var pool = new PlotWorkerPool(1) { WorkersTakeJobs = false };
        using var good = Column.Wrap(Enumerable.Range(0, 100).Select(i => (double)i).ToArray());
        using var bad = TestStorage<double>.MakeColumn([.. Enumerable.Range(0, 100).Select(i => (double)i)], out var storage);
        storage.FailOn(0, fail: true);
        var table = DataTable.Create().Add("x", Column.Uniform(0, 1, 100)).Add("good", good).Add("bad", bad).Build();
        using var engine = new PlotEngine(new NullBackend(), 640, 360, 1, pool);
        var failures = new List<WorkFailure>();
        engine.WorkFailed += (in WorkFailure failure) => failures.Add(failure);
        engine.SetFigure(
            Figure.HConcat(
                Plot.Create(table).Line(table.Col<double>("x"), table.Col<double>("good")).Build(),
                Plot.Create(table).Line(table.Col<double>("x"), table.Col<double>("bad")).Build()
            )
        );

        Eventually.True(
            () =>
            {
                engine.RenderFrame();
                return failures.Count > 0;
            },
            "the failure"
        );

        var failure = Assert.Single(failures);
        Assert.Equal((1, 0), (failure.Plot, failure.Layer));
    }

    /// <summary>A provisional frame allocates nothing once warm (invariant 3, design §4.8).</summary>
    /// <remarks>
    /// Preconditions:
    /// - The scene of <see cref="Pyramid_Building_DrawsProvisionalAndLearnsAsItFills"/>, chunk 1 held, drawn until the worker
    ///   reads it, and 10 frames more.
    ///
    /// Steps:
    /// - Count the bytes this thread allocates over 20 frames, each zooming the view by a pixel.
    ///
    /// Expected:
    /// - 0 bytes, and each frame is provisional.
    /// </remarks>
    [Fact]
    public void ProvisionalFrames_AllocateNothingOnceWarm()
    {
        using var pool = new PlotWorkerPool(1) { WorkersTakeJobs = false };
        using var y = TestStorage<float>.MakeColumn(TwoChunks(), out var storage);
        storage.Hold(1);
        var table = DataTable.Create().Add("x", Column.Uniform(0, 1, 2 * Chunk)).Add("y", y).Build();
        using var engine = new PlotEngine(new NullBackend(), 640, 360, 1, pool);
        engine.Options.ZoomAnimation = TimeSpan.Zero;
        engine.SetPlot(Plot.Create(table).Line(table.Col<double>("x"), table.Col<float>("y")).Build());
        engine.RenderFrame();
        storage.WaitForWorker(1);
        for (var i = 0; i < 10; i++)
        {
            Frame(engine);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var provisional = true;
        for (var i = 0; i < 20; i++)
        {
            Frame(engine);
            provisional &= engine.IsProvisional;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(provisional);
        Assert.Equal(0, allocated);

        static void Frame(PlotEngine engine)
        {
            engine.Views.ZoomAt(Panel, new System.Numerics.Vector2(320, 180), 1.001, animate: false);
            engine.RenderFrame();
        }
    }

    /// <summary>Values of two chunks: chunk 0 in [−1, 1], chunk 1 in [−10, 10].</summary>
    private static float[] TwoChunks()
    {
        var values = new float[2 * Chunk];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = MathF.Sin(i * 0.001f) * (i < Chunk ? 1 : 10);
        }

        return values;
    }

    /// <summary>Draws until a frame is not provisional, within the watchdog.</summary>
    private static void Settle(PlotEngine engine) =>
        Eventually.True(
            () =>
            {
                engine.RenderFrame();
                return !engine.IsProvisional;
            },
            "a frame that is not provisional"
        );

    private static PanelView2D View(PlotEngine engine) => (PanelView2D)engine.CurrentView(Panel);

    /// <summary>Returns the elements the last frame recorded: rectangles and polylines.</summary>
    private static int Elements(PlotEngine engine) => engine.Plan!.List.Rects.Length + engine.Plan.List.Polylines.Length;
}
