using System.Numerics;

namespace HyperPlot.Rendering.D3D11.Tests;

/// <summary>
/// The CPU reference of the polylines (design §5.3, §10), written apart from the shaders: it walks each run of finite points
/// as one path, lays every dash along the whole path, and takes at each pixel center the smallest distance to any dash (or
/// the whole run when solid). The coverage of a stroke of half width r at distance D is the length of [−r, r] inside the
/// pixel's box [D − 0.5, D + 0.5], kept as 8 bits as the R8 texture keeps it; the color goes over the image premultiplied.
/// </summary>
internal static class LineReference
{
    /// <summary>Draws the polylines in order over an opaque image of RGBA bytes.</summary>
    internal static void Draw(
        byte[] image,
        int width,
        int height,
        IEnumerable<(Vector2[] Points, LineStyle Style, (int X, int Y, int W, int H)? Clip)> polylines
    )
    {
        foreach (var (points, style, clip) in polylines)
        {
            if (style.Width == 0)
            {
                continue;
            }

            var pieces = Pieces(points, style.Dash);
            var r = style.Width / 2;
            var a = style.Color.A;
            float[] source = [Multiply(style.Color.R, a), Multiply(style.Color.G, a), Multiply(style.Color.B, a), a];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (clip is { } c && (x < c.X || y < c.Y || x >= c.X + c.W || y >= c.Y + c.H))
                    {
                        continue;
                    }

                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    var d = float.PositiveInfinity;
                    foreach (var (from, to) in pieces)
                    {
                        d = MathF.Min(d, Distance(p, from, to));
                    }

                    var coverage = Math.Clamp(MathF.Min(d + 0.5f, r) - MathF.Max(d - 0.5f, -r), 0, 1);

                    // Exactly 0 coverage leaves the pixel as it is; the GPU blends 0 over it, which changes nothing.
                    var stored = MathF.Round(coverage * 255) / 255;
                    var index = ((y * width) + x) * 4;
                    var alpha = source[3] / 255 * stored;
                    for (var channel = 0; channel < 4; channel++)
                    {
                        var value = (source[channel] * stored) + (image[index + channel] * (1 - alpha));
                        image[index + channel] = (byte)MathF.Round(value);
                    }
                }
            }
        }
    }

    /// <summary>The straight pieces that are inked: each dash cut into its parts on each segment, or each solid segment.</summary>
    private static List<(Vector2 From, Vector2 To)> Pieces(Vector2[] points, LineDash? dash)
    {
        var pieces = new List<(Vector2, Vector2)>();
        foreach (var run in Runs(points))
        {
            if (run.Count < 2)
            {
                continue;
            }

            // The path's length at each point.
            var at = new float[run.Count];
            for (var i = 1; i < run.Count; i++)
            {
                at[i] = at[i - 1] + Vector2.Distance(run[i - 1], run[i]);
            }

            var total = at[^1];
            if (dash is not { } pattern)
            {
                for (var i = 1; i < run.Count; i++)
                {
                    pieces.Add((run[i - 1], run[i]));
                }

                continue;
            }

            // Dash k covers [k·P − offset, k·P − offset + On] along the path, clipped to the path.
            var (on, period) = (pattern[0], pattern.Period);
            for (var k = (int)MathF.Floor(pattern.Offset / period) - 1; (k * period) - pattern.Offset <= total; k++)
            {
                var start = MathF.Max((k * period) - pattern.Offset, 0);
                var end = MathF.Min((k * period) - pattern.Offset + on, total);
                if (end < start)
                {
                    continue;
                }

                for (var i = 1; i < run.Count; i++)
                {
                    var from = MathF.Max(start, at[i - 1]);
                    var to = MathF.Min(end, at[i]);

                    // A dash that only touches a segment's end adds that point, which is also the end of the neighbor's
                    // piece: the smallest distance does not change.
                    if (to < from)
                    {
                        continue;
                    }

                    var length = at[i] - at[i - 1];
                    var direction = length > 1e-6f ? (run[i] - run[i - 1]) / length : Vector2.Zero;
                    pieces.Add((run[i - 1] + (direction * (from - at[i - 1])), run[i - 1] + (direction * (to - at[i - 1]))));
                }
            }
        }

        return pieces;
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

    private static float Distance(Vector2 p, Vector2 from, Vector2 to)
    {
        var line = to - from;
        var lengthSquared = line.LengthSquared();
        var t = lengthSquared > 0 ? Math.Clamp(Vector2.Dot(p - from, line) / lengthSquared, 0, 1) : 0;
        return Vector2.Distance(p, from + (line * t));
    }

    private static float Multiply(byte c, byte a) => ((c * a) + 127) / 255;
}
