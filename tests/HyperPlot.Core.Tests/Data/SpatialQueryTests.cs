using HyperPlot.Testing;

namespace HyperPlot.Tests;

/// <summary>The queries of a full snapshot of a spatial tile index against plain loops over every point (design §3.4, row C3a).</summary>
[Collection("Large data")]
public sealed class SpatialQueryTests
{
    private const int Points = 20_000;

    private static readonly int[] Levels = [0, 1, 2, 5, 8, 13, 21, 32];

    /// <summary>Count bounds the number of points of a rectangle at every level, and both of its numbers agree with a loop over the cells.</summary>
    /// <remarks>
    /// Preconditions:
    /// - The full snapshot of 20,000 points in nested clusters, with leaves of 16 points, checked against the columns.
    /// - 60 seeded rectangles: 20 with ends anywhere from 10% below to 10% above the ranges, 20 that are a millionth
    ///   of the ranges wide, and 20 whose two ends are the values of one point.
    ///
    /// Steps:
    /// - For each rectangle and each maxLevel of 0, 1, 2, 5, 8, 13, 21, and 32, call Count.
    /// - Count by a loop over every point the points whose grid cells are among the grid cells of the rectangle, and
    ///   the points whose values are in the rectangle.
    /// - Sum, over the cells that GetCells returns for every value at that level, the points of the cells that lie
    ///   wholly among the grid cells of the rectangle, and the points of the other cells that share a grid cell with it.
    ///
    /// Expected:
    /// - A rectangle that misses a range holds no point by its values, and Count answers 0 and 0 at every level.
    /// - Inside ≤ the points by their grid cells ≤ Inside + Touching.
    /// - The points by their values ≤ Inside + Touching.
    /// - Inside and Touching are the two sums.
    /// </remarks>
    [Fact]
    public void Count_AtEveryLevel_BoundsThePointsOfTheRectangle()
    {
        var (xs, ys) = SpatialData.Nested(Points, seed: 40);
        using var built = SpatialBuilt.Of(xs, ys);
        var snapshot = built.Snapshot;
        SpatialReference.Check(snapshot, SpatialAxisData.Of(xs), SpatialAxisData.Of(ys), new RowRange(0, Points), SpatialReference.Small);
        var (px, py, _) = SpatialReference.ReadAll(snapshot);

        foreach (var rect in Rectangles(snapshot, px, py, seed: 41))
        {
            var grid = SpatialReference.Grid(snapshot, rect);
            var byCells = SpatialReference.CountIn(snapshot, px, py, rect);
            long byValues = 0;
            for (var i = 0; i < px.Length; i++)
            {
                if (px[i] >= rect.XMin && px[i] <= rect.XMax && py[i] >= rect.YMin && py[i] <= rect.YMax)
                {
                    byValues++;
                }
            }

            // A rectangle that misses a range the index found holds no point and counts none (design §3.4 "範囲で切る").
            if (Misses(snapshot, rect))
            {
                Assert.Equal(0, byValues);
                foreach (var level in Levels)
                {
                    Assert.Equal(default, snapshot.Count(rect, level));
                }

                continue;
            }

            foreach (var level in Levels)
            {
                long inside = 0;
                long touching = 0;
                foreach (var cell in SpatialReference.Cells(snapshot, SpatialReference.Everything, level))
                {
                    if (SpatialReference.Within(cell, grid))
                    {
                        inside += cell.Count;
                    }
                    else if (SpatialReference.Touches(cell, grid))
                    {
                        touching += cell.Count;
                    }
                }

                var count = snapshot.Count(rect, level);
                if (count != new SpatialCount(inside, touching) || inside > byCells || byCells > inside + touching || byValues > inside + touching)
                {
                    Assert.Fail(
                        $"Count of {rect} at level {level} is {count}; the cells give ({inside}, {touching}), the grid cells {byCells} points, and the values {byValues} points."
                    );
                }
            }
        }
    }

    /// <summary>A rectangle whose ends are the ends of a cell touches nothing from the level of that cell on.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 20,000 seeded double points spread evenly over [0, 1024) on both axes, and both ranges passed as [0, 1024], so
    ///   that a grid cell is 2^-22 wide and the ends of every cell are exact.
    /// - The rectangle [256, 512 − 2^-22] × [0, 256 − 2^-22]: the second cell of level 2 on x and the first on y.
    ///
    /// Steps:
    /// - Count by a loop the points with 256 ≤ x &lt; 512 and y &lt; 256, and the points with x &lt; 512 and y &lt; 512.
    /// - Call Count with the rectangle at maxLevel 0, 1, 2, 3, and 32.
    /// - Call Count with the rectangle of the whole ranges at maxLevel 0.
    ///
    /// Expected:
    /// - maxLevel 0: Inside 0, and every point touches.
    /// - maxLevel 1: Inside 0, and the points of the first cell of level 1 touch.
    /// - maxLevel 2, 3, and 32: Inside is the points of the rectangle, and Touching is 0.
    /// - The whole ranges at maxLevel 0: every point is inside, and Touching is 0.
    /// </remarks>
    [Fact]
    public void Count_RectangleOnTheEndsOfACell_TouchesNothing()
    {
        var random = new Random(42);
        var xs = new double[Points];
        var ys = new double[Points];
        long inRect = 0;
        long inQuarter = 0;
        for (var i = 0; i < Points; i++)
        {
            xs[i] = random.NextSingle() * 1024.0;
            ys[i] = random.NextSingle() * 1024.0;
            inRect += xs[i] >= 256 && xs[i] < 512 && ys[i] < 256 ? 1 : 0;
            inQuarter += xs[i] < 512 && ys[i] < 512 ? 1 : 0;
        }

        var range = new ScaleRange(0, 0, 1024);
        var input = new SpatialIndexInput
        {
            Rows = RowRange.All,
            XRange = range,
            YRange = range,
        };
        using var built = SpatialBuilt.Of(xs, ys, input);
        var snapshot = built.Snapshot;
        var cell = Math.ScaleB(1, -22);
        var rect = new SpatialRect(256, 512 - cell, 0, 256 - cell);

        Assert.InRange(inRect, 1, inQuarter - 1);
        Assert.Equal(new SpatialCount(0, Points), snapshot.Count(rect, 0));
        Assert.Equal(new SpatialCount(0, inQuarter), snapshot.Count(rect, 1));
        Assert.Equal(new SpatialCount(inRect, 0), snapshot.Count(rect, 2));
        Assert.Equal(new SpatialCount(inRect, 0), snapshot.Count(rect, 3));
        Assert.Equal(new SpatialCount(inRect, 0), snapshot.Count(rect, 32));
        Assert.Equal(new SpatialCount(Points, 0), snapshot.Count(new SpatialRect(0, 1024, 0, 1024), 0));
    }

    /// <summary>GetCells returns every cell with points that touches the rectangle, in the order of the codes, and the whole count for a short destination.</summary>
    /// <remarks>
    /// Preconditions:
    /// - The full snapshot of 20,000 points in nested clusters, with leaves of 16 points, checked against the columns:
    ///   the check reads the cells of every value at every level and compares them with the points.
    /// - The 60 seeded rectangles of the Count test.
    ///
    /// Steps:
    /// - For each rectangle and each maxLevel of 0, 1, 2, 5, 8, 13, 21, and 32, take from the cells of every value at
    ///   that level the ones that share a grid cell with the rectangle, by a loop; none for a rectangle that misses a range.
    /// - Call GetCells with an empty destination, with a destination of the count it returned, and with one of 3 cells
    ///   filled with a marker.
    ///
    /// Expected:
    /// - The three calls return the number of the cells of the loop.
    /// - The full destination holds those cells in their order, which is the order of the codes.
    /// - The short destination holds the first 3 of them, or all of them and the marker in the rest.
    /// - At maxLevel 32 every cell is a leaf, and no cell is deeper than maxLevel.
    /// - More than 50 of the 480 pairs of a rectangle and a level have more than 3 cells, so the short destination is short.
    /// </remarks>
    [Fact]
    public void GetCells_AtEveryLevel_ReturnsTheTouchingCellsInTheOrderOfTheCodes()
    {
        var (xs, ys) = SpatialData.Nested(Points, seed: 40);
        using var built = SpatialBuilt.Of(xs, ys);
        var snapshot = built.Snapshot;
        SpatialReference.Check(snapshot, SpatialAxisData.Of(xs), SpatialAxisData.Of(ys), new RowRange(0, Points), SpatialReference.Small);
        var (px, py, _) = SpatialReference.ReadAll(snapshot);
        var marker = new SpatialCell(ulong.MaxValue, 99, -1, -1, SpatialCellKind.Leaf);
        var some = 0;

        foreach (var rect in Rectangles(snapshot, px, py, seed: 41))
        {
            var grid = SpatialReference.Grid(snapshot, rect);
            var misses = Misses(snapshot, rect);
            foreach (var level in Levels)
            {
                var expected = misses
                    ? []
                    : SpatialReference
                    .Cells(snapshot, SpatialReference.Everything, level)
                    .Where(cell => SpatialReference.Touches(cell, grid))
                    .ToArray();
                var cells = SpatialReference.Cells(snapshot, rect, level);
                Assert.True(expected.AsSpan().SequenceEqual(cells), $"The cells of {rect} at level {level} differ from the loop.");
                Assert.DoesNotContain(cells, cell => cell.Level > level || (level == 32 && cell.Kind == SpatialCellKind.Inner));

                SpatialCell[] few = [marker, marker, marker];
                Assert.Equal(expected.Length, snapshot.GetCells(rect, level, few));
                var written = Math.Min(3, expected.Length);
                Assert.True(expected.AsSpan(0, written).SequenceEqual(few.AsSpan(0, written)), "A short destination does not hold the first cells.");
                Assert.DoesNotContain(few.AsSpan(written).ToArray(), cell => cell != marker);
                some += expected.Length > 3 ? 1 : 0;
            }
        }

        Assert.True(some > 50, $"Only {some} of the calls have more than 3 cells.");
    }

    /// <summary>ReadPoints reads any part of the copy: the same points as the whole read.</summary>
    /// <remarks>
    /// Preconditions:
    /// - The full snapshot of 20,000 evenly spread float points, read whole with one call.
    ///
    /// Steps:
    /// - Read 500 points from the point 1,234, the last 3 points, and the whole copy 777 points at a time.
    ///
    /// Expected:
    /// - Each read holds the x, the y, and the row of the same points of the whole read.
    /// </remarks>
    [Fact]
    public void ReadPoints_PartOfTheCopy_ReadsTheSamePointsAsTheWholeRead()
    {
        var (xs, ys) = SpatialData.Uniform(Points, seed: 43);
        using var built = SpatialBuilt.Of(xs, ys);
        var snapshot = built.Snapshot;
        var (px, py, rows) = SpatialReference.ReadAll(snapshot);
        Assert.Equal(Points, px.Length);

        AssertPart(1_234, 500);
        AssertPart(Points - 3, 3);
        for (var first = 0; first < Points; first += 777)
        {
            AssertPart(first, Math.Min(777, Points - first));
        }

        void AssertPart(int first, int count)
        {
            var x = new double[count];
            var y = new double[count];
            var r = new long[count];
            snapshot.ReadPoints(first, x, y, r);
            Assert.True(px.AsSpan(first, count).SequenceEqual(x), $"The x of the {count} points from {first} differ.");
            Assert.True(py.AsSpan(first, count).SequenceEqual(y), $"The y of the {count} points from {first} differ.");
            Assert.True(rows.AsSpan(first, count).SequenceEqual(r), $"The rows of the {count} points from {first} differ.");
        }
    }

    /// <summary>Acquire, Count, GetCells, ReadPoints, and Release allocate nothing.</summary>
    /// <remarks>
    /// Preconditions:
    /// - The complete index of 20,000 points in nested clusters, with leaves of 16 points.
    /// - A rectangle over the middle half of both ranges, a destination of 4,096 cells, and spans of 256 points.
    /// - One round of the calls made before the measurement.
    ///
    /// Steps:
    /// - 100 rounds as a warm-up; collect; read the bytes allocated on the thread, then 100 rounds: Acquire, Count at
    ///   maxLevel 32 and 8, GetCells at maxLevel 32, ReadPoints of 256 points, Release. Read the bytes again.
    ///
    /// Expected:
    /// - The difference is 0 bytes.
    /// - The calls answered: GetCells returned a cell, and Count found points.
    /// </remarks>
    [Fact]
    public void Queries_OnACompleteIndex_AllocateNothing()
    {
        var (xs, ys) = SpatialData.Nested(Points, seed: 44);
        using var built = SpatialBuilt.Of(xs, ys);
        var (xr, yr) = (built.Snapshot.XRange, built.Snapshot.YRange);
        var rect = new SpatialRect(
            xr.Lower + ((xr.Upper - xr.Lower) / 4),
            xr.Upper - ((xr.Upper - xr.Lower) / 4),
            yr.Lower + ((yr.Upper - yr.Lower) / 4),
            yr.Upper - ((yr.Upper - yr.Lower) / 4)
        );
        var cells = new SpatialCell[4_096];
        var x = new double[256];
        var y = new double[256];
        var rows = new long[256];
        long points = 0;
        var returned = 0;

        Round();

        PerformanceAssert.DoesNotAllocate(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                Round();
            }
        });

        Assert.True(points > 0 && returned > 0, "The queries found nothing to answer.");

        void Round()
        {
            var snapshot = built.Index.Acquire();
            var deep = snapshot.Count(rect, 32);
            var coarse = snapshot.Count(rect, 8);
            points = deep.Inside + coarse.Touching;
            returned = snapshot.GetCells(rect, 32, cells);
            snapshot.ReadPoints(100, x, y, rows);
            snapshot.Release();
        }
    }

    // Whether a rectangle misses the x or the y range of a snapshot whose ranges the index found.
    private static bool Misses(SpatialSnapshot snapshot, in SpatialRect rect) =>
        rect.XMax < snapshot.XRange.Lower
        || rect.XMin > snapshot.XRange.Upper
        || rect.YMax < snapshot.YRange.Lower
        || rect.YMin > snapshot.YRange.Upper;

    // 60 rectangles in the differences from the origins: wide ones that reach outside the ranges, narrow ones, and
    // ones that are a single point of the copy.
    private static SpatialRect[] Rectangles(SpatialSnapshot snapshot, double[] px, double[] py, int seed)
    {
        var random = new Random(seed);
        var (xr, yr) = (snapshot.XRange, snapshot.YRange);
        var (xw, yw) = (xr.Upper - xr.Lower, yr.Upper - yr.Lower);
        var rects = new List<SpatialRect>();
        for (var i = 0; i < 20; i++)
        {
            var (x0, x1) = Sorted(Anywhere(xr.Lower, xw), Anywhere(xr.Lower, xw));
            var (y0, y1) = Sorted(Anywhere(yr.Lower, yw), Anywhere(yr.Lower, yw));
            rects.Add(new SpatialRect(x0, x1, y0, y1));
        }

        for (var i = 0; i < 20; i++)
        {
            var at = random.Next(px.Length);
            rects.Add(new SpatialRect(px[at] - (xw * 5e-7), px[at] + (xw * 5e-7), py[at] - (yw * 5e-7), py[at] + (yw * 5e-7)));
        }

        for (var i = 0; i < 20; i++)
        {
            var at = random.Next(px.Length);
            rects.Add(new SpatialRect(px[at], px[at], py[at], py[at]));
        }

        return [.. rects];

        double Anywhere(double lower, double width) => lower + (((random.NextDouble() * 1.2) - 0.1) * width);

        static (double Low, double High) Sorted(double a, double b) => a <= b ? (a, b) : (b, a);
    }
}
