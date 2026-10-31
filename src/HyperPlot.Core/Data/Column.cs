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

    /// <summary>
    /// Wraps native memory that the caller owns, without copying it. The length may exceed what a managed array or a
    /// <see cref="Memory{T}"/> can hold.
    /// </summary>
    /// <typeparam name="T">The element type: <see cref="float"/>, <see cref="double"/>, <see cref="int"/>, <see cref="long"/>, or <see cref="byte"/>.</typeparam>
    /// <param name="pointer">
    /// The first value. The memory stays valid and in place until the column is no longer read; disposing the column does
    /// not free it. After changing values, call <see cref="Column{T}.Invalidate(long, long)"/>.
    /// </param>
    /// <param name="length">The number of values. <paramref name="pointer"/> may be null only when it is 0.</param>
    /// <returns>A column that reads the memory, with <see cref="ColumnStorageKind.Native"/> storage.</returns>
    public static unsafe Column<T> Wrap<T>(T* pointer, long length)
        where T : unmanaged, INumber<T>
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (pointer == null && length != 0)
        {
            throw new ArgumentNullException(nameof(pointer), "A null pointer can only wrap a length of 0.");
        }

        ColumnElement<T>.Validate();
        return new Column<T>(new PointerColumnStorage<T>(pointer), length);
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
    /// <param name="offset">The byte offset of the first value. A multiple of the size of <typeparamref name="T"/>, at most the file's length.</param>
    /// <param name="length">The number of values, or <see langword="null"/> for all the values after <paramref name="offset"/>.</param>
    /// <returns>A column that keeps the file open until it is disposed. A range of no values maps nothing and keeps nothing open.</returns>
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
        if (offset > fileLength)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, $"The file holds {fileLength} bytes.");
        }

        var available = (fileLength - offset) / sizeof(T);
        var count = length ?? available;
        if (count < 0 || count > available)
        {
            throw new ArgumentOutOfRangeException(nameof(length), count, $"The file holds {available} values after the offset.");
        }

        return new Column<T>(new MappedColumnStorage<T>(path, offset, count), count);
    }

    /// <summary>Makes an evenly spaced column that holds no values: element <c>i</c> is <c>start + i * step</c> (design §3.1).</summary>
    /// <param name="start">The first value.</param>
    /// <param name="step">The difference between neighbors. Finite and greater than 0.</param>
    /// <param name="length">The number of elements.</param>
    /// <returns>The column.</returns>
    public static UniformColumn Uniform(double start, double step, long length) => new(start, step, length);

    /// <summary>
    /// Makes an evenly spaced column of integers that holds no values: element <c>i</c> is <c>start + i * step</c>, exact in
    /// <see cref="long"/> (design §3.1). For date-time ticks or nanoseconds, whose values pass 2^53, where a
    /// <see cref="double"/> start would round. Referenced as <c>Col&lt;long&gt;</c>.
    /// </summary>
    /// <param name="start">The first value.</param>
    /// <param name="step">The difference between neighbors. Greater than 0.</param>
    /// <param name="length">The number of elements. The last value must fit in a <see cref="long"/>.</param>
    /// <returns>The column.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="step"/> is not greater than 0, <paramref name="length"/> is negative, or the last value does not fit
    /// in a <see cref="long"/>.
    /// </exception>
    public static UniformColumn UniformInteger(long start, long step, long length) => new(start, step, length);

    /// <summary>
    /// Makes a column of categories from their names (design §3.1, §4.2, row G7b): the categories are the names, sorted in
    /// ordinal order, and a null value is missing.
    /// </summary>
    /// <param name="values">The names, one an element; null for a missing value.</param>
    /// <returns>The column.</returns>
    /// <exception cref="ArgumentException">A value is empty.</exception>
    public static CategoricalColumn Categorical(ReadOnlySpan<string?> values) => CategoricalColumn.Create(values, null);

    /// <summary>
    /// Makes a column of categories in a given order (design §3.1, §4.2, row G7b): every value that is not null is one of
    /// <paramref name="categories"/>, which may hold names no value uses.
    /// </summary>
    /// <param name="values">The names, one an element; null for a missing value.</param>
    /// <param name="categories">The categories in their order: none null, empty, or there twice.</param>
    /// <returns>The column.</returns>
    /// <exception cref="ArgumentException">A category is null, empty, or there twice, or a value is not one of them.</exception>
    public static CategoricalColumn Categorical(ReadOnlySpan<string?> values, ReadOnlySpan<string> categories) =>
        CategoricalColumn.Create(values, categories.ToArray());

    /// <summary>Gets what kind of column this is, for messages: <c>UniformColumn</c> or <c>Column&lt;Single&gt;</c>.</summary>
    internal abstract string KindName { get; }

    /// <summary>
    /// Returns the range of the values that are not NaN, from statistics a worker computed (design §4.2): false when there is
    /// none. Computes the statistics of a chunk that has none yet. An integer column's ends are exact.
    /// </summary>
    internal abstract bool TryGetRange(out ScaleEnd min, out ScaleEnd max);

    /// <summary>
    /// Starts computing the chunk statistics that M4 needs of this column as X (design §5.5.1), or returns null when they are
    /// all there or the column needs none.
    /// </summary>
    internal abstract Task? PrecomputeStatsIfMissing(PlotWorkerPool pool);

    /// <summary>Returns d(v(i)), the difference of the element's value from an origin (design §3.6): exact for integer columns.</summary>
    internal abstract double DifferenceAt(long index, long origin);

    /// <summary>Writes d(v(i)) of the first elements into <paramref name="destination"/>, at most a chunk of them.</summary>
    internal abstract void ReadDifferences(Span<double> destination, long origin);

    /// <summary>
    /// Returns the least i whose difference from <paramref name="origin"/> reaches <paramref name="boundary"/>, or the length
    /// when there is none, for a non-decreasing column (the search of M4, design §5.5.1).
    /// </summary>
    internal abstract long LowerBound(double boundary, long origin);

    /// <summary>Returns whether the column is non-decreasing without NaN, from its statistics; a uniform column always is.</summary>
    internal abstract bool IsNonDecreasing(PlotWorkerPool pool);

    /// <summary>
    /// Starts the pass that finds each category's least and greatest value of this column as Y (design §5.5.3, row G7b);
    /// only a <see cref="Column{T}"/> has values to read.
    /// </summary>
    internal virtual CategoryExtremes StartCategoryExtremes(CategoricalColumn x, PlotWorkerPool pool, RowRange rows) =>
        throw new InvalidOperationException($"A {KindName} is not a Y column of bars.");

    /// <summary>Returns the gather of the rows of a split layer's categories that do not lie in one run (design §5.5.3, row G7c).</summary>
    internal abstract GatherChannel MakeGather(ReadOnlySpan<long> counts);

    /// <summary>Adds a series with this column as X to a batch (design §5.5.1).</summary>
    internal abstract int AddSeries<TY>(M4Batch batch, MinMaxPyramid<TY> y, M4Point[] destination, RowRange rows)
        where TY : unmanaged, INumber<TY>;

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
