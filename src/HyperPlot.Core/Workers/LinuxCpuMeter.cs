using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace HyperPlot;

/// <summary>
/// The meter of Linux: the first line of <c>/proc/stat</c> for the machine, and each worker's CPU-time clock for its
/// thread (design §8.1 "読み方"). The line advances by 10 ms for each processor, so the rule samples every 200 ms there.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed partial class LinuxCpuMeter : CpuMeter
{
    // CLOCK_THREAD_CPUTIME_ID of <time.h>.
    private const int ThreadCpuClock = 3;

    // USER_HZ: the unit of the fields of /proc/stat on every Linux target (proc(5)).
    private const double TicksPerSecond = 100;

    private const double NanosecondsPerSecond = 1e9;

    private readonly Lock _gate = new();
    private readonly SafeFileHandle _stat;

    // Each worker's CPU-time clock, valid while its flag is set. Read and cleared under the lock.
    private readonly int[] _clocks;
    private readonly bool[] _attached;

    private long _lastBusy;
    private long _lastTotal;
    private long _lastWorkers;
    private long _lastAt;
    private bool _hasLast;

    /// <summary>Initializes a new instance of the <see cref="LinuxCpuMeter"/> class.</summary>
    /// <param name="workers">The number of the pool's workers.</param>
    /// <exception cref="IOException"><c>/proc/stat</c> cannot be opened.</exception>
    public LinuxCpuMeter(int workers)
    {
        _clocks = new int[workers];
        _attached = new bool[workers];
        _stat = File.OpenHandle("/proc/stat", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }

    /// <inheritdoc/>
    public override void Attach(int worker)
    {
        if (!TryGetClock(out var clock))
        {
            // No clock for the thread: every sample fails, and the pool never rests.
            return;
        }

        lock (_gate)
        {
            _clocks[worker] = clock;
            _attached[worker] = true;
        }
    }

    /// <inheritdoc/>
    public override void Detach(int worker)
    {
        lock (_gate)
        {
            _attached[worker] = false;
        }
    }

    /// <inheritdoc/>
    public override long ThreadTime() => ClockGetTime(ThreadCpuClock, out var time) == 0 ? time.Nanoseconds : 0;

    /// <inheritdoc/>
    public override void Observe(long units, long ticks)
    {
        // The unit is the nanosecond: nothing to learn.
    }

    /// <inheritdoc/>
    public override long ToTicks(long units) => (long)(units * (TimeProvider.System.TimestampFrequency / NanosecondsPerSecond));

    /// <inheritdoc/>
    public override bool TrySample(long now, out CpuSample sample)
    {
        sample = default;
        lock (_gate)
        {
            if (!TryReadMachine(out var busy, out var total))
            {
                _hasLast = false;
                return false;
            }

            long workers = 0;
            for (var i = 0; i < _clocks.Length; i++)
            {
                if (!_attached[i] || ClockGetTime(_clocks[i], out var time) != 0)
                {
                    _hasLast = false;
                    return false;
                }

                workers += time.Nanoseconds;
            }

            var hadLast = _hasLast;
            var busyTicks = busy - _lastBusy;
            var totalTicks = total - _lastTotal;
            var workerNanoseconds = workers - _lastWorkers;
            var ticks = now - _lastAt;
            _lastBusy = busy;
            _lastTotal = total;
            _lastWorkers = workers;
            _lastAt = now;
            _hasLast = true;
            if (!hadLast || ticks <= 0 || totalTicks <= 0)
            {
                return false;
            }

            // The processors come from the line too: Environment.ProcessorCount is the container's limit, not the machine's.
            var seconds = (double)ticks / TimeProvider.System.TimestampFrequency;
            var processors = totalTicks / TicksPerSecond / seconds;
            var idle = (totalTicks - busyTicks) / TicksPerSecond / seconds;
            sample = new CpuSample(processors, idle, workerNanoseconds / NanosecondsPerSecond / seconds);
            return true;
        }
    }

    // glibc before 2.34 has pthread_getcpuclockid in libpthread, not in libc (PR #423's review).
    private static bool TryGetClock(out int clock)
    {
        try
        {
            return PthreadGetCpuClockId(PthreadSelf(), out clock) == 0;
        }
        catch (EntryPointNotFoundException)
        {
            try
            {
                return PthreadGetCpuClockIdOfLibPthread(PthreadSelf(), out clock) == 0;
            }
            catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
            {
                clock = 0;
                return false;
            }
        }
    }

    // Reads "cpu  user nice system idle iowait irq softirq steal ...": the busy ticks are all but idle and iowait.
    private bool TryReadMachine(out long busy, out long total)
    {
        busy = 0;
        total = 0;
        Span<byte> line = stackalloc byte[256];
        var read = RandomAccess.Read(_stat, line, 0);
        line = line[..read];
        if (!line.StartsWith("cpu "u8))
        {
            return false;
        }

        var at = 4;
        long idle = 0;
        var fields = 0;
        while (fields < 8 && at < line.Length && line[at] != (byte)'\n')
        {
            if (line[at] == (byte)' ')
            {
                at++;
                continue;
            }

            long value = 0;
            while (at < line.Length && line[at] is >= (byte)'0' and <= (byte)'9')
            {
                value = (value * 10) + (line[at] - (byte)'0');
                at++;
            }

            // Fields 3 and 4 are idle and iowait.
            if (fields is 3 or 4)
            {
                idle += value;
            }

            total += value;
            fields++;
        }

        busy = total - idle;
        return fields >= 5;
    }

    // int clock_gettime(clockid_t clockid, struct timespec *tp): 0 on success.
    [LibraryImport("libc", EntryPoint = "clock_gettime")]
    private static partial int ClockGetTime(int clock, out Timespec time);

    // pthread_t pthread_self(void): an unsigned long on the Linux targets of the engine.
    [LibraryImport("libc", EntryPoint = "pthread_self")]
    private static partial nuint PthreadSelf();

    // int pthread_getcpuclockid(pthread_t thread, clockid_t *clockid): 0 on success.
    [LibraryImport("libc", EntryPoint = "pthread_getcpuclockid")]
    private static partial int PthreadGetCpuClockId(nuint thread, out int clock);

    [LibraryImport("libpthread.so.0", EntryPoint = "pthread_getcpuclockid")]
    private static partial int PthreadGetCpuClockIdOfLibPthread(nuint thread, out int clock);

    // struct timespec on the 64-bit Linux targets of the engine: two 64-bit fields.
    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long Seconds;
        public long Nanosecond;

        public readonly long Nanoseconds => (Seconds * 1_000_000_000) + Nanosecond;
    }
}
