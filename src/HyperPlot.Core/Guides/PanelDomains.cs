namespace HyperPlot;

/// <summary>
/// A panel's learned domains in the scale space (design §4.2, row G6b): x, y, and the secondary x and y (row G6a). A shared
/// direction gives every panel the same.
/// </summary>
internal readonly record struct PanelDomains(ScaleRange X, ScaleRange Y, ScaleRange X2, ScaleRange Y2);
