namespace HyperPlot;

/// <summary>Makes the position scales of a plot (design §4.2).</summary>
public static class Scale
{
    /// <summary>Returns a linear scale that learns its domain from the data.</summary>
    /// <returns>The scale; set a domain with <see cref="LinearScaleSpec.WithDomain(double, double)"/>.</returns>
    public static LinearScaleSpec Linear() => new();

    /// <summary>Returns a log10 scale that learns its domain from the data.</summary>
    /// <returns>The scale; set a domain with <see cref="LogScaleSpec.WithDomain(double, double)"/>.</returns>
    public static LogScaleSpec Log() => new();

    /// <summary>Returns a symmetric log scale, sign(v) · log10(1 + |v| / C), that learns its domain from the data.</summary>
    /// <param name="constant">C, the width near 0 where the scale is close to linear: finite and greater than 0.</param>
    /// <returns>The scale.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="constant"/> is not finite or not greater than 0.</exception>
    public static SymlogScaleSpec Symlog(double constant = 1)
    {
        CheckPositive(constant, nameof(constant));
        return new SymlogScaleSpec(constant);
    }

    /// <summary>Returns a power scale, sign(v) · |v|^k, that learns its domain from the data.</summary>
    /// <param name="exponent">k: finite and greater than 0.</param>
    /// <returns>The scale.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="exponent"/> is not finite or not greater than 0.</exception>
    public static PowScaleSpec Pow(double exponent)
    {
        CheckPositive(exponent, nameof(exponent));
        return new PowScaleSpec(exponent);
    }

    /// <summary>Returns a square-root scale: <see cref="Pow(double)"/> with k = 0.5.</summary>
    /// <returns>The scale.</returns>
    public static PowScaleSpec Sqrt() => new(0.5);

    /// <summary>Returns a time scale over a long date-time column, in a unit from an epoch (design §3.1).</summary>
    /// <param name="unit">The unit of the column's values.</param>
    /// <param name="epoch">The date-time of the value 0; by default 0001-01-01 for <see cref="TimeUnit.Ticks"/>, and the Unix epoch otherwise.</param>
    /// <returns>The scale.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="unit"/> is not a named <see cref="TimeUnit"/>.</exception>
    public static TimeScaleSpec Time(TimeUnit unit = TimeUnit.Ticks, DateTime? epoch = null)
    {
        if (unit is not (TimeUnit.Ticks or TimeUnit.Nanoseconds or TimeUnit.Microseconds or TimeUnit.Milliseconds or TimeUnit.Seconds))
        {
            throw new ArgumentOutOfRangeException(nameof(unit), unit, "The unit must be a named TimeUnit.");
        }

        return new TimeScaleSpec(unit, epoch ?? (unit == TimeUnit.Ticks ? DateTime.MinValue : DateTime.UnixEpoch));
    }

    /// <summary>Returns a scale of a user's transform (requirement N-6).</summary>
    /// <param name="transform">The transform: strictly increasing, with an inverse.</param>
    /// <returns>The scale.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transform"/> is null.</exception>
    public static TransformScaleSpec Transform(IScale transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        return new TransformScaleSpec(transform);
    }

    /// <summary>Returns a band scale for the categories of a <see cref="CategoricalColumn"/> on x (design §4.2, row G7b).</summary>
    /// <param name="paddingInner">The share of a step between two bands: at least 0 and at most 1.</param>
    /// <param name="paddingOuter">The space before the first band and after the last, in steps: at least 0 and at most 1.</param>
    /// <param name="align">How the outer space is shared between the two ends: at least 0 and at most 1.</param>
    /// <returns>The scale.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is NaN or outside [0, 1].</exception>
    public static BandScaleSpec Band(double paddingInner = 0.1, double paddingOuter = 0.05, double align = 0.5)
    {
        Check(paddingInner, nameof(paddingInner));
        Check(paddingOuter, nameof(paddingOuter));
        Check(align, nameof(align));
        return new BandScaleSpec
        {
            PaddingInner = paddingInner,
            PaddingOuter = paddingOuter,
            Align = align,
        };

        static void Check(double value, string name)
        {
            if (!(value >= 0 && value <= 1))
            {
                throw new ArgumentOutOfRangeException(name, value, "The value is at least 0 and at most 1.");
            }
        }
    }

    private static void CheckPositive(double value, string name)
    {
        if (!double.IsFinite(value) || !(value > 0))
        {
            throw new ArgumentOutOfRangeException(name, value, "The value must be finite and greater than 0.");
        }
    }
}
