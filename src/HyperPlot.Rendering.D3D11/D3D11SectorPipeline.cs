using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace HyperPlot.Rendering.D3D11;

/// <summary>
/// The GPU objects of the polar trapezoids (design §5.1, §5.3, row G11): the shaders, the blend and rasterizer states, the
/// constant buffer, and the dynamic structured buffer of pieces. Each call draws as one shape in three passes over the
/// target's 32-bit mask, which row R4's polylines share. Made with the device and made again after a loss.
/// </summary>
internal sealed unsafe class D3D11SectorPipeline
{
    private readonly D3D11Backend _backend;
    private ID3D11VertexShader* _sectorVs;
    private ID3D11VertexShader* _boundsVs;
    private ID3D11PixelShader* _maskPs;
    private ID3D11PixelShader* _colorPs;
    private ID3D11PixelShader* _zeroPs;
    private ID3D11BlendState* _overBlend;
    private ID3D11RasterizerState* _rasterizer;
    private ID3D11Buffer* _constants;
    private ID3D11Buffer* _instances;
    private ID3D11ShaderResourceView* _instanceView;
    private int _instanceCapacity;

    internal D3D11SectorPipeline(D3D11Backend backend) => _backend = backend;

    /// <summary>Gets the capacity of the piece buffer, for the tests of its growth.</summary>
    internal int InstanceCapacity => _instanceCapacity;

    internal void Make()
    {
        _sectorVs = MakeVertexShader(Shaders.SectorVS);
        _boundsVs = MakeVertexShader(Shaders.SectorBoundsVS);
        _maskPs = MakePixelShader(Shaders.SectorMaskPS);
        _colorPs = MakePixelShader(Shaders.SectorColorPS);
        _zeroPs = MakePixelShader(Shaders.SectorZeroPS);
        var device = _backend.Device;

        // The color over the target, premultiplied, as the other fills (design §5.3).
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
            ByteWidth = (uint)sizeof(SectorConstants),
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
        Release(ref _zeroPs);
        Release(ref _colorPs);
        Release(ref _maskPs);
        Release(ref _boundsVs);
        Release(ref _sectorVs);
        _instanceCapacity = 0;
    }

    /// <summary>Sends the list's pieces, growing the buffer to twice its size when they do not fit.</summary>
    internal void Upload(ReadOnlySpan<SectorInstance> sectors)
    {
        if (sectors.IsEmpty)
        {
            return;
        }

        if (sectors.Length > _instanceCapacity)
        {
            Grow(sectors.Length);
        }

        D3D11_MAPPED_SUBRESOURCE mapped;
        _backend.Check(_backend.Context->Map((ID3D11Resource*)_instances, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map");
        sectors.CopyTo(new Span<SectorInstance>(mapped.pData, _instanceCapacity));
        _backend.Context->Unmap((ID3D11Resource*)_instances, 0);
    }

    /// <summary>Sets what every sector step shares: after a step of another kind, which binds its own.</summary>
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
        context->PSSetConstantBuffers(0, 1, &constants);
        var instanceView = _instanceView;
        context->VSSetShaderResources(1, 1, &instanceView);
        context->OMSetBlendState(_overBlend, null, 0xFFFFFFFF);
    }

    /// <summary>
    /// Draws one call as one shape (design §5.3): the samples of its pieces ORed into the mask, the color once over its box
    /// times the share of samples set, and 0 back over the same box.
    /// </summary>
    internal void Draw(D3D11RenderTarget target, in SectorCall call)
    {
        var width = target.Description.Width;
        var height = target.Description.Height;
        var left = (int)Math.Clamp(Math.Max(call.Clip.Left, (long)MathF.Floor(call.Min.X)), 0, width);
        var top = (int)Math.Clamp(Math.Max(call.Clip.Top, (long)MathF.Floor(call.Min.Y)), 0, height);
        var right = (int)Math.Clamp(Math.Min(call.Clip.Right, (long)MathF.Ceiling(call.Max.X)), 0, width);
        var bottom = (int)Math.Clamp(Math.Min(call.Clip.Bottom, (long)MathF.Ceiling(call.Max.Y)), 0, height);
        if (left >= right || top >= bottom)
        {
            return;
        }

        var context = _backend.Context;
        var scissor = new RECT(left, top, right, bottom);
        context->RSSetScissorRects(1, &scissor);
        var values = new SectorConstants
        {
            InverseWidth = 1f / width,
            InverseHeight = 1f / height,
            First = (uint)call.Start,
            CenterX = call.Center.X,
            CenterY = call.Center.Y,
            Color = call.Color,
            Left = left,
            Top = top,
            Right = right,
            Bottom = bottom,
        };
        D3D11_MAPPED_SUBRESOURCE mapped;
        _backend.Check(context->Map((ID3D11Resource*)_constants, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map");
        *(SectorConstants*)mapped.pData = values;
        context->Unmap((ID3D11Resource*)_constants, 0);

        ID3D11ShaderResourceView* noView = null;
        var colorTarget = target.View;
        var mask = target.MaskAccess;
        var maskView = target.MaskResourceView;

        // 1. The samples of every piece; no render target, the mask as an unordered view.
        context->OMSetRenderTargetsAndUnorderedAccessViews(0, null, null, 0, 1, &mask, null);
        context->VSSetShader(_sectorVs, null, 0);
        context->PSSetShader(_maskPs, null, 0);
        context->DrawInstanced(4, (uint)call.Count, 0, 0);

        // 2. The color once, over the target; the mask leaves the output before it is read.
        context->OMSetRenderTargets(1, &colorTarget, null);
        context->VSSetShader(_boundsVs, null, 0);
        context->PSSetShader(_colorPs, null, 0);
        context->PSSetShaderResources(0, 1, &maskView);
        context->Draw(4, 0);

        // 3. 0 back over the same box, for the next call; the shader resource leaves first.
        context->PSSetShaderResources(0, 1, &noView);
        context->OMSetRenderTargetsAndUnorderedAccessViews(0, null, null, 0, 1, &mask, null);
        context->PSSetShader(_zeroPs, null, 0);
        context->Draw(4, 0);
    }

    /// <summary>Leaves nothing bound, so that a readback, a clear, or another pass finds the target and the mask free.</summary>
    internal void Unbind()
    {
        var context = _backend.Context;
        ID3D11ShaderResourceView* noView = null;
        context->OMSetRenderTargets(0, null, null);
        context->VSSetShaderResources(1, 1, &noView);
        context->PSSetShaderResources(0, 1, &noView);
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

    private ID3D11VertexShader* MakeVertexShader(ReadOnlySpan<byte> bytecode)
    {
        fixed (byte* code = bytecode)
        {
            ID3D11VertexShader* shader;
            _backend.Check(_backend.Device->CreateVertexShader(code, (nuint)bytecode.Length, null, &shader), "CreateVertexShader");
            return shader;
        }
    }

    private ID3D11PixelShader* MakePixelShader(ReadOnlySpan<byte> bytecode)
    {
        fixed (byte* code = bytecode)
        {
            ID3D11PixelShader* shader;
            _backend.Check(_backend.Device->CreatePixelShader(code, (nuint)bytecode.Length, null, &shader), "CreatePixelShader");
            return shader;
        }
    }

    private void Grow(int needed)
    {
        Release(ref _instanceView);
        Release(ref _instances);
        var capacity = Math.Max(needed, Math.Max(64, _instanceCapacity * 2));
        var description = new D3D11_BUFFER_DESC
        {
            ByteWidth = (uint)(capacity * sizeof(SectorInstance)),
            Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
            MiscFlags = (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_BUFFER_STRUCTURED,
            StructureByteStride = (uint)sizeof(SectorInstance),
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

    /// <summary>The <c>Sectors</c> constant buffer of Sectors.hlsl, 64 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SectorConstants
    {
        public float InverseWidth;
        public float InverseHeight;
        public uint First;
        public uint Unused;
        public float CenterX;
        public float CenterY;
        public float Unused2;
        public float Unused3;
        public System.Numerics.Vector4 Color;
        public float Left;
        public float Top;
        public float Right;
        public float Bottom;
    }
}
