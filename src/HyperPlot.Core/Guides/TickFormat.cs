using System.Globalization;

namespace HyperPlot;

/// <summary>
/// The default tick labels (design §4.6), written in the invariant culture without allocating: numbers from M · 10^E, with
/// the decimals of an axis kept alike or each label its own, in the exponent form when a fixed label would pass 12
/// characters; date-times by the coarsest calendar boundary each tick lies on.
/// </summary>
internal static class TickFormat
{
    /// <summary>The longest fixed label; an axis with a longer one writes all of its labels in the exponent form.</summary>
    public const int MaxFixedLength = 12;

    /// <summary>The size of the buffer a label is written into, the default's and a user's.</summary>
    public const int BufferLength = 64;

    /// <summary>Returns the decimals a value needs: none for an integer, otherwise the digits after the point without the trailing zeros.</summary>
    public static int Decimals(TickNumber number)
    {
        if (number.Exponent >= 0 || number.Mantissa == 0)
        {
            return 0;
        }

        var decimals = -number.Exponent;
        var mantissa = Int128.Abs(number.Mantissa);
        while (decimals > 0 && mantissa % 10 == 0)
        {
            mantissa /= 10;
            decimals--;
        }

        return decimals;
    }

    /// <summary>Returns the length of a value's fixed label with a number of decimals.</summary>
    public static int FixedLength(TickNumber number, int decimals)
    {
        Span<char> buffer = stackalloc char[BufferLength];
        return TryWriteFixed(number, decimals, buffer, out var written) ? written : int.MaxValue;
    }

    /// <summary>
    /// Writes a value with a number of decimals at least its own (design §4.6): the sign U+002D, no sign for 0, no point
    /// when there are no decimals.
    /// </summary>
    public static bool TryWriteFixed(TickNumber number, int decimals, Span<char> destination, out int written)
    {
        written = 0;
        if (number.Mantissa == 0)
        {
            return Write("0", destination, ref written) && (decimals == 0 || WriteZeros('.', decimals, destination, ref written));
        }

        // The digits of |M| · 10^(E + decimals), an integer since decimals ≥ −E for every label of the axis.
        var shift = number.Exponent + decimals;
        var digits = Int128.Abs(number.Mantissa);
        if (shift >= 0)
        {
            digits *= TickNumber.IntegerPow10(Math.Min(shift, 38));
        }
        else
        {
            digits /= TickNumber.IntegerPow10(-shift);
        }

        Span<char> text = stackalloc char[48];
        if (!digits.TryFormat(text, out var length, default, CultureInfo.InvariantCulture))
        {
            return false;
        }

        if (number.Mantissa < 0 && !Write("-", destination, ref written))
        {
            return false;
        }

        var whole = length - decimals;
        if (whole <= 0)
        {
            if (!Write("0.", destination, ref written) || !WriteZeros(null, -whole, destination, ref written))
            {
                return false;
            }

            return Write(text[..length], destination, ref written);
        }

        if (!Write(text[..whole], destination, ref written))
        {
            return false;
        }

        return decimals == 0 || (Write(".", destination, ref written) && Write(text.Slice(whole, decimals), destination, ref written));
    }

    /// <summary>
    /// Writes a value as m.mmm e±x, with the exponent of the axis's largest magnitude and the mantissa's decimals the axis
    /// needs (design §4.6): 2.5e-7, 1e15.
    /// </summary>
    public static bool TryWriteExponent(TickNumber number, int exponent, int decimals, Span<char> destination, out int written)
    {
        written = 0;
        var mantissa = new TickNumber(number.Mantissa, number.Exponent - exponent);
        if (!TryWriteFixed(mantissa, decimals, destination, out written))
        {
            return false;
        }

        if (number.Mantissa == 0)
        {
            return true;
        }

        if (!Write("e", destination, ref written))
        {
            return false;
        }

        return exponent.TryFormat(destination[written..], out var count, default, CultureInfo.InvariantCulture) && Advance(count, ref written);
    }

    /// <summary>Returns the exponent of a value's largest power of ten, ⌊log10 |value|⌋, exactly from M and E.</summary>
    public static int Magnitude(TickNumber number)
    {
        var digits = 0;
        var mantissa = Int128.Abs(number.Mantissa);
        while (mantissa >= 10)
        {
            mantissa /= 10;
            digits++;
        }

        return number.Exponent + digits;
    }

    /// <summary>
    /// Writes a date-time tick by the boundary it lies on (design §4.6): yyyy, yyyy-MM, MM-dd, HH:mm, HH:mm:ss, ss.fff,
    /// ss.ffffff, ss.fffffffff.
    /// </summary>
    /// <param name="time">The date-time, to 100 ns.</param>
    /// <param name="nanoseconds">The nanoseconds below 100, from 0 to 99.</param>
    /// <param name="boundary">The coarsest boundary the tick lies on.</param>
    /// <param name="destination">The buffer.</param>
    /// <param name="written">The characters written.</param>
    public static bool TryWriteTime(DateTime time, int nanoseconds, TimeInterval boundary, Span<char> destination, out int written)
    {
        written = 0;
        var subSecond = ((time.Ticks % TimeSpan.TicksPerSecond) * 100) + nanoseconds;
        return boundary switch
        {
            TimeInterval.Year => time.TryFormat(destination, out written, "yyyy", CultureInfo.InvariantCulture),
            TimeInterval.Month => time.TryFormat(destination, out written, "yyyy-MM", CultureInfo.InvariantCulture),
            TimeInterval.Day => time.TryFormat(destination, out written, "MM-dd", CultureInfo.InvariantCulture),
            TimeInterval.Hour or TimeInterval.Minute => time.TryFormat(destination, out written, "HH:mm", CultureInfo.InvariantCulture),
            TimeInterval.Second => time.TryFormat(destination, out written, "HH:mm:ss", CultureInfo.InvariantCulture),
            TimeInterval.Millisecond => SubSecond(time, subSecond / 1_000_000, 3, destination, out written),
            TimeInterval.Microsecond => SubSecond(time, subSecond / 1_000, 6, destination, out written),
            _ => SubSecond(time, subSecond, 9, destination, out written),
        };
    }

    private static bool SubSecond(DateTime time, long fraction, int digits, Span<char> destination, out int written)
    {
        written = 0;
        if (!time.TryFormat(destination, out written, "ss", CultureInfo.InvariantCulture) || !Write(".", destination, ref written))
        {
            return false;
        }

        Span<char> format = stackalloc char[2];
        format[0] = 'D';
        format[1] = (char)('0' + digits);
        return fraction.TryFormat(destination[written..], out var count, format, CultureInfo.InvariantCulture) && Advance(count, ref written);
    }

    private static bool Write(ReadOnlySpan<char> text, Span<char> destination, ref int written)
    {
        if (!text.TryCopyTo(destination[written..]))
        {
            return false;
        }

        written += text.Length;
        return true;
    }

    private static bool WriteZeros(char? first, int count, Span<char> destination, ref int written)
    {
        var needed = count + (first is null ? 0 : 1);
        if (destination.Length - written < needed)
        {
            return false;
        }

        if (first is { } c)
        {
            destination[written++] = c;
        }

        destination.Slice(written, count).Fill('0');
        written += count;
        return true;
    }

    private static bool Advance(int count, ref int written)
    {
        written += count;
        return true;
    }
}
