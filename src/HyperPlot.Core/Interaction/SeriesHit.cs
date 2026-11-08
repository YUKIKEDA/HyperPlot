namespace HyperPlot.Interaction;

/// <summary>What a hit test looks for in each series (design §6.3, row I2a, Issue #337 Decision 6).</summary>
public enum HitMode
{
    /// <summary>
    /// The row whose x is nearest the cursor's, in each series, whatever its distance: the radius is not used. A hover reads
    /// each series' value at the cursor's x so.
    /// </summary>
    X,

    /// <summary>The point nearest the cursor within the radius, in each series; a series with none there answers nothing.</summary>
    XY,
}

/// <summary>
/// One series' answer to a hit test (design §6.3, row I2a): where it is, the row it found, and the row's values. A struct,
/// so that a hover's hit test allocates nothing (invariant 3). Its x and y are an exception of invariant 12: 3D adds a z
/// (Issue #337 Decision 11).
/// </summary>
/// <param name="Plot">The plot of the figure, 0 for a plot.</param>
/// <param name="Panel">The panel of the plot.</param>
/// <param name="Layer">The layer's position in the plot's spec.</param>
/// <param name="Series">The series of the layer: 0, or the low (0) and the high (1) of a band; a split layer's part.</param>
/// <param name="Row">
/// The row the hit found: in the plot's table, a split or a facet's gathered row taken back to it; in the derived table for a
/// stat (<paramref name="Derived"/>); the element's position in the raster's column for an image.
/// </param>
/// <param name="X">The row's x, in the data units of the layer's x scale.</param>
/// <param name="Y">The row's y, in the data units of the layer's y scale.</param>
/// <param name="Distance">The distance from the cursor to the point on the screen, in pixels.</param>
/// <param name="Derived">Whether <paramref name="Row"/> is a row of a stat's derived table, which has no row of the plot's table.</param>
/// <param name="Provisional">Whether the answer comes from indexes still being built (invariant 4): a later one may differ.</param>
public readonly record struct SeriesHit(
    int Plot,
    int Panel,
    int Layer,
    int Series,
    long Row,
    double X,
    double Y,
    float Distance,
    bool Derived,
    bool Provisional
);
