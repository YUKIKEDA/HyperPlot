using System.Numerics;
using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace HyperPlot.Points3D;

/// <summary>How the points are drawn.</summary>
internal enum Method
{
    /// <summary>A: instanced quads with a D32_FLOAT depth test and write.</summary>
    Markers,

    /// <summary>B: compute rasterization, InterlockedMin of the depth, then of the index, then a full-screen resolve.</summary>
    Compute,
}

/// <summary>Takes the GPU times of a frame: milliseconds from the frame's first timestamp to each later one.</summary>
internal delegate void TimedHandler(long frame, ReadOnlySpan<double> times);

/// <summary>The <c>Frame</c> constant buffer of Points3D.hlsl, 96 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameConstants
{
    public Matrix4x4 Transform;
    public Vector2 Size;
    public Vector2 InverseSize;
    public float Diameter;
    public uint Footprint;
    public uint Count;
    public uint GroupsX;
}

/// <summary>
/// SPIKE (row K0a, Issue #477): a D3D11 device of its own, the points in one structured buffer, a 1,920 × 1,080 target with
/// a D32_FLOAT depth buffer and two R32_UINT textures, the shaders of both methods, and GPU timestamps around each pass.
/// Not product code: no device-loss handling, every failure throws.
/// </summary>
internal sealed unsafe class Gpu : IDisposable
{
    /// <summary>The timestamps of a frame: the start, after the clears, and after each pass (A has 1, B has 3).</summary>
    public const int MaxStamps = 5;

    private const int Sets = 8;
    private const uint Empty = 0xFFFFFFFF;

    /// <summary>
    /// The most points of a page of the octree scene: one buffer for a run of whole nodes. The 1e7 runs made one buffer of
    /// 1e7 points and a view a node over it; at 1e8 a node's view into one buffer of 1e8 points (1.6 GB) failed with
    /// E_INVALIDARG, while the view of the whole buffer did not. Which bound the runtime checked was not found: the debug
    /// layer was off and the message named no node (Issue #477). A page keeps each buffer and each view's first element
    /// within what the 1e7 runs made. A node of more points than this gets a page of its own.
    /// </summary>
    private const int PagePoints = 10_000_000;

    private readonly int _width;
    private readonly int _height;
    private readonly int _count;
    private readonly nint[] _disjoint = new nint[Sets];
    private readonly nint[] _stamps = new nint[Sets * MaxStamps];
    private readonly long[] _setFrame = new long[Sets];
    private readonly int[] _setStamps = new int[Sets];
    private readonly bool[] _pending = new bool[Sets];

    // The octree scene's pages (buffers of whole nodes) and views: one over each node's points of its page, by node (SPIKE,
    // hypotheses 2 to 4).
    private readonly List<nint> _pages = [];
    private nint[] _nodeViews = [];
    private int _nextSet;

    private ID3D11Device* _device;
    private ID3D11DeviceContext* _context;
    private ID3D11Buffer* _points;
    private ID3D11ShaderResourceView* _pointView;
    private ID3D11Buffer* _constants;
    private ID3D11Texture2D* _target;
    private ID3D11RenderTargetView* _targetView;
    private ID3D11Texture2D* _staging;
    private ID3D11Texture2D* _depth;
    private ID3D11DepthStencilView* _depthView;
    private ID3D11Texture2D* _depthKeys;
    private ID3D11UnorderedAccessView* _depthKeysAccess;
    private ID3D11ShaderResourceView* _depthKeysView;
    private ID3D11Texture2D* _nearest;
    private ID3D11UnorderedAccessView* _nearestAccess;
    private ID3D11ShaderResourceView* _nearestView;
    private ID3D11VertexShader* _markerVS;
    private ID3D11PixelShader* _discPS;
    private ID3D11PixelShader* _squarePS;
    private ID3D11ComputeShader* _depthCS;
    private ID3D11ComputeShader* _indexCS;
    private ID3D11VertexShader* _resolveVS;
    private ID3D11PixelShader* _resolvePS;
    private ID3D11RasterizerState* _rasterizer;
    private ID3D11DepthStencilState* _depthLess;
    private ID3D11DepthStencilState* _depthAlways;

    /// <summary>
    /// Makes the device, the targets, the shaders, and the timer, and the one buffer of <paramref name="points"/> that
    /// <see cref="Draw"/> reads. The octree scene passes no points: its points go to <see cref="SetNodes"/>.
    /// </summary>
    public Gpu(bool warp, int width, int height, ReadOnlySpan<Point3> points)
    {
        (_width, _height, _count) = (width, height, points.Length);
        try
        {
            MakeDevice(warp);
            IsWarp = warp;
            MakeTargets();
            MakePoints(points);
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

    /// <summary>The octree scene's pages: the buffers <see cref="SetNodes"/> made.</summary>
    public int Pages => _pages.Count;

    /// <summary>The number of thread groups along x of B's dispatch, and along y.</summary>
    public (uint X, uint Y) Groups
    {
        get
        {
            var groups = (uint)((_count + 255) / 256);
            var x = Math.Min(groups, 65535u);
            return (x, (groups + x - 1) / x);
        }
    }

    /// <summary>
    /// Records one frame: clears, the point passes of the method, a timestamp after each, and sends the commands. Nothing
    /// waits for the GPU.
    /// </summary>
    public void Draw(Method method, in FrameConstants constants, long frame)
    {
        if (_points == null)
        {
            throw new InvalidOperationException("Draw reads the one buffer of points; this device was made without points.");
        }

        var set = BeginTimer(frame);
        var context = _context;
        D3D11_MAPPED_SUBRESOURCE mapped;
        Check(context->Map((ID3D11Resource*)_constants, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map");
        *(FrameConstants*)mapped.pData = constants;
        context->Unmap((ID3D11Resource*)_constants, 0);

        var white = stackalloc float[] { 1, 1, 1, 1 };
        context->ClearRenderTargetView(_targetView, white);
        context->ClearDepthStencilView(_depthView, (uint)D3D11_CLEAR_FLAG.D3D11_CLEAR_DEPTH, 1, 0);
        if (method == Method.Compute)
        {
            var empty = stackalloc uint[] { Empty, Empty, Empty, Empty };
            context->ClearUnorderedAccessViewUint(_depthKeysAccess, empty);
            context->ClearUnorderedAccessViewUint(_nearestAccess, empty);
        }

        Stamp(set, 1);
        var buffer = _constants;
        var pointView = _pointView;
        var viewport = new D3D11_VIEWPORT
        {
            Width = _width,
            Height = _height,
            MaxDepth = 1,
        };
        var targetView = _targetView;
        if (method == Method.Markers)
        {
            context->RSSetViewports(1, &viewport);
            context->RSSetState(_rasterizer);
            context->IASetInputLayout(null);
            context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
            context->VSSetConstantBuffers(0, 1, &buffer);
            context->PSSetConstantBuffers(0, 1, &buffer);
            context->VSSetShaderResources(0, 1, &pointView);
            context->VSSetShader(_markerVS, null, 0);
            context->PSSetShader(constants.Footprint != 0 ? _squarePS : _discPS, null, 0);
            context->OMSetBlendState(null, null, 0xFFFFFFFF);
            context->OMSetDepthStencilState(_depthLess, 0);
            context->OMSetRenderTargets(1, &targetView, _depthView);
            context->DrawInstanced(4, (uint)_count, 0, 0);
            Stamp(set, 2);
            EndTimer(set, 3);
        }
        else
        {
            var (groupsX, groupsY) = Groups;
            context->CSSetConstantBuffers(0, 1, &buffer);
            context->CSSetShaderResources(0, 1, &pointView);

            // Pass 1: the nearest depth of each pixel.
            var access = _depthKeysAccess;
            context->CSSetUnorderedAccessViews(0, 1, &access, null);
            context->CSSetShader(_depthCS, null, 0);
            context->Dispatch(groupsX, groupsY, 1);
            Stamp(set, 2);

            // Pass 2: the least index among the points at that depth; the depths are read through a view of their own.
            access = null;
            context->CSSetUnorderedAccessViews(0, 1, &access, null);
            var depthKeysView = _depthKeysView;
            context->CSSetShaderResources(1, 1, &depthKeysView);
            access = _nearestAccess;
            context->CSSetUnorderedAccessViews(1, 1, &access, null);
            context->CSSetShader(_indexCS, null, 0);
            context->Dispatch(groupsX, groupsY, 1);
            access = null;
            context->CSSetUnorderedAccessViews(1, 1, &access, null);
            ID3D11ShaderResourceView* noView = null;
            context->CSSetShaderResources(1, 1, &noView);
            Stamp(set, 3);

            // Resolve: the colors over the target and the depths into the depth buffer.
            context->RSSetViewports(1, &viewport);
            context->RSSetState(_rasterizer);
            context->IASetInputLayout(null);
            context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            var views = stackalloc ID3D11ShaderResourceView*[] { _pointView, _depthKeysView, _nearestView };
            context->PSSetShaderResources(0, 3, views);
            context->VSSetShader(_resolveVS, null, 0);
            context->PSSetShader(_resolvePS, null, 0);
            context->OMSetBlendState(null, null, 0xFFFFFFFF);
            context->OMSetDepthStencilState(_depthAlways, 0);
            context->OMSetRenderTargets(1, &targetView, _depthView);
            context->Draw(3, 0);
            var none = stackalloc ID3D11ShaderResourceView*[] { null, null, null };
            context->PSSetShaderResources(0, 3, none);
            Stamp(set, 4);
            EndTimer(set, 5);
        }

        context->OMSetRenderTargets(0, null, null);
        context->Flush();
    }

    /// <summary>
    /// Puts the octree's points on the GPU in pages, each a buffer of a run of whole nodes of at most
    /// <see cref="PagePoints"/> points, and makes a view over each node's points of its page, so that A's marker shader reads
    /// a node's points from index 0: SV_InstanceID does not count the start instance of a draw. Replaces the pages and the
    /// views made before. The nodes are in the order of their points, which follow each other from the first point.
    /// </summary>
    public void SetNodes(ReadOnlySpan<OctreeNode> nodes, ReadOnlySpan<Point3> points)
    {
        ReleaseNodes();
        _nodeViews = new nint[nodes.Length];
        long next = 0;
        var start = 0;
        while (start < nodes.Length)
        {
            var first = nodes[start].First;
            var end = start;
            while (end < nodes.Length && (end == start || nodes[end].First + nodes[end].Count - first <= PagePoints))
            {
                if (nodes[end].First != next)
                {
                    throw new ArgumentException($"Node {end} starts at point {nodes[end].First}, not at {next} after the node before.");
                }

                next += nodes[end].Count;
                end++;
            }

            var length = (int)(next - first);
            var page = MakeStructuredBuffer(points.Slice((int)first, length));
            _pages.Add((nint)page);
            for (var i = start; i < end; i++)
            {
                var description = new D3D11_SHADER_RESOURCE_VIEW_DESC
                {
                    Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN,
                    ViewDimension = D3D_SRV_DIMENSION.D3D11_SRV_DIMENSION_BUFFER,
                };
                description.Buffer.FirstElement = (uint)(nodes[i].First - first);
                description.Buffer.NumElements = (uint)nodes[i].Count;
                ID3D11ShaderResourceView* view;
                Check(
                    _device->CreateShaderResourceView((ID3D11Resource*)page, &description, &view),
                    $"CreateShaderResourceView of node {i} (points {nodes[i].First} to {nodes[i].First + nodes[i].Count}, page {_pages.Count - 1} of {length} points from {first})"
                );
                _nodeViews[i] = (nint)view;
            }

            start = end;
        }

        if (next != points.Length)
        {
            throw new ArgumentException($"The nodes hold {next} points, the buffer {points.Length}.");
        }
    }

    /// <summary>
    /// Records one frame of the octree scene: the clears, then A's markers for each node chosen, one draw a node over the
    /// node's view, with a timestamp after the clears and after the draws, and sends the commands. Nothing waits for the GPU.
    /// </summary>
    public void DrawNodes(in FrameConstants constants, ReadOnlySpan<int> selected, ReadOnlySpan<OctreeNode> nodes, long frame)
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

        var buffer = _constants;
        var viewport = new D3D11_VIEWPORT
        {
            Width = _width,
            Height = _height,
            MaxDepth = 1,
        };
        var targetView = _targetView;
        context->RSSetViewports(1, &viewport);
        context->RSSetState(_rasterizer);
        context->IASetInputLayout(null);
        context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        context->VSSetConstantBuffers(0, 1, &buffer);
        context->PSSetConstantBuffers(0, 1, &buffer);
        context->VSSetShader(_markerVS, null, 0);
        context->PSSetShader(constants.Footprint != 0 ? _squarePS : _discPS, null, 0);
        context->OMSetBlendState(null, null, 0xFFFFFFFF);
        context->OMSetDepthStencilState(_depthLess, 0);
        context->OMSetRenderTargets(1, &targetView, _depthView);
        for (var i = 0; i < selected.Length; i++)
        {
            var node = selected[i];
            var view = (ID3D11ShaderResourceView*)_nodeViews[node];
            context->VSSetShaderResources(0, 1, &view);
            context->DrawInstanced(4, (uint)nodes[node].Count, 0, 0);
        }

        ID3D11ShaderResourceView* none = null;
        context->VSSetShaderResources(0, 1, &none);
        Stamp(set, 2);
        EndTimer(set, 3);
        context->OMSetRenderTargets(0, null, null);
        context->Flush();
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

        Release(ref _depthAlways);
        Release(ref _depthLess);
        Release(ref _rasterizer);
        Release(ref _resolvePS);
        Release(ref _resolveVS);
        Release(ref _indexCS);
        Release(ref _depthCS);
        Release(ref _squarePS);
        Release(ref _discPS);
        Release(ref _markerVS);
        Release(ref _nearestView);
        Release(ref _nearestAccess);
        Release(ref _nearest);
        ReleaseNodes();
        Release(ref _depthKeysView);
        Release(ref _depthKeysAccess);
        Release(ref _depthKeys);
        Release(ref _depthView);
        Release(ref _depth);
        Release(ref _staging);
        Release(ref _targetView);
        Release(ref _target);
        Release(ref _constants);
        Release(ref _pointView);
        Release(ref _points);
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

        description.Format = DXGI_FORMAT.DXGI_FORMAT_R32_UINT;
        description.BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE);
        MakeKeys(&description, out _depthKeys, out _depthKeysAccess, out _depthKeysView);
        MakeKeys(&description, out _nearest, out _nearestAccess, out _nearestView);
    }

    // An R32_UINT texture with a UAV and a view to read it; what was made is released when a later step fails.
    private void MakeKeys(
        D3D11_TEXTURE2D_DESC* description,
        out ID3D11Texture2D* texture,
        out ID3D11UnorderedAccessView* access,
        out ID3D11ShaderResourceView* view
    )
    {
        ID3D11Texture2D* made;
        Check(_device->CreateTexture2D(description, null, &made), "CreateTexture2D");
        ID3D11UnorderedAccessView* madeAccess = null;
        ID3D11ShaderResourceView* madeView = null;
        var result = _device->CreateUnorderedAccessView((ID3D11Resource*)made, null, &madeAccess);
        if (result.SUCCEEDED)
        {
            result = _device->CreateShaderResourceView((ID3D11Resource*)made, null, &madeView);
        }

        if (result.FAILED)
        {
            Release(ref madeAccess);
            Release(ref made);
        }

        Check(result, "CreateUnorderedAccessView");
        texture = made;
        access = madeAccess;
        view = madeView;
    }

    private void ReleaseNodes()
    {
        for (var i = 0; i < _nodeViews.Length; i++)
        {
            ReleaseNint(ref _nodeViews[i]);
        }

        _nodeViews = [];
        for (var i = 0; i < _pages.Count; i++)
        {
            var page = _pages[i];
            ReleaseNint(ref page);
        }

        _pages.Clear();
    }

    private void MakePoints(ReadOnlySpan<Point3> points)
    {
        if (!points.IsEmpty)
        {
            MakePointBuffer(points);
        }

        MakeConstants();
    }

    // An immutable structured buffer of the points, which a shader reads.
    private ID3D11Buffer* MakeStructuredBuffer(ReadOnlySpan<Point3> points)
    {
        var description = new D3D11_BUFFER_DESC
        {
            // In long: 1e8 points of 16 bytes pass int.MaxValue at 134,217,728 points.
            ByteWidth = (uint)((long)points.Length * sizeof(Point3)),
            Usage = D3D11_USAGE.D3D11_USAGE_IMMUTABLE,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
            MiscFlags = (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_BUFFER_STRUCTURED,
            StructureByteStride = (uint)sizeof(Point3),
        };
        ID3D11Buffer* buffer;

        // The span bounds the bytes the device copies: ByteWidth is its length.
        fixed (Point3* data = points)
        {
            var initial = new D3D11_SUBRESOURCE_DATA { pSysMem = data };
            Check(_device->CreateBuffer(&description, &initial, &buffer), "CreateBuffer");
        }

        return buffer;
    }

    private void MakePointBuffer(ReadOnlySpan<Point3> points)
    {
        _points = MakeStructuredBuffer(points);
        var view = new D3D11_SHADER_RESOURCE_VIEW_DESC
        {
            Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN,
            ViewDimension = D3D_SRV_DIMENSION.D3D11_SRV_DIMENSION_BUFFER,
        };
        view.Buffer.FirstElement = 0;
        view.Buffer.NumElements = (uint)points.Length;
        ID3D11ShaderResourceView* resourceView;
        Check(_device->CreateShaderResourceView((ID3D11Resource*)_points, &view, &resourceView), "CreateShaderResourceView");
        _pointView = resourceView;
    }

    private void MakeConstants()
    {
        ID3D11Buffer* buffer;
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
        fixed (byte* code = Shaders.MarkerVS)
        {
            ID3D11VertexShader* shader;
            Check(_device->CreateVertexShader(code, (nuint)Shaders.MarkerVS.Length, null, &shader), "CreateVertexShader");
            _markerVS = shader;
        }

        fixed (byte* code = Shaders.ResolveVS)
        {
            ID3D11VertexShader* shader;
            Check(_device->CreateVertexShader(code, (nuint)Shaders.ResolveVS.Length, null, &shader), "CreateVertexShader");
            _resolveVS = shader;
        }

        _discPS = MakePixelShader(Shaders.DiscPS);
        _squarePS = MakePixelShader(Shaders.SquarePS);
        _resolvePS = MakePixelShader(Shaders.ResolvePS);
        _depthCS = MakeComputeShader(Shaders.DepthCS);
        _indexCS = MakeComputeShader(Shaders.IndexCS);
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

    private ID3D11ComputeShader* MakeComputeShader(ReadOnlySpan<byte> bytes)
    {
        fixed (byte* code = bytes)
        {
            ID3D11ComputeShader* shader;
            Check(_device->CreateComputeShader(code, (nuint)bytes.Length, null, &shader), "CreateComputeShader");
            return shader;
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
        depth.DepthFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_ALWAYS;
        Check(_device->CreateDepthStencilState(&depth, &depthState), "CreateDepthStencilState");
        _depthAlways = depthState;
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
