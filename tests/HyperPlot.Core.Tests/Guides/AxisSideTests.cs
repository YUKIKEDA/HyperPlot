using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>
/// The axes of row G6a (design §4.1, §4.2, §4.6, §4.8, §10's G6a row): the sides of the axes, the secondary scales, and the
/// axes of a transform, with the fake text backend: one glyph a character of 0.5625 em, lines of 1.25 em.
/// </summary>
public sealed class AxisSideTests : IDisposable
{
    private const int Width = 640;
    private const int Height = 360;
    private const int Length = 500;
    private const int Narrow = 100;
    private readonly PlotWorkerPool _pool = new(1);
    private readonly Column<float> _a;
    private readonly Column<float> _b;
    private readonly Column<double> _c;
    private readonly Column<double> _rule;
    private readonly DataTable _table;

    public AxisSideTests()
    {
        _a = Column.Wrap(Enumerable.Range(0, Length).Select(i => (float)Math.Sin(i / 40.0) * 3).ToArray());
        _b = Column.Wrap(Enumerable.Range(0, Length).Select(i => (float)Math.Cos(i / 30.0)).ToArray());
        _c = Column.Wrap(Enumerable.Range(0, Length).Select(i => 1 + (i / (Length - 1.0))).ToArray());
        _rule = Column.Wrap(Enumerable.Repeat(10.0, Length).ToArray());
        _table = DataTable
            .Create()
            .Add("t", Column.Uniform(0, 1, Length))
            .Add("u", Column.Uniform(0, 2, Length))
            .Add("a", _a)
            .Add("b", _b)
            .Add("c", _c)
            .Add("ten", _rule)
            .Add("k", Column.Categorical([.. Enumerable.Range(0, Length).Select(i => (string?)(i % 2 == 0 ? "p" : "q"))], ["p", "q"]))
            .Build();
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
        _c.Dispose();
        _rule.Dispose();
        _pool.Dispose();
    }

    /// <summary>The factories and the builders check their arguments and put the layers on the scales they name (design §4.1).</summary>
    /// <remarks>
    /// Preconditions:
    /// - The fixture's table.
    ///
    /// Steps:
    /// - Give each factory a side out of range, a time scale a secondary axis, and null scales; build a line, an area, a bar,
    ///   a text, and an image layer with SecondaryX and SecondaryY.
    ///
    /// Expected:
    /// - ArgumentOutOfRangeException, ArgumentException, ArgumentNullException; each built layer on both secondary scales, a
    ///   layer without the calls on none, and the builder's secondary scales in the spec.
    /// </remarks>
    [Fact]
    public void Builders_CheckTheirArguments()
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Scale.Linear().WithAxisSide((AxisSide)4));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Scale.Band().WithAxisSide((AxisSide)(-1)));
        _ = Assert.Throws<ArgumentException>(() => Scale.Time().WithSecondaryAxis());
        _ = Assert.Throws<ArgumentNullException>(() => ((LinearScaleSpec)null!).WithAxisSide(AxisSide.Top));
        _ = Assert.Throws<ArgumentNullException>(() => ((LinearScaleSpec)null!).WithSecondaryAxis());
        _ = Assert.Throws<ArgumentNullException>(() => Plot.Create(_table).ScaleX2(null!));
        _ = Assert.Throws<ArgumentNullException>(() => Plot.Create(_table).ScaleY2(null!));
        Assert.Equal(AxisSide.Top, Scale.Linear().WithAxisSide(AxisSide.Top).Side);
        Assert.Equal(AxisSide.Top, Scale.Band().WithAxisSide(AxisSide.Top).Side);
        var axis = Scale.Linear().WithSecondaryAxis(new Times(2), "two").SecondaryAxis!;
        Assert.Equal("two", axis.Title);
        Assert.IsType<Times>(axis.Transform);

        using var raster = Column.Wrap(new float[4]);
        var spec = Plot.Create(_table)
            .Line(_table.Col<double>("t"), _table.Col<float>("a"), l => l.SecondaryX().SecondaryY())
            .Area(_table.Col<double>("t"), _table.Col<float>("a"), configure: l => l.SecondaryX().SecondaryY())
            .Bar(_table.Col<double>("t"), _table.Col<float>("a"), configure: l => l.SecondaryX().SecondaryY())
            .Text(_table.Col<double>("t"), _table.Col<float>("a"), _table.Col<float>("a"), l => l.SecondaryX().SecondaryY())
            .Image(Raster.Of(raster, 2), l => l.SecondaryX().SecondaryY())
            .Line(_table.Col<double>("t"), _table.Col<float>("b"))
            .ScaleX2(Scale.Linear())
            .ScaleY2(Scale.Log())
            .Build();
        Assert.All(spec.Layers.Take(5), l => Assert.True(l.SecondaryX && l.SecondaryY));
        Assert.False(spec.Layers[5].SecondaryX || spec.Layers[5].SecondaryY);
        Assert.IsType<LinearScaleSpec>(spec.ScaleX2);
        Assert.IsType<LogScaleSpec>(spec.ScaleY2);
    }

    /// <summary>
    /// The compile refuses axes that do not go together (design §4.1, §4.8): a layer on a missing secondary scale, a secondary
    /// scale with no layer, two second axes in a direction, sides of the other direction, a secondary scale on its primary's
    /// side, a transform's axis on a secondary scale or a color scale's inner scale, a side on a color scale, axes in polar,
    /// categories on a secondary x, a transform that does not map its ends back, and a primary no layer lies on.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The fixture's table; specs each breaking one rule.
    ///
    /// Steps:
    /// - Render each.
    ///
    /// Expected:
    /// - ArgumentException each; the all-secondary spec with a primary domain given is drawn.
    /// </remarks>
    [Fact]
    public void Compile_RefusesAxesThatDoNotGoTogether()
    {
        var on = new LineLayerSpec
        {
            X = _table.Col<double>("t"),
            Y = _table.Col<float>("b"),
            SecondaryY = true,
        };
        var off = new LineLayerSpec { X = _table.Col<double>("t"), Y = _table.Col<float>("a") };
        PlotSpec[] refused =
        [
            Spec(off, on),
            Spec(off) with
            {
                ScaleY2 = Scale.Linear(),
            },
            Spec(off, on) with
            {
                ScaleY = Scale.Linear().WithSecondaryAxis(),
                ScaleY2 = Scale.Linear(),
            },
            Spec(off, on) with
            {
                ScaleY2 = Scale.Linear().WithAxisSide(AxisSide.Left),
            },
            Spec(off, on) with
            {
                ScaleY = Scale.Linear().WithAxisSide(AxisSide.Right),
                ScaleY2 = Scale.Linear().WithAxisSide(AxisSide.Right),
            },
            Spec(off) with
            {
                ScaleX = Scale.Linear() with { Side = AxisSide.Left },
            },
            Spec(off) with
            {
                ScaleY = Scale.Linear() with { Side = AxisSide.Top },
            },
            Spec(off) with
            {
                ScaleX = Scale.Band() with { Side = AxisSide.Right },
            },
            Spec(off, on) with
            {
                ScaleY2 = Scale.Linear().WithSecondaryAxis(),
            },
            Spec(off) with
            {
                Coord = Coord.Polar(),
                ScaleY = Scale.Linear().WithAxisSide(AxisSide.Right),
            },
            Spec(off, on) with
            {
                Coord = Coord.Polar(),
                ScaleY2 = Scale.Linear(),
            },
            Spec(off) with
            {
                Coord = Coord.Polar(),
                ScaleY = Scale.Linear().WithSecondaryAxis(),
            },
            Spec(on) with
            {
                ScaleY2 = Scale.Linear(),
            },
            Spec(off) with
            {
                ScaleY = Scale.Linear().WithDomain(-2, 3).WithSecondaryAxis(new Square()),
            },
            Spec(
                new BarLayerSpec
                {
                    X = _table.Cat("k").ToColumnRef(),
                    Y = _table.Col<float>("a"),
                    SecondaryX = true,
                }
            ) with
            {
                ScaleX = Scale.Band(),
                ScaleX2 = Scale.Linear(),
            },
            Spec(
                new RectLayerSpec
                {
                    XMin = _table.Col<double>("t"),
                    XMax = _table.Col<double>("t"),
                    YMin = _table.Col<float>("a"),
                    YMax = _table.Col<float>("b"),
                    FillBy = _table.Col<float>("a"),
                }
            ) with
            {
                ScaleColor = Scale.Sequential(scale: Scale.Linear().WithAxisSide(AxisSide.Right)),
            },
            Spec(
                new RectLayerSpec
                {
                    XMin = _table.Col<double>("t"),
                    XMax = _table.Col<double>("t"),
                    YMin = _table.Col<float>("a"),
                    YMax = _table.Col<float>("b"),
                    FillBy = _table.Col<float>("a"),
                }
            ) with
            {
                ScaleColor = Scale.Sequential(scale: Scale.Linear().WithSecondaryAxis()),
            },
        ];
        foreach (var spec in refused)
        {
            using var plan = Plan(1);
            _ = Assert.Throws<ArgumentException>(() => plan.Render(spec));
        }

        using var given = Plan(1);
        _ = given.Render(Spec(on) with { ScaleY = Scale.Linear().WithDomain(0, 1), ScaleY2 = Scale.Linear() });
        Assert.True(given.Guides.Y2.Count > 0);
    }

    /// <summary>
    /// A layer on a secondary scale is mapped by it (design §4.1): its y by the secondary y's domain and transform, its x and
    /// M4's columns by the secondary x's.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A bare theme (the panel is the target); y's domains [−3, 3] and [−6, 6], then rules at 10 on a linear y [0, 100] and
    ///   a log secondary y [1, 1000]; x's domains [0, 499] and [0, 998] for the same column on both, on a target 100 wide
    ///   (fewer columns than the 500 points, so that M4 reduces them).
    ///
    /// Steps:
    /// - Render the same line on the primary and the secondary y; the rules; the same line on the primary and the secondary x.
    ///
    /// Expected:
    /// - The secondary line's distances from the middle are half the primary's; the primary rule at 0.9 of the height from
    ///   the top, the secondary at 2/3; the secondary x line ends at half the width, the primary at the width, with about
    ///   half as many points.
    /// </remarks>
    [Fact]
    public void SecondaryScales_MapTheirLayers()
    {
        var bare = Theme.Light with { ShowAxes = false, ShowGrid = false, Padding = 0 };
        using (var plan = Plan(1))
        {
            var list = plan.Render(
                new PlotSpec
                {
                    Data = _table,
                    Layers =
                    [
                        new LineLayerSpec { X = _table.Col<double>("t"), Y = _table.Col<float>("a") },
                        new LineLayerSpec
                        {
                            X = _table.Col<double>("t"),
                            Y = _table.Col<float>("a"),
                            SecondaryY = true,
                        },
                    ],
                    ScaleY = Scale.Linear().WithDomain(-3, 3),
                    ScaleY2 = Scale.Linear().WithDomain(-6, 6),
                    Theme = bare,
                }
            );
            var (primary, secondary) = (GeomTesting.SegmentsOf(list, 0), GeomTesting.SegmentsOf(list, 1));
            Assert.Equal(primary.Length, secondary.Length);
            for (var i = 0; i < primary.Length; i++)
            {
                Assert.Equal(primary[i].Start.X, secondary[i].Start.X);
                Assert.Equal((primary[i].Start.Y - (Height / 2f)) / 2, secondary[i].Start.Y - (Height / 2f), 1e-3);
            }
        }

        using (var plan = Plan(1))
        {
            var list = plan.Render(
                new PlotSpec
                {
                    Data = _table,
                    Layers =
                    [
                        new RuleLayerSpec { Y = _table.Col<double>("ten") },
                        new RuleLayerSpec { Y = _table.Col<double>("ten"), SecondaryY = true },
                    ],
                    ScaleY = Scale.Linear().WithDomain(0, 100),
                    ScaleY2 = Scale.Log().WithDomain(1, 1000),
                    Theme = bare,
                }
            );
            Assert.Equal(Height * 0.9, GeomTesting.SegmentsOf(list, 0)[0].Start.Y, 1e-3);
            Assert.Equal(Height * 2.0 / 3, GeomTesting.SegmentsOf(list, 1)[0].Start.Y, 1e-3);
        }

        using (var plan = new PlotPlan(_pool, Narrow, Height, new TextEngine(new FakeTextBackend()), 1))
        {
            var list = plan.Render(
                new PlotSpec
                {
                    Data = _table,
                    Layers =
                    [
                        new LineLayerSpec { X = _table.Col<double>("t"), Y = _table.Col<float>("a") },
                        new LineLayerSpec
                        {
                            X = _table.Col<double>("t"),
                            Y = _table.Col<float>("a"),
                            SecondaryX = true,
                        },
                    ],
                    ScaleX = Scale.Linear().WithDomain(0, Length - 1),
                    ScaleX2 = Scale.Linear().WithDomain(0, (Length - 1) * 2),
                    Theme = bare,
                }
            );
            var (primary, secondary) = (GeomTesting.SegmentsOf(list, 0), GeomTesting.SegmentsOf(list, 1));
            Assert.Equal(Narrow, primary.Max(s => Math.Max(s.Start.X, s.End.X)), 1e-3);
            Assert.Equal(Narrow / 2f, secondary.Max(s => Math.Max(s.Start.X, s.End.X)), 1e-3);

            // M4 ran over the secondary x's own 100 columns, of which the points fill half: about half as many points.
            Assert.InRange(secondary.Length, (primary.Length / 2) - 4, (primary.Length / 2) + 4);
        }
    }

    /// <summary>
    /// A secondary scale learns from the layers on it only, the primary from the others (design §4.2): bars' baseline on the
    /// secondary, and the x of a layer on the secondary y only on the primary x.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A line of a in [−3, 3] over t (0 to 499) on the primary; bars of c in [1, 2] over u (0 to 998) on the secondary y.
    ///
    /// Steps:
    /// - Render with learned domains.
    ///
    /// Expected:
    /// - The primary y a's range; the secondary y [0, 2] (the bars' baseline); the primary x [0, 998] with the bars' half
    ///   width, which takes u.
    /// </remarks>
    [Fact]
    public void SecondaryScales_LearnFromTheirLayers()
    {
        using var plan = Plan(1);
        _ = plan.Render(
            new PlotSpec
            {
                Data = _table,
                Layers =
                [
                    new LineLayerSpec { X = _table.Col<double>("t"), Y = _table.Col<float>("a") },
                    new BarLayerSpec
                    {
                        X = _table.Col<double>("u"),
                        Y = _table.Col<double>("c"),
                        SecondaryY = true,
                    },
                ],
                ScaleY2 = Scale.Linear(),
                Theme = Theme.Light with { ShowAxes = false, ShowGrid = false },
            }
        );
        var a = _a.GetChunk(0).ToArray();
        var (low, high) = GeomTesting.Ends(plan.YDomain);
        Assert.Equal((a.Min(), a.Max()), ((float)low, (float)high));
        var y2 = plan.Guides.Y2.FinalDomain;
        Assert.Equal((0.0, 2.0), (y2.Origin + y2.Lower, y2.Origin + y2.Upper));
        Assert.True(GeomTesting.Ends(plan.XDomain).Max >= 998);
    }

    /// <summary>
    /// The axis of a transform shows the transformed values (design §4.1): ticks the extended algorithm chooses over the
    /// transformed ends, each where its value maps back, not widened by nice; a decreasing transform's ticks run the other
    /// way; a copy shows the primary's ticks, a log one's too; the grid is the primary's only.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - x [0, 600] with an axis of x / 60 on the top, then of −x / 60; y log [1, 1000] with a copy on the right; nice on y.
    ///
    /// Steps:
    /// - Render; read the second axes' ticks and the grid.
    ///
    /// Expected:
    /// - x2's tick values v at the pixels of 60 v (or −60 v); y2's labels and pixels those of y; the first polylines the
    ///   primary's grid lines, as many as x's and y's ticks, then the data.
    /// </remarks>
    [Fact]
    public void TransformAxis_ShowsTheTransformedValues()
    {
        foreach (var factor in (ReadOnlySpan<double>)[60, -60])
        {
            using var plan = Plan(1);
            var list = plan.Render(
                Spec(new LineLayerSpec { X = _table.Col<double>("t"), Y = _table.Col<double>("c") }) with
                {
                    ScaleX = Scale.Linear().WithDomain(0, 600).WithSecondaryAxis(new Times(1 / factor), "minutes"),
                    ScaleY = Scale.Log().WithDomain(1, 1000) with { Nice = true },
                }
            );
            var (x, x2) = (plan.Guides.X, plan.Guides.X2);
            Assert.True(x2.Count >= 3);
            Assert.Equal(x.Length, x2.Length);
            for (var i = 0; i < x2.Count; i++)
            {
                var value = double.Parse(x2.TickLabels[i].Replace('−', '-'), System.Globalization.CultureInfo.InvariantCulture);
                var pixel = value * factor / 600 * x2.Length;
                Assert.Equal(pixel, factor > 0 ? x2.Pixels[i] : x2.Length - x2.Pixels[i], 1e-6);
            }

            var grid = plan.Guides.X.Count + plan.Guides.Y.Count;
            Assert.All(list.Polylines[..grid].ToArray(), p => Assert.Equal(1, p.SegmentCount));
            Assert.True(list.Polylines[grid].SegmentCount > 1);
        }

        using var copy = Plan(1);
        _ = copy.Render(
            Spec(new LineLayerSpec { X = _table.Col<double>("t"), Y = _table.Col<double>("c") }) with
            {
                ScaleY = Scale.Log().WithDomain(1, 1000).WithSecondaryAxis(),
            }
        );
        Assert.Equal(copy.Guides.Y.TickLabels.ToArray(), copy.Guides.Y2.TickLabels.ToArray());
        Assert.Equal(copy.Guides.Y.Pixels.ToArray(), copy.Guides.Y2.Pixels.ToArray());
    }

    /// <summary>
    /// The bands follow the sides (design §4.6): x on the top moves its band above the panel, y on the right moves its band to
    /// the right of the panel and the legend past it, both second axes take both bands, and no axes take none.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The full spec (title, two labels, axis titles "X" and "Y") at the scales 1 and 1.5; x on the top; y on the right; a
    ///   secondary y titled "Y2" and an axis of x on the top; and the same with ShowAxes false.
    ///
    /// Steps:
    /// - Render each.
    ///
    /// Expected:
    /// - x on the top: the panel's top lower by the x band, the same height; y on the right: the panel's left at the padding,
    ///   the same width, the legend's labels past the right band; both: the height less the top band and the width less the
    ///   right band; without axes, the default panel.
    /// </remarks>
    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public void Layout_BandsFollowTheSides(float scale)
    {
        var theme = Theme.Light;
        int R(float v) => (int)Math.Floor((v * scale) + 0.5);
        int Up(double v) => (int)Math.Ceiling(v);
        double Line(float size) => FakeTextBackend.LineHeight(size * scale);
        var (p, g, t) = (R(theme.Padding), R(theme.Spacing), R(theme.TickLength));
        var xBand = t + g + Up(Line(theme.TickLabelSize)) + g + Up(Line(theme.AxisTitleSize));

        using var basic = Plan(scale);
        _ = basic.Render(Full());
        var d = basic.Guides;
        var yBand = d.Left - p;

        using var top = Plan(scale);
        _ = top.Render(Full() with { ScaleX = Scale.Linear().WithTitle("X").WithAxisSide(AxisSide.Top) });
        Assert.Equal((d.Top + xBand, d.Height, d.Left, d.Width), (top.Guides.Top, top.Guides.Height, top.Guides.Left, top.Guides.Width));

        using var right = Plan(scale);
        var list = right.Render(Full() with { ScaleY = Scale.Linear().WithTitle("Y").WithAxisSide(AxisSide.Right) });
        Assert.Equal((d.Top, d.Height, p, d.Width), (right.Guides.Top, right.Guides.Height, right.Guides.Left, right.Guides.Width));
        var legend = list.Texts.ToArray().Single(x => x.Text == "ch1");
        Assert.True(legend.Position.X > right.Guides.Left + right.Guides.Width + yBand);

        using var both = Plan(scale);
        _ = both.Render(Both());
        Assert.Equal(d.Top + t + g + Up(Line(theme.TickLabelSize)) + g + Up(Line(theme.AxisTitleSize)), both.Guides.Top);
        Assert.Equal(d.Height - (both.Guides.Top - d.Top), both.Guides.Height);
        Assert.True(both.Guides.Width < d.Width);

        using var none = Plan(scale);
        _ = none.Render(Both() with { Theme = theme with { ShowAxes = false } });
        using var noneBasic = Plan(scale);
        _ = noneBasic.Render(Full() with { Theme = theme with { ShowAxes = false } });
        Assert.Equal(
            (noneBasic.Guides.Left, noneBasic.Guides.Top, noneBasic.Guides.Width, noneBasic.Guides.Height),
            (none.Guides.Left, none.Guides.Top, none.Guides.Width, none.Guides.Height)
        );
    }

    /// <summary>
    /// The top and right axes are the bottom's and the left's mirrors (design §4.6): the top labels' bottoms at the panel's top
    /// less T and G, the right labels' left edges at its right plus T and G, the top title under the plot's title, the right
    /// title turned clockwise against the legend; the labels held inside F; flipped, x's top is the right and y's right the top.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A secondary y "Y2" on the right and an axis of x "min" on the top, with the plot's title; then the same flipped.
    ///
    /// Steps:
    /// - Render; read the texts.
    ///
    /// Expected:
    /// - Each y2 label at x = left + width + T + G, aligned left and in the middle; each x2 label aligned at its bottom at y =
    ///   top − T − G, inside [P, width − P]; "Y2" turned clockwise at x = the target's width − P − the legend's band, its top
    ///   centered on the panel; "min" at y = P + the title's band. Flipped, y2's labels lie above the panel and x2's right of it.
    /// </remarks>
    [Fact]
    public void Record_TopAndRightMirrorTheBottomAndLeft()
    {
        var theme = Theme.Light;
        var (p, g, t) = (10, (int)Math.Floor(theme.Spacing + 0.5), (int)Math.Floor(theme.TickLength + 0.5));
        using var plan = Plan(1);
        var list = plan.Render(Both());
        var guides = plan.Guides;
        var texts = list.Texts.ToArray();
        foreach (var label in guides.Y2.TickLabels)
        {
            var text = texts.First(x => x.Text == label && x.Position.X > guides.Left + guides.Width);
            Assert.Equal(guides.Left + guides.Width + t + g, text.Position.X);
            Assert.Equal((TextAlign.Left, TextBaseline.Middle), (text.Style.Horizontal, text.Style.Vertical));
            Assert.InRange(text.Position.Y, p, Height - p);
        }

        foreach (var label in guides.X2.TickLabels)
        {
            var text = texts.First(x => x.Text == label && x.Position.Y < guides.Top);
            Assert.Equal(guides.Top - t - g, text.Position.Y);
            Assert.Equal(TextBaseline.Bottom, text.Style.Vertical);
            Assert.InRange(text.Position.X, p, Width - p - (label.Length * FakeTextBackend.Advance(theme.TickLabelSize)) + 1e-3);
        }

        var titleBand = (int)Math.Ceiling(FakeTextBackend.LineHeight(theme.TitleSize)) + g;
        var y2 = texts.Single(x => x.Text == "Y2");
        Assert.Equal(TextRotation.Clockwise90, y2.Style.Rotation);
        var legendLeft = texts.Single(x => x.Text == "ch1").Position.X - (int)Math.Floor(theme.LegendSwatch + 0.5) - g;
        Assert.Equal(legendLeft - g, y2.Position.X, 1e-3);
        Assert.Equal(guides.Top + (guides.Height / 2f) - (2 * FakeTextBackend.Advance(theme.AxisTitleSize) / 2), y2.Position.Y, 1e-3);
        Assert.Equal(p + titleBand, texts.Single(x => x.Text == "min").Position.Y);

        using var flipped = Plan(1);
        var flippedList = flipped.Render(Both() with { Coord = Coord.Cartesian(flip: true) });
        var f = flipped.Guides;
        var flippedTexts = flippedList.Texts.ToArray();
        Assert.All(f.Y2.TickLabels.ToArray(), label => Assert.Contains(flippedTexts, x => x.Text == label && x.Position.Y <= f.Top));
        Assert.All(f.X2.TickLabels.ToArray(), label => Assert.Contains(flippedTexts, x => x.Text == label && x.Position.X >= f.Left + f.Width));
    }

    /// <summary>
    /// The diff puts the axes' changes in their stages (design §4.8): a layer moved to the secondary y is the data stage; a
    /// secondary scale's domain the scale stage; a side the looks stage, which extracts again only when the panel's width
    /// changes; a new node of the same secondary axis nothing.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The two-axes spec.
    ///
    /// Steps:
    /// - Render it; then each change in turn; read the layer plans and the counters.
    ///
    /// Expected:
    /// - New layer plans for the move; one more extraction and layout for the domain; one more layout and no extraction for
    ///   y moved to the right (the width stays); no layout for the same axis anew.
    /// </remarks>
    [Fact]
    public void Diff_PutsTheChangesInTheirStages()
    {
        using var plan = Plan(1);
        var spec = Both();
        _ = plan.Render(spec);
        var layer = plan.Layers[1];

        // The third layer moves onto the secondary y: the layers are made again.
        _ = plan.Render(spec with { Layers = [spec.Layers[0], spec.Layers[1], spec.Layers[2] with { SecondaryY = true }] });
        Assert.NotSame(layer, plan.Layers[1]);

        _ = plan.Render(spec);
        var (layouts, extractions) = (plan.Layouts, plan.Extractions);
        _ = plan.Render(spec with { ScaleY2 = Scale.Linear().WithDomain(-2, 2).WithTitle("Y2") });
        Assert.Equal((layouts + 1, extractions + 1), (plan.Layouts, plan.Extractions));

        using var sides = Plan(1);
        var basic = Full() with { Theme = Theme.Light with { ShowGrid = false } };
        _ = sides.Render(basic);
        (layouts, extractions) = (sides.Layouts, sides.Extractions);
        _ = sides.Render(basic with { ScaleY = Scale.Linear().WithTitle("Y").WithAxisSide(AxisSide.Right) });
        Assert.Equal((layouts + 1, extractions), (sides.Layouts, sides.Extractions));

        var transform = new Times(1.0 / 60);
        using var same = Plan(1);
        _ = same.Render(basic with { ScaleX = Scale.Linear().WithSecondaryAxis(transform, "min") });
        layouts = same.Layouts;
        _ = same.Render(basic with { ScaleX = Scale.Linear().WithSecondaryAxis(transform, "min") });
        Assert.Equal(layouts, same.Layouts);
    }

    /// <summary>Once warm, the axes of row G6a drawn again over two domains in turn allocate nothing (design §4.8).</summary>
    /// <remarks>
    /// Preconditions:
    /// - The two-axes spec with y on the right and x on the top swapped in, at two y2 domains.
    ///
    /// Steps:
    /// - Render both a few times, then five times more in turn.
    ///
    /// Expected:
    /// - 0 bytes on this thread.
    /// </remarks>
    [Fact]
    public void Warm_AllocatesNothing()
    {
        using var plan = Plan(1);
        var transform = new Times(1.0 / 60);
        var one = Both() with { ScaleX = Scale.Linear().WithTitle("X").WithAxisSide(AxisSide.Top).WithSecondaryAxis(transform, "min") };
        var two = one with { ScaleY2 = Scale.Linear().WithDomain(-3, 3).WithTitle("Y2") };
        for (var i = 0; i < 3; i++)
        {
            _ = plan.Render(one);
            _ = plan.Render(two);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 5; i++)
        {
            _ = plan.Render(one);
            _ = plan.Render(two);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private PlotPlan Plan(float scale) => new(_pool, Width, Height, new TextEngine(new FakeTextBackend()), scale);

    private PlotSpec Spec(params LayerSpec[] layers) => new() { Data = _table, Layers = layers };

    private PlotSpec Full() =>
        Plot.Create(_table)
            .Line(_table.Col<double>("t"), _table.Col<float>("a"), l => l.Label("ch1"))
            .Line(_table.Col<double>("t"), _table.Col<float>("b"), l => l.Label("ch2"))
            .ScaleX(Scale.Linear().WithTitle("X"))
            .ScaleY(Scale.Linear().WithTitle("Y"))
            .Title("Title")
            .Build();

    /// <summary>The full spec with a secondary y "Y2" on the right (a layer of b on it) and an axis of x / 60 "min" on the top.</summary>
    private PlotSpec Both() =>
        Plot.Create(_table)
            .Line(_table.Col<double>("t"), _table.Col<float>("a"), l => l.Label("ch1"))
            .Line(_table.Col<double>("t"), _table.Col<float>("b"), l => l.Label("ch2").SecondaryY())
            .Line(_table.Col<double>("t"), _table.Col<double>("c"))
            .ScaleX(Scale.Linear().WithTitle("X").WithSecondaryAxis(new Times(1.0 / 60), "min"))
            .ScaleY(Scale.Linear().WithTitle("Y"))
            .ScaleY2(Scale.Linear().WithTitle("Y2"))
            .Title("Title")
            .Build();

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

    /// <summary>x², whose inverse is the root: not monotonic over a domain across 0.</summary>
    private sealed class Square : IScale
    {
        public void Forward(ReadOnlySpan<double> values, Span<double> destination)
        {
            for (var i = 0; i < values.Length; i++)
            {
                destination[i] = values[i] * values[i];
            }
        }

        public void Inverse(ReadOnlySpan<double> values, Span<double> destination)
        {
            for (var i = 0; i < values.Length; i++)
            {
                destination[i] = Math.Sqrt(values[i]);
            }
        }
    }
}
