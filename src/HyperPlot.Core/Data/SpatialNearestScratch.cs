namespace HyperPlot;

/// <summary>
/// The buffers of <see cref="SpatialSnapshot.Nearest{TScreen}"/>, made once and lent to every query, so that a query
/// allocates nothing (design §6.3 "探し方"): the queue of 65,536 cells, each a distance (8 bytes) and a row of the table
/// (4 bytes), and the x and y of one leaf's points. 851,968 bytes with the default sizes. A plot's plan makes one the first
/// time it makes a layer on the index path, and its hit tests share it; one thread uses it at a time, the frame's.
/// </summary>
internal sealed class SpatialNearestScratch
{
    /// <summary>The most cells the queue holds (Issue #471 Decision 20).</summary>
    internal const int QueueCells = 1 << 16;

    /// <summary>Initializes a new instance of the <see cref="SpatialNearestScratch"/> class.</summary>
    /// <param name="leafCapacity">
    /// The most points read from one leaf: the index's <see cref="SpatialIndexTuning.LeafCapacity"/>, 4,096 by default.
    /// </param>
    /// <param name="queueCells">The most cells the queue holds. The tests pass a few to make it overflow.</param>
    public SpatialNearestScratch(int leafCapacity = 4096, int queueCells = QueueCells)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(leafCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(queueCells, 1);
        Keys = new double[queueCells];
        Cells = new int[queueCells];
        Xs = new double[leafCapacity];
        Ys = new double[leafCapacity];
    }

    /// <summary>Gets the most points read from one leaf.</summary>
    public int LeafCapacity => Xs.Length;

    /// <summary>Gets the distances of the queued cells: a binary heap, the least first.</summary>
    internal double[] Keys { get; }

    /// <summary>Gets the rows of the table of the queued cells, beside <see cref="Keys"/>.</summary>
    internal int[] Cells { get; }

    /// <summary>Gets the x values of a leaf being read.</summary>
    internal double[] Xs { get; }

    /// <summary>Gets the y values of a leaf being read.</summary>
    internal double[] Ys { get; }
}
