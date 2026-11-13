using System.Numerics;
using HyperPlot.Interaction;
using HyperPlot.Testing;

namespace HyperPlot.Tests;

/// <summary>
/// The hit test of a point layer on the index path (design §6.3 "散布の索引の道", §10 R3c row, Issue #471): its raw points
/// by the search over its index, and its density's bin under the cursor.
/// </summary>
[Collection("Large data")]
public sealed class IndexedPointHitTests
{
    private const int Over = LayerPlan.MaxElements + 1;
    private const int Dense = 400_000;
    private static readonly PanelId Panel = new(0, 0);

    /// <summary>
    /// A layer of raw points on the index path answers, in both modes, a point within half a pixel of the nearest a loop
    /// finds, with its row's values, a count of 1, and the same point for the same cursor (Issue #471 Decision 21).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 131,073 seeded points over [0, 100]², drawn as raw points of 1 DIP at the view [40, 45]², 640 × 360, every page
    ///   sent.
    ///
    /// Steps:
    /// - At 60 cursors over the panel, hit test with a radius of 20 pixels in XY and in X, twice each.
    /// - Find by a loop over every point its pixel by the panel's linear map, and the least distance (along alone in X).
    ///
    /// Expected:
    /// - A hit is found when the loop's least distance is below the reach less half a pixel, and only within the reach.
    /// - A hit's distance (along alone in X) is at most the least plus half a pixel; its x and y are its row's values; its
    ///   count is 1, its row is not −1, it is not provisional; the second call answers the same row.
    /// </remarks>
    [Fact]
    public void RawPoints_AnswerWithinHalfAPixelOfTheNearest()
    {
        var random = new Random(11);
        var xs = Enumerable.Range(0, Over).Select(_ => random.NextDouble() * 100).ToArray();
        var ys = Enumerable.Range(0, Over).Select(_ => random.NextDouble() * 100).ToArray();
        using var x = Column.Wrap(xs);
        using var y = Column.Wrap(ys);
        var table = DataTable.Create().Add("x", x).Add("y", y).Build();
        var backend = new NullBackend();
        using var engine = Drawn(
            backend,
            Plot.Create(table).Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Draw(PointDraw.Points).Size(1)),
            (40, 45, 40, 45)
        );
        Assert.False(((IndexedPointPlan)engine.Plan!.Layers[0]).DrewDensity);
        var origin = Pixel(engine, 40, 40);
        var perUnit = (Pixel(engine, 45, 45) - origin) / 5;
        var hits = new SeriesHit[2];
        const float Radius = 20;
        var marker = 0.5 * engine.Scale;

        for (var k = 0; k < 60; k++)
        {
            var cursor = Pixel(engine, 40 + (5 * ((k % 10) + 0.5) / 10), 40 + (5 * ((k / 10) + 0.5) / 6));
            foreach (var mode in (HitMode[])[HitMode.XY, HitMode.X])
            {
                var least = double.PositiveInfinity;
                for (var i = 0; i < Over; i++)
                {
                    var (along, across) = (origin.X + ((xs[i] - 40) * perUnit.X), origin.Y + ((ys[i] - 40) * perUnit.Y));
                    least = Math.Min(least, Measure(mode, cursor, along, across));
                }

                var reach = mode == HitMode.XY ? Radius + marker : Radius;
                var found = hits[..engine.HitTest(cursor, Radius, mode, hits)];
                if (found.Length == 0)
                {
                    Assert.True(least >= reach - 0.5 - 1e-3, $"{mode} at {cursor}: nothing found, the least distance {least}");
                    continue;
                }

                var hit = Assert.Single(found);
                Assert.Equal((1L, false), (hit.Count, hit.Provisional));
                Assert.InRange(hit.Row, 0, Over - 1);
                Assert.Equal(xs[hit.Row], hit.X, 9);
                Assert.Equal(ys[hit.Row], hit.Y, 9);
                var distance = Measure(mode, cursor, origin.X + ((hit.X - 40) * perUnit.X), origin.Y + ((hit.Y - 40) * perUnit.Y));

                // 1e-3 pixels for the floats of the drawn position against the loop's doubles.
                Assert.True(distance <= least + 0.5 + 1e-3, $"{mode} at {cursor}: {distance} against the least {least}");
                Assert.Equal(hit.Row, hits[..engine.HitTest(cursor, Radius, mode, hits)][0].Row);
            }
        }
    }

    /// <summary>
    /// A layer drawn as a density answers with the bin drawn under the cursor: a row of −1, the bin's center, a distance of
    /// 0, and the bin's points as a loop counts them; nothing outside the data's range (design §6.3 "密度で描いている層",
    /// Issue #471 Decisions 2 and 10).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 400,000 seeded points over [0, 100]² drawn as a density at the view [−50, 150]², 640 × 360.
    ///
    /// Steps:
    /// - At 30 cursors over [0, 100]², hit test in XY with a radius of 8 and in X with a radius of 0.
    /// - Find the bin the recording draws there by the design's rule: the cell of the chosen level that holds the cursor, its
    ///   own tile or its deepest ancestor's, whose bins are cells 8 levels deeper; count by a loop the points whose grid
    ///   cells lie in that bin.
    /// - Hit test at a cursor over x = −25, outside the data.
    ///
    /// Expected:
    /// - Both modes: one hit of row −1, distance 0, the bin's center as x and y, and the loop's count.
    /// - Outside the data: no hit.
    /// </remarks>
    [Fact]
    public void Density_AnswersWithTheBinUnderTheCursor()
    {
        var (xs, ys, table) = Uniform(Dense, seed: 12);
        var backend = new NullBackend();
        using var engine = Drawn(
            backend,
            Plot.Create(table).Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Draw(PointDraw.Density)),
            (-50, 150, -50, 150)
        );
        var plan = (IndexedPointPlan)engine.Plan!.Layers[0];
        Assert.True(plan.DrewDensity);
        var snapshot = plan.SpatialIndex!.Acquire();
        try
        {
            var (ox, oy) = (snapshot.XRange.Origin, snapshot.YRange.Origin);
            var grid = new (uint X, uint Y)[xs.Length];
            for (var i = 0; i < xs.Length; i++)
            {
                grid[i] = snapshot.GridAt(xs[i] - ox, ys[i] - oy);
            }

            var hits = new SeriesHit[2];
            var random = new Random(13);
            for (var k = 0; k < 30; k++)
            {
                var (vx, vy) = (random.NextDouble() * 100, random.NextDouble() * 100);
                var (gx, gy) = snapshot.GridAt(vx - ox, vy - oy);
                var level = BinLevel(snapshot, gx, gy, plan.TileLevel);
                var shift = 32 - level;
                long count = 0;
                foreach (var (px, py) in grid)
                {
                    count += px >> shift == gx >> shift && py >> shift == gy >> shift ? 1 : 0;
                }

                var (cornerX, cornerY, extentX, extentY) = snapshot.CellBox(SpatialCode.Interleave(gx >> shift, gy >> shift), level);
                var cursor = Pixel(engine, vx, vy);
                foreach (var (mode, radius) in (ReadOnlySpan<(HitMode, float)>)[(HitMode.XY, 8f), (HitMode.X, 0f)])
                {
                    var hit = Assert.Single(hits[..engine.HitTest(cursor, radius, mode, hits)]);
                    Assert.Equal((-1L, 0f, count), (hit.Row, hit.Distance, hit.Count));
                    Assert.Equal(ox + cornerX + (extentX / 2), hit.X, 9);
                    Assert.Equal(oy + cornerY + (extentY / 2), hit.Y, 9);
                }
            }

            Assert.Equal(0, engine.HitTest(Pixel(engine, -25, 50), 8, HitMode.XY, hits));
        }
        finally
        {
            snapshot.Release();
        }
    }

    /// <summary>
    /// A point of another layer comes before a density's bin, and the hover's tooltip names both, the bin with "n =" and its
    /// points as a whole number (Issue #471 Decisions 10 and 15).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 400,000 seeded points over [0, 100]² drawn as a density (layer 0), and a line along y = 50 (layer 1), at the view
    ///   [0, 100]², 640 × 360, the default hover.
    ///
    /// Steps:
    /// - Hit test in XY with a radius of 8 on the line at x = 30.
    /// - Hover there and draw.
    ///
    /// Expected:
    /// - Two hits: the line's (layer 1) first, then the bin (layer 0, row −1).
    /// - The tooltip's text holds ", n = " and the bin's count.
    /// </remarks>
    [Fact]
    public void PointsComeBeforeBins_AndTheTooltipCountsTheBin()
    {
        var (_, _, table) = Uniform(Dense, seed: 14);
        using var lineX = Column.Wrap(Enumerable.Range(0, Dense).Select(i => i * 100.0 / Dense).ToArray());
        using var lineY = Column.Wrap(Enumerable.Repeat(50.0, Dense).ToArray());
        var both = DataTable.Create()
            .Add("x", table.Col<double>("x").Column)
            .Add("y", table.Col<double>("y").Column)
            .Add("lx", lineX)
            .Add("ly", lineY)
            .Build();
        var backend = new NullBackend();
        using var engine = Drawn(
            backend,
            Plot.Create(both)
                .Point(both.Col<double>("x"), both.Col<double>("y"), p => p.Draw(PointDraw.Density))
                .Line(both.Col<double>("lx"), both.Col<double>("ly")),
            (0, 100, 0, 100)
        );
        var cursor = Pixel(engine, 30, 50);
        var hits = new SeriesHit[4];
        var found = hits[..engine.HitTest(cursor, 8, HitMode.XY, hits)];

        Assert.Equal(2, found.Length);
        Assert.Equal(1, found[0].Layer);
        Assert.True(found[0].Row >= 0);
        Assert.Equal((0, -1L), (found[1].Layer, found[1].Row));

        engine.OnPointer(new PointerEvent(PointerEventKind.Move, cursor, PointerButtons.None, PointerButtons.None, ModifierKeys.None, PointerKind.Mouse, 0));
        engine.RenderFrame();
        var count = engine.Hover.ToArray().Single(h => h.Row < 0).Count;
        Assert.Contains(backend.LastList!.Texts.ToArray(), t => t.Text.Contains($", n = {count}", StringComparison.Ordinal));
    }

    /// <summary>A warm hit test of a layer on the index path, of raw points and of a density, allocates nothing (invariant 3).</summary>
    /// <remarks>
    /// Preconditions:
    /// - 400,000 seeded points over [0, 100]², Auto with the default threshold: raw points at the view [40, 41]², a density
    ///   at the whole view; each view hit tested once.
    ///
    /// Steps:
    /// - At each view, 20 hit tests in XY and in X while counting the thread's allocations, after a warm-up.
    ///
    /// Expected:
    /// - 0 bytes at each view.
    /// </remarks>
    [Fact]
    public void HitTest_OnTheIndexPath_AllocatesNothing()
    {
        var (_, _, table) = Uniform(Dense, seed: 15);
        var backend = new NullBackend();
        using var engine = Drawn(
            backend,
            Plot.Create(table).Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Density(threshold: 100_000)),
            (40, 41, 40, 41)
        );
        var hits = new SeriesHit[2];
        Assert.False(((IndexedPointPlan)engine.Plan!.Layers[0]).DrewDensity);
        HitsAt(Pixel(engine, 40.5, 40.5));

        Show(engine, backend, (0, 100, 0, 100));
        Assert.True(((IndexedPointPlan)engine.Plan!.Layers[0]).DrewDensity);
        HitsAt(Pixel(engine, 50, 50));

        void HitsAt(Vector2 cursor) =>
            PerformanceAssert.DoesNotAllocate(() =>
            {
                for (var i = 0; i < 20; i++)
                {
                    _ = engine.HitTest(cursor, 8, HitMode.XY, hits);
                    _ = engine.HitTest(cursor, 8, HitMode.X, hits);
                }
            });
    }

    // The level of the bin the recording draws over a grid cell (design §5.5.2 "水準の選び方"): 8 below the cell's own tile
    // at the chosen level or its deepest ancestor's.
    private static int BinLevel(SpatialSnapshot snapshot, uint gx, uint gy, int chosen)
    {
        var tiles = snapshot.Tiles!;
        for (var at = chosen; at >= 0; at--)
        {
            var shift = 32 - at;
            var code = at == 0 ? 0 : SpatialCode.Interleave(gx >> shift, gy >> shift);
            if (tiles.TryGetTile(code, at, out _))
            {
                return at + DensityTiles.BinLevels;
            }
        }

        Assert.Fail("No tile above the cursor: the data is meant to draw from tiles.");
        return 0;
    }

    private static double Measure(HitMode mode, Vector2 cursor, double along, double across)
    {
        var da = along - cursor.X;
        var dc = mode == HitMode.XY ? across - cursor.Y : 0;
        return Math.Sqrt((da * da) + (dc * dc));
    }

    private static (double[] X, double[] Y, DataTable Table) Uniform(int count, int seed)
    {
        var random = new Random(seed);
        var xs = Enumerable.Range(0, count).Select(_ => random.NextDouble() * 100).ToArray();
        var ys = Enumerable.Range(0, count).Select(_ => random.NextDouble() * 100).ToArray();
        return (xs, ys, DataTable.Create().Add("x", Column.Wrap(xs)).Add("y", Column.Wrap(ys)).Build());
    }

    private static PlotEngine Drawn(NullBackend backend, PlotBuilder builder, (double X0, double X1, double Y0, double Y1) view)
    {
        var engine = new PlotEngine(backend, 640, 360) { WaitsForIndexes = true };
        engine.SetPlot(builder.ScaleX(Scale.Linear().WithDomain(0, 100)).ScaleY(Scale.Linear().WithDomain(0, 100)).Build());
        engine.RenderFrame();
        Show(engine, backend, view);
        return engine;
    }

    // Sets a view and draws until every page and face is on the GPU.
    private static void Show(PlotEngine engine, NullBackend backend, (double X0, double X1, double Y0, double Y1) view)
    {
        engine.Views.SetView(Panel, new PanelView2D(ViewRange.FromValues(view.X0, view.X1), ViewRange.FromValues(view.Y0, view.Y1)));
        for (var i = 0; i < 200 && (i == 0 || engine.IsProvisional); i++)
        {
            backend.Images.Sent();
            engine.RenderFrame();
        }
    }

    private static Vector2 Pixel(PlotEngine engine, double x, double y) =>
        engine.ViewToPixel(Panel, ViewRange.FromValues(x, x), ViewRange.FromValues(y, y));
}
