using System.Numerics;
using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>The compile of row G1 (design §4.2, §4.8): checks, learning, the diff, the wait, and the recorded lines.</summary>
[Collection("Large data")]
public sealed class PlotPlanTests
{
    private const int Width = 300;
    private const int Height = 200;
    private const int Length = PyramidData.Length;

    /// <summary>
    /// The plan records the lines that M4Batch and RenderList give when put together by hand with the same boundaries and
    /// mapping, over domains learned as the union of every layer's values without NaN (design §4.2, §4.8).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Three chunks of data: layer 1 has an evenly spaced X from 10 with step 0.5 and float Y; layer 2 a stored X that starts
    ///   lower and ends higher, and double Y with a gap of NaN and a dash.
    ///
    /// Steps:
    /// - Render the spec; build the pyramids, X statistics, M4 batch, boundaries, and polylines by hand over domains computed
    ///   from the raw arrays.
    ///
    /// Expected:
    /// - The learned domains equal the raw minimum and maximum of all X and all Y without NaN; the segments and polylines equal
    ///   the hand-built list's, entry for entry.
    /// </remarks>
    [Fact]
    public void Render_TwoLayers_EqualsMfourAndRenderListByHand()
    {
        using var pool = new PlotWorkerPool(2);
        using var scene = new Scene();
        using var plan = new PlotPlan(pool, Width, Height);

        var list = plan.Render(scene.Spec());

        var x = (Math.Min(10, scene.Xs[0]), Math.Max(10 + (0.5 * (Length - 1)), scene.Xs[^1]));
        var y = (Math.Min(Min(scene.A), Min(scene.B)), Math.Max(Max(scene.A), Max(scene.B)));
        Assert.Equal(x, plan.XDomain);
        Assert.Equal(y, plan.YDomain);
        Assert.Equal(2, list.Polylines.Length);
        AssertSameLines(scene.ByHand(pool, x, y), list);
    }

    /// <summary>Learning covers the edges of design §4.2: no element, no value but NaN, one value, and infinite values.</summary>
    /// <remarks>
    /// Preconditions:
    /// - Tables of empty columns; of a Y all NaN; of a Y of one repeated value; of a Y with +∞ and one with −∞.
    ///
    /// Steps:
    /// - Render each with learned domains; render the infinite ones again with a y domain given.
    ///
    /// Expected:
    /// - Empty columns give [0, 1] for x and y; all NaN gives [0, 1] for y; one value v gives [v − 0.5, v + 0.5]; +∞ and −∞
    ///   throw InvalidOperationException naming the column and the value; with a domain given they draw.
    /// </remarks>
    [Fact]
    public void Render_LearningEdges_FollowTheDesign()
    {
        using var pool = new PlotWorkerPool(1);

        Assert.Equal(((0d, 1d), (0d, 1d)), Domains(pool, Column.Uniform(5, 1, 0), []));
        Assert.Equal((0d, 1d), Domains(pool, Column.Uniform(5, 1, 3), [float.NaN, float.NaN, float.NaN]).Y);
        Assert.Equal((2.5, 3.5), Domains(pool, Column.Uniform(5, 1, 3), [3, 3, float.NaN]).Y);
        Assert.Equal((4.5, 5.5), Domains(pool, Column.Uniform(5, 1, 1), [1]).X);
        foreach (var infinity in new[] { float.PositiveInfinity, float.NegativeInfinity })
        {
            using var y = Column.Wrap(new[] { 1f, 2f, infinity });
            var table = DataTable.Create().Add("t", Column.Uniform(0, 1, 3)).Add("wild", y).Build();
            var learned = Plot.Create(table).Line(table.Col<double>("t"), table.Col<float>("wild")).Build();
            using var plan = new PlotPlan(pool, Width, Height);

            var exception = Assert.Throws<InvalidOperationException>(() => plan.Render(learned));
            Assert.Contains("\"wild\"", exception.Message, StringComparison.Ordinal);
            Assert.Contains(infinity.ToString(System.Globalization.CultureInfo.CurrentCulture), exception.Message, StringComparison.Ordinal);

            var given = learned with { ScaleY = Scale.Linear().WithDomain(0, 3) };
            Assert.True(plan.Render(given).Segments.Length > 0);
        }
    }

    /// <summary>The compile refuses a spec made directly that design §4.8 refuses, with the exception it names.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A table of an evenly spaced "t", a stored decreasing "down", and floats "a"; another table of the same shape; a layer
    ///   and a scale of types derived through the copy constructor.
    ///
    /// Steps:
    /// - Render specs with a null Data, Layers, ScaleX, ScaleY, or layer; a default X; a Y of the other table; "t" as Y; the
    ///   derived layer and scale; a domain of (1, 1); a width of -1; "down" as X; a domain too narrow for 1,000 pixels.
    ///
    /// Expected:
    /// - ArgumentNullException for the nulls; ArgumentException for the references, the kinds (naming the type), the domain,
    ///   and the narrow domain (naming the domain and the width); ArgumentOutOfRangeException for the width;
    ///   InvalidOperationException for the decreasing X, with the x domain learned and with it given (no learning reads X).
    /// </remarks>
    [Fact]
    public void Render_SpecOutOfContract_Throws()
    {
        using var pool = new PlotWorkerPool(1);
        using var down = Column.Wrap(new double[] { 3, 2, 1 });
        using var a = Column.Wrap(new float[] { 1, 2, 3 });
        var table = DataTable.Create().Add("t", Column.Uniform(0, 1, 3)).Add("down", down).Add("a", a).Build();
        var other = DataTable.Create().Add("t", Column.Uniform(0, 1, 3)).Add("down", down).Add("a", a).Build();
        var good = Plot.Create(table).Line(table.Col<double>("t"), table.Col<float>("a")).Build();
        var line = (LineLayerSpec)good.Layers[0];

        void Throws<TException>(PlotSpec spec, string? text = null)
            where TException : Exception
        {
            using var plan = new PlotPlan(pool, Width, Height);
            var exception = Assert.Throws<TException>(() => plan.Render(spec));
            if (text is not null)
            {
                Assert.Contains(text, exception.Message, StringComparison.Ordinal);
            }
        }

        Throws<ArgumentNullException>(good with { Data = null! });
        Throws<ArgumentNullException>(good with { Layers = null! });
        Throws<ArgumentNullException>(good with { ScaleX = null! });
        Throws<ArgumentNullException>(good with { ScaleY = null! });
        Throws<ArgumentNullException>(good with { Layers = [null!] });
        Throws<ArgumentException>(good with { Layers = [line with { X = default }] }, "default reference");
        Throws<ArgumentException>(good with { Layers = [line with { Y = other.Col<float>("a") }] }, "another table");
        Throws<ArgumentException>(good with { Layers = [line with { Y = table.Col<double>("t") }] }, "\"t\" of layer 0 is a UniformColumn");
        Throws<ArgumentException>(good with { Layers = [new OtherLayer(line)] }, "OtherLayer");
        Throws<ArgumentException>(good with { ScaleY = new OtherScale(good.ScaleY) }, "OtherScale");
        Throws<ArgumentException>(good with { ScaleX = new LinearScaleSpec { Domain = (1, 1) } }, "domain");
        Throws<ArgumentOutOfRangeException>(good with { Layers = [line with { Style = line.Style with { Width = -1 } }] });
        Throws<InvalidOperationException>(good with { Layers = [line with { X = table.Col<double>("down") }] }, "non-decreasing");
        Throws<InvalidOperationException>(
            good with
            {
                Layers = [line with { X = table.Col<double>("down") }],
                ScaleX = Scale.Linear().WithDomain(0, 3),
            },
            "non-decreasing"
        );

        using var wide = new PlotPlan(pool, 1000, Height);
        var narrow = good with { ScaleX = Scale.Linear().WithDomain(1e15, 1e15 + 0.25) };
        var exception = Assert.Throws<ArgumentException>(() => wide.Render(narrow));
        Assert.Contains("[1000000000000000, 1000000000000000.2]", exception.Message, StringComparison.Ordinal);
        Assert.Contains("1000 pixel columns", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A new spec redoes only the stages it changes, counted (design §4.8): pyramids for new Y columns, M4 for data and
    /// scales, recording for those and the strokes. Each result equals what a new plan records for the same spec.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The scene of two layers and spare columns "c" and "d"; one plan.
    ///
    /// Steps:
    /// - Render, in order: the spec; it again; a stroke changed; the x domain given; layer 2's X swapped for "t" (and layer 1
    ///   copied unchanged); layer 2's Y swapped for "c"; a third layer over "d"; the third layer taken off again; two layers
    ///   over "a"; the same layers in a new table over the same columns.
    ///
    /// Expected:
    /// - Pyramids, extractions, recordings added: 2/1/1, 0/0/0, 0/0/1, 0/1/1, 0/1/1, 1/1/1 with "b"'s pyramid disposed,
    ///   1/1/1, 0/1/1 with two pyramids left, 0/1/1 with one pyramid left (that of "a"), and 0/1/1. Every list equals a new
    ///   plan's for the spec.
    /// </remarks>
    [Fact]
    public void Render_ChangedSpecs_RedoOnlyTheChangedStages()
    {
        using var pool = new PlotWorkerPool(2);
        using var scene = new Scene();
        using var plan = new PlotPlan(pool, Width, Height);
        var spec = scene.Spec();
        var first = (LineLayerSpec)spec.Layers[0];
        var second = (LineLayerSpec)spec.Layers[1];
        var t = scene.Table;

        Step(spec, (2, 1, 1));
        Step(spec, (0, 0, 0));
        var restyled = spec with { Layers = [first with { Style = first.Style with { Width = 3 } }, second] };
        Step(restyled, (0, 0, 1));
        var domained = restyled with { ScaleX = Scale.Linear().WithDomain(100, 200) };
        Step(domained, (0, 1, 1));
        Step(domained with { Layers = [domained.Layers[0] with { }, second with { X = t.Col<double>("t") }] }, (0, 1, 1));
        var b = plan.Pyramids.Single(p => ReferenceEquals(p.Column, scene.BColumn));
        var swapped = domained with { Layers = [domained.Layers[0], second with { Y = t.Col<float>("c") }] };
        Step(swapped, (1, 1, 1));
        Assert.True(b.IsDisposed);
        var added = swapped with { Layers = [.. swapped.Layers, second with { Y = t.Col<float>("d") }] };
        Step(added, (1, 1, 1));
        Step(added with { Layers = [added.Layers[0], added.Layers[1]] }, (0, 1, 1));
        Assert.Equal(2, plan.Pyramids.Count);
        var shared = added with { Layers = [first, first with { Style = first.Style with { Width = 2 } }] };
        Step(shared, (0, 1, 1));
        Assert.Single(plan.Pyramids);
        var moved = DataTable.Create().Add("t", scene.T).Add("a", scene.AColumn).Build();
        var retabled = shared with { Data = moved, Layers = [first with { X = moved.Col<double>("t"), Y = moved.Col<float>("a") }] };
        Step(retabled, (0, 1, 1));
        Assert.Single(plan.Pyramids);

        void Step(PlotSpec next, (int Pyramids, int Extractions, int Recordings) expected)
        {
            var before = (plan.PyramidsBuilt, plan.Extractions, plan.Recordings);
            var list = plan.Render(next);
            Assert.Equal(
                expected,
                (plan.PyramidsBuilt - before.PyramidsBuilt, plan.Extractions - before.Extractions, plan.Recordings - before.Recordings)
            );
            using var fresh = new PlotPlan(pool, Width, Height);
            AssertSameLines(fresh.Render(next), list);
        }
    }

    /// <summary>A spec that fails part way leaves nothing behind: the next spec draws as on a new plan (design §4.8).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A plan that drew the scene; a spec of the scene's layers with an x domain too narrow for the width; a spec over
    ///   another table whose Y holds +∞.
    ///
    /// Steps:
    /// - Render the narrow spec, then the scene's; render the infinite spec, then the scene's.
    ///
    /// Expected:
    /// - The narrow spec throws ArgumentException and the infinite one InvalidOperationException; after each, the scene's
    ///   spec records what a new plan records.
    /// </remarks>
    [Fact]
    public void Render_AfterAFailure_DrawsTheNextSpec()
    {
        using var pool = new PlotWorkerPool(2);
        using var scene = new Scene();
        using var plan = new PlotPlan(pool, Width, Height);
        using var fresh = new PlotPlan(pool, Width, Height);
        var spec = scene.Spec();
        _ = plan.Render(spec);
        using var wild = Column.Wrap(new[] { 1f, 2f, float.PositiveInfinity });
        var infinite = Line(Column.Uniform(0, 1, 3), wild);

        Assert.Throws<ArgumentException>(() => plan.Render(spec with { ScaleX = Scale.Linear().WithDomain(1e15, 1e15 + 0.25) }));
        AssertSameLines(fresh.Render(spec), plan.Render(spec));
        Assert.Throws<InvalidOperationException>(() => plan.Render(infinite));
        AssertSameLines(fresh.Render(spec), plan.Render(spec));
    }

    /// <summary>Render waits for the pyramid to be built: it does not return while a worker is held on a chunk (design §4.8).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread; a Y column of three chunks whose chunk 1 the worker is held on.
    ///
    /// Steps:
    /// - Render on another thread; wait until the worker reads chunk 1; read whether Render returned; release the chunk.
    ///
    /// Expected:
    /// - Render has not returned while the chunk is held; afterwards it returns the lines a plan over an unheld column of the
    ///   same values records.
    /// </remarks>
    [Fact]
    public async Task Render_PyramidBuilding_WaitsForIt()
    {
        var values = Values(1);
        using var pool = new PlotWorkerPool(1);
        using var held = TestStorage<float>.MakeColumn(values, out var storage);
        storage.Hold(1);
        using var plan = new PlotPlan(pool, Width, Height);

        var rendering = Task.Run(() => plan.Render(Line(Column.Uniform(0, 1, Length), held)));
        storage.WaitForWorker(1);
        var returnedWhileHeld = rendering.IsCompleted;
        storage.Release(1);

        Assert.False(returnedWhileHeld);
        Eventually.Completes(rendering);
        var drawn = await rendering;
        using var plain = Column.Wrap(values);
        using var reference = new PlotPlan(pool, Width, Height);
        AssertSameLines(reference.Render(Line(Column.Uniform(0, 1, Length), plain)), drawn);
    }

    /// <summary>
    /// After values change and Invalidate is called, Render waits for the rebuild and draws the new values, for a Y column and
    /// for a stored X column (design §4.8).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A plan that rendered a stored X and a float Y over three chunks.
    ///
    /// Steps:
    /// - Rewrite the Y values in chunk 1, Invalidate them, Render; then shift the X values from chunk 1 on, Invalidate X,
    ///   Render.
    ///
    /// Expected:
    /// - Each Render extracts again and equals what a new plan records over new columns of the changed values.
    /// </remarks>
    [Fact]
    public void Render_AfterInvalidate_DrawsTheNewValues()
    {
        var xs = Enumerable.Range(0, Length).Select(i => (double)i).ToArray();
        var ys = Values(2);
        using var pool = new PlotWorkerPool(2);
        using var x = Column.Wrap(xs);
        using var y = Column.Wrap(ys);
        using var plan = new PlotPlan(pool, Width, Height);
        var spec = Line(x, y);
        _ = plan.Render(spec);

        ys.AsSpan(Column.ChunkLength, 1000).Fill(50);
        y.Invalidate(Column.ChunkLength, 1000);
        var extractions = plan.Extractions;
        AssertSameLines(Fresh(pool, xs, ys), plan.Render(spec));
        Assert.Equal(extractions + 1, plan.Extractions);

        for (var i = Column.ChunkLength; i < Length; i++)
        {
            xs[i] += Length / 4;
        }

        x.Invalidate(Column.ChunkLength, Length - Column.ChunkLength);
        AssertSameLines(Fresh(pool, xs, ys), plan.Render(spec));
        Assert.Equal(extractions + 2, plan.Extractions);
    }

    /// <summary>A chunk that fails to read makes Render throw that exception as it was, and nothing is drawn (design §4.8, §7.5).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A headless renderer over a backend that counts draws, with a pool of 1 thread; a Y column whose chunk 2 throws
    ///   IOException on the worker.
    ///
    /// Steps:
    /// - Render the plot twice.
    ///
    /// Expected:
    /// - IOException (not wrapped) each time; the backend drew nothing.
    /// </remarks>
    [Fact]
    public void Render_ChunkFails_ThrowsItAndDrawsNothing()
    {
        using var pool = new PlotWorkerPool(1);
        using var y = TestStorage<float>.MakeColumn(Values(3), out var storage);
        storage.FailOn(2, fail: true);
        var backend = new CountingBackend();
        using var renderer = new HeadlessRenderer(backend, Width, Height, pool);
        var spec = Line(Column.Uniform(0, 1, Length), y);

        Assert.Throws<IOException>(() => renderer.Render(spec));
        Assert.Throws<IOException>(() => renderer.Render(spec));
        Assert.Equal(0, backend.Draws);
    }

    /// <summary>Disposing the renderer disposes the pyramids of the plot drawn last, and leaves the pool and the columns (design §7.5).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A headless renderer with a pool of its own that drew the scene of two layers.
    ///
    /// Steps:
    /// - Keep its pyramids; dispose the renderer; build a pyramid on the pool and read a column.
    ///
    /// Expected:
    /// - Two pyramids, both disposed after; the pool still builds and the column still reads; Render(PlotSpec) throws
    ///   ObjectDisposedException and Render(null) ArgumentNullException.
    /// </remarks>
    [Fact]
    public void Dispose_Renderer_DisposesThePyramids()
    {
        using var pool = new PlotWorkerPool(1);
        using var scene = new Scene();
        var backend = new CountingBackend();
        var renderer = new HeadlessRenderer(backend, Width, Height, pool);
        Assert.Throws<ArgumentNullException>(() => renderer.Render((PlotSpec)null!));
        renderer.Render(scene.Spec());
        var pyramids = renderer.Plan!.Pyramids.ToArray();

        renderer.Dispose();

        Assert.Equal(2, pyramids.Length);
        Assert.All(pyramids, p => Assert.True(p.IsDisposed));
        Assert.Equal(1, backend.Draws);
        using var again = pool.BuildPyramid(scene.AColumn);
        Eventually.Completes(again.Completion);
        Assert.Throws<ObjectDisposedException>(() => renderer.Render(scene.Spec()));
    }

    /// <summary>
    /// Once warm, rendering the spec drawn last allocates nothing, and neither does a new x domain, which extracts and
    /// records again (design §4.8, invariant 3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A plan that rendered the scene's spec and two specs of other x domains, twice each.
    ///
    /// Steps:
    /// - Count the bytes this thread allocates over 20 more Render calls of the same spec, then over 20 that alternate the
    ///   two x domains.
    ///
    /// Expected:
    /// - 0 bytes both times; the second window extracted 20 times.
    /// </remarks>
    [Fact]
    public void Render_SameSpecOrNewXDomainWarm_AllocatesNothing()
    {
        using var pool = new PlotWorkerPool(1);
        using var scene = new Scene();
        using var plan = new PlotPlan(pool, Width, Height);
        var spec = scene.Spec();
        PlotSpec[] zooms = [spec with { ScaleX = Scale.Linear().WithDomain(100, 2000) }, spec with { ScaleX = Scale.Linear().WithDomain(50, 900) }];
        foreach (var warm in new[] { spec, spec, zooms[0], zooms[1], zooms[0], zooms[1], spec })
        {
            _ = plan.Render(warm);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 20; i++)
        {
            _ = plan.Render(spec);
        }

        var same = GC.GetAllocatedBytesForCurrentThread() - before;
        var extractions = plan.Extractions;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 20; i++)
        {
            _ = plan.Render(zooms[i & 1]);
        }

        var zoomed = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal((0L, 0L), (same, zoomed));
        Assert.Equal(extractions + 20, plan.Extractions);
    }

    internal static void AssertSameLines(RenderList expected, RenderList actual)
    {
        Assert.Equal(expected.Polylines.ToArray(), actual.Polylines.ToArray());
        Assert.Equal(expected.Segments.ToArray(), actual.Segments.ToArray());
    }

    private static ((double Min, double Max) X, (double Min, double Max) Y) Domains(PlotWorkerPool pool, UniformColumn x, float[] values)
    {
        using var y = Column.Wrap(values);
        using var plan = new PlotPlan(pool, Width, Height);
        _ = plan.Render(Line(x, y));
        return (plan.XDomain, plan.YDomain);
    }

    private static PlotSpec Line(Column x, Column y)
    {
        var table = DataTable.Create().Add("x", x).Add("y", y).Build();
        return new PlotSpec { Data = table, Layers = [new LineLayerSpec { X = Ref(table, 0), Y = Ref(table, 1) }] };
    }

    private static ColumnRef Ref(DataTable table, int index) =>
        table.GetColumn(index) switch
        {
            UniformColumn => table.Col<double>(table.GetName(index)),
            Column<double> => table.Col<double>(table.GetName(index)),
            _ => table.Col<float>(table.GetName(index)),
        };

    private static RenderList Fresh(PlotWorkerPool pool, double[] xs, float[] ys)
    {
        using var x = Column.Wrap((double[])xs.Clone());
        using var y = Column.Wrap((float[])ys.Clone());
        using var plan = new PlotPlan(pool, Width, Height);

        // The list is the plan's own and outlives it: Dispose frees the pyramids only.
        return plan.Render(Line(x, y));
    }

    private static float[] Values(int seed)
    {
        var random = new Random(seed);
        return [.. Enumerable.Range(0, Length).Select(i => (float)(Math.Sin(i * 3e-5) + random.NextDouble()))];
    }

    private static double Min(IEnumerable<double> values) => values.Where(v => !double.IsNaN(v)).Min();

    private static double Max(IEnumerable<double> values) => values.Where(v => !double.IsNaN(v)).Max();

    private sealed record OtherLayer : LayerSpec
    {
        public OtherLayer(LayerSpec original)
            : base(original) { }
    }

    private sealed record OtherScale : ScaleSpec
    {
        public OtherScale(ScaleSpec original)
            : base(original) { }
    }

    private sealed class CountingBackend : IGpuBackend
    {
        public bool IsWarp => true;

        public int Draws { get; private set; }

        public GpuFrameTime? LastFrameTime => null;

        public IRenderTarget CreateRenderTarget(in RenderTargetDescription description) => new Target(description);

        public FrameScope BeginFrame() => new(this, 1);

        public void Draw(IRenderTarget target, RenderList list) => Draws++;

        public void EndFrame(long token) { }

        public void Dispose() { }

        private sealed class Target(RenderTargetDescription description) : IRenderTarget
        {
            public RenderTargetDescription Description => description;

            public void Clear(Color color) { }

            public void ReadPixels(Span<byte> destination) { }

            public void Dispose() { }
        }
    }

    /// <summary>
    /// Two layers over three chunks: an evenly spaced X with float Y "a", and a stored X "xs" with double Y "b" holding a gap
    /// of NaN; spare float columns "c" and "d".
    /// </summary>
    private sealed class Scene : IDisposable
    {
        public Scene()
        {
            var random = new Random(7);
            A = [.. Enumerable.Range(0, Length).Select(i => (double)(float)(Math.Sin(i * 2e-5) + random.NextDouble()))];
            B = [.. Enumerable.Range(0, Length).Select(i => (Math.Cos(i * 1e-5) * 3) + random.NextDouble())];
            B.AsSpan(Length / 2, 5000).Fill(double.NaN);
            var step = 0.0;
            Xs = [.. Enumerable.Range(0, Length).Select(_ => 8 + (step += 0.5 + (random.NextDouble() * 0.01)))];
            T = Column.Uniform(10, 0.5, Length);
            AColumn = Column.Wrap(A.Select(v => (float)v).ToArray());
            BColumn = Column.Wrap(B);
            XsColumn = Column.Wrap(Xs);
            C = Column.Wrap(A.Select(v => (float)(v * 2)).ToArray());
            D = Column.Wrap(A.Select(v => (float)-v).ToArray());
            Table = DataTable.Create().Add("t", T).Add("a", AColumn).Add("xs", XsColumn).Add("b", BColumn).Add("c", C).Add("d", D).Build();
        }

        public double[] A { get; }

        public double[] B { get; }

        public double[] Xs { get; }

        public UniformColumn T { get; }

        public Column<float> AColumn { get; }

        public Column<double> BColumn { get; }

        public Column<double> XsColumn { get; }

        public Column<float> C { get; }

        public Column<float> D { get; }

        public DataTable Table { get; }

        public PlotSpec Spec() =>
            Plot.Create(Table)
                .Line(Table.Col<double>("t"), Table.Col<float>("a"), l => l.Color(new Color(31, 119, 180, 255)).Width(1.5f))
                .Line(Table.Col<double>("xs"), Table.Col<double>("b"), l => l.Color(new Color(214, 39, 40, 255)).Dash(new LineDash(6, 3)))
                .Build();

        /// <summary>Puts the M4 batch and the list together by hand, as design §4.8 says the plan does.</summary>
        public RenderList ByHand(PlotWorkerPool pool, (double Min, double Max) x, (double Min, double Max) y)
        {
            using var a = pool.BuildPyramid(AColumn);
            using var b = pool.BuildPyramid(BColumn);
            Eventually.Completes(a.Completion);
            Eventually.Completes(b.Completion);
            Eventually.Completes(pool.PrecomputeStats(XsColumn));
            var batch = new M4Batch();
            M4Point[][] outputs = [new M4Point[M4.MaxPointCount(Width)], new M4Point[M4.MaxPointCount(Width)]];
            _ = batch.Add(T, a, outputs[0]);
            _ = batch.Add(XsColumn, b, outputs[1]);
            var boundaries = new double[Width + 1];
            for (var k = 0; k <= Width; k++)
            {
                boundaries[k] = x.Min + ((x.Max - x.Min) * k / Width);
            }

            pool.Extract(batch, boundaries);
            var list = new RenderList();
            var styles = Spec().Layers.Cast<LineLayerSpec>().Select(l => l.Style).ToArray();
            for (var s = 0; s < 2; s++)
            {
                var points = new List<Vector2>();
                foreach (var point in outputs[s].AsSpan(0, batch.GetResult(s).Count))
                {
                    if (point.BreakBefore)
                    {
                        points.Add(new Vector2(float.NaN));
                    }

                    points.Add(
                        new Vector2(
                            (float)((point.X - x.Min) / (x.Max - x.Min) * Width),
                            (float)(Height - ((point.Y - y.Min) / (y.Max - y.Min) * Height))
                        )
                    );
                }

                list.DrawPolyline(points.ToArray(), styles[s]);
            }

            return list;
        }

        public void Dispose()
        {
            AColumn.Dispose();
            BColumn.Dispose();
            XsColumn.Dispose();
            C.Dispose();
            D.Dispose();
        }
    }
}
