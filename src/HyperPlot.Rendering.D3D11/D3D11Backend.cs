using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace HyperPlot.Rendering.D3D11;

/// <summary>
/// The Direct3D 11 backend (design §5.3): one device, hardware or WARP, and the offscreen render targets made on it. Use
/// <see cref="Shared"/>, or make one of your own with <see cref="Create"/> and dispose it.
/// </summary>
/// <remarks>
/// One thread at a time uses a backend and its render targets: the immediate context is not free-threaded, and the backend
/// does not check the thread. After a device loss, the call that saw it throws <see cref="GpuDeviceLostException"/>, and
/// the next frame makes the device and the render targets again.
/// </remarks>
public sealed unsafe class D3D11Backend : IGpuBackend
{
    private static readonly Lazy<D3D11Backend> _shared = new(
        () => new D3D11Backend(useWarp: false, adapterLuid: null, isShared: true, createDevice: null, text: null),
        LazyThreadSafetyMode.ExecutionAndPublication
    );

    private readonly bool _useWarp;
    private readonly long? _adapterLuid;
    private readonly bool _isShared;

    // The render targets and the host's surfaces: released on a device loss and made again with the device (design §5.3).
    private readonly List<IDeviceResource> _resources = [];
    private readonly D3D11LinePipeline _lines;
    private readonly D3D11GlyphPipeline _glyphs;
    private readonly D3D11RectPipeline _rects;
    private readonly D3D11ColumnPipeline _columns;
    private readonly D3D11MarkerPipeline _markers;
    private readonly D3D11ResidentMarkerPipeline _residentMarkers;
    private readonly D3D11SectorPipeline _sectors;
    private readonly D3D11ImagePipeline _images;
    private readonly DWriteTextBackend? _directWrite;
    private readonly D3D11FrameTimer _timer;
    private long[] _open = new long[8];
    private int _depth;
    private long _nextToken = 1;
    private long _frames;
    private ID3D11Device* _device;
    private ID3D11DeviceContext* _context;
    private State _state;

    private D3D11Backend(bool useWarp, long? adapterLuid, bool isShared, Func<D3D_DRIVER_TYPE, int>? createDevice, TextEngine? text)
    {
        (_useWarp, _adapterLuid) = (useWarp, adapterLuid);
        _isShared = isShared;
        CreateDevice = createDevice;
        _lines = new D3D11LinePipeline(this);
        _glyphs = new D3D11GlyphPipeline(this);
        _rects = new D3D11RectPipeline(this);
        _columns = new D3D11ColumnPipeline(this);
        _markers = new D3D11MarkerPipeline(this);
        _residentMarkers = new D3D11ResidentMarkerPipeline(this);
        _sectors = new D3D11SectorPipeline(this);
        _images = new D3D11ImagePipeline(this);
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

    /// <summary>
    /// Gets the locally unique identifier of the device's adapter, as a 64-bit value (the high part above the low part): a
    /// host compares it with the adapter it shows on, since a shared texture opens on the same adapter only (design §7.2).
    /// It changes when the device is made again after a loss.
    /// </summary>
    public long AdapterLuid { get; private set; }

    /// <inheritdoc />
    public GpuFrameTime? LastFrameTime => _timer.Last;

    /// <summary>Gets the GPU time of the newest timed frame's point pass. Spike R3b2.</summary>
    public GpuFrameTime? LastPassTime => _timer.LastPass;

    /// <summary>Notes which pending query sets read as ready now (<see cref="TimerProbe.ReadyQpc"/>). Spike R3b2.</summary>
    public void ProbeTimerReady()
    {
        if (_state == State.Ready)
        {
            _timer.ProbeReady();
        }
    }

    /// <inheritdoc />
    public long DeviceGeneration { get; private set; }

    /// <inheritdoc />
    /// <remarks>Shapes and rasterizes with DirectWrite (design §5.6).</remarks>
    public TextEngine Text { get; }

    /// <inheritdoc />
    public ResidencyCache Images { get; } = new();

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

    internal D3D11RectPipeline Rects => _rects;

    internal D3D11ColumnPipeline Columns => _columns;

    /// <summary>Gets the pipeline of the markers, for the tests of its buffer.</summary>
    internal D3D11MarkerPipeline Markers => _markers;

    /// <summary>Gets the pipeline of the resident markers, for the tests of its pages.</summary>
    internal D3D11ResidentMarkerPipeline ResidentMarkers => _residentMarkers;

    /// <summary>Gets the polar trapezoids' pipeline, for the tests of its buffer.</summary>
    internal D3D11SectorPipeline Sectors => _sectors;

    /// <summary>Gets the DirectWrite text backend, or null when a test gave the backend a text engine of its own.</summary>
    internal DWriteTextBackend? DirectWrite => _directWrite;

    internal D3D11FrameTimer Timer => _timer;

    /// <summary>Gets the number of frames open, for the tests of nesting.</summary>
    internal int OpenFrames => _depth;

    /// <summary>Makes a backend with a device of its own.</summary>
    /// <param name="useWarp">Use WARP even when there is hardware: for tests, which need the same pixels on every machine.</param>
    /// <param name="adapterLuid">
    /// The hardware adapter to use, by its <see cref="AdapterLuid"/>; null for the default one. When that adapter has no
    /// device of feature level 11_0 (or no adapter has the identifier), the backend falls back to WARP, as without one, and
    /// <see cref="AdapterLuid"/> tells (design §7.2).
    /// </param>
    /// <returns>The backend. Dispose it after its render targets.</returns>
    /// <exception cref="InvalidOperationException">No device could be made.</exception>
    public static D3D11Backend Create(bool useWarp = false, long? adapterLuid = null) =>
        new(useWarp, adapterLuid, isShared: false, createDevice: null, text: null);

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
        _resources.Add(target);
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
        _timer.DrainSource = own.Texture;

        // Shaping and the atlas come first, so that a text that fails draws nothing of the list (design §5.6).
        var width = own.Description.Width;
        var height = own.Description.Height;
        Text.Prepare(list, width, height);
        var steps = Text.Steps;
        _glyphs.Upload(Text.Atlas, Text.Instances);

        // The tiles the frame sends go before the steps are looked at: the cache's staging is free once sent (row G9).
        // The pages of points first: the image pipeline tells the cache that the frame's uploads were sent (row R3a).
        _residentMarkers.UploadPages(Images.Points);
        _images.Upload(Images, list.Images);
        if (steps.IsEmpty)
        {
            return;
        }

        _lines.Upload(list);
        _rects.Upload(list.Rects);
        _columns.Upload(list.Columns);
        _markers.Upload(list.Markers);
        _residentMarkers.Upload(list.ResidentCells);
        var residentRuns = list.ResidentRuns;
        _sectors.Upload(list.Sectors);
        var polylines = list.Polylines;
        var sectorCalls = list.SectorCalls;
        DrawStepKind? bound = null;
        for (var at = 0; at < steps.Length; at++)
        {
            ref readonly var step = ref steps[at];

            // The steps go in the list's order; each pipeline binds what it shares when the kind changes.
            if (step.Kind != bound)
            {
                Unbind(bound);
                switch (step.Kind)
                {
                    case DrawStepKind.Polyline:
                        _lines.Bind(own);
                        break;
                    case DrawStepKind.Glyphs:
                        _glyphs.Bind(own);
                        break;
                    case DrawStepKind.Rects:
                        _rects.Bind(own);
                        break;
                    case DrawStepKind.Sectors:
                        _sectors.Bind(own);
                        break;
                    case DrawStepKind.Images:
                        _images.Bind(own);
                        break;
                    case DrawStepKind.Markers:
                        _markers.Bind(own);
                        break;
                    case DrawStepKind.ResidentMarkers:
                        _residentMarkers.Bind(own);
                        break;
                    default:
                        _columns.Bind(own);
                        break;
                }

                bound = step.Kind;
            }

            switch (step.Kind)
            {
                case DrawStepKind.Polyline:
                    _lines.Draw(own, polylines[step.Index]);
                    break;
                case DrawStepKind.Glyphs:
                    _glyphs.Draw(own, step);
                    break;
                case DrawStepKind.Rects:
                    _rects.Draw(own, step);
                    break;
                case DrawStepKind.Sectors:
                    _sectors.Draw(own, sectorCalls[step.Index]);
                    break;
                case DrawStepKind.Images:
                    _images.Draw(own, step);
                    break;
                case DrawStepKind.Markers:
                    _markers.Draw(own, step);
                    break;
                case DrawStepKind.ResidentMarkers:
                {
                    // Spike R3b2: the consecutive resident steps go as a group, in the probe's order, timed as the point pass.
                    var end = at + 1;
                    while (end < steps.Length && steps[end].Kind == DrawStepKind.ResidentMarkers)
                    {
                        end++;
                    }

                    _timer.MarkPassStart();
                    _residentMarkers.DrawGroup(own, residentRuns, steps[at..end]);
                    _timer.MarkPassEnd();
                    at = end - 1;
                    break;
                }

                default:
                    _columns.Draw(own, step);
                    break;
            }
        }

        // Nothing stays bound, so that a readback, a clear, or a page update finds every texture free.
        Unbind(bound);
    }

    /// <inheritdoc />
    public void Copy(IRenderTarget source, IRenderTarget destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (source is not D3D11RenderTarget from || !from.BelongsTo(this) || destination is not D3D11RenderTarget to || !to.BelongsTo(this))
        {
            throw new ArgumentException("A render target belongs to another backend.", nameof(source));
        }

        if (from == to || from.Description.Width != to.Description.Width || from.Description.Height != to.Description.Height)
        {
            throw new ArgumentException("The targets are two of the same size.", nameof(destination));
        }

        from.ThrowIfDisposed();
        to.ThrowIfDisposed();
        using var frame = BeginFrame();
        _context->CopyResource((ID3D11Resource*)to.Texture, (ID3D11Resource*)from.Texture);
    }

    private void Unbind(DrawStepKind? kind)
    {
        switch (kind)
        {
            case DrawStepKind.Polyline:
                _lines.Unbind();
                break;
            case DrawStepKind.Glyphs:
                _glyphs.Unbind();
                break;
            case DrawStepKind.Rects:
                _rects.Unbind();
                break;
            case DrawStepKind.Columns:
                _columns.Unbind();
                break;
            case DrawStepKind.Sectors:
                _sectors.Unbind();
                break;
            case DrawStepKind.Images:
                _images.Unbind();
                break;
            case DrawStepKind.Markers:
                _markers.Unbind();
                break;
            case DrawStepKind.ResidentMarkers:
                _residentMarkers.Unbind();
                break;
            default:
                break;
        }
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

        foreach (var resource in _resources.ToArray())
        {
            resource.Dispose();
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

    /// <summary>Adds a resource made on the device, so that a loss releases it and the next frame makes it again.</summary>
    internal void Add(IDeviceResource resource) => _resources.Add(resource);

    internal void Remove(IDeviceResource resource) => _resources.Remove(resource);

    /// <summary>Gets a value indicating whether the device is there: false between a loss and the next frame.</summary>
    internal bool IsReady => _state == State.Ready;

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
        foreach (var resource in _resources)
        {
            resource.ReleaseResources();
        }

        ReleaseDevice();

        // The tiles were on the lost device: the cache forgets them and sends the visible ones again (invariant 10).
        Images.Reset();
        _state = State.Lost;
        throw new GpuDeviceLostException(message, result, reason);
    }

    private void MakeAgain()
    {
        MakeDevice();
        try
        {
            foreach (var resource in _resources)
            {
                resource.MakeResources();
            }
        }
        catch (InvalidOperationException)
        {
            // A target that failed to come back leaves the backend lost, with nothing half made, so that the next frame
            // tries the whole remake again (design §5.3). A loss in the middle has already done this through Lose.
            foreach (var resource in _resources)
            {
                resource.ReleaseResources();
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
        DeviceGeneration++;
        try
        {
            _lines.Make();
            _glyphs.Make();
            _rects.Make();
            _columns.Make();
            _markers.Make();
            _residentMarkers.Make();
            _sectors.Make();
            _images.Make();
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

        // The adapter asked for (design §7.2): hardware only, found by its identifier. A device on an adapter is made with
        // the unknown driver type.
        IDXGIAdapter* adapter = null;
        if (driver == D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE && _adapterLuid is { } luid)
        {
            var found = FindAdapter(luid, &adapter);
            if (found < 0)
            {
                return found;
            }
        }

        // Feature level 11_0 or above (requirement §3); BGRA for the WPF host's shared textures and the render targets.
        var levels = stackalloc D3D_FEATURE_LEVEL[] { D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0 };
        ID3D11Device* device;
        ID3D11DeviceContext* context;
        D3D_FEATURE_LEVEL level;
        var result = DirectX.D3D11CreateDevice(
            adapter,
            adapter == null ? driver : D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN,
            HMODULE.NULL,
            (uint)D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            levels,
            2,
            TerraFX.Interop.DirectX.D3D11.D3D11_SDK_VERSION,
            &device,
            &level,
            &context
        );
        if (adapter != null)
        {
            _ = adapter->Release();
        }

        if (result.FAILED)
        {
            return result;
        }

        _device = device;
        _context = context;
        IsWarp = driver == D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP;
        AdapterLuid = ReadAdapterLuid(device);
        return result;
    }

    /// <summary>Finds the adapter with an identifier through DXGI; the caller releases it.</summary>
    private static int FindAdapter(long luid, IDXGIAdapter** adapter)
    {
        IDXGIFactory4* factory;
        var iid = IID.IID_IDXGIFactory4;
        var result = DirectX.CreateDXGIFactory2(0, &iid, (void**)&factory);
        if (result.FAILED)
        {
            return result;
        }

        var id = new LUID { LowPart = (uint)luid, HighPart = (int)(luid >> 32) };
        var adapterId = IID.IID_IDXGIAdapter;
        result = factory->EnumAdapterByLuid(id, &adapterId, (void**)adapter);
        _ = factory->Release();
        return result;
    }

    /// <summary>Reads the identifier of a device's adapter.</summary>
    private static long ReadAdapterLuid(ID3D11Device* device)
    {
        IDXGIDevice* dxgi;
        var iid = IID.IID_IDXGIDevice;
        if (((IUnknown*)device)->QueryInterface(&iid, (void**)&dxgi).FAILED)
        {
            return 0;
        }

        IDXGIAdapter* adapter;
        var result = dxgi->GetAdapter(&adapter);
        _ = dxgi->Release();
        if (result.FAILED)
        {
            return 0;
        }

        DXGI_ADAPTER_DESC description;
        result = adapter->GetDesc(&description);
        _ = adapter->Release();
        return result.FAILED ? 0 : ((long)description.AdapterLuid.HighPart << 32) | description.AdapterLuid.LowPart;
    }

    private void ReleaseDevice()
    {
        _timer.Release();
        _images.Release();
        _sectors.Release();
        _columns.Release();
        _markers.Release();
        _residentMarkers.Release();
        _rects.Release();
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
        new(useWarp, adapterLuid: null, isShared: false, createDevice, text);
}
