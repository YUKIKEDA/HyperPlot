using System.Numerics;
using HyperPlot.Rendering;

namespace HyperPlot;

/// <summary>Sets the fill, the outline, and the label of an area or a band layer (design §4.1).</summary>
public sealed class AreaBuilder
{
    internal AreaBuilder() { }

    /// <summary>Gets the fill set so far: null for the layer's series color (design §4.1).</summary>
    internal Color? FillColor { get; private set; }

    /// <summary>Gets the outline set so far.</summary>
    internal StrokeSpec? OutlineStyle { get; private set; }

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
    /// <param name="outline">The stroke in DIPs, or null for none. Without a color, it takes the layer's series color.</param>
    /// <returns>This builder.</returns>
    public AreaBuilder Outline(StrokeSpec? outline)
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

    /// <summary>Gets the column set so far to split the fill and the outline by (row G7c).</summary>
    internal ColumnRef? ColorByRef { get; private set; }

    /// <summary>Gets the column set so far to split the fill by (row G7c).</summary>
    internal ColumnRef? FillByRef { get; private set; }

    /// <summary>Splits the fill and the outline into a series a category, in the color scale's colors (design §4.1, row G7c).</summary>
    /// <param name="category">The categorical column of the plot's table.</param>
    /// <returns>This builder.</returns>
    public AreaBuilder ColorBy(CategoryRef category)
    {
        ColorByRef = category.ToColumnRef();
        return this;
    }

    /// <summary>Splits the fill only into a series a category, before <see cref="ColorBy"/> (design §4.1, row G7c).</summary>
    /// <param name="category">The categorical column of the plot's table.</param>
    /// <returns>This builder.</returns>
    public AreaBuilder FillBy(CategoryRef category)
    {
        FillByRef = category.ToColumnRef();
        return this;
    }
}

/// <summary>Sets the fill and the label of a bar or a rect layer (design §4.1).</summary>
public sealed class FillBuilder
{
    internal FillBuilder() { }

    /// <summary>Gets the fill set so far: null for the layer's series color (design §4.1).</summary>
    internal Color? FillColor { get; private set; }

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

    /// <summary>Gets the column set so far to color the fill by (row G7c).</summary>
    internal ColumnRef? ColorByRef { get; private set; }

    /// <summary>Splits the layer into a series a category, in the color scale's colors (design §4.1, row G7c).</summary>
    /// <param name="category">The categorical column of the plot's table.</param>
    /// <returns>This builder.</returns>
    public FillBuilder ColorBy(CategoryRef category)
    {
        ColorByRef = category.ToColumnRef();
        return this;
    }

    /// <summary>
    /// Colors each bar or rect by a number, through a sequential or a diverging color scale (design §4.1, row G7c). At most
    /// 131,072 elements are drawn so.
    /// </summary>
    /// <typeparam name="T">The element type of the column.</typeparam>
    /// <param name="values">The numeric column of the plot's table, as long as the layer's elements.</param>
    /// <returns>This builder.</returns>
    public FillBuilder ColorBy<T>(ColumnRef<T> values)
        where T : unmanaged, INumber<T>
    {
        ColorByRef = values;
        return this;
    }
}
