namespace HyperPlot.Tests;

/// <summary>
/// Follows the jobs of one index through <see cref="SpatialIndexTuning.JobEnded"/>: it records each job with the state
/// and the version of the index at the job's end, and can hold the workers at the end of a job. A single job is held
/// before it queues what follows, so the build stands still there; a unit is held before it is counted as ended, so its
/// stage does not end.
/// </summary>
internal sealed class SpatialProbe
{
    private const int Watchdog = 30_000;

    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _attached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Step> _steps = [];
    private SpatialIndex? _index;

    /// <summary>Gets or sets what a worker runs at the end of each job, after the job is recorded.</summary>
    public Action<SpatialJob, SpatialIndex>? AfterJob { get; set; }

    /// <summary>Gets the jobs that ended, in the order they were recorded.</summary>
    public Step[] Steps
    {
        get
        {
            lock (_gate)
            {
                return [.. _steps];
            }
        }
    }

    /// <summary>Gets the single jobs that ended, without the units of the stages, in the order they ended.</summary>
    public SpatialJob[] Singles => [.. Steps.Select(step => step.Job).Where(job => !IsUnit(job))];

    /// <summary>Returns the constants with the probe as the listener of the jobs.</summary>
    public SpatialIndexTuning Tuning(SpatialIndexTuning? from = null) => (from ?? SpatialReference.Small) with { JobEnded = OnJobEnded };

    /// <summary>Tells the probe its index. The jobs that ended before wait for this call.</summary>
    public SpatialIndex Attach(SpatialIndex index)
    {
        _index = index;
        _attached.TrySetResult();
        return index;
    }

    /// <summary>Returns the number of jobs of a kind that ended.</summary>
    public int CountOf(SpatialJob job)
    {
        lock (_gate)
        {
            return _steps.Count(step => step.Job == job);
        }
    }

    /// <summary>Holds every worker that ends a job of a kind until the hold is released. Set it before the build starts.</summary>
    public Hold HoldAt(SpatialJob job)
    {
        var hold = new Hold();
        var before = AfterJob;
        AfterJob = (ended, index) =>
        {
            before?.Invoke(ended, index);
            if (ended == job)
            {
                hold.Enter();
            }
        };
        return hold;
    }

    private static bool IsUnit(SpatialJob job) => job is SpatialJob.Range or SpatialJob.First or SpatialJob.Pass or SpatialJob.Write;

    private void OnJobEnded(SpatialJob job, long ticks)
    {
        if (!_attached.Task.Wait(Watchdog))
        {
            throw new TimeoutException("The probe was not told its index within the watchdog.");
        }

        var index = _index!;
        lock (_gate)
        {
            _steps.Add(new Step(job, index.State, index.Version, Environment.CurrentManagedThreadId, ticks));
        }

        AfterJob?.Invoke(job, index);
    }

    /// <summary>One job at its end.</summary>
    /// <param name="Job">The job.</param>
    /// <param name="State">The state of the index at the end of the job.</param>
    /// <param name="Version">The version of the index at the end of the job.</param>
    /// <param name="Thread">The managed thread that ran the job.</param>
    /// <param name="Ticks">The time of the job in the ticks of the system's time provider.</param>
    internal readonly record struct Step(SpatialJob Job, SpatialIndexState State, long Version, int Thread, long Ticks);

    /// <summary>A place where the workers of a build stand until the test lets them go.</summary>
    internal sealed class Hold
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Waits until a worker stands at the hold.</summary>
        public void WaitForWorker() => Assert.True(_entered.Task.Wait(Watchdog), "No worker reached the hold within the watchdog.");

        /// <summary>Lets the workers go, now and from here on.</summary>
        public void Release() => _released.TrySetResult();

        /// <summary>Stands at the hold until it is released. The watchdog ends a wait that a failed test left behind.</summary>
        internal void Enter()
        {
            _entered.TrySetResult();
            _released.Task.Wait(Watchdog);
        }
    }
}
