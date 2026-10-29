namespace HyperPlot.Tests;

/// <summary>The position scales of row G2 (design §4.1, §4.2): the transforms, the factories, and the domains.</summary>
public sealed class ScaleSpecTests
{
    /// <summary>
    /// The built-in transforms give the values of their formulas, undo each other, and are strictly increasing across 0
    /// (design §4.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The transforms of log, symlog with C = 1 and 0.5, and pow with k = 2 and 0.5; 2,000 sorted random values in
    ///   [-1e6, 1e6] with -1e-9, 0, and 1e-9 among them (positive values only for log).
    ///
    /// Steps:
    /// - Map sample values forward; map the sorted values forward and back.
    ///
    /// Expected:
    /// - log10(100) = 2, symlog(9) = 1 for C = 1 and log10(19) for C = 0.5, symlog(-9) = -1, pow 2 of -3 = -9, pow 0.5 of 4 =
    ///   2; log of 0 is −∞ and of -1 NaN. The mapped values strictly increase (symlog and pow through 0), and mapping back
    ///   gives each value within 1e-9 of its size.
    /// </remarks>
    [Fact]
    public void Transforms_FollowTheirFormulas()
    {
        Assert.Equal([2, double.NegativeInfinity, double.NaN], Forward(Scale.Log().CreateTransform(), 100, 0, -1));
        Assert.Equal([1, -1, 0], Forward(Scale.Symlog().CreateTransform(), 9, -9, 0));
        Assert.Equal(Math.Log10(19), Forward(Scale.Symlog(0.5).CreateTransform(), 9)[0]);
        Assert.Equal([-9, 9], Forward(Scale.Pow(2).CreateTransform(), -3, 3));
        Assert.Equal([2, -2], Forward(Scale.Sqrt().CreateTransform(), 4, -4));

        var random = new Random(83);
        double[] values = [.. Enumerable.Range(0, 2000).Select(_ => (random.NextDouble() - 0.5) * 2e6).Concat([-1e-9, 0, 1e-9]).Order()];
        double[] positive = [.. values.Where(v => v > 0)];
        foreach (
            var (transform, inputs) in new (IScale, double[])[]
            {
                (Scale.Log().CreateTransform(), positive),
                (Scale.Symlog().CreateTransform(), values),
                (Scale.Symlog(0.5).CreateTransform(), values),
                (Scale.Pow(2).CreateTransform(), values),
                (Scale.Sqrt().CreateTransform(), values),
            }
        )
        {
            var mapped = new double[inputs.Length];
            var back = new double[inputs.Length];
            transform.Forward(inputs, mapped);
            transform.Inverse(mapped, back);
            for (var i = 0; i < inputs.Length; i++)
            {
                Assert.True(i == 0 || mapped[i - 1] < mapped[i], $"{transform.GetType().Name} does not increase at {inputs[i]}.");
                Assert.True(
                    Math.Abs(back[i] - inputs[i]) <= 1e-9 * Math.Max(1, Math.Abs(inputs[i])),
                    $"{transform.GetType().Name} does not come back to {inputs[i]}: {back[i]}."
                );
            }
        }
    }

    /// <summary>The factories refuse what design §4.2 refuses, and set the defaults it gives.</summary>
    /// <remarks>
    /// Preconditions:
    /// - None.
    ///
    /// Steps:
    /// - Make symlog and pow scales with 0, -1, NaN, and ∞; a time scale with an unnamed unit; a transform scale of null;
    ///   time scales with their default epochs; a square-root scale.
    ///
    /// Expected:
    /// - ArgumentOutOfRangeException naming constant, exponent, and unit; ArgumentNullException for null. The default epoch
    ///   is 0001-01-01 for ticks and the Unix epoch for the other units; Sqrt is k = 0.5; reverse, clamp, and zero are off.
    /// </remarks>
    [Fact]
    public void Factories_RefuseAndDefaultAsTheDesignSays()
    {
        foreach (var bad in new[] { 0, -1, double.NaN, double.PositiveInfinity })
        {
            Assert.Equal("constant", Assert.Throws<ArgumentOutOfRangeException>(() => Scale.Symlog(bad)).ParamName);
            Assert.Equal("exponent", Assert.Throws<ArgumentOutOfRangeException>(() => Scale.Pow(bad)).ParamName);
        }

        Assert.Equal("unit", Assert.Throws<ArgumentOutOfRangeException>(() => Scale.Time((TimeUnit)5)).ParamName);
        Assert.Throws<ArgumentNullException>(() => Scale.Transform(null!));
        Assert.Equal(DateTime.MinValue, Scale.Time().Epoch);
        Assert.Equal(DateTime.UnixEpoch, Scale.Time(TimeUnit.Nanoseconds).Epoch);
        Assert.Equal(DateTime.UnixEpoch, Scale.Time(TimeUnit.Seconds).Epoch);
        Assert.Equal(new DateTime(2000, 1, 1), Scale.Time(TimeUnit.Milliseconds, new DateTime(2000, 1, 1)).Epoch);
        Assert.Equal(0.5, Scale.Sqrt().Exponent);
        var linear = Scale.Linear();
        Assert.False(linear.Reverse || linear.Clamp || linear.Zero);
    }

    /// <summary>Each kind's WithDomain refuses a domain its ends or their mapped values do not allow (design §4.2).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A transform that maps a negative value to NaN.
    ///
    /// Steps:
    /// - Give log domains with min 0 and -1; pow 2 a domain up to 1e200; the transform a domain from -1; linear, symlog, and
    ///   pow domains with min = max and NaN; time domains with min = max as longs, and as date-times 5 ticks past a millisecond in a millisecond scale (rounding outward would give them a width of 1).
    ///
    /// Expected:
    /// - ArgumentException for each; a log domain of (1e-300, 1) and a symlog domain of (-1e300, 1e300) are taken.
    /// </remarks>
    [Fact]
    public void WithDomain_OutsideTheKindsConditions_Throws()
    {
        Assert.Throws<ArgumentException>(() => Scale.Log().WithDomain(0, 1));
        Assert.Throws<ArgumentException>(() => Scale.Log().WithDomain(-1, 1));
        Assert.Throws<ArgumentException>(() => Scale.Pow(2).WithDomain(0, 1e200));
        Assert.Throws<ArgumentException>(() => Scale.Transform(new Log2()).WithDomain(-1, 1));
        Assert.Throws<ArgumentException>(() => Scale.Linear().WithDomain(1, 1));
        Assert.Throws<ArgumentException>(() => Scale.Symlog().WithDomain(double.NaN, 1));
        Assert.Throws<ArgumentException>(() => Scale.Pow(2).WithDomain(2, 1));
        Assert.Throws<ArgumentException>(() => Scale.Time().WithDomain(5, 5));
        var instant = new DateTime(2026, 1, 1).AddTicks(5);
        Assert.Throws<ArgumentException>(() => Scale.Time(TimeUnit.Milliseconds).WithDomain(instant, instant));
        Assert.Equal((1e-300, 1), Scale.Log().WithDomain(1e-300, 1).Domain);
        Assert.Equal((-1e300, 1e300), Scale.Symlog().WithDomain(-1e300, 1e300).Domain);
    }

    /// <summary>
    /// A date-time domain is turned into the unit from the epoch and rounded outward, ⌊⌋ at the start and ⌈⌉ at the end, also
    /// before the epoch; a value that does not fit in a long is refused; the kind of the date-time is not read (design §4.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A millisecond scale from the Unix epoch; date-times 5 ticks past a millisecond in 2026 and 5 ticks before one in
    ///   1969; a nanosecond scale.
    ///
    /// Steps:
    /// - Give each pair as a domain; give the nanosecond scale a domain up to 2300; give the same instants as Utc and Local.
    ///
    /// Expected:
    /// - 2026: the start is floored and the end ceiled; 1969 (negative): the start goes toward −∞ and the end toward +∞; the
    ///   ticks scale keeps the ticks; 2300 in nanoseconds throws ArgumentOutOfRangeException naming max; Utc and Local give
    ///   the same domain.
    /// </remarks>
    [Fact]
    public void WithDomain_DateTimes_RoundOutward()
    {
        var milliseconds = Scale.Time(TimeUnit.Milliseconds);
        var start = new DateTime(2026, 10, 29, 0, 0, 0, 1, DateTimeKind.Utc).AddTicks(5);
        var end = start.AddMilliseconds(3);
        var startUnits = (start - DateTime.UnixEpoch).Ticks / 10_000;
        Assert.Equal((startUnits, startUnits + 4), milliseconds.WithDomain(start, end).Domain);

        var before = new DateTime(1969, 12, 31, 23, 59, 59, 998, DateTimeKind.Utc).AddTicks(-5);
        var after = new DateTime(1969, 12, 31, 23, 59, 59, 999, DateTimeKind.Utc).AddTicks(-5);
        Assert.Equal((-3L, -1L), milliseconds.WithDomain(before, after).Domain);

        Assert.Equal((start.Ticks, end.Ticks), Scale.Time().WithDomain(start, end).Domain);
        var nanoseconds = Scale.Time(TimeUnit.Nanoseconds);
        Assert.Equal("max", Assert.Throws<ArgumentOutOfRangeException>(() => nanoseconds.WithDomain(start, new DateTime(2300, 1, 1))).ParamName);
        var local = DateTime.SpecifyKind(start, DateTimeKind.Local);
        Assert.Equal(
            milliseconds.WithDomain(start, end).Domain,
            milliseconds.WithDomain(local, DateTime.SpecifyKind(end, DateTimeKind.Local)).Domain
        );
    }

    private static double[] Forward(IScale? transform, params double[] values)
    {
        var mapped = new double[values.Length];
        transform!.Forward(values, mapped);
        return mapped;
    }

    /// <summary>log2, which maps a value at or below 0 to −∞ or NaN.</summary>
    internal sealed class Log2 : IScale
    {
        public void Forward(ReadOnlySpan<double> values, Span<double> destination)
        {
            for (var i = 0; i < values.Length; i++)
            {
                destination[i] = Math.Log2(values[i]);
            }
        }

        public void Inverse(ReadOnlySpan<double> values, Span<double> destination)
        {
            for (var i = 0; i < values.Length; i++)
            {
                destination[i] = Math.Pow(2, values[i]);
            }
        }
    }
}
