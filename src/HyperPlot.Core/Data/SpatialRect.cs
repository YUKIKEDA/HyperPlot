namespace HyperPlot;

/// <summary>
/// A rectangle of the scale space that a query of a <see cref="SpatialSnapshot"/> asks about (design §3.4). The ends are
/// differences from the origins of the snapshot's ranges, and both ends belong to the rectangle.
/// </summary>
/// <param name="XMin">The lower end on x.</param>
/// <param name="XMax">The upper end on x.</param>
/// <param name="YMin">The lower end on y.</param>
/// <param name="YMax">The upper end on y.</param>
internal readonly record struct SpatialRect(double XMin, double XMax, double YMin, double YMax);
