using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace HyperPlot.Rendering.D3D11;

/// <summary>
/// The Direct3D 11 backend (design §5.3): one device, hardware or WARP, and the offscreen render targets made on it. Use
/// <see cref="Shared"/>, or make one of your own with <see cref="Create(bool)"/> and dispose it.
/// </summary>
/// <remarks>
/// One thread at a time uses a backend and its render targets: the immediate context is not free-threaded, and the backend
/// does not check the thread. After a device loss, the call that saw it throws <see cref="GpuDeviceLostException"/>, and
/// the next frame makes the device and the render targets again.
/// </remarks>
public sealed unsafe class D3D11Backend : IGpuBackend
{
    private static readonly Lazy<D3D11Backend> _shared = new(
        () => new D3D11Backend(useWarp: false, isShared: true, createDevice: null, text: null),
        LazyThreadSafetyMode.ExecutionAndPublication
    );

    private readonly bool _useWarp;
    private readonly bool _isShared;
    private readonly List<D3D11RenderTarget> _targets = [];
    private readonly D3D11LinePipeline _lines;
    private readonly D3D11GlyphPipeline _glyphs;
    private readonly DWriteTextBackend? _directWrite;
    private readonly D3D11FrameTimer _timer;
    private long[] _open = new long[8];
    private int _depth;
    private long _nextToken = 1;
    private long _frames;
    private ID3D11Device* _device;
    private ID3D11DeviceContext* _context;
    private State _state;

    private D3D11Backend(bool useWarp, bool isShared, Func<D3D_DRIVER_TYPE, int>? createDevice, TextEngine? text)
    {
        _useWarp = useWarp;
        _isShared = isShared;
        CreateDevice = createDevice;
        _lines = new D3D11LinePipeline(this);
        _glyphs = new D3D11GlyphPipeline(this);
        _timer = new D3D11FrameTimer(this);
        if (text is null)
        {
            _directWrite = new DWriteTextBackend();
            text = new TextEngine(_directWrite);
        }

        Text = text;
        try
        {
            MakeDevice();
        }
        catch
        {
            _directWrite?.Dispose();
            throw;
        }
    }

    private enum State
    {
        Ready,
        Lost,
        Disposed,
    }

    /// <summary>
    /// Gets the backend every plot shares, made when first read: hardware, or WARP when there is none. It lives until the
    /// process ends; its <see cref="Dispose"/> does nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">No device could be made, WARP included.</exception>
    public static D3D11Backend Shared => _shared.Value;

    /// <inheritdoc />
    public bool IsWarp { get; private set; }

    /// <inheritdoc />
    public GpuFrameTime? LastFrameTime => _timer.Last;

    /// <inheritdoc />
    /// <remarks>Shapes and rasterizes with DirectWrite (design §5.6).</remarks>
    public TextEngine Text { get; }

    /// <summary>Gets or sets a test hook that replaces the HRESULT of a checked call; the name of the call comes first.</summary>
    internal Func<string, int, int>? CheckResult { get; set; }

    /// <summary>Gets or sets a test hook that replaces what <c>GetDeviceRemovedReason</c> returns.</summary>
    internal Func<int>? RemovedReason { get; set; }

    /// <summary>Gets or sets a test hook that replaces the HRESULT of <c>D3D11CreateDevice</c> for a driver type.</summary>
    internal Func<D3D_DRIVER_TYPE, int>? CreateDevice { get; set; }

    internal ID3D11Device* Device => _device;

    internal ID3D11DeviceContext* Context => _context;

    internal D3D11LinePipeline Lines => _lines;

    internal D3D11GlyphPipeline Glyphs => _glyphs;

    /// <summary>Gets the DirectWrite text backend, or null when a test gave the backend a text engine of its own.</summary>
    internal DWriteTextBackend? DirectWrite => _directWrite;

    internal D3D11FrameTimer Timer => _timer;

    /// <summary>Gets the number of frames open, for the tests of nesting.</summary>
    internal int OpenFrames => _depth;

    /// <summary>Makes a backend with a device of its own.</summary>
    /// <param name="useWarp">Use WARP even when there is hardware: for tests, which need the same pixels on every machine.</param>
    /// <returns>The backend. Dispose it after its render targets.</returns>
    /// <exception cref="InvalidOperationException">No device could be made.</exception>
    public static D3D11Backend Create(bool useWarp = false) => new(useWarp, isShared: false, createDevice: null, text: null);

    /// <inheritdoc />
    public IRenderTarget CreateRenderTarget(in RenderTargetDescription description)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(description.Width, 1, nameof(description));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(description.Width, RenderTargetDescription.MaxSide, nameof(description));
        ArgumentOutOfRangeException.ThrowIfLessThan(description.Height, 1, nameof(description));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(description.Height, RenderTargetDescription.MaxSide, nameof(description));
        if (description.Depth != DepthFormat.None)
        {
            throw new NotSupportedException("A depth texture comes with the 3D rows after v1 (design §13.1).");
        }

        using var frame = BeginFrame();
        var target = new D3D11RenderTarget(this, description);
        _targets.Add(target);
        return target;
    }

    /// <inheritdoc />
    public FrameScope BeginFrame()
    {
        ObjectDisposedException.ThrowIf(_state == State.Disposed, this);
        if (_state == State.Lost)
        {
            MakeAgain();
        }

        if (_depth == _open.Length)
        {
            Array.Resize(ref _open, _open.Length * 2);
        }

        var token = _nextToken++;
        _open[_depth++] = token;
        if (_depth == 1)
        {
            // The outermost frame: it gets a number and, if a query set is free, its GPU time (design §5.2).
            _timer.Begin(_frames++);
        }

        return new FrameScope(this, token);
    }

    /// <inheritdoc />
    public void Draw(IRenderTarget target, RenderList list)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(list);
        if (target is not D3D11RenderTarget own || !own.BelongsTo(this))
        {
            throw new ArgumentException("The render target belongs to another backend.", nameof(target));
        }

        own.ThrowIfDisposed();
        using var frame = BeginFrame();

        // Shaping and the atlas come first, so that a text that fails draws nothing of the list (design §5.6).
        var width = own.Description.Width;
        var height = own.Description.Height;
        Text.Prepare(list, width, height);
        var steps = Text.Steps;
        _glyphs.Upload(Text.Atlas, Text.Instances);
        if (steps.IsEmpty)
        {
            return;
        }

        _lines.Upload(list);
        var polylines = list.Polylines;
        var bound = DrawStepKind.Polyline;
        var any = false;
        foreach (ref readonly var step in steps)
        {
            // The steps go in the list's order; each pipeline binds what it shares when the kind changes.
            if (!any || step.Kind != bound)
            {
                if (step.Kind == DrawStepKind.Polyline)
                {
                    _glyphs.Unbind();
                    _lines.Bind(own);
                }
                else
                {
                    _lines.Unbind();
                    _glyphs.Bind(own);
                }

                bound = step.Kind;
                any = true;
            }

            if (step.Kind == DrawStepKind.Polyline)
            {
                _lines.Draw(own, polylines[step.Index]);
            }
            else
            {
                _glyphs.Draw(own, step);
            }
        }

        // Nothing stays bound, so that a readback, a clear, or a page update finds every texture free.
        _lines.Unbind();
        _glyphs.Unbind();
    }

    /// <inheritdoc />
    public void EndFrame(long token)
    {
        // A token that is not open (closed already, 0, or of a disposed backend) does nothing: closing never throws for
        // the order of the calls (design §5.2).
        if (token <= 0 || _depth == 0)
        {
            return;
        }

        var at = Array.LastIndexOf(_open, token, _depth - 1, _depth);
        if (at < 0)
        {
            return;
        }

        // The frame and every frame still open inside it close.
        _depth = at;
        if (_state != State.Ready)
        {
            return;
        }

        // Clears and copies return no HRESULT, so closing asks the device once whether it is still there.
        var reason = ReadRemovedReason();
        if (reason != S.S_OK)
        {
            Lose(reason, reason, "The device was lost during the frame.");
        }

        // A frame that saw a loss has no time, even if an inner call made the device again: the loss released its queries,
        // and the timer made again has no frame open (design §5.2).
        if (_depth == 0)
        {
            _timer.End();
        }
    }

    /// <summary>Releases the device and every render target still alive. Does nothing for <see cref="Shared"/>.</summary>
    public void Dispose()
    {
        if (_isShared || _state == State.Disposed)
        {
            return;
        }

        foreach (var target in _targets.ToArray())
        {
            target.Dispose();
        }

        ReleaseDevice();
        _directWrite?.Dispose();
        _state = State.Disposed;
    }

    /// <summary>Throws for a failed HRESULT: <see cref="GpuDeviceLostException"/> for a loss, after marking the backend lost.</summary>
    internal void Check(int result, string call)
    {
        if (CheckResult is { } hook)
        {
            result = hook(call, result);
        }

        if (result >= 0)
        {
            return;
        }

        if (IsDeviceLost(result))
        {
            var reason = ReadRemovedReason();
            Lose(result, reason == S.S_OK ? result : reason, $"The device was lost in {call}.");
        }

        throw new InvalidOperationException($"{call} failed with 0x{result:X8}.") { HResult = result };
    }

    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_state == State.Disposed, this);

    internal void Remove(D3D11RenderTarget target) => _targets.Remove(target);

    private static bool IsDeviceLost(int result) =>
        result
            is DXGI.DXGI_ERROR_DEVICE_REMOVED
                or DXGI.DXGI_ERROR_DEVICE_RESET
                or DXGI.DXGI_ERROR_DEVICE_HUNG
                or DXGI.DXGI_ERROR_DRIVER_INTERNAL_ERROR;

    private int ReadRemovedReason() => RemovedReason is { } hook ? hook() : (int)_device->GetDeviceRemovedReason();

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private void Lose(int result, int reason, string message)
    {
        foreach (var target in _targets)
        {
            target.ReleaseResources();
        }

        ReleaseDevice();
        _state = State.Lost;
        throw new GpuDeviceLostException(message, result, reason);
    }

    private void MakeAgain()
    {
        MakeDevice();
        try
        {
            foreach (var target in _targets)
            {
                target.MakeResources();
            }
        }
        catch (InvalidOperationException)
        {
            // A target that failed to come back leaves the backend lost, with nothing half made, so that the next frame
            // tries the whole remake again (design §5.3). A loss in the middle has already done this through Lose.
            foreach (var target in _targets)
            {
                target.ReleaseResources();
            }

            ReleaseDevice();
            _state = State.Lost;
            throw;
        }
    }

    /// <summary>Makes the device: hardware first unless WARP is asked for, then WARP. Leaves the backend lost if both fail.</summary>
    private void MakeDevice()
    {
        var result = _useWarp ? E.E_FAIL : TryMakeDevice(D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE);
        if (result < 0)
        {
            result = TryMakeDevice(D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP);
        }

        if (result < 0)
        {
            _state = State.Lost;
            throw new InvalidOperationException($"D3D11CreateDevice failed with 0x{result:X8}, WARP included.") { HResult = result };
        }

        _state = State.Ready;
        try
        {
            _lines.Make();
            _glyphs.Make();
            _timer.Make();
        }
        catch (InvalidOperationException)
        {
            // The pipeline is part of the device: without it the backend stays lost, and the next frame tries again.
            ReleaseDevice();
            _state = State.Lost;
            throw;
        }
    }

    private int TryMakeDevice(D3D_DRIVER_TYPE driver)
    {
        if (CreateDevice is { } hook && hook(driver) is var forced && forced < 0)
        {
            return forced;
        }

        // Feature level 11_0 or above (requirement §3); BGRA for the WPF host's shared textures and the render targets.
        var levels = stackalloc D3D_FEATURE_LEVEL[] { D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0 };
        ID3D11Device* device;
        ID3D11DeviceContext* context;
        D3D_FEATURE_LEVEL level;
        var result = DirectX.D3D11CreateDevice(
            null,
            driver,
            HMODULE.NULL,
            (uint)D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            levels,
            2,
            TerraFX.Interop.DirectX.D3D11.D3D11_SDK_VERSION,
            &device,
            &level,
            &context
        );
        if (result.FAILED)
        {
            return result;
        }

        _device = device;
        _context = context;
        IsWarp = driver == D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP;
        return result;
    }

    private void ReleaseDevice()
    {
        _timer.Release();
        _glyphs.Release();
        _lines.Release();
        if (_context != null)
        {
            _context->ClearState();
            _ = _context->Release();
            _context = null;
        }

        if (_device != null)
        {
            _ = _device->Release();
            _device = null;
        }
    }

    /// <summary>Makes a backend for a test: a hook on making the device, and a text engine of the test's own (design §10).</summary>
    internal static D3D11Backend CreateForTest(bool useWarp, Func<D3D_DRIVER_TYPE, int>? createDevice, TextEngine? text = null) =>
        new(useWarp, isShared: false, createDevice, text);
}
