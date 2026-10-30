using TerraFX.Interop.DirectX;

namespace HyperPlot.Rendering.D3D11.Tests;

/// <summary>The column trapezoids on WARP against a CPU reference, within 1 a channel (design §5.1, §5.3, §10). Windows only.</summary>
public sealed class ColumnTests
{
    private const int Size = 32;
    private static readonly Color Background = new(250, 245, 235, 255);

    /// <summary>
    /// Sloped top and bottom edges, an edge across many pixel rows, and a column narrower than a pixel cover each pixel by
    /// the area it shares with the region between the edges (design §5.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A 32 × 32 WARP target cleared to an opaque background.
    ///
    /// Steps:
    /// - Draw a trapezoid with both edges sloped, one whose top edge falls 20 rows across 3 pixels, and one 0.4 pixel wide;
    ///   read back.
    ///
    /// Expected:
    /// - Every channel of every pixel within 1 of the reference, which integrates the overlap finely.
    /// </remarks>
    [Fact]
    public void Coverage_SlopedSteepAndNarrow_MatchTheReference() =>
        Check(
            (new ColumnTrapezoid(2.25f, 9.5f, 4.3f, 7.9f, 18.6f, 15.1f), new Color(20, 40, 160, 255)),
            (new ColumnTrapezoid(12f, 15f, 2.5f, 22.7f, 28f, 28f), new Color(200, 60, 20, 255)),
            (new ColumnTrapezoid(20.2f, 20.6f, 5.5f, 6.1f, 12.25f, 12.25f), new Color(0, 0, 0, 255))
        );

    /// <summary>
    /// Edges that cross paint the region between them in either order: a bow tie, two triangles meeting where the edges
    /// meet (design §5.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A half-transparent trapezoid whose top edge goes from above its bottom edge to below it.
    ///
    /// Steps:
    /// - Draw it; read back.
    ///
    /// Expected:
    /// - Within 1 of the reference, which takes the absolute difference of the clamped edges.
    /// </remarks>
    [Fact]
    public void Crossing_PaintsTheBowTie() => Check((new ColumnTrapezoid(4.5f, 27.5f, 6.25f, 25.75f, 20f, 10f), new Color(30, 120, 200, 160)));

    /// <summary>
    /// A trapezoid with a coordinate that is not finite, or with Left at or past Right, draws nothing; the clip holds them
    /// (design §5.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Trapezoids with NaN, ∞, Left = Right, and Left > Right, then one under a clip that cuts it.
    ///
    /// Steps:
    /// - Draw them; read back.
    ///
    /// Expected:
    /// - The list holds one record; within 1 of the reference, with nothing outside the clip.
    /// </remarks>
    [Fact]
    public void SkippedAndClipped()
    {
        var black = new Color(0, 0, 0, 255);
        var list = new RenderList();
        list.DrawColumns(
            [
                new ColumnTrapezoid(1, 5, float.NaN, 2, 9, 9),
                new ColumnTrapezoid(1, 5, 1, 2, float.PositiveInfinity, 9),
                new ColumnTrapezoid(5, 5, 1, 2, 9, 9),
                new ColumnTrapezoid(6, 5, 1, 2, 9, 9),
            ],
            black
        );
        list.SetClip(8, 4, 10, 10);
        list.DrawColumns([new ColumnTrapezoid(2.5f, 25.5f, 2.5f, 3.5f, 25.5f, 22.5f)], black);

        using var backend = D3D11Backend.Create(useWarp: true);
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(Size, Size));
        target.Clear(Background);
        backend.Draw(target, list);
        var actual = new byte[Size * Size * 4];
        target.ReadPixels(actual);

        AssertClose(Reference((new ColumnTrapezoid(2.5f, 25.5f, 2.5f, 3.5f, 25.5f, 22.5f), black, (8, 4, 10, 10))), actual);
    }

    /// <summary>Recording and drawing trapezoids again allocates nothing once the buffers have grown (design §5.1, §5.3).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend and a 64 × 64 target; one frame of 64 trapezoids to grow the buffers.
    ///
    /// Steps:
    /// - Five times: clear the list, record the 64 trapezoids from a span, draw.
    ///
    /// Expected:
    /// - 0 bytes on this thread.
    /// </remarks>
    [Fact]
    public void RecordAndDraw_SteadyState_AllocatesNothing()
    {
        using var backend = D3D11Backend.Create(useWarp: true);
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(64, 64));
        var columns = Enumerable.Range(0, 64).Select(i => new ColumnTrapezoid(i, i + 1, 10 + (i % 7), 11 + (i % 5), 50, 50)).ToArray();
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
            list.DrawColumns(columns, new Color(0, 0, 0, 64));
            backend.Draw(target, list);
        }
    }

    /// <summary>After a device loss, the next frame makes the trapezoid pipeline again and draws the same pixels (design §5.3).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend and a 32 × 32 target; a translucent sloped trapezoid.
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
        var column = new ColumnTrapezoid(3.5f, 24.25f, 6.75f, 12.5f, 27.25f, 20.5f);
        var color = new Color(30, 60, 200, 180);
        var list = new RenderList();
        list.DrawColumns([column], color);
        var expected = Reference((column, color, null));

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

    private static void Check(params (ColumnTrapezoid Column, Color Color)[] columns)
    {
        using var backend = D3D11Backend.Create(useWarp: true);
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(Size, Size));
        var list = new RenderList();
        foreach (var (column, color) in columns)
        {
            list.DrawColumns([column], color);
        }

        target.Clear(Background);
        backend.Draw(target, list);
        var actual = new byte[Size * Size * 4];
        target.ReadPixels(actual);

        AssertClose(Reference(columns.Select(c => (c.Column, c.Color, ((int, int, int, int)?)null)).ToArray()), actual);
    }

    /// <summary>
    /// The CPU reference, written apart from the shader: for each pixel, the overlap is integrated over x by the midpoint
    /// rule in 4,096 steps, each taking the length of [min(top, bottom), max(top, bottom)] inside the pixel's rows.
    /// </summary>
    private static byte[] Reference(params (ColumnTrapezoid Column, Color Color, (int X, int Y, int W, int H)? Clip)[] columns)
    {
        var image = new byte[Size * Size * 4];
        for (var i = 0; i < image.Length; i += 4)
        {
            // Four statements, not one tuple assignment (Issue #117).
            image[i] = Background.R;
            image[i + 1] = Background.G;
            image[i + 2] = Background.B;
            image[i + 3] = Background.A;
        }

        const int Steps = 4096;
        foreach (var (c, color, clip) in columns)
        {
            var a = color.A;
            double[] source = [Multiply(color.R, a), Multiply(color.G, a), Multiply(color.B, a), a];
            for (var row = 0; row < Size; row++)
            {
                for (var col = 0; col < Size; col++)
                {
                    if (clip is { } k && (col < k.X || row < k.Y || col >= k.X + k.W || row >= k.Y + k.H))
                    {
                        continue;
                    }

                    var from = Math.Max(col, c.Left);
                    var to = Math.Min(col + 1, c.Right);
                    if (!(from < to))
                    {
                        continue;
                    }

                    var coverage = 0.0;
                    var dx = (to - from) / Steps;
                    for (var s = 0; s < Steps; s++)
                    {
                        var x = from + ((s + 0.5) * dx);
                        var t = (x - c.Left) / (c.Right - c.Left);
                        var top = c.TopLeft + ((c.TopRight - c.TopLeft) * t);
                        var bottom = c.BottomLeft + ((c.BottomRight - c.BottomLeft) * t);
                        var low = Math.Clamp(Math.Min(top, bottom), row, row + 1);
                        var high = Math.Clamp(Math.Max(top, bottom), row, row + 1);
                        coverage += (high - low) * dx;
                    }

                    if (coverage == 0)
                    {
                        continue;
                    }

                    var index = ((row * Size) + col) * 4;
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
