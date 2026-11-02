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
    private FigurePlan? _figure;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="HeadlessRenderer"/> class with a render target of its own.</summary>
    /// <param name="backend">The backend. Its owner disposes it, after the renderer.</param>
    /// <param name="width">The width in physical pixels, from 1 to 16,384, apart from any screen.</param>
    /// <param name="height">The height in physical pixels, from 1 to 16,384.</param>
    /// <param name="pool">
    /// The pool that builds the pyramids of the plots drawn with <see cref="Render(PlotSpec)"/>, or null for
    /// <see cref="PlotWorkerPool.Shared"/>. Its owner disposes it, after the renderer.
    /// </param>
    /// <param name="scale">
    /// The scale from DIPs to physical pixels that <see cref="Render(PlotSpec)"/> applies to the theme's lengths and the
    /// layers' strokes (1.5 at 150 %, design §4.6); <see cref="Render(RenderList)"/> and <see cref="MeasureText"/> stay in
    /// physical pixels.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">A side is outside 1 to 16,384, or the scale is not finite or not above 0.</exception>
    public HeadlessRenderer(IGpuBackend backend, int width, int height, PlotWorkerPool? pool = null, float scale = 1)
    {
        ArgumentNullException.ThrowIfNull(backend);

        // Checked here too, so that the exception names the caller's argument, not the backend's description.
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, RenderTargetDescription.MaxSide);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, RenderTargetDescription.MaxSide);
        if (!float.IsFinite(scale) || scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "The scale is finite and above 0.");
        }

        Scale = scale;
        _backend = backend;
        _pool = pool;
        _target = backend.CreateRenderTarget(new RenderTargetDescription(width, height));
    }

    /// <summary>Gets the width in physical pixels.</summary>
    public int Width => _target.Description.Width;

    /// <summary>Gets the height in physical pixels.</summary>
    public int Height => _target.Description.Height;

    /// <summary>Gets the scale from DIPs to physical pixels of <see cref="Render(PlotSpec)"/> (design §4.6).</summary>
    public float Scale { get; }

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
    /// Draws a plot over the whole image (design §4.6, §7.5): compiles it against the plot drawn last and does again only
    /// what changed, waits until the indexes are built for the columns' current values, fills the image with the theme's
    /// background, lays out the guides, and draws the lines in the panel and the axes, titles, and legend around it. Once
    /// warm, drawing the same spec again allocates nothing; a new tick label allocates its string and its shaping (row I1
    /// removes that).
    /// </summary>
    /// <param name="plot">The plot. Its columns stay with their owner, who disposes them after the renderer.</param>
    /// <exception cref="ArgumentNullException"><paramref name="plot"/>, one of its members (its theme included), or a layer is null.</exception>
    /// <exception cref="ArgumentException">
    /// The plot does not check (design §4.8): a reference that is default or names another table, a Y, YMin, or YMax column
    /// that is not a <see cref="Column{T}"/>, a layer of a type the plot does not draw, a rule with both X and Y or
    /// neither, a Baseline not finite, a bar's Width not finite and above 0, a CapWidth not finite and at least 0, a step's
    /// Mode not named, a scale that is not one of the position scales of
    /// <see cref="Scale"/>, a domain or a stroke out of range, a time scale over a column that is not a long date-time
    /// column, or an x domain too narrow for the panel's width (in the scale space, or once mapped back to data units). Or
    /// a member of the theme is out of range (design §4.7): a text size not finite, not above 0, or past 512 pixels at the
    /// scale; a width or a length not finite or below 0; a tick spacing not finite or not above 0; a font family or a
    /// locale null or empty.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A learned domain has an end that is not finite, in data units or once the scale maps it (a log scale over a value at
    /// or below 0, design §4.2), or an X column, or a rule's column, decreases or holds NaN. A segment or a rect layer has
    /// more than 131,072 elements (design §5.5.3). A failure to read a column while building its
    /// indexes is thrown again as it was, and nothing is drawn. While ticks are chosen (the theme shows axes or a grid): a
    /// time scale's domain reaches outside the years 1 to 9999, a <see cref="TickFormatter"/> or
    /// <see cref="TimeTickFormatter"/> returns false or a length outside 0 to 64, or a default label does not fit 64
    /// characters (design §4.6). Shaping a text failed, or the texts' glyphs need more than 8 atlas pages (design §5.6). An
    /// exception that a user's <see cref="IScale"/>, tick format, or <see cref="IGeom"/> throws comes out as it is.
    /// </exception>
    /// <exception cref="GpuDeviceLostException">The device was lost; the next frame makes it again, and the image is gone.</exception>
    /// <exception cref="ObjectDisposedException">The renderer, its backend, or its pool was disposed.</exception>
    public void Render(PlotSpec plot)
    {
        ArgumentNullException.ThrowIfNull(plot);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // A plot alone and a figure have plans of their own: the one not drawn any more is released (design §7.5, row G6c).
        _figure?.Dispose();
        _figure = null;
        _plan ??= new PlotPlan(_pool ?? PlotWorkerPool.Shared, Width, Height, _backend.Text, Scale, _backend.Images);

        // A headless image is the same whenever it is drawn: every visible tile of an image is sent, past a frame's budget
        // (design §4.8, row G9).
        _backend.Images.Unbounded = true;
        try
        {
            var list = _plan.Render(plot);
            using var frame = _backend.BeginFrame();
            _target.Clear(plot.Theme.Background);
            _backend.Draw(_target, list);
        }
        finally
        {
            _backend.Images.Unbounded = false;
        }
    }

    /// <summary>
    /// Measures a text as <see cref="RenderList.DrawText"/> draws it with the renderer's backend (design §5.6, §7.5),
    /// through the backend's <see cref="TextEngine"/>, whose cache the next draw of the same text uses. Opens no frame.
    /// </summary>
    /// <param name="text">The text; a line feed starts a new line.</param>
    /// <param name="style">The style; only the font, the size, the weight, and the locale change the size.</param>
    /// <returns>The size in physical pixels.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/>, the font family, or the locale is null.</exception>
    /// <exception cref="ArgumentException">The font family or the locale is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The size is not finite or outside (0, 512], or the weight, an alignment, or the rotation is not a named value.
    /// </exception>
    /// <exception cref="InvalidOperationException">Shaping failed.</exception>
    /// <exception cref="ObjectDisposedException">The renderer was disposed.</exception>
    public TextMetrics MeasureText(string text, in TextStyle style)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _backend.Text.Measure(text, style);
    }

    /// <summary>
    /// Compiles a figure of several plots (design §4.5, §4.8, row G6c), waits until its plots' indexes are built, and draws it
    /// over the whole image: the figure's background, each cell in its plot's background, each plot laid out in its cell as a
    /// plot alone, and the figure's title. Nothing is drawn when a plot throws.
    /// </summary>
    /// <param name="figure">The figure.</param>
    /// <exception cref="ArgumentNullException"><paramref name="figure"/> or a member, a plot, or a cell is null.</exception>
    /// <exception cref="ArgumentException">
    /// The figure does not check (design §4.5): no plot or more than 256, more than 256 panels together, a number of columns,
    /// rows, or a span below 1, a cell outside the grid or over another, a spacing not finite or below 0; or a plot does not
    /// check, as <see cref="Render(PlotSpec)"/> says.
    /// </exception>
    /// <exception cref="InvalidOperationException">A plot fails as <see cref="Render(PlotSpec)"/> says.</exception>
    /// <exception cref="GpuDeviceLostException">The device was lost; the next frame makes it again, and the image is gone.</exception>
    /// <exception cref="ObjectDisposedException">The renderer, its backend, or its pool was disposed.</exception>
    public void Render(FigureSpec figure)
    {
        ArgumentNullException.ThrowIfNull(figure);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _plan?.Dispose();
        _plan = null;
        _figure ??= new FigurePlan(_pool ?? PlotWorkerPool.Shared, Width, Height, _backend.Text, Scale, _backend.Images);
        _backend.Images.Unbounded = true;
        try
        {
            _figure.Render(figure);
            using var frame = _backend.BeginFrame();
            _target.Clear(figure.Theme.Background);
            _backend.Draw(_target, _figure.List);
            for (var i = 0; i < _figure.Drawn; i++)
            {
                _backend.Draw(_target, _figure.Plans[i].List);
            }
        }
        finally
        {
            _backend.Images.Unbounded = false;
        }
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
            _figure?.Dispose();
            _target.Dispose();
        }
    }
}
