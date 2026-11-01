using System.Globalization;
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

    /// <summary>
    /// Bars over categories (§10.1 scene 13, row G7b): a band scale with 12 categories, several rows in some, negative values,
    /// and labels too wide for every band, shown every second category.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 36 rows over 12 categories named "Category 01" to "Category 12" (3 rows each, one of them missing in category 7,
    ///   NaN in category 9), seeded values from −4 to 8; the fake text backend; Theme.Light; Scale.Band().
    ///
    /// Steps:
    /// - Render at 640 × 360; compare with <c>Golden/categorical-bar.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void CategoricalBar_MatchesBaseline()
    {
        var names = new string?[36];
        var values = new double[36];
        var random = new Random(41);
        for (var i = 0; i < 36; i++)
        {
            var category = i / 3;
            names[i] = category == 6 && i % 3 == 2 ? null : $"Category {category + 1:00}";
            values[i] = category == 8 && i % 3 == 0 ? double.NaN : Math.Round((random.NextDouble() * 12) - 4, 2);
        }

        using var pool = new PlotWorkerPool(2);
        using var y = Column.Wrap(values);
        var table = DataTable.Create().Add("group", Column.Categorical(names)).Add("y", y).Build();
        var spec = Plot.Create(table)
            .Bar(table.Cat("group"), table.Col<double>("y"), 0, b => b.Fill(new Color(31, 119, 180, 220)).Label("value"))
            .ScaleX(Scale.Band().WithTitle("Group"))
            .ScaleY(Scale.Linear().WithTitle("Value"))
            .Title("Categories")
            .Build();

        Check(spec, pool, "categorical-bar.png");
    }

    /// <summary>
    /// Layers split by categories (§10.1 scene 14, row G7c): a line whose three categories are mixed row by row (gathered),
    /// bars whose categories lie in runs (drawn over their runs), and the ordinal scale's title and category rows in the
    /// legend.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 300 rows: an evenly spaced X; a line's Y, a sine shifted by its category, with category i mod 3 of "a", "b", "c";
    ///   a bar's Y, with category ⌊i / 100⌋; the fake text backend; Theme.Light; an ordinal scale titled "Sensor".
    ///
    /// Steps:
    /// - Render at 640 × 360; compare with <c>Golden/color-categorical.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void CategoricalColor_MatchesBaseline()
    {
        string[] categories = ["a", "b", "c"];
        var mixed = new string?[300];
        var runs = new string?[300];
        var line = new float[300];
        var bars = new float[300];
        for (var i = 0; i < 300; i++)
        {
            (mixed[i], runs[i]) = (categories[i % 3], categories[i / 100]);
            line[i] = (float)(Math.Sin(i / 20.0) + ((i % 3) * 1.5));
            bars[i] = (float)(1 + Math.Abs(Math.Cos(i / 37.0)));
        }

        using var pool = new PlotWorkerPool(2);
        using var lineY = Column.Wrap(line);
        using var barY = Column.Wrap(bars);
        var table = DataTable
            .Create()
            .Add("t", Column.Uniform(0, 1, 300))
            .Add("y", lineY)
            .Add("level", barY)
            .Add("sensor", Column.Categorical(mixed, categories))
            .Add("phase", Column.Categorical(runs, categories))
            .Build();
        var spec = Plot.Create(table)
            .Bar(table.Col<double>("t"), table.Col<float>("level"), configure: b => b.ColorBy(table.Cat("phase")))
            .Line(table.Col<double>("t"), table.Col<float>("y"), l => l.ColorBy(table.Cat("sensor")).Width(1.5f))
            .ScaleColor(Scale.Ordinal().WithTitle("Sensor"))
            .ScaleX(Scale.Linear().WithTitle("Time"))
            .ScaleY(Scale.Linear().WithTitle("Value"))
            .Title("Split by category")
            .Build();

        Check(spec, pool, "color-categorical.png");
    }

    /// <summary>
    /// A continuous color (§10.1 scene 15, row G7c): a grid of rects colored by a number through viridis, and the color bar
    /// with its title and ticks.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A 20 × 12 grid of unit rects, each colored by sin(x / 3) · cos(y / 2) · 10; the fake text backend; Theme.Light; a
    ///   sequential scale titled "Value".
    ///
    /// Steps:
    /// - Render at 640 × 360; compare with <c>Golden/color-continuous.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void ContinuousColor_MatchesBaseline()
    {
        const int Columns = 20;
        const int Rows = 12;
        var (x0, y0, value) = (new double[Columns * Rows], new double[Columns * Rows], new double[Columns * Rows]);
        for (var i = 0; i < value.Length; i++)
        {
            (x0[i], y0[i]) = (i % Columns, i / Columns);
            value[i] = Math.Sin(x0[i] / 3) * Math.Cos(y0[i] / 2) * 10;
        }

        using var pool = new PlotWorkerPool(2);
        using var xMin = Column.Wrap(x0);
        using var xMax = Column.Wrap(x0.Select(v => v + 1).ToArray());
        using var yMin = Column.Wrap(y0);
        using var yMax = Column.Wrap(y0.Select(v => v + 1).ToArray());
        using var v = Column.Wrap(value);
        var table = DataTable.Create().Add("x0", xMin).Add("x1", xMax).Add("y0", yMin).Add("y1", yMax).Add("v", v).Build();
        var spec = Plot.Create(table)
            .Rect(
                table.Col<double>("x0"),
                table.Col<double>("x1"),
                table.Col<double>("y0"),
                table.Col<double>("y1"),
                r => r.ColorBy(table.Col<double>("v"))
            )
            .ScaleColor(Scale.Sequential(ColorMaps.Viridis, Scale.Linear().WithTitle("Value")))
            .Title("Continuous color")
            .Build();

        Check(spec, pool, "color-continuous.png");
    }

    /// <summary>
    /// Text labels (§10.1 scene 16, row G8): bars over 6 categories with their values above them, and their categories' names
    /// inside them, turned and colored by the category.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 6 categories "Alpha" to "Zeta" with seeded values from 2 to 10 and a column of their halves; the fake text backend;
    ///   Theme.Light; a band x and a y domain of [0, 1.2 times the greatest value], so that the tallest bar's label stays in
    ///   the panel.
    ///
    /// Steps:
    /// - Render at 640 × 360 bars, values above them (bottom-aligned, 4 DIPs up, "N0"), and names at half their height
    ///   (clockwise, colored by the category); compare with <c>Golden/text-labels.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void TextLabels_MatchBaseline()
    {
        string?[] names = ["Alpha", "Beta", "Gamma", "Delta", "Epsilon", "Zeta"];
        var random = new Random(16);
        var values = names.Select(_ => Math.Round(2 + (random.NextDouble() * 8), 1)).ToArray();
        using var pool = new PlotWorkerPool(2);
        using var y = Column.Wrap(values);
        using var half = Column.Wrap(values.Select(v => v / 2).ToArray());
        var table = DataTable.Create().Add("name", Column.Categorical(names)).Add("y", y).Add("half", half).Build();
        var spec = Plot.Create(table)
            .Bar(table.Cat("name"), table.Col<double>("y"), 0, b => b.Fill(new Color(200, 200, 200, 255)))
            .Text(
                table.Cat("name"),
                table.Col<double>("y"),
                table.Col<double>("y"),
                t =>
                    t.Align(TextAlign.Center, TextBaseline.Bottom)
                        .Offset(0, 4)
                        .Format(static (v, d, out n) => v.TryFormat(d, out n, "N0", CultureInfo.InvariantCulture))
            )
            .Text(
                table.Cat("name"),
                table.Col<double>("half"),
                table.Cat("name"),
                t => t.Rotation(TextRotation.Clockwise90).ColorBy(table.Cat("name"))
            )
            .ScaleX(Scale.Band().WithTitle("Name"))
            .ScaleY(Scale.Linear().WithDomain(0, values.Max() * 1.2).WithTitle("Value"))
            .Title("Text labels")
            .Build();

        Check(spec, pool, "text-labels.png");
    }

    /// <summary>
    /// A raster of numbers magnified (§10.1 scene 17, row G9): 40 × 24 floats of sin · cos with a small rectangle of NaN, in
    /// viridis through a sequential scale, read nearest, with its color bar.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The raster above; the fake text backend; Theme.Light.
    ///
    /// Steps:
    /// - Render at 640 × 360; compare with <c>Golden/image-nearest.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void ImageNearest_MatchesBaseline()
    {
        using var pool = new PlotWorkerPool(2);
        using var values = Column.Wrap(Field());
        var spec = Plot.Create(DataTable.Create().Build())
            .Image(Raster.Of(values, 40))
            .ScaleX(Scale.Linear().WithTitle("Column"))
            .ScaleY(Scale.Linear().WithTitle("Row"))
            .ScaleColor(Scale.Sequential(ColorMaps.Viridis, Scale.Linear().WithTitle("Value")))
            .Title("Image, nearest")
            .Build();

        Check(spec, pool, "image-nearest.png");
    }

    /// <summary>
    /// The same raster read bilinear, row 0 at the bottom, and turned over left to right by its extent (§10.1 scene 18).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The raster of scene 17; RasterOrigin.Lower; the extent (40, 0, 0, 24); Theme.Light.
    ///
    /// Steps:
    /// - Render at 640 × 360; compare with <c>Golden/image-bilinear.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void ImageBilinear_MatchesBaseline()
    {
        using var pool = new PlotWorkerPool(2);
        using var values = Column.Wrap(Field());
        var spec = Plot.Create(DataTable.Create().Build())
            .Image(Raster.Of(values, 40), i => i.Interpolation(RasterInterpolation.Bilinear).Origin(RasterOrigin.Lower).Extent(40, 0, 0, 24))
            .ScaleColor(Scale.Sequential(ColorMaps.Viridis, Scale.Linear().WithTitle("Value")))
            .Title("Image, bilinear")
            .Build();

        Check(spec, pool, "image-bilinear.png");
    }

    /// <summary>
    /// An RGBA image over a filled rectangle (§10.1 scene 19): 64 × 48 pixels of a color grid and bands of alpha 0, 128, and
    /// 255, at an opacity of 0.8.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The image above over a gray rectangle covering its left half; Theme.Light.
    ///
    /// Steps:
    /// - Render at 640 × 360; compare with <c>Golden/image-rgba.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void ImageRgba_MatchesBaseline()
    {
        const int Width = 64;
        const int Height = 48;
        var pixels = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var i = ((y * Width) + x) * 4;
                pixels[i] = (byte)(x * 4);
                pixels[i + 1] = (byte)(y * 5);
                pixels[i + 2] = (byte)(((x / 8) + (y / 8)) % 2 == 0 ? 200 : 40);
                pixels[i + 3] =
                    y < 16 ? (byte)255
                    : y < 32 ? (byte)128
                    : (byte)0;
            }
        }

        using var pool = new PlotWorkerPool(2);
        using var bytes = Column.Wrap(pixels);
        using var x0 = Column.Wrap(new double[] { 0 });
        using var x1 = Column.Wrap(new double[] { 32 });
        using var y0 = Column.Wrap(new double[] { 0 });
        using var y1 = Column.Wrap(new double[] { 48 });
        var table = DataTable.Create().Add("x0", x0).Add("x1", x1).Add("y0", y0).Add("y1", y1).Build();
        var spec = Plot.Create(table)
            .Rect(
                table.Col<double>("x0"),
                table.Col<double>("x1"),
                table.Col<double>("y0"),
                table.Col<double>("y1"),
                r => r.Fill(new Color(128, 128, 128, 255))
            )
            .Image(Raster.Rgba(bytes, Width), i => i.Extent(0, 64, 0, 48).Opacity(0.8))
            .Title("RGBA image")
            .Build();

        Check(spec, pool, "image-rgba.png");
    }

    /// <summary>Returns 40 × 24 values of sin · cos, row 0 first, with NaN over columns 30 to 33 of rows 4 to 7.</summary>
    private static float[] Field()
    {
        var values = new float[40 * 24];
        for (var row = 0; row < 24; row++)
        {
            for (var column = 0; column < 40; column++)
            {
                values[(row * 40) + column] =
                    column is >= 30 and < 34 && row is >= 4 and < 8 ? float.NaN : (float)(Math.Sin(column / 6.0) * Math.Cos(row / 4.0));
            }
        }

        return values;
    }

    private static void Check(PlotSpec spec, PlotWorkerPool pool, string baseline)
    {
        using var backend = D3D11Backend.CreateForTest(useWarp: true, createDevice: null, new TextEngine(new FakeTextBackend()));
        using var renderer = new HeadlessRenderer(backend, Width, Height, pool);
        renderer.Render(spec);

        GoldenImage.AssertMatches(renderer, GoldenImageTests.Baseline(baseline), GoldenImageTests.Tolerance, GoldenImageTests.Output);
    }
}
