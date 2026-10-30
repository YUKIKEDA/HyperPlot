using System.Runtime.CompilerServices;

namespace HyperPlot;

/// <summary>
/// The cartesian transform of a panel (design §4.4, row G4): from a point's differences from the origins of the scale space
/// to the target's physical pixels, with the origins it is taken from. It is the 4×4 transform written as its factors on
/// each axis (a move by the domain's lower end, a scale from the domain's width to the panel's, a flip, a move to the
/// panel's edge), which it applies in the order of the formula of rows G1 to G3: a product of the factors rounds
/// differently and would move pixels. Clamp is applied to a difference before it is mapped. The public contract
/// (<c>CoordSpec</c>) comes in row G10, and the GPU applies the transform from row R3 (<c>SetTransform</c>).
/// </summary>
internal readonly struct CartesianTransform
{
    private readonly Factors _x;
    private readonly Factors _y;

    private CartesianTransform(Factors x, Factors y, long xOrigin, long yOrigin) => (_x, _y, XOrigin, YOrigin) = (x, y, xOrigin, yOrigin);

    /// <summary>Gets the origin of x in the scale space (design §3.6).</summary>
    public long XOrigin { get; }

    /// <summary>Gets the origin of y in the scale space.</summary>
    public long YOrigin { get; }

    /// <summary>
    /// Makes the transform of a panel from its axes: x runs left to right and y bottom to top over the domains' differences,
    /// or the other way when reversed.
    /// </summary>
    public static CartesianTransform Create(in GeomFrame.Axis x, in GeomFrame.Axis y) =>
        new(Factors.Of(x, flip: x.Reverse), Factors.Of(y, flip: !y.Reverse), x.Domain.Origin, y.Domain.Origin);

    /// <summary>Returns the pixel x of a difference dx from <see cref="XOrigin"/>.</summary>
    public double MapX(double dx) => _x.Map(dx);

    /// <summary>Returns the pixel y of a difference dy from <see cref="YOrigin"/>.</summary>
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

    /// <summary>One axis's factors: t = (d − lower) / width · size, then offset + t, or offset + size − t when flipped.</summary>
    private readonly record struct Factors(double Lower, double Width, int Size, int Offset, bool Flip)
    {
        public static Factors Of(in GeomFrame.Axis axis, bool flip) =>
            new(axis.Domain.Lower, axis.Domain.Upper - axis.Domain.Lower, axis.Size, axis.Offset, flip);

        // Inlined into the span loop: a call an element was a fifth of a segment layer's frame (docs/benchmarks/g4-geoms.md).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double Map(double d)
        {
            var t = (d - Lower) / Width * Size;
            return Offset + (Flip ? Size - t : t);
        }
    }
}
