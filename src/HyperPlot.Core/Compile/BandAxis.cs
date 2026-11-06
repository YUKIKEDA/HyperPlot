namespace HyperPlot;

/// <summary>
/// A band axis in the space of category numbers (design §4.2, row G7b): band j covers [j, j + 1 − PaddingInner] there, and
/// the domain [L, U] maps that space to the axis's pixels as a linear scale does. With m = max(1, n − pi + 2 po), the
/// domain is L = −(m − n + pi) · align and U = L + m, which d3's band gives for any pixel length: step = W / m and the
/// first band starts at step · (m − n + pi) · align. Reverse puts category i at position n − 1 − i, as d3 assigns a
/// reversed range's positions.
/// </summary>
/// <param name="Count">The number of categories n.</param>
/// <param name="PaddingInner">The share of a step between two bands.</param>
/// <param name="Reverse">Whether category i is at position n − 1 − i.</param>
internal readonly record struct BandAxis(int Count, double PaddingInner, bool Reverse)
{
    /// <summary>Gets the band's width in the space of positions: 1 − PaddingInner.</summary>
    public double Width => 1 - PaddingInner;

    /// <summary>Returns the domain of the positions' space for a scale and a number of categories.</summary>
    public static ScaleRange Domain(BandScaleSpec scale, int count)
    {
        var m = Math.Max(1, count - scale.PaddingInner + (2 * scale.PaddingOuter));
        var lower = -(m - count + scale.PaddingInner) * scale.Align;
        return ScaleRange.FromDouble(lower, lower + m);
    }

    /// <summary>Returns the position of a category: its number, or the reverse. A position's category is the same function.</summary>
    public int Position(int category) => Reverse ? Count - 1 - category : category;

    /// <summary>
    /// Returns the categories whose bands lie at least partly over an axis's pixels, position j's band starting at pixel
    /// a · j + b (row I1b): those of a view's range, so that a loop over them is bounded by the view, not by the categories.
    /// First is past Last when none does.
    /// </summary>
    public (int First, int Last) Visible(double a, double b, int offset, int size)
    {
        if (a == 0 || !double.IsFinite(a) || !double.IsFinite(b))
        {
            return (0, Count - 1);
        }

        var (p0, p1) = ((offset - b) / a, (offset + size - b) / a);
        var (low, high) = (Math.Min(p0, p1), Math.Max(p0, p1));

        // Band j covers [j, j + Width]: it shows when it ends past the low end and starts before the high end.
        var first = (int)Math.Clamp(Math.Ceiling(low - Width), 0, Count);
        var last = (int)Math.Clamp(Math.Floor(high), -1, Count - 1);
        return Reverse ? (Count - 1 - last, Count - 1 - first) : (first, last);
    }
}
