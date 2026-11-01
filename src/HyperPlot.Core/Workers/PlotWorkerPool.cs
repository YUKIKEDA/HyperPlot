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
        () => new PlotWorkerPool(Math.Max(1, Environment.ProcessorCount - 1), isShared: true),
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
    private volatile bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="PlotWorkerPool"/> class and starts its threads.</summary>
    /// <param name="threadCount">The number of threads, at least 1.</param>
    public PlotWorkerPool(int threadCount)
        : this(threadCount, isShared: false) { }

    private PlotWorkerPool(int threadCount, bool isShared)
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
    }

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
    /// <exception cref="ArgumentException">A batch's boundaries are fewer than 2, not finite, or not strictly increasing.</exception>
    /// <exception cref="ObjectDisposedException">The pool was disposed before the call.</exception>
    /// <exception cref="InvalidOperationException">Called on one of a pool's worker threads.</exception>
    /// <remarks>After all have ended, the first failed series' exception of the first batch that has one is thrown again as it was.</remarks>
    internal void ExtractTogether(M4Batch[] batches, ScaleOrigin[] origins, double[][] boundaries, int count)
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

        for (var i = 0; i < count; i++)
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

        // The event is not disposed: a query on another thread may still call Wake, which must not throw.
    }

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

    /// <summary>Wakes the threads to look for work. Neither allocates nor waits (design §3.3).</summary>
    internal void Wake()
    {
        if (!_disposed)
        {
            _wake.Set();
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
