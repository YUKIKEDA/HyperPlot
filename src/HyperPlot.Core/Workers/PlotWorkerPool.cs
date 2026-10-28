using System.Numerics;

namespace HyperPlot;

/// <summary>
/// A fixed set of background threads that build pyramids chunk by chunk (design §8.1). Use <see cref="Shared"/>, or make
/// a pool of your own and dispose it when done.
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
    private PyramidWork[] _pyramids = [];
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

        foreach (var thread in _threads)
        {
            thread.Start();
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
    /// Starts building a min/max pyramid over a column and returns it at once (design §3.3). Queries answer from the chunk
    /// statistics until each chunk is built.
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
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pyramids = [.. _pyramids, pyramid.Work];
        }

        Wake();
        return pyramid;
    }

    /// <summary>
    /// Stops taking work, lets each thread finish the chunk it is building, and joins the threads. Pyramids with chunks left
    /// get <see cref="ObjectDisposedException"/> as their fault. Does nothing for <see cref="Shared"/>.
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

        PyramidWork[] left;
        lock (_gate)
        {
            left = _pyramids;
            _pyramids = [];
        }

        foreach (var pyramid in left)
        {
            pyramid.OnPoolDisposed();
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

    internal void Unregister(PyramidWork pyramid)
    {
        lock (_gate)
        {
            var index = Array.IndexOf(_pyramids, pyramid);
            if (index >= 0)
            {
                _pyramids = [.. _pyramids[..index], .. _pyramids[(index + 1)..]];
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
        var pyramids = Volatile.Read(ref _pyramids);

        // Requested chunks of every pyramid come before the rest (design §3.3).
        foreach (var pyramid in pyramids)
        {
            if (pyramid.RunOne(requestedOnly: true))
            {
                return true;
            }
        }

        foreach (var pyramid in pyramids)
        {
            if (pyramid.RunOne(requestedOnly: false))
            {
                return true;
            }
        }

        return false;
    }
}
