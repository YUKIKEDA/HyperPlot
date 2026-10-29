using System.Numerics;

namespace HyperPlot.Tests;

/// <summary>The origin and differences of the scale space (design §3.6, row D2): the difference d and the re-anchoring.</summary>
public sealed class ScaleSpaceTests
{
    private const long Ticks2026 = 639_288_288_000_000_000;

    /// <summary>
    /// An integer column's difference is the exact difference rounded once to the nearest double, for any two longs, and it
    /// does not decrease as the value grows (design §3.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Origins: long.MinValue, -1, 0, 2026's .NET ticks, 1970-based nanoseconds of 2026, long.MaxValue. Values: those, their
    ///   neighbors, and 2,000 random longs over the whole range.
    ///
    /// Steps:
    /// - Take each value's difference from each origin; compare it with the exact difference as a fraction.
    ///
    /// Expected:
    /// - No exception (long.MaxValue − long.MinValue overflows a long); each difference is at most as far from the exact one as
    ///   either neighboring double; over the sorted values, the differences do not decrease.
    /// </remarks>
    [Fact]
    public void Difference_Integers_IsTheExactDifferenceRoundedOnce()
    {
        long[] origins = [long.MinValue, -1, 0, Ticks2026, 1_793_232_000_000_000_000, long.MaxValue];
        var random = new Random(53);
        var values = origins
            .SelectMany(o => new[] { o, o == long.MinValue ? o : o - 1, o == long.MaxValue ? o : o + 1 })
            .Concat(Enumerable.Range(0, 2000).Select(_ => random.NextInt64(long.MinValue, long.MaxValue)))
            .Order()
            .ToArray();
        foreach (var origin in origins)
        {
            var previous = double.NegativeInfinity;
            foreach (var value in values)
            {
                var difference = ScaleSpace.Difference(value, origin);
                AssertNearest(difference, (BigInteger)value - origin);
                Assert.True(difference >= previous, $"{value} - {origin} decreased.");
                previous = difference;
            }
        }

        Assert.Equal(18446744073709551615d, ScaleSpace.Difference(long.MaxValue, long.MinValue));
    }

    /// <summary>
    /// A column of int or byte takes the exact integer difference; a floating-point column takes (double)v − (double)o, whose
    /// origin above 2^53 is itself rounded by at most half its ulp (design §3.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The origin 2026's .NET ticks + 1, which a double cannot hold.
    ///
    /// Steps:
    /// - Take the differences of 1,000 random int and byte values from random origins in [2^58, 2^62), and of float and
    ///   double values near the origin.
    ///
    /// Expected:
    /// - The int and byte ones equal the long ones, and (double)v − (double)o differs from them for some bytes; the
    ///   floating-point ones equal (double)v − (double)o; (double)o is at most
    ///   64 (half of its ulp, 128) from o, and is not o.
    /// </remarks>
    [Fact]
    public void Difference_OtherTypes_FollowTheirFormulas()
    {
        const long origin = Ticks2026 + 1;
        var random = new Random(59);
        var differs = 0;
        for (var i = 0; i < 1000; i++)
        {
            var far = random.NextInt64(1L << 58, 1L << 62);
            var integer = random.Next(int.MinValue, int.MaxValue);
            var small = (byte)random.Next(256);
            Assert.Equal(ScaleSpace.Difference((long)integer, far), ScaleSpace.Difference<int>(integer, far));
            Assert.Equal(ScaleSpace.Difference((long)small, far), ScaleSpace.Difference<byte>(small, far));
            differs += (double)small - (double)far != ScaleSpace.Difference((long)small, far) ? 1 : 0;
        }

        // The floating-point formula would give other values for some of them, so the integer one is the one in use.
        Assert.True(differs > 0, "No byte value told the two formulas apart.");
        Assert.Equal(6.39288288e17 - (double)origin, ScaleSpace.Difference(6.39288288e17, origin));
        Assert.Equal(6.39e17f - (double)origin, ScaleSpace.Difference(6.39e17f, origin));
        var rounding = BigInteger.Abs(new BigInteger((double)origin) - origin);
        Assert.InRange(rounding, BigInteger.One, new BigInteger(64));
    }

    /// <summary>
    /// Re-anchoring moves the origin to the floor of the lower difference, so that the lower difference lies in [0, 1), and
    /// moves the range by at most the bounds of design §3.6.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Ranges at the origin 2026's .NET ticks with lower differences -0.3, -0.1, -0.49 (in (-0.5, 0)), -0.5, -0.7, -1 (in
    ///   [-1, -0.5]), -1.3, -2.7 (below -1), 0, 0.3 (in [0, 1)), 2.7 and 1e6 + 0.3 (large); upper differences of 1 more.
    ///   A range with lower 3.25 and upper 2^60 + 1024.
    ///
    /// Steps:
    /// - Anchor each; compare origin + difference before and after as exact fractions.
    ///
    /// Expected:
    /// - The lower difference after lies in [0, 1) and the origin moved by the floor. The lower end is bit for bit the same,
    ///   except in (-0.5, 0), where it moves by at most 2^-54 and -0.3 moves (by 2^-54). The upper end moves by at most half
    ///   the ulp of the upper difference after: not at all when the origin moves up (lower ≥ 0) and the upper difference is below
    ///   2^53, and 0.5 + 2^-53 moves when the origin moves down by 1 (1.5 + 2^-53 is not on the grid of [1, 2)). The upper end of 2^60 + 1024 moves by at most half its ulp (128), and
    ///   does move.
    /// </remarks>
    [Fact]
    public void Anchor_LowerDifferences_StayWithinTheBounds()
    {
        double[] exact = [-0.5, -0.7, -1, -1.3, -2.7, 0, 0.3, 2.7, 1e6 + 0.3];
        double[] rounded = [-0.3, -0.1, -0.49];
        foreach (var lower in exact.Concat(rounded))
        {
            var range = new ScaleRange(Ticks2026, lower, lower + 1);
            var anchored = range.Anchor();

            Assert.InRange(anchored.Lower, 0, Math.BitDecrement(1.0));
            Assert.Equal(Ticks2026 + (long)Math.Floor(lower), anchored.Origin);
            var lowerMove = Rational.Abs(Rational.Position(anchored.Origin, anchored.Lower) - Rational.Position(range.Origin, range.Lower));
            var upperMove = Rational.Abs(Rational.Position(anchored.Origin, anchored.Upper) - Rational.Position(range.Origin, range.Upper));
            if (exact.Contains(lower))
            {
                Assert.True(lowerMove.IsZero, $"The lower end of {lower} moved.");
            }
            else
            {
                Assert.True(lowerMove <= Rational.PowerOfTwo(-54), $"The lower end of {lower} moved more than 2^-54.");
            }

            Assert.True(upperMove <= Rational.HalfUlp(anchored.Upper), $"The upper end of {lower} moved more than half its ulp.");
            if (lower >= 0)
            {
                Assert.True(upperMove.IsZero, $"The upper end of {lower} moved, with the origin moved up.");
            }
        }

        // Moving the origin down adds to the upper difference: 0.5 + 2^-53 + 1 needs a finer grid than [1, 2) has, and rounds.
        var down = new ScaleRange(Ticks2026, -0.3, Math.BitIncrement(0.5)).Anchor();
        Assert.False((Rational.Position(down.Origin, down.Upper) - Rational.Position(Ticks2026, Math.BitIncrement(0.5))).IsZero);

        var minusPointThree = new ScaleRange(Ticks2026, -0.3, 1).Anchor();
        Assert.Equal(
            Rational.PowerOfTwo(-54),
            Rational.Abs(Rational.Position(minusPointThree.Origin, minusPointThree.Lower) - Rational.Position(Ticks2026, -0.3))
        );

        var wide = new ScaleRange(0, 3.25, Math.Pow(2, 60) + 1024);
        var moved = wide.Anchor();
        var upper = Rational.Abs(Rational.Position(moved.Origin, moved.Upper) - Rational.Position(wide.Origin, wide.Upper));
        Assert.False(upper.IsZero);
        Assert.True(upper <= Rational.PowerOfTwo(7), "The upper end moved more than half its ulp.");
    }

    /// <summary>
    /// An origin past the range of long is clamped to it (design §3.6), and the ranges of doubles and integers start where the
    /// design says.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A range at long.MinValue + 5 with lower -10.5; the doubles -1e300 and 0, 1e300 and 2e300, 2^64 and 2^65, and 2^63
    ///   and 2^64; the longs 2026's .NET ticks and that + 10^12 + 1.
    ///
    /// Steps:
    /// - Anchor the first; make a range of the doubles and of the longs.
    ///
    /// Expected:
    /// - The first's origin is long.MinValue with lower -5.5 and upper 6; the doubles' origin is long.MinValue with lower
    ///   -1e300 - (double)long.MinValue; the positive ones' origin is long.MaxValue (2^64 is the first shift that skips the
    ///   Int128 sum, and 2^63 still takes it); the longs' origin is the first long, its lower 0, and its upper exactly 10^12 + 1.
    /// </remarks>
    [Fact]
    public void Ranges_PastLongAndOfIntegers_StartWhereTheDesignSays()
    {
        Assert.Equal(new ScaleRange(long.MinValue, -5.5, 6), new ScaleRange(long.MinValue + 5, -10.5, 1).Anchor());
        Assert.Equal(new ScaleRange(long.MinValue, -1e300 - (double)long.MinValue, -(double)long.MinValue), ScaleRange.FromDouble(-1e300, 0));
        Assert.Equal(
            new ScaleRange(long.MaxValue, 1e300 - (double)long.MaxValue, 2e300 - (double)long.MaxValue),
            ScaleRange.FromDouble(1e300, 2e300)
        );
        Assert.Equal(long.MaxValue, ScaleRange.FromDouble(Math.ScaleB(1, 64), Math.ScaleB(1, 65)).Origin);
        Assert.Equal(long.MaxValue, ScaleRange.FromDouble(Math.ScaleB(1, 63), Math.ScaleB(1, 64)).Origin);
        Assert.Equal(new ScaleRange(Ticks2026, 0, 1e12 + 1), ScaleRange.FromInteger(Ticks2026, Ticks2026 + 1_000_000_000_001));
        Assert.Equal(new ScaleRange(100, 0, 1900), ScaleRange.FromDouble(100, 2000));
        Assert.Equal(new ScaleRange(-1, 0.75, 1.5), ScaleRange.FromDouble(-0.25, 0.5));
    }

    /// <summary>Asserts that a double is as near to an exact integer as either neighboring double.</summary>
    private static void AssertNearest(double value, BigInteger exact)
    {
        var distance = Rational.Abs(Rational.Of(value) - Rational.Of(exact));
        Assert.True(distance <= Rational.Abs(Rational.Of(Math.BitIncrement(value)) - Rational.Of(exact)), $"{value} is not the nearest to {exact}.");
        Assert.True(distance <= Rational.Abs(Rational.Of(Math.BitDecrement(value)) - Rational.Of(exact)), $"{value} is not the nearest to {exact}.");
    }
}
