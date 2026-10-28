namespace HyperPlot.Tests;

/// <summary>
/// The worker pool and the chunk states of design §3.3. Each test owns a pool whose thread it can park on a held chunk of a
/// "blocker" pyramid, so that the states of the pyramid under test stay where the test put them.
/// </summary>
[Collection("Large data")]
public sealed class PlotWorkerPoolTests
{
    private const int Unbuilt = 0;
    private const int Requested = 1;
    private const int Building = 2;
    private const int Done = 3;
    private const int Failed = 4;

    /// <summary>
    /// While a chunk is not built, a query answers from its statistics and marks the result provisional; after Completion the
    /// same query is exact. A requested chunk is built before the chunks before it in position order.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread, parked on chunk 0 of the pyramid under test (3 chunks of seeded floats).
    ///
    /// Steps:
    /// - Query a part of chunk 2 and extract M4 over it with Break and with Connect; release chunk 0; wait for Completion;
    ///   query again.
    ///
    /// Expected:
    /// - Before: chunk 1 unbuilt, chunk 2 requested; with no statistics of chunk 2 yet, the range is provisional with Count 0
    ///   and HasNaN false, and the query did not compute them; M4 is provisional and writes only First and Last in each column
    ///   (and Before and After), and with Connect it uses the boundary elements: Before at the element before the view, then
    ///   10 first-last pairs, then After. None of it read a raw value of the pyramid's chunks.
    /// - After: the pool built the chunks in the order 0, 2, 1; the range equals the loop's and is not provisional; IsComplete.
    /// </remarks>
    [Fact]
    public void GetMinMax_ChunkNotBuilt_IsProvisionalUntilBuiltAndRequestedChunkGoesFirst()
    {
        var values = PyramidData.Convert<float>(PyramidData.Values(PyramidData.Length, "None", 3, integral: false));
        using var pool = new PlotWorkerPool(1);
        using var column = TestStorage<float>.MakeColumn(values, out var storage);
        storage.Hold(0);
        using var pyramid = pool.BuildPyramid(column);
        storage.WaitForWorker(0);
        var start = (2L * Column.ChunkLength) + 100;

        var rawBefore = pyramid.RawElementsRead;
        var before = pyramid.GetMinMax(start, 1000);
        var points = new M4Point[M4.MaxPointCount(10)];
        var x = Column.Uniform(0, 1, values.Length);
        var boundaries = Enumerable.Range(0, 11).Select(k => (double)(start + (k * 100))).ToArray();
        var provisional = M4.Extract(x, pyramid, boundaries, points);
        var roles = points[..provisional.Count].Aggregate(M4Roles.None, (all, p) => all | p.Roles);
        var connected = M4.Extract(x, pyramid, boundaries, points, new M4Options(M4Gaps.Connect));
        var connectedPoints = points[..connected.Count].Select(p => (p.Index, p.Roles)).ToArray();
        var rawRead = pyramid.RawElementsRead - rawBefore;

        Assert.Equal((Unbuilt, Requested), (pyramid.GetState(1), pyramid.GetState(2)));
        Assert.Equal((true, 0L, false), (before.IsProvisional, before.Count, before.HasNaN));
        Assert.False(column.TryGetStoredStats(2, out _));
        Assert.True(provisional.IsProvisional);
        Assert.Equal(M4Roles.Before | M4Roles.First | M4Roles.Last | M4Roles.After, roles);
        Assert.True(connected.IsProvisional);
        Assert.Equal(start - 1, connectedPoints[0].Index);
        Assert.Equal(22, connectedPoints.Length);
        Assert.Equal(0, rawRead);

        storage.Release(0);
        Eventually.Completes(pyramid.Completion);
        var after = pyramid.GetMinMax(start, 1000);
        var expected = Reference.MinMax(PyramidData.AsStored(values), start, start + 1000);

        Assert.Equal([0, 2, 1], storage.WorkerOrder);
        Assert.False(after.IsProvisional);
        Assert.Equal((expected.Min, expected.Max), ((double)after.Min, (double)after.Max));
        Assert.True(pyramid.IsComplete);
        Assert.False(M4.Extract(x, pyramid, boundaries, points).IsProvisional);
    }

    /// <summary>
    /// When the caller changed values and has not called Invalidate yet, the block disagrees with the raw values; the query and
    /// the search for a value that is not NaN do not trust the block and answer provisionally.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A built pyramid over one chunk: element 10 is the minimum (-5) and element 20 is NaN, so bucket [0, 64) is partly NaN.
    ///
    /// Steps:
    /// - Without Invalidate, raise element 10 to 1 and make elements 0 to 63 NaN; query [0, 1000); search [0, 200) for the first
    ///   element that is not NaN.
    ///
    /// Expected:
    /// - The query is provisional (the block's minimum -5 is no longer in the raw values); the search does not scan the raw
    ///   values to make up for the block: it answers Unknown and is provisional.
    /// </remarks>
    [Fact]
    public void Query_ValuesChangedWithoutInvalidate_AnswersProvisionally()
    {
        var values = Enumerable.Range(0, 5000).Select(i => (float)(i % 7)).ToArray();
        values[10] = -5;
        values[20] = float.NaN;
        using var pool = new PlotWorkerPool(1);
        using var column = Column.Wrap(values);
        using var pyramid = MinMaxPyramidTests.Build(pool, column);
        Assert.Equal((-5f, false), (pyramid.GetMinMax(0, 1000).Min, pyramid.GetMinMax(0, 1000).IsProvisional));

        values[10] = 1;
        values.AsSpan(0, 64).Fill(float.NaN);
        var range = pyramid.GetMinMax(0, 1000);
        var provisional = false;
        var first = pyramid.FindNumber(0, 200, fromEnd: false, ref provisional);

        Assert.True(range.IsProvisional);
        Assert.Equal((MinMaxPyramid<float>.Unknown, true), (first, provisional));
    }

    /// <summary>
    /// A chunk requested in one pyramid is built before the unbuilt chunks of another pyramid on the same pool.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread parked on a blocker pyramid; pyramid A (3 chunks) whose chunk 0 is held, then pyramid B (3 chunks).
    ///
    /// Steps:
    /// - Query a part of B's chunk 2; release the blocker; wait until B's chunk 2 is done; release A's chunk 0.
    ///
    /// Expected:
    /// - B's chunk 2 is done while A's chunk 0 is not: the worker took the request before A's work, which comes first in
    ///   registration order. Taking A first would hold the worker on A's chunk 0 and B's chunk 2 would never be done.
    /// </remarks>
    [Fact]
    public void RunOne_RequestInAnotherPyramid_GoesBeforeUnbuiltChunks()
    {
        using var pool = new PlotWorkerPool(1);
        using var blockerColumn = TestStorage<float>.MakeColumn(new float[10], out var blocker);
        blocker.Hold(0);
        using var blockerPyramid = pool.BuildPyramid(blockerColumn);
        blocker.WaitForWorker(0);
        using var columnA = TestStorage<float>.MakeColumn(new float[PyramidData.Length], out var storageA);
        storageA.Hold(0);
        using var pyramidA = pool.BuildPyramid(columnA);
        using var columnB = Column.Wrap(new float[PyramidData.Length]);
        using var pyramidB = pool.BuildPyramid(columnB);

        _ = pyramidB.GetMinMax((2L * Column.ChunkLength) + 10, 100);
        blocker.Release(0);
        try
        {
            Eventually.True(() => pyramidB.GetState(2) == Done, "B's requested chunk");
            Assert.NotEqual(Done, pyramidA.GetState(0));
        }
        finally
        {
            storageA.Release(0);
        }

        Eventually.Completes(pyramidA.Completion);
    }

    /// <summary>
    /// An Invalidate that comes while a worker builds the chunk makes the worker put the chunk back and build it again (rule 2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread; 3 chunks of zeros; chunk 1 held while the worker reads it.
    ///
    /// Steps:
    /// - While chunk 1 is building, write 7 into it and invalidate it; release it; wait for Completion and for IsComplete.
    ///
    /// Expected:
    /// - Chunk 1 is building and the pyramid is not complete while held; the worker read chunk 1 twice; afterwards the pyramid
    ///   is complete and the range over chunk 1 has Max 7, not provisional.
    /// </remarks>
    [Fact]
    public void Build_InvalidateDuringBuild_BuildsTheChunkAgain()
    {
        var values = new float[PyramidData.Length];
        using var pool = new PlotWorkerPool(1);
        using var column = TestStorage<float>.MakeColumn(values, out var storage);
        storage.Hold(1);
        using var pyramid = pool.BuildPyramid(column);
        storage.WaitForWorker(1);

        Assert.Equal(Building, pyramid.GetState(1));
        Assert.False(pyramid.IsComplete);
        values[Column.ChunkLength + 5] = 7;
        column.Invalidate(Column.ChunkLength + 5, 1);
        storage.Release(1);

        Eventually.Completes(pyramid.Completion);
        Eventually.True(() => pyramid.IsComplete, "the pyramid to be complete");
        var range = pyramid.GetMinMax(Column.ChunkLength, 1000);
        Assert.True(storage.WorkerReads(1) >= 4, "The stats pass and the pyramid pass read chunk 1 in each of the two builds.");
        Assert.Equal((7f, false), (range.Max, range.IsProvisional));
    }

    /// <summary>
    /// A chunk whose build throws is failed: Fault and Completion carry the exception, a query does not queue it again, and it is
    /// built again only after its generation moves (rule 3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread; 3 chunks of seeded floats; the storage throws for chunk 1 on the worker.
    ///
    /// Steps:
    /// - Wait for Completion. Park the worker on a blocker pyramid, query chunk 1, and read its state.
    /// - Stop the failure, invalidate chunk 1, query it, read the state; release the blocker and wait for IsComplete.
    ///
    /// Expected:
    /// - Completion faults with the IOException, which is also Fault; chunk 1 stays failed after the query, which is provisional.
    /// - After the Invalidate the query requests chunk 1; it is then built, and the query is exact.
    /// </remarks>
    [Fact]
    public void Build_ChunkThrows_FailsUntilItsGenerationMoves()
    {
        var values = PyramidData.Convert<float>(PyramidData.Values(PyramidData.Length, "None", 5, integral: false));
        using var pool = new PlotWorkerPool(1);
        using var column = TestStorage<float>.MakeColumn(values, out var storage);
        storage.FailOn(1, fail: true);
        using var pyramid = pool.BuildPyramid(column);

        var fault = Eventually.Faults(pyramid.Completion);
        Assert.IsType<IOException>(fault);
        Assert.Same(fault, pyramid.Fault);
        Assert.Equal(Failed, pyramid.GetState(1));

        using var blockerColumn = TestStorage<float>.MakeColumn(new float[10], out var blocker);
        blocker.Hold(0);
        using var blockerPyramid = pool.BuildPyramid(blockerColumn);
        blocker.WaitForWorker(0);
        var start = (long)Column.ChunkLength + 10;
        Assert.True(pyramid.GetMinMax(start, 100).IsProvisional);
        Assert.Equal(Failed, pyramid.GetState(1));

        storage.FailOn(1, fail: false);
        column.Invalidate(start, 1);
        _ = pyramid.GetMinMax(start, 100);
        Assert.Equal(Requested, pyramid.GetState(1));
        blocker.Release(0);

        Eventually.True(() => pyramid.IsComplete, "chunk 1 to be built again");
        var expected = Reference.MinMax(PyramidData.AsStored(values), start, start + 100);
        var range = pyramid.GetMinMax(start, 100);
        Assert.Equal((expected.Max, false), ((double)range.Max, range.IsProvisional));
    }

    /// <summary>
    /// A built chunk whose generation moves is requested again by the next query, and built again (done → requested).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread; a built pyramid over 3 chunks of zeros; the worker parked on a blocker pyramid.
    ///
    /// Steps:
    /// - Write 3 into chunk 0 and invalidate it; query a part of chunk 0; read its state; release the blocker.
    ///
    /// Expected:
    /// - The query is provisional and leaves chunk 0 requested; after the build, the query has Max 3 and is not provisional.
    /// </remarks>
    [Fact]
    public void GetMinMax_BuiltChunkInvalidated_RequestsItAgain()
    {
        var values = new float[PyramidData.Length];
        using var pool = new PlotWorkerPool(1);
        using var column = Column.Wrap(values);
        using var pyramid = MinMaxPyramidTests.Build(pool, column);
        using var blockerColumn = TestStorage<float>.MakeColumn(new float[10], out var blocker);
        blocker.Hold(0);
        using var blockerPyramid = pool.BuildPyramid(blockerColumn);
        blocker.WaitForWorker(0);

        values[50] = 3;
        column.Invalidate(50, 1);
        var stale = pyramid.GetMinMax(0, 1000);

        Assert.True(stale.IsProvisional);
        Assert.Equal(Requested, pyramid.GetState(0));
        blocker.Release(0);
        Eventually.True(() => pyramid.IsComplete, "chunk 0 to be built again");
        var range = pyramid.GetMinMax(0, 1000);
        Assert.Equal((3f, false), (range.Max, range.IsProvisional));
    }

    /// <summary>
    /// Disposing a pyramid while a worker builds one of its chunks waits for that chunk, then frees the blocks.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread; a pyramid over 3 chunks whose chunk 0 is held while the worker reads it.
    ///
    /// Steps:
    /// - Dispose the pyramid on a thread of the test; check that it has not returned; release chunk 0; join the thread.
    ///
    /// Expected:
    /// - Dispose returns only after the release; Completion ends with ObjectDisposedException; a query throws it too.
    /// </remarks>
    [Fact]
    public void Dispose_WhileAChunkBuilds_WaitsForThatChunk()
    {
        using var pool = new PlotWorkerPool(1);
        using var column = TestStorage<float>.MakeColumn(new float[PyramidData.Length], out var storage);
        storage.Hold(0);
        var pyramid = pool.BuildPyramid(column);
        storage.WaitForWorker(0);

        var disposer = new Thread(pyramid.Dispose);
        disposer.Start();
        Assert.False(disposer.Join(0));
        storage.Release(0);
        Assert.True(disposer.Join(30_000));

        Assert.IsType<ObjectDisposedException>(Eventually.Faults(pyramid.Completion));
        Assert.Throws<ObjectDisposedException>(() => pyramid.GetMinMax(0, 1));
    }

    /// <summary>
    /// Disposing the pool lets the worker finish its chunk, joins it, and gives every pyramid of the pool ObjectDisposedException
    /// as its fault, since nothing is built again after it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread; a small pyramid built to completion; a pyramid over 3 chunks whose chunk 0 is held while the
    ///   worker reads it.
    ///
    /// Steps:
    /// - Dispose the pool on a thread of the test; release chunk 0; join; build another pyramid on the pool.
    ///
    /// Expected:
    /// - Chunk 0 is done, chunks 1 and 2 unbuilt; Fault and Completion carry ObjectDisposedException; BuildPyramid throws it;
    ///   the built pyramid's Fault is ObjectDisposedException too, while its Completion stays successful; the shared pool's
    ///   Dispose does nothing.
    /// </remarks>
    [Fact]
    public void Dispose_Pool_FinishesTheChunkAndFaultsWhatIsLeft()
    {
        var pool = new PlotWorkerPool(1);
        using var small = Column.Wrap(new float[100]);
        using var built = MinMaxPyramidTests.Build(pool, small);
        using var column = TestStorage<float>.MakeColumn(new float[PyramidData.Length], out var storage);
        storage.Hold(0);
        using var pyramid = pool.BuildPyramid(column);
        storage.WaitForWorker(0);

        var disposer = new Thread(pool.Dispose);
        disposer.Start();
        storage.Release(0);
        Assert.True(disposer.Join(30_000));

        Assert.Equal((Done, Unbuilt, Unbuilt), (pyramid.GetState(0), pyramid.GetState(1), pyramid.GetState(2)));
        Assert.IsType<ObjectDisposedException>(pyramid.Fault);
        Assert.IsType<ObjectDisposedException>(Eventually.Faults(pyramid.Completion));
        Assert.Throws<ObjectDisposedException>(() => pool.BuildPyramid(column));
        Assert.IsType<ObjectDisposedException>(built.Fault);
        Assert.True(built.Completion.IsCompletedSuccessfully);
        PlotWorkerPool.Shared.Dispose();
        Assert.True(PlotWorkerPool.Shared.ThreadCount >= 1);
        using var shared = PlotWorkerPool.Shared.BuildPyramid(Column.Wrap(new float[3]));
        Eventually.Completes(shared.Completion);
    }

    /// <summary>
    /// Until a stored X column's statistics are computed, M4 does not compute them on the calling thread: it finds the boundaries
    /// by a search over the elements and marks the result provisional; after PrecomputeStats the same extraction is not.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread; a built pyramid over two chunks and 70,001 floats; a stored double X whose chunk 1 is held when
    ///   the worker reads it.
    ///
    /// Steps:
    /// - PrecomputeStats on X; while chunk 1 is held, extract M4 over 500 columns; release; wait for the task; extract again.
    ///
    /// Expected:
    /// - The first extraction is provisional, its points equal the loop's, and the X statistics of chunk 1 are still missing;
    ///   the second is not provisional and gives the same points; after an Invalidate of X, the next is provisional again.
    /// </remarks>
    [Fact]
    public void Extract_XStatisticsMissing_IsProvisionalUntilPrecomputed()
    {
        var values = PyramidData.Convert<float>(PyramidData.Values(PyramidData.Length, "Sparse", 13, integral: false));
        var xValues = Enumerable.Range(0, values.Length).Select(i => i * 0.5).ToArray();
        using var pool = new PlotWorkerPool(1);
        using var column = Column.Wrap(values);
        using var pyramid = MinMaxPyramidTests.Build(pool, column);
        using var x = TestStorage<double>.MakeColumn(xValues, out var storage);
        storage.Hold(1);
        var stats = pool.PrecomputeStats(x);
        storage.WaitForWorker(1);
        var boundaries = Enumerable.Range(0, 501).Select(k => k * (xValues[^1] / 500)).ToArray();
        var points = new M4Point[M4.MaxPointCount(500)];
        var expected = Reference.M4(xValues, PyramidData.AsStored(values), boundaries, M4Gaps.Break);

        var early = M4.Extract(x, pyramid, boundaries, points);
        var earlyPoints = points[..early.Count].Select(p => new Reference.Point(p.Index, p.X, p.Y, p.Roles, p.BreakBefore)).ToArray();
        var missing = !x.TryGetStoredStats(1, out _);
        storage.Release(1);
        Eventually.Completes(stats);
        var late = M4.Extract(x, pyramid, boundaries, points);

        Assert.True(early.IsProvisional);
        Assert.True(missing);
        Assert.Equal(expected, earlyPoints);
        Assert.False(late.IsProvisional);
        Assert.Equal(expected, points[..late.Count].Select(p => new Reference.Point(p.Index, p.X, p.Y, p.Roles, p.BreakBefore)));
        x.Invalidate(5, 1);
        Assert.True(M4.Extract(x, pyramid, boundaries, points).IsProvisional);
    }

    /// <summary>
    /// PrecomputeStats ends at once for an empty column, faults with a chunk's exception, and faults with ObjectDisposedException
    /// when the pool is disposed first.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread; an empty column; a column of 3 chunks whose chunk 1 throws on the worker; a column of 3 chunks
    ///   whose chunk 0 is held.
    ///
    /// Steps:
    /// - PrecomputeStats on each; dispose the pool on a thread of the test while chunk 0 of the last column is held; release it.
    ///
    /// Expected:
    /// - The empty column's task has ended; the failing column's task faults with the IOException; the last one faults with
    ///   ObjectDisposedException.
    /// </remarks>
    [Fact]
    public void PrecomputeStats_EmptyFailingOrDisposed_EndsAsTheDesignSays()
    {
        var pool = new PlotWorkerPool(1);
        using var empty = Column.Wrap(Array.Empty<float>());
        Assert.True(pool.PrecomputeStats(empty).IsCompletedSuccessfully);
        using var failing = TestStorage<float>.MakeColumn(new float[PyramidData.Length], out var failingStorage);
        failingStorage.FailOn(1, fail: true);
        Assert.IsType<IOException>(Eventually.Faults(pool.PrecomputeStats(failing)));

        using var held = TestStorage<float>.MakeColumn(new float[PyramidData.Length], out var heldStorage);
        heldStorage.Hold(0);
        var task = pool.PrecomputeStats(held);
        heldStorage.WaitForWorker(0);
        var disposer = new Thread(pool.Dispose);
        disposer.Start();
        heldStorage.Release(0);
        Assert.True(disposer.Join(30_000));

        Assert.IsType<ObjectDisposedException>(Eventually.Faults(task));
    }

    /// <summary>
    /// The pool refuses a thread count below 1.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - None.
    ///
    /// Steps:
    /// - Make pools of 0 and -1 threads.
    ///
    /// Expected:
    /// - ArgumentOutOfRangeException for both.
    /// </remarks>
    [Fact]
    public void Constructor_NoThreads_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlotWorkerPool(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlotWorkerPool(-1));
    }

    /// <summary>
    /// A reader that finds the sequence odd (a worker is writing the block) answers provisionally instead of reading the block.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread; a built pyramid over one chunk of pattern A (in [0, 1)); a worker hook that holds the worker after
    ///   it writes a block, before the sequence goes even.
    ///
    /// Steps:
    /// - Query a range of buckets. Between the reader's check of the state and its read of the sequence, a reader hook writes
    ///   pattern B (in [10, 11)), invalidates, requests the chunk, and waits until the worker has written B's block and is held.
    /// - Release the worker.
    ///
    /// Expected:
    /// - The answer is provisional: the reader saw an odd sequence. Reading on would see B's block under A's generation check.
    /// </remarks>
    [Fact]
    public void GetMinMax_SequenceOddAtRead_IsProvisional() => OverlapARebuild(beforeSequenceRead: true);

    /// <summary>
    /// A reader whose block was rewritten after its first read of the sequence finds the sequence changed and answers provisionally.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - As in GetMinMax_SequenceOddAtRead_IsProvisional.
    ///
    /// Steps:
    /// - Query a range of buckets. After the reader's first read of the sequence (even), a reader hook writes pattern B,
    ///   invalidates, requests the chunk, and waits until the worker has written B's block and is held. Release the worker.
    ///
    /// Expected:
    /// - The answer is provisional: the second read of the sequence differs from the first.
    /// </remarks>
    [Fact]
    public void GetMinMax_SequenceChangesDuringRead_IsProvisional() => OverlapARebuild(beforeSequenceRead: false);

    /// <summary>
    /// A query that overlaps a rebuild reads either a provisional answer or the answer of one whole build, never a mix (seqlock).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread; one chunk whose values are pattern A (in [0, 1)) or pattern B (in [10, 11)).
    ///
    /// Steps:
    /// - A writer thread switches the chunk between A and B 300 times; each time it writes all values, invalidates, and waits
    ///   until the pyramid is complete again, so that no build reads values being written.
    /// - Three reader threads query a range aligned to 4096 elements (buckets only, no raw values) as fast as they can.
    ///
    /// Expected:
    /// - Every answer is provisional, or equals A's or B's exact range; none mixes the two.
    /// </remarks>
    [Fact]
    public void GetMinMax_DuringRebuilds_NeverReadsAHalfWrittenBlock()
    {
        var random = new Random(43);
        var patternA = Enumerable.Range(0, Column.ChunkLength).Select(_ => (float)random.NextDouble()).ToArray();
        var patternB = patternA.Select(v => v + 10).ToArray();
        var values = patternA.ToArray();
        const long start = 4096;
        const long length = 4096 * 200;
        var a = Reference.MinMax(PyramidData.AsStored(patternA), start, start + length);
        var b = Reference.MinMax(PyramidData.AsStored(patternB), start, start + length);
        using var pool = new PlotWorkerPool(1);
        using var column = Column.Wrap(values);
        using var pyramid = MinMaxPyramidTests.Build(pool, column);
        var stop = 0;
        var mixed = 0;
        var exact = 0;

        var readers = Enumerable
            .Range(0, 3)
            .Select(_ => new Thread(() =>
            {
                while (Volatile.Read(ref stop) == 0)
                {
                    var range = pyramid.GetMinMax(start, length);
                    if (range.IsProvisional)
                    {
                        continue;
                    }

                    var pair = ((double)range.Min, (double)range.Max);
                    if (pair == (a.Min, a.Max) || pair == (b.Min, b.Max))
                    {
                        Interlocked.Increment(ref exact);
                    }
                    else
                    {
                        Interlocked.Increment(ref mixed);
                    }
                }
            }))
            .ToArray();
        foreach (var reader in readers)
        {
            reader.Start();
        }

        for (var round = 0; round < 300; round++)
        {
            (round % 2 == 0 ? patternB : patternA).CopyTo(values, 0);
            column.Invalidate();
            Eventually.True(
                () =>
                {
                    _ = pyramid.GetMinMax(start, length);
                    return pyramid.IsComplete;
                },
                "the rebuild"
            );
        }

        Volatile.Write(ref stop, 1);
        foreach (var reader in readers)
        {
            Assert.True(reader.Join(30_000));
        }

        Assert.Equal(0, mixed);
        Assert.True(exact > 0, "The readers saw no exact answer, so the test checked nothing.");
    }

    private static void OverlapARebuild(bool beforeSequenceRead)
    {
        var random = new Random(47);
        var values = Enumerable.Range(0, Column.ChunkLength).Select(_ => (float)random.NextDouble()).ToArray();
        var patternB = values.Select(v => v + 10).ToArray();
        using var pool = new PlotWorkerPool(1);
        using var column = Column.Wrap(values);
        using var pyramid = MinMaxPyramidTests.Build(pool, column);
        using var written = new ManualResetEventSlim(initialState: false);
        using var hold = new ManualResetEventSlim(initialState: false);
        pyramid.TestBlockWritten = () =>
        {
            written.Set();
            Assert.True(hold.Wait(30_000), "The test did not release the worker.");
        };
        void Rebuild()
        {
            pyramid.TestBeforeSequenceRead = null;
            pyramid.TestAfterSequenceRead = null;
            patternB.CopyTo(values, 0);
            column.Invalidate();
            pyramid.RequestAgain(0);
            Assert.True(written.Wait(30_000), "The worker did not write the block.");
        }

        if (beforeSequenceRead)
        {
            pyramid.TestBeforeSequenceRead = Rebuild;
        }
        else
        {
            pyramid.TestAfterSequenceRead = Rebuild;
        }

        MinMax<float> range;
        try
        {
            range = pyramid.GetMinMax(4096, 4096 * 200);
        }
        finally
        {
            hold.Set();
        }

        Assert.True(written.IsSet, "The hook did not run, so the test checked nothing.");
        Assert.True(range.IsProvisional);
        pyramid.TestBlockWritten = null;
    }
}
