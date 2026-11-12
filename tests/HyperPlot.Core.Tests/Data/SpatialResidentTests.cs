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
