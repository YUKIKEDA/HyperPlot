using System.Numerics;
using HyperPlot.Interaction;
using HyperPlot.Rendering;
using HyperPlot.Testing;

namespace HyperPlot.Tests;

/// <summary>
/// The selection of point layers (design §6.4 "散布の選択", §10 R3c row, Issue #471): their rows on both paths, and the
/// highlight their data layer draws, of raw points and of a density.
/// </summary>
[Collection("Large data")]
public sealed class PointSelectionTests
{
    private const int Cpu = LayerPlan.MaxElements;
    private const int Dense = 400_000;
    private static readonly PanelId Panel = new(0, 0);

    // A concave lasso: a square with a notch cut down from its top, as shares of its ranges.
    private static readonly Vector2[] Notched = [new(0, 0), new(1, 0), new(1, 1), new(0.5f, 0.3f), new(0, 1)];

    /// <summary>The kinds of predicate a test selects with.</summary>
    public enum Predicate
    {
        /// <summary>A rectangle.</summary>
        Rectangle,

        /// <summary>An x range.</summary>
        XRange,

        /// <summary>A lasso with a notch.</summary>
        Lasso,
    }

    /// <summary>
    /// A point layer answers <see cref="PlotEngine.SelectedRowsAsync"/> with the rows a loop finds by the predicate, on the CPU
    /// path from the workers' bits and on the index path from the index (design §6.4 "行の集合", Issue #471 Decision 4); on the
    /// index path, the cells meet the predicate inside it, across its boundary, and, for the lasso, outside it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Seeded points over [0, 100]²: 131,072 (the CPU path) or 400,000 (the index path), raw points at the view [0, 100]².
    ///
    /// Steps:
    /// - Select the rectangle [20.3, 61.7] × [30.1, 70.9], the x range [20.3, 61.7], or a lasso over [20, 80] × [15, 85] with
    ///   a notch cut down to 30 % of its height from its top. Ask the layer's rows.
    /// - Find by a loop the rows whose x and y the selection holds. On the index path, count the cells by how they meet it.
    ///
    /// Expected:
    /// - The rows are the loop's. On the index path, some cells lie inside and some across; for the lasso, some outside.
    /// </remarks>
    [Theory]
    [InlineData(Cpu, Predicate.Rectangle)]
    [InlineData(Cpu, Predicate.XRange)]
    [InlineData(Cpu, Predicate.Lasso)]
    [InlineData(Dense, Predicate.Rectangle)]
    [InlineData(Dense, Predicate.XRange)]
    [InlineData(Dense, Predicate.Lasso)]
    public void SelectedRows_MatchALoop_OnBothPaths(int points, Predicate predicate)
    {
        var (xs, ys, table) = Uniform(points, seed: 31);
        using var engine = Drawn(
            new NullBackend(),
            Plot.Create(table).Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Draw(PointDraw.Points))
        );
        var selection = predicate switch
        {
            Predicate.Rectangle => Selection2D.Rectangle(Panel, ViewRange.FromValues(20.3, 61.7), ViewRange.FromValues(30.1, 70.9)),
            Predicate.XRange => Selection2D.XRange(Panel, ViewRange.FromValues(20.3, 61.7)),
            _ => Selection2D.Lasso(Panel, ViewRange.FromValues(20.0, 80.0), ViewRange.FromValues(15.0, 85.0), Notched),
        };
        engine.Select(selection);
        var taken = (Selection2D)engine.Selection(0)!;

        var rows = Rows(engine);

        var expected = Enumerable.Range(0, points).Where(i => taken.Holds(xs[i] - taken.X.Origin, ys[i] - taken.Y.Origin)).ToArray();
        Assert.Equal(expected.Length, rows.Count);
        Assert.All(expected, i => Assert.True(rows.Contains(i)));
        if (points > Cpu)
        {
            var snapshot = ((IndexedPointPlan)engine.Plan!.Layers[0]).SpatialIndex!.Acquire();
            try
            {
                var (inside, outside, straddles) = SpatialSelection.Covers(snapshot, taken);
                Assert.True(inside > 0 && straddles > 0, $"Cells inside {inside}, across {straddles}.");
                Assert.True(predicate != Predicate.Lasso || outside > 0, $"Lasso cells outside {outside}.");
            }
            finally
            {
                snapshot.Release();
            }
        }
    }

    /// <summary>
    /// A split point layer and a facet's panel answer with the plot's table's rows, on the index path (design §6.4 "行の集合"):
    /// a part's gathered rows are taken back to the table's.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 300,000 seeded points over [0, 100]² and a category g of "a", "b", "c" by the row's remainder of 3; raw points.
    ///
    /// Steps:
    /// - Color the layer by g (three parts on the index path) and select the rectangle [10, 40] × [55, 90].
    /// - Facet the layer by g (three panels of 100,000 points, each on the index path) and select the same rectangle in
    ///   panel 1.
    ///
    /// Expected:
    /// - The split layer: the rows of every category the rectangle holds. The facet: the rows of b the rectangle holds.
    /// </remarks>
    [Fact]
    public void SelectedRows_OfSplitAndFacetedPoints_AreTheTablesRows()
    {
        const int Count = 300_000;
        var (xs, ys, points) = Uniform(Count, seed: 32);
        var table = DataTable
            .Create()
            .Add("x", points.Col<double>("x").Column)
            .Add("y", points.Col<double>("y").Column)
            .Add("g", Column.Categorical([.. Enumerable.Range(0, Count).Select(i => (string?)"abc"[i % 3].ToString())]))
            .Build();
        var (x, y) = (table.Col<double>("x"), table.Col<double>("y"));
        bool Holds(int i) => xs[i] is >= 10 and <= 40 && ys[i] is >= 55 and <= 90;

        using var split = Drawn(new NullBackend(), Plot.Create(table).Point(x, y, p => p.Draw(PointDraw.Points).ColorBy(table.Cat("g"))));
        Assert.All(((SplitPlan)split.Plan!.Layers[0]).Parts, part => Assert.IsType<IndexedPointPlan>(part));
        split.Select(Selection2D.Rectangle(Panel, ViewRange.FromValues(10.0, 40.0), ViewRange.FromValues(55.0, 90.0)));
        var all = Enumerable.Range(0, Count).Where(Holds).ToArray();
        var rows = Rows(split);
        Assert.Equal(all.Length, rows.Count);
        Assert.All(all, i => Assert.True(rows.Contains(i)));

        using var faceted = new PlotEngine(new NullBackend(), 640, 360) { WaitsForIndexes = true };
        faceted.SetPlot(
            Plot.Create(table)
                .Point(x, y, p => p.Draw(PointDraw.Points))
                .FacetWrap(table.Cat("g"))
                .ScaleX(Scale.Linear().WithDomain(0, 100))
                .ScaleY(Scale.Linear().WithDomain(0, 100))
                .Build()
        );
        faceted.RenderFrame();
        faceted.Select(Selection2D.Rectangle(new PanelId(0, 1), ViewRange.FromValues(10.0, 40.0), ViewRange.FromValues(55.0, 90.0)));
        var b = Enumerable.Range(0, Count).Where(i => i % 3 == 1 && Holds(i)).ToArray();
        var panelRows = Rows(faceted);
        Assert.Equal(b.Length, panelRows.Count);
        Assert.All(b, i => Assert.True(panelRows.Contains(i)));
    }

    /// <summary>
    /// Before the index is full, a layer's rows wait for it and are then exact; while a changed column's index is built
    /// again, they come from the last full snapshot without waiting (design §6.4 "索引ができる前の行の集合", Issue #471 Decision
    /// 13).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 200,000 seeded points over [0, 100]², raw points, on a pool of 3 workers, in an engine that does not wait for its
    ///   indexes; the x column's storage holds the reads of chunk 0.
    ///
    /// Steps:
    /// - Hold chunk 0, draw a frame, select the rectangle [20, 60] × [30, 70], and ask the rows; wait 300 ms. Release the
    ///   chunk; wait for the rows.
    /// - Draw until not provisional. Mirror every x (x → 100 − x), invalidate the column, draw a frame, which queues the new
    ///   build and draws the last full snapshot, and ask the rows. Draw until not provisional and ask them again.
    ///
    /// Expected:
    /// - The first rows have not ended after 300 ms; once the chunk is released, they are a loop's over the values.
    /// - The second rows are a loop's over the values before the mirror: the snapshot drawn, not the one being built. The third
    ///   are a loop's over the mirrored values.
    /// </remarks>
    [Fact]
    public void SelectedRows_WaitForTheFirstFullIndex_AndNotForARebuild()
    {
        const int Count = 200_000;
        var (values, ys, _) = Uniform(Count, seed: 33);
        using var x = TestStorage<double>.MakeColumn((double[])values.Clone(), out var storage);
        using var y = Column.Wrap(ys);
        var table = DataTable.Create().Add("x", x).Add("y", y).Build();
        var backend = new NullBackend();
        using var pool = new PlotWorkerPool(3);
        using var engine = new PlotEngine(backend, 640, 360, pool: pool);
        var rectangle = Selection2D.Rectangle(Panel, ViewRange.FromValues(20.0, 60.0), ViewRange.FromValues(30.0, 70.0));
        var before = Enumerable.Range(0, Count).Where(i => values[i] is >= 20 and <= 60 && ys[i] is >= 30 and <= 70).ToArray();

        storage.Hold(0);
        Task<RowSelection> first;
        try
        {
            engine.SetPlot(
                Plot.Create(table)
                    .Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Draw(PointDraw.Points))
                    .ScaleX(Scale.Linear().WithDomain(0, 100))
                    .ScaleY(Scale.Linear().WithDomain(0, 100))
                    .Build()
            );
            engine.RenderFrame();
            engine.Select(rectangle);
            first = engine.SelectedRowsAsync(0, 0).AsTask();
            Thread.Sleep(300);
            Assert.False(first.IsCompleted, "The rows ended before the index was built.");
        }
        finally
        {
            storage.Release(0);
        }

        Same(before, Done(first));

        Settle(engine, backend);
        for (var i = 0; i < Count; i++)
        {
            storage.Values[i] = 100 - values[i];
        }

        // The frame queues the build of the mirrored values and draws the last full snapshot, which the rows come from.
        x.Invalidate();
        engine.RenderFrame();
        Same(before, Done(engine.SelectedRowsAsync(0, 0).AsTask()));

        Settle(engine, backend);
        var after = Enumerable.Range(0, Count).Where(i => 100 - values[i] is >= 20 and <= 60 && ys[i] is >= 30 and <= 70).ToArray();
        Same(after, Done(engine.SelectedRowsAsync(0, 0).AsTask()));

        static void Same(int[] expected, RowSelection rows)
        {
            Assert.Equal(expected.Length, rows.Count);
            Assert.All(expected, i => Assert.True(rows.Contains(i)));
        }
    }

    /// <summary>
    /// A new selection draws the data layer of a plot with a point layer again, and a frame of the mouse alone copies it; a
    /// plot without one copies its data layer for a new selection too (design §5.4 "残し方と見分け方", Issue #471 Decision 6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 400,000 seeded points over [0, 100]² (the index path) at the view [0, 100]²; and the engine fixture's line.
    ///
    /// Steps:
    /// - Over the points: move the pointer twice and draw after each; select a rectangle and draw; move and draw; select
    ///   another and draw.
    /// - Over the line: select a rectangle and draw.
    ///
    /// Expected:
    /// - Over the points: a frame of a new selection draws the plot's list and the overlay (2 lists); a frame of a move draws
    ///   the overlay over the copy (1 list, 1 copy).
    /// - Over the line: the frame of the new selection draws the overlay over the copy.
    /// </remarks>
    [Fact]
    public void NewSelection_DrawsAPointPlotAgain_TheMouseAloneCopiesIt()
    {
        var backend = new NullBackend();
        var (_, _, table) = Uniform(Dense, seed: 34);
        using var engine = Drawn(backend, Plot.Create(table).Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Draw(PointDraw.Points)));
        Move(engine, Pixel(engine, 50, 50));
        Move(engine, Pixel(engine, 51, 50));
        Assert.Equal((1, 1), (backend.DrawsInFrame, backend.CopiesInFrame));

        engine.Select(Selection2D.Rectangle(Panel, ViewRange.FromValues(20.0, 60.0), ViewRange.FromValues(30.0, 70.0)));
        engine.RenderFrame();
        Assert.Equal(2, backend.DrawsInFrame);

        Move(engine, Pixel(engine, 52, 50));
        Assert.Equal((1, 1), (backend.DrawsInFrame, backend.CopiesInFrame));

        engine.Select(Selection2D.Rectangle(Panel, ViewRange.FromValues(10.0, 60.0), ViewRange.FromValues(30.0, 70.0)));
        engine.RenderFrame();
        Assert.Equal(2, backend.DrawsInFrame);

        using var f = new EngineFixture();
        f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, f.Middle));
        f.Engine.RenderFrame();
        f.Engine.Select(Selection2D.Rectangle(Panel, ViewRange.FromValues(200.0, 600.0), ViewRange.FromValues(-5.0, 5.0)));
        f.Engine.RenderFrame();
        Assert.Equal((1, 1), (f.Backend.DrawsInFrame, f.Backend.CopiesInFrame));
    }

    /// <summary>
    /// The resident markers' runs name the region of the selection, whole under the panel's clip: a rectangle's and an x
    /// range's box of the pixels its ends map to, and a lasso's mask over the panel's pixels, set where the polygon holds the
    /// pixel's center by the even-odd rule (design §6.4 "生の点の強調", Issue #471 Decision 6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 400,000 seeded points over [0, 100]², raw points at the view [0, 100]², 640 × 360.
    ///
    /// Steps:
    /// - Select the rectangle [20, 60] × [30, 70], draw, and read the plan's list; the same for the x range [20, 60] and the
    ///   notched lasso over [20, 80] × [15, 85].
    ///
    /// Expected:
    /// - Every run names region 1, the list's only region, and is clipped to the panel.
    /// - The rectangle: a box from the pixels of (20, 70) to (60, 30), no mask. The x range: from x 20's pixel to x 60's,
    ///   across the whole target's height; within a thousandth of a pixel, the engine's map of the ends. The lasso: a mask
    ///   from the panel's corner whose every pixel of the panel holds its center as the even-odd rule over the polygon's
    ///   pixels does, but a center within a thousandth of a pixel of an edge.
    /// </remarks>
    [Fact]
    public void Runs_NameTheRegionOfTheSelection_UnderThePanelsClip()
    {
        var (_, _, table) = Uniform(Dense, seed: 35);
        using var engine = Drawn(
            new NullBackend(),
            Plot.Create(table).Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Draw(PointDraw.Points))
        );
        var g = engine.Plan!.Guides;
        var clip = new ClipRect(g.Left, g.Top, g.Left + g.Width, g.Top + g.Height);

        var list = Highlighted(Selection2D.Rectangle(Panel, ViewRange.FromValues(20.0, 60.0), ViewRange.FromValues(30.0, 70.0)));
        var (low, high) = (Pixel(engine, 20, 70), Pixel(engine, 60, 30));
        var region = Assert.Single(list.Highlights.ToArray());
        Near(new Vector4(low.X, low.Y, high.X, high.Y), region.Box);
        Assert.Equal(0u, region.MaskStride);

        list = Highlighted(Selection2D.XRange(Panel, ViewRange.FromValues(20.0, 60.0)));
        region = Assert.Single(list.Highlights.ToArray());
        Near(new Vector4(low.X, float.MinValue, high.X, float.MaxValue), region.Box);

        list = Highlighted(Selection2D.Lasso(Panel, ViewRange.FromValues(20.0, 80.0), ViewRange.FromValues(15.0, 85.0), Notched));
        region = Assert.Single(list.Highlights.ToArray());
        Assert.Equal((g.Left, g.Top), (region.MaskLeft, region.MaskTop));
        var polygon = Notched.Select(s => Pixel(engine, 20 + (s.X * 60.0), 15 + (s.Y * 70.0))).ToArray();
        for (var py = g.Top; py < g.Top + g.Height; py++)
        {
            for (var px = g.Left; px < g.Left + g.Width; px++)
            {
                var center = new Vector2(px + 0.5f, py + 0.5f);
                var (inside, edge) = EvenOdd(polygon, center);
                Assert.True(edge || inside == list.Holds(1, center), $"The pixel ({px}, {py}).");
            }
        }

        // The engine maps the ends for the overlay apart from the plan, in other floats: a thousandth of a pixel.
        static void Near(Vector4 expected, Vector4 actual) =>
            Assert.True(Vector4.Distance(expected, actual) < 1e-3, $"The box {actual}, not {expected}.");

        RenderList Highlighted(Selection2D selection)
        {
            engine.Select(selection);
            engine.RenderFrame();
            var list = engine.Plan!.List;
            var runs = list.ResidentRuns.ToArray();
            Assert.NotEmpty(runs);
            Assert.All(runs, run => Assert.Equal((1, clip), (run.Highlight, run.Clip)));
            return list;
        }
    }

    /// <summary>
    /// A point layer on the CPU path fades its points outside the selection's region to 0.2 of its opacity, judged at each
    /// center, and keeps the others (design §6.4 "見た目", Issue #471 Decision 12).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 2,000 seeded points over [0, 100]², raw points of opacity 0.6 in black, at the view [0, 100]².
    ///
    /// Steps:
    /// - Select the notched lasso over [20, 80] × [15, 85], draw, and read the plan's markers.
    ///
    /// Expected:
    /// - A marker whose center the region holds has alpha 0.6 · 255 rounded, the others 0.12 · 255 rounded; both are met.
    /// </remarks>
    [Fact]
    public void CpuPath_FadesThePointsOutsideTheRegion()
    {
        var (_, _, table) = Uniform(2_000, seed: 36);
        using var engine = Drawn(
            new NullBackend(),
            Plot.Create(table).Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Opacity(0.6).Color(new Color(0, 0, 0, 255)))
        );
        engine.Select(Selection2D.Lasso(Panel, ViewRange.FromValues(20.0, 80.0), ViewRange.FromValues(15.0, 85.0), Notched));
        engine.RenderFrame();
        var list = engine.Plan!.List;
        var (kept, faded) = (Math.Round(0.6 * 255) / 255, Math.Round(0.12 * 255) / 255);
        var (some, others) = (false, false);
        foreach (var marker in list.Markers)
        {
            var inside = list.Holds(1, marker.Center);
            Assert.Equal(inside ? kept : faded, marker.Color.W, 5);
            (some, others) = (some || inside, others || !inside);
        }

        Assert.True(some && others);
    }

    /// <summary>
    /// A density's highlight cuts its pieces by the selection's region: each tile names the region, each fill is cut at it,
    /// faded outside; the index makes nothing for it (design §6.4 "密度の強調", Issue #471 Decision 7).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Seeded points over [0, 100]² drawn as a density at the view [0, 100]², 640 × 360: 400,000 (the root's tile) and
    ///   131,073 (no tile: fills).
    ///
    /// Steps:
    /// - Read the index's version, the snapshot's tiles, and the plan's rectangles. Select the notched lasso over [20, 80] × [15, 85], draw, and read
    ///   the plan's list; read the version and the tiles again.
    ///
    /// Expected:
    /// - The tiles: every image names region 1. The fills: of the rectangles not recorded before the selection, one whose
    ///   middle the region holds has the alpha of a fill before, one outside a fifth of such an alpha, rounded; both are met.
    /// - The version and the tiles are the same: no work was made.
    /// </remarks>
    [Theory]
    [InlineData(Dense)]
    [InlineData(Cpu + 1)]
    public void Density_IsCutByTheRegion_WithoutWork(int points)
    {
        var (_, _, table) = Uniform(points, seed: 37);
        using var engine = Drawn(
            new NullBackend(),
            Plot.Create(table).Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Draw(PointDraw.Density))
        );
        var plan = (IndexedPointPlan)engine.Plan!.Layers[0];
        var index = plan.SpatialIndex!;
        var (version, tiles) = (index.Version, Tiles(index));
        var before = engine.Plan.List.Rects.ToArray();

        engine.Select(Selection2D.Lasso(Panel, ViewRange.FromValues(20.0, 80.0), ViewRange.FromValues(15.0, 85.0), Notched));
        engine.RenderFrame();
        var list = engine.Plan!.List;
        Assert.True(plan.DrewDensity);
        if (points == Dense)
        {
            Assert.NotEmpty(list.Images.ToArray());
            Assert.All(list.Images.ToArray(), image => Assert.Equal(1u, image.Highlight));
        }
        else
        {
            // The rectangles the selection made: the guides' and the fills inside the region whole are as before. A piece
            // inside keeps an alpha of the fills before; one outside takes a fifth of one, rounded.
            var alphas = before.Select(r => (int)Math.Round(r.Color.W * 255)).ToHashSet();
            var (some, others) = (false, false);
            foreach (var piece in list.Rects.ToArray().Except(before))
            {
                var inside = list.Holds(1, new Vector2(piece.X + (piece.Width / 2), piece.Y + (piece.Height / 2)));
                (some, others) = (some || inside, others || !inside);
                var alpha = (int)Math.Round(piece.Color.W * 255);
                Assert.True(
                    inside ? alphas.Contains(alpha) : alphas.Any(a => (int)Math.Round(a * 0.2) == alpha),
                    $"The rectangle at ({piece.X}, {piece.Y}), inside {inside}, of alpha {alpha}."
                );
            }

            Assert.True(some && others);
        }

        Assert.Equal((version, tiles), (index.Version, Tiles(index)));

        static DensityTiles? Tiles(SpatialIndex index)
        {
            var snapshot = index.Acquire();
            try
            {
                return snapshot.Tiles;
            }
            finally
            {
                snapshot.Release();
            }
        }
    }

    /// <summary>
    /// The frames of the mouse alone over a plot whose points are highlighted allocate nothing: they copy the kept layer and
    /// draw the hover over it (invariant 3, design §6.4 "生の点の強調").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 400,000 seeded points over [0, 100]² (the index path) at the view [0, 100]², the notched lasso selected, a frame
    ///   drawn.
    ///
    /// Steps:
    /// - Move the pointer over 8 places and draw after each, 5 times round to warm; count the thread's allocations over 5
    ///   more rounds.
    ///
    /// Expected:
    /// - Each frame of the rounds copies the kept layer; 0 bytes.
    /// </remarks>
    [Fact]
    public void HighlightedFramesOfTheMouse_AllocateNothing()
    {
        var backend = new NullBackend();
        var (_, _, table) = Uniform(Dense, seed: 38);
        using var engine = Drawn(backend, Plot.Create(table).Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Draw(PointDraw.Points)));
        engine.Select(Selection2D.Lasso(Panel, ViewRange.FromValues(20.0, 80.0), ViewRange.FromValues(15.0, 85.0), Notched));
        engine.RenderFrame();
        var copies = 0;
        void Round()
        {
            for (var i = 0; i < 8; i++)
            {
                Move(engine, Pixel(engine, 30 + (5 * i), 50));
                copies += backend.CopiesInFrame;
            }
        }

        PerformanceAssert.DoesNotAllocate(() =>
        {
            for (var k = 0; k < 5; k++)
            {
                Round();
            }
        });
        Assert.True(copies >= 40, $"{copies} frames of 40 or more copied the kept layer.");
    }

    // Whether a polygon holds a point by the even-odd rule, as the engine fills a lasso, and whether an edge it crosses passes
    // within a thousandth of a pixel of it.
    private static (bool Inside, bool Edge) EvenOdd(Vector2[] polygon, Vector2 point)
    {
        var (inside, edge) = (false, false);
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            var (a, b) = (polygon[i], polygon[j]);
            if ((a.Y > point.Y) != (b.Y > point.Y))
            {
                var crossing = a.X + ((point.Y - a.Y) * (b.X - a.X) / (b.Y - a.Y));
                edge |= Math.Abs(point.X - crossing) < 1e-3;
                inside ^= point.X < crossing;
            }
        }

        return (inside, edge);
    }

    private static RowSelection Rows(PlotEngine engine) => Done(engine.SelectedRowsAsync(0, 0).AsTask());

    // The rows once their task ends, waited for with a watchdog.
    private static RowSelection Done(Task<RowSelection> task)
    {
        Eventually.Completes(task);
        return task.Result;
    }

    private static void Move(PlotEngine engine, Vector2 at)
    {
        engine.OnPointer(
            new PointerEvent(PointerEventKind.Move, at, PointerButtons.None, PointerButtons.None, ModifierKeys.None, PointerKind.Mouse, 0)
        );
        engine.RenderFrame();
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

    private static (double[] X, double[] Y, DataTable Table) Uniform(int count, int seed)
    {
        var random = new Random(seed);
        var xs = Enumerable.Range(0, count).Select(_ => random.NextDouble() * 100).ToArray();
        var ys = Enumerable.Range(0, count).Select(_ => random.NextDouble() * 100).ToArray();
        return (xs, ys, DataTable.Create().Add("x", Column.Wrap(xs)).Add("y", Column.Wrap(ys)).Build());
    }

    // An engine that waits for its indexes over a plot of domains [0, 100]², drawn until every page and face is on the GPU.
    private static PlotEngine Drawn(NullBackend backend, PlotBuilder builder)
    {
        var engine = new PlotEngine(backend, 640, 360) { WaitsForIndexes = true };
        engine.SetPlot(builder.ScaleX(Scale.Linear().WithDomain(0, 100)).ScaleY(Scale.Linear().WithDomain(0, 100)).Build());
        for (var i = 0; i < 200 && (i == 0 || engine.IsProvisional); i++)
        {
            backend.Images.Sent();
            engine.RenderFrame();
        }

        return engine;
    }

    private static Vector2 Pixel(PlotEngine engine, double x, double y) =>
        engine.ViewToPixel(Panel, ViewRange.FromValues(x, x), ViewRange.FromValues(y, y));
}
