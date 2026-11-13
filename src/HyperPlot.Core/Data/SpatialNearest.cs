namespace HyperPlot;

/// <summary>What <see cref="SpatialSnapshot.Nearest{TScreen}"/> found, and what it read to find it (design §6.3, row R3c).</summary>
/// <param name="Found">Whether a point is drawn nearer than the reach: the radius, plus the marker's radius in XY.</param>
/// <param name="Row">The point's row in the columns.</param>
/// <param name="X">The point's x, as a difference from the origin of <see cref="SpatialSnapshot.XRange"/>.</param>
/// <param name="Y">The point's y, as a difference from the origin of <see cref="SpatialSnapshot.YRange"/>.</param>
/// <param name="Along">The pixel along at which the point is drawn.</param>
/// <param name="Across">The pixel across at which the point is drawn.</param>
/// <param name="Distance">The point's distance from the cursor in pixels: along x alone in <see cref="Interaction.HitMode.X"/>.</param>
/// <param name="Overflowed">
/// Whether a cell was left out because the queue was full (Issue #471 Decision 20): the answer is then provisional, and a
/// nearer point may have been missed.
/// </param>
/// <param name="CellsVisited">The cells taken from the queue. Tests only.</param>
/// <param name="PointsRead">The points read from the copy. Tests only.</param>
internal readonly record struct SpatialNearest(
    bool Found,
    long Row,
    double X,
    double Y,
    double Along,
    double Across,
    double Distance,
    bool Overflowed,
    int CellsVisited,
    long PointsRead
);
