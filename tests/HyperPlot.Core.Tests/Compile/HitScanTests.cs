using HyperPlot.Interaction;

namespace HyperPlot.Tests;

/// <summary>The scan of a point layer's mapped pixels in <see cref="HitScan"/> (design §6.3, row R3c, Issue #607).</summary>
public sealed class HitScanTests
{
    private const int Width = 640;
    private const int Height = 360;
    private const float Radius = 8;
    private const double Marker = 4;

    /// <summary>
    /// The scan of points answers as a brute force over every point does: the least distance within the reach and inside the
    /// panel, then the least row within half a pixel of it (design §6.3 "同じ距離", Issue #471 Decision 16, PR #643's review).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A panel of 640 × 360 pixels, a radius of 8 pixels and markers of 4. 400 seeded sets of points within 15 pixels of
    ///   a cursor, half of them of 1 to 8 points and half of 1 to 300, a twentieth of them NaN, some
    ///   past the panel's edges; in every other set the points and the cursor lie on a grid of quarter pixels, so that
    ///   equal distances occur. Each set is asked in both modes, with no radii of the points' own and with them.
    /// - Two sets made by hand: rows whose distances 3.9, 3.4, 3.0, and 4.35 chain within half a pixel of each other; and
    ///   a row 3.6 pixels away before four rows exactly 3 pixels away.
    ///
    /// Steps:
    /// - Scan each set, and compute the answer with a reference that measures every point and shares no code with the scan.
    ///
    /// Expected:
    /// - The same number of hits, the same row, and the same distance in every case.
    /// - The cases include a hit that is not the row of the least distance, a tie of the least distance, no hit, and a point
    ///   past the panel's edge within the radius of the cursor, and an XY hit farther along than the radius (within the
    ///   marker's reach).
    /// - The chain answers row 1 in XY (3.4 is within 3.5, row 0's 3.9 is not); the tie answers row 1.
    /// </remarks>
    [Fact]
    public void Points_AnswerAsTheBruteForce()
    {
        using var store = new PyramidStore();
        using var xColumn = Column.Wrap(new double[300]);
        using var yColumn = Column.Wrap(new double[300]);
        var random = new Random(643);
        var (notLeast, ties, misses, outside, beyond) = (0, 0, 0, 0, 0);
        for (var set = 0; set < 400; set++)
        {
            var grid = set % 2 == 0;
            var count = set % 4 < 2 ? random.Next(1, 9) : random.Next(1, 301);
            var (along, across) = (Coordinate(random, Width, grid), Coordinate(random, Height, grid));
            var xs = new double[count];
            var ys = new double[count];
            var radii = new float[count];
            for (var i = 0; i < count; i++)
            {
                xs[i] = random.Next(20) == 0 ? double.NaN : Near(random, along, grid);
                ys[i] = random.Next(20) == 0 ? double.NaN : Near(random, across, grid);
                radii[i] = random.Next(1, 5);
            }

            foreach (var mode in (ReadOnlySpan<HitMode>)[HitMode.X, HitMode.XY])
            {
                foreach (var own in (ReadOnlySpan<bool>)[false, true])
                {
                    var expected = Reference(xs, ys, own ? radii : null, mode, along, across);
                    Assert.Equal(expected.Hit, Scan(xs, ys, own ? radii : default, mode, along, across));
                    notLeast += expected.NotLeast ? 1 : 0;
                    ties += expected.Tie ? 1 : 0;
                    misses += expected.Hit.Found == 0 ? 1 : 0;
                    outside += expected.Outside ? 1 : 0;
                    beyond += mode == HitMode.XY && expected.Hit.Found == 1 && Math.Abs(xs[expected.Hit.Row] - along) > Radius ? 1 : 0;
                }
            }
        }

        Assert.True(
            notLeast > 0 && ties > 0 && misses > 0 && outside > 0 && beyond > 0,
            $"answers not of the least row {notLeast}, ties {ties}, misses {misses}, outside {outside}, XY hits beyond the radius along {beyond}"
        );

        // A chain of distances within half a pixel of each other: the bound is the least distance plus half a pixel.
        var (cx, cy) = (100.25, 100.25);
        double[] chainXs = [cx + 3.9, cx - 3.4, cx, cx + 4.35];
        double[] chainYs = [cy, cy, cy + 3.0, cy];
        var chain = Scan(chainXs, chainYs, default, HitMode.XY, cx, cy);
        Assert.Equal(Reference(chainXs, chainYs, null, HitMode.XY, cx, cy).Hit, chain);
        Assert.Equal((1, 1L), (chain.Found, chain.Row));

        // Equal distances: the least row of the four at 3 pixels, after a row outside the bound.
        double[] tieXs = [cx + 3.6, cx, cx + 3, cx - 3, cx];
        double[] tieYs = [cy, cy - 3, cy, cy, cy + 3];
        var tie = Scan(tieXs, tieYs, default, HitMode.XY, cx, cy);
        Assert.Equal(Reference(tieXs, tieYs, null, HitMode.XY, cx, cy).Hit, tie);
        Assert.Equal((1, 1L), (tie.Found, tie.Row));

        (int Found, long Row, float Distance) Scan(double[] xs, double[] ys, ReadOnlySpan<float> radii, HitMode mode, double along, double across)
        {
            var results = new SeriesHit[1];
            var scan = new HitScan(results, store, 0, 0, mode, Radius, along, across)
            {
                X = new GeomFrame.Axis(null, new ScaleRange(0, 0, Width), false, false, 0, Width),
                Y = new GeomFrame.Axis(null, new ScaleRange(0, 0, Height), true, false, 0, Height),
            };
            scan.Points(xs, ys, 0, xColumn, yColumn, Marker, radii);
            return scan.Found == 0 ? (0, -1, 0) : (scan.Found, results[0].Row, results[0].Distance);
        }
    }

    /// <summary>
    /// Measures every point: inside the panel widened by a hundredth of a pixel, within the radius along in X, within the
    /// radius and the marker's (or the point's own) in XY; the least distance, then the first row within half a pixel of it.
    /// </summary>
    private static ((int Found, long Row, float Distance) Hit, bool NotLeast, bool Tie, bool Outside) Reference(
        double[] xs,
        double[] ys,
        float[]? radii,
        HitMode mode,
        double along,
        double across
    )
    {
        var distances = new double[xs.Length];
        var (least, outside) = (double.PositiveInfinity, false);
        for (var i = 0; i < xs.Length; i++)
        {
            distances[i] = double.PositiveInfinity;
            var inside = xs[i] >= -0.01 && xs[i] <= Width + 0.01 && ys[i] >= -0.01 && ys[i] <= Height + 0.01;
            var (dx, dy) = (xs[i] - along, ys[i] - across);
            if (!inside)
            {
                outside |= Math.Abs(dx) <= Radius && Math.Abs(dy) <= Radius;
                continue;
            }

            if (mode == HitMode.X)
            {
                if (Math.Abs(dx) <= Radius)
                {
                    distances[i] = Math.Abs(dx);
                }
            }
            else
            {
                var within = Radius + (radii is null ? Marker : radii[i]);
                if ((dx * dx) + (dy * dy) <= within * within)
                {
                    distances[i] = Math.Sqrt((dx * dx) + (dy * dy));
                }
            }

            least = Math.Min(least, distances[i]);
        }

        if (double.IsPositiveInfinity(least))
        {
            return ((0, -1, 0), false, false, outside);
        }

        var answer = Array.FindIndex(distances, d => d <= least + 0.5);
        var first = Array.IndexOf(distances, least);
        var tie = Array.LastIndexOf(distances, least) != first;
        var (ax, ay) = (xs[answer] - along, ys[answer] - across);
        return ((1, answer, (float)Math.Sqrt((ax * ax) + (ay * ay))), answer != first, tie, outside);
    }

    // A cursor's coordinate inside the panel, on a grid of quarter pixels or not.
    private static double Coordinate(Random random, int size, bool grid) => Snap(1 + (random.NextDouble() * (size - 2)), grid);

    // A point's coordinate within 15 pixels of the cursor's: some lie past the panel's edges when the cursor is near one.
    private static double Near(Random random, double cursor, bool grid) => Snap(cursor + ((random.NextDouble() * 30) - 15), grid);

    private static double Snap(double value, bool grid) => grid ? Math.Round(value * 4) / 4 : value;
}
