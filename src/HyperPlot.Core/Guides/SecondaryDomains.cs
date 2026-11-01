namespace HyperPlot;

/// <summary>
/// The secondary scales' transforms and learned domains a layout takes (design §4.6, row G6a): the default for a plot
/// without secondary scales.
/// </summary>
/// <param name="XTransform">The secondary x scale's transform, or null for linear and time.</param>
/// <param name="X">The secondary x scale's learned domain in its scale space.</param>
/// <param name="YTransform">The secondary y scale's transform.</param>
/// <param name="Y">The secondary y scale's learned domain.</param>
internal readonly record struct SecondaryDomains(IScale? XTransform, ScaleRange X, IScale? YTransform, ScaleRange Y);
