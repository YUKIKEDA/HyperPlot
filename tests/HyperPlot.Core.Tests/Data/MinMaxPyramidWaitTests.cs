namespace HyperPlot.Tests;

/// <summary>The internal wait of a pyramid for its rebuild, <c>WhenComplete</c> (design §3.3, row G1).</summary>
[Collection("Large data")]
public sealed class MinMaxPyramidWaitTests
{
    private const int Done = 3;

    /// <summary>A complete pyramid returns a task that has ended.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A pyramid over three chunks, built to completion.
    ///
    /// Steps:
    /// - Call WhenComplete.
    ///
    /// Expected:
    /// - The task is Task.CompletedTask: nothing is allocated for it.
    /// </remarks>
    [Fact]
    public void WhenComplete_Complete_ReturnsAnEndedTask()
    {
        using var pool = new PlotWorkerPool(1);
        using var column = Column.Wrap(new float[PyramidData.Length]);
        using var pyramid = MinMaxPyramidTests.Build(pool, column);

        Assert.Same(Task.CompletedTask, pyramid.WhenComplete());
    }

    /// <summary>
    /// After an Invalidate, the wait requests the stale chunk, ends once it is built, and does not build the other chunks
    /// again; calls during one wait share the task.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread; a pyramid over three chunks built to completion; the worker parked on a blocker pyramid.
    ///
    /// Steps:
    /// - Invalidate chunk 1; call WhenComplete twice; read the task and chunk 1's state; release the blocker.
    ///
    /// Expected:
    /// - The two calls return the same task, not ended while the worker is parked, with chunk 1 requested; it then ends
    ///   successfully, IsComplete is true, chunk 1 was read once more by a worker, chunks 0 and 2 not.
    /// </remarks>
    [Fact]
    public void WhenComplete_AfterInvalidate_RebuildsOnlyTheStaleChunk()
    {
        using var pool = new PlotWorkerPool(1);
        using var column = TestStorage<float>.MakeColumn(new float[PyramidData.Length], out var storage);
        using var pyramid = MinMaxPyramidTests.Build(pool, column);
        int[] reads = [storage.WorkerReads(0), storage.WorkerReads(1), storage.WorkerReads(2)];
        using var blocker = Park(pool, out var blockerStorage);

        column.Invalidate(Column.ChunkLength + 5, 1);
        var task = pyramid.WhenComplete();
        var again = pyramid.WhenComplete();
        var endedWhileParked = task.IsCompleted;
        var state = pyramid.GetState(1);
        blockerStorage.Release(0);

        Assert.Same(task, again);
        Assert.False(endedWhileParked);
        Assert.Equal(1, state);
        Eventually.Completes(task);
        Assert.True(pyramid.IsComplete);
        Assert.Equal([reads[0], reads[1] + 1, reads[2]], [storage.WorkerReads(0), storage.WorkerReads(1), storage.WorkerReads(2)]);
        Assert.Equal(Done, pyramid.GetState(1));
    }

    /// <summary>
    /// A pyramid that has a fault before the call returns a task that ended with it, even once the failed chunk was built
    /// again after an Invalidate.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pyramid whose chunk 1 failed to read.
    ///
    /// Steps:
    /// - Wait for Completion to fault; call WhenComplete. Stop the failure, invalidate chunk 1, query it so that it is built
    ///   again, wait until IsComplete; call WhenComplete.
    ///
    /// Expected:
    /// - Both tasks have already faulted, with the pyramid's Fault.
    /// </remarks>
    [Fact]
    public void WhenComplete_FaultBefore_EndsWithIt()
    {
        using var pool = new PlotWorkerPool(1);
        using var column = TestStorage<float>.MakeColumn(new float[PyramidData.Length], out var storage);
        storage.FailOn(1, fail: true);
        using var pyramid = pool.BuildPyramid(column);
        var fault = Eventually.Faults(pyramid.Completion);

        var task = pyramid.WhenComplete();
        storage.FailOn(1, fail: false);
        column.Invalidate(Column.ChunkLength, 1);
        _ = pyramid.GetMinMax(Column.ChunkLength, 1);
        Eventually.True(() => pyramid.IsComplete, "the failed chunk to be built again");
        var later = pyramid.WhenComplete();

        Assert.True(task.IsFaulted);
        Assert.Same(fault, task.Exception!.InnerException);
        Assert.True(later.IsFaulted);
        Assert.Same(fault, later.Exception!.InnerException);
    }

    /// <summary>A chunk that fails while the wait is open ends the wait with its exception.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A pyramid built to completion, then its storage made to fail chunk 1.
    ///
    /// Steps:
    /// - Invalidate chunk 1; call WhenComplete.
    ///
    /// Expected:
    /// - The task faults with the IOException, which is the pyramid's Fault.
    /// </remarks>
    [Fact]
    public void WhenComplete_FailsDuringTheWait_EndsWithTheException()
    {
        using var pool = new PlotWorkerPool(1);
        using var column = TestStorage<float>.MakeColumn(new float[PyramidData.Length], out var storage);
        using var pyramid = MinMaxPyramidTests.Build(pool, column);
        storage.FailOn(1, fail: true);

        column.Invalidate(Column.ChunkLength, 1);
        var exception = Eventually.Faults(pyramid.WhenComplete());

        Assert.IsType<IOException>(exception);
        Assert.Same(pyramid.Fault, exception);
    }

    /// <summary>
    /// Disposing the pyramid ends an open wait with ObjectDisposedException, and a later call returns that too, also for a
    /// pyramid that was complete.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread; two pyramids built to completion; the worker parked on a blocker pyramid.
    ///
    /// Steps:
    /// - Invalidate chunk 0 of the first; call WhenComplete; dispose it; call WhenComplete again; release the blocker. Dispose
    ///   the second, still complete, and call WhenComplete.
    ///
    /// Expected:
    /// - The three tasks have faulted with ObjectDisposedException.
    /// </remarks>
    [Fact]
    public void WhenComplete_Disposed_EndsWithObjectDisposedException()
    {
        using var pool = new PlotWorkerPool(1);
        using var column = Column.Wrap(new float[PyramidData.Length]);
        var pyramid = MinMaxPyramidTests.Build(pool, column);
        using var blocker = Park(pool, out var blockerStorage);

        column.Invalidate(0, 1);
        var open = pyramid.WhenComplete();
        pyramid.Dispose();
        var later = pyramid.WhenComplete();
        blockerStorage.Release(0);

        Assert.IsType<ObjectDisposedException>(Eventually.Faults(open));
        Assert.IsType<ObjectDisposedException>(Eventually.Faults(later));

        using var other = Column.Wrap(new float[10]);
        var complete = MinMaxPyramidTests.Build(pool, other);
        complete.Dispose();
        Assert.IsType<ObjectDisposedException>(Eventually.Faults(complete.WhenComplete()));
    }

    /// <summary>Disposing the pool ends an open wait with ObjectDisposedException: nothing is built after it.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread; a pyramid built to completion; the worker parked on a blocker pyramid.
    ///
    /// Steps:
    /// - Invalidate chunk 0; call WhenComplete; dispose the pool on a thread of the test and wait until it waits for the
    ///   worker; release the blocker; join the thread.
    ///
    /// Expected:
    /// - The task faults with ObjectDisposedException.
    /// </remarks>
    [Fact]
    public void WhenComplete_PoolDisposed_EndsWithObjectDisposedException()
    {
        var pool = new PlotWorkerPool(1);
        using var column = Column.Wrap(new float[PyramidData.Length]);
        using var pyramid = MinMaxPyramidTests.Build(pool, column);
        using var blocker = Park(pool, out var blockerStorage);

        column.Invalidate(0, 1);
        var task = pyramid.WhenComplete();
        var disposing = new Thread(pool.Dispose);
        disposing.Start();
        Eventually.True(() => (disposing.ThreadState & ThreadState.WaitSleepJoin) != 0, "Dispose to wait for the worker");
        blockerStorage.Release(0);
        Assert.True(disposing.Join(30_000), "Dispose did not end within the watchdog.");

        Assert.IsType<ObjectDisposedException>(Eventually.Faults(task));
    }

    /// <summary>Parks the pool's one worker on a chunk of a blocker pyramid until the test releases chunk 0 of its storage.</summary>
    private static MinMaxPyramid<float> Park(PlotWorkerPool pool, out TestStorage<float> storage)
    {
        var column = TestStorage<float>.MakeColumn(new float[10], out storage);
        storage.Hold(0);
        var pyramid = pool.BuildPyramid(column);
        storage.WaitForWorker(0);
        return pyramid;
    }
}
