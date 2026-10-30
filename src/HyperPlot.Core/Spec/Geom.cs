using HyperPlot.Rendering;

namespace HyperPlot;

/// <summary>
/// A geom a user adds (design §4.1, §5.5.3, requirement N-6), through <see cref="CustomLayerSpec"/>. Called once a layer,
/// on the thread that renders, never once a point: <see cref="Learn"/> when the plot learns its domains, and
/// <see cref="Record"/> each time it records. An exception either throws comes out of the render as it is.
/// </summary>
/// <remarks>
/// The geom bounds what it records by the panel's pixels, as the built-in geoms do (requirement R-1, design §5.5.3): a
/// frame that sends the GPU an amount that grows with the data breaks the frame budget.
/// </remarks>
public interface IGeom
{
    /// <summary>Adds the ranges of the geom's values to the plot's domains; adding nothing leaves them as they are.</summary>
    /// <param name="ranges">The plot's ranges.</param>
    void Learn(GeomRanges ranges);

    /// <summary>
    /// Records the geom into the panel: the list is clipped to the panel, and <paramref name="frame"/> maps data values to
    /// the target's physical pixels. The geom may set its own clip; the plot sets the panel's back afterwards.
    /// </summary>
    /// <param name="frame">The panel and the mapping, usable inside this call only.</param>
    /// <param name="list">The list to record into.</param>
    void Record(GeomFrame frame, RenderList list);
}

/// <summary>
/// The ranges an <see cref="IGeom"/> adds while the plot learns its domains (design §4.2). The plot keeps one and reuses it.
/// x and y have a method each; 3D adds z the same way (design §11, invariant 12).
/// </summary>
public sealed class GeomRanges
{
    internal GeomRanges() { }

    internal ScaleEnd? XMin { get; private set; }

    internal ScaleEnd? XMax { get; private set; }

    internal ScaleEnd? YMin { get; private set; }

    internal ScaleEnd? YMax { get; private set; }

    /// <summary>Adds a range of x values.</summary>
    /// <param name="min">The least value: finite.</param>
    /// <param name="max">The greatest value: finite and at least <paramref name="min"/>.</param>
    /// <exception cref="ArgumentException">A value is not finite, or <paramref name="min"/> is above <paramref name="max"/>.</exception>
    public void AddX(double min, double max)
    {
        Check(min, max);
        (XMin, XMax) = Union(XMin, XMax, ScaleEnd.Of(min), ScaleEnd.Of(max));
    }

    /// <summary>Adds a range of y values.</summary>
    /// <param name="min">The least value: finite.</param>
    /// <param name="max">The greatest value: finite and at least <paramref name="min"/>.</param>
    /// <exception cref="ArgumentException">A value is not finite, or <paramref name="min"/> is above <paramref name="max"/>.</exception>
    public void AddY(double min, double max)
    {
        Check(min, max);
        (YMin, YMax) = Union(YMin, YMax, ScaleEnd.Of(min), ScaleEnd.Of(max));
    }

    /// <summary>Adds a range of long x values, exactly (a date-time column, design §3.6).</summary>
    /// <param name="min">The least value.</param>
    /// <param name="max">The greatest value: at least <paramref name="min"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="min"/> is above <paramref name="max"/>.</exception>
    public void AddX(long min, long max)
    {
        Check(min, max);
        (XMin, XMax) = Union(XMin, XMax, ScaleEnd.Of(min), ScaleEnd.Of(max));
    }

    /// <summary>Adds a range of long y values, exactly.</summary>
    /// <param name="min">The least value.</param>
    /// <param name="max">The greatest value: at least <paramref name="min"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="min"/> is above <paramref name="max"/>.</exception>
    public void AddY(long min, long max)
    {
        Check(min, max);
        (YMin, YMax) = Union(YMin, YMax, ScaleEnd.Of(min), ScaleEnd.Of(max));
    }

    /// <summary>Forgets the ranges, before the geom learns again.</summary>
    internal void Clear() => (XMin, XMax, YMin, YMax) = (null, null, null, null);

    private static void Check(double min, double max)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max) || min > max)
        {
            throw new ArgumentException($"A range is finite with its min at most its max; [{min}, {max}] is not.");
        }
    }

    private static void Check(long min, long max)
    {
        if (min > max)
        {
            throw new ArgumentException($"A range has its min at most its max; [{min}, {max}] is not.");
        }
    }

    private static (ScaleEnd Min, ScaleEnd Max) Union(ScaleEnd? min, ScaleEnd? max, ScaleEnd low, ScaleEnd high) =>
        (min is { } a && Less(a, low) ? a : low, max is { } b && Less(high, b) ? b : high);

    private static bool Less(ScaleEnd a, ScaleEnd b) => a.IsInteger && b.IsInteger ? a.Integer < b.Integer : a.Value < b.Value;
}

/// <summary>
/// The panel and the mapping an <see cref="IGeom"/> records with (design §4.4, §5.5.3): data values to the target's
/// physical pixels, through the scale's transform, clamp, and reverse, in double. The plot keeps one and reuses it; it
/// can be used only inside <see cref="IGeom.Record"/>. x and y have a method each; 3D adds z the same way (invariant 12).
/// </summary>
public sealed class GeomFrame
{
    private readonly double[] _one = new double[1];
    private readonly double[] _mapped = new double[1];
    private Axis _x;
    private Axis _y;
    private float _scale;
    private bool _active;

    internal GeomFrame() { }

    /// <summary>Gets the panel's left edge, in whole physical pixels.</summary>
    /// <exception cref="InvalidOperationException">Used outside <see cref="IGeom.Record"/>.</exception>
    public int Left => Active()._x.Offset;

    /// <summary>Gets the panel's top edge.</summary>
    /// <exception cref="InvalidOperationException">Used outside <see cref="IGeom.Record"/>.</exception>
    public int Top => Active()._y.Offset;

    /// <summary>Gets the panel's width.</summary>
    /// <exception cref="InvalidOperationException">Used outside <see cref="IGeom.Record"/>.</exception>
    public int Width => Active()._x.Size;

    /// <summary>Gets the panel's height.</summary>
    /// <exception cref="InvalidOperationException">Used outside <see cref="IGeom.Record"/>.</exception>
    public int Height => Active()._y.Size;

    /// <summary>Gets the scale from DIPs to physical pixels.</summary>
    /// <exception cref="InvalidOperationException">Used outside <see cref="IGeom.Record"/>.</exception>
    public float Scale => Active()._scale;

    /// <summary>Returns the target's x pixel of an x value, or NaN when the scale maps it to a value that is not finite.</summary>
    /// <param name="value">The value, in data units.</param>
    /// <returns>The pixel.</returns>
    /// <exception cref="InvalidOperationException">Used outside <see cref="IGeom.Record"/>.</exception>
    public double PixelX(double value) => Map(Active()._x, value, ofX: true);

    /// <summary>Returns the target's y pixel of a y value, or NaN when the scale maps it to a value that is not finite.</summary>
    /// <param name="value">The value, in data units.</param>
    /// <returns>The pixel.</returns>
    /// <exception cref="InvalidOperationException">Used outside <see cref="IGeom.Record"/>.</exception>
    public double PixelY(double value) => Map(Active()._y, value, ofX: false);

    /// <summary>Returns the target's x pixel of a long x value, exactly from the domain's origin (design §3.6).</summary>
    /// <param name="value">The value, in data units.</param>
    /// <returns>The pixel.</returns>
    /// <exception cref="InvalidOperationException">Used outside <see cref="IGeom.Record"/>.</exception>
    public double PixelX(long value) => Map(Active()._x, value, ofX: true);

    /// <summary>Returns the target's y pixel of a long y value, exactly from the domain's origin.</summary>
    /// <param name="value">The value, in data units.</param>
    /// <returns>The pixel.</returns>
    /// <exception cref="InvalidOperationException">Used outside <see cref="IGeom.Record"/>.</exception>
    public double PixelY(long value) => Map(Active()._y, value, ofX: false);

    /// <summary>Opens the frame for one <see cref="IGeom.Record"/>.</summary>
    internal void Open(in Axis x, in Axis y, float scale)
    {
        (_x, _y, _scale, _active) = (x, y, scale, true);
    }

    /// <summary>Closes the frame after <see cref="IGeom.Record"/>.</summary>
    internal void Close() => _active = false;

    /// <summary>Maps a difference from an axis's origin, in the scale space, to the target's pixel (design §4.4).</summary>
    internal static double Pixel(in Axis axis, double d, bool ofX)
    {
        if (!double.IsFinite(d))
        {
            return double.NaN;
        }

        var domain = axis.Domain;
        d = axis.Clamp ? Math.Clamp(d, domain.Lower, domain.Upper) : d;
        var t = (d - domain.Lower) / (domain.Upper - domain.Lower) * axis.Size;
        return axis.Offset + (ofX == axis.Reverse ? axis.Size - t : t);
    }

    private double Map(in Axis axis, double value, bool ofX)
    {
        if (axis.Transform is null)
        {
            return Pixel(axis, value - axis.Domain.Origin, ofX);
        }

        _one[0] = value;
        axis.Transform.Forward(_one, _mapped);
        return Pixel(axis, _mapped[0] - axis.Domain.Origin, ofX);
    }

    private double Map(in Axis axis, long value, bool ofX) =>
        axis.Transform is null ? Pixel(axis, ScaleSpace.Difference(value, axis.Domain.Origin), ofX) : Map(axis, (double)value, ofX);

    private GeomFrame Active() =>
        _active ? this : throw new InvalidOperationException("A GeomFrame is used only inside IGeom.Record, while the plot records.");

    /// <summary>One axis of the frame: its scale's transform, domain, options, and the panel's span along it.</summary>
    internal readonly record struct Axis(IScale? Transform, ScaleRange Domain, bool Reverse, bool Clamp, int Offset, int Size);
}
