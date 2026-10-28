namespace HyperPlot.Rendering;

/// <summary>
/// Draws into an offscreen render target of any backend and reads it back, as pixels or a PNG (design §7.5). For tests,
/// image output, and the NativeAOT check. Row R1 draws clears only.
/// </summary>
/// <remarks>One thread at a time uses a renderer and its backend.</remarks>
public sealed class HeadlessRenderer : IDisposable
{
    private readonly IRenderTarget _target;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="HeadlessRenderer"/> class with a render target of its own.</summary>
    /// <param name="backend">The backend. Its owner disposes it, after the renderer.</param>
    /// <param name="width">The width in physical pixels, from 1 to 16,384, apart from any screen.</param>
    /// <param name="height">The height in physical pixels, from 1 to 16,384.</param>
    /// <exception cref="ArgumentOutOfRangeException">A side is outside 1 to 16,384.</exception>
    public HeadlessRenderer(IGpuBackend backend, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _target = backend.CreateRenderTarget(new RenderTargetDescription(width, height));
    }

    /// <summary>Gets the width in physical pixels.</summary>
    public int Width => _target.Description.Width;

    /// <summary>Gets the height in physical pixels.</summary>
    public int Height => _target.Description.Height;

    /// <summary>Fills the image with one color.</summary>
    /// <param name="color">The color.</param>
    /// <exception cref="GpuDeviceLostException">The device was lost; the next frame makes it again, and the image is gone.</exception>
    public void Clear(Color color)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _target.Clear(color);
    }

    /// <summary>Reads the image as RGBA, top row first, alpha not premultiplied. Allocates nothing after the first call.</summary>
    /// <param name="destination">At least Width · Height · 4 bytes.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short.</exception>
    /// <exception cref="GpuDeviceLostException">The device was lost; the next frame makes it again, and the image is gone.</exception>
    public void ReadPixels(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _target.ReadPixels(destination);
    }

    /// <summary>
    /// Writes the image as a PNG: RGBA, 8 bits, with the sRGB chunk (design §7.5). Allocates one image of pixels
    /// (Width · Height · 4 bytes), a 64 KiB buffer, and one row.
    /// </summary>
    /// <param name="stream">Where the PNG goes; it need not seek.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="GpuDeviceLostException">The device was lost; the next frame makes it again, and the image is gone.</exception>
    public void WritePng(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var pixels = new byte[Width * Height * 4];
        _target.ReadPixels(pixels);
        PngWriter.Write(stream, pixels, Width, Height);
    }

    /// <summary>Releases the renderer's render target. The backend stays with its owner.</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _target.Dispose();
        }
    }
}
