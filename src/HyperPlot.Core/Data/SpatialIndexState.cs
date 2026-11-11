namespace HyperPlot;

/// <summary>The state of a <see cref="SpatialIndex"/> (design §3.4 "状態").</summary>
internal enum SpatialIndexState
{
    /// <summary>No worker has taken the build yet.</summary>
    Waiting,

    /// <summary>A build allocates, finds the ranges, counts, and makes the table.</summary>
    Counting,

    /// <summary>A build writes the copy.</summary>
    Writing,

    /// <summary>A build has published its full snapshot and makes the density tiles.</summary>
    Density,

    /// <summary>The last build ended and published its snapshot.</summary>
    Complete,

    /// <summary>The last build threw.</summary>
    Failed,

    /// <summary>The index is disposed.</summary>
    Disposed,
}
