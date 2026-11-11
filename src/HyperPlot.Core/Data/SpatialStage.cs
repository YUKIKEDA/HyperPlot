namespace HyperPlot;

/// <summary>How far the build behind a <see cref="SpatialSnapshot"/> had come when the snapshot was made (design §3.4).</summary>
internal enum SpatialStage
{
    /// <summary>Nothing is counted yet: the snapshot knows only the number of rows.</summary>
    None,

    /// <summary>The counts of levels 0 to 8 are there; the table and the copy are not.</summary>
    Coarse,

    /// <summary>The counts, the table, and the copy are there.</summary>
    Full,
}
