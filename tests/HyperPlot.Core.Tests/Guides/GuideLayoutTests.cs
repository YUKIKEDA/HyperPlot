using System.Numerics;
using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>
/// The layout and the recording of the guides of row G3 (design §4.6, §4.7, §4.8, §10), with the fake text backend: one
/// glyph a character of 0.5625 em, lines of 1.25 em.
/// </summary>
public sealed class GuideLayoutTests : IDisposable
{
    private const int Width = 640;
    private const int Height = 360;
    private const int Length = 500;
    private readonly PlotWorkerPool _pool = new(1);
    private readonly Column<float> _a;
    private readonly Column<float> _b;
    private readonly DataTable _table;

    public GuideLayoutTests()
    {
        _a = Column.Wrap(Enumerable.Range(0, Length).Select(i => (float)Math.Sin(i / 40.0) * 3).ToArray());
        _b = Column.Wrap(Enumerable.Range(0, Length).Select(i => (float)Math.Cos(i / 30.0)).ToArray());
        _table = DataTable.Create().Add("t", Column.Uniform(0, 1, Length)).Add("a", _a).Add("b", _b).Build();
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
        _pool.Dispose();
    }

    /// <summary>
    /// The panel is what the bands leave (design §4.6): padding, the title's height and gap, the legend's width and gaps, the
    /// x band of tick, gap, and line (and the axis title's), the y band of tick, gap, the widest y label (and the axis
    /// title's), at the scales 1 and 1.5; without axes there are no bands.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A 640 × 360 target; a spec with a title "Title", two labeled layers "ch1" and "ch2", axis titles "X" and "Y".
    ///
    /// Steps:
    /// - Lay out at the scales 1 and 1.5, and once more with ShowAxes false.
    ///
    /// Expected:
    /// - The panel's edges by the formulas of §4.6 with the fake backend's sizes, rounding P, G, T, and the swatch by ⌊v · s
    ///   + 0.5⌋ and each text extent up.
    /// </remarks>
    [Theory]
    [InlineData(1f, true)]
    [InlineData(1.5f, true)]
    [InlineData(1f, false)]
    public void Layout_PanelFollowsTheBands(float scale, bool axes)
    {
        var theme = Theme.Light with { ShowAxes = axes };
        var spec = Full() with { Theme = theme };
        using var plan = Plan(scale);
        _ = plan.Render(spec);
        var guides = plan.Guides;

        int R(float v) => (int)Math.Floor((v * scale) + 0.5);
        int Up(double v) => (int)Math.Ceiling(v);
        double Line(float size) => FakeTextBackend.LineHeight(size * scale);
        double TextWidth(string s, float size) => s.Length * FakeTextBackend.Advance(size * scale);
        var (p, g, t) = (R(theme.Padding), R(theme.Spacing), R(theme.TickLength));
        var title = Up(Line(theme.TitleSize)) + g;
        var legend = Up(R(theme.LegendSwatch) + g + TextWidth("ch1", theme.LegendSize)) + g;
        var xBand = axes ? t + g + Up(Line(theme.TickLabelSize)) + g + Up(Line(theme.AxisTitleSize)) : 0;
        var widest = guides.Y.TickLabels.ToArray().Max(l => TextWidth(l, theme.TickLabelSize));
        var yBand = axes ? t + g + Up(widest) + g + Up(Line(theme.AxisTitleSize)) : 0;

        Assert.Equal(p + title, guides.Top);
        Assert.Equal(Height - p - xBand - guides.Top, guides.Height);
        Assert.Equal(p + yBand, guides.Left);
        Assert.Equal(Width - p - legend - guides.Left, guides.Width);
    }

    /// <summary>
    /// Null and empty titles and labels are none (design §4.1): no title band, no legend, no axis title bands.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The same spec with every title and label null, then with every one empty.
    ///
    /// Steps:
    /// - Lay out both.
    ///
    /// Expected:
    /// - Both panels equal, starting at the padding at the top and ending at the padding on the right; the only texts are
    ///   the tick labels.
    /// </remarks>
    [Fact]
    public void Layout_NullAndEmpty_AreNone()
    {
        using var nulls = Plan(1);
        using var empties = Plan(1);
        _ = nulls.Render(Titled(null));
        _ = empties.Render(Titled(string.Empty));
        Assert.Equal((10, Width - 10), (nulls.Guides.Top, nulls.Guides.Left + nulls.Guides.Width));
        Assert.Equal(
            (nulls.Guides.Left, nulls.Guides.Top, nulls.Guides.Width, nulls.Guides.Height),
            (empties.Guides.Left, empties.Guides.Top, empties.Guides.Width, empties.Guides.Height)
        );
        var texts = empties.Render(Titled(string.Empty)).Texts.Length;
        Assert.Equal(empties.Guides.X.Count + empties.Guides.Y.Count, texts);

        PlotSpec Titled(string? text) =>
            Plot.Create(_table)
                .Line(_table.Col<double>("t"), _table.Col<float>("a"), l => l.Label(text))
                .ScaleX(Scale.Linear().WithTitle(text))
                .ScaleY(Scale.Linear().WithTitle(text))
                .Title(text)
                .Build();
    }

    /// <summary>
    /// The y ticks are chosen before x, and the widest y label sets the panel's width (design §4.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The same data with the y domain [0, 1] and [0, 1000000].
    ///
    /// Steps:
    /// - Lay out both.
    ///
    /// Expected:
    /// - The panel of the long labels is narrower by the difference of the widest labels, rounded up.
    /// </remarks>
    [Fact]
    public void Layout_YFirst_ItsLabelsSetTheWidth()
    {
        using var narrow = Plan(1);
        using var wide = Plan(1);
        _ = narrow.Render(Full() with { ScaleY = Scale.Linear().WithDomain(0, 1) });
        _ = wide.Render(Full() with { ScaleY = Scale.Linear().WithDomain(0, 1_000_000) });
        static double Widest(PlotPlan plan) => plan.Guides.Y.TickLabels.ToArray().Max(l => l.Length * FakeTextBackend.Advance(12));
        Assert.Equal((int)Math.Ceiling(Widest(wide)) - (int)Math.Ceiling(Widest(narrow)), narrow.Guides.Width - wide.Guides.Width);
    }

    /// <summary>A target too small for a panel draws only the background and throws nothing (design §4.6).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A 40 × 30 target and the full spec.
    ///
    /// Steps:
    /// - Render.
    ///
    /// Expected:
    /// - No command, and the guides report an empty panel.
    /// </remarks>
    [Fact]
    public void Layout_TooSmall_BackgroundOnly()
    {
        using var plan = new PlotPlan(_pool, 40, 30, new TextEngine(new FakeTextBackend()), 1);
        var list = plan.Render(Full());
        Assert.True(plan.Guides.Empty);
        Assert.Equal(0, list.Commands.Length);
    }

    /// <summary>
    /// The guides are recorded in the order of §4.6: the panel, the grid and the data under the panel's clip, then the axes,
    /// the labels, the titles, and the legend over everything.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The full spec at the scale 1.
    ///
    /// Steps:
    /// - Render; read the commands.
    ///
    /// Expected:
    /// - A rectangle first, of the panel and its color; polylines under the panel's clip (the grid, then the two data lines);
    ///   then polylines and texts with no clip; the last texts are the title and the legend's labels.
    /// </remarks>
    [Fact]
    public void Record_Order()
    {
        using var plan = Plan(1);
        var list = plan.Render(Full());
        var g = plan.Guides;
        var commands = list.Commands.ToArray();
        Assert.Equal(RenderCommandKind.Rect, commands[0].Kind);
        Assert.Equal(new RectInstance(g.Left, g.Top, g.Width, g.Height, Vector4.One), list.Rects[0]);
        var panel = new ClipRect(g.Left, g.Top, g.Left + g.Width, g.Top + g.Height);
        var grid = g.X.Count + g.Y.Count;
        var polylines = list.Polylines.ToArray();
        Assert.All(polylines.Take(grid + 2), p => Assert.Equal(panel, p.Clip));
        Assert.All(polylines.Skip(grid + 2), p => Assert.Equal(ClipRect.All, p.Clip));
        Assert.Equal(DataStyles(), polylines.Skip(grid).Take(2).Select(p => p.Style).ToArray());
        var texts = list.Texts.ToArray();
        Assert.Equal(["Title", "ch1", "ch2"], texts.TakeLast(3).Select(t => t.Text).ToArray());
        Assert.All(texts, t => Assert.Equal(ClipRect.All, t.Clip));

        static LineStyle[] DataStyles() =>
            [new LineStyle(new Color(31, 119, 180, 255), 1), new LineStyle(new Color(255, 127, 14, 255), 2, new LineDash(6, 3))];
    }

    /// <summary>
    /// Lines sit on pixels (design §4.6): of rounded width 1 at ⌊p⌋ + 0.5, of width 2 at ⌊p + 0.5⌋; the axis lines' centers
    /// half their width outside the panel; a tick runs from the axis line's center to T − its half width past the edge.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The full spec with axis, grid, and tick widths 1, then 2.
    ///
    /// Steps:
    /// - Render each; read the polylines after the data.
    ///
    /// Expected:
    /// - The x axis line at y = bottom + 0.5 (width 1) or bottom + 1 (width 2); the y axis line at x = left − 0.5 or left − 1;
    ///   each x tick from the x axis line to bottom + T − width / 2, at a grid line's x.
    /// </remarks>
    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    public void Record_LinesOnPixels(float width)
    {
        using var plan = Plan(1);
        var theme = Theme.Light with { AxisWidth = width, GridWidth = width, TickWidth = width };
        var list = plan.Render(Full() with { Theme = theme });
        var g = plan.Guides;
        var segments = list.Segments.ToArray();
        var polylines = list.Polylines.ToArray();
        var grid = g.X.Count + g.Y.Count;
        var bottom = g.Top + g.Height;
        var half = width / 2;
        var xAxis = segments[polylines[grid + 2].SegmentStart];
        var yAxis = segments[polylines[grid + 3].SegmentStart];
        Assert.Equal(bottom + half, xAxis.Start.Y);
        Assert.Equal(g.Left - half, yAxis.Start.X);
        for (var i = 0; i < g.X.Count; i++)
        {
            var gridLine = segments[polylines[i].SegmentStart];
            var expected = width == 1 ? MathF.Floor((float)(g.Left + g.X.Pixels[i])) + 0.5f : MathF.Floor((float)(g.Left + g.X.Pixels[i]) + 0.5f);
            Assert.Equal(expected, gridLine.Start.X);
            var tick = segments[polylines[grid + 4 + i].SegmentStart];
            Assert.Equal(expected, tick.Start.X);
            Assert.Equal(bottom + half, tick.Start.Y);
            Assert.Equal(bottom + theme.TickLength - half, tick.End.Y);
        }
    }

    /// <summary>
    /// The legend's sample is the layer's stroke times the scale, and the texts take the theme's color, size times the
    /// scale, font, and locale (design §4.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The full spec with the font "Mono" and the locale "ja-jp", at the scale 1.5.
    ///
    /// Steps:
    /// - Render; read the last two polylines and every text.
    ///
    /// Expected:
    /// - The samples' widths 1.5 and 3, the dash 9 on 4.5; every text in the Foreground, "Mono", "ja-jp", and the title at
    ///   16 · 1.5.
    /// </remarks>
    [Fact]
    public void Record_LegendAndTextStyles()
    {
        using var plan = Plan(1.5f);
        var theme = Theme.Light with { FontFamily = "Mono", Locale = "ja-jp" };
        var list = plan.Render(Full() with { Theme = theme });
        var polylines = list.Polylines.ToArray();
        Assert.Equal(new LineStyle(new Color(31, 119, 180, 255), 1.5f), polylines[^2].Style);
        Assert.Equal(new LineStyle(new Color(255, 127, 14, 255), 3, new LineDash(9, 4.5f)), polylines[^1].Style);
        var texts = list.Texts.ToArray();
        Assert.All(texts, t => Assert.Equal((theme.Foreground, "Mono", "ja-jp"), (t.Style.Color, t.Style.FontFamily, t.Style.Locale)));
        Assert.Equal(16 * 1.5f, texts.Single(t => t.Text == "Title").Style.Size);
    }

    /// <summary>A legend row is as tall as the larger of the line and the sample's width (design §4.6).</summary>
    /// <remarks>
    /// Preconditions:
    /// - Two labeled layers of width 20 (the legend's line is 15 tall), then of width 1.
    ///
    /// Steps:
    /// - Render each; read the two labels' positions.
    ///
    /// Expected:
    /// - The labels 20 apart, then 15 apart, the first a half row below the panel's top.
    /// </remarks>
    [Fact]
    public void Record_LegendRows()
    {
        foreach (var (width, row) in new[] { (20f, 20f), (1f, FakeTextBackend.LineHeight(12)) })
        {
            using var plan = Plan(1);
            var spec = Plot.Create(_table)
                .Line(_table.Col<double>("t"), _table.Col<float>("a"), l => l.Width(width).Label("ch1"))
                .Line(_table.Col<double>("t"), _table.Col<float>("b"), l => l.Width(width).Label("ch2"))
                .Build();
            var texts = plan.Render(spec).Texts.ToArray();
            var first = texts.Single(t => t.Text == "ch1").Position.Y;
            var second = texts.Single(t => t.Text == "ch2").Position.Y;
            Assert.Equal(row, second - first);
            Assert.Equal(plan.Guides.Top + (row / 2), first);
        }
    }

    /// <summary>
    /// At the scale 1.5 the data's widths and dashes are 1.5 times the spec's; at 1 they are the spec's (design §4.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A layer of width 2, dash (6, 3, 1).
    ///
    /// Steps:
    /// - Render at 1 and at 1.5 with the bare theme.
    ///
    /// Expected:
    /// - (2, 6, 3, 1) and (3, 9, 4.5, 1.5).
    /// </remarks>
    [Fact]
    public void Scale_DataStrokes()
    {
        var spec = Plot.Create(_table)
            .Line(_table.Col<double>("t"), _table.Col<float>("a"), l => l.Width(2).Dash(new LineDash(6, 3, 1)))
            .Theme(TestPlans.Bare)
            .Build();
        using var one = Plan(1);
        using var half = Plan(1.5f);
        Assert.Equal(new LineStyle(new Color(0, 0, 0, 255), 2, new LineDash(6, 3, 1)), one.Render(spec).Polylines[0].Style);
        Assert.Equal(new LineStyle(new Color(0, 0, 0, 255), 3, new LineDash(9, 4.5f, 1.5f)), half.Render(spec).Polylines[0].Style);
    }

    /// <summary>
    /// Each theme member out of range throws an ArgumentException naming it, a null theme ArgumentNullException, and a text
    /// size past 512 at the scale too (design §4.7).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The full spec with one member broken at a time.
    ///
    /// Steps:
    /// - Render each.
    ///
    /// Expected:
    /// - The exception names the member; 400 is fine at 1 and fails at 1.5.
    /// </remarks>
    [Fact]
    public void Theme_Checks()
    {
        var broken = new (Theme Theme, string Name)[]
        {
            (Theme.Light with { TitleSize = 0 }, nameof(Theme.TitleSize)),
            (Theme.Light with { AxisTitleSize = float.NaN }, nameof(Theme.AxisTitleSize)),
            (Theme.Light with { TickLabelSize = -1 }, nameof(Theme.TickLabelSize)),
            (Theme.Light with { LegendSize = float.PositiveInfinity }, nameof(Theme.LegendSize)),
            (Theme.Light with { AxisWidth = -1 }, nameof(Theme.AxisWidth)),
            (Theme.Light with { GridWidth = float.NaN }, nameof(Theme.GridWidth)),
            (Theme.Light with { TickWidth = float.NegativeInfinity }, nameof(Theme.TickWidth)),
            (Theme.Light with { TickLength = -0.5f }, nameof(Theme.TickLength)),
            (Theme.Light with { Padding = -1 }, nameof(Theme.Padding)),
            (Theme.Light with { Spacing = float.NaN }, nameof(Theme.Spacing)),
            (Theme.Light with { LegendSwatch = -2 }, nameof(Theme.LegendSwatch)),
            (Theme.Light with { TickSpacingX = 0 }, nameof(Theme.TickSpacingX)),
            (Theme.Light with { TickSpacingY = float.NaN }, nameof(Theme.TickSpacingY)),
            (Theme.Light with { FontFamily = string.Empty }, nameof(Theme.FontFamily)),
            (Theme.Light with { Locale = null! }, nameof(Theme.Locale)),
        };
        foreach (var (theme, name) in broken)
        {
            using var plan = Plan(1);
            Assert.Contains(name, Assert.Throws<ArgumentException>(() => plan.Render(Full() with { Theme = theme })).Message);
        }

        using var nulls = Plan(1);
        _ = Assert.Throws<ArgumentNullException>(() => nulls.Render(Full() with { Theme = null! }));
        using var one = Plan(1);
        _ = one.Render(Full() with { Theme = Theme.Light with { TitleSize = 400 } });
        using var big = Plan(1.5f);
        Assert.Contains(
            nameof(Theme.TitleSize),
            Assert.Throws<ArgumentException>(() => big.Render(Full() with { Theme = Theme.Light with { TitleSize = 400 } })).Message
        );
    }

    /// <summary>The builders' null checks (design §4.1): WithTitle on a null scale and Theme(null) throw ArgumentNullException.</summary>
    /// <remarks>
    /// Preconditions:
    /// - None.
    ///
    /// Steps:
    /// - Call WithTitle on a null scale and PlotBuilder.Theme with null.
    ///
    /// Expected:
    /// - ArgumentNullException twice.
    /// </remarks>
    [Fact]
    public void Builders_NullChecks()
    {
        _ = Assert.Throws<ArgumentNullException>(() => ((LinearScaleSpec)null!).WithTitle("x"));
        _ = Assert.Throws<ArgumentNullException>(() => Plot.Create(_table).Theme(null!));
    }

    /// <summary>
    /// The diff of row G3 (design §4.8): the same spec does nothing again; a title, the theme's colors, a label, a format, or
    /// an axis title lays out and records again without M4; a padding that changes W extracts again; Nice extracts again.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The full spec drawn once.
    ///
    /// Steps:
    /// - Render the same spec, then each change, reading the counts each time.
    ///
    /// Expected:
    /// - (layouts, extractions, recordings) changes: (0, 0, 0); (1, 0, 1) for each look; (1, 1, 1) for the padding and nice.
    /// </remarks>
    [Fact]
    public void Diff_Counts()
    {
        using var plan = Plan(1);
        var spec = Full();
        _ = plan.Render(spec);
        Step(spec, (0, 0, 0));

        // A format of x and a label of the same length: neither changes W (x labels set only the x band's height).
        TickFormatter format = static (double v, Span<char> d, out int w) =>
            v.TryFormat(d, out w, "0.0", System.Globalization.CultureInfo.InvariantCulture);
        var looks = new[]
        {
            spec with
            {
                Title = "Other",
            },
            spec with
            {
                Theme = spec.Theme with { Foreground = new Color(1, 2, 3, 255) },
            },
            spec with
            {
                Layers = [((LineLayerSpec)spec.Layers[0]) with { Label = "ch9" }, spec.Layers[1]],
            },
            spec with
            {
                ScaleX = ((LinearScaleSpec)spec.ScaleX) with { TickFormat = format },
            },
            spec with
            {
                ScaleX = ((LinearScaleSpec)spec.ScaleX).WithTitle("Other x"),
            },
        };
        foreach (var look in looks)
        {
            _ = plan.Render(spec);
            Step(look, (1, 0, 1));
        }

        _ = plan.Render(spec);
        Step(spec with { Theme = spec.Theme with { Padding = 30 } }, (1, 1, 1));
        _ = plan.Render(spec);
        Step(spec with { ScaleX = ((LinearScaleSpec)spec.ScaleX) with { Nice = true } }, (1, 1, 1));

        void Step(PlotSpec next, (int Layouts, int Extractions, int Recordings) expected)
        {
            var before = (plan.Layouts, plan.Extractions, plan.Recordings);
            _ = plan.Render(next);
            Assert.Equal(expected, (plan.Layouts - before.Layouts, plan.Extractions - before.Extractions, plan.Recordings - before.Recordings));
        }
    }

    /// <summary>
    /// Once warm, the same spec again and two domains in turn allocate nothing (design §4.6): the labels hit the table and
    /// the shaping cache.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The full spec and a copy with another y domain, each drawn once.
    ///
    /// Steps:
    /// - Render the same spec five times; render the two specs in turn five times.
    ///
    /// Expected:
    /// - The two domains' y labels give the panel two widths; 0 bytes on this thread for each.
    /// </remarks>
    [Fact]
    public void Allocation_SameSpecAndTwoDomains_Nothing()
    {
        using var plan = Plan(1);
        var first = Full() with { ScaleY = Scale.Linear().WithDomain(-3, 3) };
        var second = first with { ScaleY = Scale.Linear().WithDomain(-10, 10) };
        _ = plan.Render(first);
        _ = plan.Render(second);
        _ = plan.Render(first);
        Assert.NotEqual(plan.Guides.Width, Width(second));

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 5; i++)
        {
            _ = plan.Render(first);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 5; i++)
        {
            _ = plan.Render(second);
            _ = plan.Render(first);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        int Width(PlotSpec spec)
        {
            using var other = Plan(1);
            _ = other.Render(spec);
            return other.Guides.Width;
        }
    }

    /// <summary>
    /// A time domain outside the years 1 to 9999 throws while ticks are chosen, and draws when neither axes nor grid ask for
    /// ticks (design §4.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A long column of seconds from the Unix epoch reaching year −250.
    ///
    /// Steps:
    /// - Render with the light theme, then with ShowAxes and ShowGrid false.
    ///
    /// Expected:
    /// - InvalidOperationException, then lines.
    /// </remarks>
    [Fact]
    public void Time_OutsideDateTime_OnlyWhenTicksAreChosen()
    {
        using var seconds = Column.Wrap(new long[] { -70_000_000_000, -10, 0 });
        using var y = Column.Wrap(new float[] { 1, 2, 3 });
        var table = DataTable.Create().Add("t", seconds).Add("y", y).Build();
        var spec = Plot.Create(table).Line(table.Col<long>("t"), table.Col<float>("y")).ScaleX(Scale.Time(TimeUnit.Seconds)).Build();
        using var plan = Plan(1);
        _ = Assert.Throws<InvalidOperationException>(() => plan.Render(spec));
        using var bare = Plan(1);
        Assert.True(bare.Render(spec with { Theme = Theme.Light with { ShowAxes = false, ShowGrid = false } }).Polylines.Length > 0);
    }

    /// <summary>A label of 0 characters is not drawn (design §4.6).</summary>
    /// <remarks>
    /// Preconditions:
    /// - The full spec with a y format that writes nothing.
    ///
    /// Steps:
    /// - Render.
    ///
    /// Expected:
    /// - No text of the y ticks: the texts are the x labels, the titles, and the legend only.
    /// </remarks>
    [Fact]
    public void EmptyLabel_NotDrawn()
    {
        TickFormatter nothing = static (double _, Span<char> _, out int w) =>
        {
            w = 0;
            return true;
        };
        using var plan = Plan(1);
        var spec = Full();
        var list = plan.Render(spec with { ScaleY = ((LinearScaleSpec)spec.ScaleY) with { TickFormat = nothing } });
        Assert.Equal(plan.Guides.X.Count + 3 + 2, list.Texts.Length);
    }

    private PlotPlan Plan(float scale) => new(_pool, Width, Height, new TextEngine(new FakeTextBackend()), scale);

    private PlotSpec Full() =>
        Plot.Create(_table)
            .Line(_table.Col<double>("t"), _table.Col<float>("a"), l => l.Color(new Color(31, 119, 180, 255)).Label("ch1"))
            .Line(
                _table.Col<double>("t"),
                _table.Col<float>("b"),
                l => l.Color(new Color(255, 127, 14, 255)).Width(2).Dash(new LineDash(6, 3)).Label("ch2")
            )
            .ScaleX(Scale.Linear().WithTitle("X"))
            .ScaleY(Scale.Linear().WithTitle("Y"))
            .Title("Title")
            .Build();
}
