using System.Numerics;
using HyperPlot.Rendering;
using static HyperPlot.Tests.GeomTesting;

namespace HyperPlot.Tests;

/// <summary>The learning and the recording of row G4's geoms (design §4.2, §5.5.3, §10 G4 row).</summary>
public sealed class GeomPlanTests
{
    private static readonly LineStyle Black = new(new Color(0, 0, 0, 255), 1);
    private static readonly Color Fill = new(0, 0, 0, 128);
    private static readonly double[] EdgeXs = [0.5, 3.5];
    private static readonly double[] RuleXs = [10.5, 30.25];

    /// <summary>
    /// Each geom's domains are the union of its channels' ranges (design §4.2): an area's and a bar's Baseline joins y, a
    /// bar's x widens by half a bar on each side, a vertical rule learns x only and a horizontal one y only.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An evenly spaced "t" of 0 to 9 and stored columns "a" (2 to 5), "lo" (1 to 4), "hi" (3 to 8), "x" (1 to 6).
    ///
    /// Steps:
    /// - Render each geom alone with learned domains.
    ///
    /// Expected:
    /// - Step [0, 9] × [2, 5]; area with Baseline -3: y [-3, 5]; bar: x [-0.45, 9.45] (width 0.9) and y [0, 5]; band and
    ///   error bar: y [1, 8]; rect over x and a: [1, 6] × [2, 5]; segment from (x, lo) to (a, hi): [1, 6] × [1, 8]; a
    ///   vertical rule on x: [1, 6] × [0, 1]; a horizontal one on x: [0, 1] × [1, 6].
    /// </remarks>
    [Fact]
    public void Learn_EachGeom_IsTheUnionOfItsChannels()
    {
        using var pool = new PlotWorkerPool(1);
        using var a = Column.Wrap(new double[] { 2, 5, 3, 4, 2, 3, 4, 5, 2, 3 });
        using var lo = Column.Wrap(new double[] { 1, 2, 3, 4, 1, 2, 3, 4, 1, 2 });
        using var hi = Column.Wrap(new double[] { 3, 8, 5, 6, 3, 4, 5, 6, 7, 8 });
        using var x = Column.Wrap(new double[] { 1, 1, 2, 2, 3, 4, 5, 5, 6, 6 });
        var table = DataTable.Create().Add("t", Column.Uniform(0, 1, 10)).Add("a", a).Add("lo", lo).Add("hi", hi).Add("x", x).Build();
        var (t, ra, rlo, rhi, rx) = (
            table.Col<double>("t"),
            table.Col<double>("a"),
            table.Col<double>("lo"),
            table.Col<double>("hi"),
            table.Col<double>("x")
        );

        Assert.Equal(((0d, 9d), (2d, 5d)), Learned(new StepLayerSpec { X = t, Y = ra }));
        Assert.Equal(
            ((0d, 9d), (-3d, 5d)),
            Learned(
                new AreaLayerSpec
                {
                    X = t,
                    Y = ra,
                    Baseline = -3,
                }
            )
        );
        var bar = Learned(new BarLayerSpec { X = t, Y = ra });
        Close(-0.45, bar.Item1.Item1);
        Close(9.45, bar.Item1.Item2);
        Assert.Equal((0d, 5d), bar.Item2);
        Assert.Equal(
            ((0d, 9d), (1d, 8d)),
            Learned(
                new BandLayerSpec
                {
                    X = t,
                    YMin = rlo,
                    YMax = rhi,
                }
            )
        );
        Assert.Equal(
            ((0d, 9d), (1d, 8d)),
            Learned(
                new ErrorBarLayerSpec
                {
                    X = t,
                    YMin = rlo,
                    YMax = rhi,
                }
            )
        );
        Assert.Equal(
            ((1d, 6d), (2d, 5d)),
            Learned(
                new RectLayerSpec
                {
                    XMin = rx,
                    XMax = rx,
                    YMin = ra,
                    YMax = ra,
                }
            )
        );
        Assert.Equal(
            ((1d, 6d), (1d, 8d)),
            Learned(
                new SegmentLayerSpec
                {
                    X = rx,
                    Y = rlo,
                    XEnd = ra,
                    YEnd = rhi,
                }
            )
        );
        Assert.Equal(((1d, 6d), (0d, 1d)), Learned(new RuleLayerSpec { X = rx }));
        Assert.Equal(((0d, 1d), (1d, 6d)), Learned(new RuleLayerSpec { Y = rx }));

        ((double, double), (double, double)) Learned(LayerSpec layer)
        {
            using var plan = TestPlans.Create(pool, Size, Size);
            _ = plan.Render(Plot.Create(table).Theme(TestPlans.Bare).Build() with { Layers = [layer] });
            return (Ends(plan.XDomain), Ends(plan.YDomain));
        }
    }

    /// <summary>The three step modes turn between the points as design §5.5.3 draws them, in one polyline.</summary>
    /// <remarks>
    /// Preconditions:
    /// - Points (10, 10), (20, 30), (40, 20); the pixels are (x, 100 − y).
    ///
    /// Steps:
    /// - Render a step layer in each mode.
    ///
    /// Expected:
    /// - The list holds the panel and one polyline: After (10, 90) (20, 90) (20, 70) (40, 70) (40, 80); Before (10, 90)
    ///   (10, 70) (20, 70) (20, 80) (40, 80); Middle (10, 90) (15, 90) (15, 70) (20, 70) (30, 70) (30, 80) (40, 80).
    /// </remarks>
    [Fact]
    public void Step_Modes_TurnBetweenThePoints()
    {
        using var pool = new PlotWorkerPool(1);
        using var x = Column.Wrap(new double[] { 10, 20, 40 });
        using var y = Column.Wrap(new double[] { 10, 30, 20 });
        var table = DataTable.Create().Add("x", x).Add("y", y).Build();
        var cases = new (StepMode Mode, Vector2[] Points)[]
        {
            (StepMode.After, [new(10, 90), new(20, 90), new(20, 70), new(40, 70), new(40, 80)]),
            (StepMode.Before, [new(10, 90), new(10, 70), new(20, 70), new(20, 80), new(40, 80)]),
            (StepMode.Middle, [new(10, 90), new(15, 90), new(15, 70), new(20, 70), new(30, 70), new(30, 80), new(40, 80)]),
        };

        foreach (var (mode, points) in cases)
        {
            using var plan = TestPlans.Create(pool, Size, Size);
            var actual = plan.Render(
                Spec(
                    table,
                    new StepLayerSpec
                    {
                        X = table.Col<double>("x"),
                        Y = table.Col<double>("y"),
                        Mode = mode,
                    }
                )
            );
            var expected = Expected();
            expected.DrawPolyline(points, Black);
            AssertSameList(expected, actual);
        }
    }

    /// <summary>
    /// A step decimated by M4 covers each pixel column over the same vertical extent as the step of every point (design
    /// §5.5.3): M4 keeps each column's first, least, greatest, and last points.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 20,000 points at random increasing x in [0, 100) and a random walk of y inside (0, 100).
    ///
    /// Steps:
    /// - Render a step layer in each mode; walk the recorded segments and the step of every point, in double, column by
    ///   column.
    ///
    /// Expected:
    /// - In each of the 100 columns, the least and the greatest pixel y agree within 1e-3.
    /// </remarks>
    [Fact]
    public void Step_DecimatedByMfour_CoversEachColumnAsTheFullStep()
    {
        const int Count = 20_000;
        using var pool = new PlotWorkerPool(2);
        var random = new Random(7);
        var xs = new double[Count];
        var ys = new double[Count];
        var (at, value) = (0.0, 50.0);
        for (var i = 0; i < Count; i++)
        {
            at += random.NextDouble() * 2 * 100 / Count * 0.999;
            value = Math.Clamp(value + ((random.NextDouble() - 0.5) * 6), 1, 99);
            (xs[i], ys[i]) = (at, value);
        }

        using var x = Column.Wrap(xs);
        using var y = Column.Wrap(ys);
        var table = DataTable.Create().Add("x", x).Add("y", y).Build();
        foreach (var mode in new[] { StepMode.After, StepMode.Before, StepMode.Middle })
        {
            using var plan = TestPlans.Create(pool, Size, Size);
            var list = plan.Render(
                Spec(
                    table,
                    new StepLayerSpec
                    {
                        X = table.Col<double>("x"),
                        Y = table.Col<double>("y"),
                        Mode = mode,
                    }
                )
            );
            var recorded = Extents(SegmentsOf(list, 0).Select(s => ((double)s.Start.X, (double)s.Start.Y, (double)s.End.X, (double)s.End.Y)));
            var full = Extents(FullStep(xs, ys, mode));
            for (var c = 0; c < Size; c++)
            {
                Assert.True(
                    Math.Abs(recorded[c].Min - full[c].Min) < 1e-3 && Math.Abs(recorded[c].Max - full[c].Max) < 1e-3,
                    $"{mode}, column {c}: recorded {recorded[c]}, full {full[c]}."
                );
            }
        }

        static IEnumerable<(double, double, double, double)> FullStep(double[] xs, double[] ys, StepMode mode)
        {
            for (var i = 1; i < xs.Length; i++)
            {
                var (x0, y0, x1, y1) = (xs[i - 1], 100 - ys[i - 1], xs[i], 100 - ys[i]);
                var turn = mode switch
                {
                    StepMode.After => x1,
                    StepMode.Before => x0,
                    _ => (x0 + x1) / 2,
                };
                yield return (x0, y0, turn, y0);
                yield return (turn, y0, turn, y1);
                yield return (turn, y1, x1, y1);
            }
        }

        static (double Min, double Max)[] Extents(IEnumerable<(double X0, double Y0, double X1, double Y1)> segments)
        {
            var extents = Enumerable.Repeat((Min: double.PositiveInfinity, Max: double.NegativeInfinity), Size).ToArray();
            foreach (var (x0, y0, x1, y1) in segments)
            {
                var first = (int)Math.Floor(Math.Min(x0, x1));
                var last = Math.Max(first, (int)Math.Ceiling(Math.Max(x0, x1)) - 1);
                for (var c = Math.Max(first, 0); c <= Math.Min(last, Size - 1); c++)
                {
                    extents[c] = (Math.Min(extents[c].Min, Math.Min(y0, y1)), Math.Max(extents[c].Max, Math.Max(y0, y1)));
                }
            }

            return extents;
        }
    }

    /// <summary>
    /// An area makes one trapezoid a pixel column (design §5.5.3): sloped along the line where no point lies inside, flat
    /// over the extremes of the edges, the points, and the Baseline where one does; the edges cross where the line crosses
    /// the Baseline; the outline follows the line over the fill.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Points (0.5, 20) and (3.5, 50), in pixels (0.5, 80) and (3.5, 50): the line falls 10 pixels a column.
    ///
    /// Steps:
    /// - Render an area with Baseline 0 and an outline; render one with Baseline 30 (pixel 70).
    ///
    /// Expected:
    /// - Baseline 0: (0.5, 1, 75, 75, 100, 100), (1, 2, 75, 65, 100, 100), (2, 3, 65, 55, 100, 100), (3, 3.5, 50, 50, 100,
    ///   100), then the outline. Baseline 30: (0.5, 1, 70, 70, 80, 80), (1, 2, 75, 65, 70, 70), (2, 3, 65, 55, 70, 70), (3,
    ///   3.5, 50, 50, 70, 70): the second column's edges cross.
    /// </remarks>
    [Fact]
    public void Area_Columns_SlopedFlatAndCrossing()
    {
        using var pool = new PlotWorkerPool(1);
        using var x = Column.Wrap(EdgeXs);
        using var y = Column.Wrap(new double[] { 20, 50 });
        var table = DataTable.Create().Add("x", x).Add("y", y).Build();
        var outline = new LineStyle(new Color(200, 0, 0, 255), 2);
        var area = new AreaLayerSpec
        {
            X = table.Col<double>("x"),
            Y = table.Col<double>("y"),
            Outline = outline,
        };

        using var plan = TestPlans.Create(pool, Size, Size);
        var expected = Expected();
        expected.DrawColumns(
            [new(0.5f, 1, 75, 75, 100, 100), new(1, 2, 75, 65, 100, 100), new(2, 3, 65, 55, 100, 100), new(3, 3.5f, 50, 50, 100, 100)],
            Fill
        );
        expected.DrawPolyline([new(0.5f, 80), new(3.5f, 50)], outline);
        AssertSameList(expected, plan.Render(Spec(table, area)));

        Assert.Equal(
            [new(0.5f, 1, 70, 70, 80, 80), new(1, 2, 75, 65, 70, 70), new(2, 3, 65, 55, 70, 70), new(3, 3.5f, 50, 50, 70, 70)],
            Trapezoids(plan.Render(Spec(table, area with { Baseline = 30, Outline = null })))
        );
    }

    /// <summary>
    /// Where a flat column fills past the line, it does so by at most the slope of a segment beside it times one pixel
    /// (design §5.5.3); a sloped column follows the line.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 40 points at random increasing x in [0, 100) and random y in [10, 90]; Baseline 0, below them all.
    ///
    /// Steps:
    /// - Render the area; sample each trapezoid's top at 33 x, and the line at the same x.
    ///
    /// Expected:
    /// - The line minus the top is at least 0 minus 1e-3 and at most the steepest slope of the segments over the column times
    ///   1 plus 1e-3.
    /// </remarks>
    [Fact]
    public void Area_FlatColumn_FillsPastTheLineWithinItsSlope()
    {
        using var pool = new PlotWorkerPool(1);
        var random = new Random(11);
        var xs = Enumerable.Range(0, 40).Select(_ => random.NextDouble() * 99.5).Order().ToArray();
        var ys = xs.Select(_ => 10 + (random.NextDouble() * 80)).ToArray();
        using var x = Column.Wrap(xs);
        using var y = Column.Wrap(ys);
        var table = DataTable.Create().Add("x", x).Add("y", y).Build();
        using var plan = TestPlans.Create(pool, Size, Size);

        var trapezoids = Trapezoids(plan.Render(Spec(table, new AreaLayerSpec { X = table.Col<double>("x"), Y = table.Col<double>("y") })));

        Assert.NotEmpty(trapezoids);
        foreach (var t in trapezoids)
        {
            var slope = 0.0;
            for (var i = 1; i < xs.Length; i++)
            {
                if (xs[i] > Math.Floor(t.Left) && xs[i - 1] < Math.Floor(t.Left) + 1)
                {
                    slope = Math.Max(slope, Math.Abs((ys[i] - ys[i - 1]) / (xs[i] - xs[i - 1])));
                }
            }

            for (var s = 0; s <= 32; s++)
            {
                var at = t.Left + ((t.Right - t.Left) * s / 32.0);
                var top = t.TopLeft + ((t.TopRight - t.TopLeft) * (at - t.Left) / (t.Right - t.Left));
                var past = (100 - Line(at)) - top;
                Assert.InRange(past, -1e-3, (slope * 1) + 1e-3);
            }
        }

        double Line(double at)
        {
            var found = Array.FindIndex(xs, v => v >= at);
            var i = Math.Clamp(found < 0 ? xs.Length - 1 : found, 1, xs.Length - 1);
            return ys[i - 1] + ((ys[i] - ys[i - 1]) * (at - xs[i - 1]) / (xs[i] - xs[i - 1]));
        }
    }

    /// <summary>
    /// On a log y, the default Baseline 0 maps to −∞: an area and bars fill to the panel's bottom, or its top when the scale
    /// is reversed, and the learned domain leaves the Baseline out (design §4.2, §5.5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Y values 2, 10, 100 at x 10, 50, 90.
    ///
    /// Steps:
    /// - Render an area and bars of width 4 on a log y with a learned domain; then on one with domain [1, 1000], and on it
    ///   reversed.
    ///
    /// Expected:
    /// - The learned domain starts at log10 2 in the scale space; on [1, 1000], one edge of every trapezoid (the flat ones
    ///   keep the lesser pixel as the top) is at 100 and all three bars reach 100; reversed, 0.
    /// </remarks>
    [Fact]
    public void LogY_AreaAndBars_FillToThePanelEdge()
    {
        using var pool = new PlotWorkerPool(1);
        using var x = Column.Wrap(new double[] { 10, 50, 90 });
        using var y = Column.Wrap(new double[] { 2, 10, 100 });
        var table = DataTable.Create().Add("x", x).Add("y", y).Build();
        var (rx, ry) = (table.Col<double>("x"), table.Col<double>("y"));
        var layers = Spec(
            table,
            new AreaLayerSpec { X = rx, Y = ry },
            new BarLayerSpec
            {
                X = rx,
                Y = ry,
                Width = 4,
            }
        );
        using var learner = TestPlans.Create(pool, Size, Size);
        _ = learner.Render(layers with { ScaleY = Scale.Log() });
        Close(Math.Log10(2), Ends(learner.YDomain).Min);
        foreach (var reverse in new[] { false, true })
        {
            using var plan = TestPlans.Create(pool, Size, Size);
            var list = plan.Render(layers with { ScaleY = Scale.Log().WithDomain(1, 1000) with { Reverse = reverse } });

            var edge = reverse ? 0f : 100f;
            Assert.All(Trapezoids(list), t => Assert.True((t.BottomLeft, t.BottomRight) == (edge, edge) || (t.TopLeft, t.TopRight) == (edge, edge)));
            var bars = DataRects(list);
            Assert.Equal(3, bars.Length);
            Assert.All(bars, b => Assert.Equal(edge, reverse ? b.Y : b.Y + b.Height, 3));
        }
    }

    /// <summary>A band's top follows YMax and its bottom YMin, sloped or flat by the area's rule (design §5.5.3).</summary>
    /// <remarks>
    /// Preconditions:
    /// - x 0.5 and 3.5; YMin 10 and 20 (pixels 90 to 80); YMax 30 and 60 (pixels 70 to 40).
    ///
    /// Steps:
    /// - Render the band.
    ///
    /// Expected:
    /// - Column 0 flat from the top's least pixel to the bottom's greatest: (0.5, 1, 65, 65, 90, 90); column 1 sloped
    ///   between the lines' values at 1 and 2; column 3 flat (3, 3.5, 40, 40, 80.83…, 80.83…).
    /// </remarks>
    [Fact]
    public void Band_TopFollowsYMaxAndBottomYMin()
    {
        using var pool = new PlotWorkerPool(1);
        using var x = Column.Wrap(EdgeXs);
        using var lo = Column.Wrap(new double[] { 10, 20 });
        using var hi = Column.Wrap(new double[] { 30, 60 });
        var table = DataTable.Create().Add("x", x).Add("lo", lo).Add("hi", hi).Build();
        using var plan = TestPlans.Create(pool, Size, Size);

        var trapezoids = Trapezoids(
            plan.Render(
                Spec(
                    table,
                    new BandLayerSpec
                    {
                        X = table.Col<double>("x"),
                        YMin = table.Col<double>("lo"),
                        YMax = table.Col<double>("hi"),
                    }
                )
            )
        );

        Assert.Equal(4, trapezoids.Length);
        Assert.Equal(new ColumnTrapezoid(0.5f, 1, 65, 65, 90, 90), trapezoids[0]);
        Assert.Equal(new ColumnTrapezoid(1, 2, (float)High(1), (float)High(2), (float)Low(1), (float)Low(2)), trapezoids[1]);
        Assert.Equal(new ColumnTrapezoid(3, 3.5f, 40, 40, (float)Low(3), (float)Low(3)), trapezoids[3]);

        static double High(double at) => 70 + ((40.0 - 70) * (at - 0.5) / 3);

        static double Low(double at) => 90 + ((80.0 - 90) * (at - 0.5) / 3);
    }

    /// <summary>
    /// Up to 4W visible bars are drawn one by one from the Baseline; past 4W, each pixel column holds its greatest value
    /// above the Baseline and its least below (design §5.5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Few: x 10, 30, 50, 70 (evenly spaced, width 18 by default), y 30, 5, 50, 10, Baseline 10. Many: 800 values at x
    ///   = i / 8, a seeded sine around 50, Baseline 50.
    ///
    /// Steps:
    /// - Render both.
    ///
    /// Expected:
    /// - Few: the rects [x − 9, x + 9] × [Baseline, y] mapped, as RenderList records them. Many: per column with a value
    ///   above 50, the rect from its greatest to 50; with one below, from 50 to its least; one pixel wide.
    /// </remarks>
    [Fact]
    public void Bar_FewAreBarsManyArePixelColumns()
    {
        using var pool = new PlotWorkerPool(1);
        using var few = Column.Wrap(new double[] { 30, 5, 50, 10 });
        var fewTable = DataTable.Create().Add("x", Column.Uniform(10, 20, 4)).Add("y", few).Build();
        using var plan = TestPlans.Create(pool, Size, Size);
        var expected = Expected();
        expected.DrawRect(1, 70, 18, 20, Fill);
        expected.DrawRect(21, 90, 18, 5, Fill);
        expected.DrawRect(41, 50, 18, 40, Fill);
        expected.DrawRect(61, 90, 18, 0, Fill);
        AssertSameList(
            expected,
            plan.Render(
                Spec(
                    fewTable,
                    new BarLayerSpec
                    {
                        X = fewTable.Col<double>("x"),
                        Y = fewTable.Col<double>("y"),
                        Baseline = 10,
                    }
                )
            )
        );

        var values = Enumerable.Range(0, 800).Select(i => 50 + (40 * Math.Sin(i * 0.05))).ToArray();
        using var many = Column.Wrap(values);
        var manyTable = DataTable.Create().Add("x", Column.Uniform(0, 0.125, 800)).Add("y", many).Build();
        var columns = new RenderList();
        for (var c = 0; c < Size; c++)
        {
            var inside = values[(c * 8)..((c + 1) * 8)];
            if (inside.Max() > 50)
            {
                columns.DrawRect(c, (float)(100 - inside.Max()), 1, (float)(inside.Max() - 50), Fill);
            }

            if (inside.Min() < 50)
            {
                columns.DrawRect(c, 50, 1, (float)(50 - inside.Min()), Fill);
            }
        }

        var actual = DataRects(
            plan.Render(
                Spec(
                    manyTable,
                    new BarLayerSpec
                    {
                        X = manyTable.Col<double>("x"),
                        Y = manyTable.Col<double>("y"),
                        Baseline = 50,
                    }
                )
            )
        );
        var wanted = columns.Rects.ToArray().Select(r => (r.X, r.Y, r.Width, r.Height)).ToArray();
        Assert.Equal(wanted.Length, actual.Length);
        for (var i = 0; i < wanted.Length; i++)
        {
            Assert.Equal(wanted[i].X, actual[i].X);
            Close(wanted[i].Y, actual[i].Y);
            Close(wanted[i].Height, actual[i].Height);
        }
    }

    /// <summary>
    /// A bar's default width is 0.9 times an evenly spaced X's step, or a stored X's mean spacing, or 0.9 for one element
    /// (design §5.5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - X columns: evenly spaced with step 2; stored 10, 11, 14; stored 50 alone.
    ///
    /// Steps:
    /// - Render a bar of each.
    ///
    /// Expected:
    /// - Bars 1.8, 1.8, and 0.9 pixels wide.
    /// </remarks>
    [Fact]
    public void Bar_DefaultWidth_FollowsTheX()
    {
        using var pool = new PlotWorkerPool(1);
        using var three = Column.Wrap(new double[] { 10, 11, 14 });
        using var one = Column.Wrap(new double[] { 50 });
        using var y3 = Column.Wrap(new double[] { 20, 20, 20 });
        using var y1 = Column.Wrap(new double[] { 20 });

        Assert.Equal(1.8, Width(Column.Uniform(10, 2, 3), y3), 4);
        Assert.Equal(1.8, Width(three, y3), 4);
        Assert.Equal(0.9, Width(one, y1), 4);

        double Width(Column x, Column y)
        {
            var table = DataTable.Create().Add("x", x).Add("y", y).Build();
            var layer = new BarLayerSpec { X = new ColumnRefProbe(table, 0).Ref, Y = new ColumnRefProbe(table, 1).Ref };
            using var plan = TestPlans.Create(pool, Size, Size);
            return DataRects(plan.Render(Spec(table, layer)))[0].Width;
        }
    }

    /// <summary>
    /// Up to 4W visible error bars draw a line each and caps CapWidth · s wide, none at CapWidth 0; past 4W, a line a pixel
    /// column from the least YMin to the greatest YMax without caps; all in one polyline broken by NaN (design §5.5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Few: x 20 and 50, YMin 10 and 40, YMax 30 and 70. Many: 800 at x = i / 8, YMin a sine around 30 and YMax one
    ///   around 70.
    ///
    /// Steps:
    /// - Render the few with CapWidth 6 and 0; render the many.
    ///
    /// Expected:
    /// - CapWidth 6: one polyline of each bar, then its caps 3 to each side, each followed by NaN; 0: the bars alone. Many:
    ///   one polyline, a line at each column's center from the pixel of its greatest YMax to that of its least YMin.
    /// </remarks>
    [Fact]
    public void ErrorBar_FewHaveCapsManyArePixelColumns()
    {
        using var pool = new PlotWorkerPool(1);
        using var x = Column.Wrap(new double[] { 20, 50 });
        using var lo = Column.Wrap(new double[] { 10, 40 });
        using var hi = Column.Wrap(new double[] { 30, 70 });
        var table = DataTable.Create().Add("x", x).Add("lo", lo).Add("hi", hi).Build();
        var layer = new ErrorBarLayerSpec
        {
            X = table.Col<double>("x"),
            YMin = table.Col<double>("lo"),
            YMax = table.Col<double>("hi"),
        };
        var nan = new Vector2(float.NaN);
        using var plan = TestPlans.Create(pool, Size, Size);

        var capped = Expected();
        capped.DrawPolyline(
            [
                new(20, 90),
                new(20, 70),
                nan,
                new(17, 90),
                new(23, 90),
                nan,
                new(17, 70),
                new(23, 70),
                nan,
                new(50, 60),
                new(50, 30),
                nan,
                new(47, 60),
                new(53, 60),
                nan,
                new(47, 30),
                new(53, 30),
                nan,
            ],
            Black
        );
        AssertSameList(capped, plan.Render(Spec(table, layer)));
        var bare = Expected();
        bare.DrawPolyline([new(20, 90), new(20, 70), nan, new(50, 60), new(50, 30), nan], Black);
        AssertSameList(bare, plan.Render(Spec(table, layer with { CapWidth = 0 })));

        var lows = Enumerable.Range(0, 800).Select(i => 30 + (10 * Math.Sin(i * 0.07))).ToArray();
        var highs = Enumerable.Range(0, 800).Select(i => 70 + (10 * Math.Cos(i * 0.05))).ToArray();
        using var manyLo = Column.Wrap(lows);
        using var manyHi = Column.Wrap(highs);
        var many = DataTable.Create().Add("x", Column.Uniform(0, 0.125, 800)).Add("lo", manyLo).Add("hi", manyHi).Build();
        var list = plan.Render(
            Spec(
                many,
                new ErrorBarLayerSpec
                {
                    X = many.Col<double>("x"),
                    YMin = many.Col<double>("lo"),
                    YMax = many.Col<double>("hi"),
                }
            )
        );

        Assert.Equal(1, list.Polylines.Length);
        var segments = SegmentsOf(list, 0);
        Assert.Equal(Size, segments.Length);
        for (var c = 0; c < Size; c++)
        {
            var top = 100 - highs[(c * 8)..((c + 1) * 8)].Max();
            var bottom = 100 - lows[(c * 8)..((c + 1) * 8)].Min();
            Assert.Equal(c + 0.5f, segments[c].Start.X);
            Assert.Equal(c + 0.5f, segments[c].End.X);
            Close(top, segments[c].Start.Y);
            Close(bottom, segments[c].End.Y);
        }
    }

    /// <summary>
    /// Up to 4W (4H) rules draw a line each across the panel; past that, one at the center of each pixel column (row) that
    /// holds an element, found by search; all in one polyline (design §5.5.3). A Y that decreases throws.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Few: x 10.5 and 30.25; y 20 and 60. Many: 1,000 values, ten in each even column (row) 0 to 98; a decreasing Y.
    ///
    /// Steps:
    /// - Render vertical and horizontal rules of each; render a horizontal rule on the decreasing Y.
    ///
    /// Expected:
    /// - Few: (10.5, 0)–(10.5, 100) and (30.25, 0)–(30.25, 100); (0, 80)–(100, 80) and (0, 40)–(100, 40); each followed by
    ///   NaN. Many: 50 lines at x = c + 0.5, and 50 at y = 100 − r − 0.5, for the even c and r. The decreasing Y throws
    ///   InvalidOperationException.
    /// </remarks>
    [Fact]
    public void Rule_FewAreLinesManyArePixelColumnsOrRows()
    {
        using var pool = new PlotWorkerPool(1);
        using var few = Column.Wrap(RuleXs);
        using var fewY = Column.Wrap(new double[] { 20, 60 });
        var many = Enumerable.Range(0, 1000).Select(i => ((i / 10) * 2) + ((i % 10) * 0.05)).Select(v => (double)v).ToArray();
        using var manyColumn = Column.Wrap(many);
        using var down = Column.Wrap(new double[] { 3, 2, 1 });
        var table = DataTable.Create().Add("few", few).Add("fy", fewY).Build();
        var manyTable = DataTable.Create().Add("many", manyColumn).Build();
        var downTable = DataTable.Create().Add("down", down).Build();
        var nan = new Vector2(float.NaN);
        using var plan = TestPlans.Create(pool, Size, Size);

        var vertical = Expected();
        vertical.DrawPolyline([new(10.5f, 0), new(10.5f, 100), nan, new(30.25f, 0), new(30.25f, 100), nan], Black);
        AssertSameList(vertical, plan.Render(Spec(table, new RuleLayerSpec { X = table.Col<double>("few") })));
        var horizontal = Expected();
        horizontal.DrawPolyline([new(0, 80), new(100, 80), nan, new(0, 40), new(100, 40), nan], Black);
        AssertSameList(horizontal, plan.Render(Spec(table, new RuleLayerSpec { Y = table.Col<double>("fy") })));

        var columns = SegmentsOf(plan.Render(Spec(manyTable, new RuleLayerSpec { X = manyTable.Col<double>("many") })), 0);
        Assert.Equal(Enumerable.Range(0, 50).Select(c => (2 * c) + 0.5f), columns.Select(s => s.Start.X));
        var rows = SegmentsOf(plan.Render(Spec(manyTable, new RuleLayerSpec { Y = manyTable.Col<double>("many") })), 0);
        Assert.Equal(Enumerable.Range(0, 50).Select(r => 100 - (2 * r) - 0.5f), rows.Select(s => s.Start.Y));

        _ = Assert.Throws<InvalidOperationException>(() => plan.Render(Spec(downTable, new RuleLayerSpec { Y = downTable.Col<double>("down") })));
    }

    /// <summary>
    /// Segments and rects draw up to 131,072 elements on any panel, and throw past it with the count and the limit (design
    /// §5.5.3); segments are one polyline; a rect's smaller mapped corner is its top left on reversed and log scales.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Columns of 131,072 and 131,073 elements; rects from (10, 1) to (20, 10) and from (60, 100) to (40, 10).
    ///
    /// Steps:
    /// - Render segments and rects of each length on a 100 × 100 and a 4 × 4 target; render the two rects on a reversed x
    ///   and a log y with domain [1, 100].
    ///
    /// Expected:
    /// - 131,072 draws: one polyline of 131,072 segments, 131,072 rects; 131,073 throws InvalidOperationException naming
    ///   131073 and 131072 on both targets. The rects are (80, 50, 10, 50) and (40, 0, 20, 50).
    /// </remarks>
    [Fact]
    public void SegmentAndRect_LimitAndCorners()
    {
        using var pool = new PlotWorkerPool(1);
        foreach (var length in new[] { LayerPlan.MaxElements, LayerPlan.MaxElements + 1 })
        {
            using var start = Column.Wrap(Enumerable.Range(0, length).Select(i => (double)(i % 100)).ToArray());
            using var end = Column.Wrap(Enumerable.Range(0, length).Select(i => (double)((i % 100) + 0.5)).ToArray());
            var table = DataTable.Create().Add("s", start).Add("e", end).Build();
            var (s, e) = (table.Col<double>("s"), table.Col<double>("e"));
            var segment = Spec(
                table,
                new SegmentLayerSpec
                {
                    X = s,
                    Y = s,
                    XEnd = e,
                    YEnd = e,
                }
            );
            var rect = Spec(
                table,
                new RectLayerSpec
                {
                    XMin = s,
                    XMax = e,
                    YMin = s,
                    YMax = e,
                }
            );
            foreach (var size in new[] { Size, 4 })
            {
                using var plan = TestPlans.Create(pool, size, size);
                if (length == LayerPlan.MaxElements)
                {
                    var lines = plan.Render(segment);
                    Assert.Equal((1, length), (lines.Polylines.Length, lines.Segments.Length));
                    Assert.Equal(length + 1, plan.Render(rect).Rects.Length);
                    continue;
                }

                foreach (var spec in new[] { segment, rect })
                {
                    var message = Assert.Throws<InvalidOperationException>(() => plan.Render(spec)).Message;
                    Assert.Contains("131073", message, StringComparison.Ordinal);
                    Assert.Contains("131072", message, StringComparison.Ordinal);
                }
            }
        }

        using var x0 = Column.Wrap(new double[] { 10, 60 });
        using var x1 = Column.Wrap(new double[] { 20, 40 });
        using var y0 = Column.Wrap(new double[] { 1, 100 });
        using var y1 = Column.Wrap(new double[] { 10, 10 });
        var corners = DataTable.Create().Add("x0", x0).Add("x1", x1).Add("y0", y0).Add("y1", y1).Build();
        var layer = new RectLayerSpec
        {
            XMin = corners.Col<double>("x0"),
            XMax = corners.Col<double>("x1"),
            YMin = corners.Col<double>("y0"),
            YMax = corners.Col<double>("y1"),
        };
        var flipped = Spec(corners, layer) with
        {
            ScaleX = Scale.Linear().WithDomain(0, Size) with { Reverse = true },
            ScaleY = Scale.Log().WithDomain(1, 100),
        };
        using var flippedPlan = TestPlans.Create(pool, Size, Size);
        Assert.Equal([(80f, 50f, 10f, 50f), (40f, 0f, 20f, 50f)], DataRects(flippedPlan.Render(flipped)));
    }

    /// <summary>
    /// No geom draws an element with an end that maps to a value that is not finite (design §5.5.3): a bar whose left end
    /// is 0 on a log x, a NaN value of a bar, a segment, a rect, and an error bar.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - x 0.5, 2, 4 on a log x with domain [0.1, 10]; y 5, NaN, 5; bars of width 1.
    ///
    /// Steps:
    /// - Render bars, segments from (x, y) to (5, 8), rects over [x, 5] × [y, 8], and error bars from y to 8.
    ///
    /// Expected:
    /// - One bar (the third); two segments, two rects, and two error bars: the first and the third.
    /// </remarks>
    [Fact]
    public void Elements_WithAnEndNotFinite_AreNotDrawn()
    {
        using var pool = new PlotWorkerPool(1);
        using var x = Column.Wrap(new double[] { 0.5, 2, 4 });
        using var y = Column.Wrap(new double[] { 5, double.NaN, 5 });
        using var five = Column.Wrap(new double[] { 5, 5, 5 });
        using var eight = Column.Wrap(new double[] { 8, 8, 8 });
        var table = DataTable.Create().Add("x", x).Add("y", y).Add("five", five).Add("eight", eight).Build();
        var (rx, ry, r5, r8) = (table.Col<double>("x"), table.Col<double>("y"), table.Col<double>("five"), table.Col<double>("eight"));
        using var plan = TestPlans.Create(pool, Size, Size);
        PlotSpec On(LayerSpec layer) => Spec(table, layer) with { ScaleX = Scale.Log().WithDomain(0.1, 10) };

        var bar = new BarLayerSpec
        {
            X = rx,
            Y = ry,
            Width = 1,
        };
        var segment = new SegmentLayerSpec
        {
            X = rx,
            Y = ry,
            XEnd = r5,
            YEnd = r8,
        };
        var rect = new RectLayerSpec
        {
            XMin = rx,
            XMax = r5,
            YMin = ry,
            YMax = r8,
        };
        var errorBar = new ErrorBarLayerSpec
        {
            X = rx,
            YMin = ry,
            YMax = r8,
            CapWidth = 0,
        };

        Assert.Single(DataRects(plan.Render(On(bar))));
        Assert.Equal(2, plan.Render(On(segment)).Segments.Length);
        Assert.Equal(2, DataRects(plan.Render(On(rect))).Length);
        Assert.Equal(2, plan.Render(On(errorBar)).Segments.Length);
    }

    /// <summary>A time scale refuses a new geom's channel that is not a long date-time column, naming it and its kind (design §4.8).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A table of a Column&lt;long&gt; "t", a Column&lt;double&gt; "d", and floats "a".
    ///
    /// Steps:
    /// - Render a bar over "d" and a vertical rule over "d" with a time x scale; a bar over "t".
    ///
    /// Expected:
    /// - ArgumentException naming "d" and Column&lt;double&gt; for both; the bar over "t" draws.
    /// </remarks>
    [Fact]
    public void TimeScale_NewGeomChannelNotLong_Throws()
    {
        using var pool = new PlotWorkerPool(1);
        using var t = Column.Wrap(new long[] { 1, 2, 3 });
        using var d = Column.Wrap(new double[] { 1, 2, 3 });
        using var a = Column.Wrap(new float[] { 1, 2, 3 });
        var table = DataTable.Create().Add("t", t).Add("d", d).Add("a", a).Build();
        using var plan = TestPlans.Create(pool, Size, Size);
        var plot = Plot.Create(table).Theme(TestPlans.Bare).ScaleX(Scale.Time(TimeUnit.Nanoseconds)).Build();

        foreach (
            LayerSpec layer in new LayerSpec[]
            {
                new BarLayerSpec { X = table.Col<double>("d"), Y = table.Col<float>("a") },
                new RuleLayerSpec { X = table.Col<double>("d") },
            }
        )
        {
            var message = Assert.Throws<ArgumentException>(() => plan.Render(plot with { Layers = [layer] })).Message;
            Assert.Contains("\"d\"", message, StringComparison.Ordinal);
            Assert.Contains(d.KindName, message, StringComparison.Ordinal);
        }

        Assert.Equal(
            3,
            DataRects(plan.Render(plot with { Layers = [new BarLayerSpec { X = table.Col<long>("t"), Y = table.Col<float>("a") }] })).Length
        );
    }

    /// <summary>A probe that turns an index of a table into an untyped reference, whatever the column's type.</summary>
    private readonly struct ColumnRefProbe(DataTable table, int index)
    {
        public ColumnRef Ref =>
            table.GetColumn(index) switch
            {
                Column<double> => table.Col<double>(table.GetName(index)),
                UniformColumn => table.Col<double>(table.GetName(index)),
                _ => throw new InvalidOperationException(),
            };
    }
}
