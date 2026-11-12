namespace HyperPlot;

/// <summary>The constants of the rule by which a pool's workers rest (design §8.1 "定数").</summary>
/// <param name="SamplePeriod">The time between two readings of the machine.</param>
/// <param name="BusyShare">The idle share of the machine below which the level may rise.</param>
/// <param name="IdleShare">The idle share above which the level halves.</param>
/// <param name="OtherCores">The cores the others must use for the level to rise; with fewer, it halves.</param>
/// <param name="MostRest">The greatest level: a worker rests at most this many times the CPU time of its jobs.</param>
/// <param name="RestStep">The rest a worker saves up before it waits.</param>
/// <param name="LongestRest">The longest single wait.</param>
internal readonly record struct WorkerYieldTuning(
    TimeSpan SamplePeriod,
    double BusyShare,
    double IdleShare,
    double OtherCores,
    int MostRest,
    TimeSpan RestStep,
    TimeSpan LongestRest
)
{
    /// <summary>Gets the constants the engine ships with. Linux reads the machine in steps of 10 ms, so its period is longer.</summary>
    public static WorkerYieldTuning Default =>
        new(
            TimeSpan.FromMilliseconds(OperatingSystem.IsLinux() ? 200 : 50),
            BusyShare: 0.02,
            IdleShare: 0.08,
            OtherCores: 1.5,
            MostRest: 4,
            TimeSpan.FromMilliseconds(15.6),
            TimeSpan.FromMilliseconds(200)
        );
}

/// <summary>
/// Makes the workers of one pool rest between background jobs while the machine has no idle CPU and something other
/// than the workers is using it (design §8.1 "譲り方", requirement N-12). The workers keep the normal priority: a thread
/// that runs answers the GC's suspension, the frame's jobs, and its locks as before, and a thread that rests holds none.
/// </summary>
internal sealed class WorkerYield : IDisposable
{
    // The rest the calling worker has saved up, in ticks. A worker's thread belongs to one pool.
    [ThreadStatic]
    private static long _owed;

    private readonly CpuMeter _meter;
    private readonly WorkerYieldTuning _tuning;
    private readonly long _period;
    private readonly long _restStep;
    private readonly long _longestRest;

    // Set when the pool's disposal begins: a rest in progress ends, and none starts.
    private readonly ManualResetEventSlim _stopped = new(initialState: false);

    private long _sampledAt = TimeProvider.System.GetTimestamp();
    private int _level;
    private long _restCount;
    private long _restTicks;
    private long _restsCutShort;

    // What the samples read, summed by the one thread that samples; the benchmarks report their means (design §9).
    private long _samples;
    private double _idleShares;
    private double _otherCores;
    private double _workerCores;
    private long _levels;

    /// <summary>Initializes a new instance of the <see cref="WorkerYield"/> class.</summary>
    /// <param name="meter">What reads the machine and the workers' threads.</param>
    /// <param name="tuning">The constants of the rule.</param>
    public WorkerYield(CpuMeter meter, WorkerYieldTuning tuning)
    {
        _meter = meter;
        _tuning = tuning;
        _period = ToTicks(tuning.SamplePeriod);
        _restStep = ToTicks(tuning.RestStep);
        _longestRest = ToTicks(tuning.LongestRest);
    }

    /// <summary>Gets the meter.</summary>
    public CpuMeter Meter => _meter;

    /// <summary>Gets the level of rest, from 0 to the tuning's greatest: a worker rests this many times the CPU time of its background jobs.</summary>
    public int Level => Volatile.Read(ref _level);

    /// <summary>Gets the number of rests the workers have started.</summary>
    public long RestCount => Interlocked.Read(ref _restCount);

    /// <summary>Gets the total length of the rests the workers have started, as asked for.</summary>
    public TimeSpan RestTime => TimeSpan.FromSeconds((double)Interlocked.Read(ref _restTicks) / TimeProvider.System.TimestampFrequency);

    /// <summary>Gets the number of rests that the pool's disposal ended before their time.</summary>
    public long RestsCutShort => Interlocked.Read(ref _restsCutShort);

    /// <summary>Gets the means of what the samples read so far. The benchmarks only, after the work has ended.</summary>
    public (long Samples, double IdleShare, double OtherCores, double WorkerCores, double Level) Means =>
        _samples == 0 ? default : (_samples, _idleShares / _samples, _otherCores / _samples, _workerCores / _samples, (double)_levels / _samples);

    /// <summary>Starts the measurement of a background job on the calling worker.</summary>
    /// <param name="startedAt">The time now, a timestamp of <see cref="TimeProvider.System"/>.</param>
    /// <returns>The CPU the calling thread has used, in the meter's units.</returns>
    public long Begin(out long startedAt)
    {
        startedAt = TimeProvider.System.GetTimestamp();
        return _meter.ThreadTime();
    }

    /// <summary>
    /// Ends the measurement of a background job that the calling worker ran: reads the machine when the period has
    /// passed, and rests when the worker has saved up enough. Called outside every job, so nothing waits for the rest
    /// but the worker.
    /// </summary>
    /// <param name="cpuAtStart">What <see cref="Begin"/> returned.</param>
    /// <param name="startedAt">What <see cref="Begin"/> gave.</param>
    public void AfterBackground(long cpuAtStart, long startedAt)
    {
        var now = TimeProvider.System.GetTimestamp();
        var units = _meter.ThreadTime() - cpuAtStart;
        _meter.Observe(units, now - startedAt);

        // One worker reads for all: the one whose job ended first after the period.
        var last = Volatile.Read(ref _sampledAt);
        if (now - last >= _period && Interlocked.CompareExchange(ref _sampledAt, now, last) == last)
        {
            Sample(now);
        }

        var level = Volatile.Read(ref _level);
        if (level == 0)
        {
            _owed = 0;
            return;
        }

        // By the CPU time, not the wall time: the time a competitor took from the job is not rested for again.
        _owed += level * _meter.ToTicks(units);
        if (_owed < _restStep)
        {
            return;
        }

        var rest = Math.Min(_owed, _longestRest);
        _owed = 0;
        _ = Interlocked.Increment(ref _restCount);
        _ = Interlocked.Add(ref _restTicks, rest);
        if (_stopped.Wait(TimeSpan.FromSeconds((double)rest / TimeProvider.System.TimestampFrequency)))
        {
            _ = Interlocked.Increment(ref _restsCutShort);
        }
    }

    /// <summary>Reads the machine and moves the level by what it read. One thread at a time.</summary>
    /// <param name="now">The time now, a timestamp of <see cref="TimeProvider.System"/>.</param>
    public void Sample(long now)
    {
        if (_meter.TrySample(now, out var sample))
        {
            Apply(sample);
        }
    }

    /// <summary>Ends the rests: the pool's disposal has begun.</summary>
    public void Stop() => _stopped.Set();

    /// <summary>Frees the event the rests wait on. Called after the workers' threads have ended.</summary>
    public void Dispose() => _stopped.Dispose();

    private static long ToTicks(TimeSpan time) => (long)(time.TotalSeconds * TimeProvider.System.TimestampFrequency);

    private void Apply(in CpuSample sample)
    {
        var level = _level;
        if (sample.IdleShare < _tuning.BusyShare && sample.OtherCores >= _tuning.OtherCores)
        {
            level = Math.Min(_tuning.MostRest, level + 1);
        }
        else if (sample.IdleShare > _tuning.IdleShare || sample.OtherCores < _tuning.OtherCores)
        {
            // The machine has room again, or the workers have it to themselves.
            level /= 2;
        }

        Volatile.Write(ref _level, level);
        _samples++;
        _idleShares += sample.IdleShare;
        _otherCores += sample.OtherCores;
        _workerCores += sample.WorkerCores;
        _levels += level;
    }
}
