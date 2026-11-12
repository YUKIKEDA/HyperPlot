namespace HyperPlot.Tests;

/// <summary>
/// How a worker pool yields the CPU: its level of rest follows the readings of the machine, a worker rests after its
/// background jobs only, and nothing waits for a rest (design §8.1 "譲り方", §10, requirement N-12, row S4).
/// </summary>
[Collection("Large data")]
public sealed class PlotWorkerPoolYieldTests
{
    private const double Processors = 16;

    // A period no test reaches: the tests take the samples themselves.
    private static WorkerYieldTuning Tuning => WorkerYieldTuning.Default with { SamplePeriod = TimeSpan.FromHours(1) };

    private static long Milliseconds(double count) => (long)(count * TimeProvider.System.TimestampFrequency / 1000);

    /// <summary>The engine ships with pools that yield; a pool made with <c>yields: false</c>, and every pool of the tests, never reads the machine.</summary>
    /// <remarks>
    /// Preconditions:
    /// - The test assembly turned the default off before any test, and kept the value it found.
    ///
    /// Steps:
    /// - Read the value the assembly found. Make a pool without saying, one with <c>yields: false</c>, and one with
    ///   <c>yields: true</c>. Make pools of 0 threads both ways.
    ///
    /// Expected:
    /// - The value found is true: what a pool made without saying, the shared one included, does outside the tests.
    /// - The pool made without saying and the one with false have no rule, so they take no reading and never rest.
    /// - The pool with true has a rule at level 0 on Windows and Linux, and none elsewhere.
    /// - 0 threads throws ArgumentOutOfRangeException that names the parameter.
    /// </remarks>
    [Fact]
    public void Constructor_Yields_DecidesWhetherThePoolReadsTheMachine()
    {
        Assert.True(TestAssemblySetup.ShippedYields);
        using (var byDefault = new PlotWorkerPool(1))
        {
            Assert.Null(byDefault.Yield);
        }

        using (var never = new PlotWorkerPool(1, yields: false))
        {
            Assert.Null(never.Yield);
        }

        using (var yielding = new PlotWorkerPool(1, yields: true))
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            {
                Assert.NotNull(yielding.Yield);
                Assert.Equal(0, yielding.Yield.Level);
            }
            else
            {
                Assert.Null(yielding.Yield);
            }
        }

        Assert.Equal("threadCount", Assert.Throws<ArgumentOutOfRangeException>(() => new PlotWorkerPool(0)).ParamName);
        Assert.Equal("threadCount", Assert.Throws<ArgumentOutOfRangeException>(() => new PlotWorkerPool(0, yields: true)).ParamName);
    }

    /// <summary>The level rises by 1 for a reading of a full machine that others use, halves for a reading with room or without others, and stays otherwise.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread with a meter that gives the readings of the test, 16 logical processors, and the shipped
    ///   constants: below 2 % idle and 1.5 cores or more of others to rise, above 8 % idle or fewer than 1.5 cores of
    ///   others to halve, at most 4.
    ///
    /// Steps:
    /// - Give 6 readings of 0.1 idle cores with the workers on 8.
    /// - Give a reading of 5 % idle with the others on 3 cores, and one that fails.
    /// - Give 4 readings of 10 % idle.
    /// - Give 3 full readings again, then 3 of no idle core with the workers on 15 (the others on 1).
    /// - Give a full reading with the others on exactly 1.5 cores.
    ///
    /// Expected:
    /// - The levels are 1, 2, 3, 4, 4, 4: one step a reading, and no further than the greatest.
    /// - The reading between the thresholds and the failed one leave 4.
    /// - The readings with room give 2, 1, 0, 0.
    /// - The full readings give 1, 2, 3; the workers alone on a full machine give 1, 0, 0: they do not rest for themselves.
    /// - Exactly 1.5 cores of others raises the level to 1.
    /// </remarks>
    [Fact]
    public void Level_FollowsTheReadingsOfTheMachine()
    {
        var meter = new ScriptedMeter();
        using var pool = new PlotWorkerPool(1, meter, Tuning);
        var rule = pool.Yield!;
        var full = new CpuSample(Processors, IdleCores: 0.1, WorkerCores: 8);
        var levels = new List<int>();

        int After(CpuSample sample, bool fails = false)
        {
            meter.Next = sample;
            meter.Fails = fails;
            rule.Sample(0);
            return rule.Level;
        }

        for (var i = 0; i < 6; i++)
        {
            levels.Add(After(full));
        }

        Assert.Equal([1, 2, 3, 4, 4, 4], levels);
        Assert.Equal(4, After(new CpuSample(Processors, IdleCores: 0.8, WorkerCores: 12.2)));
        Assert.Equal(4, After(new CpuSample(Processors, IdleCores: 16, WorkerCores: 0), fails: true));

        var room = new CpuSample(Processors, IdleCores: 1.6, WorkerCores: 8);
        Assert.Equal([2, 1, 0, 0], (int[])[After(room), After(room), After(room), After(room)]);

        Assert.Equal([1, 2, 3], (int[])[After(full), After(full), After(full)]);
        var alone = new CpuSample(Processors, IdleCores: 0, WorkerCores: 15);
        Assert.Equal([1, 0, 0], (int[])[After(alone), After(alone), After(alone)]);

        Assert.Equal(1, After(new CpuSample(Processors, IdleCores: 0, WorkerCores: 14.5)));
    }

    /// <summary>A worker rests after its background jobs for the level times the CPU time they used, and does not rest at level 0.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread with a meter that gives each job 8 ms of CPU time, and the shipped constants (a rest is
    ///   taken once 15.6 ms are saved up).
    /// - Two float columns of 3 chunks.
    ///
    /// Steps:
    /// - At level 0, compute the statistics of the first column ahead and wait.
    /// - Raise the level to 2 with 2 readings of a full machine, compute the statistics of the second column, and wait
    ///   for the task and for the third rest.
    ///
    /// Expected:
    /// - After the first column there was no rest.
    /// - After the second there were 3 rests of 48 ms in all: 2 times the 24 ms of the 3 jobs.
    /// - No rest was cut short.
    /// </remarks>
    [Fact]
    public void BackgroundJobs_AtALevel_AreFollowedByRestsOfTheLevelTimesTheirCpuTime()
    {
        var meter = new ScriptedMeter { JobCpu = Milliseconds(8) };
        using var pool = new PlotWorkerPool(1, meter, Tuning);
        var rule = pool.Yield!;
        using var first = Column.Wrap(new float[PyramidData.Length]);
        using var second = Column.Wrap(new float[PyramidData.Length]);

        Eventually.Completes(pool.PrecomputeStats(first));
        Assert.Equal(0, rule.RestCount);

        Raise(meter, rule, 2);
        Eventually.Completes(pool.PrecomputeStats(second));
        Eventually.True(() => rule.RestCount == 3, "the rest after the third job");

        Assert.Equal(48, rule.RestTime.TotalMilliseconds, 0.01);
        Assert.Equal(0, rule.RestsCutShort);
    }

    /// <summary>A worker does not rest after a frame's jobs or after a chunk a query asked for.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread whose caller takes no frame job, with a meter that gives each job 8 ms of CPU time; built
    ///   series for a batch of 4.
    /// - A pyramid of 3 chunks whose chunk 0 the storage holds, with the worker in it.
    ///
    /// Steps:
    /// - Raise the level to 2. Extract the batch of 4 on another thread, and ask the pyramid for the minimum and maximum
    ///   of chunk 2, which requests that chunk.
    /// - Release chunk 0, and wait for the extraction, the pyramid, and the worker's record of its 7 jobs.
    ///
    /// Expected:
    /// - The worker ran 4 frame jobs and 3 chunks, and rested twice: after chunk 0 and chunk 1, which nothing asked
    ///   for. The 4 frame jobs and the requested chunk were not followed by a rest.
    /// </remarks>
    [Fact]
    public void FrameJobsAndRequestedChunks_AtALevel_AreNotFollowedByARest()
    {
        var meter = new ScriptedMeter { JobCpu = Milliseconds(8) };
        using var pool = new PlotWorkerPool(1, meter, Tuning) { CallerTakesJobs = false };
        var rule = pool.Yield!;
        using var series = new BuiltSeries(pool, 4);
        using var column = TestStorage<float>.MakeColumn(new float[PyramidData.Length], out var storage);
        storage.Hold(0);
        using var pyramid = pool.BuildPyramid(column);
        storage.WaitForWorker(0);
        var restsBefore = rule.RestCount;
        var frames = 0;
        var chunks = 0;
        pool.TestTook = frame => _ = frame ? Interlocked.Increment(ref frames) : Interlocked.Increment(ref chunks);

        Raise(meter, rule, 2);
        var extraction = Task.Run(() => pool.Extract(series.Batch, default, series.Boundaries), TestContext.Current.CancellationToken);
        Eventually.True(() => pool.IsQueued(series.Batch), "the batch to be in the workers' list");
        Assert.True(pyramid.GetMinMax(2L * Column.ChunkLength, 1000).IsProvisional);
        storage.Release(0);
        Eventually.Completes(extraction);
        Eventually.Completes(pyramid.Completion);
        Eventually.True(() => Volatile.Read(ref frames) == 4 && Volatile.Read(ref chunks) == 3, "the worker to record its 7 jobs");
        Eventually.True(() => rule.RestCount - restsBefore >= 2, "the rest after the last chunk");
        pool.TestTook = null;

        Assert.Equal(2, rule.RestCount - restsBefore);
    }

    /// <summary>The disposal of a pyramid and of the pool do not wait for a worker's rest.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread with a meter that gives each job 10 minutes of CPU time, and constants whose longest rest
    ///   is 10 minutes: a rest outlasts the watchdog of the test.
    /// - A pyramid of 1 chunk.
    ///
    /// Steps:
    /// - Raise the level to 1, build the pyramid, and wait for it and for the worker's rest to start.
    /// - Dispose of the pyramid, then of the pool, each on another thread with a watchdog of 30 s.
    ///
    /// Expected:
    /// - Both disposals return within the watchdog: the rest is outside the job a disposal waits for, and the pool's
    ///   disposal ends it.
    /// - The one rest was cut short.
    /// </remarks>
    [Fact]
    public void Dispose_WhileAWorkerRests_DoesNotWaitForTheRest()
    {
        var meter = new ScriptedMeter { JobCpu = Milliseconds(600_000) };
        var pool = new PlotWorkerPool(1, meter, Tuning with { LongestRest = TimeSpan.FromMinutes(10) });
        var rule = pool.Yield!;
        using var column = Column.Wrap(new float[1000]);
        Raise(meter, rule, 1);
        var pyramid = pool.BuildPyramid(column);
        Eventually.Completes(pyramid.Completion);
        Eventually.True(() => rule.RestCount == 1, "the worker's rest");

        Eventually.Completes(Task.Run(pyramid.Dispose, TestContext.Current.CancellationToken));
        Eventually.Completes(Task.Run(pool.Dispose, TestContext.Current.CancellationToken));

        Assert.Equal(1, rule.RestsCutShort);
        Assert.Equal(TimeSpan.FromMinutes(10), rule.RestTime);
    }

    /// <summary>A pool at its greatest level builds the same pyramid, the same spatial tile index, and the same density tiles as a pool that never rests.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 20,000 seeded float points, and a float column of 3 chunks of seeded values.
    /// - A pool of 3 threads that never rests, and one of 3 threads at level 4 whose meter gives each job 0.5 ms of CPU
    ///   time and whose rest is taken at once (a step of 0), so every background job is followed by a rest of 2 ms.
    ///
    /// Steps:
    /// - With each pool, build the index with a density split of 64, and the pyramid.
    ///
    /// Expected:
    /// - The resting pool rested.
    /// - The points of the two copies are the same at the same places, with the same rows.
    /// - The root's density tile has the same width and the same bytes.
    /// - The minimum, maximum, and count of 50 seeded ranges of the pyramid are the same.
    /// </remarks>
    [Fact]
    public void Builds_OnAPoolAtItsGreatestLevel_AreTheSameAsOnAPoolThatNeverRests()
    {
        var (xs, ys) = SpatialData.Uniform(20_000, seed: 421);
        var values = PyramidData.Convert<float>(PyramidData.Values(PyramidData.Length, "Sparse", 421, integral: false));
        var tuning = SpatialReference.Small with { DensitySplit = 64, TotalShare = 1e6 };
        var meter = new ScriptedMeter { JobCpu = Milliseconds(0.5) };
        using var plain = new PlotWorkerPool(3, yields: false);
        using var resting = new PlotWorkerPool(3, meter, Tuning with { RestStep = TimeSpan.Zero });
        Raise(meter, resting.Yield!, 4);

        var results = new List<((double[] X, double[] Y, long[] Rows) Points, int BytesPerBin, byte[] Bins, MinMax<float>[] Ranges)>();
        foreach (var pool in (PlotWorkerPool[])[plain, resting])
        {
            using var x = Column.Wrap(xs);
            using var y = Column.Wrap(ys);
            using var index = SpatialReference.Build(pool, x, y, new SpatialIndexInput { Rows = RowRange.All }, tuning);
            var snapshot = index.Acquire();
            Assert.True(snapshot.Tiles!.TryGetTile(0, 0, out var root));
            var points = SpatialReference.ReadAll(snapshot);
            var (width, bins) = (root.BytesPerBin, root.Bins.ToArray());
            snapshot.Release();

            using var column = Column.Wrap(values);
            using var pyramid = pool.BuildPyramid(column);
            Eventually.Completes(pyramid.Completion);
            var random = new Random(421);
            var ranges = new MinMax<float>[50];
            for (var i = 0; i < ranges.Length; i++)
            {
                var start = random.NextInt64(values.Length);
                ranges[i] = pyramid.GetMinMax(start, random.NextInt64(values.Length - start + 1));
            }

            results.Add((points, width, bins, ranges));
        }

        Assert.True(resting.Yield!.RestCount > 0);
        SpatialReference.AssertSamePoints(results[0].Points, results[1].Points);
        Assert.Equal(results[0].BytesPerBin, results[1].BytesPerBin);
        Assert.Equal(results[0].Bins, results[1].Bins);
        Assert.Equal(results[0].Ranges, results[1].Ranges);
    }

    /// <summary>The meter of the operating system counts the CPU of a worker whose job has not ended as the workers', and gives no sample before it can.</summary>
    /// <remarks>
    /// Preconditions:
    /// - Windows or Linux. The meter of the operating system for 1 worker, and a thread attached as that worker.
    ///
    /// Steps:
    /// - Take a sample before the thread attached, and one right after.
    /// - On the thread, spin for 20 ms as a first job and tell the meter of it; then spin until told to stop, without
    ///   ending a job.
    /// - While it spins, take a sample, wait 300 ms, and take another.
    ///
    /// Expected:
    /// - The sample before the thread attached gives nothing, and the first sample after gives nothing (no earlier reading).
    /// - The thread's CPU time grew over the first job, and converts to between 5 and 40 ms.
    /// - The last sample gives the workers between 0.3 and 1.3 cores although no job ended in it, idle cores between 0
    ///   and the processors, and 1 processor or more.
    /// </remarks>
    [Fact]
    public void SystemMeter_AWorkerInALongJob_IsCountedAsTheWorkers()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() || OperatingSystem.IsLinux(), "Only Windows and Linux have a meter.");
        var meter = CpuMeter.TryCreate(1);
        Assert.NotNull(meter);
        Assert.False(meter.TrySample(TimeProvider.System.GetTimestamp(), out _));

        using var attached = new ManualResetEventSlim();
        using var spinning = new ManualResetEventSlim();
        var stop = false;
        long firstJob = 0;
        var thread = new Thread(() =>
        {
            meter.Attach(0);
            attached.Set();
            var startedAt = TimeProvider.System.GetTimestamp();
            var cpu = meter.ThreadTime();
            while (TimeProvider.System.GetElapsedTime(startedAt) < TimeSpan.FromMilliseconds(20)) { }

            firstJob = meter.ThreadTime() - cpu;
            meter.Observe(firstJob, TimeProvider.System.GetTimestamp() - startedAt);
            spinning.Set();
            while (!Volatile.Read(ref stop)) { }

            meter.Detach(0);
        })
        {
            IsBackground = true,
        };
        thread.Start();
        try
        {
            Assert.True(attached.Wait(30_000, TestContext.Current.CancellationToken));
            Assert.False(meter.TrySample(TimeProvider.System.GetTimestamp(), out _));
            Assert.True(spinning.Wait(30_000, TestContext.Current.CancellationToken));
            Assert.InRange(TimeProvider.System.GetElapsedTime(0, meter.ToTicks(firstJob)).TotalMilliseconds, 5, 40);

            _ = meter.TrySample(TimeProvider.System.GetTimestamp(), out _);
            using var never = new ManualResetEventSlim();
            _ = never.Wait(300, TestContext.Current.CancellationToken);
            Assert.True(meter.TrySample(TimeProvider.System.GetTimestamp(), out var sample));

            Assert.InRange(sample.WorkerCores, 0.3, 1.3);
            Assert.InRange(sample.Processors, 1, 4096);
            Assert.InRange(sample.IdleCores, 0, sample.Processors);
        }
        finally
        {
            Volatile.Write(ref stop, true);
            thread.Join();
        }

        Assert.False(meter.TrySample(TimeProvider.System.GetTimestamp(), out _));
    }

    // Gives readings of a full machine that others use until the level is the one asked for.
    private static void Raise(ScriptedMeter meter, WorkerYield rule, int level)
    {
        meter.Next = new CpuSample(Processors, IdleCores: 0.1, WorkerCores: 8);
        while (rule.Level < level)
        {
            rule.Sample(0);
        }
    }

    /// <summary>A meter that gives the readings a test sets, and the same CPU time to every job.</summary>
    private sealed class ScriptedMeter : CpuMeter
    {
        private long _time;

        /// <summary>Gets or sets the reading the next sample gives.</summary>
        public CpuSample Next { get; set; }

        /// <summary>Gets or sets a value indicating whether the next sample fails.</summary>
        public bool Fails { get; set; }

        /// <summary>Gets or sets the CPU time of every job, in ticks of <see cref="TimeProvider.System"/>.</summary>
        public long JobCpu { get; set; }

        /// <summary>Gets the number of samples taken.</summary>
        public int Samples { get; private set; }

        public override void Attach(int worker) { }

        public override void Detach(int worker) { }

        // Each reading is one job's CPU time after the last: the reading at a job's end is that much after the one at its start.
        public override long ThreadTime() => Interlocked.Add(ref _time, JobCpu);

        public override void Observe(long units, long ticks) { }

        public override long ToTicks(long units) => units;

        public override bool TrySample(long now, out CpuSample sample)
        {
            Samples++;
            sample = Next;
            return !Fails;
        }
    }

    /// <summary>The built series of one batch.</summary>
    private sealed class BuiltSeries : IDisposable
    {
        private const int Columns = 64;
        private readonly List<Column<float>> _columns = [];
        private readonly List<MinMaxPyramid<float>> _pyramids = [];

        public BuiltSeries(PlotWorkerPool pool, int series)
        {
            var x = Column.Uniform(0, 1, PyramidData.Length);
            Batch = new M4Batch();
            for (var s = 0; s < series; s++)
            {
                var column = Column.Wrap(PyramidData.Convert<float>(PyramidData.Values(PyramidData.Length, "Sparse", 100 + s, integral: false)));
                _columns.Add(column);
                var pyramid = MinMaxPyramidTests.Build(pool, column);
                _pyramids.Add(pyramid);
                _ = Batch.Add(x, pyramid, new M4Point[M4.MaxPointCount(Columns)]);
            }

            Boundaries = [.. Enumerable.Range(0, Columns + 1).Select(k => k * ((PyramidData.Length - 1.0) / Columns))];
        }

        public M4Batch Batch { get; }

        public double[] Boundaries { get; }

        public void Dispose()
        {
            foreach (var pyramid in _pyramids)
            {
                pyramid.Dispose();
            }

            foreach (var column in _columns)
            {
                column.Dispose();
            }
        }
    }
}
