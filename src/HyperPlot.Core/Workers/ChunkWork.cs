namespace HyperPlot;

/// <summary>
/// A pass over the chunks of columns on the workers (row G7c): one call of a delegate a chunk, in any order and at once on
/// several workers, and a completion when the last one returns. The compile starts it and waits; it is not on the frame
/// path.
/// </summary>
internal sealed class ChunkWork(PlotWorkerPool pool, int chunks, Action<int> build) : PoolWork
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _chunks = chunks;
    private int _next;
    private int _remaining = chunks;

    /// <summary>Gets a task that ends when every chunk has run, or with the first exception a chunk threw.</summary>
    public Task Completion => _completion.Task;

    internal override void OnPoolDisposed() => _completion.TrySetException(new ObjectDisposedException(nameof(PlotWorkerPool)));

    private protected override int TryTake(bool requestedOnly)
    {
        if (requestedOnly)
        {
            return -1;
        }

        var chunk = Interlocked.Increment(ref _next) - 1;
        return chunk < _chunks ? chunk : -1;
    }

    private protected override void Build(int chunk)
    {
        try
        {
            build(chunk);
        }
        catch (Exception exception)
        {
            _completion.TrySetException(exception);
        }

        if (Interlocked.Decrement(ref _remaining) == 0)
        {
            pool.Unregister(this);
            _completion.TrySetResult();
        }
    }
}
