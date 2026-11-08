using HyperPlot.Interaction;
using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>
/// The engine's frames over a raster whose mipmap is being updated (design §3.7 "更新の進め方", §4.8, §10 "ラスタのミップマップ
/// （W1b3）", row W1b3, Issue #314): the frames do not wait, draw nothing until the first update ends, then only the tiles on
/// the GPU while it is updated again, and a failed update is made again when the column moves. The workers are held with
/// <see cref="TestStorage{T}"/>.
/// </summary>
public sealed class MipmapUpdateTests
{
    // Two chunks: rows 0 to 698 lie in chunk 0, rows 699 to 999 in chunk 1.
    private const int Width = 1500;
    private const int Height = 1000;

    /// <summary>
    /// The first update does not hold the frame, which draws no layer until it ends; then the frames draw the tiles a frame
    /// that waits draws (Issue #314 Decisions 1, 2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread; a numeric raster of 1,500 × 1,000 over a storage whose chunk 1 is held on the worker and whose
    ///   reads on the test's thread are counted.
    ///
    /// Steps:
    /// - Draw. Release; draw until not provisional. Draw the plot with an engine that waits.
    ///
    /// Expected:
    /// - Held: the frame returns, records no layer, and is provisional. Released: as many image records as the waiting
    ///   engine's first frame. The test's thread never read the column.
    /// </remarks>
    [Fact]
    public void FirstUpdate_DoesNotWaitAndDrawsOnceMade()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = new Data();
        using var engine = new PlotEngine(new NullBackend(), 640, 360, 1, pool);
        try
        {
            data.Storage.Hold(1);
            engine.SetPlot(data.Spec);
            engine.RenderFrame();
            Assert.Equal(0, engine.Plan!.LayersRecorded);
            Assert.True(engine.IsProvisional);
        }
        finally
        {
            data.Storage.Release(1);
        }

        Settle(engine);
        Assert.Equal(1, engine.Plan.LayersRecorded);
        Assert.Equal(0, data.Storage.WatchedReads);
        using var waiting = new PlotEngine(new NullBackend(), 640, 360, 1, pool) { WaitsForIndexes = true };
        waiting.SetPlot(data.Spec);
        waiting.RenderFrame();
        Assert.Equal(waiting.Plan!.List.Images.Length, engine.Plan.List.Images.Length);
    }

    /// <summary>
    /// While the mipmap is updated after an Invalidate, the frames draw the tiles already on the GPU and send none, level 0
    /// included; once it ends, the changed rows' tiles are sent again (Issue #314 Decisions 2, 3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The raster of <see cref="FirstUpdate_DoesNotWaitAndDrawsOnceMade"/>, drawn until not provisional.
    ///
    /// Steps:
    /// - Hold chunk 1; double the values; Invalidate the raster; draw 3 frames, zoom into a corner, whose level 0 tiles
    ///   were never sent, and draw 3 more. Release; draw until not provisional, adding
    ///   up the bytes each frame sends. Before each frame, the test says the last frame's tiles were sent, as a backend does
    ///   (the null backend sends nothing).
    ///
    /// Expected:
    /// - Held: each frame is provisional and sends 0 bytes, and the 3 before the zoom record as many image records as
    ///   before. Released: the
    ///   frames send tiles again (more than 0 bytes over them), and the update wrote tiles.
    /// </remarks>
    [Fact]
    public void Update_DrawsTheTilesOnTheGpuAndSendsNone()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = new Data();
        using var engine = new PlotEngine(new NullBackend(), 640, 360, 1, pool);
        engine.SetPlot(data.Spec);
        Settle(engine);
        var images = engine.Plan!.List.Images.Length;
        try
        {
            data.Storage.Hold(1);
            for (var i = 0; i < data.Storage.Values.Length; i++)
            {
                data.Storage.Values[i] *= 2;
            }

            data.Raster.Invalidate(0, Height);
            for (var i = 0; i < 6; i++)
            {
                // Zoomed into a corner, the frames ask for tiles of level 0 that were never sent.
                if (i == 3)
                {
                    engine.Views.SetView(new PanelId(0, 0), new PanelView2D(ViewRange.FromValues(0.0, 200.0), ViewRange.FromValues(0.0, 120.0)));
                }

                // The null backend sends nothing: the test says the last frame's tiles were sent, as a backend does.
                engine.Plan.Images.Sent();
                engine.RenderFrame();
                Assert.True(engine.IsProvisional);
                Assert.Equal(0, engine.Plan.Images.BytesThisFrame);
                if (i < 3)
                {
                    Assert.Equal(images, engine.Plan.List.Images.Length);
                }
            }
        }
        finally
        {
            data.Storage.Release(1);
        }

        var sent = 0L;
        Eventually.True(
            () =>
            {
                engine.Plan.Images.Sent();
                engine.RenderFrame();
                sent += engine.Plan.Images.BytesThisFrame;
                return !engine.IsProvisional;
            },
            "a frame that is not provisional"
        );
        Assert.True(sent > 0);
        Assert.True(Mipmap(engine).TilesWritten > 0);
    }

    /// <summary>
    /// A failed update does not throw from the frame (Issue #314 Decision 4): WorkFailed tells it once, the same version
    /// does not start it again, and a version that moves makes it again and draws.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread; the raster, whose chunk 1 fails on the worker.
    ///
    /// Steps:
    /// - Draw until WorkFailed came, and until no update runs; draw 5 frames more. Stop the failure; Invalidate the raster;
    ///   draw until not provisional.
    ///
    /// Expected:
    /// - Failed: one WorkFailed of plot 0 and layer 0 with an IOException; no layer is recorded; the 5 frames start no
    ///   update. Made again: still one WorkFailed, and the layer is recorded.
    /// </remarks>
    [Fact]
    public void Failure_IsToldOnceAndMadeAgainWhenTheColumnMoves()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = new Data();
        data.Storage.FailOn(1, fail: true);
        using var engine = new PlotEngine(new NullBackend(), 640, 360, 1, pool);
        var failures = new List<WorkFailure>();
        engine.WorkFailed += (in WorkFailure failure) => failures.Add(failure);
        engine.SetPlot(data.Spec);
        Eventually.True(
            () =>
            {
                engine.RenderFrame();
                return failures.Count > 0;
            },
            "the failure"
        );

        // An update started again for the same version would run now: the frames wait until none runs.
        Eventually.True(
            () =>
            {
                engine.RenderFrame();
                return !Mipmap(engine).Updating;
            },
            "no update to run"
        );
        var started = Mipmap(engine).UpdatesStarted;
        for (var i = 0; i < 5; i++)
        {
            engine.RenderFrame();
        }

        var failure = Assert.Single(failures);
        Assert.Equal((0, 0), (failure.Plot, failure.Layer));
        Assert.IsType<IOException>(failure.Exception);
        Assert.Equal(0, engine.Plan!.LayersRecorded);
        Assert.Equal(started, Mipmap(engine).UpdatesStarted);

        data.Storage.FailOn(1, fail: false);
        data.Raster.Invalidate(0, Height);
        Settle(engine);
        Assert.Single(failures);
        Assert.Equal(1, engine.Plan.LayersRecorded);
    }

    /// <summary>
    /// The rows of a chunk whose update failed are made by the next update, though only another chunk moved since (Issue
    /// #314 Decision 4): the generations are recorded once the update ends.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread; the raster, drawn until not provisional.
    ///
    /// Steps:
    /// - Fail chunk 1 on the worker; change the values of its rows, Invalidate them, and draw until WorkFailed came. Stop
    ///   the failure; change the values of rows of chunk 0, Invalidate those only, and draw until not provisional.
    ///
    /// Expected:
    /// - Every texel of each level above 0 equals the one of a mipmap made from the raster's values from nothing.
    /// </remarks>
    [Fact]
    public void FailedRows_AreMadeByTheNextUpdate()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = new Data();
        using var engine = new PlotEngine(new NullBackend(), 640, 360, 1, pool);
        var failures = 0;
        engine.WorkFailed += (in WorkFailure _) => failures++;
        engine.SetPlot(data.Spec);
        Settle(engine);

        data.Storage.FailOn(1, fail: true);
        for (var i = 800 * Width; i < Width * Height; i++)
        {
            data.Storage.Values[i] = 1000 + (i % 7);
        }

        data.Raster.Invalidate(800, 200);
        Eventually.True(
            () =>
            {
                engine.RenderFrame();
                return failures > 0;
            },
            "the failure"
        );

        data.Storage.FailOn(1, fail: false);
        for (var i = 0; i < 100 * Width; i++)
        {
            data.Storage.Values[i] = -1000 - (i % 5);
        }

        data.Raster.Invalidate(0, 100);
        Settle(engine);

        var made = (RasterPyramid<float>)Mipmap(engine);
        using var fresh = (RasterPyramid<float>)data.Raster.MakePyramid();
        fresh.Update(pool, wait: true);
        for (var level = 1; level < made.LevelCount; level++)
        {
            var (w, h) = made.Sizes[level];
            for (var row = 0; row < h; row++)
            {
                for (var column = 0; column < w; column++)
                {
                    Assert.Equal(fresh.Read(level, column, row, 0), made.Read(level, column, row, 0));
                }
            }
        }
    }

    /// <summary>
    /// An engine disposed while the mipmap is updated does not wait, and the levels are freed once the update ends (Issue
    /// #314 Decision 5).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread; the raster, chunk 1 held on the worker; drawn once, so that the first update runs.
    ///
    /// Steps:
    /// - Dispose the engine; release the chunk; wait until the levels are freed.
    ///
    /// Expected:
    /// - Dispose returns while the chunk is held, and the levels are not freed yet; once released, they are.
    /// </remarks>
    [Fact]
    public void Dispose_DoesNotWaitAndFreesOnceTheUpdateEnds()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = new Data();
        var engine = new PlotEngine(new NullBackend(), 640, 360, 1, pool);
        RasterPyramid mipmap;
        try
        {
            data.Storage.Hold(1);
            engine.SetPlot(data.Spec);
            engine.RenderFrame();
            mipmap = Mipmap(engine);
            data.Storage.WaitForWorker(1);
            engine.Dispose();
            Assert.False(mipmap.Freed);
        }
        finally
        {
            data.Storage.Release(1);
        }

        Eventually.True(() => mipmap.Freed, "the levels to be freed");
    }

    /// <summary>
    /// A mipmap disposed as its update ends, after the update has looked whether it was disposed and before its task
    /// completes, is still freed (PR #320 review): one of the two frees the levels.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread; the raster drawn until not provisional; an update's end that disposes the engine.
    ///
    /// Steps:
    /// - Invalidate the raster and draw, so that an update runs; wait until the levels are freed.
    ///
    /// Expected:
    /// - The levels are freed.
    /// </remarks>
    [Fact]
    public void DisposeAsTheUpdateEnds_StillFrees()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = new Data();
        var engine = new PlotEngine(new NullBackend(), 640, 360, 1, pool);
        engine.SetPlot(data.Spec);
        Settle(engine);
        var mipmap = Mipmap(engine);
        mipmap.OnEnding = engine.Dispose;

        data.Raster.Invalidate(0, Height);
        engine.RenderFrame();
        Eventually.True(() => mipmap.Freed, "the levels to be freed");
    }

    /// <summary>
    /// An engine's frames send an image's tiles within a frame's budget, and stay provisional until every visible tile is
    /// sent (design §5.5.3, Issue #319); an engine that waits sends them all at its first frame.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread; the raster on a 1,920 × 1,080 target, whose visible tiles are more than a frame's budget holds.
    ///
    /// Steps:
    /// - Draw until a frame is not provisional, saying the last frame's tiles were sent before each (the null backend sends
    ///   nothing), and keep the most bytes a frame sends and the number of frames that send. Draw the plot with an engine
    ///   that waits, once.
    ///
    /// Expected:
    /// - No frame sends more than the budget (4 MB), more than one frame sends, and the last frame draws no tile from an
    ///   ancestor. The waiting engine's first frame sends more than the budget.
    /// </remarks>
    [Fact]
    public void Frames_SendWithinTheBudget()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = new Data();
        using var engine = new PlotEngine(new NullBackend(), 1920, 1080, 1, pool);
        engine.SetPlot(data.Spec);
        var (most, sending) = (0L, 0);
        Eventually.True(
            () =>
            {
                engine.Plan?.Images.Sent();
                engine.RenderFrame();
                var bytes = engine.Plan!.Images.BytesThisFrame;
                (most, sending) = (Math.Max(most, bytes), sending + (bytes > 0 ? 1 : 0));
                return !engine.IsProvisional;
            },
            "a frame that is not provisional"
        );

        Assert.True(most <= ResidencyCache.FrameUpload, $"{most} bytes in a frame");
        Assert.True(sending > 1, $"{sending} frames sent");
        Assert.False(((ImagePlan)engine.Plan!.Layers[0]).Provisional);

        using var waiting = new PlotEngine(new NullBackend(), 1920, 1080, 1, pool) { WaitsForIndexes = true };
        waiting.SetPlot(data.Spec);
        waiting.RenderFrame();
        Assert.True(waiting.Plan!.Images.BytesThisFrame > ResidencyCache.FrameUpload);
    }

    /// <summary>A provisional frame during an update allocates nothing once warm (invariant 3, design §4.8).</summary>
    /// <remarks>
    /// Preconditions:
    /// - The raster drawn until not provisional; chunk 1 held, the raster invalidated, and 10 frames drawn.
    ///
    /// Steps:
    /// - Count the bytes this thread allocates over 20 frames. Release chunk 1 at the end.
    ///
    /// Expected:
    /// - 0 bytes, and each frame is provisional.
    /// </remarks>
    [Fact]
    public void FramesDuringAnUpdate_AllocateNothingOnceWarm()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = new Data();
        using var engine = new PlotEngine(new NullBackend(), 640, 360, 1, pool);
        engine.SetPlot(data.Spec);
        Settle(engine);
        try
        {
            data.Storage.Hold(1);
            data.Raster.Invalidate(0, Height);
            for (var i = 0; i < 10; i++)
            {
                engine.RenderFrame();
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            var provisional = true;
            for (var i = 0; i < 20; i++)
            {
                engine.RenderFrame();
                provisional &= engine.IsProvisional;
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(provisional);
            Assert.Equal(0, allocated);
        }
        finally
        {
            data.Storage.Release(1);
        }
    }

    private static RasterPyramid Mipmap(PlotEngine engine) => ((ImagePlan)engine.Plan!.Layers[0]).Mipmap;

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

    /// <summary>A numeric raster of 1,500 × 1,000 over a storage a test holds, fails, and watches on the test's thread.</summary>
    private sealed class Data : IDisposable
    {
        public Data()
        {
            Column = TestStorage<float>.MakeColumn([.. Enumerable.Range(0, Width * Height).Select(i => MathF.Sin(i * 0.0001f))], out var storage);
            Storage = storage;
            Storage.Watched = Thread.CurrentThread;
            Raster = HyperPlot.Raster.Of(Column, Width);
            Spec = new PlotSpec { Data = DataTable.Create().Build(), Layers = [new ImageLayerSpec { Raster = Raster }] };
        }

        public Column<float> Column { get; }

        public TestStorage<float> Storage { get; }

        public Raster<float> Raster { get; }

        public PlotSpec Spec { get; }

        public void Dispose() => Column.Dispose();
    }
}
