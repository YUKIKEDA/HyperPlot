using System.Numerics;

namespace HyperPlot.Rendering.D3D11.Tests;

/// <summary>The polar trapezoids on WARP against a CPU reference, within 1 a channel (design §5.1, §5.3, §10 G11 row). Windows only.</summary>
public sealed class SectorTests
{
    private const int Size = 64;
    private static readonly Color Background = new(250, 245, 235, 255);
    private static readonly Vector2 Center = new(32.3f, 31.7f);

    /// <summary>
    /// A fan, a part of a ring, a trapezoid with sloped edges, ones across 12 o'clock both ways, and one whose edges swap
    /// cover each pixel by the share of its 32 samples inside (design §5.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A 64 × 64 WARP target cleared to an opaque background; a center off the pixel grid.
    ///
    /// Steps:
    /// - Draw each trapezoid alone in its color; read back.
    ///
    /// Expected:
    /// - Every channel of every pixel within 1 of the reference, which tests each sample's angle and radius in double.
    /// </remarks>
    [Fact]
    public void Coverage_MatchesTheReference()
    {
        (SectorTrapezoid Sector, Color Color)[] cases =
        [
            (new(0.3f, 1.4f, 25, 25, 0, 0), new Color(20, 40, 160, 255)),
            (new(2.0f, 3.5f, 28, 28, 15, 15), new Color(200, 60, 20, 200)),
            (new(4.0f, 5.0f, 12, 27, 5, 9), new Color(0, 120, 60, 255)),
            (new(-0.4f, 0.25f, 20, 20, 10, 10), new Color(90, 20, 140, 255)),
            (new(5.9f, 6.6f, 9, 9, 3, 3), new Color(30, 30, 30, 160)),
            (new(1.6f, 2.6f, 5, 20, 18, 8), new Color(160, 160, 20, 220)),
        ];
        foreach (var (sector, color) in cases)
        {
            _ = Check(([sector], color, null));
        }
    }

    /// <summary>
    /// The trapezoids of one call are one shape: wedges that share their sides leave no seam, opaque or half transparent;
    /// two calls that overlap are both painted (design §5.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 100 wedges of 2π / 100 out to radius 25, as an area of a polar plot draws them.
    ///
    /// Steps:
    /// - Draw them in one opaque call; read back. Draw them in one call of alpha 128; read back. Draw a trapezoid twice in
    ///   two calls of alpha 128; read back.
    ///
    /// Expected:
    /// - Opaque: every pixel within 24 of the center exactly the color. Half transparent: those pixels all one color, and
    ///   the image within 1 of the reference. Two calls: within 2 of the reference that blends the shape twice, each blend
    ///   rounded to 8 bits on both sides (one call alone is within 1).
    /// </remarks>
    [Fact]
    public void OneCall_IsOneShape()
    {
        var wedges = Enumerable
            .Range(0, 100)
            .Select(i => new SectorTrapezoid(i * MathF.Tau / 100, (i + 1) * MathF.Tau / 100, 25, 25, 0, 0))
            .ToArray();
        var opaque = new Color(20, 90, 200, 255);
        var image = Draw((wedges, opaque, null));
        foreach (var (x, y) in Inside(24))
        {
            Assert.Equal(Premultiplied(opaque), image.AsSpan(((y * Size) + x) * 4, 4).ToArray());
        }

        var half = new Color(20, 90, 200, 128);
        var translucent = Check((wedges, half, null));
        var first = Inside(24).First();
        var color = translucent.AsSpan(((first.Y * Size) + first.X) * 4, 4).ToArray();
        foreach (var (x, y) in Inside(24))
        {
            Assert.Equal(color, translucent.AsSpan(((y * Size) + x) * 4, 4).ToArray());
        }

        SectorTrapezoid[] one = [new(0.5f, 2.5f, 26, 26, 6, 6)];
        AssertClose(Reference((one, half, null), (one, half, null)), Draw((one, half, null), (one, half, null)), tolerance: 2);

        static IEnumerable<(int X, int Y)> Inside(float radius) =>
            Enumerable
                .Range(0, Size * Size)
                .Select(i => (i % Size, i / Size))
                .Where(p => (new Vector2(p.Item1 + 0.5f, p.Item2 + 0.5f) - Center).Length() < radius - 1);

        static byte[] Premultiplied(Color c) =>
            [(byte)Math.Round(Multiply(c.R, c.A)), (byte)Math.Round(Multiply(c.G, c.A)), (byte)Math.Round(Multiply(c.B, c.A)), c.A];
    }

    /// <summary>
    /// Trapezoids that are not finite, empty, or wider than a full turn draw nothing; a clip holds a call (design §5.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A call of trapezoids with NaN, ∞, From = To, From > To, and a span of 7; a ring from 0.1 to 6.2 between radii 4
    ///   and 28 clipped to (10, 12, 30, 20), which cuts it on all four sides.
    ///
    /// Steps:
    /// - Draw them; read back.
    ///
    /// Expected:
    /// - The first call paints nothing: the image is the background where the second does not reach; the image within 1 of
    ///   the reference, with nothing outside the clip.
    /// </remarks>
    [Fact]
    public void SkippedAndClipped()
    {
        SectorTrapezoid[] skipped =
        [
            new(float.NaN, 1, 10, 10, 0, 0),
            new(0, 1, float.PositiveInfinity, 10, 0, 0),
            new(1, 1, 10, 10, 0, 0),
            new(2, 1, 10, 10, 0, 0),
            new(0, 7, 10, 10, 0, 0),
        ];
        var nothing = Draw((skipped, new Color(0, 0, 0, 255), null));
        Assert.All(nothing.Chunk(4), p => Assert.Equal([Background.R, Background.G, Background.B, Background.A], p));
        _ = Check((skipped, new Color(0, 0, 0, 255), null), ([new(0.1f, 6.2f, 28, 28, 4, 4)], new Color(200, 20, 20, 255), (10, 12, 30, 20)));
    }

    /// <summary>
    /// A call starts from an empty mask: the samples of the call before it do not paint in its color, though its box covers
    /// them (design §5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A fan from 0.3 to 1.4 out to 20, then a whole ring from 26 to 29 whose box holds the fan, in other colors.
    ///
    /// Steps:
    /// - Draw both; read back.
    ///
    /// Expected:
    /// - Within 1 of the reference: the fan in its own color only.
    /// </remarks>
    [Fact]
    public void NextCall_StartsFromAnEmptyMask() =>
        _ = Check(
            ([new(0.3f, 1.4f, 20, 20, 0, 0)], new Color(20, 40, 160, 255), null),
            ([new(0, MathF.Tau, 29, 29, 26, 26)], new Color(200, 60, 20, 255), null)
        );

    /// <summary>Once warm, recording and drawing the polar trapezoids allocates nothing (design §5.1, §10 G11 row).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP target; a list of 100 wedges drawn once.
    ///
    /// Steps:
    /// - Five times: clear the list, record the wedges, draw.
    ///
    /// Expected:
    /// - 0 bytes on this thread.
    /// </remarks>
    [Fact]
    public void Warm_AllocatesNothing()
    {
        var wedges = Enumerable
            .Range(0, 100)
            .Select(i => new SectorTrapezoid(i * MathF.Tau / 100, (i + 1) * MathF.Tau / 100, 25, 25, 0, 0))
            .ToArray();
        using var backend = D3D11Backend.Create(useWarp: true);
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(Size, Size));
        var list = new RenderList();
        list.DrawSectors(wedges, Center, new Color(0, 0, 0, 255));
        backend.Draw(target, list);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 5; i++)
        {
            list.Clear();
            list.DrawSectors(wedges, Center, new Color(0, 0, 0, 255));
            backend.Draw(target, list);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>
    /// On a curved edge, the 32 samples a pixel (design §5.3) differ from the area the pixel shares with a disk by at most
    /// 0.112, the bound of design §5.3 for the arcs of polar trapezoids (a radial side is straight: 0.111, row R4).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Disks of radius 0.5 to 600, each at 64 seeded centers inside a pixel.
    ///
    /// Steps:
    /// - For each pixel the circle crosses, count the samples inside the disk and take the exact area of the disk and the
    ///   pixel's square (the integral of the disk's height over the square, in closed form).
    ///
    /// Expected:
    /// - The areas of all the pixels add up to the disk's (the reference checks itself); the largest difference within 0.01
    ///   of 0.112.
    /// </remarks>
    [Fact]
    public void Samples_DifferFromTheAreaOfACircle()
    {
        var random = new Random(23);
        var worst = 0.0;
        var at = string.Empty;
        foreach (var radius in new[] { 0.5, 1, 1.5, 2, 3, 5, 8, 13, 20, 40, 80, 160, 320, 600 })
        {
            for (var n = 0; n < 64; n++)
            {
                var (cx, cy) = (random.NextDouble(), random.NextDouble());
                var extent = (int)Math.Ceiling(radius) + 1;
                if (n == 0)
                {
                    // The reference itself: the areas of all the pixels add up to the disk's.
                    var total = 0.0;
                    for (var py = -extent; py <= extent; py++)
                    {
                        for (var px = -extent; px <= extent; px++)
                        {
                            total += DiskArea(cx, cy, radius, px, py);
                        }
                    }

                    Assert.Equal(Math.PI * radius * radius, total, 1e-6 * radius * radius);
                }

                for (var py = -extent; py <= extent; py++)
                {
                    for (var px = -extent; px <= extent; px++)
                    {
                        // Only the pixels the circle crosses: a corner inside and one outside.
                        var near = Math.Sqrt(
                            Math.Pow(Math.Max(0, Math.Max(px - cx, cx - (px + 1))), 2) + Math.Pow(Math.Max(0, Math.Max(py - cy, cy - (py + 1))), 2)
                        );
                        var far = Math.Sqrt(
                            Math.Pow(Math.Max(Math.Abs(px - cx), Math.Abs(px + 1 - cx)), 2)
                                + Math.Pow(Math.Max(Math.Abs(py - cy), Math.Abs(py + 1 - cy)), 2)
                        );
                        if (!(near < radius && far > radius))
                        {
                            continue;
                        }

                        var count = JoinReference.Samples.Count(s => Math.Pow(px + s.X - cx, 2) + Math.Pow(py + s.Y - cy, 2) <= radius * radius);
                        var difference = Math.Abs((count / 32.0) - DiskArea(cx, cy, radius, px, py));
                        if (difference > worst)
                        {
                            (worst, at) = (difference, $"radius {radius}, center ({cx}, {cy}), pixel ({px}, {py})");
                        }
                    }
                }
            }
        }

        Assert.True(Math.Abs(worst - 0.112) <= 0.01, $"The largest difference is {worst:F4}, at {at}.");
    }

    /// <summary>
    /// Returns the area of the disk of a center and a radius inside the unit square at (x0, y0): the integral over x of the
    /// disk's height inside the square, in pieces between the x where the circle meets the square's sides, each in closed
    /// form through G(u) = (u √(r² − u²) + r² asin(u / r)) / 2.
    /// </summary>
    private static double DiskArea(double cx, double cy, double r, int x0, int y0)
    {
        var (x1, y1) = (x0 + 1.0, y0 + 1.0);
        var cuts = new List<double> { x0, x1, cx - r, cx + r };
        foreach (var y in new double[] { y0, y1 })
        {
            if (Math.Abs(y - cy) <= r)
            {
                var w = Math.Sqrt((r * r) - ((y - cy) * (y - cy)));
                cuts.Add(cx - w);
                cuts.Add(cx + w);
            }
        }

        var xs = cuts.Where(x => x >= x0 && x <= x1).Order().ToArray();
        var area = 0.0;
        for (var i = 0; i + 1 < xs.Length; i++)
        {
            var (a, b) = (xs[i], xs[i + 1]);
            var m = (a + b) / 2;
            if (!(b > a) || Math.Abs(m - cx) >= r)
            {
                continue;
            }

            var s = Math.Sqrt((r * r) - ((m - cx) * (m - cx)));
            if (Math.Min(y1, cy + s) <= Math.Max(y0, cy - s))
            {
                continue;
            }

            // The height between the bound below and the one above: the square's side or the circle's arc.
            var arc = G(b - cx, r) - G(a - cx, r);
            var top = cy + s < y1 ? (cy * (b - a)) + arc : y1 * (b - a);
            var bottom = cy - s > y0 ? (cy * (b - a)) - arc : y0 * (b - a);
            area += top - bottom;
        }

        return area;

        static double G(double u, double r) => ((u * Math.Sqrt(Math.Max(0, (r * r) - (u * u)))) + (r * r * Math.Asin(Math.Clamp(u / r, -1, 1)))) / 2;
    }

    private static byte[] Check(params (SectorTrapezoid[] Sectors, Color Color, (int X, int Y, int W, int H)? Clip)[] calls)
    {
        var actual = Draw(calls);
        AssertClose(Reference(calls), actual);
        return actual;
    }

    private static byte[] Draw(params (SectorTrapezoid[] Sectors, Color Color, (int X, int Y, int W, int H)? Clip)[] calls)
    {
        using var backend = D3D11Backend.Create(useWarp: true);
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(Size, Size));
        var list = new RenderList();
        foreach (var (sectors, color, clip) in calls)
        {
            if (clip is { } c)
            {
                list.SetClip(c.X, c.Y, c.W, c.H);
            }
            else
            {
                list.ResetClip();
            }

            list.DrawSectors(sectors, Center, color);
        }

        target.Clear(Background);
        backend.Draw(target, list);
        var actual = new byte[Size * Size * 4];
        target.ReadPixels(actual);
        return actual;
    }

    /// <summary>
    /// The CPU reference, written apart from the shader and from <see cref="RenderList"/>'s pieces (the span is checked in
    /// float, as the contract states it): a sample is inside a call
    /// when it lies in any of its trapezoids, its angle from 12 o'clock taken in double and folded into the turn from From,
    /// and its radius between the edges there. Each pixel's coverage is the count of its 32 samples inside, / 32.
    /// </summary>
    private static byte[] Reference(params (SectorTrapezoid[] Sectors, Color Color, (int X, int Y, int W, int H)? Clip)[] calls)
    {
        var image = new byte[Size * Size * 4];
        for (var i = 0; i < image.Length; i += 4)
        {
            image[i] = Background.R;
            image[i + 1] = Background.G;
            image[i + 2] = Background.B;
            image[i + 3] = Background.A;
        }

        foreach (var (sectors, color, clip) in calls)
        {
            var a = color.A;
            double[] source = [Multiply(color.R, a), Multiply(color.G, a), Multiply(color.B, a), a];
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    if (clip is { } k && (x < k.X || y < k.Y || x >= k.X + k.W || y >= k.Y + k.H))
                    {
                        continue;
                    }

                    var count = 0;
                    foreach (var sample in JoinReference.Samples)
                    {
                        var (px, py) = (x + (double)sample.X - Center.X, y + (double)sample.Y - Center.Y);
                        if (sectors.Any(s => Inside(s, px, py)))
                        {
                            count++;
                        }
                    }

                    if (count == 0)
                    {
                        continue;
                    }

                    var coverage = count / 32.0;
                    var index = ((y * Size) + x) * 4;
                    var alpha = source[3] / 255 * coverage;
                    for (var channel = 0; channel < 4; channel++)
                    {
                        var value = (source[channel] * coverage) + (image[index + channel] * (1 - alpha));
                        image[index + channel] = (byte)Math.Round(value);
                    }
                }
            }
        }

        return image;

        static bool Inside(SectorTrapezoid s, double px, double py)
        {
            var finite = float.IsFinite(s.From) && float.IsFinite(s.To) && float.IsFinite(s.OuterFrom) && float.IsFinite(s.OuterTo);
            var span = (double)s.To - s.From;
            if (!finite || !float.IsFinite(s.InnerFrom) || !float.IsFinite(s.InnerTo) || !(s.To - s.From > 0) || s.To - s.From > 2 * MathF.PI)
            {
                return false;
            }

            var t = Math.Atan2(px, -py) - s.From;
            t -= 2 * Math.PI * Math.Floor(t / (2 * Math.PI));
            if (t > span)
            {
                return false;
            }

            var share = t / span;
            var outer = s.OuterFrom + ((s.OuterTo - s.OuterFrom) * share);
            var inner = s.InnerFrom + ((s.InnerTo - s.InnerFrom) * share);
            var r = Math.Sqrt((px * px) + (py * py));
            return r >= Math.Min(inner, outer) && r <= Math.Max(inner, outer);
        }
    }

    private static double Multiply(byte c, byte a) => ((c * a) + 127) / 255;

    private static void AssertClose(byte[] expected, byte[] actual, int tolerance = 1)
    {
        var worst = 0;
        var at = -1;
        for (var i = 0; i < expected.Length; i++)
        {
            var difference = Math.Abs(expected[i] - actual[i]);
            if (difference > worst)
            {
                (worst, at) = (difference, i);
            }
        }

        Assert.True(
            worst <= tolerance,
            $"Pixel ({(at / 4) % Size}, {at / 4 / Size}) channel {at % 4}: expected {(at < 0 ? 0 : expected[at])}, got {(at < 0 ? 0 : actual[at])}."
        );
    }
}
