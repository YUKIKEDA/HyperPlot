using System.Numerics;

namespace HyperPlot;

/// <summary>
/// A column with stored values, read one chunk at a time (design §3.1, §3.2). Make one with <see cref="Column.Wrap{T}(T[])"/>,
/// <see cref="Column.Wrap{T}(T*, long)"/>, <see cref="Column.CopyToNative{T}(ReadOnlySpan{T})"/>,
/// <see cref="Column.CreateNative{T}(long, NativeChunkFiller{T})"/>, or <see cref="Column.MapFile{T}(string, long, long?)"/>.
/// </summary>
/// <typeparam name="T">The element type: <see cref="float"/>, <see cref="double"/>, <see cref="int"/>, <see cref="long"/>, or <see cref="byte"/>.</typeparam>
/// <remarks>
/// Reads may run on any number of threads at once. <see cref="Invalidate(long, long)"/> may run beside them; a read that
/// overlaps the change sees the old or the new values. <see cref="Dispose"/> must not run beside a read.
/// </remarks>
public sealed class Column<T> : Column, IDisposable
    where T : unmanaged, INumber<T>
{
    private readonly ColumnStorage<T> _storage;
    private readonly StatsEntry?[] _stats;
    private readonly long[] _generations;

    internal Column(ColumnStorage<T> storage, long length)
        : base(length)
    {
        _storage = storage;
        _stats = new StatsEntry?[ChunkCount];
        _generations = new long[ChunkCount];
    }

    /// <summary>Gets where the values live.</summary>
    public ColumnStorageKind StorageKind => _storage.Kind;

    /// <summary>Returns one chunk's values. Does not allocate.</summary>
    /// <param name="chunkIndex">The chunk, from 0 to <see cref="Column.ChunkCount"/> - 1.</param>
    /// <returns><see cref="Column.ChunkLength"/> values, or fewer for the last chunk.</returns>
    public ReadOnlySpan<T> GetChunk(int chunkIndex)
    {
        CheckChunkIndex(chunkIndex);
        return _storage.GetChunk(chunkIndex, ChunkLengthOf(chunkIndex));
    }

    /// <summary>Returns one element. For many elements, read chunks with <see cref="GetChunk(int)"/>.</summary>
    /// <param name="index">The element, from 0 to <see cref="Column.Length"/> - 1.</param>
    /// <returns>The value.</returns>
    public T GetValue(long index)
    {
        if ((ulong)index >= (ulong)Length)
        {
            ThrowHelper.ThrowIndexOutOfRange(index, Length);
        }

        var chunk = (int)(index >> ChunkShift);
        return _storage.GetChunk(chunk, ChunkLengthOf(chunk))[(int)(index & (ChunkLength - 1))];
    }

    /// <summary>
    /// Returns a chunk's statistics. The first call for a chunk computes them with one SIMD pass over the chunk; later calls
    /// return the stored result until the chunk is invalidated. Does not allocate after the first call.
    /// </summary>
    /// <param name="chunkIndex">The chunk, from 0 to <see cref="Column.ChunkCount"/> - 1.</param>
    /// <returns>The statistics.</returns>
    public ChunkStats<T> GetStats(int chunkIndex)
    {
        CheckChunkIndex(chunkIndex);
        var generation = Volatile.Read(ref _generations[chunkIndex]);
        var entry = Volatile.Read(ref _stats[chunkIndex]);
        if (entry is not null && entry.Generation == generation)
        {
            return entry.Stats;
        }

        var stats = ChunkStatsKernel.Compute(_storage.GetChunk(chunkIndex, ChunkLengthOf(chunkIndex)));

        // Two threads may compute the same chunk; both results are equal. A result computed from values that changed
        // meanwhile carries the old generation, so the next read computes again.
        Volatile.Write(ref _stats[chunkIndex], new StatsEntry(generation, stats));
        return stats;
    }

    /// <summary>Returns the statistics of the whole column, combined from every chunk's.</summary>
    /// <returns>The statistics. For an empty column, <see cref="ChunkStats{T}.Count"/> is 0.</returns>
    public ChunkStats<T> GetTotalStats()
    {
        var total = ChunkStats<T>.Empty;
        for (var chunk = 0; chunk < ChunkCount; chunk++)
        {
            total = chunk == 0 ? GetStats(0) : ChunkStats<T>.Combine(total, GetStats(chunk));
        }

        return total;
    }

    /// <summary>
    /// Gets whether every value is at most the next one, with no NaN: what a time series' X needs (design §3.3). Reads the
    /// statistics of every chunk.
    /// </summary>
    /// <returns><see langword="true"/> for an empty column.</returns>
    public bool IsNonDecreasing() => ChunkCount == 0 || GetTotalStats().IsNonDecreasing;

    /// <summary>
    /// Tells the column that values in a range changed, so that statistics computed before are not returned again. Call it
    /// after writing to an array that <see cref="Column.Wrap{T}(T[])"/> wrapped.
    /// </summary>
    /// <param name="start">The first changed element.</param>
    /// <param name="length">The number of changed elements.</param>
    public void Invalidate(long start, long length)
    {
        if (start < 0 || length < 0 || start > Length - length)
        {
            ThrowHelper.ThrowRangeOutOfColumn(start, length, Length);
        }

        if (length == 0)
        {
            return;
        }

        var first = (int)(start >> ChunkShift);
        var last = (int)((start + length - 1) >> ChunkShift);
        for (var chunk = first; chunk <= last; chunk++)
        {
            Interlocked.Increment(ref _generations[chunk]);
        }

        IncrementVersion();
    }

    /// <summary>Tells the column that any of its values may have changed.</summary>
    public void Invalidate() => Invalidate(0, Length);

    /// <summary>Releases native memory or the mapped file. A wrapped array is left as it is.</summary>
    public void Dispose() => _storage.Dispose();

    private int ChunkLengthOf(int chunkIndex) => (int)Math.Min(ChunkLength, Length - ((long)chunkIndex << ChunkShift));

    private sealed class StatsEntry(long generation, ChunkStats<T> stats)
    {
        public long Generation { get; } = generation;

        public ChunkStats<T> Stats { get; } = stats;
    }
}
