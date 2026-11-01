namespace HyperPlot.Tests;

/// <summary>Series extracted across the pool (design §5.5.1, §8.1, §10 "系列ごとの並列（S1）").</summary>
[Collection("Large data")]
public sealed class M4BatchTests
{
    private const int Columns = 480;
    private const int Building = 2;

    /// <summary>
    /// Every series of a batch gets the points M4.Extract gives it alone, for both X kinds, both gap modes, and 1, 4, and 16
    /// series.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 16 Y columns of 70,001 seeded floats with sparse NaN, their pyramids built; X evenly spaced and stored (with
    ///   statistics); 480 pixel columns over the whole column.
    ///
    /// Steps:
    /// - For each count, X kind, and gap mode: extract a batch on a pool of 3 threads, and each series alone.
    ///
    /// Expected:
    /// - Equal results (count, provisional) and equal points, series by series; no series failed.
    /// </remarks>
    [Fact]
    public void Extract_Series_EqualsTheSeriesAlone()
    {
        using var pool = new PlotWorkerPool(3);
        using var fixture = new Fixture(pool, 16);
        foreach (var count in new[] { 1, 4, 16 })
        {
            foreach (var stored in new[] { false, true })
            {
                foreach (var gaps in new[] { M4Gaps.Break, M4Gaps.Connect })
                {
                    var batch = new M4Batch();
                    var outputs = new M4Point[count][];
                    for (var s = 0; s < count; s++)
                    {
                        outputs[s] = new M4Point[M4.MaxPointCount(Columns)];
                        _ = stored
                            ? batch.Add(fixture.StoredX, fixture.Pyramids[s], outputs[s], new M4Options(gaps))
                            : batch.Add(fixture.UniformX, fixture.Pyramids[s], outputs[s], new M4Options(gaps));
                    }

                    pool.Extract(batch, default, fixture.Boundaries);

                    for (var s = 0; s < count; s++)
                    {
                        var alone = new M4Point[M4.MaxPointCount(Columns)];
                        var expected = stored
                            ? M4.Extract(fixture.StoredX, fixture.Pyramids[s], default, fixture.Boundaries, alone, new M4Options(gaps))
                            : M4.Extract(fixture.UniformX, fixture.Pyramids[s], default, fixture.Boundaries, alone, new M4Options(gaps));
                        var actual = batch.GetResult(s);
                        Assert.Null(batch.GetError(s));
                        Assert.Equal((expected.Count, expected.IsProvisional), (actual.Count, actual.IsProvisional));
                        Assert.Equal(alone.AsSpan(0, expected.Count).ToArray(), outputs[s].AsSpan(0, actual.Count).ToArray());
                    }
                }
            }
        }
    }

    /// <summary>
    /// Extracting a batch again allocates nothing (design §5.5.1, invariant 3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A batch of 16 series on a pool of 3 threads; one extraction to warm up.
    ///
    /// Steps:
    /// - Extract 20 times.
    ///
    /// Expected:
    /// - 0 bytes on this thread.
    /// </remarks>
    [Fact]
    public void Extract_SteadyState_AllocatesNothing()
    {
        using var pool = new PlotWorkerPool(3);
        using var fixture = new Fixture(pool, 16);
        var batch = fixture.Batch(16);
        pool.Extract(batch, default, fixture.Boundaries);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 20; i++)
        {
            pool.Extract(batch, default, fixture.Boundaries);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>
    /// With every worker held in a chunk build, the caller alone ends the batch (design §8.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 2 threads; built series; two more pyramids whose chunk 0 the storage holds, each taking one worker.
    ///
    /// Steps:
    /// - Wait until both workers are held; extract a batch of 8 series; release the chunks.
    ///
    /// Expected:
    /// - The extraction ends while both workers are still held, with the results of each series alone.
    /// </remarks>
    [Fact]
    public void Extract_WorkersBusy_CallerEndsTheBatch()
    {
        using var pool = new PlotWorkerPool(2);
        using var fixture = new Fixture(pool, 8);
        using var first = TestStorage<float>.MakeColumn(new float[PyramidData.Length], out var firstStorage);
        using var second = TestStorage<float>.MakeColumn(new float[PyramidData.Length], out var secondStorage);
        firstStorage.Hold(0);
        secondStorage.Hold(0);
        using var held1 = pool.BuildPyramid(first);
        using var held2 = pool.BuildPyramid(second);
        try
        {
            firstStorage.WaitForWorker(0);
            secondStorage.WaitForWorker(0);
            var batch = fixture.Batch(8);

            pool.Extract(batch, default, fixture.Boundaries);

            Assert.Equal((Building, Building), (held1.GetState(0), held2.GetState(0)));
            fixture.AssertEqualsAlone(batch);
        }
        finally
        {
            firstStorage.Release(0);
            secondStorage.Release(0);
        }
    }

    /// <summary>
    /// A worker takes the jobs of a frame before the chunks it has queued (design §8.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread with the caller kept from taking jobs; a pyramid of 3 chunks whose chunk 0 the storage holds;
    ///   the pool records what its worker takes.
    ///
    /// Steps:
    /// - Wait until the worker holds chunk 0; extract a batch of 4 series on another thread; release chunk 0; wait for the
    ///   extraction, the pyramid, and the worker's record of the last chunk (recorded after the chunk is built).
    ///
    /// Expected:
    /// - After chunk 0, the worker took the 4 frame jobs, then the chunks left; the results equal each series alone.
    /// </remarks>
    [Fact]
    public void Worker_FrameJobs_ComeBeforeChunks()
    {
        using var pool = new PlotWorkerPool(1) { CallerTakesJobs = false };
        using var fixture = new Fixture(pool, 4);
        var taken = new List<bool>();
        using var column = TestStorage<float>.MakeColumn(new float[PyramidData.Length], out var storage);
        storage.Hold(0);
        using var pyramid = pool.BuildPyramid(column);
        storage.WaitForWorker(0);
        pool.TestTook = frame =>
        {
            lock (taken)
            {
                taken.Add(frame);
            }
        };
        var batch = fixture.Batch(4);
        var extraction = Task.Run(() => pool.Extract(batch, default, fixture.Boundaries), TestContext.Current.CancellationToken);
        Eventually.True(() => pool.IsQueued(batch), "the batch to be in the workers' list");

        storage.Release(0);
        Eventually.Completes(extraction);
        Eventually.Completes(pyramid.Completion);

        // The worker records a job after it ran it, so the last chunk can complete the pyramid before its record is added.
        Eventually.True(
            () =>
            {
                lock (taken)
                {
                    return taken.Count >= 7;
                }
            },
            "the worker to record the last chunk"
        );
        pool.TestTook = null;
        lock (taken)
        {
            // The first entry is chunk 0, which the worker had taken before the batch came.
            Assert.Equal([false, true, true, true, true, false, false], taken);
        }

        fixture.AssertEqualsAlone(batch);
    }

    /// <summary>
    /// A series that throws fails alone; the smallest failed number's exception is thrown again (design §8.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A batch of 4 series where series 0 has a disposed pyramid and series 2 a destination too short.
    ///
    /// Steps:
    /// - Extract.
    ///
    /// Expected:
    /// - ObjectDisposedException (series 0's, not series 2's ArgumentException); GetError is non-null for 0 and 2 and null
    ///   for 1 and 3, which have results.
    /// </remarks>
    [Fact]
    public void Extract_SeriesThrow_KeepsTheOthersAndThrowsTheFirst()
    {
        using var pool = new PlotWorkerPool(3);
        using var fixture = new Fixture(pool, 4);
        using var lost = Column.Wrap(new float[1000]);
        var disposed = MinMaxPyramidTests.Build(pool, lost);
        disposed.Dispose();
        var batch = new M4Batch();
        _ = batch.Add(Column.Uniform(0, 1, 1000), disposed, new M4Point[M4.MaxPointCount(Columns)]);
        _ = batch.Add(fixture.UniformX, fixture.Pyramids[1], new M4Point[M4.MaxPointCount(Columns)]);
        _ = batch.Add(fixture.UniformX, fixture.Pyramids[2], new M4Point[3]);
        _ = batch.Add(fixture.UniformX, fixture.Pyramids[3], new M4Point[M4.MaxPointCount(Columns)]);
        var boundaries = Enumerable.Range(0, Columns + 1).Select(k => k * (999.0 / Columns)).ToArray();

        Assert.Throws<ObjectDisposedException>(() => pool.Extract(batch, default, boundaries));

        Assert.IsType<ObjectDisposedException>(batch.GetError(0));
        Assert.IsType<ArgumentException>(batch.GetError(2));
        Assert.Null(batch.GetError(1));
        Assert.Null(batch.GetError(3));
        Assert.True(batch.GetResult(1).Count > 0);
        Assert.True(batch.GetResult(3).Count > 0);
    }

    /// <summary>
    /// Batches extracted together each run over their own boundaries (row G6a): a plot's pairs of scales, side by side, give
    /// each series the points it gets alone; an empty batch is skipped; the first failing batch's error is thrown, after all
    /// have ended.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The fixture's 4 series; three batches: two series over the whole column, none, and two over its first half; then a
    ///   batch with a disposed pyramid before a good one.
    ///
    /// Steps:
    /// - Extract the three together on a pool of 3 threads; each series alone over its batch's boundaries. Then the failing
    ///   pair together.
    ///
    /// Expected:
    /// - Equal points series by series; then ObjectDisposedException, with the good batch's series extracted.
    /// </remarks>
    [Fact]
    public void ExtractTogether_EachBatchOverItsOwnBoundaries()
    {
        using var pool = new PlotWorkerPool(3);
        using var fixture = new Fixture(pool, 4);
        var half = fixture.Boundaries.Select(b => b / 2).ToArray();
        var outputs = new M4Point[4][];
        M4Batch[] batches = [new(), new(), new()];
        for (var s = 0; s < 4; s++)
        {
            outputs[s] = new M4Point[M4.MaxPointCount(Columns)];
            _ = batches[s < 2 ? 0 : 2].Add(fixture.UniformX, fixture.Pyramids[s], outputs[s]);
        }

        pool.ExtractTogether(batches, [default, default, default], [fixture.Boundaries, [], half], 3);
        for (var s = 0; s < 4; s++)
        {
            var alone = new M4Point[M4.MaxPointCount(Columns)];
            var expected = M4.Extract(fixture.UniformX, fixture.Pyramids[s], default, s < 2 ? fixture.Boundaries : half, alone);
            var actual = batches[s < 2 ? 0 : 2].GetResult(s % 2);
            Assert.Equal(expected.Count, actual.Count);
            Assert.Equal(alone.AsSpan(0, expected.Count).ToArray(), outputs[s].AsSpan(0, actual.Count).ToArray());
        }

        using var lost = Column.Wrap(new float[1000]);
        var disposed = MinMaxPyramidTests.Build(pool, lost);
        disposed.Dispose();
        M4Batch[] failing = [new(), new()];
        _ = failing[0].Add(Column.Uniform(0, 1, 1000), disposed, new M4Point[M4.MaxPointCount(Columns)]);
        _ = failing[1].Add(fixture.UniformX, fixture.Pyramids[0], new M4Point[M4.MaxPointCount(Columns)]);
        var boundaries = Enumerable.Range(0, Columns + 1).Select(k => k * (999.0 / Columns)).ToArray();
        _ = Assert.Throws<ObjectDisposedException>(() => pool.ExtractTogether(failing, [default, default], [boundaries, boundaries], 2));
        Assert.True(failing[1].GetResult(0).Count > 0);
    }

    /// <summary>
    /// Each extraction starts each series afresh: a series that failed and then succeeds has no error and its new result
    /// (design §5.5.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A batch of 1 series with a stored X whose statistics are computed.
    ///
    /// Steps:
    /// - Make X decrease at one element, invalidate it, and compute its statistics; extract. Put the element back,
    ///   invalidate, compute; extract.
    ///
    /// Expected:
    /// - The first extraction throws InvalidOperationException, with GetError non-null and the result default; the
    ///   second ends with GetError null and the result of the series alone.
    /// </remarks>
    [Fact]
    public void Extract_SeriesFailsThenSucceeds_ForgetsTheError()
    {
        using var pool = new PlotWorkerPool(2);
        using var fixture = new Fixture(pool, 1);
        var values = Enumerable.Range(0, PyramidData.Length).Select(i => (double)i).ToArray();
        using var x = Column.Wrap(values);
        var output = new M4Point[M4.MaxPointCount(Columns)];
        var batch = new M4Batch();
        _ = batch.Add(x, fixture.Pyramids[0], output);
        values[5] = -1;
        x.Invalidate(5, 1);
        Eventually.Completes(pool.PrecomputeStats(x));

        Assert.Throws<InvalidOperationException>(() => pool.Extract(batch, default, fixture.Boundaries));
        Assert.NotNull(batch.GetError(0));
        Assert.Equal(0, batch.GetResult(0).Count);

        values[5] = 5;
        x.Invalidate(5, 1);
        Eventually.Completes(pool.PrecomputeStats(x));
        pool.Extract(batch, default, fixture.Boundaries);

        Assert.Null(batch.GetError(0));
        var alone = new M4Point[M4.MaxPointCount(Columns)];
        var expected = M4.Extract(x, fixture.Pyramids[0], default, fixture.Boundaries, alone);
        Assert.Equal(expected.Count, batch.GetResult(0).Count);
        Assert.Equal(alone.AsSpan(0, expected.Count).ToArray(), output.AsSpan(0, expected.Count).ToArray());
    }

    /// <summary>
    /// Extract returns only after the last job ends, also when a worker holds it (design §8.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread with the caller kept from taking jobs; the worker held in a chunk read; a batch of 1 series.
    ///
    /// Steps:
    /// - Extract on another thread and spin up to 200 ms for it to end; release the chunk; wait for the extraction.
    ///
    /// Expected:
    /// - The extraction has not ended while the worker is held; afterwards the series has the result of the series alone.
    /// </remarks>
    [Fact]
    public void Extract_LastJobOnAWorker_WaitsForIt()
    {
        using var pool = new PlotWorkerPool(1) { CallerTakesJobs = false };
        using var fixture = new Fixture(pool, 1);
        using var column = TestStorage<float>.MakeColumn(new float[PyramidData.Length], out var storage);
        storage.Hold(0);
        using var pyramid = pool.BuildPyramid(column);
        storage.WaitForWorker(0);
        var batch = fixture.Batch(1);
        var extraction = Task.Run(() => pool.Extract(batch, default, fixture.Boundaries), TestContext.Current.CancellationToken);
        try
        {
            Eventually.True(() => pool.IsQueued(batch), "the batch to be in the workers' list");
            Assert.False(SpinWait.SpinUntil(() => extraction.IsCompleted, 200), "Extract returned while its job waited on a held worker.");
        }
        finally
        {
            storage.Release(0);
        }

        Eventually.Completes(extraction);
        fixture.AssertEqualsAlone(batch);
    }

    /// <summary>
    /// Arguments are checked once before any series runs; a worker cannot extract; batches on two threads at once each
    /// equal the series alone (design §5.5.1, §8.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 2 threads and two batches of 4 series each.
    ///
    /// Steps:
    /// - Extract null, with null boundaries, with one boundary, NaN, and decreasing boundaries; read a result and an error
    ///   out of range; add nulls; extract from a worker thread (through a storage read); extract the two batches on two
    ///   threads at once, 50 times.
    ///
    /// Expected:
    /// - ArgumentNullException, ArgumentException with no series run (no error, default results), ArgumentOutOfRangeException,
    ///   InvalidOperationException on the worker, and equal results from both threads.
    /// </remarks>
    [Fact]
    public void Arguments_WorkerAndTwoThreads_BehaveAsTheDesignSays()
    {
        using var pool = new PlotWorkerPool(2);
        using var fixture = new Fixture(pool, 8);
        var batch = fixture.Batch(4);
        Assert.Throws<ArgumentNullException>(() => pool.Extract(null!, default, fixture.Boundaries));
        Assert.Throws<ArgumentNullException>(() => pool.Extract(batch, default, null!));
        foreach (var bad in new[] { new[] { 0.0 }, new[] { 0.0, double.NaN }, new[] { 5.0, 1.0 } })
        {
            Assert.Throws<ArgumentException>(() => pool.Extract(batch, default, bad));
        }

        Assert.Equal(default, batch.GetResult(0).Count);
        Assert.Null(batch.GetError(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => batch.GetResult(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => batch.GetError(-1));
        Assert.Throws<ArgumentNullException>(() => batch.Add(fixture.UniformX, fixture.Pyramids[0], null!));

        Exception? onWorker = null;
        using var column = TestStorage<float>.MakeColumn(new float[10], out var storage);
        storage.OnWorkerRead = () => onWorker = Record.Exception(() => pool.Extract(batch, default, fixture.Boundaries));
        using (var pyramid = pool.BuildPyramid(column))
        {
            Eventually.Completes(pyramid.Completion);
        }

        Assert.IsType<InvalidOperationException>(onWorker);

        var other = fixture.Batch(4, offset: 4);
        for (var round = 0; round < 50; round++)
        {
            var one = Task.Run(() => pool.Extract(batch, default, fixture.Boundaries), TestContext.Current.CancellationToken);
            var two = Task.Run(() => pool.Extract(other, default, fixture.Boundaries), TestContext.Current.CancellationToken);
            Eventually.Completes(one);
            Eventually.Completes(two);
        }

        fixture.AssertEqualsAlone(batch);
        fixture.AssertEqualsAlone(other);
    }

    /// <summary>
    /// A pool disposed while it extracts still ends every series; the next extraction throws (design §8.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 2 threads and a batch of 16 series.
    ///
    /// Steps:
    /// - Extract on one thread and dispose the pool on another at once, 10 times with a new pool each; extract once more.
    ///
    /// Expected:
    /// - Each extraction either ends with the results of the series alone, or, when the dispose came first, throws
    ///   ObjectDisposedException before any series ran; the extraction after the dispose throws ObjectDisposedException.
    /// </remarks>
    [Fact]
    public void Dispose_DuringExtract_EndsEverySeries()
    {
        for (var round = 0; round < 10; round++)
        {
            var pool = new PlotWorkerPool(2);
            using var fixture = new Fixture(pool, 16);
            var batch = fixture.Batch(16);
            var extraction = Task.Run(() => pool.Extract(batch, default, fixture.Boundaries), TestContext.Current.CancellationToken);
            var dispose = Task.Run(pool.Dispose, TestContext.Current.CancellationToken);
            Eventually.Completes(dispose);

            // A dispose that came first makes the extraction throw before any series ran; otherwise every series ended.
            Eventually.True(() => extraction.IsCompleted, "the extraction to end");
            if (extraction.IsFaulted)
            {
                Assert.IsType<ObjectDisposedException>(extraction.Exception!.InnerException);
                Assert.Equal(0, batch.GetResult(0).Count);
            }
            else
            {
                fixture.AssertEqualsAlone(batch);
            }

            Assert.Throws<ObjectDisposedException>(() => pool.Extract(batch, default, fixture.Boundaries));
        }
    }

    /// <summary>Y columns with built pyramids, both X kinds, and boundaries over the whole column.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly List<Column<float>> _columns = [];
        private readonly Dictionary<M4Batch, (int Offset, M4Point[][] Outputs)> _batches = [];

        public Fixture(PlotWorkerPool pool, int series)
        {
            for (var s = 0; s < series; s++)
            {
                var column = Column.Wrap(PyramidData.Convert<float>(PyramidData.Values(PyramidData.Length, "Sparse", 100 + s, integral: false)));
                _columns.Add(column);
                Pyramids.Add(MinMaxPyramidTests.Build(pool, column));
            }

            UniformX = Column.Uniform(0, 1, PyramidData.Length);
            StoredX = Column.Wrap(Enumerable.Range(0, PyramidData.Length).Select(i => (double)i).ToArray());
            Eventually.Completes(pool.PrecomputeStats(StoredX));
            Boundaries = [.. Enumerable.Range(0, Columns + 1).Select(k => k * ((PyramidData.Length - 1.0) / Columns))];
        }

        public List<MinMaxPyramid<float>> Pyramids { get; } = [];

        public UniformColumn UniformX { get; }

        public Column<double> StoredX { get; }

        public double[] Boundaries { get; }

        public M4Batch Batch(int count, int offset = 0)
        {
            var batch = new M4Batch();
            var outputs = new M4Point[count][];
            for (var s = 0; s < count; s++)
            {
                outputs[s] = new M4Point[M4.MaxPointCount(Columns)];
                _ = batch.Add(UniformX, Pyramids[offset + s], outputs[s]);
            }

            _batches[batch] = (offset, outputs);
            return batch;
        }

        public void AssertEqualsAlone(M4Batch batch)
        {
            var (offset, outputs) = _batches[batch];
            for (var s = 0; s < batch.Count; s++)
            {
                Assert.Null(batch.GetError(s));
                var alone = new M4Point[M4.MaxPointCount(Columns)];
                var expected = M4.Extract(UniformX, Pyramids[offset + s], default, Boundaries, alone);
                var actual = batch.GetResult(s);
                Assert.Equal((expected.Count, expected.IsProvisional), (actual.Count, actual.IsProvisional));
                Assert.Equal(alone.AsSpan(0, expected.Count).ToArray(), outputs[s].AsSpan(0, actual.Count).ToArray());
            }
        }

        public void Dispose()
        {
            foreach (var pyramid in Pyramids)
            {
                pyramid.Dispose();
            }

            foreach (var column in _columns)
            {
                column.Dispose();
            }

            StoredX.Dispose();
        }
    }
}
