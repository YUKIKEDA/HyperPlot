using HyperPlot.Rendering;

namespace HyperPlot;

/// <summary>
/// The guides of one panel (design §4.6, row G6b): its rectangle, the ticks of its axes, the axes on its four sides, and
/// which of them it draws. A plot without a facet has one; a facet has one a panel, whose ticks of a shared direction are
/// the first panel's.
/// </summary>
internal sealed class PanelGuide
{
    public PanelGuide(TextEngine text, LabelTable labels)
    {
        foreach (var axis in (ReadOnlySpan<TickAxis>)[OwnX, OwnY, OwnX2, OwnY2])
        {
            axis.Text = text;
            axis.Labels = labels;
        }

        (X, Y, X2, Y2) = (OwnX, OwnY, OwnX2, OwnY2);
    }

    /// <summary>Gets or sets the ticks of x: the panel's own, or the first panel's when the panels share x (row G6b).</summary>
    public TickAxis X { get; set; }

    /// <summary>Gets or sets the ticks of y.</summary>
    public TickAxis Y { get; set; }

    /// <summary>Gets or sets the ticks of x's second axis (row G6a).</summary>
    public TickAxis X2 { get; set; }

    /// <summary>Gets or sets the ticks of y's second axis.</summary>
    public TickAxis Y2 { get; set; }

    /// <summary>Gets the panel's own ticks of x, which a free x chooses.</summary>
    public TickAxis OwnX { get; } = new();

    /// <summary>Gets the panel's own ticks of y.</summary>
    public TickAxis OwnY { get; } = new();

    /// <summary>Gets the panel's own ticks of x's second axis.</summary>
    public TickAxis OwnX2 { get; } = new();

    /// <summary>Gets the panel's own ticks of y's second axis.</summary>
    public TickAxis OwnY2 { get; } = new();

    /// <summary>Gets the transform of x's secondary axis of a transform (row G6a): the user's after the primary's.</summary>
    public SecondaryTransform SecondaryX { get; } = new();

    /// <summary>Gets the transform of y's secondary axis of a transform.</summary>
    public SecondaryTransform SecondaryY { get; } = new();

    /// <summary>Gets the axes on the panel's four sides: at Bottom, Top, Left, Right (row G6a).</summary>
    public PlotGuides.SideAxis[] Sides { get; } = new PlotGuides.SideAxis[4];

    /// <summary>
    /// Gets whether the panel draws the axis on each side (row G6b): a shared direction's only where no panel lies beside it,
    /// a free direction's always.
    /// </summary>
    public bool[] Draws { get; } = [true, true, true, true];

    /// <summary>
    /// Gets each side's offset of its axis from the panel's edge (row G6b): a strip between the panel and its axis.
    /// </summary>
    public int[] Offsets { get; } = new int[4];

    /// <summary>Gets or sets the panel's left edge, in whole pixels.</summary>
    public int Left { get; set; }

    /// <summary>Gets or sets the panel's top edge.</summary>
    public int Top { get; set; }

    /// <summary>Gets or sets the panel's width.</summary>
    public int Width { get; set; }

    /// <summary>Gets or sets the panel's height.</summary>
    public int Height { get; set; }

    /// <summary>Gets or sets the panel's polar transform, or null when it is not polar (row G11).</summary>
    public PolarTransform? Polar { get; set; }

    /// <summary>Gets or sets the band of the angle's labels outside the circle (design §4.6); 0 when not polar.</summary>
    public int ThetaBand { get; set; }

    /// <summary>Gets or sets the panel's row in the facet's grid (row G6b).</summary>
    public int Row { get; set; }

    /// <summary>Gets or sets the panel's column in the facet's grid.</summary>
    public int Column { get; set; }

    /// <summary>Gets or sets the strip above the panel (a wrap's category, row G6b), or null for none.</summary>
    public string? Strip { get; set; }
}
