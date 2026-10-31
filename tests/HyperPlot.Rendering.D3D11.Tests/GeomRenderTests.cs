using HyperPlot.Testing;
using HyperPlot.Tests;

namespace HyperPlot.Rendering.D3D11.Tests;

/// <summary>Row G4's geoms and row G10's flip drawn on WARP (design §5.5.3, §10.1 scenes 8, 9, and 11), with the fake text backend.</summary>
public sealed class GeomRenderTests
{
    private const int Width = 640;
    private const int Height = 360;
    private const int Points = 100;

    /// <summary>
    /// The fill geoms (§10.1 scene 8): an area with Baseline 0 and an outline, a band, 20 bars, and 4 rects over 100 x.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 100 evenly spaced x from 0 to 99; seeded curves; bars and rects whose other elements are NaN, so not drawn; the fake
    ///   text backend; Theme.Light.
    ///
    /// Steps:
    /// - Render at 640 × 360; compare with <c>Golden/geoms-fill.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void Fill_MatchesBaseline()
    {
        var curve = Enumerable.Range(0, Points).Select(i => 3 + (1.5 * Math.Sin(i * 0.08))).ToArray();
        var low = curve.Select((v, i) => v - 0.6 - (0.3 * Math.Cos(i * 0.2))).ToArray();
        var high = curve.Select((v, i) => v + 0.6 + (0.3 * Math.Sin(i * 0.15))).ToArray();
        var area = Enumerable.Range(0, Points).Select(i => 1.2 + (0.8 * Math.Cos(i * 0.05))).ToArray();
        var bars = Enumerable.Range(0, Points).Select(i => i % 5 == 2 ? -1 - ((0.08 * i) % 1.5) : double.NaN).ToArray();
        double[] Rect(params double[] values) => Enumerable.Range(0, Points).Select(i => i < values.Length ? values[i] : double.NaN).ToArray();
        using var pool = new PlotWorkerPool(2);
        using var lowColumn = Column.Wrap(low);
        using var highColumn = Column.Wrap(high);
        using var areaColumn = Column.Wrap(area);
        using var barColumn = Column.Wrap(bars);
        using var x0 = Column.Wrap(Rect(5, 30, 55, 80));
        using var x1 = Column.Wrap(Rect(15, 42, 63, 95));
        using var y0 = Column.Wrap(Rect(5.2, 5.6, 5.0, 5.4));
        using var y1 = Column.Wrap(Rect(6.0, 6.3, 6.4, 5.9));
        var table = DataTable
            .Create()
            .Add("x", Column.Uniform(0, 1, Points))
            .Add("low", lowColumn)
            .Add("high", highColumn)
            .Add("area", areaColumn)
            .Add("bar", barColumn)
            .Add("x0", x0)
            .Add("x1", x1)
            .Add("y0", y0)
            .Add("y1", y1)
            .Build();
        var x = table.Col<double>("x");
        var spec = Plot.Create(table)
            .Band(x, table.Col<double>("low"), table.Col<double>("high"), b => b.Fill(new Color(31, 119, 180, 90)).Label("band"))
            .Area(
                x,
                table.Col<double>("area"),
                0,
                a => a.Fill(new Color(44, 160, 44, 110)).Outline(new StrokeSpec(new Color(20, 100, 20, 255), 1.5f)).Label("area")
            )
            .Bar(x, table.Col<double>("bar"), 0, 3, b => b.Fill(new Color(214, 39, 40, 200)).Label("bar"))
            .Rect(
                table.Col<double>("x0"),
                table.Col<double>("x1"),
                table.Col<double>("y0"),
                table.Col<double>("y1"),
                r => r.Fill(new Color(148, 103, 189, 140)).Label("rect")
            )
            .ScaleY(Scale.Linear().WithDomain(-2.5, 6.5).WithTitle("Value"))
            .ScaleX(Scale.Linear().WithTitle("x"))
            .Title("Fill geoms")
            .Build();

        Check(spec, pool, "geoms-fill.png");
    }

    /// <summary>
    /// The line geoms (§10.1 scene 9): steps in the three modes, vertical and horizontal rules, segments, and error bars with
    /// caps over 100 x.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 100 evenly spaced x from 0 to 99; seeded steps; rules at three x and two y; eight segments and ten error bars, the
    ///   other elements NaN; the fake text backend; Theme.Light.
    ///
    /// Steps:
    /// - Render at 640 × 360; compare with <c>Golden/geoms-line.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void Line_MatchesBaseline()
    {
        var random = new Random(9);
        double[] Walk(double start) => Enumerable.Range(0, Points).Select(_ => start += random.Next(-1, 2) * 0.25).ToArray();
        double[] Sparse(int every, Func<int, double> value) =>
            Enumerable.Range(0, Points).Select(i => i % every == 0 && i < 8 * every ? value(i) : double.NaN).ToArray();
        using var pool = new PlotWorkerPool(2);
        using var after = Column.Wrap(Walk(8));
        using var before = Column.Wrap(Walk(6));
        using var middle = Column.Wrap(Walk(4));
        using var vrule = Column.Wrap(Enumerable.Range(0, Points).Select(i => 20.0 + (30 * (i / 34))).ToArray());
        using var hrule = Column.Wrap(Enumerable.Range(0, Points).Select(i => i < 50 ? 1.0 : 9.5).ToArray());
        using var sx = Column.Wrap(Sparse(12, i => i));
        using var sy = Column.Wrap(Sparse(12, i => 0.5 + (i * 0.02)));
        using var ex = Column.Wrap(Sparse(12, i => i + 8));
        using var ey = Column.Wrap(Sparse(12, i => 2.5 - (i * 0.015)));
        var center = Enumerable.Range(0, Points).Select(i => 2 + Math.Sin(i * 0.1)).ToArray();
        using var lo = Column.Wrap(center.Select((v, i) => i % 10 == 5 ? v - 0.5 : double.NaN).ToArray());
        using var hi = Column.Wrap(center.Select((v, i) => i % 10 == 5 ? v + 0.4 : double.NaN).ToArray());
        var table = DataTable
            .Create()
            .Add("x", Column.Uniform(0, 1, Points))
            .Add("after", after)
            .Add("before", before)
            .Add("middle", middle)
            .Add("vrule", vrule)
            .Add("hrule", hrule)
            .Add("sx", sx)
            .Add("sy", sy)
            .Add("ex", ex)
            .Add("ey", ey)
            .Add("lo", lo)
            .Add("hi", hi)
            .Build();
        var x = table.Col<double>("x");
        var spec = Plot.Create(table)
            .VRule(table.Col<double>("vrule"), l => l.Color(new Color(127, 127, 127, 255)).Dash(new LineDash(4, 3)).Label("vrule"))
            .HRule(table.Col<double>("hrule"), l => l.Color(new Color(188, 189, 34, 255)).Width(2).Label("hrule"))
            .Step(x, table.Col<double>("after"), StepMode.After, l => l.Color(new Color(31, 119, 180, 255)).Label("after"))
            .Step(x, table.Col<double>("before"), StepMode.Before, l => l.Color(new Color(255, 127, 14, 255)).Label("before"))
            .Step(x, table.Col<double>("middle"), StepMode.Middle, l => l.Color(new Color(44, 160, 44, 255)).Label("middle"))
            .Segment(
                table.Col<double>("sx"),
                table.Col<double>("sy"),
                table.Col<double>("ex"),
                table.Col<double>("ey"),
                l => l.Color(new Color(148, 103, 189, 255)).Width(1.5f).Label("segment")
            )
            .ErrorBar(x, table.Col<double>("lo"), table.Col<double>("hi"), 6, l => l.Color(new Color(214, 39, 40, 255)).Label("errorbar"))
            .ScaleY(Scale.Linear().WithDomain(0, 10).WithTitle("Value"))
            .ScaleX(Scale.Linear().WithTitle("x"))
            .Title("Line geoms")
            .Build();

        Check(spec, pool, "geoms-line.png");
    }

    /// <summary>
    /// The flipped coordinate system (§10.1 scene 11, row G10): a line, an area, bars, vertical and horizontal rules, and
    /// error bars with caps, x on the left axis and y on the bottom one.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 100 evenly spaced x from 0 to 99; seeded curves; bars and error bars whose other elements are NaN, so not drawn;
    ///   rules at two x and one y; the fake text backend; Theme.Light; Coord.Cartesian(flip: true).
    ///
    /// Steps:
    /// - Render at 640 × 360; compare with <c>Golden/coord-flip.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void Flip_MatchesBaseline()
    {
        var line = Enumerable.Range(0, Points).Select(i => 6 + (2 * Math.Sin(i * 0.09))).ToArray();
        var area = Enumerable.Range(0, Points).Select(i => 2 + Math.Cos(i * 0.06)).ToArray();
        var bars = Enumerable.Range(0, Points).Select(i => i % 10 == 4 ? 1 + ((0.13 * i) % 2.5) : double.NaN).ToArray();
        var center = Enumerable.Range(0, Points).Select(i => 6 + (2 * Math.Sin(i * 0.09))).ToArray();
        using var pool = new PlotWorkerPool(2);
        using var lineColumn = Column.Wrap(line);
        using var areaColumn = Column.Wrap(area);
        using var barColumn = Column.Wrap(bars);
        using var lo = Column.Wrap(center.Select((v, i) => i % 10 == 7 ? v - 0.8 : double.NaN).ToArray());
        using var hi = Column.Wrap(center.Select((v, i) => i % 10 == 7 ? v + 0.6 : double.NaN).ToArray());
        using var vrule = Column.Wrap(Enumerable.Range(0, Points).Select(i => i < 50 ? 25.0 : 75.0).ToArray());
        using var hrule = Column.Wrap(Enumerable.Range(0, Points).Select(_ => 8.5).ToArray());
        var table = DataTable
            .Create()
            .Add("x", Column.Uniform(0, 1, Points))
            .Add("line", lineColumn)
            .Add("area", areaColumn)
            .Add("bar", barColumn)
            .Add("lo", lo)
            .Add("hi", hi)
            .Add("vrule", vrule)
            .Add("hrule", hrule)
            .Build();
        var x = table.Col<double>("x");
        var spec = Plot.Create(table)
            .Area(x, table.Col<double>("area"), 0, a => a.Fill(new Color(44, 160, 44, 110)).Label("area"))
            .Bar(x, table.Col<double>("bar"), 0, 3, b => b.Fill(new Color(214, 39, 40, 200)).Label("bar"))
            .VRule(table.Col<double>("vrule"), l => l.Color(new Color(127, 127, 127, 255)).Dash(new LineDash(4, 3)).Label("vrule"))
            .HRule(table.Col<double>("hrule"), l => l.Color(new Color(188, 189, 34, 255)).Width(2).Label("hrule"))
            .Line(x, table.Col<double>("line"), l => l.Color(new Color(31, 119, 180, 255)).Width(1.5f).Label("line"))
            .ErrorBar(x, table.Col<double>("lo"), table.Col<double>("hi"), 6, l => l.Color(new Color(148, 103, 189, 255)).Label("errorbar"))
            .ScaleY(Scale.Linear().WithDomain(0, 10).WithTitle("Value"))
            .ScaleX(Scale.Linear().WithTitle("x"))
            .Coord(Coord.Cartesian(flip: true))
            .Title("Flipped")
            .Build();

        Check(spec, pool, "coord-flip.png");
    }

    /// <summary>
    /// The polar coordinate system with x as the angle (§10.1 scene 12, row G11): an area, bars of a rose, and a line around
    /// the circle, with the angle's ticks around it and the radius's axis on its left.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 100 evenly spaced x from 0 to 99; a seeded area and line; 12 bars whose other elements are NaN; the fake text
    ///   backend; Theme.Light; Coord.Polar().
    ///
    /// Steps:
    /// - Render at 640 × 360; compare with <c>Golden/coord-polar.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void Polar_MatchesBaseline()
    {
        var line = Enumerable.Range(0, Points).Select(i => 6 + (2 * Math.Sin(i * 0.19))).ToArray();
        var area = Enumerable.Range(0, Points).Select(i => 2 + Math.Cos(i * 0.12)).ToArray();
        var bars = Enumerable.Range(0, Points).Select(i => i % 8 == 4 ? 3 + ((0.37 * i) % 4) : double.NaN).ToArray();
        using var pool = new PlotWorkerPool(2);
        using var lineColumn = Column.Wrap(line);
        using var areaColumn = Column.Wrap(area);
        using var barColumn = Column.Wrap(bars);
        var table = DataTable
            .Create()
            .Add("x", Column.Uniform(0, 1, Points))
            .Add("line", lineColumn)
            .Add("area", areaColumn)
            .Add("bar", barColumn)
            .Build();
        var x = table.Col<double>("x");
        var spec = Plot.Create(table)
            .Area(x, table.Col<double>("area"), 0, a => a.Fill(new Color(44, 160, 44, 110)).Label("area"))
            .Bar(x, table.Col<double>("bar"), 0, 6, b => b.Fill(new Color(214, 39, 40, 160)).Label("bar"))
            .Line(x, table.Col<double>("line"), l => l.Color(new Color(31, 119, 180, 255)).Width(1.5f).Label("line"))
            .ScaleY(Scale.Linear().WithDomain(0, 10).WithTitle("Value"))
            .ScaleX(Scale.Linear().WithDomain(0, 100).WithTitle("x"))
            .Coord(Coord.Polar())
            .Title("Polar")
            .Build();

        Check(spec, pool, "coord-polar.png");
    }

    /// <summary>
    /// The polar coordinate system with y as the angle and a hole (§10.1 scene 12, row G11): bars become parts of rings of a
    /// donut.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 5 bars at x 1 to 5 with y from 20 to 90; the fake text backend; Theme.Light; Coord.Polar(PolarTheta.Y,
    ///   innerRadius: 0.4), the y domain [0, 100].
    ///
    /// Steps:
    /// - Render at 640 × 360; compare with <c>Golden/coord-polar-donut.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void PolarDonut_MatchesBaseline()
    {
        using var pool = new PlotWorkerPool(2);
        using var values = Column.Wrap(new[] { 35.0, 90, 20, 62, 48 });
        var table = DataTable.Create().Add("x", Column.Uniform(1, 1, 5)).Add("y", values).Build();
        var spec = Plot.Create(table)
            .Bar(table.Col<double>("x"), table.Col<double>("y"), 0, 0.8, b => b.Fill(new Color(31, 119, 180, 220)).Label("share"))
            .ScaleY(Scale.Linear().WithDomain(0, 100).WithTitle("Percent"))
            .ScaleX(Scale.Linear().WithTitle("Group"))
            .Coord(Coord.Polar(PolarTheta.Y, innerRadius: 0.4))
            .Title("Donut")
            .Build();

        Check(spec, pool, "coord-polar-donut.png");
    }

    private static void Check(PlotSpec spec, PlotWorkerPool pool, string baseline)
    {
        using var backend = D3D11Backend.CreateForTest(useWarp: true, createDevice: null, new TextEngine(new FakeTextBackend()));
        using var renderer = new HeadlessRenderer(backend, Width, Height, pool);
        renderer.Render(spec);

        GoldenImage.AssertMatches(renderer, GoldenImageTests.Baseline(baseline), GoldenImageTests.Tolerance, GoldenImageTests.Output);
    }
}
