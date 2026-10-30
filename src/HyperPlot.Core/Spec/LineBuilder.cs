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
    /// <param name="width">The width in DIPs, times the renderer's scale (design §4.6): finite and at least 0.</param>
    /// <returns>This builder.</returns>
    public LineBuilder Width(float width)
    {
        Style = Style with { Width = width };
        return this;
    }

    /// <summary>Gets the label set so far.</summary>
    internal string? LabelText { get; private set; }

    /// <summary>Sets the legend's label.</summary>
    /// <param name="label">The label; null or empty leaves the layer out of the legend.</param>
    /// <returns>This builder.</returns>
    public LineBuilder Label(string? label)
    {
        LabelText = label;
        return this;
    }

    /// <summary>Sets the dash pattern.</summary>
    /// <param name="dash">The pattern in DIPs, or null for a solid line.</param>
    /// <returns>This builder.</returns>
    public LineBuilder Dash(LineDash? dash)
    {
        Style = Style with { Dash = dash };
        return this;
    }
}
