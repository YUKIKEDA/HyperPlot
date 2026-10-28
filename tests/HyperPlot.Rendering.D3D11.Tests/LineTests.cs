using System.Numerics;
using TerraFX.Interop.DirectX;

namespace HyperPlot.Rendering.D3D11.Tests;

/// <summary>The polylines on WARP against the CPU reference, within 1 a channel (design §5.3, §10). Windows only.</summary>
public sealed class LineTests
{
    private const int Size = 64;
    private static readonly Color Background = new(250, 245, 235, 255);

    public static TheoryData<float> Widths => [0.25f, 0.5f, 1f, 1.5f, 3f];

    /// <summary>
    /// Solid lines of every width, at fractional positions, horizontal, vertical, and slanted, match the reference.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A 64 × 64 WARP target cleared to an opaque background.
    ///
    /// Steps:
    /// - Draw a horizontal line at y 10.3, a vertical one at x 20.7, and a slanted one from (5.2, 60.1) to (58.9, 30.6),
    ///   opaque dark blue, at the width under test; read back.
    ///
    /// Expected:
    /// - Every channel of every pixel within 1 of the reference.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Widths))]
    public void Solid_Widths_MatchTheReference(float width)
    {
        var style = new LineStyle(new Color(20, 40, 160, 255), width);
        Check(
            (new Vector2[] { new(3, 10.3f), new(61, 10.3f) }, style, null),
            (new Vector2[] { new(20.7f, 2), new(20.7f, 62) }, style, null),
            (new Vector2[] { new(5.2f, 60.1f), new(58.9f, 30.6f) }, style, null)
        );
    }

    /// <summary>
    /// A sharp zigzag covers each pixel once at its joins, opaque or half-transparent: no join is darker (design §5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A zigzag with turns of about 160 degrees, width 3.
    ///
    /// Steps:
    /// - Draw it opaque, then half-transparent elsewhere; read back.
    ///
    /// Expected:
    /// - Within 1 of the reference, which takes the smallest distance to the whole path, so a pixel is covered once.
    /// </remarks>
    [Fact]
    public void Joins_SharpTurns_CoverEachPixelOnce()
    {
        Vector2[] zigzag = [new(4, 4), new(30, 12), new(6, 20), new(32, 28), new(8, 36)];
        var shifted = zigzag.Select(p => p + new Vector2(28, 20)).ToArray();
        Check((zigzag, new LineStyle(new Color(10, 120, 60, 255), 3), null), (shifted, new LineStyle(new Color(200, 30, 30, 128), 3), null));
    }

    /// <summary>
    /// An M4 polyline, four points to a pixel column, matches the reference opaque and half-transparent.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 60 pixel columns, each with first, minimum, maximum, and last points at seeded heights; width 1.
    ///
    /// Steps:
    /// - Draw it opaque; draw it again half-transparent, shifted down; read back.
    ///
    /// Expected:
    /// - Within 1 of the reference.
    /// </remarks>
    [Fact]
    public void M4_FourPointsAColumn_MatchesTheReference()
    {
        var random = new Random(4);
        var points = new List<Vector2>();
        for (var column = 0; column < 60; column++)
        {
            for (var k = 0; k < 4; k++)
            {
                points.Add(new Vector2(column + 2 + (k / 4f), 8 + (random.NextSingle() * 20)));
            }
        }

        var line = points.ToArray();
        Check(
            (line, new LineStyle(new Color(0, 0, 0, 255), 1), null),
            (line.Select(p => p + new Vector2(0, 30)).ToArray(), new LineStyle(new Color(0, 90, 200, 160), 1), null)
        );
    }

    /// <summary>
    /// Dashes run on across joins, honor the offset, draw dots for On 0, and start again after a break (design §5.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An L-shaped path with dash 7 on 4 off and offset 2, width 2; a path of dots (On 0, Off 5), width 3; a path with a
    ///   NaN in it, dashed 6 and 3.
    ///
    /// Steps:
    /// - Draw the three; read back.
    ///
    /// Expected:
    /// - Within 1 of the reference, which lays the dashes along each whole run and starts each run's pattern at 0.
    /// </remarks>
    [Fact]
    public void Dashes_AcrossJoinsDotsAndBreaks_MatchTheReference()
    {
        Check(
            (new Vector2[] { new(4, 6), new(40, 6), new(40, 30) }, new LineStyle(new Color(90, 20, 120, 255), 2, new LineDash(7, 4, 2)), null),
            (new Vector2[] { new(4, 40), new(60, 44) }, new LineStyle(new Color(10, 10, 10, 255), 3, new LineDash(0, 5)), null),
            (
                new Vector2[] { new(4, 54), new(25, 54), new(float.NaN, 0), new(30, 58), new(60, 50) },
                new LineStyle(new Color(160, 80, 0, 200), 1.5f, new LineDash(6, 3)),
                null
            )
        );
    }

    /// <summary>
    /// What draws nothing leaves the image alone; a repeated point draws a dot; a clip keeps the line inside it; a second
    /// line over the first finds no coverage left behind (design §5.1, §5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Width 0; one point; a point between NaNs; two equal points (a dot of width 6); a slanted line clipped to (10, 10,
    ///   20, 15), then a faint unclipped line across where the clipped one would run outside its clip, so that coverage left
    ///   outside a clip would show; then two lines on the same path, red then translucent green, so that the green one would
    ///   show red's band if its coverage stayed.
    ///
    /// Steps:
    /// - Draw them; read back.
    ///
    /// Expected:
    /// - Within 1 of the reference, which draws nothing for the first three, a disc for the dot, nothing outside the clip,
    ///   and each line of the pair on its own.
    /// </remarks>
    [Fact]
    public void EmptyDotsClipAndBands_MatchTheReference()
    {
        var black = new LineStyle(new Color(0, 0, 0, 255), 2);
        Check(
            (new Vector2[] { new(2, 2), new(40, 40) }, black with { Width = 0 }, null),
            (new Vector2[] { new(30, 30) }, black, null),
            (new Vector2[] { new(float.NaN, 1), new(50, 50), new(1, float.NaN) }, black, null),
            (new Vector2[] { new(50.3f, 12.6f), new(50.3f, 12.6f) }, black with { Width = 6 }, null),
            (new Vector2[] { new(0, 0), new(64, 40) }, black, (10, 10, 20, 15)),
            (new Vector2[] { new(0, 6), new(64, 30) }, new LineStyle(new Color(0, 0, 220, 90), 1), null),
            (new Vector2[] { new(5, 50), new(58, 58) }, new LineStyle(new Color(220, 0, 0, 255), 4), null),
            (new Vector2[] { new(5, 50), new(58, 58) }, new LineStyle(new Color(0, 200, 0, 100), 2), null)
        );
    }

    /// <summary>
    /// Drawing into another backend's target, or with nulls, throws (design §5.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two WARP backends, one target on each.
    ///
    /// Steps:
    /// - Draw a list on the first backend into the second's target; draw with a null target and a null list.
    ///
    /// Expected:
    /// - ArgumentException, then ArgumentNullException twice.
    /// </remarks>
    [Fact]
    public void Draw_OtherBackendOrNull_Throws()
    {
        using var first = D3D11Backend.Create(useWarp: true);
        using var second = D3D11Backend.Create(useWarp: true);
        using var target = second.CreateRenderTarget(new RenderTargetDescription(4, 4));
        var list = new RenderList();

        Assert.Throws<ArgumentException>(() => first.Draw(target, list));
        Assert.Throws<ArgumentNullException>(() => first.Draw(null!, list));
        using var own = first.CreateRenderTarget(new RenderTargetDescription(4, 4));
        Assert.Throws<ArgumentNullException>(() => first.Draw(own, null!));
    }

    /// <summary>
    /// Recording and drawing a frame again allocates nothing once the buffers have grown (design §5.1, §5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend and a 256 × 64 target; 16 polylines of 1,024 points; one frame to grow the buffers.
    ///
    /// Steps:
    /// - Five times: clear the list, record the 16 polylines, draw.
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
            for (var series = 0; series < 16; series++)
            {
                list.DrawPolyline(points, new LineStyle(new Color(0, 0, 0, 128), 1));
            }

            backend.Draw(target, list);
        }
    }

    /// <summary>
    /// After a device loss, the next frame makes the line pipeline and the coverage texture again, and draws the same pixels
    /// (design §5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend and a 64 × 64 target; a dashed translucent zigzag.
    ///
    /// Steps:
    /// - Clear and draw; read. Lose the device through Map; clear and draw again; read.
    ///
    /// Expected:
    /// - The loss throws GpuDeviceLostException; both reads equal the reference.
    /// </remarks>
    [Fact]
    public void DeviceLost_NextFrame_DrawsTheSamePixels()
    {
        using var backend = D3D11Backend.Create(useWarp: true);
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(Size, Size));
        var line = (
            Points: new Vector2[] { new(4, 4), new(50, 30), new(10, 58) },
            Style: new LineStyle(new Color(30, 60, 200, 180), 2.5f, new LineDash(8, 3))
        );
        var list = new RenderList();
        list.DrawPolyline(line.Points, line.Style);
        var expected = Reference((line.Points, line.Style, null));

        AssertClose(expected, DrawAndRead());
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

    private static void Check(params (Vector2[] Points, LineStyle Style, (int X, int Y, int W, int H)? Clip)[] polylines)
    {
        using var backend = D3D11Backend.Create(useWarp: true);
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(Size, Size));
        var list = new RenderList();
        foreach (var (points, style, clip) in polylines)
        {
            if (clip is { } c)
            {
                list.SetClip(c.X, c.Y, c.W, c.H);
            }
            else
            {
                list.ResetClip();
            }

            list.DrawPolyline(points, style);
        }

        target.Clear(Background);
        backend.Draw(target, list);
        var actual = new byte[Size * Size * 4];
        target.ReadPixels(actual);

        AssertClose(Reference(polylines), actual);
    }

    private static byte[] Reference(params (Vector2[] Points, LineStyle Style, (int X, int Y, int W, int H)? Clip)[] polylines)
    {
        var expected = new byte[Size * Size * 4];
        for (var i = 0; i < expected.Length; i += 4)
        {
            (expected[i], expected[i + 1], expected[i + 2], expected[i + 3]) = (Background.R, Background.G, Background.B, Background.A);
        }

        LineReference.Draw(expected, Size, Size, polylines);
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
