namespace HyperPlot;

/// <summary>
/// The base of a coordinate system: how the scales' two positions are placed on the panel (design §4.1, §4.4). Row G10
/// compiles <see cref="CartesianCoordSpec"/>; polar comes in row G11.
/// </summary>
public abstract record CoordSpec : SpecNode
{
    /// <summary>Initializes a new instance of the <see cref="CoordSpec"/> class with a new <see cref="SpecNode.Id"/>.</summary>
    private protected CoordSpec() { }
}

/// <summary>The cartesian coordinate system (design §4.4): x along one side of the panel, y along the other.</summary>
public sealed record CartesianCoordSpec : CoordSpec
{
    /// <summary>
    /// Gets a value indicating whether x and y swap sides: when true, the x data runs on the left axis, bottom to top, and
    /// the y data on the bottom axis, left to right. The scales stay with their data, reverse and clamp included.
    /// </summary>
    public bool Flip { get; init; }
}

/// <summary>Makes the coordinate systems of a plot (design §4.4).</summary>
public static class Coord
{
    /// <summary>Returns the cartesian coordinate system.</summary>
    /// <param name="flip">Whether the x data runs on the left axis and the y data on the bottom axis.</param>
    /// <returns>The coordinate system.</returns>
    public static CartesianCoordSpec Cartesian(bool flip = false) => new() { Flip = flip };
}
