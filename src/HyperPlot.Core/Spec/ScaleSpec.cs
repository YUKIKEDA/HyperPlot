namespace HyperPlot;

/// <summary>
/// The base of a scale: how data values map to a position (design §4.1, §4.2). Row G1 compiles
/// <see cref="LinearScaleSpec"/> only; a scale of another type is refused by the compile.
/// </summary>
public abstract record ScaleSpec : SpecNode
{
    /// <summary>Initializes a new instance of the <see cref="ScaleSpec"/> class with a new <see cref="SpecNode.Id"/>.</summary>
    private protected ScaleSpec() { }
}
