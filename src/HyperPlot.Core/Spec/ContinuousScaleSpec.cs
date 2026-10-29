namespace HyperPlot;

/// <summary>
/// The base of the continuous position scales of row G2 (design §4.1, §4.2): linear, log, symlog, pow, time, and a user's
/// <see cref="IScale"/>. Every one can reverse its mapping and clamp values to its domain.
/// </summary>
public abstract record ContinuousScaleSpec : ScaleSpec
{
    /// <summary>Initializes a new instance of the <see cref="ContinuousScaleSpec"/> class with a new <see cref="SpecNode.Id"/>.</summary>
    private protected ContinuousScaleSpec() { }

    /// <summary>Gets a value indicating whether the mapping runs the other way: x from right to left, y from top to bottom.</summary>
    public bool Reverse { get; init; }

    /// <summary>
    /// Gets a value indicating whether values outside the domain are drawn at its nearer end, so that a line runs along the
    /// edge; otherwise the line goes on past the edge and the target cuts it.
    /// </summary>
    public bool Clamp { get; init; }

    /// <summary>Gets a value indicating whether a learned domain is widened to hold 0 (design §4.2). Linear, symlog, and pow.</summary>
    internal abstract bool ZeroOption { get; }

    /// <summary>Gets the domain given in data units, or null; a time scale's is in <see cref="TimeScaleSpec.Domain"/>.</summary>
    internal abstract (double Min, double Max)? GivenDomain { get; }

    /// <summary>Returns the transform to the scale space, or null for a scale that maps data units linearly (linear, time).</summary>
    internal abstract IScale? CreateTransform();

    /// <summary>Throws unless a domain set with <c>with</c> meets the conditions of the kind's <c>WithDomain</c> (design §4.8).</summary>
    internal abstract void CheckGivenDomain();

    /// <summary>Throws unless a domain is finite with its min below its max.</summary>
    private protected static void CheckRange(double min, double max)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max) || !(min < max))
        {
            throw new ArgumentException($"A domain is finite with its min below its max; [{min}, {max}] is not.");
        }
    }

    /// <summary>Throws unless both ends of a domain map to finite values of the scale space (design §4.2).</summary>
    private protected static void CheckMapped(IScale transform, double min, double max)
    {
        CheckRange(min, max);
        Span<double> ends = [min, max];
        Span<double> mapped = stackalloc double[2];
        transform.Forward(ends, mapped);
        if (!double.IsFinite(mapped[0]) || !double.IsFinite(mapped[1]))
        {
            throw new ArgumentException($"The domain [{min}, {max}] maps to [{mapped[0]}, {mapped[1]}], whose ends are not both finite.");
        }
    }
}
