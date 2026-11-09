using System.Numerics;
using HyperPlot.Interaction;
using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>
/// The selection (design §6.4 "選択", §10 "選択（I3a）", requirements I-3 and I-7, row I3a, Issue #364): the tool and its
/// bindings, the value and its notices, the selected rows checked against every row, and the highlight.
/// </summary>
public sealed class SelectionTests
{
    private static readonly PanelId Panel = new(0, 0);

    /// <summary>The default bindings select with Shift (a rectangle, an x range on the x axis, a lasso with Alt, points by a click), and Escape has two entries.</summary>
    /// <remarks>
    /// Preconditions:
    /// - The default bindings.
    ///
    /// Steps:
    /// - Look up the bindings of a left drag with Shift over a panel and over the x axis, with Shift and Alt over a panel, a
    ///   left click with Shift, and Escape.
    ///
    /// Expected:
    /// - Selection tools of the kinds Rectangle, XRange, Lasso, and Points; Escape's entries are CancelTool, then ClearSelection.
    /// </remarks>
    [Fact]
    public void Defaults_BindTheSelectionTools()
    {
        var bindings = InputBindings.CreateDefault();
        SelectionKind KindOf(InputGestureKind gesture, ModifierKeys modifiers, HitAreaKind area)
        {
            var binding = bindings.Single(b =>
                b.Gesture.Kind == gesture && b.Modifiers == modifiers && (b.Areas & area) != 0 && b.Tool is SelectionTool
            );
            return Assert.IsType<SelectionTool>(binding.Tool).Kind;
        }

        Assert.Equal(SelectionKind.Rectangle, KindOf(InputGestureKind.Drag, ModifierKeys.Shift, HitAreaKind.Panel));
        Assert.Equal(SelectionKind.XRange, KindOf(InputGestureKind.Drag, ModifierKeys.Shift, HitAreaKind.AxisX));
        Assert.Equal(SelectionKind.Lasso, KindOf(InputGestureKind.Drag, ModifierKeys.Shift | ModifierKeys.Alt, HitAreaKind.Panel));
        Assert.Equal(SelectionKind.Points, KindOf(InputGestureKind.Click, ModifierKeys.Shift, HitAreaKind.Panel));
        var escape = bindings.Where(b => b.Gesture.Kind == InputGestureKind.Key && b.Gesture.Key == PlotKey.Escape).Select(b => b.Command).ToArray();
        Assert.Equal([ViewCommand.CancelTool, ViewCommand.ClearSelection], escape);
    }

    /// <summary>
    /// A drag with Shift selects the rectangle it spans, replacing the selection before; the selection stays through a zoom,
    /// Escape clears it when no tool runs, and each change is told once (Issue #364 Decisions 3, 6, 7, 8).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The engine fixture.
    ///
    /// Steps:
    /// - Drag with Shift from 30 % to 60 % of the panel; drag again from 50 % to 80 %; zoom with the wheel and let it end;
    ///   press Escape twice.
    ///
    /// Expected:
    /// - After the first drag a Rectangle of panel 0 whose x and y ranges are the view's values at the drag's corners; the
    ///   second replaces it; the zoom leaves it; the first Escape clears it and the second is not used. SelectionChanged came
    ///   three times, each for plot 0.
    /// </remarks>
    [Fact]
    public void Drag_SelectsARectangleThatStaysUntilEscape()
    {
        using var f = new EngineFixture();
        var told = new List<int>();
        f.Engine.SelectionChanged += told.Add;
        var (a, b) = (At(f, 0.3, 0.3), At(f, 0.6, 0.6));
        var (va, vb) = (View(f, a), View(f, b));
        f.Drag(a, b, PointerButtons.Left, ModifierKeys.Shift);

        var first = Assert.IsType<Selection2D>(f.Engine.Selection(0));
        Assert.Equal((SelectionKind.Rectangle, Panel), (first.Kind, first.Panel));
        Assert.Equal(Math.Min(va.X.LowValue, vb.X.LowValue), first.X.LowValue, 6);
        Assert.Equal(Math.Max(va.X.LowValue, vb.X.LowValue), first.X.HighValue, 6);
        Assert.Equal(Math.Min(va.Y.LowValue, vb.Y.LowValue), first.Y.LowValue, 6);
        Assert.Equal(Math.Max(va.Y.LowValue, vb.Y.LowValue), first.Y.HighValue, 6);

        f.Drag(At(f, 0.5, 0.5), At(f, 0.8, 0.8), PointerButtons.Left, ModifierKeys.Shift);
        var second = f.Engine.Selection(0);
        Assert.NotSame(first, second);

        f.Wheel(f.Middle, 2);
        f.Settle();
        Assert.Same(second, f.Engine.Selection(0));

        Assert.True(f.Key(PlotKey.Escape));
        Assert.Null(f.Engine.Selection(0));
        Assert.False(f.Key(PlotKey.Escape));
        Assert.Equal([0, 0, 0], told);
    }

    /// <summary>
    /// A drag with Shift over the x axis selects an x range whose y is the panel's whole; a lasso keeps vertices a DIP apart,
    /// at most 1,024, as shares of the bounds it spans (Issue #364 Decision 11); a click with Shift adds the points it hits.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The engine fixture.
    ///
    /// Steps:
    /// - Drag with Shift along the x axis below the panel. Drag with Shift and Alt round a circle in 3,000 steps. Click with
    ///   Shift on the line's point at row 300, then at row 600.
    ///
    /// Expected:
    /// - An XRange whose y is the panel's current y range. A Lasso of 3 to 1,024 vertices, each share in [0, 1]. Points: one
    ///   point of row about 300, then two points.
    /// </remarks>
    [Fact]
    public void Tools_SelectAnXRangeALassoAndPoints()
    {
        using var f = new EngineFixture();
        var axis = new Vector2(0, f.Rect.Top + f.Rect.Height + 8);
        f.Drag(axis with { X = At(f, 0.2, 0).X }, axis with { X = At(f, 0.4, 0).X }, PointerButtons.Left, ModifierKeys.Shift);
        var range = Assert.IsType<Selection2D>(f.Engine.Selection(0));
        Assert.Equal(SelectionKind.XRange, range.Kind);
        Assert.Equal(f.View().Y, range.Y);

        var path = Enumerable
            .Range(0, 3001)
            .Select(i => f.Middle + (60 * new Vector2(MathF.Cos(i * MathF.Tau / 3000), MathF.Sin(i * MathF.Tau / 3000))))
            .ToArray();
        f.Engine.OnPointer(f.Pointer(PointerEventKind.Down, path[0], PointerButtons.Left, ModifierKeys.Shift | ModifierKeys.Alt));
        foreach (var point in path[1..])
        {
            f.Engine.OnPointer(
                f.Pointer(PointerEventKind.Move, point, PointerButtons.None, ModifierKeys.Shift | ModifierKeys.Alt) with
                {
                    Buttons = PointerButtons.Left,
                }
            );
        }

        f.Engine.OnPointer(f.Pointer(PointerEventKind.Up, path[^1], PointerButtons.Left, ModifierKeys.Shift | ModifierKeys.Alt));
        var lasso = Assert.IsType<Selection2D>(f.Engine.Selection(0));
        Assert.Equal(SelectionKind.Lasso, lasso.Kind);
        Assert.InRange(lasso.Polygon.Length, 3, 1024);
        Assert.All(lasso.Polygon.ToArray(), v => Assert.True(v.X is >= 0 and <= 1 && v.Y is >= 0 and <= 1));

        Click(f, LinePoint(f, 300));
        var one = Assert.IsType<Selection2D>(f.Engine.Selection(0));
        Assert.Equal(SelectionKind.Points, one.Kind);
        Assert.InRange(Assert.Single(one.Points.ToArray()).Row, 295, 305);
        Click(f, LinePoint(f, 600));
        Assert.Equal(2, f.Engine.Selection(0)!.Points.Length);
    }

    /// <summary>
    /// <see cref="PlotEngine.Select"/> checks what it takes (design §6.4): the panel, polar, the content of each kind; and a
    /// new spec of another mapping drops the selection with a notice (Issue #364 Decision 8).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The engine fixture; a plot in polar.
    ///
    /// Steps:
    /// - Select panel 5; a rectangle of NaN; a lasso of 2 vertices; points of none; a rectangle in polar. Select a valid
    ///   rectangle, then set a spec whose y scale is symlog, and draw.
    ///
    /// Expected:
    /// - ArgumentOutOfRangeException for the panel; ArgumentException for the others. The spec of another mapping clears the
    ///   selection and tells once.
    /// </remarks>
    [Fact]
    public void Select_ChecksAndANewMappingClears()
    {
        using var f = new EngineFixture();
        var x = ViewRange.FromValues(10.0, 20.0);
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => f.Engine.Select(Selection2D.Rectangle(new PanelId(0, 5), x, x)));
        _ = Assert.Throws<ArgumentException>(() => f.Engine.Select(Selection2D.Rectangle(Panel, x, new ViewRange(0, double.NaN, 1))));
        _ = Assert.Throws<ArgumentException>(() => f.Engine.Select(Selection2D.Lasso(Panel, x, x, [Vector2.Zero, Vector2.One])));
        _ = Assert.Throws<ArgumentException>(() => f.Engine.Select(Selection2D.OfPoints(Panel, [])));

        using var polar = new EngineFixture(b => b.Coord(Coord.Polar()));
        _ = Assert.Throws<ArgumentException>(() => polar.Engine.Select(Selection2D.Rectangle(Panel, x, x)));

        var told = 0;
        f.Engine.SelectionChanged += _ => told++;
        f.Engine.Select(Selection2D.Rectangle(Panel, x, ViewRange.FromValues(-5.0, 5.0)));
        f.Engine.SetPlot(f.Spec with { ScaleY = Scale.Symlog() });
        f.Engine.RenderFrame();
        Assert.Null(f.Engine.Selection(0));
        Assert.Equal(2, told);
    }

    /// <summary>
    /// The selected rows of a line, checked against every row (Issue #364 Decisions 4, 9, 10): an x range is one run found by
    /// search; a rectangle and a lasso are a bit a row the workers set; points are their rows.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The engine fixture: a line of 1,000 points, y = 10 · sin(0.02 · i).
    ///
    /// Steps:
    /// - Select the x range [100.5, 300.2]; the rectangle [100, 700] × [−3, 5]; a lasso of the triangle (200, −10), (800, −10),
    ///   (500, 10); points at rows 3 and 9. Ask the rows of layer 0 each time.
    ///
    /// Expected:
    /// - The x range: one run, rows 101 to 300. The rectangle and the lasso: exactly the rows whose (x, y) lies inside. The
    ///   points: rows 3 and 9. None derived.
    /// </remarks>
    [Fact]
    public void SelectedRows_OfALineMatchEveryRow()
    {
        using var f = new EngineFixture();
        static double Y(int i) => MathF.Sin(i * 0.02f) * 10;

        f.Engine.Select(Selection2D.XRange(Panel, ViewRange.FromValues(100.5, 300.2)));
        var run = SelectedRows(f.Engine);
        Assert.Equal([(101L, 301L)], run.Ranges());

        f.Engine.Select(Selection2D.Rectangle(Panel, ViewRange.FromValues(100.0, 700.0), ViewRange.FromValues(-3.0, 5.0)));
        var box = SelectedRows(f.Engine);
        var inBox = Enumerable.Range(0, 1000).Where(i => i is >= 100 and <= 700 && Y(i) is >= -3 and <= 5).ToArray();
        Assert.Equal(inBox.Length, box.Count);
        Assert.All(inBox, i => Assert.True(box.Contains(i)));

        Vector2[] triangle = [new(0, 0), new(1, 0), new(0.5f, 1)];
        f.Engine.Select(Selection2D.Lasso(Panel, ViewRange.FromValues(200.0, 800.0), ViewRange.FromValues(-10.0, 10.0), triangle));
        var lasso = SelectedRows(f.Engine);
        bool InTriangle(int i)
        {
            var (u, v) = ((i - 200) / 600.0, (Y(i) + 10) / 20.0);
            return v >= 0 && v <= 1 && u >= v / 2 && u <= 1 - (v / 2);
        }

        var inLasso = Enumerable.Range(200, 601).Where(InTriangle).ToArray();
        Assert.InRange(lasso.Count, inLasso.Length - 2, inLasso.Length + 2);
        Assert.False(lasso.Derived);

        var hits = new SeriesHit[2];
        _ = f.Engine.HitTest(LinePoint(f, 3), 8, HitMode.XY, hits);
        var three = hits[0];
        _ = f.Engine.HitTest(LinePoint(f, 9), 8, HitMode.XY, hits);
        f.Engine.Select(Selection2D.OfPoints(Panel, [three, hits[0]]));
        var points = SelectedRows(f.Engine);
        Assert.Equal(2, points.Count);
        Assert.True(points.Contains(three.Row) && points.Contains(hits[0].Row));
    }

    /// <summary>
    /// The element layers' rows (Issue #364 Decision 10): a bar meets a rectangle when its x is inside and its span from the
    /// base meets the y range; an error bar by its line; a rectangle by overlapping; a rule answers nothing; a split layer
    /// answers with the table's rows; a stat's rows are derived.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 40 rows: x = row, y = row % 8 − 2, a category "g" alternating "a" and "b".
    ///
    /// Steps:
    /// - Select the rectangle [10, 20] × [3, 4] over: bars; error bars from y − 1 to y; rectangles [x, x + 0.5] × [y, y + 0.5];
    ///   a vertical rule at x; a line split by g; a line of a density of y (x range [−10, 10]).
    ///
    /// Expected:
    /// - Bars: rows 10 to 20 whose span [min(0, y), max(0, y)] meets [3, 4]. Error bars: rows whose [y − 1, y] meets it.
    ///   Rectangles: rows whose box meets it. The rule: none. The split line: rows 10 to 20 with y in [3, 4], of both
    ///   categories, as table rows. The density: some rows, derived.
    /// </remarks>
    [Fact]
    public void SelectedRows_OfTheElementLayers()
    {
        const int Rows = 40;
        double Y(int i) => (i % 8) - 2;
        using var x = Column.Wrap(Enumerable.Range(0, Rows).Select(i => (double)i).ToArray());
        using var x1 = Column.Wrap(Enumerable.Range(0, Rows).Select(i => i + 0.5).ToArray());
        using var y = Column.Wrap(Enumerable.Range(0, Rows).Select(Y).ToArray());
        using var low = Column.Wrap(Enumerable.Range(0, Rows).Select(i => Y(i) - 1).ToArray());
        using var y1 = Column.Wrap(Enumerable.Range(0, Rows).Select(i => Y(i) + 0.5).ToArray());
        var table = DataTable
            .Create()
            .Add("x", x)
            .Add("x1", x1)
            .Add("y", y)
            .Add("lo", low)
            .Add("y1", y1)
            .Add("g", Column.Categorical([.. Enumerable.Range(0, Rows).Select(i => i % 2 == 0 ? "a" : "b")]))
            .Build();
        var (tx, ty) = (table.Col<double>("x"), table.Col<double>("y"));
        bool Meets(double a, double b) => Math.Max(a, b) >= 3 && Math.Min(a, b) <= 4;
        var inX = Enumerable.Range(10, 11).ToArray();

        RowSelection RowsOf(PlotSpec spec, Selection2D? selection = null)
        {
            using var engine = new PlotEngine(new NullBackend(), 640, 360) { WaitsForIndexes = true };
            engine.SetPlot(spec);
            engine.RenderFrame();
            engine.Select(selection ?? Selection2D.Rectangle(Panel, ViewRange.FromValues(10.0, 20.0), ViewRange.FromValues(3.0, 4.0)));
            return SelectedRows(engine);
        }

        Assert.Equal(inX.Count(i => Meets(0, Y(i))), RowsOf(Plot.Create(table).Bar(tx, ty).Build()).Count);
        Assert.Equal(inX.Count(i => Meets(Y(i) - 1, Y(i))), RowsOf(Plot.Create(table).ErrorBar(tx, table.Col<double>("lo"), ty).Build()).Count);
        Assert.Equal(
            Enumerable.Range(0, Rows).Count(i => i + 0.5 >= 10 && i <= 20 && Meets(Y(i), Y(i) + 0.5)),
            RowsOf(Plot.Create(table).Rect(tx, table.Col<double>("x1"), ty, table.Col<double>("y1")).Build()).Count
        );
        Assert.Equal(0, RowsOf(Plot.Create(table).Line(tx, ty).VRule(tx).Build() with { Layers = [new RuleLayerSpec { X = tx }] }).Count);

        var split = RowsOf(Plot.Create(table).Line(tx, ty, l => l.ColorBy(table.Cat("g"))).Build());
        var expected = inX.Where(i => Y(i) is >= 3 and <= 4).ToArray();
        Assert.Equal(expected.Length, split.Count);
        Assert.All(expected, i => Assert.True(split.Contains(i)));

        var density = RowsOf(
            new PlotSpec { Data = table, Layers = [new LineLayerSpec { X = ty, Stat = Stat.Density() }] },
            Selection2D.XRange(Panel, ViewRange.FromValues(-10.0, 10.0))
        );
        Assert.True(density.Count > 0);
        Assert.True(density.Derived);
    }

    /// <summary>
    /// The highlight (Issue #364 Decision 5): the selection's range in 15 % of the foreground, the line again twice as wide
    /// clipped to the range, points marked; changing the selection records no layer again.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The engine fixture (a line of width 1).
    ///
    /// Steps:
    /// - Select the x range [200, 400] and draw; select points at rows 3 and 9 and draw.
    ///
    /// Expected:
    /// - The x range: a rect of 15 % of the foreground the panel's height, and a polyline of width 2 clipped to the range's
    ///   pixels; the plan records nothing again, the frame copies the kept plot. Points: two markers (four sector calls).
    /// </remarks>
    [Fact]
    public void Highlight_DrawsTheRangeTheLineAndThePoints()
    {
        using var f = new EngineFixture();
        f.Engine.Select(Selection2D.XRange(Panel, ViewRange.FromValues(200.0, 400.0)));
        f.Engine.RenderFrame();
        var recorded = f.Engine.Plan!.Recordings;
        f.Engine.Select(Selection2D.XRange(Panel, ViewRange.FromValues(200.0, 401.0)));
        f.Engine.RenderFrame();
        Assert.Equal(recorded, f.Engine.Plan.Recordings);
        Assert.Equal((1, 1), (f.Backend.CopiesInFrame, f.Backend.DrawsInFrame));

        var list = f.Backend.LastList!;
        var foreground = Theme.Light.Foreground;
        var faint = Premultiplied(foreground with { A = (byte)Math.Round(foreground.A * 0.15) });
        var (left, right) = (Pixel(f, 200).X, Pixel(f, 401).X);
        Assert.Contains(list.Rects.ToArray(), r => r.Color == faint && r.Height == f.Rect.Height && Math.Abs(r.X - left) < 1);
        var thick = Assert.Single(list.Polylines.ToArray());
        Assert.Equal(2, thick.Style.Width);
        Assert.InRange(thick.Clip.Left, (long)left - 1, (long)left + 1);
        Assert.InRange(thick.Clip.Right, (long)right - 1, (long)right + 2);

        var hits = new SeriesHit[2];
        _ = f.Engine.HitTest(LinePoint(f, 3), 8, HitMode.XY, hits);
        var three = hits[0];
        _ = f.Engine.HitTest(LinePoint(f, 9), 8, HitMode.XY, hits);
        f.Engine.Select(Selection2D.OfPoints(Panel, [three, hits[0]]));
        f.Engine.RenderFrame();
        Assert.Equal(4, f.Backend.LastList!.SectorCalls.Length);
    }

    /// <summary>Warm frames of the mouse alone with a selection's highlight allocate nothing (Issue #364, §10).</summary>
    /// <remarks>
    /// Preconditions:
    /// - The engine fixture with a lasso selected.
    ///
    /// Steps:
    /// - Move over 8 pixels and draw after each, 3 times round to warm; count the thread's allocations over 5 more rounds.
    ///
    /// Expected:
    /// - 0 bytes.
    /// </remarks>
    [Fact]
    public void Highlight_WarmFramesAllocateNothing()
    {
        using var f = new EngineFixture();
        Vector2[] triangle = [new(0, 0), new(1, 0), new(0.5f, 1)];
        f.Engine.Select(Selection2D.Lasso(Panel, ViewRange.FromValues(200.0, 800.0), ViewRange.FromValues(-10.0, 10.0), triangle));
        void Round()
        {
            for (var i = 0; i < 8; i++)
            {
                f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, At(f, 0.1 + (i * 0.1), 0.5)));
                f.Engine.RenderFrame();
            }
        }

        for (var k = 0; k < 3; k++)
        {
            Round();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var k = 0; k < 5; k++)
        {
            Round();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>Returns layer 0's selected rows of plot 0, waited for on the test's thread: the engine is called from its own thread only.</summary>
    private static RowSelection SelectedRows(PlotEngine engine) => engine.SelectedRowsAsync(0, 0).AsTask().GetAwaiter().GetResult();

    private static void Click(EngineFixture f, Vector2 at)
    {
        f.Engine.OnPointer(f.Pointer(PointerEventKind.Down, at, PointerButtons.Left, ModifierKeys.Shift));
        f.Engine.OnPointer(f.Pointer(PointerEventKind.Up, at, PointerButtons.Left, ModifierKeys.Shift));
        f.Time.Advance(TimeSpan.FromSeconds(1));
    }

    private static Vector2 At(EngineFixture f, double fx, double fy) =>
        new((float)(f.Rect.Left + (fx * f.Rect.Width)), (float)(f.Rect.Top + ((1 - fy) * f.Rect.Height)));

    private static (ViewRange X, ViewRange Y) View(EngineFixture f, Vector2 pixel) => new ToolContext(f.Engine).PixelToView(Panel, pixel);

    private static Vector2 Pixel(EngineFixture f, double x) =>
        f.Engine.ViewToPixel(Panel, ViewRange.FromValues(x, x), ViewRange.FromValues(0.0, 0.0));

    private static Vector2 LinePoint(EngineFixture f, int row)
    {
        var y = MathF.Sin(row * 0.02f) * 10;
        return f.Engine.ViewToPixel(Panel, ViewRange.FromValues((double)row, row), ViewRange.FromValues((double)y, y));
    }

    private static Vector4 Premultiplied(Color color) =>
        new(
            HyperPlot.Rendering.Premultiplied.Multiply(color.R, color.A) / 255f,
            HyperPlot.Rendering.Premultiplied.Multiply(color.G, color.A) / 255f,
            HyperPlot.Rendering.Premultiplied.Multiply(color.B, color.A) / 255f,
            color.A / 255f
        );
}
