namespace HyperPlot;

/// <summary>
/// An axis on the side opposite a primary scale's that shows a transform of its values (design §4.1, row G6a), as ggplot2's
/// <c>sec_axis</c> does: the layers are mapped by the primary scale, and the ticks are chosen over the transformed ends of
/// its domain and placed where their values map back to. It draws no grid.
/// </summary>
/// <remarks>
/// The transform is strictly monotonic over the domain, increasing or decreasing: the compile checks only that both ends map
/// back to themselves, and a transform that folds inside the domain draws ticks over each other. The diff compares the
/// transform by reference and the title and format by value, so that a host that makes its spec again each frame reuses one
/// <see cref="IScale"/>.
/// </remarks>
public sealed record SecondaryAxisSpec : SpecNode
{
    /// <summary>
    /// Gets the transform from the primary scale's data values to the axis's values, <see cref="IScale.Forward"/> one way and
    /// <see cref="IScale.Inverse"/> back; null for a copy of the primary axis (ggplot2's <c>dup_axis</c>).
    /// </summary>
    public IScale? Transform { get; init; }

    /// <summary>Gets the axis title; null or empty for none.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the tick format, or null for the default (design §4.6).</summary>
    public TickFormatter? TickFormat { get; init; }
}
