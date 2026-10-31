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
    /// <param name="list">
    /// The list to record into: the plot's own, which already holds the panel, the grid, and the layers before this one. Do
    /// not call <see cref="RenderList.Clear"/> on it, which removes them all.
    /// </param>
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
/// <see cref="PixelX(double)"/> and <see cref="PixelY(double)"/> return the pixel along each data axis, which a flipped
/// coordinate system puts on the other side (<see cref="Flipped"/>) and a polar one in its stretched space
/// (<see cref="Polar"/>); <see cref="Map(double, double, out double, out double)"/> returns a point of the target.
/// </summary>
public sealed class GeomFrame
{
    private readonly double[] _one = new double[1];
    private readonly double[] _mapped = new double[1];
    private Axis _x;
    private Axis _y;
    private CartesianTransform _transform;
    private PolarTransform _polar;
    private (int Left, int Top, int Width, int Height) _panel;
    private float _scale;
    private bool _flipped;
    private bool _isPolar;
    private Color _series;
    private bool _active;

    internal GeomFrame() { }

    /// <summary>Gets the panel's left edge, in whole physical pixels, whatever the coordinate system.</summary>
    /// <exception cref="InvalidOperationException">Used outside <see cref="IGeom.Record"/>.</exception>
    public int Left => Active()._panel.Left;

    /// <summary>Gets the panel's top edge.</summary>
    /// <exception cref="InvalidOperationException">Used outside <see cref="IGeom.Record"/>.</exception>
    public int Top => Active()._panel.Top;

    /// <summary>Gets the panel's width.</summary>
    /// <exception cref="InvalidOperationException">Used outside <see cref="IGeom.Record"/>.</exception>
    public int Width => Active()._panel.Width;

    /// <summary>Gets the panel's height.</summary>
    /// <exception cref="InvalidOperationException">Used outside <see cref="IGeom.Record"/>.</exception>
    public int Height => Active()._panel.Height;

    /// <summary>
    /// Gets a value indicating whether the coordinate system is flipped (design §4.4, row G10): then
    /// <see cref="PixelX(double)"/> returns the target's vertical pixel, and <see cref="PixelY(double)"/> the horizontal one.
    /// </summary>
    /// <exception cref="InvalidOperationException">Used outside <see cref="IGeom.Record"/>.</exception>
    public bool Flipped => Active()._flipped;

    /// <summary>
    /// Gets a value indicating whether the coordinate system is polar (design §4.4, row G11): then <see cref="PixelX(double)"/>
    /// and <see cref="PixelY(double)"/> return pixels of the stretched space (the angle along the outer circle and the
    /// radius out from the inner one, for whichever data axis each is), and <see cref="Map(double, double, out double, out double)"/>
    /// returns the target's point. A straight line between two mapped points is a chord; a geom that wants the curve cuts it.
    /// </summary>
    /// <exception cref="InvalidOperationException">Used outside <see cref="IGeom.Record"/>.</exception>
    public bool Polar => Active()._isPolar;

    /// <summary>
    /// Gets the layer's series color (design §4.1, row G7a): the theme's palette at the layer's position, the color the
    /// built-in geoms take when the spec gives none. The legend draws a user's geom's sample in it.
    /// </summary>
    /// <exception cref="InvalidOperationException">Used outside <see cref="IGeom.Record"/>.</exception>
    public Color SeriesColor => Active()._series;

    /// <summary>Gets the scale from DIPs to physical pixels.</summary>
    /// <exception cref="InvalidOperationException">Used outside <see cref="IGeom.Record"/>.</exception>
    public float Scale => Active()._scale;

    /// <summary>
    /// Returns the target's pixel along the x axis of an x value: horizontal, or vertical when <see cref="Flipped"/>. NaN
    /// when the scale maps the value to one that is not finite.
    /// </summary>
    /// <param name="value">The value, in data units.</param>
    /// <returns>The pixel.</returns>
    /// <exception cref="InvalidOperationException">Used outside <see cref="IGeom.Record"/>.</exception>
    public double PixelX(double value) => Along(Active()._x, value, ofX: true);

    /// <summary>
    /// Returns the target's pixel along the y axis of a y value: vertical, or horizontal when <see cref="Flipped"/>. NaN
    /// when the scale maps the value to one that is not finite.
    /// </summary>
    /// <param name="value">The value, in data units.</param>
    /// <returns>The pixel.</returns>
    /// <exception cref="InvalidOperationException">Used outside <see cref="IGeom.Record"/>.</exception>
    public double PixelY(double value) => Along(Active()._y, value, ofX: false);

    /// <summary>
    /// Maps a data point to the target's point (horizontal, vertical), whatever the coordinate system:
    /// <see cref="PixelX(double)"/> and <see cref="PixelY(double)"/>, swapped when <see cref="Flipped"/>, and taken around
    /// the circle when <see cref="Polar"/>. A coordinate is NaN when it does not map.
    /// </summary>
    /// <param name="x">The x value, in data units.</param>
    /// <param name="y">The y value, in data units.</param>
    /// <param name="pixelX">The target's horizontal pixel.</param>
    /// <param name="pixelY">The target's vertical pixel.</param>
    /// <exception cref="InvalidOperationException">Used outside <see cref="IGeom.Record"/>.</exception>
    public void Map(double x, double y, out double pixelX, out double pixelY)
    {
        var alongX = PixelX(x);
        var alongY = PixelY(y);
        if (_isPolar)
        {
            var point = double.IsFinite(alongX) && double.IsFinite(alongY) ? _polar.Map(alongX, alongY) : new System.Numerics.Vector2(float.NaN);
            (pixelX, pixelY) = (point.X, point.Y);
            return;
        }

        pixelX = _flipped ? alongY : alongX;
        pixelY = _flipped ? alongX : alongY;
    }

    /// <summary>Returns the target's pixel along the x axis of a long x value, exactly from the domain's origin (design §3.6).</summary>
    /// <param name="value">The value, in data units.</param>
    /// <returns>The pixel.</returns>
    /// <exception cref="InvalidOperationException">Used outside <see cref="IGeom.Record"/>.</exception>
    public double PixelX(long value) => Along(Active()._x, value, ofX: true);

    /// <summary>Returns the target's pixel along the y axis of a long y value, exactly from the domain's origin.</summary>
    /// <param name="value">The value, in data units.</param>
    /// <returns>The pixel.</returns>
    /// <exception cref="InvalidOperationException">Used outside <see cref="IGeom.Record"/>.</exception>
    public double PixelY(long value) => Along(Active()._y, value, ofX: false);

    /// <summary>Opens the frame for one <see cref="IGeom.Record"/>.</summary>
    internal void Open(
        in Axis x,
        in Axis y,
        float scale,
        bool flipped,
        (int Left, int Top, int Width, int Height) panel,
        PolarTransform? polar,
        Color series
    )
    {
        _series = series;
        (_x, _y, _transform, _scale, _flipped, _active) = (x, y, CartesianTransform.Create(x, y), scale, flipped, true);
        (_panel, _isPolar, _polar) = (panel, polar.HasValue, polar.GetValueOrDefault());
    }

    /// <summary>Closes the frame after <see cref="IGeom.Record"/>.</summary>
    internal void Close() => _active = false;

    /// <summary>
    /// Maps a difference from an axis's origin, in the scale space, to the target's pixel through a panel's transform
    /// (design §4.4): clamped first when the scale clamps, NaN when it is not finite.
    /// </summary>
    internal static double Pixel(in CartesianTransform transform, in Axis axis, double d, bool ofX)
    {
        if (!double.IsFinite(d))
        {
            return double.NaN;
        }

        d = axis.Clamp ? Math.Clamp(d, axis.Domain.Lower, axis.Domain.Upper) : d;
        return ofX ? transform.MapX(d) : transform.MapY(d);
    }

    private double Pixel(in Axis axis, double d, bool ofX) => Pixel(_transform, axis, d, ofX);

    private double Along(in Axis axis, double value, bool ofX)
    {
        if (axis.Transform is null)
        {
            return Pixel(axis, value - axis.Domain.Origin, ofX);
        }

        _one[0] = value;
        axis.Transform.Forward(_one, _mapped);
        return Pixel(axis, _mapped[0] - axis.Domain.Origin, ofX);
    }

    private double Along(in Axis axis, long value, bool ofX) =>
        axis.Transform is null ? Pixel(axis, ScaleSpace.Difference(value, axis.Domain.Origin), ofX) : Along(axis, (double)value, ofX);

    private GeomFrame Active() =>
        _active ? this : throw new InvalidOperationException("A GeomFrame is used only inside IGeom.Record, while the plot records.");

    /// <summary>
    /// One axis of the frame: its scale's transform, domain, and clamp, and the panel's span along the side it runs on.
    /// <c>Descending</c> is whether its pixels decrease as its data increases: a vertical axis not reversed, or a horizontal
    /// one reversed. <c>Offset</c> and <c>Size</c> are the panel's left and width, or its top and height for a vertical axis.
    /// </summary>
    internal readonly record struct Axis(IScale? Transform, ScaleRange Domain, bool Descending, bool Clamp, int Offset, int Size);
}
