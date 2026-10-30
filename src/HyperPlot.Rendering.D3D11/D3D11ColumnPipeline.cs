using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace HyperPlot.Rendering.D3D11;

/// <summary>
/// The GPU objects of the column trapezoids (design §5.1, §5.3): the shaders, the blend and rasterizer states, the constant
/// buffer, and the dynamic structured buffer of trapezoid records. Made with the device and made again after a loss.
/// </summary>
internal sealed unsafe class D3D11ColumnPipeline
{
    private readonly D3D11Backend _backend;
    private ID3D11VertexShader* _vertexShader;
    private ID3D11PixelShader* _pixelShader;
    private ID3D11BlendState* _overBlend;
    private ID3D11RasterizerState* _rasterizer;
    private ID3D11Buffer* _constants;
    private ID3D11Buffer* _instances;
    private ID3D11ShaderResourceView* _instanceView;
    private int _instanceCapacity;

    internal D3D11ColumnPipeline(D3D11Backend backend) => _backend = backend;

    /// <summary>Gets the capacity of the trapezoid buffer, for the tests of its growth.</summary>
    internal int InstanceCapacity => _instanceCapacity;

    internal void Make()
    {
        var device = _backend.Device;
        fixed (byte* code = Shaders.ColumnsVS)
        {
            ID3D11VertexShader* shader;
            _backend.Check(device->CreateVertexShader(code, (nuint)Shaders.ColumnsVS.Length, null, &shader), "CreateVertexShader");
            _vertexShader = shader;
        }

        fixed (byte* code = Shaders.ColumnsPS)
        {
            ID3D11PixelShader* shader;
            _backend.Check(device->CreatePixelShader(code, (nuint)Shaders.ColumnsPS.Length, null, &shader), "CreatePixelShader");
            _pixelShader = shader;
        }

        // Color over the target, premultiplied, as the rectangles (design §5.3).
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
            ByteWidth = (uint)sizeof(ColumnConstants),
            Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
        };
        ID3D11Buffer* buffer;
        _backend.Check(device->CreateBuffer(&constants, null, &buffer), "CreateBuffer");
        _constants = buffer;
        _instanceCapacity = 0;
    }

    internal void Release()
    {
        Release(ref _instanceView);
        Release(ref _instances);
        Release(ref _constants);
        Release(ref _rasterizer);
        Release(ref _overBlend);
        Release(ref _pixelShader);
        Release(ref _vertexShader);
        _instanceCapacity = 0;
    }

    /// <summary>Sends the list's trapezoid records, growing the buffer to twice its size when they do not fit.</summary>
    internal void Upload(ReadOnlySpan<ColumnInstance> columns)
    {
        if (columns.IsEmpty)
        {
            return;
        }

        if (columns.Length > _instanceCapacity)
        {
            Grow(columns.Length);
        }

        // The mapped memory holds capacity records; the span bounds the copy to the records there are.
        D3D11_MAPPED_SUBRESOURCE mapped;
        _backend.Check(_backend.Context->Map((ID3D11Resource*)_instances, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map");
        columns.CopyTo(new Span<ColumnInstance>(mapped.pData, _instanceCapacity));
        _backend.Context->Unmap((ID3D11Resource*)_instances, 0);
    }

    /// <summary>Sets what every trapezoid step shares: after a step of another kind, which binds its own.</summary>
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
        context->VSSetShader(_vertexShader, null, 0);
        context->PSSetShader(_pixelShader, null, 0);
        context->OMSetBlendState(_overBlend, null, 0xFFFFFFFF);
        var view = target.View;
        context->OMSetRenderTargets(1, &view, null);
    }

    /// <summary>Draws one step: its trapezoids under one clip, in one call.</summary>
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
        var values = new ColumnConstants
        {
            InverseWidth = 1f / width,
            InverseHeight = 1f / height,
            First = (uint)step.Index,
        };
        D3D11_MAPPED_SUBRESOURCE mapped;
        _backend.Check(context->Map((ID3D11Resource*)_constants, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map");
        *(ColumnConstants*)mapped.pData = values;
        context->Unmap((ID3D11Resource*)_constants, 0);
        context->DrawInstanced(4, (uint)step.Count, 0, 0);
    }

    /// <summary>Leaves nothing bound, so that a readback or a clear finds the target free.</summary>
    internal void Unbind()
    {
        var context = _backend.Context;
        ID3D11ShaderResourceView* noView = null;
        context->VSSetShaderResources(0, 1, &noView);
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

    private void Grow(int needed)
    {
        Release(ref _instanceView);
        Release(ref _instances);
        var capacity = Math.Max(needed, Math.Max(64, _instanceCapacity * 2));
        var description = new D3D11_BUFFER_DESC
        {
            ByteWidth = (uint)(capacity * sizeof(ColumnInstance)),
            Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
            MiscFlags = (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_BUFFER_STRUCTURED,
            StructureByteStride = (uint)sizeof(ColumnInstance),
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

    /// <summary>The <c>Columns</c> constant buffer of Columns.hlsl, 16 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ColumnConstants
    {
        public float InverseWidth;
        public float InverseHeight;
        public uint First;
        public uint Unused;
    }
}
