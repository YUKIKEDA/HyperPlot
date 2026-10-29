using System.Numerics;

namespace HyperPlot.Tests;

/// <summary>An exact fraction, to compare positions of the scale space without rounding (design §3.6). Tests only.</summary>
/// <param name="Numerator">The numerator.</param>
/// <param name="Denominator">The denominator, greater than 0.</param>
internal readonly record struct Rational(BigInteger Numerator, BigInteger Denominator)
{
    /// <summary>Gets a value indicating whether the fraction is 0.</summary>
    public bool IsZero => Numerator.IsZero;

    public static Rational operator -(Rational left, Rational right) =>
        Make((left.Numerator * right.Denominator) - (right.Numerator * left.Denominator), left.Denominator * right.Denominator);

    public static Rational operator +(Rational left, Rational right) =>
        Make((left.Numerator * right.Denominator) + (right.Numerator * left.Denominator), left.Denominator * right.Denominator);

    public static bool operator <=(Rational left, Rational right) => left.Numerator * right.Denominator <= right.Numerator * left.Denominator;

    public static bool operator >=(Rational left, Rational right) => right <= left;

    /// <summary>Returns the exact value of an integer.</summary>
    public static Rational Of(BigInteger value) => new(value, BigInteger.One);

    /// <summary>Returns the exact value of a long.</summary>
    public static Rational Of(long value) => Of((BigInteger)value);

    /// <summary>Returns the exact value of a finite double: its significand times a power of 2.</summary>
    public static Rational Of(double value)
    {
        var bits = BitConverter.DoubleToInt64Bits(value);
        var exponent = (int)((bits >> 52) & 0x7FF);
        var significand = bits & 0xF_FFFF_FFFF_FFFFL;
        if (exponent == 0)
        {
            exponent = 1;
        }
        else
        {
            significand |= 1L << 52;
        }

        exponent -= 1075;
        var numerator = new BigInteger(bits < 0 ? -significand : significand);
        return exponent >= 0 ? Of(numerator << exponent) : Make(numerator, BigInteger.One << -exponent);
    }

    /// <summary>Returns the exact position origin + difference.</summary>
    public static Rational Position(long origin, double difference) => Of(origin) + Of(difference);

    /// <summary>Returns 2^<paramref name="exponent"/>.</summary>
    public static Rational PowerOfTwo(int exponent) =>
        exponent >= 0 ? Of(BigInteger.One << exponent) : new(BigInteger.One, BigInteger.One << -exponent);

    /// <summary>Returns half the distance from a positive double to the next one up.</summary>
    public static Rational HalfUlp(double value) =>
        Make(
            (Of(Math.BitIncrement(Math.Abs(value))) - Of(Math.Abs(value))).Numerator,
            (Of(Math.BitIncrement(Math.Abs(value))) - Of(Math.Abs(value))).Denominator * 2
        );

    /// <summary>Returns the absolute value.</summary>
    public static Rational Abs(Rational value) => new(BigInteger.Abs(value.Numerator), value.Denominator);

    private static Rational Make(BigInteger numerator, BigInteger denominator)
    {
        var divisor = BigInteger.GreatestCommonDivisor(numerator, denominator);
        return divisor.IsZero ? new(BigInteger.Zero, BigInteger.One) : new(numerator / divisor, denominator / divisor);
    }
}
