using System.Numerics;

namespace HyperPlot.Rendering.D3D11.Tests;

/// <summary>
/// The CPU reference of the polylines that are not round (design §5.1, §5.3, row R4), written apart from the shaders and from
/// <see cref="RenderList"/>'s pieces: it walks each run as one path, cuts it into the dashes of SVG 2 §13.5.7, and makes the
/// stroke's shapes in double (a box for each dash's part of each segment, a cap at each end of each dash, a join at each
/// point strictly inside a dash, a dot for each dash of length 0 and each run of length 0). Each pixel's coverage is the
/// count of its 32 samples (design §5.3) inside any shape, / 32; the color goes over the image premultiplied.
/// </summary>
internal static class JoinReference
{
    /// <summary>Gets the 32 samples of a pixel: x = (i + 0.5) / 32, y = ((13 i mod 32) + 0.5) / 32.</summary>
    internal static readonly Vector2[] Samples =
    [
        .. Enumerable.Range(0, 32).Select(i => new Vector2((i + 0.5f) / 32, (((13 * i) & 31) + 0.5f) / 32)),
    ];

    /// <summary>Draws the polylines in order over an opaque image of RGBA bytes.</summary>
    internal static void Draw(byte[] image, int width, int height, IEnumerable<(Vector2[] Points, LineStyle Style)> polylines)
    {
        foreach (var (points, style) in polylines)
        {
            if (style.Width == 0)
            {
                continue;
            }

            var shapes = Shapes(points, style);
            var a = style.Color.A;
            double[] source = [Multiply(style.Color.R, a), Multiply(style.Color.G, a), Multiply(style.Color.B, a), a];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var count = 0;
                    foreach (var sample in Samples)
                    {
                        var q = new Vector2d(x + sample.X, y + sample.Y);
                        if (shapes.Exists(shape => shape(q)))
                        {
                            count++;
                        }
                    }

                    if (count == 0)
                    {
                        continue;
                    }

                    var coverage = count / 32.0;
                    var index = ((y * width) + x) * 4;
                    var alpha = source[3] / 255 * coverage;
                    for (var channel = 0; channel < 4; channel++)
                    {
                        var value = (source[channel] * coverage) + (image[index + channel] * (1 - alpha));
                        image[index + channel] = (byte)Math.Round(value);
                    }
                }
            }
        }
    }

    /// <summary>The shapes of a stroke, each a test of whether a point is inside it.</summary>
    internal static List<Func<Vector2d, bool>> Shapes(Vector2[] points, LineStyle style)
    {
        var shapes = new List<Func<Vector2d, bool>>();
        var r = style.Width / 2.0;
        foreach (var run in Runs(points))
        {
            if (run.Count < 2)
            {
                continue;
            }

            // The length along the run at each point, in float as RenderList sums it, and the segments with a length.
            var at = new double[run.Count];
            var along = 0f;
            for (var i = 1; i < run.Count; i++)
            {
                along += Vector2.Distance(run[i - 1], run[i]);
                at[i] = along;
            }

            var total = at[^1];
            if (total == 0)
            {
                // A run of length 0: its direction is the positive x axis (SVG 2 §9.4).
                if (Drawn(style, 0, 0, total))
                {
                    AddDot(shapes, ToD(run[0]), new Vector2d(1, 0), r, style.Cap);
                }

                continue;
            }

            foreach (var (start, end) in Dashes(style, total))
            {
                AddDash(shapes, run, at, start, end, r, style);
            }
        }

        return shapes;
    }

    /// <summary>The dashes along a path of a length: [start, end] clipped to it, in SVG 2 §13.5.7's way; the whole path when solid.</summary>
    private static IEnumerable<(double Start, double End)> Dashes(LineStyle style, double total)
    {
        if (style.Dash is not { } dash)
        {
            yield return (0, total);
            yield break;
        }

        var period = (double)dash.Period;
        for (var k = (long)Math.Floor(dash.Offset / period) - 1; (k * period) - dash.Offset <= total; k++)
        {
            var start = (k * period) - dash.Offset;
            for (var i = 0; i < dash.Count; i += 2)
            {
                var from = start;
                var to = start + dash[i];
                start = to + dash[i + 1];
                if (to < 0 || from > total || (to == 0 && from < 0) || (from == total && to > total))
                {
                    continue;
                }

                yield return (Math.Max(from, 0), Math.Min(to, total));
            }
        }
    }

    private static bool Drawn(LineStyle style, double start, double end, double total) =>
        style.Dash is not { } ? true : Dashes(style, Math.Max(total, 1e-9)).Any(d => d.Start <= start && d.End >= end);

    private static void AddDash(
        List<Func<Vector2d, bool>> shapes,
        List<Vector2> run,
        double[] at,
        double start,
        double end,
        double r,
        LineStyle style
    )
    {
        if (end == start)
        {
            var (point, direction) = PointAt(run, at, start);
            AddDot(shapes, point, direction, r, style.Cap);
            return;
        }

        // A box for the dash's part of each segment with a length.
        for (var i = 1; i < run.Count; i++)
        {
            var from = Math.Max(start, at[i - 1]);
            var to = Math.Min(end, at[i]);
            if (!(to > from))
            {
                continue;
            }

            var p0 = ToD(run[i - 1]);
            var d = (ToD(run[i]) - p0).Normalized();
            var a0 = from - at[i - 1];
            var a1 = to - at[i - 1];
            shapes.Add(q => InBox(q - p0, d, a0, a1, r));
        }

        // The caps at the dash's ends, along the segment the dash leaves by.
        var (startPoint, startDirection) = PointAt(run, at, start, after: true);
        var (endPoint, endDirection) = PointAt(run, at, end, after: false);
        AddCap(shapes, startPoint, -startDirection, r, style.Cap);
        AddCap(shapes, endPoint, endDirection, r, style.Cap);

        // The joins at the points strictly inside the dash.
        for (var i = 1; i < run.Count - 1; i++)
        {
            if (!(at[i] > start && at[i] < end))
            {
                continue;
            }

            var incoming = Direction(run, i, backward: true);
            var outgoing = Direction(run, i, backward: false);
            if (incoming is { } din && outgoing is { } dout)
            {
                AddJoin(shapes, ToD(run[i]), din, dout, r, style);
            }
        }
    }

    private static void AddJoin(List<Func<Vector2d, bool>> shapes, Vector2d p, Vector2d din, Vector2d dout, double r, LineStyle style)
    {
        if (style.Join == LineJoin.Round)
        {
            shapes.Add(q => (q - p).LengthSquared() <= r * r);
            return;
        }

        var turn = din.Cross(dout);
        if (turn == 0)
        {
            return;
        }

        var sign = turn > 0 ? -1 : 1;
        var a = new Vector2d(-din.Y, din.X) * (sign * r);
        var b = new Vector2d(-dout.Y, dout.X) * (sign * r);
        var cosHalf = Math.Sqrt(Math.Max(0, (1 + din.Dot(dout)) / 2));
        if (style.Join == LineJoin.Miter && cosHalf * style.MiterLimit >= 1)
        {
            var sum = a + b;
            var m = sum * (2 * r * r / sum.LengthSquared());
            shapes.Add(q => InConvex(q - p, [new Vector2d(0, 0), a, m, b]));
        }
        else
        {
            shapes.Add(q => InConvex(q - p, [new Vector2d(0, 0), a, b]));
        }
    }

    private static void AddCap(List<Func<Vector2d, bool>> shapes, Vector2d p, Vector2d outward, double r, LineCap cap)
    {
        if (cap == LineCap.Round)
        {
            shapes.Add(q => (q - p).LengthSquared() <= r * r);
        }
        else if (cap == LineCap.Square)
        {
            shapes.Add(q => InBox(q - p, outward, 0, r, r));
        }
    }

    private static void AddDot(List<Func<Vector2d, bool>> shapes, Vector2d p, Vector2d direction, double r, LineCap cap)
    {
        if (cap == LineCap.Round)
        {
            shapes.Add(q => (q - p).LengthSquared() <= r * r);
        }
        else if (cap == LineCap.Square)
        {
            shapes.Add(q => InBox(q - p, direction, -r, r, r));
        }
    }

    private static bool InBox(Vector2d q, Vector2d d, double from, double to, double r)
    {
        var a = q.Dot(d);
        var c = q.Dot(new Vector2d(-d.Y, d.X));
        return a >= from && a <= to && Math.Abs(c) <= r;
    }

    private static bool InConvex(Vector2d q, Vector2d[] corners)
    {
        var (positive, negative) = (true, true);
        for (var i = 0; i < corners.Length; i++)
        {
            var e = (corners[(i + 1) % corners.Length] - corners[i]).Cross(q - corners[i]);
            positive &= e >= 0;
            negative &= e <= 0;
        }

        return positive || negative;
    }

    /// <summary>The point at a length along the run, and the direction there: of the segment after it, or before it.</summary>
    private static (Vector2d Point, Vector2d Direction) PointAt(List<Vector2> run, double[] at, double s, bool after = true)
    {
        for (var i = 1; i < run.Count; i++)
        {
            var length = at[i] - at[i - 1];
            var inside = after ? s >= at[i - 1] && s < at[i] : s > at[i - 1] && s <= at[i];
            if (length > 0 && inside)
            {
                var p0 = ToD(run[i - 1]);
                var d = (ToD(run[i]) - p0).Normalized();
                return (p0 + (d * (s - at[i - 1])), d);
            }
        }

        // At the run's very end (after) or start (before): the nearest segment with a length.
        var last = after ? Direction(run, run.Count - 1, backward: true) : Direction(run, 0, backward: false);
        return (ToD(after ? run[^1] : run[0]), last ?? new Vector2d(1, 0));
    }

    /// <summary>The direction of the nearest segment with a length before (backward) or after a point.</summary>
    private static Vector2d? Direction(List<Vector2> run, int i, bool backward)
    {
        if (backward)
        {
            for (var j = i; j > 0; j--)
            {
                if (run[j] != run[j - 1])
                {
                    return (ToD(run[j]) - ToD(run[j - 1])).Normalized();
                }
            }
        }
        else
        {
            for (var j = i; j < run.Count - 1; j++)
            {
                if (run[j] != run[j + 1])
                {
                    return (ToD(run[j + 1]) - ToD(run[j])).Normalized();
                }
            }
        }

        return null;
    }

    private static List<List<Vector2>> Runs(Vector2[] points)
    {
        var runs = new List<List<Vector2>> { new() };
        foreach (var point in points)
        {
            if (float.IsFinite(point.X) && float.IsFinite(point.Y))
            {
                runs[^1].Add(point);
            }
            else if (runs[^1].Count > 0)
            {
                runs.Add([]);
            }
        }

        return runs;
    }

    private static Vector2d ToD(Vector2 v) => new(v.X, v.Y);

    private static double Multiply(byte c, byte a) => ((c * a) + 127) / 255;

    /// <summary>A point or a direction in double.</summary>
    internal readonly record struct Vector2d(double X, double Y)
    {
        public static Vector2d operator +(Vector2d a, Vector2d b) => new(a.X + b.X, a.Y + b.Y);

        public static Vector2d operator -(Vector2d a, Vector2d b) => new(a.X - b.X, a.Y - b.Y);

        public static Vector2d operator -(Vector2d a) => new(-a.X, -a.Y);

        public static Vector2d operator *(Vector2d a, double s) => new(a.X * s, a.Y * s);

        public double Dot(Vector2d b) => (X * b.X) + (Y * b.Y);

        public double Cross(Vector2d b) => (X * b.Y) - (Y * b.X);

        public double LengthSquared() => Dot(this);

        public Vector2d Normalized() => this * (1 / Math.Sqrt(LengthSquared()));
    }
}
