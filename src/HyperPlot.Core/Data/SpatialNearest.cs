namespace HyperPlot;

/// <summary>How <see cref="SpatialSnapshot.Nearest"/> measures the distance from the cursor to a point (spike of row R3c).</summary>
internal enum SpatialHitMode
{
    /// <summary>The distance on the screen, in pixels, over both axes.</summary>
    XY,

    /// <summary>The distance on the screen, in pixels, along x only.</summary>
    X,
}

/// <summary>What <see cref="SpatialSnapshot.Nearest"/> found, and what it read to find it (spike of row R3c).</summary>
/// <param name="Found">Whether a point lies nearer than the radius (plus the marker's radius in <see cref="SpatialHitMode.XY"/>).</param>
/// <param name="Row">The point's row in the columns.</param>
/// <param name="X">The point's x, as a difference from the origin of <see cref="SpatialSnapshot.XRange"/>.</param>
/// <param name="Y">The point's y, as a difference from the origin of <see cref="SpatialSnapshot.YRange"/>.</param>
/// <param name="Distance">The point's distance from the cursor in pixels.</param>
/// <param name="CellsVisited">The cells taken from the queue and opened, read, or answered by one point.</param>
/// <param name="PointsRead">The points read from the copy.</param>
/// <param name="QueuePeak">The most cells the queue held at once.</param>
/// <param name="Overflowed">Whether a cell was left out because the queue was full: the answer may then miss a nearer point.</param>
internal readonly record struct SpatialNearest(
    bool Found,
    long Row,
    double X,
    double Y,
    double Distance,
    int CellsVisited,
    long PointsRead,
    int QueuePeak,
    bool Overflowed
);

/// <summary>
/// The buffers of <see cref="SpatialSnapshot.Nearest"/>, made once and reused by every query, so that a query allocates
/// nothing (spike of row R3c).
/// </summary>
internal sealed class SpatialNearestScratch
{
    /// <summary>Initializes a new instance of the <see cref="SpatialNearestScratch"/> class.</summary>
    /// <param name="leafCapacity">The most points read from one leaf: the index's LeafCapacity.</param>
    /// <param name="queueCapacity">The most cells the queue holds.</param>
    public SpatialNearestScratch(int leafCapacity, int queueCapacity = 1 << 16)
    {
        LeafCapacity = leafCapacity;
        Keys = new double[queueCapacity];
        Cells = new int[queueCapacity];
        Xs = new double[leafCapacity];
        Ys = new double[leafCapacity];
    }

    /// <summary>Gets the most points read from one leaf.</summary>
    public int LeafCapacity { get; }

    /// <summary>Gets the distances of the queued cells: a binary heap, the least first.</summary>
    internal double[] Keys { get; }

    /// <summary>Gets the rows of the table of the queued cells, beside <see cref="Keys"/>.</summary>
    internal int[] Cells { get; }

    /// <summary>Gets the x values of a leaf being read.</summary>
    internal double[] Xs { get; }

    /// <summary>Gets the y values of a leaf being read.</summary>
    internal double[] Ys { get; }
}
