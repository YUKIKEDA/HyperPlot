using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace HyperPlot.Rendering.D3D11.Tests;

/// <summary>
/// A Direct3D 9Ex device on adapter 0, as WPF's <c>D3DImage</c> uses (design §7.2): it opens a shared texture's handle and
/// reads its pixels back, so that a test sees what the host would show. A shared texture opens on the same adapter only, so
/// the backend under test is made on <see cref="AdapterLuid"/>.
/// </summary>
internal sealed unsafe class D3D9Reader : IDisposable
{
    // The values of d3d9.h.
    private const uint D3D_SDK_VERSION = 32;
    private const uint D3DCREATE_FPU_PRESERVE = 0x2;
    private const uint D3DCREATE_MULTITHREADED = 0x4;
    private const uint D3DCREATE_HARDWARE_VERTEXPROCESSING = 0x40;
    private const uint D3DUSAGE_RENDERTARGET = 0x1;
    private const uint D3DLOCK_READONLY = 0x10;

    private IDirect3D9Ex* _d3d9;
    private IDirect3DDevice9Ex* _device;

    public D3D9Reader()
    {
        IDirect3D9Ex* d3d9;
        Check(Direct3DCreate9Ex(D3D_SDK_VERSION, &d3d9), "Direct3DCreate9Ex");
        _d3d9 = d3d9;
        LUID luid;
        Check(d3d9->GetAdapterLUID(0, &luid), "GetAdapterLUID");
        AdapterLuid = ((long)luid.HighPart << 32) | luid.LowPart;

        // The settings of "WPF and Direct3D9 Interoperation": a 1 × 1 back buffer, windowed, multithreaded, FPU kept.
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
        Check(
            d3d9->CreateDeviceEx(
                0,
                D3DDEVTYPE.D3DDEVTYPE_HAL,
                GetDesktopWindow(),
                D3DCREATE_HARDWARE_VERTEXPROCESSING | D3DCREATE_MULTITHREADED | D3DCREATE_FPU_PRESERVE,
                &parameters,
                null,
                &device
            ),
            "CreateDeviceEx"
        );
        _device = device;
    }

    /// <summary>Gets the identifier of adapter 0, as <see cref="D3D11Backend.AdapterLuid"/> gives it.</summary>
    public long AdapterLuid { get; }

    /// <summary>Opens a shared texture and returns its pixels, B8G8R8A8 premultiplied, row after row with no padding.</summary>
    public byte[] Read(nint handle, int width, int height)
    {
        IDirect3DTexture9* texture;
        var shared = (HANDLE)handle;
        Check(
            _device->CreateTexture(
                (uint)width,
                (uint)height,
                1,
                D3DUSAGE_RENDERTARGET,
                D3DFORMAT.D3DFMT_A8R8G8B8,
                D3DPOOL.D3DPOOL_DEFAULT,
                &texture,
                &shared
            ),
            "CreateTexture"
        );
        IDirect3DSurface9* surface = null;
        IDirect3DSurface9* system = null;
        try
        {
            Check(texture->GetSurfaceLevel(0, &surface), "GetSurfaceLevel");
            Check(
                _device->CreateOffscreenPlainSurface((uint)width, (uint)height, D3DFORMAT.D3DFMT_A8R8G8B8, D3DPOOL.D3DPOOL_SYSTEMMEM, &system, null),
                "CreateOffscreenPlainSurface"
            );
            Check(_device->GetRenderTargetData(surface, system), "GetRenderTargetData");
            D3DLOCKED_RECT locked;
            Check(system->LockRect(&locked, null, D3DLOCK_READONLY), "LockRect");
            var pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                new ReadOnlySpan<byte>((byte*)locked.pBits + (y * locked.Pitch), width * 4).CopyTo(pixels.AsSpan(y * width * 4));
            }

            _ = system->UnlockRect();
            return pixels;
        }
        finally
        {
            if (system != null)
            {
                _ = system->Release();
            }

            if (surface != null)
            {
                _ = surface->Release();
            }

            _ = texture->Release();
        }
    }

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

    private static void Check(HRESULT result, string call)
    {
        if (result.FAILED)
        {
            throw new InvalidOperationException($"{call} failed with 0x{result.Value:X8}.");
        }
    }
}
