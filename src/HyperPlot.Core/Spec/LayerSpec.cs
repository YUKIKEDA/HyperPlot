namespace HyperPlot;

/// <summary>
/// The base of a layer: one geom over the plot's data (design §4.1). The compile takes the layers of rows G1 and G4 and a
/// <see cref="CustomLayerSpec"/>, and refuses a layer of another type.
/// </summary>
public abstract record LayerSpec : SpecNode
{
    /// <summary>Initializes a new instance of the <see cref="LayerSpec"/> class with a new <see cref="SpecNode.Id"/>.</summary>
    private protected LayerSpec() { }

    /// <summary>Gets the legend's label; null or empty leaves the layer out of the legend (design §4.6).</summary>
    public string? Label { get; init; }

    /// <summary>
    /// Gets the column whose values color the layer (design §4.1, row G7c): the stroke of a line geom, the fill and the outline
    /// of a fill geom. A <see cref="CategoricalColumn"/> (through <see cref="CategoryRef.ToColumnRef"/>) splits the layer into
    /// a series a category; a numeric column colors each element of a bar or a rect. Null colors nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A split layer draws each category as a series: a frame's work is the number of series times the pixels, about
    /// 0.17 ms of CPU a series over 1,920 pixel columns on the reference machine. Past about 12 series a frame takes more
    /// than requirement P-1's 8 ms; at most 256 categories split a layer.
    /// </para>
    /// <para>
    /// The rows of a category that lie in one run are drawn over the layer's own columns. The rows of the others are
    /// gathered into native columns of their own, once, and again when a column changes: as much memory as the layer's
    /// columns for those rows (design §5.5.3).
    /// </para>
    /// </remarks>
    public ColumnRef? ColorBy { get; init; }

    /// <summary>
    /// Gets the column whose values color the fill only, before <see cref="ColorBy"/> (design §4.1, row G7c). Only the fill
    /// geoms take it. Null leaves the fill to <see cref="ColorBy"/>.
    /// </summary>
    public ColumnRef? FillBy { get; init; }

    /// <summary>
    /// Gets how the layer places its elements against each other (design §4.1, row G5a); null for the geom's default: a
    /// stack for bars and areas split by categories, the elements as they are otherwise.
    /// </summary>
    /// <remarks>
    /// A stack over a numeric x keeps, on the workers, the union of the groups' x values and two cumulative columns a group:
    /// as many bytes as the union's length times (an x value plus 16 a group), with their pyramids. It is made again when a
    /// column of the layer changes.
    /// </remarks>
    public PositionSpec? Position { get; init; }

    /// <summary>
    /// Gets what the layer draws from its columns (design §4.1, row G5b): bins, counts, or summaries, which the workers make
    /// once a version of the columns; null draws the columns as they are.
    /// </summary>
    public StatSpec? Stat { get; init; }
}
