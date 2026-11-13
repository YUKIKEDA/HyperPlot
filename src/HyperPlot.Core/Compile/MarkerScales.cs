namespace HyperPlot;

/// <summary>
/// The compiled size scale of a plot (design §4.2 "行 R3f の大きさと形のスケール"): the transform, the domain, and the
/// diameters that map the numbers of the point layers' SizeBy to sizes. One a plan; mapping allocates nothing.
/// </summary>
internal sealed class SizeScale
{
    private readonly double[] _one = new double[1];
    private readonly double[] _oneMapped = new double[1];
    private ContinuousScaleSpec? _inner;

    /// <summary>Gets the spec: the plot's, or the default.</summary>
    public SizeScaleSpec Spec { get; private set; } = Scale.Size();

    /// <summary>Gets the inner continuous scale.</summary>
    public ContinuousScaleSpec Inner => Spec.Scale;

    /// <summary>Gets the transform of the inner scale, or null for none (linear, time).</summary>
    public IScale? Transform { get; private set; }

    /// <summary>Gets or sets the domain in the scale's space, learned from the size columns or given.</summary>
    public ScaleRange Domain { get; set; }

    /// <summary>Takes a spec; the domain comes after the learning.</summary>
    public void Set(SizeScaleSpec spec)
    {
        // The transform is made again only for a new inner scale: drawing the same spec again allocates nothing.
        Spec = spec;
        if (!ReferenceEquals(_inner, spec.Scale))
        {
            (_inner, Transform) = (spec.Scale, spec.Scale.CreateTransform());
        }
    }

    /// <summary>
    /// Returns the diameter of a value in data units, in DIPs (design §4.2): the area is linear from the domain's low end to
    /// its high end, and a value outside takes the end's size. NaN for a value that is NaN or does not map.
    /// </summary>
    public float Diameter(double value)
    {
        var s = Forward(value);
        if (!double.IsFinite(s))
        {
            return float.NaN;
        }

        var (low, high) = (Domain.Origin + Domain.Lower, Domain.Origin + Domain.Upper);

        // A domain of no width has no low and high: every value takes the middle size.
        var t = high > low ? Math.Clamp((s - low) / (high - low), 0, 1) : 0.5;
        var (min, max) = ((double)Spec.RangeMin, (double)Spec.RangeMax);
        return (float)Math.Sqrt((min * min) + (t * ((max * max) - (min * min))));
    }

    private double Forward(double value)
    {
        if (Transform is null)
        {
            return value;
        }

        _one[0] = value;
        Transform.Forward(_one, _oneMapped);
        return _oneMapped[0];
    }
}

/// <summary>
/// The compiled shape scale of a plot (design §4.2 "行 R3f の大きさと形のスケール"): the shapes of the categories of the
/// point layers' ShapeBy. One a plan.
/// </summary>
internal sealed class ShapeScale
{
    private MarkerShape[] _shapes = [];

    /// <summary>Gets the spec: the plot's, or the default.</summary>
    public ShapeScaleSpec? Spec { get; private set; }

    /// <summary>Gets the categories the layers' shape columns share.</summary>
    public CategoricalColumn Categories { get; private set; } = null!;

    /// <summary>Takes a spec and the shape columns' categories.</summary>
    public void Set(ShapeScaleSpec spec, CategoricalColumn categories)
    {
        // The shapes are copied only for a new spec: drawing the same spec again allocates nothing.
        if (!ReferenceEquals(Spec, spec))
        {
            _shapes = [.. spec.Shapes];
        }

        (Spec, Categories) = (spec, categories);
    }

    /// <summary>Returns the shape of a category: its number is below the shapes' count, which the compile checked.</summary>
    public MarkerShape Of(int category) => _shapes[category];
}
