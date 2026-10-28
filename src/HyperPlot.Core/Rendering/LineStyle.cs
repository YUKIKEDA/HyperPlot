namespace HyperPlot.Rendering;

/// <summary>A dash pattern: one dash and one gap, measured along the line in physical pixels (design §5.1).</summary>
/// <param name="On">The length of a dash, finite and at least 0. 0 with a gap draws dots: each dash has round ends.</param>
/// <param name="Off">The length of a gap, finite and at least 0; <paramref name="On"/> + <paramref name="Off"/> is above 0.</param>
/// <param name="Offset">How far into the pattern the line starts, finite.</param>
public readonly record struct LineDash(float On, float Off, float Offset = 0);

/// <summary>How a polyline is stroked (design §5.1): round joins and ends, anti-aliased.</summary>
/// <param name="Color">The color.</param>
/// <param name="Width">The width in physical pixels, finite and at least 0. 0 draws nothing; below 1, the line is fainter by the share of the pixel it covers.</param>
/// <param name="Dash">The dash pattern, or null for a solid line. The pattern runs on across joins.</param>
public readonly record struct LineStyle(Color Color, float Width, LineDash? Dash = null);
