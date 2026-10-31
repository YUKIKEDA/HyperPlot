using HyperPlot.Rendering;

namespace HyperPlot;

/// <summary>
/// The stroke of a layer in a spec (design §4.1, row G7a): <see cref="LineStyle"/>'s members, with a color that may be left
/// out. Without a color, the plot strokes the layer in its series color, from the theme's palette (design §4.7). The width
/// and the dash lengths are in DIPs, times the renderer's scale (design §4.6).
/// </summary>
/// <param name="Color">The color, or null for the layer's series color.</param>
/// <param name="Width">The width in DIPs: finite and at least 0. 0 draws nothing.</param>
/// <param name="Dash">The dash pattern in DIPs, or null for a solid line.</param>
public readonly record struct StrokeSpec(Color? Color, float Width = 1, LineDash? Dash = null)
{
    /// <summary>Gets how the line turns at a point between two segments: <see cref="LineJoin.Round"/> by default.</summary>
    public LineJoin Join { get; init; }

    /// <summary>Gets how the line ends, at each end of a run and of each dash: <see cref="LineCap.Round"/> by default.</summary>
    public LineCap Cap { get; init; }

    /// <summary>
    /// Gets the most a miter may reach past its point, as a multiple of half the width: 4 by default, finite and at least 1
    /// when <see cref="Join"/> is <see cref="LineJoin.Miter"/>.
    /// </summary>
    public float MiterLimit { get; init; } = 4;

    /// <summary>Returns the stroke with its color, or a color given for it when it has none (design §4.1).</summary>
    internal LineStyle Resolve(HyperPlot.Color series) =>
        new(Color ?? series, Width, Dash)
        {
            Join = Join,
            Cap = Cap,
            MiterLimit = MiterLimit,
        };
}
