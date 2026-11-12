using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HyperPlot;

/// <summary>
/// The meter of Windows: cycle counts, which do not advance by the clock's tick (design §8.1 "読み方").
/// <c>GetSystemTimes</c> and <c>GetThreadTimes</c> advance by 15.6 ms: over 50 ms the machine's total read 469 to 1,031 ms
/// where 800 ms was right (the reference machine, Issue #422).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsCpuMeter : CpuMeter
{
    // THREAD_QUERY_LIMITED_INFORMATION: enough for QueryThreadCycleTime.
    private const uint QueryLimitedInformation = 0x0800;

    private readonly Lock _gate = new();

    // Each worker's own handle, 0 before it attached and after it detached. Read and closed under the lock.
    private readonly nint[] _threads;

    // One count for each logical processor of the process's processor group; filled by each sample.
    private readonly ulong[] _idle;

    // The cycles of one second: the greatest of the jobs' cycles over their wall time, 0 until a job was observed.
    private long _cyclesPerSecond;
    private ulong _lastIdle;
    private ulong _lastWorkers;
    private long _lastAt;
    private bool _hasLast;

    /// <summary>Initializes a new instance of the <see cref="WindowsCpuMeter"/> class.</summary>
    /// <param name="workers">The number of the pool's workers.</param>
    public WindowsCpuMeter(int workers)
    {
        _threads = new nint[workers];

        // With no buffer, the call gives the bytes it needs: 8 for each logical processor.
        uint bytes = 0;
        _ = QueryIdleProcessorCycleTimeSize(ref bytes, 0);
        _idle = new ulong[bytes / sizeof(ulong)];
    }

    /// <inheritdoc/>
    public override void Attach(int worker)
    {
        var handle = OpenThread(QueryLimitedInformation, 0, GetCurrentThreadId());
        lock (_gate)
        {
            _threads[worker] = handle;
        }
    }

    /// <inheritdoc/>
    public override void Detach(int worker)
    {
        lock (_gate)
        {
            if (_threads[worker] != 0)
            {
                _ = CloseHandle(_threads[worker]);
                _threads[worker] = 0;
            }
        }
    }

    /// <inheritdoc/>
    public override long ThreadTime() => QueryThreadCycleTime(GetCurrentThread(), out var cycles) != 0 ? (long)cycles : 0;

    /// <inheritdoc/>
    public override void Observe(long units, long ticks)
    {
        // A thread cannot use more cycles than the wall time holds, so the greatest ratio is the rate of a job that
        // was not preempted. A job shorter than 0.1 ms is left out: the timestamp's step would be 0.1 % of it or more.
        if (units <= 0 || ticks < TimeProvider.System.TimestampFrequency / 10_000)
        {
            return;
        }

        var rate = (long)(units * (double)TimeProvider.System.TimestampFrequency / ticks);
        var seen = Volatile.Read(ref _cyclesPerSecond);
        while (rate > seen)
        {
            var before = Interlocked.CompareExchange(ref _cyclesPerSecond, rate, seen);
            if (before == seen)
            {
                break;
            }

            seen = before;
        }
    }

    /// <inheritdoc/>
    public override long ToTicks(long units)
    {
        var rate = Volatile.Read(ref _cyclesPerSecond);
        return rate == 0 ? 0 : (long)(units * (double)TimeProvider.System.TimestampFrequency / rate);
    }

    /// <inheritdoc/>
    public override bool TrySample(long now, out CpuSample sample)
    {
        sample = default;
        lock (_gate)
        {
            var bytes = (uint)(_idle.Length * sizeof(ulong));
            if (_idle.Length == 0 || QueryIdleProcessorCycleTime(ref bytes, ref _idle[0]) == 0)
            {
                _hasLast = false;
                return false;
            }

            ulong idle = 0;
            foreach (var cycles in _idle)
            {
                idle += cycles;
            }

            ulong workers = 0;
            foreach (var thread in _threads)
            {
                if (thread == 0 || QueryThreadCycleTime(thread, out var cycles) == 0)
                {
                    _hasLast = false;
                    return false;
                }

                workers += cycles;
            }

            var hadLast = _hasLast;
            var idleCycles = idle - _lastIdle;
            var workerCycles = workers - _lastWorkers;
            var ticks = now - _lastAt;
            _lastIdle = idle;
            _lastWorkers = workers;
            _lastAt = now;
            _hasLast = true;

            var rate = Volatile.Read(ref _cyclesPerSecond);
            if (!hadLast || rate == 0 || ticks <= 0)
            {
                return false;
            }

            // The cycles one core has in the interval.
            var core = rate * ((double)ticks / TimeProvider.System.TimestampFrequency);
            sample = new CpuSample(_idle.Length, Math.Min(_idle.Length, idleCycles / core), workerCycles / core);
            return true;
        }
    }

    // BOOL QueryIdleProcessorCycleTime(PULONG BufferLength, PULONG64 ProcessorIdleCycleTime): the idle cycles of each
    // logical processor of the calling thread's processor group; BufferLength is in bytes, in and out.
    [LibraryImport("kernel32", EntryPoint = "QueryIdleProcessorCycleTime")]
    private static partial int QueryIdleProcessorCycleTime(ref uint bufferLength, ref ulong processorIdleCycleTime);

    // The same function with no buffer: it writes the bytes it needs to BufferLength.
    [LibraryImport("kernel32", EntryPoint = "QueryIdleProcessorCycleTime")]
    private static partial int QueryIdleProcessorCycleTimeSize(ref uint bufferLength, nint processorIdleCycleTime);

    // BOOL QueryThreadCycleTime(HANDLE ThreadHandle, PULONG64 CycleTime).
    [LibraryImport("kernel32", EntryPoint = "QueryThreadCycleTime")]
    private static partial int QueryThreadCycleTime(nint thread, out ulong cycleTime);

    // HANDLE GetCurrentThread(): a pseudo handle of the calling thread, which is not closed.
    [LibraryImport("kernel32", EntryPoint = "GetCurrentThread")]
    private static partial nint GetCurrentThread();

    // DWORD GetCurrentThreadId().
    [LibraryImport("kernel32", EntryPoint = "GetCurrentThreadId")]
    private static partial uint GetCurrentThreadId();

    // HANDLE OpenThread(DWORD dwDesiredAccess, BOOL bInheritHandle, DWORD dwThreadId): NULL on failure.
    [LibraryImport("kernel32", EntryPoint = "OpenThread")]
    private static partial nint OpenThread(uint desiredAccess, int inheritHandle, uint threadId);

    // BOOL CloseHandle(HANDLE hObject).
    [LibraryImport("kernel32", EntryPoint = "CloseHandle")]
    private static partial int CloseHandle(nint handle);
}
