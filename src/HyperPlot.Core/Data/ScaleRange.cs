namespace HyperPlot;

/// <summary>
/// A range of the scale space as an integer origin and two <see cref="double"/> differences from it (design §3.6): a domain
/// of the compile (§4.8) and, from row I1, a view (§6.2). The one place that picks and moves the origin.
/// </summary>
/// <param name="Origin">The origin, an integer in the column's unit.</param>
/// <param name="Lower">The lower end, as a difference from <see cref="Origin"/>.</param>
/// <param name="Upper">The upper end, as a difference from <see cref="Origin"/>.</param>
internal readonly record struct ScaleRange(long Origin, double Lower, double Upper)
{
    private const double TwoToThe64 = 18446744073709551616.0;

    /// <summary>Gets the range that no domain equals: its ends are NaN.</summary>
    public static ScaleRange None => new(0, double.NaN, double.NaN);

    /// <summary>Returns the range of two values given as doubles, anchored (design §3.6).</summary>
    /// <param name="min">The lower end.</param>
    /// <param name="max">The upper end.</param>
    /// <returns>The range.</returns>
    public static ScaleRange FromDouble(double min, double max) => new ScaleRange(0, min, max).Anchor();

    /// <summary>Returns the range of two integers: the origin is <paramref name="min"/>, and the differences are exact before one rounding.</summary>
    /// <param name="min">The lower end.</param>
    /// <param name="max">The upper end.</param>
    /// <returns>The range.</returns>
    public static ScaleRange FromInteger(long min, long max) => new(min, 0, ScaleSpace.Difference(max, min));

    /// <summary>
    /// Moves the origin to the floor of the lower end, so that the lower difference lies in [0, 1) (design §3.6). An origin
    /// past the range of <see cref="long"/> is clamped to it. The rounding this brings is bounded in §3.6.
    /// </summary>
    /// <returns>The range with its origin moved.</returns>
    public ScaleRange Anchor()
    {
        var shift = Math.Floor(Lower);
        if (!double.IsFinite(shift) || shift == 0)
        {
            return this;
        }

        // A shift of 2^64 or more moves any origin past the range of long; a smaller one is exact in Int128.
        var target =
            shift >= TwoToThe64 ? Int128.MaxValue
            : shift <= -TwoToThe64 ? Int128.MinValue
            : Origin + (Int128)shift;
        var origin = (long)Int128.Clamp(target, long.MinValue, long.MaxValue);
        var moved = (double)((Int128)origin - Origin);
        return new ScaleRange(origin, Lower - moved, Upper - moved);
    }
}
