using System.Numerics;
using System.Runtime.InteropServices;

namespace HyperPlot;

/// <summary>
/// Work that a <see cref="PlotWorkerPool"/> drives a chunk at a time (a pyramid, or statistics computed ahead), without the
/// element type, so that one pool holds work of every type.
/// </summary>
internal abstract class PoolWork
{
    private int _inFlight;
    private int _disposed;

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Takes one chunk to build and builds it. Returns <see langword="false"/> when there was none to take.</summary>
    /// <param name="requestedOnly">Take only a chunk that a query asked for.</param>
    internal bool RunOne(bool requestedOnly)
    {
        // The count rises before the check, so that Dispose, which sets the flag and then waits for the count to reach 0,
        // never frees a block that this call writes.
        Interlocked.Increment(ref _inFlight);
        try
        {
            if (IsDisposed)
            {
                return false;
            }

            var chunk = TryTake(requestedOnly);
            if (chunk < 0)
            {
                return false;
            }

            Build(chunk);
            return true;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    /// <summary>Called once the pool has stopped its threads with work of this pyramid left.</summary>
    internal abstract void OnPoolDisposed();

    private protected abstract int TryTake(bool requestedOnly);

    private protected abstract void Build(int chunk);

    /// <summary>Marks the pyramid disposed and waits for the chunk a worker is building, if any. Returns false if it was already disposed.</summary>
    internal bool BeginDispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return false;
        }

        // At most one chunk build per worker is in flight (design §8.1: Dispose waits for one chunk, invariant 7).
        var spin = default(SpinWait);
        while (Volatile.Read(ref _inFlight) != 0)
        {
            spin.SpinOnce();
        }

        return true;
    }
}

/// <summary>
/// A min/max pyramid over one column, built chunk by chunk in the background by a <see cref="PlotWorkerPool"/> (design §3.3).
/// Make one with <see cref="PlotWorkerPool.BuildPyramid{T}(Column{T})"/>.
/// </summary>
/// <typeparam name="T">The element type of the column.</typeparam>
/// <remarks>
/// Queries may run on any thread while the pyramid is built. A chunk that is not built yet is answered from its chunk
/// statistics and marked provisional. Dispose the pyramid before its column, and not beside a query.
/// </remarks>
public sealed unsafe class MinMaxPyramid<T> : IDisposable
    where T : unmanaged, INumber<T>
{
    private const int Unbuilt = 0;
    private const int Requested = 1;
    private const int Building = 2;
    private const int Done = 3;
    private const int Failed = 4;

    /// <summary>
    /// What a search inside a read returns when the block disagrees with the raw values: a rebuild overlapped the read, or the
    /// caller changed values without Invalidate yet.
    /// </summary>
    private const int Inconsistent = -2;

    private readonly PlotWorkerPool _pool;
    private readonly PyramidLayout _fullLayout;
    private readonly PyramidLayout _lastLayout;
    private readonly nint[] _blocks;
    private readonly int[] _states;
    private readonly long[] _sequences;
    private readonly long[] _builtGenerations;
    private readonly long[] _failedGenerations;
    private readonly int[] _passed;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _firstPassRemaining;
    private long _version;
    private Exception? _fault;

    internal MinMaxPyramid(PlotWorkerPool pool, Column<T> column)
    {
        _pool = pool;
        Work = new WorkItem(this);
        Column = column;
        var chunks = column.ChunkCount;
        _fullLayout = new PyramidLayout(HyperPlot.Column.ChunkLength, sizeof(T));
        _lastLayout = chunks == 0 ? _fullLayout : new PyramidLayout(column.GetChunkLength(chunks - 1), sizeof(T));
        _blocks = new nint[chunks];
        _states = new int[chunks];
        _sequences = new long[chunks];
        _builtGenerations = new long[chunks];
        _failedGenerations = new long[chunks];
        _passed = new int[chunks];
        _firstPassRemaining = chunks;
        try
        {
            for (var chunk = 0; chunk < chunks; chunk++)
            {
                _blocks[chunk] = (nint)NativeMemory.AlignedAlloc((nuint)LayoutOf(chunk).ByteCount, 64);
            }
        }
        catch
        {
            FreeBlocks();
            throw;
        }

        if (chunks == 0)
        {
            _completion.TrySetResult();
        }
    }

    /// <summary>Gets the column the pyramid is built over.</summary>
    public Column<T> Column { get; }

    /// <summary>Gets a number that grows each time a chunk is built. A provisional result may change when it does.</summary>
    public long Version => Volatile.Read(ref _version);

    /// <summary>Gets a value indicating whether every chunk is built for the column's current values.</summary>
    public bool IsComplete
    {
        get
        {
            for (var chunk = 0; chunk < _states.Length; chunk++)
            {
                if (Volatile.Read(ref _states[chunk]) != Done || Volatile.Read(ref _builtGenerations[chunk]) != Column.GetGeneration(chunk))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Gets a task that ends when every chunk has been built once, or has failed. It ends with <see cref="Fault"/> when a
    /// chunk failed, and with <see cref="ObjectDisposedException"/> when the pyramid or its pool was disposed first.
    /// </summary>
    public Task Completion => _completion.Task;

    /// <summary>Gets the first exception a chunk build threw, or <see langword="null"/>.</summary>
    public Exception? Fault => Volatile.Read(ref _fault);

    /// <summary>Gets or sets a call made between a reader's check of a built chunk's state and its first read of the sequence. Tests only.</summary>
    internal Action? TestBeforeSequenceRead { get; set; }

    /// <summary>Gets or sets a call made after a reader's first read of the sequence, before it reads the block. Tests only.</summary>
    internal Action? TestAfterSequenceRead { get; set; }

    /// <summary>Gets or sets a call a worker makes after writing a block, before the sequence goes even again. Tests only.</summary>
    internal Action? TestBlockWritten { get; set; }

    /// <summary>Gets the part of the pyramid that the pool drives.</summary>
    internal PoolWork Work { get; }

    /// <summary>Gets the bytes of native memory the pyramid's blocks take.</summary>
    internal long BlockBytes => _blocks.Length == 0 ? 0 : (((long)_blocks.Length - 1) * _fullLayout.ByteCount) + _lastLayout.ByteCount;

    /// <summary>
    /// Returns the exact minimum and maximum of a range (design §3.3). Both ends go down to the raw values; chunks whose
    /// pyramid is not built are answered from their statistics and make the result provisional. Does not allocate.
    /// </summary>
    /// <param name="start">The first element.</param>
    /// <param name="length">The number of elements. 0 gives a result whose <see cref="MinMax{T}.Count"/> is 0.</param>
    /// <returns>The minimum and the maximum.</returns>
    public MinMax<T> GetMinMax(long start, long length)
    {
        ThrowIfDisposed();
        if (start < 0 || length < 0 || start > Column.Length - length)
        {
            ThrowHelper.ThrowRangeOutOfColumn(start, length, Column.Length);
        }

        Query(start, start + length, wantCount: true, out var result);
        return result.Found
            ? new MinMax<T>(result.Min, result.Max, result.Count, result.HasNaN, result.IsProvisional)
            : new MinMax<T>(default, default, 0, result.HasNaN, result.IsProvisional);
    }

    /// <summary>
    /// Stops building and frees the blocks. Waits for the chunk a worker is building, if any (design §8.1). Dispose the
    /// pyramid before its column.
    /// </summary>
    public void Dispose()
    {
        if (!Work.BeginDispose())
        {
            return;
        }

        _pool.Unregister(Work);
        FreeBlocks();
        _completion.TrySetException(new ObjectDisposedException(nameof(MinMaxPyramid<T>)));
    }

    /// <summary>Gets a chunk's state, for tests: 0 unbuilt, 1 requested, 2 building, 3 done, 4 failed.</summary>
    internal int GetState(int chunk) => Volatile.Read(ref _states[chunk]);

    /// <summary>Requests a built chunk again, as a query that found it stale would. Tests only.</summary>
    internal void RequestAgain(int chunk) => Request(chunk, Done);

    /// <summary>
    /// Computes the minimum and the maximum of [start, end) with the positions of their first occurrences, and the count when
    /// asked. The caller checked the range.
    /// </summary>
    internal void Query(long start, long end, bool wantCount, out RangeResult result)
    {
        result = default;
        if (start >= end)
        {
            return;
        }

        var firstChunk = (int)(start >> HyperPlot.Column.ChunkShift);
        var lastChunk = (int)((end - 1) >> HyperPlot.Column.ChunkShift);
        for (var chunk = firstChunk; chunk <= lastChunk; chunk++)
        {
            var chunkStart = (long)chunk << HyperPlot.Column.ChunkShift;
            var layout = LayoutOf(chunk);
            var lo = (int)(Math.Max(start, chunkStart) - chunkStart);
            var hi = (int)(Math.Min(end, chunkStart + layout.ChunkLength) - chunkStart);
            var whole = lo == 0 && hi == layout.ChunkLength;
            if (whole && wantCount)
            {
                // The chunk statistics are exact for a whole chunk, built or not; the positions are not needed here.
                MergeStats(ref result, Column.GetStats(chunk), chunkStart, chunkStart + hi - 1, provisional: false);
                continue;
            }

            if (TryBeginRead(chunk, out var sequence))
            {
                TestAfterSequenceRead?.Invoke();
                var local = default(LocalResult);
                QueryChunk(chunk, layout, lo, hi, wantCount, ref local);

                // The position of an extremum is missing when the raw values changed under the block: a rebuild overlapped the
                // read, or the caller wrote values and has not called Invalidate yet. Either way the chunk is answered provisionally.
                if (EndRead(chunk, sequence) && (!local.Found || (local.MinIndex >= 0 && local.MaxIndex >= 0)))
                {
                    Merge(ref result, in local, chunkStart);
                    continue;
                }
            }

            MergeStats(ref result, Column.GetStats(chunk), chunkStart + lo, chunkStart + hi - 1, provisional: true);
        }
    }

    /// <summary>
    /// Returns the first (or last) element in [start, end) that is not NaN, or -1. Skips buckets that are all NaN; a chunk
    /// whose pyramid is not built is scanned and sets <paramref name="provisional"/>.
    /// </summary>
    internal long FindNumber(long start, long end, bool fromEnd, ref bool provisional)
    {
        if (start >= end)
        {
            return -1;
        }

        var firstChunk = (int)(start >> HyperPlot.Column.ChunkShift);
        var lastChunk = (int)((end - 1) >> HyperPlot.Column.ChunkShift);
        for (var step = 0; step <= lastChunk - firstChunk; step++)
        {
            var chunk = fromEnd ? lastChunk - step : firstChunk + step;
            var chunkStart = (long)chunk << HyperPlot.Column.ChunkShift;
            var layout = LayoutOf(chunk);
            var lo = (int)(Math.Max(start, chunkStart) - chunkStart);
            var hi = (int)(Math.Min(end, chunkStart + layout.ChunkLength) - chunkStart);
            var found = -1;
            if (TryBeginRead(chunk, out var sequence))
            {
                found = fromEnd ? FindLastInChunk(chunk, layout, lo, hi) : FindFirstInChunk(chunk, layout, lo, hi);

                // As in Query, a block that disagrees with the raw values (Inconsistent) is not trusted.
                if (!EndRead(chunk, sequence) || found == Inconsistent)
                {
                    provisional = true;
                    found = ScanForNumber(Column.GetChunk(chunk), lo, hi, fromEnd);
                }
            }
            else
            {
                provisional = true;
                found = ScanForNumber(Column.GetChunk(chunk), lo, hi, fromEnd);
            }

            if (found >= 0)
            {
                return chunkStart + found;
            }
        }

        return -1;
    }

    private void OnPoolDisposed()
    {
        // No chunk is built again after the pool is gone, even after an Invalidate, so the fault is set in every case.
        var disposed = new ObjectDisposedException(nameof(PlotWorkerPool));
        Interlocked.CompareExchange(ref _fault, disposed, null);
        _completion.TrySetException(disposed);
    }

    private int TryTake(bool requestedOnly)
    {
        for (var chunk = 0; chunk < _states.Length; chunk++)
        {
            if (Interlocked.CompareExchange(ref _states[chunk], Building, Requested) == Requested)
            {
                return chunk;
            }
        }

        if (requestedOnly)
        {
            return -1;
        }

        for (var chunk = 0; chunk < _states.Length; chunk++)
        {
            if (Interlocked.CompareExchange(ref _states[chunk], Building, Unbuilt) == Unbuilt)
            {
                return chunk;
            }
        }

        return -1;
    }

    private void Build(int chunk)
    {
        var generation = Column.GetGeneration(chunk);
        try
        {
            // The statistics are computed ahead here, so that queries of whole chunks find them (design §3.2).
            _ = Column.GetStats(chunk);
            var values = Column.GetChunk(chunk);

            // Rule 1 makes this worker the only writer of the block, so the sequence goes odd and even again (seqlock).
            Interlocked.Increment(ref _sequences[chunk]);
            try
            {
                BuildBlock(chunk, values);
                Volatile.Write(ref _builtGenerations[chunk], generation);
                TestBlockWritten?.Invoke();
            }
            finally
            {
                Interlocked.Increment(ref _sequences[chunk]);
            }
        }
        catch (Exception exception)
        {
            // Any exception fails the chunk (rule 3): the next build waits for the chunk's generation to move.
            Volatile.Write(ref _failedGenerations[chunk], generation);
            Interlocked.CompareExchange(ref _fault, exception, null);
            Volatile.Write(ref _states[chunk], Failed);
            CountFirstPass(chunk);
            return;
        }

        // Rule 2: an Invalidate that came while the block was written leaves the chunk unbuilt, and this pool takes it again.
        if (Column.GetGeneration(chunk) == generation)
        {
            Volatile.Write(ref _states[chunk], Done);
            Interlocked.Increment(ref _version);
            CountFirstPass(chunk);
        }
        else
        {
            Volatile.Write(ref _states[chunk], Unbuilt);
            _pool.Wake();
        }
    }

    private static int ScanForNumber(ReadOnlySpan<T> values, int lo, int hi, bool fromEnd)
    {
        if (fromEnd)
        {
            for (var i = hi - 1; i >= lo; i--)
            {
                if (!T.IsNaN(values[i]))
                {
                    return i;
                }
            }
        }
        else
        {
            for (var i = lo; i < hi; i++)
            {
                if (!T.IsNaN(values[i]))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static int FirstEqual(ReadOnlySpan<T> values, int lo, int hi, T target)
    {
        for (var i = lo; i < hi; i++)
        {
            if (values[i] == target)
            {
                return i;
            }
        }

        return -1;
    }

    private static void MergeStats(ref RangeResult result, ChunkStats<T> stats, long firstIndex, long lastIndex, bool provisional)
    {
        result.IsProvisional |= provisional;
        result.HasNaN |= stats.NaNCount > 0;
        if (stats.Count == 0)
        {
            return;
        }

        result.Count += stats.Count;

        // The statistics carry no positions: a provisional minimum takes the range's first element, a maximum its last.
        if (!result.Found || stats.Min < result.Min)
        {
            (result.Min, result.MinIndex) = (stats.Min, firstIndex);
        }

        if (!result.Found || stats.Max > result.Max)
        {
            (result.Max, result.MaxIndex) = (stats.Max, lastIndex);
        }

        result.Found = true;
    }

    private static void Merge(ref RangeResult result, in LocalResult local, long chunkStart)
    {
        result.HasNaN |= local.HasNaN;
        result.Count += local.Count;
        if (!local.Found)
        {
            return;
        }

        // Chunks come in order, so a strict comparison keeps the first occurrence.
        if (!result.Found || local.Min < result.Min)
        {
            (result.Min, result.MinIndex) = (local.Min, chunkStart + local.MinIndex);
        }

        if (!result.Found || local.Max > result.Max)
        {
            (result.Max, result.MaxIndex) = (local.Max, chunkStart + local.MaxIndex);
        }

        result.Found = true;
    }

    private PyramidLayout LayoutOf(int chunk) => chunk == _blocks.Length - 1 ? _lastLayout : _fullLayout;

    internal void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Work.IsDisposed, this);
    }

    private void CountFirstPass(int chunk)
    {
        if (Interlocked.Exchange(ref _passed[chunk], 1) != 0 || Interlocked.Decrement(ref _firstPassRemaining) != 0)
        {
            return;
        }

        var fault = Fault;
        if (fault is null)
        {
            _completion.TrySetResult();
        }
        else
        {
            _completion.TrySetException(fault);
        }
    }

    /// <summary>
    /// Returns true when the block holds the chunk's current values and no worker writes it; otherwise requests the chunk
    /// when the state table allows it (design §3.3) and returns false. Neither allocates nor waits.
    /// </summary>
    private bool TryBeginRead(int chunk, out long sequence)
    {
        sequence = 0;
        var generation = Column.GetGeneration(chunk);
        switch (Volatile.Read(ref _states[chunk]))
        {
            case Done:
                if (Volatile.Read(ref _builtGenerations[chunk]) != generation)
                {
                    Request(chunk, Done);
                    return false;
                }

                TestBeforeSequenceRead?.Invoke();
                sequence = Volatile.Read(ref _sequences[chunk]);
                return (sequence & 1) == 0;
            case Unbuilt:
                Request(chunk, Unbuilt);
                return false;
            case Failed:
                // Rule 3: a failed chunk is queued again only after its generation moved.
                if (Volatile.Read(ref _failedGenerations[chunk]) != generation)
                {
                    Request(chunk, Failed);
                }

                return false;
            default:
                return false;
        }
    }

    private bool EndRead(int chunk, long sequence)
    {
        // The reads of the block happen before the second read of the sequence.
        Interlocked.MemoryBarrier();
        return Volatile.Read(ref _sequences[chunk]) == sequence;
    }

    private void Request(int chunk, int from)
    {
        if (Interlocked.CompareExchange(ref _states[chunk], Requested, from) == from)
        {
            _pool.Wake();
        }
    }

    private void FreeBlocks()
    {
        for (var chunk = 0; chunk < _blocks.Length; chunk++)
        {
            var block = _blocks[chunk];
            _blocks[chunk] = 0;
            if (block != 0)
            {
                NativeMemory.AlignedFree((void*)block);
            }
        }
    }

    // The spans below cover regions of a block that the constructor allocated for the chunk's layout, and live no longer
    // than the call that made them. Blocks are freed only in Dispose, which must not run beside a query or a build.
    private Span<T> Mins(int chunk, PyramidLayout layout, int level) => new((byte*)_blocks[chunk] + layout.MinOffset(level), layout.Count(level));

    private Span<T> Maxs(int chunk, PyramidLayout layout, int level) => new((byte*)_blocks[chunk] + layout.MaxOffset(level), layout.Count(level));

    private Span<byte> States(int chunk, PyramidLayout layout, int level) =>
        new((byte*)_blocks[chunk] + layout.StateOffset(level), (layout.Count(level) + 3) >> 2);

    private BucketNaN StateOf(int chunk, PyramidLayout layout, int level, int bucket) =>
        (BucketNaN)((States(chunk, layout, level)[bucket >> 2] >> ((bucket & 3) * 2)) & 3);

    private void BuildBlock(int chunk, ReadOnlySpan<T> values)
    {
        var layout = LayoutOf(chunk);
        for (var level = 0; level < PyramidLayout.LevelCount; level++)
        {
            States(chunk, layout, level).Clear();
        }

        var mins = Mins(chunk, layout, 0);
        var maxs = Maxs(chunk, layout, 0);
        var states = States(chunk, layout, 0);
        for (var bucket = 0; bucket < layout.Count(0); bucket++)
        {
            var first = bucket << PyramidLayout.FirstShift;
            var slice = values.Slice(first, Math.Min(PyramidLayout.BucketSize(0), values.Length - first));
            var numbers = PyramidKernel.MinMax(slice, out mins[bucket], out maxs[bucket]);
            var state =
                numbers == slice.Length ? BucketNaN.None
                : numbers == 0 ? BucketNaN.All
                : BucketNaN.Some;
            states[bucket >> 2] |= (byte)((int)state << ((bucket & 3) * 2));
        }

        for (var level = 1; level < PyramidLayout.LevelCount; level++)
        {
            var childMins = Mins(chunk, layout, level - 1);
            var childMaxs = Maxs(chunk, layout, level - 1);
            var childCount = layout.Count(level - 1);
            mins = Mins(chunk, layout, level);
            maxs = Maxs(chunk, layout, level);
            states = States(chunk, layout, level);
            for (var bucket = 0; bucket < layout.Count(level); bucket++)
            {
                var found = false;
                var anyNaN = false;
                var min = default(T);
                var max = default(T);
                var end = Math.Min((bucket + 1) << PyramidLayout.ShiftStep, childCount);
                for (var child = bucket << PyramidLayout.ShiftStep; child < end; child++)
                {
                    var childState = StateOf(chunk, layout, level - 1, child);
                    anyNaN |= childState != BucketNaN.None;
                    if (childState == BucketNaN.All)
                    {
                        continue;
                    }

                    min = found ? T.Min(min, childMins[child]) : childMins[child];
                    max = found ? T.Max(max, childMaxs[child]) : childMaxs[child];
                    found = true;
                }

                (mins[bucket], maxs[bucket]) = (min, max);
                var state =
                    !anyNaN ? BucketNaN.None
                    : found ? BucketNaN.Some
                    : BucketNaN.All;
                states[bucket >> 2] |= (byte)((int)state << ((bucket & 3) * 2));
            }
        }
    }

    /// <summary>
    /// Covers [lo, hi) of a chunk with raw runs at the ends (at most 63 elements each) and the coarsest whole buckets
    /// between (design §3.3), and locates the first occurrence of the minimum and the maximum.
    /// </summary>
    private void QueryChunk(int chunk, PyramidLayout layout, int lo, int hi, bool wantCount, ref LocalResult local)
    {
        var values = Column.GetChunk(chunk);
        var minPiece = default(Piece);
        var maxPiece = default(Piece);
        var position = lo;
        while (position < hi)
        {
            var piece = NextPiece(layout, position, hi);
            T min;
            T max;
            if (piece.Level < 0)
            {
                var slice = values[piece.Start..piece.End];
                var numbers = PyramidKernel.MinMax(slice, out min, out max);
                local.Count += numbers;
                local.HasNaN |= numbers < slice.Length;
                if (numbers == 0)
                {
                    position = piece.End;
                    continue;
                }
            }
            else
            {
                var state = StateOf(chunk, layout, piece.Level, piece.Bucket);
                local.HasNaN |= state != BucketNaN.None;
                if (state == BucketNaN.All)
                {
                    position = piece.End;
                    continue;
                }

                if (wantCount)
                {
                    local.Count +=
                        state == BucketNaN.None ? piece.End - piece.Start : CountInBucket(chunk, layout, values, piece.Level, piece.Bucket);
                }

                min = Mins(chunk, layout, piece.Level)[piece.Bucket];
                max = Maxs(chunk, layout, piece.Level)[piece.Bucket];
            }

            if (!local.Found || min < local.Min)
            {
                (local.Min, minPiece) = (min, piece);
            }

            if (!local.Found || max > local.Max)
            {
                (local.Max, maxPiece) = (max, piece);
            }

            local.Found = true;
            position = piece.End;
        }

        if (local.Found)
        {
            local.MinIndex = Locate(chunk, layout, values, minPiece, local.Min, isMin: true);
            local.MaxIndex = Locate(chunk, layout, values, maxPiece, local.Max, isMin: false);
        }
    }

    private static Piece NextPiece(PyramidLayout layout, int position, int hi)
    {
        for (var level = PyramidLayout.LevelCount - 1; level >= 0; level--)
        {
            var size = PyramidLayout.BucketSize(level);
            if ((position & (size - 1)) != 0)
            {
                continue;
            }

            var end = Math.Min(position + size, layout.ChunkLength);
            if (end <= hi)
            {
                return new Piece(level, position >> PyramidLayout.Shift(level), position, end);
            }
        }

        // A raw run up to the next 64-element boundary, or to the end of the range.
        return new Piece(-1, 0, position, Math.Min(hi, (position | (PyramidLayout.BucketSize(0) - 1)) + 1));
    }

    private int Locate(int chunk, PyramidLayout layout, ReadOnlySpan<T> values, Piece piece, T target, bool isMin)
    {
        if (piece.Level < 0)
        {
            return FirstEqual(values, piece.Start, piece.End, target);
        }

        var level = piece.Level;
        var bucket = piece.Bucket;
        while (level > 0)
        {
            var childValues = isMin ? Mins(chunk, layout, level - 1) : Maxs(chunk, layout, level - 1);
            var end = Math.Min((bucket + 1) << PyramidLayout.ShiftStep, layout.Count(level - 1));
            var next = -1;
            for (var child = bucket << PyramidLayout.ShiftStep; child < end; child++)
            {
                if (StateOf(chunk, layout, level - 1, child) != BucketNaN.All && childValues[child] == target)
                {
                    next = child;
                    break;
                }
            }

            if (next < 0)
            {
                // The block disagrees with the raw values; the caller answers the chunk provisionally.
                return -1;
            }

            bucket = next;
            level--;
        }

        var start = bucket << PyramidLayout.FirstShift;
        return FirstEqual(values, start, Math.Min(start + PyramidLayout.BucketSize(0), values.Length), target);
    }

    private long CountInBucket(int chunk, PyramidLayout layout, ReadOnlySpan<T> values, int level, int bucket)
    {
        var state = StateOf(chunk, layout, level, bucket);
        var start = bucket << PyramidLayout.Shift(level);
        var end = Math.Min(start + PyramidLayout.BucketSize(level), layout.ChunkLength);
        switch (state)
        {
            case BucketNaN.None:
                return end - start;
            case BucketNaN.All:
                return 0;
        }

        if (level == 0)
        {
            return PyramidKernel.CountNumbers(values[start..end]);
        }

        long count = 0;
        var childEnd = Math.Min((bucket + 1) << PyramidLayout.ShiftStep, layout.Count(level - 1));
        for (var child = bucket << PyramidLayout.ShiftStep; child < childEnd; child++)
        {
            count += CountInBucket(chunk, layout, values, level - 1, child);
        }

        return count;
    }

    private int FindFirstInChunk(int chunk, PyramidLayout layout, int lo, int hi)
    {
        var values = Column.GetChunk(chunk);
        var position = lo;
        while (position < hi)
        {
            var piece = NextPiece(layout, position, hi);
            if (piece.Level < 0)
            {
                var found = ScanForNumber(values, piece.Start, piece.End, fromEnd: false);
                if (found >= 0)
                {
                    return found;
                }
            }
            else if (StateOf(chunk, layout, piece.Level, piece.Bucket) != BucketNaN.All)
            {
                return DescendToNumber(chunk, layout, values, piece.Level, piece.Bucket, fromEnd: false);
            }

            position = piece.End;
        }

        return -1;
    }

    private int FindLastInChunk(int chunk, PyramidLayout layout, int lo, int hi)
    {
        var values = Column.GetChunk(chunk);
        var position = hi;
        while (position > lo)
        {
            var piece = PreviousPiece(layout, lo, position);
            if (piece.Level < 0)
            {
                var found = ScanForNumber(values, piece.Start, piece.End, fromEnd: true);
                if (found >= 0)
                {
                    return found;
                }
            }
            else if (StateOf(chunk, layout, piece.Level, piece.Bucket) != BucketNaN.All)
            {
                return DescendToNumber(chunk, layout, values, piece.Level, piece.Bucket, fromEnd: true);
            }

            position = piece.Start;
        }

        return -1;
    }

    /// <summary>Returns the first (or last) element of a bucket that is not all NaN, skipping children that are.</summary>
    private int DescendToNumber(int chunk, PyramidLayout layout, ReadOnlySpan<T> values, int level, int bucket, bool fromEnd)
    {
        while (true)
        {
            var start = bucket << PyramidLayout.Shift(level);
            var end = Math.Min(start + PyramidLayout.BucketSize(level), layout.ChunkLength);
            var state = StateOf(chunk, layout, level, bucket);
            if (state == BucketNaN.None)
            {
                return fromEnd ? end - 1 : start;
            }

            if (level == 0)
            {
                var found = ScanForNumber(values, start, end, fromEnd);
                return found >= 0 ? found : Inconsistent;
            }

            var first = bucket << PyramidLayout.ShiftStep;
            var last = Math.Min(first + (1 << PyramidLayout.ShiftStep), layout.Count(level - 1)) - 1;
            var next = -1;
            for (var step = 0; step <= last - first; step++)
            {
                var child = fromEnd ? last - step : first + step;
                if (StateOf(chunk, layout, level - 1, child) != BucketNaN.All)
                {
                    next = child;
                    break;
                }
            }

            if (next < 0)
            {
                // The block disagrees with the raw values; the caller answers the chunk provisionally.
                return Inconsistent;
            }

            bucket = next;
            level--;
        }
    }

    private static Piece PreviousPiece(PyramidLayout layout, int lo, int position)
    {
        for (var level = PyramidLayout.LevelCount - 1; level >= 0; level--)
        {
            var shift = PyramidLayout.Shift(level);
            var bucket = (position - 1) >> shift;
            var start = bucket << shift;
            var end = Math.Min(start + PyramidLayout.BucketSize(level), layout.ChunkLength);
            if (end == position && start >= lo)
            {
                return new Piece(level, bucket, start, position);
            }
        }

        return new Piece(-1, 0, Math.Max(lo, (position - 1) & ~(PyramidLayout.BucketSize(0) - 1)), position);
    }

    /// <summary>Forwards the pool's calls to the pyramid.</summary>
    private sealed class WorkItem(MinMaxPyramid<T> pyramid) : PoolWork
    {
        internal override void OnPoolDisposed() => pyramid.OnPoolDisposed();

        private protected override int TryTake(bool requestedOnly) => pyramid.TryTake(requestedOnly);

        private protected override void Build(int chunk) => pyramid.Build(chunk);
    }

    /// <summary>A run of raw values (level -1) or one bucket, in chunk-local positions.</summary>
    private readonly record struct Piece(int Level, int Bucket, int Start, int End);

    /// <summary>The result of a query over one chunk, in chunk-local positions.</summary>
    private struct LocalResult
    {
        public bool Found;
        public bool HasNaN;
        public T Min;
        public T Max;
        public int MinIndex;
        public int MaxIndex;
        public long Count;
    }

    /// <summary>The result of a query over a range, in column positions.</summary>
    internal struct RangeResult
    {
        public bool Found;
        public bool HasNaN;
        public bool IsProvisional;
        public T Min;
        public T Max;
        public long MinIndex;
        public long MaxIndex;
        public long Count;
    }
}
