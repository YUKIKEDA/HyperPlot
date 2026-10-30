using System.Numerics;
using TerraFX.Interop.DirectX;

namespace HyperPlot.Rendering.D3D11.Tests;

/// <summary>
/// Row R4's joins, caps, and dash patterns on WARP against the CPU reference of 32 samples a pixel, within 1 a channel
/// (design §5.1, §5.3, §10). Windows only.
/// </summary>
public sealed class LineJoinTests
{
    private const int Size = 96;
    private static readonly Color Background = new(250, 245, 235, 255);

    /// <summary>
    /// Each join with each cap draws the union of its boxes, joins, and caps (design §5.1): a zigzag of a 30°, a 90°, and a
    /// 150° turn, opaque, then the same again half transparent over it, which also checks that no sample stays set.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A 96 × 96 target; a zigzag 6 pixels wide at points off the sample grid.
    ///
    /// Steps:
    /// - Draw the zigzag opaque, then shifted and half transparent; read back.
    ///
    /// Expected:
    /// - Every channel within 1 of the reference.
    /// </remarks>
    /// <param name="join">The join.</param>
    /// <param name="cap">The cap.</param>
    [Theory]
    [InlineData(LineJoin.Miter, LineCap.Butt)]
    [InlineData(LineJoin.Miter, LineCap.Square)]
    [InlineData(LineJoin.Miter, LineCap.Round)]
    [InlineData(LineJoin.Bevel, LineCap.Butt)]
    [InlineData(LineJoin.Bevel, LineCap.Square)]
    [InlineData(LineJoin.Bevel, LineCap.Round)]
    [InlineData(LineJoin.Round, LineCap.Butt)]
    [InlineData(LineJoin.Round, LineCap.Square)]
    public void JoinsAndCaps_MatchTheReference(LineJoin join, LineCap cap)
    {
        Vector2[] zigzag = [new(10.13f, 20.27f), new(40.21f, 20.27f), new(66.19f, 35.27f), new(66.19f, 70.31f), new(30.37f, 49.63f)];
        var opaque = new LineStyle(new Color(20, 60, 160, 255), 6) { Join = join, Cap = cap };
        var shifted = zigzag.Select(p => p + new Vector2(5.5f, 8.25f)).ToArray();
        var translucent = opaque with { Color = new Color(200, 40, 30, 128) };

        Check((zigzag, opaque), (shifted, translucent));
    }

    /// <summary>
    /// A miter becomes a bevel when it reaches past MiterLimit half widths: 1 / cos(θ / 2) above the limit (design §5.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - MiterLimit 2, so the limit is a turn of 120°; turns of 116° and 124°.
    ///
    /// Steps:
    /// - Draw both 8 pixels wide; read back.
    ///
    /// Expected:
    /// - Within 1 of the reference, which mitered the first and beveled the second.
    /// </remarks>
    [Fact]
    public void MiterLimit_TurnsToABevel()
    {
        var style = new LineStyle(new Color(0, 0, 0, 255), 8)
        {
            Join = LineJoin.Miter,
            Cap = LineCap.Butt,
            MiterLimit = 2,
        };

        Check((Turn(new Vector2(14.3f, 30.1f), 116), style), (Turn(new Vector2(14.3f, 75.2f), 124), style));

        static Vector2[] Turn(Vector2 start, float degrees)
        {
            var corner = start + new Vector2(30, 0);
            var radians = degrees * MathF.PI / 180;
            return [start, corner, corner + (new Vector2(MathF.Cos(radians), -MathF.Sin(radians)) * 30)];
        }
    }

    /// <summary>
    /// The degenerate shapes (design §5.1): a reversal leaves two boxes cut flat; segments of length 0 are skipped when the
    /// join is found; a run of length 0 is a circle, an axis-aligned square, or nothing; a NaN breaks the line with a cap on
    /// each side.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - For each cap: a reversal, a turn with a repeated point at its corner, a run of three equal points, and a NaN break.
    ///
    /// Steps:
    /// - Draw them 6 pixels wide with a miter join; read back.
    ///
    /// Expected:
    /// - Within 1 of the reference.
    /// </remarks>
    /// <param name="cap">The cap.</param>
    [Theory]
    [InlineData(LineCap.Butt)]
    [InlineData(LineCap.Square)]
    [InlineData(LineCap.Round)]
    public void DegenerateShapes_MatchTheReference(LineCap cap)
    {
        var style = new LineStyle(new Color(30, 30, 30, 255), 6) { Join = LineJoin.Miter, Cap = cap };
        Vector2[] reversal = [new(10.2f, 12.3f), new(50.4f, 12.3f), new(30.1f, 12.3f)];
        Vector2[] repeated = [new(10.2f, 30.4f), new(40.3f, 30.4f), new(40.3f, 30.4f), new(60.1f, 48.2f)];
        Vector2[] still = [new(80.3f, 20.6f), new(80.3f, 20.6f), new(80.3f, 20.6f)];
        Vector2[] broken = [new(10.4f, 70.2f), new(40.6f, 64.1f), new(float.NaN, 0), new(50.3f, 64.1f), new(85.2f, 80.3f)];

        Check((reversal, style), (repeated, style), (still, style), (broken, style));
    }

    /// <summary>
    /// A pattern of 8 intervals runs on across joins; each dash has its caps, a join lies only where a dash covers the
    /// point, and a dash that ends exactly at a point gets a cap there (design §5.1). Dashes of length 0 draw dots by the cap.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A turning line with an 8-interval pattern; a two-segment line whose first dash ends exactly at the corner (the first
    ///   segment is 12 long, the dash 12); a line of dots, On 0.
    ///
    /// Steps:
    /// - For each cap, draw them 5 pixels wide with a bevel join; read back.
    ///
    /// Expected:
    /// - Within 1 of the reference.
    /// </remarks>
    /// <param name="cap">The cap.</param>
    [Theory]
    [InlineData(LineCap.Butt)]
    [InlineData(LineCap.Square)]
    [InlineData(LineCap.Round)]
    public void Dashes_MatchTheReference(LineCap cap)
    {
        var style = new LineStyle(new Color(80, 20, 120, 220), 5, LineDash.Of(9, 3, 4, 3, 1, 3, 4, 5) with { Offset = 2 })
        {
            Join = LineJoin.Bevel,
            Cap = cap,
        };
        Vector2[] turning = [new(8.3f, 10.2f), new(60.4f, 10.2f), new(80.2f, 40.3f), new(40.1f, 50.7f)];
        Vector2[] corner = [new(8.3f, 70.4f), new(20.3f, 70.4f), new(30.3f, 88.1f)];
        var dots = new LineStyle(new Color(0, 90, 40, 255), 4, new LineDash(0, 7)) { Join = LineJoin.Bevel, Cap = cap };
        Vector2[] dotted = [new(40.2f, 75.3f), new(90.1f, 70.2f)];

        Check((turning, style), (corner, style with { Dash = new LineDash(12, 4) }), (dotted, dots));
    }

    /// <summary>
    /// A pattern of more than one dash and gap is drawn as a union of samples with the default round join and round cap too,
    /// as row R2's shader reads one dash and gap only (design §5.1, §5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A turning line with a 6-interval pattern (3 repeated), round join and cap by default; a 4-interval pattern of a dot.
    ///
    /// Steps:
    /// - Draw them 5 pixels wide; read back.
    ///
    /// Expected:
    /// - Within 1 of the reference: dashes with round ends, round joins inside dashes, round dots.
    /// </remarks>
    [Fact]
    public void Dashes_RoundByDefault_MatchTheReference()
    {
        var pattern = new LineStyle(new Color(10, 110, 60, 230), 5, LineDash.Of(11, 4, 3) with { Offset = 1.5f });
        var dotted = new LineStyle(new Color(120, 30, 30, 255), 5, LineDash.Of(9, 4, 0, 4));
        Vector2[] turning = [new(8.3f, 12.1f), new(62.2f, 14.4f), new(82.6f, 52.3f), new(30.2f, 60.8f)];
        Vector2[] straight = [new(6.4f, 82.2f), new(90.1f, 78.4f)];

        Check((turning, pattern), (straight, dotted));
    }

    /// <summary>A line half a pixel wide, mitered, covers its samples as the reference does (design §5.3).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A zigzag 0.5 wide; a shallow line at a slope near 1/5, 1 wide.
    ///
    /// Steps:
    /// - Draw both; read back.
    ///
    /// Expected:
    /// - Within 1 of the reference.
    /// </remarks>
    [Fact]
    public void ThinAndShallow_MatchTheReference()
    {
        var thin = new LineStyle(new Color(0, 0, 0, 255), 0.5f) { Join = LineJoin.Miter };
        var shallow = new LineStyle(new Color(0, 0, 0, 255), 1) { Join = LineJoin.Miter, Cap = LineCap.Butt };

        Check(([new(5.3f, 40.2f), new(30.1f, 10.4f), new(55.7f, 44.6f)], thin), ([new(4.2f, 70.1f), new(90.3f, 87.3f)], shallow));
    }

    /// <summary>
    /// The 32-bit mask is made the first time a line that is not round is drawn, not before; after a device loss the next
    /// frame makes it again and draws the same pixels (design §5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP target; a round line, then a mitered dashed one.
    ///
    /// Steps:
    /// - Draw the round line; read whether the mask exists. Draw the mitered one; read it and the pixels. Lose the device;
    ///   draw again; read.
    ///
    /// Expected:
    /// - No mask after the round line, a mask after the mitered one; both reads of the mitered line equal the reference.
    /// </remarks>
    [Fact]
    public void Mask_MadeWhenNeeded_AndAgainAfterALoss()
    {
        using var backend = D3D11Backend.Create(useWarp: true);
        using var target = (D3D11RenderTarget)backend.CreateRenderTarget(new RenderTargetDescription(Size, Size));
        var round = new RenderList();
        round.DrawPolyline([new(4, 4), new(50, 30)], new LineStyle(new Color(0, 0, 0, 255), 2));
        backend.Draw(target, round);
        Assert.False(target.HasMask);

        Vector2[] points = [new(6.2f, 8.4f), new(60.3f, 40.1f), new(20.6f, 80.2f)];
        var style = new LineStyle(new Color(30, 60, 200, 180), 5, LineDash.Of(10, 3, 2, 3)) { Join = LineJoin.Miter, Cap = LineCap.Square };
        var list = new RenderList();
        list.DrawPolyline(points, style);
        var expected = Reference((points, style));

        AssertClose(expected, DrawAndRead());
        Assert.True(target.HasMask);
        backend.CheckResult = (call, result) => call == "Map" ? DXGI.DXGI_ERROR_DEVICE_REMOVED : result;
        _ = Assert.Throws<GpuDeviceLostException>(() => target.ReadPixels(new byte[Size * Size * 4]));
        backend.CheckResult = null;

        AssertClose(expected, DrawAndRead());

        byte[] DrawAndRead()
        {
            target.Clear(Background);
            backend.Draw(target, list);
            var pixels = new byte[Size * Size * 4];
            target.ReadPixels(pixels);
            return pixels;
        }
    }

    /// <summary>Recording and drawing lines that are not round again allocates nothing once the buffers have grown (design §5.3).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP target; 1,024 points of an M4-like line; one frame to grow the buffers.
    ///
    /// Steps:
    /// - Five times: clear the list, record 8 mitered and 8 dashed square-capped polylines, draw.
    ///
    /// Expected:
    /// - 0 bytes on this thread.
    /// </remarks>
    [Fact]
    public void RecordAndDraw_SteadyState_AllocatesNothing()
    {
        using var backend = D3D11Backend.Create(useWarp: true);
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(256, 64));
        var random = new Random(2);
        var points = Enumerable.Range(0, 1024).Select(i => new Vector2(i / 4f, random.NextSingle() * 64)).ToArray();
        var miter = new LineStyle(new Color(0, 0, 0, 128), 1.5f) { Join = LineJoin.Miter };
        var dashed = new LineStyle(new Color(0, 0, 0, 128), 1, LineDash.Of(6, 2, 1, 2)) { Cap = LineCap.Square };
        var list = new RenderList();
        Frame();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 5; i++)
        {
            Frame();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        void Frame()
        {
            list.Clear();
            for (var series = 0; series < 8; series++)
            {
                list.DrawPolyline(points, miter);
                list.DrawPolyline(points, dashed);
            }

            backend.Draw(target, list);
        }
    }

    /// <summary>
    /// The 32 samples' share of a pixel differs from the area a straight edge or a strip cuts from it by at most the bounds
    /// design §5.3 writes: 0.112 for an edge, 0.110 for a strip 1 or 2 wide, 0.138 for 0.5, and 0.156 for 0.25 (row R4).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The reference's 32 samples.
    ///
    /// Steps:
    /// - For every edge normal 0.5° apart and 257 offsets across the pixel, compare the share of samples inside with the area,
    ///   for a half-plane and for strips 0.25, 0.5, 1, and 2 wide; the slope near 1/5 is the worst.
    ///
    /// Expected:
    /// - The worst difference of each is below its bound and above the bound less 0.01, so the bounds are not loose.
    /// </remarks>
    [Fact]
    public void Samples_DifferFromTheAreaWithinTheBounds()
    {
        Assert.InRange(Worst(double.PositiveInfinity), 0.102, 0.112);
        Assert.InRange(Worst(2), 0.100, 0.110);
        Assert.InRange(Worst(1), 0.100, 0.110);
        Assert.InRange(Worst(0.5), 0.128, 0.138);
        Assert.InRange(Worst(0.25), 0.146, 0.156);

        static double Worst(double width)
        {
            var worst = 0.0;
            for (var step = 0; step < 720; step++)
            {
                var angle = step * Math.PI / 360;
                var (nx, ny) = (Math.Cos(angle), Math.Sin(angle));
                var low = Math.Min(0, nx) + Math.Min(0, ny) - (double.IsInfinity(width) ? 0 : width);
                var high = Math.Max(0, nx) + Math.Max(0, ny);
                for (var k = 0; k <= 256; k++)
                {
                    var d = low + ((high - low) * k / 256);
                    var inside = JoinReference.Samples.Count(s => (nx * s.X) + (ny * s.Y) <= d && (nx * s.X) + (ny * s.Y) >= d - width);
                    var area = Area(nx, ny, d) - (double.IsInfinity(width) ? 0 : Area(nx, ny, d - width));
                    worst = Math.Max(worst, Math.Abs((inside / 32.0) - area));
                }
            }

            return worst;
        }

        // The area of the unit pixel where nx x + ny y <= d, by clipping the square.
        static double Area(double nx, double ny, double d)
        {
            (double X, double Y)[] square = [(0, 0), (1, 0), (1, 1), (0, 1)];
            var clipped = new List<(double X, double Y)>();
            for (var i = 0; i < 4; i++)
            {
                var (a, b) = (square[i], square[(i + 1) % 4]);
                var (fa, fb) = ((nx * a.X) + (ny * a.Y) - d, (nx * b.X) + (ny * b.Y) - d);
                if (fa <= 0)
                {
                    clipped.Add(a);
                }

                if ((fa < 0) != (fb < 0) && fa != fb)
                {
                    var t = fa / (fa - fb);
                    clipped.Add((a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t)));
                }
            }

            var sum = 0.0;
            for (var i = 0; i < clipped.Count; i++)
            {
                var (p, q) = (clipped[i], clipped[(i + 1) % clipped.Count]);
                sum += (p.X * q.Y) - (q.X * p.Y);
            }

            return Math.Abs(sum) / 2;
        }
    }

    private static void Check(params (Vector2[] Points, LineStyle Style)[] polylines)
    {
        using var backend = D3D11Backend.Create(useWarp: true);
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(Size, Size));
        var list = new RenderList();
        foreach (var (points, style) in polylines)
        {
            list.DrawPolyline(points, style);
        }

        target.Clear(Background);
        backend.Draw(target, list);
        var actual = new byte[Size * Size * 4];
        target.ReadPixels(actual);

        AssertClose(Reference(polylines), actual);
    }

    private static byte[] Reference(params (Vector2[] Points, LineStyle Style)[] polylines)
    {
        var expected = new byte[Size * Size * 4];
        for (var i = 0; i < expected.Length; i += 4)
        {
            // Four statements, not one tuple assignment (Issue #117).
            expected[i] = Background.R;
            expected[i + 1] = Background.G;
            expected[i + 2] = Background.B;
            expected[i + 3] = Background.A;
        }

        JoinReference.Draw(expected, Size, Size, polylines);
        return expected;
    }

    private static void AssertClose(byte[] expected, byte[] actual)
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
            worst <= 1,
            $"Channel {at % 4} of pixel ({(at / 4) % Size}, {at / 4 / Size}) differs by {worst}: expected {(at < 0 ? 0 : expected[at])}, got {(at < 0 ? 0 : actual[at])}."
        );
    }
}
