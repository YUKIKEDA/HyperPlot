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

    // The bytes that posix_fallocate reserves between two looks at whether the build was stopped.
    private const long ReservePiece = 1 << 20;

    // EINTR and EOPNOTSUPP on the Linux targets of the engine (x64 and arm64).
    private const int Interrupted = 4;
    private const int NotSupported = 95;

    private readonly int[] _widths;
    private readonly nint[] _regions;
    private readonly FileStream? _stream;
    private readonly MemoryMappedFile? _file;
    private readonly MemoryMappedViewAccessor? _view;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
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

    private SpatialCopy(long capacity, ReadOnlySpan<int> valueWidths, string folder, SpatialCopyAllocator.TempFileKind kind, Func<bool>? stopped)
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
                // On Linux the name is removed at once, so the file is gone when the process ends, and fallocate or
                // posix_fallocate reserves the blocks: .NET's PreallocationSize ignores a file system that cannot reserve.
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    BufferSize = 0,
                };
                _stream = new FileStream(path, options);
                File.Delete(path);
                Reserve(_stream, size, atOnce: kind == SpatialCopyAllocator.TempFileKind.Unlinked, stopped);
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
            // No one has the copy yet: the file is closed here, so that it is gone when the exception reaches the caller.
            _disposed = 1;
            Array.Clear(_regions);
            Close();
            throw;
        }
    }

    /// <summary>
    /// Spike R3b2 (Issue #472, O3): writes the view's dirty pages to the file and takes the process's pages out of its
    /// working set, so that the copy's pages are on the standby list, where memory that another allocation takes reuses
    /// them first. Returns false for a copy in native memory.
    /// </summary>
    internal bool ProbeFlushAndTrim()
    {
        if (_stream is null)
        {
            return false;
        }

        _ = FlushViewOfFile(_regions[0], 0);
        _stream.Flush(flushToDisk: true);
        _ = SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
        return true;
    }

    /// <summary>Spike R3b2 (Issue #472, O3): reads one byte of each 4 KiB page of the copy in order; returns their sum.</summary>
    internal long ProbeTouch()
    {
        var first = (byte*)_regions[0];
        long sum = 0;
        for (long at = 0; at < ByteCount; at += 4096)
        {
            sum += first[at];
        }

        return sum;
    }

    [LibraryImport("kernel32.dll")]
    private static partial int FlushViewOfFile(nint address, nuint bytes);

    [LibraryImport("kernel32.dll")]
    private static partial int SetProcessWorkingSetSize(nint process, nint minimum, nint maximum);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    /// <summary>Gets the number of points the copy has room for: the number of rows the index reads.</summary>
    public long Capacity { get; }

    /// <summary>Gets the bytes of the copy: the sum of the native blocks, or the size of the temporary file.</summary>
    public long ByteCount { get; }

    /// <summary>Gets a value indicating whether the copy is a view of a temporary file.</summary>
    public bool IsMapped => _stream != null;

    /// <summary>
    /// Gets a task that ends when a disposed copy holds nothing any more: at the end of <see cref="Dispose"/> for native
    /// memory, and once the thread pool has unmapped and closed the temporary file. It does not end before <see cref="Dispose"/>.
    /// </summary>
    public Task Closed => _closed.Task;

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
    /// <param name="stopped">
    /// Returns whether the build that asked for the copy was stopped, or <see langword="null"/> when nothing stops it.
    /// Read between the pieces of a file that the file system reserves piece by piece.
    /// </param>
    /// <returns>The copy. Its contents are not set.</returns>
    /// <exception cref="PlatformNotSupportedException"><paramref name="kind"/> is <see cref="SpatialCopyAllocator.TempFileKind.None"/>.</exception>
    /// <exception cref="IOException">The file cannot be created, or the volume has no room for it.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="stopped"/> returned <see langword="true"/> between two pieces.</exception>
    public static SpatialCopy Map(
        long capacity,
        ReadOnlySpan<int> valueWidths,
        string folder,
        SpatialCopyAllocator.TempFileKind kind,
        Func<bool>? stopped = null
    ) => new(capacity, valueWidths, folder, kind, stopped);

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

    /// <summary>
    /// Frees the native blocks, or hands the temporary file to the thread pool, which unmaps and closes it (closing deletes
    /// it on Windows): that takes 1.3 to 2.9 s for the copy of 1e9 points (docs/benchmarks/c3a-spatial-index.md), and the
    /// caller may be the UI thread. <see cref="Closed"/> ends when it is done. A second call does nothing.
    /// </summary>
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

            _closed.TrySetResult();
            return;
        }

        Array.Clear(_regions);
        ThreadPool.UnsafeQueueUserWorkItem(static copy => copy.Close(), this, preferLocal: false);
    }

    private void Close()
    {
        if (_pointerAcquired)
        {
            _view!.SafeMemoryMappedViewHandle.ReleasePointer();
        }

        // The accessor's Dispose first writes every changed page to the file and waits for it: 25 to 44 s for a copy of
        // 1e9 points that the writing stage had begun (docs/benchmarks/c3a-spatial-index.md). The file is going to be
        // deleted, so the view's handle is closed first, which unmaps without writing; the accessor then has nothing to write.
        _view?.SafeMemoryMappedViewHandle.Dispose();
        _view?.Dispose();
        _file?.Dispose();
        _stream?.Dispose();
        _closed.TrySetResult();
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

    // Reserves the blocks of the file. With atOnce, fallocate reserves them in one call where the file system can.
    private static void Reserve(FileStream stream, long size, bool atOnce, Func<bool>? stopped)
    {
        var handle = stream.SafeFileHandle;
        var held = false;
        try
        {
            handle.DangerousAddRef(ref held);
            var descriptor = (int)handle.DangerousGetHandle();
            var error = atOnce ? ReserveAtOnce(descriptor, size) : NotSupported;
            if (error == NotSupported)
            {
                error = ReserveInPieces(descriptor, size, stopped);
            }

            if (error != 0)
            {
                throw new IOException($"The file system could not reserve {size} bytes for the copy of a spatial tile index: error {error}.", error);
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

    // Returns 0, or the error number of fallocate(2): EOPNOTSUPP where the file system cannot reserve.
    private static int ReserveAtOnce(int descriptor, long size)
    {
        while (true)
        {
            if (Fallocate(descriptor, 0, 0, size) == 0)
            {
                return 0;
            }

            var error = Marshal.GetLastPInvokeError();
            if (error != Interrupted)
            {
                return error;
            }
        }
    }

    // Where the file system has no fallocate (9p, some network file systems), glibc's posix_fallocate writes one byte
    // into every block: 12 GB took 17.4 min on 9p (docs/benchmarks/c3a-spatial-index.md). Piece by piece, so that a
    // stopped build does not wait for the whole file. Returns 0 or the error number.
    private static int ReserveInPieces(int descriptor, long size, Func<bool>? stopped)
    {
        for (long offset = 0; offset < size; offset += ReservePiece)
        {
            if (stopped?.Invoke() == true)
            {
                throw new OperationCanceledException("The build of the spatial tile index was stopped while it reserved its temporary file.");
            }

            // posix_fallocate returns the error number and does not set errno (posix_fallocate(3)).
            var error = PosixFallocate(descriptor, offset, Math.Min(ReservePiece, size - offset));
            if (error != 0)
            {
                return error;
            }
        }

        return 0;
    }

    // int fallocate(int fd, int mode, off_t offset, off_t len): −1 with errno set on failure (fallocate(2)). Mode 0
    // reserves the blocks and sets the size, as posix_fallocate does.
    [LibraryImport("libc", EntryPoint = "fallocate", SetLastError = true)]
    private static partial int Fallocate(int fd, int mode, long offset, long length);

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
