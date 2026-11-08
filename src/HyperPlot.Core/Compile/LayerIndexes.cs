using System.Numerics;
using System.Runtime.ExceptionServices;

namespace HyperPlot;

/// <summary>What a layer can draw in a frame (design §4.8, row W1b1).</summary>
internal enum LayerReadiness
{
    /// <summary>Its indexes and its workers' results are there for its columns' current values.</summary>
    Ready,

    /// <summary>It draws the results of an older version of its columns while the workers make the new ones: the frame is provisional.</summary>
    Previous,

    /// <summary>It draws nothing until its workers' results are there: the frame is provisional.</summary>
    Pending,
}

/// <summary>
/// How a plan's layers reach their indexes and their workers' results (design §4.8): a headless render waits for them, so
/// that its image is the same whenever it is drawn; the engine's frame does not wait (invariant 7, row W1b1), and reads
/// what is there, the statistics the workers have computed included. A failure is thrown as it was, in both.
/// </summary>
/// <remarks>Used from the plan's thread only. Allocates only when it starts a pass of statistics, once a version of a column.</remarks>
internal sealed class LayerIndexes(PlotWorkerPool pool, Func<Column, PlotPyramid?> held)
{
    // The passes of statistics started for columns without a pyramid, with the version they were started at.
    private readonly Dictionary<Column, (long Version, Task Task)> _requests = new(ReferenceEqualityComparer.Instance);

    /// <summary>Gets the pool the workers' passes run on.</summary>
    public PlotWorkerPool Pool => pool;

    /// <summary>Gets or initializes the queue a layer's work that waits for the workers runs on in a frame that does not wait (row W1b2).</summary>
    public MakingQueue? Makings { get; init; }

    /// <summary>Gets or sets a value indicating whether a call waits for what it reads (a headless render) or not (the engine's frame).</summary>
    public bool Waits { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a range read since it was cleared left out a chunk without statistics: the
    /// domain learned from it is provisional (row W1b1).
    /// </summary>
    public bool Incomplete { get; set; }

    /// <summary>Returns whether a task has ended: waits for it when the calls wait. A failed task's exception is thrown as it was.</summary>
    public bool Done(Task task)
    {
        if (!Waits && !task.IsCompleted)
        {
            return false;
        }

        task.GetAwaiter().GetResult();
        return true;
    }

    /// <summary>
    /// Returns whether a pyramid is built for its column's current values: waits for it when the calls wait, and otherwise
    /// requests its stale chunks again. A fault is thrown as it was.
    /// </summary>
    public bool Complete<T>(MinMaxPyramid<T> pyramid)
        where T : unmanaged, INumber<T>
    {
        if (Waits)
        {
            pyramid.WhenComplete().GetAwaiter().GetResult();
            return true;
        }

        if (pyramid.Fault is { } fault)
        {
            ExceptionDispatchInfo.Throw(fault);
        }

        return pyramid.RequestStale();
    }

    /// <summary>
    /// Returns whether the statistics M4 reads of a column as X are there (design §5.5.1): waits for them when the calls wait;
    /// otherwise starts their pass once a version and returns <see cref="LayerReadiness.Pending"/> until they are.
    /// </summary>
    public LayerReadiness XStats(Column column)
    {
        if (Waits)
        {
            column.PrecomputeStatsIfMissing(pool)?.GetAwaiter().GetResult();
            return LayerReadiness.Ready;
        }

        if (column.HasAllStats)
        {
            return LayerReadiness.Ready;
        }

        Request(column);
        return LayerReadiness.Pending;
    }

    /// <summary>
    /// Returns the range of a run of a column's values (design §4.2): from every chunk's statistics, computed on the caller
    /// when the calls wait; otherwise from those the workers have computed, starting the pass of the others and marking the
    /// range <see cref="Incomplete"/> (row W1b1).
    /// </summary>
    public bool TryGetRange(Column column, RowRange rows, out ScaleEnd low, out ScaleEnd high)
    {
        if (Waits)
        {
            return rows == RowRange.All ? column.TryGetRange(out low, out high) : column.TryGetRange(rows, out low, out high);
        }

        var found = column.TryGetStoredRange(rows, out low, out high, out var complete);
        if (!complete)
        {
            Incomplete = true;
            Request(column);
        }

        return found;
    }

    /// <summary>
    /// Returns whether a plan's pyramid is built for its column's current values (row W1b1): waits for it when the calls
    /// wait, and otherwise requests its stale chunks again. A fault is thrown as it was.
    /// </summary>
    public bool Complete(PlotPyramid pyramid)
    {
        if (Waits)
        {
            pyramid.WhenComplete().GetAwaiter().GetResult();
            return true;
        }

        if (pyramid.Fault is { } fault)
        {
            ExceptionDispatchInfo.Throw(fault);
        }

        return pyramid.RequestStale();
    }

    /// <summary>Returns the readiness of a layer of parts: the least ready part's.</summary>
    public static LayerReadiness Join(LayerReadiness a, LayerReadiness b) => a > b ? a : b;

    /// <summary>Forgets the passes of columns the plan may no longer read (its layers were made again).</summary>
    public void Forget() => _requests.Clear();

    /// <summary>
    /// Starts the pass of a column's missing statistics, once a version: a column with a pyramid gets them from its build
    /// (design §3.3). A pass of the current version that failed throws its exception, until the version moves.
    /// </summary>
    private void Request(Column column)
    {
        if (held(column) is not null)
        {
            return;
        }

        var version = column.Version;
        if (_requests.TryGetValue(column, out var request) && request.Version == version)
        {
            if (request.Task.IsFaulted)
            {
                request.Task.GetAwaiter().GetResult();
            }

            return;
        }

        if (column.PrecomputeStatsIfMissing(pool) is { } task)
        {
            _requests[column] = (version, task);
        }
    }
}
