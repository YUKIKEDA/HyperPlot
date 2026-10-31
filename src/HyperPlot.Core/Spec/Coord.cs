namespace HyperPlot;

/// <summary>
/// The base of a coordinate system: how the scales' two positions are placed on the panel (design §4.1, §4.4):
/// <see cref="CartesianCoordSpec"/> or <see cref="PolarCoordSpec"/>.
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

/// <summary>
/// The polar coordinate system (design §4.4, row G11): one data axis runs around a circle in the panel's center, and the
/// other from the circle's inner radius out to its outer one. A full turn is drawn. The scales stay with their data, reverse
/// and clamp included.
/// </summary>
public sealed record PolarCoordSpec : CoordSpec
{
    /// <summary>Gets the data axis that runs around the circle: <see cref="PolarTheta.X"/> by default.</summary>
    public PolarTheta Theta { get; init; }

    /// <summary>
    /// Gets where the angle starts, in radians from 12 o'clock, in the direction of <see cref="Clockwise"/>: finite, 0 by
    /// default.
    /// </summary>
    public double Start { get; init; }

    /// <summary>Gets a value indicating whether the angle runs clockwise; true by default.</summary>
    public bool Clockwise { get; init; } = true;

    /// <summary>
    /// Gets the radius of the hole in the middle, as a share of the outer radius: at least 0 and below 1, 0 by default.
    /// </summary>
    public double InnerRadius { get; init; }

    /// <summary>Throws unless the members are in range (design §4.8): a spec made with <c>with</c> is checked when it is drawn.</summary>
    /// <exception cref="ArgumentException">A member is out of its range.</exception>
    internal void Validate()
    {
        if ((uint)Theta > (uint)PolarTheta.Y)
        {
            throw new ArgumentException($"The polar coordinate system's Theta is {Theta}, not a named PolarTheta value.");
        }

        if (!double.IsFinite(Start))
        {
            throw new ArgumentException($"The polar coordinate system's Start is {Start}; it is finite.");
        }

        if (!(InnerRadius >= 0 && InnerRadius < 1))
        {
            throw new ArgumentException($"The polar coordinate system's InnerRadius is {InnerRadius}; it is at least 0 and below 1.");
        }
    }
}

/// <summary>Which data axis a polar coordinate system runs around the circle (design §4.4, row G11).</summary>
public enum PolarTheta
{
    /// <summary>x is the angle and y the radius.</summary>
    X,

    /// <summary>y is the angle and x the radius.</summary>
    Y,
}

/// <summary>Makes the coordinate systems of a plot (design §4.4).</summary>
public static class Coord
{
    /// <summary>Returns the cartesian coordinate system.</summary>
    /// <param name="flip">Whether the x data runs on the left axis and the y data on the bottom axis.</param>
    /// <returns>The coordinate system.</returns>
    public static CartesianCoordSpec Cartesian(bool flip = false) => new() { Flip = flip };

    /// <summary>Returns the polar coordinate system (row G11).</summary>
    /// <param name="theta">The data axis that runs around the circle.</param>
    /// <param name="start">Where the angle starts, in radians from 12 o'clock in the direction of <paramref name="clockwise"/>: finite.</param>
    /// <param name="clockwise">Whether the angle runs clockwise.</param>
    /// <param name="innerRadius">The radius of the hole in the middle, as a share of the outer radius: at least 0 and below 1.</param>
    /// <returns>The coordinate system.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="theta"/> is not a named value, <paramref name="start"/> is not finite, or
    /// <paramref name="innerRadius"/> is below 0, at least 1, or NaN.
    /// </exception>
    public static PolarCoordSpec Polar(PolarTheta theta = PolarTheta.X, double start = 0, bool clockwise = true, double innerRadius = 0)
    {
        if ((uint)theta > (uint)PolarTheta.Y)
        {
            throw new ArgumentOutOfRangeException(nameof(theta), theta, "Theta is a named PolarTheta value.");
        }

        if (!double.IsFinite(start))
        {
            throw new ArgumentOutOfRangeException(nameof(start), start, "The start is finite.");
        }

        if (!(innerRadius >= 0 && innerRadius < 1))
        {
            throw new ArgumentOutOfRangeException(nameof(innerRadius), innerRadius, "The inner radius is at least 0 and below 1.");
        }

        return new()
        {
            Theta = theta,
            Start = start,
            Clockwise = clockwise,
            InnerRadius = innerRadius,
        };
    }
}
