namespace HyperPlot;

/// <summary>A linear position scale (design §4.2): its domain is given, or learned from every layer's column.</summary>
public sealed record LinearScaleSpec : ContinuousScaleSpec
{
    /// <summary>Gets the domain, or null to learn it from the data. A domain is finite, with Min below Max.</summary>
    public (double Min, double Max)? Domain { get; init; }

    /// <summary>Gets the format of the tick labels, or null for the default (design §4.6).</summary>
    public TickFormatter? TickFormat { get; init; }

    /// <inheritdoc />
    internal override TickFormatter? NumericFormat => TickFormat;

    /// <summary>Gets a value indicating whether a learned domain is widened to hold 0. A given domain is kept as it is.</summary>
    public bool Zero { get; init; }

    /// <inheritdoc />
    internal override bool ZeroOption => Zero;

    /// <inheritdoc />
    internal override (double Min, double Max)? GivenDomain => Domain;

    /// <summary>Returns a copy with a domain given.</summary>
    /// <param name="min">The value at the start of the axis: finite.</param>
    /// <param name="max">The value at the end of the axis: finite and above <paramref name="min"/>.</param>
    /// <returns>The copy.</returns>
    /// <exception cref="ArgumentException">A value is not finite, or <paramref name="min"/> is not below <paramref name="max"/>.</exception>
    public LinearScaleSpec WithDomain(double min, double max)
    {
        CheckRange(min, max);
        return this with { Domain = (min, max) };
    }

    /// <inheritdoc />
    internal override IScale? CreateTransform() => null;

    /// <inheritdoc />
    internal override void CheckGivenDomain()
    {
        if (Domain is { } domain)
        {
            CheckRange(domain.Min, domain.Max);
        }
    }
}
