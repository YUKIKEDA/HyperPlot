namespace HyperPlot;

/// <summary>
/// Writes the label of a tick of a numeric scale (design §4.6), as <see cref="ISpanFormattable.TryFormat"/> does: without
/// allocating, into a buffer of 64 characters. Called on the thread that renders, for the candidate labels while the ticks
/// are chosen. 0 characters is an empty label, which is not drawn.
/// </summary>
/// <param name="value">The tick's value, in data units.</param>
/// <param name="destination">The buffer.</param>
/// <param name="charsWritten">The number of characters written.</param>
/// <returns>Whether the label fit; false throws <see cref="InvalidOperationException"/> from the render.</returns>
public delegate bool TickFormatter(double value, Span<char> destination, out int charsWritten);

/// <summary>
/// Writes the label of a tick of a time scale (design §4.6), without allocating, into a buffer of 64 characters.
/// </summary>
/// <param name="value">The tick's value, in the column's unit (<see cref="TimeScaleSpec.Unit"/> from <see cref="TimeScaleSpec.Epoch"/>).</param>
/// <param name="boundary">The coarsest calendar boundary the tick lies on: a tick at midnight on the 1st of a month is on <see cref="TimeInterval.Month"/>.</param>
/// <param name="destination">The buffer.</param>
/// <param name="charsWritten">The number of characters written.</param>
/// <returns>Whether the label fit; false throws <see cref="InvalidOperationException"/> from the render.</returns>
public delegate bool TimeTickFormatter(long value, TimeInterval boundary, Span<char> destination, out int charsWritten);

/// <summary>The calendar boundaries of time ticks, coarsest first (design §4.6).</summary>
public enum TimeInterval
{
    /// <summary>January 1st, midnight.</summary>
    Year,

    /// <summary>The 1st of a month, midnight.</summary>
    Month,

    /// <summary>Midnight.</summary>
    Day,

    /// <summary>A whole hour.</summary>
    Hour,

    /// <summary>A whole minute.</summary>
    Minute,

    /// <summary>A whole second.</summary>
    Second,

    /// <summary>A whole millisecond.</summary>
    Millisecond,

    /// <summary>A whole microsecond.</summary>
    Microsecond,

    /// <summary>A nanosecond.</summary>
    Nanosecond,
}

/// <summary>Copies of the continuous scales with an axis title (design §4.1).</summary>
public static class ScaleSpecExtensions
{
    /// <summary>Returns a copy of a scale with an axis title.</summary>
    /// <typeparam name="T">The kind of scale.</typeparam>
    /// <param name="scale">The scale.</param>
    /// <param name="title">The title; null or empty for none.</param>
    /// <returns>The copy, with a new <see cref="SpecNode.Id"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scale"/> is null.</exception>
    public static T WithTitle<T>(this T scale, string? title)
        where T : ContinuousScaleSpec
    {
        ArgumentNullException.ThrowIfNull(scale);
        return scale with { Title = title };
    }
}
