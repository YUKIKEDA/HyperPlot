using System.Numerics;

namespace HyperPlot;

/// <summary>
/// Computes the chunk statistics of one column ahead, a chunk per worker turn (design §3.2): what M4 reads of a stored X
/// column without scanning it on the frame path (design §5.5.1).
/// </summary>
internal sealed class StatsWork<T> : PoolWork
    where T : unmanaged, INumber<T>
{
    private readonly PlotWorkerPool _pool;
    private readonly Column<T> _column;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _next;
    private int _remaining;

    internal StatsWork(PlotWorkerPool pool, Column<T> column)
    {
        _pool = pool;
        _column = column;
        _remaining = column.ChunkCount;
        if (_remaining == 0)
        {
            _completion.TrySetResult();
        }
    }

    internal Task Completion => _completion.Task;

    internal override void OnPoolDisposed() => _completion.TrySetException(new ObjectDisposedException(nameof(PlotWorkerPool)));

    private protected override int TryTake(bool requestedOnly)
    {
        // Nothing is ever requested; the chunks go in position order.
        if (requestedOnly)
        {
            return -1;
        }

        var chunk = Interlocked.Increment(ref _next) - 1;
        return chunk < _column.ChunkCount ? chunk : -1;
    }

    private protected override void Build(int chunk)
    {
        try
        {
            _ = _column.GetStats(chunk);
        }
        catch (Exception exception)
        {
            // A failed chunk ends the work; the statistics of that chunk are computed when first read instead.
            _completion.TrySetException(exception);
        }

        if (Interlocked.Decrement(ref _remaining) == 0)
        {
            _pool.Unregister(this);
            _completion.TrySetResult();
        }
    }
}
