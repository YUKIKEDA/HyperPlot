using HyperPlot.Rendering;

namespace HyperPlot;

/// <summary>Sets the fill, the outline, and the label of an area or a band layer (design §4.1).</summary>
public sealed class AreaBuilder
{
    internal AreaBuilder() { }

    /// <summary>Gets the fill set so far.</summary>
    internal Color FillColor { get; private set; } = new(0, 0, 0, 128);

    /// <summary>Gets the outline set so far.</summary>
    internal LineStyle? OutlineStyle { get; private set; }

    /// <summary>Gets the label set so far.</summary>
    internal string? LabelText { get; private set; }

    /// <summary>Sets the fill.</summary>
    /// <param name="color">The color.</param>
    /// <returns>This builder.</returns>
    public AreaBuilder Fill(Color color)
    {
        FillColor = color;
        return this;
    }

    /// <summary>Sets the outline.</summary>
    /// <param name="outline">The stroke in DIPs, or null for none.</param>
    /// <returns>This builder.</returns>
    public AreaBuilder Outline(LineStyle? outline)
    {
        OutlineStyle = outline;
        return this;
    }

    /// <summary>Sets the legend's label.</summary>
    /// <param name="label">The label; null or empty leaves the layer out of the legend.</param>
    /// <returns>This builder.</returns>
    public AreaBuilder Label(string? label)
    {
        LabelText = label;
        return this;
    }
}

/// <summary>Sets the fill and the label of a bar or a rect layer (design §4.1).</summary>
public sealed class FillBuilder
{
    internal FillBuilder() { }

    /// <summary>Gets the fill set so far.</summary>
    internal Color FillColor { get; private set; } = new(0, 0, 0, 128);

    /// <summary>Gets the label set so far.</summary>
    internal string? LabelText { get; private set; }

    /// <summary>Sets the fill.</summary>
    /// <param name="color">The color.</param>
    /// <returns>This builder.</returns>
    public FillBuilder Fill(Color color)
    {
        FillColor = color;
        return this;
    }

    /// <summary>Sets the legend's label.</summary>
    /// <param name="label">The label; null or empty leaves the layer out of the legend.</param>
    /// <returns>This builder.</returns>
    public FillBuilder Label(string? label)
    {
        LabelText = label;
        return this;
    }
}
