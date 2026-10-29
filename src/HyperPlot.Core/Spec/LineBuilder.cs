using HyperPlot.Rendering;

namespace HyperPlot;

/// <summary>
/// Sets the stroke of a line layer inside <see cref="PlotBuilder.Line{TX, TY}(ColumnRef{TX}, ColumnRef{TY}, Action{LineBuilder}?)"/>
/// (design §4.1). The stroke is checked when the layer is added (design §5.1).
/// </summary>
public sealed class LineBuilder
{
    internal LineBuilder() { }

    /// <summary>Gets the stroke set so far: by default black, 1 pixel wide, solid.</summary>
    internal LineStyle Style { get; private set; } = new(new Color(0, 0, 0, 255), 1);

    /// <summary>Sets the color.</summary>
    /// <param name="color">The color.</param>
    /// <returns>This builder.</returns>
    public LineBuilder Color(Color color)
    {
        Style = Style with { Color = color };
        return this;
    }

    /// <summary>Sets the width.</summary>
    /// <param name="width">The width in physical pixels: finite and at least 0.</param>
    /// <returns>This builder.</returns>
    public LineBuilder Width(float width)
    {
        Style = Style with { Width = width };
        return this;
    }

    /// <summary>Sets the dash pattern.</summary>
    /// <param name="dash">The pattern, or null for a solid line.</param>
    /// <returns>This builder.</returns>
    public LineBuilder Dash(LineDash? dash)
    {
        Style = Style with { Dash = dash };
        return this;
    }
}
