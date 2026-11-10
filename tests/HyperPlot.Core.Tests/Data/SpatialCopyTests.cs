using System.Numerics;
using System.Runtime.InteropServices;

namespace HyperPlot.Tests;

/// <summary>
/// The copy of a spatial tile index: where it is put, the widths of what it holds, its temporary file, and what a
/// failure to make it does (design §3.4 "写しと置き場所", row C3a).
/// </summary>
[Collection("Large data")]
public sealed class SpatialCopyTests
{
    private const int Short = 70_001;

    /// <summary>Lists the storages of the two columns and whether their copy goes to a temporary file.</summary>
    public static TheoryData<string, string, bool> Storages =>
        new()
        {
            { "managed", "managed", false },
            { "native", "managed", false },
            { "native", "native", false },
            { "mapped", "managed", true },
            { "native", "mapped", true },
            { "mapped", "mapped", true },
        };

    /// <summary>The copy is in a temporary file when a column is memory-mapped, and in native memory otherwise.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 70,001 seeded points of float x and y. Each column is a managed array, a native block, or a memory-mapped file.
    /// - A new, empty folder is the <c>TempFolder</c> of the input. The allocation seam records its one request.
    ///
    /// Steps:
    /// - Build the index of every row with 3 workers, look at the folder, then release the snapshot and dispose the
    ///   index and the pool, and look at the folder again.
    ///
    /// Expected:
    /// - The request says "mapped" exactly when a column is memory-mapped, names the folder, and has the 70,001 rows.
    /// - The copy is a mapped file exactly then. Its three regions are 4 bytes wide and start at addresses that are
    ///   multiples of 64. It takes 840,012 bytes of native memory, or a file of 840,132 bytes.
    /// - While the index lives, a mapped copy has one file <c>hyperplot-*.tmp</c> of the copy's size in the folder on
    ///   Windows and no file on Linux; a native copy has none.
    /// - The snapshot agrees with the brute force.
    /// - After the last release the snapshot is freed and the folder is empty.
    /// </remarks>
    /// <param name="xStorage">The storage of the x column.</param>
    /// <param name="yStorage">The storage of the y column.</param>
    /// <param name="mapped">Whether the copy goes to a temporary file.</param>
    [Theory]
    [MemberData(nameof(Storages))]
    public void Build_ColumnsOfEachStorage_PutTheCopyInATempFileOnlyForAMappedColumn(string xStorage, string yStorage, bool mapped)
    {
        var (xs, ys) = SpatialData.Uniform(Short, seed: 41);
        using var folder = new TempFolder();
        using var xFile = TempFile.Write<float>(xs, 0);
        using var yFile = TempFile.Write<float>(ys, 0);
        var allocator = new RecordingCopyAllocator();
        SpatialSnapshot snapshot;
        using (
            var built = SpatialBuilt.Over(
                Make(xStorage, xs, xFile),
                Make(yStorage, ys, yFile),
                new SpatialIndexInput { Rows = RowRange.All, TempFolder = folder.Path },
                allocator: allocator
            )
        )
        {
            snapshot = built.Snapshot;
            var request = Assert.Single(allocator.Requests);
            Assert.Equal(mapped, request.Mapped);
            Assert.Equal(folder.Path, request.TempFolder);
            Assert.Equal(Short, request.Capacity);
            var copy = request.Copy!;
            Assert.Equal(mapped, copy.IsMapped);
            Assert.Equal(3, copy.RegionCount);
            Assert.Equal([4, 4, 4], (int[])[copy.Width(0), copy.Width(1), copy.Width(2)]);

            for (var region = 0; region < 3; region++)
            {
                Assert.Equal(0, AddressOf(copy.GetRegion<uint>(region).Read(0, 1)) % 64);
            }

            // 70,001 × 4 = 280,004 bytes a region. In the file, the second and the third start at the next multiples of 64.
            Assert.Equal(mapped ? 280_064 + 280_064 + 280_004 : 3 * 280_004, copy.ByteCount);

            var files = folder.CopyFiles();
            if (mapped && OperatingSystem.IsWindows())
            {
                Assert.Equal(copy.ByteCount, new FileInfo(Assert.Single(files)).Length);
            }
            else
            {
                Assert.Empty(files);
            }

            Assert.True(built.Index.IsComplete);
            SpatialReference.Check(snapshot, SpatialAxisData.Of(xs), SpatialAxisData.Of(ys), new RowRange(0, Short), SpatialReference.Small);
            Assert.False(snapshot.IsFreed);
        }

        Assert.True(snapshot.IsFreed);
        Assert.Empty(Directory.GetFileSystemEntries(folder.Path));
    }

    /// <summary>Without a transform an axis is stored with the type of its column.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 1,000 seeded integers from −100 to 100 as x, in a column of each type a column can hold (byte takes 0 to 200);
    ///   y is the same values as <see cref="double"/>. No transform.
    ///
    /// Steps:
    /// - Build the index of every row and read the request of the allocation seam and the copy.
    ///
    /// Expected:
    /// - The request asks for the width of the column's type for x and 8 bytes for y, and the regions of the copy are
    ///   those widths with 4 bytes for the row.
    /// - The snapshot agrees with the brute force.
    /// </remarks>
    /// <param name="type">The type of the x column.</param>
    /// <param name="width">The bytes of one x value of the copy.</param>
    [Theory]
    [InlineData("byte", 1)]
    [InlineData("int", 4)]
    [InlineData("float", 4)]
    [InlineData("long", 8)]
    [InlineData("double", 8)]
    public void Build_NoTransform_StoresTheTypeOfTheColumn(string type, int width)
    {
        var widths = type switch
        {
            "byte" => StoredWidths<byte>(transform: null, offset: 100),
            "int" => StoredWidths<int>(transform: null),
            "float" => StoredWidths<float>(transform: null),
            "long" => StoredWidths<long>(transform: null),
            _ => StoredWidths<double>(transform: null),
        };

        Assert.Equal([width, 8, 4], widths);
    }

    /// <summary>With a transform a float column is stored as float and a column of any other type as double.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 1,000 seeded integers from −100 to 100 as x, in a column of each type a column can hold (byte takes 0 to 200);
    ///   y is the same values as <see cref="double"/>. The x axis has the pow transform of exponent 3.
    ///
    /// Steps:
    /// - Build the index of every row and read the request of the allocation seam and the copy.
    ///
    /// Expected:
    /// - The request asks for 4 bytes for a float column and 8 bytes for every other type, and 8 bytes for y; the
    ///   regions of the copy are those widths with 4 bytes for the row.
    /// - The snapshot agrees with the brute force over the cubes: rounded to float for the float column.
    /// </remarks>
    /// <param name="type">The type of the x column.</param>
    /// <param name="width">The bytes of one x value of the copy.</param>
    [Theory]
    [InlineData("byte", 8)]
    [InlineData("int", 8)]
    [InlineData("float", 4)]
    [InlineData("long", 8)]
    [InlineData("double", 8)]
    public void Build_Transform_StoresFloatForAFloatColumnAndDoubleForTheOthers(string type, int width)
    {
        var cube = new PowTransform(3);
        var widths = type switch
        {
            "byte" => StoredWidths<byte>(cube, offset: 100),
            "int" => StoredWidths<int>(cube),
            "float" => StoredWidths<float>(cube),
            "long" => StoredWidths<long>(cube),
            _ => StoredWidths<double>(cube),
        };

        Assert.Equal([width, 8, 4], widths);
    }

    /// <summary>The row of a point takes 4 bytes up to 2^32 rows and 8 bytes above.</summary>
    /// <remarks>
    /// Preconditions:
    /// - None: the function that decides the width is called by itself, for a copy of 2^32 rows is 48 GB.
    ///
    /// Steps:
    /// - Ask the width for 1 row, 2^32 − 1, 2^32, 2^32 + 1, and 2^40 rows.
    ///
    /// Expected:
    /// - 4 bytes up to 2^32 rows, whose last row is the number 2^32 − 1, and 8 bytes from 2^32 + 1 rows.
    /// </remarks>
    /// <param name="rows">The number of rows of the copy.</param>
    /// <param name="width">The bytes of the row of one point.</param>
    [Theory]
    [InlineData(1L, 4)]
    [InlineData((1L << 32) - 1, 4)]
    [InlineData(1L << 32, 4)]
    [InlineData((1L << 32) + 1, 8)]
    [InlineData(1L << 40, 8)]
    public void PositionWidthFor_RowsAroundTwoToThe32_Is4BytesUpTo2To32(long rows, int width)
    {
        Assert.Equal(width, SpatialCopy.PositionWidthFor(rows));
    }

    /// <summary>A <c>TempFolder</c> that does not exist fails the build with the exception of the file system.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 70,001 points of float x and y in memory-mapped columns. The <c>TempFolder</c> is a folder name inside a new
    ///   folder, and nothing has that name.
    ///
    /// Steps:
    /// - Start the build and wait for <c>Completion</c> to end.
    ///
    /// Expected:
    /// - <c>Completion</c> ends with a <see cref="DirectoryNotFoundException"/>, which is the <c>Fault</c>; the state is
    ///   "failed" and the index is not complete.
    /// - The snapshot is still the empty one: <c>Count</c> answers 0 inside and 70,001 touching.
    /// - Nothing was made in the new folder.
    /// </remarks>
    [Fact]
    public void Build_TempFolderThatDoesNotExist_PutsDirectoryNotFoundInFault()
    {
        using var folder = new TempFolder();
        var missing = Path.Combine(folder.Path, "missing");

        var fault = FailedBuild(SpatialCopyAllocator.Default, missing, mappedColumns: true);

        Assert.IsType<DirectoryNotFoundException>(fault);
        Assert.Empty(Directory.GetFileSystemEntries(folder.Path));
    }

    /// <summary>A copy that the allocation seam cannot make for lack of space fails the build with that exception.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 70,001 points of float x and y in memory-mapped columns, and a new folder as the <c>TempFolder</c>.
    /// - The allocation seam throws an <see cref="IOException"/> with the message of a full disk and makes no copy.
    ///
    /// Steps:
    /// - Start the build and wait for <c>Completion</c> to end.
    ///
    /// Expected:
    /// - The seam was asked once, for a mapped copy in the folder.
    /// - <c>Completion</c> ends with the seam's exception, which is the <c>Fault</c>; the state is "failed".
    /// - The snapshot is still the empty one, and the folder is empty.
    /// </remarks>
    [Fact]
    public void Build_AllocationWithoutSpace_PutsTheIOExceptionInFault()
    {
        using var folder = new TempFolder();
        var full = new IOException("There is not enough space on the disk.");
        var allocator = new RecordingCopyAllocator { Failure = full };

        var fault = FailedBuild(allocator, folder.Path, mappedColumns: true);

        Assert.Same(full, fault);
        var request = Assert.Single(allocator.Requests);
        Assert.True(request.Mapped);
        Assert.Equal(folder.Path, request.TempFolder);
        Assert.Empty(Directory.GetFileSystemEntries(folder.Path));
    }

    /// <summary>On an OS without a temporary file, the index of memory-mapped columns fails with <see cref="PlatformNotSupportedException"/>.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 70,001 points of float x and y in memory-mapped columns, and a new folder as the <c>TempFolder</c>.
    /// - The allocation seam answers that the OS has no kind of temporary file, as on macOS.
    ///
    /// Steps:
    /// - Start the build and wait for <c>Completion</c> to end.
    ///
    /// Expected:
    /// - <c>Completion</c> ends with a <see cref="PlatformNotSupportedException"/>, which is the <c>Fault</c>; the state
    ///   is "failed".
    /// - The snapshot is still the empty one, and the folder is empty.
    /// </remarks>
    [Fact]
    public void Build_MappedColumnsOnAnOsWithoutATempFile_PutsPlatformNotSupportedInFault()
    {
        using var folder = new TempFolder();
        var allocator = new RecordingCopyAllocator { Kind = SpatialCopyAllocator.TempFileKind.None };

        var fault = FailedBuild(allocator, folder.Path, mappedColumns: true);

        Assert.IsType<PlatformNotSupportedException>(fault);
        Assert.Empty(Directory.GetFileSystemEntries(folder.Path));
    }

    /// <summary>On an OS without a temporary file, the index of columns in memory is built as on any OS.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 70,001 seeded points of float x and y in managed arrays.
    /// - The allocation seam answers that the OS has no kind of temporary file, as on macOS.
    ///
    /// Steps:
    /// - Build the index of every row.
    ///
    /// Expected:
    /// - The index is complete without a fault, its copy is not a mapped file, and the snapshot agrees with the brute force.
    /// </remarks>
    [Fact]
    public void Build_ManagedColumnsOnAnOsWithoutATempFile_BuildsInNativeMemory()
    {
        var (xs, ys) = SpatialData.Uniform(Short, seed: 42);
        var allocator = new RecordingCopyAllocator { Kind = SpatialCopyAllocator.TempFileKind.None };

        using var built = SpatialBuilt.Over(Column.Wrap(xs), Column.Wrap(ys), new SpatialIndexInput { Rows = RowRange.All }, allocator: allocator);

        Assert.True(built.Index.IsComplete);
        Assert.Null(built.Index.Fault);
        Assert.False(Assert.Single(allocator.Requests).Copy!.IsMapped);
        SpatialReference.Check(built.Snapshot, SpatialAxisData.Of(xs), SpatialAxisData.Of(ys), new RowRange(0, Short), SpatialReference.Small);
    }

    /// <summary>A build that fails after it made its temporary file closes the file, so that no name is left.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 70,001 points of float x and y in memory-mapped columns, and a new folder as the <c>TempFolder</c>.
    /// - The y axis has a transform that throws on its first call, which comes after the allocation.
    ///
    /// Steps:
    /// - Start the build with 3 workers and wait for <c>Completion</c> to end.
    ///
    /// Expected:
    /// - The allocation seam made one mapped copy.
    /// - <c>Completion</c> ends with the transform's exception, which is the <c>Fault</c>.
    /// - Once the workers have left the build, the folder is empty.
    /// </remarks>
    [Fact]
    public void Build_FailureAfterTheAllocation_ClosesTheTempFile()
    {
        var (xs, ys) = SpatialData.Uniform(Short, seed: 43);
        using var folder = new TempFolder();
        using var xFile = TempFile.Write<float>(xs, 0);
        using var yFile = TempFile.Write<float>(ys, 0);
        var allocator = new RecordingCopyAllocator();
        var thrown = new InvalidOperationException("The transform failed on purpose.");
        using var pool = new PlotWorkerPool(3);
        using var x = Column.MapFile<float>(xFile.Path);
        using var y = Column.MapFile<float>(yFile.Path);
        using var index = SpatialReference.Start(
            pool,
            x,
            y,
            new SpatialIndexInput
            {
                Rows = RowRange.All,
                YTransform = new ThrowingScale(thrown),
                TempFolder = folder.Path,
            },
            allocator: allocator
        );

        Assert.Same(thrown, Eventually.Faults(index.Completion));

        Assert.Same(thrown, index.Fault);
        Assert.True(Assert.Single(allocator.Requests).Copy!.IsMapped);
        Eventually.True(() => Directory.GetFileSystemEntries(folder.Path).Length == 0, "The temporary file of the failed build stayed.");
    }

    /// <summary>A process that is killed while it writes the copy leaves no file in the <c>TempFolder</c>.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A file of 70,001 seeded float x values followed by as many y values, and a new folder.
    /// - A child process, this test executable in another role, maps the two halves as columns and builds their index
    ///   with the folder as <c>TempFolder</c>. A worker of it stops when it has written the first unit of points into
    ///   the copy, makes the file "ready.txt" in the folder, and does not tell the build that the unit ended.
    ///
    /// Steps:
    /// - Wait for "ready.txt", look at the folder, kill the child, wait for its exit, and look at the folder again.
    ///
    /// Expected:
    /// - While the child is stopped in the writing: one file <c>hyperplot-*.tmp</c> of 840,132 bytes on Windows, where
    ///   the file has a name until its last handle closes; no such file on Linux, where the name is removed at once.
    /// - The child did not end by itself: its exit code is not 0, 2, or 3.
    /// - After the exit, the folder holds no <c>hyperplot-*.tmp</c>.
    /// </remarks>
    [Fact]
    public void Build_ChildProcessKilledInTheWriting_LeavesNoFileInTheTempFolder()
    {
        var (xs, ys) = SpatialData.Uniform(Short, seed: 44);
        using var folder = new TempFolder();
        using var data = TempFile.Write<float>([.. xs, .. ys], 0);
        var ready = Path.Combine(folder.Path, SpatialCopyChild.ReadyName);

        using var child = SpatialCopyChild.Start(data.Path, Short, folder.Path);
        try
        {
            Eventually.True(() => File.Exists(ready) || child.HasExited, "The child did not reach the writing of the copy.");
            Assert.False(child.HasExited, child.HasExited ? $"The child ended by itself: {child.StandardError.ReadToEnd()}" : string.Empty);

            var files = folder.CopyFiles();
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(840_132, new FileInfo(Assert.Single(files)).Length);
            }
            else
            {
                Assert.Empty(files);
            }

            child.Kill(entireProcessTree: true);
            Assert.True(child.WaitForExit(30_000), "The child did not exit after the kill.");

            Assert.DoesNotContain(child.ExitCode, (int[])[0, 2, 3]);
            Eventually.True(() => folder.CopyFiles().Length == 0, "The temporary file of the killed process stayed.");
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit(30_000);
            }
        }
    }

    private static unsafe long AddressOf(ReadOnlySpan<uint> region)
    {
        // The address is only compared with the alignment: nothing is read through it.
        fixed (uint* first = region)
        {
            return (long)first;
        }
    }

    private static Column<float> Make(string storage, float[] values, TempFile file) =>
        storage switch
        {
            "managed" => Column.Wrap(values),
            "native" => Column.CopyToNative<float>(values),
            _ => Column.MapFile<float>(file.Path),
        };

    // Builds the index of 1,000 integers as TX against the same values as double, checks it against the brute force,
    // and returns the widths of the three regions of its copy, which are also what the seam was asked for.
    private static int[] StoredWidths<TX>(PowTransform? transform, int offset = 0)
        where TX : unmanaged, INumber<TX>
    {
        var random = new Random(45);
        var ys = new double[1_000];
        var xs = new TX[ys.Length];
        for (var row = 0; row < ys.Length; row++)
        {
            ys[row] = random.Next(-100, 101) + offset;
            xs[row] = TX.CreateChecked(ys[row]);
        }

        var allocator = new RecordingCopyAllocator();
        using var built = SpatialBuilt.Over(
            Column.Wrap(xs),
            Column.Wrap(ys),
            new SpatialIndexInput { Rows = RowRange.All, XTransform = transform },
            allocator: allocator
        );

        Assert.True(built.Index.IsComplete);
        var request = Assert.Single(allocator.Requests);
        var copy = request.Copy!;
        Assert.Equal(request.ValueWidths, (int[])[copy.Width(0), copy.Width(1)]);
        Func<double, double>? forward = transform is null ? null : value => Forward(transform, value);
        SpatialReference.Check(
            built.Snapshot,
            SpatialAxisData.Of(xs, forward),
            SpatialAxisData.Of(ys),
            new RowRange(0, xs.Length),
            SpatialReference.Small
        );
        return [copy.Width(0), copy.Width(1), copy.Width(2)];
    }

    private static double Forward(PowTransform transform, double value)
    {
        var result = 0.0;
        transform.Forward(MemoryMarshal.CreateReadOnlySpan(ref value, 1), MemoryMarshal.CreateSpan(ref result, 1));
        return result;
    }

    // Starts a build of 70,001 float points that cannot make its copy, and returns what Completion ended with after
    // the checks that every such failure shares.
    private static Exception FailedBuild(SpatialCopyAllocator allocator, string tempFolder, bool mappedColumns)
    {
        var (xs, ys) = SpatialData.Uniform(Short, seed: 46);
        using var xFile = TempFile.Write<float>(xs, 0);
        using var yFile = TempFile.Write<float>(ys, 0);
        using var pool = new PlotWorkerPool(3);
        using var x = mappedColumns ? Column.MapFile<float>(xFile.Path) : Column.Wrap(xs);
        using var y = mappedColumns ? Column.MapFile<float>(yFile.Path) : Column.Wrap(ys);
        using var index = SpatialReference.Start(
            pool,
            x,
            y,
            new SpatialIndexInput { Rows = RowRange.All, TempFolder = tempFolder },
            allocator: allocator
        );

        var fault = Eventually.Faults(index.Completion);

        Assert.Same(fault, index.Fault);
        Assert.Equal(SpatialIndexState.Failed, index.State);
        Assert.False(index.IsComplete);
        var snapshot = index.Acquire();
        Assert.Equal(SpatialStage.None, snapshot.Stage);
        Assert.Equal(new SpatialCount(0, Short), snapshot.Count(SpatialReference.Everything, 32));
        snapshot.Release();
        return fault;
    }
}
