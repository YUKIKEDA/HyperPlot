namespace HyperPlot.Tests;

/// <summary>The nearest point of a full snapshot against a loop over every point (spike of row R3c, design §6.3).</summary>
[Collection("Large data")]
public sealed class SpatialNearestTests
{
    private const int Points = 200_000;
    private const int Queries = 300;
    private const double Radius = 8;
    private const double Tolerance = 0.5;

    /// <summary>Nearest answers within half a pixel of the nearest point a loop finds, and allocates nothing.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 200,000 seeded points, spread evenly or in 16 Gaussian clusters of standard deviations from 1e-3 to 10 with 0.1 %
    ///   outliers 1e4 away, indexed with leaves of 256 points and no cap on the table.
    /// - 300 seeded queries: the cursor on a seeded point, on a seeded point moved by up to 20 pixels, or anywhere in the
    ///   ranges; a view centered on the cursor whose x and y spans are each 10^-u of the range (u uniform in [0, 4)), seen
    ///   at 1,920 × 1,080 pixels, so that the pixels of a unit differ on the two axes; a radius of 8 pixels; markers of no
    ///   radius, or of a largest radius of 6 pixels, as a layer whose points take their size from data passes.
    ///
    /// Steps:
    /// - For each query and each mode (XY and X), call Nearest with the view as the bounds.
    /// - Find by a loop over every point of the copy the least distance in pixels among the points in the view.
    /// - Call Nearest over every query again after a warm-up, counting the bytes allocated.
    ///
    /// Expected:
    /// - Nothing overflows the queue.
    /// - A found point is in the view, nearer than the reach (the radius, plus the marker's radius in XY), no farther
    ///   than the loop's least distance plus half a pixel, and its row, values, and distance agree with the copy.
    /// - Nothing is found only when the loop's least distance is not below the reach less half a pixel.
    /// - The second pass allocates 0 bytes.
    /// </remarks>
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 6)]
    [InlineData(true, 6)]
    public void Nearest_AgainstEveryPoint_IsWithinHalfAPixel(bool clustered, double largestPointRadius)
    {
        var (xs, ys) = clustered ? Clustered(seed: 70) : Uniform(seed: 71);
        var tuning = new SpatialIndexTuning(LeafCapacity: 256, RowsPerUnit: 1 << 12, TableShare: 1e6, WideRows: 1L << 32);
        using var built = SpatialBuilt.Of(xs, ys, tuning: tuning);
        var snapshot = built.Snapshot;
        Assert.Equal(SpatialStage.Full, snapshot.Stage);
        var (px, py, rows) = SpatialReference.ReadAll(snapshot);
        var positionOfRow = new int[Points];
        for (var i = 0; i < rows.Length; i++)
        {
            positionOfRow[rows[i]] = i;
        }

        var scratch = new SpatialNearestScratch(tuning.LeafCapacity);
        var queries = MakeQueries(snapshot, px, py, seed: clustered ? 72 : 73);
        foreach (var mode in (SpatialHitMode[])[SpatialHitMode.XY, SpatialHitMode.X])
        {
            var reach = mode == SpatialHitMode.XY ? Radius + largestPointRadius : Radius;
            foreach (var q in queries)
            {
                var found = snapshot.Nearest(q.X, q.Y, q.Ppx, q.Ppy, Radius, largestPointRadius, mode, q.View, scratch);
                var least = double.PositiveInfinity;
                for (var i = 0; i < px.Length; i++)
                {
                    if (In(q.View, px[i], py[i]))
                    {
                        least = Math.Min(least, Distance(q, mode, px[i], py[i]));
                    }
                }

                var what = $"{mode} query {q}: found {found}, the loop's least distance {least}";
                Assert.False(found.Overflowed, what);
                if (found.Found)
                {
                    var at = positionOfRow[found.Row];
                    Assert.True(In(q.View, found.X, found.Y), what);
                    Assert.True(found.Distance < reach, what);
                    Assert.True(found.Distance <= least + Tolerance + 1e-9, what);
                    Assert.Equal(px[at], found.X);
                    Assert.Equal(py[at], found.Y);
                    Assert.Equal(Distance(q, mode, found.X, found.Y), found.Distance, 1e-9);
                }
                else
                {
                    Assert.True(least >= reach - Tolerance, what);
                }
            }

            var first = queries[0];
            _ = snapshot.Nearest(first.X, first.Y, first.Ppx, first.Ppy, Radius, largestPointRadius, mode, first.View, scratch);
            var before = GC.GetAllocatedBytesForCurrentThread();
            foreach (var q in queries)
            {
                _ = snapshot.Nearest(q.X, q.Y, q.Ppx, q.Ppy, Radius, largestPointRadius, mode, q.View, scratch);
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
    }

    private static bool In(in SpatialRect rect, double x, double y) => x >= rect.XMin && x <= rect.XMax && y >= rect.YMin && y <= rect.YMax;

    private static double Distance(Query q, SpatialHitMode mode, double x, double y)
    {
        var dx = (x - q.X) * q.Ppx;
        var dy = mode == SpatialHitMode.XY ? (y - q.Y) * q.Ppy : 0;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static Query[] MakeQueries(SpatialSnapshot snapshot, double[] px, double[] py, int seed)
    {
        var random = new Random(seed);
        var (xr, yr) = (snapshot.XRange, snapshot.YRange);
        var (width, height) = (xr.Upper - xr.Lower, yr.Upper - yr.Lower);
        var queries = new Query[Queries];
        for (var i = 0; i < Queries; i++)
        {
            var (spanX, spanY) = (width * Math.Pow(10, -4 * random.NextDouble()), height * Math.Pow(10, -4 * random.NextDouble()));
            var (ppx, ppy) = (1920 / spanX, 1080 / spanY);
            var point = random.Next(px.Length);
            var (x, y) = (i % 3) switch
            {
                0 => (px[point], py[point]),
                1 => (px[point] + ((random.NextDouble() - 0.5) * 40 / ppx), py[point] + ((random.NextDouble() - 0.5) * 40 / ppy)),
                _ => (xr.Lower + (random.NextDouble() * width), yr.Lower + (random.NextDouble() * height)),
            };
            var view = new SpatialRect(x - (spanX / 2), x + (spanX / 2), y - (spanY / 2), y + (spanY / 2));
            queries[i] = new Query(x, y, ppx, ppy, view);
        }

        return queries;
    }

    private static (float[] X, float[] Y) Uniform(int seed)
    {
        var random = new Random(seed);
        var x = new float[Points];
        var y = new float[Points];
        for (var i = 0; i < Points; i++)
        {
            x[i] = (float)(random.NextDouble() * 1000);
            y[i] = (float)(random.NextDouble() * 1000);
        }

        return (x, y);
    }

    private static (float[] X, float[] Y) Clustered(int seed)
    {
        const int Clusters = 16;
        var random = new Random(seed);
        var centers = new (double X, double Y, double Sigma)[Clusters];
        for (var k = 0; k < Clusters; k++)
        {
            centers[k] = (random.NextDouble() * 1000, random.NextDouble() * 1000, 1e-3 * Math.Pow(1e4, random.NextDouble()));
        }

        var x = new float[Points];
        var y = new float[Points];
        for (var i = 0; i < Points; i++)
        {
            if (random.NextDouble() < 0.001)
            {
                x[i] = (float)((random.NextDouble() * 2e4) - 1e4);
                y[i] = (float)((random.NextDouble() * 2e4) - 1e4);
                continue;
            }

            var c = centers[random.Next(Clusters)];
            var r = Math.Sqrt(-2 * Math.Log(1 - random.NextDouble()));
            var a = 2 * Math.PI * random.NextDouble();
            x[i] = (float)(c.X + (c.Sigma * r * Math.Cos(a)));
            y[i] = (float)(c.Y + (c.Sigma * r * Math.Sin(a)));
        }

        return (x, y);
    }

    private readonly record struct Query(double X, double Y, double Ppx, double Ppy, SpatialRect View);
}
