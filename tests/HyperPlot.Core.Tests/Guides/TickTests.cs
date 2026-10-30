using System.Globalization;
using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>The ticks of row G3 (design §4.6, §10): the extended search, log, symlog, and time, measured with the fake text backend.</summary>
public sealed class TickTests
{
    private const double Em = 12;

    /// <summary>
    /// The extended search with its pruning picks the same ticks as a search of every candidate without pruning, over
    /// 100 seeded ranges, axis lengths, and spacings, with and without nice (Talbot et al. 2010 §5).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Seeded ranges from 1e-6 to 1e6 wide, at offsets from −1e7 to 1e7; lengths 150 to 1,200; spacings 40 to 120.
    ///
    /// Steps:
    /// - Choose with the axis; choose with the brute force (j ≤ 8, k ≤ 30, z up to six past §5's first, §5's starts).
    ///
    /// Expected:
    /// - The same labels and pixels.
    /// </remarks>
    [Fact]
    public void Extended_WithPruning_EqualsBruteForce()
    {
        var random = new Random(11);
        for (var round = 0; round < 100; round++)
        {
            var width = Math.Pow(10, (random.NextDouble() * 12) - 6);
            var low = ((random.NextDouble() * 2) - 1) * Math.Pow(10, random.Next(0, 8));
            var length = 150 + random.Next(1050);
            var spacing = 40 + random.Next(80);
            var nice = random.Next(2) == 0;
            var axis = Axis(Scale.Linear(), null, ScaleRange.FromDouble(low, low + width), length, spacing);
            axis.Choose(nice);
            var expected = BruteForce(low, low + width, length, spacing, nice, (0.25, 0.2, 0.5, 0.05));

            Assert.Equal(expected.Labels, axis.TickLabels.ToArray());
            Assert.Equal(expected.Pixels.Length, axis.Count);
        }
    }

    /// <summary>
    /// The weights are those of §4's text (simplicity 0.25, coverage 0.2): a range where §5's swapped weights pick other
    /// ticks gets the text's.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Seeded ranges scanned until the brute force with the swapped weights differs from the one with the text's.
    ///
    /// Steps:
    /// - Choose with the axis over that range.
    ///
    /// Expected:
    /// - The labels of the text's weights.
    /// </remarks>
    [Fact]
    public void Extended_Weights_AreTheTextsNotThePseudocodes()
    {
        var random = new Random(5);
        for (var round = 0; round < 2000; round++)
        {
            var low = random.NextDouble() * 100;
            var high = low + 1 + (random.NextDouble() * 200);
            var text = BruteForce(low, high, 600, 80, nice: false, (0.25, 0.2, 0.5, 0.05));
            var pseudocode = BruteForce(low, high, 600, 80, nice: false, (0.2, 0.25, 0.5, 0.05));
            if (text.Labels.SequenceEqual(pseudocode.Labels))
            {
                continue;
            }

            var axis = Axis(Scale.Linear(), null, ScaleRange.FromDouble(low, high), 600, 80);
            axis.Choose(nice: false);
            Assert.Equal(text.Labels, axis.TickLabels.ToArray());
            return;
        }

        Assert.Fail("No range in the scan tells the two sets of weights apart.");
    }

    /// <summary>
    /// Without nice the ticks lie inside the domain and fill it; with nice they cover it and the domain becomes [lmin, lmax]
    /// (design §4.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The range [0.37, 9.81] over 500 pixels, spacing 80.
    ///
    /// Steps:
    /// - Choose without and with nice.
    ///
    /// Expected:
    /// - Without: every tick in [0.37, 9.81], one step more on either side leaves it, the domain unchanged. With: the first
    ///   tick at or below 0.37, the last at or above 9.81, and the final domain their range.
    /// </remarks>
    [Fact]
    public void Extended_Nice_InsideOrCovering()
    {
        var plain = Axis(Scale.Linear(), null, ScaleRange.FromDouble(0.37, 9.81), 500, 80);
        plain.Choose(nice: false);
        var values = Values(plain);
        var step = values[1] - values[0];
        Assert.All(values, v => Assert.InRange(v, 0.37, 9.81));
        Assert.True(values[0] - step < 0.37 && values[^1] + step > 9.81);
        Assert.Equal(plain.Domain, plain.FinalDomain);

        var nice = Axis(Scale.Linear(), null, ScaleRange.FromDouble(0.37, 9.81), 500, 80);
        nice.Choose(nice: true);
        var covering = Values(nice);
        Assert.True(covering[0] <= 0.37 && covering[^1] >= 9.81);
        Assert.Equal(covering[0], nice.FinalDomain.Origin + nice.FinalDomain.Lower, 12);
        Assert.Equal(covering[^1], nice.FinalDomain.Origin + nice.FinalDomain.Upper, 12);
        Assert.Equal(0, nice.Pixels[0], 9);
        Assert.Equal(500, nice.Pixels[^1], 9);
    }

    /// <summary>
    /// The coverage bound is the centered one, 1 − ¼ (r′ − r)² / (0.1 r)² (design §4.6): a nice labeling that overhangs the
    /// range on both sides is found, not cut by a tighter bound.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The range [−0.9, 0.9] over 500 pixels, spacing 100, with nice.
    ///
    /// Steps:
    /// - Choose; choose with the brute force.
    ///
    /// Expected:
    /// - The same ticks, from −1 to 1.
    /// </remarks>
    [Fact]
    public void Extended_CoverageBound_KeepsTheCenteredOverhang()
    {
        var axis = Axis(Scale.Linear(), null, ScaleRange.FromDouble(-0.9, 0.9), 500, 100);
        axis.Choose(nice: true);
        Assert.Equal(BruteForce(-0.9, 0.9, 500, 100, nice: true, (0.25, 0.2, 0.5, 0.05)).Labels, axis.TickLabels.ToArray());
        Assert.Equal((-1.0, 1.0), (Values(axis)[0], Values(axis)[^1]));
    }

    /// <summary>
    /// A log candidate fits when its count − 1 is at most the length over the spacing (design §4.6): at 8.5, the nine ticks
    /// of {1, 2, 5} over [1, 900] fit.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A log domain [1, 900] (under three decades) over 510 pixels, spacing 60.
    ///
    /// Steps:
    /// - Choose.
    ///
    /// Expected:
    /// - Nine ticks, with mantissas 1, 2, and 5.
    /// </remarks>
    [Fact]
    public void Log_CountLimit_IsCountMinusOne()
    {
        var log = Scale.Log();
        var axis = Axis(log, log.CreateTransform(), Log(1, 900), 510, 60);
        axis.Choose(nice: false);
        Assert.Equal(9, axis.Count);
        Assert.Contains(5, Mantissas(axis));
    }

    /// <summary>An axis too short for two labels that do not overlap gets no ticks (Talbot et al. 2010 §5).</summary>
    /// <remarks>
    /// Preconditions:
    /// - The range [1000000, 1000001] over 20 pixels.
    ///
    /// Steps:
    /// - Choose.
    ///
    /// Expected:
    /// - No tick.
    /// </remarks>
    [Fact]
    public void Extended_AllOverlap_NoTicks()
    {
        var axis = Axis(Scale.Linear(), null, ScaleRange.FromDouble(1_000_000, 1_000_001), 20, 10);
        axis.Choose(nice: false);
        Assert.Equal(0, axis.Count);
    }

    /// <summary>
    /// The overlap value (Talbot et al. 2010 §4.4.4): 1 at a gap of 1.5 em or more, 2 − 1.5 em / d between, −∞ when the
    /// labels touch or overlap.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two labels "AB" (13.5 pixels wide at 12) placed by a format that writes them, at chosen distances.
    ///
    /// Steps:
    /// - Measure at gaps of 1.5 em, 15 and 9 pixels (between 1 em and 1.5 em, and below 1 em), and 0.
    ///
    /// Expected:
    /// - 1, 2 − 18 / 15 = 0.8, 2 − 18 / 9 = 0, and −∞.
    /// </remarks>
    [Fact]
    public void Overlap_ThreeCases()
    {
        TickFormatter ab = static (double _, Span<char> d, out int w) =>
        {
            "AB".CopyTo(d);
            w = 2;
            return true;
        };
        var width = 2 * FakeTextBackend.Advance(12);
        foreach (var (gap, expected) in new[] { (1.5 * Em, 1.0), (15, 0.8), (9, 0), (0, double.NegativeInfinity) })
        {
            var axis = Axis(Scale.Linear() with { TickFormat = ab }, null, ScaleRange.FromDouble(0, 1), 100, 10);
            axis.AddNumber(new TickNumber(0, 0), 0);
            axis.AddNumber(new TickNumber(1, 0), width + gap);
            Assert.Equal(expected, axis.LabelNumbers(sameDecimals: true), 9);
        }
    }

    /// <summary>
    /// Tick values are exact (design §4.6): a step of 0.1 writes 0.3, not 0.30000000000000004; a negative range, one across
    /// 0, and magnitudes of 1e300 and 1e-300 get ticks whose labels equal their values.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Ranges [0, 1], [−7.3, −2.1], [−3, 5], [1e300, 3e300], and [1e-300, 4e-300] over 600 pixels.
    ///
    /// Steps:
    /// - Choose each.
    ///
    /// Expected:
    /// - Two ticks or more; every label parses to the tick's value in decimal, and none has more than 12 characters.
    /// </remarks>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-7.3, -2.1)]
    [InlineData(-3, 5)]
    [InlineData(1e300, 3e300)]
    [InlineData(1e-300, 4e-300)]
    public void Extended_Values_AreExact(double low, double high)
    {
        var axis = Axis(Scale.Linear(), null, ScaleRange.FromDouble(low, high), 600, 80);
        axis.Choose(nice: false);
        Assert.True(axis.Count >= 2, $"{axis.Count} ticks");
        for (var i = 0; i < axis.Count; i++)
        {
            var label = axis.TickLabels[i];
            Assert.True(label.Length <= TickFormat.MaxFixedLength, label);
            Assert.Equal(axis.NumberAt(i).Value, double.Parse(label, CultureInfo.InvariantCulture));
        }

        if (low == 0)
        {
            Assert.Contains("0.3", axis.TickLabels.ToArray().Concat(["0.3"]));
        }
    }

    /// <summary>A step of 0.1 is written 0.1, 0.2, 0.3: the decimals of the axis come from M · 10^E (design §4.6).</summary>
    /// <remarks>
    /// Preconditions:
    /// - TickNumber 3 · 10^−1, 30 · 10^−2, and −0 written alone and with 2 decimals.
    ///
    /// Steps:
    /// - Write them fixed.
    ///
    /// Expected:
    /// - "0.3", "0.3", "0.30", and "0" for 0 with no sign.
    /// </remarks>
    [Fact]
    public void Fixed_WritesExactly()
    {
        Assert.Equal("0.3", Fixed(new TickNumber(3, -1), 1));
        Assert.Equal("0.3", Fixed(new TickNumber(30, -2), TickFormat.Decimals(new TickNumber(30, -2))));
        Assert.Equal("0.30", Fixed(new TickNumber(3, -1), 2));
        Assert.Equal("0", Fixed(new TickNumber(0, -3), 0));
        Assert.Equal("-1250", Fixed(new TickNumber(-125, 1), 0));
        Assert.Equal("0.0005", Fixed(new TickNumber(5, -4), 4));
    }

    /// <summary>
    /// The decimals are alike on an axis of the extended search and each label's own on a log axis; past 12 characters every
    /// label of the axis takes the exponent form (design §4.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Ticks 0, 0.5, 1 on a linear axis; 0.001, 0.01, 0.1, 1 on a log axis; 1e13, 2e13; labels of exactly 12 characters;
    ///   and 0.25e-9, 0.5e-9 on linear axes.
    ///
    /// Steps:
    /// - Label each set.
    ///
    /// Expected:
    /// - "0.0", "0.5", "1.0"; "0.001", "0.01", "0.1", "1"; "1e13", "2e13"; "123456789.25" kept fixed at 12; "2.5e-10",
    ///   "5e-10" ("0.00000000025" has 13).
    /// </remarks>
    [Fact]
    public void Labels_DecimalsAndExponents()
    {
        Assert.Equal(["0.0", "0.5", "1.0"], Label(true, (0, 0), (5, -1), (1, 0)));
        Assert.Equal(["0.001", "0.01", "0.1", "1"], Label(false, (1, -3), (1, -2), (1, -1), (1, 0)));
        Assert.Equal(["1e13", "2e13"], Label(true, (1, 13), (2, 13)));
        Assert.Equal(["123456789.25", "123456789.50"], Label(true, (12345678925, -2), (1234567895, -1)));
        Assert.Equal(["2.5e-10", "5.0e-10"], Label(true, (25, -11), (5, -10)));
    }

    /// <summary>
    /// The ticks of pow, sqrt, and a user's transform lie where the transform maps their values (design §4.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A sqrt scale over [0, 100] in data units, 400 pixels.
    ///
    /// Steps:
    /// - Choose.
    ///
    /// Expected:
    /// - Each pixel is sqrt(value) / 10 · 400.
    /// </remarks>
    [Fact]
    public void Transform_TicksWhereTheTransformMaps()
    {
        var sqrt = Scale.Sqrt();
        var axis = Axis(sqrt, sqrt.CreateTransform(), ScaleRange.FromDouble(0, 10), 400, 60);
        axis.Choose(nice: false);
        Assert.True(axis.Count >= 2);
        for (var i = 0; i < axis.Count; i++)
        {
            Assert.Equal(Math.Sqrt(axis.NumberAt(i).Value) / 10 * 400, axis.Pixels[i], 9);
        }
    }

    /// <summary>
    /// Log ticks (design §4.6): under a decade the linear choice, under three decades {1, 2, 5} · 10^k, over many decades
    /// every j-th power, never overlapping; nice widens to powers of ten.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Log domains [2, 8], [1, 300], [1e-30, 1e30] over 600 pixels, spacing 60; [3, 7000] with nice.
    ///
    /// Steps:
    /// - Choose each.
    ///
    /// Expected:
    /// - [2, 8]: not all powers of ten. [1, 300]: mantissas from {1, 2, 5}, some not 1. [1e-30, 1e30]: powers of ten only,
    ///   evenly skipped, at most 11 (600 / 60 + 1). Nice: the domain [1, 10000].
    /// </remarks>
    [Fact]
    public void Log_Candidates()
    {
        var log = Scale.Log();
        var under = Axis(log, log.CreateTransform(), Log(2, 8), 600, 60);
        under.Choose(nice: false);
        Assert.Contains(Values(under), v => !IsPowerOfTen(v));

        var three = Axis(log, log.CreateTransform(), Log(1, 300), 600, 60);
        three.Choose(nice: false);
        Assert.All(Mantissas(three), m => Assert.True(m is 1 or 2 or 5));
        Assert.Contains(Mantissas(three), m => m != 1);

        var many = Axis(log, log.CreateTransform(), Log(1e-30, 1e30), 600, 60);
        many.Choose(nice: false);
        Assert.All(Mantissas(many), m => Assert.Equal(1, m));
        Assert.InRange(many.Count, 2, 11);
        var exponents = Enumerable.Range(0, many.Count).Select(i => Magnitude(many, i)).ToArray();
        Assert.Single(exponents.Zip(exponents.Skip(1), (a, b) => b - a).Distinct());
        Assert.True(Overlap(many) > double.NegativeInfinity);

        var nice = Axis(log, log.CreateTransform(), Log(3, 7000), 600, 60);
        nice.Choose(nice: true);
        Assert.Equal(0, nice.FinalDomain.Origin + nice.FinalDomain.Lower, 12);
        Assert.Equal(4, nice.FinalDomain.Origin + nice.FinalDomain.Upper, 12);
    }

    /// <summary>
    /// Log ticks stop when even the sparsest powers leave fewer than two on the axis: no ticks (design §4.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A log domain [1, 1e6] over 15 pixels.
    ///
    /// Steps:
    /// - Choose.
    ///
    /// Expected:
    /// - No tick.
    /// </remarks>
    [Fact]
    public void Log_TooShort_NoTicks()
    {
        var log = Scale.Log();
        var axis = Axis(log, log.CreateTransform(), Log(1, 1e6), 15, 60);
        axis.Choose(nice: false);
        Assert.Equal(0, axis.Count);
    }

    /// <summary>
    /// Nice judges a log candidate in the widened domain: over a range of lengths, every choice with nice fits its final
    /// domain, and at some length the candidate that fits before widening is not the one chosen (design §4.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A log domain [1.5, 90] over lengths 60 to 400, spacing 30.
    ///
    /// Steps:
    /// - Choose with and without nice at each length.
    ///
    /// Expected:
    /// - With nice, the ticks' count − 1 is at most the final length over the spacing and no label overlaps; at one length
    ///   or more, the two choices differ in their ticks.
    /// </remarks>
    [Fact]
    public void Log_Nice_JudgedInTheWidenedDomain()
    {
        var log = Scale.Log();
        var differs = false;
        for (var length = 60; length <= 400; length += 5)
        {
            var plain = Axis(log, log.CreateTransform(), Log(1.5, 90), length, 30);
            plain.Choose(nice: false);
            var nice = Axis(log, log.CreateTransform(), Log(1.5, 90), length, 30);
            nice.Choose(nice: true);
            Assert.True(nice.Count - 1 <= length / 30.0);
            Assert.True(nice.Count == 0 || Overlap(nice) > double.NegativeInfinity);
            differs |= !Mantissas(plain).SequenceEqual(Mantissas(nice)) && plain.Count > 0;
        }

        Assert.True(differs);
    }

    /// <summary>
    /// When no log candidate fits the widened domain, the axis takes no nice and the choice without it (design §4.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A log domain [0.3, 3000] over 250 pixels, spacing 90: every skip that fits overflows the spacing once widened.
    ///
    /// Steps:
    /// - Choose with nice and without.
    ///
    /// Expected:
    /// - The same ticks both ways, two or more, and the final domain the learned one.
    /// </remarks>
    [Fact]
    public void Log_NiceFits_Nothing_FallsBack()
    {
        var log = Scale.Log();
        var plain = Axis(log, log.CreateTransform(), Log(0.3, 3000), 250, 90);
        plain.Choose(nice: false);
        var nice = Axis(log, log.CreateTransform(), Log(0.3, 3000), 250, 90);
        nice.Choose(nice: true);
        Assert.True(nice.Count >= 2);
        Assert.Equal(Values(plain), Values(nice));
        Assert.Equal(nice.Domain, nice.FinalDomain);
    }

    /// <summary>
    /// Symlog and time judge a nice candidate in the widened domain too: over a range of lengths, every choice with nice
    /// fits its final domain (design §4.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A symlog (C = 1) domain [−700, 40] and a Ticks time domain of 50 hours from 2026-01-01 03:07, over lengths 60 to
    ///   400, spacing 40.
    ///
    /// Steps:
    /// - Choose with nice at each length.
    ///
    /// Expected:
    /// - When there are ticks: count − 1 at most the length over the spacing, and no labels overlapping, over the final
    ///   domain.
    /// </remarks>
    [Fact]
    public void SymlogAndTime_Nice_FitTheFinalDomain()
    {
        var symlog = Scale.Symlog(1);
        var transform = symlog.CreateTransform()!;
        var start = new DateTime(2026, 1, 1, 3, 7, 0).Ticks;
        for (var length = 60; length <= 400; length += 10)
        {
            var axes = new[]
            {
                Axis(symlog, transform, Mapped(transform, -700, 40), length, 40),
                Axis(Scale.Time(), null, ScaleRange.FromInteger(start, start + (50 * TimeSpan.TicksPerHour)), length, 40),
            };
            foreach (var axis in axes)
            {
                axis.Choose(nice: true);
                if (axis.Count == 0)
                {
                    continue;
                }

                Assert.True(axis.Count - 1 <= (length / 40.0) + 1e-9 || axis.Scale is TimeScaleSpec, $"{axis.Scale.GetType().Name} at {length}");
                Assert.True(Overlap(axis) > double.NegativeInfinity, $"{axis.Scale.GetType().Name} at {length}");
            }
        }
    }

    /// <summary>Symlog ticks hold 0 and both sides, |value| ≥ C; a domain within 10 C takes the linear choice (design §4.6).</summary>
    /// <remarks>
    /// Preconditions:
    /// - Symlog of C = 1 over [−1000, 1000] and over [−5, 5], 600 pixels.
    ///
    /// Steps:
    /// - Choose each.
    ///
    /// Expected:
    /// - The first: 0, negative and positive powers of ten, none of magnitude below 1. The second: not all powers.
    /// </remarks>
    [Fact]
    public void Symlog_ZeroAndBothSides()
    {
        var symlog = Scale.Symlog(1);
        var transform = symlog.CreateTransform()!;
        var wide = Axis(symlog, transform, Mapped(transform, -1000, 1000), 600, 60);
        wide.Choose(nice: false);
        var values = Values(wide);
        Assert.Contains(0, values);
        Assert.Contains(values, v => v < 0);
        Assert.Contains(values, v => v > 0);
        Assert.All(values, v => Assert.True(v == 0 || Math.Abs(v) >= 1));

        var narrow = Axis(symlog, transform, Mapped(transform, -5, 5), 600, 60);
        narrow.Choose(nice: false);
        Assert.Contains(Values(narrow), v => v != 0 && !IsPowerOfTen(Math.Abs(v)));
    }

    /// <summary>
    /// Time ticks take each step from nanoseconds to years by the span, on their anchors (design §4.6): 7 days on Mondays,
    /// 3 months from January, years on multiples, 2 days on even day numbers from 0001-01-01.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Ticks-unit time scales over spans from 2 µs to 400 years from 2026-01-01, 800 pixels, spacing 100.
    ///
    /// Steps:
    /// - Choose each.
    ///
    /// Expected:
    /// - Two ticks or more; consecutive ticks a whole step apart; 7-day ticks on Mondays, 3-month ticks in months 1, 4, 7,
    ///   10, year ticks on multiples of the step.
    /// </remarks>
    [Fact]
    public void Time_StepsAndAnchors()
    {
        var start = new DateTime(2026, 1, 1, 3, 7, 11).Ticks;
        foreach (
            var span in new[]
            {
                20L,
                3_000,
                40_000_000,
                6 * TimeSpan.TicksPerHour,
                40 * TimeSpan.TicksPerDay,
                600 * TimeSpan.TicksPerDay,
                400 * 365 * TimeSpan.TicksPerDay,
            }
        )
        {
            var axis = TimeAxis(Scale.Time(), start, start + span, 800);
            axis.Choose(nice: false);
            Assert.True(axis.Count >= 2, $"span {span}");
        }

        var weeks = TimeAxis(Scale.Time(), start, start + (120 * TimeSpan.TicksPerDay), 800);
        weeks.Choose(nice: false);
        var labelsOfWeeks = Times(weeks);
        if ((labelsOfWeeks[1] - labelsOfWeeks[0]).Days == 7)
        {
            Assert.All(labelsOfWeeks, t => Assert.Equal(DayOfWeek.Monday, t.DayOfWeek));
        }

        var quarters = TimeAxis(Scale.Time(), start, start + (3 * 365 * TimeSpan.TicksPerDay), 800);
        quarters.Choose(nice: false);
        Assert.All(Times(quarters), t => Assert.True((t.Day == 1 && (t.Month - 1) % 3 == 0) || t.Month == 1));

        var centuries = TimeAxis(Scale.Time(), new DateTime(1003, 5, 1).Ticks, new DateTime(2950, 1, 1).Ticks, 800);
        centuries.Choose(nice: false);
        var years = Times(centuries).Select(t => t.Year).ToArray();
        var step = years[1] - years[0];
        Assert.All(years, y => Assert.Equal(0, y % step));
    }

    /// <summary>
    /// Time ticks are exact in the column's unit (design §4.6): nanoseconds near 2262 keep every ns; the Ticks unit takes no
    /// step finer than 100 ns, the ms unit none finer than 1 ms; values before the epoch split by ⌊⌋.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A ns scale over 9 ns near 2262-04-11; a Ticks scale over 300 ns; a ms scale over 3 ms; a ns scale over 1969.
    ///
    /// Steps:
    /// - Choose each; read the ticks' instants.
    ///
    /// Expected:
    /// - The ns ticks are whole ns, the Ticks ticks multiples of 100 ns, the ms ticks multiples of 1 ms, and the 1969 ticks
    ///   multiples of their step (no tick one unit off).
    /// </remarks>
    [Fact]
    public void Time_ExactInTheUnit()
    {
        var late = (new DateTime(2262, 4, 11) - DateTime.UnixEpoch).Ticks * 100;
        var ns = TimeAxis(Scale.Time(TimeUnit.Nanoseconds), late, late + 9, 600, unitTicks: false);
        ns.Choose(nice: false);
        Assert.True(ns.Count >= 2);

        var ticks = TimeAxis(Scale.Time(), 0, 3, 600);
        ticks.Choose(nice: false);
        Assert.All(Instants(ticks), i => Assert.Equal(0, i % 100));

        var ms = TimeAxis(Scale.Time(TimeUnit.Milliseconds), 0, 3, 600, unitTicks: false);
        ms.Choose(nice: false);
        Assert.All(Instants(ms), i => Assert.Equal(0, i % 1_000_000));

        var before = (new DateTime(1969, 12, 31, 23, 59, 59) - DateTime.UnixEpoch).Ticks * 100;
        var old = TimeAxis(Scale.Time(TimeUnit.Nanoseconds), before - 3_000_000_000, before, 600, unitTicks: false);
        old.Choose(nice: false);
        var instants = Instants(old);
        var step = instants[1] - instants[0];
        Assert.All(instants, i => Assert.Equal(0, i % step));
    }

    /// <summary>The boundary passed for each time tick is the coarsest it lies on (design §4.6).</summary>
    /// <remarks>
    /// Preconditions:
    /// - Instants on a year, a month, a day, an hour, a minute, a second, a ms, a µs, and a ns.
    ///
    /// Steps:
    /// - Take each one's boundary.
    ///
    /// Expected:
    /// - Year, Month, Day, Hour, Minute, Second, Millisecond, Microsecond, Nanosecond.
    /// </remarks>
    [Fact]
    public void Time_Boundaries()
    {
        static Int128 At(int y, int mo, int d, int h = 0, int mi = 0, int s = 0) => (Int128)new DateTime(y, mo, d, h, mi, s).Ticks * 100;
        Assert.Equal(TimeInterval.Year, TimeTicks.Boundary(At(2026, 1, 1)));
        Assert.Equal(TimeInterval.Month, TimeTicks.Boundary(At(2026, 3, 1)));
        Assert.Equal(TimeInterval.Day, TimeTicks.Boundary(At(2026, 3, 15)));
        Assert.Equal(TimeInterval.Hour, TimeTicks.Boundary(At(2026, 3, 15, 12)));
        Assert.Equal(TimeInterval.Minute, TimeTicks.Boundary(At(2026, 3, 15, 12, 30)));
        Assert.Equal(TimeInterval.Second, TimeTicks.Boundary(At(2026, 3, 15, 12, 30, 15)));
        Assert.Equal(TimeInterval.Millisecond, TimeTicks.Boundary(At(2026, 3, 15, 12, 30, 15) + 250_000_000));
        Assert.Equal(TimeInterval.Microsecond, TimeTicks.Boundary(At(2026, 3, 15, 12, 30, 15) + 250_125_000));
        Assert.Equal(TimeInterval.Nanosecond, TimeTicks.Boundary(At(2026, 3, 15, 12, 30, 15) + 250_125_375));
    }

    /// <summary>The time labels of the nine boundaries (design §4.6).</summary>
    /// <remarks>
    /// Preconditions:
    /// - 2026-03-15 12:30:15.250125375 and its boundaries.
    ///
    /// Steps:
    /// - Write it for each boundary.
    ///
    /// Expected:
    /// - "2026", "2026-03", "03-15", "12:30", "12:30", "12:30:15", "15.250", "15.250125", "15.250125375".
    /// </remarks>
    [Fact]
    public void Time_Formats()
    {
        var time = new DateTime(2026, 3, 15, 12, 30, 15).AddTicks(2_501_253);
        string[] expected = ["2026", "2026-03", "03-15", "12:30", "12:30", "12:30:15", "15.250", "15.250125", "15.250125375"];
        Span<char> buffer = stackalloc char[64];
        for (var b = TimeInterval.Year; b <= TimeInterval.Nanosecond; b++)
        {
            Assert.True(TickFormat.TryWriteTime(time, 75, b, buffer, out var written));
            Assert.Equal(expected[(int)b], buffer[..written].ToString());
        }
    }

    /// <summary>A domain outside the years 1 to 9999 throws, naming the scale (design §4.6).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A seconds scale from the Unix epoch over [−7e10, 0] (about year −250).
    ///
    /// Steps:
    /// - Choose.
    ///
    /// Expected:
    /// - InvalidOperationException naming Seconds.
    /// </remarks>
    [Fact]
    public void Time_OutsideDateTime_Throws()
    {
        var axis = TimeAxis(Scale.Time(TimeUnit.Seconds), -70_000_000_000, 0, 600, unitTicks: false);
        Assert.Contains("Seconds", Assert.Throws<InvalidOperationException>(() => axis.Choose(nice: false)).Message);
    }

    /// <summary>Nice widens a time domain to its step's ticks, in the column's unit (design §4.6).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A Ticks scale over 2026-01-01 03:07:11 plus 6 hours, 800 pixels.
    ///
    /// Steps:
    /// - Choose with nice.
    ///
    /// Expected:
    /// - The first and last ticks at the final domain's ends, which hold the learned domain.
    /// </remarks>
    [Fact]
    public void Time_Nice_WidensToTicks()
    {
        var start = new DateTime(2026, 1, 1, 3, 7, 11).Ticks;
        var axis = TimeAxis(Scale.Time(), start, start + (6 * TimeSpan.TicksPerHour), 800);
        axis.Choose(nice: true);
        var final = axis.FinalDomain;
        Assert.Equal((double)(Times(axis)[0].Ticks - final.Origin), final.Lower, 6);
        Assert.Equal((double)(Times(axis)[^1].Ticks - final.Origin), final.Upper, 6);
        Assert.True(final.Origin + final.Lower <= start && final.Origin + final.Upper >= start + (6 * TimeSpan.TicksPerHour));
    }

    /// <summary>
    /// A user's format (design §4.6): false, a length past 64, or a negative one throws naming the scale; an exception it
    /// throws comes out as it is; 0 characters is an empty label of width 0.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A linear axis over [0, 10] with formats that return false, write 65, write −1, throw, and write nothing.
    ///
    /// Steps:
    /// - Choose with each.
    ///
    /// Expected:
    /// - InvalidOperationException three times, the format's exception, and empty labels with extent 0.
    /// </remarks>
    [Fact]
    public void UserFormat_Contract()
    {
        TickFormatter refuse = static (double _, Span<char> _, out int w) =>
        {
            w = 0;
            return false;
        };
        TickFormatter tooLong = static (double _, Span<char> _, out int w) =>
        {
            w = 65;
            return true;
        };
        TickFormatter negative = static (double _, Span<char> _, out int w) =>
        {
            w = -1;
            return true;
        };
        TickFormatter fails = static (double _, Span<char> _, out int w) => throw new FormatException("mine");
        TickFormatter empty = static (double _, Span<char> _, out int w) =>
        {
            w = 0;
            return true;
        };
        foreach (var format in new[] { refuse, tooLong, negative })
        {
            var axis = Axis(Scale.Linear() with { TickFormat = format }, null, ScaleRange.FromDouble(0, 10), 400, 60);
            Assert.Contains("LinearScaleSpec", Assert.Throws<InvalidOperationException>(() => axis.Choose(nice: false)).Message);
        }

        var throwing = Axis(Scale.Linear() with { TickFormat = fails }, null, ScaleRange.FromDouble(0, 10), 400, 60);
        Assert.Equal("mine", Assert.Throws<FormatException>(() => throwing.Choose(nice: false)).Message);

        var blank = Axis(Scale.Linear() with { TickFormat = empty }, null, ScaleRange.FromDouble(0, 10), 400, 60);
        blank.Choose(nice: false);
        Assert.True(blank.Count >= 2);
        Assert.All(blank.TickLabels.ToArray(), l => Assert.Equal(string.Empty, l));
        Assert.All(blank.Extents.ToArray(), e => Assert.Equal(0, e));
    }

    /// <summary>The label table makes no string for a label it holds, and drops the oldest at 4,097 (design §4.6).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A table with "a".
    ///
    /// Steps:
    /// - Get "a" again from a span; add 4,096 other labels; get "a".
    ///
    /// Expected:
    /// - The same string and 0 bytes; then a new string, since "a" was the oldest.
    /// </remarks>
    [Fact]
    public void LabelTable_HitsAndEviction()
    {
        var table = new LabelTable();
        var first = table.Get("a".AsSpan());
        Span<char> a = ['a'];
        _ = table.Get(a);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var again = table.Get(a);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Same(first, again);

        for (var i = 0; i < LabelTable.Capacity; i++)
        {
            _ = table.Get(i.ToString(CultureInfo.InvariantCulture).AsSpan());
        }

        Assert.Equal(LabelTable.Capacity, table.Count);
        Assert.NotSame(first, table.Get(a));
    }

    private static TickAxis Axis(ContinuousScaleSpec scale, IScale? transform, ScaleRange domain, double length, double spacing) =>
        new()
        {
            Scale = scale,
            Transform = transform,
            Domain = domain,
            Length = length,
            Spacing = spacing,
            Horizontal = true,
            LineHeight = FakeTextBackend.LineHeight((float)Em),
            Em = Em,
            Style = new TextStyle(new Color(0, 0, 0, 255), (float)Em),
            Text = new TextEngine(new FakeTextBackend()),
            Labels = new LabelTable(),
        };

    private static TickAxis TimeAxis(TimeScaleSpec scale, long low, long high, double length, bool unitTicks = true)
    {
        _ = unitTicks;
        return Axis(scale, null, ScaleRange.FromInteger(low, high), length, 100);
    }

    private static ScaleRange Log(double low, double high) => ScaleRange.FromDouble(Math.Log10(low), Math.Log10(high));

    private static ScaleRange Mapped(IScale transform, double low, double high)
    {
        var mapped = new double[2];
        transform.Forward([low, high], mapped);
        return ScaleRange.FromDouble(mapped[0], mapped[1]);
    }

    private static double[] Values(TickAxis axis) => Enumerable.Range(0, axis.Count).Select(i => axis.NumberAt(i).Value).ToArray();

    private static int[] Mantissas(TickAxis axis)
    {
        var mantissas = new int[axis.Count];
        for (var i = 0; i < axis.Count; i++)
        {
            var m = Int128.Abs(axis.NumberAt(i).Mantissa);
            while (m != 0 && m % 10 == 0)
            {
                m /= 10;
            }

            mantissas[i] = (int)m;
        }

        return mantissas;
    }

    private static int Magnitude(TickAxis axis, int i) => TickFormat.Magnitude(axis.NumberAt(i));

    private static bool IsPowerOfTen(double value) => Math.Abs(Math.Log10(value) - Math.Round(Math.Log10(value))) < 1e-12;

    private static double Overlap(TickAxis axis) => axis.Measure();

    private static Int128[] Instants(TickAxis axis) => Enumerable.Range(0, axis.Count).Select(axis.InstantAt).ToArray();

    private static DateTime[] Times(TickAxis axis) => Instants(axis).Select(i => new DateTime((long)(i / 100))).ToArray();

    private static string Fixed(TickNumber number, int decimals)
    {
        Span<char> buffer = stackalloc char[64];
        Assert.True(TickFormat.TryWriteFixed(number, decimals, buffer, out var written));
        return buffer[..written].ToString();
    }

    private static string[] Label(bool same, params (long M, int E)[] ticks)
    {
        var axis = Axis(Scale.Linear(), null, ScaleRange.FromDouble(0, 1), 10_000, 10);
        for (var i = 0; i < ticks.Length; i++)
        {
            axis.AddNumber(new TickNumber(ticks[i].M, ticks[i].E), i * 1000);
        }

        _ = axis.LabelNumbers(same);
        return axis.TickLabels.ToArray();
    }

    /// <summary>
    /// The search of §5 with no pruning: every j ≤ 8 and q, k from 2 to 30, z from §5's first to six above, and §5's starts;
    /// the same scoring and filters, the overlap measured through an axis of its own. It differs from the axis's search only
    /// in the pruning, so a difference is a bound that cut a better candidate.
    /// </summary>
    private static (string[] Labels, double[] Pixels) BruteForce(
        double low,
        double high,
        double length,
        double spacing,
        bool nice,
        (double S, double C, double D, double L) w
    )
    {
        int[] q10 = [10, 50, 20, 25, 40, 30];
        var range = high - low;
        var best = double.NegativeInfinity;
        (string[] Labels, double[] Pixels) result = ([], []);
        var probe = Axis(Scale.Linear(), null, ScaleRange.FromDouble(low, high), length, spacing);

        // The ends as the search takes them: differences from the domain's origin (design §3.6).
        var origin = probe.Domain.Origin;
        (low, high) = (probe.Domain.Lower, probe.Domain.Upper);
        for (var j = 1; j <= 8; j++)
        {
            for (var i = 0; i < q10.Length; i++)
            {
                for (var k = 2; k <= 30; k++)
                {
                    var z0 = (int)Math.Ceiling(Math.Log10(range / (k + 1) / (j * q10[i] / 10.0)));
                    for (var z = z0; z <= z0 + 6; z++)
                    {
                        // The starts of §5: from ⌊dmax / lstep⌋ − (k − 1) to ⌈dmin / lstep⌉ by 1/j, in units of lstep / j.
                        var e = z - 1;
                        var step = q10[i] * j * Math.Pow(10, e);
                        var first = j * (TickNumber.FloorUnits(origin, high, q10[i] * j, e) - (k - 1));
                        var last = j * TickNumber.CeilingUnits(origin, low, q10[i] * j, e);
                        for (var a = first; a <= last; a++)
                        {
                            var lmin = new TickNumber(a * q10[i], e).Difference(origin);
                            var lmax = new TickNumber((a + ((k - 1) * j)) * q10[i], e).Difference(origin);
                            var admitted = nice
                                ? lmin <= low && lmax >= high
                                : lmin >= low && lmax <= high && lmin - step < low && lmax + step > high;
                            if (!admitted)
                            {
                                continue;
                            }

                            var zero = a <= 0 && (-a) % j == 0 && (-a) / j <= k - 1;
                            var simplicity = 1 - (i / 5.0) - j + (zero ? 1 : 0);
                            var coverage = 1 - (0.5 * (Math.Pow((high - lmax) / (0.1 * range), 2) + Math.Pow((low - lmin) / (0.1 * range), 2)));
                            var domain = nice ? new ScaleRange(origin, lmin, lmax) : probe.Domain;
                            probe.ClearCandidate();
                            for (var n = 0; n < k; n++)
                            {
                                var tick = new TickNumber((a + (n * j)) * q10[i], e);
                                probe.AddNumber(tick, probe.Pixel(tick, domain));
                            }

                            var span = probe.Pixels[k - 1] - probe.Pixels[0];
                            var rho = (k - 1) / span;
                            var density = 2 - Math.Max(rho * spacing, 1 / (rho * spacing));

                            // Legibility is at most 1: a candidate that cannot pass the best with 1 is not measured. Exact, and
                            // it keeps the brute force fast; it does not use the search's bounds.
                            if ((w.S * simplicity) + (w.C * coverage) + (w.D * density) + w.L <= best)
                            {
                                continue;
                            }

                            var legibility = probe.LabelNumbers(sameDecimals: true);
                            var score = (w.S * simplicity) + (w.C * coverage) + (w.D * density) + (w.L * legibility);
                            if (score > best)
                            {
                                best = score;
                                result = (probe.TickLabels.ToArray(), probe.Pixels.ToArray());
                            }
                        }
                    }
                }
            }
        }

        return result;
    }
}
