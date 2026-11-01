using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>
/// The layout and the recording of row G6b's facets (design §4.6, §10's G6b row): the panels' sizes and places by the
/// formulas, the strips, the tick bands of shared and free directions, the axes on the outer panels, and the facets with a
/// flip, a polar system, a secondary scale, and layers without rows, with the fake text backend: one glyph a character of
/// 0.5625 em, lines of 1.25 em.
/// </summary>
public sealed class FacetLayoutTests : IDisposable
{
    private const int Width = 640;
    private const int Height = 360;
    private const int Regions = 5;
    private const int Moments = 40;
    private const int Bottom = 0;
    private const int Top = 1;
    private const int Left = 2;
    private const int Right = 3;
    private static readonly string[] RegionNames = ["North", "South", "East", "West", "Center"];
    private static readonly string[] ChannelNames = ["Online", "Store"];
    private readonly PlotWorkerPool _pool = new(2);
    private readonly List<IDisposable> _owned = [];
    private readonly DataTable _table;

    public FacetLayoutTests()
    {
        // Each region's run of 40 moments, the two channels in turn; the last region's values a thousand times the others'.
        var count = Regions * Moments * 2;
        var (t, y) = (new double[count], new float[count]);
        var (region, channel) = (new string?[count], new string?[count]);
        for (var i = 0; i < count; i++)
        {
            var (r, k) = (i / (Moments * 2), i % 2);
            var time = (i % (Moments * 2)) / 2;
            (t[i], region[i], channel[i]) = (time, RegionNames[r], ChannelNames[k]);
            y[i] = (float)(((r * 6) + (k * 2) + (3 * Math.Sin(time / 5.0))) * (r == Regions - 1 ? 1000 : 1));
        }

        _table = DataTable
            .Create()
            .Add("t", Own(Column.Wrap(t)))
            .Add("y", Own(Column.Wrap(y)))
            .Add("region", Column.Categorical(region, RegionNames))
            .Add("channel", Column.Categorical(channel, ChannelNames))
            .Build();
    }

    public void Dispose()
    {
        foreach (var owned in _owned)
        {
            owned.Dispose();
        }

        _pool.Dispose();
    }

    /// <summary>
    /// A wrap's panels follow §4.6's formulas (design §4.6 steps 1 to 6): Hp from the height less the title's band, the strips,
    /// the shared bottom band once, the x title's band, and the gaps; the rows' tops; Wp from the width less the y title's
    /// band, the shared left band once, the legend's band, and the gaps; the columns' lefts; ⌊⌋'s pixels left at the right and
    /// the bottom.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The table; a split line under a wrap of 5 regions (2 × 3), with a title, axis titles, and the channels' legend; the
    ///   scales 1 and 1.5.
    ///
    /// Steps:
    /// - Render; compute the formulas from the theme, the fake metrics, the y labels, and the legend's place.
    ///
    /// Expected:
    /// - Each panel's left, top, width, and height as computed.
    /// </remarks>
    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public void Layout_FollowsTheFormulas(float scale)
    {
        var theme = Theme.Light;
        int R(float v) => (int)Math.Floor((v * scale) + 0.5);
        int Up(double v) => (int)Math.Ceiling(v);
        double LineOf(float size) => FakeTextBackend.LineHeight(size * scale);
        var (p, g, t) = (R(theme.Padding), R(theme.Spacing), R(theme.TickLength));
        var titleBand = Up(LineOf(theme.TitleSize)) + g;
        var tickBand = t + g + Up(LineOf(theme.TickLabelSize));
        var axisTitle = g + Up(LineOf(theme.AxisTitleSize));
        var strip = Up(LineOf(theme.AxisTitleSize)) + g;

        using var plan = Plan(scale);
        var list = plan.Render(Wrap(FacetScales.Fixed));
        var guides = plan.Guides;
        var height = (int)Math.Floor((Height - (2 * p) - titleBand - axisTitle - (2 * strip) - tickBand - g) / 2.0);
        int[] tops = [p + titleBand + strip, p + titleBand + strip + height + g + strip];
        var widest = guides.PanelAt(0).Y.TickLabels.ToArray().Max(l => l.Length * FakeTextBackend.Advance(theme.TickLabelSize * scale));
        var leftBand = t + g + Up(widest);
        var legend = list.Texts.ToArray().Single(x => x.Text == "Online").Position.X - R(theme.LegendSwatch) - g;
        var legendBand = Width - p + g - (int)legend;
        var width = (int)Math.Floor((Width - (2 * p) - axisTitle - legendBand - leftBand - (2 * g)) / 3.0);
        for (var i = 0; i < Regions; i++)
        {
            var panel = guides.PanelAt(i);
            var left = p + axisTitle + leftBand + ((i % 3) * (width + g));
            Assert.Equal((left, tops[i / 3], width, height), (panel.Left, panel.Top, panel.Width, panel.Height));
        }
    }

    /// <summary>
    /// A free direction's tick bands lie a row or a column each (design §4.6 steps 3 and 5): FreeX puts a bottom band under each
    /// row; FreeY puts a left band before each column, the column's widest labels. A free axis's ends beside another panel keep
    /// G / 2 of room; those at the figure's edge, the room to it (row G3b).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The table, whose last region's values are a thousand times the others'; the split line under a wrap, FreeX, then
    ///   under a grid of the regions as columns, FreeY.
    ///
    /// Steps:
    /// - Render each.
    ///
    /// Expected:
    /// - FreeX: the second row's top past the first's bottom by a tick band, a gap, and a strip, and each panel's x room G / 2
    ///   beside another panel; FreeY: each column's left past the one before by that column's band of its widest labels, the
    ///   last region's the widest.
    /// </remarks>
    [Fact]
    public void Layout_FreeBandsFollowRowsAndColumns()
    {
        var theme = Theme.Light;
        var (g, t) = ((int)Math.Floor(theme.Spacing + 0.5), (int)Math.Floor(theme.TickLength + 0.5));
        var tickBand = t + g + (int)Math.Ceiling(FakeTextBackend.LineHeight(theme.TickLabelSize));
        var strip = (int)Math.Ceiling(FakeTextBackend.LineHeight(theme.AxisTitleSize)) + g;

        using var freeX = Plan(1);
        _ = freeX.Render(Wrap(FacetScales.FreeX));
        var guides = freeX.Guides;
        var first = guides.PanelAt(0);
        Assert.Equal(first.Top + first.Height + tickBand + g + strip, guides.PanelAt(3).Top);
        Assert.Equal(g / 2.0, guides.PanelAt(1).X.Before);
        Assert.Equal(g / 2.0, guides.PanelAt(1).X.After);
        Assert.Equal(first.Left - (int)Math.Floor(theme.Padding + 0.5), guides.PanelAt(0).X.Before);
        Assert.Equal(g / 2.0, guides.PanelAt(0).X.After);

        using var freeY = Plan(1);
        _ = freeY.Render(Line() with { Facet = Facet.Grid(columns: _table.Cat("region"), scales: FacetScales.FreeY) });
        var columns = freeY.Guides;
        var bands = new int[Regions];
        for (var c = 0; c < Regions; c++)
        {
            var widest = columns.PanelAt(c).Y.TickLabels.ToArray().Max(l => l.Length * FakeTextBackend.Advance(theme.TickLabelSize));
            bands[c] = t + g + (int)Math.Ceiling(widest);
        }

        for (var c = 1; c < Regions; c++)
        {
            Assert.Equal(columns.PanelAt(c - 1).Left + columns.PanelAt(c - 1).Width + g + bands[c], columns.PanelAt(c).Left);
        }

        Assert.Equal(bands.Max(), bands[^1]);
        Assert.True(bands[^1] > bands[0]);
    }

    /// <summary>
    /// A color bar's length comes from the grid's height, r · Hp + (r − 1) · G, held to its range (design §4.6), not from one
    /// panel's.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 2D bins of t and y under a wrap of 5 regions (2 rows) at 640 × 250.
    ///
    /// Steps:
    /// - Render.
    ///
    /// Expected:
    /// - The color bar's length 2 · Hp + G, between the theme's half length and its length, and longer than one panel's.
    /// </remarks>
    [Fact]
    public void Layout_ColorBarTakesTheGridsHeight()
    {
        using var plan = new PlotPlan(_pool, Width, 250, new TextEngine(new FakeTextBackend()), 1);
        _ = plan.Render(Plot.Create(_table).Bin2D(_table.Col<double>("t"), _table.Col<float>("y")).FacetWrap(_table.Cat("region")).Build());
        var guides = plan.Guides;
        var g = (int)Math.Floor(Theme.Light.Spacing + 0.5);
        var expected = (2 * guides.PanelAt(0).Height) + g;
        Assert.InRange(expected, 100, 200);
        Assert.Equal(expected, guides.ColorBarLength);
    }

    /// <summary>
    /// An axis on the top and one on the right take their bands on the grid's sides as well (design §4.6): the top band once
    /// above the first row's strips, the axis past the strip; the right band after the last column; and a too small target
    /// draws only the background.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The split line with an axis of x / 60 on the top and a secondary y on the right, under a wrap; then the plain wrap at
    ///   60 × 60.
    ///
    /// Steps:
    /// - Render; read the top axis's labels.
    ///
    /// Expected:
    /// - The first row's top past the padding, the title's band, the top title's band, the tick band, and a strip; the top
    ///   labels' bottoms at the panel's top less the strip, T, and G; the right axis drawn where no panel lies on the right;
    ///   the small target's list empty.
    /// </remarks>
    [Fact]
    public void Layout_TopAndRightAxesTakeTheGridsSides()
    {
        var theme = Theme.Light;
        var (p, g, t) = ((int)Math.Floor(theme.Padding + 0.5), (int)Math.Floor(theme.Spacing + 0.5), (int)Math.Floor(theme.TickLength + 0.5));
        int Up(double v) => (int)Math.Ceiling(v);
        var titleBand = Up(FakeTextBackend.LineHeight(theme.TitleSize)) + g;
        var tickBand = t + g + Up(FakeTextBackend.LineHeight(theme.TickLabelSize));
        var axisTitle = g + Up(FakeTextBackend.LineHeight(theme.AxisTitleSize));
        var strip = Up(FakeTextBackend.LineHeight(theme.AxisTitleSize)) + g;
        var spec = Plot.Create(_table)
            .Line(_table.Col<double>("t"), _table.Col<float>("y"), l => l.ColorBy(_table.Cat("channel")))
            .Line(_table.Col<double>("t"), _table.Col<float>("y"), l => l.SecondaryY())
            .ScaleX(Scale.Linear().WithTitle("Week").WithSecondaryAxis(new Times(1.0 / 60), "Hour"))
            .ScaleY2(Scale.Linear().WithTitle("Other"))
            .FacetWrap(_table.Cat("region"))
            .Title("Title")
            .Build();
        using var plan = Plan(1);
        var list = plan.Render(spec);
        var guides = plan.Guides;
        Assert.Equal(p + titleBand + axisTitle + tickBand + strip, guides.PanelAt(0).Top);
        var top = guides.PanelAt(0).Top - strip - t - g;
        var labels = list.Texts.ToArray().Where(x => x.Style.Vertical == TextBaseline.Bottom && x.Style.Size == theme.TickLabelSize).ToArray();
        Assert.NotEmpty(labels);
        Assert.All(labels, x => Assert.Equal(top, x.Position.Y, 1e-3));
        Assert.Equal([false, false, true, false, true], Enumerable.Range(0, Regions).Select(i => guides.PanelAt(i).Draws[Right]));

        using var small = new PlotPlan(_pool, 60, 60, new TextEngine(new FakeTextBackend()), 1);
        Assert.Equal(0, small.Render(Wrap(FacetScales.Fixed)).Commands.Length);
    }

    /// <summary>
    /// A shared direction's axis is drawn only where no panel lies beside it, a free one's on every panel (design §4.6): a
    /// wrap's x under the last row and under the panel above the last row's missing one, its y on the left column; FreeX's x
    /// under every panel; the panel above the missing one records its labels under itself.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The split line under a wrap of 5 regions (2 × 3), Fixed, then FreeX.
    ///
    /// Steps:
    /// - Render each; read each panel's drawn sides and the texts under the third panel.
    ///
    /// Expected:
    /// - Fixed: bottoms F, F, T, T, T and lefts T, F, F, T, F; FreeX: every bottom; the third panel's x labels at its bottom +
    ///   T + G, as many as its labels.
    /// </remarks>
    [Fact]
    public void Record_SharedAxesOnTheOuterPanels()
    {
        var theme = Theme.Light;
        var (g, t) = ((int)Math.Floor(theme.Spacing + 0.5), (int)Math.Floor(theme.TickLength + 0.5));
        using var plan = Plan(1);
        var list = plan.Render(Wrap(FacetScales.Fixed));
        var guides = plan.Guides;
        Assert.Equal([false, false, true, true, true], Enumerable.Range(0, Regions).Select(i => guides.PanelAt(i).Draws[Bottom]));
        Assert.Equal([true, false, false, true, false], Enumerable.Range(0, Regions).Select(i => guides.PanelAt(i).Draws[Left]));
        var third = guides.PanelAt(2);
        var under = list.Texts.ToArray().Count(x => x.Position.Y == third.Top + third.Height + t + g && x.Position.X >= third.Left - 20);
        Assert.Equal(third.X.TickLabels.ToArray().Count(l => l.Length > 0), under);

        using var free = Plan(1);
        _ = free.Render(Wrap(FacetScales.FreeX));
        Assert.All(Enumerable.Range(0, Regions), i => Assert.True(free.Guides.PanelAt(i).Draws[Bottom]));
        Assert.Equal([true, false, false, true, false], Enumerable.Range(0, Regions).Select(i => free.Guides.PanelAt(i).Draws[Left]));
    }

    /// <summary>
    /// The strips and the figure's guides (design §4.6): a wrap's category centered above each panel, its bottom G above it; a
    /// grid's column categories above the first row and its row categories turned clockwise right of the last column, centered
    /// on the row; the legend and the axis titles once, the titles centered on the grid; a background a panel.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The split line under a wrap of the regions, then under a grid of channels by regions.
    ///
    /// Steps:
    /// - Render each; read the texts and the rectangles.
    ///
    /// Expected:
    /// - The strips at their places and rotations; one "Online" (the legend's) and one "Week"; a panel background at each
    ///   panel's rectangle.
    /// </remarks>
    [Fact]
    public void Record_StripsAndTheFiguresGuides()
    {
        var theme = Theme.Light;
        var g = (int)Math.Floor(theme.Spacing + 0.5);
        var advance = FakeTextBackend.Advance(theme.AxisTitleSize);
        var strip = (int)Math.Ceiling(FakeTextBackend.LineHeight(theme.AxisTitleSize)) + g;
        using var wrap = Plan(1);
        var list = wrap.Render(Wrap(FacetScales.Fixed));
        var texts = list.Texts.ToArray();
        for (var i = 0; i < Regions; i++)
        {
            var panel = wrap.Guides.PanelAt(i);
            var text = texts.Single(x => x.Text == RegionNames[i]);
            Assert.Equal(panel.Left + (panel.Width / 2f) - (RegionNames[i].Length * advance / 2), text.Position.X, 1e-3);
            Assert.Equal(panel.Top - g, text.Position.Y, 1e-3);
            Assert.Equal(TextBaseline.Bottom, text.Style.Vertical);
            Assert.Contains(list.Rects.ToArray(), r => (r.X, r.Y, r.Width, r.Height) == (panel.Left, panel.Top, panel.Width, panel.Height));
        }

        Assert.Single(texts, x => x.Text == "Online");
        var week = texts.Single(x => x.Text == "Week");
        var (left, right) = (wrap.Guides.PanelAt(0).Left, wrap.Guides.PanelAt(2).Left + wrap.Guides.PanelAt(2).Width);
        Assert.Equal(left + ((right - left) / 2f) - (4 * advance / 2), week.Position.X, 1e-3);

        using var grid = Plan(1);
        var gridTexts = grid.Render(Line() with { Facet = Facet.Grid(rows: _table.Cat("channel"), columns: _table.Cat("region")) }).Texts.ToArray();
        for (var c = 0; c < Regions; c++)
        {
            var panel = grid.Guides.PanelAt(c);
            var text = gridTexts.Single(x => x.Text == RegionNames[c]);
            Assert.Equal(panel.Top - g, text.Position.Y, 1e-3);
        }

        for (var r = 0; r < ChannelNames.Length; r++)
        {
            var panel = grid.Guides.PanelAt((r * Regions) + Regions - 1);
            var text = gridTexts.Single(x => x.Text == ChannelNames[r] && x.Style.Rotation == TextRotation.Clockwise90);
            Assert.Equal(panel.Left + panel.Width + strip, text.Position.X, 1e-3);
            Assert.Equal(panel.Top + (panel.Height / 2f) - (ChannelNames[r].Length * advance / 2), text.Position.Y, 1e-3);
        }
    }

    /// <summary>
    /// Facets go with a flip, a polar system, and a secondary scale (design §4.5, §4.6): flipped, a free x is the vertical one,
    /// drawn on every panel's left; polar, each panel's circle centered in it, the radius's axis on the left column; a shared
    /// secondary y on the right where no panel lies beside it, a free one on every panel.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The split line under a wrap of the regions: flipped with FreeX; polar; with a secondary y, Fixed and FreeY.
    ///
    /// Steps:
    /// - Render each; read the panels' drawn sides and circles.
    ///
    /// Expected:
    /// - Flipped: every left drawn, bottoms only on the outer panels; polar: each circle's center at its panel's center, the
    ///   radius's axis on panels 0 and 3; the secondary y: rights F, F, T, F, T, then all.
    /// </remarks>
    [Fact]
    public void FlipPolarAndSecondary_GoWithFacets()
    {
        using var flipped = Plan(1);
        _ = flipped.Render(Wrap(FacetScales.FreeX) with { Coord = Coord.Cartesian(flip: true) });
        Assert.All(Enumerable.Range(0, Regions), i => Assert.True(flipped.Guides.PanelAt(i).Draws[Left]));
        Assert.Equal([false, false, true, true, true], Enumerable.Range(0, Regions).Select(i => flipped.Guides.PanelAt(i).Draws[Bottom]));

        using var polar = Plan(1);
        _ = polar.Render(Wrap(FacetScales.Fixed) with { Coord = Coord.Polar() });
        for (var i = 0; i < Regions; i++)
        {
            var panel = polar.Guides.PanelAt(i);
            Assert.Equal(panel.Left + (panel.Width / 2.0), panel.Polar!.Value.CenterX);
            Assert.Equal(panel.Top + (panel.Height / 2.0), panel.Polar!.Value.CenterY);
        }

        Assert.Equal([true, false, false, true, false], Enumerable.Range(0, Regions).Select(i => polar.Guides.PanelAt(i).Draws[Left]));

        foreach (
            var (scales, rights) in new[]
            {
                (FacetScales.Fixed, new[] { false, false, true, false, true }),
                (FacetScales.FreeY, new[] { true, true, true, true, true }),
            }
        )
        {
            using var second = Plan(1);
            _ = second.Render(
                Plot.Create(_table)
                    .Line(_table.Col<double>("t"), _table.Col<float>("y"))
                    .Line(_table.Col<double>("t"), _table.Col<float>("y"), l => l.SecondaryY())
                    .ScaleY2(Scale.Linear())
                    .FacetWrap(_table.Cat("region"), scales: scales)
                    .Build()
            );
            Assert.Equal(rights, Enumerable.Range(0, Regions).Select(i => second.Guides.PanelAt(i).Draws[Right]));
        }
    }

    /// <summary>
    /// An image and a user's geom read no rows and are drawn in every panel (design §4.5, Issue #243): one raster's mipmap for
    /// all; a free direction's panels learn the geom's ranges each.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A raster of 4 × 2 under the line, and a geom that adds x [−50, 50], under a wrap of the regions, Free.
    ///
    /// Steps:
    /// - Render.
    ///
    /// Expected:
    /// - An image plan in every panel and one mipmap; a custom plan in every panel, each learned once, and each panel's x
    ///   domain from −50 to 50.
    /// </remarks>
    [Fact]
    public void Rowless_DrawInEveryPanel()
    {
        using var values = Column.Wrap(new float[8]);
        var spec = Plot.Create(_table)
            .Image(Raster.Of(values, 4))
            .Custom(new Wide())
            .Line(_table.Col<double>("t"), _table.Col<float>("y"))
            .FacetWrap(_table.Cat("region"), scales: FacetScales.Free)
            .Build();
        using var plan = Plan(1);
        _ = plan.Render(spec);
        Assert.Equal(1, plan.RasterCount);
        Assert.All(plan.Panels, p => Assert.IsType<ImagePlan>(p.Layers[0]));
        Assert.All(plan.Panels, p => Assert.Equal(1, Assert.IsType<CustomPlan>(p.Layers[1]).Learned));
        Assert.All(plan.Panels, p => Assert.Equal((-50.0, 50.0), (p.LearnedX.Origin + p.LearnedX.Lower, p.LearnedX.Origin + p.LearnedX.Upper)));
    }

    private PlotSpec Line() =>
        Plot.Create(_table)
            .Line(_table.Col<double>("t"), _table.Col<float>("y"), l => l.ColorBy(_table.Cat("channel")))
            .ScaleColor(Scale.Ordinal().WithTitle("Channel"))
            .ScaleX(Scale.Linear().WithTitle("Week"))
            .ScaleY(Scale.Linear().WithTitle("Sales"))
            .Title("Title")
            .Build();

    private PlotSpec Wrap(FacetScales scales) => Line() with { Facet = Facet.Wrap(_table.Cat("region"), scales: scales) };

    private T Own<T>(T column)
        where T : IDisposable
    {
        _owned.Add(column);
        return column;
    }

    private PlotPlan Plan(float scale) => new(_pool, Width, Height, new TextEngine(new FakeTextBackend()), scale);

    /// <summary>A geom that adds x [−50, 50] and draws nothing.</summary>
    private sealed class Wide : IGeom
    {
        public void Learn(GeomRanges ranges) => ranges.AddX(-50, 50);

        public void Record(GeomFrame frame, RenderList list) { }
    }

    /// <summary>A transform that multiplies by a factor.</summary>
    private sealed class Times(double factor) : IScale
    {
        public void Forward(ReadOnlySpan<double> values, Span<double> destination)
        {
            for (var i = 0; i < values.Length; i++)
            {
                destination[i] = values[i] * factor;
            }
        }

        public void Inverse(ReadOnlySpan<double> values, Span<double> destination)
        {
            for (var i = 0; i < values.Length; i++)
            {
                destination[i] = values[i] / factor;
            }
        }
    }
}
