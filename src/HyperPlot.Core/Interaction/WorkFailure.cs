namespace HyperPlot.Interaction;

/// <summary>
/// A failure of the work behind a layer (design §4.8, §7.1, row W1b1): a worker's pass (a pyramid's chunk, the statistics of
/// a column, the extremes or sums of categories), or what a layer's data turned out to be (an X that decreases, an infinite
/// value a domain would learn). The layer is not drawn until one of its columns' versions moves; then it is tried again. A
/// value, like <see cref="ViewChange"/>.
/// </summary>
public readonly struct WorkFailure
{
    internal WorkFailure(int plot, int layer, Exception exception) => (Plot, Layer, Exception) = (plot, layer, exception);

    /// <summary>Gets the plot's position: 0 for a plot alone, its index in a figure.</summary>
    public int Plot { get; }

    /// <summary>Gets the layer's position in the plot's spec.</summary>
    public int Layer { get; }

    /// <summary>Gets the exception, as the work threw it.</summary>
    public Exception Exception { get; }
}

/// <summary>Handles a failure of a layer's work.</summary>
/// <param name="failure">The failure.</param>
public delegate void WorkFailedHandler(in WorkFailure failure);
