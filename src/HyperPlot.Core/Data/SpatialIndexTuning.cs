namespace HyperPlot;

/// <summary>
/// The constants of a spatial tile index's build (design §3.4 "定数"). The tests pass small values to reach the deep and
/// the capped shapes of the table with few points.
/// </summary>
/// <param name="LeafCapacity">The most points of a leaf that is neither coincident nor capped.</param>
/// <param name="RowsPerUnit">The rows of one unit of work: a power of two no greater than a chunk's length.</param>
/// <param name="TableShare">The share of the source's bytes that the table may take.</param>
/// <param name="WideRows">
/// The number of rows from which the counters of a build are 8 bytes wide: a count of 2^32 does not fit 4 bytes. The
/// width of the positions in the copy does not follow it (<see cref="SpatialCopy.PositionWidthFor"/>).
/// </param>
internal readonly record struct SpatialIndexTuning(int LeafCapacity, int RowsPerUnit, double TableShare, long WideRows)
{
    /// <summary>The number of groups of units: at most this many workers build one index at a time.</summary>
    public const int GroupCount = 8;

    /// <summary>Gets the constants that <see cref="PlotWorkerPool"/> builds an index with.</summary>
    public static SpatialIndexTuning Default => new(LeafCapacity: 4096, RowsPerUnit: 1 << 16, TableShare: 0.02, WideRows: 1L << 32);

    /// <summary>
    /// Gets what a worker calls after each job of a build that did not throw, with the job and its time in the ticks of
    /// <see cref="TimeProvider.GetTimestamp"/> of <see cref="TimeProvider.System"/>, before the jobs that follow it can
    /// be taken. The benchmarks time the stages with it, and the tests stop a build at a stage.
    /// </summary>
    public Action<SpatialJob, long>? JobEnded { get; init; }
}
