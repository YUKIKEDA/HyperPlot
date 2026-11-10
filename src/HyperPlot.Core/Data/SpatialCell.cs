namespace HyperPlot;

/// <summary>One cell that <see cref="SpatialSnapshot.GetCells"/> returns (design §3.4).</summary>
/// <param name="Code">The cell's number at its level: the upper 2 × <paramref name="Level"/> bits of its points' codes.</param>
/// <param name="Level">The cell's level: 0 is the whole range, and 32 is one grid cell.</param>
/// <param name="First">The position of the cell's first point in the snapshot's copy.</param>
/// <param name="Count">The number of the cell's points.</param>
/// <param name="Kind">What the cell is.</param>
internal readonly record struct SpatialCell(ulong Code, int Level, long First, long Count, SpatialCellKind Kind);
