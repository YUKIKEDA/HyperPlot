namespace HyperPlot.Tests;

/// <summary>
/// The states of a spatial tile index through its first build, and the three snapshots it publishes on the way:
/// the empty one, the coarse one, and the full one (design §3.4 "状態と公開", row C3a).
/// </summary>
[Collection("Large data")]
public sealed class SpatialIndexStateTests
{
    /// <summary>An index that no worker has begun waits for its build and answers with the empty snapshot: nothing inside, every row touching.</summary>
    /// <remarks>
    /// Preconditions:
    /// - Two float columns of 5,000 seeded rows and the rows [<paramref name="start"/>, <paramref name="end"/>), of
    ///   which <paramref name="rows"/> are in the columns.
    /// - A pool of 1 thread whose worker is kept in a job of another index.
    ///
    /// Steps:
    /// - Call BuildSpatialIndex and read the state, the version, IsComplete, Completion, and Fault.
    /// - Acquire the snapshot and ask it its stage, its counts, Count with every value at maxLevel 32 and with a small
    ///   rectangle at maxLevel 0, GetCells, CoarseDensity, and ReadPoints of 1 point.
    /// - Let the worker go and wait for Completion.
    ///
    /// Expected:
    /// - The state is Waiting before and after the calls, the version is 0, IsComplete is false, Completion has not
    ///   ended, and Fault is null.
    /// - The snapshot has the stage None and is provisional, with 0 points and 0 excluded rows.
    /// - Both calls of Count answer 0 inside and <paramref name="rows"/> touching.
    /// - GetCells returns 0, CoarseDensity is empty, and ReadPoints throws InvalidOperationException.
    /// - After the worker is let go, the index is complete at the version 2.
    /// </remarks>
    [Theory]
    [InlineData(0, long.MaxValue, 5_000)]
    [InlineData(100, 1_100, 1_000)]
    [InlineData(4_000, 9_000, 1_000)]
    public void Acquire_BeforeAnyWorkerBegins_ReturnsTheEmptySnapshot(long start, long end, long rows)
    {
        using var rig = new SpatialRig();
        SpatialIndex index;
        using (rig.BlockWorker())
        {
            index = rig.Start(new SpatialIndexInput { Rows = new RowRange(start, end) });

            Assert.Equal(SpatialIndexState.Waiting, index.State);
            Assert.Equal(0, index.Version);
            Assert.False(index.IsComplete);
            Assert.False(index.Completion.IsCompleted);
            Assert.Null(index.Fault);

            var snapshot = index.Acquire();
            Assert.Equal(SpatialStage.None, snapshot.Stage);
            Assert.True(snapshot.IsProvisional);
            Assert.Equal(0, snapshot.PointCount);
            Assert.Equal(0, snapshot.ExcludedCount);
            Assert.Equal(new SpatialCount(0, rows), snapshot.Count(SpatialReference.Everything, 32));
            Assert.Equal(new SpatialCount(0, rows), snapshot.Count(new SpatialRect(0, 1, 0, 1), 0));
            Assert.Equal(0, snapshot.GetCells(SpatialReference.Everything, 32, new SpatialCell[4]));
            Assert.True(snapshot.CoarseDensity.IsEmpty);
            Assert.Throws<InvalidOperationException>(() => snapshot.ReadPoints(0, new double[1], new double[1], new long[1]));
            snapshot.Release();

            Assert.Equal(SpatialIndexState.Waiting, index.State);
            Assert.Equal(0, index.Version);
            Assert.Empty(rig.Probe.Steps);
        }

        Eventually.Completes(index.Completion);
        Assert.True(index.IsComplete);
        Assert.Equal(2, index.Version);
    }

    /// <summary>
    /// The first build goes Counting, Writing, Complete, and publishes the empty, the coarse, and the full snapshot in
    /// that order, with the version one more at each.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 5,000 seeded float points, 2,000 of them within one hundredth of a first-level cell, so that the build counts
    ///   again at least once. Leaves of 16 points and units of 256 rows: 20 units a stage.
    /// - A pool of 1 thread, so the jobs run one after another. A probe notes, at the end of each job, the state and
    ///   the version of the index, the current snapshot, and whether Completion has ended.
    ///
    /// Steps:
    /// - Build the index and wait for Completion.
    ///
    /// Expected:
    /// - The jobs are Allocate, 20 Range, EndRange, 20 First, PublishCoarse, then 20 Pass and EndPass for each pass
    ///   (at least one), Table, 20 Write, PublishFull.
    /// - The state at the end of every job before Table is Counting, from Table to the last Write it is Writing, and
    ///   at PublishFull it is Complete.
    /// - The version is 0 before PublishCoarse, 1 from PublishCoarse to the last Write, and 2 at PublishFull.
    /// - The current snapshot has the stage None, Coarse, and Full over the same three spans, and is provisional
    ///   until the last.
    /// - Completion has ended at the end of PublishFull and at the end of no job before it.
    /// </remarks>
    [Fact]
    public void Build_FirstBuild_PassesTheStatesAndPublishesThreeSnapshots()
    {
        var values = SpatialData.Uniform(5_000, seed: 50);
        var random = new Random(51);
        for (var row = 0; row < 2_000; row++)
        {
            values.X[row] = (float)(10 + (random.NextDouble() * 0.01));
            values.Y[row] = (float)(500 + (random.NextDouble() * 0.1));
        }

        using var rig = new SpatialRig(values);
        rig.Watch();

        var index = rig.Build();

        const int Units = 20;
        var passes = rig.Probe.CountOf(SpatialJob.EndPass);
        Assert.True(passes >= 1, "The build did not count again.");
        List<SpatialJob> jobs = [SpatialJob.Allocate, .. Enumerable.Repeat(SpatialJob.Range, Units), SpatialJob.EndRange];
        jobs.AddRange([.. Enumerable.Repeat(SpatialJob.First, Units), SpatialJob.PublishCoarse]);
        for (var pass = 0; pass < passes; pass++)
        {
            jobs.AddRange([.. Enumerable.Repeat(SpatialJob.Pass, Units), SpatialJob.EndPass]);
        }

        jobs.AddRange([SpatialJob.Table, .. Enumerable.Repeat(SpatialJob.Write, Units), SpatialJob.PublishFull]);
        var steps = rig.Probe.Steps;
        var sights = rig.Sights;
        Assert.Equal(jobs, steps.Select(step => step.Job));
        Assert.Equal(jobs, sights.Select(sight => sight.Job));

        var coarse = jobs.IndexOf(SpatialJob.PublishCoarse);
        var table = jobs.IndexOf(SpatialJob.Table);
        var last = jobs.Count - 1;
        for (var i = 0; i <= last; i++)
        {
            var state =
                i == last ? SpatialIndexState.Complete
                : i >= table ? SpatialIndexState.Writing
                : SpatialIndexState.Counting;
            var version =
                i == last ? 2
                : i >= coarse ? 1
                : 0;
            var stage =
                i == last ? SpatialStage.Full
                : i >= coarse ? SpatialStage.Coarse
                : SpatialStage.None;
            var (step, sight) = (steps[i], sights[i]);
            if (
                step.State != state
                || step.Version != version
                || sight.Stage != stage
                || sight.Provisional != (i != last)
                || sight.Ended != (i == last)
            )
            {
                Assert.Fail(
                    $"Job {i} ({jobs[i]}) ended as {step} with {sight}; the state {state}, the version {version}, and the stage {stage} were expected."
                );
            }
        }

        Assert.True(index.IsComplete);
        Assert.Equal(2, index.Version);
        Assert.NotSame(sights[coarse].Snapshot, sights[0].Snapshot);
        Assert.NotSame(sights[last].Snapshot, sights[coarse].Snapshot);
        Assert.Same(sights[coarse].Snapshot, sights[last - 1].Snapshot);
        Assert.Single(rig.Allocator.Requests);
    }

    /// <summary>One worker begins a build: the others neither begin a second one while it runs nor win the same start.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 8 threads. An index of 5,000 seeded points whose first job, Allocate, is kept from returning, so the
    ///   index stays in Counting with no job queued.
    /// - 20 other indexes of 2,000 points on the same pool, each with a probe and an allocator of its own. A worker
    ///   looks at the first index before each of their jobs, and all the free workers race to begin each of them.
    ///
    /// Steps:
    /// - Start the first index and wait until a worker is in the hold.
    /// - Build the 20 others one after another, and wait for each.
    /// - Read the state, the jobs, and the allocator of the first index, then let its worker go and wait for Completion.
    ///
    /// Expected:
    /// - Each of the 20 ran Allocate once and asked for one copy.
    /// - While held, the first index is in Counting, its only job is the one Allocate, and its allocator got one request.
    /// - When complete, it has still run Allocate once and asked for one copy, and its version is 2.
    /// </remarks>
    [Fact]
    public void Build_WhileABuildRuns_NoOtherWorkerBeginsOne()
    {
        using var rig = new SpatialRig(workers: 8);
        var hold = rig.Probe.HoldAt(SpatialJob.Allocate);
        var index = rig.Start();
        try
        {
            hold.WaitForWorker();
            for (var i = 0; i < 20; i++)
            {
                var (xs, ys) = SpatialData.Uniform(2_000, seed: 60 + i);
                using var x = Column.Wrap(xs);
                using var y = Column.Wrap(ys);
                var probe = new SpatialProbe();
                var allocator = new RecordingCopyAllocator();
                using var other = SpatialReference.Build(rig.Pool, x, y, new SpatialIndexInput { Rows = RowRange.All }, null, allocator, probe);

                Assert.True(other.IsComplete);
                Assert.Equal(1, probe.CountOf(SpatialJob.Allocate));
                Assert.Single(allocator.Requests);
            }

            Assert.Equal(SpatialIndexState.Counting, index.State);
            Assert.Equal((SpatialJob[])[SpatialJob.Allocate], rig.Probe.Steps.Select(step => step.Job));
            Assert.Single(rig.Allocator.Requests);
        }
        finally
        {
            hold.Release();
        }

        Eventually.Completes(index.Completion);
        Assert.Equal(1, rig.Probe.CountOf(SpatialJob.Allocate));
        Assert.Single(rig.Allocator.Requests);
        Assert.Equal(2, index.Version);
    }

    /// <summary>
    /// The coarse snapshot answers CoarseDensity and Count as a loop over every row does down to level 8, returns no
    /// cell, and has no point to read.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 20,000 seeded float points; every 97th row has NaN on one axis, in turn. A pool of 1 thread whose worker is
    ///   kept at the end of PublishCoarse.
    /// - 23 rectangles in differences from the origins: 20 seeded ones with ends anywhere from 10% below to 10% above
    ///   the ranges, the ranges themselves, one a millionth of the ranges wide, and every value.
    ///
    /// Steps:
    /// - Start the index, wait for the hold, and acquire the snapshot.
    /// - Compute by a loop over every row the ranges, the number of points and of excluded rows, the 256 × 256 counts
    ///   of the cells of level 8, and, for each rectangle and each maxLevel of 0 to 8, 9, 20, and 32, the points of the
    ///   cells of level min(maxLevel, 8) that lie wholly among the grid cells of the rectangle and the points of the
    ///   other cells that share a grid cell with it.
    /// - Call Count for each rectangle and level, GetCells into a buffer of marked cells, and ReadPoints of 1 point.
    /// - Let the worker go, wait for Completion, and acquire the full snapshot.
    ///
    /// Expected:
    /// - While held: the state is Counting, the version is 1, IsComplete is false; the snapshot has the stage Coarse
    ///   and is provisional, with the ranges and the two counts of the loop.
    /// - CoarseDensity is the 65,536 counts of the loop, and Count answers the two sums of the loop every time.
    /// - GetCells returns 0 and leaves the buffer as it was. ReadPoints throws InvalidOperationException.
    /// - After the build, the coarse snapshot still has the stage Coarse and answers the same counts; the full
    ///   snapshot has the same CoarseDensity and agrees with the brute force over the columns.
    /// </remarks>
    [Fact]
    public void Acquire_AfterTheCoarsePublish_AnswersDownToLevelEight()
    {
        const int Points = 20_000;
        var values = SpatialData.Uniform(Points, seed: 52);
        for (var row = 0; row < Points; row += 97)
        {
            (row % 2 == 0 ? values.X : values.Y)[row] = float.NaN;
        }

        using var rig = new SpatialRig(values);
        var hold = rig.Probe.HoldAt(SpatialJob.PublishCoarse);
        var index = rig.Start();
        SpatialSnapshot coarse;
        long[] density;
        (SpatialRect Rect, int Level, SpatialCount Count)[] asked;
        try
        {
            hold.WaitForWorker();
            coarse = index.Acquire();

            Assert.Equal(SpatialIndexState.Counting, index.State);
            Assert.Equal(1, index.Version);
            Assert.False(index.IsComplete);
            Assert.False(index.Completion.IsCompleted);
            Assert.Equal(SpatialStage.Coarse, coarse.Stage);
            Assert.True(coarse.IsProvisional);

            var x = SpatialAxisData.Of(rig.Xs);
            var y = SpatialAxisData.Of(rig.Ys);
            var all = new RowRange(0, Points);
            var (xRange, yRange) = (x.Range(all), y.Range(all));
            Assert.Equal(xRange, coarse.XRange);
            Assert.Equal(yRange, coarse.YRange);

            var gx = new List<uint>();
            var gy = new List<uint>();
            for (var row = 0; row < Points; row++)
            {
                if (x.IsFinite(row) && y.IsFinite(row))
                {
                    gx.Add(SpatialReference.Cell(x.Difference(row, xRange.Origin), xRange.Lower, xRange.Upper));
                    gy.Add(SpatialReference.Cell(y.Difference(row, yRange.Origin), yRange.Lower, yRange.Upper));
                }
            }

            Assert.Equal(gx.Count, coarse.PointCount);
            Assert.Equal(Points - gx.Count, coarse.ExcludedCount);
            Assert.Equal(207, coarse.ExcludedCount);

            density = new long[256 * 256];
            for (var i = 0; i < gx.Count; i++)
            {
                density[((gy[i] >> 24) << 8) + (gx[i] >> 24)]++;
            }

            Assert.True(coarse.CoarseDensity.SequenceEqual(density), "The coarse density is not the counts of the cells of level 8.");

            var found = new List<(SpatialRect Rect, int Level, SpatialCount Count)>();
            foreach (var rect in Rectangles(xRange, yRange, seed: 53))
            {
                var grid = SpatialReference.Grid(coarse, rect);
                foreach (var level in (int[])[0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 20, 32])
                {
                    var shift = 32 - Math.Min(level, 8);
                    var width = (1UL << shift) - 1;
                    long inside = 0;
                    long touching = 0;
                    for (var i = 0; i < gx.Count; i++)
                    {
                        var x0 = ((ulong)gx[i] >> shift) << shift;
                        var y0 = ((ulong)gy[i] >> shift) << shift;
                        var (x1, y1) = (x0 + width, y0 + width);
                        if (x0 >= grid.X0 && x1 <= grid.X1 && y0 >= grid.Y0 && y1 <= grid.Y1)
                        {
                            inside++;
                        }
                        else if (x1 >= grid.X0 && x0 <= grid.X1 && y1 >= grid.Y0 && y0 <= grid.Y1)
                        {
                            touching++;
                        }
                    }

                    var count = coarse.Count(rect, level);
                    if (count != new SpatialCount(inside, touching))
                    {
                        Assert.Fail($"Count of {rect} at level {level} is {count} on the coarse snapshot; the loop gives ({inside}, {touching}).");
                    }

                    found.Add((rect, level, count));
                }
            }

            asked = [.. found];

            var marked = new SpatialCell(ulong.MaxValue, -1, -1, -1, SpatialCellKind.CappedLeaf);
            var cells = new SpatialCell[8];
            Array.Fill(cells, marked);
            Assert.Equal(0, coarse.GetCells(SpatialReference.Everything, 32, cells));
            Assert.Equal(0, coarse.GetCells(SpatialReference.Everything, 0, cells));
            Assert.All(cells, cell => Assert.Equal(marked, cell));
            Assert.Throws<InvalidOperationException>(() => coarse.ReadPoints(0, new double[1], new double[1], new long[1]));
        }
        finally
        {
            hold.Release();
        }

        Eventually.Completes(index.Completion);
        var full = index.Acquire();
        Assert.NotSame(coarse, full);
        Assert.Equal(2, index.Version);

        Assert.Equal(SpatialStage.Coarse, coarse.Stage);
        Assert.True(coarse.IsProvisional);
        Assert.True(coarse.CoarseDensity.SequenceEqual(density), "The coarse snapshot changed after the full one was published.");
        foreach (var (rect, level, count) in asked)
        {
            Assert.Equal(count, coarse.Count(rect, level));
        }

        Assert.Equal(SpatialStage.Full, full.Stage);
        Assert.False(full.IsProvisional);
        Assert.True(full.CoarseDensity.SequenceEqual(density), "The full snapshot does not hold the coarse counts of the coarse one.");
        rig.Check(full);
        coarse.Release();
        full.Release();
    }

    /// <summary>The rectangles of the coarse counts, in differences from the origins of the two ranges.</summary>
    private static List<SpatialRect> Rectangles(ScaleRange xRange, ScaleRange yRange, int seed)
    {
        var random = new Random(seed);
        var (xWidth, yWidth) = (xRange.Upper - xRange.Lower, yRange.Upper - yRange.Lower);
        var rects = new List<SpatialRect>();
        for (var i = 0; i < 20; i++)
        {
            var x = Ends(random, xRange.Lower, xWidth);
            var y = Ends(random, yRange.Lower, yWidth);
            rects.Add(new SpatialRect(x.Min, x.Max, y.Min, y.Max));
        }

        rects.Add(new SpatialRect(xRange.Lower, xRange.Upper, yRange.Lower, yRange.Upper));
        var (tinyX, tinyY) = (xRange.Lower + (xWidth * 0.4), yRange.Lower + (yWidth * 0.6));
        rects.Add(new SpatialRect(tinyX, tinyX + (xWidth * 1e-6), tinyY, tinyY + (yWidth * 1e-6)));
        rects.Add(SpatialReference.Everything);
        return rects;
    }

    /// <summary>Two seeded ends on one axis, anywhere from 10% below the range to 10% above it.</summary>
    private static (double Min, double Max) Ends(Random random, double lower, double width)
    {
        var a = lower + (width * ((random.NextDouble() * 1.2) - 0.1));
        var b = lower + (width * ((random.NextDouble() * 1.2) - 0.1));
        return (Math.Min(a, b), Math.Max(a, b));
    }
}
