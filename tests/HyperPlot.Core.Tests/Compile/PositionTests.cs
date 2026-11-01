using HyperPlot.Rendering;
using static HyperPlot.Tests.GeomTesting;

namespace HyperPlot.Tests;

/// <summary>The positions of row G5a: their checks, stacks, fills, dodges, the categorical x of error bars and rules, learning, and the diff (design §10 G5a row).</summary>
public sealed class PositionTests
{
    private static readonly string[] Groups = ["a", "b", "c"];

    /// <summary>The builders refuse what row G5a does not take, and the positions without a value are shared (design §4.1).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A table of a categorical and a numeric column, and a second table.
    ///
    /// Steps:
    /// - Make dodges of −0.1, 1, and NaN; read Identity, Stack, and Fill twice; give each builder a null position; add a
    ///   categorical error bar and rule of a default reference and of another table's column.
    ///
    /// Expected:
    /// - ArgumentOutOfRangeException for the dodges, the same instances, ArgumentNullException for the builders, and
    ///   ArgumentException for the references.
    /// </remarks>
    [Fact]
    public void Builders_CheckTheirArguments()
    {
        foreach (var padding in (double[])[-0.1, 1, double.NaN])
        {
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => Position.Dodge(padding));
        }

        Assert.Same(Position.Identity, Position.Identity);
        Assert.Same(Position.Stack, Position.Stack);
        Assert.Same(Position.Fill, Position.Fill);
        Assert.Equal(0.25, Position.Dodge(0.25).Padding);

        using var y = Column.Wrap(new double[] { 1, 2 });
        var t = DataTable.Create().Add("c", Column.Categorical(["a", "b"])).Add("y", y).Build();
        var other = DataTable.Create().Add("c", Column.Categorical(["a", "b"])).Build();
        _ = Assert.Throws<ArgumentNullException>(() => Plot.Create(t).Bar(t.Cat("c"), t.Col<double>("y"), configure: b => b.Position(null!)));
        _ = Assert.Throws<ArgumentNullException>(() =>
            Plot.Create(t).Area(t.Col<double>("y"), t.Col<double>("y"), configure: a => a.Position(null!))
        );
        _ = Assert.Throws<ArgumentNullException>(() => Plot.Create(t).VRule(t.Cat("c"), r => r.Position(null!)));
        _ = Assert.Throws<ArgumentException>(() => Plot.Create(t).ErrorBar(default, t.Col<double>("y"), t.Col<double>("y")));
        _ = Assert.Throws<ArgumentException>(() => Plot.Create(t).ErrorBar(other.Cat("c"), t.Col<double>("y"), t.Col<double>("y")));
        _ = Assert.Throws<ArgumentException>(() => Plot.Create(t).VRule(default(CategoryRef)));
        _ = Assert.Throws<ArgumentException>(() => Plot.Create(t).VRule(other.Cat("c")));
    }

    /// <summary>
    /// The compile refuses a position a geom does not take, each pair by name (design §4.1, §4.8): stack and fill off bars and
    /// areas, dodge off bars and categorical error bars and vertical rules, stack over colors a bar, a baseline that is not 0
    /// under a stack, a padding out of range, an unknown position, and too many cells.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A table of numbers, two categorical columns, a categorical column of 65,537 categories, and a raster; a 100 × 100
    ///   target without guides.
    ///
    /// Steps:
    /// - Render each refused spec.
    ///
    /// Expected:
    /// - ArgumentException naming the layer for each.
    /// </remarks>
    [Fact]
    public void Compile_RefusesPositionsAGeomDoesNotTake()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = Data.Create();
        var t = data.Table;
        var (x, y, cx, g) = (t.Col<double>("x").ToColumnRef(), t.Col<double>("y").ToColumnRef(), t.Cat("cx").ToColumnRef(), t.Cat("g").ToColumnRef());
        using var raster = Column.Wrap(new float[4]);
        PositionSpec[] stackAndDodge = [Position.Stack, Position.Fill, Position.Dodge()];
        var refused = new List<LayerSpec>();
        foreach (var position in stackAndDodge)
        {
            refused.Add(
                new LineLayerSpec
                {
                    X = x,
                    Y = y,
                    Position = position,
                }
            );
            refused.Add(
                new StepLayerSpec
                {
                    X = x,
                    Y = y,
                    Position = position,
                }
            );
            refused.Add(
                new BandLayerSpec
                {
                    X = x,
                    YMin = y,
                    YMax = y,
                    Position = position,
                }
            );
            refused.Add(
                new RectLayerSpec
                {
                    XMin = x,
                    XMax = x,
                    YMin = y,
                    YMax = y,
                    Position = position,
                }
            );
            refused.Add(new RuleLayerSpec { Y = y, Position = position });
            refused.Add(
                new SegmentLayerSpec
                {
                    X = x,
                    Y = y,
                    XEnd = x,
                    YEnd = y,
                    Position = position,
                }
            );
            refused.Add(
                new TextLayerSpec
                {
                    X = x,
                    Y = y,
                    Text = y,
                    Position = position,
                }
            );
            refused.Add(new ImageLayerSpec { Raster = Raster.Of(raster, 2), Position = position });
            refused.Add(new CustomLayerSpec(new NoGeom()) { Position = position });
        }

        refused.AddRange([
            new AreaLayerSpec
            {
                X = x,
                Y = y,
                Position = Position.Dodge(),
            },
            new ErrorBarLayerSpec
            {
                X = cx,
                YMin = y,
                YMax = y,
                Position = Position.Stack,
            },
            new ErrorBarLayerSpec
            {
                X = cx,
                YMin = y,
                YMax = y,
                Position = Position.Fill,
            },
            new RuleLayerSpec { X = cx, Position = Position.Stack },
            new RuleLayerSpec { X = cx, Position = Position.Fill },
            new ErrorBarLayerSpec
            {
                X = x,
                YMin = y,
                YMax = y,
                Position = Position.Dodge(),
            },
            new RuleLayerSpec { X = x, Position = Position.Dodge() },
            new BarLayerSpec
            {
                X = x,
                Y = y,
                ColorBy = y,
                Position = Position.Stack,
            },
            new BarLayerSpec
            {
                X = x,
                Y = y,
                ColorBy = y,
                Position = Position.Fill,
            },
            new BarLayerSpec
            {
                X = x,
                Y = y,
                Baseline = 1,
                Position = Position.Stack,
            },
            new BarLayerSpec
            {
                X = x,
                Y = y,
                Baseline = 1,
                Position = Position.Fill,
            },
            new AreaLayerSpec
            {
                X = x,
                Y = y,
                Baseline = 1,
                Position = Position.Stack,
            },
            new AreaLayerSpec
            {
                X = x,
                Y = y,
                Baseline = -1,
                ColorBy = g,
            },
            new BarLayerSpec
            {
                X = x,
                Y = y,
                Position = Position.Dodge() with { Padding = 1 },
            },
            new BarLayerSpec
            {
                X = x,
                Y = y,
                Position = Position.Dodge() with { Padding = -0.5 },
            },
            new BarLayerSpec
            {
                X = x,
                Y = y,
                Position = new UnknownPosition(),
            },
        ]);
        using var plan = TestPlans.Create(pool, Size, Size);
        foreach (var layer in refused)
        {
            var spec = Spec(t, layer) with
            {
                ScaleX = layer
                    is ErrorBarLayerSpec { X.Column: CategoricalColumn }
                        or RuleLayerSpec { X.IsDefault: false, X.Column: CategoricalColumn }
                    ? Scale.Band()
                    : Scale.Linear().WithDomain(0, Size),
            };
            var thrown = Assert.Throws<ArgumentException>(() => plan.Render(spec));
            Assert.Contains("layer 0", thrown.Message, StringComparison.OrdinalIgnoreCase);
        }

        // A categorical error bar split by color holds a cell a category pair: at most 2^24 of them (design §5.5.3).
        string[] names = [.. Enumerable.Range(0, 65_537).Select(i => $"x{i}")];
        string[] colors = [.. Enumerable.Range(0, 256).Select(i => $"c{i}")];
        var wide = DataTable
            .Create()
            .Add("x", Column.Categorical(["x0", "x1"], names))
            .Add("c", Column.Categorical(["c0", "c1"], colors))
            .Add("y", Column.Wrap(new double[] { 1, 2 }))
            .Build();
        var cells = Spec(
            wide,
            new ErrorBarLayerSpec
            {
                X = wide.Cat("x").ToColumnRef(),
                YMin = wide.Col<double>("y"),
                YMax = wide.Col<double>("y"),
                ColorBy = wide.Cat("c").ToColumnRef(),
            }
        ) with
        {
            ScaleX = Scale.Band(),
        };
        _ = Assert.Throws<ArgumentException>(() => plan.Render(cells));
    }

    /// <summary>
    /// Bars and areas split by categories stack by default, and nothing else does (design §4.1): a split line, an unsplit
    /// bar, and a split bar given Identity draw as before.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two groups of bars over a numeric x at the same x values, and the same as a line; a 100 × 100 target without guides.
    ///
    /// Steps:
    /// - Render each without a position and with the one it should mean.
    ///
    /// Expected:
    /// - The split bar and area record as with Stack; the split line, the unsplit bar, and the split bar with Identity as
    ///   with Identity.
    /// </remarks>
    [Fact]
    public void Defaults_StackSplitBarsAndAreasOnly()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = Data.Create();
        var t = data.Table;
        var (x, y, g) = (t.Col<double>("nx").ToColumnRef(), t.Col<double>("ny").ToColumnRef(), t.Cat("ng").ToColumnRef());
        (LayerSpec Default, PositionSpec Meant)[] cases =
        [
            (
                new BarLayerSpec
                {
                    X = x,
                    Y = y,
                    Width = 4,
                    ColorBy = g,
                },
                Position.Stack
            ),
            (
                new AreaLayerSpec
                {
                    X = x,
                    Y = y,
                    FillBy = g,
                },
                Position.Stack
            ),
            (
                new LineLayerSpec
                {
                    X = x,
                    Y = y,
                    ColorBy = g,
                },
                Position.Identity
            ),
            (new BarLayerSpec { X = t.Cat("cx").ToColumnRef(), Y = t.Col<double>("y") }, Position.Identity),
        ];
        foreach (var (layer, meant) in cases)
        {
            using var a = TestPlans.Create(pool, Size, Size);
            using var b = TestPlans.Create(pool, Size, Size);
            var scale = layer is BarLayerSpec { X.Column: CategoricalColumn } ? (ScaleSpec)Scale.Band() : Scale.Linear().WithDomain(0, Size);
            AssertSameList(
                b.Render(Spec(t, layer with { Position = meant }) with { ScaleX = scale }),
                a.Render(Spec(t, layer) with { ScaleX = scale })
            );
        }

        using var identity = TestPlans.Create(pool, Size, Size);
        using var stacked = TestPlans.Create(pool, Size, Size);
        var bars = new BarLayerSpec
        {
            X = x,
            Y = y,
            Width = 4,
            ColorBy = g,
        };
        Assert.NotEqual(
            stacked.Render(Spec(t, bars)).Rects.ToArray(),
            identity.Render(Spec(t, bars with { Position = Position.Identity })).Rects.ToArray()
        );
    }

    /// <summary>
    /// Bars over categories stack and fill as stacking the rows one by one does (design §4.1, §5.5.3): ggplot2's pos_stack,
    /// positive and negative apart, the last group next to 0.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 4 x categories and 3 groups: three rows in one cell, a NaN row, rows of a missing x and of a missing group, negative
    ///   values, and an x of zeros; band paddings of 0; a y domain of [−50, 50] on a 100 × 100 target without guides.
    ///
    /// Steps:
    /// - Render with Stack and with Fill; stack the rows one by one on the CPU, the groups from the last.
    ///
    /// Expected:
    /// - The same pieces, a group after another, in the group's palette color.
    /// </remarks>
    [Fact]
    public void CategoryStack_IsTheRowsStackedOneByOne()
    {
        using var pool = new PlotWorkerPool(2);
        using var data = Data.Create();
        var t = data.Table;
        foreach (var position in (PositionSpec[])[Position.Stack, Position.Fill])
        {
            using var plan = TestPlans.Create(pool, Size, Size);
            var spec = Spec(
                t,
                new BarLayerSpec
                {
                    X = t.Cat("cx").ToColumnRef(),
                    Y = t.Col<double>("y"),
                    ColorBy = t.Cat("g").ToColumnRef(),
                    Position = position,
                }
            ) with
            {
                ScaleX = Scale.Band(0, 0),
                ScaleY = Scale.Linear().WithDomain(-50, 50),
            };
            var list = plan.Render(spec);
            var expected = RowByRow(data, 4, position is FillPositionSpec);
            var rects = list.Rects[1..(expected.Count + 1)].ToArray();
            Assert.Equal(expected.Count, rects.Length);

            // The legend of the groups takes room on the right: the bands split the panel's width.
            var panel = list.Rects[0];
            Assert.Equal((0f, 0f, 100f), (panel.X, panel.Y, panel.Height));
            var band = panel.Width / 4.0;
            for (var i = 0; i < expected.Count; i++)
            {
                var (k, category, low, high) = expected[i];
                Close(band * category, rects[i].X);
                Close(band, rects[i].Width);
                Close(50 - high, rects[i].Y);
                Close(high - low, rects[i].Height);
                Assert.Equal(Premultiplied(Palettes.Tab10[k]), rects[i].Color);
            }
        }
    }

    /// <summary>
    /// Bars over a numeric x stack the rows at equal x and only those (design §4.1, §5.5.3): an x one ulp away is a bar of
    /// its own; each group is drawn from its inner end, 0 under the last.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Groups a at x 10, 20, 30 and b at 20, 30, 30 + 1 ulp, 40 with a negative value; bars 4 wide; a y domain of
    ///   [−50, 50] on a 100 × 100 target without guides.
    ///
    /// Steps:
    /// - Render the bars stacked.
    ///
    /// Expected:
    /// - Group a's pieces over b's at 20 and 30, b's from 0, the bar at 30 + 1 ulp apart, the negative piece below 0.
    /// </remarks>
    [Fact]
    public void NumericBarStack_StacksEqualXOnly()
    {
        using var pool = new PlotWorkerPool(1);
        var after30 = Math.BitIncrement(30.0);
        using var xs = Column.Wrap(new double[] { 10, 20, 30, 20, 30, after30, 40 });
        using var ys = Column.Wrap(new double[] { 1, 2, 3, 5, -1, 6, 4 });
        var t = DataTable.Create().Add("x", xs).Add("y", ys).Add("g", Column.Categorical(["a", "a", "a", "b", "b", "b", "b"])).Build();
        using var plan = TestPlans.Create(pool, Size, Size);
        var spec = Spec(
            t,
            new BarLayerSpec
            {
                X = t.Col<double>("x"),
                Y = t.Col<double>("y"),
                Width = 4,
                ColorBy = t.Cat("g").ToColumnRef(),
            }
        ) with
        {
            ScaleY = Scale.Linear().WithDomain(-50, 50),
        };
        var list = plan.Render(spec);
        var panel = list.Rects[0];
        Assert.Equal((0f, 0f, 100f), (panel.X, panel.Y, panel.Height));
        var rects = DataRects(list)[..7];

        // Group a: at 10 from 0 to 1, at 20 from 5 to 7, at 30 from 0 to 3 (b is below 0 there). Group b: at 20 from 0 to 5,
        // at 30 from −1 to 0, at 30 + 1 ulp from 0 to 6, at 40 from 0 to 4.
        (double X, double Low, double High)[] expected = [(10, 0, 1), (20, 5, 7), (30, 0, 3), (20, 0, 5), (30, -1, 0), (after30, 0, 6), (40, 0, 4)];
        var actual = rects.Select(r => (X: (r.X + (r.Width / 2.0)) * Size / panel.Width, Low: 50.0 - (r.Y + r.Height), High: 50.0 - r.Y)).ToArray();
        foreach (var (px, low, high) in expected)
        {
            Assert.Contains(actual, r => Math.Abs(r.X - px) < 1e-3 && Math.Abs(r.Low - low) < 1e-3 && Math.Abs(r.High - high) < 1e-3);
        }
    }

    /// <summary>
    /// An area stack is each group interpolated along its own x and summed (design §4.1, §5.5.3, ggplot2's stat_align): 0
    /// outside a group's range, a vertical edge where it starts, ends, or steps, and a point where it crosses 0, so that the
    /// positive and negative pieces meet on 0.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Group a at x 0, 2, 4 and group b at x 1, 3.5, 3.5, 5 that crosses 0 between 2 and 3.5 and steps at 3.5.
    ///
    /// Steps:
    /// - Stack them; at each point of the union, evaluate each group directly from its rows (no merge) from the side the
    ///   point stands for, and sum above and below 0 from the last group.
    ///
    /// Expected:
    /// - The union holds every x of both groups and the crossing at 2.25; two points at x 0 (a starts), 1 (b starts), 3.5 (b
    ///   steps), 4 (a ends), and 5 (b ends); each P and N matches the direct sums.
    /// </remarks>
    [Fact]
    public void AreaStack_IsEachGroupInterpolatedAndSummed()
    {
        using var ax = Column.Wrap(new double[] { 0, 2, 4 });
        using var ay = Column.Wrap(new double[] { 1, 3, 1 });
        using var bx = Column.Wrap(new double[] { 1, 3.5, 3.5, 5 });
        using var by = Column.Wrap(new double[] { 2, -2, 1, 2 });
        (double[] X, double[] Y)[] rows = [([0, 2, 4], [1, 3, 1]), ([1, 3.5, 3.5, 5], [2, -2, 1, 2])];
        using var stack = SeriesStack.Build(
            [new StackSeries(ax, ay, 0, 3), new StackSeries(bx, by, 0, 4)],
            area: true,
            fill: false,
            integer: false,
            origin: 0
        );
        var u = Enumerable.Range(0, (int)stack.Length).Select(i => stack.X.DifferenceAt(i, 0)).ToArray();
        Assert.Equal([0, 0, 1, 1, 2, 2.25, 3.5, 3.5, 4, 4, 5, 5], u);
        for (var i = 0; i < u.Length; i++)
        {
            // A point is the left side of its x when the next point has the same x, else the right side.
            var left = i + 1 < u.Length && u[i + 1] == u[i];
            double[] values = [Direct(rows[0], u[i], left), Direct(rows[1], u[i], left)];
            var (p, n) = (0.0, 0.0);
            for (var k = 1; k >= 0; k--)
            {
                (p, n) = (p + Math.Max(values[k], 0), n + Math.Min(values[k], 0));
                Assert.Equal(p, stack.Positive[k]?.DifferenceAt(i, 0) ?? 0, 12);
                Assert.Equal(n, stack.Negative[k]?.DifferenceAt(i, 0) ?? 0, 12);
            }
        }
    }

    /// <summary>
    /// Bands under a pixel join a pixel column of categories, each group's piece from the least inner end to the greatest
    /// outer end there (design §5.5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 5,000 x categories (50 a pixel column) and 2 groups with seeded positive values; band paddings of 0.
    ///
    /// Steps:
    /// - Render the bars stacked; scan the categories of each pixel column on the CPU.
    ///
    /// Expected:
    /// - A piece a group a pixel column, from the scan's least P(k + 1) to its greatest P(k).
    /// </remarks>
    [Fact]
    public void CategoryStack_UnderAPixel_JoinsAPixelColumn()
    {
        using var pool = new PlotWorkerPool(2);
        const int Categories = 5_000;
        var random = new Random(5);
        var codes = Enumerable.Range(0, 2 * Categories).Select(i => i % Categories).ToArray();
        var groups = Enumerable.Range(0, 2 * Categories).Select(i => i / Categories).ToArray();
        var values = Enumerable.Range(0, 2 * Categories).Select(_ => 1 + (random.NextDouble() * 20)).ToArray();
        string[] names = [.. Enumerable.Range(0, Categories).Select(i => $"x{i}")];
        using var y = Column.Wrap(values);
        var t = DataTable
            .Create()
            .Add("x", Column.Categorical(codes.Select(c => (string?)names[c]).ToArray(), names))
            .Add("g", Column.Categorical(groups.Select(c => (string?)Groups[c]).ToArray(), Groups.AsSpan(0, 2)))
            .Add("y", y)
            .Build();
        using var plan = TestPlans.Create(pool, Size, Size);
        var spec = Spec(
            t,
            new BarLayerSpec
            {
                X = t.Cat("x").ToColumnRef(),
                Y = t.Col<double>("y"),
                ColorBy = t.Cat("g").ToColumnRef(),
            }
        ) with
        {
            ScaleX = Scale.Band(0, 0),
        };
        var list = plan.Render(spec);

        // The legend takes room on the right: category i's band starts at W · i / 5,000 of the panel's width W.
        var panel = list.Rects[0];
        Assert.Equal((0f, 0f, 100f), (panel.X, panel.Y, panel.Height));
        var width = (int)panel.Width;
        var rects = DataRects(list)[..(2 * width)];
        double Sum(int category, int from) => Enumerable.Range(from, 2 - from).Sum(g => values[(g * Categories) + category]);
        for (var k = 0; k < 2; k++)
        {
            for (var c = 0; c < width; c++)
            {
                var (first, last) = ((int)Math.Ceiling(c * (double)Categories / width), (int)Math.Ceiling((c + 1) * (double)Categories / width) - 1);
                var span = Enumerable.Range(first, last - first + 1).ToArray();
                var top = span.Max(category => Sum(category, k));
                var bottom = k == 1 ? 0 : span.Min(category => Sum(category, 1));
                var rect = rects[(k * width) + c];
                Close(c, rect.X);
                Close(Size - top, rect.Y);
                Close(top - bottom, rect.Height);
            }
        }
    }

    /// <summary>
    /// A dodge places each group in its fixed place of the band, the first on the left, leaving a group's place empty where
    /// it has no row, less the padding (design §4.1): bars, error bars, and vertical rules over categories; and bars over a
    /// numeric x at x + Width · ((k + 0.5) / n − 0.5). Under a pixel, and past 4W, the groups lie over each other as without.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 2 x categories and 3 groups, group b without a row in x1; bands of 50 pixels; a padding of 0.2; numeric bars 6 wide
    ///   in 3 groups at the same x; 5,000 categories; 500 numeric bars.
    ///
    /// Steps:
    /// - Render the bars, the error bars, and the rules dodged, also over a reversed band; render the numeric bars dodged;
    ///   render the many categories and the many bars dodged and as they are.
    ///
    /// Expected:
    /// - Each element in its place's middle part, the reversed band's categories swapped but its groups still left to right;
    ///   the numeric bars at their shifted, narrowed spans; the many as without a dodge.
    /// </remarks>
    [Fact]
    public void Dodge_PlacesEachGroupInItsPlace()
    {
        using var pool = new PlotWorkerPool(1);
        using var y = Column.Wrap(new double[] { 10, 20, 30, 40, 50 });
        using var y2 = Column.Wrap(new double[] { 15, 25, 35, 45, 55 });
        var t = DataTable
            .Create()
            .Add("x", Column.Categorical(["x0", "x0", "x0", "x1", "x1"]))
            .Add("g", Column.Categorical(["a", "b", "c", "a", "c"], Groups))
            .Add("y", y)
            .Add("y2", y2)
            .Build();
        var (x, g) = (t.Cat("x").ToColumnRef(), t.Cat("g").ToColumnRef());
        foreach (var reverse in (bool[])[false, true])
        {
            using var plan = TestPlans.Create(pool, Size, Size);
            var scale = Scale.Band(0, 0) with { Reverse = reverse };
            var spec = Spec(
                t,
                new BarLayerSpec
                {
                    X = x,
                    Y = t.Col<double>("y"),
                    ColorBy = g,
                    Position = Position.Dodge(0.2),
                },
                new ErrorBarLayerSpec
                {
                    X = x,
                    YMin = t.Col<double>("y"),
                    YMax = t.Col<double>("y2"),
                    ColorBy = g,
                    CapWidth = 0,
                    Position = Position.Dodge(0.2),
                },
                new RuleLayerSpec
                {
                    X = x,
                    ColorBy = g,
                    Position = Position.Dodge(0.2),
                }
            ) with
            {
                ScaleX = scale,
            };
            var list = plan.Render(spec);

            // The legend takes room on the right: two bands split the panel's width, and three places each band.
            var panel = list.Rects[0];
            Assert.Equal((0f, 0f, 100f), (panel.X, panel.Y, panel.Height));
            var band = panel.Width / 2.0;
            var place = band / 3;
            (int Group, int Category, double Y)[] bars = [(0, 0, 10), (0, 1, 40), (1, 0, 20), (2, 0, 30), (2, 1, 50)];
            var rects = DataRects(list)[..5];
            for (var i = 0; i < bars.Length; i++)
            {
                var (group, category, value) = bars[i];
                var left = (band * (reverse ? 1 - category : category)) + (place * group) + (place * 0.1);
                Close(left, rects[i].X);
                Close(place * 0.8, rects[i].Width);
                Close(Size - value, rects[i].Y);
            }

            // The error bars' and the rules' lines stand in the same places' middles.
            var middles = bars.Select(b => (band * (reverse ? 1 - b.Category : b.Category)) + (place * b.Group) + (place / 2))
                .OrderBy(m => m)
                .ToArray();
            var lines = Enumerable
                .Range(0, list.Polylines.Length)
                .SelectMany(i => SegmentsOf(list, i))
                .Where(s => s.Start.X == s.End.X)
                .Select(s => (double)s.Start.X)
                .Distinct()
                .Order()
                .ToArray();
            Assert.Equal(middles.Length, lines.Length);
            for (var i = 0; i < middles.Length; i++)
            {
                Close(middles[i], lines[i]);
            }
        }

        // Numeric bars: x + Width · ((k + 0.5) / n − 0.5), Width / n · (1 − padding) wide.
        using var nx = Column.Wrap(new double[] { 50, 50, 50 });
        using var ny = Column.Wrap(new double[] { 10, 20, 30 });
        var n = DataTable.Create().Add("x", nx).Add("y", ny).Add("g", Column.Categorical(["a", "b", "c"])).Build();
        using var numeric = TestPlans.Create(pool, Size, Size);
        var numericList = numeric.Render(
            Spec(
                n,
                new BarLayerSpec
                {
                    X = n.Col<double>("x"),
                    Y = n.Col<double>("y"),
                    Width = 6,
                    ColorBy = n.Cat("g").ToColumnRef(),
                    Position = Position.Dodge(0.5),
                }
            )
        );
        var pixels = numericList.Rects[0].Width / Size;
        var numericRects = DataRects(numericList)[..3];
        for (var k = 0; k < 3; k++)
        {
            var center = 50 + (6 * (((k + 0.5) / 3) - 0.5));
            Close((center - 0.5) * pixels, numericRects[k].X);
            Close(pixels, numericRects[k].Width);
        }

        // Under a pixel and past 4W, a dodge draws as the elements are.
        using var many = Data.CreateMany();
        foreach (var layer in (LayerSpec[])[many.CategoryBars, many.NumericBars])
        {
            using var dodged = TestPlans.Create(pool, Size, Size);
            using var plain = TestPlans.Create(pool, Size, Size);
            var scale = layer is BarLayerSpec { X.Column: CategoricalColumn } ? (ScaleSpec)Scale.Band(0, 0) : Scale.Linear().WithDomain(0, Size);
            AssertSameList(
                plain.Render(Spec(many.Table, layer with { Position = Position.Identity }) with { ScaleX = scale }),
                dodged.Render(Spec(many.Table, layer with { Position = Position.Dodge(0.3) }) with { ScaleX = scale })
            );
        }
    }

    /// <summary>
    /// An error bar over categories runs from the least YMin to the greatest YMax of each category's rows, and a vertical rule
    /// stands at each category that has a row (design §5.5.3), split or not.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The table of the stack test: 4 x categories, rows of a missing x, NaN values; band paddings of 0; caps of 0.
    ///
    /// Steps:
    /// - Render an error bar layer and a rule layer, unsplit and split by group.
    ///
    /// Expected:
    /// - A vertical line a category with values, at its band's middle, from the CPU's extremes; rules at the categories
    ///   with rows; split, a line a group and category.
    /// </remarks>
    [Fact]
    public void CategoryErrorBarsAndRules_TakeEachCategorysRows()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = Data.Create();
        var t = data.Table;
        var x = t.Cat("cx").ToColumnRef();
        using var plan = TestPlans.Create(pool, Size, Size);
        var spec = Spec(
            t,
            new ErrorBarLayerSpec
            {
                X = x,
                YMin = t.Col<double>("y"),
                YMax = t.Col<double>("y2"),
                CapWidth = 0,
            },
            new RuleLayerSpec { X = x }
        ) with
        {
            ScaleX = Scale.Band(0, 0),
            ScaleY = Scale.Linear().WithDomain(-50, 50),
        };
        var list = plan.Render(spec);
        var bars = SegmentsOf(list, 0);
        var used = Enumerable.Range(0, 4).Where(c => data.Rows.Any(r => r.X == c && !double.IsNaN(r.Y))).ToArray();
        Assert.Equal(used.Length, bars.Length);
        for (var i = 0; i < used.Length; i++)
        {
            var rows = data.Rows.Where(r => r.X == used[i] && !double.IsNaN(r.Y)).ToArray();
            Close((25 * used[i]) + 12.5, bars[i].Start.X);
            Close(50 - rows.Min(r => r.Y), bars[i].Start.Y);
            Close(50 - rows.Max(r => r.Y2), bars[i].End.Y);
        }

        var rules = SegmentsOf(list, 1).Select(s => (double)s.Start.X).ToArray();
        var withRows = Enumerable.Range(0, 4).Where(c => data.Rows.Any(r => r.X == c)).Select(c => (25 * c) + 12.5).ToArray();
        Assert.Equal(withRows.Length, rules.Length);
        for (var i = 0; i < rules.Length; i++)
        {
            Close(withRows[i], rules[i]);
        }

        // Split: a line a group and a category with values, a rule a group and a category with rows.
        using var split = TestPlans.Create(pool, Size, Size);
        var splitList = split.Render(
            spec with
            {
                Layers =
                [
                    new ErrorBarLayerSpec
                    {
                        X = x,
                        YMin = t.Col<double>("y"),
                        YMax = t.Col<double>("y2"),
                        CapWidth = 0,
                        ColorBy = t.Cat("g").ToColumnRef(),
                    },
                    new RuleLayerSpec { X = x, ColorBy = t.Cat("g").ToColumnRef() },
                ],
            }
        );
        var cells = data.Rows.Where(r => r.X >= 0 && r.G >= 0 && !double.IsNaN(r.Y)).Select(r => (r.X, r.G)).Distinct().Count();
        var rowCells = data.Rows.Where(r => r.X >= 0 && r.G >= 0).Select(r => (r.X, r.G)).Distinct().Count();

        // The legend's three swatches are polylines too, after the layers'.
        var segments = Enumerable.Range(0, splitList.Polylines.Length - Groups.Length).Select(i => SegmentsOf(splitList, i).Length).ToArray();
        Assert.Equal(cells + rowCells, segments.Sum());
    }

    /// <summary>
    /// A stacked layer's y domain is what it stacks to (design §4.2): the greatest P(0) and the least N(0) with 0; a fill's is
    /// [0, 1], or [−1, 1] with negative values; a dodge leaves both domains as they are; a log y leaves 0 out.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The stack test's table and the numeric groups; learned domains.
    ///
    /// Steps:
    /// - Render stacked, filled, and dodged bars; and stacked numeric bars on a log y.
    ///
    /// Expected:
    /// - The y domains from the CPU's stack, [−1, 1], the identity's domains, and the log domain of the stack's positive ends.
    /// </remarks>
    [Fact]
    public void Learning_TakesTheStackedEnds()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = Data.Create();
        var t = data.Table;
        var bars = new BarLayerSpec
        {
            X = t.Cat("cx").ToColumnRef(),
            Y = t.Col<double>("y"),
            ColorBy = t.Cat("g").ToColumnRef(),
        };
        PlotSpec Learned(LayerSpec layer, ScaleSpec? y = null) =>
            new()
            {
                Theme = TestPlans.Bare,
                Data = t,
                Layers = [layer],
                ScaleX = Scale.Band(0, 0),
                ScaleY = y ?? Scale.Linear(),
            };

        var pieces = RowByRow(data, 4, fill: false);
        using (var plan = TestPlans.Create(pool, Size, Size))
        {
            _ = plan.Render(Learned(bars with { Position = Position.Stack }));
            Assert.Equal((pieces.Min(p => p.Low), pieces.Max(p => p.High)), Ends(plan.YDomain));
        }

        using (var plan = TestPlans.Create(pool, Size, Size))
        {
            _ = plan.Render(Learned(bars with { Position = Position.Fill }));
            Assert.Equal((-1.0, 1.0), Ends(plan.YDomain));
        }

        using (var dodged = TestPlans.Create(pool, Size, Size))
        using (var plain = TestPlans.Create(pool, Size, Size))
        {
            _ = dodged.Render(Learned(bars with { Position = Position.Dodge(0.2) }));
            _ = plain.Render(Learned(bars with { Position = Position.Identity }));
            Assert.Equal(Ends(plain.YDomain), Ends(dodged.YDomain));
            Assert.Equal(Ends(plain.XDomain), Ends(dodged.XDomain));
        }

        using (var plan = TestPlans.Create(pool, Size, Size))
        {
            var numeric = new BarLayerSpec
            {
                X = t.Col<double>("nx"),
                Y = t.Col<double>("ny"),
                Width = 4,
                ColorBy = t.Cat("ng").ToColumnRef(),
            };
            _ = plan.Render(Learned(numeric) with { ScaleX = Scale.Linear(), ScaleY = Scale.Log() });
            var (low, high) = Ends(plan.YDomain);
            var (least, most) = data.NumericStackedEnds();
            Assert.Equal(Math.Log10(least), low, 9);
            Assert.Equal(Math.Log10(most), high, 9);
        }
    }

    /// <summary>
    /// Stacked bars flip with the coordinate system and turn into sectors in polar (design §4.4): the pieces' rectangles
    /// swap their axes, and polar records a sector a piece.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The stack test's bars stacked without a split (so no legend) on a square 100 × 100 target without guides.
    ///
    /// Steps:
    /// - Render cartesian, flipped, and polar.
    ///
    /// Expected:
    /// - The flipped rectangles are the cartesian ones with x and y swapped; polar holds as many sectors as there are pieces.
    /// </remarks>
    [Fact]
    public void Stack_FlipsAndTurnsPolar()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = Data.Create();
        var t = data.Table;

        // Not split, so that no legend narrows the panel: a square panel swaps its axes into each other.
        var spec = Spec(
            t,
            new BarLayerSpec
            {
                X = t.Cat("cx").ToColumnRef(),
                Y = t.Col<double>("y"),
                Position = Position.Stack,
            }
        ) with
        {
            ScaleX = Scale.Band(0, 0),
            ScaleY = Scale.Linear().WithDomain(-50, 50),
        };
        using var cartesian = TestPlans.Create(pool, Size, Size);
        using var flipped = TestPlans.Create(pool, Size, Size);
        using var polar = TestPlans.Create(pool, Size, Size);
        var plain = DataRects(cartesian.Render(spec));
        var count = plain.Length;
        Assert.True(count > 4);
        var swapped = DataRects(flipped.Render(spec with { Coord = Coord.Cartesian(flip: true) }));
        Assert.Equal(count, swapped.Length);
        for (var i = 0; i < count; i++)
        {
            Close(Size - plain[i].Y - plain[i].Height, swapped[i].X);
            Close(plain[i].Height, swapped[i].Width);
            Close(plain[i].X, Size - swapped[i].Y - swapped[i].Height);
        }

        // A piece is a sector or more (a long one is cut into pieces of at most π / 16), past the panel's own sectors.
        using var empty = TestPlans.Create(pool, Size, Size);
        var sectors = polar.Render(spec with { Coord = Coord.Polar() }).Sectors.Length;
        var panel = empty.Render(spec with { Coord = Coord.Polar(), Layers = [] }).Sectors.Length;
        Assert.InRange(sectors - panel, count, int.MaxValue);
    }

    /// <summary>
    /// A change that brings, removes, or swaps a stack makes the layer again; between Identity and Dodge, and a padding,
    /// it records again only (design §4.8); a new Y version sums again, and a numeric stack is made again.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The stack test's categorical bars and the numeric groups' bars.
    ///
    /// Steps:
    /// - Render Stack, Fill, Identity, Dodge, Dodge(0.3); change a value of Y and invalidate it; render again.
    ///
    /// Expected:
    /// - A new layer plan after Stack → Fill and Fill → Identity, the same after Identity → Dodge → Dodge(0.3) with no
    ///   extraction; after the invalidation, the new sums, and a new plan for the numeric stack.
    /// </remarks>
    [Fact]
    public void Diff_MakesTheLayerAgainForStacksOnly()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = Data.Create();
        var t = data.Table;
        var bars = new BarLayerSpec
        {
            X = t.Cat("cx").ToColumnRef(),
            Y = t.Col<double>("y"),
            ColorBy = t.Cat("g").ToColumnRef(),
        };
        PlotSpec Of(PositionSpec position) =>
            Spec(t, bars with { Position = position }) with
            {
                ScaleX = Scale.Band(0, 0),
                ScaleY = Scale.Linear().WithDomain(-50, 50),
            };
        using var plan = TestPlans.Create(pool, Size, Size);
        _ = plan.Render(Of(Position.Stack));
        var first = plan.Layers[0];
        _ = plan.Render(Of(Position.Fill));
        Assert.NotSame(first, plan.Layers[0]);
        var filled = plan.Layers[0];
        _ = plan.Render(Of(Position.Identity));
        Assert.NotSame(filled, plan.Layers[0]);
        var identity = plan.Layers[0];
        var extractions = plan.Extractions;
        _ = plan.Render(Of(Position.Dodge()));
        _ = plan.Render(Of(Position.Dodge(0.3)));
        Assert.Same(identity, plan.Layers[0]);
        Assert.Equal(extractions, plan.Extractions);

        // A new version of Y: the stack sums again.
        _ = plan.Render(Of(Position.Stack));
        data.SetY(0, 9);
        var list = plan.Render(Of(Position.Stack));
        var expected = RowByRow(data, 4, fill: false);
        Assert.Equal(expected.Count, DataRects(list).Length - Groups.Length);

        // A numeric stack is made again on a new version, its union changing length.
        var numeric = Spec(
            t,
            new BarLayerSpec
            {
                X = t.Col<double>("nx"),
                Y = t.Col<double>("ny"),
                Width = 4,
                ColorBy = t.Cat("ng").ToColumnRef(),
            }
        );
        _ = plan.Render(numeric);
        var before = plan.Layers[0];
        data.SetNumericY(0, 3);
        _ = plan.Render(numeric);
        Assert.NotSame(before, plan.Layers[0]);
        Assert.False(plan.Layers[0].Stale);
    }

    /// <summary>Once warm, stacked bars and areas and dodged bars and error bars drawn again allocate nothing (design §4.8).</summary>
    /// <remarks>
    /// Preconditions:
    /// - The stack test's categorical bars stacked, the numeric groups' areas stacked, and dodged bars with error bars;
    ///   each spec and a new node of it in turn.
    ///
    /// Steps:
    /// - Render each a few times, then five times more in turn.
    ///
    /// Expected:
    /// - 0 bytes on this thread.
    /// </remarks>
    [Fact]
    public void Warm_AllocatesNothing()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = Data.Create();
        var t = data.Table;
        var (cx, g) = (t.Cat("cx").ToColumnRef(), t.Cat("g").ToColumnRef());
        PlotSpec[] specs =
        [
            Spec(
                t,
                new BarLayerSpec
                {
                    X = cx,
                    Y = t.Col<double>("y"),
                    ColorBy = g,
                }
            ) with
            {
                ScaleX = Scale.Band(0, 0),
                ScaleY = Scale.Linear().WithDomain(-50, 50),
            },
            Spec(
                t,
                new AreaLayerSpec
                {
                    X = t.Col<double>("nx"),
                    Y = t.Col<double>("ny"),
                    FillBy = t.Cat("ng").ToColumnRef(),
                    Outline = new StrokeSpec(null),
                }
            ),
            Spec(
                t,
                new BarLayerSpec
                {
                    X = cx,
                    Y = t.Col<double>("y"),
                    ColorBy = g,
                    Position = Position.Dodge(0.1),
                },
                new ErrorBarLayerSpec
                {
                    X = cx,
                    YMin = t.Col<double>("y"),
                    YMax = t.Col<double>("y2"),
                    ColorBy = g,
                    Position = Position.Dodge(0.1),
                }
            ) with
            {
                ScaleX = Scale.Band(),
                ScaleY = Scale.Linear().WithDomain(-50, 50),
            },
        ];
        foreach (var spec in specs)
        {
            using var plan = TestPlans.Create(pool, Size, Size);
            var renewed = spec with { Layers = [.. spec.Layers.Select(l => l with { })] };
            for (var i = 0; i < 3; i++)
            {
                _ = plan.Render(spec);
                _ = plan.Render(renewed);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 5; i++)
            {
                _ = plan.Render(spec);
                _ = plan.Render(renewed);
            }

            var spent = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(spent == 0, $"spec {Array.IndexOf(specs, spec)}: {spent}");
        }
    }

    /// <summary>
    /// Stacks the rows of each x category one by one, as ggplot2's pos_stack does: the rows sorted by group from the last,
    /// positive and negative values from 0 apart; a group's piece runs from the sum before its first row to the sum after its
    /// last. Returns the pieces a group after another, a category after another, above 0 before below.
    /// </summary>
    private static List<(int Group, int Category, double Low, double High)> RowByRow(Data data, int categories, bool fill)
    {
        var pieces = new (double Low, double High)?[Groups.Length, categories, 2];
        for (var category = 0; category < categories; category++)
        {
            var rows = data.Rows.Where(r => r.X == category && r.G >= 0 && !double.IsNaN(r.Y)).OrderByDescending(r => r.G).ToArray();
            var (up, down) = (0.0, 0.0);
            foreach (var row in rows)
            {
                var side = row.Y < 0 ? 1 : 0;
                var start = side == 0 ? up : down;
                (up, down) = side == 0 ? (up + row.Y, down) : (up, down + row.Y);
                var end = side == 0 ? up : down;
                var known = pieces[row.G, category, side];
                pieces[row.G, category, side] = (known?.Low ?? start, end);
            }

            for (var g = 0; g < Groups.Length; g++)
            {
                for (var side = 0; side < 2; side++)
                {
                    if (fill && pieces[g, category, side] is { } piece)
                    {
                        var total = side == 0 ? up : -down;
                        pieces[g, category, side] = total == 0 ? (0, 0) : (piece.Low / total, piece.High / total);
                    }
                }
            }
        }

        var result = new List<(int, int, double, double)>();
        for (var g = 0; g < Groups.Length; g++)
        {
            for (var category = 0; category < categories; category++)
            {
                for (var side = 0; side < 2; side++)
                {
                    if (pieces[g, category, side] is { } piece && piece.Low != piece.High)
                    {
                        result.Add((g, category, Math.Min(piece.Low, piece.High), Math.Max(piece.Low, piece.High)));
                    }
                }
            }
        }

        return result;
    }

    /// <summary>Returns a group's value at x read directly from its rows: 0 outside, the first and last row at an x for its left and right sides, the line between rows.</summary>
    private static double Direct((double[] X, double[] Y) rows, double x, bool left)
    {
        var (xs, ys) = rows;
        if (x < xs[0] || x > xs[^1])
        {
            return 0;
        }

        var first = Array.IndexOf(xs, x);
        if (first >= 0)
        {
            var last = Array.LastIndexOf(xs, x);
            return left ? (first == 0 ? 0 : ys[first]) : (last == xs.Length - 1 ? 0 : ys[last]);
        }

        var next = Array.FindIndex(xs, v => v > x);
        var (x0, y0, x1, y1) = (xs[next - 1], ys[next - 1], xs[next], ys[next]);
        return y0 + ((y1 - y0) * (x - x0) / (x1 - x0));
    }

    /// <summary>Returns a color as a rect records it: premultiplied, in [0, 1].</summary>
    private static System.Numerics.Vector4 Premultiplied(Color color) =>
        new(color.R / 255f * (color.A / 255f), color.G / 255f * (color.A / 255f), color.B / 255f * (color.A / 255f), color.A / 255f);

    private sealed record UnknownPosition : PositionSpec
    {
        public UnknownPosition()
            : base(Position.Stack) { }
    }

    private sealed class NoGeom : IGeom
    {
        public void Learn(GeomRanges ranges) { }

        public void Record(GeomFrame frame, RenderList list) { }
    }

    /// <summary>
    /// The table of the tests, 15 rows: over 4 x categories ("cx") and 3 groups ("g"), Y and Y2 with three rows in one cell,
    /// a NaN, a missing x, a missing group, negative values, and an x of zeros; and two numeric groups ("nx", "ny", "ng")
    /// in x order: a at 10, then a and b at each of 20 to 80 (both gathered), all positive.
    /// </summary>
    private sealed class Data : IDisposable
    {
        private readonly double[] _y;
        private readonly double[] _numericX;
        private readonly double[] _numericY;
        private readonly Column<double> _yColumn;
        private readonly Column<double> _y2Column;
        private readonly Column<double> _numericXColumn;
        private readonly Column<double> _numericYColumn;

        private Data()
        {
            (int X, int G, double Y)[] rows =
            [
                (0, 0, 3),
                (0, 0, 2),
                (0, 1, -4),
                (0, 2, 5),
                (0, 2, double.NaN),
                (0, 0, 1),
                (1, 1, 1),
                (1, 2, -2),
                (1, 2, -3),
                (1, 0, -1),
                (2, 0, 0),
                (2, 1, 0),
                (3, 2, 4),
                (-1, 0, 7),
                (3, -1, 9),
            ];
            Rows = [.. rows.Select((r, i) => (r.X, r.G, r.Y, Y2: r.Y + 1 + (i % 3)))];
            _y = [.. Rows.Select(r => r.Y)];
            (_yColumn, _y2Column) = (Column.Wrap(_y), Column.Wrap(Rows.Select(r => r.Y2).ToArray()));

            // In x order, the groups interleaved: a at 10, then a and b at each of 20 to 80.
            NumericGroups = [0, .. Enumerable.Range(0, 14).Select(i => i % 2)];
            _numericX = [10, .. Enumerable.Range(0, 14).Select(i => 20.0 + (10 * (i / 2)))];
            _numericY = [.. Enumerable.Range(0, 15).Select(i => 1.0 + (i % 5) + (i % 2))];
            (_numericXColumn, _numericYColumn) = (Column.Wrap(_numericX), Column.Wrap(_numericY));
            Table = DataTable
                .Create()
                .Add("cx", Column.Categorical(Rows.Select(r => r.X < 0 ? null : $"x{r.X}").ToArray(), ["x0", "x1", "x2", "x3"]))
                .Add("g", Column.Categorical(Rows.Select(r => r.G < 0 ? null : Groups[r.G]).ToArray(), Groups))
                .Add("y", _yColumn)
                .Add("y2", _y2Column)
                .Add("x", _yColumn)
                .Add("nx", _numericXColumn)
                .Add("ny", _numericYColumn)
                .Add("ng", Column.Categorical(NumericGroups.Select(g => (string?)Groups[g]).ToArray(), Groups.AsSpan(0, 2)))
                .Build();
        }

        public (int X, int G, double Y, double Y2)[] Rows { get; }

        public int[] NumericGroups { get; }

        public DataTable Table { get; }

        public static Data Create() => new();

        public static ManyData CreateMany() => new();

        /// <summary>Returns the least and the greatest end of the numeric groups' stacked bars that are not 0: b, and a over b.</summary>
        public (double Least, double Most) NumericStackedEnds()
        {
            var ends = new List<double>();
            foreach (var x in _numericX.Distinct())
            {
                var b = Enumerable.Range(0, 15).Where(r => NumericGroups[r] == 1 && _numericX[r] == x).Sum(r => _numericY[r]);
                var a = Enumerable.Range(0, 15).Where(r => NumericGroups[r] == 0 && _numericX[r] == x).Sum(r => _numericY[r]);
                ends.AddRange(new[] { b, a + b }.Where(v => v != 0));
            }

            return (ends.Min(), ends.Max());
        }

        public void SetY(int row, double value)
        {
            _y[row] = value;
            Rows[row] = Rows[row] with { Y = value };
            _yColumn.Invalidate();
        }

        public void SetNumericY(int row, double value)
        {
            _numericY[row] = value;
            _numericYColumn.Invalidate();
        }

        public void Dispose()
        {
            _yColumn.Dispose();
            _y2Column.Dispose();
            _numericXColumn.Dispose();
            _numericYColumn.Dispose();
        }
    }

    /// <summary>5,000 categories of bars under a pixel, and 500 numeric bars past 4W, both in 3 groups.</summary>
    private sealed class ManyData : IDisposable
    {
        private readonly Column<double> _x;
        private readonly Column<double> _y;

        public ManyData()
        {
            const int Rows = 5_000;
            string[] names = [.. Enumerable.Range(0, Rows).Select(i => $"x{i}")];
            (_x, _y) = (
                Column.Wrap(Enumerable.Range(0, Rows).Select(i => i * 100.0 / Rows).ToArray()),
                Column.Wrap(Enumerable.Range(0, Rows).Select(i => 1.0 + (i % 7)).ToArray())
            );
            Table = DataTable
                .Create()
                .Add("c", Column.Categorical(Enumerable.Range(0, Rows).Select(i => (string?)names[i]).ToArray(), names))
                .Add("g", Column.Categorical(Enumerable.Range(0, Rows).Select(i => (string?)Groups[i % 3]).ToArray(), Groups))
                .Add("x", _x)
                .Add("y", _y)
                .Build();
            CategoryBars = new BarLayerSpec
            {
                X = Table.Cat("c").ToColumnRef(),
                Y = Table.Col<double>("y"),
                ColorBy = Table.Cat("g").ToColumnRef(),
            };
            NumericBars = new BarLayerSpec
            {
                X = Table.Col<double>("x"),
                Y = Table.Col<double>("y"),
                Width = 0.02,
                ColorBy = Table.Cat("g").ToColumnRef(),
            };
        }

        public DataTable Table { get; }

        public LayerSpec CategoryBars { get; }

        public LayerSpec NumericBars { get; }

        public void Dispose()
        {
            _x.Dispose();
            _y.Dispose();
        }
    }
}
