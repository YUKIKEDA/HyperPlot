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
}
