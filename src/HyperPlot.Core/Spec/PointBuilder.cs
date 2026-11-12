using System.Numerics;

namespace HyperPlot;

/// <summary>Sets the look and the label of a point layer (design §4.1, §5.5.2, row R3a).</summary>
public sealed class PointBuilder
{
    internal PointBuilder() { }

    /// <summary>Gets the color set so far: null for the layer's series color (design §4.1).</summary>
    internal Color? ColorValue { get; private set; }

    /// <summary>Gets the size set so far, in DIPs.</summary>
    internal float SizeValue { get; private set; } = 6;

    /// <summary>Gets the shape set so far.</summary>
    internal MarkerShape ShapeValue { get; private set; } = MarkerShape.Circle;

    /// <summary>Gets the opacity set so far.</summary>
    internal double OpacityValue { get; private set; } = 1;

    /// <summary>Gets the label set so far.</summary>
    internal string? LabelText { get; private set; }

    /// <summary>Gets the column set so far to color the points by.</summary>
    internal ColumnRef? ColorByRef { get; private set; }

    /// <summary>Gets a value indicating whether the layer's x goes on the secondary x scale (row G6a).</summary>
    internal bool SecondaryXValue { get; private set; }

    /// <summary>Gets a value indicating whether the layer's y goes on the secondary y scale (row G6a).</summary>
    internal bool SecondaryYValue { get; private set; }

    /// <summary>Sets the points' color.</summary>
    /// <param name="color">The color.</param>
    /// <returns>This builder.</returns>
    public PointBuilder Color(Color color)
    {
        ColorValue = color;
        return this;
    }

    /// <summary>Sets the points' size.</summary>
    /// <param name="size">The diameter of a disc, the side of a square, or the span of a cross, in DIPs: finite, above 0, and at most 512.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> is not finite, above 0, and at most 512.</exception>
    public PointBuilder Size(float size)
    {
        if (!IsSize(size))
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "A point's size is finite, above 0, and at most 512 DIPs.");
        }

        SizeValue = size;
        return this;
    }

    /// <summary>Sets the points' shape.</summary>
    /// <param name="shape">The shape.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="shape"/> is not a named value.</exception>
    public PointBuilder Shape(MarkerShape shape)
    {
        if (!Enum.IsDefined(shape))
        {
            throw new ArgumentOutOfRangeException(nameof(shape), shape, "The shape is a named MarkerShape value.");
        }

        ShapeValue = shape;
        return this;
    }

    /// <summary>Sets the points' opacity, which multiplies the alpha of each point's color.</summary>
    /// <param name="opacity">The opacity, from 0 to 1.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="opacity"/> is not from 0 to 1.</exception>
    public PointBuilder Opacity(double opacity)
    {
        ImageBuilder.CheckOpacity(opacity, nameof(opacity));
        OpacityValue = opacity;
        return this;
    }

    /// <summary>Sets the legend's label.</summary>
    /// <param name="label">The label; null or empty leaves the layer out of the legend.</param>
    /// <returns>This builder.</returns>
    public PointBuilder Label(string? label)
    {
        LabelText = label;
        return this;
    }

    /// <summary>Splits the layer into a part a category, in the color scale's colors (design §4.1, §5.5.2).</summary>
    /// <param name="category">The categorical column of the plot's table.</param>
    /// <returns>This builder.</returns>
    public PointBuilder ColorBy(CategoryRef category)
    {
        ColorByRef = category.ToColumnRef();
        return this;
    }

    /// <summary>
    /// Colors each point by a number, through a sequential or a diverging color scale (design §4.1, §5.5.2). A layer of at
    /// most 131,072 rows is drawn so.
    /// </summary>
    /// <typeparam name="T">The element type of the column.</typeparam>
    /// <param name="values">The numeric column of the plot's table, as long as the layer's points.</param>
    /// <returns>This builder.</returns>
    public PointBuilder ColorBy<T>(ColumnRef<T> values)
        where T : unmanaged, INumber<T>
    {
        ColorByRef = values;
        return this;
    }

    /// <summary>Puts the layer's x on <see cref="PlotSpec.ScaleX2"/> (design §4.1, row G6a).</summary>
    /// <returns>This builder.</returns>
    public PointBuilder SecondaryX()
    {
        SecondaryXValue = true;
        return this;
    }

    /// <summary>Puts the layer's y on <see cref="PlotSpec.ScaleY2"/> (design §4.1, row G6a).</summary>
    /// <returns>This builder.</returns>
    public PointBuilder SecondaryY()
    {
        SecondaryYValue = true;
        return this;
    }

    /// <summary>Returns whether a size is one a point layer takes: finite, above 0, and at most 512 DIPs.</summary>
    internal static bool IsSize(float size) => size is > 0 and <= 512;
}
