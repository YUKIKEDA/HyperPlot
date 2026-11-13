namespace HyperPlot;

/// <summary>
/// How a point layer on the index path draws its points (design §5.5.2 "行 R3b1 の密度と切り替え", row R3b1): as raw points,
/// as a density, or by the number of points in the view. A layer on the CPU path draws raw points whatever it holds.
/// </summary>
public enum PointDraw
{
    /// <summary>
    /// Switches by the number of points in the view: to a density when it passes <see cref="PointLayerSpec.DensityThreshold"/>,
    /// back to raw points when it falls below 0.8 times that, and as the frame before in between.
    /// </summary>
    Auto = 0,

    /// <summary>Always raw points.</summary>
    Points = 1,

    /// <summary>Always a density.</summary>
    Density = 2,
}

/// <summary>
/// How a density maps the number of points of a bin to a place on its colors (design §5.5.2 "行 R3b1 の密度と切り替え", row
/// R3b1): with the count c of a bin and the most m of the bins drawn, 0 for an empty bin and 1 for the most.
/// </summary>
public enum DensityNorm
{
    /// <summary>log(1 + c) / log(1 + m).</summary>
    Log = 0,

    /// <summary>c / m.</summary>
    Linear = 1,

    /// <summary>The cube root of c over the cube root of m.</summary>
    Cbrt = 2,
}
