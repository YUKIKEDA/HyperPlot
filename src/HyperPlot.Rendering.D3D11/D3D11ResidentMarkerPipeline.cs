using System.Numerics;
using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace HyperPlot.Rendering.D3D11;

/// <summary>
/// The GPU objects of the resident markers (design §5.1 "常駐の点", §5.5.2, row R3a): the shaders, the states, the constant
/// buffer of a run, the dynamic buffer of the frame's cells, and a structured buffer for each page of points the residency
/// cache keeps. Made with the device and made again after a loss; the pages are sent again from Core then.
/// </summary>
internal sealed unsafe class D3D11ResidentMarkerPipeline
{
    private readonly D3D11Backend _backend;
    private ID3D11VertexShader* _vertexShader;
    private ID3D11PixelShader* _pixelShader;
    private ID3D11BlendState* _overBlend;
    private ID3D11RasterizerState* _rasterizer;
    private ID3D11Buffer* _constants;
    private ID3D11Buffer* _cells;
    private ID3D11ShaderResourceView* _cellView;
    private int _cellCapacity;

    // The buffer and the view of each slot of the cache's pages, 0 where the slot holds none. Pointers kept as integers:
    // an array cannot hold pointers.
    private nint[] _pages = [];
    private nint[] _pageViews = [];

    internal D3D11ResidentMarkerPipeline(D3D11Backend backend) => _backend = backend;

    /// <summary>Gets the number of pages that have a buffer. Tests only.</summary>
    internal int PageCount => _pages.Count(p => p != 0);

    internal void Make()
    {
        var device = _backend.Device;
        fixed (byte* code = Shaders.ResidentMarkerVS)
        {
            ID3D11VertexShader* shader;
            _backend.Check(device->CreateVertexShader(code, (nuint)Shaders.ResidentMarkerVS.Length, null, &shader), "CreateVertexShader");
            _vertexShader = shader;
        }

        fixed (byte* code = Shaders.MarkerPS)
        {
            ID3D11PixelShader* shader;
            _backend.Check(device->CreatePixelShader(code, (nuint)Shaders.MarkerPS.Length, null, &shader), "CreatePixelShader");
            _pixelShader = shader;
        }

        // Color over the target, premultiplied, as the other markers (design §5.3).
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
            ByteWidth = (uint)sizeof(RunConstants),
            Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
        };
        ID3D11Buffer* buffer;
        _backend.Check(device->CreateBuffer(&constants, null, &buffer), "CreateBuffer");
        _constants = buffer;
        _cellCapacity = 0;
    }

    internal void Release()
    {
        for (var slot = 0; slot < _pages.Length; slot++)
        {
            ReleasePage(slot);
        }

        Release(ref _cellView);
        Release(ref _cells);
        Release(ref _constants);
        Release(ref _rasterizer);
        Release(ref _overBlend);
        Release(ref _pixelShader);
        Release(ref _vertexShader);
        _cellCapacity = 0;
    }

    /// <summary>
    /// Releases the buffers of the pages the cache let go of and makes a buffer for each page it staged. Called before the
    /// cache is told that the frame's uploads were sent.
    /// </summary>
    internal void UploadPages(PointPages pages)
    {
        foreach (var slot in pages.Dropped)
        {
            if (slot < _pages.Length)
            {
                ReleasePage(slot);
            }
        }

        foreach (ref readonly var upload in pages.Uploads)
        {
            if (upload.Slot >= _pages.Length)
            {
                var length = Math.Max(16, Math.Max(upload.Slot + 1, _pages.Length * 2));
                Array.Resize(ref _pages, length);
                Array.Resize(ref _pageViews, length);
            }

            ReleasePage(upload.Slot);
            var points = pages.Staged(upload);
            var description = new D3D11_BUFFER_DESC
            {
                ByteWidth = (uint)(points.Length * sizeof(ResidentPoint)),
                Usage = D3D11_USAGE.D3D11_USAGE_IMMUTABLE,
                BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
                MiscFlags = (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_BUFFER_STRUCTURED,
                StructureByteStride = (uint)sizeof(ResidentPoint),
            };
            ID3D11Buffer* buffer;

            // The span bounds the bytes the device copies: ByteWidth is its length.
            fixed (ResidentPoint* data = points)
            {
                var initial = new D3D11_SUBRESOURCE_DATA { pSysMem = data };
                _backend.Check(_backend.Device->CreateBuffer(&description, &initial, &buffer), "CreateBuffer");
            }

            var view = new D3D11_SHADER_RESOURCE_VIEW_DESC
            {
                Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN,
                ViewDimension = D3D_SRV_DIMENSION.D3D11_SRV_DIMENSION_BUFFER,
            };
            view.Buffer.FirstElement = 0;
            view.Buffer.NumElements = (uint)points.Length;
            ID3D11ShaderResourceView* resourceView;
            var result = _backend.Device->CreateShaderResourceView((ID3D11Resource*)buffer, &view, &resourceView);
            if (result.FAILED)
            {
                // The buffer has no owner yet.
                _ = buffer->Release();
            }

            _backend.Check(result, "CreateShaderResourceView");
            _pages[upload.Slot] = (nint)buffer;
            _pageViews[upload.Slot] = (nint)resourceView;
        }
    }

    /// <summary>Sends the frame's cells' records, growing the buffer to twice its size when they do not fit.</summary>
    internal void Upload(ReadOnlySpan<ResidentCellRecord> cells)
    {
        if (cells.IsEmpty)
        {
            return;
        }

        if (cells.Length > _cellCapacity)
        {
            Release(ref _cellView);
            Release(ref _cells);
            var capacity = Math.Max(cells.Length, Math.Max(64, _cellCapacity * 2));
            var description = new D3D11_BUFFER_DESC
            {
                ByteWidth = (uint)(capacity * sizeof(ResidentCellRecord)),
                Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
                BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
                CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
                MiscFlags = (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_BUFFER_STRUCTURED,
                StructureByteStride = (uint)sizeof(ResidentCellRecord),
            };
            ID3D11Buffer* buffer;
            _backend.Check(_backend.Device->CreateBuffer(&description, null, &buffer), "CreateBuffer");
            _cells = buffer;
            var view = new D3D11_SHADER_RESOURCE_VIEW_DESC
            {
                Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN,
                ViewDimension = D3D_SRV_DIMENSION.D3D11_SRV_DIMENSION_BUFFER,
            };
            view.Buffer.FirstElement = 0;
            view.Buffer.NumElements = (uint)capacity;
            ID3D11ShaderResourceView* resourceView;
            _backend.Check(_backend.Device->CreateShaderResourceView((ID3D11Resource*)_cells, &view, &resourceView), "CreateShaderResourceView");
            _cellView = resourceView;
            _cellCapacity = capacity;
        }

        // The mapped memory holds capacity records; the span bounds the copy to the records there are.
        D3D11_MAPPED_SUBRESOURCE mapped;
        _backend.Check(_backend.Context->Map((ID3D11Resource*)_cells, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map");
        cells.CopyTo(new Span<ResidentCellRecord>(mapped.pData, _cellCapacity));
        _backend.Context->Unmap((ID3D11Resource*)_cells, 0);
    }

    /// <summary>Sets what every run shares: after a step of another kind, which binds its own.</summary>
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
        context->VSSetConstantBuffers(1, 1, &constants);
        var cellView = _cellView;
        context->VSSetShaderResources(2, 1, &cellView);
        context->VSSetShader(_vertexShader, null, 0);
        context->PSSetShader(_pixelShader, null, 0);
        context->OMSetBlendState(_overBlend, null, 0xFFFFFFFF);
        var view = target.View;
        context->OMSetRenderTargets(1, &view, null);
    }

    /// <summary>Draws one run: points of one page under one clip, in one call. A run whose page has no buffer draws nothing.</summary>
    internal void Draw(D3D11RenderTarget target, in ResidentMarkerRun run)
    {
        if ((uint)run.Slot >= (uint)_pageViews.Length || _pageViews[run.Slot] == 0 || run.Count <= 0)
        {
            return;
        }

        var width = target.Description.Width;
        var height = target.Description.Height;
        var scissor = new RECT(
            (int)Math.Clamp(run.Clip.Left, 0, width),
            (int)Math.Clamp(run.Clip.Top, 0, height),
            (int)Math.Clamp(run.Clip.Right, 0, width),
            (int)Math.Clamp(run.Clip.Bottom, 0, height)
        );
        var context = _backend.Context;
        context->RSSetScissorRects(1, &scissor);
        var pageView = (ID3D11ShaderResourceView*)_pageViews[run.Slot];
        context->VSSetShaderResources(1, 1, &pageView);
        var values = new RunConstants
        {
            InverseSize = new Vector2(1f / width, 1f / height),
            First = (uint)run.First,
            Cells = (uint)run.Cells,
            Size = run.Size,
            Kind = run.Kind,
            Swap = run.Swap ? 1u : 0u,
            Color = run.Color,
            ClampLow = run.ClampLow,
            ClampHigh = run.ClampHigh,
        };
        D3D11_MAPPED_SUBRESOURCE mapped;
        _backend.Check(context->Map((ID3D11Resource*)_constants, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map");
        *(RunConstants*)mapped.pData = values;
        context->Unmap((ID3D11Resource*)_constants, 0);
        context->DrawInstanced(4, (uint)run.Count, 0, 0);
    }

    /// <summary>Leaves nothing bound, so that a readback or a clear finds the target free.</summary>
    internal void Unbind()
    {
        var context = _backend.Context;
        ID3D11ShaderResourceView* noView = null;
        context->VSSetShaderResources(1, 1, &noView);
        context->VSSetShaderResources(2, 1, &noView);
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

    private void ReleasePage(int slot)
    {
        if (_pageViews[slot] != 0)
        {
            _ = ((IUnknown*)_pageViews[slot])->Release();
            _pageViews[slot] = 0;
        }

        if (_pages[slot] != 0)
        {
            _ = ((IUnknown*)_pages[slot])->Release();
            _pages[slot] = 0;
        }
    }

    /// <summary>The <c>ResidentMarkers</c> constant buffer of Markers.hlsl, 64 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RunConstants
    {
        public Vector2 InverseSize;
        public uint First;
        public uint Cells;
        public float Size;
        public uint Kind;
        public uint Swap;
        public uint Unused;
        public Vector4 Color;
        public Vector2 ClampLow;
        public Vector2 ClampHigh;
    }
}
