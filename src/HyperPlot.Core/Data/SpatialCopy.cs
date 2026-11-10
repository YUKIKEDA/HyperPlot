using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace HyperPlot;

/// <summary>
/// The reordered copy of a spatial tile index (design §3.4 "写しと置き場所"): one region for the values of each axis
/// and one for each point's original position, every region on a 64-byte boundary. The regions are blocks of native
/// memory, or parts of one view of a temporary file that is gone when the process ends.
/// </summary>
/// <remarks>
/// A copy is written by the workers of one build, each to places that no other worker writes, and read only after the
/// build has published it. <see cref="Dispose"/> runs after everything that reads or writes the copy is done: the index
/// frees an unpublished copy after its jobs have ended, and a published one when its snapshot's last reference is
/// returned.
/// </remarks>
internal sealed unsafe partial class SpatialCopy : IDisposable
{
    private const int Alignment = 64;

    private readonly int[] _widths;
    private readonly nint[] _regions;
    private readonly FileStream? _stream;
    private readonly MemoryMappedFile? _file;
    private readonly MemoryMappedViewAccessor? _view;
    private bool _pointerAcquired;
    private int _disposed;

    private SpatialCopy(long capacity, ReadOnlySpan<int> valueWidths)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
        _widths = [.. valueWidths, PositionWidthFor(capacity)];
        _regions = new nint[_widths.Length];
        try
        {
            for (var region = 0; region < _widths.Length; region++)
            {
                _regions[region] = (nint)NativeMemory.AlignedAlloc(checked((nuint)capacity * (nuint)_widths[region]), Alignment);
                ByteCount += capacity * _widths[region];
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private SpatialCopy(long capacity, ReadOnlySpan<int> valueWidths, string folder, SpatialCopyAllocator.TempFileKind kind)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        if (kind == SpatialCopyAllocator.TempFileKind.None)
        {
            throw new PlatformNotSupportedException("The copy of a spatial tile index of memory-mapped columns needs Windows or Linux.");
        }

        Capacity = capacity;
        _widths = [.. valueWidths, PositionWidthFor(capacity)];
        _regions = new nint[_widths.Length];
        Span<long> offsets = stackalloc long[_widths.Length];
        long size = 0;
        for (var region = 0; region < _widths.Length; region++)
        {
            offsets[region] = (size + Alignment - 1) / Alignment * Alignment;
            size = checked(offsets[region] + (capacity * _widths[region]));
        }

        ByteCount = size;
        var path = Path.Combine(folder, $"hyperplot-{Guid.NewGuid()}.tmp");
        try
        {
            if (kind == SpatialCopyAllocator.TempFileKind.DeleteOnClose)
            {
                // Windows deletes the file when its last handle closes, which is also when the process is killed, and
                // PreallocationSize throws when the volume has no room for the whole file (design §3.4 "一時ファイル").
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    Options = FileOptions.DeleteOnClose,
                    PreallocationSize = size,
                    BufferSize = 0,
                };
                _stream = new FileStream(path, options);
            }
            else
            {
                // On Linux the name is removed at once, so the file is gone when the process ends, and posix_fallocate
                // reserves the blocks: .NET's PreallocationSize ignores a file system that cannot reserve.
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    BufferSize = 0,
                };
                _stream = new FileStream(path, options);
                File.Delete(path);
                Reserve(_stream, size);
            }

            _file = MemoryMappedFile.CreateFromFile(
                _stream,
                null,
                size,
                MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None,
                leaveOpen: true
            );
            _view = _file.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite);
            byte* pointer = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            _pointerAcquired = true;

            // The view starts at offset 0 of the file, on a page boundary, so a region at a multiple of 64 bytes in the file
            // is on a 64-byte boundary in memory. The view covers `size` bytes, and each region ends at or before them.
            var first = pointer + _view.PointerOffset;
            for (var region = 0; region < _widths.Length; region++)
            {
                _regions[region] = (nint)(first + offsets[region]);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Gets the number of points the copy has room for: the number of rows the index reads.</summary>
    public long Capacity { get; }

    /// <summary>Gets the bytes of the copy: the sum of the native blocks, or the size of the temporary file.</summary>
    public long ByteCount { get; }

    /// <summary>Gets a value indicating whether the copy is a view of a temporary file.</summary>
    public bool IsMapped => _stream != null;

    /// <summary>Gets a value indicating whether the memory or the file was released. The tests use it to see that a build which did not publish freed its copy.</summary>
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Gets the number of regions: one for each axis, and the last one for the original positions.</summary>
    public int RegionCount => _widths.Length;

    /// <summary>
    /// Returns the bytes of one original position: 4 when every position of <paramref name="capacity"/> rows fits a
    /// <see cref="uint"/>, and 8 otherwise.
    /// </summary>
    /// <param name="capacity">The number of rows the index reads.</param>
    /// <returns>4 or 8.</returns>
    public static int PositionWidthFor(long capacity) => capacity <= 1L << 32 ? sizeof(uint) : sizeof(long);

    /// <summary>Allocates a copy in native memory, one block for each region.</summary>
    /// <param name="capacity">The number of points: positive.</param>
    /// <param name="valueWidths">The bytes of one value of each axis.</param>
    /// <returns>The copy. Its contents are not set.</returns>
    public static SpatialCopy Allocate(long capacity, ReadOnlySpan<int> valueWidths) => new(capacity, valueWidths);

    /// <summary>Creates a temporary file of the copy's size in a folder and maps it with one view.</summary>
    /// <param name="capacity">The number of points: positive.</param>
    /// <param name="valueWidths">The bytes of one value of each axis.</param>
    /// <param name="folder">The folder of the file.</param>
    /// <param name="kind">How this operating system keeps the file from outliving the process.</param>
    /// <returns>The copy. Its contents are not set.</returns>
    /// <exception cref="PlatformNotSupportedException"><paramref name="kind"/> is <see cref="SpatialCopyAllocator.TempFileKind.None"/>.</exception>
    /// <exception cref="IOException">The file cannot be created, or the volume has no room for it.</exception>
    public static SpatialCopy Map(long capacity, ReadOnlySpan<int> valueWidths, string folder, SpatialCopyAllocator.TempFileKind kind) =>
        new(capacity, valueWidths, folder, kind);

    /// <summary>Returns the bytes of one element of a region.</summary>
    /// <param name="region">The region.</param>
    /// <returns>1, 4, or 8.</returns>
    public int Width(int region) => _widths[region];

    /// <summary>Returns one region as elements of its width.</summary>
    /// <typeparam name="T">The type of the region's elements.</typeparam>
    /// <param name="region">The region's number: an axis, or the last number for the original positions.</param>
    /// <returns>The region's elements, each access checked against the copy's capacity.</returns>
    /// <exception cref="ArgumentException"><typeparamref name="T"/> is not as wide as the region's elements.</exception>
    public Region<T> GetRegion<T>(int region)
        where T : unmanaged
    {
        if (sizeof(T) != _widths[region])
        {
            ThrowHelper.ThrowElementWidthMismatch(sizeof(T), _widths[region]);
        }

        // The region holds Capacity elements of T's width, and lives until Dispose (see the remarks of the type).
        return new Region<T>((T*)_regions[region], Capacity);
    }

    /// <summary>Copies one point into a run of places, in every region.</summary>
    /// <param name="source">The position of the point to copy.</param>
    /// <param name="first">The first place to fill.</param>
    /// <param name="count">The number of places to fill.</param>
    public void Fill(long source, long first, long count)
    {
        if ((ulong)source >= (ulong)Capacity)
        {
            ThrowHelper.ThrowIndexOutOfBlock(source, Capacity);
        }

        if (first < 0 || count < 0 || count > Capacity - first)
        {
            ThrowHelper.ThrowRangeOutOfBlock(first, count, Capacity);
        }

        for (var region = 0; region < _widths.Length; region++)
        {
            // The bits of an element are copied whatever its type, so an unsigned integer of the same width stands for it.
            switch (_widths[region])
            {
                case sizeof(byte):
                    Fill(GetRegion<byte>(region), source, first, count);
                    break;
                case sizeof(uint):
                    Fill(GetRegion<uint>(region), source, first, count);
                    break;
                default:
                    Fill(GetRegion<ulong>(region), source, first, count);
                    break;
            }
        }
    }

    /// <summary>Unmaps and closes the temporary file, which deletes it, or frees the native blocks. A second call does nothing.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_stream == null)
        {
            for (var region = 0; region < _regions.Length; region++)
            {
                NativeMemory.AlignedFree((void*)_regions[region]);
                _regions[region] = 0;
            }

            return;
        }

        Array.Clear(_regions);
        if (_pointerAcquired)
        {
            _view!.SafeMemoryMappedViewHandle.ReleasePointer();
        }

        _view?.Dispose();
        _file?.Dispose();
        _stream.Dispose();
    }

    private static void Fill<T>(Region<T> region, long source, long first, long count)
        where T : unmanaged
    {
        var value = region.Read(source, 1)[0];
        while (count > 0)
        {
            var length = (int)Math.Min(count, 1 << 30);
            region.Write(first, length).Fill(value);
            first += length;
            count -= length;
        }
    }

    private static void Reserve(FileStream stream, long size)
    {
        var handle = stream.SafeFileHandle;
        var held = false;
        try
        {
            handle.DangerousAddRef(ref held);

            // posix_fallocate returns the error number and does not set errno (posix_fallocate(3)).
            var error = PosixFallocate((int)handle.DangerousGetHandle(), 0, size);
            if (error != 0)
            {
                throw new IOException($"posix_fallocate could not reserve {size} bytes for the copy of a spatial tile index: error {error}.", error);
            }
        }
        finally
        {
            if (held)
            {
                handle.DangerousRelease();
            }
        }
    }

    // int posix_fallocate(int fd, off_t offset, off_t len): off_t is 64 bits on the 64-bit Linux targets of the engine.
    [LibraryImport("libc", EntryPoint = "posix_fallocate")]
    private static partial int PosixFallocate(int fd, long offset, long length);

    /// <summary>One region of a copy as elements of one type. Every read and write checks its place against the region's length.</summary>
    /// <typeparam name="T">The type of the region's elements.</typeparam>
    internal readonly ref struct Region<T>
        where T : unmanaged
    {
        private readonly T* _first;
        private readonly long _length;

        /// <summary>Initializes a new instance of the <see cref="Region{T}"/> struct.</summary>
        /// <param name="first">The region's first element.</param>
        /// <param name="length">The number of elements of the region.</param>
        internal Region(T* first, long length)
        {
            _first = first;
            _length = length;
        }

        /// <summary>Writes one element when its position is in the region.</summary>
        /// <param name="position">The position. <see cref="ulong.MaxValue"/>, the mark of a dropped point, is outside every region.</param>
        /// <param name="value">The element.</param>
        /// <returns>Whether the element was written.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryWrite(ulong position, T value)
        {
            if (position >= (ulong)_length)
            {
                return false;
            }

            // The position is below the region's length.
            _first[position] = value;
            return true;
        }

        /// <summary>Returns a run of the region's elements to read.</summary>
        /// <param name="first">The position of the first element to read.</param>
        /// <param name="count">The number of elements to read.</param>
        /// <returns>The elements, read-only.</returns>
        public ReadOnlySpan<T> Read(long first, int count) => Write(first, count);

        /// <summary>Returns a run of the region's elements to write.</summary>
        /// <param name="first">The position of the first element of the run.</param>
        /// <param name="count">The number of elements of the run.</param>
        /// <returns>The elements.</returns>
        public Span<T> Write(long first, int count)
        {
            if (first < 0 || count < 0 || count > _length - first)
            {
                ThrowHelper.ThrowRangeOutOfBlock(first, count, _length);
            }

            // The run is inside the region.
            return new Span<T>(_first + first, count);
        }
    }
}
