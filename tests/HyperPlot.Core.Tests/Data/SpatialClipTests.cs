namespace HyperPlot.Tests;

/// <summary>
/// A query's rectangle clipped to the ranges the index found itself (design §3.4 "範囲で切る", row R3c, Issue #471 Decision
/// 3), against the rectangles that only touch the ranges and the ranges passed to the index.
/// </summary>
public sealed class SpatialClipTests
{
    private const int Columns = 40;
    private const int RowsOfPoints = 25;
    private const int Points = Columns * RowsOfPoints;

    /// <summary>
    /// A rectangle that misses the range of x or of y counts nothing and touches no cell, whichever side it misses on; one
    /// that only touches a range's end counts the points at that end.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 1,000 double points on a grid of 40 × 25: x from 10 to 90, y from 20 to 80, with leaves of 16 points; the index
    ///   finds both ranges, so x is [0, 80] and y is [0, 60] from the origins 10 and 20.
    ///
    /// Steps:
    /// - Count at maxLevel 32 and 8, and GetCells at maxLevel 32, for rectangles that miss x on the left and on the right
    ///   with y over its whole range, and that miss y below and above with x over its whole range.
    /// - The same for the rectangles [−10, 0] × [0, 60] and [80, 100] × [0, 60], which touch an end of x.
    ///
    /// Expected:
    /// - The four that miss: Count is 0 inside and 0 touching at both levels; GetCells returns 0.
    /// - Each that touches an end: Inside ≤ 25 ≤ Inside + Touching at maxLevel 32 (the 25 points of the column at that
    ///   end), and GetCells returns a cell.
    /// </remarks>
    [Fact]
    public void Queries_RectangleThatMissesARange_CountNothing()
    {
        var (xs, ys) = Grid();
        using var built = SpatialBuilt.Of(xs, ys, tuning: SpatialReference.Small);
        var snapshot = built.Snapshot;
        Assert.Equal(new ScaleRange(10, 0, 80), snapshot.XRange);
        Assert.Equal(new ScaleRange(20, 0, 60), snapshot.YRange);
        var cells = new SpatialCell[Points];

        SpatialRect[] misses = [new(-50, -1e-9, 0, 60), new(80 + 1e-9, 200, 0, 60), new(0, 80, -5, -1e-9), new(0, 80, 60 + 1e-9, 70)];
        foreach (var rect in misses)
        {
            Assert.Equal(new SpatialCount(0, 0), snapshot.Count(rect, 32));
            Assert.Equal(new SpatialCount(0, 0), snapshot.Count(rect, 8));
            Assert.Equal(0, snapshot.GetCells(rect, 32, cells));
        }

        foreach (var rect in (SpatialRect[])[new(-10, 0, 0, 60), new(80, 100, 0, 60)])
        {
            var count = snapshot.Count(rect, 32);
            Assert.InRange((long)RowsOfPoints, count.Inside, count.Inside + count.Touching);
            Assert.True(snapshot.GetCells(rect, 32, cells) > 0, $"{rect} touches no cell.");
        }
    }

    /// <summary>
    /// An axis whose two ends are one value is clipped too: a rectangle beside the value counts nothing, one through it
    /// counts every point.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 1,000 points whose x are all 5 and whose y run from 20 to 80, with leaves of 16 points: x's range is [0, 0] from
    ///   the origin 5.
    ///
    /// Steps:
    /// - Count at maxLevel 32 the rectangles [−1, −0.5] × [0, 60], [0.5, 1] × [0, 60], [0, 0] × [0, 60], and
    ///   [−1, 1] × [0, 60].
    ///
    /// Expected:
    /// - The first two: 0 and 0. The last two: Inside + Touching is 1,000, as every point is in grid cell 0 of x.
    /// </remarks>
    [Fact]
    public void Count_AxisOfOneValue_ClipsToThatValue()
    {
        var (_, ys) = Grid();
        var xs = Enumerable.Repeat(5.0, Points).ToArray();
        using var built = SpatialBuilt.Of(xs, ys, tuning: SpatialReference.Small);
        var snapshot = built.Snapshot;
        Assert.Equal(new ScaleRange(5, 0, 0), snapshot.XRange);

        Assert.Equal(new SpatialCount(0, 0), snapshot.Count(new SpatialRect(-1, -0.5, 0, 60), 32));
        Assert.Equal(new SpatialCount(0, 0), snapshot.Count(new SpatialRect(0.5, 1, 0, 60), 32));
        var on = snapshot.Count(new SpatialRect(0, 0, 0, 60), 32);
        Assert.Equal(Points, on.Inside + on.Touching);
        var through = snapshot.Count(new SpatialRect(-1, 1, 0, 60), 32);
        Assert.Equal(Points, through.Inside + through.Touching);
    }

    /// <summary>
    /// A range passed to the index is not clipped: the points outside it lie in its end grid cells, and a rectangle past
    /// its end counts them. The other axis, whose range the index found, is still clipped.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The 1,000 points of the grid, x's range passed as [0, 80] from the origin 10, y's found, leaves of 16 points.
    ///
    /// Steps:
    /// - Count at maxLevel 32 the rectangle [−50, −1] × [0, 60], which misses x's range on the left.
    /// - Count at maxLevel 32 the rectangle [0, 80] × [−5, −1], which misses y's range below.
    ///
    /// Expected:
    /// - The first counts the 25 points of x's lower end cell: Inside ≤ 25 ≤ Inside + Touching.
    /// - The second counts nothing.
    /// </remarks>
    [Fact]
    public void Count_RangePassed_CountsTheEndCellsOutsideIt()
    {
        var (xs, ys) = Grid();
        var input = new SpatialIndexInput { Rows = RowRange.All, XRange = new ScaleRange(10, 0, 80) };
        using var built = SpatialBuilt.Of(xs, ys, input, tuning: SpatialReference.Small);
        var snapshot = built.Snapshot;

        var outside = snapshot.Count(new SpatialRect(-50, -1, 0, 60), 32);
        Assert.InRange((long)RowsOfPoints, outside.Inside, outside.Inside + outside.Touching);
        Assert.Equal(new SpatialCount(0, 0), snapshot.Count(new SpatialRect(0, 80, -5, -1), 32));
    }

    // The points of a grid of 40 columns and 25 rows: x from 10 to 90, y from 20 to 80, both ends included.
    private static (double[] X, double[] Y) Grid()
    {
        var xs = new double[Points];
        var ys = new double[Points];
        for (var i = 0; i < Points; i++)
        {
            xs[i] = 10 + (80.0 * (i % Columns) / (Columns - 1));
            ys[i] = 20 + (60.0 * (i / Columns) / (RowsOfPoints - 1));
        }

        return (xs, ys);
    }
}
