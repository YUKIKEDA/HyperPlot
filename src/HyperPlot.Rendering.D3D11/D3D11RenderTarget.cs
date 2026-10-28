using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace HyperPlot.Rendering.D3D11;

/// <summary>
/// An offscreen render target (design §5.3): <c>B8G8R8A8_UNORM</c>, premultiplied alpha, no MSAA, the sRGB values as they
/// are. Its staging texture for readback is made once and reused.
/// </summary>
internal sealed unsafe class D3D11RenderTarget : IRenderTarget
{
    private readonly D3D11Backend _backend;
    private ID3D11Texture2D* _texture;
    private ID3D11RenderTargetView* _view;
    private ID3D11Texture2D* _staging;
    private bool _disposed;

    internal D3D11RenderTarget(D3D11Backend backend, RenderTargetDescription description)
    {
        _backend = backend;
        Description = description;
        try
        {
            MakeResources();
        }
        catch
        {
            // The backend does not hold the target yet, so nothing else would release what was made before the failure.
            ReleaseResources();
            throw;
        }
    }

    /// <inheritdoc />
    public RenderTargetDescription Description { get; }

    /// <inheritdoc />
    public void Clear(Color color)
    {
        ThrowIfDisposed();
        _backend.BeginFrame();

        // The integer premultiplied value k, written as k / 255: the GPU's float-to-UNORM rounding gives back k exactly.
        var a = color.A;
        var rgba = stackalloc float[]
        {
            Premultiplied.Multiply(color.R, a) / 255f,
            Premultiplied.Multiply(color.G, a) / 255f,
            Premultiplied.Multiply(color.B, a) / 255f,
            a / 255f,
        };
        _backend.Context->ClearRenderTargetView(_view, rgba);
        _backend.EndFrame();
    }

    /// <inheritdoc />
    public void ReadPixels(Span<byte> destination)
    {
        ThrowIfDisposed();
        var width = Description.Width;
        var height = Description.Height;
        if (destination.Length < width * height * 4)
        {
            throw new ArgumentException(
                $"The destination holds {destination.Length} bytes; the target needs {width * height * 4}.",
                nameof(destination)
            );
        }

        _backend.BeginFrame();
        var context = _backend.Context;
        context->CopyResource((ID3D11Resource*)_staging, (ID3D11Resource*)_texture);

        // Map waits for the GPU: the headless readback the caller asked for (invariant 7).
        D3D11_MAPPED_SUBRESOURCE mapped;
        _backend.Check(context->Map((ID3D11Resource*)_staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped), "Map");
        try
        {
            // The mapped rows are RowPitch bytes apart and hold width BGRA pixels each; the spans bound every read.
            var source = new ReadOnlySpan<byte>(mapped.pData, (int)(mapped.RowPitch * (uint)(height - 1)) + (width * 4));
            for (var y = 0; y < height; y++)
            {
                var row = source.Slice((int)(y * mapped.RowPitch), width * 4);
                var output = destination.Slice(y * width * 4, width * 4);
                for (var x = 0; x < width * 4; x += 4)
                {
                    var alpha = row[x + 3];
                    output[x] = Premultiplied.Divide(row[x + 2], alpha);
                    output[x + 1] = Premultiplied.Divide(row[x + 1], alpha);
                    output[x + 2] = Premultiplied.Divide(row[x], alpha);
                    output[x + 3] = alpha;
                }
            }
        }
        finally
        {
            context->Unmap((ID3D11Resource*)_staging, 0);
        }

        _backend.EndFrame();
    }

    /// <summary>Releases the textures and removes the target from its backend.</summary>
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

    /// <summary>Makes the textures from the description and clears them to transparent (design §5.3).</summary>
    internal void MakeResources()
    {
        var device = _backend.Device;
        var description = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)Description.Width,
            Height = (uint)Description.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE),
        };
        ID3D11Texture2D* texture;
        _backend.Check(device->CreateTexture2D(&description, null, &texture), "CreateTexture2D");
        _texture = texture;

        ID3D11RenderTargetView* view;
        _backend.Check(device->CreateRenderTargetView((ID3D11Resource*)_texture, null, &view), "CreateRenderTargetView");
        _view = view;

        description.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
        description.BindFlags = 0;
        description.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
        ID3D11Texture2D* staging;
        _backend.Check(device->CreateTexture2D(&description, null, &staging), "CreateTexture2D");
        _staging = staging;

        // D3D11 leaves a new texture's contents undefined; transparent is what a read right after sees.
        var transparent = stackalloc float[4];
        _backend.Context->ClearRenderTargetView(_view, transparent);
    }

    /// <summary>Releases the textures; the description stays, so that the backend can make them again.</summary>
    internal void ReleaseResources()
    {
        Release(ref _view);
        Release(ref _staging);
        Release(ref _texture);
    }

    private static void Release<T>(ref T* pointer)
        where T : unmanaged
    {
        if (pointer != null)
        {
            _ = ((IUnknown*)pointer)->Release();
            pointer = null;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _backend.ThrowIfDisposed();
    }
}
