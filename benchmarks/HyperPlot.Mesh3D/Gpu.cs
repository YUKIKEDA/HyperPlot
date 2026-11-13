using System.Numerics;
using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace HyperPlot.Mesh3D;

/// <summary>How the mesh is drawn.</summary>
internal enum Mode
{
    /// <summary>Opaque, with a D32_FLOAT depth test and write.</summary>
    Opaque,

    /// <summary>Translucent at the frame's opacity, through WBOIT: accum and revealage, then the composite.</summary>
    Translucent,
}

/// <summary>Takes the GPU times of a frame: milliseconds from the frame's first timestamp to each later one.</summary>
internal delegate void TimedHandler(long frame, ReadOnlySpan<double> times);

/// <summary>The <c>Frame</c> constant buffer of Mesh3D.hlsl, 192 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameConstants
{
    public Matrix4x4 ViewProjection;
    public Matrix4x4 View;
    public Vector3 Scale;
    public float Alpha;
    public Vector3 ToLight;
    public float Ambient;
    public Vector3 Eye;
    public uint WeightKind;
    public uint Capacity;
    public uint Unused0;
    public uint Unused1;
    public uint Unused2;
}

/// <summary>A fragment of the capture, as CapturePS writes it, 32 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Fragment
{
    public float ViewDepth;
    public float Depth;
    public Vector3 Color;
    public float Alpha;
    public uint Primitive;
    public uint Next;
}

/// <summary>
/// SPIKE (row K0d, Issue #490): a D3D11 device of its own, the mesh in a structured buffer and an index buffer, a
/// 1,920 × 1,080 BGRA8 target with a D32_FLOAT depth buffer, WBOIT's accum (RGBA16F) and revealage (R8), the capture of
/// every fragment for the sorted reference (made on first use), and GPU timestamps around each pass.
/// Not product code: no device-loss handling, every failure throws.
/// </summary>
internal sealed unsafe class Gpu : IDisposable
{
    /// <summary>The timestamps of a frame: the start, after the clears, and after each pass (opaque 1, WBOIT 2).</summary>
    public const int MaxStamps = 4;

    /// <summary>The end of a pixel's fragment list.</summary>
    public const uint End = 0xFFFFFFFF;

    private const int Sets = 8;

    private readonly int _width;
    private readonly int _height;
    private readonly int _indexCount;
    private readonly nint[] _disjoint = new nint[Sets];
    private readonly nint[] _stamps = new nint[Sets * MaxStamps];
    private readonly long[] _setFrame = new long[Sets];
    private readonly int[] _setStamps = new int[Sets];
    private readonly bool[] _pending = new bool[Sets];
    private int _nextSet;
    private int _capacity;

    private ID3D11Device* _device;
    private ID3D11DeviceContext* _context;
    private ID3D11Buffer* _vertices;
    private ID3D11ShaderResourceView* _vertexView;
    private ID3D11Buffer* _indices;
    private ID3D11Buffer* _constants;
    private ID3D11Texture2D* _target;
    private ID3D11RenderTargetView* _targetView;
    private ID3D11Texture2D* _targetStaging;
    private ID3D11Texture2D* _depth;
    private ID3D11DepthStencilView* _depthView;
    private ID3D11Texture2D* _accum;
    private ID3D11RenderTargetView* _accumTarget;
    private ID3D11ShaderResourceView* _accumView;
    private ID3D11Texture2D* _accumStaging;
    private ID3D11Texture2D* _revealage;
    private ID3D11RenderTargetView* _revealageTarget;
    private ID3D11ShaderResourceView* _revealageView;
    private ID3D11Texture2D* _heads;
    private ID3D11UnorderedAccessView* _headsAccess;
    private ID3D11Texture2D* _headsStaging;
    private ID3D11Buffer* _fragments;
    private ID3D11UnorderedAccessView* _fragmentsAccess;
    private ID3D11Buffer* _fragmentsStaging;
    private ID3D11Buffer* _counter;
    private ID3D11UnorderedAccessView* _counterAccess;
    private ID3D11Buffer* _counterStaging;
    private ID3D11VertexShader* _meshVS;
    private ID3D11PixelShader* _opaquePS;
    private ID3D11PixelShader* _accumPS;
    private ID3D11PixelShader* _capturePS;
    private ID3D11VertexShader* _compositeVS;
    private ID3D11PixelShader* _compositePS;
    private ID3D11RasterizerState* _rasterizer;
    private ID3D11RasterizerState* _scissored;
    private ID3D11DepthStencilState* _depthWrite;
    private ID3D11DepthStencilState* _depthTest;
    private ID3D11DepthStencilState* _depthOff;
    private ID3D11BlendState* _accumBlend;
    private ID3D11BlendState* _compositeBlend;

    public Gpu(bool warp, int width, int height, Mesh mesh)
    {
        (_width, _height, _indexCount) = (width, height, mesh.Indices.Length);
        try
        {
            MakeDevice(warp);
            IsWarp = warp;
            MakeTargets();
            MakeMesh(mesh);
            MakeShaders();
            MakeStates();
            MakeTimer();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public bool IsWarp { get; }

    /// <summary>
    /// Records one frame: the clears, the passes of the mode, a timestamp after each, and sends the commands. Nothing waits
    /// for the GPU. The translucent frame is a whole WBOIT frame: the redraw of decision 4 of Issue #483 costs one of these.
    /// </summary>
    public void Draw(Mode mode, in FrameConstants constants, long frame)
    {
        var set = BeginTimer(frame);
        var context = _context;
        Upload(constants);
        var white = stackalloc float[] { 1, 1, 1, 1 };
        context->ClearRenderTargetView(_targetView, white);
        context->ClearDepthStencilView(_depthView, (uint)D3D11_CLEAR_FLAG.D3D11_CLEAR_DEPTH, 1, 0);
        if (mode == Mode.Translucent)
        {
            var zero = stackalloc float[] { 0, 0, 0, 0 };
            context->ClearRenderTargetView(_accumTarget, zero);
            context->ClearRenderTargetView(_revealageTarget, white);
        }

        Stamp(set, 1);
        BindMesh(_rasterizer);
        var targetView = _targetView;
        if (mode == Mode.Opaque)
        {
            context->PSSetShader(_opaquePS, null, 0);
            context->OMSetBlendState(null, null, 0xFFFFFFFF);
            context->OMSetDepthStencilState(_depthWrite, 0);
            context->OMSetRenderTargets(1, &targetView, _depthView);
            context->DrawIndexed((uint)_indexCount, 0, 0);
            Stamp(set, 2);
            EndTimer(set, 3);
        }
        else
        {
            // Accumulation: both targets, the depth tested (against the opaque pass's; this scene has none) and not written.
            var targets = stackalloc ID3D11RenderTargetView*[] { _accumTarget, _revealageTarget };
            context->PSSetShader(_accumPS, null, 0);
            context->OMSetBlendState(_accumBlend, null, 0xFFFFFFFF);
            context->OMSetDepthStencilState(_depthTest, 0);
            context->OMSetRenderTargets(2, targets, _depthView);
            context->DrawIndexed((uint)_indexCount, 0, 0);
            Stamp(set, 2);

            // Composite over the target.
            context->OMSetRenderTargets(1, &targetView, null);
            context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            var views = stackalloc ID3D11ShaderResourceView*[] { _accumView, _revealageView };
            context->PSSetShaderResources(0, 2, views);
            context->VSSetShader(_compositeVS, null, 0);
            context->PSSetShader(_compositePS, null, 0);
            context->OMSetBlendState(_compositeBlend, null, 0xFFFFFFFF);
            context->OMSetDepthStencilState(_depthOff, 0);
            context->Draw(3, 0);
            var none = stackalloc ID3D11ShaderResourceView*[] { null, null };
            context->PSSetShaderResources(0, 2, none);
            Stamp(set, 3);
            EndTimer(set, 4);
        }

        context->OMSetRenderTargets(0, null, null);
        context->Flush();
    }

    /// <summary>
    /// Captures every fragment of the translucent pass whose pixel row is in [top, bottom) into the per-pixel lists, waits,
    /// and returns how many fragments the pass made; more than <paramref name="capacity"/> means some were dropped.
    /// </summary>
    public uint Capture(FrameConstants constants, int capacity, int top, int bottom)
    {
        EnsureCapture(capacity);
        constants.Capacity = (uint)_capacity;
        var context = _context;
        Upload(constants);
        var empty = stackalloc uint[] { End, End, End, End };
        context->ClearUnorderedAccessViewUint(_headsAccess, empty);
        uint zero = 0;
        context->UpdateSubresource((ID3D11Resource*)_counter, 0, null, &zero, 4, 4);
        var scissor = new RECT { left = 0, top = top, right = _width, bottom = bottom };
        BindMesh(_scissored);
        context->RSSetScissorRects(1, &scissor);
        context->PSSetShader(_capturePS, null, 0);
        context->OMSetBlendState(null, null, 0xFFFFFFFF);
        context->OMSetDepthStencilState(_depthOff, 0);
        var access = stackalloc ID3D11UnorderedAccessView*[] { _headsAccess, _fragmentsAccess, _counterAccess };
        context->OMSetRenderTargetsAndUnorderedAccessViews(0, null, null, 1, 3, access, null);
        context->DrawIndexed((uint)_indexCount, 0, 0);
        context->OMSetRenderTargets(0, null, null);

        context->CopyResource((ID3D11Resource*)_counterStaging, (ID3D11Resource*)_counter);
        D3D11_MAPPED_SUBRESOURCE mapped;
        Check(context->Map((ID3D11Resource*)_counterStaging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped), "Map");

        // The staging buffer is 16 bytes; the count is its first 4.
        var count = *(uint*)mapped.pData;
        context->Unmap((ID3D11Resource*)_counterStaging, 0);
        return count;
    }

    /// <summary>Reads the list heads of rows [top, bottom) and the first <paramref name="count"/> fragments of the last capture.</summary>
    public (uint[] Heads, Fragment[] Fragments) ReadCapture(int top, int bottom, uint count)
    {
        var context = _context;
        var kept = (int)Math.Min(count, (uint)_capacity);
        var heads = new uint[_width * (bottom - top)];
        context->CopyResource((ID3D11Resource*)_headsStaging, (ID3D11Resource*)_heads);
        D3D11_MAPPED_SUBRESOURCE mapped;
        Check(context->Map((ID3D11Resource*)_headsStaging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped), "Map");
        try
        {
            // The mapped rows are RowPitch bytes apart; the span covers every row, and each read is a slice of it.
            var source = new ReadOnlySpan<byte>(mapped.pData, (int)(mapped.RowPitch * (uint)(_height - 1)) + (_width * 4));
            for (var y = top; y < bottom; y++)
            {
                MemoryMarshal.Cast<byte, uint>(source.Slice((int)(y * mapped.RowPitch), _width * 4)).CopyTo(heads.AsSpan((y - top) * _width, _width));
            }
        }
        finally
        {
            context->Unmap((ID3D11Resource*)_headsStaging, 0);
        }

        var fragments = new Fragment[kept];
        if (kept > 0)
        {
            var box = new D3D11_BOX { left = 0, right = (uint)(kept * sizeof(Fragment)), top = 0, bottom = 1, front = 0, back = 1 };
            context->CopySubresourceRegion((ID3D11Resource*)_fragmentsStaging, 0, 0, 0, 0, (ID3D11Resource*)_fragments, 0, &box);
            Check(context->Map((ID3D11Resource*)_fragmentsStaging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped), "Map");
            try
            {
                // The staging buffer holds _capacity fragments, and kept is at most that.
                new ReadOnlySpan<Fragment>(mapped.pData, kept).CopyTo(fragments);
            }
            finally
            {
                context->Unmap((ID3D11Resource*)_fragmentsStaging, 0);
            }
        }

        return (heads, fragments);
    }

    /// <summary>
    /// Reads every timed frame that is done, oldest first, without waiting, or waiting for all when asked; calls back with
    /// the frame and the times in milliseconds from the first stamp to each later one.
    /// </summary>
    public void Poll(TimedHandler timed, bool wait)
    {
        Span<double> times = stackalloc double[MaxStamps];
        var stamps = stackalloc ulong[MaxStamps];
        for (var k = 0; k < Sets; k++)
        {
            var set = (_nextSet + k) % Sets;
            if (!_pending[set])
            {
                continue;
            }

            D3D11_QUERY_DATA_TIMESTAMP_DISJOINT info;
            var flags = (uint)D3D11_ASYNC_GETDATA_FLAG.D3D11_ASYNC_GETDATA_DONOTFLUSH;
            while (!Ready(set, &info, stamps, flags))
            {
                if (!wait)
                {
                    return;
                }

                Thread.SpinWait(1000);
            }

            _pending[set] = false;
            if (info.Disjoint || info.Frequency == 0)
            {
                continue;
            }

            var count = _setStamps[set];
            for (var s = 1; s < count; s++)
            {
                times[s - 1] = (stamps[s] - stamps[0]) * 1000.0 / info.Frequency;
            }

            timed(_setFrame[set], times[..(count - 1)]);
        }
    }

    /// <summary>Copies the target to its staging texture, waits for it, and returns its BGRA8 rows, packed.</summary>
    public byte[] Read() => ReadTexture(_target, _targetStaging, 4);

    /// <summary>Copies accum (RGBA16F) to its staging texture, waits for it, and returns its rows, packed: 8 bytes a pixel.</summary>
    public byte[] ReadAccum()
    {
        EnsureAccumStaging();
        return ReadTexture(_accum, _accumStaging, 8);
    }

    public void Dispose()
    {
        for (var i = 0; i < _stamps.Length; i++)
        {
            ReleaseNint(ref _stamps[i]);
        }

        for (var i = 0; i < Sets; i++)
        {
            ReleaseNint(ref _disjoint[i]);
        }

        Release(ref _compositeBlend);
        Release(ref _accumBlend);
        Release(ref _depthOff);
        Release(ref _depthTest);
        Release(ref _depthWrite);
        Release(ref _scissored);
        Release(ref _rasterizer);
        Release(ref _compositePS);
        Release(ref _compositeVS);
        Release(ref _capturePS);
        Release(ref _accumPS);
        Release(ref _opaquePS);
        Release(ref _meshVS);
        Release(ref _counterStaging);
        Release(ref _counterAccess);
        Release(ref _counter);
        Release(ref _fragmentsStaging);
        Release(ref _fragmentsAccess);
        Release(ref _fragments);
        Release(ref _headsStaging);
        Release(ref _headsAccess);
        Release(ref _heads);
        Release(ref _revealageView);
        Release(ref _revealageTarget);
        Release(ref _revealage);
        Release(ref _accumStaging);
        Release(ref _accumView);
        Release(ref _accumTarget);
        Release(ref _accum);
        Release(ref _depthView);
        Release(ref _depth);
        Release(ref _targetStaging);
        Release(ref _targetView);
        Release(ref _target);
        Release(ref _constants);
        Release(ref _indices);
        Release(ref _vertexView);
        Release(ref _vertices);
        if (_context != null)
        {
            _context->ClearState();
            Release(ref _context);
        }

        Release(ref _device);
    }

    private static void Check(HRESULT result, string call)
    {
        if (result.FAILED)
        {
            throw new InvalidOperationException($"{call} failed with 0x{result.Value:X8}.") { HResult = result.Value };
        }
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

    private static void ReleaseNint(ref nint pointer)
    {
        if (pointer != 0)
        {
            _ = ((IUnknown*)pointer)->Release();
            pointer = 0;
        }
    }

    private void Upload(in FrameConstants constants)
    {
        D3D11_MAPPED_SUBRESOURCE mapped;
        Check(_context->Map((ID3D11Resource*)_constants, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map");

        // The buffer was made sizeof(FrameConstants) bytes long.
        *(FrameConstants*)mapped.pData = constants;
        _context->Unmap((ID3D11Resource*)_constants, 0);
    }

    // The input assembler, the vertex shader, the constants, the viewport, and the rasterizer of a pass over the mesh.
    private void BindMesh(ID3D11RasterizerState* rasterizer)
    {
        var context = _context;
        var viewport = new D3D11_VIEWPORT
        {
            Width = _width,
            Height = _height,
            MaxDepth = 1,
        };
        var buffer = _constants;
        var vertexView = _vertexView;
        context->RSSetViewports(1, &viewport);
        context->RSSetState(rasterizer);
        context->IASetInputLayout(null);
        context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->IASetIndexBuffer(_indices, DXGI_FORMAT.DXGI_FORMAT_R32_UINT, 0);
        context->VSSetConstantBuffers(0, 1, &buffer);
        context->PSSetConstantBuffers(0, 1, &buffer);
        context->VSSetShaderResources(0, 1, &vertexView);
        context->VSSetShader(_meshVS, null, 0);
    }

    private byte[] ReadTexture(ID3D11Texture2D* texture, ID3D11Texture2D* staging, int bytesPerPixel)
    {
        _context->CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)texture);
        D3D11_MAPPED_SUBRESOURCE mapped;
        Check(_context->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped), "Map");
        var rowBytes = _width * bytesPerPixel;
        var pixels = new byte[rowBytes * _height];
        try
        {
            // The mapped rows are RowPitch bytes apart; the span bounds every read.
            var source = new ReadOnlySpan<byte>(mapped.pData, (int)(mapped.RowPitch * (uint)(_height - 1)) + rowBytes);
            for (var y = 0; y < _height; y++)
            {
                source.Slice((int)(y * mapped.RowPitch), rowBytes).CopyTo(pixels.AsSpan(y * rowBytes, rowBytes));
            }
        }
        finally
        {
            _context->Unmap((ID3D11Resource*)staging, 0);
        }

        return pixels;
    }

    private void MakeDevice(bool warp)
    {
        var levels = stackalloc D3D_FEATURE_LEVEL[] { D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0 };
        ID3D11Device* device;
        ID3D11DeviceContext* context;
        D3D_FEATURE_LEVEL level;
        Check(
            DirectX.D3D11CreateDevice(
                null,
                warp ? D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP : D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
                HMODULE.NULL,
                (uint)D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                levels,
                2,
                TerraFX.Interop.DirectX.D3D11.D3D11_SDK_VERSION,
                &device,
                &level,
                &context
            ),
            "D3D11CreateDevice"
        );
        _device = device;
        _context = context;
    }

    private ID3D11Texture2D* MakeTexture(DXGI_FORMAT format, D3D11_BIND_FLAG bind, bool staging = false)
    {
        var description = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
            Usage = staging ? D3D11_USAGE.D3D11_USAGE_STAGING : D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = staging ? 0 : (uint)bind,
            CPUAccessFlags = staging ? (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ : 0,
        };
        ID3D11Texture2D* texture;
        Check(_device->CreateTexture2D(&description, null, &texture), "CreateTexture2D");
        return texture;
    }

    // Each view is made right after its texture and stored in a field at once, so that Dispose releases what a failed step left.
    private void MakeTargets()
    {
        var both = D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE;
        _target = MakeTexture(DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET);
        ID3D11RenderTargetView* targetView;
        Check(_device->CreateRenderTargetView((ID3D11Resource*)_target, null, &targetView), "CreateRenderTargetView");
        _targetView = targetView;
        _targetStaging = MakeTexture(DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, 0, staging: true);

        _depth = MakeTexture(DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT, D3D11_BIND_FLAG.D3D11_BIND_DEPTH_STENCIL);
        ID3D11DepthStencilView* depthView;
        Check(_device->CreateDepthStencilView((ID3D11Resource*)_depth, null, &depthView), "CreateDepthStencilView");
        _depthView = depthView;

        _accum = MakeTexture(DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT, both);
        ID3D11ShaderResourceView* view;
        Check(_device->CreateRenderTargetView((ID3D11Resource*)_accum, null, &targetView), "CreateRenderTargetView");
        _accumTarget = targetView;
        Check(_device->CreateShaderResourceView((ID3D11Resource*)_accum, null, &view), "CreateShaderResourceView");
        _accumView = view;

        _revealage = MakeTexture(DXGI_FORMAT.DXGI_FORMAT_R8_UNORM, both);
        Check(_device->CreateRenderTargetView((ID3D11Resource*)_revealage, null, &targetView), "CreateRenderTargetView");
        _revealageTarget = targetView;
        Check(_device->CreateShaderResourceView((ID3D11Resource*)_revealage, null, &view), "CreateShaderResourceView");
        _revealageView = view;
    }

    private void EnsureAccumStaging()
    {
        if (_accumStaging == null)
        {
            _accumStaging = MakeTexture(DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT, 0, staging: true);
        }
    }

    // The capture's resources, made on first use: the heads, the fragments (capacity × 32 bytes), the counter, and their staging copies.
    private void EnsureCapture(int capacity)
    {
        if (_heads != null)
        {
            if (capacity != _capacity)
            {
                throw new InvalidOperationException("The capture's capacity is fixed once made.");
            }

            return;
        }

        _capacity = capacity;
        _heads = MakeTexture(DXGI_FORMAT.DXGI_FORMAT_R32_UINT, D3D11_BIND_FLAG.D3D11_BIND_UNORDERED_ACCESS);
        ID3D11UnorderedAccessView* access;
        Check(_device->CreateUnorderedAccessView((ID3D11Resource*)_heads, null, &access), "CreateUnorderedAccessView");
        _headsAccess = access;
        _headsStaging = MakeTexture(DXGI_FORMAT.DXGI_FORMAT_R32_UINT, 0, staging: true);

        var description = new D3D11_BUFFER_DESC
        {
            ByteWidth = (uint)(capacity * sizeof(Fragment)),
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_UNORDERED_ACCESS,
            MiscFlags = (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_BUFFER_STRUCTURED,
            StructureByteStride = (uint)sizeof(Fragment),
        };
        ID3D11Buffer* buffer;
        Check(_device->CreateBuffer(&description, null, &buffer), "CreateBuffer");
        _fragments = buffer;
        var fragmentsAccess = new D3D11_UNORDERED_ACCESS_VIEW_DESC
        {
            Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN,
            ViewDimension = D3D11_UAV_DIMENSION.D3D11_UAV_DIMENSION_BUFFER,
        };
        fragmentsAccess.Buffer.NumElements = (uint)capacity;
        Check(_device->CreateUnorderedAccessView((ID3D11Resource*)_fragments, &fragmentsAccess, &access), "CreateUnorderedAccessView");
        _fragmentsAccess = access;
        description.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
        description.BindFlags = 0;
        description.MiscFlags = 0;
        description.StructureByteStride = 0;
        description.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
        Check(_device->CreateBuffer(&description, null, &buffer), "CreateBuffer");
        _fragmentsStaging = buffer;

        var counter = new D3D11_BUFFER_DESC
        {
            ByteWidth = 16,
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_UNORDERED_ACCESS,
            MiscFlags = (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_BUFFER_ALLOW_RAW_VIEWS,
        };
        Check(_device->CreateBuffer(&counter, null, &buffer), "CreateBuffer");
        _counter = buffer;
        var counterAccess = new D3D11_UNORDERED_ACCESS_VIEW_DESC
        {
            Format = DXGI_FORMAT.DXGI_FORMAT_R32_TYPELESS,
            ViewDimension = D3D11_UAV_DIMENSION.D3D11_UAV_DIMENSION_BUFFER,
        };
        counterAccess.Buffer.NumElements = 4;
        counterAccess.Buffer.Flags = (uint)D3D11_BUFFER_UAV_FLAG.D3D11_BUFFER_UAV_FLAG_RAW;
        Check(_device->CreateUnorderedAccessView((ID3D11Resource*)_counter, &counterAccess, &access), "CreateUnorderedAccessView");
        _counterAccess = access;
        counter.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
        counter.BindFlags = 0;
        counter.MiscFlags = 0;
        counter.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
        Check(_device->CreateBuffer(&counter, null, &buffer), "CreateBuffer");
        _counterStaging = buffer;
    }

    private void MakeMesh(Mesh mesh)
    {
        var description = new D3D11_BUFFER_DESC
        {
            ByteWidth = (uint)(mesh.Vertices.Length * sizeof(MeshVertex)),
            Usage = D3D11_USAGE.D3D11_USAGE_IMMUTABLE,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
            MiscFlags = (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_BUFFER_STRUCTURED,
            StructureByteStride = (uint)sizeof(MeshVertex),
        };
        ID3D11Buffer* buffer;

        // The arrays bound the bytes the device copies: each ByteWidth is its array's length in bytes.
        fixed (MeshVertex* data = mesh.Vertices)
        {
            var initial = new D3D11_SUBRESOURCE_DATA { pSysMem = data };
            Check(_device->CreateBuffer(&description, &initial, &buffer), "CreateBuffer");
        }

        _vertices = buffer;
        var view = new D3D11_SHADER_RESOURCE_VIEW_DESC
        {
            Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN,
            ViewDimension = D3D_SRV_DIMENSION.D3D11_SRV_DIMENSION_BUFFER,
        };
        view.Buffer.FirstElement = 0;
        view.Buffer.NumElements = (uint)mesh.Vertices.Length;
        ID3D11ShaderResourceView* resourceView;
        Check(_device->CreateShaderResourceView((ID3D11Resource*)_vertices, &view, &resourceView), "CreateShaderResourceView");
        _vertexView = resourceView;

        var indices = new D3D11_BUFFER_DESC
        {
            ByteWidth = (uint)(mesh.Indices.Length * sizeof(uint)),
            Usage = D3D11_USAGE.D3D11_USAGE_IMMUTABLE,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_INDEX_BUFFER,
        };
        fixed (uint* data = mesh.Indices)
        {
            var initial = new D3D11_SUBRESOURCE_DATA { pSysMem = data };
            Check(_device->CreateBuffer(&indices, &initial, &buffer), "CreateBuffer");
        }

        _indices = buffer;

        var constants = new D3D11_BUFFER_DESC
        {
            ByteWidth = (uint)sizeof(FrameConstants),
            Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
        };
        Check(_device->CreateBuffer(&constants, null, &buffer), "CreateBuffer");
        _constants = buffer;
    }

    private void MakeShaders()
    {
        _meshVS = MakeVertexShader(Shaders.MeshVS);
        _compositeVS = MakeVertexShader(Shaders.CompositeVS);
        _opaquePS = MakePixelShader(Shaders.OpaquePS);
        _accumPS = MakePixelShader(Shaders.AccumPS);
        _capturePS = MakePixelShader(Shaders.CapturePS);
        _compositePS = MakePixelShader(Shaders.CompositePS);
    }

    private ID3D11VertexShader* MakeVertexShader(ReadOnlySpan<byte> bytes)
    {
        fixed (byte* code = bytes)
        {
            ID3D11VertexShader* shader;
            Check(_device->CreateVertexShader(code, (nuint)bytes.Length, null, &shader), "CreateVertexShader");
            return shader;
        }
    }

    private ID3D11PixelShader* MakePixelShader(ReadOnlySpan<byte> bytes)
    {
        fixed (byte* code = bytes)
        {
            ID3D11PixelShader* shader;
            Check(_device->CreatePixelShader(code, (nuint)bytes.Length, null, &shader), "CreatePixelShader");
            return shader;
        }
    }

    private void MakeStates()
    {
        // Both faces are drawn (design §13.5: an open surface shows its back). Clockwise on the target is front, the D3D11
        // default: the meshes are counter-clockwise seen from their normals' side in the right-handed box, and the
        // projection's y flip to the target turns that clockwise.
        var rasterizer = new D3D11_RASTERIZER_DESC
        {
            FillMode = D3D11_FILL_MODE.D3D11_FILL_SOLID,
            CullMode = D3D11_CULL_MODE.D3D11_CULL_NONE,
            DepthClipEnable = BOOL.TRUE,
        };
        ID3D11RasterizerState* state;
        Check(_device->CreateRasterizerState(&rasterizer, &state), "CreateRasterizerState");
        _rasterizer = state;
        rasterizer.ScissorEnable = BOOL.TRUE;
        Check(_device->CreateRasterizerState(&rasterizer, &state), "CreateRasterizerState");
        _scissored = state;

        var depth = new D3D11_DEPTH_STENCIL_DESC
        {
            DepthEnable = BOOL.TRUE,
            DepthWriteMask = D3D11_DEPTH_WRITE_MASK.D3D11_DEPTH_WRITE_MASK_ALL,
            DepthFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_LESS,
        };
        ID3D11DepthStencilState* depthState;
        Check(_device->CreateDepthStencilState(&depth, &depthState), "CreateDepthStencilState");
        _depthWrite = depthState;
        depth.DepthWriteMask = D3D11_DEPTH_WRITE_MASK.D3D11_DEPTH_WRITE_MASK_ZERO;
        Check(_device->CreateDepthStencilState(&depth, &depthState), "CreateDepthStencilState");
        _depthTest = depthState;
        depth.DepthEnable = BOOL.FALSE;
        Check(_device->CreateDepthStencilState(&depth, &depthState), "CreateDepthStencilState");
        _depthOff = depthState;

        // Accumulation: accum adds (C·α, α)·w (ONE, ONE); revealage keeps dst·(1 - α) (ZERO, INV_SRC_COLOR on R8).
        var blend = new D3D11_BLEND_DESC { IndependentBlendEnable = BOOL.TRUE };
        blend.RenderTarget[0] = new D3D11_RENDER_TARGET_BLEND_DESC
        {
            BlendEnable = BOOL.TRUE,
            SrcBlend = D3D11_BLEND.D3D11_BLEND_ONE,
            DestBlend = D3D11_BLEND.D3D11_BLEND_ONE,
            BlendOp = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD,
            SrcBlendAlpha = D3D11_BLEND.D3D11_BLEND_ONE,
            DestBlendAlpha = D3D11_BLEND.D3D11_BLEND_ONE,
            BlendOpAlpha = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD,
            RenderTargetWriteMask = (byte)D3D11_COLOR_WRITE_ENABLE.D3D11_COLOR_WRITE_ENABLE_ALL,
        };
        blend.RenderTarget[1] = new D3D11_RENDER_TARGET_BLEND_DESC
        {
            BlendEnable = BOOL.TRUE,
            SrcBlend = D3D11_BLEND.D3D11_BLEND_ZERO,
            DestBlend = D3D11_BLEND.D3D11_BLEND_INV_SRC_COLOR,
            BlendOp = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD,
            SrcBlendAlpha = D3D11_BLEND.D3D11_BLEND_ZERO,
            DestBlendAlpha = D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA,
            BlendOpAlpha = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD,
            RenderTargetWriteMask = (byte)D3D11_COLOR_WRITE_ENABLE.D3D11_COLOR_WRITE_ENABLE_ALL,
        };
        ID3D11BlendState* blendState;
        Check(_device->CreateBlendState(&blend, &blendState), "CreateBlendState");
        _accumBlend = blendState;

        // Composite: average·(1 - revealage) + target·revealage (INV_SRC_ALPHA, SRC_ALPHA; the target's alpha is kept).
        var composite = new D3D11_BLEND_DESC();
        composite.RenderTarget[0] = new D3D11_RENDER_TARGET_BLEND_DESC
        {
            BlendEnable = BOOL.TRUE,
            SrcBlend = D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA,
            DestBlend = D3D11_BLEND.D3D11_BLEND_SRC_ALPHA,
            BlendOp = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD,
            SrcBlendAlpha = D3D11_BLEND.D3D11_BLEND_ZERO,
            DestBlendAlpha = D3D11_BLEND.D3D11_BLEND_ONE,
            BlendOpAlpha = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD,
            RenderTargetWriteMask = (byte)D3D11_COLOR_WRITE_ENABLE.D3D11_COLOR_WRITE_ENABLE_ALL,
        };
        Check(_device->CreateBlendState(&composite, &blendState), "CreateBlendState");
        _compositeBlend = blendState;
    }

    private void MakeTimer()
    {
        for (var set = 0; set < Sets; set++)
        {
            _disjoint[set] = (nint)MakeQuery(D3D11_QUERY.D3D11_QUERY_TIMESTAMP_DISJOINT);
            for (var s = 0; s < MaxStamps; s++)
            {
                _stamps[(set * MaxStamps) + s] = (nint)MakeQuery(D3D11_QUERY.D3D11_QUERY_TIMESTAMP);
            }
        }
    }

    private ID3D11Query* MakeQuery(D3D11_QUERY kind)
    {
        var description = new D3D11_QUERY_DESC { Query = kind };
        ID3D11Query* query;
        Check(_device->CreateQuery(&description, &query), "CreateQuery");
        return query;
    }

    /// <summary>Starts a timed frame on the next set, or returns -1 when that set is still pending (the frame is not timed).</summary>
    private int BeginTimer(long frame)
    {
        var set = _nextSet;
        if (_pending[set])
        {
            return -1;
        }

        _nextSet = (_nextSet + 1) % Sets;
        _setFrame[set] = frame;
        _context->Begin((ID3D11Asynchronous*)_disjoint[set]);
        Stamp(set, 0);
        return set;
    }

    private void Stamp(int set, int index)
    {
        if (set >= 0)
        {
            _context->End((ID3D11Asynchronous*)_stamps[(set * MaxStamps) + index]);
        }
    }

    private void EndTimer(int set, int stamps)
    {
        if (set >= 0)
        {
            _context->End((ID3D11Asynchronous*)_disjoint[set]);
            _setStamps[set] = stamps;
            _pending[set] = true;
        }
    }

    private bool Ready(int set, D3D11_QUERY_DATA_TIMESTAMP_DISJOINT* info, ulong* stamps, uint flags)
    {
        if (_context->GetData((ID3D11Asynchronous*)_disjoint[set], info, (uint)sizeof(D3D11_QUERY_DATA_TIMESTAMP_DISJOINT), flags) != S.S_OK)
        {
            return false;
        }

        for (var s = 0; s < _setStamps[set]; s++)
        {
            if (_context->GetData((ID3D11Asynchronous*)_stamps[(set * MaxStamps) + s], stamps + s, sizeof(ulong), flags) != S.S_OK)
            {
                return false;
            }
        }

        return true;
    }
}
