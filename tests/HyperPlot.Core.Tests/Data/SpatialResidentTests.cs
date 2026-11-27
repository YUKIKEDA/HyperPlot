using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>The resident cells of a full snapshot and the points read for the GPU (design §5.5.2 "常駐のセルとページ", §10 R3a row).</summary>
[Collection("Large data")]
public sealed class SpatialResidentTests
{
    /// <summary>
    /// The resident cells cover the copy in its order without a gap or an overlap, and each holds at most 32,768 points or
    /// is a leaf of the table.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Full snapshots of 100,000 uniform float points, of 100,000 double points in nested clusters, and of 2,000 points.
    ///
    /// Steps:
    /// - Read ResidentCells of each, and the leaves of the table (GetCells over everything at level 32).
    ///
    /// Expected:
    /// - The first cell begins at 0, each next cell begins where the one before ends, and the last ends at PointCount.
    /// - No cell is empty; a cell of more than 32,768 points is one of the leaves.
    /// - 100,000 points make more than one cell, and each has an extent above 0; 2,000 points make one cell, the whole range.
    /// </remarks>
    [Fact]
    public void ResidentCells_CoverTheCopyInOrder()
    {
        var (ux, uy) = SpatialData.Uniform(100_000, seed: 3);
        var (nx, ny) = SpatialData.Nested(100_000, seed: 4);
        using var uniform = SpatialBuilt.Of(ux, uy);
        using var nested = SpatialBuilt.Of(nx, ny);
        foreach (var snapshot in new[] { uniform.Snapshot, nested.Snapshot })
        {
            var cells = snapshot.ResidentCells.ToArray();
            var leaves = SpatialReference.Cells(snapshot, SpatialReference.Everything, 32).ToDictionary(c => c.First, c => c.Count);
            Assert.True(cells.Length > 1);
            long next = 0;
            foreach (var cell in cells)
            {
                Assert.Equal(next, cell.First);
                Assert.True(cell.Count > 0);
                Assert.True(cell.Count <= SpatialSnapshot.CellCapacity || (leaves.TryGetValue(cell.First, out var count) && count == cell.Count));
                Assert.True(cell.ExtentX > 0 && cell.ExtentY > 0);
                next += cell.Count;
            }

            Assert.Equal(snapshot.PointCount, next);
        }

        var (sx, sy) = SpatialData.Uniform(2_000, seed: 5);
        using var small = SpatialBuilt.Of(sx, sy);
        var one = Assert.Single(small.Snapshot.ResidentCells.ToArray());
        Assert.Equal((0L, 2_000L), (one.First, one.Count));
        Assert.Equal(small.Snapshot.XRange.Lower, one.CornerX);
        Assert.Equal(small.Snapshot.XRange.Upper - small.Snapshot.XRange.Lower, one.ExtentX);
    }

    /// <summary>
    /// More resident cells than the snapshot's limit stop at an upper level (design §5.5.2 "セルの表", row R3b2): the
    /// deepest level at which the cells, every deeper one taken at its ancestor of that level, are within the limit. A
    /// snapshot whose cells are the limit exactly is not stopped, and the cells still cover the copy in its order.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 200,000 double points in nested clusters, whose resident cells lie at several levels: the full snapshot with no
    ///   limit has N cells.
    ///
    /// Steps:
    /// - Build the same points with the limits N, N − 1, N / 3, 4, 1, and 0.
    /// - The reference, apart from the code's walk: each cell of no limit is put at a level ℓ (the root's extent over its
    ///   extent) and an index on each axis (its corner over its extent); its ancestor at a level L ≤ ℓ is the index shifted
    ///   by ℓ − L. For each L, the groups are the cells at or above L alone and the cells below L by their ancestor; the
    ///   level taken is the deepest whose groups are within the limit.
    ///
    /// Expected:
    /// - With N the cells are those of no limit. With each other limit the cells are the reference's groups, as (first,
    ///   count), in the copy's order, no more than the limit or 1: they cover the copy without a gap or an overlap.
    /// - With N − 1 some cell holds more than 32,768 points and is not a leaf of the table; with 0 the one cell is the root.
    /// </remarks>
    [Fact]
    public void ResidentCells_PastTheLimit_StopAtAnUpperLevel()
    {
        var (xs, ys) = SpatialData.Nested(200_000, seed: 7);
        using var free = SpatialBuilt.Of(xs, ys);
        var natural = free.Snapshot.ResidentCells.ToArray();
        var (rootX, rootY, rootExtentX, _) = free.Snapshot.CellBox(0, 0);
        var count = natural.Length;
        Assert.True(count > 12, $"{count} cells: too few for the limits.");
        var levels = natural.Select(c => (int)Math.Round(Math.Log2(rootExtentX / c.ExtentX))).ToArray();
        Assert.True(levels.Distinct().Count() > 2, "The cells do not lie at several levels.");
        var leaves = SpatialReference.Cells(free.Snapshot, SpatialReference.Everything, 32).ToDictionary(c => c.First, c => c.Count);

        foreach (var limit in new[] { count, count - 1, count / 3, 4, 1, 0 })
        {
            using var limited = SpatialBuilt.Of(xs, ys, new SpatialIndexInput { Rows = RowRange.All, CellLimit = limit });
            var cells = limited.Snapshot.ResidentCells.ToArray().Select(c => (c.First, c.Count)).ToArray();
            Assert.Equal(Expected(limit), cells);
            Assert.InRange(cells.Length, 1, Math.Max(1, limit));
            if (limit == count - 1)
            {
                Assert.Contains(cells, c => c.Count > SpatialSnapshot.CellCapacity && !(leaves.TryGetValue(c.First, out var n) && n == c.Count));
            }
        }

        // The groups of the reference at the deepest level within the limit: the cells of no limit at that level or above,
        // and those below by their ancestor there, in the copy's order.
        (long First, long Count)[] Expected(int limit)
        {
            for (var level = 32; level >= 0; level--)
            {
                var groups = Groups(level);
                if (groups.Length <= limit || level == 0)
                {
                    return groups;
                }
            }

            return [];
        }

        (long First, long Count)[] Groups(int level)
        {
            var groups = new List<(long First, long Count)>();
            (int, long, long)? last = null;
            for (var c = 0; c < natural.Length; c++)
            {
                var cell = natural[c];
                var at = levels[c];
                var (ix, iy) = ((long)Math.Round((cell.CornerX - rootX) / cell.ExtentX), (long)Math.Round((cell.CornerY - rootY) / cell.ExtentY));
                var key = at <= level ? (at, ix, iy) : (level, ix >> (at - level), iy >> (at - level));
                if (key == last)
                {
                    groups[^1] = (groups[^1].First, groups[^1].Count + cell.Count);
                }
                else
                {
                    groups.Add((cell.First, cell.Count));
                    last = key;
                }
            }

            return [.. groups];
        }
    }

    /// <summary>
    /// A page staged for the GPU holds every point of its part of the copy once, each block in steps of 104,729 through
    /// the copy's order (design §5.5.2 "常駐のセルとページ", "ブロック"): two points that follow each other in the copy are
    /// not drawn one after the other. The block is part of what names a page.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The full snapshot of 100,000 uniform float points: a whole page of 65,536 points and a page of 34,464.
    /// - A residency cache with no frame budget.
    ///
    /// Steps:
    /// - Request both pages with blocks of 32,768 points, the block of a layer of one part, wait for the workers' reads, and
    ///   request them again; read what was staged for each. Read the same parts of the copy with ReadBlock.
    /// - Request page 0 with blocks of 512 points (and again once read), and with blocks of 32,768 again. Forget the
    ///   snapshot. Request page 0 with a block of 0 points.
    ///
    /// Expected:
    /// - Point k of a block of a staged page is point k × 104,729 modulo the block's count of the same block of the copy:
    ///   the whole page has two blocks of 32,768, and the second page a block of 32,768 and one of the 1,696 points left.
    /// - Point 1 of the whole page is point 6,425 of the copy's page, and point 32,769 is point 39,193.
    /// - The blocks of 512 take a slot and a staged page of their own, in steps within each 512; asking with 32,768 again
    ///   gives the first slot and stages nothing. After the forgetting no page is in use.
    /// - A block of 0 points throws ArgumentOutOfRangeException.
    /// </remarks>
    [Fact]
    public void Pages_HoldTheirBlocksPointsInSteps()
    {
        const int Block = PointPages.MaxBlockPoints;
        var (xs, ys) = SpatialData.Uniform(100_000, seed: 3);
        using var built = SpatialBuilt.Of(xs, ys);
        var cache = new ResidencyCache { Unbounded = true };
        cache.BeginFrame();
        Assert.Equal((0, 1), (cache.Points.Read(built.Snapshot, 0, Block), cache.Points.Read(built.Snapshot, 1, Block)));
        var uploads = cache.Points.Uploads.ToArray();
        Assert.Equal([PointPages.PagePoints, 100_000 - PointPages.PagePoints], uploads.Select(u => u.Count));
        for (var page = 0; page < 2; page++)
        {
            Check(page, Block, cache.Points.Staged(uploads[page]));
        }

        var first = new ResidentPoint[PointPages.PagePoints];
        built.Snapshot.ReadBlock(0, first);
        Assert.Equal(
            (first[104_729 % Block], first[Block + (104_729 % Block)]),
            (cache.Points.Staged(uploads[0])[1], cache.Points.Staged(uploads[0])[Block + 1])
        );
        Assert.Equal(6_425, 104_729 % Block);

        Assert.Equal(2, cache.Points.Read(built.Snapshot, 0, 512));
        Assert.Equal(0, cache.Points.Request(built.Snapshot, 0, Block));
        Assert.Equal((3, 3), (cache.Points.Uploads.Length, cache.Points.PagesInUse));
        Check(0, 512, cache.Points.Staged(cache.Points.Uploads[2]));
        cache.Points.Forget(built.Snapshot);
        Assert.Equal(0, cache.Points.PagesInUse);
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => cache.Points.Request(built.Snapshot, 0, 0));

        void Check(int page, int block, ReadOnlySpan<ResidentPoint> staged)
        {
            var copy = new ResidentPoint[staged.Length];
            built.Snapshot.ReadBlock((long)page * PointPages.PagePoints, copy);
            for (var k = 0; k < staged.Length; k++)
            {
                var start = k / block * block;
                var length = Math.Min(block, staged.Length - start);
                Assert.Equal(copy[start + (int)(((long)(k - start) * PointPages.Stride) % length)], staged[k]);
            }
        }
    }

    /// <summary>
    /// ReadBlock writes each point's cell and its position in the cell: taken back through the cell's corner and extent, the
    /// position is the point's value to within 2^-32 of the cell's extent, wherever the block begins and ends.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The full snapshot of 100,000 double points in nested clusters, and of 100,000 uniform float points.
    ///
    /// Steps:
    /// - Read every point with ReadPoints. Read the copy with ReadBlock in blocks of 65,536, then in blocks of 1,000 from
    ///   position 777, then one block of 3 that ends at the last point.
    ///
    /// Expected:
    /// - Each point's cell holds its position in the copy.
    /// - corner + position / 2^32 × extent is at most the point's value and less than 2^-32 of the extent (and a rounding
    ///   of the value) below it, on both axes.
    /// </remarks>
    [Fact]
    public void ReadBlock_GivesEachPointsCellAndPosition()
    {
        var (nx, ny) = SpatialData.Nested(100_000, seed: 6);
        var (ux, uy) = SpatialData.Uniform(100_000, seed: 7);
        using var nested = SpatialBuilt.Of(nx, ny);
        using var uniform = SpatialBuilt.Of(ux, uy);
        foreach (var snapshot in new[] { nested.Snapshot, uniform.Snapshot })
        {
            var (px, py, _) = SpatialReference.ReadAll(snapshot);
            var cells = snapshot.ResidentCells.ToArray();
            var count = (int)snapshot.PointCount;
            var points = new ResidentPoint[count];
            for (var first = 0; first < count; first += 65_536)
            {
                snapshot.ReadBlock(first, points.AsSpan(first, Math.Min(65_536, count - first)));
            }

            Check(0, points);

            var some = new ResidentPoint[1_000];
            for (var first = 777; first + some.Length <= count; first += 31_000)
            {
                snapshot.ReadBlock(first, some);
                Check(first, some);
            }

            var last = new ResidentPoint[3];
            snapshot.ReadBlock(count - 3, last);
            Check(count - 3, last);

            void Check(int from, ResidentPoint[] read)
            {
                for (var i = 0; i < read.Length; i++)
                {
                    var cell = cells[read[i].Cell];
                    var at = from + i;
                    Assert.InRange(at, cell.First, cell.First + cell.Count - 1);
                    Within(px[at], cell.CornerX, cell.ExtentX, read[i].X);
                    Within(py[at], cell.CornerY, cell.ExtentY, read[i].Y);
                }
            }
        }

        static void Within(double value, double corner, double extent, uint unit)
        {
            var back = corner + (unit / SpatialSnapshot.CellUnits * extent);
            var slack = (extent / SpatialSnapshot.CellUnits) + (Math.Abs(value) * 4e-16) + (Math.Abs(corner) * 4e-16);
            Assert.InRange(value - back, -slack, 2 * slack);
        }
    }

    /// <summary>
    /// ReadBlock refuses a snapshot that is not full and points outside the copy, reads nothing into an empty span, and
    /// gives position 0 on an axis whose values are all one.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The full snapshot of 500 points whose x are all 7 and whose y are 0 to 499; an index of no rows.
    ///
    /// Steps:
    /// - ReadBlock from −1, from 498 for 3 points, and from 0 for 501 points; ReadBlock with an empty span at 500.
    /// - ReadBlock of all 500 points. ReadBlock of the index of no rows with an empty span.
    ///
    /// Expected:
    /// - ArgumentOutOfRangeException for the three ranges; nothing for the empty spans.
    /// - Every X is 0, and the Y of the points are not all one value.
    /// </remarks>
    [Fact]
    public void ReadBlock_ArgumentsAndAFlatAxis()
    {
        var xs = Enumerable.Repeat(7.0, 500).ToArray();
        var ys = Enumerable.Range(0, 500).Select(i => (double)i).ToArray();
        using var built = SpatialBuilt.Of(xs, ys);
        var snapshot = built.Snapshot;
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.ReadBlock(-1, new ResidentPoint[1]));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.ReadBlock(498, new ResidentPoint[3]));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.ReadBlock(0, new ResidentPoint[501]));
        snapshot.ReadBlock(500, []);

        var points = new ResidentPoint[500];
        snapshot.ReadBlock(0, points);
        Assert.All(points, p => Assert.Equal(0u, p.X));
        Assert.True(points.Select(p => p.Y).Distinct().Count() > 1);

        using var empty = SpatialBuilt.Of(Array.Empty<double>(), Array.Empty<double>());
        Assert.Equal(0, empty.Snapshot.ResidentCells.Length);
        empty.Snapshot.ReadBlock(0, []);
    }
}
