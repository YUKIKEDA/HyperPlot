using System.Runtime.CompilerServices;

namespace HyperPlot;

/// <summary>
/// The cartesian transform of a panel (design §4.4, row G4): from a point's differences from the domains' origins in the
/// scale space to the target's physical pixels; the origins come with the domains (row R3 adds them when it hands the
/// transform to the GPU). It is the 4×4 transform written as its factors on
/// each axis (a move by the domain's lower end, a scale from the domain's width to the panel's, a reversal, a move to the
/// panel's edge), which it applies in the order of the formula of rows G1 to G3: a product of the factors rounds
/// differently and would move pixels. Clamp is applied to a difference before it is mapped. Each axis maps to the pixels
/// along its own side of the panel: with flip (row G10), x to the vertical pixels and y to the horizontal ones, and the
/// record swaps them back. The public 4×4 contract and the GPU's use of it come in row R3 (<c>SetTransform</c>).
/// </summary>
internal readonly struct CartesianTransform
{
    private readonly Factors _x;
    private readonly Factors _y;

    private CartesianTransform(Factors x, Factors y) => (_x, _y) = (x, y);

    /// <summary>
    /// Makes the transform of a panel from its axes: each runs over its span of the panel, toward lower pixels when it is
    /// <see cref="GeomFrame.Axis.Descending"/>.
    /// </summary>
    public static CartesianTransform Create(in GeomFrame.Axis x, in GeomFrame.Axis y) =>
        new(Factors.Of(x, x.Descending), Factors.Of(y, y.Descending));

    /// <summary>Returns the pixel along the x axis of a difference dx from the x domain's origin.</summary>
    public double MapX(double dx) => _x.Map(dx);

    /// <summary>Returns the pixel along the y axis of a difference dy from the y domain's origin.</summary>
    public double MapY(double dy) => _y.Map(dy);

    /// <summary>
    /// Maps a span of values on one axis to pixels, the same as the frame maps one at a time: each less
    /// <paramref name="shift"/> (the origin, for values a scale's transform mapped), NaN when not finite, clamped to the
    /// axis's domain when it clamps. <paramref name="pixels"/> may be <paramref name="values"/>.
    /// </summary>
    public void Map(in GeomFrame.Axis axis, bool ofX, ReadOnlySpan<double> values, Span<double> pixels, double shift)
    {
        var factors = ofX ? _x : _y;
        var (lower, upper, clamp) = (axis.Domain.Lower, axis.Domain.Upper, axis.Clamp);
        for (var i = 0; i < values.Length; i++)
        {
            var d = values[i] - shift;
            pixels[i] = !double.IsFinite(d) ? double.NaN : factors.Map(clamp ? Math.Clamp(d, lower, upper) : d);
        }
    }

    /// <summary>One axis's factors: t = (d − lower) / width · size, then offset + t, or offset + size − t when descending.</summary>
    private readonly record struct Factors(double Lower, double Width, int Size, int Offset, bool Descending)
    {
        public static Factors Of(in GeomFrame.Axis axis, bool descending) =>
            new(axis.Domain.Lower, axis.Domain.Upper - axis.Domain.Lower, axis.Size, axis.Offset, descending);

        // Inlined into the span loop: a call an element was a fifth of a segment layer's frame (docs/benchmarks/g4-geoms.md).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double Map(double d)
        {
            var t = (d - Lower) / Width * Size;
            return Offset + (Descending ? Size - t : t);
        }
    }
}
