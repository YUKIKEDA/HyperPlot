namespace HyperPlot;

/// <summary>A power position scale, sign(v) · |v|^k; <see cref="Scale.Sqrt"/> is k = 0.5 (design §4.2).</summary>
public sealed record PowScaleSpec : ContinuousScaleSpec
{
    // One transform a scale: checking a domain and drawing do not make another (design §4.8, invariant 3). A copy by `with`
    // keeps k and shares it.
    private readonly PowTransform _transform;

    internal PowScaleSpec(double exponent) => (Exponent, _transform) = (exponent, new PowTransform(exponent));

    /// <summary>Gets k: finite and greater than 0.</summary>
    public double Exponent { get; }

    /// <summary>Gets the domain in data units, or null to learn it from the data. A domain is finite, with Min below Max.</summary>
    public (double Min, double Max)? Domain { get; init; }

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
    /// <exception cref="ArgumentException">A value or its mapped value is not finite, or <paramref name="min"/> is not below <paramref name="max"/>.</exception>
    public PowScaleSpec WithDomain(double min, double max)
    {
        CheckMapped(CreateTransform(), min, max);
        return this with { Domain = (min, max) };
    }

    /// <inheritdoc />
    internal override IScale CreateTransform() => _transform;

    /// <inheritdoc />
    internal override void CheckGivenDomain()
    {
        if (Domain is { } domain)
        {
            CheckMapped(CreateTransform(), domain.Min, domain.Max);
        }
    }
}
