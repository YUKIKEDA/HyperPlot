namespace HyperPlot.Tests;

/// <summary>
/// One spatial tile index over two float columns of test storage, on a pool of its own, with a probe on its jobs and
/// an allocator that records its copies: what the tests of the states and the lifetime of an index start from
/// (design §3.4 "状態と公開", row C3a). The pool has 1 thread unless the test asks for more, so that the jobs run in
/// one order and a held job stops the build.
/// </summary>
internal sealed class SpatialRig : IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<Sight> _sights = [];
    private SpatialIndex? _index;

    /// <summary>Initializes a new instance of the <see cref="SpatialRig"/> class over seeded points spread evenly.</summary>
    /// <param name="length">The number of rows.</param>
    /// <param name="seed">The seed of the points (<see cref="SpatialData.Uniform"/>).</param>
    /// <param name="workers">The number of threads of the pool.</param>
    public SpatialRig(int length = 5_000, int seed = 1, int workers = 1)
        : this(SpatialData.Uniform(length, seed), workers) { }

    /// <summary>Initializes a new instance of the <see cref="SpatialRig"/> class over the values of two arrays.</summary>
    /// <param name="values">The x and the y of each row. The columns read these arrays, so a test changes a value by writing it.</param>
    /// <param name="workers">The number of threads of the pool.</param>
    public SpatialRig((float[] X, float[] Y) values, int workers = 1)
    {
        (Xs, Ys) = values;
        X = TestStorage<float>.MakeColumn(Xs, out var xStorage);
        Y = TestStorage<float>.MakeColumn(Ys, out var yStorage);
        XStorage = xStorage;
        YStorage = yStorage;
        Pool = new PlotWorkerPool(workers);
    }

    /// <summary>Gets the x of each row: the array the column reads.</summary>
    public float[] Xs { get; }

    /// <summary>Gets the y of each row: the array the column reads.</summary>
    public float[] Ys { get; }

    /// <summary>Gets the column of x.</summary>
    public Column<float> X { get; }

    /// <summary>Gets the column of y.</summary>
    public Column<float> Y { get; }

    /// <summary>Gets the storage of x, which can make a worker's read of a chunk throw.</summary>
    public TestStorage<float> XStorage { get; }

    /// <summary>Gets the storage of y, which can make a worker's read of a chunk throw.</summary>
    public TestStorage<float> YStorage { get; }

    /// <summary>Gets the pool that builds the index.</summary>
    public PlotWorkerPool Pool { get; }

    /// <summary>Gets the probe on the jobs of the index. Set its holds before <see cref="Start"/>.</summary>
    public SpatialProbe Probe { get; } = new();

    /// <summary>Gets the allocator of the index, which records the copy of each build.</summary>
    public RecordingCopyAllocator Allocator { get; } = new();

    /// <summary>Gets the index that <see cref="Start"/> made.</summary>
    public SpatialIndex Index => _index ?? throw new InvalidOperationException("The index of the rig has not been started.");

    /// <summary>Gets what <see cref="Watch"/> saw at the end of each job, in the order of the jobs.</summary>
    public Sight[] Sights
    {
        get
        {
            lock (_gate)
            {
                return [.. _sights];
            }
        }
    }

    /// <summary>Starts the build of the index and does not wait for it.</summary>
    /// <param name="input">The input, or <see langword="null"/> for every row with no transform and no passed range.</param>
    /// <param name="tuning">The constants, or <see langword="null"/> for <see cref="SpatialReference.Small"/>.</param>
    /// <returns>The index.</returns>
    public SpatialIndex Start(SpatialIndexInput? input = null, SpatialIndexTuning? tuning = null)
    {
        _index = SpatialReference.Start(Pool, X, Y, input ?? new SpatialIndexInput { Rows = RowRange.All }, tuning, Allocator, Probe);
        return _index;
    }

    /// <summary>Starts the build of the index and waits, within the watchdog, until it is complete.</summary>
    /// <param name="input">The input, or <see langword="null"/> for every row with no transform and no passed range.</param>
    /// <param name="tuning">The constants, or <see langword="null"/> for <see cref="SpatialReference.Small"/>.</param>
    /// <returns>The index.</returns>
    public SpatialIndex Build(SpatialIndexInput? input = null, SpatialIndexTuning? tuning = null)
    {
        var index = Start(input, tuning);
        Eventually.Completes(index.Completion);
        return index;
    }

    /// <summary>
    /// Keeps the only worker of the pool in a job of another index until the returned object is disposed, so that the
    /// index of the rig stays as it is: an index that waits for a build goes on waiting.
    /// </summary>
    /// <returns>The hold of the worker.</returns>
    public Blocker BlockWorker() => new(Pool);

    /// <summary>
    /// Notes, at the end of each job from here on, the current snapshot of the index and whether
    /// <see cref="SpatialIndex.Completion"/> has ended. For builds that no test disposes while they run.
    /// </summary>
    public void Watch()
    {
        var before = Probe.AfterJob;
        Probe.AfterJob = (job, index) =>
        {
            before?.Invoke(job, index);
            var snapshot = index.Acquire();
            var sight = new Sight(job, snapshot, snapshot.Stage, snapshot.IsProvisional, index.Completion.IsCompleted, snapshot.Tiles is not null);
            snapshot.Release();
            lock (_gate)
            {
                _sights.Add(sight);
            }
        };
    }

    /// <summary>Checks a full snapshot against the values that the arrays hold now (<see cref="SpatialReference.Check"/>).</summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="xRange">The passed range of x, or <see langword="null"/> when the index computed it.</param>
    /// <param name="yRange">The passed range of y, or <see langword="null"/> when the index computed it.</param>
    public void Check(SpatialSnapshot snapshot, ScaleRange? xRange = null, ScaleRange? yRange = null) =>
        SpatialReference.Check(
            snapshot,
            SpatialAxisData.Of(Xs),
            SpatialAxisData.Of(Ys),
            new RowRange(0, Xs.Length),
            SpatialReference.Small,
            xRange,
            yRange
        );

    /// <inheritdoc />
    public void Dispose()
    {
        _index?.Dispose();
        X.Dispose();
        Y.Dispose();
        Pool.Dispose();
    }

    /// <summary>The current snapshot of the index at the end of one job.</summary>
    /// <param name="Job">The job.</param>
    /// <param name="Snapshot">The current snapshot, already given back: only its identity is of use.</param>
    /// <param name="Stage">The stage of the snapshot.</param>
    /// <param name="Provisional">Whether the snapshot was provisional.</param>
    /// <param name="Ended">Whether <see cref="SpatialIndex.Completion"/> had ended.</param>
    /// <param name="HasTiles">Whether the snapshot had its density tiles.</param>
    internal readonly record struct Sight(SpatialJob Job, SpatialSnapshot Snapshot, SpatialStage Stage, bool Provisional, bool Ended, bool HasTiles);

    /// <summary>The only worker of a pool, kept at the end of the first job of an index of one row.</summary>
    internal sealed class Blocker : IDisposable
    {
        private readonly Column<float> _x = Column.Wrap(new float[1]);
        private readonly Column<float> _y = Column.Wrap(new float[1]);
        private readonly SpatialProbe.Hold _hold;
        private readonly SpatialIndex _index;

        /// <summary>Initializes a new instance of the <see cref="Blocker"/> class and waits until the worker is kept.</summary>
        /// <param name="pool">The pool of 1 thread.</param>
        public Blocker(PlotWorkerPool pool)
        {
            var probe = new SpatialProbe();
            _hold = probe.HoldAt(SpatialJob.Allocate);
            _index = SpatialReference.Start(pool, _x, _y, new SpatialIndexInput { Rows = RowRange.All }, probe: probe);
            _hold.WaitForWorker();
        }

        /// <summary>Lets the worker go and disposes the index that kept it, which waits for the job the worker was in.</summary>
        public void Dispose()
        {
            _hold.Release();
            _index.Dispose();
            _x.Dispose();
            _y.Dispose();
        }
    }
}
