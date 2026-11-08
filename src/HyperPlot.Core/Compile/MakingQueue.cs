namespace HyperPlot;

/// <summary>
/// Runs the makings of the plans of one pyramid store one after another on a .NET thread pool task (design §4.8 "層を作る段",
/// §8.1, row W1b2): a making may wait for the workers, the plans' thread never does. One runner at a time, so that the
/// threads that wait are at most the stores', and a figure's plots never make the same pyramid twice.
/// </summary>
internal sealed class MakingQueue
{
    private readonly Lock _gate = new();
    private readonly Queue<Making> _queue = new();
    private bool _running;

    /// <summary>Gets a value indicating whether no making runs or waits to. Tests only.</summary>
    public bool Idle
    {
        get
        {
            lock (_gate)
            {
                return !_running;
            }
        }
    }

    /// <summary>Queues a making, and starts the runner when none runs.</summary>
    public void Post(Making making)
    {
        lock (_gate)
        {
            _queue.Enqueue(making);
            if (_running)
            {
                return;
            }

            _running = true;
        }

        _ = Task.Run(Run);
    }

    private void Run()
    {
        while (true)
        {
            Making next;
            lock (_gate)
            {
                if (!_queue.TryDequeue(out next!))
                {
                    _running = false;
                    return;
                }
            }

            next.Run();
        }
    }
}

/// <summary>
/// A piece of work a plan hands to its <see cref="MakingQueue"/> (row W1b2): it runs once on the queue's task, and its plan
/// reads <see cref="IsDone"/> at its frames without waiting. A plan that no longer wants it abandons it; what it made is then
/// freed, by the plan or by the task, whichever comes last.
/// </summary>
internal abstract class Making
{
    private int _done;
    private int _abandoned;
    private int _freed;

    /// <summary>Gets a value indicating whether the work has ended, made or failed.</summary>
    public bool IsDone => Volatile.Read(ref _done) != 0;

    /// <summary>Gets the exception the work threw, or null; read once <see cref="IsDone"/>.</summary>
    public Exception? Error { get; private set; }

    /// <summary>Runs the work, on the queue's task.</summary>
    public void Run()
    {
        try
        {
            if (Volatile.Read(ref _abandoned) == 0)
            {
                Work();
            }
        }
        catch (Exception exception)
        {
            // The plan reports it as the layers' failure (design §4.8, Issue #297 Decision 5).
            Error = exception;
        }
        finally
        {
            Volatile.Write(ref _done, 1);
            if (Volatile.Read(ref _abandoned) != 0)
            {
                FreeOnce();
            }
        }
    }

    /// <summary>Gives the work up (row W1b2, Issue #297 Decision 8): nothing waits for it, and what it made is freed once it ends.</summary>
    public void Abandon()
    {
        Volatile.Write(ref _abandoned, 1);
        if (IsDone)
        {
            FreeOnce();
        }
    }

    /// <summary>The work: may wait for the workers; touches no state of the plan's but what it was given.</summary>
    protected abstract void Work();

    /// <summary>Frees what the work made, for a plan that did not take it.</summary>
    protected abstract void Free();

    private void FreeOnce()
    {
        if (Interlocked.Exchange(ref _freed, 1) == 0)
        {
            Free();
        }
    }
}
