namespace HyperPlot.Interaction;

/// <summary>
/// Fits the y range to the rows the x range shows (design §6.2, row I1a): each proposal's y becomes the least and the
/// greatest y of the lines, steps, and areas between its x ends, read from their pyramids in O(log n). A proposal over x
/// with no such row, or a plot whose x has a transform, keeps its y. Not on by default: add it to
/// <see cref="PlotEngine.Constraints"/>.
/// </summary>
/// <param name="engine">The engine whose plot it reads.</param>
public sealed class AutoRangeY(PlotEngine engine) : IViewConstraint
{
    private readonly PlotEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    /// <inheritdoc />
    public bool Constrain(ViewProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (proposal.TryGet2D(out var x, out _) && _engine.TryVisibleY(proposal.Panel, x, out var y))
        {
            proposal.Set2D(x, y);
        }

        return true;
    }
}
