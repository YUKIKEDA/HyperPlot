namespace HyperPlot.Tests;

/// <summary>
/// The lifetime of a spatial tile index and of its snapshots: the count of references, who frees a snapshot, Dispose in
/// each state, and the disposal of the pool (design §3.4 "参照の数", "寿命", "プールの破棄", and rule 5 of "状態と公開";
/// row C3a).
/// </summary>
[Collection("Large data")]
public sealed class SpatialIndexLifetimeTests
{
    /// <summary>
    /// A borrowed snapshot is read after it was replaced and after Dispose, and a worker frees it after its last
    /// reference was given back.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The complete index of 5,000 seeded float points on a pool of 1 thread, with its full snapshot borrowed twice.
    ///
    /// Steps:
    /// - Write v / 2 + 1 over every x, call Invalidate on x, wait for WhenComplete, and borrow the new snapshot.
    /// - Read every point of the old snapshot, give one of its references back, and read every point again.
    /// - Keep the worker in a job of another index, give the last reference back, and read IsFreed; let the worker go.
    /// - Dispose the index, check the new snapshot against the columns, and give it back.
    ///
    /// Expected:
    /// - The two borrows of the complete index are the same object, and the new snapshot is another.
    /// - The old snapshot reads the points it read before the change, with two references and with one, and is not freed.
    /// - While the worker is kept, the old snapshot is not freed after its last reference went back: the thread that
    ///   gives it back does not free it. Once the worker goes on, the snapshot is freed and its copy released.
    /// - After Dispose the new snapshot is not freed, agrees with the brute force, and its copy is not released. After
    ///   its reference went back, it is freed and its copy released.
    /// </remarks>
    [Fact]
    public void Snapshot_BorrowedAcrossAReplacementAndDispose_IsFreedByAWorkerAfterItsLastRelease()
    {
        using var rig = new SpatialRig(seed: 63);
        var index = rig.Build();
        var old = index.Acquire();
        var again = index.Acquire();
        Assert.Same(old, again);
        var before = SpatialReference.ReadAll(old);

        for (var row = 0; row < rig.Xs.Length; row++)
        {
            rig.Xs[row] = (rig.Xs[row] / 2) + 1;
        }

        rig.X.Invalidate();
        Eventually.Completes(index.WhenComplete());
        var fresh = index.Acquire();
        Assert.NotSame(old, fresh);
        var requests = rig.Allocator.Requests;
        Assert.Equal(2, requests.Length);

        Assert.False(old.IsFreed);
        SpatialReference.AssertSamePoints(before, SpatialReference.ReadAll(old));
        again.Release();
        Assert.False(old.IsFreed);
        SpatialReference.AssertSamePoints(before, SpatialReference.ReadAll(old));
        using (rig.BlockWorker())
        {
            old.Release();
            Assert.False(old.IsFreed);
            Assert.False(requests[0].Copy!.IsDisposed);
        }

        Eventually.True(() => old.IsFreed, "the replaced snapshot to be freed");
        Eventually.True(() => requests[0].Copy!.IsDisposed, "the copy of the replaced snapshot to be released");

        index.Dispose();
        Assert.False(fresh.IsFreed);
        Assert.False(requests[1].Copy!.IsDisposed);
        rig.Check(fresh);
        fresh.Release();
        Eventually.True(() => fresh.IsFreed, "the last snapshot to be freed");
        Eventually.True(() => requests[1].Copy!.IsDisposed, "the copy of the last snapshot to be released");
    }

    /// <summary>Once the pool is disposed, the thread that gives the last reference back frees the snapshot.</summary>
    /// <remarks>
    /// Preconditions:
    /// - The complete index of 5,000 seeded float points on a pool of 1 thread, with its full snapshot borrowed.
    ///
    /// Steps:
    /// - Dispose the pool, then the index; check the snapshot against the columns after each.
    /// - Give the reference back and read IsFreed at once.
    ///
    /// Expected:
    /// - The snapshot agrees with the brute force after both disposals and is not freed.
    /// - Right after Release, on the same thread, the snapshot is freed and its copy released.
    /// </remarks>
    [Fact]
    public void Snapshot_ReleasedAfterThePoolWasDisposed_IsFreedByTheCallingThread()
    {
        using var rig = new SpatialRig(seed: 64);
        var index = rig.Build();
        var snapshot = index.Acquire();
        var copy = Assert.Single(rig.Allocator.Requests).Copy!;

        rig.Pool.Dispose();
        rig.Check(snapshot);
        index.Dispose();
        rig.Check(snapshot);
        Assert.False(snapshot.IsFreed);
        Assert.False(copy.IsDisposed);

        snapshot.Release();
        Assert.True(snapshot.IsFreed);
        Assert.True(copy.IsDisposed);
    }

    /// <summary>Acquire, raced against 300 replacements of the current snapshot, never returns a freed snapshot.</summary>
    /// <remarks>
    /// Preconditions:
    /// - The complete index of 500 seeded float points on a pool of 2 threads, and every point of its snapshot, read.
    /// - 3 threads that each loop: Acquire, read IsFreed, Count over everything at maxLevel 32, ReadPoints of all 500
    ///   points, read IsFreed again, Release. Each has ended one round before the replacements begin.
    ///
    /// Steps:
    /// - 300 times: call Invalidate on x (the values stay) and wait for WhenComplete.
    /// - Stop the threads and join them. Dispose the index.
    ///
    /// Expected:
    /// - No reader saw a freed snapshot, before or after its reads; every Count was 500 inside and 0 touching; every
    ///   ReadPoints returned the points of the first snapshot; no reader threw.
    /// - The version is 302: one publication for each replacement after the two of the first build.
    /// - After Dispose, the copy of every build is released.
    /// </remarks>
    [Fact]
    public void Acquire_RacedAgainstReplacements_NeverReturnsAFreedSnapshot()
    {
        const int Rounds = 300;
        const int Readers = 3;
        const int Points = 500;
        using var rig = new SpatialRig(Points, seed: 65, workers: 2);
        var index = rig.Build();
        var first = index.Acquire();
        var expected = SpatialReference.ReadAll(first);
        first.Release();

        long stopped = 0;
        var rounds = new long[Readers];
        var failures = new string?[Readers];
        var threads = new Thread[Readers];
        for (var i = 0; i < Readers; i++)
        {
            var slot = i;
            threads[i] = new Thread(() =>
            {
                var x = new double[Points];
                var y = new double[Points];
                var rows = new long[Points];
                try
                {
                    while (Volatile.Read(ref stopped) == 0)
                    {
                        var snapshot = index.Acquire();
                        var freedBefore = snapshot.IsFreed;
                        var count = snapshot.Count(SpatialReference.Everything, 32);
                        snapshot.ReadPoints(0, x, y, rows);
                        var same =
                            x.AsSpan().SequenceEqual(expected.X)
                            && y.AsSpan().SequenceEqual(expected.Y)
                            && rows.AsSpan().SequenceEqual(expected.Rows);
                        var freedAfter = snapshot.IsFreed;
                        snapshot.Release();
                        if (freedBefore || freedAfter || !same || count != new SpatialCount(Points, 0))
                        {
                            failures[slot] = $"freed before {freedBefore}, freed after {freedAfter}, same points {same}, count {count}";
                            return;
                        }

                        Interlocked.Increment(ref rounds[slot]);
                    }
                }
                catch (Exception exception)
                {
                    failures[slot] = exception.ToString();
                }
            });
            threads[i].Start();
        }

        try
        {
            Eventually.True(
                () => rounds.All(count => Interlocked.Read(ref count) > 0) || failures.Any(f => f is not null),
                "every reader to end a round"
            );
            for (var round = 0; round < Rounds; round++)
            {
                rig.X.Invalidate();
                Eventually.Completes(index.WhenComplete());
            }
        }
        finally
        {
            Volatile.Write(ref stopped, 1);
            foreach (var thread in threads)
            {
                Assert.True(thread.Join(30_000), "A reader did not end within the watchdog.");
            }
        }

        Assert.All(failures, Assert.Null);
        Assert.Equal(2 + Rounds, index.Version);
        index.Dispose();
        var requests = rig.Allocator.Requests;
        Assert.Equal(1 + Rounds, requests.Length);
        Eventually.True(() => requests.All(request => request.Copy!.IsDisposed), "the copy of every build to be released");
    }

    /// <summary>
    /// Dispose during a build waits for the job in flight, withdraws the jobs that are left, frees the copy of the
    /// build, ends the unfinished tasks with ObjectDisposedException, and leaves a borrowed snapshot readable.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 5,000 seeded float points on a pool of 1 thread whose worker is kept at the end of the job
    ///   <paramref name="held"/> of the first build: First and Write are unit jobs, PublishCoarse and Table single ones.
    /// - The state is Counting (First, PublishCoarse) or Writing (Table, Write), and the version is 0 (First) or 1.
    /// - The current snapshot is borrowed, and a task of WhenComplete is taken.
    ///
    /// Steps:
    /// - Call Dispose on another thread, and wait until the state is Disposed.
    /// - Let the worker go; inside the same job, note the state, whether the call of Dispose has returned, and the
    ///   number of jobs that have ended. Wait for Dispose.
    /// - Read the index, the tasks, the copy, and the borrowed snapshot; give the snapshot back; call Dispose again.
    /// - Dispose the pool, which joins the worker, and count the jobs.
    ///
    /// Expected:
    /// - Inside the kept job the state is Disposed and Dispose has not returned: it waits for the job in flight.
    /// - After Dispose: the version is as it was, Fault is null, Completion and the task taken before are faulted with
    ///   ObjectDisposedException, WhenComplete returns a task faulted with it, and Acquire throws it.
    /// - The copy of the build is released when Dispose returns.
    /// - The borrowed snapshot still answers: the empty one (0 inside, 5,000 touching) for First, the coarse one (5,000
    ///   inside) for the others.
    /// - The second Dispose changes nothing, no job ended after the kept one, and PublishFull never ran.
    /// - Fault is still null after the disposal of the pool: the pool does not set the Fault of an index that was
    ///   disposed before it.
    /// </remarks>
    [Theory]
    [InlineData("First")]
    [InlineData("PublishCoarse")]
    [InlineData("Table")]
    [InlineData("Write")]
    public void Dispose_DuringABuild_WaitsForTheJobInFlightAndEndsTheBuild(string held)
    {
        var job = Enum.Parse<SpatialJob>(held);
        using var rig = new SpatialRig(seed: 66);
        var hold = rig.Probe.HoldAt(job);
        var keep = rig.Probe.AfterJob!;
        Task? disposing = null;
        (SpatialIndexState State, bool Returned, int Jobs)? inJob = null;
        rig.Probe.AfterJob = (ended, index) =>
        {
            keep(ended, index);
            if (ended == job)
            {
                inJob ??= (index.State, disposing!.IsCompleted, rig.Probe.Steps.Length);
            }
        };

        var index = rig.Start();
        SpatialSnapshot borrowed;
        Task waiting;
        long version;
        try
        {
            hold.WaitForWorker();
            Assert.Equal(job is SpatialJob.Table or SpatialJob.Write ? SpatialIndexState.Writing : SpatialIndexState.Counting, index.State);
            version = index.Version;
            Assert.Equal(job == SpatialJob.First ? 0 : 1, version);
            borrowed = index.Acquire();
            waiting = index.WhenComplete();
            Assert.False(waiting.IsCompleted);
            disposing = Task.Run(index.Dispose, TestContext.Current.CancellationToken);
            Eventually.True(() => index.State == SpatialIndexState.Disposed, "Dispose to set the state");
        }
        finally
        {
            hold.Release();
        }

        Eventually.Completes(disposing);
        Assert.NotNull(inJob);
        Assert.Equal((SpatialIndexState.Disposed, false), (inJob.Value.State, inJob.Value.Returned));

        AssertDisposed(index);
        Assert.Equal(version, index.Version);
        Assert.Null(index.Fault);
        Assert.IsType<ObjectDisposedException>(Eventually.Faults(index.Completion));
        Assert.IsType<ObjectDisposedException>(Eventually.Faults(waiting));
        Assert.True(Assert.Single(rig.Allocator.Requests).Copy!.IsDisposed);

        Assert.Equal(job == SpatialJob.First ? SpatialStage.None : SpatialStage.Coarse, borrowed.Stage);
        Assert.Equal(
            job == SpatialJob.First ? new SpatialCount(0, 5_000) : new SpatialCount(5_000, 0),
            borrowed.Count(SpatialReference.Everything, 0)
        );
        borrowed.Release();

        index.Dispose();
        AssertDisposed(index);
        Assert.Equal(version, index.Version);
        rig.Pool.Dispose();
        Assert.Equal(inJob.Value.Jobs, rig.Probe.Steps.Length);
        Assert.Equal(0, rig.Probe.CountOf(SpatialJob.PublishFull));
        Assert.Null(index.Fault);
    }

    /// <summary>
    /// Dispose of an index that no job runs for: one that waits for a worker begins no build, and one that is complete
    /// or failed keeps how its tasks ended.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 5,000 seeded float points on a pool of 1 thread, in the state <paramref name="state"/>:
    ///   Waiting (the worker is kept in a job of another index), Complete, or Failed (the transform of x threw).
    /// - The current snapshot is borrowed, and a task of WhenComplete is taken.
    ///
    /// Steps:
    /// - Call Dispose twice. Read the index, the tasks, and the borrowed snapshot, and give the snapshot back.
    /// - Let the worker go (Waiting), dispose the pool, count the jobs, and read Fault.
    ///
    /// Expected:
    /// - After Dispose, in every state: the state is Disposed, the version is as it was, WhenComplete returns a task
    ///   faulted with ObjectDisposedException, and Acquire throws it.
    /// - Waiting: Completion and the task taken before are faulted with ObjectDisposedException, Fault is null, the
    ///   borrowed snapshot is the empty one, and no job of the index ever ran: no copy was asked for.
    /// - Complete: Completion and the task taken before ended without an exception, Fault is null, and the borrowed
    ///   full snapshot agrees with the brute force and is not freed, which it would be if the second Dispose gave the
    ///   reference of the index back again; after its own reference went back it is freed and its copy released.
    /// - Failed: Completion, the task taken before, and Fault hold the exception of the transform.
    /// - After the disposal of the pool, Allocate ran as often as before Dispose, and Fault is as it was: the pool
    ///   does not set the Fault of an index that was disposed before it.
    /// </remarks>
    [Theory]
    [InlineData("Waiting")]
    [InlineData("Complete")]
    [InlineData("Failed")]
    public void Dispose_WhileNoJobRuns_EndsWhatWaitsAndKeepsWhatEnded(string state)
    {
        var from = Enum.Parse<SpatialIndexState>(state);
        using var rig = new SpatialRig(seed: 67);
        var thrown = new InvalidOperationException("The scale of the test threw.");
        var input = new SpatialIndexInput { Rows = RowRange.All, XTransform = new ThrowingScale(from == SpatialIndexState.Failed ? thrown : null) };
        var blocker = from == SpatialIndexState.Waiting ? rig.BlockWorker() : null;
        try
        {
            var index = rig.Start(input);
            if (from == SpatialIndexState.Complete)
            {
                Eventually.Completes(index.Completion);
            }
            else if (from == SpatialIndexState.Failed)
            {
                Assert.Same(thrown, Eventually.Faults(index.Completion));
            }

            Assert.Equal(from, index.State);
            var version = index.Version;
            var allocations = rig.Probe.CountOf(SpatialJob.Allocate);
            var borrowed = index.Acquire();
            var waiting = index.WhenComplete();

            index.Dispose();
            index.Dispose();

            AssertDisposed(index);
            Assert.Equal(version, index.Version);
            switch (from)
            {
                case SpatialIndexState.Waiting:
                    Assert.IsType<ObjectDisposedException>(Eventually.Faults(index.Completion));
                    Assert.IsType<ObjectDisposedException>(Eventually.Faults(waiting));
                    Assert.Null(index.Fault);
                    Assert.Equal(SpatialStage.None, borrowed.Stage);
                    Assert.Equal(new SpatialCount(0, 5_000), borrowed.Count(SpatialReference.Everything, 0));
                    borrowed.Release();
                    break;
                case SpatialIndexState.Complete:
                    Eventually.Completes(index.Completion);
                    Eventually.Completes(waiting);
                    Assert.Null(index.Fault);
                    var copy = Assert.Single(rig.Allocator.Requests).Copy!;
                    rig.Check(borrowed);
                    Assert.False(borrowed.IsFreed);
                    Assert.False(copy.IsDisposed);
                    borrowed.Release();
                    Eventually.True(() => borrowed.IsFreed, "the snapshot of the disposed index to be freed");
                    Eventually.True(() => copy.IsDisposed, "the copy of the disposed index to be released");
                    break;
                default:
                    Assert.Same(thrown, Eventually.Faults(index.Completion));
                    Assert.Same(thrown, Eventually.Faults(waiting));
                    Assert.Same(thrown, index.Fault);
                    Assert.Equal(SpatialStage.None, borrowed.Stage);
                    borrowed.Release();
                    break;
            }

            blocker?.Dispose();
            rig.Pool.Dispose();
            Assert.Equal(allocations, rig.Probe.CountOf(SpatialJob.Allocate));
            Assert.Same(from == SpatialIndexState.Failed ? thrown : null, index.Fault);
            if (from == SpatialIndexState.Waiting)
            {
                Assert.Empty(rig.Probe.Steps);
                Assert.Empty(rig.Allocator.Requests);
            }
        }
        finally
        {
            blocker?.Dispose();
        }
    }

    /// <summary>
    /// Dispose, raced against the jobs that publish, lets no snapshot become the current one once the state is Disposed,
    /// and leaves no copy behind (rule 5).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 2,000 seeded float points (8 units a stage) on a pool of 2 threads.
    /// - 200 rounds, each with an index of its own. A round calls Dispose on a thread of the .NET pool once a chosen
    ///   job has ended, after a pause of a few spins: the last First (so that Dispose meets PublishCoarse) in the even
    ///   rounds, and one of the eight Write jobs (so that it meets PublishFull) in the odd ones. The job and the pause
    ///   change from round to round, so that Dispose lands before, inside, and after the publishing job. The test does
    ///   not need a round to land inside: the mutation check of the row shows that rounds do on the machine of the row.
    ///
    /// Steps:
    /// - In each round, spin until the state is Disposed and read the version; wait for Dispose and read it again.
    /// - Call Acquire, and wait for the copies of the round.
    /// - After the last round, dispose the pool and count the jobs of every round again.
    ///
    /// Expected:
    /// - The version read once the state was Disposed is the version after Dispose: nothing was published between.
    /// - Acquire throws ObjectDisposedException.
    /// - The copy of the round is released, whether Dispose freed it with the build, a publishing job freed the
    ///   snapshot it could not publish, or the published snapshot went back to a worker.
    /// - No job of a round ended after its Dispose returned.
    /// </remarks>
    [Fact]
    public void Dispose_RacedAgainstThePublishingJobs_PublishesNothingOnceTheStateIsDisposed()
    {
        const int Rounds = 200;
        const int Units = 8;
        var (xs, ys) = SpatialData.Uniform(2_000, seed: 68);
        using var x = Column.Wrap(xs);
        using var y = Column.Wrap(ys);
        var pool = new PlotWorkerPool(2);
        var ended = new (SpatialProbe Probe, int Jobs)[Rounds];
        try
        {
            for (var round = 0; round < Rounds; round++)
            {
                var trigger = round % 2 == 0 ? SpatialJob.First : SpatialJob.Write;
                var nth = round % 2 == 0 ? Units : 1 + ((round / 2) % Units);
                var pause = ((round / 16) % 12) * 40;
                var probe = new SpatialProbe();
                var allocator = new RecordingCopyAllocator();
                var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var seen = 0;
                probe.AfterJob = (job, _) =>
                {
                    if (job == trigger && Interlocked.Increment(ref seen) == nth)
                    {
                        reached.TrySetResult();
                    }
                };
                var index = SpatialReference.Start(pool, x, y, new SpatialIndexInput { Rows = RowRange.All }, allocator: allocator, probe: probe);
                var disposing = reached.Task.ContinueWith(
                    _ =>
                    {
                        Thread.SpinWait(pause);
                        index.Dispose();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default
                );

                // A spin without a pause reads the version while the job that Dispose met is still in flight; the wait
                // after it covers a round that the spins did not.
                for (var spin = 0; spin < 20_000_000 && index.State != SpatialIndexState.Disposed; spin++)
                {
                    Thread.SpinWait(1);
                }

                Eventually.True(() => index.State == SpatialIndexState.Disposed, $"Dispose of round {round} to set the state");
                var version = index.Version;
                Eventually.Completes(disposing);
                if (index.Version != version)
                {
                    Assert.Fail($"Round {round}: the version went from {version} to {index.Version} after the state was Disposed.");
                }

                Assert.Throws<ObjectDisposedException>(() => index.Acquire());
                ended[round] = (probe, probe.Steps.Length);
                var requests = allocator.Requests;
                Assert.Single(requests);
                Eventually.True(() => requests[0].Copy!.IsDisposed, $"the copy of round {round} to be released");
            }
        }
        finally
        {
            pool.Dispose();
        }

        for (var round = 0; round < Rounds; round++)
        {
            if (ended[round].Probe.Steps.Length != ended[round].Jobs)
            {
                Assert.Fail($"Round {round}: {ended[round].Probe.Steps.Length - ended[round].Jobs} jobs ended after Dispose returned.");
            }
        }
    }

    /// <summary>
    /// The disposal of the pool gives a complete index ObjectDisposedException as its Fault and leaves the first
    /// exception of a failed one; nothing is built again, and the current snapshot stays.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 5,000 seeded float points on a pool of 1 thread, with a transform of x. The index is complete, or, when
    ///   <paramref name="failed"/>, its build failed with the exception of the transform.
    /// - The current snapshot is borrowed: the full one, with every point read, or the empty one.
    ///
    /// Steps:
    /// - Dispose the pool, and read Fault, Completion, and WhenComplete.
    /// - Stop the throwing, call Invalidate on x, and call WhenComplete and Acquire.
    ///
    /// Expected:
    /// - Complete: Fault is an ObjectDisposedException, Completion still ended without an exception, and, while the
    ///   versions are as they were, WhenComplete returns an ended task. After Invalidate, WhenComplete returns a task
    ///   faulted with the exception of Fault, and the state stays Complete.
    /// - Failed: Fault and Completion still hold the exception of the transform; WhenComplete returns a task faulted
    ///   with ObjectDisposedException, before and after Invalidate; the state stays Failed.
    /// - Both: Acquire returns the borrowed snapshot, which is provisional and answers as before; the version is as it
    ///   was, and Allocate ran once.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PoolDispose_AfterABuildEnded_SetsFaultOnlyWhereThereIsNone(bool failed)
    {
        using var rig = new SpatialRig(seed: 69);
        var thrown = new InvalidOperationException("The scale of the test threw.");
        var scale = new ThrowingScale(failed ? thrown : null);
        var index = rig.Start(new SpatialIndexInput { Rows = RowRange.All, XTransform = scale });
        if (failed)
        {
            Assert.Same(thrown, Eventually.Faults(index.Completion));
        }
        else
        {
            Eventually.Completes(index.Completion);
        }

        var borrowed = index.Acquire();
        Assert.Equal(failed ? SpatialStage.None : SpatialStage.Full, borrowed.Stage);
        var before = failed ? default : SpatialReference.ReadAll(borrowed);
        var counted = borrowed.Count(SpatialReference.Everything, 0);
        var version = index.Version;

        rig.Pool.Dispose();

        if (failed)
        {
            Assert.Same(thrown, index.Fault);
            Assert.Same(thrown, Eventually.Faults(index.Completion));
            Assert.IsType<ObjectDisposedException>(Eventually.Faults(index.WhenComplete()));
            scale.Thrown = null;
            rig.X.Invalidate();
            Assert.IsType<ObjectDisposedException>(Eventually.Faults(index.WhenComplete()));
            Assert.Same(thrown, index.Fault);
        }
        else
        {
            var gone = Assert.IsType<ObjectDisposedException>(index.Fault);
            Assert.True(index.Completion.IsCompletedSuccessfully);
            Assert.True(index.IsComplete);
            Assert.True(index.WhenComplete().IsCompletedSuccessfully);
            rig.X.Invalidate();
            Assert.False(index.IsComplete);
            Assert.Same(gone, Eventually.Faults(index.WhenComplete()));
            Assert.Same(gone, index.Fault);
        }

        var again = index.Acquire();
        Assert.Same(borrowed, again);
        again.Release();
        Assert.Equal(failed ? SpatialIndexState.Failed : SpatialIndexState.Complete, index.State);
        Assert.True(borrowed.IsProvisional);
        Assert.Equal(counted, borrowed.Count(SpatialReference.Everything, 0));
        if (!failed)
        {
            SpatialReference.AssertSamePoints(before, SpatialReference.ReadAll(borrowed));
        }

        Assert.Equal(version, index.Version);
        Assert.Equal(1, rig.Probe.CountOf(SpatialJob.Allocate));
        borrowed.Release();
    }

    /// <summary>
    /// The disposal of the pool during a build ends Completion and the task of WhenComplete with the
    /// ObjectDisposedException that Fault holds, frees the copy of the build, and leaves the current snapshot.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 5,000 seeded float points on a pool of 1 thread whose worker is kept at the end of PublishCoarse of the first
    ///   build. A task of WhenComplete is taken.
    ///
    /// Steps:
    /// - Dispose the pool on another thread, wait until the pool refuses work, let the worker go, and wait for the
    ///   disposal.
    /// - Read Fault, the tasks, the jobs, and the copy; call WhenComplete and Acquire; dispose the index.
    ///
    /// Expected:
    /// - Fault is an ObjectDisposedException; Completion, the task taken before, and a task of WhenComplete taken after
    ///   are faulted with that same exception.
    /// - PublishCoarse is the last job that ended, the version is 1, and the copy of the build is released.
    /// - Acquire returns the coarse snapshot, which is provisional and counts 5,000 points inside.
    /// - After Dispose of the index, Acquire throws ObjectDisposedException.
    /// </remarks>
    [Fact]
    public void PoolDispose_DuringABuild_EndsTheTasksAndFreesTheCopy()
    {
        using var rig = new SpatialRig(seed: 70);
        var hold = rig.Probe.HoldAt(SpatialJob.PublishCoarse);
        var index = rig.Start();
        Task waiting;
        Task disposing;
        try
        {
            hold.WaitForWorker();
            waiting = index.WhenComplete();
            Assert.False(waiting.IsCompleted);
            disposing = Task.Run(rig.Pool.Dispose, TestContext.Current.CancellationToken);
            Eventually.True(() => RefusesWork(rig.Pool), "the pool to be disposed");
        }
        finally
        {
            hold.Release();
        }

        Eventually.Completes(disposing);
        var gone = Assert.IsType<ObjectDisposedException>(index.Fault);
        Assert.Same(gone, Eventually.Faults(index.Completion));
        Assert.Same(gone, Eventually.Faults(waiting));
        Assert.Same(gone, Eventually.Faults(index.WhenComplete()));
        Assert.False(index.IsComplete);
        Assert.Equal(SpatialJob.PublishCoarse, rig.Probe.Steps[^1].Job);
        Assert.Equal(1, index.Version);
        Assert.True(Assert.Single(rig.Allocator.Requests).Copy!.IsDisposed);

        var coarse = index.Acquire();
        Assert.Equal(SpatialStage.Coarse, coarse.Stage);
        Assert.True(coarse.IsProvisional);
        Assert.Equal(new SpatialCount(5_000, 0), coarse.Count(SpatialReference.Everything, 0));
        coarse.Release();

        index.Dispose();
        AssertDisposed(index);
    }

    private static void AssertDisposed(SpatialIndex index)
    {
        Assert.Equal(SpatialIndexState.Disposed, index.State);
        Assert.Throws<ObjectDisposedException>(() => index.Acquire());
        Assert.IsType<ObjectDisposedException>(Eventually.Faults(index.WhenComplete()));
    }

    // A pass of no chunk does nothing on a pool that lives, and throws on a disposed one as every entry of the pool does.
    private static bool RefusesWork(PlotWorkerPool pool)
    {
        try
        {
            _ = pool.RunChunks(0, static _ => { });
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }
}
