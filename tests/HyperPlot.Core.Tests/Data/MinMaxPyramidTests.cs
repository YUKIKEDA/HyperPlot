using System.Numerics;

namespace HyperPlot.Tests;

/// <summary>Tests that build columns of several megabytes; they run apart from the others (.cursor/rules/testing.mdc).</summary>
[CollectionDefinition("Large data", DisableParallelization = true)]
public sealed class LargeDataGroup;

[Collection("Large data")]
public sealed class MinMaxPyramidTests
{
    private static readonly int[] BoundLengths = [1, 65, PyramidData.Length];

    public static TheoryData<string, string> Cases =>
        new()
        {
            { "float", "None" },
            { "float", "Sparse" },
            { "float", "Buckets" },
            { "float", "Chunk" },
            { "float", "All" },
            { "double", "Sparse" },
            { "int", "None" },
            { "long", "None" },
            { "byte", "None" },
        };

    /// <summary>
    /// The exact range query equals a loop over every element, across chunk seams, bucket edges, and NaN patterns.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two chunks and 70,001 elements of seeded values with ties; NaN none, sparse, in whole and partial buckets, in a whole
    ///   chunk, or everywhere (floating types); a pyramid built to completion.
    ///
    /// Steps:
    /// - Query 400 ranges: random starts and lengths from 0 to the whole column (log-distributed), plus ranges that start or end
    ///   on chunk seams and 64- and 4096-element boundaries.
    ///
    /// Expected:
    /// - Min, Max, Count, and HasNaN equal the loop's; Min and Max are default when Count is 0; no result is provisional.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Cases))]
    public void GetMinMax_RandomRanges_EqualALoopOverEveryElement(string type, string pattern)
    {
        switch (type)
        {
            case "float":
                CheckRanges<float>(pattern);
                break;
            case "double":
                CheckRanges<double>(pattern);
                break;
            case "int":
                CheckRanges<int>(pattern);
                break;
            case "long":
                CheckRanges<long>(pattern);
                break;
            default:
                CheckRanges<byte>(pattern);
                break;
        }
    }

    /// <summary>
    /// With NaN in every bucket, Count comes from the buckets' counts: a query reads at most the raw ends of its partial chunks.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two chunks and 70,001 floats with NaN at one in eight (every 64-element bucket partly NaN); the pyramid built and the
    ///   chunk statistics computed.
    ///
    /// Steps:
    /// - Query [100, 2 chunks + 5,000), whose first and last chunks are partial and whose middle chunk is whole; count the raw
    ///   values the query read.
    ///
    /// Expected:
    /// - Count, Min, and Max equal the loop's; at most 2 · (63 + 2 · 64) raw values were read: per partial chunk, the raw end
    ///   (at most 63) and the 64-element buckets where the minimum and the maximum are located.
    /// </remarks>
    [Fact]
    public void GetMinMax_SparseNaN_ReadsOnlyTheRawEnds()
    {
        var random = new Random(53);
        var values = Enumerable.Range(0, PyramidData.Length).Select(_ => random.Next(8) == 0 ? float.NaN : (float)random.NextDouble()).ToArray();
        using var pool = new PlotWorkerPool(2);
        using var column = Column.Wrap(values);
        using var pyramid = Build(pool, column);
        const long start = 100;
        const long end = (2L * Column.ChunkLength) + 5000;
        var before = pyramid.RawElementsRead;

        var range = pyramid.GetMinMax(start, end - start);

        var expected = Reference.MinMax(PyramidData.AsStored(values), start, end);
        Assert.Equal((expected.Count, expected.Min, expected.Max), (range.Count, (double)range.Min, (double)range.Max));
        Assert.InRange(pyramid.RawElementsRead - before, 0, 2 * (63 + (2 * 64)));
    }

    /// <summary>
    /// A build stores the statistics of the chunks it computed them for, so that queries of whole chunks and X searches find
    /// them (design §3.2, §3.3), equal to the statistics the column computes itself.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two chunks and 70,001 floats with sparse NaN, and as many doubles without NaN, sorted; no statistics computed.
    ///
    /// Steps:
    /// - Build each pyramid to completion; read each chunk's stored statistics; compute the statistics of a copy of each
    ///   column, which shares no state.
    ///
    /// Expected:
    /// - Every chunk has stored statistics, equal to the copy's in every field; the sorted column is non-decreasing.
    /// </remarks>
    [Fact]
    public void Build_NoStatsYet_StoresTheStatsOfItsPass()
    {
        using var pool = new PlotWorkerPool(2);
        CheckStored(PyramidData.Convert<float>(PyramidData.Values(PyramidData.Length, "Sparse", 5, integral: false)));
        CheckStored(Enumerable.Range(0, PyramidData.Length).Select(i => i * 0.5).ToArray());

        void CheckStored<T>(T[] values)
            where T : unmanaged, INumber<T>
        {
            using var column = Column.Wrap(values);
            using var copy = Column.Wrap((T[])values.Clone());
            using var pyramid = Build(pool, column);
            for (var chunk = 0; chunk < column.ChunkCount; chunk++)
            {
                Assert.True(column.TryGetStoredStats(chunk, out var stored));
                var expected = copy.GetStats(chunk);
                Assert.Equal(
                    (expected.Min, expected.Max, expected.First, expected.Last, expected.Sum, expected.Count, expected.NaNCount),
                    (stored.Min, stored.Max, stored.First, stored.Last, stored.Sum, stored.Count, stored.NaNCount)
                );
                Assert.Equal(expected.IsNonDecreasing, stored.IsNonDecreasing);
            }

            Assert.Equal(typeof(T) == typeof(double), column.IsNonDecreasing());
        }
    }

    /// <summary>
    /// The pyramid's blocks stay within the bound of design §3.3.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - float and double columns of two chunks and 70,001 elements, and of 1 and 65 elements.
    ///
    /// Steps:
    /// - Read the bytes of the blocks.
    ///
    /// Expected:
    /// - At most n · 2 · sizeof(T) · (1/64 + 1/512 + 1/4096 + 1/32768 + 1/262144) for the minima and maxima, plus the counts
    ///   n · (1/64 + 2/512 + 2/4096 + 2/32768 + 4/262144), plus per chunk 5 partial buckets of 2 · sizeof(T) + 4 bytes and 3
    ///   bytes of alignment (design §3.3).
    /// </remarks>
    [Fact]
    public void BlockBytes_Columns_StayWithinTheDesignBound()
    {
        using var pool = new PlotWorkerPool(1);
        foreach (var length in BoundLengths)
        {
            CheckBound<float>(pool, length);
            CheckBound<double>(pool, length);
        }

        static void CheckBound<T>(PlotWorkerPool pool, int length)
            where T : unmanaged, INumber<T>
        {
            using var column = Column.Wrap(new T[length]);
            using var pyramid = pool.BuildPyramid(column);
            var size = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
            var fraction = (1.0 / 64) + (1.0 / 512) + (1.0 / 4096) + (1.0 / 32768) + (1.0 / 262144);
            var counts = (length / 64.0) + (2.0 * length * ((1.0 / 512) + (1.0 / 4096) + (1.0 / 32768))) + (4.0 * length / 262144);

            // Per chunk: 5 partial buckets of min, max, and a count of at most 4 bytes, and at most 3 bytes of alignment.
            var bound = (length * 2.0 * size * fraction) + counts + (column.ChunkCount * 5.0 * ((2 * size) + 4)) + (3.0 * column.ChunkCount);
            Assert.InRange(pyramid.BlockBytes, 1, (long)bound);
        }
    }

    /// <summary>
    /// A column of no elements has a pyramid that is complete when made, and an empty range has no values.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An empty float column; a column of 1 element.
    ///
    /// Steps:
    /// - Build pyramids; query the empty range of each, and the single element.
    ///
    /// Expected:
    /// - The empty column's pyramid is complete at once; empty ranges give Count 0; the single element is its own min and max.
    /// </remarks>
    [Fact]
    public void GetMinMax_SmallColumns_AnswerAtTheSmallEnd()
    {
        using var pool = new PlotWorkerPool(1);
        using var empty = Column.Wrap(Array.Empty<float>());
        using var emptyPyramid = pool.BuildPyramid(empty);
        Assert.True(emptyPyramid.Completion.IsCompletedSuccessfully);
        Assert.True(emptyPyramid.IsComplete);
        Assert.Equal(0, emptyPyramid.GetMinMax(0, 0).Count);

        var single = new float[1];
        single[0] = 4.5f;
        using var one = Column.Wrap(single);
        using var onePyramid = Build(pool, one);
        Assert.Equal(0, onePyramid.GetMinMax(1, 0).Count);
        var range = onePyramid.GetMinMax(0, 1);
        Assert.Equal((4.5f, 4.5f, 1L, false), (range.Min, range.Max, range.Count, range.IsProvisional));
    }

    /// <summary>
    /// A range outside the column throws, and a disposed pyramid throws instead of reading freed blocks.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pyramid over 10 floats.
    ///
    /// Steps:
    /// - Query (-1, 1), (5, 6), and (0, -1); dispose twice and query.
    ///
    /// Expected:
    /// - ArgumentOutOfRangeException for each range; the second Dispose does nothing; ObjectDisposedException after it.
    /// </remarks>
    [Fact]
    public void GetMinMax_BadRangeOrDisposed_Throws()
    {
        using var pool = new PlotWorkerPool(1);
        using var column = Column.Wrap(new float[10]);
        var pyramid = Build(pool, column);

        Assert.Throws<ArgumentOutOfRangeException>(() => pyramid.GetMinMax(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => pyramid.GetMinMax(5, 6));
        Assert.Throws<ArgumentOutOfRangeException>(() => pyramid.GetMinMax(0, -1));
        pyramid.Dispose();
        pyramid.Dispose();
        Assert.Throws<ObjectDisposedException>(() => pyramid.GetMinMax(0, 1));
    }

    /// <summary>
    /// Steady-state queries and extractions over a built pyramid do not allocate (design §11, invariant 3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A float series of two chunks and 70,001 elements with sparse NaN, its pyramid built, X evenly spaced and stored;
    ///   one warm-up run of each call.
    ///
    /// Steps:
    /// - Run 100 times: a range query across a seam, and M4 over 1,920 columns with both gap modes and both X kinds.
    ///
    /// Expected:
    /// - 0 bytes on this thread.
    /// </remarks>
    [Fact]
    public void GetMinMaxAndExtract_SteadyState_AllocateNothing()
    {
        using var pool = new PlotWorkerPool(2);
        var values = PyramidData.Convert<float>(PyramidData.Values(PyramidData.Length, "Sparse", 7, integral: false));
        using var column = Column.Wrap(values);
        using var pyramid = Build(pool, column);
        var uniform = Column.Uniform(0, 1, values.Length);
        using var stored = Column.Wrap(Enumerable.Range(0, values.Length).Select(i => (double)i).ToArray());
        var boundaries = Enumerable.Range(0, 1921).Select(k => k * (values.Length / 1920.0)).ToArray();
        var points = new M4Point[M4.MaxPointCount(1920)];
        Eventually.Completes(pool.PrecomputeStats(stored));
        Run();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            Run();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        void Run()
        {
            _ = pyramid.GetMinMax(Column.ChunkLength - 1000, 5000).Count;
            _ = M4.Extract(uniform, pyramid, boundaries, points).Count;
            _ = M4.Extract(stored, pyramid, boundaries, points, new M4Options(M4Gaps.Connect)).Count;
        }
    }

    internal static MinMaxPyramid<T> Build<T>(PlotWorkerPool pool, Column<T> column)
        where T : unmanaged, INumber<T>
    {
        var pyramid = pool.BuildPyramid(column);
        Eventually.Completes(pyramid.Completion);
        return pyramid;
    }

    private static void CheckRanges<T>(string pattern)
        where T : unmanaged, INumber<T>
    {
        var integral = typeof(T) != typeof(float) && typeof(T) != typeof(double);
        var values = PyramidData.Convert<T>(PyramidData.Values(PyramidData.Length, pattern, 11, integral));
        var reference = PyramidData.AsStored(values);
        using var pool = new PlotWorkerPool(4);
        using var column = Column.Wrap(values);
        using var pyramid = Build(pool, column);
        var random = new Random(23);
        var ranges = new List<(long Start, long Length)>
        {
            (0, values.Length),
            (0, Column.ChunkLength),
            (Column.ChunkLength, Column.ChunkLength),
            (Column.ChunkLength - 64, 128),
            (Column.ChunkLength - 1, 2),
            (4096, 4096),
            (4095, 4098),
            (64, 63),
            (63, 1),
            (values.Length - 1, 1),
            (values.Length, 0),
        };
        for (var i = 0; i < 390; i++)
        {
            var length = (long)Math.Min(values.Length, Math.Pow(values.Length, random.NextDouble()));
            var start = random.NextInt64(0, values.Length - length + 1);
            ranges.Add((start, length));
        }

        foreach (var (start, length) in ranges)
        {
            var actual = pyramid.GetMinMax(start, length);
            var expected = Reference.MinMax(reference, start, start + length);
            Assert.False(actual.IsProvisional);
            Assert.Equal(expected.Count, actual.Count);
            Assert.Equal(expected.HasNaN, actual.HasNaN);
            Assert.Equal(expected.Min, double.CreateTruncating(actual.Min));
            Assert.Equal(expected.Max, double.CreateTruncating(actual.Max));
        }
    }
}
