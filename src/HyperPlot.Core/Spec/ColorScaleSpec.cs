namespace HyperPlot;

/// <summary>
/// The base of a color scale (design §4.1, §4.2, row G7c): it maps the values of the layers' <see cref="LayerSpec.ColorBy"/>
/// and <see cref="LayerSpec.FillBy"/> to colors. A plot has one, <see cref="PlotSpec.ScaleColor"/>, shared by every layer.
/// </summary>
public abstract record ColorScaleSpec : ScaleSpec
{
    /// <summary>Initializes a new instance of the <see cref="ColorScaleSpec"/> class with a new <see cref="SpecNode.Id"/>.</summary>
    private protected ColorScaleSpec() { }
}

/// <summary>
/// The ordinal color scale (design §4.2, row G7c): the categories of a <see cref="CategoricalColumn"/>, each a color of a
/// palette. Category i takes the palette's color at i mod n. The domain is all the column's categories.
/// </summary>
public sealed record OrdinalScaleSpec : ColorScaleSpec
{
    internal OrdinalScaleSpec() { }

    /// <summary>Gets the palette; null takes the theme's (<see cref="Theme.Palette"/>).</summary>
    public Palette? Palette { get; init; }

    /// <summary>Gets the legend's title; null or empty draws none.</summary>
    public string? Title { get; init; }
}

/// <summary>
/// The sequential color scale (design §4.2, row G7c): a number goes through <see cref="Scale"/>'s transform, then from the
/// domain's low end (0) to its high end (1) through <see cref="Map"/>. Values outside the domain take the end colors.
/// </summary>
/// <remarks>
/// Inside the type, <c>Scale</c> names the property; the static class is written <c>HyperPlot.Scale</c>.
/// </remarks>
public sealed record SequentialScaleSpec : ColorScaleSpec
{
    internal SequentialScaleSpec(ColorMap map, ContinuousScaleSpec scale) => (Map, Scale) = (map, scale);

    /// <summary>Gets the color map.</summary>
    public ColorMap Map { get; init; }

    /// <summary>
    /// Gets the continuous scale that holds the transform, the domain, reverse, nice, zero, the tick format, and the color
    /// bar's title. Its <see cref="ContinuousScaleSpec.Clamp"/> is not used: values outside the domain take the end colors.
    /// </summary>
    public ContinuousScaleSpec Scale { get; init; }
}

/// <summary>
/// The diverging color scale (design §4.2, row G7c): as the sequential one, with the middle of <see cref="Map"/> at
/// <see cref="Mid"/>. With m the middle in the scale's space, a value v at or below m takes t = 0.5 · (v − low) / (m − low),
/// one above it t = 0.5 + 0.5 · (v − m) / (high − m), as d3-scale's diverging scale.
/// </summary>
public sealed record DivergingScaleSpec : ColorScaleSpec
{
    internal DivergingScaleSpec(ColorMap map, ContinuousScaleSpec scale) => (Map, Scale) = (map, scale);

    /// <summary>Gets the color map.</summary>
    public ColorMap Map { get; init; }

    /// <summary>
    /// Gets the middle in data units, finite; null takes the middle of the domain in the scale's space. The compile refuses a
    /// middle outside the domain.
    /// </summary>
    public double? Mid { get; init; }

    /// <summary>Gets the continuous scale that holds the transform, the domain, and the rest, as <see cref="SequentialScaleSpec.Scale"/>.</summary>
    public ContinuousScaleSpec Scale { get; init; }
}
