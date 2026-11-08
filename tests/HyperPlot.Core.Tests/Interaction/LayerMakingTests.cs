using HyperPlot.Interaction;

namespace HyperPlot.Tests;

/// <summary>
/// The layers the engine makes off the frame (design §4.8 "層を作る段", §10 "層を作る段（W1b2）", row W1b2, Issue #297): a
/// stat's derived table, a stack over a numeric x, a split's gather, and a facet's panels. The frames do not wait for them;
/// the workers are held with <see cref="TestStorage{T}"/>.
/// </summary>
public sealed class LayerMakingTests
{
    private const int Length = 1000;

    /// <summary>The kinds of layers made off the frame.</summary>
    public static TheoryData<string> Kinds =>
        ["bin", "density", "smooth", "bin 2D", "count", "summary", "quantiles", "numeric stack", "split", "facet"];

    /// <summary>
    /// The first frame of a layer made off the frame returns without waiting and draws none of the plot's layers; once the
    /// making ends, the frames draw what a frame that waits draws of the same plot (design §4.8, Issue #297 Decisions 2, 6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread, held on another column's chunk, so that every worker job waits behind it. A table of a numeric
    ///   x and y, each over a storage that counts the reads on the test's thread, and two categorical columns. A plot of the
    ///   kind: a bin, a density, a smooth, a 2D bin, a count, a mean, quantiles, an area stacked over the numeric x by a
    ///   category, a line split by a category, a line in a facet.
    ///
    /// Steps:
    /// - Draw; release the worker; draw until a frame is not provisional. Draw the plot with an engine that waits.
    ///
    /// Expected:
    /// - Held: the frame returns, no layer is recorded (a facet: the background and the title only), and it is provisional.
    ///   Released: as many elements as the waiting engine's first frame draws. The test's thread never read the columns.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Kinds))]
    public void FirstBuild_DoesNotWaitAndDrawsOnceMade(string kind)
    {
        using var pool = new PlotWorkerPool(1);
        using var data = new Data();
        var spec = Make(kind, data.Table);
        using var engine = new PlotEngine(new NullBackend(), 640, 360, 1, pool);
        using (var blocker = new Blocker(pool))
        {
            engine.SetPlot(spec);
            engine.RenderFrame();
            Assert.Equal(0, engine.Plan!.LayersRecorded);
            Assert.True(engine.IsProvisional);
        }

        Settle(engine);
        Assert.True(engine.Plan.LayersRecorded > 0);
        Assert.Equal((0, 0), (data.X.WatchedReads, data.Y.WatchedReads));
        using var waiting = new PlotEngine(new NullBackend(), 640, 360, 1, pool) { WaitsForIndexes = true };
        waiting.SetPlot(spec);
        waiting.RenderFrame();
        Assert.Equal(Elements(waiting), Elements(engine));
    }

    /// <summary>
    /// After a column of one layer moves, the layer drawn stays until the new one is made, and the other layers are not made
    /// again (design §4.8, Issue #297 Decisions 3, 7).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread; layer 0 a bin over x, layer 1 a bin over y, drawn until not provisional.
    ///
    /// Steps:
    /// - Hold the x's chunk on the worker, double its values, Invalidate it, draw. Release; draw until not provisional.
    ///
    /// Expected:
    /// - Held: the frame is provisional, layer 0 is the one drawn before, and the frame records as many elements. Released:
    ///   layer 0 is a new plan, layer 1 the same as at first, and the elements are the ones of an engine that waits.
    /// </remarks>
    [Fact]
    public void Remake_KeepsTheLayerDrawnAndTheOthers()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = new Data();
        var t = data.Table;
        var spec = Spec(
            t,
            Scale.Linear(),
            new BarLayerSpec { X = t.Col<double>("x"), Stat = Stat.Bin() },
            new BarLayerSpec { X = t.Col<double>("y"), Stat = Stat.Bin() }
        );
        using var engine = new PlotEngine(new NullBackend(), 640, 360, 1, pool);
        engine.SetPlot(spec);
        Settle(engine);
        var (first0, first1, elements) = (engine.Plan!.Layers[0], engine.Plan.Layers[1], Elements(engine));

        data.X.Hold(0);
        for (var i = 0; i < Length; i++)
        {
            data.X.Values[i] *= 2;
        }

        data.XColumn.Invalidate();
        engine.RenderFrame();
        Assert.True(engine.IsProvisional);
        Assert.Same(first0, engine.Plan.Layers[0]);
        Assert.Equal(elements, Elements(engine));

        data.X.Release(0);
        Settle(engine);
        Assert.Same(first1, engine.Plan.Layers[1]);
        Assert.NotSame(first0, engine.Plan.Layers[0]);
        using var waiting = new PlotEngine(new NullBackend(), 640, 360, 1, pool) { WaitsForIndexes = true };
        waiting.SetPlot(spec);
        waiting.RenderFrame();
        Assert.Equal(Elements(waiting), Elements(engine));
    }

    /// <summary>
    /// While a split's series or a facet's panels gather their columns' newer rows, the frames draw the older ones and are
    /// provisional (design §4.8, Issue #297 Decision 4).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread; a line split by a category, and a line in a facet by a category, drawn until not provisional.
    ///
    /// Steps:
    /// - For each: hold the y's chunk on the worker, double its values, Invalidate it, draw. Release; draw until not
    ///   provisional.
    ///
    /// Expected:
    /// - Held: the frame is provisional and records as many layers and elements as before. Released: the elements of an
    ///   engine that waits.
    /// </remarks>
    [Theory]
    [InlineData("split")]
    [InlineData("facet")]
    public void Regather_DrawsTheOlderRowsAndIsProvisional(string kind)
    {
        using var pool = new PlotWorkerPool(1);
        using var data = new Data();
        var spec = Make(kind, data.Table);
        using var engine = new PlotEngine(new NullBackend(), 640, 360, 1, pool);
        engine.SetPlot(spec);
        Settle(engine);
        var (layers, elements) = (engine.Plan!.LayersRecorded, Elements(engine));

        data.Y.Hold(0);
        for (var i = 0; i < Length; i++)
        {
            data.Y.Values[i] *= 2;
        }

        data.YColumn.Invalidate();
        engine.RenderFrame();
        Assert.True(engine.IsProvisional);
        Assert.Equal((layers, elements), (engine.Plan.LayersRecorded, Elements(engine)));

        data.Y.Release(0);
        Settle(engine);
        using var waiting = new PlotEngine(new NullBackend(), 640, 360, 1, pool) { WaitsForIndexes = true };
        waiting.SetPlot(spec);
        waiting.RenderFrame();
        Assert.Equal(Elements(waiting), Elements(engine));
    }

    /// <summary>
    /// A spec whose data stage changes draws no layer until its layers are made (design §4.8, Issue #297 Decision 2): the
    /// older layers read other columns.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread; a bin over x, drawn until not provisional.
    ///
    /// Steps:
    /// - Hold the worker; set a bin over y; draw. Release; draw until not provisional.
    ///
    /// Expected:
    /// - Held: no layer is recorded, and the frame is provisional. Released: one layer is recorded.
    /// </remarks>
    [Fact]
    public void DataStageChange_DrawsNoLayerUntilMade()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = new Data();
        var t = data.Table;
        using var engine = new PlotEngine(new NullBackend(), 640, 360, 1, pool);
        engine.SetPlot(Spec(t, Scale.Linear(), new BarLayerSpec { X = t.Col<double>("x"), Stat = Stat.Bin() }));
        Settle(engine);
        Assert.Equal(1, engine.Plan!.LayersRecorded);

        using (var blocker = new Blocker(pool))
        {
            engine.SetPlot(Spec(t, Scale.Linear(), new BarLayerSpec { X = t.Col<double>("y"), Stat = Stat.Bin() }));
            engine.RenderFrame();
            Assert.Equal(0, engine.Plan.LayersRecorded);
            Assert.True(engine.IsProvisional);
        }

        Settle(engine);
        Assert.Equal(1, engine.Plan.LayersRecorded);
    }

    /// <summary>
    /// A making's failure does not throw from the frame (design §4.8, Issue #297 Decisions 5, 11): WorkFailed tells it once a
    /// layer, the failed layer draws nothing, and it is made again when its columns move. A facet's panel limit fails every
    /// layer.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread. Plots of a healthy line (layer 0) and a failing layer 1 over a column "bad": a bin whose chunk
    ///   fails on the worker, a bin of more bins than a stat makes, an area stacked over an x that decreases; and the two
    ///   lines in a facet by a category of more panels than a plot has.
    ///
    /// Steps:
    /// - For each: draw until WorkFailed came; draw until not provisional, and twice more. Make the column right (stop the
    ///   failure, narrow the values, or make the x increase), Invalidate it, and draw until not provisional; for the facet,
    ///   Invalidate the y of its layers.
    ///
    /// Expected:
    /// - Failed: one WorkFailed of layer 1 with the exception of its kind (IOException, ArgumentException,
    ///   InvalidOperationException), or one of each layer with the facet's ArgumentException; the frames throw nothing; one
    ///   layer is recorded (none for the facet); no further WorkFailed. Made again: the same WorkFailed only, and two layers
    ///   are recorded; the facet is made again and fails again, once a layer, and draws none.
    /// </remarks>
    [Fact]
    public void Failures_AreToldOnceAndMadeAgain()
    {
        var cases = new (string Name, Func<DataTable, PlotSpec> Make, Type Error)[]
        {
            ("worker", t => Healthy(t, new BarLayerSpec { X = t.Col<double>("bad"), Stat = Stat.Bin() }), typeof(IOException)),
            ("bins", t => Healthy(t, new BarLayerSpec { X = t.Col<double>("bad"), Stat = Stat.Bin(step: 1e-3) }), typeof(ArgumentException)),
            (
                "stack",
                t =>
                    Healthy(
                        t,
                        new AreaLayerSpec
                        {
                            X = t.Col<double>("bad"),
                            Y = t.Col<double>("y"),
                            ColorBy = t.Cat("g").ToColumnRef(),
                            Position = Position.Stack,
                        }
                    ),
                typeof(InvalidOperationException)
            ),
            (
                "panels",
                t =>
                    Healthy(t, new LineLayerSpec { X = t.Col<double>("u"), Y = t.Col<double>("y") }) with
                    {
                        Facet = HyperPlot.Facet.Wrap(t.Cat("many")),
                    },
                typeof(ArgumentException)
            ),
        };
        foreach (var (name, make, error) in cases)
        {
            using var pool = new PlotWorkerPool(1);
            using var data = new Data();
            var bad = data.Bad;
            bad.FailOn(0, fail: name == "worker");
            if (name == "stack")
            {
                for (var i = 0; i < Length; i++)
                {
                    bad.Values[i] = Length - i;
                }
            }

            using var engine = new PlotEngine(new NullBackend(), 640, 360, 1, pool);
            var failures = new List<WorkFailure>();
            engine.WorkFailed += (in WorkFailure failure) => failures.Add(failure);
            engine.SetPlot(make(data.Table));
            Eventually.True(
                () =>
                {
                    engine.RenderFrame();
                    return failures.Count > 0;
                },
                $"the failure of the {name}"
            );
            Settle(engine, $"after the failure of the {name}");
            engine.RenderFrame();
            engine.RenderFrame();

            var facet = name == "panels";
            Assert.Equal(facet ? new[] { (0, 0), (0, 1) } : [(0, 1)], failures.Select(f => (f.Plot, f.Layer)));
            Assert.All(failures, f => Assert.IsType(error, f.Exception));
            Assert.Equal(facet ? 0 : 1, engine.Plan!.LayersRecorded);

            // The facet's category cannot change: a column of its layers moves, and the facet fails again.
            bad.FailOn(0, fail: false);
            for (var i = 0; i < Length; i++)
            {
                bad.Values[i] = name == "bins" ? i * 1e-7 : i;
            }

            (facet ? data.YColumn : data.BadColumn).Invalidate();
            Settle(engine, $"after the {name} is made again");
            Assert.Equal(facet ? 4 : 1, failures.Count);
            Assert.Equal(facet ? 0 : 2, engine.Plan.LayersRecorded);
        }

        static PlotSpec Healthy(DataTable t, LayerSpec layer) =>
            Spec(t, Scale.Linear(), new LineLayerSpec { X = t.Col<double>("u"), Y = t.Col<double>("y") }, layer);
    }

    /// <summary>
    /// A new layer stands beside the one drawn until its pyramids are built, not only its making (Issue #297 Decision 7): a
    /// layer over coarse pyramids would draw coarser than the one it replaces.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread; an area stacked over a numeric x by a category, drawn until not provisional.
    ///
    /// Steps:
    /// - Have the making hold the worker on another column's chunk before it reserves a pyramid, so that the pyramids of its
    ///   stacked columns are not built. Invalidate the y; draw until the new layer stands by. Compute the statistics the
    ///   frames asked for on a second pool, so that the new layer's Poll is ready and only its pyramids are missing; draw 5
    ///   frames more. Release the worker; draw until not provisional.
    ///
    /// Expected:
    /// - Held: the drawn layer is the one before, the new one still stands by, and the frames are provisional. Released: the
    ///   new layer is drawn.
    /// </remarks>
    [Fact]
    public void Standby_WaitsForItsPyramids()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = new Data();
        using var engine = new PlotEngine(new NullBackend(), 640, 360, 1, pool);
        Blocker? blocker = null;
        try
        {
            engine.SetPlot(Make("numeric stack", data.Table));
            Settle(engine);
            var old = engine.Plan!.Layers[0];

            engine.Plan.BeforeReserve = () => blocker ??= new Blocker(pool);
            data.YColumn.Invalidate();
            Eventually.True(
                () =>
                {
                    engine.RenderFrame();
                    return engine.Plan.StandsBy(0);
                },
                "the new layer to stand by"
            );
            using (var other = new PlotWorkerPool(1))
            {
                foreach (var column in engine.Plan.StatsRequested)
                {
                    if (column.PrecomputeStatsIfMissing(other) is { } task)
                    {
                        Eventually.Completes(task);
                    }
                }
            }

            for (var i = 0; i < 5; i++)
            {
                engine.RenderFrame();
                Assert.True(engine.IsProvisional);
            }

            Assert.Same(old, engine.Plan.Layers[0]);
            Assert.True(engine.Plan.StandsBy(0));

            blocker!.Dispose();
            Settle(engine);
            Assert.NotSame(old, engine.Plan.Layers[0]);
        }
        finally
        {
            blocker?.Dispose();
        }
    }

    /// <summary>
    /// A making given up gives back the pyramids it reserved once it ends (design §4.8, Issue #297 Decision 8): a new spec
    /// replaces the one being made.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread, held on another column's chunk; a line over y and a bin over x, so that the making reserves the
    ///   y's pyramid and then waits for the bin.
    ///
    /// Steps:
    /// - Draw until the store holds the y's pyramid; set a spec of a bin over x only, and draw; release the worker; draw
    ///   until not provisional, and wait until the store's makings are idle.
    ///
    /// Expected:
    /// - The store holds no pyramid: the bin reads none, and the given-up making gave back its reservation.
    /// </remarks>
    [Fact]
    public void Abandoned_GivesBackItsReservations()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = new Data();
        var t = data.Table;
        using var engine = new PlotEngine(new NullBackend(), 640, 360, 1, pool);
        using (var blocker = new Blocker(pool))
        {
            engine.SetPlot(
                Spec(
                    t,
                    Scale.Linear(),
                    new LineLayerSpec { X = t.Col<double>("x"), Y = t.Col<double>("y") },
                    new BarLayerSpec { X = t.Col<double>("x"), Stat = Stat.Bin() }
                )
            );
            Eventually.True(
                () =>
                {
                    engine.RenderFrame();
                    return engine.Plan!.PyramidsInStore == 1;
                },
                "the making to reserve the y's pyramid"
            );
            engine.SetPlot(Spec(t, Scale.Linear(), new BarLayerSpec { X = t.Col<double>("x"), Stat = Stat.Bin() }));
            engine.RenderFrame();
        }

        Settle(engine);
        Eventually.True(() => engine.Plan!.MakingsIdle, "the makings to end");
        Assert.Equal(0, engine.Plan!.PyramidsInStore);
    }

    /// <summary>An engine disposed while a making runs does not wait for it (design §4.8, Issue #297 Decision 8).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread, held on another column's chunk; a line in a facet by a category, drawn once.
    ///
    /// Steps:
    /// - Dispose the engine; release the worker; wait until the store's makings are idle.
    ///
    /// Expected:
    /// - Dispose returns while the worker is held: the making has not read the y yet. The making ends.
    /// </remarks>
    [Fact]
    public void Dispose_DoesNotWaitForAMaking()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = new Data();
        var engine = new PlotEngine(new NullBackend(), 640, 360, 1, pool);
        PlotPlan plan;
        using (var blocker = new Blocker(pool))
        {
            engine.SetPlot(Make("facet", data.Table));
            engine.RenderFrame();
            plan = engine.Plan!;
            engine.Dispose();
            Assert.Equal(0, data.Y.WorkerReads(0));
        }

        Eventually.True(() => plan.MakingsIdle, "the makings to end");
    }

    /// <summary>
    /// A provisional frame after the making ends allocates nothing once warm (invariant 3, design §4.8): the layers made off
    /// the frame are drawn as the others.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread; a bin over a column, and a line over a Y of two chunks; drawn until not provisional. Then chunk
    ///   1 held on the worker and the Y invalidated, so that its pyramid is built again behind the bin made off the frame;
    ///   drawn until the worker reads chunk 1, and 10 frames more.
    ///
    /// Steps:
    /// - Count the bytes this thread allocates over 20 frames. Release chunk 1 at the end, so that the pyramid's disposal
    ///   does not wait for the hold's watchdog.
    ///
    /// Expected:
    /// - 0 bytes, and each frame is provisional and records both layers.
    /// </remarks>
    [Fact]
    public void ProvisionalFramesAfterTheMaking_AllocateNothingOnceWarm()
    {
        const int Chunk = Column.ChunkLength;
        using var pool = new PlotWorkerPool(1);
        var values = new float[2 * Chunk];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = MathF.Sin(i * 0.001f) * (i < Chunk ? 1 : 10);
        }

        using var y = TestStorage<float>.MakeColumn(values, out var storage);
        using var z = Column.Wrap(Enumerable.Range(0, 2 * Chunk).Select(i => (double)(i % 97)).ToArray());
        var t = DataTable.Create().Add("x", Column.Uniform(0, 1, 2 * Chunk)).Add("y", y).Add("z", z).Build();
        using var engine = new PlotEngine(new NullBackend(), 640, 360, 1, pool);
        try
        {
            engine.SetPlot(
                Spec(
                    t,
                    Scale.Linear(),
                    new BarLayerSpec { X = t.Col<double>("z"), Stat = Stat.Bin() },
                    new LineLayerSpec { X = t.Col<double>("x"), Y = t.Col<float>("y") }
                )
            );
            Settle(engine);
            var reads = storage.WorkerReads(1);
            storage.Hold(1);
            y.Invalidate();
            Eventually.True(
                () =>
                {
                    engine.RenderFrame();
                    return storage.WorkerReads(1) > reads;
                },
                "the worker to read chunk 1 again"
            );
            for (var i = 0; i < 10; i++)
            {
                engine.RenderFrame();
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            var (provisional, recorded) = (true, true);
            for (var i = 0; i < 20; i++)
            {
                engine.RenderFrame();
                provisional &= engine.IsProvisional;
                recorded &= engine.Plan!.LayersRecorded == 2;
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(provisional);
            Assert.True(recorded);
            Assert.Equal(0, allocated);
        }
        finally
        {
            storage.Release(1);
        }
    }

    private static PlotSpec Make(string kind, DataTable t)
    {
        var (x, y, c, g) = (t.Col<double>("x"), t.Col<double>("y"), t.Cat("c").ToColumnRef(), t.Cat("g").ToColumnRef());
        return kind switch
        {
            "bin" => Spec(t, Scale.Linear(), new BarLayerSpec { X = x, Stat = Stat.Bin() }),
            "density" => Spec(t, Scale.Linear(), new LineLayerSpec { X = x, Stat = Stat.Density() }),
            "smooth" => Spec(
                t,
                Scale.Linear(),
                new LineLayerSpec
                {
                    X = x,
                    Y = y,
                    Stat = Stat.Smooth(5),
                }
            ),
            "bin 2D" => Spec(
                t,
                Scale.Linear(),
                new RectLayerSpec
                {
                    XMin = x,
                    YMin = y,
                    Stat = Stat.Bin2D(),
                }
            ),
            "count" => Spec(t, Scale.Band(), new BarLayerSpec { X = c, Stat = Stat.Count }),
            "summary" => Spec(
                t,
                Scale.Band(),
                new BarLayerSpec
                {
                    X = c,
                    Y = y,
                    Stat = Stat.Summary(),
                }
            ),
            "quantiles" => Spec(
                t,
                Scale.Band(),
                new ErrorBarLayerSpec
                {
                    X = c,
                    YMin = y,
                    YMax = y,
                    Stat = Stat.Summary(SummaryOp.Quantile(0.25), SummaryOp.Quantile(0.75)),
                }
            ),
            "numeric stack" => Spec(
                t,
                Scale.Linear(),
                new AreaLayerSpec
                {
                    X = x,
                    Y = y,
                    ColorBy = g,
                    Position = Position.Stack,
                }
            ),
            "split" => Spec(
                t,
                Scale.Linear(),
                new LineLayerSpec
                {
                    X = x,
                    Y = y,
                    ColorBy = g,
                }
            ),
            "facet" => Spec(t, Scale.Linear(), new LineLayerSpec { X = x, Y = y }) with { Facet = HyperPlot.Facet.Wrap(t.Cat("g")) },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static PlotSpec Spec(DataTable t, ScaleSpec x, params LayerSpec[] layers) =>
        new()
        {
            Data = t,
            Layers = layers,
            ScaleX = x,
        };

    /// <summary>Draws until a frame is not provisional, within the watchdog.</summary>
    private static void Settle(PlotEngine engine, string what = "") =>
        Eventually.True(
            () =>
            {
                engine.RenderFrame();
                return !engine.IsProvisional;
            },
            $"a frame that is not provisional {what}"
        );

    /// <summary>Returns the elements the last frame recorded: rectangles and polylines.</summary>
    private static int Elements(PlotEngine engine) => engine.Plan!.List.Rects.Length + engine.Plan.List.Polylines.Length;

    /// <summary>
    /// The table the tests draw: a numeric x and y and a column "bad" over storages whose reads a test holds and counts on the
    /// test's thread, a uniform u, categories c (4) and g (2), and "many", a category of 300 values.
    /// </summary>
    private sealed class Data : IDisposable
    {
        public Data()
        {
            var names = new[] { "a", "b", "c", "d" };
            XColumn = TestStorage<double>.MakeColumn([.. Enumerable.Range(0, Length).Select(i => (double)i)], out var x);
            YColumn = TestStorage<double>.MakeColumn([.. Enumerable.Range(0, Length).Select(i => Math.Sin(i * 0.02))], out var y);
            BadColumn = TestStorage<double>.MakeColumn([.. Enumerable.Range(0, Length).Select(i => (double)i)], out var bad);
            (X, Y, Bad) = (x, y, bad);
            (X.Watched, Y.Watched, Bad.Watched) = (Thread.CurrentThread, Thread.CurrentThread, Thread.CurrentThread);
            Table = DataTable
                .Create()
                .Add("x", XColumn)
                .Add("y", YColumn)
                .Add("bad", BadColumn)
                .Add("u", Column.Uniform(0, 1, Length))
                .Add("c", Column.Categorical([.. Enumerable.Range(0, Length).Select(i => names[i % 4])], names))
                .Add("g", Column.Categorical([.. Enumerable.Range(0, Length).Select(i => i % 2 == 0 ? "p" : "q")], ["p", "q"]))
                .Add("many", Column.Categorical([.. Enumerable.Range(0, Length).Select(i => "m" + (i % 300))]))
                .Build();
        }

        public Column<double> XColumn { get; }

        public Column<double> YColumn { get; }

        public Column<double> BadColumn { get; }

        public TestStorage<double> X { get; }

        public TestStorage<double> Y { get; }

        public TestStorage<double> Bad { get; }

        public DataTable Table { get; }

        public void Dispose()
        {
            XColumn.Dispose();
            YColumn.Dispose();
            BadColumn.Dispose();
        }
    }

    /// <summary>Holds a pool's only worker on another column's chunk, so that every job waits behind it, until disposed (once).</summary>
    private sealed class Blocker : IDisposable
    {
        private readonly Column<double> _column;
        private readonly TestStorage<double> _storage;
        private readonly MinMaxPyramid<double> _pyramid;
        private bool _disposed;

        public Blocker(PlotWorkerPool pool)
        {
            _column = TestStorage<double>.MakeColumn(new double[100], out _storage);
            _storage.Hold(0);
            _pyramid = pool.BuildPyramid(_column);
            _storage.WaitForWorker(0);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _storage.Release(0);
            _pyramid.Dispose();
            _column.Dispose();
        }
    }
}
