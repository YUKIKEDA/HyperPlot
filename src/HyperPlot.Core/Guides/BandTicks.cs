namespace HyperPlot;

/// <summary>
/// The ticks of a band axis (design §4.6, row G7b): one in the middle of a category's band, labeled with its name, on the
/// categories whose number is a multiple of k. Two labels side by side keep a gap of at least half an em (PR #164). k
/// starts at the least that keeps the labels' known lower size apart, and grows past an overlapping pair to the least that
/// would keep that pair apart, so that the labels measured are bounded by the axis's pixels, not by the categories.
/// </summary>
internal static class BandTicks
{
    /// <summary>Chooses the ticks of a band axis into the axis's candidate set; the final domain is the band's, without nice.</summary>
    public static void Choose(TickAxis axis, BandScaleSpec band)
    {
        axis.SetFinalDomain(axis.Domain);
        var categories = axis.Categories;
        var count = categories?.CategoryCount ?? 0;
        if (categories is null || count == 0)
        {
            return;
        }

        var bands = new BandAxis(count, band.PaddingInner, band.Reverse);
        var domain = axis.Domain;
        var step = axis.Length / (domain.Upper - domain.Lower);

        // The domain's low end as a position: its origin and difference (design §3.6). The difference alone is off by the
        // origin, ⌊L⌋ = −1 for the default padding: a band to the left (Issue #190).
        var low = domain.Origin + domain.Lower;

        // The categories whose middle lies over the axis (row I1b): a view's, so that the labels measured are bounded by the
        // view, not by the categories. The full domain holds every middle.
        var high = domain.Origin + domain.Upper;
        var (firstPosition, lastPosition) = (
            (long)Math.Clamp(Math.Ceiling(low - (bands.Width / 2)), 0, count),
            (long)Math.Clamp(Math.Floor(high - (bands.Width / 2)), -1, count - 1)
        );
        if (firstPosition > lastPosition)
        {
            return;
        }

        var (from, to) = band.Reverse ? (count - 1 - lastPosition, count - 1 - firstPosition) : (firstPosition, lastPosition);
        var least = axis.Horizontal ? 1f : axis.LineHeight;
        var first = Extent(axis, categories.Categories[(int)from]);
        var gap = axis.Em / 2;

        // The least k that keeps two labels of the least size apart, and the first label and one of the least size. Held inside
        // F, the first label moves toward its neighbor by s₀ (design §4.6, row G3b).
        var firstPixel = (bands.Position((int)from) + (bands.Width / 2) - low) * step;
        var firstShift = Math.Abs(axis.Hold(firstPixel, first) - firstPixel);
        var k = (long)Math.Max(Math.Max(1, Math.Ceiling((least + gap) / step)), Math.Ceiling((((first + least) / 2) + gap + firstShift) / step));
        while (true)
        {
            axis.ClearCandidate();
            var (overlap, sum) = (false, 0.0);
            var (previous, previousHeld, previousShift) = (0f, 0.0, 0.0);

            // The labels sit on the categories whose number is a multiple of k, so that they stay as a view pans.
            for (var i = (from + k - 1) / k * k; i <= to; i += Math.Min(k, count))
            {
                var category = (int)i;
                var label = categories.Categories[category];
                var extent = category == from ? first : Extent(axis, label);
                var pixel = (bands.Position(category) + (bands.Width / 2) - low) * step;
                var held = axis.Hold(pixel, extent);
                var shift = Math.Abs(held - pixel);

                // The boxes as drawn, held inside F: an end label that moves comes nearer its neighbor.
                if (axis.Count > 0 && Math.Abs(held - previousHeld) - ((extent + previous) / 2.0) < gap)
                {
                    (overlap, sum) = (true, ((extent + previous) / 2.0) + shift + previousShift);
                    break;
                }

                axis.AddLabel(label, pixel, extent);
                (previous, previousHeld, previousShift) = (extent, held, shift);
            }

            if (!overlap || k > to - from)
            {
                return;
            }

            // The pair k apart came too near: the next k keeps a pair of its sizes and shifts apart.
            k = Math.Max(k + 1, (long)Math.Ceiling((sum + gap) / step));
        }
    }

    private static float Extent(TickAxis axis, string label) => axis.Horizontal ? axis.Text.Measure(label, axis.Style).Width : axis.LineHeight;
}
