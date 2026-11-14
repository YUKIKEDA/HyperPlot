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

    /// <summary>The kinds of bins a density draws (design §5.5.2 "水準の選び方").</summary>
    public enum BinKind
    {
        /// <summary>A bin of the cell's own tile at the chosen level.</summary>
        Own,

        /// <summary>A bin of the tile of the cell's deepest ancestor that has one.</summary>
        Ancestor,

        /// <summary>A cell with no tile above it, drawn as one fill.</summary>
        Fill,
    }

    /// <summary>
    /// A layer drawn as a density answers with the bin drawn under the cursor, of each kind (its own tile's, an ancestor's,
    /// a fill): a row of −1, the bin's center, a distance of 0, and the bin's points as a loop counts them, before their
    /// normalization; nothing outside the data's range (design §6.3 "密度で描いている層", Issue #471 Decisions 2 and 10).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Seeded points over [0, 100]² drawn as a density, 640 × 360: 131,073 points at the view [0, 100]² (level 0, and no
    ///   cell has more points than the density split of 262,144, so none has a tile: fills); 400,000 at [25, 75]² (the
    ///   panel's height of about 316 pixels spans half the range: level 1, whose cells of about 100,000 points have no tile:
    ///   the root's); 400,000 at [−50, 150]² (level 0: the root's own tile).
    ///
    /// Steps:
    /// - At 30 cursors over the view's part of [0, 100]², hit test in XY with a radius of 8 and in X with a radius of 0.
    /// - Find the bin the recording draws there by the design's rule: the cell of the chosen level that holds the cursor (or
    ///   the leaf above it), its own tile or its deepest ancestor's, whose bins are cells 8 levels deeper, or the cell itself
    ///   as a fill; count by a loop the points whose grid cells lie in that bin.
    /// - At the wider view, hit test at a cursor over x = −25, outside the data.
    ///
    /// Expected:
    /// - Both modes: one hit of row −1, distance 0, the bin's center as x and y, and the loop's count; the kind of the case
    ///   is among the bins met.
    /// - Outside the data: no hit.
    /// </remarks>
    [Theory]
    [InlineData(Over, 0.0, 100.0, BinKind.Fill)]
    [InlineData(Dense, 25.0, 75.0, BinKind.Ancestor)]
    [InlineData(Dense, -50.0, 150.0, BinKind.Own)]
    public void Density_AnswersWithTheBinUnderTheCursor(int points, double low, double high, BinKind kind)
    {
        var (xs, ys, table) = Uniform(points, seed: 12);
        var backend = new NullBackend();
        using var engine = Drawn(
            backend,
            Plot.Create(table).Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Draw(PointDraw.Density)),
            (low, high, low, high)
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
            var kinds = new HashSet<BinKind>();
            var (from, to) = (Math.Max(low, 0), Math.Min(high, 100));
            for (var k = 0; k < 30; k++)
            {
                var (vx, vy) = (from + (random.NextDouble() * (to - from)), from + (random.NextDouble() * (to - from)));
                var (level, met) = DrawnBin(snapshot, vx - ox, vy - oy, plan.TileLevel);
                kinds.Add(met);
                var (gx, gy) = snapshot.GridAt(vx - ox, vy - oy);
                var count = CountIn(grid, gx, gy, level);
                var code = level == 0 ? 0 : SpatialCode.Interleave(gx >> (32 - level), gy >> (32 - level));
                var (cornerX, cornerY, extentX, extentY) = snapshot.CellBox(code, level);
                var cursor = Pixel(engine, vx, vy);
                foreach (var (mode, radius) in (ReadOnlySpan<(HitMode, float)>)[(HitMode.XY, 8f), (HitMode.X, 0f)])
                {
                    var hit = Assert.Single(hits[..engine.HitTest(cursor, radius, mode, hits)]);
                    Assert.Equal((-1L, 0f, count), (hit.Row, hit.Distance, hit.Count));
                    Assert.Equal(ox + cornerX + (extentX / 2), hit.X, 9);
                    Assert.Equal(oy + cornerY + (extentY / 2), hit.Y, 9);
                }
            }

            Assert.Contains(kind, kinds);
            if (low < -25)
            {
                Assert.Equal(0, engine.HitTest(Pixel(engine, -25, 50), 8, HitMode.XY, hits));
            }
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
        var both = DataTable
            .Create()
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

        engine.OnPointer(
            new PointerEvent(PointerEventKind.Move, cursor, PointerButtons.None, PointerButtons.None, ModifierKeys.None, PointerKind.Mouse, 0)
        );
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

    /// <summary>
    /// A hover over a density marks the bin with its frame, in the target's pixels of the bin's box (Issue #471 Decision 10).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 400,000 seeded points over [0, 100]² drawn as a density at the view [0, 100]², 640 × 360, the default hover.
    ///
    /// Steps:
    /// - Hover at (30, 60) and draw. Find the bin's box by the design's rule and map its corners to pixels.
    ///
    /// Expected:
    /// - The recording holds the frame's top side: a rectangle at the box's left and top, as wide as the box (within a pixel
    ///   for the rounding of the corners), one line high; and its left side, as high as the box.
    /// </remarks>
    [Fact]
    public void Hover_MarksABinWithItsFrame()
    {
        var (_, _, table) = Uniform(Dense, seed: 16);
        var backend = new NullBackend();
        using var engine = Drawn(
            backend,
            Plot.Create(table).Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Draw(PointDraw.Density)),
            (0, 100, 0, 100)
        );
        var plan = (IndexedPointPlan)engine.Plan!.Layers[0];
        var cursor = Pixel(engine, 30, 60);
        engine.OnPointer(
            new PointerEvent(PointerEventKind.Move, cursor, PointerButtons.None, PointerButtons.None, ModifierKeys.None, PointerKind.Mouse, 0)
        );
        engine.RenderFrame();

        var snapshot = plan.SpatialIndex!.Acquire();
        try
        {
            var (ox, oy) = (snapshot.XRange.Origin, snapshot.YRange.Origin);
            var (level, _) = DrawnBin(snapshot, 30 - ox, 60 - oy, plan.TileLevel);
            var (gx, gy) = snapshot.GridAt(30 - ox, 60 - oy);
            var code = level == 0 ? 0 : SpatialCode.Interleave(gx >> (32 - level), gy >> (32 - level));
            var (cornerX, cornerY, extentX, extentY) = snapshot.CellBox(code, level);
            var low = Pixel(engine, ox + cornerX, oy + cornerY);
            var high = Pixel(engine, ox + cornerX + extentX, oy + cornerY + extentY);
            var (left, top, width, height) = (Math.Min(low.X, high.X), Math.Min(low.Y, high.Y), Math.Abs(high.X - low.X), Math.Abs(high.Y - low.Y));
            var line = MathF.Max(1, MathF.Round(engine.Scale));
            var rects = backend.LastList!.Rects.ToArray();
            Assert.Contains(rects, r => Near(r.X, left) && Near(r.Y, top) && Near(r.Width, width) && r.Height == line);
            Assert.Contains(rects, r => Near(r.X, left) && Near(r.Y, top) && Near(r.Height, height) && r.Width == line);
        }
        finally
        {
            snapshot.Release();
        }

        static bool Near(float a, float b) => Math.Abs(a - b) < 1;
    }

    /// <summary>
    /// A click of the selection of points on a density's bin, where no point answers, selects the range of the bin drawn there
    /// as a rectangle: a bin of the chosen level's own tile, an ancestor's larger bin, or a leaf's fill whole, so that the
    /// range changes with the zoom (design §6.4 "密度のクリック", Issue #471 Decisions 9 and 17). A click on a point of another
    /// layer over the density selects that point alone.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 1,100,000 seeded points over [0, 100]² drawn as a density (layer 0), and a line along y = 50 (layer 1), at the view
    ///   [0, 100]², 640 × 360, the default bindings (Shift and a click select points). Each cell of level 1 holds about
    ///   275,000 points, past the density split of 262,144, so it has a tile of its own.
    ///
    /// Steps:
    /// - Shift-click at (30, 70), where only the density answers. Find the bin drawn there by the design's rule: of the
    ///   root's tile (level 0, bins of level 8).
    /// - Zoom to the view [25, 75]² (tile level 1) and shift-click at (30, 70) again; find the bin drawn there: of its
    ///   level-1 cell's own tile (bins of level 9).
    /// - Shift-click at (30, 50), on the line.
    ///
    /// Expected:
    /// - After each click on the density: a rectangle on the panel whose x and y ranges, as values, are the bin's box. The
    ///   two bins are of other levels, so the ranges differ.
    /// - After the click on the line: a selection of points with one point, the line's (layer 1, a row of 0 or more).
    /// </remarks>
    [Fact]
    public void PointsClick_OnADensitysBin_SelectsTheBinsRange()
    {
        var backend = new NullBackend();
        using var engine = LineOverDensity(backend, seed: 17, count: 1_100_000);
        var plan = (IndexedPointPlan)engine.Plan!.Layers[0];
        var hits = new SeriesHit[4];
        var time = 0L;
        Assert.Equal(-1, Assert.Single(hits[..engine.HitTest(Pixel(engine, 30, 70), 8, HitMode.XY, hits)]).Row);

        var wide = ClickBin();
        Show(engine, backend, (25, 75, 25, 75));
        var narrow = ClickBin();
        Assert.Equal((8, 9), (wide, narrow));

        Click(Pixel(engine, 30, 50));
        var selection = Assert.IsType<Selection2D>(engine.Selection(0));
        Assert.Equal(SelectionKind.Points, selection.Kind);
        var point = Assert.Single(selection.Points.ToArray());
        Assert.Equal(1, point.Layer);
        Assert.True(point.Row >= 0);

        // Clicks the density at (30, 70) and checks the rectangle against the bin drawn there; returns the bin's level.
        int ClickBin()
        {
            Click(Pixel(engine, 30, 70));
            var rectangle = Assert.IsType<Selection2D>(engine.Selection(0));
            Assert.Equal((SelectionKind.Rectangle, Panel), (rectangle.Kind, rectangle.Panel));
            var snapshot = plan.SpatialIndex!.Acquire();
            try
            {
                var (ox, oy) = (snapshot.XRange.Origin, snapshot.YRange.Origin);
                var (level, _) = DrawnBin(snapshot, 30 - ox, 70 - oy, plan.TileLevel);
                var (gx, gy) = snapshot.GridAt(30 - ox, 70 - oy);
                var code = level == 0 ? 0 : SpatialCode.Interleave(gx >> (32 - level), gy >> (32 - level));
                var (cornerX, cornerY, extentX, extentY) = snapshot.CellBox(code, level);
                Assert.Equal(ox + cornerX, rectangle.X.LowValue, 9);
                Assert.Equal(ox + cornerX + extentX, rectangle.X.HighValue, 9);
                Assert.Equal(oy + cornerY, rectangle.Y.LowValue, 9);
                Assert.Equal(oy + cornerY + extentY, rectangle.Y.HighValue, 9);
                return level;
            }
            finally
            {
                snapshot.Release();
            }
        }

        // Ten seconds apart, so that no two clicks make a double click.
        void Click(Vector2 at)
        {
            time += 10 * System.Diagnostics.Stopwatch.Frequency;
            engine.OnPointer(
                new PointerEvent(PointerEventKind.Down, at, PointerButtons.Left, PointerButtons.Left, ModifierKeys.Shift, PointerKind.Mouse, time)
            );
            engine.OnPointer(
                new PointerEvent(PointerEventKind.Up, at, PointerButtons.Left, PointerButtons.None, ModifierKeys.Shift, PointerKind.Mouse, time)
            );
        }
    }

    /// <summary>
    /// In the switch's band a hit answers with the drawing the last recording chose (Issue #471 Decision 11): bins when the
    /// layer came into the band as a density, points when it came in as raw points.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 131,073 seeded points over [0, 100]², Auto with a threshold of 10,000, an engine that waits for its indexes. The
    ///   leaves are the cells of level 3, 12.5 wide, of about 2,048 points each (of 4,096 at most), and the upper count
    ///   counts a leaf the view touches whole.
    /// - Views centered on (53, 53), off the leaves' corners: a side under 6 touches one leaf (about 2,048, under the
    ///   band), a side from 6 to 19 four (about 8,200, in it), a wider one nine or more (above it). A view centered on a
    ///   corner of four leaves would never fall under the band.
    ///
    /// Steps:
    /// - Zoom in from a side of 100 to a side of 2 in 200 steps, then out again, drawing each view. At each view whose
    ///   upper count lies between 8,000 and 10,000, hit test the view's middle in XY with a radius of 40 pixels.
    ///
    /// Expected:
    /// - A view in the band drawn as a density answers with a bin (row −1); one drawn as raw points answers with rows of 0
    ///   or more. Both kinds of view are met.
    /// </remarks>
    [Fact]
    public void SwitchBand_AnswersWithTheLastDrawing()
    {
        const double Center = 53;
        var (_, _, table) = Uniform(Over, seed: 18);
        var backend = new NullBackend();
        using var engine = Drawn(
            backend,
            Plot.Create(table).Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Density(threshold: 10_000)),
            (0, 100, 0, 100)
        );
        var plan = (IndexedPointPlan)engine.Plan!.Layers[0];
        var hits = new SeriesHit[4];
        var sides = Enumerable.Range(0, 201).Select(i => 100 * Math.Pow(0.02, i / 200.0)).ToArray();
        var (asDensity, asPoints) = (false, false);

        foreach (var side in sides.Concat(sides.Reverse()))
        {
            Show(engine, backend, (Center - (side / 2), Center + (side / 2), Center - (side / 2), Center + (side / 2)));
            var count = plan.Switch.LastCount;
            if (count < 10_000 * DensitySwitch.Back || count > 10_000)
            {
                continue;
            }

            var found = hits[..engine.HitTest(Pixel(engine, Center, Center), 40, HitMode.XY, hits)];
            if (found.Length == 0)
            {
                continue;
            }

            var hit = Assert.Single(found);
            if (plan.DrewDensity)
            {
                Assert.Equal(-1, hit.Row);
                asDensity = true;
            }
            else
            {
                Assert.True(hit.Row >= 0);
                asPoints = true;
            }
        }

        Assert.True(asDensity && asPoints, $"The band was met as a density: {asDensity}; as raw points: {asPoints}.");
    }

    /// <summary>
    /// While a changed column's index is built again, the layer answers from the last full snapshot it draws, provisionally
    /// (Issue #471 Decision 5); once the new one is drawn, not provisionally.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 131,073 points on whole numbers (x = i mod 100, y = i mod 97), raw points, on a pool of 1 worker; the x column's
    ///   storage holds the reads of chunk 0. Drawn until not provisional.
    ///
    /// Steps:
    /// - Hold chunk 0, invalidate the x column, draw a frame, and hit test in XY at (50, 50) with a radius of 8.
    /// - Release the chunk, draw until not provisional, and hit test again.
    ///
    /// Expected:
    /// - During the build: one hit at (50, 50), provisional, of a row whose x is 50 and y 50.
    /// - After it: the same point, not provisional.
    /// </remarks>
    [Fact]
    public void Rebuild_AnswersFromTheLastFullSnapshot_Provisionally()
    {
        using var xs = TestStorage<double>.MakeColumn(Enumerable.Range(0, Over).Select(i => (double)(i % 100)).ToArray(), out var storage);
        using var ys = Column.Wrap(Enumerable.Range(0, Over).Select(i => (double)(i % 97)).ToArray());
        var table = DataTable.Create().Add("x", xs).Add("y", ys).Build();
        var backend = new NullBackend();
        using var pool = new PlotWorkerPool(1);
        using var engine = new PlotEngine(backend, 640, 360, pool: pool);
        engine.SetPlot(
            Plot.Create(table)
                .Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Draw(PointDraw.Points))
                .ScaleX(Scale.Linear().WithDomain(0, 100))
                .ScaleY(Scale.Linear().WithDomain(0, 100))
                .Build()
        );
        Settle(engine, backend);
        var hits = new SeriesHit[2];
        var at = Pixel(engine, 50, 50);

        storage.Hold(0);
        try
        {
            xs.Invalidate();
            backend.Images.Sent();
            engine.RenderFrame();
            Assert.True(engine.IsProvisional);
            var during = Assert.Single(hits[..engine.HitTest(at, 8, HitMode.XY, hits)]);
            Assert.Equal((50.0, 50.0, true), (during.X, during.Y, during.Provisional));
        }
        finally
        {
            storage.Release(0);
        }

        Settle(engine, backend);
        var after = Assert.Single(hits[..engine.HitTest(at, 8, HitMode.XY, hits)]);
        Assert.Equal((50.0, 50.0, false), (after.X, after.Y, after.Provisional));
    }

    /// <summary>
    /// A point whose page is not on the GPU yet still answers, from the index's copy, and the answer is provisional while the
    /// frame is (Issue #471 Decision 14); once every page is sent, not.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 400,000 seeded points over [0, 100]² drawn as raw points (7 pages), in an engine that keeps the upload budget of a
    ///   frame (one that waits for its indexes sends every page at once).
    ///
    /// Steps:
    /// - Draw a frame, wait for the index, and draw another without sending the first frame's uploads: the two send at most
    ///   5 pages. Hit test in XY at 20 points' pixels with a radius of 8.
    /// - Draw until every page is sent; hit test again.
    ///
    /// Expected:
    /// - The first frame missed pages; each hit test finds a point, provisional.
    /// - After: each finds a point, not provisional.
    /// </remarks>
    [Fact]
    public void PointsNotOnTheGpu_AnswerProvisionally()
    {
        var (xs, ys, table) = Uniform(Dense, seed: 19);
        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, 640, 360);
        engine.SetPlot(
            Plot.Create(table)
                .Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Draw(PointDraw.Points))
                .ScaleX(Scale.Linear().WithDomain(0, 100))
                .ScaleY(Scale.Linear().WithDomain(0, 100))
                .Build()
        );
        engine.RenderFrame();
        var plan = (IndexedPointPlan)engine.Plan!.Layers[0];
        Eventually.Completes(plan.SpatialIndex!.WhenComplete());

        // No Sent since the first frame: the frames share one upload budget of 4 MiB, 5 pages.
        engine.RenderFrame();
        Assert.False(plan.DrewDensity);
        Assert.True(plan.MissedPages);
        var hits = new SeriesHit[2];
        for (var i = 0; i < 20; i++)
        {
            Assert.True(Assert.Single(hits[..engine.HitTest(Pixel(engine, xs[i], ys[i]), 8, HitMode.XY, hits)]).Provisional);
        }

        Show(engine, backend, (0, 100, 0, 100));
        Assert.False(plan.MissedPages);
        for (var i = 0; i < 20; i++)
        {
            Assert.False(Assert.Single(hits[..engine.HitTest(Pixel(engine, xs[i], ys[i]), 8, HitMode.XY, hits)]).Provisional);
        }
    }

    /// <summary>
    /// Past the limit of design §5.5.2, where points are drawn on the units of their resident cell, the distance and the
    /// hover's marker are where the point is drawn, and the x and y its true values (Issue #471 Decision 1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 131,073 points: 130,973 seeded over [0, 10^6]², and 100 in a cluster 10^-3 wide at (500,000.3, 500,000.7); raw
    ///   points; the view the cluster's square, 640 × 360. A resident cell is then about 2.5 × 10^5 wide, so that its unit
    ///   is tens of pixels.
    /// - Each cluster point's drawn pixel found apart from the plan: its resident cell and unit by ReadBlock, the unit's
    ///   value mapped by the engine.
    ///
    /// Steps:
    /// - Hit test in XY with a radius of 8 at the drawn pixel of each cluster point drawn in the panel (a point drawn up to a
    ///   unit below its value may lie left of or below it, where no hit test reaches); read the hover marker's pixel of the
    ///   hit.
    ///
    /// Expected:
    /// - Each finds a point at a distance under 0.01 pixels from its drawn pixel; the marker lies within 0.01 pixels of the
    ///   drawn pixel; the x and y are the row's values.
    /// - Some cluster point is drawn in the panel, and some such point more than a pixel from its value's pixel: the case is
    ///   past the limit.
    /// </remarks>
    [Fact]
    public void DeepZoom_MeasuresWhereThePointsAreDrawn()
    {
        const int Cluster = 100;
        var random = new Random(20);
        var xs = new double[Over];
        var ys = new double[Over];
        for (var i = 0; i < Over; i++)
        {
            (xs[i], ys[i]) =
                i < Over - Cluster
                    ? (random.NextDouble() * 1e6, random.NextDouble() * 1e6)
                    : (500_000.3 + (random.NextDouble() * 1e-3), 500_000.7 + (random.NextDouble() * 1e-3));
        }

        using var x = Column.Wrap(xs);
        using var y = Column.Wrap(ys);
        var table = DataTable.Create().Add("x", x).Add("y", y).Build();
        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, 640, 360) { WaitsForIndexes = true };
        engine.SetPlot(
            Plot.Create(table)
                .Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Draw(PointDraw.Points))
                .ScaleX(Scale.Linear().WithDomain(0, 1e6))
                .ScaleY(Scale.Linear().WithDomain(0, 1e6))
                .Build()
        );
        engine.RenderFrame();
        Show(engine, backend, (500_000.3, 500_000.301, 500_000.7, 500_000.701));
        var plan = (IndexedPointPlan)engine.Plan!.Layers[0];
        var snapshot = plan.SpatialIndex!.Acquire();
        try
        {
            var (px, py, rows) = SpatialReference.ReadAll(snapshot);
            var resident = new ResidentPoint[px.Length];
            snapshot.ReadBlock(0, resident);
            var cells = snapshot.ResidentCells.ToArray();
            var (ox, oy) = (snapshot.XRange.Origin, snapshot.YRange.Origin);
            var hits = new SeriesHit[2];
            var snapped = false;
            var tested = 0;

            // The panel's pixels: the view's lower-left and upper-right corners, y downward.
            var (low, high) = (Pixel(engine, 500_000.3, 500_000.701), Pixel(engine, 500_000.301, 500_000.7));
            for (var position = 0; position < px.Length; position++)
            {
                if (rows[position] < Over - Cluster)
                {
                    continue;
                }

                var (point, cell) = (resident[position], cells[resident[position].Cell]);
                var drawn = Pixel(
                    engine,
                    ox + cell.CornerX + (point.X / SpatialSnapshot.CellUnits * cell.ExtentX),
                    oy + cell.CornerY + (point.Y / SpatialSnapshot.CellUnits * cell.ExtentY)
                );

                // A point drawn a unit below its value may fall outside the panel, where no hit test reaches.
                if (!(drawn.X >= low.X && drawn.X <= high.X && drawn.Y >= low.Y && drawn.Y <= high.Y))
                {
                    continue;
                }

                tested++;
                snapped |= Vector2.Distance(drawn, Pixel(engine, xs[rows[position]], ys[rows[position]])) > 1;
                var hit = Assert.Single(hits[..engine.HitTest(drawn, 8, HitMode.XY, hits)]);
                Assert.True(hit.Distance < 0.01, $"The hit at {drawn} is {hit.Distance} pixels away.");
                Assert.Equal((xs[hit.Row], ys[hit.Row]), (hit.X, hit.Y));
                Assert.True(engine.Plan!.HitPixel(hit, out var marker));
                Assert.True(Vector2.Distance(marker, drawn) < 0.01, $"The marker {marker} is not at {drawn}.");
            }

            Assert.True(tested > 0, "No cluster point is drawn in the panel.");
            Assert.True(snapped, "No cluster point in the panel is drawn a pixel from its value.");
        }
        finally
        {
            snapshot.Release();
        }
    }

    // The bin the recording draws over a point, by the design's rule (design §5.5.2 "水準の選び方"): the cell of the chosen
    // level that holds it, or the leaf above; a bin of its own tile or of its deepest ancestor's, 8 levels deeper; or the
    // cell itself as a fill. Returns the bin's level and its kind.
    private static (int Level, BinKind Kind) DrawnBin(SpatialSnapshot snapshot, double dx, double dy, int chosen)
    {
        Span<SpatialCell> one = stackalloc SpatialCell[1];
        Assert.Equal(1, snapshot.GetCells(new SpatialRect(dx, dx, dy, dy), chosen, one));
        var cell = one[0];
        var tiles = snapshot.Tiles!;
        for (var at = Math.Min(cell.Level, DensityTiles.DeepestTileLevel); at >= 0; at--)
        {
            if (tiles.TryGetTile(cell.Code >> (2 * (cell.Level - at)), at, out _))
            {
                return (at + DensityTiles.BinLevels, at == chosen ? BinKind.Own : BinKind.Ancestor);
            }
        }

        return (cell.Level, BinKind.Fill);
    }

    // The points whose grid cells lie in the cell of a level that holds a grid cell.
    private static long CountIn((uint X, uint Y)[] grid, uint gx, uint gy, int level)
    {
        if (level == 0)
        {
            return grid.Length;
        }

        var shift = 32 - level;
        long count = 0;
        foreach (var (px, py) in grid)
        {
            count += px >> shift == gx >> shift && py >> shift == gy >> shift ? 1 : 0;
        }

        return count;
    }

    private static void Settle(PlotEngine engine, NullBackend backend)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var provisional = true;
        while (provisional)
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), "The frames stayed provisional for 60 s.");
            backend.Images.Sent();
            engine.RenderFrame();
            Thread.Sleep(1);
            provisional = engine.IsProvisional;
        }
    }

    private static PlotEngine LineOverDensity(NullBackend backend, int seed, int count = Dense)
    {
        var (_, _, table) = Uniform(count, seed);
        var lineX = Column.Wrap(Enumerable.Range(0, count).Select(i => i * 100.0 / count).ToArray());
        var lineY = Column.Wrap(Enumerable.Repeat(50.0, count).ToArray());
        var both = DataTable
            .Create()
            .Add("x", table.Col<double>("x").Column)
            .Add("y", table.Col<double>("y").Column)
            .Add("lx", lineX)
            .Add("ly", lineY)
            .Build();
        return Drawn(
            backend,
            Plot.Create(both)
                .Point(both.Col<double>("x"), both.Col<double>("y"), p => p.Draw(PointDraw.Density))
                .Line(both.Col<double>("lx"), both.Col<double>("ly")),
            (0, 100, 0, 100)
        );
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
