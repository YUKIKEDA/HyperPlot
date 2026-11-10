using System.Runtime.InteropServices;

namespace HyperPlot.Tests;

/// <summary>
/// The counting passes, the cap on the rows, and the transient memory of the table builder of a spatial tile index,
/// driven by hand over codes whose table is known (design §3.4 "構築の段", "表の上限", "メモリ"; row C3a).
/// </summary>
[Collection("Large data")]
public sealed class SpatialTableBuilderTests
{
    private const int Level9Cells = 262_144;

    private static SpatialLayout Plane => SpatialLayout.Plane;

    private static SpatialIndexTuning Small => SpatialReference.Small;

    /// <summary>Evenly spread points end the counting in 2 passes, with the table and the transient blocks of the design's example of 1e9 points.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 1,310,720 seeded codes in a shuffled order: 5 in each of the 262,144 cells of level 9, so 20 in each cell of
    ///   level 8. Leaves of 16 points. The cells have the shape of the design's 1e9 evenly spread points with leaves of
    ///   4,096 points: a cell of level 8 is over the capacity, and a cell of level 9 is within it.
    ///
    /// Steps:
    /// - Run the stages of a build by hand, the units of the 8 groups in a seeded order, and check the table and the copy.
    ///
    /// Expected:
    /// - The table and the copy agree with the brute force.
    /// - The counting takes 2 passes. The table has 87,381 rows after the first one, the cells of the levels 0 to 8, and
    ///   349,525 after the second: 11,184,800 bytes.
    /// - The counters of the second pass are 4 for each of the 65,536 cells of level 8 (k = 1), with 16 bytes of extents
    ///   a cell: 16,777,216 bytes for the 8 groups.
    /// - Every leaf is a cell of level 9 with 5 points, and the group counts of the leaves take (262,144 + 1) × 64 =
    ///   16,777,280 bytes.
    /// </remarks>
    [Fact]
    public void Run_FivePointsInEveryCellOfLevel9_EndsInTwoPasses()
    {
        var codes = Lattice(perCell: 5, seed: 61);
        using var run = SpatialTableRun.Of(Plane, codes, Small, shuffle: 3);

        var leaves = run.Check();
        int[] rows = [87_381, 349_525];
        Assert.Equal(2, run.Builder.PassCount);
        Assert.Equal(rows, run.RowsAfterPass);
        Assert.Equal(11_184_800, run.Rows.Length * Marshal.SizeOf<SpatialRow>());
        Assert.Equal(8L * ((Level9Cells * 4) + (65_536 * 16)), run.FirstPassBytes);
        Assert.Equal(16_777_216, run.FirstPassBytes);
        Assert.Equal(Level9Cells, leaves.Length);
        for (var i = 0; i < leaves.Length; i++)
        {
            if (leaves[i].Level != 9 || leaves[i].Count != 5 || leaves[i].Code != (ulong)i || leaves[i].Kind != (byte)SpatialCellKind.Leaf)
            {
                Assert.Fail($"Leaf {i} is the cell {leaves[i].Code} of level {leaves[i].Level} with {leaves[i].Count} points.");
            }
        }

        Assert.Equal(16_777_280, run.SlotBytes);
    }

    /// <summary>A pass counts k levels below a cell, where 4^k leaves hold the cell's points: k is from 1 to 4.</summary>
    /// <remarks>
    /// Preconditions:
    /// - <paramref name="count"/> seeded codes in one cell of level 8, the least and the greatest code of the cell among
    ///   them, so the deepest cell that holds them all is the cell itself. Leaves of 16 points. The counts are the ends of
    ///   each k: 17 and 64 (k = 1), 65 and 256 (2), 257 and 1,024 (3), 1,025 and 4,096 (4), and 4,097 and 100,000, where
    ///   k stays 4.
    /// - With <paramref name="wide"/>, the counters are 8 bytes wide, as for 2^32 rows or more.
    ///
    /// Steps:
    /// - Run the stages by hand, read the bytes of the counters that the first count made for the next pass, and check
    ///   the table and the copy.
    ///
    /// Expected:
    /// - The table has 33 rows after the first count: the root and 4 cells at each of the levels 1 to 8.
    /// - The counters and the extents of the next pass take 8 groups × (4^k counters + 16 bytes), a counter being 4 bytes,
    ///   or 8 when wide.
    /// - The table and the copy agree with the brute force.
    /// </remarks>
    [Theory]
    [InlineData(17, 1, false)]
    [InlineData(64, 1, false)]
    [InlineData(65, 2, false)]
    [InlineData(256, 2, false)]
    [InlineData(257, 3, false)]
    [InlineData(1_024, 3, false)]
    [InlineData(1_025, 4, false)]
    [InlineData(4_096, 4, false)]
    [InlineData(4_097, 4, false)]
    [InlineData(100_000, 4, false)]
    [InlineData(64, 1, true)]
    [InlineData(65, 2, true)]
    [InlineData(100_000, 4, true)]
    public void Run_PointsOfOneCell_CountsTheLevelsThatTheirNumberAsksFor(int count, int k, bool wide)
    {
        var random = new Random(count);
        var cell = 0xB5C3UL << 48;
        var codes = new ulong[count];
        codes[0] = cell;
        codes[1] = cell | ((1UL << 48) - 1);
        for (var i = 2; i < count; i++)
        {
            codes[i] = cell | Bits(random, 48);
        }

        random.Shuffle(codes);
        var tuning = wide ? Small with { WideRows = 1 } : Small;
        using var run = SpatialTableRun.Of(Plane, codes, tuning);

        Assert.Equal(33, run.RowsAfterPass[0]);
        Assert.Equal(8L * (((1L << (2 * k)) * (wide ? 8 : 4)) + 16), run.FirstPassBytes);
        run.Check();
    }

    /// <summary>A cell one level above the deepest one is counted 1 level down, and its grid cells become coincident leaves.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 400 codes in a shuffled order: 100 at each of the 4 grid cells of one cell of level 31. Leaves of 16 points, so
    ///   the number of the points asks for k = 3, and only 1 level is left.
    ///
    /// Steps:
    /// - Run the stages by hand and check the table and the copy.
    ///
    /// Expected:
    /// - The counters of the pass after the first count are 4: 8 groups × (4 × 4 + 16) = 256 bytes.
    /// - The counting takes 2 passes, and the table has 129 rows: the root and 4 cells at each of the levels 1 to 32.
    /// - The leaves with points are 4 coincident leaves of 100 points at level 32.
    /// </remarks>
    [Fact]
    public void Run_PointsOfFourGridCellsOfOneCell_CountsOneLevel()
    {
        var random = new Random(31);
        var cell = Bits(random, 64) & ~3UL;
        var codes = new ulong[400];
        for (var i = 0; i < codes.Length; i++)
        {
            codes[i] = cell | (uint)(i % 4);
        }

        random.Shuffle(codes);
        using var run = SpatialTableRun.Of(Plane, codes, Small);

        var leaves = run.Check().Where(leaf => leaf.Count > 0).ToArray();
        Assert.Equal(256, run.FirstPassBytes);
        Assert.Equal(2, run.Builder.PassCount);
        Assert.Equal(129, run.Rows.Length);
        Assert.Equal(4, leaves.Length);
        Assert.All(
            leaves,
            leaf =>
            {
                Assert.Equal(32, leaf.Level);
                Assert.Equal(100, leaf.Count);
                Assert.Equal((byte)SpatialCellKind.CoincidentLeaf, leaf.Kind);
            }
        );
    }

    /// <summary>The counting takes 25 passes, the most that the design allows, when one point parts from a pile at every level.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 41 codes in a shuffled order: 17 at one grid cell, and for each of the levels 9 to 32 one code that shares the
    ///   pile's cell of the level above and lies in another cell of that level. Leaves of 16 points, so every cell that
    ///   holds the pile is over the capacity down to the grid cell, and k is 1 in every pass.
    ///
    /// Steps:
    /// - Run the stages by hand and check the table and the copy.
    ///
    /// Expected:
    /// - The counting takes 1 + 24 = 25 passes: the first count, and one pass for each of the levels 9 to 32.
    /// - The table has 33 rows after the first count and 4 more after each later pass, 129 in the end.
    /// - The pile is a coincident leaf of 17 points at level 32, and the 24 other points are leaves of 1 point, one at
    ///   each of the levels 9 to 32.
    /// </remarks>
    [Fact]
    public void Run_OnePointPartingFromAPileAtEveryLevel_TakesTwentyFivePasses()
    {
        var random = new Random(25);
        var pile = Bits(random, 64);
        var codes = new ulong[41];
        Array.Fill(codes, pile);
        for (var level = 9; level <= 32; level++)
        {
            codes[level - 9] = pile ^ (3UL << (64 - (2 * level)));
        }

        random.Shuffle(codes);
        using var run = SpatialTableRun.Of(Plane, codes, Small);

        var leaves = run.Check().Where(leaf => leaf.Count > 0).ToArray();
        Assert.Equal(25, run.Builder.PassCount);
        Assert.Equal(Enumerable.Range(0, 25).Select(pass => 33 + (4 * pass)), run.RowsAfterPass);
        var coincident = Assert.Single(leaves, leaf => leaf.Kind == (byte)SpatialCellKind.CoincidentLeaf);
        Assert.Equal(32, coincident.Level);
        Assert.Equal(17, coincident.Count);
        int[] levels = [.. leaves.Where(leaf => leaf.Kind == (byte)SpatialCellKind.Leaf).Select(leaf => (int)leaf.Level).Order()];
        Assert.Equal(Enumerable.Range(9, 24), levels);
        Assert.Equal(25, leaves.Length);
    }

    /// <summary>Clusters inside clusters down to one grid cell end the counting within 25 passes, with the table of the brute force.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 200,000 seeded codes around one centre: each code keeps the centre's cell of a level drawn evenly from 0 to 32
    ///   and is random below it, so about 6,000 codes are the centre's own grid cell. Leaves of 16 points.
    ///
    /// Steps:
    /// - Run the stages by hand, the units of the 8 groups in a seeded order, and check the table and the copy.
    ///
    /// Expected:
    /// - The table and the copy agree with the brute force.
    /// - The counting takes more than 2 and at most 25 passes.
    /// - A coincident leaf of more than 5,000 points is at the centre.
    /// </remarks>
    [Fact]
    public void Run_NestedClusters_EndsWithinTwentyFivePasses()
    {
        var codes = Nested(Plane, 200_000, seed: 8, out var centre);
        using var run = SpatialTableRun.Of(Plane, codes, Small, shuffle: 5);

        var leaves = run.Check();
        Assert.InRange(run.Builder.PassCount, 3, 25);
        Assert.Contains(
            leaves,
            leaf => leaf.Kind == (byte)SpatialCellKind.CoincidentLeaf && leaf.Count > 5_000 && leaf.Code == Plane.CellOf(centre, leaf.Level)
        );
    }

    /// <summary>Two piles that part deep below the first level are found in 3 passes: the pass after the first count starts where they part.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 2,000 codes in a shuffled order: 1,000 at each of two grid cells that share their cell of level 20 and lie in
    ///   two cells of level 21. Leaves of 16 points.
    ///
    /// Steps:
    /// - Run the stages by hand and check the table and the copy.
    ///
    /// Expected:
    /// - The counting takes 3 passes. The pass after the first count counts the levels 21 to 24 below the cell of
    ///   level 20 (k = 4), and the next pass sees that each cell of level 24 holds one code.
    /// - The table has 33 rows after the first count and 109 after each later pass: 4 cells at each of the levels 9
    ///   to 21, and 8 at each of the levels 22 to 24.
    /// - The leaves with points are 2 coincident leaves of 1,000 points at level 24.
    /// </remarks>
    [Fact]
    public void Run_TwoPilesPartingAtLevel21_StartsFromTheCellThatHoldsBoth()
    {
        var random = new Random(21);
        var pile = Bits(random, 64);
        var codes = new ulong[2_000];
        for (var i = 0; i < codes.Length; i++)
        {
            codes[i] = i % 2 == 0 ? pile : pile ^ (1UL << (64 - (2 * 21)));
        }

        random.Shuffle(codes);
        using var run = SpatialTableRun.Of(Plane, codes, Small);

        var leaves = run.Check().Where(leaf => leaf.Count > 0).ToArray();
        int[] rows = [33, 109, 109];
        Assert.Equal(rows, run.RowsAfterPass);
        Assert.Equal(8L * ((256 * 4) + 16), run.FirstPassBytes);
        Assert.Equal(2, leaves.Length);
        Assert.All(
            leaves,
            leaf =>
            {
                Assert.Equal(24, leaf.Level);
                Assert.Equal(1_000, leaf.Count);
                Assert.Equal((byte)SpatialCellKind.CoincidentLeaf, leaf.Kind);
            }
        );
    }

    /// <summary>Points at one grid cell only are one coincident leaf at the root, after the first count alone.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 5,000 codes of one value. Leaves of 16 points.
    ///
    /// Steps:
    /// - Run the stages by hand and check the table and the copy.
    ///
    /// Expected:
    /// - The counting takes 1 pass, and the table is 1 row: a coincident leaf of 5,000 points at level 0.
    /// - The copy holds the points in the order of their rows.
    /// </remarks>
    [Fact]
    public void Run_PointsAtOneGridCell_MakesTheRootACoincidentLeaf()
    {
        var codes = new ulong[5_000];
        Array.Fill(codes, 0x0123_4567_89AB_CDEFUL);
        using var run = SpatialTableRun.Of(Plane, codes, Small);

        var leaf = Assert.Single(run.Check());
        Assert.Equal(1, run.Builder.PassCount);
        Assert.Single(run.Rows);
        Assert.Equal((byte)SpatialCellKind.CoincidentLeaf, leaf.Kind);
        Assert.Equal(0, leaf.Level);
        Assert.Equal(5_000, leaf.Count);
        Assert.Equal(Enumerable.Range(0, 5_000).Select(row => (uint)row), run.Positions());
    }

    /// <summary>A cap of 125 rows splits the cells of the shallow levels first and, in the level where the room ends, in the order of the codes.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 100,000 seeded codes, spread evenly, so every cell of the levels 0 to 4 is over the capacity of 16 points.
    /// - At most 125 rows: the root, the 4 + 16 + 64 cells of the levels 1 to 3, and room for the children of 10 cells
    ///   of level 3.
    ///
    /// Steps:
    /// - Run the stages by hand and check the table and the copy.
    ///
    /// Expected:
    /// - The table has 125 rows and is capped, and the counting ends with the first count.
    /// - The rows 0 to 20, the cells of the levels 0 to 2, are split. The rows 21 to 84 are the cells of level 3 in the
    ///   order of their codes: the cells 0 to 9 are split, and the cells 10 to 63 are capped leaves.
    /// - The rows 85 to 124 are the cells 0 to 39 of level 4, all capped leaves.
    /// - The table and the copy agree with the brute force.
    /// </remarks>
    [Fact]
    public void Run_CapOf125Rows_SplitsTheShallowCellsFirstInTheOrderOfTheCodes()
    {
        var codes = Uniform(100_000, seed: 125);
        using var run = SpatialTableRun.Of(Plane, codes, Small, maxTableRows: 125);

        run.Check(capped: true);
        Assert.Equal(125, run.Rows.Length);
        Assert.Equal(1, run.Builder.PassCount);
        for (var i = 0; i < run.Rows.Length; i++)
        {
            var row = run.Rows[i];
            var level =
                i < 1 ? 0
                : i < 5 ? 1
                : i < 21 ? 2
                : i < 85 ? 3
                : 4;
            var first =
                i < 1 ? 0
                : i < 5 ? 1
                : i < 21 ? 5
                : i < 85 ? 21
                : 85;
            var kind = i < 31 ? SpatialCellKind.Inner : SpatialCellKind.CappedLeaf;
            if (row.Level != level || row.Code != (ulong)(i - first) || row.Kind != (byte)kind)
            {
                Assert.Fail($"Row {i} is the cell {row.Code} of level {row.Level} with the kind {row.Kind}.");
            }
        }
    }

    /// <summary>A capped table is the rows that fit of the table without a cap, in the order in which that table made them.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 60,000 seeded codes of clusters inside clusters, whose table without a cap takes several passes. Leaves of 16 points.
    /// - For the first pass, a middle pass, and the last pass that adds rows, four caps: the rows before the pass, 3 more,
    ///   the middle of the pass, and 1 less than the rows after the pass.
    ///
    /// Steps:
    /// - Run the stages without a cap and note the rows after each pass.
    /// - Run them again with each cap, and check the table and the copy.
    ///
    /// Expected:
    /// - Without a cap, the rows that a pass adds are in the order of their levels and, in a level, of their codes.
    /// - With a cap, the table is capped, and its rows are the rows before the pass and as many groups of 4 as the cap
    ///   has room for. The counting ends with that pass.
    /// - Each row is the cell of the same row of the table without a cap. A cell that is a leaf there is the same leaf; a
    ///   cell that is split there is split when its children are among the rows, and a capped leaf otherwise.
    /// - Every capped table and its copy agree with the brute force.
    /// </remarks>
    [Fact]
    public void Run_CapInsideAPass_KeepsTheRowsThatTheUncappedTableMadeFirst()
    {
        var codes = Nested(Plane, 60_000, seed: 44, out _);
        using var whole = SpatialTableRun.Of(Plane, codes, Small);
        whole.Check();
        var after = whole.RowsAfterPass;
        for (var pass = 0; pass < after.Count; pass++)
        {
            for (var i = (pass == 0 ? 1 : after[pass - 1]) + 1; i < after[pass]; i++)
            {
                var (above, row) = (whole.Rows[i - 1], whole.Rows[i]);
                if (row.Level < above.Level || (row.Level == above.Level && row.Code <= above.Code))
                {
                    Assert.Fail($"Row {i} of pass {pass} is out of the order of the levels and the codes.");
                }
            }
        }

        var last = after.Count - 1;
        while (after[last] == after[last - 1])
        {
            last--;
        }

        Assert.True(last >= 3, $"The table took {last + 1} passes that add rows.");
        foreach (var pass in (int[])[0, last / 2, last])
        {
            var before = pass == 0 ? 1 : after[pass - 1];
            foreach (var cap in (int[])[before, before + 3, (before + after[pass]) / 2, after[pass] - 1])
            {
                using var run = SpatialTableRun.Of(Plane, codes, Small, maxTableRows: cap);

                run.Check(capped: true);
                var expected = before + (4 * ((cap - before) / 4));
                Assert.Equal(expected, run.Rows.Length);
                Assert.Equal(pass + 1, run.Builder.PassCount);
                for (var i = 0; i < expected; i++)
                {
                    var (row, full) = (run.Rows[i], whole.Rows[i]);
                    var kind =
                        full.Kind != (byte)SpatialCellKind.Inner ? full.Kind
                        : full.FirstChild < expected ? (byte)SpatialCellKind.Inner
                        : (byte)SpatialCellKind.CappedLeaf;
                    var known = full.Kind is (byte)SpatialCellKind.Inner or (byte)SpatialCellKind.Leaf;
                    if (row.Code != full.Code || row.Level != full.Level || (known && row.Kind != kind))
                    {
                        Assert.Fail($"Row {i} under the cap {cap} is the cell {row.Code} of level {row.Level} with the kind {row.Kind}.");
                    }
                }
            }
        }
    }

    /// <summary>The counters of 8 bytes build the table and the copy of the counters of 4 bytes.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 60,000 seeded codes of clusters inside clusters. Leaves of 16 points.
    ///
    /// Steps:
    /// - Run the stages by hand with the counters of 4 bytes, and again with the counters of 8 bytes.
    ///
    /// Expected:
    /// - The second table and its copy agree with the brute force.
    /// - The two tables are the same bytes, and the two copies hold the rows in the same order.
    /// - The group counts of the leaves take twice the bytes.
    /// </remarks>
    [Fact]
    public void Run_WideCounters_BuildTheSameTableAndCopy()
    {
        var codes = Nested(Plane, 60_000, seed: 64, out _);
        using var narrow = SpatialTableRun.Of(Plane, codes, Small, shuffle: 1);
        using var wide = SpatialTableRun.Of(Plane, codes, Small with { WideRows = 1 }, shuffle: 2);

        wide.Check();
        Assert.True(MemoryMarshal.AsBytes<SpatialRow>(narrow.Rows).SequenceEqual(MemoryMarshal.AsBytes<SpatialRow>(wide.Rows)), "The tables differ.");
        Assert.Equal(narrow.Positions(), wide.Positions());
        Assert.Equal(2 * narrow.SlotBytes, wide.SlotBytes);
    }

    /// <summary>The transient blocks of a build stay within the sizes of the design's memory table, and each is freed at the stage that the table names.</summary>
    /// <remarks>
    /// Preconditions:
    /// - Leaves of 16 points, and the codes of <paramref name="shape"/>: "lattice" is 5 codes in each cell of level 9
    ///   (1,310,720 codes), "nested" is 200,000 codes of clusters inside clusters, "uniform" is 100,000 codes spread
    ///   evenly, "pile" is 41 codes that take 25 passes, and "few" is 10 codes, fewer than one leaf holds.
    ///
    /// Steps:
    /// - Run the stages by hand, reading the bytes of the blocks after the single job of each pass and at the table stage.
    ///
    /// Expected:
    /// - The first counters take 65,536 × 24 bytes × 8 groups = 12,582,912 bytes.
    /// - The counters and the extents of every pass take less than 256 × n / 16 bytes.
    /// - The records of the open cells and the lists of rows take at most 248 × n / 16 + 56 bytes.
    /// - The group counts of the leaves take (the leaves with points + 1) × 64 bytes, which is at most twice the table
    ///   and 64 bytes.
    /// - After the table stage the first counters, the counters of the passes, and the records are freed; after the
    ///   writing, the group counts.
    /// </remarks>
    [Theory]
    [InlineData("lattice")]
    [InlineData("nested")]
    [InlineData("uniform")]
    [InlineData("pile")]
    [InlineData("few")]
    public void Run_AnyPoints_KeepsTheTransientBlocksWithinTheDesignsSizes(string shape)
    {
        var codes = shape switch
        {
            "lattice" => Lattice(perCell: 5, seed: 62),
            "nested" => Nested(Plane, 200_000, seed: 9, out _),
            "uniform" => Uniform(100_000, seed: 10),
            "pile" => Pile(seed: 11),
            _ => Uniform(10, seed: 12),
        };
        using var run = SpatialTableRun.Of(Plane, codes, Small);

        var leaves = run.Check();
        long n = codes.Length;
        Assert.Equal(12_582_912, run.FirstBytes);
        Assert.True(run.MaxPassBytes < 256 * n / 16, $"A pass took {run.MaxPassBytes} bytes of counters for {n} points.");
        Assert.True(run.MaxOpenBytes <= (248 * n / 16) + 56, $"The open cells took {run.MaxOpenBytes} bytes for {n} points.");
        Assert.Equal((leaves.Count(leaf => leaf.Count > 0) + 1) * 64L, run.SlotBytes);
        Assert.True(run.SlotBytes <= (2L * run.Rows.Length * Marshal.SizeOf<SpatialRow>()) + 64);
        Assert.Equal(0, run.Builder.FirstBytes);
        Assert.Equal(0, run.Builder.PassBytes);
        Assert.Equal(0, run.Builder.OpenBytes);
        Assert.Equal(0, run.Builder.SlotBytes);
    }

    /// <summary>The scratch arrays of a build are 3 arrays of one unit a group at most: 12,582,912 bytes with the default unit.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A build over the rows [0, 1e9) with units of 65,536 rows, and one over the rows [5, 1,005), which is 1 unit.
    ///
    /// Steps:
    /// - Lay the units and allocate the scratch arrays, with and without the arrays of the transformed values.
    ///
    /// Expected:
    /// - Over 1e9 rows, each of the 8 groups has the codes and the transformed x and y of one unit: 65,536 × 8 bytes × 3 × 8
    ///   = 12,582,912 bytes. Without a transform, the codes alone: 4,194,304 bytes.
    /// - Over 1,000 rows, only the last group has a unit, and its arrays are 1,000 long: 24,000 bytes.
    /// </remarks>
    [Fact]
    public void AllocateScratch_UnitsOfTheDefaultSize_TakesThreeArraysOfOneUnitAGroup()
    {
        var large = new SpatialBuild(0);
        large.Lay(0, 1_000_000_000, 65_536);
        large.AllocateScratch(x: true, y: true);
        var plain = new SpatialBuild(1);
        plain.Lay(0, 1_000_000_000, 65_536);
        plain.AllocateScratch(x: false, y: false);
        var small = new SpatialBuild(2);
        small.Lay(5, 1_005, 65_536);
        small.AllocateScratch(x: true, y: true);

        Assert.Equal(12_582_912, ScratchBytes(large));
        Assert.Equal(4_194_304, ScratchBytes(plain));
        Assert.Equal(24_000, ScratchBytes(small));
        Assert.Equal(1_000, small.Codes(7).Length);
        for (var group = 0; group < 7; group++)
        {
            Assert.Empty(small.Codes(group));
        }
    }

    /// <summary>The builder splits three axes into 8 children a cell from level 5 down to level 21, with the table of the brute force.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 120,000 seeded points of double x, y, and z over [−50, 50] an axis: clusters inside clusters around one centre,
    ///   half as wide at each step down to 2^-21 of the range, and 500 points at the centre itself. Every 53rd row has
    ///   NaN on one axis.
    /// - Axes of 21 bits, the layout of three axes, and leaves of 16 points.
    ///
    /// Steps:
    /// - Encode the points, and compute the code of each finite row by the formula of the design.
    /// - Run the stages by hand over the codes with the three values of each point, and check the table and the copy.
    ///
    /// Expected:
    /// - The codes are those of the formula, in the order of the rows, and the rows with NaN are left out.
    /// - The first counters take 32,768 × 24 bytes × 8 groups.
    /// - The table and the copy agree with the brute force over the codes: 8 children a split cell, each point in its
    ///   cell with its three values.
    /// - The counting takes more than 2 and at most 1 + 16 = 17 passes; a leaf is at level 21, and the points at the
    ///   centre are a coincident leaf.
    /// </remarks>
    [Fact]
    public void Run_ThreeAxes_AgreesWithBruteForce()
    {
        const int Length = 120_000;
        var random = new Random(333);
        var values = new double[3][];
        for (var axis = 0; axis < 3; axis++)
        {
            values[axis] = new double[Length];
        }

        double[] centre = [12.5, -30.25, 4.75];
        for (var row = 0; row < Length; row++)
        {
            var width = row < 500 ? 0 : Math.ScaleB(60, -random.Next(22));
            for (var axis = 0; axis < 3; axis++)
            {
                values[axis][row] = centre[axis] + ((random.NextDouble() - 0.5) * width);
            }

            if (row % 53 == 0)
            {
                values[row % 3][row] = double.NaN;
            }
        }

        var space = SpatialLayout.Space;
        var range = new ScaleRange(0, -50, 50);
        var grid = new SpatialAxis(range, space.BitsPerAxis);
        var codes = new ulong[Length];
        var count = SpatialCode.Encode<double, double, double>(values[0], values[1], values[2], grid, grid, grid, codes);

        var finite = Enumerable.Range(0, Length).Where(row => row % 53 != 0).ToArray();
        Assert.Equal(finite.Length, count);
        for (var i = 0; i < count; i++)
        {
            var row = finite[i];
            var expected = SpatialReference.Code(
                SpatialReference.Cell(values[0][row], -50, 50, 21),
                SpatialReference.Cell(values[1][row], -50, 50, 21),
                SpatialReference.Cell(values[2][row], -50, 50, 21)
            );
            if (codes[i] != expected)
            {
                Assert.Fail($"The code of row {row} is {codes[i]:X}, and the formula gives {expected:X}.");
            }
        }

        double[][] stored = [.. values.Select(axis => finite.Select(row => axis[row]).ToArray())];
        using var run = SpatialTableRun.Of(space, codes[..count], Small, values: stored, shuffle: 7);

        var leaves = run.Check();
        Assert.Equal(32_768L * 24 * 8, run.FirstBytes);
        Assert.InRange(run.Builder.PassCount, 3, 17);
        Assert.Contains(leaves, leaf => leaf.Level == 21 && leaf.Kind == (byte)SpatialCellKind.Leaf && leaf.Count > 0);
        Assert.Contains(leaves, leaf => leaf.Kind == (byte)SpatialCellKind.CoincidentLeaf && leaf.Count >= 490);
    }

    private static long ScratchBytes(SpatialBuild build)
    {
        long bytes = 0;
        for (var group = 0; group < SpatialIndexTuning.GroupCount; group++)
        {
            bytes += sizeof(ulong) * (long)build.Codes(group).Length;
            if (build.Codes(group).Length > 0)
            {
                bytes += sizeof(double) * (long)((build.Transformed(group, 0)?.Length ?? 0) + (build.Transformed(group, 1)?.Length ?? 0));
            }
        }

        return bytes;
    }

    // The lowest `count` bits at random.
    private static ulong Bits(Random random, int count) =>
        count == 0 ? 0 : unchecked((ulong)random.NextInt64(long.MinValue, long.MaxValue)) >> (64 - count);

    private static ulong[] Uniform(int count, int seed)
    {
        var random = new Random(seed);
        var codes = new ulong[count];
        for (var i = 0; i < count; i++)
        {
            codes[i] = Bits(random, 64);
        }

        return codes;
    }

    // The same number of codes in every cell of level 9, at random places of the cell, in a shuffled order.
    private static ulong[] Lattice(int perCell, int seed)
    {
        var random = new Random(seed);
        var codes = new ulong[Level9Cells * perCell];
        for (var i = 0; i < codes.Length; i++)
        {
            codes[i] = ((ulong)(i / perCell) << 46) | Bits(random, 46);
        }

        random.Shuffle(codes);
        return codes;
    }

    // Each code keeps the centre's cell of a level drawn evenly from 0 to the deepest level, and is random below it.
    private static ulong[] Nested(SpatialLayout layout, int count, int seed, out ulong centre)
    {
        var random = new Random(seed);
        centre = Bits(random, layout.TotalBits);
        var codes = new ulong[count];
        for (var i = 0; i < count; i++)
        {
            var low = layout.BitsBelow(random.Next(layout.BitsPerAxis + 1));
            codes[i] = low == 64 ? Bits(random, 64) : ((centre >> low) << low) | Bits(random, low);
        }

        return codes;
    }

    // 17 codes at one grid cell, and one code that parts from them at each of the levels 9 to 32.
    private static ulong[] Pile(int seed)
    {
        var random = new Random(seed);
        var pile = Bits(random, 64);
        var codes = new ulong[41];
        Array.Fill(codes, pile);
        for (var level = 9; level <= 32; level++)
        {
            codes[level - 9] = pile ^ (3UL << (64 - (2 * level)));
        }

        random.Shuffle(codes);
        return codes;
    }
}
