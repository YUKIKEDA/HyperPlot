namespace HyperPlot;

/// <summary>
/// A plot: its data, its layers in drawing order, and its position scales (design §4.1). Make one with
/// <see cref="Plot.Create(DataTable)"/>, or set the members directly; the compile checks them either way (design §4.8).
/// </summary>
public sealed record PlotSpec : SpecNode
{
    /// <summary>Gets the table the layers' columns belong to.</summary>
    public required DataTable Data { get; init; }

    /// <summary>Gets the layers, drawn in order. None draws nothing.</summary>
    public required IReadOnlyList<LayerSpec> Layers { get; init; }

    /// <summary>Gets the scale of x, shared by every layer. By default a linear scale that learns its domain (design §4.2).</summary>
    public ScaleSpec ScaleX { get; init; } = Scale.Linear();

    /// <summary>Gets the scale of y, shared by every layer. By default a linear scale that learns its domain (design §4.2).</summary>
    public ScaleSpec ScaleY { get; init; } = Scale.Linear();

    /// <summary>Gets the title above the plot; null or empty for none (design §4.6).</summary>
    public string? Title { get; init; }

    /// <summary>Gets the theme of the guides: <see cref="Theme.Light"/> by default (design §4.7).</summary>
    public Theme Theme { get; init; } = Theme.Light;

    /// <summary>Gets the coordinate system: <see cref="HyperPlot.Coord.Cartesian(bool)"/> without flip by default (design §4.4).</summary>
    public CoordSpec Coord { get; init; } = HyperPlot.Coord.Cartesian();

    /// <summary>
    /// Gets the color scale that the layers' <see cref="LayerSpec.ColorBy"/> and <see cref="LayerSpec.FillBy"/> share (design
    /// §4.2, row G7c). Null takes an ordinal scale for categories and a sequential one for numbers.
    /// </summary>
    public ColorScaleSpec? ScaleColor { get; init; }

    /// <summary>
    /// Gets a secondary x scale, independent of <see cref="ScaleX"/> (design §4.1, row G6a): the layers with
    /// <see cref="LayerSpec.SecondaryX"/> are mapped by it, and it learns its domain from them only. Null for none.
    /// </summary>
    public ContinuousScaleSpec? ScaleX2 { get; init; }

    /// <summary>Gets a secondary y scale, independent of <see cref="ScaleY"/> (row G6a): the layers with <see cref="LayerSpec.SecondaryY"/> are mapped by it.</summary>
    public ContinuousScaleSpec? ScaleY2 { get; init; }

    /// <summary>
    /// Gets the facet that splits the plot into panels by categories (design §4.5, row G6b): <see cref="FacetWrapSpec"/> or
    /// <see cref="FacetGridSpec"/>. Null for one panel.
    /// </summary>
    public FacetSpec? Facet { get; init; }
}
