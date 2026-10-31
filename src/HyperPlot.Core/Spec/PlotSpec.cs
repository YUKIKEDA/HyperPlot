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
}
