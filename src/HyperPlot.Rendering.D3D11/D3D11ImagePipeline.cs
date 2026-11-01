using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace HyperPlot.Rendering.D3D11;

/// <summary>
/// The GPU objects of the image tiles (design §5.2, §5.3, row G9): the shaders, the blend and rasterizer states, the constant
/// buffer, the structured buffer of tile records, a texture array a page of the residency cache (R32_FLOAT for numbers,
/// R8G8B8A8_UNORM for colors, faces of 258 × 258), and the texture of the color tables. Made with the device; after a loss the
/// cache forgets its slots and sends the visible tiles again from Core.
/// </summary>
internal sealed unsafe class D3D11ImagePipeline
{
    private readonly D3D11Backend _backend;
    private readonly List<Page>[] _pages =
    [
        [],
        [],
    ];

    private ID3D11VertexShader* _vertexShader;
    private ID3D11PixelShader* _pixelShader;
    private ID3D11BlendState* _overBlend;
    private ID3D11RasterizerState* _rasterizer;
    private ID3D11Buffer* _constants;
    private ID3D11Buffer* _instances;
    private ID3D11ShaderResourceView* _instanceView;
    private ID3D11Texture2D* _tables;
    private ID3D11ShaderResourceView* _tableView;
    private int _instanceCapacity;

    internal D3D11ImagePipeline(D3D11Backend backend) => _backend = backend;

    internal void Make()
    {
        var device = _backend.Device;
        fixed (byte* code = Shaders.ImageVS)
        {
            ID3D11VertexShader* shader;
            _backend.Check(device->CreateVertexShader(code, (nuint)Shaders.ImageVS.Length, null, &shader), "CreateVertexShader");
            _vertexShader = shader;
        }

        fixed (byte* code = Shaders.ImagePS)
        {
            ID3D11PixelShader* shader;
            _backend.Check(device->CreatePixelShader(code, (nuint)Shaders.ImagePS.Length, null, &shader), "CreatePixelShader");
            _pixelShader = shader;
        }

        // Premultiplied color over the target, as the rectangles (design §5.3).
        var blend = default(D3D11_BLEND_DESC);
        blend.RenderTarget[0] = new D3D11_RENDER_TARGET_BLEND_DESC
        {
            BlendEnable = BOOL.TRUE,
            SrcBlend = D3D11_BLEND.D3D11_BLEND_ONE,
            DestBlend = D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA,
            BlendOp = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD,
            SrcBlendAlpha = D3D11_BLEND.D3D11_BLEND_ONE,
            DestBlendAlpha = D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA,
            BlendOpAlpha = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD,
            RenderTargetWriteMask = (byte)D3D11_COLOR_WRITE_ENABLE.D3D11_COLOR_WRITE_ENABLE_ALL,
        };
        ID3D11BlendState* blendState;
        _backend.Check(device->CreateBlendState(&blend, &blendState), "CreateBlendState");
        _overBlend = blendState;

        var rasterizer = new D3D11_RASTERIZER_DESC
        {
            FillMode = D3D11_FILL_MODE.D3D11_FILL_SOLID,
            CullMode = D3D11_CULL_MODE.D3D11_CULL_NONE,
            DepthClipEnable = BOOL.TRUE,
            ScissorEnable = BOOL.TRUE,
        };
        ID3D11RasterizerState* state;
        _backend.Check(device->CreateRasterizerState(&rasterizer, &state), "CreateRasterizerState");
        _rasterizer = state;

        var constants = new D3D11_BUFFER_DESC
        {
            ByteWidth = (uint)sizeof(ImageConstants),
            Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
        };
        ID3D11Buffer* buffer;
        _backend.Check(device->CreateBuffer(&constants, null, &buffer), "CreateBuffer");
        _constants = buffer;

        // The color tables: a row of 256 straight colors a table (design §4.2).
        var tables = new D3D11_TEXTURE2D_DESC
        {
            Width = 256,
            Height = ResidencyCache.MaxColorTables,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
        };
        ID3D11Texture2D* texture;
        _backend.Check(device->CreateTexture2D(&tables, null, &texture), "CreateTexture2D");
        _tables = texture;
        ID3D11ShaderResourceView* view;
        _backend.Check(device->CreateShaderResourceView((ID3D11Resource*)_tables, null, &view), "CreateShaderResourceView");
        _tableView = view;
        _instanceCapacity = 0;
    }

    internal void Release()
    {
        foreach (var pages in _pages)
        {
            foreach (var page in pages)
            {
                var view = page.View;
                var texture = page.Texture;
                Release(ref view);
                Release(ref texture);
            }

            pages.Clear();
        }

        Release(ref _tableView);
        Release(ref _tables);
        Release(ref _instanceView);
        Release(ref _instances);
        Release(ref _constants);
        Release(ref _rasterizer);
        Release(ref _overBlend);
        Release(ref _pixelShader);
        Release(ref _vertexShader);
        _instanceCapacity = 0;
    }

    /// <summary>
    /// Sends what the frame needs: the cache's new pages as texture arrays, its staged tiles and color tables, and the list's
    /// tile records. Then the cache's staging is free again.
    /// </summary>
    internal void Upload(ResidencyCache cache, ReadOnlySpan<ImageInstance> images)
    {
        var context = _backend.Context;
        for (var format = 0; format < 2; format++)
        {
            while (_pages[format].Count < cache.PageCount((ResidencyCache.Format)format))
            {
                _pages[format].Add(MakePage((ResidencyCache.Format)format));
            }
        }

        foreach (ref readonly var upload in cache.Uploads)
        {
            var page = _pages[(int)upload.Format][upload.Slot / ResidencyCache.PageSlots];
            var face = cache.Face(upload);
            fixed (uint* source = face)
            {
                context->UpdateSubresource(
                    (ID3D11Resource*)page.Texture,
                    (uint)(upload.Slot % ResidencyCache.PageSlots),
                    null,
                    source,
                    ResidencyCache.Side * 4,
                    0
                );
            }
        }

        Span<uint> row = stackalloc uint[256];
        foreach (var (index, map) in cache.TableUploads)
        {
            for (var i = 0; i < 256; i++)
            {
                var color = map[i];
                row[i] = color.R | ((uint)color.G << 8) | ((uint)color.B << 16) | ((uint)color.A << 24);
            }

            var box = new D3D11_BOX(0, index, 0, 256, index + 1, 1);
            fixed (uint* source = row)
            {
                context->UpdateSubresource((ID3D11Resource*)_tables, 0, &box, source, 256 * 4, 0);
            }
        }

        cache.Sent();
        if (images.IsEmpty)
        {
            return;
        }

        if (images.Length > _instanceCapacity)
        {
            Grow(images.Length);
        }

        D3D11_MAPPED_SUBRESOURCE mapped;
        _backend.Check(context->Map((ID3D11Resource*)_instances, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map");
        images.CopyTo(new Span<ImageInstance>(mapped.pData, _instanceCapacity));
        context->Unmap((ID3D11Resource*)_instances, 0);
    }

    /// <summary>Sets what every image step shares: after a step of another kind, which binds its own.</summary>
    internal void Bind(D3D11RenderTarget target)
    {
        var context = _backend.Context;
        var viewport = new D3D11_VIEWPORT
        {
            Width = target.Description.Width,
            Height = target.Description.Height,
            MaxDepth = 1,
        };
        context->RSSetViewports(1, &viewport);
        context->RSSetState(_rasterizer);
        context->IASetInputLayout(null);
        context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        var constants = _constants;
        context->VSSetConstantBuffers(0, 1, &constants);
        var instanceView = _instanceView;
        context->VSSetShaderResources(0, 1, &instanceView);
        context->PSSetShaderResources(0, 1, &instanceView);
        var tableView = _tableView;
        context->PSSetShaderResources(3, 1, &tableView);
        context->VSSetShader(_vertexShader, null, 0);
        context->PSSetShader(_pixelShader, null, 0);
        context->OMSetBlendState(_overBlend, null, 0xFFFFFFFF);
        var view = target.View;
        context->OMSetRenderTargets(1, &view, null);
    }

    /// <summary>Draws one step: its tiles of one page under one clip, in one call.</summary>
    internal void Draw(D3D11RenderTarget target, in DrawStep step)
    {
        var width = target.Description.Width;
        var height = target.Description.Height;
        var scissor = new RECT(
            (int)Math.Clamp(step.Clip.Left, 0, width),
            (int)Math.Clamp(step.Clip.Top, 0, height),
            (int)Math.Clamp(step.Clip.Right, 0, width),
            (int)Math.Clamp(step.Clip.Bottom, 0, height)
        );
        var context = _backend.Context;
        context->RSSetScissorRects(1, &scissor);

        // The page: numbers on t1, colors on t2 (ImagePlan.PageOf).
        var (format, index) = (step.Page / 1024, step.Page % 1024);
        var page = _pages[format][index].View;
        context->PSSetShaderResources(format == 0 ? 1u : 2u, 1, &page);
        var values = new ImageConstants
        {
            InverseWidth = 1f / width,
            InverseHeight = 1f / height,
            First = (uint)step.Index,
        };
        D3D11_MAPPED_SUBRESOURCE mapped;
        _backend.Check(context->Map((ID3D11Resource*)_constants, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map");
        *(ImageConstants*)mapped.pData = values;
        context->Unmap((ID3D11Resource*)_constants, 0);
        context->DrawInstanced(4, (uint)step.Count, 0, 0);
    }

    /// <summary>Leaves nothing bound, so that a readback or a clear finds the target free.</summary>
    internal void Unbind()
    {
        var context = _backend.Context;
        ID3D11ShaderResourceView* noView = null;
        context->VSSetShaderResources(0, 1, &noView);
        for (var slot = 0u; slot < 4; slot++)
        {
            context->PSSetShaderResources(slot, 1, &noView);
        }

        context->OMSetRenderTargets(0, null, null);
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

    private Page MakePage(ResidencyCache.Format format)
    {
        var description = new D3D11_TEXTURE2D_DESC
        {
            Width = ResidencyCache.Side,
            Height = ResidencyCache.Side,
            MipLevels = 1,
            ArraySize = ResidencyCache.PageSlots,
            Format = format == ResidencyCache.Format.Float ? DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT : DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
        };
        ID3D11Texture2D* texture;
        _backend.Check(_backend.Device->CreateTexture2D(&description, null, &texture), "CreateTexture2D");
        ID3D11ShaderResourceView* view;
        _backend.Check(_backend.Device->CreateShaderResourceView((ID3D11Resource*)texture, null, &view), "CreateShaderResourceView");
        return new Page(texture, view);
    }

    private void Grow(int needed)
    {
        Release(ref _instanceView);
        Release(ref _instances);
        var capacity = Math.Max(needed, Math.Max(64, _instanceCapacity * 2));
        var description = new D3D11_BUFFER_DESC
        {
            ByteWidth = (uint)(capacity * sizeof(ImageInstance)),
            Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
            MiscFlags = (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_BUFFER_STRUCTURED,
            StructureByteStride = (uint)sizeof(ImageInstance),
        };
        ID3D11Buffer* buffer;
        _backend.Check(_backend.Device->CreateBuffer(&description, null, &buffer), "CreateBuffer");
        _instances = buffer;
        var view = new D3D11_SHADER_RESOURCE_VIEW_DESC
        {
            Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN,
            ViewDimension = D3D_SRV_DIMENSION.D3D11_SRV_DIMENSION_BUFFER,
        };
        view.Buffer.FirstElement = 0;
        view.Buffer.NumElements = (uint)capacity;
        ID3D11ShaderResourceView* resourceView;
        _backend.Check(_backend.Device->CreateShaderResourceView((ID3D11Resource*)_instances, &view, &resourceView), "CreateShaderResourceView");
        _instanceView = resourceView;
        _instanceCapacity = capacity;
    }

    /// <summary>A texture array of a page of slots and its view.</summary>
    private readonly struct Page(ID3D11Texture2D* texture, ID3D11ShaderResourceView* view)
    {
        public ID3D11Texture2D* Texture { get; } = texture;

        public ID3D11ShaderResourceView* View { get; } = view;
    }

    /// <summary>The <c>Images</c> constant buffer of Image.hlsl, 16 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ImageConstants
    {
        public float InverseWidth;
        public float InverseHeight;
        public uint First;
        public uint Unused;
    }
}
