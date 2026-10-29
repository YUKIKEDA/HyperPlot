namespace HyperPlot;

/// <summary>
/// A time position scale over a long date-time column (<see cref="Column{T}"/> of <see cref="long"/>, or an integer
/// <see cref="UniformColumn"/>), with its unit and epoch (design §3.1, §4.2). It maps like a linear scale, exactly in the
/// column's unit.
/// </summary>
public sealed record TimeScaleSpec : ContinuousScaleSpec
{
    internal TimeScaleSpec(TimeUnit unit, DateTime epoch) => (Unit, Epoch) = (unit, epoch);

    /// <summary>Gets the unit of the column's values.</summary>
    public TimeUnit Unit { get; }

    /// <summary>Gets the date-time of the value 0.</summary>
    public DateTime Epoch { get; }

    /// <summary>Gets the domain in the column's unit, or null to learn it from the data. Min is below Max.</summary>
    public (long Min, long Max)? Domain { get; init; }

    /// <inheritdoc />
    internal override bool ZeroOption => false;

    /// <inheritdoc />
    internal override (double Min, double Max)? GivenDomain => null;

    /// <summary>Returns a copy with a domain given in the column's unit.</summary>
    /// <param name="min">The value at the start of the axis.</param>
    /// <param name="max">The value at the end of the axis: above <paramref name="min"/>.</param>
    /// <returns>The copy.</returns>
    /// <exception cref="ArgumentException"><paramref name="min"/> is not below <paramref name="max"/>.</exception>
    public TimeScaleSpec WithDomain(long min, long max)
    {
        CheckRange(min, max);
        return this with { Domain = (min, max) };
    }

    /// <summary>
    /// Returns a copy with a domain given as date-times, turned into the column's unit and rounded outward: the start down
    /// (⌊⌋) and the end up (⌈⌉). <see cref="DateTime.Kind"/> is not read.
    /// </summary>
    /// <param name="min">The date-time at the start of the axis.</param>
    /// <param name="max">The date-time at the end of the axis: after <paramref name="min"/>.</param>
    /// <returns>The copy.</returns>
    /// <exception cref="ArgumentException"><paramref name="min"/> is not before <paramref name="max"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A date-time in the unit does not fit in a <see cref="long"/>.</exception>
    public TimeScaleSpec WithDomain(DateTime min, DateTime max)
    {
        if (!(min < max))
        {
            throw new ArgumentException($"A domain has its min before its max; [{min:O}, {max:O}] does not.");
        }

        return WithDomain(ToUnit(min, ceiling: false, nameof(min)), ToUnit(max, ceiling: true, nameof(max)));
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

    private static void CheckRange(long min, long max)
    {
        if (!(min < max))
        {
            throw new ArgumentException($"A domain has its min below its max; [{min}, {max}] does not.");
        }
    }

    /// <summary>Turns a date-time into the scale's unit from its epoch, rounding down or up.</summary>
    private long ToUnit(DateTime value, bool ceiling, string name)
    {
        Int128 ticks = value.Ticks - Epoch.Ticks;
        var units = Unit switch
        {
            TimeUnit.Nanoseconds => ticks * 100,
            TimeUnit.Microseconds => Divide(ticks, 10, ceiling),
            TimeUnit.Milliseconds => Divide(ticks, 10_000, ceiling),
            TimeUnit.Seconds => Divide(ticks, 10_000_000, ceiling),
            _ => ticks,
        };
        if (units < long.MinValue || units > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(name, value, $"The date-time does not fit in a long of {Unit} from {Epoch:O}.");
        }

        return (long)units;
    }

    /// <summary>Divides rounding toward −∞ (⌊⌋) or toward +∞ (⌈⌉), negative values included.</summary>
    private static Int128 Divide(Int128 value, long divisor, bool ceiling)
    {
        var (quotient, remainder) = Int128.DivRem(value, divisor);
        return remainder == 0 ? quotient
            : ceiling ? (remainder > 0 ? quotient + 1 : quotient)
            : remainder < 0 ? quotient - 1
            : quotient;
    }
}
