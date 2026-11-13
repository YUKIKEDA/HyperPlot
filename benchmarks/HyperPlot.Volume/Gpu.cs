using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace HyperPlot.Volume;

/// <summary>Takes the GPU times of a frame: milliseconds from the frame's first timestamp to each later one.</summary>
internal delegate void TimedHandler(long frame, ReadOnlySpan<double> times);

/// <summary>The <c>Frame</c> constant buffer of Volume.hlsl, 192 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameConstants
{
    public Matrix4x4 InverseTransform;
    public Vector4 Eye;
    public Vector4 RangeLow;
    public Vector4 RangeHigh;
    public Vector2 Size;
    public Vector2 InverseSize;
    public uint Volume;
    public uint Grid;
    public uint Brick;
    public uint Side;
    public uint SlotsX;
    public uint SlotsY;
    public uint SlotsZ;
    public uint SlotsPerAtlas;
    public Vector3 InverseAtlas;
    public float Threshold;
    public float ReferenceSteps;
    public uint MaxSteps;
    public uint Atlases;
    public float Padding;
}

/// <summary>
/// The slots of the atlas: up to 4 R16_UNORM Texture3D of the same size, each at most 2,048 texels a side (FL11_0,
/// D3D11_REQ_TEXTURE3D_U_V_OR_W_DIMENSION) and at most PerAtlasBytes (D3D11 limits a resource to 2,048 MB, less on a GPU with
/// less than 8 GB). Slot s is in atlas s / SlotsPerAtlas, x fastest inside it.
/// </summary>
internal readonly record struct AtlasLayout(int Atlases, int SlotsX, int SlotsY, int SlotsZ, int Side)
{
    public const int MaxAtlases = 4;
    public const long PerAtlasBytes = 1536L << 20;

    public int SlotsPerAtlas => SlotsX * SlotsY * SlotsZ;

    public int Capacity => Atlases * SlotsPerAtlas;

    /// <summary>The bytes of all atlases, slack included.</summary>
    public long Bytes => (long)Atlases * (SlotsX * Side) * (SlotsY * Side) * (SlotsZ * Side) * sizeof(ushort);

    /// <summary>The fewest atlases, each about the same, that hold the slots wanted.</summary>
    public static AtlasLayout For(int side, long brickBytes, int wanted)
    {
        var maxSide = 2048 / side;
        var perAtlas = (int)Math.Min(PerAtlasBytes / brickBytes, (long)maxSide * maxSide * maxSide);
        var atlases = (wanted + perAtlas - 1) / perAtlas;
        if (atlases > MaxAtlases)
        {
            throw new ArgumentException($"{wanted} slots need {atlases} atlases; the shader reads {MaxAtlases}.");
        }

        var target = (wanted + atlases - 1) / atlases;
        var x = Math.Min(maxSide, target);
        var rest = (target + x - 1) / x;
        var z = (rest + maxSide - 1) / maxSide;
        var y = (rest + z - 1) / z;
        return new AtlasLayout(atlases, x, y, z, side);
    }
}

/// <summary>
/// SPIKE (row K0e, Issue #491): a D3D11 device of its own, the atlas of bricks, the table, the transfer function, a 1,920 ×
/// 1,080 BGRA8 target with a depth buffer the march reads, the ray-marching shader, and GPU timestamps around the clears, the
/// upload, and the march. Not product code: no device-loss handling, every failure throws.
/// </summary>
internal sealed unsafe class Gpu : IDisposable
{
    /// <summary>The timestamps of a frame: the start, after the clears, after the upload, after the march.</summary>
    public const int MaxStamps = 4;

    private const int Sets = 8;

    private readonly int _width;
    private readonly int _height;
    private readonly BrickStore _store;
    private readonly nint[] _atlases = new nint[AtlasLayout.MaxAtlases];
    private readonly nint[] _atlasViews = new nint[AtlasLayout.MaxAtlases];
    private readonly nint[] _disjoint = new nint[Sets];
    private readonly nint[] _stamps = new nint[Sets * MaxStamps];
    private readonly long[] _setFrame = new long[Sets];
    private readonly bool[] _pending = new bool[Sets];
    private int _nextSet;

    private ID3D11Device* _device;
    private ID3D11DeviceContext* _context;
    private ID3D11Texture2D* _target;
    private ID3D11RenderTargetView* _targetView;
    private ID3D11Texture2D* _staging;
    private ID3D11Texture2D* _depth;
    private ID3D11DepthStencilView* _depthView;
    private ID3D11ShaderResourceView* _depthRead;
    private ID3D11Texture3D* _table;
    private ID3D11ShaderResourceView* _tableView;
    private ID3D11Texture1D* _transfer;
    private ID3D11ShaderResourceView* _transferView;
    private ID3D11SamplerState* _sampler;
    private ID3D11Buffer* _constants;
    private ID3D11VertexShader* _vertexShader;
    private ID3D11PixelShader* _pixelShader;
    private ID3D11RasterizerState* _rasterizer;

    public Gpu(bool warp, int width, int height, BrickStore store, TransferFunction transfer, AtlasLayout layout, bool loadFilter, string shaderPath)
    {
        (_width, _height, _store, Layout) = (width, height, store, layout);
        try
        {
            MakeDevice(warp);
            IsWarp = warp;
            MakeTargets();
            MakeVolume(transfer);
            MakeShaders(shaderPath, loadFilter);
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

    public AtlasLayout Layout { get; }

    /// <summary>The bytes of the GPU's resources for the volume: the atlases, the table, and the transfer function.</summary>
    public long VolumeBytes => Layout.Bytes + ((long)_store.Grid * _store.Grid * _store.Grid * sizeof(uint)) + (TransferFunction.Entries * 16);

    /// <summary>The constants that do not change from frame to frame.</summary>
    public FrameConstants Fixed(float threshold) =>
        new()
        {
            Size = new Vector2(_width, _height),
            InverseSize = new Vector2(1f / _width, 1f / _height),
            Volume = (uint)_store.Size,
            Grid = (uint)_store.Grid,
            Brick = (uint)_store.Brick,
            Side = (uint)_store.Side,
            SlotsX = (uint)Layout.SlotsX,
            SlotsY = (uint)Layout.SlotsY,
            SlotsZ = (uint)Layout.SlotsZ,
            SlotsPerAtlas = (uint)Layout.SlotsPerAtlas,
            InverseAtlas = new Vector3(1f / (Layout.SlotsX * Layout.Side), 1f / (Layout.SlotsY * Layout.Side), 1f / (Layout.SlotsZ * Layout.Side)),
            Threshold = threshold,
            ReferenceSteps = TransferFunction.ReferenceSteps,

            // Every level-0 step of the longest chord (√3), and as many again for the skips.
            MaxSteps = (uint)(8 * _store.Size),
            Atlases = (uint)Layout.Atlases,
        };

    /// <summary>
    /// Records one frame: clears, the bricks residency asked for and the table, the march, a timestamp after each, and sends
    /// the commands. Nothing waits for the GPU. Returns the bytes sent: the bricks, and the table.
    /// </summary>
    public (long Bricks, long Table) Draw(in FrameConstants constants, Residency residency, long frame)
    {
        var set = BeginTimer(frame);
        var context = _context;
        D3D11_MAPPED_SUBRESOURCE mapped;
        Check(context->Map((ID3D11Resource*)_constants, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map");
        *(FrameConstants*)mapped.pData = constants;
        context->Unmap((ID3D11Resource*)_constants, 0);

        var white = stackalloc float[] { 1, 1, 1, 1 };
        context->ClearRenderTargetView(_targetView, white);
        context->ClearDepthStencilView(_depthView, (uint)D3D11_CLEAR_FLAG.D3D11_CLEAR_DEPTH, 1, 0);
        Stamp(set, 1);

        // The bricks into their slots; each brick's Side³ codes are contiguous, x fastest.
        var side = (uint)_store.Side;
        var rowPitch = side * sizeof(ushort);
        var depthPitch = rowPitch * side;
        var bricks = 0L;
        for (var i = 0; i < residency.UploadCount; i++)
        {
            var slot = residency.UploadSlots[i];
            var atlas = slot / Layout.SlotsPerAtlas;
            var inAtlas = slot - (atlas * Layout.SlotsPerAtlas);
            var x = (uint)(inAtlas % Layout.SlotsX) * side;
            var y = (uint)(inAtlas / Layout.SlotsX % Layout.SlotsY) * side;
            var z = (uint)(inAtlas / (Layout.SlotsX * Layout.SlotsY)) * side;
            var box = new D3D11_BOX
            {
                left = x,
                top = y,
                front = z,
                right = x + side,
                bottom = y + side,
                back = z + side,
            };
            context->UpdateSubresource((ID3D11Resource*)_atlases[atlas], 0, &box, _store.Address(residency.UploadBricks[i]), rowPitch, depthPitch);
            bricks += _store.BrickBytes;
        }

        var grid = (uint)_store.Grid;
        fixed (uint* table = residency.Table)
        {
            // The array holds Grid³ entries, the texture's size.
            context->UpdateSubresource((ID3D11Resource*)_table, 0, null, table, grid * sizeof(uint), grid * grid * sizeof(uint));
        }

        Stamp(set, 2);

        var viewport = new D3D11_VIEWPORT
        {
            Width = _width,
            Height = _height,
            MaxDepth = 1,
        };
        context->RSSetViewports(1, &viewport);
        context->RSSetState(_rasterizer);
        context->IASetInputLayout(null);
        context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        var buffer = _constants;
        context->PSSetConstantBuffers(0, 1, &buffer);
        var views = stackalloc ID3D11ShaderResourceView*[7];
        for (var a = 0; a < AtlasLayout.MaxAtlases; a++)
        {
            views[a] = (ID3D11ShaderResourceView*)_atlasViews[a];
        }

        views[4] = _tableView;
        views[5] = _transferView;
        views[6] = _depthRead;
        context->PSSetShaderResources(0, 7, views);
        var sampler = _sampler;
        context->PSSetSamplers(0, 1, &sampler);
        context->VSSetShader(_vertexShader, null, 0);
        context->PSSetShader(_pixelShader, null, 0);
        context->OMSetBlendState(null, null, 0xFFFFFFFF);
        context->OMSetDepthStencilState(null, 0);
        var targetView = _targetView;
        context->OMSetRenderTargets(1, &targetView, null);
        context->Draw(3, 0);
        for (var v = 0; v < 7; v++)
        {
            views[v] = null;
        }

        context->PSSetShaderResources(0, 7, views);
        Stamp(set, 3);
        EndTimer(set);
        context->OMSetRenderTargets(0, null, null);
        context->Flush();
        return (bricks, (long)grid * grid * grid * sizeof(uint));
    }

    /// <summary>
    /// Reads every timed frame that is done, oldest first, without waiting, or waiting for all when asked; calls back with
    /// the frame and the times in milliseconds from the first stamp to each later one.
    /// </summary>
    public void Poll(TimedHandler timed, bool wait)
    {
        Span<double> times = stackalloc double[MaxStamps - 1];
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

            for (var s = 1; s < MaxStamps; s++)
            {
                times[s - 1] = (stamps[s] - stamps[0]) * 1000.0 / info.Frequency;
            }

            timed(_setFrame[set], times);
        }
    }

    /// <summary>Copies the target to the staging texture, waits for it, and returns its BGRA8 rows, packed.</summary>
    public byte[] Read()
    {
        _context->CopyResource((ID3D11Resource*)_staging, (ID3D11Resource*)_target);
        D3D11_MAPPED_SUBRESOURCE mapped;
        Check(_context->Map((ID3D11Resource*)_staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped), "Map");
        var pixels = new byte[_width * _height * 4];
        try
        {
            // The mapped rows are RowPitch bytes apart; the spans bound every read.
            var rowBytes = _width * 4;
            var source = new ReadOnlySpan<byte>(mapped.pData, (int)(mapped.RowPitch * (uint)(_height - 1)) + rowBytes);
            for (var y = 0; y < _height; y++)
            {
                source.Slice((int)(y * mapped.RowPitch), rowBytes).CopyTo(pixels.AsSpan(y * rowBytes, rowBytes));
            }
        }
        finally
        {
            _context->Unmap((ID3D11Resource*)_staging, 0);
        }

        return pixels;
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

        Release(ref _rasterizer);
        Release(ref _pixelShader);
        Release(ref _vertexShader);
        Release(ref _constants);
        Release(ref _sampler);
        Release(ref _transferView);
        Release(ref _transfer);
        Release(ref _tableView);
        Release(ref _table);
        for (var a = 0; a < AtlasLayout.MaxAtlases; a++)
        {
            ReleaseNint(ref _atlasViews[a]);
            ReleaseNint(ref _atlases[a]);
        }

        Release(ref _depthRead);
        Release(ref _depthView);
        Release(ref _depth);
        Release(ref _staging);
        Release(ref _targetView);
        Release(ref _target);
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

    // Compiles an entry point of the HLSL file with fxc's compiler; the source and the names are pinned for the call.
    private static byte[] Compile(byte[] source, string sourceName, ReadOnlySpan<byte> entry, ReadOnlySpan<byte> profile, bool loadFilter)
    {
        // D3DCOMPILE_ENABLE_STRICTNESS | D3DCOMPILE_OPTIMIZATION_LEVEL3
        const uint Flags = (1u << 11) | (1u << 15);
        var name = Encoding.UTF8.GetBytes(sourceName + "\0");
        var defineName = "FILTER_LOAD\0"u8;
        var defineValue = "1\0"u8;
        ID3DBlob* code = null;
        ID3DBlob* errors = null;
        try
        {
            fixed (byte* data = source)
            fixed (byte* namePointer = name)
            fixed (byte* entryPointer = entry)
            fixed (byte* profilePointer = profile)
            fixed (byte* defineNamePointer = defineName)
            fixed (byte* defineValuePointer = defineValue)
            {
                var defines = stackalloc D3D_SHADER_MACRO[2];
                defines[0] = new D3D_SHADER_MACRO { Name = (sbyte*)defineNamePointer, Definition = (sbyte*)defineValuePointer };
                defines[1] = default;
                var result = DirectX.D3DCompile(
                    data,
                    (nuint)source.Length,
                    (sbyte*)namePointer,
                    loadFilter ? defines : null,
                    null,
                    (sbyte*)entryPointer,
                    (sbyte*)profilePointer,
                    Flags,
                    0,
                    &code,
                    &errors
                );
                if (result.FAILED)
                {
                    var message =
                        errors == null ? string.Empty : Encoding.UTF8.GetString((byte*)errors->GetBufferPointer(), (int)errors->GetBufferSize());
                    throw new InvalidOperationException($"D3DCompile failed with 0x{result.Value:X8}: {message}") { HResult = result.Value };
                }
            }

            // The blob holds GetBufferSize bytes from GetBufferPointer while it is alive.
            return new ReadOnlySpan<byte>(code->GetBufferPointer(), (int)code->GetBufferSize()).ToArray();
        }
        finally
        {
            Release(ref code);
            Release(ref errors);
        }
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

    private void MakeTargets()
    {
        var description = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET,
        };
        ID3D11Texture2D* texture;
        Check(_device->CreateTexture2D(&description, null, &texture), "CreateTexture2D");
        _target = texture;
        ID3D11RenderTargetView* targetView;
        Check(_device->CreateRenderTargetView((ID3D11Resource*)_target, null, &targetView), "CreateRenderTargetView");
        _targetView = targetView;

        description.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
        description.BindFlags = 0;
        description.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
        Check(_device->CreateTexture2D(&description, null, &texture), "CreateTexture2D");
        _staging = texture;

        // The depth of the opaque shapes: written as D32_FLOAT, read by the march as R32_FLOAT.
        description.Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT;
        description.CPUAccessFlags = 0;
        description.Format = DXGI_FORMAT.DXGI_FORMAT_R32_TYPELESS;
        description.BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_DEPTH_STENCIL | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE);
        Check(_device->CreateTexture2D(&description, null, &texture), "CreateTexture2D");
        _depth = texture;
        var depthDescription = new D3D11_DEPTH_STENCIL_VIEW_DESC
        {
            Format = DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT,
            ViewDimension = D3D11_DSV_DIMENSION.D3D11_DSV_DIMENSION_TEXTURE2D,
        };
        ID3D11DepthStencilView* depthView;
        Check(_device->CreateDepthStencilView((ID3D11Resource*)_depth, &depthDescription, &depthView), "CreateDepthStencilView");
        _depthView = depthView;
        var readDescription = new D3D11_SHADER_RESOURCE_VIEW_DESC
        {
            Format = DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT,
            ViewDimension = D3D_SRV_DIMENSION.D3D11_SRV_DIMENSION_TEXTURE2D,
        };
        readDescription.Texture2D.MipLevels = 1;
        ID3D11ShaderResourceView* depthRead;
        Check(_device->CreateShaderResourceView((ID3D11Resource*)_depth, &readDescription, &depthRead), "CreateShaderResourceView");
        _depthRead = depthRead;
    }

    private void MakeVolume(TransferFunction transfer)
    {
        var side = (uint)Layout.Side;
        var atlas = new D3D11_TEXTURE3D_DESC
        {
            Width = (uint)Layout.SlotsX * side,
            Height = (uint)Layout.SlotsY * side,
            Depth = (uint)Layout.SlotsZ * side,
            MipLevels = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_R16_UNORM,
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
        };
        for (var a = 0; a < Layout.Atlases; a++)
        {
            ID3D11Texture3D* texture;
            Check(_device->CreateTexture3D(&atlas, null, &texture), "CreateTexture3D");
            _atlases[a] = (nint)texture;
            ID3D11ShaderResourceView* view;
            Check(_device->CreateShaderResourceView((ID3D11Resource*)texture, null, &view), "CreateShaderResourceView");
            _atlasViews[a] = (nint)view;
        }

        var grid = (uint)_store.Grid;
        var table = new D3D11_TEXTURE3D_DESC
        {
            Width = grid,
            Height = grid,
            Depth = grid,
            MipLevels = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_R32_UINT,
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
        };
        ID3D11Texture3D* tableTexture;
        Check(_device->CreateTexture3D(&table, null, &tableTexture), "CreateTexture3D");
        _table = tableTexture;
        ID3D11ShaderResourceView* tableView;
        Check(_device->CreateShaderResourceView((ID3D11Resource*)_table, null, &tableView), "CreateShaderResourceView");
        _tableView = tableView;

        var line = new D3D11_TEXTURE1D_DESC
        {
            Width = TransferFunction.Entries,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT,
            Usage = D3D11_USAGE.D3D11_USAGE_IMMUTABLE,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
        };
        ID3D11Texture1D* lineTexture;

        // The table has Entries float4s, the texture's size.
        fixed (Vector4* entries = transfer.Table)
        {
            var initial = new D3D11_SUBRESOURCE_DATA { pSysMem = entries, SysMemPitch = TransferFunction.Entries * 16 };
            Check(_device->CreateTexture1D(&line, &initial, &lineTexture), "CreateTexture1D");
        }

        _transfer = lineTexture;
        ID3D11ShaderResourceView* lineView;
        Check(_device->CreateShaderResourceView((ID3D11Resource*)_transfer, null, &lineView), "CreateShaderResourceView");
        _transferView = lineView;

        var sampler = new D3D11_SAMPLER_DESC
        {
            Filter = D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_LINEAR,
            AddressU = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressV = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            ComparisonFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_NEVER,
            MaxLOD = float.MaxValue,
        };
        ID3D11SamplerState* samplerState;
        Check(_device->CreateSamplerState(&sampler, &samplerState), "CreateSamplerState");
        _sampler = samplerState;

        var constants = new D3D11_BUFFER_DESC
        {
            ByteWidth = (uint)sizeof(FrameConstants),
            Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
        };
        ID3D11Buffer* buffer;
        Check(_device->CreateBuffer(&constants, null, &buffer), "CreateBuffer");
        _constants = buffer;
    }

    private void MakeShaders(string shaderPath, bool loadFilter)
    {
        var source = File.ReadAllBytes(shaderPath);
        var vertex = Compile(source, Path.GetFileName(shaderPath), "FullScreenVS\0"u8, "vs_5_0\0"u8, loadFilter);
        fixed (byte* code = vertex)
        {
            ID3D11VertexShader* shader;
            Check(_device->CreateVertexShader(code, (nuint)vertex.Length, null, &shader), "CreateVertexShader");
            _vertexShader = shader;
        }

        var pixel = Compile(source, Path.GetFileName(shaderPath), "MarchPS\0"u8, "ps_5_0\0"u8, loadFilter);
        fixed (byte* code = pixel)
        {
            ID3D11PixelShader* shader;
            Check(_device->CreatePixelShader(code, (nuint)pixel.Length, null, &shader), "CreatePixelShader");
            _pixelShader = shader;
        }
    }

    private void MakeStates()
    {
        var rasterizer = new D3D11_RASTERIZER_DESC
        {
            FillMode = D3D11_FILL_MODE.D3D11_FILL_SOLID,
            CullMode = D3D11_CULL_MODE.D3D11_CULL_NONE,
            DepthClipEnable = BOOL.TRUE,
        };
        ID3D11RasterizerState* state;
        Check(_device->CreateRasterizerState(&rasterizer, &state), "CreateRasterizerState");
        _rasterizer = state;
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

    private void EndTimer(int set)
    {
        if (set >= 0)
        {
            _context->End((ID3D11Asynchronous*)_disjoint[set]);
            _pending[set] = true;
        }
    }

    private bool Ready(int set, D3D11_QUERY_DATA_TIMESTAMP_DISJOINT* info, ulong* stamps, uint flags)
    {
        if (_context->GetData((ID3D11Asynchronous*)_disjoint[set], info, (uint)sizeof(D3D11_QUERY_DATA_TIMESTAMP_DISJOINT), flags) != S.S_OK)
        {
            return false;
        }

        for (var s = 0; s < MaxStamps; s++)
        {
            if (_context->GetData((ID3D11Asynchronous*)_stamps[(set * MaxStamps) + s], stamps + s, sizeof(ulong), flags) != S.S_OK)
            {
                return false;
            }
        }

        return true;
    }
}
