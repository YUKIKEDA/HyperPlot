using System.Numerics;
using HyperPlot.Rendering;
using static HyperPlot.Tests.GeomTesting;

namespace HyperPlot.Tests;

/// <summary>The coordinate system's spec and the cartesian flip of row G10 (design §4.1, §4.4, §4.8, §5.5, §10 G10 row).</summary>
public sealed class CoordFlipTests
{
    private static readonly Vector2 Nan = new(float.NaN);

    /// <summary>
    /// A spec's coordinate system is cartesian without flip by default, and the same one given draws the same list; a null
    /// coordinate system throws from the builder and from the render of a spec made with <c>with</c> (design §4.1, §4.8).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A line over 800 points, from the builder with no coordinate system and with Coord.Cartesian().
    ///
    /// Steps:
    /// - Read the specs' Coord; render both; call PlotBuilder.Coord(null); render the spec with Coord = null.
    ///
    /// Expected:
    /// - A CartesianCoordSpec without flip; the same commands and records; ArgumentNullException from both nulls, the
    ///   render's naming Coord.
    /// </remarks>
    [Fact]
    public void Coord_DefaultIsCartesian_NullThrows()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = Data.Create();
        var built = Plot.Create(data.Table).Line(data.Table.Col<double>("x"), data.Table.Col<double>("y")).Theme(TestPlans.Bare).Build();
        var given = Plot.Create(data.Table)
            .Line(data.Table.Col<double>("x"), data.Table.Col<double>("y"))
            .Theme(TestPlans.Bare)
            .Coord(Coord.Cartesian())
            .Build();

        Assert.False(Assert.IsType<CartesianCoordSpec>(built.Coord).Flip);
        Assert.False(Assert.IsType<CartesianCoordSpec>(new PlotSpec { Data = data.Table, Layers = [] }.Coord).Flip);
        Assert.True(Coord.Cartesian(flip: true).Flip);
        var flipped = Plot.Create(data.Table)
            .Line(data.Table.Col<double>("x"), data.Table.Col<double>("y"))
            .Coord(Coord.Cartesian(flip: true))
            .Build();
        Assert.True(Assert.IsType<CartesianCoordSpec>(flipped.Coord).Flip);
        using var first = TestPlans.Create(pool, Size, Size);
        using var second = TestPlans.Create(pool, Size, Size);
        AssertSameList(first.Render(built), second.Render(given));

        _ = Assert.Throws<ArgumentNullException>(() => Plot.Create(data.Table).Coord(null!));
        var error = Assert.Throws<ArgumentNullException>(() => first.Render(built with { Coord = null! }));
        Assert.Contains("Coord", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// On a square panel, flipped with both scales reversed, each geom records the unflipped list with x and y swapped:
    /// polyline points, rectangles, trapezoids (horizontal ones), and clips (design §4.4, §10 G10 row). Flip alone mirrors
    /// that through the panel's center, since the target's y runs down; reversing both axes undoes the mirror.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A 100 × 100 target without guides, domains [0, 100]. Layers: a line over 800 points (M4); steps of six points in
    ///   each mode; an area with Baseline 50 and a band over 800 points; bars and error bars, four (each) and 800 (pixel
    ///   columns); vertical and horizontal rules, two and 1,000; two rects; two segments; a user's geom that draws through
    ///   GeomFrame.Map.
    ///
    /// Steps:
    /// - Render each layer alone without flip, and with flip and ScaleX and ScaleY reversed.
    ///
    /// Expected:
    /// - The same commands; each polyline's segments, bounds, and clip swapped; each rectangle's x and y, width and height
    ///   swapped; each trapezoid the same numbers, read horizontally; each clip swapped.
    /// </remarks>
    [Fact]
    public void Flip_BothScalesReversed_IsTheUnflippedRecordSwapped()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = Data.Create();
        var t = data.Table;
        var (x, y, lo, hi, fx, fy, flo, fhi) = (
            t.Col<double>("x"),
            t.Col<double>("y"),
            t.Col<double>("lo"),
            t.Col<double>("hi"),
            data.Few.Col<double>("x"),
            data.Few.Col<double>("y"),
            data.Few.Col<double>("lo"),
            data.Few.Col<double>("hi")
        );
        var many = data.Rules.Col<double>("many");
        var layers = new (DataTable Table, LayerSpec Layer)[]
        {
            (t, new LineLayerSpec { X = x, Y = y }),
            (data.Few, new StepLayerSpec { X = fx, Y = fy }),
            (
                data.Few,
                new StepLayerSpec
                {
                    X = fx,
                    Y = fy,
                    Mode = StepMode.Before,
                }
            ),
            (
                data.Few,
                new StepLayerSpec
                {
                    X = fx,
                    Y = fy,
                    Mode = StepMode.Middle,
                }
            ),
            (
                t,
                new AreaLayerSpec
                {
                    X = x,
                    Y = y,
                    Baseline = 50,
                }
            ),
            (
                t,
                new BandLayerSpec
                {
                    X = x,
                    YMin = lo,
                    YMax = hi,
                }
            ),
            (
                data.Few,
                new BarLayerSpec
                {
                    X = fx,
                    Y = fy,
                    Baseline = 10,
                }
            ),
            (
                t,
                new BarLayerSpec
                {
                    X = x,
                    Y = y,
                    Baseline = 50,
                }
            ),
            (
                data.Few,
                new ErrorBarLayerSpec
                {
                    X = fx,
                    YMin = flo,
                    YMax = fhi,
                }
            ),
            (
                t,
                new ErrorBarLayerSpec
                {
                    X = x,
                    YMin = lo,
                    YMax = hi,
                }
            ),
            (data.Few, new RuleLayerSpec { X = fx }),
            (data.Few, new RuleLayerSpec { Y = fx }),
            (data.Rules, new RuleLayerSpec { X = many }),
            (data.Rules, new RuleLayerSpec { Y = many }),
            (
                data.Few,
                new RectLayerSpec
                {
                    XMin = fx,
                    XMax = fy,
                    YMin = flo,
                    YMax = fhi,
                }
            ),
            (
                data.Few,
                new SegmentLayerSpec
                {
                    X = fx,
                    Y = flo,
                    XEnd = fy,
                    YEnd = fhi,
                }
            ),
            (data.Few, new CustomLayerSpec(new MappedGeom())),
        };
        using var straight = TestPlans.Create(pool, Size, Size);
        using var flipped = TestPlans.Create(pool, Size, Size);
        foreach (var (table, layer) in layers)
        {
            var spec = Spec(table, layer);
            var expected = straight.Render(spec);
            var actual = flipped.Render(
                spec with
                {
                    Coord = Coord.Cartesian(flip: true),
                    ScaleX = Scale.Linear().WithDomain(0, Size) with { Reverse = true },
                    ScaleY = Scale.Linear().WithDomain(0, Size) with { Reverse = true },
                }
            );
            AssertSwapped(expected, actual);
        }
    }

    /// <summary>
    /// Flipped, a geom along x works per pixel row: the M4 boundaries are H + 1, bars past 4H are drawn per row, and a
    /// horizontal rule (now vertical) is drawn one line an element up to 4W (design §4.4, §5.5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A 100 × 50 target without guides, domains [0, 100]; 300 bars at x = i / 3; 300 horizontal rules at y = i / 3. A
    ///   50 × 100 target for a flipped line over 800 points.
    ///
    /// Steps:
    /// - Render each flipped and not; read the plan's pixel columns, the rectangles and their clip, and the rule's
    ///   segments. Render the line on the tall target.
    ///
    /// Expected:
    /// - Pixel columns 50 flipped and 100 not. Flipped, the 300 bars (past 4H = 200) are rectangles one row high on whole
    ///   rows, clipped to the panel (0, 0)–(100, 50); not flipped (at most 4W = 400), 300 rectangles. Flipped, the 300 rules
    ///   (at most 4W) are 300 segments; not flipped (past 4H), at most 50. The tall target's line runs over 100 rows: M4's
    ///   output holds 4H + 2 points, more than 4W + 2.
    /// </remarks>
    [Fact]
    public void Flip_AlongXIsPerPixelRow()
    {
        using var pool = new PlotWorkerPool(1);
        using var values = Column.Wrap(Enumerable.Range(0, 300).Select(i => i / 3.0).ToArray());
        using var heights = Column.Wrap(Enumerable.Range(0, 300).Select(i => 20 + (10 * Math.Sin(i * 0.1))).ToArray());
        var table = DataTable.Create().Add("v", values).Add("h", heights).Build();
        var bars = Spec(table, new BarLayerSpec { X = table.Col<double>("v"), Y = table.Col<double>("h") });
        var rules = Spec(table, new RuleLayerSpec { Y = table.Col<double>("v") });
        var flip = Coord.Cartesian(flip: true);
        using var plan = TestPlans.Create(pool, Size, Size / 2);

        var straightBars = DataRects(plan.Render(bars));
        Assert.Equal(Size, plan.PixelColumns);
        Assert.Equal(300, straightBars.Length);
        var flippedList = plan.Render(bars with { Coord = flip });
        var flippedBars = DataRects(flippedList);
        Assert.Equal(Size / 2, plan.PixelColumns);
        Assert.All(flippedList.RectClips[1..].ToArray(), c => Assert.Equal(new ClipRect(0, 0, Size, Size / 2), c));
        Assert.InRange(flippedBars.Length, 1, Size);
        Assert.All(
            flippedBars,
            r =>
            {
                Assert.Equal(1, r.Height);
                Assert.Equal(Math.Floor(r.Y), r.Y);
            }
        );

        Assert.Equal(300, SegmentsOf(plan.Render(rules with { Coord = flip }), 0).Length);
        Assert.InRange(SegmentsOf(plan.Render(rules), 0).Length, 1, Size / 2);

        using var data = Data.Create();
        using var tall = TestPlans.Create(pool, Size / 2, Size);
        var line = Spec(data.Table, new LineLayerSpec { X = data.Table.Col<double>("x"), Y = data.Table.Col<double>("y") });
        _ = tall.Render(line with { Coord = flip });
        Assert.Equal(Size, tall.PixelColumns);
    }

    /// <summary>
    /// A user's geom reads the flip from its frame (design §4.4): Flipped, PixelX along the vertical side and PixelY along
    /// the horizontal one, Map the target's point, and the panel's rectangle the same flipped or not.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A 100 × 50 target without guides, domains [0, 100]; a geom that reads its frame at (25, 40).
    ///
    /// Steps:
    /// - Render flipped and not.
    ///
    /// Expected:
    /// - Not flipped: false, PixelX 25 and PixelY 50 − 40 / 2 = 30, Map (25, 30). Flipped: true, PixelX 50 − 25 / 2 = 37.5,
    ///   PixelY 40, Map (40, 37.5). The panel (0, 0, 100, 50) both times. With a padding of 4 and a title, the panel's left
    ///   and top differ, and the frame reports the guides' panel flipped or not.
    /// </remarks>
    [Fact]
    public void Flip_GeomFrame_ReadsTheSides()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = Data.Create();
        var geom = new ProbeGeom();
        var spec = Spec(data.Few, new CustomLayerSpec(geom));
        using var plan = TestPlans.Create(pool, Size, Size / 2);

        _ = plan.Render(spec);
        Assert.Equal((false, 25, 30, 25, 30, (0, 0, 100, 50)), geom.Read);
        _ = plan.Render(spec with { Coord = Coord.Cartesian(flip: true) });
        Assert.Equal((true, 37.5, 40, 40, 37.5, (0, 0, 100, 50)), geom.Read);

        // A panel whose left and top differ: a padding of 4 and a title band above.
        var framed = spec with
        {
            Theme = TestPlans.Bare with { Padding = 4 },
            Title = "T",
        };
        foreach (var coord in new[] { Coord.Cartesian(), Coord.Cartesian(flip: true) })
        {
            _ = plan.Render(framed with { Coord = coord });
            var g = plan.Guides;
            Assert.NotEqual(g.Left, g.Top);
            Assert.Equal((g.Left, g.Top, g.Width, g.Height), geom.Read.Panel);
        }
    }

    /// <summary>
    /// A change of flip lays out, extracts, and records once; the same flip again, in the same node or a new one, does
    /// nothing (design §4.8).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A line over 800 points with learned domains, drawn once.
    ///
    /// Steps:
    /// - Render it flipped; again; with a new node of the same flip; without flip.
    ///
    /// Expected:
    /// - (layouts, extractions, recordings) changes: (1, 1, 1), (0, 0, 0), (0, 0, 0), (1, 1, 1).
    /// </remarks>
    [Fact]
    public void Flip_Diff_IsTheScaleStage()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = Data.Create();
        var spec = Plot.Create(data.Table).Line(data.Table.Col<double>("x"), data.Table.Col<double>("y")).Theme(TestPlans.Bare).Build();
        var flipped = spec with { Coord = Coord.Cartesian(flip: true) };
        using var plan = TestPlans.Create(pool, Size, Size);
        _ = plan.Render(spec);

        Assert.Equal((1, 1, 1), Counts(flipped));
        Assert.Equal((0, 0, 0), Counts(flipped));
        Assert.Equal((0, 0, 0), Counts(flipped with { Coord = Coord.Cartesian(flip: true) }));
        Assert.Equal((1, 1, 1), Counts(spec));

        (int, int, int) Counts(PlotSpec next)
        {
            var before = (plan.Layouts, plan.Extractions, plan.Recordings);
            _ = plan.Render(next);
            return (plan.Layouts - before.Layouts, plan.Extractions - before.Extractions, plan.Recordings - before.Recordings);
        }
    }

    /// <summary>Once warm, a flipped spec drawn again allocates nothing, its geoms and guides included (design §4.8).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A 640 × 360 target with the light theme; a flipped spec of a line, an area, a band, bars, error bars, and rules over
    ///   800 points, with axis titles and a title, drawn once.
    ///
    /// Steps:
    /// - Render it five times; then a new node of the same spec five times.
    ///
    /// Expected:
    /// - 0 bytes on this thread both times.
    /// </remarks>
    [Fact]
    public void Flip_Warm_AllocatesNothing()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = Data.Create();
        var t = data.Table;
        var (x, y, lo, hi) = (t.Col<double>("x"), t.Col<double>("y"), t.Col<double>("lo"), t.Col<double>("hi"));
        var spec = Plot.Create(t)
            .Line(x, y)
            .Area(x, y, baseline: 50)
            .Band(x, lo, hi)
            .Bar(x, y)
            .ErrorBar(x, lo, hi)
            .VRule(x)
            .HRule(x)
            .ScaleX(Scale.Linear().WithTitle("X"))
            .ScaleY(Scale.Linear().WithTitle("Y"))
            .Title("Flip")
            .Coord(Coord.Cartesian(flip: true))
            .Build();
        using var plan = new PlotPlan(pool, 640, 360, new TextEngine(new FakeTextBackend()), 1);
        _ = plan.Render(spec);
        _ = plan.Render(spec with { Title = "Flip" });

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 5; i++)
        {
            _ = plan.Render(spec);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        var again = spec with { Title = "Flip" };
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 5; i++)
        {
            _ = plan.Render(again);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>Asserts that a flipped list is the straight one with x and y swapped, record for record.</summary>
    private static void AssertSwapped(RenderList straight, RenderList flipped)
    {
        Assert.Equal(straight.Commands.ToArray(), flipped.Commands.ToArray());
        Assert.Equal(
            straight
                .Polylines.ToArray()
                .Select(p => p with { Clip = Swap(p.Clip), MinX = p.MinY, MinY = p.MinX, MaxX = p.MaxY, MaxY = p.MaxX })
                .ToArray(),
            flipped.Polylines.ToArray()
        );
        Assert.Equal(
            straight.Segments.ToArray().Select(s => s with { Start = Swap(s.Start), End = Swap(s.End) }).ToArray(),
            flipped.Segments.ToArray()
        );
        Assert.Equal(
            straight.Rects.ToArray().Select(r => r with { X = r.Y, Y = r.X, Width = r.Height, Height = r.Width }).ToArray(),
            flipped.Rects.ToArray()
        );
        Assert.Equal(straight.RectClips.ToArray().Select(Swap).ToArray(), flipped.RectClips.ToArray());
        Assert.All(straight.Columns.ToArray(), c => Assert.Equal(0, c.Horizontal));
        Assert.Equal(straight.Columns.ToArray().Select(c => c with { Horizontal = 1 }).ToArray(), flipped.Columns.ToArray());
        Assert.Equal(straight.ColumnClips.ToArray().Select(Swap).ToArray(), flipped.ColumnClips.ToArray());
    }

    private static Vector2 Swap(Vector2 point) => new(point.Y, point.X);

    private static ClipRect Swap(ClipRect clip) => new(clip.Top, clip.Left, clip.Bottom, clip.Right);

    /// <summary>The tables the tests draw: 800 points (M4 and pixel columns), a few elements, and 1,000 sorted rule values.</summary>
    private sealed class Data : IDisposable
    {
        private readonly IDisposable[] _columns;

        private Data(IDisposable[] columns, DataTable table, DataTable few, DataTable rules)
        {
            (_columns, Table, Few, Rules) = (columns, table, few, rules);
        }

        public DataTable Table { get; }

        public DataTable Few { get; }

        public DataTable Rules { get; }

        public static Data Create()
        {
            var y = Column.Wrap(Enumerable.Range(0, 800).Select(i => 50 + (40 * Math.Sin(i * 0.05))).ToArray());
            var lo = Column.Wrap(Enumerable.Range(0, 800).Select(i => 30 + (10 * Math.Sin(i * 0.07))).ToArray());
            var hi = Column.Wrap(Enumerable.Range(0, 800).Select(i => 70 + (10 * Math.Cos(i * 0.05))).ToArray());
            var fx = Column.Wrap(new double[] { 10, 22.5, 41, 60, 73.25, 90 });
            var fy = Column.Wrap(new double[] { 30, 55, 12.5, 80, 47, 64 });
            var flo = Column.Wrap(new double[] { 5, 20, 8, 33, 41, 50 });
            var fhi = Column.Wrap(new double[] { 25, 60, 30, 70, 85, 95 });
            var rules = Column.Wrap(Enumerable.Range(0, 1000).Select(i => ((i / 10) * 2) + ((i % 10) * 0.05)).ToArray());
            var table = DataTable.Create().Add("x", Column.Uniform(0, 0.125, 800)).Add("y", y).Add("lo", lo).Add("hi", hi).Build();
            var few = DataTable.Create().Add("x", fx).Add("y", fy).Add("lo", flo).Add("hi", fhi).Build();
            var many = DataTable.Create().Add("many", rules).Build();
            return new Data([y, lo, hi, fx, fy, flo, fhi, rules], table, few, many);
        }

        public void Dispose()
        {
            foreach (var column in _columns)
            {
                column.Dispose();
            }
        }
    }

    /// <summary>A geom that draws a polyline through its frame's Map and learns nothing.</summary>
    private sealed class MappedGeom : IGeom
    {
        public void Learn(GeomRanges ranges) { }

        public void Record(GeomFrame frame, RenderList list)
        {
            Span<Vector2> points = stackalloc Vector2[4];
            ReadOnlySpan<(double X, double Y)> data = [(10, 20), (35.5, 70), (60, 15), (95, 88)];
            for (var i = 0; i < data.Length; i++)
            {
                frame.Map(data[i].X, data[i].Y, out var px, out var py);
                points[i] = new Vector2((float)px, (float)py);
            }

            list.DrawPolyline(points, new LineStyle(new Color(0, 0, 0, 255), 1));
        }
    }

    /// <summary>A geom that reads its frame at (25, 40) and draws nothing.</summary>
    private sealed class ProbeGeom : IGeom
    {
        public (bool Flipped, double PixelX, double PixelY, double MapX, double MapY, (int Left, int Top, int Width, int Height) Panel) Read
        {
            get;
            private set;
        }

        public void Learn(GeomRanges ranges) { }

        public void Record(GeomFrame frame, RenderList list)
        {
            frame.Map(25, 40, out var mx, out var my);
            Read = (frame.Flipped, frame.PixelX(25), frame.PixelY(40), mx, my, (frame.Left, frame.Top, frame.Width, frame.Height));
        }
    }
}
