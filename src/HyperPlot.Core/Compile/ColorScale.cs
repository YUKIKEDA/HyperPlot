namespace HyperPlot;

/// <summary>
/// The compiled color scale of a plot (design §4.2, row G7c): the palette of its categories, or the color map, transform,
/// domain, and middle that map its numbers to colors. One a plan, made again when the spec changes; mapping allocates
/// nothing.
/// </summary>
internal sealed class ColorScale
{
    private readonly double[] _one = new double[1];
    private readonly double[] _oneMapped = new double[1];

    /// <summary>Gets the spec: the plot's, or the default for its columns' kind.</summary>
    public ColorScaleSpec Spec { get; private set; } = Scale.Ordinal();

    /// <summary>Gets a value indicating whether the scale colors categories.</summary>
    public bool Categorical => Spec is OrdinalScaleSpec;

    /// <summary>Gets the palette of the categories: the ordinal scale's, or the theme's.</summary>
    public Palette Palette { get; private set; } = Palettes.Tab10;

    /// <summary>Gets the categories, when the scale colors categories and a layer splits by them.</summary>
    public CategoricalColumn? Categories { get; private set; }

    /// <summary>Gets the inner continuous scale of a sequential or diverging scale.</summary>
    public ContinuousScaleSpec? Inner { get; private set; }

    /// <summary>Gets the transform of the inner scale, or null for none (linear, time).</summary>
    public IScale? Transform { get; private set; }

    /// <summary>Gets or sets the domain in the scale's space: learned, and widened by nice through the color bar's ticks.</summary>
    public ScaleRange Domain { get; set; }

    /// <summary>Gets the color map of a sequential or diverging scale.</summary>
    public ColorMap? Map { get; private set; }

    /// <summary>Gets a value indicating whether the scale is diverging.</summary>
    public bool Diverging => Spec is DivergingScaleSpec;

    /// <summary>Gets the middle of a diverging scale in the scale's space, set by <see cref="CheckMiddle"/>.</summary>
    public double Middle { get; private set; }

    /// <summary>Takes a spec and the plot's palette; the domain comes after the learning.</summary>
    public void Set(ColorScaleSpec spec, Palette themePalette, CategoricalColumn? categories)
    {
        // The transform is made again only for a new inner scale: drawing the same spec again allocates nothing.
        var before = Inner;
        Spec = spec;
        Categories = categories;
        (Palette, Inner, Map) = spec switch
        {
            OrdinalScaleSpec o => (o.Palette ?? themePalette, (ContinuousScaleSpec?)null, (ColorMap?)null),
            SequentialScaleSpec s => (themePalette, s.Scale, s.Map),
            DivergingScaleSpec d => (themePalette, d.Scale, d.Map),
            _ => (themePalette, null, null),
        };
        if (!ReferenceEquals(before, Inner))
        {
            Transform = Inner?.CreateTransform();
        }
    }

    /// <summary>
    /// Sets the middle of a diverging scale in the scale's space, and throws unless it lies inside the domain (design §4.2):
    /// the given one mapped through the transform, or the middle of the domain.
    /// </summary>
    public void CheckMiddle()
    {
        if (Spec is not DivergingScaleSpec diverging)
        {
            return;
        }

        var (low, high) = (Domain.Origin + Domain.Lower, Domain.Origin + Domain.Upper);
        if (diverging.Mid is not { } mid)
        {
            Middle = (low + high) / 2;
            return;
        }

        Middle = Forward(mid);
        if (!(low < Middle && Middle < high))
        {
            throw new ArgumentException(
                $"The diverging color scale's Mid is {mid}, at {Middle} in its scale's space, outside the domain [{low}, {high}]; the middle lies inside the domain (design §4.2)."
            );
        }
    }

    /// <summary>Returns the color of a value in data units (design §4.2): NaN and values that do not map are transparent.</summary>
    public Color Of(double value)
    {
        var map = Map!;
        var s = Forward(value);
        if (!double.IsFinite(s))
        {
            return default;
        }

        return map.Map(Position(s));
    }

    /// <summary>Returns the position from 0 to 1 of a value of the scale's space: below 0 or above 1 outside the domain.</summary>
    public double Position(double s)
    {
        var (low, high) = (Domain.Origin + Domain.Lower, Domain.Origin + Domain.Upper);
        var t = Diverging
            ? (s <= Middle ? 0.5 * (s - low) / (Middle - low) : 0.5 + (0.5 * (s - Middle) / (high - Middle)))
            : (s - low) / (high - low);
        return Inner!.Reverse ? 1 - t : t;
    }

    /// <summary>Returns a value in the scale's space: through the transform, or as it is.</summary>
    public double Forward(double value)
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
