namespace HyperPlot;

/// <summary>Which edge of an image row 0 goes on (design §4.1, row G9).</summary>
public enum RasterOrigin
{
    /// <summary>Row 0 on the image's top edge, as an image file is read.</summary>
    Upper,

    /// <summary>Row 0 on the image's bottom edge, as a grid of y increasing up.</summary>
    Lower,
}

/// <summary>How a texel larger than a pixel is read, when an image is magnified (design §4.1, row G9).</summary>
public enum RasterInterpolation
{
    /// <summary>The nearest texel: each texel a block of one color.</summary>
    Nearest,

    /// <summary>The four nearest texels, weighted by distance.</summary>
    Bilinear,
}

/// <summary>
/// The rectangle an image is put on, in data units (design §4.1, row G9): the x of its left and right edges and the y of
/// its bottom and top edges. A left above the right, or a bottom above the top, turns the image over.
/// </summary>
/// <param name="Left">The x of the image's left edge, where column 0 is.</param>
/// <param name="Right">The x of the image's right edge.</param>
/// <param name="Bottom">The y of the image's bottom edge.</param>
/// <param name="Top">The y of the image's top edge.</param>
public readonly record struct RasterExtent(double Left, double Right, double Bottom, double Top);

/// <summary>
/// A raster put on a rectangle (design §4.1, §5.5.3, row G9): numbers painted through the plot's color scale, or an RGBA
/// image as it is. Only the visible tiles of a level of its mipmap go to the GPU.
/// </summary>
/// <remarks>
/// The layer holds its raster directly: a raster's elements (width × height) are not as many as the rows of the plot's table.
/// The plot's x and y are linear or time scales, and its coordinate system is cartesian: an image does not bend.
/// </remarks>
public sealed record ImageLayerSpec : LayerSpec
{
    /// <summary>Gets the raster.</summary>
    public required Raster Raster { get; init; }

    /// <summary>
    /// Gets the rectangle the image is put on, or null for (−0.5, width − 0.5, −0.5, height − 0.5): pixel (column c, row r)
    /// centered at x = c and at y = height − 1 − r on the upper origin, r on the lower one.
    /// </summary>
    public RasterExtent? Extent { get; init; }

    /// <summary>Gets which edge row 0 goes on: the top by default. The image's orientation is the screen's; the layer does not turn the y scale.</summary>
    public RasterOrigin Origin { get; init; }

    /// <summary>Gets how a magnified texel is read: the nearest by default. A shrunk image is read from the mipmap's averages.</summary>
    public RasterInterpolation Interpolation { get; init; }

    /// <summary>Gets the alpha the whole image is drawn with: from 0 to 1, 1 by default. A NaN element is transparent whatever it is.</summary>
    public double Opacity { get; init; } = 1;

    /// <summary>Gets the rectangle the image is put on: the given extent, or the default one of its raster.</summary>
    internal RasterExtent ExtentOrDefault => Extent ?? new RasterExtent(-0.5, Raster.Width - 0.5, -0.5, Raster.Height - 0.5);
}

/// <summary>Sets the rectangle, the origin, the reading, and the opacity of an image layer (design §4.1, row G9).</summary>
public sealed class ImageBuilder
{
    internal ImageBuilder() { }

    /// <summary>Gets the extent set so far, or null for the default.</summary>
    internal RasterExtent? ExtentValue { get; private set; }

    /// <summary>Gets the origin set so far.</summary>
    internal RasterOrigin OriginValue { get; private set; }

    /// <summary>Gets the interpolation set so far.</summary>
    internal RasterInterpolation InterpolationValue { get; private set; }

    /// <summary>Gets the opacity set so far.</summary>
    internal double OpacityValue { get; private set; } = 1;

    /// <summary>Sets the rectangle the image is put on, in data units.</summary>
    /// <param name="left">The x of the left edge, where column 0 is.</param>
    /// <param name="right">The x of the right edge.</param>
    /// <param name="bottom">The y of the bottom edge.</param>
    /// <param name="top">The y of the top edge.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is not finite, or the left equals the right or the bottom the top.</exception>
    public ImageBuilder Extent(double left, double right, double bottom, double top)
    {
        var extent = new RasterExtent(left, right, bottom, top);
        CheckExtent(extent, nameof(left));
        ExtentValue = extent;
        return this;
    }

    /// <summary>Sets which edge row 0 goes on.</summary>
    /// <param name="origin">The origin.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="origin"/> is not a named value.</exception>
    public ImageBuilder Origin(RasterOrigin origin)
    {
        CheckOrigin(origin, nameof(origin));
        OriginValue = origin;
        return this;
    }

    /// <summary>Sets how a magnified texel is read.</summary>
    /// <param name="interpolation">The interpolation.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="interpolation"/> is not a named value.</exception>
    public ImageBuilder Interpolation(RasterInterpolation interpolation)
    {
        CheckInterpolation(interpolation, nameof(interpolation));
        InterpolationValue = interpolation;
        return this;
    }

    /// <summary>Sets the alpha the whole image is drawn with.</summary>
    /// <param name="opacity">The opacity, from 0 to 1.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="opacity"/> is not from 0 to 1.</exception>
    public ImageBuilder Opacity(double opacity)
    {
        CheckOpacity(opacity, nameof(opacity));
        OpacityValue = opacity;
        return this;
    }

    /// <summary>Returns whether an extent's values are finite and its edges apart.</summary>
    internal static bool IsExtent(in RasterExtent extent) =>
        double.IsFinite(extent.Left)
        && double.IsFinite(extent.Right)
        && double.IsFinite(extent.Bottom)
        && double.IsFinite(extent.Top)
        && extent.Left != extent.Right
        && extent.Bottom != extent.Top;

    /// <summary>Returns whether an opacity is from 0 to 1.</summary>
    internal static bool IsOpacity(double opacity) => opacity is >= 0 and <= 1;

    /// <summary>Throws unless an extent's values are finite and its edges apart.</summary>
    internal static void CheckExtent(in RasterExtent extent, string name)
    {
        if (!IsExtent(extent))
        {
            throw new ArgumentOutOfRangeException(
                name,
                extent,
                "An extent's values are finite, and its left and right, and its bottom and top, apart."
            );
        }
    }

    /// <summary>Throws unless an origin is a named value.</summary>
    internal static void CheckOrigin(RasterOrigin origin, string name)
    {
        if ((uint)origin > (uint)RasterOrigin.Lower)
        {
            throw new ArgumentOutOfRangeException(name, origin, "The origin is not a named value.");
        }
    }

    /// <summary>Throws unless an interpolation is a named value.</summary>
    internal static void CheckInterpolation(RasterInterpolation interpolation, string name)
    {
        if ((uint)interpolation > (uint)RasterInterpolation.Bilinear)
        {
            throw new ArgumentOutOfRangeException(name, interpolation, "The interpolation is not a named value.");
        }
    }

    /// <summary>Throws unless an opacity is from 0 to 1.</summary>
    internal static void CheckOpacity(double opacity, string name)
    {
        if (!IsOpacity(opacity))
        {
            throw new ArgumentOutOfRangeException(name, opacity, "An opacity is from 0 to 1.");
        }
    }

    /// <summary>Gets a value indicating whether the layer's x goes on the secondary x scale (row G6a).</summary>
    internal bool SecondaryXValue { get; private set; }

    /// <summary>Gets a value indicating whether the layer's y goes on the secondary y scale (row G6a).</summary>
    internal bool SecondaryYValue { get; private set; }

    /// <summary>Puts the layer's x on <see cref="PlotSpec.ScaleX2"/> (design §4.1, row G6a).</summary>
    /// <returns>This builder.</returns>
    public ImageBuilder SecondaryX()
    {
        SecondaryXValue = true;
        return this;
    }

    /// <summary>Puts the layer's y on <see cref="PlotSpec.ScaleY2"/> (design §4.1, row G6a).</summary>
    /// <returns>This builder.</returns>
    public ImageBuilder SecondaryY()
    {
        SecondaryYValue = true;
        return this;
    }
}
