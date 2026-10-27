using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace HyperPlot.Tests;

public sealed class ColumnTests
{
    /// <summary>
    /// A column splits into chunks of 2^20 elements, with a shorter last chunk.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Lengths 0, 1, one chunk, one chunk and one element, and two chunks and a half.
    ///
    /// Steps:
    /// - Wrap an array of each length and read ChunkCount and each chunk's length.
    ///
    /// Expected:
    /// - ChunkCount is the length divided by 2^20, rounded up; every chunk but the last holds 2^20 elements; the lengths add up.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Column.ChunkLength)]
    [InlineData(Column.ChunkLength + 1)]
    [InlineData((2 * Column.ChunkLength) + (Column.ChunkLength / 2))]
    public void ChunkCount_Lengths_SplitIntoFullChunksAndAShorterLast(int length)
    {
        using var column = Column.Wrap(new float[length]);

        var expectedCount = (length + Column.ChunkLength - 1) / Column.ChunkLength;
        Assert.Equal(expectedCount, column.ChunkCount);
        long total = 0;
        for (var chunk = 0; chunk < column.ChunkCount; chunk++)
        {
            var chunkLength = column.GetChunk(chunk).Length;
            Assert.Equal(column.GetChunkLength(chunk), chunkLength);
            if (chunk < column.ChunkCount - 1)
            {
                Assert.Equal(Column.ChunkLength, chunkLength);
            }

            total += chunkLength;
        }

        Assert.Equal(length, total);
    }

    /// <summary>
    /// Every storage reads back the values it was made from, across a chunk seam.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The values 0, 1, 2, ... over one chunk and 10 elements.
    ///
    /// Steps:
    /// - Make a wrapped, a native-copied, a native-filled, and a mapped column from them.
    /// - Read elements on both sides of the seam with GetValue, and the second chunk with GetChunk.
    ///
    /// Expected:
    /// - Each element equals its index, in every storage, and StorageKind names the storage.
    /// </remarks>
    [Fact]
    public void GetValue_EveryStorage_ReadsTheValuesAcrossTheChunkSeam()
    {
        const int length = Column.ChunkLength + 10;
        var values = new int[length];
        for (var i = 0; i < length; i++)
        {
            values[i] = i;
        }

        using var file = TempFile.Write<int>(values, prefixBytes: 0);
        using var wrapped = Column.Wrap(values);
        using var copied = Column.CopyToNative<int>(values);
        using var filled = Column.CreateNative<int>(
            length,
            (first, destination) =>
            {
                for (var i = 0; i < destination.Length; i++)
                {
                    destination[i] = (int)(first + i);
                }
            }
        );
        using var mapped = Column.MapFile<int>(file.Path);

        Assert.Equal(ColumnStorageKind.Managed, wrapped.StorageKind);
        Assert.Equal(ColumnStorageKind.Native, copied.StorageKind);
        Assert.Equal(ColumnStorageKind.Native, filled.StorageKind);
        Assert.Equal(ColumnStorageKind.MemoryMapped, mapped.StorageKind);
        foreach (var column in new[] { wrapped, copied, filled, mapped })
        {
            Assert.Equal(length, column.Length);
            foreach (var index in new long[] { 0, Column.ChunkLength - 1, Column.ChunkLength, length - 1 })
            {
                Assert.Equal(index, column.GetValue(index));
            }

            var second = column.GetChunk(1);
            Assert.Equal(10, second.Length);
            Assert.Equal(Column.ChunkLength + 9, second[9]);
        }
    }

    /// <summary>
    /// Wrap reads the caller's array itself, and CopyToNative reads a copy.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - One array, wrapped and copied to native memory.
    ///
    /// Steps:
    /// - Change an element of the array.
    ///
    /// Expected:
    /// - The wrapped column reads the new value (no copy, requirement D-3); the native column keeps the old one.
    /// </remarks>
    [Fact]
    public void Wrap_ArrayChanged_ReadsTheChangeWhileTheNativeCopyDoesNot()
    {
        var values = new float[] { 1, 2, 3 };
        using var wrapped = Column.Wrap(values);
        using var copied = Column.CopyToNative<float>(values);

        values[1] = 20;

        Assert.Equal(20, wrapped.GetValue(1));
        Assert.Equal(2, copied.GetValue(1));
    }

    /// <summary>
    /// CreateNative calls the filler once per chunk, in order, with each chunk's first index and length.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A length of two chunks and 3 elements.
    ///
    /// Steps:
    /// - Record each call of the filler.
    ///
    /// Expected:
    /// - Three calls: (0, 2^20), (2^20, 2^20), (2^21, 3).
    /// </remarks>
    [Fact]
    public void CreateNative_ThreeChunks_FillsEachChunkOnceInOrder()
    {
        var calls = new List<(long First, int Length)>();

        using var column = Column.CreateNative<double>((2L * Column.ChunkLength) + 3, (first, destination) => calls.Add((first, destination.Length)));

        Assert.Equal([(0L, Column.ChunkLength), (Column.ChunkLength, Column.ChunkLength), (2L * Column.ChunkLength, 3)], calls);
    }

    /// <summary>
    /// Native chunks start on a 64-byte boundary (design §3.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A native column of two chunks of bytes, an element type whose natural alignment is 1.
    ///
    /// Steps:
    /// - Read the address of each chunk's first element.
    ///
    /// Expected:
    /// - Each address is a multiple of 64.
    /// </remarks>
    [Fact]
    public unsafe void CopyToNative_EveryChunk_StartsOn64Bytes()
    {
        using var column = Column.CopyToNative<byte>(new byte[Column.ChunkLength + 1]);

        for (var chunk = 0; chunk < column.ChunkCount; chunk++)
        {
            var address = (nint)Unsafe.AsPointer(ref MemoryMarshal.GetReference(column.GetChunk(chunk)));
            Assert.Equal(0, address % 64);
        }
    }

    /// <summary>
    /// MapFile reads values after a byte offset, and a given length reads only that many.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A file of 16 header bytes and then the doubles 0.5, 1.5, ..., 9.5.
    ///
    /// Steps:
    /// - Map it from offset 16 with no length, and with a length of 4.
    ///
    /// Expected:
    /// - The first column has 10 values and the second 4, each value i + 0.5.
    /// </remarks>
    [Fact]
    public void MapFile_OffsetAndLength_ReadsTheValuesAfterTheHeader()
    {
        var values = Enumerable.Range(0, 10).Select(i => i + 0.5).ToArray();
        using var file = TempFile.Write<double>(values, prefixBytes: 16);

        using var all = Column.MapFile<double>(file.Path, offset: 16);
        using var some = Column.MapFile<double>(file.Path, offset: 16, length: 4);

        Assert.Equal(10, all.Length);
        Assert.Equal(4, some.Length);
        Assert.Equal(values, all.GetChunk(0).ToArray());
        Assert.Equal(values[..4], some.GetChunk(0).ToArray());
    }

    /// <summary>
    /// MapFile refuses an offset that is not a multiple of the element size, and a length past the end of the file.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A file of 4 floats.
    ///
    /// Steps:
    /// - Map it at offset 2, and with a length of 5.
    ///
    /// Expected:
    /// - ArgumentException for the offset and ArgumentOutOfRangeException for the length.
    /// </remarks>
    [Fact]
    public void MapFile_BadOffsetOrLength_Throws()
    {
        using var file = TempFile.Write<float>([1, 2, 3, 4], prefixBytes: 0);

        Assert.Throws<ArgumentException>(() => Column.MapFile<float>(file.Path, offset: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => Column.MapFile<float>(file.Path, length: 5));
    }

    /// <summary>
    /// An empty file maps to an empty column; mapping an empty view would fail.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A file of 0 bytes.
    ///
    /// Steps:
    /// - Map it.
    ///
    /// Expected:
    /// - A column with length 0 and no chunks.
    /// </remarks>
    [Fact]
    public void MapFile_EmptyFile_MakesAnEmptyColumn()
    {
        using var file = TempFile.Write<float>([], prefixBytes: 0);

        using var column = Column.MapFile<float>(file.Path);

        Assert.Equal(0, column.Length);
        Assert.Equal(0, column.ChunkCount);
    }

    /// <summary>
    /// A disposed native or mapped column throws instead of reading freed memory.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A native and a mapped column of 3 floats.
    ///
    /// Steps:
    /// - Dispose each twice, then read a chunk.
    ///
    /// Expected:
    /// - The second Dispose does nothing; the read throws ObjectDisposedException.
    /// </remarks>
    [Fact]
    public void GetChunk_AfterDispose_Throws()
    {
        using var file = TempFile.Write<float>([1, 2, 3], prefixBytes: 0);
        var native = Column.CopyToNative<float>([1, 2, 3]);
        var mapped = Column.MapFile<float>(file.Path);

        foreach (var column in new[] { native, mapped })
        {
            column.Dispose();
            column.Dispose();
            Assert.Throws<ObjectDisposedException>(() => column.GetChunk(0).Length);
        }
    }

    /// <summary>
    /// Reads outside the column throw.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A column of 3 elements.
    ///
    /// Steps:
    /// - Read chunk -1 and chunk 1, and elements -1 and 3.
    ///
    /// Expected:
    /// - ArgumentOutOfRangeException for each.
    /// </remarks>
    [Fact]
    public void GetChunkAndGetValue_OutOfRange_Throw()
    {
        using var column = Column.Wrap(new float[3]);

        Assert.Throws<ArgumentOutOfRangeException>(() => column.GetChunk(-1).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => column.GetChunk(1).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => column.GetValue(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => column.GetValue(3));
    }

    /// <summary>
    /// A column holds only the element types of design §3.1.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - short, a numeric type outside the list.
    ///
    /// Steps:
    /// - Wrap a short array and copy one to native memory.
    ///
    /// Expected:
    /// - NotSupportedException from both.
    /// </remarks>
    [Fact]
    public void Wrap_UnsupportedType_Throws()
    {
        Assert.Throws<NotSupportedException>(() => Column.Wrap(new short[1]));
        Assert.Throws<NotSupportedException>(() => Column.CopyToNative<short>(new short[1]));
    }

    /// <summary>
    /// Reading a chunk and a stored statistic does not allocate (design §11, invariant 3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A wrapped and a native column of two chunks, whose statistics were read once.
    ///
    /// Steps:
    /// - Read every chunk, a value, and every chunk's statistics 100 times while counting this thread's allocations.
    ///
    /// Expected:
    /// - 0 bytes.
    /// </remarks>
    [Fact]
    public void GetChunkAndGetStats_SteadyState_AllocateNothing()
    {
        using var wrapped = Column.Wrap(new float[Column.ChunkLength + 5]);
        using var native = Column.CopyToNative<float>(new float[Column.ChunkLength + 5]);
        Read(wrapped);
        Read(native);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            Read(wrapped);
            Read(native);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        static void Read(Column<float> column)
        {
            for (var chunk = 0; chunk < column.ChunkCount; chunk++)
            {
                _ = column.GetChunk(chunk).Length;
                _ = column.GetStats(chunk).Count;
            }

            _ = column.GetValue(column.Length - 1);
        }
    }
}
