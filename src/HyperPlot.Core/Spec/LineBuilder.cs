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
    /// <param name="dash">The pattern in DIPs, or null for a solid line. Up to 8 intervals through <see cref="LineDash.Of"/>.</param>
    /// <returns>This builder.</returns>
    public LineBuilder Dash(LineDash? dash)
    {
        Style = Style with { Dash = dash };
        return this;
    }

    /// <summary>Sets how the line turns at a point between two segments (design §5.1).</summary>
    /// <param name="join">The join.</param>
    /// <returns>This builder.</returns>
    public LineBuilder Join(LineJoin join)
    {
        Style = Style with { Join = join };
        return this;
    }

    /// <summary>Sets how the line ends, at each end of a run and of each dash (design §5.1).</summary>
    /// <param name="cap">The cap.</param>
    /// <returns>This builder.</returns>
    public LineBuilder Cap(LineCap cap)
    {
        Style = Style with { Cap = cap };
        return this;
    }

    /// <summary>Sets the most a miter may reach past its point, as a multiple of half the width (design §5.1).</summary>
    /// <param name="limit">Finite and at least 1 when the join is a miter; a ratio, so the renderer's scale does not change it.</param>
    /// <returns>This builder.</returns>
    public LineBuilder MiterLimit(float limit)
    {
        Style = Style with { MiterLimit = limit };
        return this;
    }
}
