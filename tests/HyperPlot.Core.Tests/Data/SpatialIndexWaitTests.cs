namespace HyperPlot.Tests;

/// <summary>
/// <see cref="SpatialIndex.WhenComplete"/>: the task it returns for a complete index, the one task that every caller
/// waits on, when that task ends, and the thread its continuations run on (design §3.4 "WhenComplete()"; row C3a). The
/// failures that end the task are in <see cref="SpatialIndexFailureTests"/>, and the disposals in
/// <see cref="SpatialIndexLifetimeTests"/>.
/// </summary>
[Collection("Large data")]
public sealed class SpatialIndexWaitTests
{
    private const string WorkerName = "HyperPlot worker";

    /// <summary>WhenComplete on a complete index returns an ended task and allocates nothing.</summary>
    /// <remarks>
    /// Preconditions:
    /// - The complete index of 5,000 seeded float points on a pool of 1 thread.
    /// - One call of WhenComplete made before the measurement.
    ///
    /// Steps:
    /// - Read the bytes allocated on the thread, call WhenComplete 100 times, and read the bytes again.
    ///
    /// Expected:
    /// - Every task has ended without an exception.
    /// - The difference is 0 bytes: an index that is complete makes no TaskCompletionSource.
    /// - Allocate ran once: no call queued a build.
    /// </remarks>
    [Fact]
    public void WhenComplete_OnACompleteIndex_ReturnsAnEndedTaskAndAllocatesNothing()
    {
        using var rig = new SpatialRig(seed: 59);
        var index = rig.Build();
        Assert.True(index.WhenComplete().IsCompletedSuccessfully);
        var ended = 0;

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            ended += index.WhenComplete().IsCompletedSuccessfully ? 1 : 0;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(100, ended);
        Assert.Equal(0, allocated);
        Assert.Equal(1, rig.Probe.CountOf(SpatialJob.Allocate));
    }

    /// <summary>Callers that wait for the same rebuild get the same task, which ends when the rebuild is complete.</summary>
    /// <remarks>
    /// Preconditions:
    /// - The complete index of 5,000 seeded float points on a pool of 1 thread, whose worker will be kept at the end of
    ///   the next Allocate.
    /// - Invalidate was called on x.
    ///
    /// Steps:
    /// - Call WhenComplete on 8 threads that leave a barrier together, and join them.
    /// - Wait until the worker is kept, and call WhenComplete once more.
    /// - Let the worker go and wait for the task.
    ///
    /// Expected:
    /// - The 9 calls return the same task, which has not ended while the worker is kept.
    /// - The calls queued one build: Allocate ran twice in all.
    /// - After the task: IsComplete is true, the version is 5, and WhenComplete returns an ended task.
    /// </remarks>
    [Fact]
    public void WhenComplete_CalledTogetherAfterInvalidate_GivesEveryCallerTheSameTask()
    {
        const int Callers = 8;
        using var rig = new SpatialRig(seed: 60);
        var index = rig.Build();
        var hold = rig.Probe.HoldAt(SpatialJob.Allocate);
        var tasks = new Task?[Callers];
        using var barrier = new Barrier(Callers);
        var threads = new Thread[Callers];
        rig.X.Invalidate();
        try
        {
            for (var i = 0; i < Callers; i++)
            {
                var slot = i;
                threads[i] = new Thread(() =>
                {
                    if (barrier.SignalAndWait(30_000))
                    {
                        tasks[slot] = index.WhenComplete();
                    }
                });
                threads[i].Start();
            }

            foreach (var thread in threads)
            {
                Assert.True(thread.Join(30_000), "A caller of WhenComplete did not return within the watchdog.");
            }

            hold.WaitForWorker();
            var first = tasks[0];
            Assert.NotNull(first);
            Assert.All(tasks, task => Assert.Same(first, task));
            Assert.Same(first, index.WhenComplete());
            Assert.False(first.IsCompleted);
        }
        finally
        {
            hold.Release();
        }

        Eventually.Completes(tasks[0]!);
        Assert.Equal(2, rig.Probe.CountOf(SpatialJob.Allocate));
        Assert.True(index.IsComplete);
        Assert.Equal(5, index.Version);
        Assert.True(index.WhenComplete().IsCompletedSuccessfully);
    }

    /// <summary>
    /// A task taken during a build ends when the index is complete: at the end of that build, or, when a column changed
    /// during it, at the end of the build that follows.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 5,000 seeded float points on a pool of 1 thread whose worker is kept at the end of PublishCoarse of the first
    ///   build.
    ///
    /// Steps:
    /// - While the worker is kept, call WhenComplete, and call Invalidate on x when <paramref name="invalidated"/>.
    /// - Let the worker go. At the end of each PublishDensity, the last job of a build, note the state and whether the
    ///   task and Completion have ended.
    /// - Wait for the task.
    ///
    /// Expected:
    /// - The task has not ended while the worker is kept.
    /// - Without the change: one PublishDensity; at its end the state is Complete and neither task has ended yet.
    /// - With the change: two. At the end of the first the state is Waiting, at the end of the second it is Complete,
    ///   and neither task has ended at either.
    /// - The tasks end once the last PublishDensity is reported: every end is noted when the wait for the task returns.
    /// - After the task: IsComplete is true and Completion ended without an exception.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenComplete_DuringABuild_EndsWhenTheIndexIsComplete(bool invalidated)
    {
        using var rig = new SpatialRig(seed: 61);
        Task? waiting = null;
        var ends = new List<(SpatialIndexState State, bool Waiting, bool Completion)>();
        rig.Probe.AfterJob = (job, index) =>
        {
            if (job == SpatialJob.PublishDensity)
            {
                ends.Add((index.State, waiting!.IsCompleted, index.Completion.IsCompleted));
            }
        };
        var hold = rig.Probe.HoldAt(SpatialJob.PublishCoarse);
        var index = rig.Start();
        try
        {
            hold.WaitForWorker();
            waiting = index.WhenComplete();
            Assert.False(waiting.IsCompleted);
            if (invalidated)
            {
                rig.X.Invalidate();
            }
        }
        finally
        {
            hold.Release();
        }

        Eventually.Completes(waiting);
        Assert.True(index.IsComplete);
        Assert.True(index.Completion.IsCompletedSuccessfully);
        (SpatialIndexState, bool, bool)[] expected = invalidated
            ? [(SpatialIndexState.Waiting, false, false), (SpatialIndexState.Complete, false, false)]
            : [(SpatialIndexState.Complete, false, false)];
        Assert.Equal(expected, ends);
    }

    /// <summary>The continuations of Completion and of the task of WhenComplete do not run on a worker's thread.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 5,000 seeded float points on a pool of 1 thread whose worker is kept at the end of Allocate of the first build.
    /// - <paramref name="fails"/>: the transform of x throws, so that the worker ends both tasks with the exception;
    ///   otherwise the worker ends them when the index is complete.
    ///
    /// Steps:
    /// - While the worker is kept, add to each task a continuation that asks to run synchronously and returns the name
    ///   of its thread.
    /// - Let the worker go and wait for both continuations.
    ///
    /// Expected:
    /// - The jobs of the build ran on a thread whose name begins with "HyperPlot worker".
    /// - Neither continuation ran on a thread of that name.
    /// - Both tasks ended as the build did: without an exception, or with the exception of the transform.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tasks_EndedByAWorker_RunTheirContinuationsOnAnotherThread(bool fails)
    {
        using var rig = new SpatialRig(seed: 62);
        var thrown = new InvalidOperationException("The scale of the test threw.");
        var jobThreads = new List<string?>();
        rig.Probe.AfterJob = (_, _) => jobThreads.Add(Thread.CurrentThread.Name);
        var hold = rig.Probe.HoldAt(SpatialJob.Allocate);
        var index = rig.Start(new SpatialIndexInput { Rows = RowRange.All, XTransform = new ThrowingScale(fails ? thrown : null) });
        Task<string?> afterWait;
        Task<string?> afterCompletion;
        Task waiting;
        try
        {
            hold.WaitForWorker();
            waiting = index.WhenComplete();
            Assert.False(waiting.IsCompleted);
            afterWait = Follow(waiting);
            afterCompletion = Follow(index.Completion);
        }
        finally
        {
            hold.Release();
        }

        var waitThread = ThreadOf(afterWait);
        var completionThread = ThreadOf(afterCompletion);
        Assert.NotEmpty(jobThreads);
        Assert.All(jobThreads, name => Assert.StartsWith(WorkerName, name, StringComparison.Ordinal));
        Assert.False(waitThread?.StartsWith(WorkerName, StringComparison.Ordinal) == true, $"The continuation ran on {waitThread}.");
        Assert.False(completionThread?.StartsWith(WorkerName, StringComparison.Ordinal) == true, $"The continuation ran on {completionThread}.");
        if (fails)
        {
            Assert.Same(thrown, Eventually.Faults(waiting));
            Assert.Same(thrown, Eventually.Faults(index.Completion));
        }
        else
        {
            Eventually.Completes(waiting);
            Eventually.Completes(index.Completion);
        }

        static Task<string?> Follow(Task task) =>
            task.ContinueWith(
                static _ => Thread.CurrentThread.Name,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
    }

    private static string? ThreadOf(Task<string?> continuation)
    {
        Eventually.Completes(continuation);
        return continuation.GetAwaiter().GetResult();
    }
}
