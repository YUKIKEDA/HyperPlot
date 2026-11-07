using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace HyperPlot.Wpf;

/// <summary>
/// The Direct3D 9Ex device of a dispatcher's views (design §7.2, row W1a): on adapter 0, made as "WPF and Direct3D9
/// Interoperation" says (windowed with a 1 × 1 back buffer, multithreaded, the FPU kept), on the desktop's window, since it
/// never presents. It opens the shared textures of <see cref="Rendering.D3D11.D3D11SharedSurface"/> as surfaces
/// <c>D3DImage</c> shows.
/// </summary>
/// <remarks>Used from the dispatcher's thread only.</remarks>
internal sealed unsafe class D3D9Host : IDisposable
{
    // The values of d3d9.h.
    private const uint D3DSdkVersion = 32;
    private const uint FpuPreserve = 0x2;
    private const uint Multithreaded = 0x4;
    private const uint HardwareVertexProcessing = 0x40;
    private const uint SoftwareVertexProcessing = 0x20;
    private const uint UsageRenderTarget = 0x1;

    private IDirect3D9Ex* _d3d9;
    private IDirect3DDevice9Ex* _device;

    private D3D9Host(IDirect3D9Ex* d3d9, IDirect3DDevice9Ex* device, long luid)
    {
        _d3d9 = d3d9;
        _device = device;
        AdapterLuid = luid;
    }

    /// <summary>Gets the identifier of adapter 0, as <see cref="Rendering.D3D11.D3D11Backend.AdapterLuid"/> gives one.</summary>
    public long AdapterLuid { get; }

    /// <summary>Gets or sets a test hook that replaces what <c>CheckDeviceState</c> returns.</summary>
    internal Func<int>? DeviceState { get; set; }

    /// <summary>Makes the device, or returns null when this machine has none (no Direct3D 9Ex, or no device on adapter 0).</summary>
    public static D3D9Host? TryCreate()
    {
        IDirect3D9Ex* d3d9;
        if (Direct3DCreate9Ex(D3DSdkVersion, &d3d9).FAILED)
        {
            return null;
        }

        LUID luid;
        var device = MakeDevice(d3d9);
        if (device == null || d3d9->GetAdapterLUID(0, &luid).FAILED)
        {
            if (device != null)
            {
                _ = device->Release();
            }

            _ = d3d9->Release();
            return null;
        }

        return new D3D9Host(d3d9, device, ((long)luid.HighPart << 32) | luid.LowPart);
    }

    /// <summary>Gets a value indicating whether there is a device: false after one could not be made again.</summary>
    public bool HasDevice => _device != null;

    /// <summary>Gets or sets a value indicating whether making the device again fails, as while a driver is replaced. Tests only.</summary>
    internal bool FailMakeDevice { get; set; }

    /// <summary>
    /// Returns whether the device can still show (<c>CheckDeviceState</c>; <c>TestCooperativeLevel</c> always succeeds on
    /// 9Ex). A device that cannot is made again, and the surfaces opened on it are to be opened again; a device that could
    /// not be made is tried again (<see cref="HasDevice"/> tells whether it was).
    /// </summary>
    /// <returns>True when the device was there; false when it was made again (or could not be).</returns>
    public bool EnsureDevice()
    {
        if (_device == null)
        {
            _device = MakeDevice();
            return false;
        }

        var state = DeviceState is { } hook ? hook() : (int)_device->CheckDeviceState(HWND.NULL);
        if (state >= 0)
        {
            return true;
        }

        // A lost device is not reset: everything on it is released and made again (the same page).
        _ = _device->Release();
        _device = MakeDevice();
        return false;
    }

    /// <summary>
    /// Opens a shared texture's handle and returns its first level's surface, for <c>D3DImage.SetBackBuffer</c>; the caller
    /// releases it with <see cref="Release"/>. Null when it does not open (another adapter, or a lost device).
    /// </summary>
    public nint Open(nint handle, int width, int height)
    {
        if (_device == null)
        {
            return 0;
        }

        IDirect3DTexture9* texture;
        var shared = (HANDLE)handle;
        if (
            _device
                ->CreateTexture(
                    (uint)width,
                    (uint)height,
                    1,
                    UsageRenderTarget,
                    D3DFORMAT.D3DFMT_A8R8G8B8,
                    D3DPOOL.D3DPOOL_DEFAULT,
                    &texture,
                    &shared
                )
                .FAILED
        )
        {
            return 0;
        }

        // The surface holds a reference to its texture: releasing the surface last releases both.
        IDirect3DSurface9* surface;
        var result = texture->GetSurfaceLevel(0, &surface);
        _ = texture->Release();
        return result.FAILED ? 0 : (nint)surface;
    }

    /// <summary>Releases a surface <see cref="Open"/> gave.</summary>
    public static void Release(nint surface)
    {
        if (surface != 0)
        {
            _ = ((IUnknown*)surface)->Release();
        }
    }

    /// <summary>Releases the device and Direct3D 9Ex.</summary>
    public void Dispose()
    {
        if (_device != null)
        {
            _ = _device->Release();
            _device = null;
        }

        if (_d3d9 != null)
        {
            _ = _d3d9->Release();
            _d3d9 = null;
        }
    }

    private IDirect3DDevice9Ex* MakeDevice() => FailMakeDevice ? null : MakeDevice(_d3d9);

    private static IDirect3DDevice9Ex* MakeDevice(IDirect3D9Ex* d3d9)
    {
        // The device never presents: its window is only what CreateDeviceEx asks for, so the desktop's serves.
        foreach (var vertices in (ReadOnlySpan<uint>)[HardwareVertexProcessing, SoftwareVertexProcessing])
        {
            var parameters = new D3DPRESENT_PARAMETERS
            {
                Windowed = TRUE,
                SwapEffect = D3DSWAPEFFECT.D3DSWAPEFFECT_DISCARD,
                hDeviceWindow = GetDesktopWindow(),
                BackBufferWidth = 1,
                BackBufferHeight = 1,
                BackBufferFormat = D3DFORMAT.D3DFMT_UNKNOWN,
            };
            IDirect3DDevice9Ex* device;
            if (
                d3d9->CreateDeviceEx(
                    0,
                    D3DDEVTYPE.D3DDEVTYPE_HAL,
                    GetDesktopWindow(),
                    vertices | Multithreaded | FpuPreserve,
                    &parameters,
                    null,
                    &device
                ).SUCCEEDED
            )
            {
                return device;
            }
        }

        return null;
    }
}
