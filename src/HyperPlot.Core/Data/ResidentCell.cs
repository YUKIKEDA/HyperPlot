namespace HyperPlot;

/// <summary>
/// A cell of a spatial tile index whose points the GPU places from one base (design §5.5.2 "常駐のセルとページ"): the
/// range of its points in the copy, its lower corner, and its extent.
/// </summary>
/// <param name="First">The position of the cell's first point in the copy.</param>
/// <param name="Count">The number of the cell's points: at most <see cref="SpatialSnapshot.CellCapacity"/>, or more in a leaf.</param>
/// <param name="CornerX">The cell's lower x, as a difference from the origin of the snapshot's x range.</param>
/// <param name="CornerY">The cell's lower y, as a difference from the origin of the snapshot's y range.</param>
/// <param name="ExtentX">The cell's width in x, in the unit of the stored values.</param>
/// <param name="ExtentY">The cell's width in y.</param>
internal readonly record struct ResidentCell(long First, long Count, double CornerX, double CornerY, double ExtentX, double ExtentY);
