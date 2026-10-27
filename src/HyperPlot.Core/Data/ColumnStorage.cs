using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace HyperPlot;

/// <summary>Where a column's values live (design §3.2).</summary>
public enum ColumnStorageKind
{
    /// <summary>A managed array or memory region, wrapped without a copy.</summary>
    Managed,

    /// <summary>Native memory that the column owns, one 64-byte aligned block per chunk.</summary>
    Native,

    /// <summary>A file mapped for reading.</summary>
    MemoryMapped,
}

/// <summary>
/// Hands out one chunk at a time. A column calls it once per chunk, never per element, so the virtual call stays off the
/// element loop (design §3.1).
/// </summary>
internal abstract class ColumnStorage<T> : IDisposable
    where T : unmanaged
{
    public abstract ColumnStorageKind Kind { get; }

    /// <summary>Returns the chunk's elements. The column has checked the index and computed the length.</summary>
    public abstract ReadOnlySpan<T> GetChunk(int chunkIndex, int length);

    public abstract void Dispose();
}

internal sealed class ManagedColumnStorage<T>(ReadOnlyMemory<T> values) : ColumnStorage<T>
    where T : unmanaged
{
    public override ColumnStorageKind Kind => ColumnStorageKind.Managed;

    public override ReadOnlySpan<T> GetChunk(int chunkIndex, int length) => values.Span.Slice(chunkIndex << Column.ChunkShift, length);

    /// <summary>Leaves the wrapped memory as it is; the caller owns it.</summary>
    public override void Dispose() { }
}

internal sealed unsafe class NativeColumnStorage<T> : ColumnStorage<T>
    where T : unmanaged
{
    private const nuint Alignment = 64;

    private readonly nint[] _chunks;

    public NativeColumnStorage(long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ChunkCount = (int)((length + Column.ChunkLength - 1) >> Column.ChunkShift);
        _chunks = new nint[ChunkCount];
        try
        {
            for (var chunk = 0; chunk < ChunkCount; chunk++)
            {
                var elements = Math.Min(Column.ChunkLength, length - ((long)chunk << Column.ChunkShift));
                _chunks[chunk] = (nint)NativeMemory.AlignedAlloc((nuint)elements * (nuint)sizeof(T), Alignment);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int ChunkCount { get; }

    public override ColumnStorageKind Kind => ColumnStorageKind.Native;

    public override ReadOnlySpan<T> GetChunk(int chunkIndex, int length)
    {
        var pointer = _chunks[chunkIndex];
        if (pointer == 0)
        {
            ThrowHelper.ThrowColumnDisposed();
        }

        return new ReadOnlySpan<T>((void*)pointer, length);
    }

    public Span<T> GetWritableChunk(int chunkIndex, int length) => new((void*)_chunks[chunkIndex], length);

    public override void Dispose()
    {
        for (var chunk = 0; chunk < _chunks.Length; chunk++)
        {
            var pointer = Interlocked.Exchange(ref _chunks[chunk], 0);
            if (pointer != 0)
            {
                NativeMemory.AlignedFree((void*)pointer);
            }
        }
    }
}

internal sealed unsafe class MappedColumnStorage<T> : ColumnStorage<T>
    where T : unmanaged
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private byte* _first;

    public MappedColumnStorage(string path, long offset, long length)
    {
        _file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        try
        {
            _view = _file.CreateViewAccessor(offset, length * sizeof(T), MemoryMappedFileAccess.Read);
            byte* pointer = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);

            // The view starts at an allocation-granularity boundary; PointerOffset is how far before `offset` that is.
            _first = pointer + _view.PointerOffset;
        }
        catch
        {
            _view?.Dispose();
            _file.Dispose();
            throw;
        }
    }

    public override ColumnStorageKind Kind => ColumnStorageKind.MemoryMapped;

    public override ReadOnlySpan<T> GetChunk(int chunkIndex, int length)
    {
        var first = _first;
        if (first == null)
        {
            ThrowHelper.ThrowColumnDisposed();
        }

        return new ReadOnlySpan<T>(first + (((long)chunkIndex << Column.ChunkShift) * sizeof(T)), length);
    }

    public override void Dispose()
    {
        if (_first == null)
        {
            return;
        }

        _first = null;
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}
