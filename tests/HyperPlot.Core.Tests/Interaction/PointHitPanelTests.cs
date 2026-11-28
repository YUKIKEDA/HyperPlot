using System.Numerics;
using HyperPlot.Interaction;
using HyperPlot.Testing;

namespace HyperPlot.Tests;

/// <summary>
/// A point layer answers only points whose center is drawn inside the panel, on the CPU path and on the index path alike
/// (design §6.3 "散布の点が答える範囲", §10 R3c row, Issue #569); series, bars, and the selection's rows are unchanged.
/// </summary>
[Collection("Large data")]
public sealed class PointHitPanelTests
{
    // The CPU path's most rows; one more takes the index path (design §5.5.2).
    private const int Cpu = LayerPlan.MaxElements;
    private const float Radius = 8;
    private static readonly PanelId Panel = new(0, 0);

    /// <summary>The paths a point layer takes by its rows.</summary>
    public enum Path
    {
        /// <summary>131,072 rows: the pixels of the last recording.</summary>
        Cpu,

        /// <summary>131,073 rows: the search over the index.</summary>
        Index,
    }

    /// <summary>
    /// In X a point whose y lies outside the view does not answer, though its x is the cursor's, and a point of the same
    /// column inside the view does; with no point of the column in the view nothing answers. In XY a point whose center lies
    /// a pixel past the panel's edge does not answer, though its marker reaches into the panel and it is the nearest, and a
    /// point centered on the edge does. Both paths give the same answers, unflipped and flipped.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Domains [0, 100]², 640 × 360, discs of 6 DIPs, the view [40, 60]². Rows 0 to 4: A (50, 70) above the view; B 3 pixels
    ///   right of x = 50 at y = 55; E (45, 30) below the view; C a pixel right of the view's right edge at y = 50; D on the
    ///   right edge, x = 60, 3 pixels above y = 50. The other rows are seeded over [0, 10]², outside the view; the index path's
    ///   one more row lies at (1, 1).
    /// - Flipped: the same points, with x along the target's vertical (C and D then lie past and on the top edge).
    ///
    /// Steps:
    /// - X with a radius of 8 pixels at the pixel of (50, 50) and of (45, 50).
    /// - XY and X with a radius of 8 at 2 pixels inside the right edge (the top edge when flipped) at y = 50.
    ///
    /// Expected:
    /// - (50, 50) in X: B, though A lies 0 pixels along (before Issue #569, A answered). (45, 50) in X: nothing.
    /// - Inside the edge: D in XY, though C is nearer (before, C answered), and D in X.
    /// </remarks>
    [Theory]
    [InlineData(Path.Cpu, false)]
    [InlineData(Path.Index, false)]
    [InlineData(Path.Cpu, true)]
    [InlineData(Path.Index, true)]
    public void Points_AnswerOnlyWhatIsDrawnInThePanel(Path path, bool flip)
    {
        var pixel = UnitsAPixel(flip);
        var special = new (double X, double Y)[]
        {
            (50, 70),
            (50 + (3 * pixel.Along), 55),
            (45, 30),
            (60 + pixel.Along, 50),
            (60, 50 + (3 * pixel.Across)),
        };
        using var scene = Drawn(path, special, flip, clamp: false, (40, 60, 40, 60));
        var engine = scene.Engine;
        var hits = new SeriesHit[2];

        Assert.Equal(1, Answer(engine, Pixel(engine, 50, 50), HitMode.X, hits));
        Assert.Equal(-1, Answer(engine, Pixel(engine, 45, 50), HitMode.X, hits));

        // 2 pixels inside the edge that x = 60 lies on: the right edge, or the top one when flipped.
        var edge = Pixel(engine, 60, 50);
        var cursor = flip ? edge + new Vector2(0, 2) : edge - new Vector2(2, 0);
        Assert.Equal(4, Answer(engine, cursor, HitMode.XY, hits));
        Assert.Equal(4, Answer(engine, cursor, HitMode.X, hits));
    }

    /// <summary>
    /// On an axis that clamps, a point whose value lies past the view is drawn on the panel's edge and answers (design §6.3
    /// "散布の点が答える範囲"), on both paths.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - x on the domain [40, 60] clamped, y on [0, 100], 640 × 360, the view the domains. Row 0, F (70, 50), right of the
    ///   view; the other rows seeded over [0, 10]² (x clamped to the left edge, y below the view).
    ///
    /// Steps:
    /// - Draw at the view [40, 60] × [40, 60]. XY and X with a radius of 8 at 2 pixels left of the right edge at y = 50.
    ///
    /// Expected:
    /// - F in both modes, at a distance of 2 pixels.
    /// </remarks>
    [Theory]
    [InlineData(Path.Cpu)]
    [InlineData(Path.Index)]
    public void ClampedPoints_OnTheEdge_Answer(Path path)
    {
        using var scene = Drawn(path, [(70, 50)], flip: false, clamp: true, (40, 60, 40, 60));
        var engine = scene.Engine;
        var hits = new SeriesHit[2];
        var cursor = Pixel(engine, 60, 50) - new Vector2(2, 0);
        foreach (var mode in (HitMode[])[HitMode.XY, HitMode.X])
        {
            var hit = Assert.Single(hits[..engine.HitTest(cursor, Radius, mode, hits)]);
            Assert.Equal(0, hit.Row);
            Assert.Equal(2, hit.Distance, 3);
        }
    }

    /// <summary>
    /// A point answers from the frame whose view brings it into the panel, and no longer from the frame whose view takes it
    /// out (design §6.3 "散布の点が答える範囲"), on both paths.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The points of <see cref="Points_AnswerOnlyWhatIsDrawnInThePanel"/>, unflipped, at the view [40, 60]².
    ///
    /// Steps:
    /// - Set the view [40, 60] × [60, 80] and draw; X with a radius of 8 at the pixel of (50, 70).
    /// - Set the view [40, 60]² back and draw; X at the pixel of (50, 50).
    ///
    /// Expected:
    /// - A in the first view (B, at y = 55, is now below it); B in the second.
    /// </remarks>
    [Theory]
    [InlineData(Path.Cpu)]
    [InlineData(Path.Index)]
    public void Points_AnswerFromTheFrameThatDrawsThemInThePanel(Path path)
    {
        var pixel = UnitsAPixel(flip: false);
        using var scene = Drawn(path, [(50, 70), (50 + (3 * pixel.Along), 55)], flip: false, clamp: false, (40, 60, 40, 60));
        var engine = scene.Engine;
        var hits = new SeriesHit[2];

        Show(scene, (40, 60, 60, 80));
        Assert.Equal(0, Answer(engine, Pixel(engine, 50, 70), HitMode.X, hits));

        Show(scene, (40, 60, 40, 60));
        Assert.Equal(1, Answer(engine, Pixel(engine, 50, 50), HitMode.X, hits));
    }

    /// <summary>
    /// A line's and a bar's X answer the row nearest the cursor's x whatever its y, also outside the view, and a selection's x
    /// range holds the rows whose y lies outside the view, on both paths of the points (design §6.3 "散布の点が答える範囲":
    /// series, bars, and the selection are unchanged).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A line and bars over x = 0, 10, …, 100 with y = 90 at x = 50 and 5 elsewhere, at the view [40, 60]² on domains
    ///   [0, 100]², 640 × 360, on an engine that waits for its indexes: the first frame is not provisional (the guard of
    ///   Issue #637; on an engine that does not wait, the first frame is always provisional).
    /// - The points of <see cref="Points_AnswerOnlyWhatIsDrawnInThePanel"/> on each path at the same view.
    ///
    /// Steps:
    /// - X with a radius of 8 at the pixel of (50, 50) over the line, and over the bars.
    /// - Select the x range [45, 55] on the points and ask the layer's rows.
    ///
    /// Expected:
    /// - The line and the bars: row 5 (y = 90, above the view).
    /// - The points: rows 0 (A, above the view) and 1 (B), and none of the others.
    /// </remarks>
    [Fact]
    public void SeriesBarsAndSelection_KeepAnsweringOutsideTheView()
    {
        double[] lineX = [0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100];
        var lineY = lineX.Select(v => v == 50 ? 90.0 : 5).ToArray();
        using var x = Column.Wrap(lineX);
        using var y = Column.Wrap(lineY);
        var table = DataTable.Create().Add("x", x).Add("y", y).Build();
        var hits = new SeriesHit[2];
        foreach (var bars in (bool[])[false, true])
        {
            var builder = Plot.Create(table);
            builder = bars
                ? builder.Bar(table.Col<double>("x"), table.Col<double>("y"))
                : builder.Line(table.Col<double>("x"), table.Col<double>("y"));

            // The frames wait for the X statistics and the pyramid: until the workers compute them, the layer is pending and
            // answers nothing, and a busy shared pool can be late past the second frame (Issue #637).
            using var engine = new PlotEngine(new NullBackend(), 640, 360) { WaitsForIndexes = true };
            engine.SetPlot(builder.ScaleX(Scale.Linear().WithDomain(0, 100)).ScaleY(Scale.Linear().WithDomain(0, 100)).Build());
            engine.RenderFrame();
            Assert.False(engine.IsProvisional, $"bars {bars}: the first frame has its indexes.");
            engine.Views.SetView(Panel, new PanelView2D(ViewRange.FromValues(40.0, 60.0), ViewRange.FromValues(40.0, 60.0)));
            engine.RenderFrame();
            var hit = Assert.Single(hits[..engine.HitTest(Pixel(engine, 50, 50), Radius, HitMode.X, hits)]);
            Assert.Equal((5L, 90.0), (hit.Row, hit.Y));
        }

        var pixel = UnitsAPixel(flip: false);
        foreach (var path in (Path[])[Path.Cpu, Path.Index])
        {
            using var scene = Drawn(path, [(50, 70), (50 + (3 * pixel.Along), 55), (45 - 1, 30)], flip: false, clamp: false, (40, 60, 40, 60));
            var engine = scene.Engine;
            engine.Select(Selection2D.XRange(Panel, ViewRange.FromValues(45.0, 55.0)));
            var rows = Rows(engine.SelectedRowsAsync(0, 0).AsTask());
            Assert.Equal(2, rows.Count);
            Assert.True(rows.Contains(0) && rows.Contains(1), $"{path}: the rows of A and B are selected.");
        }
    }

    // The rows once their task ends, waited for with a watchdog on the test's thread (.cursor/rules/testing.mdc).
    private static RowSelection Rows(Task<RowSelection> task)
    {
        Eventually.Completes(task);
        return task.Result;
    }

    // The row of the single hit, or −1 when nothing answers.
    private static long Answer(PlotEngine engine, Vector2 cursor, HitMode mode, SeriesHit[] hits)
    {
        var found = engine.HitTest(cursor, Radius, mode, hits);
        Assert.InRange(found, 0, 1);
        return found == 0 ? -1 : hits[0].Row;
    }

    // The units of x and of y a physical pixel spans at the view [40, 60]², along the side each runs on, from a plot of the
    // same domains and size: the points are placed by it before the plot that draws them is made.
    private static (double Along, double Across) UnitsAPixel(bool flip)
    {
        using var probe = Drawn(Path.Cpu, [], flip, clamp: false, (40, 60, 40, 60));
        var (low, high) = (Pixel(probe.Engine, 40, 40), Pixel(probe.Engine, 60, 60));
        var (alongPixels, acrossPixels) = flip ? (high.Y - low.Y, high.X - low.X) : (high.X - low.X, high.Y - low.Y);
        return (20 / Math.Abs((double)alongPixels), 20 / Math.Abs((double)acrossPixels));
    }

    // A point layer over the special points first, then seeded points over [0, 10]² to the path's rows: 131,072 in all on
    // the CPU path, one more at (1, 1) on the index path. Drawn at the view until every page is on the GPU.
    private static Scene Drawn(Path path, (double X, double Y)[] special, bool flip, bool clamp, (double X0, double X1, double Y0, double Y1) view)
    {
        var count = path == Path.Cpu ? Cpu : Cpu + 1;
        var random = new Random(41);
        var xs = new double[count];
        var ys = new double[count];
        for (var i = 0; i < count; i++)
        {
            (xs[i], ys[i]) =
                i < special.Length ? special[i]
                : i == Cpu ? (1, 1)
                : (random.NextDouble() * 10, random.NextDouble() * 10);
        }

        var table = DataTable.Create().Add("x", Column.Wrap(xs)).Add("y", Column.Wrap(ys)).Build();
        var builder = Plot.Create(table)
            .Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Draw(PointDraw.Points).Size(6))
            .ScaleX(clamp ? Scale.Linear().WithDomain(40, 60) with { Clamp = true } : Scale.Linear().WithDomain(0, 100))
            .ScaleY(Scale.Linear().WithDomain(0, 100));
        if (flip)
        {
            builder = builder.Coord(Coord.Cartesian(flip: true));
        }

        var backend = new NullBackend();
        var scene = new Scene(new PlotEngine(backend, 640, 360) { WaitsForIndexes = true }, backend);
        scene.Engine.SetPlot(builder.Build());
        scene.Engine.RenderFrame();
        Assert.Equal(path == Path.Index, scene.Engine.Plan!.Layers[0] is IndexedPointPlan);
        Show(scene, view);
        return scene;
    }

    // Sets a view and draws until every page is on the GPU.
    private static void Show(Scene scene, (double X0, double X1, double Y0, double Y1) view)
    {
        var (engine, backend) = (scene.Engine, scene.Backend);
        engine.Views.SetView(Panel, new PanelView2D(ViewRange.FromValues(view.X0, view.X1), ViewRange.FromValues(view.Y0, view.Y1)));
        for (var i = 0; i < 200 && (i == 0 || engine.IsProvisional); i++)
        {
            backend.Images.Sent();
            engine.RenderFrame();
        }

        Assert.False(engine.IsProvisional);
    }

    private static Vector2 Pixel(PlotEngine engine, double x, double y) =>
        engine.ViewToPixel(Panel, ViewRange.FromValues(x, x), ViewRange.FromValues(y, y));

    /// <summary>A plot's engine and the backend it draws on.</summary>
    private sealed record Scene(PlotEngine Engine, NullBackend Backend) : IDisposable
    {
        public void Dispose() => Engine.Dispose();
    }
}
