namespace HyperPlot.Lines3D;

/// <summary>
/// A fixed team of threads that run one job together (SPIKE, row K0b): the calling thread is member 0, the helpers wait on
/// a semaphore each between jobs. A run allocates nothing; the job is a delegate made once by the caller.
/// </summary>
internal sealed class Team : IDisposable
{
    private readonly Thread[] _helpers;
    private readonly SemaphoreSlim[] _go;
    private readonly CountdownEvent _done = new(1);
    private Action<int>? _job;
    private bool _closing;
    private Exception? _failure;

    public Team(int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        Size = size;
        _helpers = new Thread[size - 1];
        _go = new SemaphoreSlim[size - 1];
        for (var i = 0; i < _helpers.Length; i++)
        {
            var member = i + 1;
            _go[i] = new SemaphoreSlim(0);
            _helpers[i] = new Thread(() => Loop(member)) { IsBackground = true, Name = "Lines3D member" };
            _helpers[i].Start();
        }
    }

    public int Size { get; }

    /// <summary>Runs the job on members 0 to members - 1 and returns when all are done; rethrows a helper's failure.</summary>
    public void Run(Action<int> job, int members)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(members, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(members, Size);
        _job = job;
        if (members > 1)
        {
            _done.Reset(members - 1);
            for (var i = 0; i < members - 1; i++)
            {
                _go[i].Release();
            }
        }

        try
        {
            job(0);
        }
        finally
        {
            if (members > 1)
            {
                _done.Wait();
            }
        }

        if (Interlocked.Exchange(ref _failure, null) is { } failure)
        {
            throw new InvalidOperationException("A member of the team failed.", failure);
        }
    }

    public void Dispose()
    {
        Volatile.Write(ref _closing, true);
        foreach (var go in _go)
        {
            go.Release();
        }

        foreach (var helper in _helpers)
        {
            helper.Join();
        }

        foreach (var go in _go)
        {
            go.Dispose();
        }

        _done.Dispose();
    }

    private void Loop(int member)
    {
        var go = _go[member - 1];
        while (true)
        {
            go.Wait();
            if (Volatile.Read(ref _closing))
            {
                return;
            }

            try
            {
                _job!(member);
            }
            catch (Exception exception)
            {
                Interlocked.Exchange(ref _failure, exception);
            }
            finally
            {
                _done.Signal();
            }
        }
    }
}
