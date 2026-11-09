namespace HyperPlot;

/// <summary>
/// Where a hit test meets the bands of a layer over categories (design §6.3 "要素の層", row I2c, Issue #355 Decisions 3 and
/// 10): a band's pixels are linear in its position, so the band under the cursor, the nearest one, or a pixel column's run of
/// bands narrower than a pixel is found by arithmetic, as the recording places them.
/// </summary>
internal static class CategoryHits
{
    /// <summary>Returns a band axis's line: the pixel of position j is a · j + b along.</summary>
    public static (double A, double B) Line(in GeomFrame.Axis x)
    {
        var b = HitScan.PixelOf(x, 0 - x.Domain.Origin);
        return (HitScan.PixelOf(x, 1 - x.Domain.Origin) - b, b);
    }

    /// <summary>Returns whether a band axis's bands are a pixel wide or more, as the recording draws them one by one.</summary>
    public static bool Wide(double a, in BandAxis band) => Math.Abs(a * band.Width) >= 1;

    /// <summary>
    /// Returns the position whose band holds a pixel along, or the nearest band's when the pixel lies in a gap: of a band's
    /// middle and the next one's, the nearer.
    /// </summary>
    public static int PositionAt(double along, double a, double b, in BandAxis band)
    {
        var t = (along - b) / a;
        var p = Math.Floor(t);
        if (t - p > band.Width)
        {
            var (here, next) = (p + (band.Width / 2), p + 1 + (band.Width / 2));
            p = Math.Abs(t - here) <= Math.Abs(t - next) ? p : p + 1;
        }

        return (int)Math.Clamp(p, 0, band.Count - 1);
    }

    /// <summary>Returns the positions whose bands start in a pixel column, as the recording joins them, or an empty run.</summary>
    public static (long From, long To) InColumn(double column, double a, double b, in BandAxis band)
    {
        var (first, last) =
            a > 0
                ? (Math.Ceiling((column - b) / a), Math.Ceiling((column + 1 - b) / a) - 1)
                : (Math.Floor((column + 1 - b) / a) + 1, Math.Floor((column - b) / a));
        return ((long)Math.Max(first, 0), (long)Math.Min(last, band.Count - 1));
    }

    /// <summary>Returns the first category of a run of positions: positions from..to are categories from..to, or the reverse.</summary>
    public static long FirstCategory(long from, long to, in BandAxis band) => band.Reverse ? band.Count - 1 - to : from;

    /// <summary>Returns the position range a radius around a pixel along reaches, clamped to the bands.</summary>
    public static (int From, int To) Reach(double along, double radius, double a, double b, in BandAxis band)
    {
        var (t0, t1) = ((along - radius - b) / a, (along + radius - b) / a);
        var (low, high) = (Math.Floor(Math.Min(t0, t1)), Math.Floor(Math.Max(t0, t1)));
        return ((int)Math.Clamp(low, 0, band.Count - 1), (int)Math.Clamp(high, 0, band.Count - 1));
    }

    /// <summary>
    /// Returns the end of a category's bar on the cursor's side of the baseline, and its row (Issue #355 Decision 3): the
    /// greatest above, the least below; the other end when the category's bar reaches only that side. −1 with no value.
    /// </summary>
    public static (double Value, long Row) Side(CategoryExtremes extremes, long category, bool above, double baseline)
    {
        var (min, max) = (extremes.Min[category], extremes.Max[category]);
        if (double.IsNaN(min))
        {
            return (double.NaN, -1);
        }

        var useMax = above ? max > baseline || !(min < baseline) : !(min < baseline) && max > baseline;
        return useMax ? (max, extremes.MaxRow[category]) : (min, extremes.MinRow[category]);
    }

    /// <summary>Returns the category of a pyramid's least or greatest over a run of categories, or −1 when the run has no value.</summary>
    public static long Extreme(MinMaxPyramid<double> pyramid, long first, long count, bool greatest, ref bool provisional)
    {
        pyramid.Query(first, first + count, wantCount: false, out var result);
        provisional |= result.IsProvisional;
        return !result.Found ? -1
            : greatest ? result.MaxIndex
            : result.MinIndex;
    }
}
