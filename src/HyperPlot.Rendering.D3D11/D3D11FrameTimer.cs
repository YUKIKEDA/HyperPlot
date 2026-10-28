using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace HyperPlot.Rendering.D3D11;

/// <summary>
/// The GPU time of outermost frames (design §5.2, §5.3): a ring of 4 query sets, each a disjoint query and two timestamps.
/// A frame's set is read some frames later, without waiting; a frame finds the ring full and is not timed rather than wait.
/// </summary>
internal sealed unsafe class D3D11FrameTimer
{
    private const int Sets = 4;

    private readonly D3D11Backend _backend;
    private readonly QuerySet[] _sets = new QuerySet[Sets];
    private int _next;
    private int _current = -1;

    internal D3D11FrameTimer(D3D11Backend backend) => _backend = backend;

    /// <summary>Gets the time of the newest frame whose time is known.</summary>
    internal GpuFrameTime? Last { get; private set; }

    /// <summary>Gets or sets a test hook called with every time read, in the order read.</summary>
    internal Action<GpuFrameTime>? TestTimed { get; set; }

    /// <summary>Gets or sets a test hook that makes the queries of the frames it returns true for read as not ready yet.</summary>
    internal Func<long, bool>? TestHold { get; set; }

    /// <summary>Gets or sets a value indicating whether the tests make every set read as disjoint (WARP never is).</summary>
    internal bool TestDisjoint { get; set; }

    internal void Make()
    {
        var device = _backend.Device;
        for (var i = 0; i < Sets; i++)
        {
            _sets[i] = new QuerySet
            {
                Disjoint = MakeQuery(device, D3D11_QUERY.D3D11_QUERY_TIMESTAMP_DISJOINT),
                Start = MakeQuery(device, D3D11_QUERY.D3D11_QUERY_TIMESTAMP),
                End = MakeQuery(device, D3D11_QUERY.D3D11_QUERY_TIMESTAMP),
            };
        }

        _next = 0;
        _current = -1;
    }

    internal void Release()
    {
        for (var i = 0; i < Sets; i++)
        {
            ref var set = ref _sets[i];
            Release(ref set.Disjoint);
            Release(ref set.Start);
            Release(ref set.End);
            set.Pending = false;
        }

        _current = -1;
    }

    /// <summary>Starts timing an outermost frame, if a set is free; a frame that finds every set pending is not timed.</summary>
    internal void Begin(long frame)
    {
        ref var set = ref _sets[_next];
        if (set.Pending || set.Disjoint == null)
        {
            _current = -1;
            return;
        }

        _current = _next;
        _next = (_next + 1) % Sets;
        set.Frame = frame;
        var context = _backend.Context;
        context->Begin((ID3D11Asynchronous*)set.Disjoint);
        context->End((ID3D11Asynchronous*)set.Start);
    }

    /// <summary>
    /// Ends the timing of the outermost frame that closes, sends the commands (a headless frame has no Present to send
    /// them), and reads every set that is ready, oldest first, without waiting.
    /// </summary>
    internal void End()
    {
        var context = _backend.Context;
        if (_current >= 0)
        {
            ref var set = ref _sets[_current];
            context->End((ID3D11Asynchronous*)set.End);
            context->End((ID3D11Asynchronous*)set.Disjoint);
            set.Pending = true;
            _current = -1;
        }

        context->Flush();
        Poll();
    }

    private static ID3D11Query* MakeQuery(ID3D11Device* device, D3D11_QUERY kind)
    {
        var description = new D3D11_QUERY_DESC { Query = kind };
        ID3D11Query* query;
        var result = device->CreateQuery(&description, &query);
        return result.SUCCEEDED ? query : null;
    }

    private static void Release(ref ID3D11Query* query)
    {
        if (query != null)
        {
            _ = query->Release();
            query = null;
        }
    }

    private void Poll()
    {
        var context = _backend.Context;

        // The oldest pending set is the one after the newest: read in that order, and stop at the first not ready, so
        // that the times arrive in the order of the frames.
        for (var k = 1; k <= Sets; k++)
        {
            ref var set = ref _sets[(_next + k - 1) % Sets];
            if (!set.Pending)
            {
                continue;
            }

            D3D11_QUERY_DATA_TIMESTAMP_DISJOINT info;
            ulong start;
            ulong end;
            var flags = (uint)D3D11_ASYNC_GETDATA_FLAG.D3D11_ASYNC_GETDATA_DONOTFLUSH;
            if (
                !(
                    TestHold?.Invoke(set.Frame) != true
                    && context->GetData((ID3D11Asynchronous*)set.Disjoint, &info, (uint)sizeof(D3D11_QUERY_DATA_TIMESTAMP_DISJOINT), flags) == S.S_OK
                    && context->GetData((ID3D11Asynchronous*)set.Start, &start, sizeof(ulong), flags) == S.S_OK
                    && context->GetData((ID3D11Asynchronous*)set.End, &end, sizeof(ulong), flags) == S.S_OK
                )
            )
            {
                // Not ready yet: the newer sets wait for it, so that times arrive in the order of the frames.
                return;
            }

            set.Pending = false;
            if (!info.Disjoint && !TestDisjoint && info.Frequency > 0 && end >= start)
            {
                // The ticks of the GPU clock over its frequency in Hz (design §5.3).
                var seconds = (end - start) / (double)info.Frequency;
                Last = new GpuFrameTime(set.Frame, TimeSpan.FromSeconds(seconds));
                TestTimed?.Invoke(Last.Value);
            }
        }
    }

    private struct QuerySet
    {
        public ID3D11Query* Disjoint;
        public ID3D11Query* Start;
        public ID3D11Query* End;
        public long Frame;
        public bool Pending;
    }
}
