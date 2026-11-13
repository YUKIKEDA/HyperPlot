using HyperPlot.Interaction;
using HyperPlot.Testing;

namespace HyperPlot.Tests;

/// <summary>
/// The nearest point of a full snapshot, found best first (design §6.3 "探し方", row R3c, Issue #471 Decisions 19 to 21),
/// against a loop over every point where it is drawn.
/// </summary>
[Collection("Large data")]
public sealed class SpatialNearestTests
{
    private const int Points = 50_000;
    private const int Queries = 300;
    private const double Radius = 8;
    private const double Tolerance = 0.5;

    // The rounding of the doubles of a box's ends against the doubles of a drawn point: far below a pixel, so that the
    // answer stays within half a pixel of the loop's least distance up to it.
    private const double Rounding = 1e-6;

    /// <summary>The shapes of data the search is compared on.</summary>
    public enum Shape
    {
        /// <summary>Points spread evenly.</summary>
        Uniform,

        /// <summary>Clusters inside clusters, and 16 Gaussian clusters with far outliers.</summary>
        Clustered,

        /// <summary>500 points at one place among others: a coincident leaf of more points than a leaf holds.</summary>
        Coincident,

        /// <summary>A table capped at about 50 rows: capped leaves of more points than a leaf holds.</summary>
        Capped,
    }

    /// <summary>
    /// Nearest answers within half a pixel of the least distance a loop finds over the points drawn, in both modes, at zooms
    /// that differ on x and y, and finds nothing only when no point is drawn within the reach less half a pixel.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 50,000 seeded points of each shape, with leaves of 16 points: spread evenly; nested and Gaussian clusters with
    ///   0.1 % outliers; 500 at one place (a coincident leaf); a table capped at about 50 rows (capped leaves).
    /// - 300 seeded queries: the cursor on a point, on a point moved by up to 20 pixels, or anywhere in the ranges; a view
    ///   whose x and y spans are each 10^-u of the range with u uniform in [−2, 4), seen at 1,920 × 1,080 pixels with y
    ///   downward, so that the pixels of a unit differ on the two axes and cells of less than half a pixel occur; a radius
    ///   of 8 pixels; markers of no radius or of 6 pixels.
    /// - The screen places a resident point at its unit of its cell, as the vertex shader does.
    ///
    /// Steps:
    /// - For each query and each mode (XY and X), call Nearest.
    /// - Find by a loop the least distance from the cursor to the points where they are drawn (each point's unit of its
    ///   resident cell, read with ReadBlock), over the first 16 points of each leaf, as the search reads them.
    ///
    /// Expected:
    /// - Nothing overflows the queue.
    /// - A found point is nearer than the reach (the radius, plus the marker's radius in XY) and no farther than the loop's
    ///   least distance plus half a pixel; its row, values, drawn pixels, and distance are the copy's.
    /// - Nothing is found only when the loop's least distance is not below the reach less half a pixel.
    /// - The same query again answers the same row.
    /// </remarks>
    [Theory]
    [InlineData(Shape.Uniform, 0)]
    [InlineData(Shape.Uniform, 6)]
    [InlineData(Shape.Clustered, 0)]
    [InlineData(Shape.Clustered, 6)]
    [InlineData(Shape.Coincident, 0)]
    [InlineData(Shape.Capped, 0)]
    public void Nearest_AgainstEveryPointDrawn_IsWithinHalfAPixel(Shape shape, double markerRadius)
    {
        var (xs, ys) = Make(shape, seed: 70 + (int)shape);
        var tuning = new SpatialIndexTuning(
            LeafCapacity: 16,
            RowsPerUnit: 1 << 12,
            TableShare: shape == Shape.Capped ? 2e-3 : 1e6,
            WideRows: 1L << 32
        );
        using var built = SpatialBuilt.Of(xs, ys, tuning: tuning);
        var snapshot = built.Snapshot;
        Assert.Equal(SpatialStage.Full, snapshot.Stage);
        var drawn = Drawn(snapshot, tuning.LeafCapacity);
        if (shape is Shape.Coincident or Shape.Capped)
        {
            var kind = shape == Shape.Coincident ? SpatialCellKind.CoincidentLeaf : SpatialCellKind.CappedLeaf;
            Assert.Contains(SpatialReference.Cells(snapshot, SpatialReference.Everything, 32), cell => cell.Kind == kind && cell.Count > 16);
        }

        var scratch = new SpatialNearestScratch(tuning.LeafCapacity);
        foreach (var mode in (HitMode[])[HitMode.XY, HitMode.X])
        {
            var reach = mode == HitMode.XY ? Radius + markerRadius : Radius;
            foreach (var q in MakeQueries(snapshot, drawn, seed: 80 + (int)shape))
            {
                var screen = q.Screen;
                var found = snapshot.Nearest(ref screen, q.Along, q.Across, Radius, markerRadius, mode, scratch);
                var least = double.PositiveInfinity;
                for (var i = 0; i < drawn.Readable.Length; i++)
                {
                    if (drawn.Readable[i])
                    {
                        var (a, c) = q.Screen.At(drawn, i);
                        least = Math.Min(least, Distance(q, mode, a, c));
                    }
                }

                var what = $"{mode} query {q}: found {found}, the loop's least distance {least}";
                Assert.False(found.Overflowed, what);
                if (found.Found)
                {
                    var at = drawn.PositionOfRow[found.Row];
                    var (a, c) = q.Screen.At(drawn, at);
                    Assert.True(found.Distance < reach, what);
                    Assert.True(found.Distance <= least + Tolerance + Rounding, what);
                    Assert.Equal((drawn.X[at], drawn.Y[at]), (found.X, found.Y));
                    Assert.Equal((a, c), (found.Along, found.Across));
                    Assert.Equal(Distance(q, mode, a, c), found.Distance, 9);
                    var again = q.Screen;
                    Assert.Equal(found.Row, snapshot.Nearest(ref again, q.Along, q.Across, Radius, markerRadius, mode, scratch).Row);
                }
                else
                {
                    Assert.True(least >= reach - Tolerance - Rounding, what);
                }
            }
        }
    }

    /// <summary>
    /// A cursor on a point stops the search at once: no cell farther than the point less half a pixel is taken, however many
    /// points lie within the reach (design §6.3 "探し方").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 50,000 evenly spread points with leaves of 16 points, the whole range seen at 1,920 × 1,080 pixels.
    /// - A radius of 2,000 pixels, which reaches every point.
    ///
    /// Steps:
    /// - Call Nearest in XY at the drawn pixel of each of 50 seeded points.
    ///
    /// Expected:
    /// - Each finds a point at a distance of 0 and reads at most the points of the four leaves that can meet at the cursor
    ///   (64), of the 50,000 within reach.
    /// </remarks>
    [Fact]
    public void Nearest_CursorOnAPoint_ReadsNoFartherCell()
    {
        var (xs, ys) = Make(Shape.Uniform, seed: 71);
        var tuning = new SpatialIndexTuning(LeafCapacity: 16, RowsPerUnit: 1 << 12, TableShare: 1e6, WideRows: 1L << 32);
        using var built = SpatialBuilt.Of(xs, ys, tuning: tuning);
        var snapshot = built.Snapshot;
        var drawn = Drawn(snapshot, tuning.LeafCapacity);
        var screen = WholeView(snapshot);
        var scratch = new SpatialNearestScratch(tuning.LeafCapacity);
        var random = new Random(72);
        for (var k = 0; k < 50; k++)
        {
            var (a, c) = screen.At(drawn, random.Next(Points));
            var found = snapshot.Nearest(ref screen, a, c, 2000, 0, HitMode.XY, scratch);
            Assert.True(found.Found);
            Assert.Equal(0.0, found.Distance);
            Assert.InRange(found.PointsRead, 1, 4 * tuning.LeafCapacity);
        }
    }

    /// <summary>
    /// A cell no nearer than the best point less half a pixel is not opened, though its box is nearer than the best point
    /// (design §6.3 "探し方"): only the half pixel lets it be left.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two points, A (8.5, 10.1) and B (11.9, 10.1), with leaves of 1 point and both ranges passed as [0, 16], so that the
    ///   leaves are the cells of level 3, [8, 10) and [10, 12) on x; a screen of 1 pixel a unit.
    ///
    /// Steps:
    /// - Call Nearest in XY at (9.3, 10.1) with a radius of 8.
    ///
    /// Expected:
    /// - A, at 0.8 pixels. B's leaf lies 0.7 pixels away, nearer than A but not nearer than 0.8 − 0.5: it is not opened, and
    ///   one point is read.
    /// </remarks>
    [Fact]
    public void Nearest_CellWithinHalfAPixelOfTheBest_IsNotOpened()
    {
        double[] xs = [8.5, 11.9];
        double[] ys = [10.1, 10.1];
        var range = new ScaleRange(0, 0, 16);
        var input = new SpatialIndexInput { Rows = RowRange.All, XRange = range, YRange = range };
        var tuning = new SpatialIndexTuning(LeafCapacity: 1, RowsPerUnit: 1 << 12, TableShare: 1e6, WideRows: 1L << 32);
        using var built = SpatialBuilt.Of(xs, ys, input, tuning: tuning);
        var snapshot = built.Snapshot;
        Assert.Contains(
            SpatialReference.Cells(snapshot, SpatialReference.Everything, 32),
            cell => cell.Level == 3 && cell.Count == 1 && snapshot.CellBox(cell.Code, 3).CornerX == 10
        );
        var screen = new LinearScreen(1, 1, 0, 0);

        var found = snapshot.Nearest(ref screen, 9.3, 10.1, 8, 0, HitMode.XY, new SpatialNearestScratch(1));

        Assert.Equal(0, found.Row);
        Assert.Equal(0.8, found.Distance, 9);
        Assert.Equal(1, found.PointsRead);
    }

    /// <summary>
    /// The reach of XY is the radius plus the marker's radius (a layer whose points take their size from data passes its
    /// largest); X uses the radius alone; with no point within the reach nothing is found (design §6.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 50,000 evenly spread points, the whole range at 1,920 × 1,080 pixels.
    /// - A cursor 12 pixels to the right of a point's drawn pixel, with no other point drawn nearer than 13 pixels: found by a
    ///   loop over seeded points.
    ///
    /// Steps:
    /// - Call Nearest in XY with a radius of 8 and a marker radius of 6, then 3; in X with a radius of 8 and a marker radius
    ///   of 6; in XY with a radius of 0 and no marker.
    ///
    /// Expected:
    /// - XY with 6: that point, at 12 pixels. XY with 3, X, and a radius of 0: nothing.
    /// </remarks>
    [Fact]
    public void Nearest_Reach_IsTheRadiusPlusTheMarkersInXY()
    {
        var (xs, ys) = Make(Shape.Uniform, seed: 73);
        var tuning = new SpatialIndexTuning(LeafCapacity: 16, RowsPerUnit: 1 << 12, TableShare: 1e6, WideRows: 1L << 32);
        using var built = SpatialBuilt.Of(xs, ys, tuning: tuning);
        var snapshot = built.Snapshot;
        var drawn = Drawn(snapshot, tuning.LeafCapacity);

        // Zoomed in so that the points are tens of pixels apart: a cursor 12 pixels right of a lone point.
        var (xr, yr) = (snapshot.XRange, snapshot.YRange);
        var screen = new LinearScreen(1920 / ((xr.Upper - xr.Lower) / 100), -1080 / ((yr.Upper - yr.Lower) / 100), xr.Lower, yr.Upper);
        var scratch = new SpatialNearestScratch(tuning.LeafCapacity);
        var random = new Random(74);
        for (var tries = 0; ; tries++)
        {
            Assert.True(tries < 1000, "No lone point was found.");
            var target = random.Next(Points);
            var (a, c) = screen.At(drawn, target);
            var cursor = (Along: a + 12, Across: c);
            var others = double.PositiveInfinity;
            for (var i = 0; i < Points; i++)
            {
                if (i != target)
                {
                    var (oa, oc) = screen.At(drawn, i);
                    others = Math.Min(others, Math.Sqrt(((oa - cursor.Along) * (oa - cursor.Along)) + ((oc - cursor.Across) * (oc - cursor.Across))));
                }
            }

            if (!(others > 13))
            {
                continue;
            }

            var hit = snapshot.Nearest(ref screen, cursor.Along, cursor.Across, 8, 6, HitMode.XY, scratch);
            Assert.True(hit.Found);
            Assert.Equal(drawn.Rows[target], hit.Row);
            Assert.Equal(12, hit.Distance, 9);
            Assert.False(snapshot.Nearest(ref screen, cursor.Along, cursor.Across, 8, 3, HitMode.XY, scratch).Found);
            Assert.False(snapshot.Nearest(ref screen, cursor.Along, cursor.Across, 8, 6, HitMode.X, scratch).Found);
            Assert.False(snapshot.Nearest(ref screen, cursor.Along, cursor.Across, 0, 0, HitMode.XY, scratch).Found);
            return;
        }
    }

    /// <summary>
    /// A queue that is full leaves the cells it cannot hold unread and answers the best point found so far as provisional
    /// (Issue #471 Decision 20); the default queue does not overflow on the same query.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 50,000 evenly spread points with leaves of 16 points, the whole range at 1,920 × 1,080 pixels.
    /// - A radius of 300 pixels at the middle of the view, which reaches thousands of cells.
    ///
    /// Steps:
    /// - Call Nearest in XY with a queue of 4 cells, then with the default queue.
    ///
    /// Expected:
    /// - The small queue overflows, and what it finds is within the reach. The default queue does not overflow, finds a
    ///   point, and reads at least as many points as the small one.
    /// </remarks>
    [Fact]
    public void Nearest_FullQueue_LeavesCellsOutAndSaysSo()
    {
        var (xs, ys) = Make(Shape.Uniform, seed: 75);
        var tuning = new SpatialIndexTuning(LeafCapacity: 16, RowsPerUnit: 1 << 12, TableShare: 1e6, WideRows: 1L << 32);
        using var built = SpatialBuilt.Of(xs, ys, tuning: tuning);
        var snapshot = built.Snapshot;
        var screen = WholeView(snapshot);

        var small = snapshot.Nearest(ref screen, 960.5, 540.5, 300, 0, HitMode.XY, new SpatialNearestScratch(tuning.LeafCapacity, queueCells: 4));
        Assert.True(small.Overflowed);
        Assert.True(!small.Found || small.Distance < 300);

        var whole = snapshot.Nearest(ref screen, 960.5, 540.5, 300, 0, HitMode.XY, new SpatialNearestScratch(tuning.LeafCapacity));
        Assert.False(whole.Overflowed);
        Assert.True(whole.Found);
        Assert.True(whole.PointsRead >= small.PointsRead);
    }

    /// <summary>A warm search allocates nothing (invariant 3, design §6.3 "探し方").</summary>
    /// <remarks>
    /// Preconditions:
    /// - 50,000 points in clusters with leaves of 16 points, 300 seeded queries, one scratch.
    ///
    /// Steps:
    /// - Run every query in both modes once to warm, then again while counting the thread's allocations.
    ///
    /// Expected:
    /// - 0 bytes, and the queries found points.
    /// </remarks>
    [Fact]
    public void Nearest_Warm_AllocatesNothing()
    {
        var (xs, ys) = Make(Shape.Clustered, seed: 76);
        var tuning = new SpatialIndexTuning(LeafCapacity: 16, RowsPerUnit: 1 << 12, TableShare: 1e6, WideRows: 1L << 32);
        using var built = SpatialBuilt.Of(xs, ys, tuning: tuning);
        var snapshot = built.Snapshot;
        var queries = MakeQueries(snapshot, Drawn(snapshot, tuning.LeafCapacity), seed: 77);
        var scratch = new SpatialNearestScratch(tuning.LeafCapacity);
        var found = 0;

        PerformanceAssert.DoesNotAllocate(() =>
        {
            found = 0;
            foreach (var q in queries)
            {
                var screen = q.Screen;
                found += snapshot.Nearest(ref screen, q.Along, q.Across, Radius, 0, HitMode.XY, scratch).Found ? 1 : 0;
                found += snapshot.Nearest(ref screen, q.Along, q.Across, Radius, 0, HitMode.X, scratch).Found ? 1 : 0;
            }
        });

        Assert.True(found > 0);
    }

    private static double Distance(Query q, HitMode mode, double along, double across)
    {
        var da = along - q.Along;
        var dc = mode == HitMode.XY ? across - q.Across : 0;
        return Math.Sqrt((da * da) + (dc * dc));
    }

    // The whole range at 1,920 × 1,080, y downward.
    private static LinearScreen WholeView(SpatialSnapshot snapshot)
    {
        var (xr, yr) = (snapshot.XRange, snapshot.YRange);
        return new LinearScreen(1920 / (xr.Upper - xr.Lower), -1080 / (yr.Upper - yr.Lower), xr.Lower, yr.Upper);
    }

    // Every point of the copy as the GPU holds it, which of them the search reads (the first LeafCapacity of each leaf),
    // and the position of each row.
    private static DrawnPoints Drawn(SpatialSnapshot snapshot, int leafCapacity)
    {
        var (px, py, rows) = SpatialReference.ReadAll(snapshot);
        var resident = new ResidentPoint[px.Length];
        snapshot.ReadBlock(0, resident);
        var readable = new bool[px.Length];
        foreach (var leaf in SpatialReference.Cells(snapshot, SpatialReference.Everything, 32))
        {
            for (var i = 0; i < Math.Min(leaf.Count, leafCapacity); i++)
            {
                readable[leaf.First + i] = true;
            }
        }

        var positionOfRow = new Dictionary<long, int>();
        for (var i = 0; i < rows.Length; i++)
        {
            positionOfRow[rows[i]] = i;
        }

        return new DrawnPoints(px, py, rows, resident, snapshot.ResidentCells.ToArray(), readable, positionOfRow);
    }

    private static Query[] MakeQueries(SpatialSnapshot snapshot, DrawnPoints drawn, int seed)
    {
        var random = new Random(seed);
        var (xr, yr) = (snapshot.XRange, snapshot.YRange);
        var (width, height) = (xr.Upper - xr.Lower, yr.Upper - yr.Lower);
        var queries = new Query[Queries];
        for (var i = 0; i < Queries; i++)
        {
            var (spanX, spanY) = (width * Math.Pow(10, 2 - (6 * random.NextDouble())), height * Math.Pow(10, 2 - (6 * random.NextDouble())));
            var point = random.Next(drawn.X.Length);
            var (x, y) = (i % 3) switch
            {
                0 => (drawn.X[point], drawn.Y[point]),
                1 => (drawn.X[point] + ((random.NextDouble() - 0.5) * 40 * spanX / 1920), drawn.Y[point] + ((random.NextDouble() - 0.5) * 40 * spanY / 1080)),
                _ => (xr.Lower + (random.NextDouble() * width), yr.Lower + (random.NextDouble() * height)),
            };

            // The view centered on (x, y): pixel 0 at its left and top, y downward.
            var screen = new LinearScreen(1920 / spanX, -1080 / spanY, x - (spanX / 2), y + (spanY / 2));
            queries[i] = new Query(screen.Along(x), screen.Across(y), screen);
        }

        return queries;
    }

    private static (double[] X, double[] Y) Make(Shape shape, int seed)
    {
        var random = new Random(seed);
        var x = new double[Points];
        var y = new double[Points];
        if (shape == Shape.Clustered)
        {
            var (nx, ny) = SpatialData.Nested(Points / 2, seed);
            var centers = new (double X, double Y, double Sigma)[16];
            for (var k = 0; k < centers.Length; k++)
            {
                centers[k] = (random.NextDouble(), random.NextDouble(), 1e-6 * Math.Pow(1e4, random.NextDouble()));
            }

            for (var i = 0; i < Points; i++)
            {
                if (i < nx.Length)
                {
                    (x[i], y[i]) = (nx[i], ny[i]);
                }
                else if (random.NextDouble() < 0.001)
                {
                    (x[i], y[i]) = ((random.NextDouble() * 20) - 10, (random.NextDouble() * 20) - 10);
                }
                else
                {
                    var c = centers[random.Next(centers.Length)];
                    var r = Math.Sqrt(-2 * Math.Log(1 - random.NextDouble()));
                    var angle = 2 * Math.PI * random.NextDouble();
                    (x[i], y[i]) = (c.X + (c.Sigma * r * Math.Cos(angle)), c.Y + (c.Sigma * r * Math.Sin(angle)));
                }
            }

            return (x, y);
        }

        for (var i = 0; i < Points; i++)
        {
            (x[i], y[i]) = shape == Shape.Coincident && i % 100 == 0 ? (0.25, 0.75) : (random.NextDouble(), random.NextDouble());
        }

        return (x, y);
    }

    /// <summary>The points of a snapshot as the GPU draws them, read apart from the search.</summary>
    private sealed record DrawnPoints(
        double[] X,
        double[] Y,
        long[] Rows,
        ResidentPoint[] Resident,
        ResidentCell[] Cells,
        bool[] Readable,
        Dictionary<long, int> PositionOfRow
    );

    private readonly record struct Query(double Along, double Across, LinearScreen Screen);

    /// <summary>
    /// A screen of a linear map on each axis, (v − origin) × pixels a unit, which places a resident point at its unit of its
    /// cell: the corner plus the unit's share of the extent, as the vertex shader does before its float rounding.
    /// </summary>
    private struct LinearScreen(double perUnitX, double perUnitY, double originX, double originY) : ISpatialScreen
    {
        private ResidentCell _cell;

        public readonly double Along(double x) => (x - originX) * perUnitX;

        public readonly double Across(double y) => (y - originY) * perUnitY;

        public void Enter(in ResidentCell cell) => _cell = cell;

        public readonly (double Along, double Across) Place(uint unitX, uint unitY) =>
            (
                Along(_cell.CornerX + (unitX / SpatialSnapshot.CellUnits * _cell.ExtentX)),
                Across(_cell.CornerY + (unitY / SpatialSnapshot.CellUnits * _cell.ExtentY))
            );

        // The drawn pixels of the point at a position of the copy, from ReadBlock's units: apart from the search's own.
        public readonly (double Along, double Across) At(DrawnPoints drawn, int position)
        {
            var point = drawn.Resident[position];
            var placed = this;
            placed.Enter(drawn.Cells[point.Cell]);
            return placed.Place(point.X, point.Y);
        }
    }
}
