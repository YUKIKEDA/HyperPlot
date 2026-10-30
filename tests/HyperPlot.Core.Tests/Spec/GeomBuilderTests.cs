using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>The builders and specs of row G4's geoms (design §4.1, §10 G4 row).</summary>
public sealed class GeomBuilderTests
{
    private static readonly Color Red = new(200, 0, 0, 255);

    /// <summary>Each builder makes its spec with the columns, the options, the looks, and the label it was given.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A table of an evenly spaced "t" and the stored "a", "b", "c".
    ///
    /// Steps:
    /// - Build one plot of every geom, each configured; build a plot of every geom with the defaults.
    ///
    /// Expected:
    /// - The layers in order, of their record types, with their channels, options, looks, and labels; the defaults are a
    ///   black stroke 1 wide, a fill of (0, 0, 0, 128), Baseline 0, Width null, CapWidth 6, Mode After, no outline, no label.
    /// </remarks>
    [Fact]
    public void Build_EveryGeom_MakesItsSpec()
    {
        var table = Table();
        var (t, a, b, c) = (table.Col<double>("t"), table.Col<float>("a"), table.Col<float>("b"), table.Col<float>("c"));
        var outline = new LineStyle(Red, 2);
        var geom = new NoGeom();

        var plot = Plot.Create(table)
            .Step(t, a, StepMode.Middle, l => l.Color(Red).Label("step"))
            .Area(t, a, 2, f => f.Fill(Red).Outline(outline).Label("area"))
            .Band(t, a, b, f => f.Fill(Red).Outline(outline).Label("band"))
            .Bar(t, a, -1, 0.5, f => f.Fill(Red).Label("bar"))
            .Rect(a, b, b, c, f => f.Fill(Red).Label("rect"))
            .VRule(t, l => l.Width(2).Label("vrule"))
            .HRule(a, l => l.Width(3).Label("hrule"))
            .Segment(a, b, c, a, l => l.Color(Red).Label("segment"))
            .ErrorBar(t, a, b, 4, l => l.Color(Red).Label("errorbar"))
            .Custom(geom, "custom")
            .Line(t, a, l => l.Label("line"))
            .Build();

        var step = Assert.IsType<StepLayerSpec>(plot.Layers[0]);
        Assert.Equal((t.ToColumnRef(), a.ToColumnRef(), StepMode.Middle, Red), (step.X, step.Y, step.Mode, step.Style.Color));
        var area = Assert.IsType<AreaLayerSpec>(plot.Layers[1]);
        Assert.Equal((2d, Red, (LineStyle?)outline), (area.Baseline, area.Fill, area.Outline));
        var band = Assert.IsType<BandLayerSpec>(plot.Layers[2]);
        Assert.Equal((a.ToColumnRef(), b.ToColumnRef(), Red, (LineStyle?)outline), (band.YMin, band.YMax, band.Fill, band.Outline));
        var bar = Assert.IsType<BarLayerSpec>(plot.Layers[3]);
        Assert.Equal((-1d, (double?)0.5, Red), (bar.Baseline, bar.Width, bar.Fill));
        var rect = Assert.IsType<RectLayerSpec>(plot.Layers[4]);
        Assert.Equal((a.ToColumnRef(), b.ToColumnRef(), b.ToColumnRef(), c.ToColumnRef()), (rect.XMin, rect.XMax, rect.YMin, rect.YMax));
        var vrule = Assert.IsType<RuleLayerSpec>(plot.Layers[5]);
        Assert.Equal((t.ToColumnRef(), default(ColumnRef), 2f), (vrule.X, vrule.Y, vrule.Style.Width));
        var hrule = Assert.IsType<RuleLayerSpec>(plot.Layers[6]);
        Assert.Equal((default(ColumnRef), a.ToColumnRef(), 3f), (hrule.X, hrule.Y, hrule.Style.Width));
        var segment = Assert.IsType<SegmentLayerSpec>(plot.Layers[7]);
        Assert.Equal((a.ToColumnRef(), b.ToColumnRef(), c.ToColumnRef(), a.ToColumnRef()), (segment.X, segment.Y, segment.XEnd, segment.YEnd));
        var errorBar = Assert.IsType<ErrorBarLayerSpec>(plot.Layers[8]);
        Assert.Equal((a.ToColumnRef(), b.ToColumnRef(), 4f, Red), (errorBar.YMin, errorBar.YMax, errorBar.CapWidth, errorBar.Style.Color));
        Assert.Same(geom, Assert.IsType<CustomLayerSpec>(plot.Layers[9]).Geom);
        Assert.Equal(
            ["step", "area", "band", "bar", "rect", "vrule", "hrule", "segment", "errorbar", "custom", "line"],
            plot.Layers.Select(l => l.Label)
        );

        var defaults = Plot.Create(table)
            .Step(t, a)
            .Area(t, a)
            .Band(t, a, b)
            .Bar(t, a)
            .Rect(a, b, b, c)
            .VRule(t)
            .Segment(a, b, c, a)
            .ErrorBar(t, a, b)
            .Custom(geom)
            .Build();
        var black = new LineStyle(new Color(0, 0, 0, 255), 1);
        var half = new Color(0, 0, 0, 128);
        var s = (StepLayerSpec)defaults.Layers[0];
        Assert.Equal((StepMode.After, black), (s.Mode, s.Style));
        var ar = (AreaLayerSpec)defaults.Layers[1];
        Assert.Equal((0d, half, (LineStyle?)null), (ar.Baseline, ar.Fill, ar.Outline));
        var bd = (BandLayerSpec)defaults.Layers[2];
        Assert.Equal((half, (LineStyle?)null), (bd.Fill, bd.Outline));
        var br = (BarLayerSpec)defaults.Layers[3];
        Assert.Equal((0d, (double?)null, half), (br.Baseline, br.Width, br.Fill));
        Assert.Equal(half, ((RectLayerSpec)defaults.Layers[4]).Fill);
        Assert.Equal(black, ((RuleLayerSpec)defaults.Layers[5]).Style);
        Assert.Equal(black, ((SegmentLayerSpec)defaults.Layers[6]).Style);
        var eb = (ErrorBarLayerSpec)defaults.Layers[7];
        Assert.Equal((6f, black), (eb.CapWidth, eb.Style));
        Assert.All(defaults.Layers, l => Assert.Null(l.Label));
    }

    /// <summary>
    /// The builders refuse a default reference, another table's column, and an evenly spaced column for a channel that
    /// builds a pyramid (design §4.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two tables of the same shape.
    ///
    /// Steps:
    /// - For each geom, add it with a default reference, a column of the other table, and, for Y, YMin, and YMax, "t".
    ///
    /// Expected:
    /// - ArgumentException each time.
    /// </remarks>
    [Fact]
    public void Geoms_BadReference_Throw()
    {
        var table = Table();
        var other = Table();
        var (t, a, b) = (table.Col<double>("t"), table.Col<float>("a"), table.Col<float>("b"));
        var (d, o) = (default(ColumnRef<float>), other.Col<float>("a"));
        var (dt, ot) = (default(ColumnRef<double>), other.Col<double>("t"));
        var uniformY = table.Col<double>("t");
        var calls = new List<Func<PlotBuilder, PlotBuilder>>
        {
            p => p.Step(dt, a),
            p => p.Step(t, o),
            p => p.Step(t, uniformY),
            p => p.Area(ot, a),
            p => p.Area(t, d),
            p => p.Area(t, uniformY),
            p => p.Band(t, d, b),
            p => p.Band(t, a, o),
            p => p.Band(t, uniformY, uniformY),
            p => p.Bar(dt, a),
            p => p.Bar(t, o),
            p => p.Bar(t, uniformY),
            p => p.Rect(d, a, a, a),
            p => p.Rect(a, a, a, o),
            p => p.VRule(dt),
            p => p.VRule(ot),
            p => p.HRule(d),
            p => p.HRule(o),
            p => p.Segment(a, a, d, a),
            p => p.Segment(a, o, a, a),
            p => p.ErrorBar(dt, a, b),
            p => p.ErrorBar(t, a, o),
            p => p.ErrorBar(t, uniformY, uniformY),
        };

        foreach (var call in calls)
        {
            _ = Assert.Throws<ArgumentException>(() => call(Plot.Create(table)));
        }
    }

    /// <summary>The builders refuse options out of range and a stroke that RenderList would refuse (design §4.1, §5.1).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A table of "t", "a", and "b".
    ///
    /// Steps:
    /// - Add an area and a bar with a Baseline of NaN, +∞, and −∞; a bar with a Width of 0, -1, and NaN; an error bar with a
    ///   CapWidth of -1 and NaN; a step, a rule, a segment, and an error bar with a width of -1; an area with an outline of
    ///   width NaN; Custom with null; a CustomLayerSpec of null.
    ///
    /// Expected:
    /// - ArgumentOutOfRangeException for the options and the strokes; ArgumentNullException for the nulls.
    /// </remarks>
    [Fact]
    public void Geoms_OutOfRange_Throw()
    {
        var table = Table();
        var (t, a, b) = (table.Col<double>("t"), table.Col<float>("a"), table.Col<float>("b"));
        foreach (var baseline in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => Plot.Create(table).Area(t, a, baseline));
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => Plot.Create(table).Bar(t, a, baseline));
        }

        foreach (var width in new[] { 0, -1, double.NaN })
        {
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => Plot.Create(table).Bar(t, a, width: width));
        }

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Plot.Create(table).ErrorBar(t, a, b, -1));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Plot.Create(table).ErrorBar(t, a, b, float.NaN));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Plot.Create(table).Step(t, a, configure: l => l.Width(-1)));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Plot.Create(table).Step(t, a, (StepMode)3));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Plot.Create(table).VRule(t, l => l.Width(-1)));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Plot.Create(table).Segment(a, a, b, b, l => l.Width(-1)));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Plot.Create(table).ErrorBar(t, a, b, configure: l => l.Width(-1)));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Plot.Create(table).Area(t, a, configure: f => f.Outline(new LineStyle(Red, float.NaN))));
        _ = Assert.Throws<ArgumentNullException>(() => Plot.Create(table).Custom(null!));
        _ = Assert.Throws<ArgumentNullException>(() => new CustomLayerSpec(null!));
    }

    /// <summary>A rule spec made directly with both X and Y, or with neither, is refused when compiled (design §4.8).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A table of "t" and "a"; rule specs with both channels and with none.
    ///
    /// Steps:
    /// - Render each.
    ///
    /// Expected:
    /// - ArgumentException each time, naming both or neither.
    /// </remarks>
    [Fact]
    public void Rule_BothOrNeither_ThrowsWhenCompiled()
    {
        using var pool = new PlotWorkerPool(1);
        var table = Table();
        var plot = Plot.Create(table).Theme(TestPlans.Bare).Build();
        var both = new RuleLayerSpec { X = table.Col<double>("t"), Y = table.Col<float>("a") };
        var neither = new RuleLayerSpec();

        using var plan = TestPlans.Create(pool, 100, 100);
        Assert.Contains("both", Assert.Throws<ArgumentException>(() => plan.Render(plot with { Layers = [both] })).Message, StringComparison.Ordinal);
        Assert.Contains(
            "neither",
            Assert.Throws<ArgumentException>(() => plan.Render(plot with { Layers = [neither] })).Message,
            StringComparison.Ordinal
        );
    }

    /// <summary>The label set through LineBuilder and on a LineLayerSpec is the base LayerSpec's Label (design §4.1).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A table of "t" and "a".
    ///
    /// Steps:
    /// - Build a line labeled "x" through LineBuilder; read it as a LayerSpec; make a LineLayerSpec with a Label.
    ///
    /// Expected:
    /// - Both read "x" through LayerSpec.
    /// </remarks>
    [Fact]
    public void LineLabel_IsTheBaseLabel()
    {
        var table = Table();
        LayerSpec built = Plot.Create(table).Line(table.Col<double>("t"), table.Col<float>("a"), l => l.Label("x")).Build().Layers[0];
        LayerSpec made = new LineLayerSpec
        {
            X = table.Col<double>("t"),
            Y = table.Col<float>("a"),
            Label = "x",
        };

        Assert.Equal(("x", "x"), (built.Label, made.Label));
    }

    private static DataTable Table() =>
        DataTable
            .Create()
            .Add("t", Column.Uniform(0, 1, 3))
            .Add("a", Column.Wrap(new float[] { 1, 2, 3 }))
            .Add("b", Column.Wrap(new float[] { 2, 3, 4 }))
            .Add("c", Column.Wrap(new float[] { 3, 4, 5 }))
            .Build();

    private sealed class NoGeom : IGeom
    {
        public void Learn(GeomRanges ranges) { }

        public void Record(GeomFrame frame, RenderList list) { }
    }
}
