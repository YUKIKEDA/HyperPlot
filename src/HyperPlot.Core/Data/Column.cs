using System.Numerics;

namespace HyperPlot;

/// <summary>
/// A sequence of values of one type, read in chunks of <see cref="ChunkLength"/> elements (design §3.1, §3.2).
/// </summary>
/// <remarks>
/// A column with stored values is a <see cref="Column{T}"/>. An evenly spaced column that holds no values is a
/// <see cref="UniformColumn"/>. The static methods of this class make both.
/// </remarks>
public abstract class Column
{
    /// <summary>The base-2 logarithm of <see cref="ChunkLength"/>.</summary>
    public const int ChunkShift = 20;

    /// <summary>The number of elements in every chunk but the last (2^20).</summary>
    public const int ChunkLength = 1 << ChunkShift;

    private long _version;

    private protected Column(long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Length = length;
    }

    /// <summary>Gets the number of elements.</summary>
    public long Length { get; }

    /// <summary>Gets the number of chunks: <see cref="Length"/> divided by <see cref="ChunkLength"/>, rounded up.</summary>
    public int ChunkCount => (int)((Length + ChunkLength - 1) >> ChunkShift);

    /// <summary>Gets a number that grows each time the values change. Results derived from the column record it (design §11, invariant 4).</summary>
    public long Version => Volatile.Read(ref _version);

    /// <summary>Gets the number of elements in a chunk.</summary>
    /// <param name="chunkIndex">The chunk, from 0 to <see cref="ChunkCount"/> - 1.</param>
    /// <returns><see cref="ChunkLength"/>, or fewer for the last chunk.</returns>
    public int GetChunkLength(int chunkIndex)
    {
        CheckChunkIndex(chunkIndex);
        return (int)Math.Min(ChunkLength, Length - ((long)chunkIndex << ChunkShift));
    }

    /// <summary>Wraps an array without copying it.</summary>
    /// <typeparam name="T">The element type: <see cref="float"/>, <see cref="double"/>, <see cref="int"/>, <see cref="long"/>, or <see cref="byte"/>.</typeparam>
    /// <param name="values">The values. After changing them, call <see cref="Column{T}.Invalidate(long, long)"/>.</param>
    /// <returns>A column that reads <paramref name="values"/>.</returns>
    public static Column<T> Wrap<T>(T[] values)
        where T : unmanaged, INumber<T>
    {
        ArgumentNullException.ThrowIfNull(values);
        return Wrap<T>(values.AsMemory());
    }

    /// <summary>Wraps a memory region without copying it.</summary>
    /// <typeparam name="T">The element type: <see cref="float"/>, <see cref="double"/>, <see cref="int"/>, <see cref="long"/>, or <see cref="byte"/>.</typeparam>
    /// <param name="values">The values. After changing them, call <see cref="Column{T}.Invalidate(long, long)"/>.</param>
    /// <returns>A column that reads <paramref name="values"/>.</returns>
    public static Column<T> Wrap<T>(ReadOnlyMemory<T> values)
        where T : unmanaged, INumber<T>
    {
        ColumnElement<T>.Validate();
        return new Column<T>(new ManagedColumnStorage<T>(values), values.Length);
    }

    /// <summary>Copies values into native memory that the column owns, aligned to 64 bytes per chunk.</summary>
    /// <typeparam name="T">The element type: <see cref="float"/>, <see cref="double"/>, <see cref="int"/>, <see cref="long"/>, or <see cref="byte"/>.</typeparam>
    /// <param name="values">The values to copy.</param>
    /// <returns>A column that owns its memory until it is disposed.</returns>
    public static Column<T> CopyToNative<T>(ReadOnlySpan<T> values)
        where T : unmanaged, INumber<T>
    {
        ColumnElement<T>.Validate();
        var storage = new NativeColumnStorage<T>(values.Length);
        for (var chunk = 0; chunk < storage.ChunkCount; chunk++)
        {
            var start = chunk << ChunkShift;
            var length = Math.Min(ChunkLength, values.Length - start);
            values.Slice(start, length).CopyTo(storage.GetWritableChunk(chunk, length));
        }

        return new Column<T>(storage, values.Length);
    }

    /// <summary>Allocates native memory that the column owns and lets the caller write each chunk once.</summary>
    /// <typeparam name="T">The element type: <see cref="float"/>, <see cref="double"/>, <see cref="int"/>, <see cref="long"/>, or <see cref="byte"/>.</typeparam>
    /// <param name="length">The number of elements. It may exceed what a managed array can hold.</param>
    /// <param name="fill">Called once per chunk, in order, with the index of the chunk's first element and the chunk to write.</param>
    /// <returns>A column that owns its memory until it is disposed.</returns>
    public static Column<T> CreateNative<T>(long length, NativeChunkFiller<T> fill)
        where T : unmanaged, INumber<T>
    {
        ArgumentNullException.ThrowIfNull(fill);
        ColumnElement<T>.Validate();
        var storage = new NativeColumnStorage<T>(length);
        try
        {
            for (var chunk = 0; chunk < storage.ChunkCount; chunk++)
            {
                var first = (long)chunk << ChunkShift;
                fill(first, storage.GetWritableChunk(chunk, (int)Math.Min(ChunkLength, length - first)));
            }
        }
        catch
        {
            storage.Dispose();
            throw;
        }

        return new Column<T>(storage, length);
    }

    /// <summary>Maps a file of raw little-endian values for reading, without loading it into memory.</summary>
    /// <typeparam name="T">The element type: <see cref="float"/>, <see cref="double"/>, <see cref="int"/>, <see cref="long"/>, or <see cref="byte"/>.</typeparam>
    /// <param name="path">The file.</param>
    /// <param name="offset">The byte offset of the first value. A multiple of the size of <typeparamref name="T"/>.</param>
    /// <param name="length">The number of values, or <see langword="null"/> for all the values after <paramref name="offset"/>.</param>
    /// <returns>A column that keeps the file open until it is disposed.</returns>
    public static unsafe Column<T> MapFile<T>(string path, long offset = 0, long? length = null)
        where T : unmanaged, INumber<T>
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ColumnElement<T>.Validate();
        if (offset % sizeof(T) != 0)
        {
            throw new ArgumentException($"The offset must be a multiple of {sizeof(T)} bytes.", nameof(offset));
        }

        var fileLength = new FileInfo(path).Length;
        var available = Math.Max(0, fileLength - offset) / sizeof(T);
        var count = length ?? available;
        if (count < 0 || count > available)
        {
            throw new ArgumentOutOfRangeException(nameof(length), count, $"The file holds {available} values after the offset.");
        }

        if (count == 0)
        {
            return new Column<T>(new ManagedColumnStorage<T>(ReadOnlyMemory<T>.Empty), 0);
        }

        return new Column<T>(new MappedColumnStorage<T>(path, offset, count), count);
    }

    /// <summary>Makes an evenly spaced column that holds no values: element <c>i</c> is <c>start + i * step</c> (design §3.1).</summary>
    /// <param name="start">The first value.</param>
    /// <param name="step">The difference between neighbors. Finite and greater than 0.</param>
    /// <param name="length">The number of elements.</param>
    /// <returns>The column.</returns>
    public static UniformColumn Uniform(double start, double step, long length) => new(start, step, length);

    private protected void IncrementVersion() => Interlocked.Increment(ref _version);

    private protected void CheckChunkIndex(int chunkIndex)
    {
        if ((uint)chunkIndex >= (uint)ChunkCount)
        {
            ThrowHelper.ThrowChunkIndexOutOfRange(chunkIndex, ChunkCount);
        }
    }
}

/// <summary>Writes one chunk of a column that <see cref="Column.CreateNative{T}(long, NativeChunkFiller{T})"/> makes.</summary>
/// <typeparam name="T">The element type.</typeparam>
/// <param name="firstIndex">The index in the column of <paramref name="destination"/>'s first element.</param>
/// <param name="destination">The chunk to write. Every element should be written; the memory starts out undefined.</param>
public delegate void NativeChunkFiller<T>(long firstIndex, Span<T> destination);
