using System.Numerics;

namespace HyperPlot.Tests;

/// <summary>
/// The answers of a spatial tile index computed by plain loops over every row, with no table (design §10: brute force),
/// and the check of a full snapshot against them.
/// </summary>
internal static class SpatialReference
{
    /// <summary>The constants of the tests: leaves of 16 points and units of 256 rows, and a table that is never capped.</summary>
    public static SpatialIndexTuning Small => new(LeafCapacity: 16, RowsPerUnit: 256, TableShare: 1e6, WideRows: 1L << 32);

    /// <summary>Gets the rectangle that holds every value.</summary>
    public static SpatialRect Everything => new(double.NegativeInfinity, double.PositiveInfinity, double.NegativeInfinity, double.PositiveInfinity);

    /// <summary>Starts a build with the constants of the tests and waits for its end.</summary>
    public static SpatialIndex Build<TX, TY>(
        PlotWorkerPool pool,
        Column<TX> x,
        Column<TY> y,
        SpatialIndexInput input,
        SpatialIndexTuning? tuning = null,
        SpatialCopyAllocator? allocator = null,
        SpatialProbe? probe = null
    )
        where TX : unmanaged, INumber<TX>
        where TY : unmanaged, INumber<TY>
    {
        var index = Start(pool, x, y, input, tuning, allocator, probe);
        Eventually.Completes(index.Completion);
        return index;
    }

    /// <summary>Starts a build with the constants of the tests, and with a probe that follows its jobs when one is passed.</summary>
    public static SpatialIndex Start<TX, TY>(
        PlotWorkerPool pool,
        Column<TX> x,
        Column<TY> y,
        SpatialIndexInput input,
        SpatialIndexTuning? tuning = null,
        SpatialCopyAllocator? allocator = null,
        SpatialProbe? probe = null
    )
        where TX : unmanaged, INumber<TX>
        where TY : unmanaged, INumber<TY>
    {
        var constants = probe?.Tuning(tuning) ?? tuning ?? Small;
        var index = pool.BuildSpatialIndex(x, y, input, constants, allocator ?? SpatialCopyAllocator.Default);
        probe?.Attach(index);
        return index;
    }

    /// <summary>The grid cell of a difference on an axis of 2^bits cells, by the formula of the design.</summary>
    public static uint Cell(double v, double lo, double hi, int bits = 32)
    {
        if (hi == lo)
        {
            return 0;
        }

        var width = hi - lo;
        if (double.IsInfinity(width))
        {
            v /= 2;
            lo /= 2;
            width = (hi / 2) - lo;
        }

        var cells = Math.Pow(2, bits);
        var g = Math.Floor((v - lo) / width * cells);
        return g >= cells - 1 ? (uint)(cells - 1)
            : g > 0 ? (uint)g
            : 0;
    }

    /// <summary>The code of two grid cells, a bit at a time.</summary>
    public static ulong Code(uint gx, uint gy)
    {
        ulong code = 0;
        for (var bit = 0; bit < 32; bit++)
        {
            code |= (ulong)((gx >> bit) & 1) << (2 * bit);
            code |= (ulong)((gy >> bit) & 1) << ((2 * bit) + 1);
        }

        return code;
    }

    /// <summary>The code of three grid cells of 21 bits, a bit at a time.</summary>
    public static ulong Code(uint gx, uint gy, uint gz)
    {
        ulong code = 0;
        for (var bit = 0; bit < 21; bit++)
        {
            code |= (ulong)((gx >> bit) & 1) << (3 * bit);
            code |= (ulong)((gy >> bit) & 1) << ((3 * bit) + 1);
            code |= (ulong)((gz >> bit) & 1) << ((3 * bit) + 2);
        }

        return code;
    }

    /// <summary>The upper 2 × level bits of a code of two axes.</summary>
    public static ulong CellAt(ulong code, int level) => level == 0 ? 0 : code >> (64 - (2 * level));

    /// <summary>The first code of a cell of a level.</summary>
    public static ulong FirstCode(SpatialCell cell) => cell.Level == 0 ? 0 : cell.Code << (64 - (2 * cell.Level));

    /// <summary>Reads every point of a full snapshot.</summary>
    public static (double[] X, double[] Y, long[] Rows) ReadAll(SpatialSnapshot snapshot)
    {
        var count = checked((int)snapshot.PointCount);
        var x = new double[count];
        var y = new double[count];
        var rows = new long[count];
        snapshot.ReadPoints(0, x, y, rows);
        return (x, y, rows);
    }

    /// <summary>The cells that a snapshot returns for a rectangle and a level.</summary>
    public static SpatialCell[] Cells(SpatialSnapshot snapshot, in SpatialRect rect, int maxLevel)
    {
        var count = snapshot.GetCells(rect, maxLevel, default);
        var cells = new SpatialCell[count];
        Assert.Equal(count, snapshot.GetCells(rect, maxLevel, cells));
        return cells;
    }

    /// <summary>
    /// Checks a full snapshot against the columns: the counts, the ranges, the copy, and the table at every level.
    /// </summary>
    /// <returns>The leaves of the table in the order of their codes.</returns>
    public static SpatialCell[] Check(
        SpatialSnapshot snapshot,
        SpatialAxisData x,
        SpatialAxisData y,
        RowRange rows,
        SpatialIndexTuning tuning,
        ScaleRange? xRange = null,
        ScaleRange? yRange = null,
        bool capped = false
    )
    {
        Assert.Equal(SpatialStage.Full, snapshot.Stage);
        var expectedX = xRange ?? x.Range(rows);
        var expectedY = yRange ?? y.Range(rows);
        Assert.Equal(expectedX, snapshot.XRange);
        Assert.Equal(expectedY, snapshot.YRange);

        // The points: the rows whose stored values are both finite.
        var points = new List<long>();
        for (var row = rows.Start; row < rows.End; row++)
        {
            if (x.IsFinite(row) && y.IsFinite(row))
            {
                points.Add(row);
            }
        }

        var finite = points.ToArray();
        Assert.Equal(finite.Length, snapshot.PointCount);
        Assert.Equal(rows.End - rows.Start - finite.Length, snapshot.ExcludedCount);

        // The copy holds each point once, with the values of its row.
        var (xs, ys, copied) = ReadAll(snapshot);
        var sorted = (long[])copied.Clone();
        Array.Sort(sorted);
        Assert.True(finite.AsSpan().SequenceEqual(sorted), "The copy does not hold every finite row once.");
        var codes = new ulong[copied.Length];
        for (var i = 0; i < copied.Length; i++)
        {
            var row = copied[i];
            var dx = x.Difference(row, expectedX.Origin);
            var dy = y.Difference(row, expectedY.Origin);
            if (dx != xs[i] || dy != ys[i])
            {
                Assert.Fail($"The point at {i} of the copy is ({xs[i]:R}, {ys[i]:R}), and row {row} holds ({dx:R}, {dy:R}).");
            }

            codes[i] = Code(Cell(dx, expectedX.Lower, expectedX.Upper), Cell(dy, expectedY.Lower, expectedY.Upper));
        }

        // At every level, the cells are in the order of their codes, their ranges follow one another from 0 to the
        // number of points, and each range holds points of its cell only. So a cell's range is its children's joined.
        SpatialCell[] leaves = [];
        for (var level = 0; level <= 32; level++)
        {
            var cells = Cells(snapshot, Everything, level);
            long next = 0;
            for (var c = 0; c < cells.Length; c++)
            {
                var cell = cells[c];
                if (cell.Level > level || cell.Count <= 0 || cell.First != next || (c > 0 && FirstCode(cells[c - 1]) >= FirstCode(cell)))
                {
                    Assert.Fail($"Cell {c} of level {level} is {cell}: its points do not begin at {next}, or it is out of the order of the codes.");
                }

                next += cell.Count;

                for (var i = cell.First; i < cell.First + cell.Count; i++)
                {
                    if (CellAt(codes[i], cell.Level) != cell.Code)
                    {
                        Assert.Fail($"The point at {i} is not in its cell {cell.Code} of level {cell.Level}.");
                    }
                }

                if (cell.Kind == SpatialCellKind.Inner)
                {
                    // Only the cells at the level asked for are returned unopened, and only a cell over the capacity whose points differ is split.
                    Assert.Equal(level, cell.Level);
                    Assert.True(cell.Count > tuning.LeafCapacity, "A cell within the capacity of a leaf was split.");
                }
            }

            Assert.Equal(copied.Length, next);
            leaves = cells;
        }

        foreach (var leaf in leaves)
        {
            var first = (int)leaf.First;
            var last = first + (int)leaf.Count - 1;
            switch (leaf.Kind)
            {
                case SpatialCellKind.Leaf:
                    Assert.True(leaf.Count <= tuning.LeafCapacity, $"A leaf holds {leaf.Count} points.");
                    break;
                case SpatialCellKind.CoincidentLeaf:
                    Assert.True(leaf.Count > tuning.LeafCapacity, "A leaf within the capacity is marked coincident.");
                    Assert.False(codes.AsSpan(first, (int)leaf.Count).ContainsAnyExcept(codes[first]), "A coincident leaf holds two grid cells.");
                    break;
                case SpatialCellKind.CappedLeaf:
                    Assert.True(capped, "A leaf is capped though the table has room.");
                    Assert.True(leaf.Count > tuning.LeafCapacity, "A leaf within the capacity is marked capped.");
                    break;
                default:
                    Assert.Fail($"A leaf of kind {leaf.Kind} came back at level 32.");
                    break;
            }

            // Inside a leaf the points are in the order of their rows.
            for (var i = first; i < last; i++)
            {
                if (copied[i] >= copied[i + 1])
                {
                    Assert.Fail($"The points of a leaf are not in the order of their rows at {i}.");
                }
            }
        }

        return leaves;
    }

    /// <summary>The grid cells of the ends of a rectangle, by the formula of the design: both ends belong to the rectangle.</summary>
    public static (uint X0, uint X1, uint Y0, uint Y1) Grid(SpatialSnapshot snapshot, in SpatialRect rect)
    {
        var (xr, yr) = (snapshot.XRange, snapshot.YRange);
        return (
            Cell(rect.XMin, xr.Lower, xr.Upper),
            Cell(rect.XMax, xr.Lower, xr.Upper),
            Cell(rect.YMin, yr.Lower, yr.Upper),
            Cell(rect.YMax, yr.Lower, yr.Upper)
        );
    }

    /// <summary>The first and the last grid cell of a cell on each axis, from its code a bit at a time.</summary>
    public static (ulong X0, ulong X1, ulong Y0, ulong Y1) Square(SpatialCell cell)
    {
        ulong x = 0;
        ulong y = 0;
        for (var bit = 0; bit < cell.Level; bit++)
        {
            x |= ((cell.Code >> (2 * bit)) & 1) << bit;
            y |= ((cell.Code >> ((2 * bit) + 1)) & 1) << bit;
        }

        var shift = 32 - cell.Level;
        var last = (1UL << shift) - 1;
        return (x << shift, (x << shift) + last, y << shift, (y << shift) + last);
    }

    /// <summary>Whether a cell and the grid cells of a rectangle share a grid cell.</summary>
    public static bool Touches(SpatialCell cell, (uint X0, uint X1, uint Y0, uint Y1) grid)
    {
        var square = Square(cell);
        return square.X1 >= grid.X0 && square.X0 <= grid.X1 && square.Y1 >= grid.Y0 && square.Y0 <= grid.Y1;
    }

    /// <summary>Whether every grid cell of a cell is among the grid cells of a rectangle.</summary>
    public static bool Within(SpatialCell cell, (uint X0, uint X1, uint Y0, uint Y1) grid)
    {
        var square = Square(cell);
        return square.X0 >= grid.X0 && square.X1 <= grid.X1 && square.Y0 >= grid.Y0 && square.Y1 <= grid.Y1;
    }

    /// <summary>Counts, by a loop over every point, the points whose grid cells lie in the grid cells of a rectangle.</summary>
    public static long CountIn(SpatialSnapshot snapshot, in SpatialRect rect)
    {
        var (xs, ys, _) = ReadAll(snapshot);
        return CountIn(snapshot, xs, ys, rect);
    }

    /// <summary>Counts the points, given as differences from the origins, whose grid cells lie in the grid cells of a rectangle.</summary>
    public static long CountIn(SpatialSnapshot snapshot, double[] xs, double[] ys, in SpatialRect rect)
    {
        var (xr, yr) = (snapshot.XRange, snapshot.YRange);
        var (x0, x1, y0, y1) = Grid(snapshot, rect);
        long count = 0;
        for (var i = 0; i < xs.Length; i++)
        {
            var gx = Cell(xs[i], xr.Lower, xr.Upper);
            var gy = Cell(ys[i], yr.Lower, yr.Upper);
            if (gx >= x0 && gx <= x1 && gy >= y0 && gy <= y1)
            {
                count++;
            }
        }

        return count;
    }
}
