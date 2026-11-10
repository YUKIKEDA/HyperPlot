namespace HyperPlot.Tests;

/// <summary>The spatial tile index of no rows: an empty row range or columns of no rows (design §3.4, row C3a).</summary>
[Collection("Large data")]
public sealed class SpatialEmptyIndexTests
{
    /// <summary>
    /// An index of no rows asks for no copy, publishes the empty, the coarse, and the full snapshot, and ends complete
    /// with a table of the root alone.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two float columns of <paramref name="length"/> rows, kept as <paramref name="storage"/>: wrapped arrays,
    ///   memory-mapped files, or native blocks.
    /// - The rows [<paramref name="start"/>, <paramref name="end"/>): an empty range inside the columns, a range that
    ///   starts after their end, or every row of columns of 0 rows.
    /// - A pool of 3 threads, an allocator that records its requests, and a probe on the jobs of the build.
    ///
    /// Steps:
    /// - Build the index and wait for Completion.
    /// - Read the jobs with the state and the version at the end of each, and the requests of the allocator.
    /// - Read the snapshot and ask it Count, GetCells, and ReadPoints.
    ///
    /// Expected:
    /// - The allocator got no request.
    /// - The jobs are Allocate, EndRange, PublishCoarse, Table, PublishFull: no unit ran.
    /// - The versions at their ends are 0, 0, 1, 1, 2, and the states are Counting three times, Writing, and Complete.
    /// - The index is complete with no Fault, and its version is 2.
    /// - The snapshot is full and not provisional, with 0 points, 0 excluded rows, both ranges with the origin 0 and
    ///   the width 0, coarse counts of 0, and a table of 1 row: the root with 0 points.
    /// - Count answers 0 inside and 0 touching, GetCells returns 0, and ReadPoints of 0 points returns.
    /// </remarks>
    [Theory]
    [InlineData("wrapped", 1_000, 5, 5)]
    [InlineData("wrapped", 1_000, 2_000, 3_000)]
    [InlineData("wrapped", 0, 0, long.MaxValue)]
    [InlineData("mapped", 1_000, 5, 5)]
    [InlineData("mapped", 0, 0, long.MaxValue)]
    [InlineData("native", 1_000, 5, 5)]
    [InlineData("native", 0, 0, long.MaxValue)]
    public void Build_NoRows_PublishesAnEmptyFullSnapshotWithoutACopy(string storage, int length, long start, long end)
    {
        var (xs, ys) = SpatialData.Uniform(length, seed: 30);
        using var xFile = TempFile.Write<float>(xs, 0);
        using var yFile = TempFile.Write<float>(ys, 0);
        var allocator = new RecordingCopyAllocator();
        var probe = new SpatialProbe();
        var input = new SpatialIndexInput { Rows = new RowRange(start, end) };

        using var built = SpatialBuilt.Over(Make(storage, xs, xFile), Make(storage, ys, yFile), input, allocator: allocator, probe: probe);

        Assert.Empty(allocator.Requests);
        SpatialJob[] jobs = [SpatialJob.Allocate, SpatialJob.EndRange, SpatialJob.PublishCoarse, SpatialJob.Table, SpatialJob.PublishFull];
        long[] versions = [0, 0, 1, 1, 2];
        SpatialIndexState[] states =
        [
            SpatialIndexState.Counting,
            SpatialIndexState.Counting,
            SpatialIndexState.Counting,
            SpatialIndexState.Writing,
            SpatialIndexState.Complete,
        ];
        var steps = probe.Steps;
        Assert.Equal(jobs, steps.Select(step => step.Job));
        Assert.Equal(versions, steps.Select(step => step.Version));
        Assert.Equal(states, steps.Select(step => step.State));

        Assert.True(built.Index.IsComplete);
        Assert.Equal(SpatialIndexState.Complete, built.Index.State);
        Assert.Null(built.Index.Fault);
        Assert.Equal(2, built.Index.Version);

        var snapshot = built.Snapshot;
        Assert.Equal(SpatialStage.Full, snapshot.Stage);
        Assert.False(snapshot.IsProvisional);
        Assert.Equal(0, snapshot.PointCount);
        Assert.Equal(0, snapshot.ExcludedCount);
        Assert.Equal(new ScaleRange(0, 0, 0), snapshot.XRange);
        Assert.Equal(new ScaleRange(0, 0, 0), snapshot.YRange);
        Assert.Equal(65_536, snapshot.CoarseDensity.Length);
        Assert.False(snapshot.CoarseDensity.ContainsAnyExcept(0L));
        Assert.Equal(1, snapshot.Rows.Length);
        Assert.Equal(0, snapshot.Rows[0].Count);
        Assert.Equal(0, snapshot.Rows[0].Level);
        Assert.Equal(new SpatialCount(0, 0), snapshot.Count(SpatialReference.Everything, 32));
        Assert.Equal(new SpatialCount(0, 0), snapshot.Count(new SpatialRect(0, 0, 0, 0), 0));
        Assert.Equal(0, snapshot.GetCells(SpatialReference.Everything, 32, default));
        snapshot.ReadPoints(0, default, default, default);
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.ReadPoints(0, new double[1], new double[1], new long[1]));
    }

    private static Column<float> Make(string storage, float[] values, TempFile file) =>
        storage switch
        {
            "wrapped" => Column.Wrap(values),
            "mapped" => Column.MapFile<float>(file.Path),
            "native" => Column.CopyToNative<float>(values),
            _ => throw new ArgumentOutOfRangeException(nameof(storage)),
        };
}
