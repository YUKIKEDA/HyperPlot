using HyperPlot.Rendering;
using static HyperPlot.Tests.GeomTesting;

namespace HyperPlot.Tests;

/// <summary>The color channels of row G7c: their checks, split layers, element colors, the legend, and the diff (design §10 G7c row).</summary>
public sealed class ColorChannelTests
{
    private static readonly string[] Names = ["a", "b", "c"];

    /// <summary>
    /// The compile refuses the color channels row G7c does not draw (design §4.1, §4.2, §4.7, §4.8), and the builder a null
    /// color scale.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A table of 300 rows with numbers, two categorical columns of the same categories, one of other categories, and one
    ///   of 257 categories; a second table.
    ///
    /// Steps:
    /// - Render each refused spec; call PlotBuilder.ScaleColor(null); render with a theme's ColorBarThickness and
    ///   ColorBarLength 0, negative, NaN, and ∞.
    ///
    /// Expected:
    /// - ArgumentException for each spec, ArgumentNullException for the builder, ArgumentException naming the member for the
    ///   theme (with no color channel in the spec).
    /// </remarks>
    [Fact]
    public void Checks_RefuseWhatG7cDoesNotDraw()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = Fixture.Create(100, mixed: true);
        var t = data.Table;
        var (x, y, y2, v) = (
            t.Col<double>("x").ToColumnRef(),
            t.Col<double>("y").ToColumnRef(),
            t.Col<double>("y2").ToColumnRef(),
            t.Col<double>("v").ToColumnRef()
        );
        var cat = t.Cat("code").ToColumnRef();
        var other = t.Cat("other").ToColumnRef();
        var many = t.Cat("many").ToColumnRef();
        using var short1 = Column.Wrap(new double[] { 1 });
        var otherTable = DataTable.Create().Add("v", Column.Wrap(new double[300])).Build();
        var shortTable = DataTable.Create().Add("x", short1).Add("c", Column.Categorical(["a"])).Build();
        PlotSpec[] refused =
        [
            Spec(
                t,
                new LineLayerSpec
                {
                    X = x,
                    Y = y,
                    ColorBy = otherTable.Col<double>("v"),
                }
            ),
            Spec(
                t,
                new LineLayerSpec
                {
                    X = x,
                    Y = y,
                    ColorBy = default(ColumnRef),
                }
            ),
            Spec(
                t,
                new LineLayerSpec
                {
                    X = x,
                    Y = y,
                    ColorBy = v,
                }
            ),
            Spec(
                t,
                new StepLayerSpec
                {
                    X = x,
                    Y = y,
                    ColorBy = v,
                }
            ),
            Spec(
                t,
                new AreaLayerSpec
                {
                    X = x,
                    Y = y,
                    ColorBy = v,
                }
            ),
            Spec(
                t,
                new BandLayerSpec
                {
                    X = x,
                    YMin = y,
                    YMax = y2,
                    ColorBy = v,
                }
            ),
            Spec(t, new RuleLayerSpec { X = x, ColorBy = v }),
            Spec(
                t,
                new SegmentLayerSpec
                {
                    X = x,
                    Y = y,
                    XEnd = x,
                    YEnd = y2,
                    ColorBy = v,
                }
            ),
            Spec(
                t,
                new ErrorBarLayerSpec
                {
                    X = x,
                    YMin = y,
                    YMax = y2,
                    ColorBy = v,
                }
            ),
            Spec(t, new CustomLayerSpec(new NoGeom()) { ColorBy = cat }),
            Spec(
                t,
                new LineLayerSpec
                {
                    X = x,
                    Y = y,
                    ColorBy = cat,
                    Style = new StrokeSpec(Colors.Red),
                }
            ),
            Spec(
                t,
                new BarLayerSpec
                {
                    X = x,
                    Y = y,
                    FillBy = cat,
                    Fill = Colors.Red,
                }
            ),
            Spec(
                t,
                new BarLayerSpec
                {
                    X = x,
                    Y = y,
                    ColorBy = cat,
                    Fill = Colors.Red,
                }
            ),
            Spec(
                t,
                new AreaLayerSpec
                {
                    X = x,
                    Y = y,
                    ColorBy = cat,
                    Outline = new StrokeSpec(Colors.Red),
                }
            ),
            Spec(
                t,
                new LineLayerSpec
                {
                    X = x,
                    Y = y,
                    FillBy = cat,
                }
            ),
            Spec(
                t,
                new LineLayerSpec
                {
                    X = x,
                    Y = y,
                    ColorBy = many,
                }
            ),
            Spec(
                t,
                new BarLayerSpec
                {
                    X = x,
                    Y = y,
                    ColorBy = v,
                }
            ) with
            {
                ScaleColor = Scale.Ordinal(),
            },
            Spec(
                t,
                new LineLayerSpec
                {
                    X = x,
                    Y = y,
                    ColorBy = cat,
                }
            ) with
            {
                ScaleColor = Scale.Sequential(),
            },
            Spec(
                t,
                new LineLayerSpec
                {
                    X = x,
                    Y = y,
                    ColorBy = cat,
                },
                new LineLayerSpec
                {
                    X = x,
                    Y = y2,
                    ColorBy = other,
                }
            ),
            Spec(
                t,
                new LineLayerSpec
                {
                    X = x,
                    Y = y,
                    ColorBy = cat,
                },
                new BarLayerSpec
                {
                    X = x,
                    Y = y,
                    ColorBy = v,
                }
            ),
            Spec(
                t,
                new BarLayerSpec
                {
                    X = x,
                    Y = y,
                    ColorBy = v,
                }
            ) with
            {
                ScaleColor = Scale.Diverging(mid: 1000),
            },
            Spec(
                t,
                new BarLayerSpec
                {
                    X = x,
                    Y = y,
                    ColorBy = v,
                }
            ) with
            {
                ScaleColor = Scale.Diverging() with { Mid = double.NaN },
            },
            Spec(
                t,
                new BarLayerSpec
                {
                    X = x,
                    Y = y,
                    ColorBy = v,
                }
            ) with
            {
                ScaleColor = Scale.Sequential() with { Scale = Scale.Linear() with { Domain = (2, 1) } },
            },
            Spec(
                shortTable,
                new BarLayerSpec
                {
                    X = shortTable.Col<double>("x"),
                    Y = shortTable.Col<double>("x"),
                    ColorBy = shortTable.Cat("c").ToColumnRef(),
                    Width = 1,
                }
            ) with
            { },
        ];
        using var plan = TestPlans.Create(pool, Size, Size);
        for (var i = 0; i < refused.Length - 1; i++)
        {
            _ = Assert.ThrowsAny<ArgumentException>(() => plan.Render(refused[i]));
        }

        _ = Assert.Throws<ArgumentNullException>(() => Plot.Create(t).ScaleColor(null!));
        foreach (var bad in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
        {
            var theme = Theme.Light with { ColorBarThickness = bad };
            Assert.Contains(
                "ColorBarThickness",
                Assert.Throws<ArgumentException>(() => plan.Render(Spec(t, new LineLayerSpec { X = x, Y = y }) with { Theme = theme })).Message,
                StringComparison.Ordinal
            );
            theme = Theme.Light with { ColorBarLength = bad };
            Assert.Contains(
                "ColorBarLength",
                Assert.Throws<ArgumentException>(() => plan.Render(Spec(t, new LineLayerSpec { X = x, Y = y }) with { Theme = theme })).Message,
                StringComparison.Ordinal
            );
        }
    }

    /// <summary>
    /// Bars over categories split by color hold at most 2^24 cells of extremes (design §5.5.3): exactly that many draw, one
    /// more is refused.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A color column of 256 categories over a categorical x of 65,536 categories, then of 65,537 (256 cells past), with
    ///   a row each of the first categories.
    ///
    /// Steps:
    /// - Validate each layer.
    ///
    /// Expected:
    /// - 256 × 65,536 passes; the next refuses with an ArgumentException that names 2^24.
    /// </remarks>
    [Fact]
    public void CategoryBars_SplitByColor_HoldAtMost2To24Cells()
    {
        var xNames = Enumerable.Range(0, 65_537).Select(i => $"x{i}").ToArray();
        var colorNames = Enumerable.Range(0, 256).Select(i => $"c{i}").ToArray();
        var rows = Enumerable.Range(0, 300).Select(i => (string?)xNames[i]).ToArray();
        using var y = Column.Wrap(new double[300]);
        var fitting = Column.Categorical(rows, xNames.Take(65_536).ToArray());
        var over = Column.Categorical(rows, xNames);
        var colors = Column.Categorical(Enumerable.Range(0, 300).Select(i => (string?)colorNames[i % 256]).ToArray(), colorNames);
        var table = DataTable.Create().Add("fits", fitting).Add("over", over).Add("y", y).Add("color", colors).Build();
        var buffer = new ColumnRef[4];
        LayerPlan.Validate(
            table,
            new BarLayerSpec
            {
                X = table.Cat("fits").ToColumnRef(),
                Y = table.Col<double>("y"),
                ColorBy = table.Cat("color").ToColumnRef(),
            },
            0,
            buffer
        );
        var error = Assert.ThrowsAny<ArgumentException>(() =>
            LayerPlan.Validate(
                table,
                new BarLayerSpec
                {
                    X = table.Cat("over").ToColumnRef(),
                    Y = table.Col<double>("y"),
                    ColorBy = table.Cat("color").ToColumnRef(),
                },
                0,
                buffer
            )
        );
        Assert.Contains(
            LayerPlan.MaxCategoryCells.ToString(System.Globalization.CultureInfo.InvariantCulture),
            error.Message,
            StringComparison.Ordinal
        );
    }

    /// <summary>
    /// A layer split by categories records what the same layer records drawn one category at a time, over that category's
    /// rows alone, in Palette[i mod n] (design §4.1, §5.5.3): for every geom, with the categories in runs and mixed row by
    /// row, past 4W rows a category.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A 100 × 100 target without guides, domains of [0, 100]; 3 categories of 500 rows each (4W = 400), seeded values.
    ///   The categories lie in runs (row i in ⌊i / 500⌋) or mix (row i in i mod 3).
    ///
    /// Steps:
    /// - Render each geom split by the categories; render the reference: one layer a category over its rows copied out, in
    ///   the palette's color (alpha 128 for an area's and a band's fill) and labeled with the category's name.
    ///
    /// Expected:
    /// - The same records, legend included; the split layer holds 3 series, gathered when mixed, none gathered in runs.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Split_RecordsAsTheLayersOfEachCategory(bool mixed)
    {
        using var pool = new PlotWorkerPool(2);
        using var data = Fixture.Create(500, mixed);
        var t = data.Table;
        var r = data.Reference;
        var (x, y, y2, x2) = (
            t.Col<double>("x").ToColumnRef(),
            t.Col<double>("y").ToColumnRef(),
            t.Col<double>("y2").ToColumnRef(),
            t.Col<double>("x2").ToColumnRef()
        );
        var cat = t.Cat("code").ToColumnRef();
        var palette = Palettes.Tab10;
        (LayerSpec Split, Func<int, LayerSpec> One)[] cases =
        [
            (
                new LineLayerSpec
                {
                    X = x,
                    Y = y,
                    ColorBy = cat,
                },
                c => new LineLayerSpec
                {
                    X = R(r, "x", c),
                    Y = R(r, "y", c),
                    Style = new StrokeSpec(palette[c]),
                    Label = Names[c],
                }
            ),
            (
                new StepLayerSpec
                {
                    X = x,
                    Y = y,
                    ColorBy = cat,
                },
                c => new StepLayerSpec
                {
                    X = R(r, "x", c),
                    Y = R(r, "y", c),
                    Style = new StrokeSpec(palette[c]),
                    Label = Names[c],
                }
            ),
            (
                new AreaLayerSpec
                {
                    X = x,
                    Y = y,
                    ColorBy = cat,
                    Position = Position.Identity,
                },
                c => new AreaLayerSpec
                {
                    X = R(r, "x", c),
                    Y = R(r, "y", c),
                    Fill = palette[c] with { A = 128 },
                    Label = Names[c],
                }
            ),
            (
                new BandLayerSpec
                {
                    X = x,
                    YMin = y,
                    YMax = y2,
                    ColorBy = cat,
                },
                c => new BandLayerSpec
                {
                    X = R(r, "x", c),
                    YMin = R(r, "y", c),
                    YMax = R(r, "y2", c),
                    Fill = palette[c] with { A = 128 },
                    Label = Names[c],
                }
            ),
            (
                new BarLayerSpec
                {
                    X = x,
                    Y = y,
                    ColorBy = cat,
                    Position = Position.Identity,
                    Width = 0.05,
                },
                c => new BarLayerSpec
                {
                    X = R(r, "x", c),
                    Y = R(r, "y", c),
                    Fill = palette[c],
                    Width = 0.05,
                    Label = Names[c],
                }
            ),
            (
                new ErrorBarLayerSpec
                {
                    X = x,
                    YMin = y,
                    YMax = y2,
                    ColorBy = cat,
                },
                c => new ErrorBarLayerSpec
                {
                    X = R(r, "x", c),
                    YMin = R(r, "y", c),
                    YMax = R(r, "y2", c),
                    Style = new StrokeSpec(palette[c]),
                    Label = Names[c],
                }
            ),
            (
                new RuleLayerSpec { X = x, ColorBy = cat },
                c => new RuleLayerSpec
                {
                    X = R(r, "x", c),
                    Style = new StrokeSpec(palette[c]),
                    Label = Names[c],
                }
            ),
            (
                new SegmentLayerSpec
                {
                    X = x,
                    Y = y,
                    XEnd = x2,
                    YEnd = y2,
                    ColorBy = cat,
                },
                c => new SegmentLayerSpec
                {
                    X = R(r, "x", c),
                    Y = R(r, "y", c),
                    XEnd = R(r, "x2", c),
                    YEnd = R(r, "y2", c),
                    Style = new StrokeSpec(palette[c]),
                    Label = Names[c],
                }
            ),
            (
                new RectLayerSpec
                {
                    XMin = x,
                    XMax = x2,
                    YMin = y,
                    YMax = y2,
                    ColorBy = cat,
                },
                c => new RectLayerSpec
                {
                    XMin = R(r, "x", c),
                    XMax = R(r, "x2", c),
                    YMin = R(r, "y", c),
                    YMax = R(r, "y2", c),
                    Fill = palette[c],
                    Label = Names[c],
                }
            ),
        ];
        foreach (var (split, one) in cases)
        {
            using var splitPlan = TestPlans.Create(pool, Size, Size);
            using var referencePlan = TestPlans.Create(pool, Size, Size);
            var actual = splitPlan.Render(Spec(t, split));
            var expected = referencePlan.Render(Spec(r, one(0), one(1), one(2)));
            AssertSameList(expected, actual);
            Assert.Equal(expected.Texts.ToArray(), actual.Texts.ToArray());
            var layer = Assert.IsType<SplitPlan>(splitPlan.Layers[0]);
            Assert.Equal((3, mixed ? 3 : 0), (layer.SeriesCount, layer.GatheredCount));
        }
    }

    /// <summary>
    /// A row of a missing category is in no series and draws nothing, and a category no row uses has no series but its row
    /// in the legend (design §4.1, §4.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 9 rows of a line over the categories a, b, c, d (given), rows of a and b mixed, two rows missing, none of c and d;
    ///   a 100 × 100 target without guides.
    ///
    /// Steps:
    /// - Render the split line.
    ///
    /// Expected:
    /// - Two series (a gathered, b gathered); their polylines hold the rows of a and of b alone; the legend lists a, b, c, d.
    /// </remarks>
    [Fact]
    public void Split_MissingAndUnusedCategories()
    {
        using var pool = new PlotWorkerPool(1);
        string?[] codes = ["a", "b", null, "a", "b", "a", null, "b", "a"];
        using var xs = Column.Wrap(Enumerable.Range(0, 9).Select(i => i * 10.0).ToArray());
        using var ys = Column.Wrap(Enumerable.Range(0, 9).Select(i => (double)(i * 5)).ToArray());
        var t = DataTable.Create().Add("x", xs).Add("y", ys).Add("c", Column.Categorical(codes, ["a", "b", "c", "d"])).Build();
        using var plan = TestPlans.Create(pool, Size, Size);
        var list = plan.Render(
            Spec(
                t,
                new LineLayerSpec
                {
                    X = t.Col<double>("x"),
                    Y = t.Col<double>("y"),
                    ColorBy = t.Cat("c").ToColumnRef(),
                }
            )
        );
        var layer = Assert.IsType<SplitPlan>(plan.Layers[0]);
        Assert.Equal((2, 2), (layer.SeriesCount, layer.GatheredCount));

        // The legend's band narrows the panel: the points are the rows' x over its width.
        var width = plan.Guides.Width;
        var a = SegmentsOf(list, 0);
        Assert.Equal([0, 30, 50, 80], a.Select(s => s.Start.X).Append(a[^1].End.X).Distinct().Select(p => Math.Round(p * 100 / width, 3)));
        var b = SegmentsOf(list, 1);
        Assert.Equal([10, 40, 70], b.Select(s => s.Start.X).Append(b[^1].End.X).Distinct().Select(p => Math.Round(p * 100 / width, 3)));
        Assert.Equal(["a", "b", "c", "d"], list.Texts.ToArray().Select(text => text.Text).ToArray());
    }

    /// <summary>
    /// The rows of a run make the same M4 points as a column of those rows alone (design §5.5.1): the first and the last
    /// point of the run stand for Before and After, and no point comes from outside it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 3,000 seeded rows over an evenly spaced X, and the run [1,000, 2,000) copied into a column of its own; 100
    ///   boundaries over the middle of the run, and over a span past both its ends.
    ///
    /// Steps:
    /// - Extract M4 over the run and over the copy.
    ///
    /// Expected:
    /// - The same points, roles, and breaks, the indices shifted by 1,000.
    /// </remarks>
    [Fact]
    public async Task RunSeries_MatchesTheRunsOwnColumn()
    {
        using var pool = new PlotWorkerPool(2);
        var random = new Random(4);
        var values = Enumerable.Range(0, 3000).Select(i => i % 97 == 0 ? double.NaN : random.NextDouble()).ToArray();
        using var y = Column.Wrap(values);
        using var own = Column.Wrap(values[1000..2000]);
        var x = Column.Uniform(0, 1, 3000);
        var ownX = Column.Uniform(1000, 1, 1000);
        using var pyramid = pool.BuildPyramid(y);
        using var ownPyramid = pool.BuildPyramid(own);
        await pyramid.WhenComplete();
        await ownPyramid.WhenComplete();
        foreach (var (low, high) in new[] { (1200.0, 1800.0), (500.0, 2500.0) })
        {
            var boundaries = Enumerable.Range(0, 101).Select(k => low + ((high - low) * k / 100)).ToArray();
            var run = new M4Point[M4.MaxPointCount(100)];
            var alone = new M4Point[M4.MaxPointCount(100)];
            var a = M4.Extract(x, pyramid, default, boundaries, run, default, new RowRange(1000, 2000));
            var b = M4.Extract(ownX, ownPyramid, default, boundaries, alone, default);
            Assert.Equal(b.Count, a.Count);
            Assert.Equal(
                alone[..b.Count].Select(p => (p.Index + 1000, p.X, p.Y, p.Roles, p.BreakBefore)),
                run[..a.Count].Select(p => (p.Index, p.X, p.Y, p.Roles, p.BreakBefore))
            );
        }
    }

    /// <summary>
    /// An area with FillBy and ColorBy of two columns splits its fill by one and its outline by the other (design §4.1); with
    /// FillBy alone, the outline is one line in the layer's series color.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 300 rows; the categories a, b, c in runs in one column and mixed in another; an area with an outline; a 100 × 100
    ///   target without guides.
    ///
    /// Steps:
    /// - Render with FillBy the runs and ColorBy the mix; then with FillBy alone.
    ///
    /// Expected:
    /// - Three fills in the palette's colors at alpha 128, then three outlines in the palette's colors; with FillBy alone,
    ///   three fills and one outline in the layer's series color (the palette's first).
    /// </remarks>
    [Fact]
    public void Area_FillByAndColorBy_SplitApart()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = Fixture.Create(100, mixed: false);
        var t = data.Table;
        var area = new AreaLayerSpec
        {
            X = t.Col<double>("x"),
            Y = t.Col<double>("y"),
            Outline = new StrokeSpec(null),
            FillBy = t.Cat("code").ToColumnRef(),
            ColorBy = t.Cat("mixed").ToColumnRef(),
            Position = Position.Identity,
        };
        using var plan = TestPlans.Create(pool, Size, Size);
        var list = plan.Render(Spec(t, area));
        var fills = list.Columns.ToArray().Select(column => column.Color).Distinct().ToArray();
        Assert.Equal(Enumerable.Range(0, 3).Select(i => Premultiplied(Palettes.Tab10[i] with { A = 128 })), fills);
        Assert.Equal(Enumerable.Range(0, 3).Select(i => Palettes.Tab10[i]), list.Polylines.ToArray().Take(3).Select(p => p.Style.Color));

        list = plan.Render(Spec(t, area with { ColorBy = null }));
        Assert.Equal(3, list.Columns.ToArray().Select(column => column.Color).Distinct().Count());
        Assert.Equal([Palettes.Tab10[0]], list.Polylines.ToArray().Select(p => p.Style.Color));
    }

    /// <summary>
    /// Bars and rects colored by a number take each element's color through the scale, draw nothing for NaN, and keep a
    /// color per bar past 4W (design §4.1, §4.2); past 131,072 they are refused.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A 100 × 100 target without guides; 500 bars (past 4W) colored by values from 0 to 499 with NaN at every 50th, a
    ///   sequential scale of [0, 499]; 4 rects colored through a diverging scale of [−1, 1]; a numeric x bar of 131,073
    ///   bars in view; bars over categories of 131,073 rows.
    ///
    /// Steps:
    /// - Render each.
    ///
    /// Expected:
    /// - 490 bars, each in viridis at t = v / 499; rects in RdBu at 0, 0.25, 0.5, 1, the first at 1 once the color column alone changes and is invalidated, and without a scale in viridis at the same
    ///   t over the learned [−1, 1]; 131,072 bars in view draw, and InvalidOperationException for 131,073 in view and for bars over categories of 131,073 rows.
    /// </remarks>
    [Fact]
    public void Elements_ColoredByANumber()
    {
        using var pool = new PlotWorkerPool(1);
        var values = Enumerable.Range(0, 500).Select(i => i % 50 == 0 ? double.NaN : i).ToArray();
        using var y = Column.Wrap(Enumerable.Repeat(50.0, 500).ToArray());
        using var v = Column.Wrap(values);
        var t = DataTable.Create().Add("x", Column.Uniform(0.1, 0.2, 500)).Add("y", y).Add("v", v).Build();
        using var plan = TestPlans.Create(pool, Size, Size);
        var list = plan.Render(
            Spec(
                t,
                new BarLayerSpec
                {
                    X = t.Col<double>("x"),
                    Y = t.Col<double>("y"),
                    Width = 0.1,
                    ColorBy = t.Col<double>("v"),
                }
            ) with
            {
                ScaleColor = Scale.Sequential(scale: Scale.Linear().WithDomain(0, 499)),
            }
        );
        var colors = list.Rects[1..491].ToArray().Select(rect => rect.Color).ToArray();
        Assert.Equal(values.Where(d => !double.IsNaN(d)).Select(d => Premultiplied(ColorMaps.Viridis.Map(d / 499))), colors);

        using var r0 = Column.Wrap(new double[] { 10, 30, 50, 70 });
        using var r1 = Column.Wrap(new double[] { 20, 40, 60, 80 });
        var rectValues = new double[] { -1, -0.5, 0, 1 };
        using var rv = Column.Wrap(rectValues);
        var rt = DataTable.Create().Add("a", r0).Add("b", r1).Add("v", rv).Build();
        var diverging = Spec(
            rt,
            new RectLayerSpec
            {
                XMin = rt.Col<double>("a"),
                XMax = rt.Col<double>("b"),
                YMin = rt.Col<double>("a"),
                YMax = rt.Col<double>("b"),
                ColorBy = rt.Col<double>("v"),
            }
        ) with
        {
            ScaleColor = Scale.Diverging(scale: Scale.Linear().WithDomain(-1, 1)),
        };
        list = plan.Render(diverging);
        Assert.Equal(
            new[] { 0, 0.25, 0.5, 1 }.Select(f => Premultiplied(ColorMaps.RdBu.Map(f))),
            list.Rects[1..5].ToArray().Select(rect => rect.Color)
        );

        // The color column changes alone: the plan records again, in the new colors (PR #173's review).
        rectValues[0] = 1;
        rv.Invalidate();
        list = plan.Render(diverging);
        Assert.Equal(Premultiplied(ColorMaps.RdBu.Map(1)), list.Rects[1].Color);
        rectValues[0] = -1;
        rv.Invalidate();

        // Without a scale, a sequential one over the column's range: −1 at 0, 1 at 1.
        list = plan.Render(
            Spec(
                rt,
                new RectLayerSpec
                {
                    XMin = rt.Col<double>("a"),
                    XMax = rt.Col<double>("b"),
                    YMin = rt.Col<double>("a"),
                    YMax = rt.Col<double>("b"),
                    ColorBy = rt.Col<double>("v"),
                }
            )
        );
        Assert.Equal(
            new[] { 0, 0.25, 0.5, 1 }.Select(f => Premultiplied(ColorMaps.Viridis.Map(f))),
            list.Rects[1..5].ToArray().Select(rect => rect.Color)
        );

        using var many = Column.Wrap(new double[LayerPlan.MaxElements + 1]);
        using var tall = Column.Wrap(Enumerable.Repeat(50.0, LayerPlan.MaxElements + 1).ToArray());
        var mt = DataTable.Create().Add("x", Column.Uniform(0, 1e-4, LayerPlan.MaxElements + 1)).Add("v", many).Add("y", tall).Build();
        var inView = Spec(
            mt,
            new BarLayerSpec
            {
                X = mt.Col<double>("x"),
                Y = mt.Col<double>("y"),
                Width = 1e-5,
                ColorBy = mt.Col<double>("v"),
            }
        ) with
        {
            ScaleColor = Scale.Sequential(scale: Scale.Linear().WithDomain(0, 1)),
            ScaleX = Scale.Linear().WithDomain(0, (LayerPlan.MaxElements - 1) * 1e-4),
        };
        Assert.Equal(LayerPlan.MaxElements, plan.Render(inView).Rects.Length - 1 - plan.Guides.ColorBarLength);
        _ = Assert.Throws<InvalidOperationException>(() =>
            plan.Render(inView with { ScaleX = Scale.Linear().WithDomain(0, LayerPlan.MaxElements * 1e-4) })
        );
        var codes = Column.Categorical(Enumerable.Repeat((string?)"a", LayerPlan.MaxElements + 1).ToArray());
        var ct = DataTable.Create().Add("x", codes).Add("v", many).Build();
        _ = Assert.Throws<InvalidOperationException>(() =>
            plan.Render(
                Spec(
                    ct,
                    new BarLayerSpec
                    {
                        X = ct.Cat("x").ToColumnRef(),
                        Y = ct.Col<double>("v"),
                        ColorBy = ct.Col<double>("v"),
                    }
                ) with
                {
                    ScaleX = Scale.Band(),
                }
            )
        );
    }

    /// <summary>
    /// Bars over categories split by color take each color category's extremes of its rows (design §5.5.3), as a count on
    /// the CPU gives them.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 600 seeded rows over 4 x categories and the colors a, b, c, mixed or in runs; a band of (0, 0); y domain [0, 100]; a 100 × 100
    ///   target without guides.
    ///
    /// Steps:
    /// - Render the bars split by color.
    ///
    /// Expected:
    /// - For each color and x category, a bar from the baseline to the CPU's greatest y, in the color's palette color.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CategoryBars_SplitByColor_TakeEachColorsExtremes(bool mixed)
    {
        using var pool = new PlotWorkerPool(2);
        var random = new Random(9);
        var xs = Enumerable.Range(0, 600).Select(i => (string?)$"x{random.Next(4)}").ToArray();
        var colors = Enumerable.Range(0, 600).Select(i => (string?)Names[mixed ? i % 3 : i / 200]).ToArray();
        var values = Enumerable.Range(0, 600).Select(_ => random.NextDouble() * 90).ToArray();
        using var y = Column.Wrap(values);
        var t = DataTable
            .Create()
            .Add("x", Column.Categorical(xs, ["x0", "x1", "x2", "x3"]))
            .Add("y", y)
            .Add("c", Column.Categorical(colors, Names))
            .Build();
        using var plan = TestPlans.Create(pool, Size, Size);
        var spec = Spec(
            t,
            new BarLayerSpec
            {
                X = t.Cat("x").ToColumnRef(),
                Y = t.Col<double>("y"),
                ColorBy = t.Cat("c").ToColumnRef(),
                Position = Position.Identity,
            }
        ) with
        {
            ScaleX = Scale.Band(0, 0),
        };
        var rects = plan.Render(spec).Rects[1..].ToArray();
        var legend = Names.Length;
        var bars = rects[..^legend];
        for (var c = 0; c < 3; c++)
        {
            for (var k = 0; k < 4; k++)
            {
                var max = Enumerable.Range(0, 600).Where(i => colors[i] == Names[c] && xs[i] == $"x{k}").Max(i => values[i]);
                Assert.Contains(bars, rect => rect.Color == Premultiplied(Palettes.Tab10[c]) && Math.Abs(rect.Y - (Size - max)) < 1e-3);
            }
        }
    }

    /// <summary>A split layer counts as one position: the layer after it keeps its series color (design §4.1).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A line split by 3 categories, then a line without a color; a 100 × 100 target without guides.
    ///
    /// Steps:
    /// - Render.
    ///
    /// Expected:
    /// - The second layer's polyline is in the palette's second color.
    /// </remarks>
    [Fact]
    public void Split_LayerCountsAsOnePosition()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = Fixture.Create(10, mixed: true);
        var t = data.Table;
        using var plan = TestPlans.Create(pool, Size, Size);
        var list = plan.Render(
            Spec(
                t,
                new LineLayerSpec
                {
                    X = t.Col<double>("x"),
                    Y = t.Col<double>("y"),
                    ColorBy = t.Cat("code").ToColumnRef(),
                },
                new LineLayerSpec { X = t.Col<double>("x"), Y = t.Col<double>("y2") }
            )
        );
        Assert.Equal(Palettes.Tab10[1], list.Polylines[3].Style.Color);
    }

    /// <summary>
    /// The legend lists the categories under the scale's title, with the first split layer's swatch in each category's
    /// color, and leaves out the split layer's label; a continuous scale draws a color bar of Lc rows, Lc the panel's height
    /// held to [100, 200], each row in its center's color, reversed with the scale, and ticks the extended algorithm chose
    /// (design §4.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The light theme and the fake text backend; bars split by a, b, c labeled "split", under an ordinal scale titled
    ///   "Sensor"; rects colored by a number over a sequential scale of [0, 10], on targets of 640 × 360, 640 × 600, and
    ///   640 × 120.
    ///
    /// Steps:
    /// - Render each; read the texts, the rects after the data, and the color bar's ticks.
    ///
    /// Expected:
    /// - The texts end in "Sensor", "a", "b", "c" without "split"; three squares in the palette's colors. The color bar has Lc
    ///   rects of 1 pixel, Lc = 200 at 360 and 600 tall, 100 at 120; row r's color is viridis at (r + 0.5) / Lc, the other
    ///   way round when reversed; its tick labels are the ticks' and lie inside the target.
    /// </remarks>
    [Fact]
    public void Legend_CategoriesAndColorBar()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = Fixture.Create(10, mixed: true);
        var t = data.Table;
        using var plan = new PlotPlan(pool, 640, 360, new TextEngine(new FakeTextBackend()), 1);
        var bars = Spec(
            t,
            new BarLayerSpec
            {
                X = t.Col<double>("x"),
                Y = t.Col<double>("y"),
                Width = 1,
                ColorBy = t.Cat("code").ToColumnRef(),
                Label = "split",
            }
        ) with
        {
            Theme = Theme.Light,
            ScaleColor = Scale.Ordinal().WithTitle("Sensor"),
        };
        var list = plan.Render(bars);
        var texts = list.Texts.ToArray().Select(text => text.Text).ToArray();
        Assert.Equal(["Sensor", "a", "b", "c"], texts[^4..]);
        Assert.DoesNotContain("split", texts);
        Assert.Equal(Enumerable.Range(0, 3).Select(i => Premultiplied(Palettes.Tab10[i])), list.Rects.ToArray()[^3..].Select(rect => rect.Color));
        Assert.All(list.Rects.ToArray()[^3..], rect => Assert.Equal(rect.Width, rect.Height));

        foreach (var (height, length) in new[] { (360, 200), (600, 200), (120, 100) })
        {
            foreach (var reverse in new[] { false, true })
            {
                using var target = new PlotPlan(pool, 640, height, new TextEngine(new FakeTextBackend()), 1);
                var inner = Scale.Linear().WithDomain(0, 10) with { Reverse = reverse };
                var rects = Spec(
                    t,
                    new RectLayerSpec
                    {
                        XMin = t.Col<double>("x"),
                        XMax = t.Col<double>("x2"),
                        YMin = t.Col<double>("y"),
                        YMax = t.Col<double>("y2"),
                        ColorBy = t.Col<double>("v"),
                    }
                ) with
                {
                    Theme = Theme.Light,
                    ScaleColor = Scale.Sequential(scale: inner),
                };
                var drawn = target.Render(rects);
                Assert.Equal(length, target.Guides.ColorBarLength);
                var bar = drawn.Rects.ToArray()[^length..];
                for (var row = 0; row < length; row++)
                {
                    // Rows are recorded from the bottom.
                    var t01 = (row + 0.5) / length;
                    Assert.Equal(Premultiplied(ColorMaps.Viridis.Map(reverse ? 1 - t01 : t01)), bar[row].Color);
                    Assert.Equal((1f, 16f), (bar[row].Height, bar[row].Width));
                }

                var ticks = target.Guides.ColorBar.TickLabels.ToArray();
                Assert.NotEmpty(ticks);
                Assert.All(ticks, label => Assert.Contains(label, drawn.Texts.ToArray().Select(text => text.Text)));
            }
        }
    }

    /// <summary>
    /// The workers find each category's runs as a count on the CPU does, once a column, and gather a mixed category's rows
    /// as the CPU does, again after the Y column changes (design §5.5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 2,500,000 seeded rows (three chunks) over 4 categories: a and b mixed, c in one run, d unused; a line split by them.
    ///
    /// Steps:
    /// - Find the runs twice; render; change Y, invalidate it, and render again.
    ///
    /// Expected:
    /// - First, last, and count as the CPU's; c a run, a and b not, d unused; the same object the second time. The split
    ///   records a and b's M4 of their rows, before and after the change, as their copied columns do.
    /// </remarks>
    [Fact]
    public void Workers_FindRunsAndGather()
    {
        const int Rows = 2_500_000;
        using var pool = new PlotWorkerPool(2);
        var random = new Random(12);
        var codes = Enumerable
            .Range(0, Rows)
            .Select(i =>
                (string?)(
                    i >= 2_000_000 ? "c"
                    : random.Next(2) == 0 ? "a"
                    : "b"
                )
            )
            .ToArray();
        var column = Column.Categorical(codes, ["a", "b", "c", "d"]);
        var runs = CategoryRuns.Of(column, pool);
        Assert.Same(runs, CategoryRuns.Of(column, pool));
        for (var c = 0; c < 4; c++)
        {
            var rows = Enumerable.Range(0, Rows).Where(i => codes[i] == Names.Append("d").ElementAt(c)).ToArray();
            Assert.Equal(rows.LongLength, runs.Count[c]);
            if (rows.Length > 0)
            {
                Assert.Equal((rows[0], rows[^1]), ((int)runs.First[c], (int)runs.Last[c]));
            }
        }

        Assert.Equal((false, false, true, false), (runs.IsRun(0), runs.IsRun(1), runs.IsRun(2), runs.IsRun(3)));

        var values = Enumerable.Range(0, Rows).Select(_ => (float)random.NextDouble() * 100).ToArray();
        using var y = Column.Wrap(values);
        var x = Column.Uniform(0, 100.0 / Rows, Rows);
        var t = DataTable.Create().Add("x", x).Add("y", y).Add("c", column).Build();
        using var plan = TestPlans.Create(pool, Size, Size);
        var spec = Spec(
            t,
            new LineLayerSpec
            {
                X = t.Col<double>("x"),
                Y = t.Col<float>("y"),
                ColorBy = t.Cat("c").ToColumnRef(),
            }
        );
        Check();
        for (var i = 0; i < Rows; i += 3)
        {
            values[i] = 100 - values[i];
        }

        y.Invalidate();
        Check();

        void Check()
        {
            var list = plan.Render(spec);
            for (var c = 0; c < 2; c++)
            {
                var rows = Enumerable.Range(0, Rows).Where(i => codes[i] == Names[c]).ToArray();
                using var own = Column.Wrap(rows.Select(i => values[i]).ToArray());
                using var ownX = Column.Wrap(rows.Select(i => i * (100.0 / Rows)).ToArray());
                var ownTable = DataTable.Create().Add("x", ownX).Add("y", own).Build();
                using var reference = TestPlans.Create(pool, Size, Size);
                var expected = reference.Render(
                    Spec(
                        ownTable,
                        new LineLayerSpec
                        {
                            X = ownTable.Col<double>("x"),
                            Y = ownTable.Col<float>("y"),
                            Label = "a",
                        }
                    )
                );
                Assert.Equal(SegmentsOf(expected, 0), SegmentsOf(list, c));
            }
        }
    }

    /// <summary>
    /// Another color column is the data stage; the inner scale's domain and a diverging middle the scale stage of the
    /// colors, which records again without extracting; the palette, the map, reverse, and the title only lay out and record
    /// (design §4.8).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A split line, and bars colored by a number, each drawn once.
    ///
    /// Steps:
    /// - Render with each change.
    ///
    /// Expected:
    /// - (layouts, extractions, recordings) changes: another color column (1, 1, 1); a wider stroke (1, 0, 1), in every series; the domain, the middle (1, 0, 1); the
    ///   palette, the map, reverse, the title (1, 0, 1).
    /// </remarks>
    [Fact]
    public void Diff_StagesByMember()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = Fixture.Create(10, mixed: true);
        var t = data.Table;
        using var plan = TestPlans.Create(pool, Size, Size);
        var line = Spec(
            t,
            new LineLayerSpec
            {
                X = t.Col<double>("x"),
                Y = t.Col<double>("y"),
                ColorBy = t.Cat("code").ToColumnRef(),
            }
        );
        _ = plan.Render(line);
        var other = line with
        {
            Layers =
            [
                new LineLayerSpec
                {
                    X = t.Col<double>("x"),
                    Y = t.Col<double>("y"),
                    ColorBy = t.Cat("mixed").ToColumnRef(),
                },
            ],
        };
        Assert.Equal((1, 1, 1), Counts(other));
        Assert.Equal((1, 0, 1), Counts(other with { ScaleColor = Scale.Ordinal(Palettes.Set1) }));
        Assert.Equal((1, 0, 1), Counts(other with { ScaleColor = Scale.Ordinal(Palettes.Set1).WithTitle("T") }));

        // A split layer's new looks reach its series: a wider stroke records again, in every category.
        var wide = other with
        {
            Layers = [((LineLayerSpec)other.Layers[0]) with { Style = new StrokeSpec(null, 3) }],
        };
        Assert.Equal((1, 0, 1), Counts(wide));
        Assert.All(plan.Render(wide).Polylines[..3].ToArray(), polyline => Assert.Equal(3f, polyline.Style.Width));

        var bars = Spec(
            t,
            new BarLayerSpec
            {
                X = t.Col<double>("x"),
                Y = t.Col<double>("y"),
                Width = 1,
                ColorBy = t.Col<double>("v"),
            }
        ) with
        {
            ScaleColor = Scale.Diverging(scale: Scale.Linear().WithDomain(0, 10)),
        };
        _ = plan.Render(bars);
        Assert.Equal((1, 0, 1), Counts(bars with { ScaleColor = Scale.Diverging(scale: Scale.Linear().WithDomain(0, 20)) }));
        Assert.Equal((1, 0, 1), Counts(bars with { ScaleColor = Scale.Diverging(mid: 3, scale: Scale.Linear().WithDomain(0, 20)) }));
        Assert.Equal((1, 0, 1), Counts(bars with { ScaleColor = Scale.Diverging(ColorMaps.PuOr, 3, Scale.Linear().WithDomain(0, 20)) }));
        Assert.Equal(
            (1, 0, 1),
            Counts(
                bars with
                {
                    ScaleColor = Scale.Diverging(ColorMaps.PuOr, 3, Scale.Linear().WithDomain(0, 20).WithTitle("T") with { Reverse = true }),
                }
            )
        );

        (int, int, int) Counts(PlotSpec next)
        {
            var before = (plan.Layouts, plan.Extractions, plan.Recordings);
            _ = plan.Render(next);
            return (plan.Layouts - before.Layouts, plan.Extractions - before.Extractions, plan.Recordings - before.Recordings);
        }
    }

    /// <summary>
    /// Once warm, split layers (in runs and gathered) and layers colored by numbers drawn again allocate nothing, the same spec
    /// and specs of other y domains in turn, which lay out and learn the colors again (design §4.8).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The light theme; a line split by mixed categories, bars split by runs, and rects colored through a log sequential
    ///   scale with a color bar, each drawn twice.
    ///
    /// Steps:
    /// - Render each five times, then two of other y domains in turn six times.
    ///
    /// Expected:
    /// - 0 bytes on this thread.
    /// </remarks>
    [Fact]
    public void Warm_AllocatesNothing()
    {
        using var pool = new PlotWorkerPool(1);
        using var mixed = Fixture.Create(200, mixed: true);
        using var runs = Fixture.Create(200, mixed: false);
        PlotSpec[] specs =
        [
            Spec(
                mixed.Table,
                new LineLayerSpec
                {
                    X = mixed.Table.Col<double>("x"),
                    Y = mixed.Table.Col<double>("y"),
                    ColorBy = mixed.Table.Cat("code").ToColumnRef(),
                }
            ) with
            {
                Theme = Theme.Light,
            },
            Spec(
                runs.Table,
                new BarLayerSpec
                {
                    X = runs.Table.Col<double>("x"),
                    Y = runs.Table.Col<double>("y"),
                    Width = 0.1,
                    ColorBy = runs.Table.Cat("code").ToColumnRef(),
                }
            ) with
            {
                Theme = Theme.Light,
            },
            Spec(
                runs.Table,
                new RectLayerSpec
                {
                    XMin = runs.Table.Col<double>("x"),
                    XMax = runs.Table.Col<double>("x2"),
                    YMin = runs.Table.Col<double>("y"),
                    YMax = runs.Table.Col<double>("y2"),
                    ColorBy = runs.Table.Col<double>("v"),
                }
            ) with
            {
                Theme = Theme.Light,
                ScaleColor = Scale.Sequential(ColorMaps.Plasma, Scale.Log()),
            },
        ];
        foreach (var spec in specs)
        {
            // The same spec, and two that take turns over other y domains and new layer nodes of the same looks (a host that builds its spec each frame), which lay out and learn the colors again.
            using var plan = new PlotPlan(pool, 640, 360, new TextEngine(new FakeTextBackend()), 1);
            PlotSpec[] turns =
            [
                spec,
                spec with
                {
                    ScaleY = Scale.Linear().WithDomain(0, 90),
                    Layers = [spec.Layers[0] with { }],
                },
                spec with
                {
                    ScaleY = Scale.Linear().WithDomain(0, 80),
                    Layers = [spec.Layers[0] with { }],
                },
            ];
            foreach (var turn in turns)
            {
                _ = plan.Render(turn);
                _ = plan.Render(turn);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 5; i++)
            {
                _ = plan.Render(spec);
            }

            for (var i = 0; i < 6; i++)
            {
                _ = plan.Render(turns[1 + (i % 2)]);
            }

            var spent = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(spent == 0, $"spec {Array.IndexOf(specs, spec)}: {spent}");
        }
    }

    /// <summary>Returns a color as a rect or a column records it: premultiplied, in [0, 1].</summary>
    private static System.Numerics.Vector4 Premultiplied(Color color)
    {
        var probe = new RenderList();
        probe.DrawRect(0, 0, 1, 1, color);
        return probe.Rects[0].Color;
    }

    /// <summary>Returns a reference table's column of a category.</summary>
    private static ColumnRef R(DataTable table, string name, int category) => table.Col<double>($"{name}{category}");

    /// <summary>A user's geom that draws nothing.</summary>
    private sealed class NoGeom : IGeom
    {
        public void Learn(GeomRanges ranges) { }

        public void Record(GeomFrame frame, RenderList list) { }
    }

    /// <summary>
    /// A table of 3 categories of m rows each, in runs or mixed, with seeded values in [0, 100], an evenly increasing x, and
    /// the reference table of each category's rows copied out; a column of the categories mixed when they are in runs, one
    /// of other categories, and one of 257.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        private readonly List<IDisposable> _owned = [];

        public DataTable Table { get; private set; } = null!;

        public DataTable Reference { get; private set; } = null!;

        public static Fixture Create(int perCategory, bool mixed)
        {
            var n = 3 * perCategory;
            var random = new Random(perCategory);
            var code = Enumerable.Range(0, n).Select(i => mixed ? i % 3 : i / perCategory).ToArray();
            var x = Enumerable.Range(0, n).Select(i => (i + 0.5) * (100.0 / n)).ToArray();
            var x2 = x.Select(v => v + (25.0 / n)).ToArray();
            var y = Enumerable.Range(0, n).Select(_ => 10 + (random.NextDouble() * 40)).ToArray();
            var y2 = y.Select(v => v + 5 + (random.NextDouble() * 40)).ToArray();
            var v = Enumerable.Range(0, n).Select(i => 1 + (i % 10.0)).ToArray();
            var result = new Fixture();
            var builder = DataTable
                .Create()
                .Add("x", result.Own(x))
                .Add("x2", result.Own(x2))
                .Add("y", result.Own(y))
                .Add("y2", result.Own(y2))
                .Add("v", result.Own(v))
                .Add("code", Column.Categorical(code.Select(c => (string?)Names[c]).ToArray(), Names))
                .Add("mixed", Column.Categorical(Enumerable.Range(0, n).Select(i => (string?)Names[i % 3]).ToArray(), Names))
                .Add("other", Column.Categorical(Enumerable.Range(0, n).Select(i => (string?)$"o{i % 3}").ToArray()))
                .Add(
                    "many",
                    Column.Categorical(
                        Enumerable.Range(0, n).Select(i => (string?)$"m{i % 257:000}").ToArray(),
                        Enumerable.Range(0, 257).Select(i => $"m{i:000}").ToArray()
                    )
                );
            result.Table = builder.Build();
            var reference = DataTable.Create();
            for (var c = 0; c < 3; c++)
            {
                var rows = Enumerable.Range(0, n).Where(i => code[i] == c).ToArray();
                _ = reference.Add($"x{c}", result.Own(rows.Select(i => x[i]).ToArray()));
                _ = reference.Add($"x2{c}", result.Own(rows.Select(i => x2[i]).ToArray()));
                _ = reference.Add($"y{c}", result.Own(rows.Select(i => y[i]).ToArray()));
                _ = reference.Add($"y2{c}", result.Own(rows.Select(i => y2[i]).ToArray()));
            }

            result.Reference = reference.Build();
            return result;
        }

        public void Dispose()
        {
            foreach (var owned in _owned)
            {
                owned.Dispose();
            }
        }

        private Column<double> Own(double[] values)
        {
            var column = Column.Wrap(values);
            _owned.Add(column);
            return column;
        }
    }
}
