using HyperPlot.Rendering;

namespace HyperPlot;

/// <summary>Sets the looks, the format, and the label of a text layer (design §4.1, row G8).</summary>
public sealed class TextBuilder
{
    internal TextBuilder() { }

    /// <summary>Gets the color set so far: null for the layer's series color.</summary>
    internal Color? ColorValue { get; private set; }

    /// <summary>Gets the size set so far, in DIPs.</summary>
    internal float SizeValue { get; private set; } = 12;

    /// <summary>Gets the horizontal alignment set so far.</summary>
    internal TextAlign HorizontalValue { get; private set; } = TextAlign.Center;

    /// <summary>Gets the vertical alignment set so far.</summary>
    internal TextBaseline VerticalValue { get; private set; } = TextBaseline.Middle;

    /// <summary>Gets the offset set so far, in DIPs.</summary>
    internal (float X, float Y) OffsetValue { get; private set; }

    /// <summary>Gets the rotation set so far.</summary>
    internal TextRotation RotationValue { get; private set; }

    /// <summary>Gets the format set so far.</summary>
    internal TickFormatter? FormatValue { get; private set; }

    /// <summary>Gets the label set so far.</summary>
    internal string? LabelText { get; private set; }

    /// <summary>Gets the column set so far to color the labels by.</summary>
    internal ColumnRef? ColorByRef { get; private set; }

    /// <summary>Sets the labels' color.</summary>
    /// <param name="color">The color.</param>
    /// <returns>This builder.</returns>
    public TextBuilder Color(Color color)
    {
        ColorValue = color;
        return this;
    }

    /// <summary>Sets the labels' size.</summary>
    /// <param name="size">The size in DIPs: finite, above 0, and at most 512.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> is not finite, above 0, and at most 512.</exception>
    public TextBuilder Size(float size)
    {
        CheckSize(size, nameof(size));
        SizeValue = size;
        return this;
    }

    /// <summary>Sets the point of the label's box put at its point.</summary>
    /// <param name="horizontal">The point across.</param>
    /// <param name="vertical">The point down.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is not a named one.</exception>
    public TextBuilder Align(TextAlign horizontal, TextBaseline vertical)
    {
        CheckAlign(horizontal, vertical, nameof(horizontal));
        (HorizontalValue, VerticalValue) = (horizontal, vertical);
        return this;
    }

    /// <summary>Sets how far the label moves from its point.</summary>
    /// <param name="x">Right, in DIPs: finite.</param>
    /// <param name="y">Up, in DIPs: finite.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is not finite.</exception>
    public TextBuilder Offset(float x, float y)
    {
        CheckOffset(x, nameof(x));
        CheckOffset(y, nameof(y));
        OffsetValue = (x, y);
        return this;
    }

    /// <summary>Sets the labels' rotation.</summary>
    /// <param name="rotation">The rotation.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rotation"/> is not a named value.</exception>
    public TextBuilder Rotation(TextRotation rotation)
    {
        CheckRotation(rotation, nameof(rotation));
        RotationValue = rotation;
        return this;
    }

    /// <summary>
    /// Sets the format of numeric labels. Pass a static lambda or a method group: a new delegate each frame writes every label
    /// again each frame.
    /// </summary>
    /// <param name="format">The format, or null for "G6" in the invariant culture.</param>
    /// <returns>This builder.</returns>
    public TextBuilder Format(TickFormatter? format)
    {
        FormatValue = format;
        return this;
    }

    /// <summary>Sets the legend's label.</summary>
    /// <param name="label">The label; null or empty leaves the layer out of the legend.</param>
    /// <returns>This builder.</returns>
    public TextBuilder Label(string? label)
    {
        LabelText = label;
        return this;
    }

    /// <summary>Splits the labels into a series a category, in the color scale's colors (design §4.1, row G7c).</summary>
    /// <param name="category">The categorical column of the plot's table.</param>
    /// <returns>This builder.</returns>
    public TextBuilder ColorBy(CategoryRef category)
    {
        ColorByRef = category.ToColumnRef();
        return this;
    }

    /// <summary>Returns whether a text size is finite, above 0, and at most 512 DIPs.</summary>
    internal static bool IsSize(float size) => float.IsFinite(size) && size > 0 && size <= TextEngine.MaxSize;

    /// <summary>Returns whether both alignments are named values.</summary>
    internal static bool IsAlign(TextAlign horizontal, TextBaseline vertical) =>
        (uint)horizontal <= (uint)TextAlign.Right && (uint)vertical <= (uint)TextBaseline.Bottom;

    /// <summary>Returns whether a rotation is a named value.</summary>
    internal static bool IsRotation(TextRotation rotation) => (uint)rotation <= (uint)TextRotation.CounterClockwise90;

    /// <summary>Throws unless a text size is finite, above 0, and at most 512 DIPs.</summary>
    internal static void CheckSize(float size, string name)
    {
        if (!IsSize(size))
        {
            throw new ArgumentOutOfRangeException(name, size, $"A text size is finite, above 0, and at most {TextEngine.MaxSize}.");
        }
    }

    /// <summary>Throws unless an offset is finite.</summary>
    internal static void CheckOffset(float offset, string name)
    {
        if (!float.IsFinite(offset))
        {
            throw new ArgumentOutOfRangeException(name, offset, "An offset is finite.");
        }
    }

    /// <summary>Throws unless both alignments are named values.</summary>
    internal static void CheckAlign(TextAlign horizontal, TextBaseline vertical, string name)
    {
        if (!IsAlign(horizontal, vertical))
        {
            throw new ArgumentOutOfRangeException(name, $"The alignment {horizontal}, {vertical} is not a named one.");
        }
    }

    /// <summary>Throws unless a rotation is a named value.</summary>
    internal static void CheckRotation(TextRotation rotation, string name)
    {
        if (!IsRotation(rotation))
        {
            throw new ArgumentOutOfRangeException(name, rotation, "The rotation is not a named value.");
        }
    }

    /// <summary>Gets a value indicating whether the layer's x goes on the secondary x scale (row G6a).</summary>
    internal bool SecondaryXValue { get; private set; }

    /// <summary>Gets a value indicating whether the layer's y goes on the secondary y scale (row G6a).</summary>
    internal bool SecondaryYValue { get; private set; }

    /// <summary>Puts the layer's x on <see cref="PlotSpec.ScaleX2"/> (design §4.1, row G6a).</summary>
    /// <returns>This builder.</returns>
    public TextBuilder SecondaryX()
    {
        SecondaryXValue = true;
        return this;
    }

    /// <summary>Puts the layer's y on <see cref="PlotSpec.ScaleY2"/> (design §4.1, row G6a).</summary>
    /// <returns>This builder.</returns>
    public TextBuilder SecondaryY()
    {
        SecondaryYValue = true;
        return this;
    }
}
