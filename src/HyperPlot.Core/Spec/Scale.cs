namespace HyperPlot;

/// <summary>Makes the scales of a plot (design §4.2).</summary>
public static class Scale
{
    /// <summary>Returns a linear scale that learns its domain from the data.</summary>
    /// <returns>The scale; set a domain with <see cref="LinearScaleSpec.WithDomain(double, double)"/>.</returns>
    public static LinearScaleSpec Linear() => new();
}
