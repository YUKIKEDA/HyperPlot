using System.Numerics;

namespace HyperPlot;

/// <summary>
/// A fixed set of background threads that build pyramids and compute column statistics ahead, chunk by chunk, and extract
/// the series of an <see cref="M4Batch"/> within a frame (design §8.1).
/// Use <see cref="Shared"/>, or make a pool of your own and dispose it when done.
/// </summary>
public sealed class PlotWorkerPool : IDisposable
{
    private static readonly Lazy<PlotWorkerPool> _shared = new(
        () => new PlotWorkerPool(Math.Max(1, Environment.ProcessorCount - 1), isShared: true, WarmsGate),
        LazyThreadSafetyMode.ExecutionAndPublication
    );

    private readonly Thread[] _threads;
    private readonly ManualResetEventSlim _wake = new(initialState: false);
    private readonly Lock _gate = new();
    private readonly bool _isShared;

    [ThreadStatic]
    private static bool _isWorkerThread;

    private PoolWork[] _works = [];
    private M4Batch? _batches;
    private SpatialSnapshot? _freeList;
    private volatile bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="PlotWorkerPool"/> class and starts its threads.</summary>
    /// <param name="threadCount">The number of threads, at least 1.</param>
    public PlotWorkerPool(int threadCount)
        : this(threadCount, isShared: false, WarmsGate) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="PlotWorkerPool"/> class that makes its lock's wait event when it is made, or
    /// not (Issue #304). Tests only: the test of the warm-up makes a pool of each kind.
    /// </summary>
    internal PlotWorkerPool(int threadCount, bool warmGate)
        : this(threadCount, isShared: false, warmGate) { }

    private PlotWorkerPool(int threadCount, bool isShared, bool warmGate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threadCount);
        _isShared = isShared;
        _threads = new Thread[threadCount];
        for (var i = 0; i < threadCount; i++)
        {
            _threads[i] = new Thread(Run) { IsBackground = true, Name = $"HyperPlot worker {i}" };
        }

        var started = 0;
        try
        {
            foreach (var thread in _threads)
            {
                thread.Start();
                started++;
            }
        }
        catch
        {
            // Stop the threads that did start, so that none is left waiting for work that never comes.
            _disposed = true;
            _wake.Set();
            for (var i = 0; i < started; i++)
            {
                _threads[i].Join();
            }

            throw;
        }

        if (warmGate)
        {
            // This thread waits on the lock while another holds it: the runtime makes the lock's wait event now.
            var holder = HoldGate(TimeSpan.FromMilliseconds(20));
            lock (_gate) { }

            holder.Join();
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether a new pool waits once on its lock while another thread holds it (Issue #304).
    /// Tests only: the runtime makes a lock's wait event the first time a thread waits on it (56 bytes, and 240 the first
    /// time in a process, under the JIT), so a Zero-Alloc test whose caller first waits on the pool's lock inside its window
    /// would count it. The test assemblies turn it on in a module initializer.
    /// </summary>
    internal static bool WarmsGate { get; set; }

    /// <summary>
    /// Gets the pool that every plot shares, made when first read with <see cref="Environment.ProcessorCount"/> − 1 threads
    /// (at least 1). It lives until the process ends; its <see cref="Dispose"/> does nothing.
    /// </summary>
    public static PlotWorkerPool Shared => _shared.Value;

    /// <summary>Gets the number of threads.</summary>
    public int ThreadCount => _threads.Length;

    /// <summary>
    /// Starts building a min/max pyramid over a column and returns it at once (design §3.3). Until a chunk is built, queries
    /// answer provisionally from its statistics if already computed, and without the chunk otherwise.
    /// </summary>
    /// <typeparam name="T">The element type of the column.</typeparam>
    /// <param name="column">The column. Dispose the pyramid before it.</param>
    /// <returns>The pyramid, whose chunks the pool fills in the background.</returns>
    public MinMaxPyramid<T> BuildPyramid<T>(Column<T> column)
        where T : unmanaged, INumber<T>
    {
        ArgumentNullException.ThrowIfNull(column);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var pyramid = new MinMaxPyramid<T>(this, column);
        var registered = false;
        lock (_gate)
        {
            if (!_disposed)
            {
                _works = [.. _works, pyramid.Work];
                registered = true;
            }
        }

        if (!registered)
        {
            // The pool was disposed while the pyramid was made: free its blocks before throwing.
            pyramid.Dispose();
            throw new ObjectDisposedException(nameof(PlotWorkerPool));
        }

        Wake();
        return pyramid;
    }

    /// <summary>
    /// Starts building a spatial tile index over an x and a y column and returns it at once (design §3.4). Until the
    /// build ends, <see cref="SpatialIndex.Acquire"/> lends what there is: nothing, then the counts of levels 0 to 8, then
    /// the table and the copy.
    /// </summary>
    /// <typeparam name="TX">The element type of the x column.</typeparam>
    /// <typeparam name="TY">The element type of the y column.</typeparam>
    /// <param name="x">The x column. Dispose the index before it.</param>
    /// <param name="y">The y column: as long as <paramref name="x"/>. Dispose the index before it.</param>
    /// <param name="input">The rows, the transforms, and the ranges.</param>
    /// <returns>The index, which the pool builds in the background. A build that throws sets <see cref="SpatialIndex.Fault"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="x"/> or <paramref name="y"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The columns differ in length; the first row is negative or after the row past the last; or a passed range has an
    /// end that is not finite or a lower end greater than its upper end.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The pool was disposed before the call.</exception>
    internal SpatialIndex BuildSpatialIndex<TX, TY>(Column<TX> x, Column<TY> y, in SpatialIndexInput input)
        where TX : unmanaged, INumber<TX>
        where TY : unmanaged, INumber<TY> => BuildSpatialIndex(x, y, input, SpatialIndexTuning.Default, SpatialCopyAllocator.Default);

    /// <summary>
    /// Starts building a spatial tile index with the constants and the allocator that the caller passes. The tests and the
    /// benchmarks only: they reach the deep and the capped shapes of the table with few points, make an allocation throw,
    /// and time the jobs.
    /// </summary>
    /// <typeparam name="TX">The element type of the x column.</typeparam>
    /// <typeparam name="TY">The element type of the y column.</typeparam>
    /// <param name="x">The x column.</param>
    /// <param name="y">The y column: as long as <paramref name="x"/>.</param>
    /// <param name="input">The rows, the transforms, and the ranges.</param>
    /// <param name="tuning">The constants of the build.</param>
    /// <param name="allocator">What makes the copy.</param>
    /// <returns>The index.</returns>
    /// <exception cref="ArgumentException">As the other overload; or the rows of a unit are not a power of two, or are more than a chunk.</exception>
    internal SpatialIndex BuildSpatialIndex<TX, TY>(
        Column<TX> x,
        Column<TY> y,
        in SpatialIndexInput input,
        SpatialIndexTuning tuning,
        SpatialCopyAllocator allocator
    )
        where TX : unmanaged, INumber<TX>
        where TY : unmanaged, INumber<TY>
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        if (x.Length != y.Length)
        {
            ThrowHelper.ThrowSpatialColumnsDiffer(x.Length, y.Length);
        }

        if (input.Rows.Start < 0 || input.Rows.Start > input.Rows.End)
        {
            ThrowHelper.ThrowSpatialRowsInvalid(input.Rows.Start, input.Rows.End);
        }

        CheckRange(input.XRange, nameof(SpatialIndexInput.XRange));
        CheckRange(input.YRange, nameof(SpatialIndexInput.YRange));

        // A unit lies in one chunk, and the units of a chunk are the same for every first row.
        if (!int.IsPow2(tuning.RowsPerUnit) || tuning.RowsPerUnit > Column.ChunkLength)
        {
            ThrowHelper.ThrowSpatialUnitInvalid(tuning.RowsPerUnit);
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        var index = new SpatialIndex<TX, TY>(this, x, y, input, tuning, allocator);
        var registered = false;
        lock (_gate)
        {
            if (!_disposed)
            {
                _works = [.. _works, index.Work];
                registered = true;
            }
        }

        if (!registered)
        {
            // The pool was disposed while the index was made: no worker is left to build it.
            index.Dispose();
            throw new ObjectDisposedException(nameof(PlotWorkerPool));
        }

        Wake();
        return index;
    }

    /// <summary>
    /// Computes the chunk statistics of a column in the background (design §3.2). Do this for a stored X column before M4
    /// extraction reads it: until every chunk's statistics are computed, an extraction over it is provisional and does not
    /// check that the column is non-decreasing (design §5.5.1).
    /// </summary>
    /// <typeparam name="T">The element type of the column.</typeparam>
    /// <param name="column">The column. Do not dispose it before the task ends.</param>
    /// <returns>
    /// A task that ends when every chunk's statistics are computed; it faults with the first exception a chunk threw, and with
    /// <see cref="ObjectDisposedException"/> when the pool is disposed first.
    /// </returns>
    public Task PrecomputeStats<T>(Column<T> column)
        where T : unmanaged, INumber<T>
    {
        ArgumentNullException.ThrowIfNull(column);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var work = new StatsWork<T>(this, column);
        if (column.ChunkCount == 0)
        {
            return work.Completion;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _works = [.. _works, work];
        }

        Wake();
        return work.Completion;
    }

    /// <summary>Runs a pass over chunks on the workers (row G7c) and returns the task of its end; no chunks end at once.</summary>
    internal Task RunChunks(int chunks, Action<int> build)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (chunks == 0)
        {
            return Task.CompletedTask;
        }

        var work = new ChunkWork(this, chunks, build);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _works = [.. _works, work];
        }

        Wake();
        return work.Completion;
    }

    /// <summary>
    /// Starts the pass that finds each category's least and greatest y (design §5.5.3, row G7b): a chunk of the Y column a
    /// worker turn, at the version the column has now. A Y column of no chunk ends at once.
    /// </summary>
    internal CategoryExtremes StartCategoryExtremes<T>(CategoricalColumn x, Column<T> y, RowRange rows)
        where T : unmanaged, INumber<T>
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = new CategoryExtremes(x.CategoryCount, y.Version);
        if (y.ChunkCount == 0)
        {
            result.Source.TrySetResult();
            return result;
        }

        var work = new CategoryExtremesWork<T>(this, x, y, result, rows);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _works = [.. _works, work];
        }

        Wake();
        return result;
    }

    /// <summary>
    /// Extracts M4 for every series of a batch between the same boundaries, the series spread over the workers and the
    /// calling thread, and returns when all have ended (design §5.5.1, §8.1). The workers take these jobs before pyramid
    /// chunks, and the caller takes jobs too, so the wait is at most one chunk a worker is building. Allocates nothing.
    /// </summary>
    /// <param name="batch">The series. One thread at a time extracts a batch; other batches may be extracted at once.</param>
    /// <param name="origin">The origins of x and y that every series answers in (design §3.6).</param>
    /// <param name="boundaries">
    /// The W + 1 boundaries of the pixel columns, as differences from the x origin: finite and strictly increasing, read by
    /// every series.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="batch"/> or <paramref name="boundaries"/> is null.</exception>
    /// <exception cref="ArgumentException">The boundaries are fewer than 2, not finite, or not strictly increasing; checked once, before any series.</exception>
    /// <exception cref="ObjectDisposedException">The pool was disposed before the call.</exception>
    /// <exception cref="InvalidOperationException">Called on one of a pool's worker threads, which would wait for its own work.</exception>
    /// <remarks>
    /// A series that throws fails alone: the others still end and keep their results. After all have ended, the exception
    /// of the failed series with the smallest number is thrown again as it was; <see cref="M4Batch.GetError(int)"/> tells
    /// each series' failure. If the pool is disposed during the call, the caller ends the jobs left.
    /// </remarks>
    public void Extract(M4Batch batch, ScaleOrigin origin, double[] boundaries)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(boundaries);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isWorkerThread)
        {
            throw new InvalidOperationException("A worker thread cannot extract a batch: it would wait for its own work.");
        }

        // The arguments every series shares are checked once, so that a caller's mistake is one exception (design §5.5.1).
        M4.ValidateBoundaries(boundaries);
        if (batch.Count == 0)
        {
            return;
        }

        batch.Begin(origin, boundaries);
        lock (_gate)
        {
            batch.Next = _batches;
            Volatile.Write(ref _batches, batch);
        }

        try
        {
            Wake();
            while (CallerTakesJobs && batch.TryRunOne()) { }

            batch.Wait();
        }
        finally
        {
            Unlink(batch);
        }

        if (batch.FirstError() is { } error)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
        }
    }

    /// <summary>
    /// Extracts several batches in one pass (row G6a): each with its own origin and boundaries, all queued at once so that the
    /// workers and the caller take the jobs of any of them, then waited for. A plot's pairs of scales are its batches: one at
    /// a time would run a secondary scale's series after the primary's instead of beside them.
    /// </summary>
    /// <param name="batches">
    /// The batches; an empty one is skipped. The first one's boundaries are checked even when it is empty, as
    /// <see cref="Extract"/> checks them: a plot without series still refuses a primary x domain too narrow for its columns.
    /// </param>
    /// <param name="origins">Each batch's origin.</param>
    /// <param name="boundaries">Each batch's boundaries.</param>
    /// <param name="count">The number of batches to take from the arrays.</param>
    /// <param name="rethrow">Whether a failed series is thrown again; false leaves the errors in the batches (row W1b1).</param>
    /// <exception cref="ArgumentException">A batch's boundaries are fewer than 2, not finite, or not strictly increasing.</exception>
    /// <exception cref="ObjectDisposedException">The pool was disposed before the call.</exception>
    /// <exception cref="InvalidOperationException">Called on one of a pool's worker threads.</exception>
    /// <remarks>After all have ended, the first failed series' exception of the first batch that has one is thrown again as it was, unless <paramref name="rethrow"/> is false.</remarks>
    internal void ExtractTogether(M4Batch[] batches, ScaleOrigin[] origins, double[][] boundaries, int count, bool rethrow = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isWorkerThread)
        {
            throw new InvalidOperationException("A worker thread cannot extract a batch: it would wait for its own work.");
        }

        for (var i = 0; i < count; i++)
        {
            if (i == 0 || batches[i].Count > 0)
            {
                M4.ValidateBoundaries(boundaries[i]);
            }
        }

        for (var i = 0; i < count; i++)
        {
            if (batches[i].Count == 0)
            {
                continue;
            }

            batches[i].Begin(origins[i], boundaries[i]);
            lock (_gate)
            {
                batches[i].Next = _batches;
                Volatile.Write(ref _batches, batches[i]);
            }
        }

        try
        {
            Wake();
            for (var i = 0; i < count; i++)
            {
                while (batches[i].Count > 0 && CallerTakesJobs && batches[i].TryRunOne()) { }
            }

            for (var i = 0; i < count; i++)
            {
                if (batches[i].Count > 0)
                {
                    batches[i].Wait();
                }
            }
        }
        finally
        {
            for (var i = 0; i < count; i++)
            {
                if (batches[i].Count > 0)
                {
                    Unlink(batches[i]);
                }
            }
        }

        // A frame that does not wait reads each series' error itself (row W1b1): a failed series fails its layer alone.
        for (var i = 0; rethrow && i < count; i++)
        {
            if (batches[i].Count > 0 && batches[i].FirstError() is { } error)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
            }
        }
    }

    /// <summary>
    /// Stops taking work, lets each thread finish the chunk it is building, and joins the threads (design §8.1). No chunk is
    /// built again afterwards, even after an Invalidate, so every pyramid of the pool gets
    /// <see cref="ObjectDisposedException"/> as its <see cref="MinMaxPyramid{T}.Fault"/>, built ones included (their
    /// <see cref="MinMaxPyramid{T}.Completion"/> stays successful), and every <see cref="PrecomputeStats{T}(Column{T})"/>
    /// task not yet ended faults with the same exception. Does nothing for <see cref="Shared"/>.
    /// </summary>
    public void Dispose()
    {
        if (_isShared || _disposed)
        {
            return;
        }

        _disposed = true;
        PoolWork[] running;

        // The lock is also the full barrier between the write of the flag and what a work reads in OnPoolDisposing: a
        // job that starts meanwhile reads the flag after its own write (IsDisposed).
        lock (_gate)
        {
            running = _works;
        }

        // Before the join, which waits for the jobs in flight.
        foreach (var work in running)
        {
            work.OnPoolDisposing();
        }

        _wake.Set();
        foreach (var thread in _threads)
        {
            thread.Join();
        }

        PoolWork[] left;
        lock (_gate)
        {
            left = _works;
            _works = [];
        }

        foreach (var work in left)
        {
            work.OnPoolDisposed();
        }

        // No worker is left to free the snapshots that were given back to it.
        DrainFree();

        // The event is not disposed: a query on another thread may still call Wake, which must not throw.
    }

    /// <summary>Gets a value indicating whether the disposal of the pool has begun: a job that would run long does not start.</summary>
    internal bool IsDisposed => _disposed;

    /// <summary>Returns whether a batch is in the list the workers read; tests wait for it before they release a worker.</summary>
    internal bool IsQueued(M4Batch batch)
    {
        lock (_gate)
        {
            for (var candidate = _batches; candidate is not null; candidate = candidate.Next)
            {
                if (candidate == batch)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Gets or sets a value indicating whether the caller of Extract takes jobs; tests turn it off to see the workers' order.</summary>
    internal bool CallerTakesJobs { get; set; } = true;

    /// <summary>
    /// Gets or sets a test hook called on a worker after it ran a job it took: true for a frame job, false for a chunk. A
    /// chunk's call comes after the chunk is built, so after a completion that chunk ends.
    /// </summary>
    internal Action<bool>? TestTook { get; set; }

    /// <summary>
    /// Holds the pool's lock on a new thread for a time, and returns that thread once it holds it (Issue #304). Tests only, and
    /// the warm-up of <see cref="WarmsGate"/>: a thread that takes the lock meanwhile waits on it.
    /// </summary>
    internal Thread HoldGate(TimeSpan duration)
    {
        using var held = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            // A wait on an event no one sets ends after the time: the holder keeps the lock that long.
            using var never = new ManualResetEventSlim();
            lock (_gate)
            {
                held.Set();
                _ = never.Wait(duration);
            }
        })
        {
            IsBackground = true,
        };
        holder.Start();
        held.Wait();
        return holder;
    }

    /// <summary>Wakes the threads to look for work. Neither allocates nor waits (design §3.3).</summary>
    internal void Wake()
    {
        if (!_disposed)
        {
            _wake.Set();
        }
    }

    /// <summary>
    /// Hands a spatial snapshot that nothing reads any more to a worker, which frees its table and its copy (design §3.4
    /// "参照の数"). Neither allocates nor waits. Once the pool is disposed, the call frees them itself.
    /// </summary>
    /// <param name="snapshot">The snapshot, whose last reference was given back.</param>
    internal void QueueFree(SpatialSnapshot snapshot)
    {
        if (_disposed)
        {
            snapshot.Free();
            return;
        }

        while (true)
        {
            var head = Volatile.Read(ref _freeList);
            snapshot.NextFree = head;
            if (Interlocked.CompareExchange(ref _freeList, snapshot, head) == head)
            {
                break;
            }
        }

        Wake();
        if (_disposed)
        {
            // Dispose may have looked at the list before the snapshot was in it.
            DrainFree();
        }
    }

    internal void Unregister(PoolWork work)
    {
        lock (_gate)
        {
            var index = Array.IndexOf(_works, work);
            if (index >= 0)
            {
                _works = [.. _works[..index], .. _works[(index + 1)..]];
            }
        }
    }

    private void Unlink(M4Batch batch)
    {
        lock (_gate)
        {
            if (_batches == batch)
            {
                Volatile.Write(ref _batches, batch.Next);
            }
            else
            {
                for (var previous = _batches; previous is not null; previous = previous.Next)
                {
                    if (previous.Next == batch)
                    {
                        previous.Next = batch.Next;
                        break;
                    }
                }
            }

            batch.Next = null;
        }
    }

    private static void CheckRange(ScaleRange? range, string name)
    {
        if (range is { } passed && !(double.IsFinite(passed.Lower) && double.IsFinite(passed.Upper) && passed.Lower <= passed.Upper))
        {
            ThrowHelper.ThrowSpatialRangeInvalid(name, passed.Lower, passed.Upper);
        }
    }

    // Frees the snapshots that QueueFree was given. The list is taken whole, so each snapshot is freed by one thread.
    private bool DrainFree()
    {
        var snapshot = Interlocked.Exchange(ref _freeList, null);
        var freed = snapshot is not null;
        while (snapshot is not null)
        {
            var next = snapshot.NextFree;
            snapshot.NextFree = null;
            snapshot.Free();
            snapshot = next;
        }

        return freed;
    }

    private void Run()
    {
        _isWorkerThread = true;
        while (!_disposed)
        {
            if (RunOne())
            {
                continue;
            }

            // A wake that comes between the search above and this reset is caught by the search below.
            _wake.Reset();
            if (_disposed || RunOne())
            {
                continue;
            }

            _wake.Wait();
        }

        // Another worker may clear the wake just after Dispose set it, while this one is past its check and about to wait:
        // each worker that leaves sets it again, and the last to clear it leaves too, so none sleeps through the dispose.
        _wake.Set();
    }

    private bool RunOne()
    {
        // The jobs of a frame come before pyramid chunks (design §8.1). With no batch, the build path takes no lock; a batch
        // linked just after this read wakes the threads, so it is not missed.
        M4Batch? batch = null;
        if (Volatile.Read(ref _batches) is not null)
        {
            lock (_gate)
            {
                for (var candidate = _batches; candidate is not null; candidate = candidate.Next)
                {
                    if (candidate.HasWork)
                    {
                        batch = candidate;
                        break;
                    }
                }
            }
        }

        if (batch is not null && batch.TryRunOne())
        {
            TestTook?.Invoke(true);
            return true;
        }

        // Freeing a snapshot gives memory back, so it comes before the work that takes memory.
        if (Volatile.Read(ref _freeList) is not null && DrainFree())
        {
            return true;
        }

        var works = Volatile.Read(ref _works);

        // Requested chunks of every pyramid come before the rest (design §3.3).
        foreach (var work in works)
        {
            if (work.RunOne(requestedOnly: true))
            {
                TestTook?.Invoke(false);
                return true;
            }
        }

        foreach (var work in works)
        {
            if (work.RunOne(requestedOnly: false))
            {
                TestTook?.Invoke(false);
                return true;
            }
        }

        return false;
    }
}
