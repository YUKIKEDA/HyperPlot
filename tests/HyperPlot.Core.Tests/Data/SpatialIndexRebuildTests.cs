namespace HyperPlot.Tests;

/// <summary>
/// The rebuild of a spatial tile index after a column changed: who queues it, what answers until it is done, and
/// what a change during a build does (design §3.4 "状態と公開", rules 2 and 3; row C3a).
/// </summary>
[Collection("Large data")]
public sealed class SpatialIndexRebuildTests
{
    /// <summary>
    /// After Invalidate, Acquire queues the rebuild, the old full snapshot answers as provisional until the new one
    /// is published, no coarse snapshot is published, and IsComplete goes from false to true.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The complete index of 5,000 seeded float points on a pool of 1 thread, and its full snapshot, borrowed and
    ///   checked against the columns. A probe notes the current snapshot at the end of each job.
    /// - The worker is then kept in a job of another index.
    ///
    /// Steps:
    /// - Write v / 2 + 1 over every value of x or of y (<paramref name="onY"/>) and call Invalidate on that column.
    /// - Read IsProvisional of the borrowed snapshot, and IsComplete and the state of the index.
    /// - Call Acquire, read the state and the version, and read every point of the borrowed snapshot.
    /// - Call WhenComplete, let the worker go, and wait for the task.
    /// - Acquire the new snapshot, then give the old one back.
    ///
    /// Expected:
    /// - After Invalidate and before any read of the index: the borrowed snapshot is provisional, IsComplete is
    ///   false, and the state is still Complete.
    /// - Acquire returns the same snapshot and moves the state to Waiting. The version stays 2, the build has run
    ///   Allocate once, and the snapshot reads the points it read before the change.
    /// - After the task: IsComplete is true, the state is Complete, the version is 3, and Allocate ran twice.
    /// - At the end of the second PublishCoarse the current snapshot was still the old full one, at the version 2.
    /// - The new snapshot is another object, is not provisional, and agrees with the brute force over the new values.
    /// - The old snapshot is still provisional, reads the same points, and is not freed until it is given back; then it is.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Acquire_AfterInvalidate_QueuesTheRebuildAndTheOldSnapshotAnswers(bool onY)
    {
        using var rig = new SpatialRig(seed: 54);
        rig.Watch();
        var index = rig.Build();
        var old = index.Acquire();
        rig.Check(old);
        var before = SpatialReference.ReadAll(old);
        var counted = old.Count(new SpatialRect(10, 60, 100, 700), 32);

        Task waiting;
        using (rig.BlockWorker())
        {
            var values = onY ? rig.Ys : rig.Xs;
            for (var row = 0; row < values.Length; row++)
            {
                values[row] = (values[row] / 2) + 1;
            }

            (onY ? rig.Y : rig.X).Invalidate();

            Assert.True(old.IsProvisional);
            Assert.False(index.IsComplete);
            Assert.Equal(SpatialIndexState.Complete, index.State);

            var again = index.Acquire();
            Assert.Same(old, again);
            again.Release();
            Assert.Equal(SpatialIndexState.Waiting, index.State);
            Assert.Equal(2, index.Version);
            Assert.Equal(1, rig.Probe.CountOf(SpatialJob.Allocate));
            SpatialReference.AssertSamePoints(before, SpatialReference.ReadAll(old));
            Assert.Equal(counted, old.Count(new SpatialRect(10, 60, 100, 700), 32));

            waiting = index.WhenComplete();
            Assert.False(waiting.IsCompleted);
        }

        Eventually.Completes(waiting);
        Assert.True(index.IsComplete);
        Assert.Equal(SpatialIndexState.Complete, index.State);
        Assert.Equal(3, index.Version);
        Assert.Equal(2, rig.Probe.CountOf(SpatialJob.Allocate));
        Assert.Null(index.Fault);

        var coarse = rig.Sights.Where(sight => sight.Job == SpatialJob.PublishCoarse).ToArray();
        var versions = rig.Probe.Steps.Where(step => step.Job == SpatialJob.PublishCoarse).Select(step => step.Version);
        Assert.Equal(2, coarse.Length);
        Assert.Equal(SpatialStage.Coarse, coarse[0].Stage);
        Assert.Same(old, coarse[1].Snapshot);
        Assert.Equal((long[])[1, 2], versions);

        var fresh = index.Acquire();
        Assert.NotSame(old, fresh);
        Assert.False(fresh.IsProvisional);
        rig.Check(fresh);
        fresh.Release();

        Assert.True(old.IsProvisional);
        Assert.False(old.IsFreed);
        SpatialReference.AssertSamePoints(before, SpatialReference.ReadAll(old));
        old.Release();
        Eventually.True(() => old.IsFreed, "the old snapshot to be freed after its last reference was given back");
    }

    /// <summary>An Invalidate during a build lets the build run to its end, which then queues the rebuild itself.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 5,000 seeded float points on a pool of 1 thread whose worker is kept at the end of PublishCoarse of the
    ///   first build. A probe notes the state, the version, and the current snapshot at the end of each job.
    ///
    /// Steps:
    /// - While the worker is kept, write v / 2 + 1 over every value of x or of y (<paramref name="onY"/>), call
    ///   Invalidate on that column, and call Acquire.
    /// - Let the worker go and wait for Completion.
    ///
    /// Expected:
    /// - Acquire returns the coarse snapshot, and the state stays Counting: a build that runs is not queued again.
    /// - The first build runs every stage: at the end of its PublishFull the state is Waiting, the version is 2, the
    ///   current snapshot is full and provisional, and Completion has not ended.
    /// - A second build follows without any call: Allocate ran twice. It publishes no coarse snapshot (the version is
    ///   2 at the end of its PublishCoarse), and at the end of its PublishFull the state is Complete, the version is
    ///   3, and Completion has not ended yet: it ends once that job is reported, and the wait for it finds both
    ///   PublishFull jobs in the probe.
    /// - The last snapshot is not provisional and agrees with the brute force over the new values.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Invalidate_DuringABuild_RunsToTheEndAndBuildsAgain(bool onY)
    {
        using var rig = new SpatialRig(seed: 55);
        rig.Watch();
        var hold = rig.Probe.HoldAt(SpatialJob.PublishCoarse);
        var index = rig.Start();
        try
        {
            hold.WaitForWorker();
            var values = onY ? rig.Ys : rig.Xs;
            for (var row = 0; row < values.Length; row++)
            {
                values[row] = (values[row] / 2) + 1;
            }

            (onY ? rig.Y : rig.X).Invalidate();

            var coarse = index.Acquire();
            Assert.Equal(SpatialStage.Coarse, coarse.Stage);
            coarse.Release();
            Assert.Equal(SpatialIndexState.Counting, index.State);
        }
        finally
        {
            hold.Release();
        }

        Eventually.Completes(index.Completion);

        Assert.Equal(2, rig.Probe.CountOf(SpatialJob.Allocate));
        var steps = rig.Probe.Steps;
        var sights = rig.Sights;
        var ends = Enumerable.Range(0, steps.Length).Where(i => steps[i].Job == SpatialJob.PublishFull).ToArray();
        Assert.Equal(2, ends.Length);
        var (first, second) = (ends[0], ends[1]);

        Assert.Equal(SpatialIndexState.Waiting, steps[first].State);
        Assert.Equal(2, steps[first].Version);
        Assert.Equal(SpatialStage.Full, sights[first].Stage);
        Assert.True(sights[first].Provisional);
        Assert.False(sights[first].Ended);

        Assert.Equal(SpatialJob.Allocate, steps[first + 1].Job);
        Assert.Equal(SpatialIndexState.Counting, steps[first + 1].State);
        Assert.Equal((long[])[1, 2], steps.Where(step => step.Job == SpatialJob.PublishCoarse).Select(step => step.Version));
        Assert.Same(sights[first].Snapshot, sights[second - 1].Snapshot);

        Assert.Equal(SpatialIndexState.Complete, steps[second].State);
        Assert.Equal(3, steps[second].Version);
        Assert.Equal(SpatialStage.Full, sights[second].Stage);
        Assert.False(sights[second].Provisional);
        Assert.False(sights[second].Ended);
        Assert.Equal(second, steps.Length - 1);

        Assert.True(index.IsComplete);
        Assert.Equal(3, index.Version);
        var last = index.Acquire();
        Assert.False(last.IsProvisional);
        rig.Check(last);
        last.Release();
    }
}
