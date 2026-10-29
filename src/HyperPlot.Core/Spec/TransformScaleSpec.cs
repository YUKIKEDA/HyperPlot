namespace HyperPlot;

/// <summary>A position scale of a user's transform (design §4.2, requirement N-6). Make one with <see cref="Scale.Transform(IScale)"/>.</summary>
public sealed record TransformScaleSpec : ContinuousScaleSpec
{
    internal TransformScaleSpec(IScale transform) => Transform = transform;

    /// <summary>Gets the transform: strictly increasing, with <see cref="IScale.Inverse"/> undoing <see cref="IScale.Forward"/>.</summary>
    public IScale Transform { get; }

    /// <summary>Gets the domain in data units, or null to learn it from the data.</summary>
    public (double Min, double Max)? Domain { get; init; }

    /// <inheritdoc />
    internal override bool ZeroOption => false;

    /// <inheritdoc />
    internal override (double Min, double Max)? GivenDomain => Domain;

    /// <summary>Returns a copy with a domain given.</summary>
    /// <param name="min">The value at the start of the axis: finite, with a finite mapped value.</param>
    /// <param name="max">The value at the end of the axis: finite and above <paramref name="min"/>, with a finite mapped value.</param>
    /// <returns>The copy.</returns>
    /// <exception cref="ArgumentException">A value or its mapped value is not finite, or <paramref name="min"/> is not below <paramref name="max"/>.</exception>
    public TransformScaleSpec WithDomain(double min, double max)
    {
        CheckMapped(Transform, min, max);
        return this with { Domain = (min, max) };
    }

    /// <inheritdoc />
    internal override IScale CreateTransform() => Transform;

    /// <inheritdoc />
    internal override void CheckGivenDomain()
    {
        if (Domain is { } domain)
        {
            CheckMapped(Transform, domain.Min, domain.Max);
        }
    }
}
