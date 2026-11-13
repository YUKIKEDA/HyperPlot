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

    /// <summary>Gets the column set so far to size the points by.</summary>
    internal ColumnRef? SizeByRef { get; private set; }

    /// <summary>Gets the column set so far to shape the points by.</summary>
    internal ColumnRef? ShapeByRef { get; private set; }

    /// <summary>Gets how the layer draws its points, set so far (row R3b1).</summary>
    internal PointDraw DrawValue { get; private set; } = PointDraw.Auto;

    /// <summary>Gets the number of points in the view above which the layer draws a density, set so far (row R3b1).</summary>
    internal long DensityThresholdValue { get; private set; } = 10_000_000;

    /// <summary>Gets how a density maps its counts to its colors, set so far (row R3b1).</summary>
    internal DensityNorm DensityNormValue { get; private set; } = DensityNorm.Log;

    /// <summary>Gets the colors of a density set so far: null for shades of the layer's color (row R3b1).</summary>
    internal ColorMap? DensityMapValue { get; private set; }

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
    /// <param name="size">The diameter of a disc, and the side of the box any other shape fits in, in DIPs: finite, above 0, and at most 512.</param>
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
        if (!HyperPlot.Rendering.MarkerStyle.IsShape(shape))
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

    /// <summary>
    /// Sizes each point by a number, through the plot's size scale (design §4.1, §5.5.2, row R3f). A layer of at most
    /// 131,072 rows is drawn so.
    /// </summary>
    /// <typeparam name="T">The element type of the column.</typeparam>
    /// <param name="values">The numeric column of the plot's table, as long as the layer's points.</param>
    /// <returns>This builder.</returns>
    public PointBuilder SizeBy<T>(ColumnRef<T> values)
        where T : unmanaged, INumber<T>
    {
        SizeByRef = values;
        return this;
    }

    /// <summary>Shapes each point by its category, through the plot's shape scale (design §4.1, §5.5.2, row R3f).</summary>
    /// <param name="category">The categorical column of the plot's table.</param>
    /// <returns>This builder.</returns>
    public PointBuilder ShapeBy(CategoryRef category)
    {
        ShapeByRef = category.ToColumnRef();
        return this;
    }

    /// <summary>
    /// Sets how the layer draws its points on the index path, more than 131,072 rows (design §5.5.2 "行 R3b1 の密度と切り替え",
    /// row R3b1). A layer of at most 131,072 rows draws raw points whatever is set.
    /// </summary>
    /// <param name="draw">Raw points, a density, or a switch by the number of points in the view.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="draw"/> is not a named value.</exception>
    public PointBuilder Draw(PointDraw draw)
    {
        if (!IsDraw(draw))
        {
            throw new ArgumentOutOfRangeException(nameof(draw), draw, "The way of drawing is a named PointDraw value.");
        }

        DrawValue = draw;
        return this;
    }

    /// <summary>
    /// Sets how the layer draws a density (design §5.5.2 "行 R3b1 の密度と切り替え", row R3b1): the mapping of a bin's points
    /// to its colors, the colors, and the number of points in the view above which <see cref="PointDraw.Auto"/> draws one.
    /// </summary>
    /// <param name="norm">How a bin's points map to a place on the colors.</param>
    /// <param name="map">The colors from the emptiest bin to the fullest, or null for shades from transparent to the layer's color.</param>
    /// <param name="threshold">The number of points in the view above which <see cref="PointDraw.Auto"/> draws a density: 1 or more.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="norm"/> is not a named value, or <paramref name="threshold"/> is less than 1.
    /// </exception>
    public PointBuilder Density(DensityNorm norm = DensityNorm.Log, ColorMap? map = null, long threshold = 10_000_000)
    {
        if (!IsDensityNorm(norm))
        {
            throw new ArgumentOutOfRangeException(nameof(norm), norm, "The mapping of a density is a named DensityNorm value.");
        }

        if (threshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "The threshold of a density is 1 point or more.");
        }

        (DensityNormValue, DensityMapValue, DensityThresholdValue) = (norm, map, threshold);
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

    /// <summary>Returns whether a way of drawing is a named value, compared name by name: the compile checks it on every render.</summary>
    internal static bool IsDraw(PointDraw draw) => draw is PointDraw.Auto or PointDraw.Points or PointDraw.Density;

    /// <summary>Returns whether a mapping of a density is a named value, compared name by name.</summary>
    internal static bool IsDensityNorm(DensityNorm norm) => norm is DensityNorm.Log or DensityNorm.Linear or DensityNorm.Cbrt;
}
