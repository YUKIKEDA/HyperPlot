using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace HyperPlot.Rendering.D3D11;

/// <summary>
/// Two shared textures a host shows (design §7.2, row W1a): <see cref="Present"/> copies a render target into the one the
/// host does not hold, and <see cref="TryTakeCompleted"/> hands over the newest copy the GPU has finished, without waiting.
/// WPF opens the handle as a Direct3D 9Ex texture for its <c>D3DImage</c>; a shared texture opens on the same adapter only
/// (<see cref="D3D11Backend.AdapterLuid"/>).
/// </summary>
/// <remarks>
/// One thread at a time uses a surface, the one that uses its backend. The textures are <c>B8G8R8A8_UNORM</c> with
/// premultiplied alpha, as the render targets are. After a device loss the next frame makes them again: the handles change,
/// and the next <see cref="TryTakeCompleted"/> after a <see cref="Present"/> gives the new one.
/// </remarks>
public sealed unsafe class D3D11SharedSurface : IDisposable, IDeviceResource
{
    private readonly D3D11Backend _backend;
    private readonly Slot[] _slots = new Slot[2];

    // The slot the host holds (−1 for none), and the number of the last copy.
    private int _held = -1;
    private long _copies;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="D3D11SharedSurface"/> class on a backend's device.</summary>
    /// <param name="backend">The backend whose render targets the surface copies. Its owner disposes it, after the surface.</param>
    /// <param name="width">The width in pixels, from 1 to 16,384: the render targets' it copies.</param>
    /// <param name="height">The height in pixels, from 1 to 16,384.</param>
    /// <exception cref="ArgumentNullException"><paramref name="backend"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A side is out of range.</exception>
    /// <exception cref="ObjectDisposedException">The backend was disposed.</exception>
    /// <exception cref="GpuDeviceLostException">The device was lost while the textures were made.</exception>
    public D3D11SharedSurface(D3D11Backend backend, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, RenderTargetDescription.MaxSide);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, RenderTargetDescription.MaxSide);
        (_backend, Width, Height) = (backend, width, height);
        using var frame = backend.BeginFrame();
        try
        {
            MakeResources();
        }
        catch
        {
            // The backend does not hold the surface yet, so nothing else would release what was made before the failure.
            ReleaseResources();
            throw;
        }

        backend.Add(this);
    }

    /// <summary>Gets the width in pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the height in pixels.</summary>
    public int Height { get; }

    /// <summary>
    /// Copies a render target into the texture the host does not hold: the other one when it holds one, the older one when
    /// it holds none (design §7.2). The copy is queued with an event query and flushed; nothing waits for the GPU. Opens a
    /// frame of the backend, or joins the caller's.
    /// </summary>
    /// <param name="source">A render target of the same backend and size.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="ArgumentException">The source belongs to another backend, or its size is not the surface's.</exception>
    /// <exception cref="ObjectDisposedException">The surface, the source, or the backend was disposed.</exception>
    /// <exception cref="GpuDeviceLostException">The device was lost.</exception>
    public void Present(IRenderTarget source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (source is not D3D11RenderTarget target || !target.BelongsTo(_backend))
        {
            throw new ArgumentException("The render target belongs to another backend.", nameof(source));
        }

        if (target.Description.Width != Width || target.Description.Height != Height)
        {
            throw new ArgumentException(
                $"The render target is {target.Description.Width} × {target.Description.Height}; the surface is {Width} × {Height}.",
                nameof(source)
            );
        }

        target.ThrowIfDisposed();
        using var frame = _backend.BeginFrame();
        var into = _held >= 0 ? 1 - _held : (_slots[0].Copy <= _slots[1].Copy ? 0 : 1);
        ref var slot = ref _slots[into];
        var context = _backend.Context;
        context->CopyResource((ID3D11Resource*)slot.Texture, (ID3D11Resource*)target.Texture);
        context->End((ID3D11Asynchronous*)slot.Query);
        context->Flush();
        slot.Copy = ++_copies;
    }

    /// <summary>
    /// Hands over the newest copy the GPU has finished that is newer than the one the host holds, without waiting (the query
    /// is read with <c>D3D11_ASYNC_GETDATA_DONOTFLUSH</c>); the host holds it from then on, and <see cref="Present"/> leaves
    /// it alone (design §7.2).
    /// </summary>
    /// <param name="sharedHandle">The texture's shared handle, to open on the same adapter; 0 when the method returns false.</param>
    /// <returns>True when a newer copy is done; false when none is, or the device is lost.</returns>
    /// <exception cref="ObjectDisposedException">The surface or the backend was disposed.</exception>
    /// <exception cref="GpuDeviceLostException">The device was lost while the query was read.</exception>
    public bool TryTakeCompleted(out nint sharedHandle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _backend.ThrowIfDisposed();
        sharedHandle = 0;
        if (!_backend.IsReady)
        {
            return false;
        }

        var heldCopy = _held >= 0 ? _slots[_held].Copy : 0;

        // The newer slot first: once it is done, the older one is not wanted.
        var newer = _slots[0].Copy >= _slots[1].Copy ? 0 : 1;
        foreach (var index in (ReadOnlySpan<int>)[newer, 1 - newer])
        {
            ref var slot = ref _slots[index];
            if (slot.Copy <= heldCopy)
            {
                continue;
            }

            var result = _backend.Context->GetData(
                (ID3D11Asynchronous*)slot.Query,
                null,
                0,
                (uint)D3D11_ASYNC_GETDATA_FLAG.D3D11_ASYNC_GETDATA_DONOTFLUSH
            );
            _backend.Check(result, "GetData");
            if (result == S.S_OK)
            {
                _held = index;
                sharedHandle = slot.Handle;
                return true;
            }
        }

        return false;
    }

    /// <summary>Releases the textures and removes the surface from its backend.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseResources();
        _backend.Remove(this);
    }

    /// <summary>Makes the two shared textures, their handles, and their queries; nothing is held or copied.</summary>
    void IDeviceResource.MakeResources() => MakeResources();

    /// <summary>Releases the textures and the queries.</summary>
    void IDeviceResource.ReleaseResources() => ReleaseResources();

    private void MakeResources()
    {
        var device = _backend.Device;
        var description = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,

            // A Direct3D 9Ex render-target texture opens the handle (design §7.2): the texture binds as a target too.
            BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE),
            MiscFlags = (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_SHARED,
        };
        var query = new D3D11_QUERY_DESC { Query = D3D11_QUERY.D3D11_QUERY_EVENT };
        for (var i = 0; i < _slots.Length; i++)
        {
            ref var slot = ref _slots[i];
            ID3D11Texture2D* texture;
            _backend.Check(device->CreateTexture2D(&description, null, &texture), "CreateTexture2D");
            slot.Texture = texture;
            IDXGIResource* resource;
            var iid = IID.IID_IDXGIResource;
            _backend.Check(((IUnknown*)texture)->QueryInterface(&iid, (void**)&resource), "QueryInterface");
            HANDLE handle;
            var result = resource->GetSharedHandle(&handle);
            _ = resource->Release();
            _backend.Check(result, "GetSharedHandle");
            slot.Handle = (nint)handle.Value;
            ID3D11Query* made;
            _backend.Check(device->CreateQuery(&query, &made), "CreateQuery");
            slot.Query = made;
            slot.Copy = 0;
        }

        _held = -1;
    }

    private void ReleaseResources()
    {
        for (var i = 0; i < _slots.Length; i++)
        {
            ref var slot = ref _slots[i];
            if (slot.Query != null)
            {
                _ = slot.Query->Release();
                slot.Query = null;
            }

            if (slot.Texture != null)
            {
                _ = slot.Texture->Release();
                slot.Texture = null;
            }

            (slot.Handle, slot.Copy) = (0, 0);
        }

        _held = -1;
    }

    /// <summary>A shared texture, its handle, its event query, and the number of the copy it holds (0 for none).</summary>
    private struct Slot
    {
        public ID3D11Texture2D* Texture;
        public ID3D11Query* Query;
        public nint Handle;
        public long Copy;
    }
}
