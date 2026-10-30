namespace HyperPlot;

/// <summary>
/// A tick value of a numeric axis kept exactly as M · 10^E (design §4.6): the extended search makes every tick as an integer
/// times a power of ten, so that a step of 0.1 gives 0.3, not 0.30000000000000004, and a label is written from M and E.
/// </summary>
internal readonly record struct TickNumber(Int128 Mantissa, int Exponent)
{
    private const int MaxExact = 18;

    private static readonly double[] Powers =
    [
        1e0,
        1e1,
        1e2,
        1e3,
        1e4,
        1e5,
        1e6,
        1e7,
        1e8,
        1e9,
        1e10,
        1e11,
        1e12,
        1e13,
        1e14,
        1e15,
        1e16,
        1e17,
        1e18,
        1e19,
        1e20,
        1e21,
        1e22,
    ];

    /// <summary>Gets the value as a double: M · 10^E, or M / 10^−E, each rounded once while 10^|E| is exact.</summary>
    public double Value =>
        Exponent >= 0 && Exponent <= 22 ? (double)Mantissa * Pow10(Exponent)
        : Exponent < 0 && -Exponent <= 22 ? (double)Mantissa / Pow10(-Exponent)
        : Parsed();

    /// <summary>
    /// Returns M · 10^E rounded once, by parsing its decimal text: past 10^±22 a power is not exact, and M · 10^E through
    /// <see cref="Math.Pow"/> can be an ulp off the value its label writes.
    /// </summary>
    private double Parsed()
    {
        Span<char> text = stackalloc char[64];
        _ = Mantissa.TryFormat(text, out var length, default, System.Globalization.CultureInfo.InvariantCulture);
        text[length++] = 'e';
        _ = Exponent.TryFormat(text[length..], out var count, default, System.Globalization.CultureInfo.InvariantCulture);
        return double.Parse(text[..(length + count)], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Returns 10^n for n from 0 to 22 exactly, and the nearest double beyond.</summary>
    public static double Pow10(int n) => n <= 22 ? Powers[n] : Math.Pow(10, n);

    /// <summary>Returns 10^n for any n: exact from 10^−22 to 10^22 as a power or its inverse.</summary>
    public static double Pow10Signed(int n) => n >= 0 ? Pow10(n) : 1 / Pow10(-n);

    /// <summary>Returns 10^n as an Int128, for n from 0 to 38.</summary>
    public static Int128 IntegerPow10(int n)
    {
        var value = Int128.One;
        for (var i = 0; i < n; i++)
        {
            value *= 10;
        }

        return value;
    }

    /// <summary>
    /// Returns M · 10^E − origin, rounded once to a double when both fit 18 digits of shift; otherwise as doubles. The
    /// difference from a domain's origin keeps a tick near 2^60 exact (design §3.6).
    /// </summary>
    public double Difference(long origin)
    {
        if (Exponent >= 0 && Exponent <= MaxExact)
        {
            return (double)((Mantissa * IntegerPow10(Exponent)) - origin);
        }

        if (Exponent < 0 && -Exponent <= MaxExact && origin == 0)
        {
            return Value;
        }

        if (Exponent < 0 && -Exponent <= MaxExact)
        {
            var scale = IntegerPow10(-Exponent);
            return (double)(Mantissa - (origin * scale)) / Pow10(-Exponent);
        }

        return Value - origin;
    }

    /// <summary>
    /// Returns ⌊(origin + d) / (u · 10^e)⌋ for an integer u, exactly in the origin and in double for d: the start of the
    /// extended search, in units of a step (design §4.6).
    /// </summary>
    public static Int128 FloorUnits(long origin, double d, Int128 u, int e) => Units(origin, d, u, e, ceiling: false);

    /// <summary>Returns ⌈(origin + d) / (u · 10^e)⌉, as <see cref="FloorUnits"/>.</summary>
    public static Int128 CeilingUnits(long origin, double d, Int128 u, int e) => Units(origin, d, u, e, ceiling: true);

    private static Int128 Units(long origin, double d, Int128 u, int e, bool ceiling)
    {
        if (e >= 0 && e <= MaxExact)
        {
            // origin = q · U + r with 0 ≤ r < U; the rest of the quotient comes from (r + d) / U in double.
            var step = u * IntegerPow10(e);
            var q = FloorDivide(origin, step);
            var r = (double)(origin - (q * step));
            return q + Round((r + d) / (double)step, ceiling);
        }

        if (e < 0 && -e <= MaxExact)
        {
            // In units of 10^e: origin · 10^−e + d · 10^−e, divided by u.
            var shift = IntegerPow10(-e);
            var scaled = origin * shift;
            var q = FloorDivide(scaled, u);
            var r = (double)(scaled - (q * u));
            return q + Round((r + (d * Pow10(-e))) / (double)u, ceiling);
        }

        return Round((origin + d) / ((double)u * Math.Pow(10, e)), ceiling);
    }

    private static Int128 Round(double value, bool ceiling) => (Int128)(ceiling ? Math.Ceiling(value) : Math.Floor(value));

    private static Int128 FloorDivide(Int128 a, Int128 b)
    {
        var q = a / b;
        return (a % b != 0 && (a < 0) != (b < 0)) ? q - 1 : q;
    }
}
