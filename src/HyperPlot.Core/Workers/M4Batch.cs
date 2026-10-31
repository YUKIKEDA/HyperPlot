using System.Numerics;

namespace HyperPlot;

/// <summary>
/// Series registered once for M4 extraction across a <see cref="PlotWorkerPool"/>, one job a series (design §5.5.1, §8.1).
/// Each frame, <see cref="PlotWorkerPool.Extract(M4Batch, ScaleOrigin, double[])"/> extracts every series between the same boundaries,
/// in the same origins.
/// </summary>
/// <remarks>
/// One thread at a time calls <see cref="Add{TX, TY}"/>, <see cref="Add{TY}"/>, <see cref="Clear"/>,
/// <see cref="PlotWorkerPool.Extract(M4Batch, ScaleOrigin, double[])"/>, <see cref="GetResult"/>, and <see cref="GetError"/> on one batch.
/// </remarks>
public sealed class M4Batch
{
    private readonly List<Job> _jobs = [];

    // A plain object, not a Lock: the wait for the last job uses Monitor.Wait and PulseAll on it.
    private readonly object _sync = new();
    private double[] _boundaries = [];
    private ScaleOrigin _origin;
    private int _next;
    private int _remaining;

    /// <summary>Gets the number of series.</summary>
    public int Count => _jobs.Count;

    /// <summary>Gets or sets the next batch in the pool's list of batches being extracted (design §8.1).</summary>
    internal M4Batch? Next { get; set; }

    /// <summary>Gets a value indicating whether a job is left to take.</summary>
    internal bool HasWork => Volatile.Read(ref _next) < _jobs.Count;

    /// <summary>Adds a series with a stored X column. Allocates its job here, so that extraction does not.</summary>
    /// <typeparam name="TX">The X element type.</typeparam>
    /// <typeparam name="TY">The Y element type.</typeparam>
    /// <param name="x">The X column, non-decreasing.</param>
    /// <param name="y">The pyramid over the Y column.</param>
    /// <param name="destination">Where the series' points go: at least <see cref="M4.MaxPointCount(int)"/> of W.</param>
    /// <param name="options">How NaN is treated.</param>
    /// <returns>The series' number, from 0 in the order of adding.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public int Add<TX, TY>(Column<TX> x, MinMaxPyramid<TY> y, M4Point[] destination, M4Options options = default)
        where TX : unmanaged, INumber<TX>
        where TY : unmanaged, INumber<TY>
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        ArgumentNullException.ThrowIfNull(destination);
        _jobs.Add(new ColumnJob<TX, TY>(x, y, destination, options, RowRange.All));
        return _jobs.Count - 1;
    }

    /// <summary>Adds a series with an evenly spaced X. Allocates its job here, so that extraction does not.</summary>
    /// <typeparam name="TY">The Y element type.</typeparam>
    /// <param name="x">The X column.</param>
    /// <param name="y">The pyramid over the Y column.</param>
    /// <param name="destination">Where the series' points go: at least <see cref="M4.MaxPointCount(int)"/> of W.</param>
    /// <param name="options">How NaN is treated.</param>
    /// <returns>The series' number, from 0 in the order of adding.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public int Add<TY>(UniformColumn x, MinMaxPyramid<TY> y, M4Point[] destination, M4Options options = default)
        where TY : unmanaged, INumber<TY>
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        ArgumentNullException.ThrowIfNull(destination);
        _jobs.Add(new UniformJob<TY>(x, y, destination, options, RowRange.All));
        return _jobs.Count - 1;
    }

    /// <summary>Registers a series of a run of rows of a stored X (row G7c); the plan's checks stand for the public ones.</summary>
    internal int AddRun<TX, TY>(Column<TX> x, MinMaxPyramid<TY> y, M4Point[] destination, RowRange rows)
        where TX : unmanaged, INumber<TX>
        where TY : unmanaged, INumber<TY>
    {
        _jobs.Add(new ColumnJob<TX, TY>(x, y, destination, default, rows));
        return _jobs.Count - 1;
    }

    /// <summary>Registers a series of a run of rows of an evenly spaced X (row G7c).</summary>
    internal int AddRun<TY>(UniformColumn x, MinMaxPyramid<TY> y, M4Point[] destination, RowRange rows)
        where TY : unmanaged, INumber<TY>
    {
        _jobs.Add(new UniformJob<TY>(x, y, destination, default, rows));
        return _jobs.Count - 1;
    }

    /// <summary>Removes every series.</summary>
    public void Clear() => _jobs.Clear();

    /// <summary>Returns a series' result of the last extraction: the points written to its destination.</summary>
    /// <param name="series">The series' number.</param>
    /// <returns>The result; default when the series failed or was never extracted.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="series"/> is not a series' number.</exception>
    public M4Result GetResult(int series) => JobAt(series).Result;

    /// <summary>Returns the exception a series' last extraction threw, or null when it did not.</summary>
    /// <param name="series">The series' number.</param>
    /// <returns>The exception, or null.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="series"/> is not a series' number.</exception>
    public Exception? GetError(int series) => JobAt(series).Error;

    /// <summary>Starts an extraction: every job is left to take, and the end event is reset.</summary>
    internal void Begin(ScaleOrigin origin, double[] boundaries)
    {
        (_origin, _boundaries) = (origin, boundaries);
        foreach (var job in _jobs)
        {
            job.Reset();
        }

        Volatile.Write(ref _remaining, _jobs.Count);
        Volatile.Write(ref _next, 0);
    }

    /// <summary>Takes one job that is left and runs it. Returns false when none was left. Called by the workers and the caller.</summary>
    internal bool TryRunOne()
    {
        var index = Interlocked.Increment(ref _next) - 1;
        if (index >= _jobs.Count)
        {
            return false;
        }

        _jobs[index].Run(_origin, _boundaries);
        if (Interlocked.Decrement(ref _remaining) == 0)
        {
            lock (_sync)
            {
                Monitor.PulseAll(_sync);
            }
        }

        return true;
    }

    /// <summary>
    /// Waits until every job that was taken has ended. A monitor on an object of the batch, not an event: nothing to
    /// dispose, and nothing allocated a frame.
    /// </summary>
    internal void Wait()
    {
        lock (_sync)
        {
            while (Volatile.Read(ref _remaining) > 0)
            {
                Monitor.Wait(_sync);
            }
        }
    }

    /// <summary>Returns the error of the series with the smallest number that failed, or null.</summary>
    internal Exception? FirstError()
    {
        foreach (var job in _jobs)
        {
            if (job.Error is { } error)
            {
                return error;
            }
        }

        return null;
    }

    private Job JobAt(int series)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(series);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(series, _jobs.Count);
        return _jobs[series];
    }

    /// <summary>One series: its last result or error.</summary>
    private abstract class Job
    {
        public M4Result Result { get; private set; }

        public Exception? Error { get; private set; }

        public void Reset() => (Result, Error) = (default, null);

        public void Run(ScaleOrigin origin, double[] boundaries)
        {
            try
            {
                Result = Extract(origin, boundaries);
            }
            catch (Exception exception)
            {
                // The series fails alone; the batch goes on with the others (design §8.1).
                Error = exception;
            }
        }

        protected abstract M4Result Extract(ScaleOrigin origin, double[] boundaries);
    }

    private sealed class ColumnJob<TX, TY>(Column<TX> x, MinMaxPyramid<TY> y, M4Point[] destination, M4Options options, RowRange rows) : Job
        where TX : unmanaged, INumber<TX>
        where TY : unmanaged, INumber<TY>
    {
        protected override M4Result Extract(ScaleOrigin origin, double[] boundaries) =>
            rows == RowRange.All
                ? M4.Extract(x, y, origin, boundaries, destination, options)
                : M4.Extract(x, y, origin, boundaries, destination, options, rows);
    }

    private sealed class UniformJob<TY>(UniformColumn x, MinMaxPyramid<TY> y, M4Point[] destination, M4Options options, RowRange rows) : Job
        where TY : unmanaged, INumber<TY>
    {
        protected override M4Result Extract(ScaleOrigin origin, double[] boundaries) =>
            rows == RowRange.All
                ? M4.Extract(x, y, origin, boundaries, destination, options)
                : M4.Extract(x, y, origin, boundaries, destination, options, rows);
    }
}
