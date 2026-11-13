using System.Numerics;
using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace HyperPlot.SurfaceLod;

/// <summary>Takes the GPU times of a frame: milliseconds from the frame's first timestamp to each later one.</summary>
internal delegate void TimedHandler(long frame, ReadOnlySpan<double> times);

/// <summary>The <c>Frame</c> constant buffer of Surface.hlsl, 128 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameConstants
{
    public Matrix4x4 Transform;
    public Vector3 Eye;
    public float ZScale;
    public Vector3 Light;
    public float ZOffset;
    public Vector2 XUniform;
    public Vector2 YUniform;
    public uint Side;
    public uint Uniform;
    public uint Pad0;
    public uint Pad1;
}

/// <summary>A tile of Surface.hlsl's <c>Tiles</c>, 32 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TileInstance
{
    public uint Face;
    public uint OriginX;
    public uint OriginY;
    public uint SampleX;
    public uint SampleY;
    public uint Step;
    public uint Pad0;
    public uint Pad1;
}

/// <summary>
/// SPIKE (row K0c, Issue #489): a D3D11 device of its own, a 1,920 × 1,080 BGRA8 target with a D32_FLOAT depth buffer,
/// the pool of heights (one R32_FLOAT texture array of square faces, as the residency cache's float pool, design §5.1;
/// the product keeps pages of 64 faces, the spike one array so that one draw reads every face), the 16 index lists, the
/// coordinate arrays, and GPU timestamps: the start, after the uploads, after the clears, after the draws. Not product
/// code: no device-loss handling, every failure throws.
/// </summary>
internal sealed unsafe class Gpu : IDisposable
{
    /// <summary>The timestamps of a frame.</summary>
    public const int MaxStamps = 4;

    private const int Sets = 8;

    private readonly int _width;
    private readonly int _height;
    private readonly int _faceSide;
    private readonly int _faces;
    private readonly int[] _indexStarts = new int[Stitch.Shapes];
    private readonly int[] _indexCounts = new int[Stitch.Shapes];
    private readonly nint[] _disjoint = new nint[Sets];
    private readonly nint[] _stamps = new nint[Sets * MaxStamps];
    private readonly long[] _setFrame = new long[Sets];
    private readonly bool[] _pending = new bool[Sets];
    private int _nextSet;
    private int _instanceCapacity;

    private ID3D11Device* _device;
    private ID3D11DeviceContext* _context;
    private ID3D11Texture2D* _target;
    private ID3D11RenderTargetView* _targetView;
    private ID3D11Texture2D* _staging;
    private ID3D11Texture2D* _depth;
    private ID3D11DepthStencilView* _depthView;
    private ID3D11Texture2D* _pool;
    private ID3D11ShaderResourceView* _poolView;
    private ID3D11Buffer* _indices;
    private ID3D11Buffer* _xs;
    private ID3D11ShaderResourceView* _xsView;
    private ID3D11Buffer* _ys;
    private ID3D11ShaderResourceView* _ysView;
    private ID3D11Buffer* _instances;
    private ID3D11ShaderResourceView* _instanceView;
    private ID3D11Buffer* _constants;
    private ID3D11Buffer* _group;
    private ID3D11VertexShader* _surfaceVS;
    private ID3D11PixelShader* _surfacePS;
    private ID3D11RasterizerState* _rasterizer;
    private ID3D11DepthStencilState* _depthLess;

    public Gpu(bool warp, int width, int height, int faceSide, int faces, int maxTiles, SurfaceData data)
    {
        (_width, _height, _faceSide, _faces) = (width, height, faceSide, faces);
        try
        {
            MakeDevice(warp);
            IsWarp = warp;
            MakeTargets();
            MakePool();
            var indices = Stitch.Build(data.Tile, _indexStarts, _indexCounts);
            StitchProblem = Stitch.Check(data.Tile, indices, _indexStarts, _indexCounts);
            IndexBytes = (long)indices.Length * sizeof(uint);
            _indices = MakeBuffer(indices, D3D11_BIND_FLAG.D3D11_BIND_INDEX_BUFFER);
            _xs = MakeBuffer(data.X, D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE);
            _xsView = MakeFloatView(_xs, data.X.Length);
            _ys = MakeBuffer(data.Y, D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE);
            _ysView = MakeFloatView(_ys, data.Y.Length);
            MakeInstances(maxTiles);
            _constants = MakeConstants(sizeof(FrameConstants));
            _group = MakeConstants(16);
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

    /// <summary>Null when every index list passed Stitch.Check, else what failed.</summary>
    public string? StitchProblem { get; }

    /// <summary>The bytes of the 16 index lists.</summary>
    public long IndexBytes { get; }

    /// <summary>The triangles of each edge shape's list.</summary>
    public int Triangles(int shape) => _indexCounts[shape] / 3;

    /// <summary>Starts a timed frame: returns the timer's set, or -1 when the next set is still pending (not timed).</summary>
    public int Begin(long frame)
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

    /// <summary>
    /// Copies a tile's (side × side) heights from a level's row-major array into a face at (x, y): the rows are read in
    /// place with the level's row pitch, and the runtime copies them before it returns.
    /// </summary>
    public void Upload(int face, int x, int y, float[] level, int levelWidth, int column, int row, int side)
    {
        var first = (row * levelWidth) + column;
        var last = ((row + side - 1) * levelWidth) + column + side;
        if (face < 0 || face >= _faces || x + side > _faceSide || y + side > _faceSide || column + side > levelWidth || last > level.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(face), "The tile does not fit the face or the level.");
        }

        var box = new D3D11_BOX
        {
            left = (uint)x,
            top = (uint)y,
            front = 0,
            right = (uint)(x + side),
            bottom = (uint)(y + side),
            back = 1,
        };

        // The span [first, last) holds every row the copy reads: side rows of side values, levelWidth apart.
        fixed (float* data = level.AsSpan(first, last - first))
        {
            _context->UpdateSubresource((ID3D11Resource*)_pool, (uint)face, &box, data, (uint)(levelWidth * sizeof(float)), 0);
        }
    }

    /// <summary>
    /// Records the rest of a frame: a stamp after the uploads, the clears, a stamp, a draw for each edge shape that has
    /// tiles (its tiles are counts[shape] instances from starts[shape]), a stamp, and sends the commands. Nothing waits
    /// for the GPU.
    /// </summary>
    public void Draw(int set, in FrameConstants constants, ReadOnlySpan<TileInstance> tiles, ReadOnlySpan<int> starts, ReadOnlySpan<int> counts)
    {
        var context = _context;
        Stamp(set, 1);
        D3D11_MAPPED_SUBRESOURCE mapped;
        Check(context->Map((ID3D11Resource*)_constants, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map");
        *(FrameConstants*)mapped.pData = constants;
        context->Unmap((ID3D11Resource*)_constants, 0);
        if (tiles.Length > _instanceCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(tiles), "More tiles than the instance buffer holds.");
        }

        if (tiles.Length > 0)
        {
            Check(context->Map((ID3D11Resource*)_instances, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map");

            // The buffer holds _instanceCapacity tiles, checked above.
            tiles.CopyTo(new Span<TileInstance>(mapped.pData, _instanceCapacity));
            context->Unmap((ID3D11Resource*)_instances, 0);
        }

        // Magenta behind the surface: a crack between two tiles shows as magenta pixels inside it.
        var clear = stackalloc float[] { 1, 0, 1, 1 };
        context->ClearRenderTargetView(_targetView, clear);
        context->ClearDepthStencilView(_depthView, (uint)D3D11_CLEAR_FLAG.D3D11_CLEAR_DEPTH, 1, 0);
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
        context->IASetIndexBuffer(_indices, DXGI_FORMAT.DXGI_FORMAT_R32_UINT, 0);
        var buffers = stackalloc ID3D11Buffer*[] { _constants, _group };
        context->VSSetConstantBuffers(0, 2, buffers);
        context->PSSetConstantBuffers(0, 1, buffers);
        var views = stackalloc ID3D11ShaderResourceView*[] { _instanceView, _poolView, _xsView, _ysView };
        context->VSSetShaderResources(0, 4, views);
        context->VSSetShader(_surfaceVS, null, 0);
        context->PSSetShader(_surfacePS, null, 0);
        context->OMSetBlendState(null, null, 0xFFFFFFFF);
        context->OMSetDepthStencilState(_depthLess, 0);
        var targetView = _targetView;
        context->OMSetRenderTargets(1, &targetView, _depthView);
        for (var shape = 0; shape < Stitch.Shapes; shape++)
        {
            if (counts[shape] == 0)
            {
                continue;
            }

            Check(context->Map((ID3D11Resource*)_group, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map");
            *(uint*)mapped.pData = (uint)starts[shape];
            context->Unmap((ID3D11Resource*)_group, 0);
            context->DrawIndexedInstanced((uint)_indexCounts[shape], (uint)counts[shape], (uint)_indexStarts[shape], 0, 0);
        }

        var none = stackalloc ID3D11ShaderResourceView*[] { null, null, null, null };
        context->VSSetShaderResources(0, 4, none);
        context->OMSetRenderTargets(0, null, null);
        Stamp(set, 3);
        if (set >= 0)
        {
            context->End((ID3D11Asynchronous*)_disjoint[set]);
            _pending[set] = true;
        }

        context->Flush();
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

        Release(ref _depthLess);
        Release(ref _rasterizer);
        Release(ref _surfacePS);
        Release(ref _surfaceVS);
        Release(ref _group);
        Release(ref _constants);
        Release(ref _instanceView);
        Release(ref _instances);
        Release(ref _ysView);
        Release(ref _ys);
        Release(ref _xsView);
        Release(ref _xs);
        Release(ref _indices);
        Release(ref _poolView);
        Release(ref _pool);
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

        description.Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT;
        description.CPUAccessFlags = 0;
        description.Format = DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT;
        description.BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_DEPTH_STENCIL;
        Check(_device->CreateTexture2D(&description, null, &texture), "CreateTexture2D");
        _depth = texture;
        ID3D11DepthStencilView* depthView;
        Check(_device->CreateDepthStencilView((ID3D11Resource*)_depth, null, &depthView), "CreateDepthStencilView");
        _depthView = depthView;
    }

    private void MakePool()
    {
        var description = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)_faceSide,
            Height = (uint)_faceSide,
            MipLevels = 1,
            ArraySize = (uint)_faces,
            Format = DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT,
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
        };
        ID3D11Texture2D* texture;
        Check(_device->CreateTexture2D(&description, null, &texture), "CreateTexture2D");
        _pool = texture;
        ID3D11ShaderResourceView* view;
        Check(_device->CreateShaderResourceView((ID3D11Resource*)_pool, null, &view), "CreateShaderResourceView");
        _poolView = view;
    }

    private ID3D11Buffer* MakeBuffer<T>(T[] values, D3D11_BIND_FLAG bind)
        where T : unmanaged
    {
        var description = new D3D11_BUFFER_DESC
        {
            ByteWidth = (uint)(values.Length * sizeof(T)),
            Usage = D3D11_USAGE.D3D11_USAGE_IMMUTABLE,
            BindFlags = (uint)bind,
        };
        ID3D11Buffer* buffer;

        // The array bounds the bytes the device copies: ByteWidth is its length.
        fixed (T* data = values)
        {
            var initial = new D3D11_SUBRESOURCE_DATA { pSysMem = data };
            Check(_device->CreateBuffer(&description, &initial, &buffer), "CreateBuffer");
        }

        return buffer;
    }

    private ID3D11ShaderResourceView* MakeFloatView(ID3D11Buffer* buffer, int count)
    {
        var view = new D3D11_SHADER_RESOURCE_VIEW_DESC
        {
            Format = DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT,
            ViewDimension = D3D_SRV_DIMENSION.D3D11_SRV_DIMENSION_BUFFER,
        };
        view.Buffer.FirstElement = 0;
        view.Buffer.NumElements = (uint)count;
        ID3D11ShaderResourceView* resourceView;
        Check(_device->CreateShaderResourceView((ID3D11Resource*)buffer, &view, &resourceView), "CreateShaderResourceView");
        return resourceView;
    }

    private void MakeInstances(int capacity)
    {
        var description = new D3D11_BUFFER_DESC
        {
            ByteWidth = (uint)(capacity * sizeof(TileInstance)),
            Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
            MiscFlags = (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_BUFFER_STRUCTURED,
            StructureByteStride = (uint)sizeof(TileInstance),
        };
        ID3D11Buffer* buffer;
        Check(_device->CreateBuffer(&description, null, &buffer), "CreateBuffer");
        _instances = buffer;
        var view = new D3D11_SHADER_RESOURCE_VIEW_DESC
        {
            Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN,
            ViewDimension = D3D_SRV_DIMENSION.D3D11_SRV_DIMENSION_BUFFER,
        };
        view.Buffer.FirstElement = 0;
        view.Buffer.NumElements = (uint)capacity;
        ID3D11ShaderResourceView* resourceView;
        Check(_device->CreateShaderResourceView((ID3D11Resource*)_instances, &view, &resourceView), "CreateShaderResourceView");
        _instanceView = resourceView;
        _instanceCapacity = capacity;
    }

    private ID3D11Buffer* MakeConstants(int bytes)
    {
        var description = new D3D11_BUFFER_DESC
        {
            ByteWidth = (uint)bytes,
            Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
        };
        ID3D11Buffer* buffer;
        Check(_device->CreateBuffer(&description, null, &buffer), "CreateBuffer");
        return buffer;
    }

    private void MakeShaders()
    {
        fixed (byte* code = Shaders.SurfaceVS)
        {
            ID3D11VertexShader* shader;
            Check(_device->CreateVertexShader(code, (nuint)Shaders.SurfaceVS.Length, null, &shader), "CreateVertexShader");
            _surfaceVS = shader;
        }

        fixed (byte* code = Shaders.SurfacePS)
        {
            ID3D11PixelShader* shader;
            Check(_device->CreatePixelShader(code, (nuint)Shaders.SurfacePS.Length, null, &shader), "CreatePixelShader");
            _surfacePS = shader;
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

        var depth = new D3D11_DEPTH_STENCIL_DESC
        {
            DepthEnable = BOOL.TRUE,
            DepthWriteMask = D3D11_DEPTH_WRITE_MASK.D3D11_DEPTH_WRITE_MASK_ALL,
            DepthFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_LESS,
        };
        ID3D11DepthStencilState* depthState;
        Check(_device->CreateDepthStencilState(&depth, &depthState), "CreateDepthStencilState");
        _depthLess = depthState;
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

    private void Stamp(int set, int index)
    {
        if (set >= 0)
        {
            _context->End((ID3D11Asynchronous*)_stamps[(set * MaxStamps) + index]);
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
