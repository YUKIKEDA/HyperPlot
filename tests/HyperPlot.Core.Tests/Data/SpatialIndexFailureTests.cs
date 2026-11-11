namespace HyperPlot.Tests;

/// <summary>
/// A build of a spatial tile index that throws: the state, <see cref="SpatialIndex.Fault"/>,
/// <see cref="SpatialIndex.Completion"/>, the snapshot that stays, and when the index builds again (design §3.4
/// "状態と公開", rule 4 and the failure; row C3a).
/// </summary>
[Collection("Large data")]
public sealed class SpatialIndexFailureTests
{
    /// <summary>
    /// An exception of the user's scale or of a chunk's read moves the index to Failed, Fault keeps it, Completion
    /// ends with it, the current snapshot stays, and the index builds again only after a column's version moved.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 5,000 seeded float points on a pool of 1 thread, with a transform of x that returns its values unchanged
    ///   until it is told to throw.
    /// - <paramref name="fromTheScale"/>: the transform throws an exception of the test; otherwise the read of chunk 0
    ///   of x on a worker throws <see cref="IOException"/>.
    /// - <paramref name="whileWriting"/>: the throwing begins at the end of the Table job, so that a Write job meets
    ///   it; otherwise it is there from the start, so that a job of the counting meets it.
    ///
    /// Steps:
    /// - Start the build and wait for Completion.
    /// - Read the state, Fault, the version, and the current snapshot; wait until the copy of the build is released.
    /// - Stop the throwing, call Acquire, and call WhenComplete.
    /// - Keep the worker in a job of another index, call Invalidate on y (<paramref name="invalidateY"/>) or on x,
    ///   call Acquire and WhenComplete, let the worker go, and wait for the task.
    ///
    /// Expected:
    /// - Completion is faulted with the exception that Fault holds: the exception of the test, or an IOException.
    /// - The state is Failed and IsComplete is false. The current snapshot is the one from before the failure and is
    ///   provisional: the empty one at the version 0 for a failure of the counting, the coarse one at the version 1
    ///   for a failure of the writing; it still answers Count (0 inside and 5,000 touching, or 5,000 inside). The copy
    ///   of the build is released.
    /// - With the versions as they were, Acquire leaves the state Failed, WhenComplete is faulted with Fault, and
    ///   Allocate ran once.
    /// - After Invalidate, Acquire moves the state to Waiting and WhenComplete does not end while the worker is kept.
    /// - Then the task ends, the state is Complete, IsComplete is true, Allocate ran twice, and the snapshot agrees
    ///   with the brute force. Fault and Completion still hold the first exception.
    /// </remarks>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    public void Build_ScaleOrChunkThrows_FailsAndBuildsAgainOnlyAfterAVersionMoved(bool fromTheScale, bool whileWriting, bool invalidateY)
    {
        using var rig = new SpatialRig(seed: 56);
        var thrown = new InvalidOperationException("The scale of the test threw.");
        var scale = new ThrowingScale();
        void Arm(bool on)
        {
            if (fromTheScale)
            {
                scale.Thrown = on ? thrown : null;
            }
            else
            {
                rig.XStorage.FailOn(0, on);
            }
        }

        if (whileWriting)
        {
            rig.Probe.AfterJob = (job, _) =>
            {
                if (job == SpatialJob.Table)
                {
                    Arm(true);
                }
            };
        }
        else
        {
            Arm(true);
        }

        var index = rig.Start(new SpatialIndexInput { Rows = RowRange.All, XTransform = scale });
        var fault = Eventually.Faults(index.Completion);
        rig.Probe.AfterJob = null;

        Assert.Same(fault, index.Fault);
        if (fromTheScale)
        {
            Assert.Same(thrown, fault);
        }
        else
        {
            Assert.IsType<IOException>(fault);
        }

        Assert.Equal(SpatialIndexState.Failed, index.State);
        Assert.False(index.IsComplete);
        Assert.Equal(whileWriting ? 1 : 0, index.Version);
        Assert.Equal(whileWriting ? 1 : 0, rig.Probe.CountOf(SpatialJob.Table));
        Assert.Equal(0, rig.Probe.CountOf(SpatialJob.PublishFull));
        var request = Assert.Single(rig.Allocator.Requests);
        Eventually.True(() => request.Copy!.IsDisposed, "the copy of the failed build to be released");

        Arm(false);
        var kept = index.Acquire();
        Assert.Equal(whileWriting ? SpatialStage.Coarse : SpatialStage.None, kept.Stage);
        Assert.True(kept.IsProvisional);
        Assert.Equal(whileWriting ? new SpatialCount(5_000, 0) : new SpatialCount(0, 5_000), kept.Count(SpatialReference.Everything, 0));
        Assert.Equal(SpatialIndexState.Failed, index.State);
        Assert.Same(fault, Eventually.Faults(index.WhenComplete()));
        Assert.Equal(SpatialIndexState.Failed, index.State);
        Assert.Equal(1, rig.Probe.CountOf(SpatialJob.Allocate));

        Task waiting;
        using (rig.BlockWorker())
        {
            (invalidateY ? rig.Y : rig.X).Invalidate();
            Assert.Equal(SpatialIndexState.Failed, index.State);
            var again = index.Acquire();
            Assert.Same(kept, again);
            again.Release();
            Assert.Equal(SpatialIndexState.Waiting, index.State);
            waiting = index.WhenComplete();
            Assert.False(waiting.IsCompleted);
        }

        kept.Release();
        Eventually.Completes(waiting);
        Assert.Equal(SpatialIndexState.Complete, index.State);
        Assert.True(index.IsComplete);
        Assert.Equal(2, rig.Probe.CountOf(SpatialJob.Allocate));
        Assert.Equal(whileWriting ? 3 : 2, index.Version);
        var full = index.Acquire();
        Assert.False(full.IsProvisional);
        rig.Check(full);
        full.Release();

        Assert.Same(fault, index.Fault);
        Assert.Same(fault, Eventually.Faults(index.Completion));
        Assert.True(index.WhenComplete().IsCompletedSuccessfully);
    }

    /// <summary>A second failure leaves the first exception in Fault and in Completion, and WhenComplete gives the second.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 5,000 seeded float points on a pool of 1 thread whose build failed with a first exception of the transform of
    ///   x.
    ///
    /// Steps:
    /// - Make the transform throw a second exception, keep the worker in a job of another index, call Invalidate on
    ///   x and WhenComplete, let the worker go, and wait for the task.
    /// - Call WhenComplete again.
    /// - Stop the throwing, call Invalidate on x, and wait for WhenComplete.
    ///
    /// Expected:
    /// - The task of the wait is faulted with the second exception: the failure that came while it waited.
    /// - Fault and Completion hold the first exception, and the state is Failed after two runs of Allocate.
    /// - WhenComplete, called after the failure with the versions as they are, is faulted with the second exception
    ///   too: the one of the last build, not Fault.
    /// - After the last Invalidate the index is complete at the version 2, and Fault still holds the first exception.
    /// </remarks>
    [Fact]
    public void Build_FailsASecondTime_KeepsTheFirstFault()
    {
        using var rig = new SpatialRig(seed: 57);
        var first = new InvalidOperationException("The first exception of the test.");
        var second = new NotSupportedException("The second exception of the test.");
        var scale = new ThrowingScale(first);
        var index = rig.Start(new SpatialIndexInput { Rows = RowRange.All, XTransform = scale });
        Assert.Same(first, Eventually.Faults(index.Completion));

        scale.Thrown = second;
        Task waiting;
        using (rig.BlockWorker())
        {
            rig.X.Invalidate();
            waiting = index.WhenComplete();
            Assert.False(waiting.IsCompleted);
        }

        Assert.Same(second, Eventually.Faults(waiting));
        Assert.Same(first, index.Fault);
        Assert.Same(first, Eventually.Faults(index.Completion));
        Assert.Equal(SpatialIndexState.Failed, index.State);
        Assert.Equal(2, rig.Probe.CountOf(SpatialJob.Allocate));
        Assert.Same(second, Eventually.Faults(index.WhenComplete()));

        scale.Thrown = null;
        rig.X.Invalidate();
        Eventually.Completes(index.WhenComplete());
        Assert.True(index.IsComplete);
        Assert.Equal(2, index.Version);
        Assert.Same(first, index.Fault);
        var full = index.Acquire();
        rig.Check(full);
        full.Release();
    }

    /// <summary>A rebuild that fails leaves the old full snapshot as the current one, and Completion as it ended.</summary>
    /// <remarks>
    /// Preconditions:
    /// - The complete index of 5,000 seeded float points on a pool of 1 thread, with a transform of x that returns its
    ///   values unchanged, and its full snapshot, borrowed and read.
    ///
    /// Steps:
    /// - Make the transform throw, call Invalidate on x, and wait for WhenComplete.
    /// - Call Acquire and read every point.
    /// - Stop the throwing, call Invalidate on x, and wait for WhenComplete.
    ///
    /// Expected:
    /// - The task is faulted with the exception of the transform, which Fault holds. The state is Failed.
    /// - Completion still ended without an exception: it ended with the first build.
    /// - Acquire returns the old snapshot, at the version 2. It is provisional, is not freed, and reads the points it
    ///   read before. The copy of the failed build is released; the copy of the first build is not.
    /// - After the last Invalidate the index is complete at the version 3 with another snapshot, and Fault still holds
    ///   the exception.
    /// </remarks>
    [Fact]
    public void Rebuild_Fails_KeepsTheOldFullSnapshot()
    {
        using var rig = new SpatialRig(seed: 58);
        var scale = new ThrowingScale();
        var index = rig.Build(new SpatialIndexInput { Rows = RowRange.All, XTransform = scale });
        var old = index.Acquire();
        rig.Check(old);
        var before = SpatialReference.ReadAll(old);

        var thrown = new InvalidOperationException("The scale of the test threw.");
        scale.Thrown = thrown;
        rig.X.Invalidate();
        Assert.Same(thrown, Eventually.Faults(index.WhenComplete()));

        Assert.Same(thrown, index.Fault);
        Assert.Equal(SpatialIndexState.Failed, index.State);
        Assert.False(index.IsComplete);
        Assert.True(index.Completion.IsCompletedSuccessfully);
        var again = index.Acquire();
        Assert.Same(old, again);
        again.Release();
        Assert.Equal(2, index.Version);
        Assert.True(old.IsProvisional);
        Assert.False(old.IsFreed);
        SpatialReference.AssertSamePoints(before, SpatialReference.ReadAll(old));
        var requests = rig.Allocator.Requests;
        Assert.Equal(2, requests.Length);
        Eventually.True(() => requests[1].Copy!.IsDisposed, "the copy of the failed rebuild to be released");
        Assert.False(requests[0].Copy!.IsDisposed);

        scale.Thrown = null;
        rig.X.Invalidate();
        Eventually.Completes(index.WhenComplete());
        Assert.True(index.IsComplete);
        Assert.Equal(3, index.Version);
        Assert.Same(thrown, index.Fault);
        var fresh = index.Acquire();
        Assert.NotSame(old, fresh);
        rig.Check(fresh);
        fresh.Release();
        old.Release();
        Eventually.True(() => old.IsFreed, "the old snapshot to be freed after its last reference was given back");
    }
}
