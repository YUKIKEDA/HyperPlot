namespace HyperPlot;

/// <summary>
/// A spatial tile index over an x and a y column (design §3.4): a quadtree of the points in the order of their Morton
/// codes, built in the background by a <see cref="PlotWorkerPool"/>. Make one with
/// <see cref="PlotWorkerPool.BuildSpatialIndex{TX, TY}(Column{TX}, Column{TY}, in SpatialIndexInput)"/>.
/// </summary>
/// <remarks>
/// <see cref="Acquire"/> may run on any thread while the index is built, and neither allocates nor waits. The index
/// is built again when a column's <see cref="Column.Version"/> has moved: at the end of a build that the change
/// overlapped, and otherwise when <see cref="Acquire"/> or <see cref="WhenComplete"/> finds the difference. Dispose the
/// index before its columns.
/// </remarks>
internal abstract class SpatialIndex : IDisposable
{
    // The state is the number of the build in the upper bits and one of these in the lower three (design §3.4 "状態").
    private const int Waiting = (int)SpatialIndexState.Waiting;
    private const int Counting = (int)SpatialIndexState.Counting;
    private const int Writing = (int)SpatialIndexState.Writing;
    private const int Density = (int)SpatialIndexState.Density;
    private const int Complete = (int)SpatialIndexState.Complete;
    private const int Failed = (int)SpatialIndexState.Failed;
    private const int Disposed = (int)SpatialIndexState.Disposed;
    private const int KindBits = 3;
    private const long KindMask = (1 << KindBits) - 1;

    // What TryTake hands to Build: a group of units, the start of a build, or the single job of a build.
    private const int StartToken = SpatialIndexTuning.GroupCount;
    private const int SingleToken = StartToken + 1;

    // The build that TryTake entered, for the Build that the same thread calls next.
    [ThreadStatic]
    private static SpatialBuild? _taken;

    private readonly Lock _gate = new();
    private readonly Lock _waiterGate = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SpatialIndexTuning _tuning;
    private readonly SpatialCopyAllocator _allocator;
    private readonly Shape _shape;
    private long _state;
    private long _version;
    private SpatialSnapshot _current;
    private SpatialBuild? _build;
    private Exception? _fault;
    private Exception? _lastFailure;
    private ObjectDisposedException? _poolGone;
    private long _failedX;
    private long _failedY;
    private TaskCompletionSource? _waiter;

    /// <summary>Initializes a new instance of the <see cref="SpatialIndex"/> class, which waits for a worker to build it.</summary>
    /// <param name="pool">The pool that builds the index.</param>
    /// <param name="x">The x column.</param>
    /// <param name="y">The y column: as long as <paramref name="x"/>.</param>
    /// <param name="input">The rows, the transforms, and the ranges.</param>
    /// <param name="tuning">The constants of the build.</param>
    /// <param name="allocator">What makes the copy.</param>
    /// <param name="shape">What the element types and the storage of the columns decide.</param>
    /// <param name="empty">Makes the snapshot that holds nothing, from the index and the number of rows it reads.</param>
    private protected SpatialIndex(
        PlotWorkerPool pool,
        Column x,
        Column y,
        in SpatialIndexInput input,
        SpatialIndexTuning tuning,
        SpatialCopyAllocator allocator,
        Shape shape,
        Func<SpatialIndex, long, SpatialSnapshot> empty
    )
    {
        Pool = pool;
        X = x;
        Y = y;
        Input = input;
        _tuning = tuning;
        _allocator = allocator;
        _shape = shape;
        Work = new WorkItem(this);
        _current = empty(this, Math.Max(0, input.Rows.EndIn(x.Length) - input.Rows.Start));
    }

    /// <summary>Gets the x column.</summary>
    public Column X { get; }

    /// <summary>Gets the y column.</summary>
    public Column Y { get; }

    /// <summary>Gets the rows, the transforms, and the ranges that the index was made with.</summary>
    public SpatialIndexInput Input { get; }

    /// <summary>Gets a number that grows by one each time the index replaces its snapshot.</summary>
    public long Version => Volatile.Read(ref _version);

    /// <summary>
    /// Gets a value indicating whether the last build ended and its snapshot was built from the current
    /// <see cref="Column.Version"/> of both columns.
    /// </summary>
    public bool IsComplete => KindOf(Volatile.Read(ref _state)) == Complete && Volatile.Read(ref _current).MatchesColumns;

    /// <summary>
    /// Gets a task that ends when a build first ends. It ends with the exception of a build that throws before that, and
    /// with <see cref="ObjectDisposedException"/> when the index or the pool is disposed before that.
    /// </summary>
    public Task Completion => _completion.Task;

    /// <summary>
    /// Gets the first exception that stopped a build, or the <see cref="ObjectDisposedException"/> of a disposed pool, or
    /// <see langword="null"/>. It is not cleared: the snapshot of the last build that ended stays in use.
    /// </summary>
    public Exception? Fault => Volatile.Read(ref _fault);

    /// <summary>Gets the state of the index (design §3.4 "状態"). The tests follow the moves of the state with it.</summary>
    internal SpatialIndexState State => (SpatialIndexState)KindOf(Volatile.Read(ref _state));

    /// <summary>Gets the part of the index that the pool drives.</summary>
    internal PoolWork Work { get; }

    /// <summary>Gets the pool that builds the index and frees the snapshots it has replaced.</summary>
    internal PlotWorkerPool Pool { get; }

    /// <summary>
    /// Lends the current snapshot (design §3.4 "参照の数"). Give it back with <see cref="SpatialSnapshot.Release"/>.
    /// Neither allocates nor waits. When a column's version has moved since the last build, the call queues a new build
    /// and returns the snapshot there is, which says so in <see cref="SpatialSnapshot.IsProvisional"/>.
    /// </summary>
    /// <returns>The snapshot, which stays readable until its release, also after the index replaces it or is disposed.</returns>
    /// <exception cref="ObjectDisposedException">The index is disposed.</exception>
    public SpatialSnapshot Acquire()
    {
        while (true)
        {
            ObjectDisposedException.ThrowIf(KindOf(Volatile.Read(ref _state)) == Disposed, this);
            Reconsider();

            // A snapshot whose last reference went back has been replaced: the read after it sees the new one.
            var snapshot = Volatile.Read(ref _current);
            if (snapshot.TryAddReference())
            {
                return snapshot;
            }
        }
    }

    /// <summary>
    /// Returns a task that ends when <see cref="IsComplete"/> is true (design §3.4 "完了を待つ"): at once when it is. A
    /// call that finds a column's version moved queues a new build. The task ends with the exception of the last build
    /// when that build threw and the versions have not moved since, with the exception of a build that throws during the wait, and
    /// with <see cref="ObjectDisposedException"/> when the index or the pool is disposed. Calls during one wait get the
    /// same task.
    /// </summary>
    /// <returns>The task.</returns>
    internal Task WhenComplete()
    {
        if (KindOf(Volatile.Read(ref _state)) == Disposed)
        {
            return Task.FromException(new ObjectDisposedException(nameof(SpatialIndex)));
        }

        if (IsComplete)
        {
            return Task.CompletedTask;
        }

        if (Volatile.Read(ref _poolGone) is { } gone)
        {
            return Task.FromException(gone);
        }

        // A failed build is not queued again until a version moves (rule 4), so a wait for it would not end.
        Reconsider();
        if (KindOf(Volatile.Read(ref _state)) == Failed && Volatile.Read(ref _lastFailure) is { } failure)
        {
            return Task.FromException(failure);
        }

        Task task;
        lock (_waiterGate)
        {
            _waiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            task = _waiter.Task;
        }

        // A build may have ended between the checks above and the start of the waiter: look again once it is there.
        EndWaitIfDone();
        return task;
    }

    /// <summary>
    /// Stops the build and gives back the index's reference to its snapshot (design §3.4 "破棄"). Waits for the jobs of
    /// the index that workers are running: at most 8 units, or one single job. A snapshot that a caller still borrows
    /// stays readable until its release. A second call does nothing.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            // A job that publishes reads the state in this lock (rule 5): after it, no snapshot becomes the current one.
            var state = Volatile.Read(ref _state);
            if (KindOf(state) == Disposed)
            {
                return;
            }

            // A unit that writes the copy sees the stop at its next check and writes no more: its first units take
            // 0.6 to 3.4 s over 1e9 points (docs/benchmarks/c3a-spatial-index.md). Before the state, so that whoever
            // reads Disposed finds the build stopped.
            Volatile.Read(ref _build)?.Stop();
            Interlocked.Exchange(ref _state, Pack(state >> KindBits, Disposed));

            // A worker that had read the state before may have put its build here since: it reads the state again
            // after it, and this reads the build again after the state.
            Volatile.Read(ref _build)?.Stop();
        }

        // Outside the lock, which a job in flight may be waiting for. After this, no job of the index runs.
        Work.BeginDispose();
        Pool.Unregister(Work);
        Withdraw();
        Volatile.Read(ref _current).Release();
        var disposed = new ObjectDisposedException(nameof(SpatialIndex));
        _completion.TrySetException(disposed);
        EndWait(disposed);
    }

    /// <summary>Makes the snapshot of the first count.</summary>
    /// <param name="contents">What the build counted.</param>
    /// <returns>The snapshot, which owns no native memory.</returns>
    private protected abstract SpatialSnapshot MakeCoarse(in SpatialSnapshot.Contents contents);

    /// <summary>Makes the snapshot of a finished build, which takes the table from the builder and owns the copy.</summary>
    /// <param name="contents">What the build counted.</param>
    /// <param name="builder">The builder, after the writing ended.</param>
    /// <param name="copy">The copy, or <see langword="null"/> when the index reads no rows.</param>
    /// <returns>The snapshot.</returns>
    private protected abstract SpatialSnapshot MakeFull(in SpatialSnapshot.Contents contents, SpatialTableBuilder builder, SpatialCopy? copy);

    /// <summary>Runs one unit of the stage that <see cref="SpatialBuild.UnitJob"/> names.</summary>
    /// <param name="build">The build.</param>
    /// <param name="group">The group of the unit, which the calling worker has.</param>
    /// <param name="unit">The unit.</param>
    private protected abstract void RunUnit(SpatialBuild build, int group, long unit);

    private static long Pack(long sequence, int kind) => (sequence << KindBits) | (uint)kind;

    private static int KindOf(long state) => (int)(state & KindMask);

    private static ScaleRange RangeOf(in SpatialBuild.Extent extent, bool integer) =>
        !extent.HasValue ? new ScaleRange(0, 0, 0)
        : integer ? ScaleRange.FromInteger(extent.Low, extent.High)
        : ScaleRange.FromDouble(extent.Least, extent.Greatest);

    private static void SetAxes(SpatialBuild build, ScaleRange x, ScaleRange y)
    {
        var bits = SpatialLayout.Plane.BitsPerAxis;
        build.XRange = x;
        build.YRange = y;
        build.XAxis = new SpatialAxis(x, bits);
        build.YAxis = new SpatialAxis(y, bits);
    }

    private int TryTake(bool requestedOnly)
    {
        // No query asks an index for a job: its jobs come after the requested chunks of the pyramids.
        if (requestedOnly)
        {
            return -1;
        }

        var state = Volatile.Read(ref _state);
        switch (KindOf(state))
        {
            case Waiting:
                // The worker that moves the state starts the build (rule 1).
                var counting = Pack((state >> KindBits) + 1, Counting);
                return Interlocked.CompareExchange(ref _state, counting, state) == state ? StartToken : -1;
            case Counting:
            case Writing:
            case Density:
                var build = Volatile.Read(ref _build);
                if (build is null || !build.Enter())
                {
                    return -1;
                }

                var token = build.TryTakeSingle() ? SingleToken : build.TryTakeGroup();
                if (token < 0)
                {
                    build.Leave();
                    return -1;
                }

                _taken = build;
                return token;
            default:
                return -1;
        }
    }

    private void RunJob(int token)
    {
        var build = _taken;
        _taken = null;
        var began = TimeProvider.System.GetTimestamp();
        try
        {
            if (token < StartToken)
            {
                var job = build!.UnitJob;
                if (job == SpatialJob.Density)
                {
                    build.Density!.RunUnit(token, build.NextUnit(token), build);
                }
                else
                {
                    RunUnit(build, token, build.NextUnit(token));
                }

                _tuning.JobEnded?.Invoke(job, TimeProvider.System.GetTimestamp() - began);
                if (build.EndUnit(token))
                {
                    Pool.Wake();
                }

                return;
            }

            SpatialJob ran;
            SpatialJob? next;
            if (token == StartToken)
            {
                var state = Volatile.Read(ref _state);
                if (KindOf(state) != Counting)
                {
                    // The index was disposed before the build began.
                    return;
                }

                build = new SpatialBuild(state >> KindBits);

                // A disposal that began before the build was here found none to stop. Each side writes with a full
                // barrier and then reads the other's word, so one of them sees the other: the build never runs on
                // under a Dispose that waits for it.
                Interlocked.Exchange(ref _build, build);
                if (KindOf(Volatile.Read(ref _state)) != Counting || Pool.IsDisposed)
                {
                    build.Stop();
                    return;
                }

                ran = SpatialJob.Allocate;
                next = Allocate(build);
            }
            else
            {
                ran = build!.SingleJob;
                next = RunSingle(build, ran);
            }

            try
            {
                _tuning.JobEnded?.Invoke(ran, TimeProvider.System.GetTimestamp() - began);
            }
            finally
            {
                // The index is complete whatever the listener does.
                if (build.Completed)
                {
                    _completion.TrySetResult();
                    EndWaitIfDone();
                }
            }

            if (next is { } job2)
            {
                Queue(build, job2);
            }
        }
        catch (Exception exception)
        {
            // Whatever a build throws stops it and becomes the fault (design §3.4 "失敗").
            Fail(build, exception);
        }
        finally
        {
            build?.Leave();
        }
    }

    // Queues what follows a single job: a stage of units, or another single job. It is the last thing the job does to
    // the build, for a worker may take what is queued at once.
    private void Queue(SpatialBuild build, SpatialJob next)
    {
        switch (next)
        {
            case SpatialJob.Range:
                build.BeginUnits(next, SpatialJob.EndRange);
                break;
            case SpatialJob.First:
                build.BeginUnits(next, SpatialJob.PublishCoarse);
                break;
            case SpatialJob.Pass:
                build.BeginUnits(next, SpatialJob.EndPass);
                break;
            case SpatialJob.Write:
                build.BeginUnits(next, SpatialJob.PublishFull);
                break;
            case SpatialJob.Density:
                build.LayDensity(build.Density!);
                build.BeginUnits(next, SpatialJob.EndDensityLevel);
                break;
            default:
                build.QueueSingle(next);
                break;
        }

        Pool.Wake();
    }

    private SpatialJob Allocate(SpatialBuild build)
    {
        // The versions are read before any value, so that a change during the build shows at its end (rule 2).
        build.XVersion = X.Version;
        build.YVersion = Y.Version;
        var length = Math.Min(X.Length, Y.Length);
        var start = Input.Rows.Start;
        var end = Math.Max(start, Input.Rows.EndIn(length));
        var rows = end - start;
        build.Lay(start, end, _tuning.RowsPerUnit);

        // MaxTableRows = max(1, ⌊TableShare × the bytes of the source / 32⌋) (design §3.4 "表の上限").
        var share = Math.Floor(_tuning.TableShare * rows * (_shape.XBytes + _shape.YBytes) / 32);
        build.Builder = new SpatialTableBuilder(SpatialLayout.Plane, _tuning, rows, (long)Math.Clamp(share, 1, int.MaxValue));
        if (rows > 0)
        {
            build.Copy = _allocator.Allocate(rows, [_shape.XStored, _shape.YStored], _shape.Mapped, Input.TempFolder, () => build.IsStopped);
            build.AllocateScratch(Input.XTransform is not null, Input.YTransform is not null);
        }

        if (Input.XRange is { } x && Input.YRange is { } y)
        {
            SetAxes(build, x, y);
            return SpatialJob.First;
        }

        return SpatialJob.Range;
    }

    private SpatialJob? RunSingle(SpatialBuild build, SpatialJob job)
    {
        var builder = build.Builder!;
        switch (job)
        {
            case SpatialJob.EndRange:
                SetAxes(
                    build,
                    Input.XRange ?? RangeOf(build.JoinExtents(0), _shape.XInteger),
                    Input.YRange ?? RangeOf(build.JoinExtents(1), _shape.YInteger)
                );
                return SpatialJob.First;
            case SpatialJob.PublishCoarse:
                var more = builder.EndFirst();
                PublishCoarse(build, builder);
                return more ? SpatialJob.Pass : SpatialJob.Table;
            case SpatialJob.EndPass:
                return builder.EndPass() ? SpatialJob.Pass : SpatialJob.Table;
            case SpatialJob.Table:
                builder.BuildTable();

                // Only Dispose moves the state under the one job of the build.
                return TryMove(build, Counting, Writing) ? SpatialJob.Write : null;
            case SpatialJob.PublishFull:
                return PublishFull(build, builder) ? SpatialJob.PlanDensity : null;
            case SpatialJob.PlanDensity:
                var bytes = (build.End - build.Start) * (_shape.XBytes + _shape.YBytes);
                build.Density = new DensityBuilder(build.Full!, _tuning.DensitySplit, _tuning.RowsPerUnit, bytes);
                return build.Density.NextLevel() ? SpatialJob.Density : SpatialJob.PublishDensity;
            case SpatialJob.EndDensityLevel:
                return build.Density!.NextLevel() ? SpatialJob.Density : SpatialJob.PublishDensity;
            default:
                PublishDensity(build);
                return null;
        }
    }

    private void PublishCoarse(SpatialBuild build, SpatialTableBuilder builder)
    {
        // Level 8 comes from the first counters, which the table stage frees; each level above is the sum of its four
        // children. A cell of level 8 is the upper 16 bits of a code: its x in the even bits and its y in the odd ones.
        const int Deepest = SpatialSnapshot.CoarseLevel;
        var coarse = new long[SpatialSnapshot.CoarseLength];
        var deepest = coarse.AsSpan(SpatialSnapshot.LevelOffset(Deepest));
        for (var cell = 0; cell < deepest.Length; cell++)
        {
            var x = (int)SpatialCode.Axis2((ulong)cell, 0);
            var y = (int)SpatialCode.Axis2((ulong)cell, 1);
            deepest[(y << Deepest) + x] = builder.FirstCount(cell);
        }

        for (var level = Deepest - 1; level >= 0; level--)
        {
            var side = 1 << level;
            var cells = coarse.AsSpan(SpatialSnapshot.LevelOffset(level), side * side);
            ReadOnlySpan<long> children = coarse.AsSpan(SpatialSnapshot.LevelOffset(level + 1), 4 * side * side);
            for (var y = 0; y < side; y++)
            {
                var upper = children.Slice(2 * y * 2 * side, 2 * side);
                var lower = children.Slice(((2 * y) + 1) * 2 * side, 2 * side);
                for (var x = 0; x < side; x++)
                {
                    cells[(y * side) + x] = upper[2 * x] + upper[(2 * x) + 1] + lower[2 * x] + lower[(2 * x) + 1];
                }
            }
        }

        build.Coarse = coarse;
        var contents = new SpatialSnapshot.Contents(
            build.XRange,
            build.YRange,
            coarse,
            builder.CountedPoints,
            builder.ExcludedCount,
            build.XVersion,
            build.YVersion,
            Disagreed: false
        );

        // A rebuild keeps the full snapshot of the build before it until its own is made (design §3.4 "公開の順").
        Publish(MakeCoarse(contents), unlessFull: true);
    }

    // Returns whether the build goes on to the density tiles: false when the index was disposed meanwhile.
    private bool PublishFull(SpatialBuild build, SpatialTableBuilder builder)
    {
        builder.FinishWrite(build.Copy);
        var contents = new SpatialSnapshot.Contents(
            build.XRange,
            build.YRange,
            build.Coarse!,
            builder.PointCount,
            builder.ExcludedCount,
            build.XVersion,
            build.YVersion,
            builder.Disagreed
        );
        var made = MakeFull(contents, builder, build.Copy);

        // The snapshot owns the copy from here on.
        build.Copy = null;

        // The build reads the table and the copy of its snapshot for the density tiles, so it borrows the snapshot as a
        // query does: a Dispose that gives the index's reference back meanwhile does not free it under the units.
        if (!Publish(made, unlessFull: false) || !made.TryAddReference())
        {
            return false;
        }

        build.Full = made;
        return TryMove(build, Writing, Density);
    }

    private void PublishDensity(SpatialBuild build)
    {
        var tiles = build.Density!.Finish();
        bool attached;
        lock (_gate)
        {
            // As a snapshot, the tiles are not published after Dispose (rule 5).
            attached = KindOf(Volatile.Read(ref _state)) != Disposed;
            if (attached)
            {
                build.Full!.AttachTiles(tiles);
                Volatile.Write(ref _version, _version + 1);
            }
        }

        if (!attached)
        {
            tiles.Dispose();
        }

        build.Stop();
        Interlocked.CompareExchange(ref _build, null, build);
        if (X.Version == build.XVersion && Y.Version == build.YVersion)
        {
            // The job that ran tells the waiters, after JobEnded: what waits for the index finds every job reported.
            build.Completed = TryMove(build, Density, Complete);
        }
        else if (TryMove(build, Density, Waiting))
        {
            // A column changed during the build: the index is built again (rule 2).
            Pool.Wake();
        }
    }

    private bool TryMove(SpatialBuild build, int from, int to) =>
        Interlocked.CompareExchange(ref _state, Pack(build.Sequence, to), Pack(build.Sequence, from)) == Pack(build.Sequence, from);

    // Returns whether the snapshot became the current one.
    private bool Publish(SpatialSnapshot made, bool unlessFull)
    {
        SpatialSnapshot? replaced = null;
        lock (_gate)
        {
            // Dispose sets its state in this lock (rule 5), so a snapshot is not published after the index gave its
            // reference back.
            if (KindOf(Volatile.Read(ref _state)) != Disposed && !(unlessFull && _current.Stage == SpatialStage.Full))
            {
                replaced = _current;
                Volatile.Write(ref _current, made);
                Volatile.Write(ref _version, _version + 1);
            }
        }

        if (replaced is null)
        {
            made.Free();
            return false;
        }

        replaced.Release();
        return true;
    }

    private void Fail(SpatialBuild? build, Exception exception)
    {
        // Only the first job that fails a build goes on: the units of the others end on their own.
        if (build is not null && !build.Stop())
        {
            return;
        }

        Interlocked.CompareExchange(ref _build, null, build);
        Interlocked.CompareExchange(ref _fault, exception, null);

        // What rule 4 and a waiter read is there before the state says the build failed.
        Volatile.Write(ref _lastFailure, exception);
        Volatile.Write(ref _failedX, build?.XVersion ?? X.Version);
        Volatile.Write(ref _failedY, build?.YVersion ?? Y.Version);
        var state = Volatile.Read(ref _state);
        var kind = KindOf(state);
        if (
            (kind != Counting && kind != Writing && kind != Density)
            || Interlocked.CompareExchange(ref _state, Pack(state >> KindBits, Failed), state) != state
        )
        {
            // The index was disposed meanwhile.
            return;
        }

        _completion.TrySetException(exception);
        EndWait(exception);
    }

    // Rules 3 and 4: a finished or a failed index whose columns have moved waits for a build again. Neither allocates
    // nor waits; an index that is waiting or being built is left alone.
    private void Reconsider()
    {
        if (Volatile.Read(ref _poolGone) is not null)
        {
            return;
        }

        var state = Volatile.Read(ref _state);
        var stale = KindOf(state) switch
        {
            Complete => !Volatile.Read(ref _current).MatchesColumns,
            Failed => X.Version != Volatile.Read(ref _failedX) || Y.Version != Volatile.Read(ref _failedY),
            _ => false,
        };
        if (stale && Interlocked.CompareExchange(ref _state, Pack(state >> KindBits, Waiting), state) == state)
        {
            Pool.Wake();
        }
    }

    private void EndWaitIfDone()
    {
        if (Volatile.Read(ref _waiter) is null)
        {
            return;
        }

        if (KindOf(Volatile.Read(ref _state)) == Disposed)
        {
            EndWait(new ObjectDisposedException(nameof(SpatialIndex)));
        }
        else if (Volatile.Read(ref _poolGone) is { } gone)
        {
            EndWait(gone);
        }
        else if (IsComplete)
        {
            EndWait(null);
        }
        else
        {
            // A build that ended for older versions is queued again, and the wait goes on.
            Reconsider();
            if (KindOf(Volatile.Read(ref _state)) == Failed && Volatile.Read(ref _lastFailure) is { } failure)
            {
                EndWait(failure);
            }
        }
    }

    private void EndWait(Exception? exception)
    {
        TaskCompletionSource? waiter;
        lock (_waiterGate)
        {
            waiter = _waiter;
            _waiter = null;
        }

        if (exception is null)
        {
            waiter?.TrySetResult();
        }
        else
        {
            waiter?.TrySetException(exception);
        }
    }

    // Frees the build that was running. No job of the index runs: the caller waited for them, or the pool has stopped.
    private void Withdraw()
    {
        if (Interlocked.Exchange(ref _build, null) is { } build)
        {
            build.Stop();
            build.Free();
        }
    }

    private void OnPoolDisposed()
    {
        // Nothing builds the index again: its snapshot stays, and what waits for a build ends (design §3.4 "プールの破棄").
        var gone = new ObjectDisposedException(nameof(PlotWorkerPool));
        Volatile.Write(ref _poolGone, gone);
        Interlocked.CompareExchange(ref _fault, gone, null);
        Withdraw();
        _completion.TrySetException(gone);
        EndWait(gone);
    }

    /// <summary>What the element types and the storage of the two columns decide for a build.</summary>
    /// <param name="XBytes">The bytes of one element of the x column.</param>
    /// <param name="YBytes">The bytes of one element of the y column.</param>
    /// <param name="XInteger">Whether x holds integers and has no transform, so that its range has an exact origin.</param>
    /// <param name="YInteger">Whether y holds integers and has no transform.</param>
    /// <param name="XStored">The bytes of one x value of the copy.</param>
    /// <param name="YStored">The bytes of one y value of the copy.</param>
    /// <param name="Mapped">Whether a column is memory-mapped, so that the copy goes to a temporary file.</param>
    private protected readonly record struct Shape(int XBytes, int YBytes, bool XInteger, bool YInteger, int XStored, int YStored, bool Mapped);

    private sealed class WorkItem(SpatialIndex index) : PoolWork
    {
        internal override void OnPoolDisposed() => index.OnPoolDisposed();

        // The pool waits for the unit in flight: one that writes the copy stops at its next check, as for Dispose.
        // A worker that puts a build here afterwards reads the pool's flag after it (RunJob).
        internal override void OnPoolDisposing() => Volatile.Read(ref index._build)?.Stop();

        private protected override int TryTake(bool requestedOnly) => index.TryTake(requestedOnly);

        private protected override void Build(int chunk) => index.RunJob(chunk);
    }
}
