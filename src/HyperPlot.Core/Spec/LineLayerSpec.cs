using HyperPlot.Rendering;

namespace HyperPlot;

/// <summary>A line through the points of two columns, drawn through M4 (design §4.1, §5.5.1).</summary>
public sealed record LineLayerSpec : LayerSpec
{
    /// <summary>Gets the X column: a <see cref="UniformColumn"/>, or a non-decreasing <see cref="Column{T}"/> with no NaN.</summary>
    public required ColumnRef X { get; init; }

    /// <summary>
    /// Gets the Y column: a <see cref="Column{T}"/>. NaN breaks the line. Not required since row G5c: a layer with a density
    /// stat has none; a layer without a stat has one, or the compile throws.
    /// </summary>
    public ColumnRef Y { get; init; }

    /// <summary>
    /// Gets the stroke: by default the layer's series color from the theme's palette (design §4.1), 1 wide, solid. The width
    /// and the dash lengths are in DIPs, times the renderer's scale (design §4.6).
    /// </summary>
    public StrokeSpec Style { get; init; } = new(null);
}
