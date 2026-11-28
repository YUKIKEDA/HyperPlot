using System.Diagnostics;
using System.Numerics;
using HyperPlot.Interaction;
using HyperPlot.Rendering;
using HyperPlot.Testing;

namespace HyperPlot.Tests;

/// <summary>
/// The plans that share one residency cache (design §5.5.2 "キャッシュを分け合う計画", §10 R3b2 row, Issue #656): the
/// cache's frame counted once a tick, the reads kept through the tick, one budget whose sum counts the other plans' last
/// raw bytes and the last tick's tiles, the change of the drawing plan's layers only, the upload of a tick over every plan,
/// and the plans' registrations.
/// </summary>
/// <remarks>
/// The engines and renderers draw on one null backend. A layer of k pages holds k × 65,536 seeded uniform points over [0,
/// 100]², drawn whole on 640 × 360 without guides at markers of 1 DIP, so that its predicted time and count stay far under
/// their thresholds and only the budget decides; it needs k × 786,432 bytes. An engine's frame is followed by the wait for
/// the reads, so that the ticks a page takes do not depend on the workers' speed. The tests do not run in parallel: their
/// tables hold up to 31 pages a layer, and the engines' frames wait for the shared pool. The waits that draw until a state
/// comes end at <see cref="Deadline"/>, 60 s: a watchdog, far above the time they take (the whole class ran in 48 s in a
/// Debug build); a test that reaches it fails, so a slow machine fails it, never passes it. The upload of the rasters ends
/// once 20 ticks in a row send nothing after something was sent (every tile in view is there), or at 30 s.
/// </remarks>
[Collection(nameof(SharedCacheTests))]
[CollectionDefinition(nameof(SharedCacheTests), DisableParallelization = true)]
public sealed class SharedCacheTests : IDisposable
{
    private const long Page = (long)PointPages.PagePoints * PointPages.PointBytes;
    private const long Face = ResidencyCache.FaceBytes;

    // The budget of a texture array of tiles and some pages: a density's tiles find a slot beside the pages.
    private const long ArrayAndFourPages = (ResidencyCache.PageSlots * Face) + (4 * Page);
    private const long ArrayAndTwentyPages = (ResidencyCache.PageSlots * Face) + (20 * Page);

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);
    private static readonly PanelId Panel = new(0, 0);

    // Two views that show every point and have the same ticks: a view moved between them records each frame.
    private static readonly PanelView2D[] Views =
    [
        new(ViewRange.FromValues(-1, 101), ViewRange.FromValues(-1, 101)),
        new(ViewRange.FromValues(-2, 102), ViewRange.FromValues(-2, 102)),
    ];

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
    /// The cache's frame moves on once a tick (design §5.5.2 "tick の数え方"): a plan that begins a frame joins the cache's
    /// current one, and the cache moves on only when the plan is in it already.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two engines A and B, each a line of 1,000 points, on one null backend with a new cache.
    ///
    /// Steps:
    /// - Draw a frame of the engines in the order given, reading the cache's frame after each: ABAB, AA, ABBA, and ABBABAB
    ///   (ticks AB, B, AB, AB: A is quiet in the second tick).
    ///
    /// Expected:
    /// - 1, 1, 2, 2; 1, 2; 1, 1, 2, 2; 1, 1, 2, 2, 3, 3, 4: A, back from its quiet tick, joins the frame B began in that
    ///   tick, and the frames then pair each A with the B of the tick before.
    /// </remarks>
    [Theory]
    [InlineData("ABAB", new long[] { 1, 1, 2, 2 })]
    [InlineData("AA", new long[] { 1, 2 })]
    [InlineData("ABBA", new long[] { 1, 1, 2, 2 })]
    [InlineData("ABBABAB", new long[] { 1, 1, 2, 2, 3, 3, 4 })]
    public void Frame_PlansDrawnInTurn_MovesOnOnceATick(string order, long[] expected)
    {
        var backend = new NullBackend();
        using var a = Engine(backend, Line());
        using var b = Engine(backend, Line());
        var frames = new List<long>();
        foreach (var name in order)
        {
            (name == 'A' ? a : b).RenderFrame();
            frames.Add(backend.Images.Frame);
        }

        Assert.Equal(expected, frames);
    }

    /// <summary>
    /// A figure is one plan (design §5.5.2 "キャッシュを分け合う計画", "計画の登録と外し"): its plots record in the figure's
    /// frame and move it on not, and only the figure registers with the cache.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Engine A draws a figure of two lines side by side on 1,280 × 360; engine B a line alone; one null backend.
    ///
    /// Steps:
    /// - Draw A, B, A, B, reading the cache's frame after each.
    ///
    /// Expected:
    /// - Two plans are registered, numbered 1 and 2. Both plots of the figure recorded in A's first frame. The frames are
    ///   1, 1, 2, 2.
    /// </remarks>
    [Fact]
    public void Frame_FiguresPlots_RecordInTheFiguresFrame()
    {
        var backend = new NullBackend();
        using var a = new PlotEngine(backend, 1280, 360, 1, _pool);
        a.SetFigure(Figure.HConcat(Line(), Line()));
        using var b = Engine(backend, Line());
        var frames = new List<long>();
        a.RenderFrame();
        frames.Add(backend.Images.Frame);
        Assert.Equal((1, 1), (a.Figure!.Plans[0].Recordings, a.Figure.Plans[1].Recordings));
        b.RenderFrame();
        frames.Add(backend.Images.Frame);
        a.RenderFrame();
        frames.Add(backend.Images.Frame);
        b.RenderFrame();
        frames.Add(backend.Images.Frame);

        Assert.Equal([1L, 2L], Numbers(backend.Images));
        Assert.Equal([1L, 1L, 2L, 2L], frames);
    }

    /// <summary>
    /// A frame of the mouse alone, which copies the kept plot (design §5.4), joins no frame of the cache (design §5.5.2
    /// "tick の数え方"); a frame that draws the plot does.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The engine fixture's line, drawn once.
    ///
    /// Steps:
    /// - Move to the middle and draw: the first frame with a hover draws the plot and keeps it. Move 3 pixels and draw.
    ///
    /// Expected:
    /// - The first frame moves the cache's frame on by one. The frame of the move alone copies the kept plot (one copy, one
    ///   list), and the cache's frame stays.
    /// </remarks>
    [Fact]
    public void Frame_OfTheMouseAlone_JoinsNoFrame()
    {
        using var f = new EngineFixture();
        var frame = f.Backend.Images.Frame;
        f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, f.Middle));
        f.Engine.RenderFrame();
        Assert.Equal((1, 2), (f.Backend.CopiesInFrame, f.Backend.DrawsInFrame));
        Assert.Equal(frame + 1, f.Backend.Images.Frame);

        f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, f.Middle + new Vector2(3, 0)));
        f.Engine.RenderFrame();
        Assert.Equal((1, 1), (f.Backend.CopiesInFrame, f.Backend.DrawsInFrame));
        Assert.Equal(frame + 1, f.Backend.Images.Frame);
    }

    /// <summary>
    /// A plot alone that draws its last list marks its items this tick's (design §5.5.2 "キャッシュを分け合う計画"): another
    /// plan drawn after it in the tick lets go of none of them, and the plot never records again.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A budget of 7 pages. Engine P: a Points layer of 3 pages, drawn until its index is complete and it is not
    ///   provisional. Then engine Q: a Points layer of 5 other pages, drawn after P each tick; together they need 8 pages.
    ///
    /// Steps:
    /// - Draw ticks (P, then Q) until Q's pages hold the 4 slots left. Then 50 more ticks.
    ///
    /// Expected:
    /// - Through the 50 ticks P records nothing and is not provisional, the cache holds 7 pages, and each tick is known full
    ///   (Q's missing page finds nothing to let go of).
    /// - Without the mark, P's pages were last used in its last recording, before Q's, and the cache lets them go first.
    /// </remarks>
    [Fact]
    public void KeptList_MarksItsItemsThisTicks_SoALaterPlanLetsGoOfNone()
    {
        var table = Table(3, 5);
        var backend = new NullBackend { Images = new ResidencyCache(7 * Page) };
        using var p = Engine(backend, Spec(table, Points(table, 0)));
        Until(() => Settled(p, backend), () => Draw(backend, p), "P did not settle");
        using var q = Engine(backend, Spec(table, Points(table, 1)));
        Until(
            () => backend.Images.Points.PagesInUse == 7 && Scanned(q, backend),
            () =>
            {
                Draw(backend, p);
                Draw(backend, q);
            },
            "Q's pages did not come"
        );

        var recordings = p.Plan!.Recordings;
        for (var tick = 0; tick < 50; tick++)
        {
            Draw(backend, p);
            Draw(backend, q);
            Assert.Equal(recordings, p.Plan.Recordings);
            Assert.False(p.IsProvisional);
            Assert.Equal(7, backend.Images.Points.PagesInUse);
            Assert.True(backend.Images.FullThisFrame);
        }
    }

    /// <summary>
    /// The reads of a view drawn in turn with another on the same backend are kept through the tick (design §5.5.2 "読みの
    /// 場所"; the spike's ReadCancellation_TwoEnginesInTurn of Issue #656, in the form the design asks for): the view's
    /// provisional mark goes in as many ticks as when it is drawn alone, and its pages are read once each.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Engine A: a Points layer of 3 pages, on a null backend with the default budget. Alone; or drawn before engine B, a
    ///   line of 1,000 points whose view moves every tick; or before engine B with a Points layer of 3 other pages, its view
    ///   still. The cache's hook counts the reads.
    ///
    /// Steps:
    /// - Draw ticks until each Points layer has scanned its full snapshot and is no longer provisional, at most 200 ticks
    ///   after its first full scan. Count the ticks from the first full scan to the first tick that is not provisional.
    ///
    /// Expected:
    /// - A takes as many ticks with B as alone, and so does B's Points layer; the reads are 3 alone and with the line, and
    ///   6 with B's Points layer.
    /// </remarks>
    [Fact]
    public void Reads_TwoEnginesInTurn_ClearAsAlone()
    {
        var alone = Clear(mode: 0);
        var line = Clear(mode: 1);
        var points = Clear(mode: 2);
        Assert.Equal(3, alone.Reads);
        Assert.Equal((alone.A, 3), (line.A, line.Reads));
        Assert.Equal((alone.A, alone.A, 6), (points.A, points.B, points.Reads));
    }

    /// <summary>
    /// A plan that stops drawing has the reads it asked for and no worker took withdrawn when the tick after the first one it
    /// did not draw in begins (design §5.5.2 "読みの場所"), not before: the other plans' frames in the ticks between do not.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Engine A: a Points layer of 3 pages on a pool of 1 worker, whose read of a page the cache's hook holds. Engine B: a
    ///   line of 1,000 points on another pool. One null backend.
    ///
    /// Steps:
    /// - Draw ticks (A, then B) until A scans its full snapshot: A asks for its 3 pages, and the worker holds the first.
    ///   Then draw B alone, twice.
    ///
    /// Expected:
    /// - After the tick of A's request, 3 reads are in use. After B's first tick alone, still 3: A asked in the tick before.
    ///   After B's second tick alone, 1: the 2 reads asked for and not taken were withdrawn, the one held is read to the
    ///   end.
    /// </remarks>
    [Fact]
    public void Reads_OfAPlanThatStops_AreWithdrawnTheTickAfterNext()
    {
        var table = Table(3);
        using var reading = new PlotWorkerPool(1);
        using var held = new ManualResetEventSlim(initialState: false);
        var entered = 0;
        var backend = new NullBackend();
        backend.Images.Points.TestRead = (_, _) =>
        {
            _ = Interlocked.Increment(ref entered);
            _ = held.Wait(Deadline);
        };
        try
        {
            using var a = new PlotEngine(backend, 640, 360, 1, reading);
            a.SetPlot(Spec(table, Points(table, 0)));
            using var b = Engine(backend, Line());
            var clock = Stopwatch.StartNew();
            while (!Scanned(a, backend))
            {
                Assert.True(clock.Elapsed < Deadline, "A did not scan its full snapshot.");
                backend.Images.Sent();
                a.RenderFrame();
                b.RenderFrame();
            }

            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref entered) >= 1, Deadline), "The worker did not take a read.");
            Assert.Equal(3, backend.Images.Points.ReadsInUse);
            b.RenderFrame();
            Assert.Equal(3, backend.Images.Points.ReadsInUse);
            b.RenderFrame();
            Assert.Equal(1, backend.Images.Points.ReadsInUse);
        }
        finally
        {
            held.Set();
        }
    }

    /// <summary>
    /// The budget's sum counts the other plans' pages (design §5.5.2 "予算の合計"; the spike's
    /// TileBytesLastFrame_IsAnotherRenderersTiles of Issue #656, scene P, in the form the design asks for): an Auto layer
    /// within the budget alone draws a density when another plan's pages leave no room for it, and that plan's pages stay.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A budget of 7 pages, under the 64 faces of a texture array, so that no density's tile is sent. Renderer P: a Points
    ///   layer of 5 pages. Renderer Q: an Auto layer of 4 other pages. One null backend.
    ///
    /// Steps:
    /// - Render P, then Q, then P again.
    ///
    /// Expected:
    /// - Q's layer needs 4 pages; the choice counts P's 5 pages, so Q's sum is 9 pages, and Q draws a density; the sum left is
    ///   P's 5 pages. The cache holds P's 5 pages, and P's render after does not record.
    /// </remarks>
    [Fact]
    public void Sum_CountsAnotherPlansPages()
    {
        var table = Table(5, 4);
        var backend = new NullBackend { Images = new ResidencyCache(7 * Page) };
        using var p = new HeadlessRenderer(backend, 640, 360, _pool);
        using var q = new HeadlessRenderer(backend, 640, 360, _pool);
        var still = Spec(table, Points(table, 0));
        p.Render(still);
        q.Render(Spec(table, Auto(table, 1)));
        var layer = Layer(q);
        Assert.Equal(4 * Page, layer.Switch.Needed);
        Assert.Equal((5 * Page, 5 * Page), (q.Plan!.Budget.LastOthers, q.Plan.Budget.LastSum));
        Assert.True(layer.DrewDensity);
        Assert.Equal(5, backend.Images.Points.PagesInUse);

        var recordings = p.Plan!.Recordings;
        p.Render(still);
        Assert.Equal(recordings, p.Plan.Recordings);
    }

    /// <summary>
    /// The budget's sum counts the tiles every plan used in the tick before (design §5.5.2 "予算の合計"; the spike's
    /// TileBytesLastFrame_IsAnotherRenderersTiles of Issue #656, scene T, in the form the design asks for): another plan's
    /// tiles enter the tick after they came, and leave once that plan is quiet.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A budget of 22 pages and 1 face. Engine A: a float raster of 1,024 × 1,024 over [0, 100]², drawn alone until its
    ///   tiles are there (its mipmap is made); then the cache forgets every tile, as after a lost device, and A's view moves
    ///   off the raster ([200, 300]²), so that no tile is used. Engine B: an Auto layer of 22 pages. Ticks draw A, then B,
    ///   until B draws raw points and is not provisional.
    ///
    /// Steps:
    /// - Move A's view back over the raster ([0, 100]²). Draw a tick of A and B, and another. Draw ticks until neither is provisional. Then draw B alone twice.
    ///
    /// Expected:
    /// - In the first tick A's raster uses 5 tiles (its 2 × 2 of level 1, and the top level's one, asked for first); B's sum is
    ///   its own 22 pages, and B draws raw points. In the second, A's 5 tiles are in B's sum, and B draws a density; the sum
    ///   left is the 5 tiles. Then neither is provisional.
    /// - B's second tick alone counts fewer tiles than its first by the faces A's list draws (A keeps its list in the last
    ///   ticks, and marks those): A, quiet, used none in the tick before. B's choice counts no other plan's bytes.
    /// </remarks>
    [Fact]
    public void Sum_CountsTheTilesOfTheTickBefore()
    {
        var table = Table(22);
        var backend = new NullBackend { Images = new ResidencyCache((22 * Page) + Face) };
        using var a = Engine(backend, Raster1024(table));
        Draw(backend, a);
        Until(() => !a.IsProvisional, () => Draw(backend, a), "A's tiles did not come");
        backend.Images.Reset();
        a.Views.SetView(Panel, new PanelView2D(ViewRange.FromValues(200, 300), ViewRange.FromValues(200, 300)));
        using var b = Engine(backend, Spec(table, Auto(table, 0)));
        Until(
            () => Settled(b, backend),
            () =>
            {
                Draw(backend, a);
                Draw(backend, b);
            },
            "B did not settle"
        );
        Assert.False(Layer(b).DrewDensity);
        Assert.Empty(a.Plan!.List.Images.ToArray());

        a.Views.SetView(Panel, new PanelView2D(ViewRange.FromValues(0, 100), ViewRange.FromValues(0, 100)));
        Draw(backend, a);
        Draw(backend, b);
        Assert.False(Layer(b).DrewDensity);
        Assert.Equal(22 * Page, b.Plan!.Budget.LastSum);

        Draw(backend, a);
        Draw(backend, b);
        Assert.True(Layer(b).DrewDensity);
        Assert.Equal(5 * Face, b.Plan.Budget.LastSum);
        Until(
            () => !a.IsProvisional && !b.IsProvisional,
            () =>
            {
                Draw(backend, a);
                Draw(backend, b);
            },
            "A and B stayed provisional"
        );

        Draw(backend, b);
        var withA = b.Plan.Budget.LastSum;
        Draw(backend, b);
        Assert.Equal(withA - (a.Plan.List.Images.Length * Face), b.Plan.Budget.LastSum);
        Assert.Equal(0, b.Plan.Budget.LastOthers);
    }

    /// <summary>
    /// Two engines whose layers need more than the budget together change the layer of the plan that draws when the sum
    /// passes it (design §5.5.2 "替える層"): the one that began later, whichever layer is larger; once both are settled no
    /// tick records again or turns provisional.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A budget of a texture array of 64 faces and 4 pages (25.67 pages). Engine A: an Auto layer, drawn alone until it
    ///   draws raw points and is not provisional. Then engine B: an Auto layer, drawn after A each tick. A of 3 pages and B
    ///   of 23, or A of 23 and B of 3: 26 pages together.
    ///
    /// Steps:
    /// - Draw ticks until B scans its full snapshot, and 20 more; then until neither is provisional, and 100 more.
    ///
    /// Expected:
    /// - B draws a density and A raw points, either way, through the 20 ticks; through the 100 neither records nor is
    ///   provisional, and the drawings stay.
    /// </remarks>
    [Theory]
    [InlineData(3, 23)]
    [InlineData(23, 3)]
    public void Change_OverTheBudgetTogether_TheLaterPlansLayer(int pagesOfA, int pagesOfB)
    {
        var table = Table(pagesOfA, pagesOfB);
        var backend = new NullBackend { Images = new ResidencyCache(ArrayAndFourPages) };
        using var a = Engine(backend, Spec(table, Auto(table, 0)));
        Until(() => Settled(a, backend), () => Draw(backend, a), "A did not settle");
        using var b = Engine(backend, Spec(table, Auto(table, 1)));
        void Tick()
        {
            Draw(backend, a);
            Draw(backend, b);
        }

        Until(() => Scanned(b, backend), Tick, "B did not scan its full snapshot");
        for (var tick = 0; tick < 20; tick++)
        {
            Tick();
            Assert.Equal((false, true), (Layer(a).DrewDensity, Layer(b).DrewDensity));
        }

        Until(() => !a.IsProvisional && !b.IsProvisional, Tick, "A and B stayed provisional");
        var recordings = (a.Plan!.Recordings, b.Plan!.Recordings);
        for (var tick = 0; tick < 100; tick++)
        {
            Tick();
            Assert.Equal(recordings, (a.Plan.Recordings, b.Plan.Recordings));
            Assert.Equal((false, false), (a.IsProvisional, b.IsProvisional));
        }

        Assert.Equal((false, true), (Layer(a).DrewDensity, Layer(b).DrewDensity));
    }

    /// <summary>
    /// A quiet plan, in neither the current frame nor the one before, is out of the other plans' sums (design §5.5.2 "予算
    /// の合計"): another plan's Auto layer goes back to raw points by the way back's condition; the quiet plan, its pages let
    /// go of, records again when it draws, draws the same points, and is in the sums again.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A budget of a texture array and 20 pages (41.67 pages), on a null backend that keeps each page sent in its slot.
    ///   Engine A: a Points layer of 12 pages, drawn alone until it is not provisional. Engine B: an Auto layer of 31 pages,
    ///   drawn after A each tick until neither is provisional (43 pages together).
    ///
    /// Steps:
    /// - Draw B alone: one tick, then another, then until it is not provisional (its 31 pages leave A at most 10 of the
    ///   budget's 41). Then draw ticks of A and B until neither is provisional.
    ///
    /// Expected:
    /// - With A drawn, B draws a density, its sum counting A's 12 pages. B's first tick alone still counts them and draws a
    ///   density; its second counts no other plan, and B draws raw points.
    /// - A records again when it draws, and draws the centers it drew first; B's sum counts A's 12 pages again, and B draws a
    ///   density.
    /// </remarks>
    [Fact]
    public void Quiet_PlanLeavesTheSumsAndComesBack()
    {
        var table = Table(12, 31);
        var gpu = new GpuPages();
        var backend = new NullBackend { Images = new ResidencyCache(ArrayAndTwentyPages), Sending = gpu.Send };
        using var a = Engine(backend, Spec(table, Points(table, 0)));
        Until(() => Settled(a, backend), () => Draw(backend, a), "A did not settle");
        var drawn = gpu.Centers(a.Plan!.List);
        Assert.Equal(12 * PointPages.PagePoints, drawn.Length);
        using var b = Engine(backend, Spec(table, Auto(table, 1)));
        void Tick()
        {
            Draw(backend, a);
            Draw(backend, b);
        }

        Until(() => !a.IsProvisional && !b.IsProvisional && Scanned(b, backend), Tick, "A and B stayed provisional");
        Assert.True(Layer(b).DrewDensity);
        Assert.Equal(12 * Page, b.Plan!.Budget.LastOthers);

        Draw(backend, b);
        Assert.True(Layer(b).DrewDensity);
        Assert.Equal(12 * Page, b.Plan.Budget.LastOthers);
        Draw(backend, b);
        Assert.False(Layer(b).DrewDensity);
        Assert.Equal(0, b.Plan.Budget.LastOthers);
        Until(() => !b.IsProvisional, () => Draw(backend, b), "B's raw points did not come");

        var recordings = a.Plan.Recordings;
        Tick();
        Until(() => !a.IsProvisional && !b.IsProvisional, Tick, "A and B stayed provisional again");
        Assert.True(a.Plan.Recordings > recordings);
        Assert.Equal(drawn, gpu.Centers(a.Plan.List));
        Assert.Equal(12 * Page, b.Plan.Budget.LastOthers);
        Assert.True(Layer(b).DrewDensity);
    }

    /// <summary>
    /// A quiet plan whose items were not let go of draws its last list again without recording (design §5.5.2 "予算の合計").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A budget of 7 pages. Engine P: a Points layer of 3 pages; engine Q: a Points layer of 2 other pages; drawn in
    ///   ticks until neither is provisional.
    ///
    /// Steps:
    /// - Draw Q alone 3 ticks, then a tick of P and Q.
    ///
    /// Expected:
    /// - P records nothing and is not provisional: its 3 pages are there.
    /// </remarks>
    [Fact]
    public void Quiet_PlanNotLetGoOf_RecordsNothing()
    {
        var table = Table(3, 2);
        var backend = new NullBackend { Images = new ResidencyCache(7 * Page) };
        using var p = Engine(backend, Spec(table, Points(table, 0)));
        using var q = Engine(backend, Spec(table, Points(table, 1)));
        Until(
            () => Settled(p, backend) && Settled(q, backend),
            () =>
            {
                Draw(backend, p);
                Draw(backend, q);
            },
            "P and Q did not settle"
        );

        var recordings = p.Plan!.Recordings;
        for (var tick = 0; tick < 3; tick++)
        {
            Draw(backend, q);
        }

        Draw(backend, p);
        Draw(backend, q);
        Assert.Equal(recordings, p.Plan.Recordings);
        Assert.False(p.IsProvisional);
    }

    /// <summary>
    /// A plan's disposal takes it off the cache (design §5.5.2 "計画の登録と外し"): no later choice counts it, and the numbers
    /// of the registrations are never used again; a plan that makes its own cache registers with it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A budget of 7 pages. Renderer P: a Points layer of 5 pages; renderer Q: an Auto layer of 4 other pages. One null
    ///   backend.
    ///
    /// Steps:
    /// - Render P, then Q. Dispose P, and render Q's plot again. Render a line with a new renderer R. Make a plan with no
    ///   cache, and dispose it.
    ///
    /// Expected:
    /// - Q first draws a density, counting P's 5 pages. After P's disposal the cache's plans are Q's (2) alone, and Q's
    ///   choice counts no other plan: Q goes back to raw points. R's plan is number 3. The plan with no cache is the one
    ///   plan of its own cache, number 1, and none after its disposal.
    /// </remarks>
    [Fact]
    public void Dispose_TakesThePlanOffTheCache()
    {
        var table = Table(5, 4);
        var backend = new NullBackend { Images = new ResidencyCache(7 * Page) };
        using var p = new HeadlessRenderer(backend, 640, 360, _pool);
        using var q = new HeadlessRenderer(backend, 640, 360, _pool);
        p.Render(Spec(table, Points(table, 0)));
        var auto = Spec(table, Auto(table, 1));
        q.Render(auto);
        Assert.True(Layer(q).DrewDensity);
        Assert.Equal(5 * Page, q.Plan!.Budget.LastOthers);

        p.Dispose();
        Assert.Equal([2L], Numbers(backend.Images));
        q.Render(auto);
        Assert.Equal(0, q.Plan.Budget.LastOthers);
        Assert.False(Layer(q).DrewDensity);

        using var r = new HeadlessRenderer(backend, 640, 360, _pool);
        r.Render(Line());
        Assert.Equal([2L, 3L], Numbers(backend.Images));

        var own = new PlotPlan(_pool, 640, 360, backend.Text, 1);
        Assert.Equal([1L], Numbers(own.Images));
        own.Dispose();
        Assert.Empty(Numbers(own.Images));
    }

    /// <summary>
    /// A tick sends at most <see cref="ResidencyCache.FrameUpload"/> over every plan (design §5.5.2 "1 tick に送る量"; the
    /// spike's FrameUpload_PerCacheFrame of Issue #656, in the form the design asks for): the tiles that do not fit go in a
    /// later tick, and a send does not take the tick's count back.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two engines of 1,280 × 1,280 on one null backend whose draws count the bytes staged and then count them sent, each a
    ///   float raster of 2,048 × 2,048 of its own over [0, 100]².
    ///
    /// Steps:
    /// - Draw ticks (A, then B) until 20 ticks in a row send nothing, or 30 s.
    ///
    /// Expected:
    /// - No tick sends more than 4,194,304 bytes, and after each engine's frame the cache's count of the tick is the bytes
    ///   its draws sent so far in the tick. Neither engine is provisional at the end.
    /// </remarks>
    [Fact]
    public void Upload_OfATick_IsEveryPlansWithinFrameUpload()
    {
        var table = Table(1);
        long tick = 0;
        var backend = new NullBackend();
        backend.Sending = cache =>
        {
            tick += cache.BytesThisFrame;
            cache.Sent();
        };
        using var a = new PlotEngine(backend, 1280, 1280, 1, _pool);
        a.SetPlot(Raster2048(table, 1));
        using var b = new PlotEngine(backend, 1280, 1280, 1, _pool);
        b.SetPlot(Raster2048(table, 2));
        var (largest, total, quiet) = (0L, 0L, 0);
        var clock = Stopwatch.StartNew();
        while (quiet < 20)
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "The tiles were still sent after 30 s.");
            tick = 0;
            a.RenderFrame();
            Assert.Equal(tick, backend.Images.BytesThisTick);
            b.RenderFrame();
            Assert.Equal(tick, backend.Images.BytesThisTick);
            (largest, total) = (Math.Max(largest, tick), total + tick);
            quiet = tick == 0 && total > 0 ? quiet + 1 : 0;
        }

        Assert.InRange(largest, Face, ResidencyCache.FrameUpload);
        Assert.True(total > ResidencyCache.FrameUpload);
        Assert.Equal((false, false), (a.IsProvisional, b.IsProvisional));
    }

    /// <summary>
    /// The pages of points of a tick share the same <see cref="ResidencyCache.FrameUpload"/> over every plan (design §5.5.2
    /// "1 tick に送る量"): a page that does not fit after another plan's in the tick goes in a later tick.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two engines on one null backend whose draws count the bytes staged, each a Points layer of 5 pages of its own
    ///   (3,932,160 bytes, within 4,194,304; the two layers' 10 pages are as many as the cache's reads).
    ///
    /// Steps:
    /// - Draw ticks (A, then B) until neither engine is provisional. Forget every page, as after a lost device, and draw a
    ///   tick, in which both layers ask for their pages; then draw ticks until neither is provisional again.
    ///
    /// Expected:
    /// - No tick after the one of the requests sends more than 4,194,304 bytes, and the pages of both layers, 7,864,320
    ///   bytes, are sent.
    /// </remarks>
    [Fact]
    public void Upload_OfATickOfPages_IsEveryPlansWithinFrameUpload()
    {
        var table = Table(5, 5);
        long tick = 0;
        var backend = new NullBackend { Sending = cache => tick += cache.Points.BytesThisFrame };
        using var a = Engine(backend, Spec(table, Points(table, 0)));
        using var b = Engine(backend, Spec(table, Points(table, 1)));
        void Tick()
        {
            Draw(backend, a);
            Draw(backend, b);
        }

        Until(() => Settled(a, backend) && Settled(b, backend), Tick, "A and B did not settle");

        // As after a lost device: both layers ask for their pages again in one tick, and their reads end before the next.
        backend.Images.Reset();
        var (largest, total) = (0L, 0L);
        Tick();
        var clock = Stopwatch.StartNew();
        while (a.IsProvisional || b.IsProvisional)
        {
            Assert.True(clock.Elapsed < Deadline, "The pages did not come again.");
            tick = 0;
            Tick();
            (largest, total) = (Math.Max(largest, tick), total + tick);
        }

        Assert.InRange(largest, Page, ResidencyCache.FrameUpload);
        Assert.Equal(10 * Page, total);
    }

    /// <summary>
    /// What waits to be sent stays within <see cref="ResidencyCache.FrameUpload"/> when frames move on before the backend
    /// sends (design §5.5.2 "1 tick に送る量", PR #668 review): the tick's count starts at 0 with each frame, and the bytes
    /// staged and not sent still bound the next frame's.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - One engine of 1,280 × 1,280 on the null backend, whose draws do not send the tiles, a float raster of 2,048 × 2,048
    ///   over [0, 100]² (more tiles than a frame sends).
    ///
    /// Steps:
    /// - Draw frames, sending nothing, until a frame stages tiles; then 3 more frames. Then send, and draw a frame.
    ///
    /// Expected:
    /// - The bytes staged and not sent stay within 4,194,304 through the 3 frames, though each moves the cache's frame on.
    ///   After the send, the next frame stages tiles again.
    /// </remarks>
    [Fact]
    public void Upload_NotSentBeforeTheFrameMovesOn_StaysWithinFrameUpload()
    {
        var table = Table(1);
        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, 1280, 1280, 1, _pool);
        engine.SetPlot(Raster2048(table, 3));
        engine.RenderFrame();
        Until(() => backend.Images.BytesThisFrame > 0, engine.RenderFrame, "No tile was staged");
        for (var frame = 0; frame < 3; frame++)
        {
            var before = backend.Images.Frame;
            engine.RenderFrame();
            Assert.True(backend.Images.Frame > before);
            Assert.InRange(backend.Images.BytesThisFrame, Face, ResidencyCache.FrameUpload);
        }

        backend.Images.Sent();
        engine.RenderFrame();
        Assert.True(backend.Images.BytesThisFrame > 0);
    }

    /// <summary>
    /// The panels' sums of two plans stay apart, though their layers have the same plot, panel, and layer numbers (design
    /// §5.5.2 "同じ値のときの順", "パネルの和").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two renderers on one null backend, each an Auto layer over the same 300,000 seeded uniform points, sized from a
    ///   first render so that each predicts 9 ms: under 14.1 alone, 16.44 summed with the fixed term once.
    ///
    /// Steps:
    /// - Render P, then Q; then both again.
    ///
    /// Expected:
    /// - Each choice read one panel; each layer predicts 9 ms and draws raw points, both times.
    /// </remarks>
    [Fact]
    public void Ties_PanelsOfTwoPlans_AreSummedApart()
    {
        var table = Table(300_000);
        var backend = new NullBackend();
        using var p = new HeadlessRenderer(backend, 640, 360, _pool);
        using var q = new HeadlessRenderer(backend, 640, 360, _pool);
        p.Render(Spec(table, Auto(table, 0)));
        var size = DiameterFor(9, Layer(p).Switch.LastCount, Layer(p).Switch.LastLargestBin);
        var spec = Spec(table, Auto(table, 0) with { Size = size });
        p.Render(spec);
        Assert.Equal(1, p.Plan!.Budget.LastPanels);
        q.Render(spec);
        Assert.Equal(1, q.Plan!.Budget.LastPanels);
        Assert.Equal((9.0, 9.0), (Math.Round(Layer(p).Switch.LastTime, 4), Math.Round(Layer(q).Switch.LastTime, 4)));
        Assert.Equal((false, false), (Layer(p).DrewDensity, Layer(q).DrewDensity));

        // P draws its last list again: a choice of Q's frame that reached P's layer would show now.
        p.Render(spec);
        q.Render(spec);
        Assert.Equal((false, false), (Layer(p).DrewDensity, Layer(q).DrewDensity));
    }

    /// <summary>
    /// Of two plans' layers that need the same bytes, the drawing plan's changes, not the one of the lower plan's number
    /// (design §5.5.2 "同じ値のときの順", "替える層").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A budget of 7 pages. Renderer P (plan 1): an Auto layer of 4 pages; renderer Q (plan 2): an Auto layer of 4 other
    ///   pages. Both are layer 0 of plot 0.
    ///
    /// Steps:
    /// - Render P, then Q, then P again.
    ///
    /// Expected:
    /// - Q draws a density and P raw points, and P's render after leaves both so.
    /// </remarks>
    [Fact]
    public void Ties_EqualNeeds_TheDrawingPlansLayerChanges()
    {
        var table = Table(4, 4);
        var backend = new NullBackend { Images = new ResidencyCache(7 * Page) };
        using var p = new HeadlessRenderer(backend, 640, 360, _pool);
        using var q = new HeadlessRenderer(backend, 640, 360, _pool);
        var first = Spec(table, Auto(table, 0));
        p.Render(first);
        q.Render(Spec(table, Auto(table, 1)));
        Assert.Equal([1L, 2L], Numbers(backend.Images));
        Assert.Equal(Layer(p).Switch.Needed, Layer(q).Switch.Needed);
        Assert.Equal((false, true), (Layer(p).DrewDensity, Layer(q).DrewDensity));
        p.Render(first);
        Assert.Equal((false, true), (Layer(p).DrewDensity, Layer(q).DrewDensity));
    }

    /// <summary>
    /// The machine's time scale reaches neither the budget's sum nor the layer it changes, when two plans of other scales
    /// share the cache (design §5.5.2 "機械の速さの倍率").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A budget of 7 pages. Renderer P: an Auto layer of 5 pages; renderer Q: an Auto layer of 4 other pages. P at a time
    ///   scale of 1.0 and Q at 2.0, or the other way.
    ///
    /// Steps:
    /// - Render P, then Q.
    ///
    /// Expected:
    /// - Either way, P draws raw points and Q a density, and Q's choice counts P's 5 pages and leaves a sum of 5 pages.
    /// </remarks>
    [Theory]
    [InlineData(1.0, 2.0)]
    [InlineData(2.0, 1.0)]
    public void TimeScale_ReachesNeitherTheSumNorTheLayer(double scaleOfP, double scaleOfQ)
    {
        var table = Table(5, 4);
        var backend = new NullBackend { Images = new ResidencyCache(7 * Page) };
        using var p = new HeadlessRenderer(backend, 640, 360, _pool) { GpuTimeScale = scaleOfP };
        using var q = new HeadlessRenderer(backend, 640, 360, _pool) { GpuTimeScale = scaleOfQ };
        p.Render(Spec(table, Auto(table, 0)));
        q.Render(Spec(table, Auto(table, 1)));
        Assert.Equal((false, true), (Layer(p).DrewDensity, Layer(q).DrewDensity));
        Assert.Equal((5 * Page, 5 * Page), (q.Plan!.Budget.LastOthers, q.Plan.Budget.LastSum));
    }

    /// <summary>
    /// A tick known full stays full for the plans drawn after in it (design §5.5.2 "予算が満ちたフレーム", "今のフレームで使った
    /// もの"): their requests of pages not on the GPU search nothing; the next tick searches again.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A budget of 7 pages. Engine P: a Points layer of 9 pages, past the budget, whose view moves between two whole views
    ///   every tick, drawn alone until the cache holds 7 pages and the tick is known full. Engine Q: a Points layer of 2
    ///   other pages, its view moving the same way, drawn after P until it scans its full snapshot.
    ///
    /// Steps:
    /// - Draw P; read the searches and the requests that missed. Draw Q. Then draw Q first in the next tick.
    ///
    /// Expected:
    /// - P's frame leaves the tick known full. Q's frame misses its 2 pages and searches nothing. The next tick, Q alone
    ///   searches.
    /// </remarks>
    [Fact]
    public void Full_TickStaysFullForTheLaterPlans()
    {
        var table = Table(9, 2);
        var backend = new NullBackend { Images = new ResidencyCache(7 * Page) };
        var pages = backend.Images.Points;
        using var p = Engine(backend, Spec(table, Points(table, 0)));
        var turn = 0;
        void Move(PlotEngine engine)
        {
            // A view needs the plot's layout: the engine's first frame draws without one.
            if (engine.Plan is { Recordings: > 0 })
            {
                engine.Views.SetView(Panel, Views[turn % 2]);
            }
        }

        Until(
            () => pages.PagesInUse == 7 && backend.Images.FullThisFrame && Scanned(p, backend),
            () =>
            {
                Move(p);
                Draw(backend, p);
                turn++;
            },
            "P did not fill the budget"
        );
        using var q = Engine(backend, Spec(table, Points(table, 1)));
        Until(
            () => Scanned(q, backend),
            () =>
            {
                Move(p);
                Draw(backend, p);
                Move(q);
                Draw(backend, q);
                turn++;
            },
            "Q did not scan its full snapshot"
        );

        Move(p);
        Draw(backend, p);
        Assert.True(backend.Images.FullThisFrame);
        var (searches, missed) = (pages.SearchesThisFrame, pages.MissedThisFrame);
        Move(q);
        Draw(backend, q);
        Assert.Equal((searches, missed + 2), (pages.SearchesThisFrame, pages.MissedThisFrame));

        turn++;
        Move(q);
        Draw(backend, q);
        Assert.True(pages.SearchesThisFrame > 0);
    }

    /// <summary>
    /// Two engines drawn in turn on one backend allocate nothing once warm (design §5.5.2 "計画の登録と外し", invariant 3):
    /// within the budget, and past it together.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Within: the default budget; engine A a Points layer of 3 pages, engine B a Points layer of 3 other pages. Past:
    ///   a budget of a texture array and 4 pages; A an Auto layer of 3 pages, then B an Auto layer of 23 (B draws a density).
    ///   A's view moves between two whole views every tick, so that it records; B's stays, so that it keeps its list.
    ///
    /// Steps:
    /// - Draw A alone until it is not provisional, then ticks of A and B until neither is. The measured run: 20 ticks. Warm
    ///   up twice, collect, and count one run.
    ///
    /// Expected:
    /// - 0 bytes; neither engine is provisional at the end, and, past the budget, B draws a density and A raw points.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ticks_OfTwoEngines_AllocateNothing(bool past)
    {
        var table = Table(3, past ? 23 : 3);
        var backend = new NullBackend { Images = past ? new ResidencyCache(ArrayAndFourPages) : new ResidencyCache() };
        using var a = Engine(backend, Spec(table, past ? Auto(table, 0) : Points(table, 0)));
        Until(() => Settled(a, backend), () => Draw(backend, a), "A did not settle");
        using var b = Engine(backend, Spec(table, past ? Auto(table, 1) : Points(table, 1)));
        var turn = 0;
        void Tick()
        {
            a.Views.SetView(Panel, Views[turn++ % 2]);
            Draw(backend, a);
            Draw(backend, b);
        }

        Until(() => Settled(a, backend) && Settled(b, backend), Tick, "A and B did not settle");
        PerformanceAssert.DoesNotAllocate(
            () =>
            {
                for (var tick = 0; tick < 20; tick++)
                {
                    Tick();
                }
            },
            warmUps: 2
        );
        Assert.Equal((false, false), (a.IsProvisional, b.IsProvisional));
        if (past)
        {
            Assert.Equal((false, true), (Layer(a).DrewDensity, Layer(b).DrewDensity));
        }
    }

    /// <summary>
    /// Draws engine A of a Points layer of 3 pages alone or before an engine B, as <see cref="Reads_TwoEnginesInTurn_ClearAsAlone"/>
    /// says, and returns the ticks each Points layer took from its first full scan to its first tick not provisional, and
    /// the reads.
    /// </summary>
    private (int A, int B, int Reads) Clear(int mode)
    {
        var table = Table(3, 3);
        var reads = 0;
        var backend = new NullBackend();
        backend.Images.Points.TestRead = (_, _) => Interlocked.Increment(ref reads);
        using var a = Engine(backend, Spec(table, Points(table, 0)));
        using var b = mode == 0 ? null : Engine(backend, mode == 1 ? Line() : Spec(table, Points(table, 1)));
        var (full, cleared) = (new[] { -1, -1 }, new[] { -1, -1 });
        var clock = Stopwatch.StartNew();
        for (var tick = 0; cleared[0] < 0 || (mode == 2 && cleared[1] < 0); tick++)
        {
            Assert.True(clock.Elapsed < Deadline, "The layers stayed provisional.");
            for (var k = 0; k < 2; k++)
            {
                Assert.True(
                    full[k] < 0 || cleared[k] >= 0 || tick - full[k] < 200,
                    $"Layer {k} stayed provisional for 200 ticks after its full scan (mode {mode})."
                );
            }

            Draw(backend, a);
            if (b is not null)
            {
                if (mode == 1 && tick > 0)
                {
                    b.Views.SetView(Panel, new PanelView2D(ViewRange.FromValues(tick % 2, 1000), ViewRange.FromValues(-1, 2)));
                }

                Draw(backend, b);
            }

            Mark(0, a);
            if (mode == 2)
            {
                Mark(1, b!);
            }

            void Mark(int k, PlotEngine engine)
            {
                if (full[k] < 0 && Scanned(engine, backend))
                {
                    full[k] = tick;
                }

                if (full[k] >= 0 && cleared[k] < 0 && !engine.IsProvisional)
                {
                    cleared[k] = tick;
                }
            }
        }

        return (cleared[0] - full[0], cleared[1] - full[1], reads);
    }

    private static IndexedPointPlan Layer(PlotEngine engine) => (IndexedPointPlan)engine.Plan!.Panels[0].Layers[0];

    /// <summary>Returns whether an engine's point layer scanned its full snapshot in the cache's frame.</summary>
    private static bool Scanned(PlotEngine engine, NullBackend backend) =>
        engine.Plan?.Panels[0].Layers is [IndexedPointPlan part, ..] && part.ScannedFull(backend.Images.Frame);

    private static IndexedPointPlan Layer(HeadlessRenderer renderer) => (IndexedPointPlan)renderer.Plan!.Panels[0].Layers[0];

    /// <summary>Returns whether an engine's point layer scanned its full snapshot in the cache's frame and the engine is not provisional.</summary>
    private static bool Settled(PlotEngine engine, NullBackend backend) =>
        Scanned(engine, backend) && Layer(engine).SpatialIndex is { IsComplete: true } && !engine.IsProvisional;

    /// <summary>Draws an engine's frame, the tiles staged before counted as sent, and waits for the reads it asked for.</summary>
    private static void Draw(NullBackend backend, PlotEngine engine)
    {
        backend.Images.Sent();
        engine.RenderFrame();
        _ = backend.Images.Points.AwaitReads();
    }

    /// <summary>Draws until a condition holds, failing past the deadline.</summary>
    private static void Until(Func<bool> done, Action draw, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(clock.Elapsed < Deadline, $"{what} in {Deadline.TotalSeconds} s.");
            draw();
        }
    }

    private static long[] Numbers(ResidencyCache cache) => [.. cache.Plans.ToArray().Select(plan => plan.Number)];

    /// <summary>Returns the markers' diameter, in pixels, that makes a count and a largest bin predict a time.</summary>
    private static float DiameterFor(double time, long count, double largestBin)
    {
        var n = count / PredictedTime.PointsUnit;
        var term = time - PredictedTime.Fixed - (PredictedTime.PerPoints * n);
        var area = term / (PredictedTime.Overlap * n * Math.Pow(largestBin, PredictedTime.Exponent));
        return (float)Math.Sqrt(area * 4 / Math.PI);
    }

    private static PointLayerSpec Points(DataTable table, int layer) =>
        new()
        {
            X = table.Col<double>($"x{layer}"),
            Y = table.Col<double>($"y{layer}"),
            Size = 1,
            Draw = PointDraw.Points,
        };

    private static PointLayerSpec Auto(DataTable table, int layer) => Points(table, layer) with { Draw = PointDraw.Auto };

    private static PlotSpec Spec(DataTable table, params LayerSpec[] layers) => GeomTesting.Spec(table, layers);

    private PlotEngine Engine(NullBackend backend, PlotSpec spec)
    {
        var engine = new PlotEngine(backend, 640, 360, 1, _pool);
        engine.SetPlot(spec);
        return engine;
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

    /// <summary>Returns a plot of a float raster of 1,024 × 1,024 over [0, 100]².</summary>
    private PlotSpec Raster1024(DataTable table)
    {
        var values = new float[1024 * 1024];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = i % 977;
        }

        var pixels = Column.Wrap(values);
        _columns.Add(pixels);
        return GeomTesting.Spec(table, Plot.Create(table).Image(Raster.Of(pixels, 1024), i => i.Extent(0, 100, 0, 100)).Build().Layers[0]);
    }

    /// <summary>Returns a plot of a float raster of 2,048 × 2,048 over [0, 100]², of values of its own seed.</summary>
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

    /// <summary>
    /// Returns a table of a pair of columns (x, y) a layer, each of whole pages of seeded uniform points over [0, 100]², and
    /// NaN in the rows past them; a count above 1,000 is a count of points, not of pages.
    /// </summary>
    private DataTable Table(params int[] sizes)
    {
        var counts = sizes.Select(size => size > 1000 ? size : size * PointPages.PagePoints).ToArray();
        var builder = DataTable.Create();
        var rows = counts.Max();
        for (var layer = 0; layer < counts.Length; layer++)
        {
            var random = new Random(664 + layer);
            var (xs, ys) = (new double[rows], new double[rows]);
            for (var i = 0; i < rows; i++)
            {
                (xs[i], ys[i]) = i < counts[layer] ? (random.NextDouble() * 100, random.NextDouble() * 100) : (double.NaN, double.NaN);
            }

            var (x, y) = (Column.Wrap(xs), Column.Wrap(ys));
            _columns.Add(x);
            _columns.Add(y);
            builder = builder.Add($"x{layer}", x).Add($"y{layer}", y);
        }

        return builder.Build();
    }
}
