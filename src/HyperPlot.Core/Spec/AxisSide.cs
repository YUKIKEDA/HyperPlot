namespace HyperPlot;

/// <summary>The side of the panel an axis lies on (design §4.1, row G6a): x on the bottom or the top, y on the left or the right.</summary>
public enum AxisSide
{
    /// <summary>Below the panel: an x axis's default.</summary>
    Bottom,

    /// <summary>Above the panel.</summary>
    Top,

    /// <summary>Left of the panel: a y axis's default.</summary>
    Left,

    /// <summary>Right of the panel.</summary>
    Right,
}
