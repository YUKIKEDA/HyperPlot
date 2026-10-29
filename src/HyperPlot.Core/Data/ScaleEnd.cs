namespace HyperPlot;

/// <summary>
/// One end of a column's range for learning a domain (design §4.2): an exact <see cref="long"/> for an integer column, or a
/// <see cref="double"/>.
/// </summary>
/// <param name="IsInteger">Whether the end is <see cref="Integer"/>.</param>
/// <param name="Integer">The end of an integer column.</param>
/// <param name="Value">The end as a <see cref="double"/>: <see cref="Integer"/> rounded once for an integer column.</param>
internal readonly record struct ScaleEnd(bool IsInteger, long Integer, double Value)
{
    /// <summary>Returns the end of an integer column.</summary>
    public static ScaleEnd Of(long value) => new(true, value, value);

    /// <summary>Returns the end of a floating-point column.</summary>
    public static ScaleEnd Of(double value) => new(false, 0, value);
}
