using System.Numerics;
using HyperPlot.Interaction;

namespace HyperPlot.Tests;

/// <summary>
/// The engine's hit test (design §6.3 "系列の最近傍", §10 "ヒットテスト（I2a）", requirement I-2, row I2a, Issue #337): each
/// series' row nearest the cursor's x, or its point nearest the cursor within a radius, checked against every row mapped to
/// the screen with <see cref="PlotEngine.ViewToPixel"/>.
/// </summary>
public sealed class HitTestTests
{
    private static readonly PanelId Panel = new(0, 0);

    /// <summary>
    /// <see cref="HitMode.X"/> gives each series the row whose x is nearest the cursor's, over a uniform and a stored x, and
    /// passes the rows whose y is NaN; the radius is not used (Issue #337 Decision 6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 2,000 rows; a line over a uniform x and one over an increasing stored x with uneven steps, both with a y that is NaN
    ///   in rows 500 to 599.
    ///
    /// Steps:
    /// - Draw with an engine that waits; hit test 40 cursors along the panel with a radius of 1 pixel.
    ///
    /// Expected:
    /// - Each cursor gives 2 hits, layers 0 and 1, whose x pixel lies as near the cursor's x as the nearest row with a number
    ///   does (every row mapped to the screen); their y is not NaN; the x and y are the row's values.
    /// </remarks>
    [Fact]
    public void X_FindsTheRowNearestTheCursorsX()
    {
        const int Rows = 2000;
        var stored = new double[Rows];
        for (var i = 1; i < Rows; i++)
        {
            stored[i] = stored[i - 1] + 0.5 + ((i % 7) * 0.25);
        }

        var ys = Enumerable.Range(0, Rows).Select(i => i is >= 500 and < 600 ? double.NaN : Math.Sin(i * 0.01)).ToArray();
        using var x = Column.Wrap(stored);
        using var y = Column.Wrap(ys);
        var table = DataTable.Create().Add("u", Column.Uniform(0, stored[^1] / (Rows - 1), Rows)).Add("x", x).Add("y", y).Build();
        using var engine = Drawn(
            new PlotSpec
            {
                Data = table,
                Layers =
                [
                    new LineLayerSpec { X = table.Col<double>("u"), Y = table.Col<double>("y") },
                    new LineLayerSpec { X = table.Col<double>("x"), Y = table.Col<double>("y") },
                ],
            }
        );
        var hits = new SeriesHit[4];
        var xOf = new Func<int, double>[] { i => i * (stored[^1] / (Rows - 1)), i => stored[i] };
        for (var k = 0; k < 40; k++)
        {
            var cursor = Inside(engine, (k + 0.5) / 40, 0.5);
            Assert.Equal(2, engine.HitTest(cursor, 1, HitMode.X, hits));
            for (var layer = 0; layer < 2; layer++)
            {
                var hit = hits[layer];
                Assert.Equal((0, 0, layer, 0), (hit.Plot, hit.Panel, hit.Layer, hit.Series));
                Assert.False(double.IsNaN(hit.Y));
                Assert.Equal(xOf[layer]((int)hit.Row), hit.X, 9);
                Assert.Equal(ys[hit.Row], hit.Y);
                var nearest = Enumerable.Range(0, Rows).Where(i => !double.IsNaN(ys[i])).Min(i => Math.Abs(PixelX(engine, xOf[layer](i)) - cursor.X));
                Assert.Equal(nearest, Math.Abs(PixelX(engine, hit.X) - cursor.X), 3);
            }
        }
    }

    /// <summary>
    /// <see cref="HitMode.XY"/> gives each series its point nearest the cursor within the radius, within half a pixel of the
    /// nearest that every row's distance on the screen gives, over the whole view and a zoomed one; outside the radius it
    /// answers nothing (Issue #337 Decision 6, Issue #341 Decision 5).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A line of 200,000 rows over a uniform x, y a random walk with a seed.
    ///
    /// Steps:
    /// - Draw with an engine that waits; hit test 30 seeded cursors with a radius of 6 pixels, at the whole view and at a
    ///   view of 1/100 of the x range.
    ///
    /// Expected:
    /// - Where some row lies within 6 pixels, one hit whose distance is at most half a pixel more than the least of every
    ///   row's, and whose row lies at that distance; where none does, no hit.
    /// </remarks>
    [Fact]
    public void XY_FindsThePointNearestTheCursor()
    {
        const int Rows = 200_000;
        var random = new Random(7);
        var ys = new double[Rows];
        for (var i = 1; i < Rows; i++)
        {
            ys[i] = ys[i - 1] + random.NextDouble() - 0.5;
        }

        using var y = Column.Wrap(ys);
        var table = DataTable.Create().Add("x", Column.Uniform(0, 1, Rows)).Add("y", y).Build();
        using var engine = Drawn(
            new PlotSpec { Data = table, Layers = [new LineLayerSpec { X = table.Col<double>("x"), Y = table.Col<double>("y") }] }
        );
        var hits = new SeriesHit[2];
        foreach (var zoomed in (ReadOnlySpan<bool>)[false, true])
        {
            if (zoomed)
            {
                var view = (PanelView2D)engine.CurrentView(Panel);
                var (low, width) = (Rows * 0.4, Rows / 100.0);
                engine.Views.SetView(Panel, view with { X = ViewRange.FromValues(low, low + width) });
                engine.RenderFrame();
            }

            for (var k = 0; k < 30; k++)
            {
                var cursor = Inside(engine, random.NextDouble(), random.NextDouble());
                var found = engine.HitTest(cursor, 6, HitMode.XY, hits);
                var best = double.PositiveInfinity;
                for (var i = 0; i < Rows; i++)
                {
                    best = Math.Min(best, Vector2.Distance(Pixel(engine, i, ys[i]), cursor));
                }

                if (best > 6)
                {
                    Assert.Equal(0, found);
                    continue;
                }

                Assert.Equal(1, found);
                Assert.InRange(hits[0].Distance, best - 1e-3, best + 0.5 + 1e-3);
                Assert.Equal(hits[0].Distance, Vector2.Distance(Pixel(engine, hits[0].Row, ys[hits[0].Row]), cursor), 3);
            }
        }
    }

    /// <summary>
    /// A band answers for its low and high edges; outside a panel there is no hit; a short buffer takes as many hits as fit
    /// and the count is all of them (Issue #337 Decisions 4, 7, 9).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A band and a line over 100 rows.
    ///
    /// Steps:
    /// - Hit test the panel's middle with X into a buffer of 4 and of 1; hit test a pixel left of the panel.
    ///
    /// Expected:
    /// - 3 hits: layer 0's series 0 and 1, then layer 1. Into 1: 3, and the first is layer 0's series 0. Left of the panel: 0.
    /// </remarks>
    [Fact]
    public void BandsBuffersAndPanels()
    {
        using var low = Column.Wrap(Enumerable.Range(0, 100).Select(i => (double)i).ToArray());
        using var high = Column.Wrap(Enumerable.Range(0, 100).Select(i => i + 10.0).ToArray());
        var table = DataTable.Create().Add("x", Column.Uniform(0, 1, 100)).Add("low", low).Add("high", high).Build();
        using var engine = Drawn(
            new PlotSpec
            {
                Data = table,
                Layers =
                [
                    new BandLayerSpec
                    {
                        X = table.Col<double>("x"),
                        YMin = table.Col<double>("low"),
                        YMax = table.Col<double>("high"),
                    },
                    new LineLayerSpec { X = table.Col<double>("x"), Y = table.Col<double>("low") },
                ],
            }
        );
        var middle = Inside(engine, 0.5, 0.5);
        var hits = new SeriesHit[4];
        Assert.Equal(3, engine.HitTest(middle, 1, HitMode.X, hits));
        Assert.Equal([(0, 0), (0, 1), (1, 0)], hits[..3].Select(h => (h.Layer, h.Series)));
        Assert.Equal(hits[0].Row, hits[1].Row);
        Assert.Equal(high.Values()[hits[1].Row], hits[1].Y);

        var one = new SeriesHit[1];
        Assert.Equal(3, engine.HitTest(middle, 1, HitMode.X, one));
        Assert.Equal((0, 0), (one[0].Layer, one[0].Series));

        Assert.Equal(0, engine.HitTest(new Vector2(1, middle.Y), 1, HitMode.X, hits));
    }

    /// <summary>
    /// A hit test needs a frame, and refuses a radius or a mode out of range; once warm it allocates nothing (invariant 3,
    /// design §6.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A line of 100,000 rows; an engine with the plot set.
    ///
    /// Steps:
    /// - Hit test before a frame. Draw; hit test with radius −1, NaN, +∞, and mode 2. Warm 10 hit tests of each mode; count the
    ///   bytes this thread allocates over 100 more.
    ///
    /// Expected:
    /// - InvalidOperationException before the frame; ArgumentOutOfRangeException for each bad argument; 0 bytes.
    /// </remarks>
    [Fact]
    public void Arguments_AndNoAllocation()
    {
        using var y = Column.Wrap(Enumerable.Range(0, 100_000).Select(i => Math.Sin(i * 0.001)).ToArray());
        var table = DataTable.Create().Add("x", Column.Uniform(0, 1, 100_000)).Add("y", y).Build();
        using var engine = new PlotEngine(new NullBackend(), 640, 360) { WaitsForIndexes = true };
        engine.SetPlot(new PlotSpec { Data = table, Layers = [new LineLayerSpec { X = table.Col<double>("x"), Y = table.Col<double>("y") }] });
        var hits = new SeriesHit[2];
        _ = Assert.Throws<InvalidOperationException>(() => engine.HitTest(new Vector2(300, 150), 4, HitMode.X, hits));

        engine.RenderFrame();
        var middle = Inside(engine, 0.5, 0.5);
        foreach (var radius in (ReadOnlySpan<float>)[-1, float.NaN, float.PositiveInfinity])
        {
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => engine.HitTest(middle, radius, HitMode.XY, hits));
        }

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => engine.HitTest(middle, 4, (HitMode)2, hits));
        for (var i = 0; i < 10; i++)
        {
            _ = engine.HitTest(middle, 4, HitMode.X, hits);
            _ = engine.HitTest(middle, 4, HitMode.XY, hits);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            _ = engine.HitTest(middle with { X = middle.X + (i % 50) }, 4, i % 2 == 0 ? HitMode.X : HitMode.XY, hits);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>
    /// A series whose pyramid is still being built answers without waiting, marked provisional (Issue #337 Decision 5).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread (`testing.mdc`); a line over a Y of two chunks whose chunk 1 is held on the worker.
    ///
    /// Steps:
    /// - Draw once; hit test the panel's middle with XY and a radius as large as the panel. Release the chunk.
    ///
    /// Expected:
    /// - The hit test returns; its hits are marked provisional.
    /// </remarks>
    [Fact]
    public void BuildingPyramid_AnswersProvisionally()
    {
        const int Chunk = Column.ChunkLength;
        using var pool = new PlotWorkerPool(1);
        using var y = TestStorage<float>.MakeColumn([.. Enumerable.Range(0, 2 * Chunk).Select(i => MathF.Sin(i * 0.001f))], out var storage);
        storage.Hold(1);
        try
        {
            var table = DataTable.Create().Add("x", Column.Uniform(0, 1, 2 * Chunk)).Add("y", y).Build();
            using var engine = new PlotEngine(new NullBackend(), 640, 360, 1, pool);
            engine.SetPlot(new PlotSpec { Data = table, Layers = [new LineLayerSpec { X = table.Col<double>("x"), Y = table.Col<float>("y") }] });
            engine.RenderFrame();
            storage.WaitForWorker(1);
            var hits = new SeriesHit[2];
            var found = engine.HitTest(Inside(engine, 0.5, 0.5), 400, HitMode.XY, hits);
            Assert.Equal(1, found);
            Assert.True(hits[0].Provisional);
        }
        finally
        {
            storage.Release(1);
        }
    }

    /// <summary>
    /// A split layer's and a facet's hits name rows of the plot's table, gathered or in runs, of the series' category; a
    /// stat's and a numeric stack's are marked derived (Issue #337 Decision 3, Issue #341 Decision 3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 3,000 rows of an increasing x, a y, a category "g" of 3 values interleaved row by row and "s" of 3 values in runs.
    ///
    /// Steps:
    /// - Hit test 20 cursors with XY and a radius of 30 pixels: a line split by g (gathered), split by s (runs), a line in a
    ///   facet by g (gathered panels). Hit test the middle with X: a density of x, and an area stacked over x by s.
    ///
    /// Expected:
    /// - The splits: each hit's row has the hit's x and y, and the category its series names. The facet: each hit's row has
    ///   its x and y and the panel's category. The density and the stack: every hit is marked derived.
    /// </remarks>
    [Fact]
    public void Rows_AreTheTablesOrMarkedDerived()
    {
        const int Rows = 3000;
        var xs = Enumerable.Range(0, Rows).Select(i => i * 0.5).ToArray();
        var ys = Enumerable.Range(0, Rows).Select(i => Math.Sin(i * 0.01) + (i % 3)).ToArray();
        var g = Enumerable.Range(0, Rows).Select(i => "abc"[i % 3].ToString()).ToArray();
        var runs = Enumerable.Range(0, Rows).Select(i => "uvw"[i * 3 / Rows].ToString()).ToArray();
        using var x = Column.Wrap(xs);
        using var y = Column.Wrap(ys);
        var table = DataTable
            .Create()
            .Add("x", x)
            .Add("y", y)
            .Add("g", Column.Categorical(g, ["a", "b", "c"]))
            .Add("s", Column.Categorical(runs, ["u", "v", "w"]))
            .Build();
        var (gx, gy, gg, gs) = (table.Col<double>("x"), table.Col<double>("y"), table.Cat("g"), table.Cat("s"));
        var hits = new SeriesHit[16];
        var random = new Random(3);
        foreach (var (by, codes, names) in new[] { (gg, g, "abc"), (gs, runs, "uvw") })
        {
            using var engine = Drawn(
                new PlotSpec
                {
                    Data = table,
                    Layers =
                    [
                        new LineLayerSpec
                        {
                            X = gx,
                            Y = gy,
                            ColorBy = by.ToColumnRef(),
                        },
                    ],
                }
            );
            var seen = 0;
            for (var k = 0; k < 20; k++)
            {
                var found = Math.Min(engine.HitTest(Inside(engine, random.NextDouble(), random.NextDouble()), 30, HitMode.XY, hits), hits.Length);
                foreach (var hit in hits[..found])
                {
                    Assert.Equal((xs[hit.Row], ys[hit.Row]), (hit.X, hit.Y));
                    Assert.Equal(names[hit.Series].ToString(), codes[hit.Row]);
                    Assert.False(hit.Derived);
                    seen++;
                }
            }

            Assert.True(seen > 0);
        }

        using (
            var facet = Drawn(
                new PlotSpec
                {
                    Data = table,
                    Layers = [new LineLayerSpec { X = gx, Y = gy }],
                    Facet = Facet.Wrap(gg),
                }
            )
        )
        {
            var seen = 0;
            for (var p = 0; p < 3; p++)
            {
                var panel = new PanelId(0, p);
                var view = (PanelView2D)facet.CurrentView(panel);
                for (var k = 0; k < 10; k++)
                {
                    var (fx, fy) = (random.NextDouble(), random.NextDouble());
                    var vx = view.X.LowValue + (fx * (view.X.HighValue - view.X.LowValue));
                    var vy = view.Y.LowValue + (fy * (view.Y.HighValue - view.Y.LowValue));
                    var cursor = facet.ViewToPixel(panel, ViewRange.FromValues(vx, vx), ViewRange.FromValues(vy, vy));
                    var found = Math.Min(facet.HitTest(cursor, 30, HitMode.XY, hits), hits.Length);
                    foreach (var hit in hits[..found])
                    {
                        Assert.Equal(p, hit.Panel);
                        Assert.Equal((xs[hit.Row], ys[hit.Row]), (hit.X, hit.Y));
                        Assert.Equal("abc"[p].ToString(), g[hit.Row]);
                        seen++;
                    }
                }
            }

            Assert.True(seen > 0);
        }

        var derivedLayers = new LayerSpec[]
        {
            new LineLayerSpec { X = gx, Stat = Stat.Density() },
            new AreaLayerSpec
            {
                X = gx,
                Y = gy,
                ColorBy = gs.ToColumnRef(),
                Position = Position.Stack,
            },
        };
        foreach (var layer in derivedLayers)
        {
            using var engine = Drawn(new PlotSpec { Data = table, Layers = [layer] });
            var found = Math.Min(engine.HitTest(Inside(engine, 0.5, 0.5), 1, HitMode.X, hits), hits.Length);
            Assert.True(found > 0);
            Assert.All(hits[..found], hit => Assert.True(hit.Derived));
        }
    }

    /// <summary>
    /// An image answers with the texel under the cursor, its center and its element's position; outside the image, nothing
    /// (Issue #337 Decision 4).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A raster of 40 × 24 numbers with the lower origin, and an RGBA raster of 8 × 6 with the upper origin; default extents.
    ///
    /// Steps:
    /// - Hit test the centers of 10 texels of each, and a pixel 3 pixels past the image's right edge.
    ///
    /// Expected:
    /// - One hit each: its x and y the texel's center, its row (r · width + c) · channels, r counted from the raster's first
    ///   row. Past the edge: none.
    /// </remarks>
    [Fact]
    public void Image_AnswersWithTheTexelUnderTheCursor()
    {
        using var numbers = Column.Wrap(Enumerable.Range(0, 40 * 24).Select(i => (float)i).ToArray());
        using var colors = Column.Wrap(new byte[8 * 6 * 4]);
        var hits = new SeriesHit[2];
        var cases = new (Raster Raster, RasterOrigin Origin)[]
        {
            (Raster.Of(numbers, 40), RasterOrigin.Lower),
            (Raster.Rgba(colors, 8), RasterOrigin.Upper),
        };
        foreach (var (raster, origin) in cases)
        {
            var spec = new PlotSpec { Data = DataTable.Create().Build(), Layers = [new ImageLayerSpec { Raster = raster, Origin = origin }] };
            using var engine = Drawn(spec);
            var (w, h, channels) = (raster.Width, raster.Height, raster.IsRgba ? 4 : 1);
            for (var k = 0; k < 10; k++)
            {
                var (c, r) = ((k * 7) % w, (k * 5) % h);
                var cy = origin == RasterOrigin.Upper ? h - 1 - r : r;
                Assert.Equal(1, engine.HitTest(Pixel(engine, c, cy), 2, HitMode.XY, hits));
                Assert.Equal(((((long)r * w) + c) * channels, (double)c, (double)cy), (hits[0].Row, hits[0].X, hits[0].Y));
            }

            var edge = Pixel(engine, w - 0.5, 1);
            Assert.Equal(0, engine.HitTest(edge with { X = edge.X + 3 }, 2, HitMode.X, hits));
        }
    }

    /// <summary>
    /// A layer on a secondary y answers in its own units; flipped, the x runs down the screen; a polar panel answers nothing
    /// (Issue #337 Decision 8, Issue #341 Decision 1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 500 rows: a line on y of i / 500 and one on a secondary y of 2 · i.
    ///
    /// Steps:
    /// - Hit test the panel's middle with X; the same plot flipped; the first line in polar.
    ///
    /// Expected:
    /// - Layer 1's y is 1,000 times layer 0's, of the same row. Flipped: the row's pixel lies within a pixel of the cursor's
    ///   y. Polar: 0.
    /// </remarks>
    [Fact]
    public void SecondaryFlipAndPolar()
    {
        using var y = Column.Wrap(Enumerable.Range(0, 500).Select(i => i / 500.0).ToArray());
        using var big = Column.Wrap(Enumerable.Range(0, 500).Select(i => i * 2.0).ToArray());
        var table = DataTable.Create().Add("x", Column.Uniform(0, 1, 500)).Add("y", y).Add("big", big).Build();
        var two = new PlotSpec
        {
            Data = table,
            Layers =
            [
                new LineLayerSpec { X = table.Col<double>("x"), Y = table.Col<double>("y") },
                new LineLayerSpec
                {
                    X = table.Col<double>("x"),
                    Y = table.Col<double>("big"),
                    SecondaryY = true,
                },
            ],
            ScaleY2 = Scale.Linear(),
        };
        var hits = new SeriesHit[4];
        using (var engine = Drawn(two))
        {
            Assert.Equal(2, engine.HitTest(Inside(engine, 0.5, 0.5), 1, HitMode.X, hits));
            Assert.Equal(hits[0].Row, hits[1].Row);
            Assert.Equal(hits[0].Y * 1000, hits[1].Y, 9);
        }

        using (var flipped = Drawn(two with { Coord = Coord.Cartesian(flip: true) }))
        {
            var cursor = Inside(flipped, 0.3, 0.5);
            Assert.Equal(2, flipped.HitTest(cursor, 1, HitMode.X, hits));
            var rowPixel = Pixel(flipped, hits[0].X, hits[0].Y);
            Assert.True(Math.Abs(rowPixel.Y - cursor.Y) <= 1, $"{rowPixel} against {cursor}");
        }

        using var polar = Drawn(
            new PlotSpec
            {
                Data = table,
                Layers = [two.Layers[0]],
                Coord = Coord.Polar(),
            }
        );
        Assert.Equal(0, polar.HitTest(new Vector2(320, 180), 50, HitMode.XY, hits));
    }

    /// <summary>
    /// An image in a gathered facet's panel keeps its element's position: a layer without rows is not taken back through the
    /// facet's gather (PR #342 review).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 300 rows of x and y and a category of 3 values interleaved; a line and a 30 × 20 raster whose extent covers the
    ///   rows with the lower origin, in a facet by the category.
    ///
    /// Steps:
    /// - In panel 1, hit test the centers of 5 texels with XY and a radius of 1 pixel.
    ///
    /// Expected:
    /// - The image's hit (layer 1) has the row (r · 30 + c) of the texel.
    /// </remarks>
    [Fact]
    public void ImageInAFacet_KeepsItsElements()
    {
        const int Rows = 300;
        using var x = Column.Wrap(Enumerable.Range(0, Rows).Select(i => i / 10.0).ToArray());
        using var y = Column.Wrap(Enumerable.Range(0, Rows).Select(i => (i % 20) * 1.0).ToArray());
        using var values = Column.Wrap(Enumerable.Range(0, 30 * 20).Select(i => (float)i).ToArray());
        var table = DataTable
            .Create()
            .Add("x", x)
            .Add("y", y)
            .Add("g", Column.Categorical([.. Enumerable.Range(0, Rows).Select(i => "abc"[i % 3].ToString())], ["a", "b", "c"]))
            .Build();
        using var engine = Drawn(
            new PlotSpec
            {
                Data = table,
                Layers =
                [
                    new LineLayerSpec { X = table.Col<double>("x"), Y = table.Col<double>("y") },
                    new ImageLayerSpec { Raster = Raster.Of(values, 30), Origin = RasterOrigin.Lower },
                ],
                Facet = Facet.Wrap(table.Cat("g")),
            }
        );
        var panel = new PanelId(0, 1);
        var hits = new SeriesHit[4];
        for (var k = 0; k < 5; k++)
        {
            var (c, r) = ((k * 5) + 2, (k * 3) + 1);
            var cursor = engine.ViewToPixel(panel, ViewRange.FromValues(c, c), ViewRange.FromValues(r, r));
            var found = Math.Min(engine.HitTest(cursor, 1, HitMode.XY, hits), hits.Length);
            var image = Assert.Single(hits[..found], h => h.Layer == 1);
            Assert.Equal((long)((r * 30) + c), image.Row);
        }
    }

    /// <summary>A figure answers in the plot under the cursor, with rows of that plot's table (design §6.3, Issue #337 Decision 7).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A figure of two plots side by side: a line over 100 rows of y = i, and a line over 200 rows of y = 1,000 + i.
    ///
    /// Steps:
    /// - Hit test the middle of plot 1's panel with X.
    ///
    /// Expected:
    /// - One hit of plot 1, whose y is 1,000 plus its row.
    /// </remarks>
    [Fact]
    public void Figure_AnswersInThePlotUnderTheCursor()
    {
        using var small = Column.Wrap(Enumerable.Range(0, 100).Select(i => (double)i).ToArray());
        using var large = Column.Wrap(Enumerable.Range(0, 200).Select(i => 1000.0 + i).ToArray());
        var first = DataTable.Create().Add("x", Column.Uniform(0, 1, 100)).Add("y", small).Build();
        var second = DataTable.Create().Add("x", Column.Uniform(0, 1, 200)).Add("y", large).Build();
        using var engine = new PlotEngine(new NullBackend(), 640, 360) { WaitsForIndexes = true };
        engine.SetFigure(
            Figure.HConcat(
                Plot.Create(first).Line(first.Col<double>("x"), first.Col<double>("y")).Build(),
                Plot.Create(second).Line(second.Col<double>("x"), second.Col<double>("y")).Build()
            )
        );
        engine.RenderFrame();
        var panel = new PanelId(1, 0);
        var view = (PanelView2D)engine.CurrentView(panel);
        var (mx, my) = ((view.X.LowValue + view.X.HighValue) / 2, (view.Y.LowValue + view.Y.HighValue) / 2);
        var hits = new SeriesHit[2];
        Assert.Equal(1, engine.HitTest(engine.ViewToPixel(panel, ViewRange.FromValues(mx, mx), ViewRange.FromValues(my, my)), 1, HitMode.X, hits));
        Assert.Equal(1, hits[0].Plot);
        Assert.Equal(1000.0 + hits[0].Row, hits[0].Y);
    }

    /// <summary>
    /// A layer not drawn yet, whose X statistics the workers have not made, answers nothing; once drawn it answers (Issue
    /// #341 Decision 4).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread (`testing.mdc`); a line over a uniform x and y, drawn until its pyramid is built; then a plot of
    ///   it and a line over a stored X of one chunk held on the worker, so that the X statistics wait.
    ///
    /// Steps:
    /// - Draw the two lines once; hit test the panel's middle with X. Release the chunk; draw until not provisional; hit test again.
    ///
    /// Expected:
    /// - Held: one hit, of layer 0. Released: two hits, of layers 0 and 1.
    /// </remarks>
    [Fact]
    public void PendingLayer_AnswersNothing()
    {
        const int Rows = 1000;
        using var pool = new PlotWorkerPool(1);
        using var x = TestStorage<double>.MakeColumn([.. Enumerable.Range(0, Rows).Select(i => (double)i)], out var storage);
        using var y = Column.Wrap(Enumerable.Range(0, Rows).Select(i => Math.Sin(i * 0.01)).ToArray());
        try
        {
            var table = DataTable.Create().Add("u", Column.Uniform(0, 1, Rows)).Add("x", x).Add("y", y).Build();
            using var engine = new PlotEngine(new NullBackend(), 640, 360, 1, pool);
            var drawn = new LineLayerSpec { X = table.Col<double>("u"), Y = table.Col<double>("y") };

            // Layer 0's pyramid is built first: on the pool's one worker, a held X statistics job taken before it would hold
            // it too, and layer 0 would have nothing to answer with (Issue #345).
            engine.SetPlot(new PlotSpec { Data = table, Layers = [drawn] });
            Eventually.True(
                () =>
                {
                    engine.RenderFrame();
                    return !engine.IsProvisional;
                },
                "layer 0's pyramid"
            );

            storage.Hold(0);
            engine.SetPlot(
                new PlotSpec { Data = table, Layers = [drawn, new LineLayerSpec { X = table.Col<double>("x"), Y = table.Col<double>("y") }] }
            );
            engine.RenderFrame();
            var hits = new SeriesHit[4];
            Assert.Equal(1, engine.HitTest(Inside(engine, 0.5, 0.5), 1, HitMode.X, hits));
            Assert.Equal(0, hits[0].Layer);

            storage.Release(0);
            Eventually.True(
                () =>
                {
                    engine.RenderFrame();
                    return !engine.IsProvisional;
                },
                "a frame that is not provisional"
            );
            Assert.Equal(2, engine.HitTest(Inside(engine, 0.5, 0.5), 1, HitMode.X, hits));
        }
        finally
        {
            storage.Release(0);
        }
    }

    private static PlotEngine Drawn(PlotSpec spec)
    {
        var engine = new PlotEngine(new NullBackend(), 640, 360) { WaitsForIndexes = true };
        engine.SetPlot(spec);
        engine.RenderFrame();
        return engine;
    }

    /// <summary>Returns a pixel inside the panel at fractions of its view's x and y ranges.</summary>
    private static Vector2 Inside(PlotEngine engine, double fx, double fy)
    {
        var view = (PanelView2D)engine.CurrentView(Panel);
        var x = view.X.LowValue + (fx * (view.X.HighValue - view.X.LowValue));
        var y = view.Y.LowValue + (fy * (view.Y.HighValue - view.Y.LowValue));
        return engine.ViewToPixel(Panel, ViewRange.FromValues(x, x), ViewRange.FromValues(y, y));
    }

    private static Vector2 Pixel(PlotEngine engine, double x, double y) =>
        engine.ViewToPixel(Panel, ViewRange.FromValues(x, x), ViewRange.FromValues(y, y));

    private static double PixelX(PlotEngine engine, double x)
    {
        var view = (PanelView2D)engine.CurrentView(Panel);
        return Pixel(engine, x, view.Y.LowValue).X;
    }
}

file static class ColumnValues
{
    public static double[] Values(this Column<double> column)
    {
        var values = new double[column.Length];
        for (var i = 0L; i < column.Length; i++)
        {
            values[i] = column.GetValue(i);
        }

        return values;
    }
}
