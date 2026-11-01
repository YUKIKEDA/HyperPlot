namespace HyperPlot;

/// <summary>
/// How a layer places its elements against each other (design §4.1, row G5a): as they are, stacked, stacked to one, or side
/// by side. The groups are the categories of the layer's <see cref="LayerSpec.FillBy"/> or <see cref="LayerSpec.ColorBy"/>;
/// a layer that is not split is one group. A position works inside its layer only.
/// </summary>
/// <remarks>Make one with <see cref="Position"/>. The compile takes the four kinds of this row and refuses another.</remarks>
public abstract record PositionSpec : SpecNode
{
    /// <summary>Initializes a new instance of the <see cref="PositionSpec"/> class with a new <see cref="SpecNode.Id"/>.</summary>
    private protected PositionSpec() { }
}

/// <summary>The elements as they are: the groups of a split layer overlap, drawn in their categories' order.</summary>
public sealed record IdentityPositionSpec : PositionSpec;

/// <summary>
/// The groups' values at the same x stacked (design §4.1): positive values up from 0, negative ones down from 0, the first
/// category outermost. The rows of one group at one x add up. Bars and areas take it; it is the default of a split one.
/// </summary>
public sealed record StackPositionSpec : PositionSpec;

/// <summary>
/// The stack divided at each x by its sum (design §4.1): positive values from 0 to 1, negative ones from −1 to 0; an x whose
/// sum is 0 stays at 0. Bars and areas take it.
/// </summary>
public sealed record FillPositionSpec : PositionSpec;

/// <summary>
/// The groups side by side (design §4.1): a band, or a bar's width over a numeric x, split into a place for each category
/// of the color column, the first on the left. Bars take it, and error bars and vertical rules over a categorical x.
/// </summary>
public sealed record DodgePositionSpec : PositionSpec
{
    /// <summary>Gets the part of each place left empty, half on each side: at least 0 and below 1, 0 by default.</summary>
    public double Padding { get; init; }
}

/// <summary>Makes the positions of a layer (design §4.1, row G5a).</summary>
public static class Position
{
    /// <summary>Gets the position that places the elements as they are.</summary>
    public static IdentityPositionSpec Identity { get; } = new();

    /// <summary>Gets the position that stacks the groups.</summary>
    public static StackPositionSpec Stack { get; } = new();

    /// <summary>Gets the position that stacks the groups and divides each x by its sum.</summary>
    public static FillPositionSpec Fill { get; } = new();

    /// <summary>Returns the position that places the groups side by side.</summary>
    /// <param name="padding">The part of each place left empty: at least 0 and below 1.</param>
    /// <returns>The position.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="padding"/> is not at least 0 and below 1.</exception>
    public static DodgePositionSpec Dodge(double padding = 0) =>
        padding is >= 0 and < 1
            ? new DodgePositionSpec { Padding = padding }
            : throw new ArgumentOutOfRangeException(nameof(padding), padding, "The padding is at least 0 and below 1.");
}
