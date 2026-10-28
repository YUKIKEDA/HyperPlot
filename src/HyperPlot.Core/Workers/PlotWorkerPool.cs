using System.Numerics;

namespace HyperPlot;

/// <summary>
/// A fixed set of background threads that build pyramids and compute column statistics ahead, chunk by chunk (design §8.1).
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
    private PoolWork[] _works = [];
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

    private void Run()
    {
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
        var works = Volatile.Read(ref _works);

        // Requested chunks of every pyramid come before the rest (design §3.3).
        foreach (var work in works)
        {
            if (work.RunOne(requestedOnly: true))
            {
                return true;
            }
        }

        foreach (var work in works)
        {
            if (work.RunOne(requestedOnly: false))
            {
                return true;
            }
        }

        return false;
    }
}
