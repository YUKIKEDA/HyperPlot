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
        () => new D3D11Backend(useWarp: false, isShared: true, createDevice: null),
        LazyThreadSafetyMode.ExecutionAndPublication
    );

    private readonly bool _useWarp;
    private readonly bool _isShared;
    private readonly List<D3D11RenderTarget> _targets = [];
    private ID3D11Device* _device;
    private ID3D11DeviceContext* _context;
    private State _state;

    private D3D11Backend(bool useWarp, bool isShared, Func<D3D_DRIVER_TYPE, int>? createDevice)
    {
        _useWarp = useWarp;
        _isShared = isShared;
        CreateDevice = createDevice;
        MakeDevice();
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

    /// <summary>Gets or sets a test hook that replaces the HRESULT of a checked call; the name of the call comes first.</summary>
    internal Func<string, int, int>? CheckResult { get; set; }

    /// <summary>Gets or sets a test hook that replaces what <c>GetDeviceRemovedReason</c> returns.</summary>
    internal Func<int>? RemovedReason { get; set; }

    /// <summary>Gets or sets a test hook that replaces the HRESULT of <c>D3D11CreateDevice</c> for a driver type.</summary>
    internal Func<D3D_DRIVER_TYPE, int>? CreateDevice { get; set; }

    internal ID3D11Device* Device => _device;

    internal ID3D11DeviceContext* Context => _context;

    /// <summary>Makes a backend with a device of its own.</summary>
    /// <param name="useWarp">Use WARP even when there is hardware: for tests, which need the same pixels on every machine.</param>
    /// <returns>The backend. Dispose it after its render targets.</returns>
    /// <exception cref="InvalidOperationException">No device could be made.</exception>
    public static D3D11Backend Create(bool useWarp = false) => new(useWarp, isShared: false, createDevice: null);

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

        BeginFrame();
        var target = new D3D11RenderTarget(this, description);
        _targets.Add(target);
        return target;
    }

    /// <inheritdoc />
    public void BeginFrame()
    {
        ObjectDisposedException.ThrowIf(_state == State.Disposed, this);
        if (_state == State.Lost)
        {
            MakeAgain();
        }
    }

    /// <inheritdoc />
    public void EndFrame()
    {
        ObjectDisposedException.ThrowIf(_state == State.Disposed, this);
        if (_state == State.Lost)
        {
            return;
        }

        // Clears and copies return no HRESULT, so the frame's end asks the device once whether it is still there.
        var reason = ReadRemovedReason();
        if (reason != S.S_OK)
        {
            Lose(reason, reason, "The device was lost during the frame.");
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

    internal static D3D11Backend CreateForTest(bool useWarp, Func<D3D_DRIVER_TYPE, int>? createDevice) => new(useWarp, isShared: false, createDevice);
}
