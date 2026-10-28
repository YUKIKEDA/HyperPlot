namespace HyperPlot.Tests;

/// <summary>
/// A managed storage whose reads on a pool's worker threads a test can hold, fail, and record, chunk by chunk. Reads on any
/// other thread (the test's own queries) pass straight through.
/// </summary>
internal sealed class TestStorage<T> : ColumnStorage<T>
    where T : unmanaged, System.Numerics.INumber<T>
{
    private const int WatchdogMilliseconds = 30_000;

    private readonly T[] _values;
    private readonly ManualResetEventSlim[] _gates;
    private readonly ManualResetEventSlim[] _entered;
    private readonly bool[] _fail;
    private readonly int[] _workerReads;
    private readonly List<int> _order = [];
    private readonly Lock _lock = new();

    public TestStorage(T[] values)
    {
        _values = values;
        var chunks = (values.Length + Column.ChunkLength - 1) / Column.ChunkLength;
        _gates = [.. Enumerable.Range(0, chunks).Select(_ => new ManualResetEventSlim(initialState: true))];
        _entered = [.. Enumerable.Range(0, chunks).Select(_ => new ManualResetEventSlim(initialState: false))];
        _fail = new bool[chunks];
        _workerReads = new int[chunks];
    }

    public override ColumnStorageKind Kind => ColumnStorageKind.Managed;

    public T[] Values => _values;

    /// <summary>Gets the chunks in the order a worker first read them.</summary>
    public int[] WorkerOrder
    {
        get
        {
            lock (_lock)
            {
                return [.. _order];
            }
        }
    }

    public static Column<T> MakeColumn(T[] values, out TestStorage<T> storage)
    {
        storage = new TestStorage<T>(values);
        return new Column<T>(storage, values.Length);
    }

    public void Hold(int chunk) => _gates[chunk].Reset();

    public void Release(int chunk) => _gates[chunk].Set();

    public void FailOn(int chunk, bool fail) => Volatile.Write(ref _fail[chunk], fail);

    public int WorkerReads(int chunk) => Volatile.Read(ref _workerReads[chunk]);

    /// <summary>Waits until a worker reads the chunk; fails the test after the watchdog instead of hanging.</summary>
    public void WaitForWorker(int chunk) =>
        Assert.True(_entered[chunk].Wait(WatchdogMilliseconds), $"No worker read chunk {chunk} within the watchdog.");

    /// <summary>Gets or sets an action run on the worker thread as it reads a chunk, before the hold.</summary>
    public Action? OnWorkerRead { get; set; }

    public override ReadOnlySpan<T> GetChunk(int chunkIndex, int length)
    {
        if (Thread.CurrentThread.Name?.StartsWith("HyperPlot worker", StringComparison.Ordinal) == true)
        {
            OnWorkerRead?.Invoke();
            Interlocked.Increment(ref _workerReads[chunkIndex]);
            lock (_lock)
            {
                if (!_order.Contains(chunkIndex))
                {
                    _order.Add(chunkIndex);
                }
            }

            _entered[chunkIndex].Set();
            if (!_gates[chunkIndex].Wait(WatchdogMilliseconds))
            {
                throw new TimeoutException($"Chunk {chunkIndex} was held past the watchdog.");
            }

            if (Volatile.Read(ref _fail[chunkIndex]))
            {
                throw new IOException($"Chunk {chunkIndex} failed on purpose.");
            }
        }

        return _values.AsSpan(chunkIndex * Column.ChunkLength, length);
    }

    public override void Dispose()
    {
        foreach (var gate in _gates)
        {
            gate.Set();
        }
    }
}

/// <summary>Waits for a condition another thread sets, with a watchdog instead of a hang (.cursor/rules/testing.mdc).</summary>
internal static class Eventually
{
    public static void True(Func<bool> condition, string what) =>
        Assert.True(SpinWait.SpinUntil(condition, 30_000), $"Timed out waiting for {what}.");

    /// <summary>Waits for a task to end successfully.</summary>
    public static void Completes(Task task)
    {
        Assert.True(((IAsyncResult)task).AsyncWaitHandle.WaitOne(30_000), "The task did not end within the watchdog.");
        Assert.True(task.IsCompletedSuccessfully, $"The task ended {task.Status}: {task.Exception?.InnerException}");
    }

    /// <summary>Waits for a task to fault and returns its exception.</summary>
    public static Exception Faults(Task task)
    {
        Assert.True(((IAsyncResult)task).AsyncWaitHandle.WaitOne(30_000), "The task did not end within the watchdog.");
        Assert.True(task.IsFaulted, $"The task ended {task.Status}.");
        return task.Exception!.InnerException!;
    }
}
