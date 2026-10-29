namespace HyperPlot.Tests;

/// <summary>M4 in origins and differences (design §3.6, §5.5.1, row D2): requirement R-6 at deep zoom.</summary>
[Collection("Large data")]
public sealed class M4OriginTests
{
    private const int Width = 1920;
    private const long Ticks2026 = 639_288_288_000_000_000;
    private const long Nanoseconds2026 = 1_793_232_000_000_000_000;
    private const int Length = Column.ChunkLength + 100_000;

    /// <summary>
    /// From the whole view down to a view one unit wide over 1,920 pixel columns, the boundaries increase and M4's points equal
    /// the brute force over the exact differences, for X in 2026's .NET ticks, in 1970-based nanoseconds, and as an integer
    /// uniform column (requirement R-6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two chunks of float Y with NaN; X as a Column&lt;long&gt; of ticks 10 apart with jitter, a Column&lt;long&gt; of
    ///   nanoseconds 1 apart, and Column.UniformInteger from 2026's ticks + 1 with step 10; their statistics computed.
    ///
    /// Steps:
    /// - Around the middle element, views 1/16 as wide each time, down to 1 unit; for each, a ScaleRange anchored as design
    ///   §3.6 says, W + 1 boundaries as its differences, and M4 with its origin.
    ///
    /// Expected:
    /// - The boundaries strictly increase; the points equal the brute force over each element's exact difference from the
    ///   origin, rounded once (index, X, Y, roles, breaks). The pixel of a point follows from its X, so the X decides it. In
    ///   the deepest view, the same boundaries as absolute doubles would not increase (the case R-6 is about).
    /// </remarks>
    [Fact]
    public void Extract_DeepZoomFarFromZero_EqualsTheExactBruteForce()
    {
        var random = new Random(61);
        var values = Enumerable.Range(0, Length).Select(i => i % 9973 == 0 ? float.NaN : (float)(Math.Sin(i * 1e-4) + random.NextDouble())).ToArray();
        var y = values.Select(v => (double)v).ToArray();
        using var pool = new PlotWorkerPool(4);
        using var yColumn = Column.Wrap(values);
        using var pyramid = MinMaxPyramidTests.Build(pool, yColumn);
        var ticks = Enumerable.Range(0, Length).Select(i => Ticks2026 + (10L * i) + random.Next(10)).ToArray();
        var nanoseconds = Enumerable.Range(0, Length).Select(i => Nanoseconds2026 + i).ToArray();
        using var ticksColumn = Column.Wrap(ticks);
        using var nanosecondsColumn = Column.Wrap(nanoseconds);
        Eventually.Completes(pool.PrecomputeStats(ticksColumn));
        Eventually.Completes(pool.PrecomputeStats(nanosecondsColumn));
        var uniform = Column.UniformInteger(Ticks2026 + 1, 10, Length);
        var uniformValues = Enumerable.Range(0, Length).Select(i => Ticks2026 + 1 + (10L * i)).ToArray();

        foreach (var (x, xValues) in new (Column, long[])[] { (ticksColumn, ticks), (nanosecondsColumn, nanoseconds), (uniform, uniformValues) })
        {
            var center = xValues[Length / 2];
            var span = (double)(xValues[^1] - xValues[0]);
            var views = 0;
            while (true)
            {
                var range = new ScaleRange(center, -span / 2, span / 2).Anchor();
                var boundaries = Boundaries(range);
                var origin = new ScaleOrigin(range.Origin, 0);
                var points = new M4Point[M4.MaxPointCount(Width)];
                var result = x is UniformColumn u
                    ? M4.Extract(u, pyramid, origin, boundaries, points)
                    : M4.Extract((Column<long>)x, pyramid, origin, boundaries, points);

                var differences = xValues.Select(v => (double)((Int128)v - range.Origin)).ToArray();
                Assert.False(result.IsProvisional);
                Assert.Equal(Reference.M4(differences, y, boundaries, M4Gaps.Break), Points(points, result));
                views++;
                if (span <= 1)
                {
                    var absolute = boundaries.Select(b => range.Origin + b).ToArray();
                    Assert.False(
                        absolute.Zip(absolute.Skip(1)).All(p => p.First < p.Second),
                        "Absolute doubles increase: the view is not deep enough."
                    );
                    break;
                }

                span = Math.Max(1, span / 16);
            }

            Assert.InRange(views, 6, 9);
        }
    }

    /// <summary>
    /// A long Y near 2026's ticks that moves by 1 is written as exact differences from the Y origin, and a double X near 6.4e17
    /// is written as (double)x − (double)origin, whose origin a double cannot hold (design §3.6, §5.5.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A Column&lt;long&gt; Y of 2026's ticks plus -1, 0, or 1 at random over an evenly spaced X; a Column&lt;double&gt; X of
    ///   6.39288288e17 + 256 i with a float Y.
    ///
    /// Steps:
    /// - Extract the first with the Y origin 2026's ticks; extract the second with the X origin 2026's ticks + 1, over a view
    ///   of 2,000 elements.
    ///
    /// Expected:
    /// - Each equals the brute force over the same formulas: Y as exact differences (-1, 0, 1), X as (double)x − (double)o;
    ///   the first has points with Y of -1 and 1, which a double of the ticks themselves could not tell apart.
    /// </remarks>
    [Fact]
    public void Extract_LongYAndDoubleXFarFromZero_FollowTheirFormulas()
    {
        var random = new Random(67);
        var yLongs = Enumerable.Range(0, Length).Select(_ => Ticks2026 + random.Next(-1, 2)).ToArray();
        using var pool = new PlotWorkerPool(4);
        using var yColumn = Column.Wrap(yLongs);
        using var pyramid = MinMaxPyramidTests.Build(pool, yColumn);
        var x = Column.Uniform(0, 1, Length);
        var boundaries = Enumerable.Range(0, Width + 1).Select(k => 1000 + (k * 100.0)).ToArray();
        var points = new M4Point[M4.MaxPointCount(Width)];
        var result = M4.Extract(x, pyramid, new ScaleOrigin(0, Ticks2026), boundaries, points);

        var xValues = Enumerable.Range(0, Length).Select(i => (double)i).ToArray();
        var yDifferences = yLongs.Select(v => (double)(v - Ticks2026)).ToArray();
        var extracted = Points(points, result);
        Assert.Equal(Reference.M4(xValues, yDifferences, boundaries, M4Gaps.Break), extracted);
        Assert.Contains(extracted, p => p.Y == -1);
        Assert.Contains(extracted, p => p.Y == 1);

        var far = Enumerable.Range(0, Length).Select(i => 6.39288288e17 + (256.0 * i)).ToArray();
        using var farColumn = Column.Wrap(far);
        Eventually.Completes(pool.PrecomputeStats(farColumn));
        using var floats = Column.Wrap(Enumerable.Range(0, Length).Select(_ => (float)random.NextDouble()).ToArray());
        using var floatPyramid = MinMaxPyramidTests.Build(pool, floats);
        const long origin = Ticks2026 + 1;
        var view = Enumerable.Range(0, Width + 1).Select(k => (256.0 * 500_000) + (k * (256.0 * 2000 / Width))).ToArray();
        result = M4.Extract(farColumn, floatPyramid, new ScaleOrigin(origin, 0), view, points);

        var farDifferences = far.Select(v => v - (double)origin).ToArray();
        var floatValues = Enumerable.Range(0, Length).Select(i => (double)floats.GetValue(i)).ToArray();
        Assert.Equal(Reference.M4(farDifferences, floatValues, view, M4Gaps.Break), Points(points, result));
    }

    /// <summary>The pool extracts each series in the batch's origins as M4.Extract does alone (design §5.5.1).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A batch of a Column&lt;long&gt; X of 2026's ticks and an integer uniform X, over the same float Y.
    ///
    /// Steps:
    /// - Extract the batch on the pool with an origin near 2026's ticks over a view of 10,000 ticks; extract each series alone.
    ///
    /// Expected:
    /// - Each series' points and result equal those of M4.Extract with the same origin and boundaries.
    /// </remarks>
    [Fact]
    public void PoolExtract_WithOrigin_EqualsEachSeriesAlone()
    {
        var random = new Random(71);
        using var pool = new PlotWorkerPool(2);
        using var y = Column.Wrap(Enumerable.Range(0, Length).Select(_ => (float)random.NextDouble()).ToArray());
        using var pyramid = MinMaxPyramidTests.Build(pool, y);
        using var ticks = Column.Wrap(Enumerable.Range(0, Length).Select(i => Ticks2026 + (10L * i)).ToArray());
        Eventually.Completes(pool.PrecomputeStats(ticks));
        var uniform = Column.UniformInteger(Ticks2026 + 3, 10, Length);
        var batch = new M4Batch();
        M4Point[][] outputs = [new M4Point[M4.MaxPointCount(Width)], new M4Point[M4.MaxPointCount(Width)]];
        _ = batch.Add(ticks, pyramid, outputs[0]);
        _ = batch.Add(uniform, pyramid, outputs[1]);
        var origin = new ScaleOrigin(Ticks2026 + 5_000_000, 0);
        var boundaries = Enumerable.Range(0, Width + 1).Select(k => 0.5 + (k * 10_000.0 / Width)).ToArray();

        pool.Extract(batch, origin, boundaries);

        var alone = new M4Point[M4.MaxPointCount(Width)];
        var first = M4.Extract(ticks, pyramid, origin, boundaries, alone);
        Assert.Equal(Points(alone, first), Points(outputs[0], batch.GetResult(0)));
        var second = M4.Extract(uniform, pyramid, origin, boundaries, alone);
        Assert.Equal(Points(alone, second), Points(outputs[1], batch.GetResult(1)));
        Assert.True(first.Count > 500);
    }

    private static double[] Boundaries(ScaleRange range)
    {
        var boundaries = new double[Width + 1];
        for (var k = 0; k <= Width; k++)
        {
            boundaries[k] = range.Lower + ((range.Upper - range.Lower) * k / Width);
            if (k > 0)
            {
                Assert.True(boundaries[k - 1] < boundaries[k], $"The boundaries of {range} do not increase.");
            }
        }

        return boundaries;
    }

    private static Reference.Point[] Points(M4Point[] points, M4Result result) =>
        [.. points.AsSpan(0, result.Count).ToArray().Select(p => new Reference.Point(p.Index, p.X, p.Y, p.Roles, p.BreakBefore))];
}
