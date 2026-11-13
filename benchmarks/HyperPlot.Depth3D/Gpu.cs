using System.Numerics;
using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace HyperPlot.Depth3D;

/// <summary>How a polyline composites with depth (Depth3D.hlsl lists what each does).</summary>
internal enum LineMethod
{
    None,
    Test,
    Prepass,
    Postpass,
    Capsules,
    Uav,
    UavFragment,
}

/// <summary>How a marker writes depth (Depth3D.hlsl lists what each does).</summary>
internal enum MarkerMethod
{
    None,
    Quad,
    Touched,
    Half,
}

/// <summary>Whether the surface is drawn, and before or after the lines or markers.</summary>
internal enum PlaneOrder
{
    None,
    Before,
    After,
}

/// <summary>The buckets of a frame's GPU time, in milliseconds.</summary>
internal enum Bucket
{
    Clear,
    Plane,
    Shapes,
    SegmentPasses,
    BoundsPasses,
    MarkerPasses,
    Total,
}

/// <summary>Takes the GPU times of a frame, by <see cref="Bucket"/>; a bucket the frame did not time is NaN.</summary>
internal delegate void TimedHandler(long frame, ReadOnlySpan<double> buckets);

/// <summary>What a frame draws, besides the method.</summary>
internal struct DrawSettings
{
    public Matrix4x4 Transform;
    public float HalfWidth;
    public float Radius;
    public float Threshold;
    public PlaneOrder Plane;

    /// <summary>A timestamp after every pass, not only around the shapes (the stamps can cost GPU time of their own).</summary>
    public bool PassTimes;
}

/// <summary>The <c>Frame</c> constant buffer of Depth3D.hlsl, 128 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameConstants
{
    public Matrix4x4 Transform;
    public Vector2 Size;
    public Vector2 InverseSize;
    public float HalfWidth;
    public float Radius;
    public float Threshold;
    public uint SegmentStart;
    public Vector4 Color;
    public Vector4 Bounds;
}

/// <summary>
/// SPIKE (row K1, Issue #509): a D3D11 device of its own, the scene's vertices and segments in structured buffers, a
/// 1,920 × 1,080 BGRA8 target with a D32_FLOAT depth buffer, the R8 coverage texture of the 2D line pipeline, an R32_UINT
/// texture for the least depth of a polyline, the shaders of every method, and GPU timestamps. Not product code: no
/// device-loss handling, every failure throws.
/// </summary>
internal sealed unsafe class Gpu : IDisposable
{
    /// <summary>The timestamps a frame can take: the start, the clears, the plane, 4 passes a polyline, the plane after.</summary>
    public const int MaxStamps = 4 + (4 * MaxTimedPolylines) + 4;

    /// <summary>The polylines a frame with a timestamp after each pass can hold.</summary>
    public const int MaxTimedPolylines = 64;

    private const int Sets = 8;
    private const int KindShapes = (int)Bucket.Shapes;
    private const int KindSegment = (int)Bucket.SegmentPasses;
    private const int KindBounds = (int)Bucket.BoundsPasses;
    private const int KindMarker = (int)Bucket.MarkerPasses;

    private readonly int _width;
    private readonly int _height;
    private readonly SceneData _scene;
    private readonly nint[] _disjoint = new nint[Sets];
    private readonly nint[] _stamps = new nint[Sets * MaxStamps];
    private readonly int[] _stampKinds = new int[Sets * MaxStamps];
    private readonly long[] _setFrame = new long[Sets];
    private readonly int[] _setStamps = new int[Sets];
    private readonly bool[] _pending = new bool[Sets];
    private int _nextSet;
    private int _set = -1;
    private int _stamp;

    private ID3D11Device* _device;
    private ID3D11DeviceContext* _context;
    private ID3D11Buffer* _vertices;
    private ID3D11ShaderResourceView* _vertexView;
    private ID3D11Buffer* _segments;
    private ID3D11ShaderResourceView* _segmentView;
    private ID3D11Buffer* _constants;
    private ID3D11Texture2D* _target;
    private ID3D11RenderTargetView* _targetView;
    private ID3D11Texture2D* _staging;
    private ID3D11Texture2D* _depth;
    private ID3D11DepthStencilView* _depthView;
    private ID3D11Texture2D* _coverage;
    private ID3D11RenderTargetView* _coverageTarget;
    private ID3D11ShaderResourceView* _coverageView;
    private ID3D11Texture2D* _lineDepth;
    private ID3D11UnorderedAccessView* _lineDepthAccess;
    private ID3D11ShaderResourceView* _lineDepthView;

    private ID3D11VertexShader* _planeVS;
    private ID3D11PixelShader* _planePS;
    private ID3D11VertexShader* _segmentVS;
    private ID3D11PixelShader* _coveragePS;
    private ID3D11PixelShader* _coverageDepthPS;
    private ID3D11PixelShader* _depthOnlyPS;
    private ID3D11PixelShader* _capsulePS;
    private ID3D11PixelShader* _uavCoveragePS;
    private ID3D11VertexShader* _boundsVS;
    private ID3D11PixelShader* _colorPS;
    private ID3D11PixelShader* _zeroPS;
    private ID3D11PixelShader* _uavColorPS;
    private ID3D11PixelShader* _uavDepthPS;
    private ID3D11PixelShader* _uavResetPS;
    private ID3D11VertexShader* _markerVS;
    private ID3D11PixelShader* _markerPS;
    private ID3D11PixelShader* _markerTouchedPS;
    private ID3D11PixelShader* _markerCorePS;
    private ID3D11PixelShader* _markerFringePS;

    private ID3D11RasterizerState* _rasterizer;
    private ID3D11BlendState* _maxBlend;
    private ID3D11BlendState* _overBlend;
    private ID3D11BlendState* _writeBlend;
    private ID3D11DepthStencilState* _depthOff;
    private ID3D11DepthStencilState* _testLess;
    private ID3D11DepthStencilState* _testLessEqual;
    private ID3D11DepthStencilState* _writeLess;
    private ID3D11DepthStencilState* _writeLessEqual;

    public Gpu(bool warp, int width, int height, SceneData scene)
    {
        (_width, _height, _scene) = (width, height, scene);
        try
        {
            MakeDevice(warp);
            IsWarp = warp;
            MakeTargets();
            MakeBuffers();
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

    /// <summary>Records one frame of polylines and sends it. Nothing waits for the GPU.</summary>
    public void DrawLines(LineMethod method, in DrawSettings settings, long frame)
    {
        Begin(settings, frame);
        if (method == LineMethod.Capsules)
        {
            // One pass for all polylines: the color of each segment comes from its first vertex. LESS: of equal depths the
            // first segment keeps the pixel, as the CPU reference does.
            WriteConstants(settings, 0, Vector4.Zero, Vector4.Zero);
            SegmentPass(_capsulePS, _targetView, null, _overBlend, _writeLess, (uint)_scene.Segments.Length, settings);
        }
        else
        {
            foreach (var polyline in _scene.Polylines)
            {
                if (!Bounds(polyline, settings, out var bounds))
                {
                    continue;
                }

                var color = Unpack(polyline.Color);
                WriteConstants(settings, (uint)polyline.SegmentStart, color, bounds);
                DrawPolyline(method, (uint)polyline.SegmentCount, settings);
            }
        }

        End(settings);
    }

    /// <summary>Records one frame of markers and sends it. Nothing waits for the GPU.</summary>
    public void DrawMarkers(MarkerMethod method, in DrawSettings settings, long frame)
    {
        Begin(settings, frame);
        WriteConstants(settings, 0, Vector4.Zero, Vector4.Zero);
        var count = (uint)_scene.Vertices.Length;
        switch (method)
        {
            case MarkerMethod.None:
                MarkerPass(_markerPS, _depthOff, count, settings);
                break;
            case MarkerMethod.Quad:
                MarkerPass(_markerPS, _writeLess, count, settings);
                break;
            case MarkerMethod.Touched:
                MarkerPass(_markerTouchedPS, _writeLess, count, settings);
                break;
            default:
                MarkerPass(_markerCorePS, _writeLess, count, settings);
                MarkerPass(_markerFringePS, _testLess, count, settings);
                break;
        }

        End(settings);
    }

    /// <summary>
    /// Reads every timed frame that is done, oldest first, without waiting, or waiting for all when asked; calls back with
    /// the frame and its buckets.
    /// </summary>
    public void Poll(TimedHandler timed, bool wait)
    {
        Span<double> buckets = stackalloc double[(int)Bucket.Total + 1];
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

            buckets.Clear();
            var timedPasses = false;
            var count = _setStamps[set];
            for (var s = 1; s < count; s++)
            {
                var milliseconds = (stamps[s] - stamps[s - 1]) * 1000.0 / info.Frequency;
                var kind = _stampKinds[(set * MaxStamps) + s];
                buckets[kind] += milliseconds;
                if (kind is KindSegment or KindBounds or KindMarker)
                {
                    buckets[KindShapes] += milliseconds;
                    timedPasses = true;
                }
            }

            if (!timedPasses)
            {
                buckets[KindSegment] = double.NaN;
                buckets[KindBounds] = double.NaN;
                buckets[KindMarker] = double.NaN;
            }

            buckets[(int)Bucket.Total] = (stamps[count - 1] - stamps[0]) * 1000.0 / info.Frequency;
            timed(_setFrame[set], buckets);
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

        Release(ref _writeLessEqual);
        Release(ref _writeLess);
        Release(ref _testLessEqual);
        Release(ref _testLess);
        Release(ref _depthOff);
        Release(ref _writeBlend);
        Release(ref _overBlend);
        Release(ref _maxBlend);
        Release(ref _rasterizer);
        Release(ref _markerFringePS);
        Release(ref _markerCorePS);
        Release(ref _markerTouchedPS);
        Release(ref _markerPS);
        Release(ref _markerVS);
        Release(ref _uavResetPS);
        Release(ref _uavDepthPS);
        Release(ref _uavColorPS);
        Release(ref _zeroPS);
        Release(ref _colorPS);
        Release(ref _boundsVS);
        Release(ref _uavCoveragePS);
        Release(ref _capsulePS);
        Release(ref _depthOnlyPS);
        Release(ref _coverageDepthPS);
        Release(ref _coveragePS);
        Release(ref _segmentVS);
        Release(ref _planePS);
        Release(ref _planeVS);
        Release(ref _lineDepthView);
        Release(ref _lineDepthAccess);
        Release(ref _lineDepth);
        Release(ref _coverageView);
        Release(ref _coverageTarget);
        Release(ref _coverage);
        Release(ref _depthView);
        Release(ref _depth);
        Release(ref _staging);
        Release(ref _targetView);
        Release(ref _target);
        Release(ref _constants);
        Release(ref _segmentView);
        Release(ref _segments);
        Release(ref _vertexView);
        Release(ref _vertices);
        if (_context != null)
        {
            _context->ClearState();
            Release(ref _context);
        }

        Release(ref _device);
    }

    private static Vector4 Unpack(uint color) => new Vector4(color & 0xFF, (color >> 8) & 0xFF, (color >> 16) & 0xFF, color >> 24) * (1f / 255);

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

    /// <summary>The passes of one polyline; the constants hold its segments, color, and box.</summary>
    private void DrawPolyline(LineMethod method, uint segments, in DrawSettings settings)
    {
        switch (method)
        {
            case LineMethod.None:
                SegmentPass(_coveragePS, _coverageTarget, null, _maxBlend, _depthOff, segments, settings);
                ColorAndZero(settings);
                break;
            case LineMethod.Test:
                SegmentPass(_coverageDepthPS, _coverageTarget, null, _maxBlend, _testLessEqual, segments, settings);
                ColorAndZero(settings);
                break;
            case LineMethod.Prepass:
                SegmentPass(_depthOnlyPS, null, null, _writeBlend, _writeLessEqual, segments, settings);
                SegmentPass(_coverageDepthPS, _coverageTarget, null, _maxBlend, _testLessEqual, segments, settings);
                ColorAndZero(settings);
                break;
            case LineMethod.Postpass:
                SegmentPass(_coverageDepthPS, _coverageTarget, null, _maxBlend, _testLessEqual, segments, settings);
                ColorAndZero(settings);
                SegmentPass(_depthOnlyPS, null, null, _writeBlend, _writeLessEqual, segments, settings);
                break;
            case LineMethod.Uav:
                SegmentPass(_uavCoveragePS, _coverageTarget, _lineDepthAccess, _maxBlend, _depthOff, segments, settings);
                BoundsPass(_uavColorPS, _targetView, null, _overBlend, _testLessEqual, read: true, settings);
                BoundsPass(_uavDepthPS, null, null, _writeBlend, _writeLessEqual, read: true, settings);
                BoundsPass(_uavResetPS, _coverageTarget, _lineDepthAccess, _writeBlend, _depthOff, read: false, settings);
                break;
            default:
                SegmentPass(_uavCoveragePS, _coverageTarget, _lineDepthAccess, _maxBlend, _testLessEqual, segments, settings);
                BoundsPass(_uavColorPS, _targetView, null, _overBlend, _depthOff, read: true, settings);
                BoundsPass(_uavDepthPS, null, null, _writeBlend, _writeLessEqual, read: true, settings);
                BoundsPass(_uavResetPS, _coverageTarget, _lineDepthAccess, _writeBlend, _depthOff, read: false, settings);
                break;
        }
    }

    // The 2D pipeline's passes 2 and 3: the color once over the target, then 0 back into the coverage.
    private void ColorAndZero(in DrawSettings settings)
    {
        BoundsPass(_colorPS, _targetView, null, _overBlend, _depthOff, read: true, settings);
        BoundsPass(_zeroPS, _coverageTarget, null, _writeBlend, _depthOff, read: false, settings);
    }

    /// <summary>
    /// The polyline's box on the target: the corners of its vertices' 3D box projected, widened by half the width and 1
    /// pixel (where its segment quads reach), whole pixels, inside the target; false when nothing is left.
    /// </summary>
    private bool Bounds(in Polyline polyline, in DrawSettings settings, out Vector4 bounds)
    {
        var (left, top, right, bottom) = (float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
        for (var corner = 0; corner < 8; corner++)
        {
            var position = new Vector3(
                (corner & 1) != 0 ? polyline.Max.X : polyline.Min.X,
                (corner & 2) != 0 ? polyline.Max.Y : polyline.Min.Y,
                (corner & 4) != 0 ? polyline.Max.Z : polyline.Min.Z
            );
            var clip = Vector4.Transform(new Vector4(position, 1), settings.Transform);
            var x = ((clip.X / clip.W * 0.5f) + 0.5f) * _width;
            var y = (0.5f - (clip.Y / clip.W * 0.5f)) * _height;
            (left, right) = (MathF.Min(left, x), MathF.Max(right, x));
            (top, bottom) = (MathF.Min(top, y), MathF.Max(bottom, y));
        }

        var reach = settings.HalfWidth + 1;
        bounds = new Vector4(
            MathF.Max(MathF.Floor(left - reach), 0),
            MathF.Max(MathF.Floor(top - reach), 0),
            MathF.Min(MathF.Ceiling(right + reach), _width),
            MathF.Min(MathF.Ceiling(bottom + reach), _height)
        );
        return bounds.X < bounds.Z && bounds.Y < bounds.W;
    }

    private void Begin(in DrawSettings settings, long frame)
    {
        BeginTimer(frame);
        var context = _context;
        var white = stackalloc float[] { 1, 1, 1, 1 };
        context->ClearRenderTargetView(_targetView, white);
        context->ClearDepthStencilView(_depthView, (uint)D3D11_CLEAR_FLAG.D3D11_CLEAR_DEPTH, 1, 0);
        Stamp((int)Bucket.Clear);

        var viewport = new D3D11_VIEWPORT
        {
            Width = _width,
            Height = _height,
            MaxDepth = 1,
        };
        context->RSSetViewports(1, &viewport);
        context->RSSetState(_rasterizer);
        context->IASetInputLayout(null);
        var buffer = _constants;
        context->VSSetConstantBuffers(0, 1, &buffer);
        context->PSSetConstantBuffers(0, 1, &buffer);
        var views = stackalloc ID3D11ShaderResourceView*[] { _vertexView, _segmentView };
        context->VSSetShaderResources(0, 2, views);
        if (settings.Plane == PlaneOrder.Before)
        {
            WriteConstants(settings, 0, Vector4.Zero, Vector4.Zero);
            Plane();
        }

        Stamp((int)Bucket.Plane);
    }

    private void End(in DrawSettings settings)
    {
        if (!settings.PassTimes)
        {
            Stamp(KindShapes);
        }

        if (settings.Plane == PlaneOrder.After)
        {
            WriteConstants(settings, 0, Vector4.Zero, Vector4.Zero);
            Plane();
        }

        Stamp((int)Bucket.Plane);
        EndTimer();
        UnbindPixelResources();
        _context->OMSetRenderTargets(0, null, null);
        _context->Flush();
    }

    private void Plane()
    {
        var context = _context;
        UnbindPixelResources();
        BindOutput(_targetView, null);
        context->OMSetBlendState(_writeBlend, null, 0xFFFFFFFF);
        context->OMSetDepthStencilState(_writeLess, 0);
        context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->VSSetShader(_planeVS, null, 0);
        context->PSSetShader(_planePS, null, 0);
        context->Draw(6, 0);
    }

    private void SegmentPass(
        ID3D11PixelShader* shader,
        ID3D11RenderTargetView* target,
        ID3D11UnorderedAccessView* access,
        ID3D11BlendState* blend,
        ID3D11DepthStencilState* depth,
        uint segments,
        in DrawSettings settings
    )
    {
        var context = _context;
        UnbindPixelResources();
        BindOutput(target, access);
        context->OMSetBlendState(blend, null, 0xFFFFFFFF);
        context->OMSetDepthStencilState(depth, 0);
        context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        context->VSSetShader(_segmentVS, null, 0);
        context->PSSetShader(shader, null, 0);
        context->DrawInstanced(4, segments, 0, 0);
        if (settings.PassTimes)
        {
            Stamp(KindSegment);
        }
    }

    private void BoundsPass(
        ID3D11PixelShader* shader,
        ID3D11RenderTargetView* target,
        ID3D11UnorderedAccessView* access,
        ID3D11BlendState* blend,
        ID3D11DepthStencilState* depth,
        bool read,
        in DrawSettings settings
    )
    {
        var context = _context;
        UnbindPixelResources();
        BindOutput(target, access);
        if (read)
        {
            // The coverage and the least depth leave the output above before they are read here.
            var views = stackalloc ID3D11ShaderResourceView*[] { _coverageView, _lineDepthView };
            context->PSSetShaderResources(2, 2, views);
        }

        context->OMSetBlendState(blend, null, 0xFFFFFFFF);
        context->OMSetDepthStencilState(depth, 0);
        context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        context->VSSetShader(_boundsVS, null, 0);
        context->PSSetShader(shader, null, 0);
        context->Draw(4, 0);
        if (settings.PassTimes)
        {
            Stamp(KindBounds);
        }
    }

    private void MarkerPass(ID3D11PixelShader* shader, ID3D11DepthStencilState* depth, uint count, in DrawSettings settings)
    {
        var context = _context;
        UnbindPixelResources();
        BindOutput(_targetView, null);
        context->OMSetBlendState(_overBlend, null, 0xFFFFFFFF);
        context->OMSetDepthStencilState(depth, 0);
        context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        context->VSSetShader(_markerVS, null, 0);
        context->PSSetShader(shader, null, 0);
        context->DrawInstanced(4, count, 0, 0);
        if (settings.PassTimes)
        {
            Stamp(KindMarker);
        }
    }

    // One render target (or none) and the depth buffer, and the least depth's unordered view in slot 1 (or none): every pass
    // binds both, so that no texture stays bound as an output where the next pass reads it.
    private void BindOutput(ID3D11RenderTargetView* target, ID3D11UnorderedAccessView* access)
    {
        var targets = stackalloc ID3D11RenderTargetView*[] { target };
        var accesses = stackalloc ID3D11UnorderedAccessView*[] { access };
        _context->OMSetRenderTargetsAndUnorderedAccessViews(target != null ? 1u : 0u, targets, _depthView, 1, 1, accesses, null);
    }

    private void UnbindPixelResources()
    {
        var none = stackalloc ID3D11ShaderResourceView*[] { null, null };
        _context->PSSetShaderResources(2, 2, none);
    }

    private void WriteConstants(in DrawSettings settings, uint segmentStart, Vector4 color, Vector4 bounds)
    {
        D3D11_MAPPED_SUBRESOURCE mapped;
        Check(_context->Map((ID3D11Resource*)_constants, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map");
        *(FrameConstants*)mapped.pData = new FrameConstants
        {
            Transform = settings.Transform,
            Size = new Vector2(_width, _height),
            InverseSize = new Vector2(1f / _width, 1f / _height),
            HalfWidth = settings.HalfWidth,
            Radius = settings.Radius,
            Threshold = settings.Threshold,
            SegmentStart = segmentStart,
            Color = color,
            Bounds = bounds,
        };
        _context->Unmap((ID3D11Resource*)_constants, 0);
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

        // The coverage of the 2D line pipeline: R8, a render target and a shader resource, 0 everywhere between polylines.
        description.Format = DXGI_FORMAT.DXGI_FORMAT_R8_UNORM;
        description.BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE);
        Check(_device->CreateTexture2D(&description, null, &texture), "CreateTexture2D");
        _coverage = texture;
        ID3D11RenderTargetView* coverageTarget;
        Check(_device->CreateRenderTargetView((ID3D11Resource*)_coverage, null, &coverageTarget), "CreateRenderTargetView");
        _coverageTarget = coverageTarget;
        ID3D11ShaderResourceView* coverageView;
        Check(_device->CreateShaderResourceView((ID3D11Resource*)_coverage, null, &coverageView), "CreateShaderResourceView");
        _coverageView = coverageView;
        var zero = stackalloc float[] { 0, 0, 0, 0 };
        _context->ClearRenderTargetView(_coverageTarget, zero);

        // The least depth of a polyline: R32_UINT, an unordered view and a shader resource, 0xFFFFFFFF between polylines.
        description.Format = DXGI_FORMAT.DXGI_FORMAT_R32_UINT;
        description.BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE);
        Check(_device->CreateTexture2D(&description, null, &texture), "CreateTexture2D");
        _lineDepth = texture;
        ID3D11UnorderedAccessView* lineDepthAccess;
        Check(_device->CreateUnorderedAccessView((ID3D11Resource*)_lineDepth, null, &lineDepthAccess), "CreateUnorderedAccessView");
        _lineDepthAccess = lineDepthAccess;
        ID3D11ShaderResourceView* lineDepthView;
        Check(_device->CreateShaderResourceView((ID3D11Resource*)_lineDepth, null, &lineDepthView), "CreateShaderResourceView");
        _lineDepthView = lineDepthView;
        var empty = stackalloc uint[] { 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF };
        _context->ClearUnorderedAccessViewUint(_lineDepthAccess, empty);
    }

    private void MakeBuffers()
    {
        MakeStructured<Vertex3>(_scene.Vertices, out _vertices, out _vertexView);

        // Markers have no segments, and D3D11 makes no buffer of 0 bytes: the segments' slot stays empty.
        if (_scene.Segments.Length > 0)
        {
            MakeStructured<uint>(_scene.Segments, out _segments, out _segmentView);
        }

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

    // An immutable structured buffer of the records and a view of it; the buffer is released when the view fails.
    private void MakeStructured<T>(ReadOnlySpan<T> records, out ID3D11Buffer* made, out ID3D11ShaderResourceView* madeView)
        where T : unmanaged
    {
        var description = new D3D11_BUFFER_DESC
        {
            ByteWidth = (uint)(records.Length * sizeof(T)),
            Usage = D3D11_USAGE.D3D11_USAGE_IMMUTABLE,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
            MiscFlags = (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_BUFFER_STRUCTURED,
            StructureByteStride = (uint)sizeof(T),
        };
        ID3D11Buffer* buffer;

        // The span bounds the bytes the device copies: ByteWidth is its length.
        fixed (T* data = records)
        {
            var initial = new D3D11_SUBRESOURCE_DATA { pSysMem = data };
            Check(_device->CreateBuffer(&description, &initial, &buffer), "CreateBuffer");
        }

        var view = new D3D11_SHADER_RESOURCE_VIEW_DESC
        {
            Format = DXGI_FORMAT.DXGI_FORMAT_UNKNOWN,
            ViewDimension = D3D_SRV_DIMENSION.D3D11_SRV_DIMENSION_BUFFER,
        };
        view.Buffer.FirstElement = 0;
        view.Buffer.NumElements = (uint)records.Length;
        ID3D11ShaderResourceView* resourceView;
        var result = _device->CreateShaderResourceView((ID3D11Resource*)buffer, &view, &resourceView);
        if (result.FAILED)
        {
            Release(ref buffer);
        }

        Check(result, "CreateShaderResourceView");
        made = buffer;
        madeView = resourceView;
    }

    private void MakeShaders()
    {
        _planeVS = MakeVertexShader(Shaders.PlaneVS);
        _segmentVS = MakeVertexShader(Shaders.SegmentVS);
        _boundsVS = MakeVertexShader(Shaders.BoundsVS);
        _markerVS = MakeVertexShader(Shaders.MarkerVS);
        _planePS = MakePixelShader(Shaders.PlanePS);
        _coveragePS = MakePixelShader(Shaders.CoveragePS);
        _coverageDepthPS = MakePixelShader(Shaders.CoverageDepthPS);
        _depthOnlyPS = MakePixelShader(Shaders.DepthOnlyPS);
        _capsulePS = MakePixelShader(Shaders.CapsulePS);
        _uavCoveragePS = MakePixelShader(Shaders.UavCoveragePS);
        _colorPS = MakePixelShader(Shaders.ColorPS);
        _zeroPS = MakePixelShader(Shaders.ZeroPS);
        _uavColorPS = MakePixelShader(Shaders.UavColorPS);
        _uavDepthPS = MakePixelShader(Shaders.UavDepthPS);
        _uavResetPS = MakePixelShader(Shaders.UavResetPS);
        _markerPS = MakePixelShader(Shaders.MarkerPS);
        _markerTouchedPS = MakePixelShader(Shaders.MarkerTouchedPS);
        _markerCorePS = MakePixelShader(Shaders.MarkerCorePS);
        _markerFringePS = MakePixelShader(Shaders.MarkerFringePS);
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
        var rasterizer = new D3D11_RASTERIZER_DESC
        {
            FillMode = D3D11_FILL_MODE.D3D11_FILL_SOLID,
            CullMode = D3D11_CULL_MODE.D3D11_CULL_NONE,
            DepthClipEnable = BOOL.TRUE,
        };
        ID3D11RasterizerState* state;
        Check(_device->CreateRasterizerState(&rasterizer, &state), "CreateRasterizerState");
        _rasterizer = state;

        _maxBlend = MakeBlend(D3D11_BLEND.D3D11_BLEND_ONE, D3D11_BLEND.D3D11_BLEND_ONE, D3D11_BLEND_OP.D3D11_BLEND_OP_MAX, enable: true);
        _overBlend = MakeBlend(D3D11_BLEND.D3D11_BLEND_ONE, D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA, D3D11_BLEND_OP.D3D11_BLEND_OP_ADD, enable: true);
        _writeBlend = MakeBlend(D3D11_BLEND.D3D11_BLEND_ONE, D3D11_BLEND.D3D11_BLEND_ZERO, D3D11_BLEND_OP.D3D11_BLEND_OP_ADD, enable: false);

        _depthOff = MakeDepth(enable: false, write: false, D3D11_COMPARISON_FUNC.D3D11_COMPARISON_ALWAYS);
        _testLess = MakeDepth(enable: true, write: false, D3D11_COMPARISON_FUNC.D3D11_COMPARISON_LESS);
        _testLessEqual = MakeDepth(enable: true, write: false, D3D11_COMPARISON_FUNC.D3D11_COMPARISON_LESS_EQUAL);
        _writeLess = MakeDepth(enable: true, write: true, D3D11_COMPARISON_FUNC.D3D11_COMPARISON_LESS);
        _writeLessEqual = MakeDepth(enable: true, write: true, D3D11_COMPARISON_FUNC.D3D11_COMPARISON_LESS_EQUAL);
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
        Check(_device->CreateBlendState(&description, &state), "CreateBlendState");
        return state;
    }

    private ID3D11DepthStencilState* MakeDepth(bool enable, bool write, D3D11_COMPARISON_FUNC comparison)
    {
        var description = new D3D11_DEPTH_STENCIL_DESC
        {
            DepthEnable = enable ? BOOL.TRUE : BOOL.FALSE,
            DepthWriteMask = write ? D3D11_DEPTH_WRITE_MASK.D3D11_DEPTH_WRITE_MASK_ALL : D3D11_DEPTH_WRITE_MASK.D3D11_DEPTH_WRITE_MASK_ZERO,
            DepthFunc = comparison,
        };

        // The stencil is off; its operations still have to be valid values.
        var keep = new D3D11_DEPTH_STENCILOP_DESC
        {
            StencilFailOp = D3D11_STENCIL_OP.D3D11_STENCIL_OP_KEEP,
            StencilDepthFailOp = D3D11_STENCIL_OP.D3D11_STENCIL_OP_KEEP,
            StencilPassOp = D3D11_STENCIL_OP.D3D11_STENCIL_OP_KEEP,
            StencilFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_ALWAYS,
        };
        description.FrontFace = keep;
        description.BackFace = keep;
        description.StencilReadMask = 0xFF;
        description.StencilWriteMask = 0xFF;
        ID3D11DepthStencilState* state;
        Check(_device->CreateDepthStencilState(&description, &state), "CreateDepthStencilState");
        return state;
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

    /// <summary>Starts a timed frame on the next set; a frame whose set is still pending is not timed.</summary>
    private void BeginTimer(long frame)
    {
        var set = _nextSet;
        if (_pending[set])
        {
            _set = -1;
            return;
        }

        _nextSet = (_nextSet + 1) % Sets;
        _setFrame[set] = frame;
        _set = set;
        _stamp = 0;
        _context->Begin((ID3D11Asynchronous*)_disjoint[set]);
        Stamp(KindShapes);
    }

    // A timestamp; the time since the one before goes to the bucket of the kind. A frame with more passes than the set holds
    // is not timed past them.
    private void Stamp(int kind)
    {
        if (_set < 0 || _stamp >= MaxStamps)
        {
            return;
        }

        _stampKinds[(_set * MaxStamps) + _stamp] = kind;
        _context->End((ID3D11Asynchronous*)_stamps[(_set * MaxStamps) + _stamp]);
        _stamp++;
    }

    private void EndTimer()
    {
        if (_set >= 0)
        {
            _context->End((ID3D11Asynchronous*)_disjoint[_set]);
            _setStamps[_set] = _stamp;
            _pending[_set] = true;
            _set = -1;
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
