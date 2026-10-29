namespace HyperPlot.Rendering;

/// <summary>
/// Draws into an offscreen render target of any backend and reads it back, as pixels or a PNG (design §7.5). For tests,
/// image output, and the NativeAOT check.
/// </summary>
/// <remarks>One thread at a time uses a renderer and its backend.</remarks>
public sealed class HeadlessRenderer : IDisposable
{
    private readonly IGpuBackend _backend;
    private readonly IRenderTarget _target;
    private readonly PlotWorkerPool? _pool;
    private PlotPlan? _plan;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="HeadlessRenderer"/> class with a render target of its own.</summary>
    /// <param name="backend">The backend. Its owner disposes it, after the renderer.</param>
    /// <param name="width">The width in physical pixels, from 1 to 16,384, apart from any screen.</param>
    /// <param name="height">The height in physical pixels, from 1 to 16,384.</param>
    /// <param name="pool">
    /// The pool that builds the pyramids of the plots drawn with <see cref="Render(PlotSpec)"/>, or null for
    /// <see cref="PlotWorkerPool.Shared"/>. Its owner disposes it, after the renderer.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">A side is outside 1 to 16,384.</exception>
    public HeadlessRenderer(IGpuBackend backend, int width, int height, PlotWorkerPool? pool = null)
    {
        ArgumentNullException.ThrowIfNull(backend);

        // Checked here too, so that the exception names the caller's argument, not the backend's description.
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, RenderTargetDescription.MaxSide);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, RenderTargetDescription.MaxSide);
        _backend = backend;
        _pool = pool;
        _target = backend.CreateRenderTarget(new RenderTargetDescription(width, height));
    }

    /// <summary>Gets the width in physical pixels.</summary>
    public int Width => _target.Description.Width;

    /// <summary>Gets the height in physical pixels.</summary>
    public int Height => _target.Description.Height;

    /// <summary>Fills the image with one color.</summary>
    /// <param name="color">The color.</param>
    /// <exception cref="GpuDeviceLostException">The device was lost; the next frame makes it again, and the image is gone.</exception>
    /// <exception cref="InvalidOperationException">After a device loss, the device or the target could not be made again; the next call tries again.</exception>
    /// <exception cref="ObjectDisposedException">The renderer or its backend was disposed.</exception>
    public void Clear(Color color)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _target.Clear(color);
    }

    /// <summary>Draws a list's commands over the image (design §7.5): clear first for a background.</summary>
    /// <param name="list">The commands.</param>
    /// <exception cref="ArgumentNullException"><paramref name="list"/> is null.</exception>
    /// <exception cref="GpuDeviceLostException">The device was lost; the next frame makes it again, and the image is gone.</exception>
    /// <exception cref="InvalidOperationException">After a device loss, the device or the target could not be made again; the next call tries again.</exception>
    /// <exception cref="ObjectDisposedException">The renderer or its backend was disposed.</exception>
    public void Render(RenderList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _backend.Draw(_target, list);
    }

    /// <summary>
    /// Draws a plot over the whole image (design §7.5): compiles it against the plot drawn last and does again only what
    /// changed, waits until the indexes are built for the columns' current values, and draws the lines. Clear first for a
    /// background. Once warm, drawing the same spec again allocates nothing.
    /// </summary>
    /// <param name="plot">The plot. Its columns stay with their owner, who disposes them after the renderer.</param>
    /// <exception cref="ArgumentNullException"><paramref name="plot"/>, one of its members, or a layer is null.</exception>
    /// <exception cref="ArgumentException">
    /// The plot does not check (design §4.8): a reference that is default or names another table, a Y column that is not a
    /// <see cref="Column{T}"/>, a layer or a scale of a kind row G1 does not compile, a domain or a stroke out of range, or
    /// an x domain too narrow for the width.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A learned domain has an infinite end (design §4.2), or an X column decreases or holds NaN. A failure to read a column
    /// while building its indexes is thrown again as it was, and nothing is drawn.
    /// </exception>
    /// <exception cref="GpuDeviceLostException">The device was lost; the next frame makes it again, and the image is gone.</exception>
    /// <exception cref="ObjectDisposedException">The renderer, its backend, or its pool was disposed.</exception>
    public void Render(PlotSpec plot)
    {
        ArgumentNullException.ThrowIfNull(plot);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _plan ??= new PlotPlan(_pool ?? PlotWorkerPool.Shared, Width, Height);
        _backend.Draw(_target, _plan.Render(plot));
    }

    /// <summary>Gets the compiled plot, or null before the first <see cref="Render(PlotSpec)"/>. Tests only.</summary>
    internal PlotPlan? Plan => _plan;

    /// <summary>Reads the image as RGBA, top row first, alpha not premultiplied. Allocates nothing after the first call.</summary>
    /// <param name="destination">At least Width · Height · 4 bytes.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short.</exception>
    /// <exception cref="GpuDeviceLostException">The device was lost; the next frame makes it again, and the image is gone.</exception>
    /// <exception cref="InvalidOperationException">After a device loss, the device or the target could not be made again; the next call tries again.</exception>
    /// <exception cref="ObjectDisposedException">The renderer or its backend was disposed.</exception>
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
    /// <exception cref="InvalidOperationException">After a device loss, the device or the target could not be made again; the next call tries again.</exception>
    /// <exception cref="ObjectDisposedException">The renderer or its backend was disposed.</exception>
    public void WritePng(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var pixels = new byte[Width * Height * 4];
        _target.ReadPixels(pixels);
        PngWriter.Write(stream, pixels, Width, Height);
    }

    /// <summary>
    /// Releases the renderer's render target and the pyramids of the plot drawn last. The backend, the pool, and the
    /// columns stay with their owners.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _plan?.Dispose();
            _target.Dispose();
        }
    }
}
