namespace HyperPlot;

/// <summary>
/// The base of every spec node: it holds the node's <see cref="Id"/> (design §4.1). A node is immutable, so two nodes with
/// the same id hold the same values; the compile compares the values of nodes whose ids differ.
/// </summary>
public abstract record SpecNode
{
    private static long _lastId;

    /// <summary>Initializes a new instance of the <see cref="SpecNode"/> class with a new <see cref="Id"/>.</summary>
    private protected SpecNode() => Id = NextId();

    /// <summary>
    /// Initializes a new instance of the <see cref="SpecNode"/> class as a copy, with a new <see cref="Id"/>: the derived
    /// record copies its own members.
    /// </summary>
    /// <param name="original">The node copied.</param>
    protected SpecNode(SpecNode original)
    {
        ArgumentNullException.ThrowIfNull(original);
        Id = NextId();
    }

    /// <summary>Gets the node's identity, new for each node made and each copy <c>with</c> makes.</summary>
    public SpecId Id { get; }

    private static SpecId NextId() => new(Interlocked.Increment(ref _lastId));
}
