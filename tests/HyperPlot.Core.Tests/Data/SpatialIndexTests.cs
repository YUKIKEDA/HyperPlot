using System.Runtime.InteropServices;

namespace HyperPlot.Tests;

/// <summary>The spatial tile index against the brute force over every row (design §3.4, row C3a).</summary>
[Collection("Large data")]
public sealed class SpatialIndexTests
{
    private const int Short = 70_001;

    /// <summary>The index of evenly spread points holds every point once, in cells that match a brute-force quantization.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 1,052,897 seeded points of float x and y, spread evenly; a pool of 3 threads; leaves of 16 points and units of 256 rows.
    ///
    /// Steps:
    /// - Build the index of every row, wait for Completion, and borrow the snapshot.
    ///
    /// Expected:
    /// - The snapshot is full and not provisional; the counts, the ranges, the copy, and the table at every level agree
    ///   with the brute force; no leaf is coincident or capped.
    /// </remarks>
    [Fact]
    public void Build_UniformPoints_AgreesWithBruteForce()
    {
        var (xs, ys) = SpatialData.Uniform(SpatialData.Length, seed: 1);
        using var built = SpatialBuilt.Of(xs, ys);

        AssertFinished(built);
        var leaves = Check(built, xs, ys);
        Assert.All(leaves, leaf => Assert.Equal(SpatialCellKind.Leaf, leaf.Kind));
    }

    /// <summary>The index of clusters inside clusters is split down to single grid cells where the points are.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 1,052,897 seeded points of double x and y in 3,125 clusters, each 2^-30 of the range wide, so about 5 grid cells
    ///   a side; a pool of 3 threads; leaves of 16 points.
    ///
    /// Steps:
    /// - Build the index of every row and borrow the snapshot.
    ///
    /// Expected:
    /// - The snapshot agrees with the brute force at every level.
    /// - Some leaf is at level 32, and some leaf of more than 16 points in one grid cell is a coincident leaf.
    /// </remarks>
    [Fact]
    public void Build_NestedClusters_SplitsDownToGridCells()
    {
        var (xs, ys) = SpatialData.Nested(SpatialData.Length, seed: 2);
        using var built = SpatialBuilt.Of(xs, ys);

        AssertFinished(built);
        var leaves = Check(built, xs, ys);
        Assert.Contains(leaves, leaf => leaf.Level == 32 && leaf.Kind == SpatialCellKind.Leaf);
        Assert.Contains(leaves, leaf => leaf.Kind == SpatialCellKind.CoincidentLeaf);
    }

    /// <summary>More points at one position than a leaf holds become one coincident leaf, in the order of their rows.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 3,000 points of float x and y: every third row is the point (12.5, 250), and the others are spread evenly.
    ///
    /// Steps:
    /// - Build the index and read the leaves.
    ///
    /// Expected:
    /// - The snapshot agrees with the brute force.
    /// - One leaf is coincident; it holds the 1,000 points of the one position.
    /// </remarks>
    [Fact]
    public void Build_ManyPointsAtOnePosition_MakesOneCoincidentLeaf()
    {
        var (xs, ys) = SpatialData.Uniform(3_000, seed: 3);
        for (var row = 0; row < xs.Length; row += 3)
        {
            xs[row] = 12.5f;
            ys[row] = 250f;
        }

        using var built = SpatialBuilt.Of(xs, ys);

        AssertFinished(built);
        var leaves = Check(built, xs, ys);
        var coincident = Assert.Single(leaves, leaf => leaf.Kind == SpatialCellKind.CoincidentLeaf);
        Assert.Equal(1_000, coincident.Count);
        var x = new double[1_000];
        var y = new double[1_000];
        var rows = new long[1_000];
        built.Snapshot.ReadPoints(coincident.First, x, y, rows);
        Assert.All(rows, row => Assert.Equal(0, row % 3));
    }

    /// <summary>A row with a value that is not finite is skipped, and its finite value on the other axis still widens that axis's range.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 70,001 points of float x and y, spread evenly over [-50, 50) × [0, 1000). Every 97th row has NaN in x, and every
    ///   89th has NaN in y. With <paramref name="infinities"/>, every 83rd row has +∞ or −∞ in x or y, so the range stage
    ///   scans the chunk; without, it reads the chunk's statistics.
    /// - Row 5 is (−1000, NaN): the least x of the column, in a row that is skipped for its y.
    ///
    /// Steps:
    /// - Build the index and borrow the snapshot.
    ///
    /// Expected:
    /// - The snapshot agrees with the brute force: the copy holds the rows whose two values are finite, and ExcludedCount
    ///   is the number of the others.
    /// - The range of x begins at −1000.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_ValuesThatAreNotFinite_AreExcludedAndCounted(bool infinities)
    {
        var (xs, ys) = SpatialData.Uniform(Short, seed: 4);
        for (var row = 0; row < Short; row++)
        {
            if (row % 97 == 0)
            {
                xs[row] = float.NaN;
            }

            if (row % 89 == 0)
            {
                ys[row] = float.NaN;
            }

            if (infinities && row % 83 == 0)
            {
                var turn = row / 83;
                var value = (turn & 2) == 0 ? float.PositiveInfinity : float.NegativeInfinity;
                ((turn & 1) == 0 ? xs : ys)[row] = value;
            }
        }

        xs[5] = -1000f;
        ys[5] = float.NaN;
        using var built = SpatialBuilt.Of(xs, ys);

        AssertFinished(built);
        Check(built, xs, ys);
        Assert.True(built.Snapshot.ExcludedCount > Short / 97);
        Assert.Equal(-1000, built.Snapshot.XRange.Origin);
        Assert.Equal(0, built.Snapshot.XRange.Lower);
    }

    /// <summary>A column of NaN gives an index with no point: every row is excluded.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 1,000 rows: x is NaN in every row, and y is spread evenly.
    ///
    /// Steps:
    /// - Build the index and ask it for the count of every value and for its cells.
    ///
    /// Expected:
    /// - PointCount is 0 and ExcludedCount is 1,000; the range of x has origin 0 and width 0.
    /// - The count is 0 inside and 0 touching, and no cell comes back.
    /// </remarks>
    [Fact]
    public void Build_ColumnOfNaN_ExcludesEveryRow()
    {
        var (xs, ys) = SpatialData.Uniform(1_000, seed: 5);
        Array.Fill(xs, float.NaN);
        using var built = SpatialBuilt.Of(xs, ys);

        AssertFinished(built);
        Check(built, xs, ys);
        Assert.Equal(0, built.Snapshot.PointCount);
        Assert.Equal(1_000, built.Snapshot.ExcludedCount);
        Assert.Equal(new ScaleRange(0, 0, 0), built.Snapshot.XRange);
        Assert.Equal(new SpatialCount(0, 0), built.Snapshot.Count(SpatialReference.Everything, 32));
        Assert.Equal(0, built.Snapshot.GetCells(SpatialReference.Everything, 32, default));
    }

    /// <summary>A range that the caller passes is used as it is, and the points outside it go to the cells at its edges.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 70,001 points of float x and y, spread evenly over [-50, 50) × [0, 1000).
    /// - The passed range of x is [-10, 10] from the origin -10, and that of y is [100.5, 400.5] from the origin 100: most
    ///   points are outside one of them.
    ///
    /// Steps:
    /// - Build the index with the range of x, of y, or both, and borrow the snapshot.
    ///
    /// Expected:
    /// - The snapshot's range of a passed axis is the passed range, and that of the other axis comes from the values.
    /// - The snapshot agrees with the brute force, which clamps a value outside the range to the first or the last cell.
    /// - Every point is counted: none is dropped for being outside.
    /// </remarks>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Build_PassedRanges_PutThePointsOutsideInTheEdgeCells(bool passX, bool passY)
    {
        var (xs, ys) = SpatialData.Uniform(Short, seed: 6);
        ScaleRange? xRange = passX ? new ScaleRange(-10, 0, 20) : null;
        ScaleRange? yRange = passY ? new ScaleRange(100, 0.5, 300.5) : null;
        using var built = SpatialBuilt.Of(
            xs,
            ys,
            new SpatialIndexInput
            {
                Rows = RowRange.All,
                XRange = xRange,
                YRange = yRange,
            }
        );

        AssertFinished(built);
        SpatialReference.Check(
            built.Snapshot,
            SpatialAxisData.Of(xs),
            SpatialAxisData.Of(ys),
            new RowRange(0, Short),
            SpatialReference.Small,
            xRange,
            yRange
        );
        Assert.Equal(Short, built.Snapshot.PointCount);
        Assert.Equal(new SpatialCount(Short, 0), built.Snapshot.Count(SpatialReference.Everything, 0));
    }

    /// <summary>An axis whose values are all one value has a range of width 0, and every point lies in its first cell.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 70,001 points: x is 7.5 in every row. With <paramref name="both"/>, y is 3 in every row; without, y is spread evenly.
    ///
    /// Steps:
    /// - Build the index and borrow the snapshot.
    ///
    /// Expected:
    /// - The range of x is [0.5, 0.5] from the origin 7.
    /// - The snapshot agrees with the brute force, whose cell on an axis of width 0 is 0.
    /// - With both axes of one value, the points are one coincident leaf.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_AxisOfOneValue_PutsEveryPointInTheFirstCell(bool both)
    {
        var (xs, ys) = SpatialData.Uniform(Short, seed: 7);
        Array.Fill(xs, 7.5f);
        if (both)
        {
            Array.Fill(ys, 3f);
        }

        using var built = SpatialBuilt.Of(xs, ys);

        AssertFinished(built);
        Assert.Equal(new ScaleRange(7, 0.5, 0.5), built.Snapshot.XRange);
        var leaves = Check(built, xs, ys);
        if (both)
        {
            var leaf = Assert.Single(leaves);
            Assert.Equal(SpatialCellKind.CoincidentLeaf, leaf.Kind);
            Assert.Equal(Short, leaf.Count);
        }
    }

    /// <summary>An index of a part of the rows holds those rows only, wherever the part begins and ends.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 1,052,897 seeded points of float x and y: one chunk of 1,048,576 rows and a short second one; units of 256 rows.
    /// - The rows [<paramref name="start"/>, <paramref name="end"/>): across the seam of the chunks, inside one unit, from
    ///   the middle of a unit to the middle of another, and from the second chunk to past the end of the columns.
    ///
    /// Steps:
    /// - Build the index of the rows and borrow the snapshot.
    ///
    /// Expected:
    /// - The snapshot agrees with the brute force over the rows that are in the columns: the ranges come from those rows
    ///   only, and the copy's row numbers are the rows of the columns.
    /// </remarks>
    [Theory]
    [InlineData(Column.ChunkLength - 1_001, Column.ChunkLength + 3_003)]
    [InlineData(300, 400)]
    [InlineData(777, 66_000)]
    [InlineData(Column.ChunkLength + 1, long.MaxValue)]
    public void Build_PartOfTheRows_HoldsThoseRowsOnly(long start, long end)
    {
        var (xs, ys) = SpatialData.Uniform(SpatialData.Length, seed: 8);
        var rows = new RowRange(start, end);
        using var built = SpatialBuilt.Of(xs, ys, new SpatialIndexInput { Rows = rows });

        AssertFinished(built);
        var read = new RowRange(start, rows.EndIn(xs.Length));
        SpatialReference.Check(built.Snapshot, SpatialAxisData.Of(xs), SpatialAxisData.Of(ys), read, SpatialReference.Small);
        Assert.Equal(read.End - read.Start, built.Snapshot.PointCount);
    }

    /// <summary>Integer columns keep exact differences from an origin that is their least value.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 70,001 rows: x is an int column over ±10^9, and y is a long column near 4 × 10^18, where a double holds only
    ///   every 512th integer.
    ///
    /// Steps:
    /// - Build the index and read every point.
    ///
    /// Expected:
    /// - The origin of each range is the least value of its column, and the range runs from 0 to greatest − least.
    /// - Each point's x and y are the exact integer differences of its row from the origins, and its cell is the cell of
    ///   those differences.
    /// </remarks>
    [Fact]
    public void Build_IntegerColumns_KeepExactDifferences()
    {
        var random = new Random(9);
        var xs = new int[Short];
        var ys = new long[Short];
        for (var row = 0; row < Short; row++)
        {
            xs[row] = random.Next(-1_000_000_000, 1_000_000_000);
            ys[row] = 4_000_000_000_000_000_000 + random.NextInt64(1L << 40);
        }

        using var built = SpatialBuilt.Of(xs, ys);

        AssertFinished(built);
        Check(built, xs, ys);
        Assert.Equal(xs.Min(), built.Snapshot.XRange.Origin);
        Assert.Equal(ys.Min(), built.Snapshot.YRange.Origin);
        Assert.Equal(0, built.Snapshot.YRange.Lower);
        Assert.Equal((double)(ys.Max() - ys.Min()), built.Snapshot.YRange.Upper);
    }

    /// <summary>Times one tick apart in 2026 fall in different grid cells, and their differences are exact (invariant 6).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A long column of .NET ticks from 2026-01-01 (about 6.39 × 10^17, where a double holds only every 128th tick), at
    ///   0, 1, 2, 2^31, 2^31 + 1, 2^32 − 3, 2^32 − 2, and 2^32 ticks after it; y is 0 in every row; leaves of 1 point.
    ///
    /// Steps:
    /// - Build the index and read its leaves and its points.
    ///
    /// Expected:
    /// - The range of x is [0, 2^32] from the first tick, so one grid cell is one tick.
    /// - Each point is a leaf of its own: no two times share a grid cell, or they would be one coincident leaf.
    /// - The x of each point is the exact number of ticks after the first.
    /// </remarks>
    [Fact]
    public void Build_TicksOneApart_FallInDifferentGridCells()
    {
        var first = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        long[] offsets = [1L << 31, 0, (1L << 32) - 2, 2, 1L << 32, 1, (1L << 31) + 1, (1L << 32) - 3];
        var xs = Array.ConvertAll(offsets, offset => first + offset);
        var ys = new int[xs.Length];
        var tuning = SpatialReference.Small with { LeafCapacity = 1 };
        using var built = SpatialBuilt.Of(xs, ys, tuning: tuning);

        AssertFinished(built);
        Assert.Equal(new ScaleRange(first, 0, 4_294_967_296.0), built.Snapshot.XRange);
        var leaves = SpatialReference.Check(built.Snapshot, SpatialAxisData.Of(xs), SpatialAxisData.Of(ys), new RowRange(0, xs.Length), tuning);
        Assert.Equal(offsets.Length, leaves.Length);
        Assert.All(leaves, leaf => Assert.Equal((1L, SpatialCellKind.Leaf), (leaf.Count, leaf.Kind)));
        var (x, _, _) = SpatialReference.ReadAll(built.Snapshot);
        double[] ticks = [0, 1, 2, 2_147_483_648, 2_147_483_649, 4_294_967_293, 4_294_967_294, 4_294_967_296];
        Assert.Equal(ticks, x);
    }

    /// <summary>
    /// A transformed float column is stored as the transform rounded to float: a value whose transform rounds to an
    /// infinity is excluded, and the cells come from the stored values.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 70,001 rows. x is a float column spread over [-50, 50) with the transform sign(v) × |v|^2; row 11 is 10^30 and
    ///   row 12 is −10^30, whose squares are finite as double and infinite as float.
    /// - y is a double column spread over [0, 1000) with the transform |v|^0.5, stored as double.
    ///
    /// Steps:
    /// - Build the index and borrow the snapshot.
    ///
    /// Expected:
    /// - ExcludedCount is 2: rows 11 and 12 are in neither the range nor the copy.
    /// - The x of each point is the transform of its row rounded to float, as a difference from the origin, and the y is
    ///   the transform as double; the cell of each point is the cell of these values.
    /// </remarks>
    [Fact]
    public void Build_TransformedFloatColumn_StoresTheRoundedTransform()
    {
        var (xs, floats) = SpatialData.Uniform(Short, seed: 10);
        var ys = Array.ConvertAll(floats, value => (double)value);
        xs[11] = 1e30f;
        xs[12] = -1e30f;
        var square = new PowTransform(2);
        var root = new PowTransform(0.5);
        using var built = SpatialBuilt.Of(
            xs,
            ys,
            new SpatialIndexInput
            {
                Rows = RowRange.All,
                XTransform = square,
                YTransform = root,
            }
        );

        AssertFinished(built);
        SpatialReference.Check(
            built.Snapshot,
            SpatialAxisData.Of(xs, Forward(square)),
            SpatialAxisData.Of(ys, Forward(root)),
            new RowRange(0, Short),
            SpatialReference.Small
        );
        Assert.Equal(2, built.Snapshot.ExcludedCount);
        Assert.InRange(built.Snapshot.XRange.Origin + built.Snapshot.XRange.Upper, 2_400, 2_500);
    }

    /// <summary>A transformed integer column is stored as double, next to a float column without a transform.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 70,001 rows. x is an int column over ±30,000 with the transform sign(v) × |v|^2; y is a float column with a
    ///   transform too (|v|^0.5), stored as float.
    ///
    /// Steps:
    /// - Build the index and borrow the snapshot.
    ///
    /// Expected:
    /// - The x of each point is the transform of its row as double, and the y is the transform rounded to float; the
    ///   snapshot agrees with the brute force over these values.
    /// </remarks>
    [Fact]
    public void Build_TransformedIntegerColumn_StoresDoubles()
    {
        var random = new Random(11);
        var (_, ys) = SpatialData.Uniform(Short, seed: 11);
        var xs = new int[Short];
        for (var row = 0; row < Short; row++)
        {
            xs[row] = random.Next(-30_000, 30_000);
        }

        var square = new PowTransform(2);
        var root = new PowTransform(0.5);
        using var built = SpatialBuilt.Of(
            xs,
            ys,
            new SpatialIndexInput
            {
                Rows = RowRange.All,
                XTransform = square,
                YTransform = root,
            }
        );

        AssertFinished(built);
        SpatialReference.Check(
            built.Snapshot,
            SpatialAxisData.Of(xs, Forward(square)),
            SpatialAxisData.Of(ys, Forward(root)),
            new RowRange(0, Short),
            SpatialReference.Small
        );
        Assert.Equal(0, built.Snapshot.ExcludedCount);
    }

    /// <summary>
    /// A small share for the table caps its rows: the table is the first rows of the table without a cap, and the cells
    /// that have no room for their children are capped leaves that hold every point.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 70,001 seeded points of double x and y in clusters inside clusters; leaves of 16 points.
    /// - A share of 0.01 of the source's 16 bytes a row: at most ⌊0.01 × 70,001 × 16 / 32⌋ = 350 rows.
    ///
    /// Steps:
    /// - Build the index with the share of 0.01, and again with a share that does not cap the table.
    ///
    /// Expected:
    /// - The capped table has 349 rows: a split adds 4 rows, and the next split would pass 350.
    /// - Each of its rows is the cell of the same row of the table without a cap, whose rows are made from the shallow
    ///   levels down. A cell that is split there is split when its children are among the 349 rows, and a capped leaf
    ///   otherwise; some leaf is capped.
    /// - The snapshot is full and not provisional, and agrees with the brute force: every point is in the copy once, in
    ///   its cell, and the points of a capped leaf are in the order of their rows.
    /// </remarks>
    [Fact]
    public void Build_SmallShareForTheTable_CapsTheRowsAndKeepsEveryPoint()
    {
        var (xs, ys) = SpatialData.Nested(Short, seed: 21);
        var small = SpatialReference.Small with { TableShare = 0.01 };
        using var whole = SpatialBuilt.Of(xs, ys);
        using var built = SpatialBuilt.Of(xs, ys, tuning: small);

        AssertFinished(built);
        var rows = built.Snapshot.Rows.ToArray();
        var full = whole.Snapshot.Rows.ToArray();
        Assert.Equal(349, rows.Length);
        Assert.True(full.Length > 350, $"The table without a cap has {full.Length} rows.");
        for (var i = 0; i < rows.Length; i++)
        {
            var kind =
                full[i].Kind != (byte)SpatialCellKind.Inner ? full[i].Kind
                : full[i].FirstChild < rows.Length ? (byte)SpatialCellKind.Inner
                : (byte)SpatialCellKind.CappedLeaf;
            var known = full[i].Kind is (byte)SpatialCellKind.Inner or (byte)SpatialCellKind.Leaf;
            if (rows[i].Code != full[i].Code || rows[i].Level != full[i].Level || (known && rows[i].Kind != kind))
            {
                Assert.Fail($"Row {i} is the cell {rows[i].Code} of level {rows[i].Level} with the kind {rows[i].Kind}.");
            }
        }

        var leaves = SpatialReference.Check(
            built.Snapshot,
            SpatialAxisData.Of(xs),
            SpatialAxisData.Of(ys),
            new RowRange(0, Short),
            small,
            capped: true
        );
        Assert.Contains(leaves, leaf => leaf.Kind == SpatialCellKind.CappedLeaf);
    }

    /// <summary>A share that leaves the table one row makes the root a capped leaf with every point in the order of the rows.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 70,001 seeded points of float x and y, spread evenly; leaves of 16 points.
    /// - A share of 10^-9, whose rows round down to 0: the table has at most 1 row.
    ///
    /// Steps:
    /// - Build the index, read the cells and every point.
    ///
    /// Expected:
    /// - The table is 1 row: the root, a capped leaf of 70,001 points.
    /// - The copy holds the rows 0 to 70,000 in their order, and the snapshot agrees with the brute force.
    /// - A rectangle that holds no cell of level 32 whole counts 0 inside and 70,001 touching.
    /// </remarks>
    [Fact]
    public void Build_ShareOfLessThanOneRow_MakesTheRootACappedLeaf()
    {
        var (xs, ys) = SpatialData.Uniform(Short, seed: 22);
        var small = SpatialReference.Small with { TableShare = 1e-9 };
        using var built = SpatialBuilt.Of(xs, ys, tuning: small);

        AssertFinished(built);
        var leaf = Assert.Single(
            SpatialReference.Check(built.Snapshot, SpatialAxisData.Of(xs), SpatialAxisData.Of(ys), new RowRange(0, Short), small, capped: true)
        );
        Assert.Equal(1, built.Snapshot.Rows.Length);
        Assert.Equal(new SpatialCell(0, 0, 0, Short, SpatialCellKind.CappedLeaf), leaf);
        Assert.Equal(Enumerable.Range(0, Short).Select(row => (long)row), SpatialReference.ReadAll(built.Snapshot).Rows);
        Assert.Equal(new SpatialCount(0, Short), built.Snapshot.Count(new SpatialRect(-10, 10, 100, 200), 32));
    }

    /// <summary>The counters of 8 bytes build the table and the copy of the counters of 4 bytes.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 70,001 seeded points of double x and y in clusters inside clusters; a pool of 3 threads; leaves of 16 points.
    ///
    /// Steps:
    /// - Build the index with the counters of 4 bytes, and again with the counters of 8 bytes, which the build uses from
    ///   2^32 rows.
    ///
    /// Expected:
    /// - The second snapshot agrees with the brute force.
    /// - The two tables are the same bytes, and the two copies hold the same points at the same places.
    /// </remarks>
    [Fact]
    public void Build_WideCounters_MakeTheSameTableAndCopy()
    {
        var (xs, ys) = SpatialData.Nested(Short, seed: 23);
        using var narrow = SpatialBuilt.Of(xs, ys);
        using var wide = SpatialBuilt.Of(xs, ys, tuning: SpatialReference.Small with { WideRows = 1 });

        AssertFinished(wide);
        Check(wide, xs, ys);
        Assert.True(
            MemoryMarshal.AsBytes(narrow.Snapshot.Rows).SequenceEqual(MemoryMarshal.AsBytes(wide.Snapshot.Rows)),
            "The tables of the two widths differ."
        );
        var (x0, y0, rows0) = SpatialReference.ReadAll(narrow.Snapshot);
        var (x1, y1, rows1) = SpatialReference.ReadAll(wide.Snapshot);
        Assert.Equal(rows0, rows1);
        Assert.Equal(x0, x1);
        Assert.Equal(y0, y1);
    }

    private static void AssertFinished(SpatialBuilt built)
    {
        Assert.Equal(SpatialStage.Full, built.Snapshot.Stage);
        Assert.False(built.Snapshot.IsProvisional);
        Assert.True(built.Index.IsComplete);
        Assert.Null(built.Index.Fault);
    }

    private static SpatialCell[] Check<TX, TY>(SpatialBuilt built, TX[] xs, TY[] ys)
        where TX : unmanaged, System.Numerics.INumber<TX>
        where TY : unmanaged, System.Numerics.INumber<TY> =>
        SpatialReference.Check(built.Snapshot, SpatialAxisData.Of(xs), SpatialAxisData.Of(ys), new RowRange(0, xs.Length), SpatialReference.Small);

    // The transform of one value, as the scale computes it.
    private static Func<double, double> Forward(PowTransform scale) =>
        value =>
        {
            Span<double> one = [value];
            scale.Forward(one, one);
            return one[0];
        };
}
