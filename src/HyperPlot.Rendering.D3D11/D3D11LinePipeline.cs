using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace HyperPlot.Rendering.D3D11;

/// <summary>
/// The GPU objects of the polyline passes (design §5.3): shaders, blend and rasterizer states, the constant buffer, and the
/// dynamic structured buffer of segments. Made with the device and made again after a loss.
/// </summary>
internal sealed unsafe class D3D11LinePipeline
{
    private readonly D3D11Backend _backend;
    private ID3D11VertexShader* _segmentVs;
    private ID3D11VertexShader* _boundsVs;
    private ID3D11PixelShader* _coveragePs;
    private ID3D11PixelShader* _colorPs;
    private ID3D11PixelShader* _zeroPs;
    private ID3D11BlendState* _maxBlend;
    private ID3D11BlendState* _overBlend;
    private ID3D11BlendState* _writeBlend;
    private ID3D11RasterizerState* _rasterizer;
    private ID3D11Buffer* _constants;
    private ID3D11Buffer* _segments;
    private ID3D11ShaderResourceView* _segmentView;
    private int _segmentCapacity;

    internal D3D11LinePipeline(D3D11Backend backend) => _backend = backend;

    /// <summary>Gets the capacity of the segment buffer, for the tests of its growth.</summary>
    internal int SegmentCapacity => _segmentCapacity;

    internal void Make()
    {
        var device = _backend.Device;
        fixed (byte* code = Shaders.SegmentVS)
        {
            ID3D11VertexShader* shader;
            _backend.Check(device->CreateVertexShader(code, (nuint)Shaders.SegmentVS.Length, null, &shader), "CreateVertexShader");
            _segmentVs = shader;
        }

        fixed (byte* code = Shaders.BoundsVS)
        {
            ID3D11VertexShader* shader;
            _backend.Check(device->CreateVertexShader(code, (nuint)Shaders.BoundsVS.Length, null, &shader), "CreateVertexShader");
            _boundsVs = shader;
        }

        _coveragePs = MakePixelShader(Shaders.CoveragePS);
        _colorPs = MakePixelShader(Shaders.ColorPS);
        _zeroPs = MakePixelShader(Shaders.ZeroPS);

        // Coverage into R8: MAX, so that overlapping segments of one polyline cover a pixel once (design §5.3).
        _maxBlend = MakeBlend(D3D11_BLEND.D3D11_BLEND_ONE, D3D11_BLEND.D3D11_BLEND_ONE, D3D11_BLEND_OP.D3D11_BLEND_OP_MAX, enable: true);

        // Color over the target, premultiplied (design §5.4).
        _overBlend = MakeBlend(D3D11_BLEND.D3D11_BLEND_ONE, D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA, D3D11_BLEND_OP.D3D11_BLEND_OP_ADD, enable: true);
        _writeBlend = MakeBlend(D3D11_BLEND.D3D11_BLEND_ONE, D3D11_BLEND.D3D11_BLEND_ZERO, D3D11_BLEND_OP.D3D11_BLEND_OP_ADD, enable: false);

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
            ByteWidth = (uint)sizeof(LineConstants),
            Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
        };
        ID3D11Buffer* buffer;
        _backend.Check(device->CreateBuffer(&constants, null, &buffer), "CreateBuffer");
        _constants = buffer;
        _segmentCapacity = 0;
    }

    internal void Release()
    {
        Release(ref _segmentView);
        Release(ref _segments);
        Release(ref _constants);
        Release(ref _rasterizer);
        Release(ref _writeBlend);
        Release(ref _overBlend);
        Release(ref _maxBlend);
        Release(ref _zeroPs);
        Release(ref _colorPs);
        Release(ref _coveragePs);
        Release(ref _boundsVs);
        Release(ref _segmentVs);
        _segmentCapacity = 0;
    }

    /// <summary>Sends the list's segments, once for every polyline step of a <c>Draw</c>.</summary>
    internal void Upload(RenderList list)
    {
        if (!list.Polylines.IsEmpty)
        {
            Upload(list.Segments);
        }
    }

    /// <summary>Sets what every polyline shares: before the first polyline, and after a glyph step, which binds its own.</summary>
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
        var segmentView = _segmentView;
        context->VSSetShaderResources(0, 1, &segmentView);
    }

    /// <summary>Draws one polyline into the target in three passes (design §5.3).</summary>
    internal void Draw(D3D11RenderTarget target, in PolylineRecord polyline)
    {
        var context = _backend.Context;
        var width = target.Description.Width;
        var height = target.Description.Height;
        ID3D11ShaderResourceView* noView = null;
        var coverageTarget = target.CoverageView;
        var colorTarget = target.View;
        var coverageView = target.CoverageResourceView;

        // The clip, inside the target; the passes 2 and 3 cover the finite points' box widened by half the width and 1
        // pixel, where pass 1 can write coverage (design §5.3).
        var clipLeft = (int)Math.Clamp(polyline.Clip.Left, 0, width);
        var clipTop = (int)Math.Clamp(polyline.Clip.Top, 0, height);
        var clipRight = (int)Math.Clamp(polyline.Clip.Right, 0, width);
        var clipBottom = (int)Math.Clamp(polyline.Clip.Bottom, 0, height);
        var reach = (polyline.Style.Width * 0.5f) + 1;
        var left = MathF.Max(polyline.MinX - reach, clipLeft);
        var top = MathF.Max(polyline.MinY - reach, clipTop);
        var right = MathF.Min(polyline.MaxX + reach, clipRight);
        var bottom = MathF.Min(polyline.MaxY + reach, clipBottom);
        if (left >= right || top >= bottom)
        {
            return;
        }

        WriteConstants(polyline, width, height, left, top, right, bottom);
        var scissor = new RECT(clipLeft, clipTop, clipRight, clipBottom);
        context->RSSetScissorRects(1, &scissor);

        // 1. Coverage of every segment into R8, combined with MAX.
        context->OMSetRenderTargets(1, &coverageTarget, null);
        context->OMSetBlendState(_maxBlend, null, 0xFFFFFFFF);
        context->VSSetShader(_segmentVs, null, 0);
        context->PSSetShader(_coveragePs, null, 0);
        context->DrawInstanced(4, (uint)polyline.SegmentCount, 0, 0);

        // 2. The color once, over the target. The coverage texture leaves the output first: D3D11 cannot bind one texture
        // as a target and a shader resource at once.
        context->OMSetRenderTargets(1, &colorTarget, null);
        context->OMSetBlendState(_overBlend, null, 0xFFFFFFFF);
        context->VSSetShader(_boundsVs, null, 0);
        context->PSSetShader(_colorPs, null, 0);
        context->PSSetShaderResources(0, 1, &coverageView);
        context->Draw(4, 0);

        // 3. Zero back over the same box, for the next polyline; the shader resource leaves first.
        context->PSSetShaderResources(0, 1, &noView);
        context->OMSetRenderTargets(1, &coverageTarget, null);
        context->OMSetBlendState(_writeBlend, null, 0xFFFFFFFF);
        context->PSSetShader(_zeroPs, null, 0);
        context->Draw(4, 0);
    }

    /// <summary>Leaves nothing bound, so that a readback or a clear of either texture finds it free.</summary>
    internal void Unbind()
    {
        var context = _backend.Context;
        ID3D11ShaderResourceView* noView = null;
        context->OMSetRenderTargets(0, null, null);
        context->VSSetShaderResources(0, 1, &noView);
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

    private ID3D11PixelShader* MakePixelShader(ReadOnlySpan<byte> bytecode)
    {
        fixed (byte* code = bytecode)
        {
            ID3D11PixelShader* shader;
            _backend.Check(_backend.Device->CreatePixelShader(code, (nuint)bytecode.Length, null, &shader), "CreatePixelShader");
            return shader;
        }
    }

    private ID3D11BlendState* MakeBlend(D3D11_BLEND source, D3D11_BLEND destination, D3D11_BLEND_OP operation, bool enable)
    {
        var description = default(D3D11_BLEND_DESC);
        description.RenderTarget[0] = new D3D11_RENDER_TARGET_BLEND_DESC
        {
            BlendEnable = enable ? BOOL.TRUE : BOOL.FALSE,
            SrcBlend = source,
            DestBlend = destination,
            BlendOp = operation,
            SrcBlendAlpha = source,
            DestBlendAlpha = destination,
            BlendOpAlpha = operation,
            RenderTargetWriteMask = (byte)D3D11_COLOR_WRITE_ENABLE.D3D11_COLOR_WRITE_ENABLE_ALL,
        };
        ID3D11BlendState* state;
        _backend.Check(_backend.Device->CreateBlendState(&description, &state), "CreateBlendState");
        return state;
    }

    /// <summary>Writes the segments into the dynamic buffer, which doubles when it is too small.</summary>
    private void Upload(ReadOnlySpan<SegmentRecord> segments)
    {
        if (segments.Length > _segmentCapacity)
        {
            Release(ref _segmentView);
            Release(ref _segments);
            var capacity = Math.Max(segments.Length, Math.Max(256, _segmentCapacity * 2));
            var description = new D3D11_BUFFER_DESC
            {
                ByteWidth = (uint)(capacity * sizeof(SegmentRecord)),
                Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
                BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
                CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
                MiscFlags = (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_BUFFER_STRUCTURED,
                StructureByteStride = (uint)sizeof(SegmentRecord),
            };
            ID3D11Buffer* buffer;
            _backend.Check(_backend.Device->CreateBuffer(&description, null, &buffer), "CreateBuffer");
            _segments = buffer;
            var view = new D3D11_SHADER_RESOURCE_VIEW_DESC
            {
                Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN,
                ViewDimension = D3D_SRV_DIMENSION.D3D11_SRV_DIMENSION_BUFFER,
            };
            view.Buffer.FirstElement = 0;
            view.Buffer.NumElements = (uint)capacity;
            ID3D11ShaderResourceView* resourceView;
            _backend.Check(_backend.Device->CreateShaderResourceView((ID3D11Resource*)_segments, &view, &resourceView), "CreateShaderResourceView");
            _segmentView = resourceView;
            _segmentCapacity = capacity;
        }

        // The mapped memory holds capacity records; the span bounds the copy to the records there are.
        D3D11_MAPPED_SUBRESOURCE mapped;
        _backend.Check(_backend.Context->Map((ID3D11Resource*)_segments, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map");
        segments.CopyTo(new Span<SegmentRecord>(mapped.pData, _segmentCapacity));
        _backend.Context->Unmap((ID3D11Resource*)_segments, 0);
    }

    private void WriteConstants(in PolylineRecord polyline, int width, int height, float left, float top, float right, float bottom)
    {
        var style = polyline.Style;
        var color = style.Color;
        var dash = style.Dash.GetValueOrDefault();
        var values = new LineConstants
        {
            InverseWidth = 1f / width,
            InverseHeight = 1f / height,
            SegmentStart = (uint)polyline.SegmentStart,
            HalfWidth = style.Width * 0.5f,
            DashOn = dash.On,
            DashOff = dash.Off,
            DashOffset = dash.Offset,
            Dashed = style.Dash.HasValue ? 1u : 0u,
            Red = Premultiplied.Multiply(color.R, color.A) / 255f,
            Green = Premultiplied.Multiply(color.G, color.A) / 255f,
            Blue = Premultiplied.Multiply(color.B, color.A) / 255f,
            Alpha = color.A / 255f,
            Left = left,
            Top = top,
            Right = right,
            Bottom = bottom,
        };
        D3D11_MAPPED_SUBRESOURCE mapped;
        _backend.Check(_backend.Context->Map((ID3D11Resource*)_constants, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map");
        *(LineConstants*)mapped.pData = values;
        _backend.Context->Unmap((ID3D11Resource*)_constants, 0);
    }

    /// <summary>The <c>Line</c> constant buffer of Line.hlsl, 64 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct LineConstants
    {
        public float InverseWidth;
        public float InverseHeight;
        public uint SegmentStart;
        public float HalfWidth;
        public float DashOn;
        public float DashOff;
        public float DashOffset;
        public uint Dashed;
        public float Red;
        public float Green;
        public float Blue;
        public float Alpha;
        public float Left;
        public float Top;
        public float Right;
        public float Bottom;
    }
}
