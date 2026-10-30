using TerraFX.Interop.DirectX;

namespace HyperPlot.Rendering.D3D11.Tests;

/// <summary>The filled rectangles on WARP against a CPU reference, within 1 a channel (design §5.1, §5.3, §10). Windows only.</summary>
public sealed class RectTests
{
    private const int Size = 32;
    private static readonly Color Background = new(250, 245, 235, 255);

    /// <summary>
    /// Rectangles with whole-pixel edges, fractional edges, and smaller than a pixel cover each pixel by the area they share
    /// with it (design §5.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A 32 × 32 WARP target cleared to an opaque background.
    ///
    /// Steps:
    /// - Draw an opaque rectangle on whole pixels, one with every edge fractional, and one of 0.4 × 0.3 inside one pixel;
    ///   read back.
    ///
    /// Expected:
    /// - Every channel of every pixel within 1 of the reference, which multiplies the overlaps along x and y.
    /// </remarks>
    [Fact]
    public void Coverage_WholeFractionalAndSubPixel_MatchTheReference() =>
        Check(
            (2, 3, 10, 6, new Color(20, 40, 160, 255), null),
            (14.25f, 2.6f, 9.5f, 7.3f, new Color(200, 60, 20, 255), null),
            (5.3f, 20.4f, 0.4f, 0.3f, new Color(0, 0, 0, 255), null)
        );

    /// <summary>
    /// Two half-transparent rectangles that overlap are each blended over what is below: the overlap is darker, unlike the
    /// one shape of a polyline (design §5.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two rectangles of alpha 128 that overlap by a fractional strip.
    ///
    /// Steps:
    /// - Draw both; read back.
    ///
    /// Expected:
    /// - Within 1 of the reference, which blends each in turn.
    /// </remarks>
    [Fact]
    public void Overlap_HalfTransparent_BlendsEach() =>
        Check((4, 4, 14.5f, 12, new Color(30, 120, 200, 128), null), (12.5f, 9.25f, 14, 15, new Color(220, 40, 40, 128), null));

    /// <summary>The clip of SetClip holds rectangles, and consecutive rectangles under another clip start a new step (design §5.3).</summary>
    /// <remarks>
    /// Preconditions:
    /// - Three rectangles: two under a clip that cuts them, one after it under the whole target.
    ///
    /// Steps:
    /// - Draw them; read back.
    ///
    /// Expected:
    /// - Within 1 of the reference: no pixel of the first two outside the clip, the third whole.
    /// </remarks>
    [Fact]
    public void Clip_CutsRectangles() =>
        Check(
            (2, 2, 20, 10, new Color(20, 120, 40, 255), (6, 4, 8, 20)),
            (3.5f, 14.5f, 20, 6, new Color(120, 20, 140, 200), (6, 4, 8, 20)),
            (20, 22, 9, 8, new Color(10, 10, 10, 255), null)
        );

    /// <summary>A rectangle of width or height 0 draws nothing (design §5.1).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A cleared target.
    ///
    /// Steps:
    /// - Record a rectangle of width 0 and one of height 0; draw; read back.
    ///
    /// Expected:
    /// - Every pixel is the background.
    /// </remarks>
    [Fact]
    public void ZeroSize_DrawsNothing() => Check((4, 4, 0, 10, new Color(0, 0, 0, 255), null), (4, 4, 10, 0, new Color(0, 0, 0, 255), null));

    /// <summary>
    /// Recording and drawing rectangles again allocates nothing once the buffers have grown (design §5.1, §5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend and a 64 × 64 target; one frame of 300 rectangles to grow the buffers.
    ///
    /// Steps:
    /// - Five times: clear the list, record the 300 rectangles, draw.
    ///
    /// Expected:
    /// - 0 bytes on this thread.
    /// </remarks>
    [Fact]
    public void RecordAndDraw_SteadyState_AllocatesNothing()
    {
        using var backend = D3D11Backend.Create(useWarp: true);
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(64, 64));
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
            for (var i = 0; i < 300; i++)
            {
                list.DrawRect(i % 60, i / 5, 3.5f, 1.25f, new Color(0, 0, 0, 64));
            }

            backend.Draw(target, list);
        }
    }

    /// <summary>After a device loss, the next frame makes the rectangle pipeline again and draws the same pixels (design §5.3).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend and a 32 × 32 target; a translucent rectangle with fractional edges.
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
        (float X, float Y, float W, float H, Color Color, (int X, int Y, int W, int H)? Clip) rect = (
            3.5f,
            6.25f,
            20.75f,
            11.5f,
            new Color(30, 60, 200, 180),
            null
        );
        var list = new RenderList();
        list.DrawRect(rect.X, rect.Y, rect.W, rect.H, rect.Color);
        var expected = Reference(rect);

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

    private static void Check(params (float X, float Y, float W, float H, Color Color, (int X, int Y, int W, int H)? Clip)[] rects)
    {
        using var backend = D3D11Backend.Create(useWarp: true);
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(Size, Size));
        var list = new RenderList();
        foreach (var (x, y, w, h, color, clip) in rects)
        {
            if (clip is { } c)
            {
                list.SetClip(c.X, c.Y, c.W, c.H);
            }
            else
            {
                list.ResetClip();
            }

            list.DrawRect(x, y, w, h, color);
        }

        target.Clear(Background);
        backend.Draw(target, list);
        var actual = new byte[Size * Size * 4];
        target.ReadPixels(actual);

        AssertClose(Reference(rects), actual);
    }

    /// <summary>
    /// The CPU reference, written apart from the shader: each pixel [i, i + 1) × [j, j + 1) is covered by the product of the
    /// lengths it shares with the rectangle along x and y, and the premultiplied color goes over the image with that coverage.
    /// </summary>
    private static byte[] Reference(params (float X, float Y, float W, float H, Color Color, (int X, int Y, int W, int H)? Clip)[] rects)
    {
        var image = new byte[Size * Size * 4];
        for (var i = 0; i < image.Length; i += 4)
        {
            // Four statements, not one tuple assignment: in the Release build of this project the tuple form left R at 0 in
            // every pixel once a half-transparent rectangle followed (seen 2026-10-30, .NET 10; the Debug build was right).
            image[i] = Background.R;
            image[i + 1] = Background.G;
            image[i + 2] = Background.B;
            image[i + 3] = Background.A;
        }

        foreach (var (x, y, w, h, color, clip) in rects)
        {
            var a = color.A;
            double[] source = [Multiply(color.R, a), Multiply(color.G, a), Multiply(color.B, a), a];
            for (var row = 0; row < Size; row++)
            {
                for (var column = 0; column < Size; column++)
                {
                    if (clip is { } c && (column < c.X || row < c.Y || column >= c.X + c.W || row >= c.Y + c.H))
                    {
                        continue;
                    }

                    var ox = Math.Clamp(Math.Min(column + 1, (double)x + w) - Math.Max(column, x), 0, 1);
                    var oy = Math.Clamp(Math.Min(row + 1, (double)y + h) - Math.Max(row, y), 0, 1);
                    var coverage = ox * oy;
                    if (coverage == 0)
                    {
                        continue;
                    }

                    var index = ((row * Size) + column) * 4;
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
    }

    private static double Multiply(byte c, byte a) => ((c * a) + 127) / 255;

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
            $"Pixel ({(at / 4) % Size}, {at / 4 / Size}) channel {at % 4}: expected {(at < 0 ? 0 : expected[at])}, got {(at < 0 ? 0 : actual[at])}."
        );
    }
}
