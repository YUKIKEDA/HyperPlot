namespace HyperPlot;

/// <summary>What a <see cref="CpuMeter"/> read of the machine between two samples, in cores (design §8.1 "譲り方").</summary>
/// <param name="Processors">The logical processors of the machine.</param>
/// <param name="IdleCores">The cores that were idle.</param>
/// <param name="WorkerCores">The cores that the threads of the pool's workers used, whatever jobs they ran.</param>
internal readonly record struct CpuSample(double Processors, double IdleCores, double WorkerCores)
{
    /// <summary>Gets the share of the machine that was idle, from 0 to 1.</summary>
    public double IdleShare => IdleCores / Processors;

    /// <summary>Gets the cores that everything but the pool's workers used: other processes and the other threads of this one.</summary>
    public double OtherCores => Processors - IdleCores - WorkerCores;
}

/// <summary>
/// Reads how much CPU the machine has idle and how much the threads of one pool's workers use (design §8.1 "読み方").
/// The readings do not advance by the clock's tick: the thresholds of the rule are as small as a tick over its window.
/// </summary>
internal abstract class CpuMeter
{
    /// <summary>Returns the meter of the operating system, or <see langword="null"/> where there is none: the pool never rests.</summary>
    /// <param name="workers">The number of the pool's workers.</param>
    /// <returns>The meter, or <see langword="null"/>.</returns>
    public static CpuMeter? TryCreate(int workers)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return new WindowsCpuMeter(workers);
            }

            if (OperatingSystem.IsLinux())
            {
                return new LinuxCpuMeter(workers);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EntryPointNotFoundException or DllNotFoundException)
        {
            // The machine cannot be read (no /proc, an export that is missing): the same as an OS without a meter.
        }

        return null;
    }

    /// <summary>Makes the calling thread the pool's worker of a number. Called once, on the worker's thread, when it starts.</summary>
    /// <param name="worker">The worker's number, from 0.</param>
    public abstract void Attach(int worker);

    /// <summary>Forgets the calling thread. Called on the worker's thread when it ends; no sample reads the thread afterwards.</summary>
    /// <param name="worker">The worker's number.</param>
    public abstract void Detach(int worker);

    /// <summary>Returns the CPU the calling thread has used since it started, in the meter's units. Neither allocates nor waits.</summary>
    /// <returns>The units, or 0 when the reading failed.</returns>
    public abstract long ThreadTime();

    /// <summary>Tells the meter of a job that used a number of units over a wall time, from which it may learn the length of a unit.</summary>
    /// <param name="units">The units of CPU the job's thread used.</param>
    /// <param name="ticks">The wall time of the job, in ticks of <see cref="TimeProvider.System"/>.</param>
    public abstract void Observe(long units, long ticks);

    /// <summary>Converts units of CPU to ticks of <see cref="TimeProvider.System"/>.</summary>
    /// <param name="units">The units.</param>
    /// <returns>The ticks, or 0 while the meter does not know the length of a unit.</returns>
    public abstract long ToTicks(long units);

    /// <summary>
    /// Reads the machine and every worker's thread now, and gives what they used since the last call that read them.
    /// One thread at a time. Does not allocate.
    /// </summary>
    /// <param name="now">The time of the call, a timestamp of <see cref="TimeProvider.System"/>.</param>
    /// <param name="sample">What was used since the last reading.</param>
    /// <returns>
    /// <see langword="false"/> when there is no earlier reading, when the meter does not know the length of a unit yet, or
    /// when a reading failed: the rule keeps its level.
    /// </returns>
    public abstract bool TrySample(long now, out CpuSample sample);
}
