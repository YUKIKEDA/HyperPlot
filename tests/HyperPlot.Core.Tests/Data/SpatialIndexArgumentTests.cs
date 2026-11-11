namespace HyperPlot.Tests;

/// <summary>The exceptions of the arguments of a spatial tile index and of its queries (design §3.4, row C3a).</summary>
[Collection("Large data")]
public sealed class SpatialIndexArgumentTests
{
    private static readonly SpatialIndexInput AllRows = new() { Rows = RowRange.All };

    /// <summary>A null column throws before an index is made.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread and one float column of 8 rows.
    ///
    /// Steps:
    /// - Call BuildSpatialIndex with null for x, and then with null for y.
    ///
    /// Expected:
    /// - Each call throws ArgumentNullException that names the parameter.
    /// </remarks>
    [Fact]
    public void BuildSpatialIndex_NullColumn_ThrowsArgumentNull()
    {
        using var pool = new PlotWorkerPool(1);
        using var column = Column.Wrap(new float[8]);

        var x = Assert.Throws<ArgumentNullException>(() => pool.BuildSpatialIndex<float, float>(null!, column, AllRows));
        var y = Assert.Throws<ArgumentNullException>(() => pool.BuildSpatialIndex<float, float>(column, null!, AllRows));

        Assert.Equal("x", x.ParamName);
        Assert.Equal("y", y.ParamName);
    }

    /// <summary>Columns of different lengths throw.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread, a float column of 8 rows, and a double column of 9 rows.
    ///
    /// Steps:
    /// - Call BuildSpatialIndex with the two columns.
    ///
    /// Expected:
    /// - ArgumentException.
    /// </remarks>
    [Fact]
    public void BuildSpatialIndex_ColumnsOfDifferentLengths_ThrowsArgument()
    {
        using var pool = new PlotWorkerPool(1);
        using var x = Column.Wrap(new float[8]);
        using var y = Column.Wrap(new double[9]);

        Assert.Throws<ArgumentException>(() => pool.BuildSpatialIndex(x, y, AllRows));
    }

    /// <summary>Rows that start below 0 or after their end throw.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread and two float columns of 8 rows.
    ///
    /// Steps:
    /// - Call BuildSpatialIndex with the rows [<paramref name="start"/>, <paramref name="end"/>): [−1, 5) and [6, 5).
    ///
    /// Expected:
    /// - ArgumentException.
    /// </remarks>
    [Theory]
    [InlineData(-1, 5)]
    [InlineData(6, 5)]
    public void BuildSpatialIndex_RowsThatStartBelowZeroOrAfterTheirEnd_ThrowsArgument(long start, long end)
    {
        using var pool = new PlotWorkerPool(1);
        using var x = Column.Wrap(new float[8]);
        using var y = Column.Wrap(new float[8]);

        Assert.Throws<ArgumentException>(() => pool.BuildSpatialIndex(x, y, new SpatialIndexInput { Rows = new RowRange(start, end) }));
    }

    /// <summary>A passed range with an end that is not finite, or with its lower end above its upper end, throws on either axis.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread and two float columns of 8 rows.
    ///
    /// Steps:
    /// - Call BuildSpatialIndex with the range [<paramref name="lower"/>, <paramref name="upper"/>] as XRange, and then as
    ///   YRange: NaN, +∞, and −∞ at each end, and [2, 1].
    ///
    /// Expected:
    /// - Both calls throw ArgumentException.
    /// </remarks>
    [Theory]
    [InlineData(double.NaN, 1)]
    [InlineData(0, double.NaN)]
    [InlineData(double.NegativeInfinity, 1)]
    [InlineData(0, double.PositiveInfinity)]
    [InlineData(double.PositiveInfinity, double.PositiveInfinity)]
    [InlineData(2, 1)]
    public void BuildSpatialIndex_RangeThatIsNotFiniteOrReversed_ThrowsArgument(double lower, double upper)
    {
        using var pool = new PlotWorkerPool(1);
        using var x = Column.Wrap(new float[8]);
        using var y = Column.Wrap(new float[8]);
        var range = new ScaleRange(0, lower, upper);

        Assert.Throws<ArgumentException>(() => pool.BuildSpatialIndex(x, y, AllRows with { XRange = range }));
        Assert.Throws<ArgumentException>(() => pool.BuildSpatialIndex(x, y, AllRows with { YRange = range }));
    }

    /// <summary>The rows of a unit are a power of two of at most 2^20, the rows of a chunk; the default is one.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread and two float columns of 8 rows.
    ///
    /// Steps:
    /// - Read RowsPerUnit of the default constants.
    /// - Call the overload that takes the constants with <paramref name="rowsPerUnit"/> rows a unit: 0, −4, 3, 1,000, and 2^21.
    ///
    /// Expected:
    /// - The default is a power of two of at most 2^20.
    /// - The call throws ArgumentException.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    [InlineData(3)]
    [InlineData(1_000)]
    [InlineData(1 << 21)]
    public void BuildSpatialIndex_UnitThatIsNotAPowerOfTwoWithinAChunk_ThrowsArgument(int rowsPerUnit)
    {
        using var pool = new PlotWorkerPool(1);
        using var x = Column.Wrap(new float[8]);
        using var y = Column.Wrap(new float[8]);
        var tuning = SpatialReference.Small with { RowsPerUnit = rowsPerUnit };

        Assert.True(int.IsPow2(SpatialIndexTuning.Default.RowsPerUnit));
        Assert.InRange(SpatialIndexTuning.Default.RowsPerUnit, 1, 1 << 20);
        Assert.Equal(1 << 20, Column.ChunkLength);
        Assert.Throws<ArgumentException>(() => pool.BuildSpatialIndex(x, y, AllRows, tuning, SpatialCopyAllocator.Default));
    }

    /// <summary>A disposed pool makes no index.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread that is disposed, and two float columns of 8 rows.
    ///
    /// Steps:
    /// - Call BuildSpatialIndex.
    ///
    /// Expected:
    /// - ObjectDisposedException.
    /// </remarks>
    [Fact]
    public void BuildSpatialIndex_DisposedPool_ThrowsObjectDisposed()
    {
        var pool = new PlotWorkerPool(1);
        pool.Dispose();
        using var x = Column.Wrap(new float[8]);
        using var y = Column.Wrap(new float[8]);

        Assert.Throws<ObjectDisposedException>(() => pool.BuildSpatialIndex(x, y, AllRows));
    }

    /// <summary>A rectangle with a NaN end or with a lower end above its upper end throws from Count and from GetCells.</summary>
    /// <remarks>
    /// Preconditions:
    /// - The full snapshot of 1,000 evenly spread float points.
    ///
    /// Steps:
    /// - Call Count and GetCells with the rectangle [<paramref name="xMin"/>, <paramref name="xMax"/>] ×
    ///   [<paramref name="yMin"/>, <paramref name="yMax"/>]: NaN at each of the four ends, x reversed, and y reversed.
    ///
    /// Expected:
    /// - Both throw ArgumentException that names the rectangle.
    /// </remarks>
    [Theory]
    [InlineData(double.NaN, 1, 0, 1)]
    [InlineData(0, double.NaN, 0, 1)]
    [InlineData(0, 1, double.NaN, 1)]
    [InlineData(0, 1, 0, double.NaN)]
    [InlineData(2, 1, 0, 1)]
    [InlineData(0, 1, 2, 1)]
    public void Query_RectangleWithNaNOrReversedEnds_ThrowsArgument(double xMin, double xMax, double yMin, double yMax)
    {
        var (xs, ys) = SpatialData.Uniform(1_000, seed: 20);
        using var built = SpatialBuilt.Of(xs, ys);
        var rect = new SpatialRect(xMin, xMax, yMin, yMax);

        var count = Assert.Throws<ArgumentException>(() => built.Snapshot.Count(rect, 8));
        var cells = Assert.Throws<ArgumentException>(() => built.Snapshot.GetCells(rect, 8, default));

        Assert.Equal("rect", count.ParamName);
        Assert.Equal("rect", cells.ParamName);
    }

    /// <summary>A deepest level below 0 or above 32 throws from Count and from GetCells; 0 and 32 do not.</summary>
    /// <remarks>
    /// Preconditions:
    /// - The full snapshot of 1,000 evenly spread float points.
    ///
    /// Steps:
    /// - Call Count and GetCells over every value with maxLevel −1 and 33, and then with 0 and 32.
    ///
    /// Expected:
    /// - −1 and 33 throw ArgumentOutOfRangeException that names maxLevel.
    /// - 0 and 32 answer: every point is inside.
    /// </remarks>
    [Fact]
    public void Query_LevelOutsideZeroToThirtyTwo_ThrowsArgumentOutOfRange()
    {
        var (xs, ys) = SpatialData.Uniform(1_000, seed: 21);
        using var built = SpatialBuilt.Of(xs, ys);
        var all = SpatialReference.Everything;

        foreach (var level in (int[])[-1, 33])
        {
            var count = Assert.Throws<ArgumentOutOfRangeException>(() => built.Snapshot.Count(all, level));
            var cells = Assert.Throws<ArgumentOutOfRangeException>(() => built.Snapshot.GetCells(all, level, default));
            Assert.Equal("maxLevel", count.ParamName);
            Assert.Equal("maxLevel", cells.ParamName);
        }

        Assert.Equal(new SpatialCount(1_000, 0), built.Snapshot.Count(all, 0));
        Assert.Equal(new SpatialCount(1_000, 0), built.Snapshot.Count(all, 32));
        Assert.Equal(1, built.Snapshot.GetCells(all, 0, default));
    }

    /// <summary>ReadPoints throws for spans of different lengths and for points outside the copy.</summary>
    /// <remarks>
    /// Preconditions:
    /// - The full snapshot of 1,000 evenly spread float points.
    ///
    /// Steps:
    /// - Call ReadPoints with spans of 3, 3, and 2 elements, of 3, 2, and 3, and of 2, 3, and 3.
    /// - Call it with spans of 3 elements from −1, from 998, and from 1,001, and with spans of 1,001 elements from 0.
    /// - Call it with spans of 3 elements from 997, and with empty spans from 1,000.
    ///
    /// Expected:
    /// - The different lengths throw ArgumentException.
    /// - The points outside throw ArgumentOutOfRangeException.
    /// - The last two calls do not throw.
    /// </remarks>
    [Fact]
    public void ReadPoints_SpansOfDifferentLengthsOrPointsOutsideTheCopy_Throws()
    {
        var (xs, ys) = SpatialData.Uniform(1_000, seed: 22);
        using var built = SpatialBuilt.Of(xs, ys);
        var snapshot = built.Snapshot;
        var x = new double[1_001];
        var y = new double[1_001];
        var rows = new long[1_001];

        Assert.Throws<ArgumentException>(() => snapshot.ReadPoints(0, x.AsSpan(0, 3), y.AsSpan(0, 3), rows.AsSpan(0, 2)));
        Assert.Throws<ArgumentException>(() => snapshot.ReadPoints(0, x.AsSpan(0, 3), y.AsSpan(0, 2), rows.AsSpan(0, 3)));
        Assert.Throws<ArgumentException>(() => snapshot.ReadPoints(0, x.AsSpan(0, 2), y.AsSpan(0, 3), rows.AsSpan(0, 3)));
        foreach (var first in (long[])[-1, 998, 1_001, long.MaxValue])
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.ReadPoints(first, x.AsSpan(0, 3), y.AsSpan(0, 3), rows.AsSpan(0, 3)));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.ReadPoints(0, x, y, rows));
        snapshot.ReadPoints(997, x.AsSpan(0, 3), y.AsSpan(0, 3), rows.AsSpan(0, 3));
        snapshot.ReadPoints(1_000, default, default, default);
    }
}
