using HyperPlot.Rendering;
using static HyperPlot.Tests.GeomTesting;

namespace HyperPlot.Tests;

/// <summary>The stats of row G5b: their checks, bins, counts, summaries, quantiles, IStat, positions, learning, and the diff (design §10 G5b row).</summary>
public sealed class StatTests
{
    private static readonly string[] Cats = ["c0", "c1", "c2", "c3"];
    private static readonly string[] Groups = ["a", "b", "c"];

    /// <summary>The factories refuse values out of range, and the builders references to no column (design §4.1).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A table of a categorical and a numeric column, and another table.
    ///
    /// Steps:
    /// - Make bins and quantiles of each value out of range, a custom summary of null, an error bar summary of a null end;
    ///   read Count twice; add each builder's layer of a default reference and of another table's column.
    ///
    /// Expected:
    /// - ArgumentOutOfRangeException, ArgumentNullException, the same instance, Median equal to Quantile(0.5), and
    ///   ArgumentException for the references.
    /// </remarks>
    [Fact]
    public void Builders_CheckTheirArguments()
    {
        Action[] outOfRange =
        [
            () => Stat.Bin(0),
            () => Stat.Bin(10_001),
            () => Stat.Bin(step: 0),
            () => Stat.Bin(step: -1),
            () => Stat.Bin(step: double.NaN),
            () => Stat.Bin(step: double.PositiveInfinity),
            () => Stat.Bin(anchor: double.NaN),
            () => Stat.Bin(anchor: double.NegativeInfinity),
            () => Stat.Bin(output: (StatOutput)5),
            () => SummaryOp.Quantile(-0.01),
            () => SummaryOp.Quantile(1.01),
            () => SummaryOp.Quantile(double.NaN),
        ];
        foreach (var bad in outOfRange)
        {
            _ = Assert.Throws<ArgumentOutOfRangeException>(bad);
        }

        _ = Assert.Throws<ArgumentNullException>(() => SummaryOp.Custom(null!));
        _ = Assert.Throws<ArgumentNullException>(() => Stat.Summary(null!, SummaryOp.Max));
        _ = Assert.Throws<ArgumentNullException>(() => Stat.Summary(SummaryOp.Min, null!));
        Assert.Same(Stat.Count, Stat.Count);
        Assert.Equal(SummaryOp.Quantile(0.5), SummaryOp.Median);
        Assert.Null(Stat.Summary().Value);

        using var y = Column.Wrap(new double[] { 1, 2 });
        var t = DataTable.Create().Add("c", Column.Categorical(["a", "b"])).Add("y", y).Build();
        var other = DataTable.Create().Add("c", Column.Categorical(["a", "b"])).Add("y", Column.Wrap(new double[] { 1, 2 })).Build();
        Action[] wrongReferences =
        [
            () => Plot.Create(t).Histogram(other.Col<double>("y")),
            () => Plot.Create(t).Histogram(default(ColumnRef<double>)),
            () => Plot.Create(t).Count(other.Cat("c")),
            () => Plot.Create(t).Summary(t.Cat("c"), other.Col<double>("y")),
            () => Plot.Create(t).SummaryRange(other.Cat("c"), t.Col<double>("y")),
        ];
        foreach (var bad in wrongReferences)
        {
            _ = Assert.Throws<ArgumentException>(bad);
        }
    }

    /// <summary>
    /// The compile refuses each stat a geom does not take, and the channels and values a stat does not take (design §4.1,
    /// §4.8): bins off bars, areas, and steps over a numeric x; counts and summaries off bars (and error bars) over categories;
    /// a Y with bins and counts; a summary's YMax other than its YMin; a layer with no stat and no Y; a numeric color column;
    /// bins out of range; a binned bar's Width, and a binned bar's or area's Baseline other than 0; a custom state out of
    /// range; an unknown stat; too many bins, cells, or rects.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The fixture's table; tables of 32,768 and 32,769 categories, and of 256 groups over 512 and 513 bins of a step of 1.
    ///
    /// Steps:
    /// - Render each refused spec; render the specs at the limits.
    ///
    /// Expected:
    /// - ArgumentException naming the layer for each refused one; the ones at the limits are drawn.
    /// </remarks>
    [Fact]
    public void Compile_RefusesWhatAStatDoesNotTake()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = Data.Create();
        var t = data.Table;
        var (cx, y, v, g, n) = (
            t.Cat("cx").ToColumnRef(),
            t.Col<double>("y").ToColumnRef(),
            t.Col<double>("v").ToColumnRef(),
            t.Cat("g").ToColumnRef(),
            t.Col<double>("n").ToColumnRef()
        );
        using var raster = Column.Wrap(new float[4]);
        var refused = new List<LayerSpec>();
        foreach (var stat in (StatSpec[])[Stat.Bin(), Stat.Count, Stat.Summary()])
        {
            refused.Add(
                new LineLayerSpec
                {
                    X = n,
                    Y = n,
                    Stat = stat,
                }
            );
            refused.Add(
                new BandLayerSpec
                {
                    X = n,
                    YMin = n,
                    YMax = n,
                    Stat = stat,
                }
            );
            refused.Add(
                new RectLayerSpec
                {
                    XMin = n,
                    XMax = n,
                    YMin = n,
                    YMax = n,
                    Stat = stat,
                }
            );
            refused.Add(new RuleLayerSpec { X = n, Stat = stat });
            refused.Add(
                new SegmentLayerSpec
                {
                    X = n,
                    Y = n,
                    XEnd = n,
                    YEnd = n,
                    Stat = stat,
                }
            );
            refused.Add(
                new TextLayerSpec
                {
                    X = n,
                    Y = n,
                    Text = n,
                    Stat = stat,
                }
            );
            refused.Add(new ImageLayerSpec { Raster = Raster.Of(raster, 2), Stat = stat });
            refused.Add(new CustomLayerSpec(new NoGeom()) { Stat = stat });
        }

        refused.AddRange([
            new BarLayerSpec { X = cx, Stat = Stat.Bin() },
            new BarLayerSpec { X = n, Stat = Stat.Count },
            new BarLayerSpec
            {
                X = n,
                Y = y,
                Stat = Stat.Summary(),
            },
            new ErrorBarLayerSpec
            {
                X = n,
                YMin = y,
                YMax = y,
                Stat = Stat.Summary(SummaryOp.Min, SummaryOp.Max),
            },
            new BarLayerSpec
            {
                X = n,
                Y = n,
                Stat = Stat.Bin(),
            },
            new BarLayerSpec
            {
                X = cx,
                Y = y,
                Stat = Stat.Count,
            },
            new ErrorBarLayerSpec
            {
                X = cx,
                YMin = y,
                YMax = v,
                Stat = Stat.Summary(SummaryOp.Min, SummaryOp.Max),
            },
            new BarLayerSpec { X = n },
            new BarLayerSpec
            {
                X = n,
                Stat = Stat.Bin(),
                ColorBy = v,
            },
            new BarLayerSpec { X = n, Stat = Stat.Bin() with { MaxBins = 0 } },
            new BarLayerSpec { X = n, Stat = Stat.Bin() with { Step = -1 } },
            new BarLayerSpec
            {
                X = n,
                Stat = Stat.Bin(),
                Width = 1,
            },
            new BarLayerSpec
            {
                X = n,
                Stat = Stat.Bin(),
                Baseline = 1,
            },
            new AreaLayerSpec
            {
                X = n,
                Stat = Stat.Bin(),
                Baseline = 1,
            },
            new BarLayerSpec
            {
                X = cx,
                Y = y,
                Stat = Stat.Summary(SummaryOp.Custom(new Sized(0))),
            },
            new BarLayerSpec
            {
                X = cx,
                Y = y,
                Stat = Stat.Summary(SummaryOp.Custom(new Sized(65))),
            },
            new BarLayerSpec { X = n, Stat = new UnknownStat() },
            new BarLayerSpec { X = n, Stat = Stat.Bin(step: 1e-3) },
        ]);
        foreach (var layer in refused)
        {
            using var plan = TestPlans.Create(pool, Size, Size);
            var categorical = layer is BarLayerSpec { X.Column: CategoricalColumn } or ErrorBarLayerSpec { X.Column: CategoricalColumn };
            var spec = Spec(t, layer) with { ScaleX = categorical ? Scale.Band() : Scale.Linear().WithDomain(0, Size) };
            var thrown = Assert.Throws<ArgumentException>(() => plan.Render(spec));
            Assert.Contains("layer 0", thrown.Message, StringComparison.OrdinalIgnoreCase);
        }

        // A time x bins a column of longs only (the time scale's check, row G2).
        using (var plan = TestPlans.Create(pool, Size, Size))
        {
            _ = Assert.Throws<ArgumentException>(() =>
                plan.Render(Spec(t, new BarLayerSpec { X = n, Stat = Stat.Bin() }) with { ScaleX = Scale.Time() })
            );
        }

        // Quantiles: two take 4 ranks of 2 KB, so 32,768 cells fill 256 MB; 32,769 do not fit.
        foreach (var (categories, fits) in (ReadOnlySpan<(int, bool)>)[(32_768, true), (32_769, false)])
        {
            string[] names = [.. Enumerable.Range(0, categories).Select(i => $"x{i}")];
            var wide = DataTable.Create().Add("x", Column.Categorical(["x0", "x1"], names)).Add("y", Column.Wrap(new double[] { 1, 2 })).Build();
            using var plan = TestPlans.Create(pool, Size, Size);
            var spec = Spec(
                wide,
                new ErrorBarLayerSpec
                {
                    X = wide.Cat("x").ToColumnRef(),
                    YMin = wide.Col<double>("y"),
                    YMax = wide.Col<double>("y"),
                    Stat = Stat.Summary(SummaryOp.Quantile(0.25), SummaryOp.Quantile(0.75)),
                }
            ) with
            {
                ScaleX = Scale.Band(),
            };
            Check(fits, () => plan.Render(spec));
        }

        // Binned bars: 256 groups × 512 bins are 131,072 rects; 513 bins are one too many.
        string[] groups = [.. Enumerable.Range(0, 256).Select(i => $"g{i}")];
        foreach (var (top, fits) in (ReadOnlySpan<(double, bool)>)[(511.5, true), (512.5, false)])
        {
            var many = DataTable.Create().Add("x", Column.Wrap(new double[] { 0, top })).Add("g", Column.Categorical(["g0", "g1"], groups)).Build();
            using var plan = TestPlans.Create(pool, Size, Size);
            Check(
                fits,
                () =>
                    plan.Render(
                        Spec(
                            many,
                            new BarLayerSpec
                            {
                                X = many.Col<double>("x"),
                                Stat = Stat.Bin(step: 1),
                                ColorBy = many.Cat("g").ToColumnRef(),
                            }
                        ) with
                        {
                            ScaleX = Scale.Linear(),
                        }
                    )
            );
        }

        static void Check(bool fits, Action render)
        {
            if (fits)
            {
                render();
            }
            else
            {
                _ = Assert.Throws<ArgumentException>(render);
            }
        }
    }

    /// <summary>
    /// The bins' edges are Vega's (design §4.1): the values vega-statistics' bin returns for the same extent and maxbins, a
    /// step given, and a single value.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Six cases and the start, stop, and step that vega-statistics 1.9.0's bin returned for them (run with Node).
    ///
    /// Steps:
    /// - Choose the edges.
    ///
    /// Expected:
    /// - The same start, stop, and step, within 1e-12.
    /// </remarks>
    [Fact]
    public void BinEdges_AreVegas()
    {
        (double Min, double Max, int MaxBins, double? Step, double Start, double Stop, double Width)[] cases =
        [
            (0, 97, 10, null, 0, 100, 10),
            (0.013, 0.0871, 10, null, 0.01, 0.09, 0.01),
            (-3, 3, 4, null, -4, 4, 2),
            (5, 5, 10, null, 5, 5.5, 0.5),
            (0, 97, 10, 7, 0, 98, 7),
            (1, 1000, 30, null, 0, 1000, 50),
        ];
        foreach (var (min, max, maxBins, step, start, stop, width) in cases)
        {
            var edges = StatWork.Edges(min, max, Stat.Bin(maxBins, step), 0);
            Assert.Equal(start, edges[0], 12);
            Assert.Equal(stop, edges[^1], 12);
            Assert.Equal(width, edges[1] - edges[0], 12);
        }

        // An anchor moves the edges by whole steps' remainder: through 3, a step of 10 starts at −7 for [0, 97].
        var anchored = StatWork.Edges(0, 97, Stat.Bin(step: 10, anchor: 3), 0);
        Assert.Equal(-7, anchored[0], 12);
        Assert.True(anchored[^1] >= 97);
    }

    /// <summary>
    /// A bin holds [a, b), the last [a, b] (design §4.1); NaN, ±∞, and rows of no group are not counted; the groups share the
    /// edges; a density is the count over the group's n and the bin's width.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Values on the edges 0, 10, 20, 30, the greatest on the last edge, NaN, ±∞, and a row of no group, in 2 groups.
    ///
    /// Steps:
    /// - Count with edges 0 to 30 by 10; count the same rows by hand.
    ///
    /// Expected:
    /// - An edge's value in the bin to its right, 30 in the last bin, the others not counted, each group over the same edges.
    /// </remarks>
    [Fact]
    public void Bin_CountsClosedOnTheLeftAndTheLastOnBoth()
    {
        using var pool = new PlotWorkerPool(2);
        double[] xs = [0, 5, 10, 10, 19.999, 20, 30, double.NaN, double.PositiveInfinity, double.NegativeInfinity, 15, 25];
        string?[] gs = ["a", "a", "a", "b", "b", "b", "a", "a", "a", "b", null, "b"];
        using var x = Column.Wrap(xs);
        var groups = Column.Categorical(gs, ["a", "b"]);
        var counts = StatWork.Bin(x, groups, null, 0, [0, 10, 20, 30], uniform: true, pool);

        // a: 0, 5 | 10 | 30 → [2, 1, 1]; b: 10, 19.999 | 20, 25 → [0, 2, 2].
        Assert.Equal([2, 1, 1, 0, 2, 2], counts);
        var calendar = StatWork.Bin(x, groups, null, 0, [0, 10, 20, 30], uniform: false, pool);
        Assert.Equal(counts, calendar);

        // Values on the edges of steps like 0.1, where the division by the step and the edges round apart by an ulp: each is in
        // the bin to its right, as counted directly against the edges (the last bin closed).
        foreach (
            var (low, high, step) in (ReadOnlySpan<(double, double, double)>)[(0, 1, 0.1), (0.13, 5.9, 0.3), (-2.1, 3.3, 0.7), (0.001, 0.0871, 0.007)]
        )
        {
            var edges = StatWork.Edges(low, high, Stat.Bin(step: step), 0);
            using var onEdges = Column.Wrap(edges.ToArray());
            var byEdges = StatWork.Bin(onEdges, null, null, 0, edges, uniform: true, pool);
            for (var i = 0; i < byEdges.Length; i++)
            {
                var expected = edges.Count(v => v >= edges[i] && (i == byEdges.Length - 1 ? v <= edges[i + 1] : v < edges[i + 1]));
                Assert.Equal(expected, byEdges[i]);
            }
        }
    }

    /// <summary>
    /// A histogram of densities on a log x (design §4.1): equal widths of log10, densities over the log width, so the bars'
    /// areas in the scale's space sum to 1; and the x domain runs from the first edge to the last.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 1,000 values from 1 to 10^4 spread over the decades; a log x; no legend (one group).
    ///
    /// Steps:
    /// - Render a histogram of densities; read the rects and the x domain.
    ///
    /// Expected:
    /// - Edges at whole powers of 10 apart equally in log10, the areas summing to 1, the domain at the first and last edges.
    /// </remarks>
    [Fact]
    public void Histogram_OfDensities_OnALogX()
    {
        using var pool = new PlotWorkerPool(1);
        var values = Enumerable.Range(0, 1_000).Select(i => Math.Pow(10, 4.0 * i / 999)).ToArray();
        using var x = Column.Wrap(values);
        var t = DataTable.Create().Add("x", x).Build();
        using var plan = TestPlans.Create(pool, Size, Size);
        _ = plan.Render(
            Spec(t, new BarLayerSpec { X = t.Col<double>("x"), Stat = Stat.Bin(maxBins: 4, output: StatOutput.Density) }) with
            {
                ScaleX = Scale.Log(),
                ScaleY = Scale.Linear(),
            }
        );
        var layer = Assert.IsType<StatPlan>(plan.Layers[0]);
        var (low, high) = Ends(plan.XDomain);
        Assert.Equal(0, low, 9);
        Assert.Equal(4, high, 9);

        // In log10, the 4 bins are 1 wide; each holds a quarter of the values, at a density of 0.25 / 1.
        var (yLow, yHigh) = Ends(plan.YDomain);
        Assert.Equal(0, yLow, 9);
        Assert.InRange(yHigh, 0.249, 0.252);
        _ = layer;
    }

    /// <summary>
    /// A time x is binned on calendar boundaries (design §4.1): over two years at most 10 bins, quarters, whose widths differ.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - .NET ticks from 2020-01-15 to 2021-12-20; a time scale of ticks.
    ///
    /// Steps:
    /// - Choose the edges with at most 10 bins.
    ///
    /// Expected:
    /// - The first day of each quarter from 2020-01-01 to 2022-01-01, 8 bins of 90 to 92 days; with the greatest value on
    ///   2022-01-01, the same 8 bins.
    /// </remarks>
    [Fact]
    public void Bin_OnATimeX_TakesCalendarBoundaries()
    {
        var (min, max) = (new DateTime(2020, 1, 15).Ticks, new DateTime(2021, 12, 20).Ticks);
        var edges = TimeTicks.BinEdges(min, max, Scale.Time(), 10);
        DateTime[] quarters = [.. Enumerable.Range(0, 9).Select(q => new DateTime(2020, 1, 1).AddMonths(3 * q))];
        Assert.Equal(quarters.Select(d => d.Ticks), edges);
        var days = edges.Zip(edges.Skip(1), (a, b) => (b - a) / TimeSpan.TicksPerDay).Distinct().Order().ToArray();
        Assert.Equal([90, 91, 92], days);

        // A greatest value on a boundary is in the last bin, closed on both sides: no extra bin past it.
        var onBoundary = TimeTicks.BinEdges(min, new DateTime(2022, 1, 1).Ticks, Scale.Time(), 10);
        Assert.Equal(new DateTime(2022, 1, 1).Ticks, onBoundary[^1]);
        Assert.Equal(9, onBoundary.Length);
    }

    /// <summary>
    /// A binned area or step puts its points at the bins' middles, and its x domain spans the bins' edges (design §4.2,
    /// §5.5.3); a column with ±∞ is binned over its finite values (§4.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Values 1 to 29 and ±∞; bins of a step of 10 over [0, 30].
    ///
    /// Steps:
    /// - Render an area and a step of the bins with learned domains; read the x domain and the step's polyline.
    ///
    /// Expected:
    /// - The x domain [0, 30]; the step's points at x 5, 15, 25 (mapped to pixels); the y domain from 0 to the greatest count.
    /// </remarks>
    [Fact]
    public void BinnedAreaAndStep_PutPointsAtTheMiddles()
    {
        using var pool = new PlotWorkerPool(1);
        var values = Enumerable.Range(1, 29).Select(i => (double)i).Append(double.PositiveInfinity).Append(double.NegativeInfinity).ToArray();
        using var x = Column.Wrap(values);
        var t = DataTable.Create().Add("x", x).Build();
        LayerSpec[] layers =
        [
            new AreaLayerSpec { X = t.Col<double>("x"), Stat = Stat.Bin(step: 10) },
            new StepLayerSpec { X = t.Col<double>("x"), Stat = Stat.Bin(step: 10) },
        ];
        foreach (var layer in layers)
        {
            using var plan = TestPlans.Create(pool, Size, Size);
            var list = plan.Render(Spec(t, layer) with { ScaleX = Scale.Linear(), ScaleY = Scale.Linear() });
            Assert.Equal((0.0, 30.0), Ends(plan.XDomain));
            Assert.Equal((0.0, 10.0), Ends(plan.YDomain));
            if (layer is AreaLayerSpec)
            {
                // Split in two groups, the bins' counts stack: the y domain reaches the greatest total, 10.
                var halves = DataTable
                    .Create()
                    .Add("x", x)
                    .Add("g", Column.Categorical([.. values.Select((_, i) => (string?)(i % 2 == 0 ? "a" : "b"))]))
                    .Build();
                using var split = TestPlans.Create(pool, Size, Size);
                _ = split.Render(
                    Spec(
                        halves,
                        new AreaLayerSpec
                        {
                            X = halves.Col<double>("x"),
                            Stat = Stat.Bin(step: 10),
                            FillBy = halves.Cat("g").ToColumnRef(),
                        }
                    ) with
                    {
                        ScaleX = Scale.Linear(),
                        ScaleY = Scale.Linear(),
                    }
                );
                Assert.Equal((0.0, 10.0), Ends(split.YDomain));
            }

            if (layer is StepLayerSpec)
            {
                var xs = SegmentsOf(list, 0).SelectMany(s => (float[])[s.Start.X, s.End.X]).Distinct().Order().ToArray();
                Assert.Equal([Size * 5f / 30, Size * 15f / 30, Size * 25f / 30], xs, (a, b) => Math.Abs(a - b) < 1e-3);
            }
        }
    }

    /// <summary>
    /// A drawn summary is type 7 between the selected ranks, and a density divides by the bin's width (design §4.1): the
    /// learned y domain is the values the plan draws.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - One category of the values 1 to 10; the values 1 to 29 in bins of a step of 10 (counts 9, 10, 10 of 29).
    ///
    /// Steps:
    /// - Render an error bar of the quartiles and a histogram of densities with learned y domains.
    ///
    /// Expected:
    /// - The y domain [3.25, 7.75] (h = 2.25 and 6.75 of the sorted values); [0, 10 / (29 · 10)].
    /// </remarks>
    [Fact]
    public void DrawnValues_AreType7AndDensities()
    {
        using var pool = new PlotWorkerPool(1);
        using var ys = Column.Wrap(Enumerable.Range(1, 10).Select(i => (double)i).ToArray());
        var t = DataTable.Create().Add("c", Column.Categorical([.. Enumerable.Repeat<string?>("only", 10)])).Add("y", ys).Build();
        using (var plan = TestPlans.Create(pool, Size, Size))
        {
            _ = plan.Render(
                Spec(
                    t,
                    new ErrorBarLayerSpec
                    {
                        X = t.Cat("c").ToColumnRef(),
                        YMin = t.Col<double>("y"),
                        YMax = t.Col<double>("y"),
                        Stat = Stat.Summary(SummaryOp.Quantile(0.25), SummaryOp.Quantile(0.75)),
                    }
                ) with
                {
                    ScaleX = Scale.Band(),
                    ScaleY = Scale.Linear(),
                }
            );
            Assert.Equal((3.25, 7.75), Ends(plan.YDomain));
        }

        using var xs = Column.Wrap(Enumerable.Range(1, 29).Select(i => (double)i).ToArray());
        var b = DataTable.Create().Add("x", xs).Build();
        using (var plan = TestPlans.Create(pool, Size, Size))
        {
            _ = plan.Render(
                Spec(b, new BarLayerSpec { X = b.Col<double>("x"), Stat = Stat.Bin(step: 10, output: StatOutput.Density) }) with
                {
                    ScaleX = Scale.Linear(),
                    ScaleY = Scale.Linear(),
                }
            );
            Assert.Equal(0, Ends(plan.YDomain).Min);
            Assert.Equal(10.0 / (29 * 10), Ends(plan.YDomain).Max, 12);
        }
    }

    /// <summary>
    /// A cell's sums do not depend on the number of workers (design §5.5.3): the chunks join in fixed groups, in their order.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 3 · 2^20 rows (3 chunks) over 4 categories, values of many magnitudes.
    ///
    /// Steps:
    /// - Summarize with 1 worker and with 4, three times.
    ///
    /// Expected:
    /// - The same sums, bit for bit.
    /// </remarks>
    [Fact]
    public void Summary_SumsAlikeOnAnyNumberOfWorkers()
    {
        const int Rows = 3 << 20;
        var random = new Random(12);
        var values = new double[Rows];
        for (var i = 0; i < Rows; i++)
        {
            values[i] = random.NextDouble() * Math.Pow(10, random.Next(-6, 6));
        }

        using var y = Column.Wrap(values);
        var x = Column.Categorical(Enumerable.Range(0, Rows).Select(i => (string?)Cats[i % 4]).ToArray(), Cats);
        using var one = new PlotWorkerPool(1);
        var expected = StatWork.Summarize(x, null, y, [], one).Sum;
        foreach (var workers in (int[])[4, 4, 4])
        {
            using var pool = new PlotWorkerPool(workers);
            Assert.Equal(expected, StatWork.Summarize(x, null, y, [], pool).Sum);
        }
    }

    /// <summary>A count is the rows of each cell (design §4.1): rows of no category or group are not counted.</summary>
    /// <remarks>
    /// Preconditions:
    /// - The fixture's rows over 4 categories and 3 groups, with missing categories and groups.
    ///
    /// Steps:
    /// - Count by the workers; count the rows by hand.
    ///
    /// Expected:
    /// - The same counts, unsplit and split.
    /// </remarks>
    [Fact]
    public void Count_IsTheRowsOfEachCell()
    {
        using var pool = new PlotWorkerPool(2);
        using var data = Data.Create();
        var x = (CategoricalColumn)data.Table.Cat("cx").ToColumnRef().Column;
        var g = (CategoricalColumn)data.Table.Cat("g").ToColumnRef().Column;
        var split = StatWork.Count(x, g, pool);
        var whole = StatWork.Count(x, null, pool);
        for (var k = 0; k < Groups.Length; k++)
        {
            for (var c = 0; c < Cats.Length; c++)
            {
                Assert.Equal(data.Rows.Count(r => r.X == c && r.G == k), split[(k * Cats.Length) + c]);
            }
        }

        for (var c = 0; c < Cats.Length; c++)
        {
            Assert.Equal(data.Rows.Count(r => r.X == c), whole[c]);
        }
    }

    /// <summary>
    /// Summaries match the CPU's (design §4.1): mean, min, max; quantiles at p = 0, 0.25, 0.5, 0.9, 1 equal the type 7 of the
    /// sorted values bit for bit, on columns of each element type (each a different number of radix passes); and the results
    /// do not change with the number of workers.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 3,000 rows over 4 categories with duplicates, negatives, −0, NaN, and a category of one value; the same values as
    ///   float, double, long, int, and byte (rounded to each type).
    ///
    /// Steps:
    /// - Summarize and select with 1 and 4 workers; sort each category's values and take type 7.
    ///
    /// Expected:
    /// - The same mean, min, max, and quantiles, bit for bit, and the same with either number of workers.
    /// </remarks>
    [Fact]
    public void Summary_MatchesTheSortedValues()
    {
        var random = new Random(8);
        const int Rows = 3_000;
        var codes = Enumerable.Range(0, Rows).Select(i => i == 0 ? 3 : random.Next(0, 3)).ToArray();
        var raw = Enumerable
            .Range(0, Rows)
            .Select(i =>
                i > 0 && i % 97 == 0 ? double.NaN
                : i % 13 == 0 ? -0.0
                : Math.Round((random.NextDouble() * 200) - 50)
            )
            .ToArray();
        var x = Column.Categorical(codes.Select(c => (string?)Cats[c]).ToArray(), Cats);
        double[] ps = [0, 0.25, 0.5, 0.9, 1];
        using var one = new PlotWorkerPool(1);
        using var four = new PlotWorkerPool(4);
        var columns = new (Column Column, Func<double, double> Round)[]
        {
            (Column.Wrap(raw), v => v),
            (Column.Wrap(raw.Select(v => (float)v).ToArray()), v => (float)v),
            (Column.Wrap(raw.Select(v => double.IsNaN(v) ? 0L : (long)v).ToArray()), v => double.IsNaN(v) ? 0 : (long)v),
            (Column.Wrap(raw.Select(v => double.IsNaN(v) ? 0 : (int)v).ToArray()), v => double.IsNaN(v) ? 0 : (int)v),
            (
                Column.Wrap(raw.Select(v => double.IsNaN(v) ? (byte)0 : (byte)Math.Clamp(v + 50, 0, 255)).ToArray()),
                v => double.IsNaN(v) ? 0 : (byte)Math.Clamp(v + 50, 0, 255)
            ),
        };
        foreach (var (column, round) in columns)
        {
            var moments = StatWork.Summarize(x, null, column, [], one);
            var ranks = StatWork.Select(x, null, column, moments.Count, ps, one);
            Assert.Equal(ranks, StatWork.Select(x, null, column, moments.Count, ps, four));
            Assert.Equal(moments.Sum, StatWork.Summarize(x, null, column, [], four).Sum);
            for (var c = 0; c < Cats.Length; c++)
            {
                var values = Enumerable
                    .Range(0, Rows)
                    .Where(i => codes[i] == c)
                    .Select(i => round(raw[i]))
                    .Where(v => !double.IsNaN(v))
                    .Order()
                    .ToArray();
                Assert.Equal(values.Length, moments.Count[c]);
                Assert.Equal(values.Min(), moments.Min[c]);
                Assert.Equal(values.Max(), moments.Max[c]);
                for (var q = 0; q < ps.Length; q++)
                {
                    var h = (values.Length - 1) * ps[q];
                    var (low, high) = (values[(int)Math.Floor(h)], values[Math.Min((int)Math.Floor(h) + 1, values.Length - 1)]);
                    var expected = low + ((h - Math.Floor(h)) * (high - low));
                    var at = c * 2 * ps.Length;
                    var (selectedLow, selectedHigh) = (ranks[at + (2 * q)], ranks[at + (2 * q) + 1]);
                    Assert.Equal(low, selectedLow);
                    Assert.Equal(high, selectedHigh);
                    Assert.Equal(expected, selectedLow + ((h - Math.Floor(h)) * (selectedHigh - selectedLow)));
                }
            }

            (column as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// A custom summary folds and merges a user's state (design §4.1), the same on any number of workers, and its exception
    /// comes out of the render.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The fixture's rows; a sum of squares; a stat that throws.
    ///
    /// Steps:
    /// - Render a bar of the sum of squares; render one of the throwing stat.
    ///
    /// Expected:
    /// - Bars at each category's sum of squares (the learned y top the greatest); the stat's exception from Render.
    /// </remarks>
    [Fact]
    public void CustomSummary_FoldsAndMerges()
    {
        using var pool = new PlotWorkerPool(4);
        using var data = Data.Create();
        var t = data.Table;
        using var plan = TestPlans.Create(pool, Size, Size);
        _ = plan.Render(
            Spec(
                t,
                new BarLayerSpec
                {
                    X = t.Cat("cx").ToColumnRef(),
                    Y = t.Col<double>("y"),
                    Stat = Stat.Summary(SummaryOp.Custom(new SumOfSquares())),
                }
            ) with
            {
                ScaleX = Scale.Band(),
                ScaleY = Scale.Linear(),
            }
        );
        var greatest = Enumerable.Range(0, Cats.Length).Max(c => data.Rows.Where(r => r.X == c && !double.IsNaN(r.Y)).Sum(r => r.Y * r.Y));
        Assert.Equal(greatest, Ends(plan.YDomain).Max, 9);

        using var failing = TestPlans.Create(pool, Size, Size);
        var thrown = Assert.ThrowsAny<Exception>(() =>
            failing.Render(
                Spec(
                    t,
                    new BarLayerSpec
                    {
                        X = t.Cat("cx").ToColumnRef(),
                        Y = t.Col<double>("y"),
                        Stat = Stat.Summary(SummaryOp.Custom(new Throwing())),
                    }
                ) with
                {
                    ScaleX = Scale.Band(),
                }
            )
        );
        Assert.Contains("stat failed", thrown.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A binned bar's pieces take the layer's position in the stat (design §5.5.3): stacked from the last group, filled to 1,
    /// dodged in the bin's width; the count bars stack by the categorical bar's path; no rect for a count of 0.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Values in 3 bins of width 10 in 2 groups, one group empty in the middle bin.
    ///
    /// Steps:
    /// - Render a histogram split by group, stacked, filled, and dodged; read the derived rects' columns.
    ///
    /// Expected:
    /// - Pieces from the CPU's cumulative counts; filled to [0, 1]; dodged halves of each bin; 5 rects, not 6.
    /// </remarks>
    [Fact]
    public void BinnedBars_TakeThePositionInTheStat()
    {
        using var pool = new PlotWorkerPool(1);
        double[] xs = [1, 2, 3, 11, 21, 22, 4, 24, 25, 26];
        string?[] gs = ["a", "a", "a", "a", "a", "a", "b", "b", "b", "b"];
        using var x = Column.Wrap(xs);
        var t = DataTable.Create().Add("x", x).Add("g", Column.Categorical(gs, ["a", "b"])).Build();
        PlotSpec Of(PositionSpec position) =>
            Spec(
                t,
                new BarLayerSpec
                {
                    X = t.Col<double>("x"),
                    Stat = Stat.Bin(step: 10),
                    ColorBy = t.Cat("g").ToColumnRef(),
                    Position = position,
                }
            ) with
            {
                ScaleX = Scale.Linear(),
                ScaleY = Scale.Linear(),
            };

        // Bins [0, 10), [10, 20), [20, 30]: a = 3, 1, 2; b = 1, 0, 3.
        using (var plan = TestPlans.Create(pool, Size, Size))
        {
            _ = plan.Render(Of(Position.Stack));
            Assert.Equal((0.0, 5.0), Ends(plan.YDomain));
        }

        using (var plan = TestPlans.Create(pool, Size, Size))
        {
            _ = plan.Render(Of(Position.Fill));
            Assert.Equal((0.0, 1.0), Ends(plan.YDomain));
        }

        using (var plan = TestPlans.Create(pool, Size, Size))
        {
            _ = plan.Render(Of(Position.Dodge()));
            Assert.Equal((0.0, 3.0), Ends(plan.YDomain));
        }

        using (var plan = TestPlans.Create(pool, Size, Size))
        {
            var list = plan.Render(Of(Position.Identity));
            var dataRects = list.Rects.Length - 1 - 2;
            Assert.Equal(5, dataRects);
        }
    }

    /// <summary>
    /// The diff (design §4.8): another stat's values make the layer again, a new node of the same values does not, and a new
    /// version of a column makes it again.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A histogram of the fixture's numbers.
    ///
    /// Steps:
    /// - Render; render a new node of the same bins; render other bins; invalidate the column and render; render other x
    ///   and y domains; render a log x.
    ///
    /// Expected:
    /// - The same layer plan, then a new one twice; the same over other domains; a new one over a log x.
    /// </remarks>
    [Fact]
    public void Diff_MakesTheLayerAgainForOtherStats()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = Data.Create();
        var t = data.Table;
        var layer = new BarLayerSpec { X = t.Col<double>("n"), Stat = Stat.Bin(maxBins: 5) };
        using var plan = TestPlans.Create(pool, Size, Size);
        _ = plan.Render(Spec(t, layer));
        var first = plan.Layers[0];
        _ = plan.Render(Spec(t, layer with { Stat = Stat.Bin(maxBins: 5) }));
        Assert.Same(first, plan.Layers[0]);
        _ = plan.Render(Spec(t, layer with { Stat = Stat.Bin(maxBins: 6) }));
        Assert.NotSame(first, plan.Layers[0]);
        var second = plan.Layers[0];
        data.SetN(0, 99);
        _ = plan.Render(Spec(t, layer with { Stat = Stat.Bin(maxBins: 6) }));
        Assert.NotSame(second, plan.Layers[0]);

        // Another y domain, or x domain, keeps the bins; another x space (log) makes them again.
        var third = plan.Layers[0];
        _ = plan.Render(
            Spec(t, layer with { Stat = Stat.Bin(maxBins: 6) }) with
            {
                ScaleY = Scale.Linear().WithDomain(0, 7),
                ScaleX = Scale.Linear().WithDomain(1, 50),
            }
        );
        Assert.Same(third, plan.Layers[0]);
        _ = plan.Render(Spec(t, layer with { Stat = Stat.Bin(maxBins: 6) }) with { ScaleX = Scale.Log() });
        Assert.NotSame(third, plan.Layers[0]);
    }

    /// <summary>Once warm, a histogram, counts, and summaries drawn again allocate nothing (design §4.8).</summary>
    /// <remarks>
    /// Preconditions:
    /// - The fixture's histogram, split counts, and a summary error bar of quartiles; each spec and a new node of it in turn.
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
            Spec(t, new BarLayerSpec { X = t.Col<double>("n"), Stat = Stat.Bin() }),
            Spec(
                t,
                new BarLayerSpec
                {
                    X = cx,
                    Stat = Stat.Count,
                    ColorBy = g,
                }
            ) with
            {
                ScaleX = Scale.Band(),
            },
            Spec(
                t,
                new ErrorBarLayerSpec
                {
                    X = cx,
                    YMin = t.Col<double>("y"),
                    YMax = t.Col<double>("y"),
                    Stat = Stat.Summary(SummaryOp.Quantile(0.25), SummaryOp.Quantile(0.75)),
                }
            ) with
            {
                ScaleX = Scale.Band(),
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

    private sealed class SumOfSquares : IStat
    {
        public int StateLength => 1;

        public void Fold(ReadOnlySpan<double> values, Span<double> state)
        {
            foreach (var v in values)
            {
                state[0] += v * v;
            }
        }

        public void Merge(ReadOnlySpan<double> other, Span<double> state) => state[0] += other[0];

        public double Result(ReadOnlySpan<double> state) => state[0];
    }

    private sealed class Throwing : IStat
    {
        public int StateLength => 1;

        public void Fold(ReadOnlySpan<double> values, Span<double> state) => throw new InvalidOperationException("The stat failed.");

        public void Merge(ReadOnlySpan<double> other, Span<double> state) { }

        public double Result(ReadOnlySpan<double> state) => 0;
    }

    private sealed class Sized(int length) : IStat
    {
        public int StateLength => length;

        public void Fold(ReadOnlySpan<double> values, Span<double> state) { }

        public void Merge(ReadOnlySpan<double> other, Span<double> state) { }

        public double Result(ReadOnlySpan<double> state) => 0;
    }

    private sealed record UnknownStat : StatSpec
    {
        public UnknownStat()
            : base(Stat.Count) { }
    }

    private sealed class NoGeom : IGeom
    {
        public void Learn(GeomRanges ranges) { }

        public void Record(GeomFrame frame, RenderList list) { }
    }

    /// <summary>The fixture: 15 rows over 4 categories ("cx") and 3 groups ("g"), values "y" and "v", and numbers "n" from 1 to 15.</summary>
    private sealed class Data : IDisposable
    {
        private readonly double[] _n;
        private readonly Column<double> _y;
        private readonly Column<double> _v;
        private readonly Column<double> _nColumn;

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
            Rows = rows;
            _y = Column.Wrap(rows.Select(r => r.Y).ToArray());
            _v = Column.Wrap(rows.Select((r, i) => (double)i).ToArray());
            _n = [.. Enumerable.Range(1, 15).Select(i => (double)i)];
            _nColumn = Column.Wrap(_n);
            Table = DataTable
                .Create()
                .Add("cx", Column.Categorical(rows.Select(r => r.X < 0 ? null : Cats[r.X]).ToArray(), Cats))
                .Add("g", Column.Categorical(rows.Select(r => r.G < 0 ? null : Groups[r.G]).ToArray(), Groups))
                .Add("y", _y)
                .Add("v", _v)
                .Add("n", _nColumn)
                .Build();
        }

        public (int X, int G, double Y)[] Rows { get; }

        public DataTable Table { get; }

        public static Data Create() => new();

        public void SetN(int row, double value)
        {
            _n[row] = value;
            _nColumn.Invalidate();
        }

        public void Dispose()
        {
            _y.Dispose();
            _v.Dispose();
            _nColumn.Dispose();
        }
    }
}
