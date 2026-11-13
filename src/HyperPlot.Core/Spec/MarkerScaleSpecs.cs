namespace HyperPlot;

/// <summary>
/// The size scale of points (design §4.1, §4.2, row R3f): it maps the numbers of the point layers'
/// <see cref="PointLayerSpec.SizeBy"/> to diameters whose area is linear in the value. A plot has one,
/// <see cref="PlotSpec.ScaleSize"/>, shared by every layer.
/// </summary>
/// <remarks>
/// A value goes through <see cref="Scale"/>'s transform, then from the domain's low end (0) to its high end (1) as t, held
/// to [0, 1]. The diameter is √(<see cref="RangeMin"/>² + t · (<see cref="RangeMax"/>² − <see cref="RangeMin"/>²)) DIPs.
/// Inside the type, <c>Scale</c> names the property; the static class is written <c>HyperPlot.Scale</c>.
/// </remarks>
public sealed record SizeScaleSpec : ScaleSpec
{
    /// <summary>The greatest diameter of a range, in DIPs: a point layer's greatest <see cref="PointLayerSpec.Size"/>.</summary>
    internal const float MaxDiameter = 512;

    internal SizeScaleSpec(ContinuousScaleSpec scale) => Scale = scale;

    /// <summary>
    /// Gets the continuous scale that holds the transform, the domain, zero, the tick format, and the legend's title. Its
    /// <see cref="ContinuousScaleSpec.Clamp"/> and nice are not used: a value outside the domain takes the end size.
    /// </summary>
    public ContinuousScaleSpec Scale { get; init; }

    /// <summary>Gets the diameter at the domain's low end, in DIPs: above 0 and at most <see cref="RangeMax"/>. 2 by default.</summary>
    public float RangeMin { get; init; } = 2;

    /// <summary>Gets the diameter at the domain's high end, in DIPs: at least <see cref="RangeMin"/> and at most 512. 16 by default.</summary>
    public float RangeMax { get; init; } = 16;

    /// <summary>Returns whether a range is one a size scale takes: finite, 0 &lt; min ≤ max ≤ 512.</summary>
    internal static bool IsRange(float min, float max) => min > 0 && min <= max && max <= MaxDiameter;
}

/// <summary>
/// The shape scale of points (design §4.1, §4.2, row R3f): the categories of the point layers'
/// <see cref="PointLayerSpec.ShapeBy"/>, each a shape. Category i takes <see cref="Shapes"/>[i]; a column of more categories
/// than shapes is refused. A plot has one, <see cref="PlotSpec.ScaleShape"/>, shared by every layer.
/// </summary>
public sealed record ShapeScaleSpec : ScaleSpec
{
    private static readonly MarkerShape[] Default =
    [
        MarkerShape.Circle,
        MarkerShape.Square,
        MarkerShape.Triangle,
        MarkerShape.Diamond,
        MarkerShape.Cross,
        MarkerShape.X,
    ];

    internal ShapeScaleSpec() { }

    /// <summary>
    /// Gets the shapes in the categories' order: one or more named values, each once. Circle, Square, Triangle, Diamond,
    /// Cross, and X by default.
    /// </summary>
    public IReadOnlyList<MarkerShape> Shapes { get; init; } = Default;

    /// <summary>Gets the legend's title; null or empty draws none.</summary>
    public string? Title { get; init; }
}
