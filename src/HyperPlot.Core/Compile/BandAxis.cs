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

    /// <summary>Returns the position of a category: its number, or the reverse.</summary>
    public int Position(int category) => Reverse ? Count - 1 - category : category;
}
