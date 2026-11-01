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

/// <summary>Copies of the position scales with an axis title (design §4.1).</summary>
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

    /// <summary>Returns a copy of a continuous scale with its axis on a side of the panel (design §4.1, row G6a).</summary>
    /// <typeparam name="T">The scale's kind.</typeparam>
    /// <param name="scale">The scale.</param>
    /// <param name="side">The side: the bottom or the top for x, the left or the right for y; the compile checks it against the direction.</param>
    /// <returns>The copy, with a new <see cref="SpecNode.Id"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scale"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="side"/> is not a named value.</exception>
    public static T WithAxisSide<T>(this T scale, AxisSide side)
        where T : ContinuousScaleSpec
    {
        ArgumentNullException.ThrowIfNull(scale);
        CheckSide(side);
        return scale with { Side = side };
    }

    /// <summary>Returns a copy of a band scale with its axis on a side of the panel (row G6a): the bottom or the top.</summary>
    /// <param name="scale">The scale.</param>
    /// <param name="side">The side.</param>
    /// <returns>The copy, with a new <see cref="SpecNode.Id"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scale"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="side"/> is not a named value.</exception>
    public static BandScaleSpec WithAxisSide(this BandScaleSpec scale, AxisSide side)
    {
        ArgumentNullException.ThrowIfNull(scale);
        CheckSide(side);
        return scale with { Side = side };
    }

    /// <summary>
    /// Returns a copy of a primary scale with an axis on the opposite side that shows a transform of its values (design
    /// §4.1, row G6a).
    /// </summary>
    /// <typeparam name="T">The scale's kind.</typeparam>
    /// <param name="scale">The scale.</param>
    /// <param name="transform">The transform from the scale's data values to the axis's, strictly monotonic; null for a copy of the axis.</param>
    /// <param name="title">The axis title, or null for none.</param>
    /// <returns>The copy, with a new <see cref="SpecNode.Id"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scale"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="scale"/> is a time scale, whose values a transform of doubles does not take.</exception>
    public static T WithSecondaryAxis<T>(this T scale, IScale? transform = null, string? title = null)
        where T : ContinuousScaleSpec
    {
        ArgumentNullException.ThrowIfNull(scale);
        if (scale is TimeScaleSpec)
        {
            throw new ArgumentException("A time scale takes no secondary axis of a transform: its values are dates, not doubles.", nameof(scale));
        }

        return scale with
        {
            SecondaryAxis = new SecondaryAxisSpec { Transform = transform, Title = title },
        };
    }

    /// <summary>Returns a copy of a band scale with an axis title (row G7b).</summary>
    /// <param name="scale">The scale.</param>
    /// <param name="title">The title; null or empty for none.</param>
    /// <returns>The copy, with a new <see cref="SpecNode.Id"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scale"/> is null.</exception>
    public static BandScaleSpec WithTitle(this BandScaleSpec scale, string? title)
    {
        ArgumentNullException.ThrowIfNull(scale);
        return scale with { Title = title };
    }

    /// <summary>
    /// Returns a copy of an ordinal color scale with a legend title (row G7c). A sequential or diverging scale takes its title
    /// from its inner continuous scale.
    /// </summary>
    /// <param name="scale">The scale.</param>
    /// <param name="title">The title; null or empty for none.</param>
    /// <returns>The copy, with a new <see cref="SpecNode.Id"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scale"/> is null.</exception>
    public static OrdinalScaleSpec WithTitle(this OrdinalScaleSpec scale, string? title)
    {
        ArgumentNullException.ThrowIfNull(scale);
        return scale with { Title = title };
    }

    private static void CheckSide(AxisSide side)
    {
        if ((uint)side > (uint)AxisSide.Right)
        {
            throw new ArgumentOutOfRangeException(nameof(side), side, "The side is a named value.");
        }
    }
}
