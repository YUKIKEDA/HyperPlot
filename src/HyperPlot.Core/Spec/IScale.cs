namespace HyperPlot;

/// <summary>
/// The transform of a position scale that a user adds (design §4.2, requirement N-6): from data values to the scale space
/// and back, a span at a time. Make a scale of it with <see cref="Scale.Transform(IScale)"/>.
/// </summary>
/// <remarks>
/// The transform must be strictly increasing, and <see cref="Inverse"/> must undo <see cref="Forward"/>. The compile calls it
/// only on the thread that renders, a span at a time for M4's output points, the pixel boundaries, and the elements of a
/// segment or a rect layer, on each render that extracts or records again; for a learned domain's ends on each extraction;
/// and for a given domain's ends once each new scale node (and in
/// <see cref="TransformScaleSpec.WithDomain(double, double)"/>). It calls it one value at a time, at most 4W times a layer
/// for a panel W pixels wide (4H for horizontal rules), for a step's middle turns, a bar's or an error bar's elements, a
/// rule's lines, and an area's or a bar's Baseline, and for each value an <see cref="IGeom"/> maps through
/// <see cref="GeomFrame"/> (design §4.2, §5.5.3). Keep work that is done once out of it, and make a call of one value
/// cheap. An exception it throws comes out of the render as it is. A transform that is not monotone is
/// not checked, and its line is not promised. A value it maps to a number that is not finite is dropped and breaks the line.
/// </remarks>
public interface IScale
{
    /// <summary>Maps data values to the scale space.</summary>
    /// <param name="values">The data values.</param>
    /// <param name="destination">Where the mapped values go: as long as <paramref name="values"/>.</param>
    void Forward(ReadOnlySpan<double> values, Span<double> destination);

    /// <summary>Maps values of the scale space back to data values.</summary>
    /// <param name="values">The values of the scale space.</param>
    /// <param name="destination">Where the data values go: as long as <paramref name="values"/>.</param>
    void Inverse(ReadOnlySpan<double> values, Span<double> destination);
}
