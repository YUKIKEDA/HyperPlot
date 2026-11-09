using System.Text;

namespace HyperPlot;

/// <content>The axis's range in words, for the plot's summary (design §7.1 "要約", row W1c, Issue #323), and a value in its format, for a hover (row I2b).</content>
internal sealed partial class TickAxis
{
    /// <summary>
    /// Writes the range the axis was last laid out over (Issue #323 Decision 3): a numeric or a time axis's two ends in the
    /// format of its tick labels, a band axis's first and last visible categories and their count. Allocates; off the frame
    /// path.
    /// </summary>
    internal void DescribeRange(StringBuilder text)
    {
        if (Band is { } band)
        {
            DescribeCategories(text, band);
            return;
        }

        var (low, high) = (Domain.Origin + Domain.Lower, Domain.Origin + Domain.Upper);
        if (!double.IsFinite(low) || !double.IsFinite(high))
        {
            _ = text.Append("no range yet");
            return;
        }

        var ends = new TickAxis
        {
            Scale = Scale,
            Transform = Transform,
            Text = Text,
            Style = Style,
            Horizontal = Horizontal,
            LineHeight = LineHeight,
            Em = Em,
            Length = Length,
        };
        if (Scale is TimeScaleSpec time && TryTimeEnds(ends, time))
        {
            _ = text.Append("from ").Append(ends.Label(0)).Append(" to ").Append(ends.Label(1));
            return;
        }

        // A log or symlog axis writes each label with its own decimals, so each end takes three significant digits; another
        // axis writes its ends with the decimals of its tick labels, and up to three more for an end the ticks' decimals do
        // not hold: "-2 to 2" for a view of [−1.5, 2.5] over ticks of whole numbers would misstate it.
        var own = Scale is LogScaleSpec or SymlogScaleSpec;
        var decimals = 0;
        for (var i = 0; i < _count; i++)
        {
            decimals = Math.Max(decimals, TickFormat.Decimals(_numbers[i]));
        }

        ends.AddNumber(Number(Inverse(low), own ? null : decimals), 0);
        ends.AddNumber(Number(Inverse(high), own ? null : decimals), 0);
        try
        {
            _ = ends.LabelNumbers(sameDecimals: !own);
            _ = text.Append("from ").Append(ends.Label(0)).Append(" to ").Append(ends.Label(1));
        }
        catch (InvalidOperationException)
        {
            // A user's format that writes no label for an end: the values as numbers.
            _ = text.Append(System.Globalization.CultureInfo.InvariantCulture, $"from {Inverse(low):G6} to {Inverse(high):G6}");
        }
    }

    /// <summary>
    /// Writes a data value in the format of the axis's tick labels, for a hover's tooltip (design §6.5, row I2b): a numeric
    /// axis's value at the ticks' decimals and up to three more (log and symlog at three significant digits), a time axis's at
    /// the finest boundary of its ticks; the user's format when the scale has one; a band axis's category name, the value
    /// being the category's number (row I2c, Issue #355 Decision 8). Returns the characters written, as many as fit. Allocates
    /// nothing.
    /// </summary>
    internal int WriteValue(double value, Span<char> destination)
    {
        var written = 0;
        if (Band is not null)
        {
            if (Categories is { } categories && value >= 0 && value < categories.CategoryCount)
            {
                return Copy(categories.Categories[(int)value], destination);
            }
        }
        else if (double.IsFinite(value) && Scale is TimeScaleSpec time)
        {
            if (WriteTime(value, time, out written))
            {
                return Copy(_buffer.AsSpan(0, written), destination);
            }
        }
        else if (double.IsFinite(value) && Scale is not null)
        {
            if (Scale.NumericFormat is { } format)
            {
                if (format(value, _buffer, out written) && written >= 0 && written <= _buffer.Length)
                {
                    return Copy(_buffer.AsSpan(0, written), destination);
                }
            }
            else if (WriteNumber(value, out written))
            {
                return Copy(_buffer.AsSpan(0, written), destination);
            }
        }

        // A value the axis's format does not write (NaN, outside the years 1 to 9999, a format that wrote nothing).
        return value.TryFormat(destination, out written, "G6", System.Globalization.CultureInfo.InvariantCulture) ? written : 0;
    }

    /// <summary>Copies as many characters as fit.</summary>
    private static int Copy(ReadOnlySpan<char> text, Span<char> destination)
    {
        var count = Math.Min(text.Length, destination.Length);
        text[..count].CopyTo(destination);
        return count;
    }

    /// <summary>Writes a value into the axis's buffer as its default labels would, with the decimals <see cref="DescribeRange"/> takes.</summary>
    private bool WriteNumber(double value, out int written)
    {
        var own = Scale is LogScaleSpec or SymlogScaleSpec;
        var decimals = 0;
        for (var i = 0; i < _count; i++)
        {
            decimals = Math.Max(decimals, TickFormat.Decimals(_numbers[i]));
        }

        var number = Number(value, own ? null : decimals);
        var places = own ? TickFormat.Decimals(number) : Math.Max(decimals, TickFormat.Decimals(number));
        if (TickFormat.FixedLength(number, places) <= TickFormat.MaxFixedLength)
        {
            return TickFormat.TryWriteFixed(number, places, _buffer, out written);
        }

        var magnitude = number.Mantissa == 0 ? 0 : TickFormat.Magnitude(number);
        var mantissa = TickFormat.Decimals(new TickNumber(number.Mantissa, number.Exponent - magnitude));
        return TickFormat.TryWriteExponent(number, magnitude, mantissa, _buffer, out written);
    }

    /// <summary>Writes a time value, in the column's unit, at the finest boundary of the ticks; false outside the years 1 to 9999.</summary>
    private bool WriteTime(double value, TimeScaleSpec time, out int written)
    {
        written = 0;
        var unit = TimeTicks.UnitNanoseconds(time.Unit);
        var boundary = TimeInterval.Day;
        for (var i = 0; i < _count; i++)
        {
            boundary = i == 0 || _boundaries[i] > boundary ? _boundaries[i] : boundary;
        }

        if (Math.Abs(value) >= 9.2e18)
        {
            return false;
        }

        var whole = Math.Floor(value);
        if (time.TickFormat is { } format)
        {
            return format((long)whole, boundary, _buffer, out written) && written >= 0 && written <= _buffer.Length;
        }

        var instant = ((Int128)time.Epoch.Ticks * 100) + ((Int128)(long)whole * unit) + (Int128)Math.Round((value - whole) * unit);
        if (instant < 0 || instant / 100 > DateTime.MaxValue.Ticks)
        {
            return false;
        }

        return TickFormat.TryWriteTime(new DateTime((long)(instant / 100)), (int)(instant % 100), boundary, _buffer, out written);
    }

    /// <summary>
    /// Returns a value as a tick number: at the ticks' decimals, or up to three more when those do not hold it; at three
    /// significant digits when null or too large. A half rounds away from 0.
    /// </summary>
    private static TickNumber Number(double value, int? decimals)
    {
        if (value == 0 || !double.IsFinite(value))
        {
            return new TickNumber(0, 0);
        }

        if (decimals is { } least)
        {
            var most = least + 3;
            while (least < most && Math.Abs(Math.Round(value, Math.Min(least, 15), MidpointRounding.AwayFromZero) - value) > Math.Abs(value) * 1e-12)
            {
                least++;
            }

            decimals = least;
        }

        var exponent =
            decimals is { } d && Math.Abs(value) * TickNumber.Pow10Signed(d) < 1e30 ? -d : (int)Math.Floor(Math.Log10(Math.Abs(value))) - 2;
        return new TickNumber((Int128)Math.Round(value / TickNumber.Pow10Signed(exponent), MidpointRounding.AwayFromZero), exponent);
    }

    /// <summary>Labels a time axis's two ends at the finest boundary of its ticks; false when an end lies outside the years 1 to 9999.</summary>
    private bool TryTimeEnds(TickAxis ends, TimeScaleSpec time)
    {
        var unit = TimeTicks.UnitNanoseconds(time.Unit);
        var epoch = (Int128)time.Epoch.Ticks * 100;
        var boundary = TimeInterval.Day;
        for (var i = 0; i < _count; i++)
        {
            boundary = i == 0 || _boundaries[i] > boundary ? _boundaries[i] : boundary;
        }

        foreach (var d in (ReadOnlySpan<double>)[Domain.Lower, Domain.Upper])
        {
            var instant = epoch + ((Int128)Domain.Origin * unit) + (Int128)Math.Round(d * unit);
            if (instant < 0 || instant / 100 > DateTime.MaxValue.Ticks)
            {
                return false;
            }

            ends.AddTime(instant, boundary, 0);
        }

        try
        {
            _ = ends.LabelTimes(time, time.Epoch.Ticks, unit);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Writes the band's first and last categories whose middle lies over the axis, and their count, as its ticks find them.</summary>
    private void DescribeCategories(StringBuilder text, BandScaleSpec band)
    {
        var count = Categories?.CategoryCount ?? 0;
        var bands = new BandAxis(count, band.PaddingInner, band.Reverse);
        var (low, high) = (Domain.Origin + Domain.Lower, Domain.Origin + Domain.Upper);
        var (first, last) = (
            (long)Math.Clamp(Math.Ceiling(low - (bands.Width / 2)), 0, count),
            (long)Math.Clamp(Math.Floor(high - (bands.Width / 2)), -1, count - 1)
        );
        if (Categories is null || first > last)
        {
            _ = text.Append("no category visible");
            return;
        }

        var (from, to) = band.Reverse ? (count - 1 - last, count - 1 - first) : (first, last);
        var shown = to - from + 1;
        _ =
            shown == 1
                ? text.Append("1 category, \"").Append(Categories.Categories[(int)from]).Append('"')
                : text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{shown} categories, from \"")
                    .Append(Categories.Categories[(int)from])
                    .Append("\" to \"")
                    .Append(Categories.Categories[(int)to])
                    .Append('"');
    }
}
