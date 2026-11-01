namespace HyperPlot;

/// <summary>
/// The band scale (design §4.1, §4.2, row G7b): the categories of a <see cref="CategoricalColumn"/> on x, each a band of the
/// axis, as d3-scale's band. With n categories over W pixels, step = W / max(1, n − <see cref="PaddingInner"/> +
/// 2 · <see cref="PaddingOuter"/>), the first band starts at (W − step · (n − <see cref="PaddingInner"/>)) · <see cref="Align"/>,
/// and each band is step · (1 − <see cref="PaddingInner"/>) wide. The domain is all the column's categories.
/// </summary>
public sealed record BandScaleSpec : ScaleSpec
{
    internal BandScaleSpec() { }

    /// <summary>Gets the share of a step between two bands: at least 0 and at most 1, 0.1 by default (Vega-Lite's for bars).</summary>
    public double PaddingInner { get; init; } = 0.1;

    /// <summary>Gets the space before the first band and after the last, in steps: at least 0 and at most 1, 0.05 by default.</summary>
    public double PaddingOuter { get; init; } = 0.05;

    /// <summary>Gets how the outer space is shared between the two ends: 0 all after, 1 all before, 0.5 by default.</summary>
    public double Align { get; init; } = 0.5;

    /// <summary>Gets a value indicating whether the categories run in the reverse of their numbers' order.</summary>
    public bool Reverse { get; init; }

    /// <summary>Gets the axis title; null or empty draws none.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the side of the panel the axis lies on (row G6a): the bottom or the top; null for the bottom.</summary>
    public AxisSide? Side { get; init; }

    /// <summary>Throws unless the paddings and the alignment are in range (design §4.8): a spec made with <c>with</c> is checked when drawn.</summary>
    /// <exception cref="ArgumentException">A value is NaN or outside [0, 1].</exception>
    internal void Validate()
    {
        Check(PaddingInner, nameof(PaddingInner));
        Check(PaddingOuter, nameof(PaddingOuter));
        Check(Align, nameof(Align));

        static void Check(double value, string name)
        {
            if (!(value >= 0 && value <= 1))
            {
                throw new ArgumentException($"The band scale's {name} is {value}; it is at least 0 and at most 1.");
            }
        }
    }
}
