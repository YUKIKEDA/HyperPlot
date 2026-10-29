using System.Numerics;
using HyperPlot.Tests;
using TerraFX.Interop.DirectX;

namespace HyperPlot.Rendering.D3D11.Tests;

/// <summary>
/// Texts on WARP with the fake text backend, against the CPU reference within 1 a channel (design §5.6, §10). Windows only.
/// </summary>
public sealed class TextTests
{
    private static readonly Color Background = new(250, 245, 235, 255);
    private static readonly Color Ink = new(20, 30, 120, 255);

    /// <summary>
    /// The 12 alignments in each of the 3 rotations, with blocks of 2 and 3 lines whose glyphs land on all 4 quarter-pixel
    /// phases, match the reference (design §5.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend over the fake text backend; a 640 × 480 target cleared to an opaque background.
    ///
    /// Steps:
    /// - For each rotation, record each of the 12 alignments at its own cell of a grid, at fractional positions: 2 lines at
    ///   size 13 and 3 lines at size 14, each with a raised '^', half-transparent for the odd cells; draw; read back.
    ///
    /// Expected:
    /// - Every channel of every pixel within 1 of the reference.
    /// </remarks>
    [Theory]
    [InlineData(TextRotation.None)]
    [InlineData(TextRotation.Clockwise90)]
    [InlineData(TextRotation.CounterClockwise90)]
    public void AlignmentsAndPhases_EachRotation_MatchTheReference(TextRotation rotation)
    {
        var cases = new List<(string Text, Vector2 Position, TextStyle Style)>();
        var cell = 0;
        foreach (var vertical in new[] { TextBaseline.Top, TextBaseline.Middle, TextBaseline.Baseline, TextBaseline.Bottom })
        {
            foreach (var horizontal in new[] { TextAlign.Left, TextAlign.Center, TextAlign.Right })
            {
                var color = cell % 2 == 0 ? Ink : new Color(180, 40, 20, 150);
                var style = new TextStyle(color, cell % 3 == 0 ? 13 : 14, Horizontal: horizontal, Vertical: vertical, Rotation: rotation);
                var text = cell % 2 == 0 ? "A^B\nCDE" : "ABC^E\nFG\nH^J";
                var position = new Vector2(80.3f + ((cell % 3) * 220), 60.6f + ((cell / 3) * 115));
                cases.Add((text, position, style));
                cell++;
            }
        }

        Check(640, 480, cases);
    }

    /// <summary>
    /// Polylines and texts recorded in turn are drawn in that order, each over the ones before; a clip cuts a text
    /// (design §5.1, §5.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend over the fake text backend; a 160 × 120 target.
    ///
    /// Steps:
    /// - Record a thick line, a text across it, a half-transparent line across the text, a text clipped to (30, 60, 50,
    ///   20), and a last text in a new clip of the whole target; draw; read back.
    ///
    /// Expected:
    /// - Within 1 of the reference drawn in the same order: the first text over the first line, the second line over the
    ///   first text, nothing of the clipped text outside its clip.
    /// </remarks>
    [Fact]
    public void LinesAndTexts_InTurn_DrawInOrderAndClip()
    {
        const int Width = 160;
        const int Height = 120;
        var fake = new FakeTextBackend();
        using var backend = D3D11Backend.CreateForTest(useWarp: true, createDevice: null, new TextEngine(fake));
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(Width, Height));
        var first = (Points: new Vector2[] { new(10, 30), new(150, 30) }, Style: new LineStyle(new Color(0, 160, 60, 255), 9));
        var second = (Points: new Vector2[] { new(40, 5), new(60, 110) }, Style: new LineStyle(new Color(200, 0, 0, 140), 6));
        var style = new TextStyle(Ink, 18, Vertical: TextBaseline.Middle);
        var list = new RenderList();
        list.DrawPolyline(first.Points, first.Style);
        list.DrawText("ABCDEFGH", new Vector2(12.4f, 30), style);
        list.DrawPolyline(second.Points, second.Style);
        list.SetClip(30, 60, 50, 20);
        list.DrawText("IJKLMNOP", new Vector2(12.4f, 70), style);
        list.SetClip(0, 0, Width, Height);
        list.DrawText("QRS", new Vector2(100, 100), style with { Rotation = TextRotation.CounterClockwise90 });

        target.Clear(Background);
        backend.Draw(target, list);
        var pixels = new byte[Width * Height * 4];
        target.ReadPixels(pixels);

        var expected = Opaque(Width, Height);
        LineReference.Draw(expected, Width, Height, [(first.Points, first.Style, null)]);
        TextReference.Draw(expected, Width, Height, fake, "ABCDEFGH", new Vector2(12.4f, 30), style);
        LineReference.Draw(expected, Width, Height, [(second.Points, second.Style, null)]);
        TextReference.Draw(expected, Width, Height, fake, "IJKLMNOP", new Vector2(12.4f, 70), style, (30, 60, 50, 20));
        TextReference.Draw(expected, Width, Height, fake, "QRS", new Vector2(100, 100), style with { Rotation = TextRotation.CounterClockwise90 });
        AssertClose(expected, pixels, Width);
    }

    /// <summary>
    /// The pixels stay right while the atlas lets old glyphs go and takes them back, and the backend sends only what the
    /// atlas wrote: whole pages when they are new, the written rectangles after (design §5.3, §5.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend over a fake text backend whose glyphs are 1,023 pixels square (a page holds 4); a 1,040 × 2,080
    ///   target, where each glyph of a Draw is a text of its own, one under the other.
    ///
    /// Steps:
    /// - Draw 1 "ab"; Draw 2 "cd"; Draw 3 "e" (the shelf of "ab" goes); Draw 4 "a" again (rasterized again); Draw 5 "a";
    ///   each time read back and compare with the reference of that Draw alone; read what each Draw sent.
    ///
    /// Expected:
    /// - Every Draw within 1 of the reference; Draw 1 sends page 0 whole; Draws 2 to 4 send one rectangle each, that of
    ///   the cells written; a Draw of glyphs already there sends nothing.
    /// </remarks>
    [Fact]
    public void AtlasEviction_EachDraw_MatchesAndSendsOnlyWhatChanged()
    {
        const int Width = 1040;
        const int Height = 2080;
        var fake = new FakeTextBackend { Box = static (_, _) => new GlyphBox(0, -1023, 1023, 1023) };
        using var backend = D3D11Backend.CreateForTest(useWarp: true, createDevice: null, new TextEngine(fake));
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(Width, Height));
        var style = new TextStyle(Ink, 16);
        var pixels = new byte[Width * Height * 4];

        string[] draws = ["ab", "cd", "e", "a", "a"];
        AtlasUpload[][] sent =
        [
            [new(0, 0, 0, 2048, 2048)],
            [new(0, 0, 1024, 2048, 1024)],
            [new(0, 0, 0, 1024, 1024)],
            [new(0, 1024, 0, 1024, 1024)],
            [],
        ];
        for (var i = 0; i < draws.Length; i++)
        {
            // Each glyph is a text of its own, one under the other, so that no pixel is blended twice.
            var list = new RenderList();
            var expected = Opaque(Width, Height);
            for (var k = 0; k < draws[i].Length; k++)
            {
                var position = new Vector2(9.5f, 1030 + (k * 1035));
                list.DrawText(draws[i][k..(k + 1)], position, style);
                TextReference.Draw(expected, Width, Height, fake, draws[i][k..(k + 1)], position, style);
            }

            target.Clear(Background);
            backend.Draw(target, list);
            target.ReadPixels(pixels);
            AssertClose(expected, pixels, Width);
            Assert.Equal(sent[i], backend.Glyphs.LastUploads);
        }

        Assert.Equal(6, fake.Rasterized.Count);
    }

    /// <summary>
    /// After a device loss, the next frame makes the page textures again from the atlas's pages on the CPU: the same
    /// pixels, with nothing shaped or rasterized again (design §5.3, §5.6, invariant 10).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend over the fake text backend; a 200 × 100 target; a list of two texts on two lines.
    ///
    /// Steps:
    /// - Draw and read; lose the device in a readback; draw and read again.
    ///
    /// Expected:
    /// - Both reads within 1 of the reference; the second Draw sends page 0 whole; the fake shaped and rasterized nothing
    ///   more.
    /// </remarks>
    [Fact]
    public void DeviceLost_NextDraw_MakesThePagesAgainFromTheCpu()
    {
        const int Width = 200;
        const int Height = 100;
        var fake = new FakeTextBackend();
        using var backend = D3D11Backend.CreateForTest(useWarp: true, createDevice: null, new TextEngine(fake));
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(Width, Height));
        var style = new TextStyle(Ink, 15);
        var list = new RenderList();
        list.DrawText("Before\nAfter", new Vector2(20.5f, 30.25f), style);
        var expected = Opaque(Width, Height);
        TextReference.Draw(expected, Width, Height, fake, "Before\nAfter", new Vector2(20.5f, 30.25f), style);

        AssertClose(expected, DrawAndRead(), Width);
        var (shaped, rasterized) = (fake.Shaped.Count, fake.Rasterized.Count);
        backend.CheckResult = (call, result) => call == "Map" ? DXGI.DXGI_ERROR_DEVICE_REMOVED : result;
        _ = Assert.Throws<GpuDeviceLostException>(() => target.ReadPixels(new byte[Width * Height * 4]));
        backend.CheckResult = null;

        AssertClose(expected, DrawAndRead(), Width);
        Assert.Equal([new AtlasUpload(0, 0, 0, 2048, 2048)], backend.Glyphs.LastUploads);
        Assert.Equal((shaped, rasterized), (fake.Shaped.Count, fake.Rasterized.Count));

        byte[] DrawAndRead()
        {
            target.Clear(Background);
            backend.Draw(target, list);
            var pixels = new byte[Width * Height * 4];
            target.ReadPixels(pixels);
            return pixels;
        }
    }

    /// <summary>
    /// A text measured through a renderer is drawn without shaping again, and two renderers on one backend share its cache
    /// (design §5.6, §7.5).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend over the fake text backend; two headless renderers on it.
    ///
    /// Steps:
    /// - Measure "Shared" with the first; render it with the second; measure it with the second.
    ///
    /// Expected:
    /// - The fake shaped once.
    /// </remarks>
    [Fact]
    public void MeasureThenDraw_TwoRenderers_ShapeOnce()
    {
        var fake = new FakeTextBackend();
        using var backend = D3D11Backend.CreateForTest(useWarp: true, createDevice: null, new TextEngine(fake));
        using var first = new HeadlessRenderer(backend, 64, 32);
        using var second = new HeadlessRenderer(backend, 64, 32);
        var style = new TextStyle(Ink, 12);

        _ = first.MeasureText("Shared", style);
        var list = new RenderList();
        list.DrawText("Shared", new Vector2(2, 20), style);
        second.Render(list);
        _ = second.MeasureText("Shared", style);

        Assert.Single(fake.Shaped);
        Assert.Same(backend.Text, backend.Text);
    }

    /// <summary>
    /// Warm, recording and drawing the same 100 labels allocates nothing, with the fake text backend and with DirectWrite
    /// (design §5.6, invariant 3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend, over the fake text backend or DirectWrite; a 640 × 480 target; 100 labels drawn once.
    ///
    /// Steps:
    /// - Ten times: clear the list, record the 100 labels, draw; count the bytes this thread allocates.
    ///
    /// Expected:
    /// - 0 bytes.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HundredLabels_Warm_AllocateNothing(bool directWrite)
    {
        using var backend = directWrite
            ? D3D11Backend.Create(useWarp: true)
            : D3D11Backend.CreateForTest(useWarp: true, createDevice: null, new TextEngine(new FakeTextBackend()));
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(640, 480));
        var labels = Enumerable.Range(0, 100).Select(i => (i * 0.25).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var list = new RenderList();
        var style = new TextStyle(Ink, 12, Horizontal: TextAlign.Center);
        Record();
        backend.Draw(target, list);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < 10; frame++)
        {
            Record();
            backend.Draw(target, list);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        void Record()
        {
            list.Clear();
            for (var i = 0; i < labels.Length; i++)
            {
                list.DrawText(labels[i], new Vector2(32 + ((i % 10) * 64), 24 + ((i / 10) * 46)), style);
            }
        }
    }

    private static void Check(int width, int height, List<(string Text, Vector2 Position, TextStyle Style)> texts)
    {
        var fake = new FakeTextBackend();
        using var backend = D3D11Backend.CreateForTest(useWarp: true, createDevice: null, new TextEngine(fake));
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(width, height));
        var list = new RenderList();
        foreach (var (text, position, style) in texts)
        {
            list.DrawText(text, position, style);
        }

        target.Clear(Background);
        backend.Draw(target, list);
        var pixels = new byte[width * height * 4];
        target.ReadPixels(pixels);

        var expected = Opaque(width, height);
        foreach (var (text, position, style) in texts)
        {
            TextReference.Draw(expected, width, height, fake, text, position, style);
        }

        AssertClose(expected, pixels, width);
    }

    private static byte[] Opaque(int width, int height)
    {
        var image = new byte[width * height * 4];
        for (var i = 0; i < image.Length; i += 4)
        {
            (image[i], image[i + 1], image[i + 2], image[i + 3]) = (Background.R, Background.G, Background.B, 255);
        }

        return image;
    }

    private static void AssertClose(byte[] expected, byte[] actual, int width)
    {
        for (var i = 0; i < expected.Length; i++)
        {
            if (Math.Abs(expected[i] - actual[i]) > 1)
            {
                var pixel = i / 4;
                Assert.Fail($"Pixel ({pixel % width}, {pixel / width}) channel {i % 4}: expected {expected[i]}, actual {actual[i]}.");
            }
        }
    }
}
