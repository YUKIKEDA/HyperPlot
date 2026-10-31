using System.Numerics;
using HyperPlot.Rendering;

namespace HyperPlot;

/// <summary>
/// The polar transform of a panel (design §4.4, row G11): from the stretched space, angle pixels a from 0 to L along the
/// outer circle and radius pixels c from 0 to R − r0, to the target's physical pixels. θ = σ · (Start + a / L · 2π), with σ
/// 1 clockwise and −1 otherwise, measured from 12 o'clock clockwise on the target, and r = r0 + c; the point is
/// center + r · (sin θ, −cos θ). Layers compute along × across as in cartesian (x pixels along, y pixels across); the angle
/// is x when theta is x, and y otherwise.
/// </summary>
internal readonly struct PolarTransform
{
    /// <summary>The largest deviation of a piece of a line from the curve it stands for, in physical pixels (design §4.4).</summary>
    public const double Tolerance = 0.25;

    /// <summary>
    /// The most pieces one segment is cut into. A segment inside the panel's domains needs a few hundred at most (a circle of
    /// radius 540 takes 104); this bounds a segment to a value far outside a domain that is not clamped.
    /// </summary>
    public const int MaxPieces = 8192;

    private readonly double _sigma;
    private readonly double _start;
    private readonly double _turn;

    /// <summary>Initializes a new instance of the <see cref="PolarTransform"/> struct.</summary>
    /// <param name="centerX">The center's x, in physical pixels.</param>
    /// <param name="centerY">The center's y.</param>
    /// <param name="radius">The outer radius R, in whole pixels.</param>
    /// <param name="inner">The inner radius r0 = ⌊InnerRadius · R⌋.</param>
    /// <param name="spec">The coordinate system.</param>
    public PolarTransform(double centerX, double centerY, int radius, int inner, PolarCoordSpec spec)
    {
        (CenterX, CenterY, Radius, Inner) = (centerX, centerY, radius, inner);
        Circumference = Turn(radius);
        ThetaIsX = spec.Theta == PolarTheta.X;
        _sigma = spec.Clockwise ? 1 : -1;
        _start = spec.Start;
        _turn = 2 * Math.PI / Circumference;
    }

    /// <summary>Gets the center's x.</summary>
    public double CenterX { get; }

    /// <summary>Gets the center's y.</summary>
    public double CenterY { get; }

    /// <summary>Gets the outer radius R.</summary>
    public int Radius { get; }

    /// <summary>Gets the inner radius r0.</summary>
    public int Inner { get; }

    /// <summary>Gets the length L of the outer circle in whole pixels: ⌈2πR⌉, the angle pixels.</summary>
    public int Circumference { get; }

    /// <summary>Gets a value indicating whether x is the angle; otherwise y is.</summary>
    public bool ThetaIsX { get; }

    /// <summary>
    /// Gets the widest span of angle pixels a trapezoid keeps: a hair under L, so that a full turn stays within 2π once
    /// rounded to float, where <see cref="RenderList.DrawSectors"/> drops a wider one.
    /// </summary>
    private double FullTurn => Circumference * (1 - 1e-6);

    /// <summary>Gets the number of pixels along the x axis: L when x is the angle, R − r0 otherwise.</summary>
    public int AlongSize => ThetaIsX ? Circumference : Radius - Inner;

    /// <summary>Gets the number of pixels along the y axis.</summary>
    public int AcrossSize => ThetaIsX ? Radius - Inner : Circumference;

    /// <summary>Returns ⌈2πR⌉.</summary>
    public static int Turn(int radius) => (int)Math.Ceiling(2 * Math.PI * radius);

    /// <summary>Returns the angle of an angle pixel, in radians from 12 o'clock clockwise on the target.</summary>
    public double Theta(double a) => _sigma * (_start + (a * _turn));

    /// <summary>Returns the radius of a radius pixel.</summary>
    public double RadiusOf(double c) => Inner + c;

    /// <summary>Returns the target's point of an angle pixel and a radius pixel.</summary>
    public Vector2 Point(double a, double c)
    {
        var (theta, r) = (Theta(a), RadiusOf(c));
        return new Vector2((float)(CenterX + (r * Math.Sin(theta))), (float)(CenterY - (r * Math.Cos(theta))));
    }

    /// <summary>Returns the target's point of an along × across point (x pixel, y pixel).</summary>
    public Vector2 Map(double along, double across) => ThetaIsX ? Point(along, across) : Point(across, along);

    /// <summary>
    /// Returns the number of pieces a segment from (a0, c0) to (a1, c1) is cut into (design §4.4): n = max(1,
    /// ⌈√((2|Δr||Δθ| + r Δθ²) / (8ε))⌉), r the larger radius of the ends, so that each piece's chord lies within ε of the
    /// curve along which the angle and the radius change linearly; at most <see cref="MaxPieces"/>.
    /// </summary>
    public int Pieces(double a0, double c0, double a1, double c1)
    {
        var dTheta = Math.Abs(a1 - a0) * _turn;
        var dRadius = Math.Abs(c1 - c0);
        var r = Math.Max(Math.Abs(RadiusOf(c0)), Math.Abs(RadiusOf(c1)));
        var n = Math.Ceiling(Math.Sqrt(((2 * dRadius * dTheta) + (r * dTheta * dTheta)) / (8 * Tolerance)));
        return n >= MaxPieces ? MaxPieces : Math.Max(1, (int)n);
    }

    /// <summary>
    /// Writes a polyline of along × across points as target points, each segment cut into <see cref="Pieces"/> pieces of
    /// equal steps in the stretched space; a point that is not finite breaks the line and is written as NaN. Grows the output
    /// to twice its size when it is short, and returns the number of points written.
    /// </summary>
    public int Polyline(ReadOnlySpan<Vector2> points, ref Vector2[] output)
    {
        var count = 0;
        var (lastA, lastC, has) = (0.0, 0.0, false);
        foreach (var point in points)
        {
            var (a, c) = ThetaIsX ? ((double)point.X, (double)point.Y) : (point.Y, point.X);
            if (!double.IsFinite(a) || !double.IsFinite(c))
            {
                Put(ref output, ref count, new Vector2(float.NaN));
                has = false;
                continue;
            }

            if (has)
            {
                var n = Pieces(lastA, lastC, a, c);
                for (var k = 1; k < n; k++)
                {
                    var t = (double)k / n;
                    Put(ref output, ref count, Point(lastA + ((a - lastA) * t), lastC + ((c - lastC) * t)));
                }
            }

            Put(ref output, ref count, Point(a, c));
            (lastA, lastC, has) = (a, c, true);
        }

        return count;
    }

    /// <summary>
    /// Writes the target points of the curve from (a0, c0) to (a1, c1) in angle and radius pixels, cut as a polyline's
    /// segment is (an arc of the grid or of an axis, design §4.6), and returns the number of points.
    /// </summary>
    public int Curve(double a0, double c0, double a1, double c1, ref Vector2[] output)
    {
        var count = 0;
        var n = Pieces(a0, c0, a1, c1);
        for (var k = 0; k <= n; k++)
        {
            var t = (double)k / n;
            Put(ref output, ref count, Point(a0 + ((a1 - a0) * t), c0 + ((c1 - c0) * t)));
        }

        return count;
    }

    /// <summary>
    /// Writes the polar trapezoid of a rectangle of the stretched space between two x pixels and two y pixels, in either
    /// order; returns false when a value is not finite. An angle span past a full turn is cut to one.
    /// </summary>
    public bool Rect(double x0, double x1, double y0, double y1, out SectorTrapezoid sector)
    {
        sector = default;
        if (!double.IsFinite(x0) || !double.IsFinite(x1) || !double.IsFinite(y0) || !double.IsFinite(y1))
        {
            return false;
        }

        var (a0, a1, c0, c1) = ThetaIsX ? (x0, x1, y0, y1) : (y0, y1, x0, x1);
        (a0, a1) = (Math.Min(a0, a1), Math.Max(a0, a1));
        (c0, c1) = (Math.Min(c0, c1), Math.Max(c0, c1));
        sector = Sector(a0, Math.Min(a1, a0 + FullTurn), c1, c1, c0, c0);
        return true;
    }

    /// <summary>
    /// Writes the polar trapezoids of a column trapezoid of the stretched space (sides along x, edges in y) and returns how
    /// many: one when x is the angle; when y is, the region cut where its edges cross and then into slabs between its
    /// corners' angles, whose radii run linearly in the angle (at most 6).
    /// </summary>
    public int Columns(in ColumnTrapezoid column, Span<SectorTrapezoid> output)
    {
        if (ThetaIsX)
        {
            output[0] = Sector(
                column.Left,
                Math.Min(column.Right, column.Left + FullTurn),
                column.TopLeft,
                column.TopRight,
                column.BottomLeft,
                column.BottomRight
            );
            return 1;
        }

        // The edges as functions of the radius pixel; split where they cross, so that each part has one edge below.
        var (c0, c1) = ((double)column.Left, (double)column.Right);
        var (e0, e1, f0, f1) = ((double)column.TopLeft, (double)column.TopRight, (double)column.BottomLeft, (double)column.BottomRight);
        var (d0, d1) = (e0 - f0, e1 - f1);
        if ((d0 < 0 && d1 > 0) || (d0 > 0 && d1 < 0))
        {
            var t = d0 / (d0 - d1);
            var (cm, em) = (c0 + ((c1 - c0) * t), e0 + ((e1 - e0) * t));
            var count = Part(c0, cm, Math.Min(e0, f0), em, Math.Max(e0, f0), em, output);
            return count + Part(cm, c1, em, Math.Min(e1, f1), em, Math.Max(e1, f1), output[count..]);
        }

        return d0 + d1 <= 0 ? Part(c0, c1, e0, e1, f0, f1, output) : Part(c0, c1, f0, f1, e0, e1, output);
    }

    private static void Put(ref Vector2[] output, ref int count, Vector2 point)
    {
        if (count == output.Length)
        {
            Array.Resize(ref output, output.Length * 2);
        }

        output[count++] = point;
    }

    /// <summary>
    /// Writes the slabs of a region of radius pixels from c0 to c1 and angle pixels from a low edge (l0 to l1) to a high one
    /// (h0 to h1), each linear in c, and returns how many: between each two of the corners' angles, the radius range at an
    /// angle is bounded by one side or edge each way, so it runs linearly in the angle.
    /// </summary>
    private int Part(double c0, double c1, double l0, double l1, double h0, double h1, Span<SectorTrapezoid> output)
    {
        if (!(c1 > c0))
        {
            return 0;
        }

        Span<double> corners = [l0, l1, h0, h1];
        corners.Sort();
        var count = 0;
        for (var i = 0; i < 3; i++)
        {
            var (a0, a1) = (corners[i], corners[i + 1]);
            if (!(a1 > a0))
            {
                continue;
            }

            var (low0, high0) = Range(a0);
            var (low1, high1) = Range(a1);
            output[count++] = Sector(a0, a1, high0, high1, low0, low1);
        }

        return count;

        // The radius pixels at an angle pixel inside the region: [c0, c1] where l(c) ≤ a ≤ h(c).
        (double Low, double High) Range(double a)
        {
            var (low, high) = (c0, c1);
            Bound(l0, l1, a, below: true, ref low, ref high);
            Bound(h0, h1, a, below: false, ref low, ref high);
            return (low, Math.Max(low, high));
        }

        // Keeps the c where the edge e(c), e0 at c0 and e1 at c1, is at most a (below) or at least a.
        void Bound(double e0, double e1, double a, bool below, ref double low, ref double high)
        {
            if (e0 == e1)
            {
                return;
            }

            var at = c0 + ((a - e0) / (e1 - e0) * (c1 - c0));
            if ((e1 > e0) == below)
            {
                high = Math.Min(high, at);
            }
            else
            {
                low = Math.Max(low, at);
            }
        }
    }

    /// <summary>The polar trapezoid over angle pixels a0 to a1 (a0 ≤ a1) with its outer and inner radius pixels at each end.</summary>
    private SectorTrapezoid Sector(double a0, double a1, double outer0, double outer1, double inner0, double inner1)
    {
        var (from, to) = (Theta(a0), Theta(a1));
        var (r0, r1, s0, s1) = (RadiusOf(outer0), RadiusOf(outer1), RadiusOf(inner0), RadiusOf(inner1));
        return _sigma > 0
            ? new SectorTrapezoid((float)from, (float)to, (float)r0, (float)r1, (float)s0, (float)s1)
            : new SectorTrapezoid((float)to, (float)from, (float)r1, (float)r0, (float)s1, (float)s0);
    }
}
