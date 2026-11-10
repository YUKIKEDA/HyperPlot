namespace HyperPlot;

/// <summary>What a cell of the table of a spatial tile index is (design §3.4).</summary>
internal enum SpatialCellKind
{
    /// <summary>A split cell: its children are in the table.</summary>
    Inner,

    /// <summary>A leaf with no more than the leaf capacity of points.</summary>
    Leaf,

    /// <summary>A leaf with more points than the leaf capacity, all in one grid cell.</summary>
    CoincidentLeaf,

    /// <summary>A leaf with more points than the leaf capacity that the cap on the table's rows kept from being split.</summary>
    CappedLeaf,
}
