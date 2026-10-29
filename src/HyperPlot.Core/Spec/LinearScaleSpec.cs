namespace HyperPlot;

/// <summary>A linear position scale (design §4.2): its domain is given, or learned from every layer's column.</summary>
public sealed record LinearScaleSpec : ScaleSpec
{
    /// <summary>Gets the domain, or null to learn it from the data. A domain is finite, with Min below Max.</summary>
    public (double Min, double Max)? Domain { get; init; }

    /// <summary>Returns a copy with a domain given.</summary>
    /// <param name="min">The value at the start of the axis: finite.</param>
    /// <param name="max">The value at the end of the axis: finite and above <paramref name="min"/>.</param>
    /// <returns>The copy.</returns>
    /// <exception cref="ArgumentException">A value is not finite, or <paramref name="min"/> is not below <paramref name="max"/>.</exception>
    public LinearScaleSpec WithDomain(double min, double max)
    {
        CheckDomain(min, max);
        return this with { Domain = (min, max) };
    }

    /// <summary>Throws unless a domain is finite with its min below its max.</summary>
    internal static void CheckDomain(double min, double max)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max) || !(min < max))
        {
            throw new ArgumentException($"A domain is finite with its min below its max; [{min}, {max}] is not.");
        }
    }
}
