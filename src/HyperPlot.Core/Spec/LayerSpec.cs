namespace HyperPlot;

/// <summary>
/// The base of a layer: one geom over the plot's data (design §4.1). Row G1 compiles <see cref="LineLayerSpec"/> only; a
/// layer of another type is refused by the compile.
/// </summary>
public abstract record LayerSpec : SpecNode
{
    /// <summary>Initializes a new instance of the <see cref="LayerSpec"/> class with a new <see cref="SpecNode.Id"/>.</summary>
    private protected LayerSpec() { }
}
