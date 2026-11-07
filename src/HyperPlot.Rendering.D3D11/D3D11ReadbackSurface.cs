using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace HyperPlot.Rendering.D3D11;

/// <summary>
/// Two staging textures a host reads a render target through when it cannot open a shared texture (design §7.2, row W1a):
/// software rendering, a lost front buffer, or a device on another adapter. <see cref="Present"/> copies into the older one,
/// and <see cref="TryRead"/> reads the newest finished copy without waiting for the GPU (invariant 7), so the host shows
/// each frame one frame late.
/// </summary>
/// <remarks>
/// One thread at a time uses a surface, the one that uses its backend. The pixels are <c>B8G8R8A8</c> with premultiplied
/// alpha, as WPF's <c>Pbgra32</c>. After a device loss the next frame makes the textures again, and the copies before it are
/// gone.
/// </remarks>
public sealed unsafe class D3D11ReadbackSurface : IDisposable, IDeviceResource
{
    private readonly D3D11Backend _backend;
    private readonly Slot[] _slots = new Slot[2];

    // The number of the last copy, and of the last copy read.
    private long _copies;
    private long _read;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="D3D11ReadbackSurface"/> class on a backend's device.</summary>
    /// <param name="backend">The backend whose render targets the surface copies. Its owner disposes it, after the surface.</param>
    /// <param name="width">The width in pixels, from 1 to 16,384: the render targets' it copies.</param>
    /// <param name="height">The height in pixels, from 1 to 16,384.</param>
    /// <exception cref="ArgumentNullException"><paramref name="backend"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A side is out of range.</exception>
    /// <exception cref="ObjectDisposedException">The backend was disposed.</exception>
    /// <exception cref="GpuDeviceLostException">The device was lost while the textures were made.</exception>
    public D3D11ReadbackSurface(D3D11Backend backend, int width, int height)
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
    /// Copies a render target into the older staging texture (the one not copied last). Nothing waits for the GPU. Opens a
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
        ref var slot = ref _slots[_slots[0].Copy <= _slots[1].Copy ? 0 : 1];
        _backend.Context->CopyResource((ID3D11Resource*)slot.Texture, (ID3D11Resource*)target.Texture);
        _backend.Context->Flush();
        slot.Copy = ++_copies;
    }

    /// <summary>
    /// Reads the newest copy the GPU has finished that has not been read, without waiting: the staging texture is mapped
    /// with <c>D3D11_MAP_FLAG_DO_NOT_WAIT</c>, and a copy still being made (<c>DXGI_ERROR_WAS_STILL_DRAWING</c>) leaves the
    /// destination as it was.
    /// </summary>
    /// <param name="destination">The pixels, <c>B8G8R8A8</c> premultiplied, row after row <paramref name="stride"/> bytes apart.</param>
    /// <param name="stride">The bytes from a row's start to the next one's: at least Width · 4.</param>
    /// <returns>True when a newer copy was written to the destination; false when none is done, or the device is lost.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The stride is below Width · 4.</exception>
    /// <exception cref="ArgumentException">The destination holds fewer than stride · (Height − 1) + Width · 4 bytes.</exception>
    /// <exception cref="ObjectDisposedException">The surface or the backend was disposed.</exception>
    /// <exception cref="GpuDeviceLostException">The device was lost while a texture was mapped.</exception>
    public bool TryRead(Span<byte> destination, int stride)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _backend.ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, Width * 4);
        var needed = ((long)stride * (Height - 1)) + (Width * 4);
        if (destination.Length < needed)
        {
            throw new ArgumentException($"The destination holds {destination.Length} bytes; the surface needs {needed}.", nameof(destination));
        }

        if (!_backend.IsReady)
        {
            return false;
        }

        // The newer slot first: once it is done, the older one is not wanted.
        var newer = _slots[0].Copy >= _slots[1].Copy ? 0 : 1;
        foreach (var index in (ReadOnlySpan<int>)[newer, 1 - newer])
        {
            ref var slot = ref _slots[index];
            if (slot.Copy <= _read)
            {
                continue;
            }

            D3D11_MAPPED_SUBRESOURCE mapped;
            var result = _backend.Context->Map(
                (ID3D11Resource*)slot.Texture,
                0,
                D3D11_MAP.D3D11_MAP_READ,
                (uint)D3D11_MAP_FLAG.D3D11_MAP_FLAG_DO_NOT_WAIT,
                &mapped
            );
            if (result == DXGI.DXGI_ERROR_WAS_STILL_DRAWING)
            {
                continue;
            }

            _backend.Check(result, "Map");
            try
            {
                // The mapped rows are RowPitch bytes apart; the spans bound every read and write.
                var rowBytes = Width * 4;
                var source = new ReadOnlySpan<byte>(mapped.pData, (int)(mapped.RowPitch * (uint)(Height - 1)) + rowBytes);
                for (var y = 0; y < Height; y++)
                {
                    source.Slice((int)(y * mapped.RowPitch), rowBytes).CopyTo(destination.Slice(y * stride, rowBytes));
                }
            }
            finally
            {
                _backend.Context->Unmap((ID3D11Resource*)slot.Texture, 0);
            }

            _read = slot.Copy;
            return true;
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

    /// <summary>Makes the two staging textures; nothing is copied.</summary>
    void IDeviceResource.MakeResources() => MakeResources();

    /// <summary>Releases the staging textures.</summary>
    void IDeviceResource.ReleaseResources() => ReleaseResources();

    private void MakeResources()
    {
        var description = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
            Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
        };
        for (var i = 0; i < _slots.Length; i++)
        {
            ID3D11Texture2D* texture;
            _backend.Check(_backend.Device->CreateTexture2D(&description, null, &texture), "CreateTexture2D");
            _slots[i] = new Slot { Texture = texture };
        }

        (_copies, _read) = (0, 0);
    }

    private void ReleaseResources()
    {
        for (var i = 0; i < _slots.Length; i++)
        {
            if (_slots[i].Texture != null)
            {
                _ = _slots[i].Texture->Release();
            }

            _slots[i] = default;
        }

        (_copies, _read) = (0, 0);
    }

    /// <summary>A staging texture and the number of the copy it holds (0 for none).</summary>
    private struct Slot
    {
        public ID3D11Texture2D* Texture;
        public long Copy;
    }
}
