namespace HyperPlot;

/// <summary>
/// A log10 position scale (design §4.2). A learned end at or below 0 fails the learning; with a domain given, values at or
/// below 0 are dropped and break the line.
/// </summary>
public sealed record LogScaleSpec : ContinuousScaleSpec
{
    /// <summary>Gets the domain in data units, or null to learn it from the data. A domain is finite, with 0 &lt; Min &lt; Max.</summary>
    public (double Min, double Max)? Domain { get; init; }

    /// <inheritdoc />
    internal override bool ZeroOption => false;

    /// <inheritdoc />
    internal override (double Min, double Max)? GivenDomain => Domain;

    /// <summary>Returns a copy with a domain given.</summary>
    /// <param name="min">The value at the start of the axis: finite and greater than 0.</param>
    /// <param name="max">The value at the end of the axis: finite and above <paramref name="min"/>.</param>
    /// <returns>The copy.</returns>
    /// <exception cref="ArgumentException">A value is not finite, <paramref name="min"/> is not above 0, or not below <paramref name="max"/>.</exception>
    public LogScaleSpec WithDomain(double min, double max)
    {
        CheckMapped(LogTransform.Instance, min, max);
        return this with { Domain = (min, max) };
    }

    /// <inheritdoc />
    internal override IScale CreateTransform() => LogTransform.Instance;

    /// <inheritdoc />
    internal override void CheckGivenDomain()
    {
        if (Domain is { } domain)
        {
            CheckMapped(LogTransform.Instance, domain.Min, domain.Max);
        }
    }
}
