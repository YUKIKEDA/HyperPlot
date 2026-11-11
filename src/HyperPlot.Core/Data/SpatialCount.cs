namespace HyperPlot;

/// <summary>
/// The answer of <see cref="SpatialSnapshot.Count"/> (design §3.4): the points in the rectangle are no fewer than
/// <see cref="Inside"/> and no more than <see cref="Inside"/> plus <see cref="Touching"/>.
/// </summary>
/// <param name="Inside">The points of the cells that lie wholly in the rectangle.</param>
/// <param name="Touching">The points of the cells that the rectangle covers in part and the query did not open.</param>
internal readonly record struct SpatialCount(long Inside, long Touching);
