using System.Numerics;
using HyperPlot.Interaction;
using HyperPlot.Rendering;
using HyperPlot.Testing;

namespace HyperPlot.Tests;

/// <summary>The hit test and the hover of a point layer on the CPU path (design §6.3, §10 R3a row).</summary>
public sealed class PointHitTests
{
    private static readonly PanelId Panel = new(0, 0);
    private static readonly double[] OneX = [2.0];
    private static readonly double[] OneY = [3.0];
    private static readonly double[] TwoXs = [2.0, 8.0];
    private static readonly double[] TwoValues = [double.NaN, 1.0];

    /// <summary>
    /// Points answer from the pixels last drawn (design §6.3): in X mode the nearest along within the radius, and of those as
    /// near the nearest across; in XY the nearest center within the radius and the marker's own; equal distances keep the
    /// later row; the x and y are the point's.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Points (2, 2), (2, 6), (5, 5), (5, 5), and (8, 1), discs of 6 DIPs, on domains [0, 10], 640 × 360.
    ///
    /// Steps:
    /// - X mode with a radius of 8 pixels at (2, 5.5), at (5, 1), and at (3.5, 5).
    /// - XY with a radius of 8 at (5, 5), 10 pixels to the right of (8, 1), and 12 pixels to the right of it; XY with a
    ///   radius of 0 inside the marker of (8, 1).
    ///
    /// Expected:
    /// - (2, 5.5): row 1, the nearer across of the two at x = 2. (5, 1): row 3, the later of the two at (5, 5). (3.5, 5):
    ///   nothing within 8 pixels along.
    /// - (5, 5): row 3 at distance 0. 10 pixels right of (8, 1): row 4 (within 8 + 3). 12 pixels right: nothing. Radius 0,
    ///   2 pixels from the center: row 4.
    /// </remarks>
    [Fact]
    public void Points_TakeTheNearest()
    {
        using var xs = Column.Wrap(new[] { 2.0, 2, 5, 5, 8 });
        using var ys = Column.Wrap(new[] { 2.0, 6, 5, 5, 1 });
        var table = DataTable.Create().Add("x", xs).Add("y", ys).Build();
        using var engine = Drawn(Domains(Plot.Create(table).Point(table.Col<double>("x"), table.Col<double>("y"))).Build());
        var hits = new SeriesHit[2];

        var column = Assert.Single(hits[..engine.HitTest(Pixel(engine, 2, 5.5), 8, HitMode.X, hits)]);
        Assert.Equal((1L, 2.0, 6.0), (column.Row, column.X, column.Y));
        Assert.Equal(3, Assert.Single(hits[..engine.HitTest(Pixel(engine, 5, 1), 8, HitMode.X, hits)]).Row);
        Assert.Equal(0, engine.HitTest(Pixel(engine, 3.5, 5), 8, HitMode.X, hits));

        var exact = Assert.Single(hits[..engine.HitTest(Pixel(engine, 5, 5), 8, HitMode.XY, hits)]);
        Assert.Equal((3L, 0f), (exact.Row, exact.Distance));
        var corner = Pixel(engine, 8, 1);
        var near = Assert.Single(hits[..engine.HitTest(corner + new Vector2(10, 0), 8, HitMode.XY, hits)]);
        Assert.Equal((4L, 8.0, 1.0), (near.Row, near.X, near.Y));
        Assert.Equal(10f, near.Distance, 3);
        Assert.Equal(0, engine.HitTest(corner + new Vector2(12, 0), 8, HitMode.XY, hits));
        Assert.Equal(4, Assert.Single(hits[..engine.HitTest(corner + new Vector2(0, 2), 0, HitMode.XY, hits)]).Row);
    }

    /// <summary>
    /// A flipped layer reads its x along the target's vertical, a polar panel answers nothing, a point that does not map is
    /// passed, and a layer split by categories answers with the category as its series and the row of the table (design §6.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Points (2, 2), (NaN, 5), (8, 6) with categories a, b, b.
    ///
    /// Steps:
    /// - Flipped: X mode at the pixel of (8, 6). Polar: XY at the panel's middle with a radius of 1,000.
    /// - Unsplit: XY with a radius of 1,000 at the pixel of (5, 5). Split by the categories: XY at the pixel of (8, 6).
    ///
    /// Expected:
    /// - Flipped: row 2. Polar: 0 hits. Unsplit: a row other than 1. Split: series 1 (category b), row 2.
    /// </remarks>
    [Fact]
    public void Points_FlippedPolarMissingAndSplit()
    {
        using var xs = Column.Wrap(new[] { 2.0, double.NaN, 8 });
        using var ys = Column.Wrap(new[] { 2.0, 5, 6 });
        var table = DataTable.Create().Add("x", xs).Add("y", ys).Add("c", Column.Categorical(["a", "b", "b"])).Build();
        var (x, y) = (table.Col<double>("x"), table.Col<double>("y"));
        var hits = new SeriesHit[4];

        using var flipped = Drawn(Domains(Plot.Create(table).Point(x, y)).Coord(Coord.Cartesian(flip: true)).Build());
        Assert.Equal(2, Assert.Single(hits[..flipped.HitTest(Pixel(flipped, 8, 6), 8, HitMode.X, hits)]).Row);

        using var polar = Drawn(Domains(Plot.Create(table).Point(x, y)).Coord(Coord.Polar()).Build());
        Assert.Equal(0, polar.HitTest(Inside(polar, 0.5, 0.5), 1000, HitMode.XY, hits));

        using var plain = Drawn(Domains(Plot.Create(table).Point(x, y)).Build());
        Assert.NotEqual(1, Assert.Single(hits[..plain.HitTest(Pixel(plain, 5, 5), 1000, HitMode.XY, hits)]).Row);

        using var split = Drawn(Domains(Plot.Create(table).Point(x, y, p => p.ColorBy(table.Cat("c")))).Build());
        var found = hits[..split.HitTest(Pixel(split, 8, 6), 8, HitMode.XY, hits)];
        var hit = Assert.Single(found);
        Assert.Equal((1, 2L), (hit.Series, hit.Row));
    }

    /// <summary>A point whose color value is NaN is not drawn, and the hit test does not answer with it (design §4.1, §6.3).</summary>
    /// <remarks>
    /// Preconditions:
    /// - Points (2, 2) and (8, 8) colored by the numbers NaN and 1 through a sequential scale.
    ///
    /// Steps:
    /// - XY with a radius of 8 pixels at the pixel of each point.
    ///
    /// Expected:
    /// - Nothing at (2, 2); row 1 at (8, 8).
    /// </remarks>
    [Fact]
    public void Points_WithoutAColor_AreNotHit()
    {
        using var xs = Column.Wrap(TwoXs);
        using var values = Column.Wrap(TwoValues);
        var table = DataTable.Create().Add("x", xs).Add("v", values).Build();
        var x = table.Col<double>("x");
        using var engine = Drawn(
            Domains(Plot.Create(table).Point(x, x, p => p.ColorBy(table.Col<double>("v")))).ScaleColor(Scale.Sequential()).Build()
        );
        var hits = new SeriesHit[2];

        Assert.Equal(0, engine.HitTest(Pixel(engine, 2, 2), 8, HitMode.XY, hits));
        Assert.Equal(1, Assert.Single(hits[..engine.HitTest(Pixel(engine, 8, 8), 8, HitMode.XY, hits)]).Row);
    }

    /// <summary>The hover's tooltip names a point layer "point", and its marker takes the layer's color made opaque (design §6.3).</summary>
    /// <remarks>
    /// Preconditions:
    /// - One point (2, 3) in a half-transparent orange; the default hover.
    ///
    /// Steps:
    /// - Hover at the point and draw.
    ///
    /// Expected:
    /// - The tooltip reads "point: 2, 3"; the hover's disc is the orange made opaque.
    /// </remarks>
    [Fact]
    public void Hover_NamesAndMarksAPoint()
    {
        using var xs = Column.Wrap(OneX);
        using var ys = Column.Wrap(OneY);
        var table = DataTable.Create().Add("x", xs).Add("y", ys).Build();
        var orange = new Color(240, 140, 20, 100);
        var backend = new NullBackend();
        using var engine = Drawn(
            Domains(Plot.Create(table).Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Color(orange))).Build(),
            backend
        );
        engine.OnPointer(
            new PointerEvent(
                PointerEventKind.Move,
                Pixel(engine, 2, 3),
                PointerButtons.None,
                PointerButtons.None,
                ModifierKeys.None,
                PointerKind.Mouse,
                0
            )
        );
        engine.RenderFrame();

        var list = backend.LastList!;
        Assert.Contains(list.Texts.ToArray(), t => t.Text == "point: 2, 3");
        Assert.Equal(Premultiplied(orange with { A = 255 }), list.SectorCalls[^1].Color);
    }

    /// <summary>A warm hit test over 131,072 points, and a recording of them again, allocate nothing (invariant 3).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A point layer of 131,072 rows, drawn once.
    ///
    /// Steps:
    /// - Hit test in both modes once and draw five views once to warm; hit test 20 times in each mode and draw the five views
    ///   again, as a warm-up; collect; then the same once more, counting the thread's allocations.
    ///
    /// Expected:
    /// - 0 bytes.
    /// </remarks>
    [Fact]
    public void HitTestAndRecord_AllocateNothing()
    {
        var count = LayerPlan.MaxElements;
        using var xs = Column.Wrap(Enumerable.Range(0, count).Select(i => (i % 1000) / 100.0).ToArray());
        using var ys = Column.Wrap(Enumerable.Range(0, count).Select(i => (i / 1000) / 14.0).ToArray());
        var table = DataTable.Create().Add("x", xs).Add("y", ys).Build();
        using var engine = Drawn(Domains(Plot.Create(table).Point(table.Col<double>("x"), table.Col<double>("y"))).Build());
        var hits = new SeriesHit[2];
        var cursor = Pixel(engine, 5, 5);
        _ = engine.HitTest(cursor, 8, HitMode.X, hits);
        _ = engine.HitTest(cursor, 8, HitMode.XY, hits);

        // The views are made before the count: a view is an object of the test's own.
        var views = Enumerable.Range(0, 5).Select(i => new PanelView2D(ViewRange.FromValues(1 + (0.1 * i), 9), ViewRange.FromValues(1, 9))).ToArray();

        // Each view is drawn once before the count: a view's tick labels are shaped the first time they appear (row I1c).
        foreach (var view in views)
        {
            Move(view);
        }

        PerformanceAssert.DoesNotAllocate(() =>
        {
            for (var i = 0; i < 20; i++)
            {
                _ = engine.HitTest(cursor, 8, HitMode.X, hits);
                _ = engine.HitTest(cursor, 8, HitMode.XY, hits);
            }

            foreach (var view in views)
            {
                Move(view);
            }
        });

        void Move(PanelView2D view)
        {
            engine.Views.SetView(Panel, view);
            engine.RenderFrame();
        }
    }

    private static PlotBuilder Domains(PlotBuilder builder) =>
        builder.ScaleX(Scale.Linear().WithDomain(0, 10)).ScaleY(Scale.Linear().WithDomain(0, 10));

    private static PlotEngine Drawn(PlotSpec spec, NullBackend? backend = null)
    {
        var engine = new PlotEngine(backend ?? new NullBackend(), 640, 360) { WaitsForIndexes = true };
        engine.SetPlot(spec);
        engine.RenderFrame();
        return engine;
    }

    private static Vector2 Inside(PlotEngine engine, double fx, double fy)
    {
        var view = (PanelView2D)engine.CurrentView(Panel);
        var x = view.X.LowValue + (fx * (view.X.HighValue - view.X.LowValue));
        var y = view.Y.LowValue + (fy * (view.Y.HighValue - view.Y.LowValue));
        return engine.ViewToPixel(Panel, ViewRange.FromValues(x, x), ViewRange.FromValues(y, y));
    }

    private static Vector2 Pixel(PlotEngine engine, double x, double y) =>
        engine.ViewToPixel(Panel, ViewRange.FromValues(x, x), ViewRange.FromValues(y, y));

    private static Vector4 Premultiplied(Color color) =>
        new(
            HyperPlot.Rendering.Premultiplied.Multiply(color.R, color.A) / 255f,
            HyperPlot.Rendering.Premultiplied.Multiply(color.G, color.A) / 255f,
            HyperPlot.Rendering.Premultiplied.Multiply(color.B, color.A) / 255f,
            color.A / 255f
        );
}
