using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace HyperPlot.E2E;

/// <summary>
/// Puts a spatial index's copy in a temporary file out of the page cache and back in, from the benchmark's side (Issue
/// #603, the owner's decision of 2026-11-22: nothing is added to the product). The copy is found by its file's path among
/// this process's mapped views (<c>hyperplot-*.tmp</c>), its pages are taken out of the working set, a handle without
/// buffering is opened on the path (which purges a file's cache when nothing maps it), and memory is taken and given back
/// so that the standby pages of the copy are reused: a mapped file's cached pages cannot be purged while the view lives.
/// The view is flushed first: the build wrote the copy, and the written pages of a temporary file stay modified in memory.
/// Whether the copy was out of the cache is told by the run's cold reads against its warm ones (.cursor/rules/bench.mdc).
/// </summary>
internal static unsafe partial class PageCache
{
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint MemMapped = 0x40000;
    private const uint PageReadWrite = 0x04;
    private const uint FileShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint FileFlagNoBuffering = 0x2000_0000;
    private const int ProcessMemoryPriority = 0;
    private const int ThreadMemoryPriority = 0;

    /// <summary>Returns the committed regions of this process's mapped views of the copies' temporary files.</summary>
    internal static List<(nint Base, long Size, string Name)> Copies()
    {
        var found = new List<(nint, long, string)>();
        var buffer = stackalloc char[1024];
        nint at = 0;
        while (VirtualQuery(at, out var info, (nuint)sizeof(MemoryInfo)) != 0)
        {
            if (info.Type == MemMapped && info.State == MemCommit)
            {
                var length = GetMappedFileName(-1, info.BaseAddress, buffer, 1024);
                var name = new string(buffer, 0, (int)length);
                if (
                    Path.GetFileName(name) is { } file
                    && file.StartsWith("hyperplot-", StringComparison.Ordinal)
                    && file.EndsWith(".tmp", StringComparison.Ordinal)
                )
                {
                    found.Add((info.BaseAddress, (long)info.RegionSize, file));
                }
            }

            var next = info.BaseAddress + (nint)info.RegionSize;
            if (next <= at)
            {
                break;
            }

            at = next;
        }

        return found;
    }

    /// <summary>Reads a byte of every 4 KiB of the copies' views, so that their pages are in the cache; returns the bytes and the seconds.</summary>
    internal static (long Bytes, double Seconds) Touch(List<(nint Base, long Size, string Name)> copies)
    {
        var clock = Stopwatch.StartNew();
        long bytes = 0;
        var sum = 0;
        foreach (var (start, size, _) in copies)
        {
            var at = (byte*)start;
            for (long offset = 0; offset < size; offset += 4096)
            {
                sum += at[offset];
            }

            bytes += size;
        }

        Sink = sum;
        return (bytes, clock.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// Takes the copies out of the page cache as far as the OS lets a process without privileges: prints each step. The
    /// process's and this thread's memory priority are lowered and the copies' pages touched again, so that they carry the
    /// lowest priority; taken out of the working set, they go to the standby list at that priority and are the first reused
    /// when memory is taken.
    /// </summary>
    internal static void Evict(List<(nint Base, long Size, string Name)> copies, long keepFree)
    {
        var clock = Stopwatch.StartNew();
        var low = new MemoryPriority { Priority = 1 };
        var lowered = SetProcessInformation(-1, ProcessMemoryPriority, &low, sizeof(MemoryPriority));
        var threadLowered = SetThreadInformation(-2, ThreadMemoryPriority, &low, sizeof(MemoryPriority));

        // The build wrote the copy through the view, and a temporary file's pages that were written stay in memory as
        // modified, which no taking of memory reuses: they are written to the file first.
        var flushed = Stopwatch.StartNew();
        var flushes = true;
        foreach (var (start, size, _) in copies)
        {
            flushes &= FlushViewOfFile(start, (nuint)size);
        }

        var flushSeconds = flushed.Elapsed.TotalSeconds;
        foreach (var (start, size, _) in copies)
        {
            // A page in the working set keeps the priority it had: out and back in, it takes this thread's.
            _ = VirtualUnlock(start, (nuint)size);
        }

        _ = Touch(copies);
        long unlocked = 0;
        foreach (var (start, size, _) in copies)
        {
            // Pages that are not locked leave the working set; the call then says ERROR_NOT_LOCKED.
            _ = VirtualUnlock(start, (nuint)size);
            unlocked += size;
        }

        var opened = new List<string>();
        foreach (var (_, _, name) in copies)
        {
            var path = Path.Combine(Path.GetTempPath(), name);
            var handle = CreateFile(path, 0, FileShareAll, 0, OpenExisting, FileFlagNoBuffering, 0);
            opened.Add(
                string.Create(CultureInfo.InvariantCulture, $"{name} {(handle == -1 ? $"not opened ({Marshal.GetLastPInvokeError()})" : "opened")}")
            );
            if (handle != -1)
            {
                _ = CloseHandle(handle);
            }
        }

        var before = Available();
        var take = Math.Max(0, before - keepFree);
        var taken = Take(take);
        var normal = new MemoryPriority { Priority = 5 };
        _ = SetProcessInformation(-1, ProcessMemoryPriority, &normal, sizeof(MemoryPriority));
        _ = SetThreadInformation(-2, ThreadMemoryPriority, &normal, sizeof(MemoryPriority));
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"evict: priority lowered {lowered} (thread {threadLowered}); flushed {flushes} in {flushSeconds:F1} s; out of the working set {unlocked} bytes; no-buffering opens: {string.Join(", ", opened)}; available {before / 1048576.0:F0} MiB, took {taken / 1048576.0:F0} MiB, then available {Available() / 1048576.0:F0} MiB; {clock.Elapsed.TotalSeconds:F1} s"
            )
        );
    }

    /// <summary>Gets the physical memory available now: the free and standby pages.</summary>
    internal static long Available()
    {
        var status = new MemoryStatus { Length = (uint)sizeof(MemoryStatus) };
        return GlobalMemoryStatusEx(ref status) ? (long)status.AvailablePhysical : 0;
    }

    // A sink for the touched bytes, so that the reads are not taken away.
    private static int Sink { get; set; }

    // Commits and writes the given bytes in blocks of 256 MiB, and gives them back: the memory manager reuses standby pages,
    // the lowest priority first, for them. Returns the bytes taken.
    private static long Take(long bytes)
    {
        const long Block = 1L << 28;
        var blocks = new List<nint>();
        long taken = 0;
        try
        {
            while (taken + Block <= bytes)
            {
                var block = VirtualAlloc(0, (nuint)Block, MemCommit | MemReserve, PageReadWrite);
                if (block == 0)
                {
                    break;
                }

                blocks.Add(block);
                var at = (byte*)block;
                for (long offset = 0; offset < Block; offset += 4096)
                {
                    at[offset] = 1;
                }

                taken += Block;
            }
        }
        finally
        {
            foreach (var block in blocks)
            {
                _ = VirtualFree(block, 0, MemRelease);
            }
        }

        return taken;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nuint VirtualQuery(nint address, out MemoryInfo info, nuint length);

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetMappedFileNameW")]
    private static partial uint GetMappedFileName(nint process, nint address, char* name, uint size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualUnlock(nint address, nuint size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessInformation(nint process, int informationClass, void* information, int size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushViewOfFile(nint address, nuint size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadInformation(nint thread, int informationClass, void* information, int size);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [LibraryImport("kernel32.dll")]
    private static partial nint VirtualAlloc(nint address, nuint size, uint type, uint protect);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualFree(nint address, nuint size, uint type);

    /// <summary>MEMORY_BASIC_INFORMATION on x64: 48 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInfo
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    /// <summary>MEMORY_PRIORITY_INFORMATION: 1 is very low, 5 normal.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryPriority
    {
        public uint Priority;
    }

    /// <summary>MEMORYSTATUSEX.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}
