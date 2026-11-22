using System.Diagnostics;
using HyperPlot.Interaction;
using HyperPlot.Rendering;
using HyperPlot.Testing;

namespace HyperPlot.Tests;

/// <summary>
/// The workers' reads of the pages of points (design §5.5.2 "ページの読み出し", §8.1 "ページの読み出しの仕事", §10 R3b2 row
/// "読み出し（Core）", row R3b2).
/// </summary>
[Collection("Large data")]
public sealed class PointPagesTests
{
    // A hold that never ends fails its test after this, rather than the run.
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The frame's thread never reads a copy (design §5.5.2 "ページの読み出し"): a worker reads each page, for a copy in
    /// memory and for one in a temporary file, and the frames send what was read.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A point layer of 400,000 seeded rows (7 pages) of managed arrays, whose copy is in memory, or of memory-mapped
    ///   files, whose copy goes to a temporary file; an engine that does not wait, on the null backend.
    ///
    /// Steps:
    /// - Read the thread's count of ReadBlock calls. Render frames until one is not provisional, saying before each that the
    ///   tiles were sent, and waiting for the workers' reads after a frame that records runs. Read the count again.
    ///
    /// Expected:
    /// - The count did not move. The 7 pages were sent, and the last frame draws 400,000 points.
    /// </remarks>
    /// <param name="mapped">Whether the columns are memory-mapped files.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Frames_DoNotReadTheCopyOnTheFramesThread(bool mapped)
    {
        const int Count = 400_000;
        var (xs, ys) = Values(Count, seed: 81);
        using var xFile = TempFile.Write<double>(xs, 0);
        using var yFile = TempFile.Write<double>(ys, 0);
        using var x = mapped ? Column.MapFile<double>(xFile.Path) : Column.Wrap(xs);
        using var y = mapped ? Column.MapFile<double>(yFile.Path) : Column.Wrap(ys);
        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, 640, 360);
        engine.SetPlot(Spec(x, y));
        var reads = SpatialSnapshot.ReadsOnThisThread;
        DrawUntilResident(engine, backend);
        Assert.Equal(reads, SpatialSnapshot.ReadsOnThisThread);
        Assert.Equal(7, backend.PagesSent);
        Assert.Equal(Count, backend.LastList!.ResidentRuns.ToArray().Sum(r => r.Count));
    }

    /// <summary>
    /// A frame does not wait for a read (design §5.5.2 "ページの読み出し"): while every read is held on its worker, the
    /// frames draw what is there, provisional, and the pages come once the reads end.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A point layer of 400,000 seeded rows (7 pages) on a pool of 1 worker, in an engine that does not wait, on the null
    ///   backend. The cache's hook holds every read on its worker until the test lets it go.
    ///
    /// Steps:
    /// - Render frames until one records runs, and until the hold has a read. Render 5 frames more.
    /// - Let the reads go, and render frames until one is not provisional.
    ///
    /// Expected:
    /// - Each of the 5 frames returns while the read is held: provisional, with 7 runs of no slot, no page sent, and 7 reads
    ///   taken. The thread's count of ReadBlock calls did not move.
    /// - Then the 7 pages are sent and the last frame draws every point.
    /// </remarks>
    [Fact]
    public void Frames_WhileReadsAreHeld_DrawProvisionallyWithoutWaiting()
    {
        const int Count = 400_000;
        var (xs, ys) = Values(Count, seed: 83);
        using var x = Column.Wrap(xs);
        using var y = Column.Wrap(ys);
        using var pool = new PlotWorkerPool(1);
        var backend = new NullBackend();
        using var hold = new Hold();
        backend.Images.Points.TestRead = hold.Hook;
        using var engine = new PlotEngine(backend, 640, 360, pool: pool);
        engine.SetPlot(Spec(x, y));
        var reads = SpatialSnapshot.ReadsOnThisThread;
        var clock = Stopwatch.StartNew();
        while (backend.LastList?.ResidentRuns.Length is null or 0)
        {
            Assert.True(clock.Elapsed < Watchdog, "The index was not built in 30 s.");
            engine.RenderFrame();
        }

        hold.WaitEntered(1);
        for (var frame = 0; frame < 5; frame++)
        {
            engine.RenderFrame();
            Assert.True(engine.IsProvisional);
            Assert.All(backend.LastList!.ResidentRuns.ToArray(), run => Assert.Equal(-1, run.Slot));
            Assert.Equal(7, backend.LastList.ResidentRuns.Length);
            Assert.Equal(0, backend.PagesSent);
            Assert.Equal(7, backend.Images.Points.ReadsInUse);
            Assert.False(hold.Released);
        }

        Assert.Equal(reads, SpatialSnapshot.ReadsOnThisThread);
        hold.Release();
        DrawUntilResident(engine, backend);
        Assert.Equal(7, backend.PagesSent);
        Assert.Equal(Count, backend.LastList!.ResidentRuns.ToArray().Sum(r => r.Count));
    }

    /// <summary>
    /// The cache registers its reader with the pool of a snapshot's index once (design §5.5.2 "プール"): asking for pages,
    /// frame after frame, does not add to the pool's works, and no other pool gets one.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The built index of 1,000,000 uniform points (16 pages) on its own pool of 3 workers; another pool of 1 worker; a
    ///   residency cache with no frame budget.
    ///
    /// Steps:
    /// - Read both pools' counts of works. Ask for page 0 and read the counts. Then, 20 times, start a frame, forget the
    ///   snapshot's pages, ask for every page, wait for the reads, and ask for them again.
    ///
    /// Expected:
    /// - The first request adds one work to the index's pool and none to the other, and one reader to the cache. The 20
    ///   rounds, which read every page 20 times, add none.
    /// </remarks>
    [Fact]
    public void Reader_IsRegisteredOncePerPool()
    {
        var (xs, ys) = SpatialData.Uniform(1_000_000, seed: 85);
        using var built = SpatialBuilt.Of(xs, ys);
        using var other = new PlotWorkerPool(1);
        var pool = built.Snapshot.Index.Pool;
        var cache = new ResidencyCache { Unbounded = true };
        var pages = cache.Points;
        var (before, otherBefore) = (pool.WorkCount, other.WorkCount);
        cache.BeginFrame();
        Assert.Equal(-1, pages.Request(built.Snapshot, 0, PointPages.MaxBlockPoints));
        Assert.Equal((before + 1, otherBefore, 1), (pool.WorkCount, other.WorkCount, pages.ReaderCount));
        for (var round = 0; round < 20; round++)
        {
            cache.BeginFrame();
            pages.Forget(built.Snapshot);
            ReadEvery(cache, built.Snapshot, pageCount: 16);
        }

        Assert.Equal((before + 1, otherBefore, 1), (pool.WorkCount, other.WorkCount, pages.ReaderCount));
    }

    /// <summary>
    /// The reads asked for, being read, and read but not sent are 10 at most, over the plots of two pools that share the
    /// cache, and a request past them waits for a later frame (design §5.5.2 "同時に読む数とステージング"); each pool's worker
    /// reads only the pages of its own pool's indexes.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The built indexes of 400,000 uniform points (7 pages) each, on two pools of 1 worker each; one residency cache with
    ///   no frame budget. The cache's hook holds every read, and records the thread and the pool of the snapshot read.
    ///
    /// Steps:
    /// - Start a frame and ask for the 7 pages of the first index, then the 7 of the second. Wait until both workers hold a
    ///   read.
    /// - Let the reads go, wait for them, and ask for all 14 pages again. Say the frame was sent; start the next, and ask
    ///   again until every page is staged.
    ///
    /// Expected:
    /// - The first 10 requests take reads and the other 4 do not: 10 reads in use, every request −1.
    /// - Once read, the 10 pages are staged and the 4 others are not; the next frames read and stage those 4.
    /// - Every thread that read read the pages of one pool only, and two threads read.
    /// </remarks>
    [Fact]
    public void Reads_AreTenAtOnceOverPools()
    {
        var (ax, ay) = SpatialData.Uniform(400_000, seed: 87);
        var (bx, by) = SpatialData.Uniform(400_000, seed: 89);
        using var first = SpatialBuilt.Of(ax, ay, workers: 1);
        using var second = SpatialBuilt.Of(bx, by, workers: 1);
        var cache = new ResidencyCache { Unbounded = true };
        var pages = cache.Points;
        using var hold = new Hold();
        var readers = new System.Collections.Concurrent.ConcurrentDictionary<int, PlotWorkerPool>();
        var mixed = false;
        pages.TestRead = (snapshot, page) =>
        {
            mixed |= readers.GetOrAdd(Environment.CurrentManagedThreadId, snapshot.Index.Pool) != snapshot.Index.Pool;
            hold.Hook(snapshot, page);
        };
        var snapshots = new[] { first.Snapshot, second.Snapshot };
        cache.BeginFrame();
        foreach (var snapshot in snapshots)
        {
            for (var page = 0; page < 7; page++)
            {
                Assert.Equal(-1, pages.Request(snapshot, page, PointPages.MaxBlockPoints));
            }
        }

        Assert.Equal(10, pages.ReadsInUse);
        hold.WaitEntered(2);
        hold.Release();
        _ = pages.AwaitReads();
        var staged = 0;
        foreach (var snapshot in snapshots)
        {
            for (var page = 0; page < 7; page++)
            {
                staged += pages.Request(snapshot, page, PointPages.MaxBlockPoints) >= 0 ? 1 : 0;
            }
        }

        Assert.Equal(10, staged);
        Assert.Equal(10, pages.Uploads.Length);
        cache.Sent();
        for (var round = 0; round < 4 && pages.PagesInUse < 14; round++)
        {
            cache.BeginFrame();
            ReadEvery(cache, second.Snapshot, pageCount: 7);
        }

        Assert.Equal(14, pages.PagesInUse);
        Assert.False(mixed);
        Assert.Equal(2, readers.Count);
    }

    /// <summary>
    /// A cache let go of takes its reader off the pool (design §5.5.2 "プール", the owner's decision of 2026-11-22): the
    /// owner that made the cache detaches it when disposed, and a detached cache asks no worker for a read.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The built index of 400,000 uniform points on its own pool; a residency cache. A plan that waits for its indexes,
    ///   with its own cache, on another pool, drawing a point layer of 400,000 seeded rows.
    ///
    /// Steps:
    /// - Read the index's pool's count of works; ask the cache for page 0, wait, and read the count. Detach the cache, read
    ///   the count, and ask for page 1.
    /// - Read the plan's pool's count of works; render the plan, so that its own cache asks for pages; dispose the plan and
    ///   read the count again.
    ///
    /// Expected:
    /// - The count grows by 1 with the request and goes back with the detach; the request after the detach is −1 and takes
    ///   no read; no reader is left.
    /// - The plan's pool's count is back where it was once the plan is disposed: the index's work and the reader are off.
    /// </remarks>
    [Fact]
    public void Detach_TakesTheReaderOffThePool()
    {
        var (xs, ys) = SpatialData.Uniform(400_000, seed: 111);
        using var built = SpatialBuilt.Of(xs, ys);
        var pool = built.Snapshot.Index.Pool;
        var cache = new ResidencyCache { Unbounded = true };
        var before = pool.WorkCount;
        cache.BeginFrame();
        _ = cache.Points.Request(built.Snapshot, 0, PointPages.MaxBlockPoints);
        _ = cache.Points.AwaitReads();
        Assert.Equal(before + 1, pool.WorkCount);
        cache.Detach();
        Assert.Equal(before, pool.WorkCount);
        Assert.Equal(0, cache.Points.ReaderCount);
        var reads = cache.Points.ReadsInUse;
        Assert.Equal(-1, cache.Points.Request(built.Snapshot, 1, PointPages.MaxBlockPoints));
        Assert.Equal(reads, cache.Points.ReadsInUse);

        var (px, py) = Values(400_000, seed: 113);
        using var x = Column.Wrap(px);
        using var y = Column.Wrap(py);
        using var other = new PlotWorkerPool(2);
        var idle = other.WorkCount;
        var plan = TestPlans.Create(other, 640, 360);
        _ = plan.Render(Spec(x, y));
        Assert.True(plan.Images.Points.ReaderCount == 1 && other.WorkCount > idle);
        plan.Dispose();
        Assert.Equal(idle, other.WorkCount);
    }

    /// <summary>
    /// The staging is made once, at the first request, as 20 pages of 786,432 bytes, and does not grow however many pages go
    /// through it (design §5.5.2 "同時に読む数とステージング").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The built index of 1,000,000 uniform points (16 pages); a residency cache with no frame budget.
    ///
    /// Steps:
    /// - Read the staging's bytes. Ask for page 0. Then, 5 times, start a frame, forget the pages, and read every page.
    ///
    /// Expected:
    /// - 0 bytes before the first request, then 15,728,640 bytes, and 15,728,640 after 80 pages went through it.
    /// </remarks>
    [Fact]
    public void Staging_IsMadeOnceAndDoesNotGrow()
    {
        var (xs, ys) = SpatialData.Uniform(1_000_000, seed: 91);
        using var built = SpatialBuilt.Of(xs, ys);
        var cache = new ResidencyCache { Unbounded = true };
        Assert.Equal(0, cache.Points.StagingBytes);
        cache.BeginFrame();
        _ = cache.Points.Request(built.Snapshot, 0, PointPages.MaxBlockPoints);
        Assert.Equal(15_728_640, cache.Points.StagingBytes);
        for (var round = 0; round < 5; round++)
        {
            cache.BeginFrame();
            cache.Points.Forget(built.Snapshot);
            ReadEvery(cache, built.Snapshot, pageCount: 16);
        }

        Assert.Equal(15_728_640, cache.Points.StagingBytes);
    }

    /// <summary>
    /// A read asked for that the next frame does not ask for again is withdrawn, and a page read that the frames no longer
    /// ask for is let go of unsent (design §5.5.2 "要らなくなった読み"); a read being read is read to the end.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The built index of 400,000 uniform points (7 pages) on a pool of 1 worker; a residency cache with no frame budget.
    ///   The cache's hook holds the read of page 0 and records every page read.
    ///
    /// Steps:
    /// - Frame 1: ask for pages 0, 1, and 2, and wait until page 0 is held. Frame 2: ask for page 2. Start frame 3.
    /// - Let the read go and wait for the reads. In frame 3, ask for page 2. Start frame 4, and say the frame was sent.
    ///
    /// Expected:
    /// - At the start of frame 3 page 1's read is withdrawn: 2 reads in use, and page 1 is never read.
    /// - Page 2 is staged in frame 3. At the start of frame 4 the read of page 0, last asked for in frame 1, is let go of:
    ///   page 2's read alone stays until the send, page 0 was never staged, and no read is in use after the send.
    /// - Pages 0 and 2 were read once each.
    /// </remarks>
    [Fact]
    public void Reads_NotAskedForAgain_AreWithdrawnAndNotSent()
    {
        var (xs, ys) = SpatialData.Uniform(400_000, seed: 93);
        using var built = SpatialBuilt.Of(xs, ys, workers: 1);
        var cache = new ResidencyCache { Unbounded = true };
        var pages = cache.Points;
        const int Block = PointPages.MaxBlockPoints;
        using var hold = new Hold();
        var read = new System.Collections.Concurrent.ConcurrentQueue<int>();
        pages.TestRead = (snapshot, page) =>
        {
            read.Enqueue(page);
            if (page == 0)
            {
                hold.Hook(snapshot, page);
            }
        };

        cache.BeginFrame();
        Assert.Equal(
            (-1, -1, -1),
            (pages.Request(built.Snapshot, 0, Block), pages.Request(built.Snapshot, 1, Block), pages.Request(built.Snapshot, 2, Block))
        );
        hold.WaitEntered(1);
        cache.BeginFrame();
        Assert.Equal(-1, pages.Request(built.Snapshot, 2, Block));
        cache.BeginFrame();
        Assert.Equal(2, pages.ReadsInUse);

        hold.Release();
        _ = pages.AwaitReads();
        Assert.True(pages.Request(built.Snapshot, 2, Block) >= 0);
        cache.BeginFrame();
        Assert.Equal(1, pages.ReadsInUse);
        Assert.Equal(1, pages.PagesInUse);
        cache.Sent();
        Assert.Equal(0, pages.ReadsInUse);
        Assert.Equal([0, 2], read.Order());
    }

    /// <summary>
    /// A plan that lets its snapshot go while a page of it is read waits for nothing (design §5.5.2 "待ち"): neither its
    /// release nor the index's disposal waits for the read, the copy stays until the read gives its reference back, and the
    /// page read is not sent.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A plan that waits for its indexes, on a pool of 1 worker, with its own cache, drawing a point layer of 400,000
    ///   seeded rows. The cache's hook holds every read.
    ///
    /// Steps:
    /// - Render: the frame asks for the pages. Wait until the read is held, and keep the part's snapshot. Dispose the plan.
    /// - Let the read go, wait for it, and start a frame of the cache.
    ///
    /// Expected:
    /// - The disposal returns while the read is held, and the snapshot is not freed then.
    /// - Once the read ends the snapshot is freed; the frame stages nothing, and no read is in use.
    /// </remarks>
    [Fact]
    public void Release_WhileAPageIsRead_WaitsForNothing()
    {
        const int Count = 400_000;
        var (xs, ys) = Values(Count, seed: 95);
        using var x = Column.Wrap(xs);
        using var y = Column.Wrap(ys);
        using var pool = new PlotWorkerPool(1);
        using var hold = new Hold();
        var plan = TestPlans.Create(pool, 640, 360);
        var cache = plan.Images;
        cache.Points.TestRead = hold.Hook;
        _ = plan.Render(Spec(x, y));
        hold.WaitEntered(1);
        var index = ((IndexedPointPlan)plan.Panels[0].Layers[0]).SpatialIndex!;
        var snapshot = index.Acquire();
        snapshot.Release();

        plan.Dispose();
        Assert.False(hold.Released);
        Assert.False(snapshot.IsFreed);

        hold.Release();
        _ = cache.Points.AwaitReads();
        Assert.True(SpinWait.SpinUntil(() => snapshot.IsFreed, Watchdog), "The snapshot was not freed once the read ended.");
        cache.BeginFrame();
        Assert.Equal(0, cache.Points.Uploads.Length);
        Assert.Equal(0, cache.Points.ReadsInUse);
    }

    /// <summary>
    /// A page read when the device was lost meanwhile is not sent, and the next request reads it again (design §5.5.2
    /// "要らなくなった読み", invariant 10).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The built index of 400,000 uniform points on a pool of 1 worker; a residency cache with no frame budget. The cache's
    ///   hook holds the first read and counts every read.
    ///
    /// Steps:
    /// - Ask for page 0, and wait until its read is held. Reset the cache, as a lost device does. Let the read go and wait.
    /// - Ask for page 0, wait for the reads, and ask again.
    ///
    /// Expected:
    /// - The first request after the reset is −1 and stages nothing: the page read before the reset was let go of, and a
    ///   read of its own was taken. The page is staged after the second read: 2 reads of page 0.
    /// </remarks>
    [Fact]
    public void Reset_WhileAPageIsRead_ReadsItAgain()
    {
        var (xs, ys) = SpatialData.Uniform(400_000, seed: 97);
        using var built = SpatialBuilt.Of(xs, ys, workers: 1);
        var cache = new ResidencyCache { Unbounded = true };
        var pages = cache.Points;
        using var hold = new Hold();
        var reads = 0;
        pages.TestRead = (snapshot, page) =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                hold.Hook(snapshot, page);
            }
        };

        cache.BeginFrame();
        Assert.Equal(-1, pages.Request(built.Snapshot, 0, PointPages.MaxBlockPoints));
        hold.WaitEntered(1);
        cache.Reset();
        hold.Release();
        _ = pages.AwaitReads();
        Assert.Equal(-1, pages.Request(built.Snapshot, 0, PointPages.MaxBlockPoints));
        Assert.Equal(0, pages.Uploads.Length);
        Assert.Equal(1, pages.ReadsInUse);
        Assert.True(pages.Read(built.Snapshot, 0, PointPages.MaxBlockPoints) >= 0);
        Assert.Equal(2, Volatile.Read(ref reads));
    }

    /// <summary>
    /// Two workers never take the same read (design §5.5.2 "読みの場所の状態"): a worker takes a read by the exchange from
    /// asked to reading, and each request is read once.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The built index of 700,000 uniform points (11 pages) on a pool of 8 workers; a residency cache with no frame
    ///   budget. The cache's hook counts the reads.
    ///
    /// Steps:
    /// - 300 times: start a frame, forget the pages, ask for 10 pages at once, wait for the reads, ask again, and say the
    ///   frame was sent.
    ///
    /// Expected:
    /// - Each round reads exactly 10 pages and stages the 10: 3,000 reads.
    /// </remarks>
    [Fact]
    public void Reads_TwoWorkersNeverTakeTheSameRead()
    {
        var (xs, ys) = SpatialData.Uniform(700_000, seed: 99);
        using var built = SpatialBuilt.Of(xs, ys, workers: 8);
        var cache = new ResidencyCache { Unbounded = true };
        var pages = cache.Points;
        var reads = 0;
        pages.TestRead = (_, _) => Interlocked.Increment(ref reads);
        for (var round = 0; round < 300; round++)
        {
            cache.BeginFrame();
            pages.Forget(built.Snapshot);
            for (var page = 0; page < 10; page++)
            {
                Assert.Equal(-1, pages.Request(built.Snapshot, page, PointPages.MaxBlockPoints));
            }

            _ = pages.AwaitReads();
            Assert.Equal(10 * (round + 1), Volatile.Read(ref reads));
            for (var page = 0; page < 10; page++)
            {
                Assert.True(pages.Request(built.Snapshot, page, PointPages.MaxBlockPoints) >= 0);
            }

            cache.Sent();
        }

        Assert.Equal(3_000, Volatile.Read(ref reads));
    }

    /// <summary>
    /// A read that throws fails the layer, as its build would (design §5.5.2 "読みの例外", the owner's decision of
    /// 2026-11-22): the read is let go of, the engine draws nothing of the layer and says so once, and a render that waits
    /// throws the read's exception as it was.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A point layer of 400,000 seeded rows. The cache's hook throws an exception of its own for page 3.
    ///
    /// Steps:
    /// - In an engine that does not wait, render frames until the failure is told, waiting for the reads after each; render
    ///   one more, then one at another view, which records.
    /// - Render the spec with a headless renderer that waits, whose hook throws the same way.
    ///
    /// Expected:
    /// - One WorkFailed; the frame after it is not provisional and records no run; after the frame that records no read is
    ///   in use.
    /// - The headless render throws the hook's exception itself.
    /// </remarks>
    [Fact]
    public void Read_ThatThrows_FailsTheLayer()
    {
        const int Count = 400_000;
        var (xs, ys) = Values(Count, seed: 101);
        using var x = Column.Wrap(xs);
        using var y = Column.Wrap(ys);
        var thrown = new InvalidOperationException("The read of page 3.");
        void Throws(SpatialSnapshot snapshot, int page)
        {
            if (page == 3)
            {
                throw thrown;
            }
        }

        var backend = new NullBackend();
        backend.Images.Points.TestRead = Throws;
        using var engine = new PlotEngine(backend, 640, 360);
        var failures = 0;
        engine.WorkFailed += (in WorkFailure _) => failures++;
        engine.SetPlot(Spec(x, y));
        var clock = Stopwatch.StartNew();
        while (failures == 0)
        {
            Assert.True(clock.Elapsed < Watchdog, "The read did not fail in 30 s.");
            backend.Images.Sent();
            engine.RenderFrame();
            _ = backend.Images.Points.AwaitReads();
            Thread.Sleep(2);
        }

        engine.RenderFrame();
        Assert.Equal(1, failures);
        Assert.False(engine.IsProvisional);
        Assert.Equal(0, backend.LastList!.ResidentRuns.Length);

        // The next frame that records lets the reads of the failed layer go: the pages it no longer asks for.
        engine.Views.SetView(new PanelId(0, 0), new PanelView2D(ViewRange.FromValues(10, 90), ViewRange.FromValues(10, 90)));
        engine.RenderFrame();
        Assert.Equal(0, backend.Images.Points.ReadsInUse);

        var waiting = new NullBackend();
        waiting.Images.Points.TestRead = Throws;
        using var pool = new PlotWorkerPool(2);
        using var renderer = new HeadlessRenderer(waiting, 640, 360, pool);
        Assert.Same(thrown, Assert.Throws<InvalidOperationException>(() => renderer.Render(Spec(x, y))));
    }

    /// <summary>
    /// The pool's disposal waits for the reads in flight, one page a worker (design §5.5.2 "待ち", invariant 7), and a
    /// read asked for that no worker took ends with the pool's exception.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The built index of 400,000 uniform points on a pool of 1 worker; a residency cache with no frame budget. The
    ///   cache's hook holds the first read and marks when that hold ends.
    ///
    /// Steps:
    /// - Ask for pages 0 and 1, and wait until page 0's read is held. On another thread, dispose the pool, and keep whether
    ///   the hold had ended when the disposal returned. Wait until the disposal has begun, then let the read go; join.
    /// - Wait for the reads.
    ///
    /// Expected:
    /// - The disposal returned after the hold ended.
    /// - The wait for the reads returns; page 1, which no worker took, was not read.
    /// </remarks>
    [Fact]
    public void PoolDispose_WaitsForTheReadInFlight()
    {
        var (xs, ys) = SpatialData.Uniform(400_000, seed: 103);
        using var built = SpatialBuilt.Of(xs, ys, workers: 1);
        var pool = built.Snapshot.Index.Pool;
        var cache = new ResidencyCache { Unbounded = true };
        var pages = cache.Points;
        using var hold = new Hold();
        var ended = false;
        var read = new System.Collections.Concurrent.ConcurrentQueue<int>();
        pages.TestRead = (snapshot, page) =>
        {
            read.Enqueue(page);
            hold.Hook(snapshot, page);
            Volatile.Write(ref ended, true);
        };

        cache.BeginFrame();
        Assert.Equal(
            (-1, -1),
            (pages.Request(built.Snapshot, 0, PointPages.MaxBlockPoints), pages.Request(built.Snapshot, 1, PointPages.MaxBlockPoints))
        );
        hold.WaitEntered(1);
        var endedAtReturn = false;
        var disposer = new Thread(() =>
        {
            pool.Dispose();
            endedAtReturn = Volatile.Read(ref ended);
        });
        disposer.Start();
        Assert.True(SpinWait.SpinUntil(() => pool.IsDisposed, Watchdog), "The disposal did not begin in 30 s.");
        hold.Release();
        Assert.True(disposer.Join(Watchdog), "The disposal did not return in 30 s.");
        Assert.True(endedAtReturn);
        _ = pages.AwaitReads();
        Assert.Equal([0], read.ToArray());
    }

    /// <summary>
    /// A headless render waits for the workers' reads and sends every page the view needs before it draws, ten at most a
    /// round, without reading a copy on the calling thread (design §5.5.2 "ヘッドレス").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A point layer of 1,000,000 seeded rows (16 pages, more than the 10 reads at once), drawn by a headless renderer that
    ///   waits, on the null backend.
    ///
    /// Steps:
    /// - Read the thread's count of ReadBlock calls; render once; read it again.
    ///
    /// Expected:
    /// - The count did not move. The 16 pages were sent over 3 draws or more, and the last list draws every point from
    ///   pages on the GPU, no run without a slot.
    /// </remarks>
    [Fact]
    public void HeadlessRender_WaitsForTheReadsAndSendsEveryPage()
    {
        const int Count = 1_000_000;
        var (xs, ys) = Values(Count, seed: 105);
        using var x = Column.Wrap(xs);
        using var y = Column.Wrap(ys);
        using var pool = new PlotWorkerPool(3);
        var backend = new NullBackend();
        using var renderer = new HeadlessRenderer(backend, 640, 360, pool);
        var reads = SpatialSnapshot.ReadsOnThisThread;
        renderer.Render(Spec(x, y));
        Assert.Equal(reads, SpatialSnapshot.ReadsOnThisThread);
        Assert.Equal(16, backend.PagesSent);
        Assert.InRange(backend.DrawsInFrame, 3, int.MaxValue);
        var runs = backend.LastList!.ResidentRuns.ToArray();
        Assert.All(runs, run => Assert.True(run.Slot >= 0));
        Assert.Equal(Count, runs.Sum(r => r.Count));
    }

    /// <summary>
    /// The frames that ask for pages and the frames that send them allocate nothing once warm (design §5.5.2 "確保",
    /// invariant 3, the Acceptance of Issue #603).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A point layer of 1,000,000 seeded rows (16 pages) over [0, 100]², in an engine that does not wait, on the null
    ///   backend; drawn until every page is there. Two views that show every point, [−1, 101]² and [−2, 102]², which have
    ///   the same ticks.
    ///
    /// Steps:
    /// - The measured run: let every page go, as a lost device does, set the next of the two views, and render frames until
    ///   one is not provisional, waiting for the workers' reads after each. Warm up with it twice, collect, and count one
    ///   run.
    ///
    /// Expected:
    /// - 0 bytes. The counted run sent the 16 pages again over 4 frames or more.
    /// </remarks>
    [Fact]
    public void FramesThatReadAndSend_AllocateNothing()
    {
        const int Count = 1_000_000;
        var (xs, ys) = Values(Count, seed: 107);
        using var x = Column.Wrap(xs);
        using var y = Column.Wrap(ys);
        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, 640, 360);
        engine.SetPlot(Spec(x, y));
        DrawUntilResident(engine, backend);
        PanelView2D[] views =
        [
            new(ViewRange.FromValues(-1, 101), ViewRange.FromValues(-1, 101)),
            new(ViewRange.FromValues(-2, 102), ViewRange.FromValues(-2, 102)),
        ];
        var (sent, frames, run) = (0, 0, 0);
        PerformanceAssert.DoesNotAllocate(
            () =>
            {
                var before = backend.PagesSent;
                backend.Images.Reset();
                engine.Views.SetView(new PanelId(0, 0), views[run++ % 2]);
                frames = 0;
                var drawing = true;
                while (drawing)
                {
                    backend.Images.Sent();
                    engine.RenderFrame();
                    _ = backend.Images.Points.AwaitReads();
                    frames++;
                    drawing = engine.IsProvisional;
                }

                sent = backend.PagesSent - before;
            },
            warmUps: 2
        );
        Assert.Equal(16, sent);
        Assert.InRange(frames, 4, int.MaxValue);
    }

    /// <summary>
    /// The table of cells is not counted in the frame's upload (design §5.5.2 "セルの表", invariant 2): a frame that sends
    /// its cells' records still sends 5 pages.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A point layer of 1,000,000 seeded rows (16 pages, 31 resident cells or more), in an engine that does not wait, on
    ///   the null backend, which keeps for each draw the bytes of the pages it sends and the cells' records of its list.
    ///
    /// Steps:
    /// - Render frames until one is not provisional, waiting for the reads after a frame that records runs.
    ///
    /// Expected:
    /// - Some draw sends 5 whole pages, 3,932,160 bytes, while its list holds the records of every resident cell; no draw
    ///   sends more than 4,194,304 bytes of pages.
    /// </remarks>
    [Fact]
    public void CellTable_IsNotCountedInTheFrameUpload()
    {
        const int Count = 1_000_000;
        var (xs, ys) = Values(Count, seed: 109);
        using var x = Column.Wrap(xs);
        using var y = Column.Wrap(ys);
        var backend = new NullBackend();
        var draws = new List<(long Bytes, int Cells)>();
        backend.Sending = cache => draws.Add((cache.Points.BytesThisFrame, backend.LastList!.ResidentCells.Length));
        using var engine = new PlotEngine(backend, 640, 360);
        engine.SetPlot(Spec(x, y));
        DrawUntilResident(engine, backend);
        var cells = ((IndexedPointPlan)engine.Plan!.Panels[0].Layers[0]).SpatialIndex!.Acquire();
        var resident = cells.ResidentCells.Length;
        cells.Release();
        Assert.InRange(resident, 31, int.MaxValue);
        Assert.Contains((5L * PointPages.PagePoints * PointPages.PointBytes, resident), draws);
        Assert.All(draws, d => Assert.InRange(d.Bytes, 0, ResidencyCache.FrameUpload));
    }

    /// <summary>Renders frames until one is not provisional, waiting for the reads after a frame that records runs.</summary>
    private static void DrawUntilResident(PlotEngine engine, NullBackend backend)
    {
        var clock = Stopwatch.StartNew();
        var drawing = true;
        while (drawing)
        {
            Assert.True(clock.Elapsed < Watchdog, "The pages did not come in 30 s.");
            backend.Images.Sent();
            engine.RenderFrame();
            if (backend.LastList!.ResidentRuns.Length > 0)
            {
                _ = backend.Images.Points.AwaitReads();
            }
            else
            {
                Thread.Sleep(2);
            }

            drawing = engine.IsProvisional;
        }
    }

    // Asks for a snapshot's pages in a frame, waits for the reads, and asks again until each is staged; says each frame was sent.
    private static void ReadEvery(ResidencyCache cache, SpatialSnapshot snapshot, int pageCount)
    {
        for (var page = 0; page < pageCount; page++)
        {
            _ = cache.Resident(snapshot, page, PointPages.MaxBlockPoints);
        }
    }

    private static (double[] X, double[] Y) Values(int count, int seed)
    {
        var random = new Random(seed);
        return (
            [.. Enumerable.Range(0, count).Select(_ => random.NextDouble() * 100)],
            [.. Enumerable.Range(0, count).Select(_ => random.NextDouble() * 100)]
        );
    }

    private static PlotSpec Spec(Column<double> x, Column<double> y)
    {
        var table = DataTable.Create().Add("x", x).Add("y", y).Build();
        return Plot.Create(table)
            .Point(table.Col<double>("x"), table.Col<double>("y"))
            .ScaleX(Scale.Linear().WithDomain(0, 100))
            .ScaleY(Scale.Linear().WithDomain(0, 100))
            .Build();
    }

    /// <summary>Holds each read that calls its hook until the test lets it go, and counts the reads it holds.</summary>
    private sealed class Hold : IDisposable
    {
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private int _entered;

        /// <summary>Gets a value indicating whether the test let the reads go.</summary>
        public bool Released => _release.IsSet;

        /// <summary>Gets the hook a worker calls before it reads: it waits for the release, and fails past the watchdog.</summary>
        public Action<SpatialSnapshot, int> Hook =>
            (snapshot, page) =>
            {
                _ = Interlocked.Increment(ref _entered);
                if (!_release.Wait(Watchdog))
                {
                    throw new TimeoutException($"The read of page {page} was held past the watchdog.");
                }
            };

        /// <summary>Waits until the given number of reads are in the hook.</summary>
        public void WaitEntered(int count) =>
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref _entered) >= count, Watchdog), $"{count} reads did not come in 30 s.");

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _release.Dispose();
        }
    }
}
