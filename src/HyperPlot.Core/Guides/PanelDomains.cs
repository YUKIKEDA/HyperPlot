namespace HyperPlot;

/// <summary>
/// A panel's learned domains in the scale space (design §4.2, row G6b): x, y, and the secondary x and y (row G6a). A shared
/// direction gives every panel the same. As a view (rows I1a, I1b), an axis with no view holds <see cref="ScaleRange.None"/>;
/// as the domains a layout takes, <paramref name="Viewed"/> flags the axes whose domain is a view (1 x, 2 y, 4 x2, 8 y2), which take
/// no nice (design §6.2).
/// </summary>
/// <param name="X">The x scale's.</param>
/// <param name="Y">The y scale's.</param>
/// <param name="X2">The secondary x scale's.</param>
/// <param name="Y2">The secondary y scale's.</param>
/// <param name="Viewed">The axes over a view, as flags.</param>
internal readonly record struct PanelDomains(ScaleRange X, ScaleRange Y, ScaleRange X2, ScaleRange Y2, byte Viewed = 0)
{
    /// <summary>Gets domains with no view on any axis.</summary>
    public static PanelDomains None => new(ScaleRange.None, ScaleRange.None, ScaleRange.None, ScaleRange.None);
}
