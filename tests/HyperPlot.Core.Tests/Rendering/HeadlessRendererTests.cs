using System.Buffers.Binary;
using HyperPlot.Rendering;

namespace HyperPlot.Tests;

public sealed class HeadlessRendererTests
{
    /// <summary>
    /// The integer premultiply and un-premultiply equal rounding the exact quotients with one half up (design §5.2), for
    /// every channel and alpha.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Every pair of a channel c and an alpha a from 0 to 255, and every premultiplied k from 0 to a.
    ///
    /// Steps:
    /// - Compare Multiply(c, a) with c · a / 255, and Divide(k, a) with k · 255 / a, each rounded with one half up in
    ///   decimal arithmetic.
    ///
    /// Expected:
    /// - Equal everywhere; Divide is 0 when a is 0; Multiply is never exactly one half; Divide of (43 · 6 premultiplied) is 43.
    /// </remarks>
    [Fact]
    public void Premultiplied_EveryValue_RoundsOneHalfUp()
    {
        for (var a = 0; a <= 255; a++)
        {
            for (var c = 0; c <= 255; c++)
            {
                var exact = c * a / 255m;
                Assert.NotEqual(0.5m, exact - decimal.Floor(exact));
                Assert.Equal((byte)decimal.Floor(exact + 0.5m), Premultiplied.Multiply((byte)c, (byte)a));
            }

            for (var k = 0; k <= a; k++)
            {
                var expected = a == 0 ? 0 : (byte)decimal.Floor((k * 255m / a) + 0.5m);
                Assert.Equal(expected, Premultiplied.Divide((byte)k, (byte)a));
            }
        }

        Assert.Equal(1, Premultiplied.Multiply(43, 6));
        Assert.Equal(43, Premultiplied.Divide(1, 6));
    }

    /// <summary>
    /// The PNG holds the pixels as RGBA with the chunks of design §7.5 in order, and splits IDAT at 64 KiB.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A fake render target of 300 × 300 seeded random pixels (incompressible, so more than 64 KiB of IDAT), and one of 2 × 1.
    ///
    /// Steps:
    /// - Write each as a PNG through a headless renderer; decode it with the test decoder, which checks every CRC.
    ///
    /// Expected:
    /// - Chunks IHDR, sRGB (0), gAMA (45455), cHRM (31270, 32900, 64000, 33000, 30000, 60000, 15000, 6000), IDAT..., IEND;
    ///   the large image has more than one IDAT, each but the last 65,536 bytes; the pixels equal the target's.
    /// </remarks>
    [Fact]
    public void WritePng_Images_HoldThePixelsAndTheChunks()
    {
        foreach (var (width, height) in new[] { (300, 300), (2, 1) })
        {
            var pixels = new byte[width * height * 4];
            new Random(width).NextBytes(pixels);
            using var renderer = new HeadlessRenderer(new FakeBackend(pixels), width, height);
            using var stream = new MemoryStream();

            renderer.WritePng(stream);

            var image = PngDecoder.Decode(stream.ToArray());
            Assert.Equal((width, height), (image.Width, image.Height));
            Assert.Equal(pixels, image.Pixels);
            var types = image.Chunks.Select(c => c.Type).ToArray();
            var idats = image.Chunks.Where(c => c.Type == "IDAT").ToArray();
            Assert.Equal(["IHDR", "sRGB", "gAMA", "cHRM", .. Enumerable.Repeat("IDAT", idats.Length), "IEND"], types);
            Assert.Equal([0], image.Chunks[1].Data);
            Assert.Equal(45455, BinaryPrimitives.ReadInt32BigEndian(image.Chunks[2].Data));
            var chromaticities = Enumerable.Range(0, 8).Select(i => BinaryPrimitives.ReadInt32BigEndian(image.Chunks[3].Data.AsSpan(i * 4)));
            Assert.Equal([31270, 32900, 64000, 33000, 30000, 60000, 15000, 6000], chromaticities);
            Assert.All(idats[..^1], c => Assert.Equal(65536, c.Data.Length));
            Assert.Equal(width == 300, idats.Length > 1);
        }
    }

    /// <summary>
    /// The renderer releases its own target only, refuses a null stream, and throws after it is disposed (design §7.5).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A fake backend of 1 × 1 pixels.
    ///
    /// Steps:
    /// - Write a PNG to null; dispose the renderer twice; clear, read, and write.
    ///
    /// Expected:
    /// - ArgumentNullException; the target is disposed once and the backend not at all; ObjectDisposedException afterwards.
    /// </remarks>
    [Fact]
    public void Dispose_Renderer_ReleasesOnlyItsTarget()
    {
        var backend = new FakeBackend(new byte[4]);
        var renderer = new HeadlessRenderer(backend, 1, 1);
        Assert.Throws<ArgumentNullException>(() => renderer.WritePng(null!));

        renderer.Dispose();
        renderer.Dispose();

        Assert.Equal((1, false), (backend.Target!.DisposeCount, backend.IsDisposed));
        Assert.Throws<ObjectDisposedException>(() => renderer.Clear(default));
        Assert.Throws<ObjectDisposedException>(() => renderer.ReadPixels(new byte[4]));
        Assert.Throws<ObjectDisposedException>(() => renderer.WritePng(new MemoryStream()));
    }

    private sealed class FakeBackend(byte[] pixels) : IGpuBackend
    {
        public bool IsWarp => true;

        public bool IsDisposed { get; private set; }

        public FakeTarget? Target { get; private set; }

        public IRenderTarget CreateRenderTarget(in RenderTargetDescription description) => Target = new FakeTarget(description, pixels);

        public void BeginFrame() { }

        public void EndFrame() { }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeTarget(RenderTargetDescription description, byte[] pixels) : IRenderTarget
    {
        public RenderTargetDescription Description => description;

        public int DisposeCount { get; private set; }

        public void Clear(Color color) { }

        public void ReadPixels(Span<byte> destination) => pixels.CopyTo(destination);

        public void Dispose() => DisposeCount++;
    }
}
