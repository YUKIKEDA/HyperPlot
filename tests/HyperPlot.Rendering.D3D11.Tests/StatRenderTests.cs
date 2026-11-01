using HyperPlot.Testing;
using HyperPlot.Tests;

namespace HyperPlot.Rendering.D3D11.Tests;

/// <summary>Row G5b's stats drawn on WARP (design §10.1 scenes 25 to 29), with the fake text backend.</summary>
public sealed class StatRenderTests
{
    private const int Width = 640;
    private const int Height = 360;
    private static readonly string[] Groups = ["north", "south", "west"];

    /// <summary>A stacked histogram (§10.1 scene 25, row G5b): 3 groups of seeded values near normal, each centered apart.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 3,000 values in 3 groups, the sum of 4 uniform values shifted by the group; the default bins; the fake text
    ///   backend; Theme.Light; axes and a title.
    ///
    /// Steps:
    /// - Render at 640 × 360; compare with <c>Golden/stat-histogram.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void Histogram_MatchesBaseline()
    {
        var random = new Random(25);
        var rows = Enumerable
            .Range(0, 3_000)
            .Select(i => (Value: ((i % 3) * 1.5) + Enumerable.Range(0, 4).Sum(_ => random.NextDouble()), Group: i % 3))
            .ToArray();
        using var pool = new PlotWorkerPool(2);
        using var x = Column.Wrap(rows.Select(r => r.Value).ToArray());
        var table = DataTable.Create().Add("x", x).Add("g", Column.Categorical(rows.Select(r => (string?)Groups[r.Group]).ToArray(), Groups)).Build();
        var spec = Plot.Create(table)
            .Histogram(table.Col<double>("x"), configure: b => b.ColorBy(table.Cat("g")))
            .ScaleX(Scale.Linear().WithTitle("Value"))
            .ScaleY(Scale.Linear().WithTitle("Count"))
            .Title("Stacked histogram")
            .Build();
        Check(spec, pool, "stat-histogram.png");
    }

    /// <summary>A histogram of densities on a log x (§10.1 scene 26, row G5b): values near log-normal, bins equal in log10.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 2,000 values 10 to the power of a sum of 4 uniform values; 20 bins at most; densities; a log x; the fake text
    ///   backend; Theme.Light.
    ///
    /// Steps:
    /// - Render at 640 × 360; compare with <c>Golden/stat-histogram-log.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void HistogramLog_MatchesBaseline()
    {
        var random = new Random(26);
        var values = Enumerable.Range(0, 2_000).Select(_ => Math.Pow(10, Enumerable.Range(0, 4).Sum(_ => random.NextDouble()))).ToArray();
        using var pool = new PlotWorkerPool(2);
        using var x = Column.Wrap(values);
        var table = DataTable.Create().Add("x", x).Build();
        var spec = Plot.Create(table)
            .Histogram(table.Col<double>("x"), Stat.Bin(maxBins: 20, output: StatOutput.Density))
            .ScaleX(Scale.Log().WithTitle("Size"))
            .ScaleY(Scale.Linear().WithTitle("Density"))
            .Title("Densities on a log x")
            .Build();
        Check(spec, pool, "stat-histogram-log.png");
    }

    /// <summary>Counts stacked by group (§10.1 scene 27, row G5b): 6 categories, a group missing from one.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 600 seeded rows over 6 categories and 3 groups, none of "south" in the fourth category; the fake text backend;
    ///   Theme.Light.
    ///
    /// Steps:
    /// - Render at 640 × 360; compare with <c>Golden/stat-count.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void Count_MatchesBaseline()
    {
        var random = new Random(27);
        string[] cities = [.. Enumerable.Range(1, 6).Select(c => $"c{c}")];
        var rows = Enumerable
            .Range(0, 600)
            .Select(_ => (City: random.Next(0, 6), Group: random.Next(0, 3)))
            .Where(r => !(r.City == 3 && r.Group == 1))
            .ToArray();
        using var pool = new PlotWorkerPool(2);
        var table = DataTable
            .Create()
            .Add("city", Column.Categorical(rows.Select(r => (string?)cities[r.City]).ToArray(), cities))
            .Add("g", Column.Categorical(rows.Select(r => (string?)Groups[r.Group]).ToArray(), Groups))
            .Build();
        var spec = Plot.Create(table)
            .Count(table.Cat("city"), b => b.ColorBy(table.Cat("g")))
            .ScaleX(Scale.Band().WithTitle("City"))
            .ScaleY(Scale.Linear().WithTitle("Rows"))
            .Title("Counts")
            .Build();
        Check(spec, pool, "stat-count.png");
    }

    /// <summary>Means as bars with the interquartile range as error bars (§10.1 scene 28, row G5b): 6 categories.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 1,200 seeded values over 6 categories, each category's spread different; the fake text backend; Theme.Light.
    ///
    /// Steps:
    /// - Render at 640 × 360; compare with <c>Golden/stat-summary.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void Summary_MatchesBaseline()
    {
        var random = new Random(28);
        string[] sites = [.. Enumerable.Range(1, 6).Select(c => $"s{c}")];
        var rows = Enumerable
            .Range(0, 1_200)
            .Select(i => (Site: i % 6, Value: 5 + (i % 6) + ((random.NextDouble() - 0.5) * (1 + (i % 6)))))
            .ToArray();
        using var pool = new PlotWorkerPool(2);
        using var y = Column.Wrap(rows.Select(r => r.Value).ToArray());
        var table = DataTable.Create().Add("site", Column.Categorical(rows.Select(r => (string?)sites[r.Site]).ToArray(), sites)).Add("y", y).Build();
        var spec = Plot.Create(table)
            .Summary(table.Cat("site"), table.Col<double>("y"))
            .SummaryRange(
                table.Cat("site"),
                table.Col<double>("y"),
                SummaryOp.Quantile(0.25),
                SummaryOp.Quantile(0.75),
                configure: e => e.Color(Colors.Black).Width(1.5f)
            )
            .ScaleX(Scale.Band().WithTitle("Site"))
            .ScaleY(Scale.Linear().WithTitle("Value"))
            .Title("Means and quartiles")
            .Build();
        Check(spec, pool, "stat-summary.png");
    }

    /// <summary>A histogram of instants (§10.1 scene 29, row G5b): two years of .NET ticks binned on calendar quarters.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 1,500 seeded instants from 2020-01-15 to 2021-12-20, more of them in the summers; a time scale; at most 10 bins; the
    ///   fake text backend; Theme.Light.
    ///
    /// Steps:
    /// - Render at 640 × 360; compare with <c>Golden/stat-histogram-time.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void HistogramTime_MatchesBaseline()
    {
        var random = new Random(29);
        var (start, end) = (new DateTime(2020, 1, 15).Ticks, new DateTime(2021, 12, 20).Ticks);
        var ticks = Enumerable
            .Range(0, 1_500)
            .Select(_ =>
            {
                var t = random.NextDouble();
                var summer = 0.5 - (0.5 * Math.Cos(t * 4 * Math.PI));
                return start + (long)((end - start) * (random.NextDouble() < 0.5 ? t : Math.Clamp(t + (0.05 * summer), 0, 1)));
            })
            .Order()
            .ToArray();
        using var pool = new PlotWorkerPool(2);
        using var x = Column.Wrap(ticks);
        var table = DataTable.Create().Add("t", x).Build();
        var spec = Plot.Create(table)
            .Histogram(table.Col<long>("t"))
            .ScaleX(Scale.Time().WithTitle("Date"))
            .ScaleY(Scale.Linear().WithTitle("Events"))
            .Title("Events by quarter")
            .Build();
        Check(spec, pool, "stat-histogram-time.png");
    }

    /// <summary>Density lines by group (§10.1 scene 30, row G5c): 3 seeded groups, their peaks and spreads apart, one of two peaks.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 1,500 values in 3 groups; the default bandwidth and points; the fake text backend; Theme.Light; axes and a title.
    ///
    /// Steps:
    /// - Render at 640 × 360; compare with <c>Golden/stat-density.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void Density_MatchesBaseline()
    {
        var (values, groups) = DensityGroups(30);
        using var pool = new PlotWorkerPool(2);
        using var x = Column.Wrap(values);
        var table = DataTable.Create().Add("x", x).Add("g", Column.Categorical(groups, Groups)).Build();
        var spec = Plot.Create(table)
            .Density(table.Col<double>("x"), configure: b => b.ColorBy(table.Cat("g")).Width(2))
            .ScaleX(Scale.Linear().WithTitle("Value"))
            .ScaleY(Scale.Linear().WithTitle("Density"))
            .Title("Densities")
            .Build();
        Check(spec, pool, "stat-density.png");
    }

    /// <summary>Stacked density areas of counts (§10.1 scene 31, row G5c): the same 3 groups as scene 30.</summary>
    /// <remarks>
    /// Preconditions:
    /// - The groups of scene 30; areas of counts filled by group, stacked by default; the fake text backend; Theme.Light.
    ///
    /// Steps:
    /// - Render at 640 × 360; compare with <c>Golden/stat-density-stack.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void DensityStack_MatchesBaseline()
    {
        var (values, groups) = DensityGroups(30);
        using var pool = new PlotWorkerPool(2);
        using var x = Column.Wrap(values);
        var table = DataTable.Create().Add("x", x).Add("g", Column.Categorical(groups, Groups)).Build();
        var spec = new PlotSpec
        {
            Data = table,
            Layers =
            [
                new AreaLayerSpec
                {
                    X = table.Col<double>("x"),
                    Stat = Stat.Density(output: StatOutput.Count),
                    FillBy = table.Cat("g").ToColumnRef(),
                },
            ],
            ScaleX = Scale.Linear().WithTitle("Value"),
            ScaleY = Scale.Linear().WithTitle("Count"),
            Title = "Stacked densities",
            Theme = Theme.Light,
        };
        Check(spec, pool, "stat-density-stack.png");
    }

    /// <summary>
    /// Moving averages over noisy series (§10.1 scene 32, row G5c): the raw lines in gray, the averages over them, one
    /// series with a gap of NaN values where its average breaks.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 2 seeded series of 600 points over one x column, sines with noise, the second NaN from x 40 to 55; windows of 6 at
    ///   200 points; the fake text backend; Theme.Light.
    ///
    /// Steps:
    /// - Render at 640 × 360; compare with <c>Golden/stat-smooth.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void Smooth_MatchesBaseline()
    {
        var random = new Random(32);
        var xs = Enumerable.Range(0, 600).Select(i => i / 6.0).ToArray();
        double Noisy(double x, int series) => Math.Sin(x / (8 + (4 * series))) + (series * 2) + ((random.NextDouble() - 0.5) * 1.5);
        var first = xs.Select(x => Noisy(x, 0)).ToArray();
        var second = xs.Select(x => x is > 40 and < 55 ? double.NaN : Noisy(x, 1)).ToArray();
        using var pool = new PlotWorkerPool(2);
        using var x = Column.Wrap(xs);
        using var y1 = Column.Wrap(first);
        using var y2 = Column.Wrap(second);
        var table = DataTable.Create().Add("x", x).Add("first", y1).Add("second", y2).Build();
        var spec = Plot.Create(table)
            .Line(table.Col<double>("x"), table.Col<double>("first"), b => b.Color(Colors.Gray))
            .Line(table.Col<double>("x"), table.Col<double>("second"), b => b.Color(Colors.Gray))
            .Smooth(table.Col<double>("x"), table.Col<double>("first"), 6, 200, b => b.Width(2).Label("first"))
            .Smooth(table.Col<double>("x"), table.Col<double>("second"), 6, 200, b => b.Width(2).Label("second"))
            .ScaleX(Scale.Linear().WithTitle("Time"))
            .ScaleY(Scale.Linear().WithTitle("Value"))
            .Title("Moving averages")
            .Build();
        Check(spec, pool, "stat-smooth.png");
    }

    /// <summary>2D bins (§10.1 scene 33, row G5c): two seeded correlated clusters in 40 × 30 bins, viridis with a color bar.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 20,000 values of x and y in two clusters; at most 40 x and 30 y bins; viridis; the fake text backend; Theme.Light.
    ///
    /// Steps:
    /// - Render at 640 × 360; compare with <c>Golden/stat-bin2d.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void Bin2D_MatchesBaseline()
    {
        var random = new Random(33);
        var points = Enumerable
            .Range(0, 20_000)
            .Select(i =>
            {
                var a = Enumerable.Range(0, 4).Sum(_ => random.NextDouble()) - 2;
                var b = Enumerable.Range(0, 4).Sum(_ => random.NextDouble()) - 2;
                return i % 3 == 0 ? (X: 6 + a, Y: 2 + (0.5 * a) + (0.4 * b)) : (X: 2 + (1.5 * a), Y: 4 - a + (0.6 * b));
            })
            .ToArray();
        using var pool = new PlotWorkerPool(2);
        using var x = Column.Wrap(points.Select(p => p.X).ToArray());
        using var y = Column.Wrap(points.Select(p => p.Y).ToArray());
        var table = DataTable.Create().Add("x", x).Add("y", y).Build();
        var spec = Plot.Create(table)
            .Bin2D(table.Col<double>("x"), table.Col<double>("y"), Stat.Bin2D(Stat.Bin(maxBins: 40), Stat.Bin(maxBins: 30)))
            .ScaleX(Scale.Linear().WithTitle("x"))
            .ScaleY(Scale.Linear().WithTitle("y"))
            .ScaleColor(Scale.Sequential(ColorMaps.Viridis, Scale.Linear().WithTitle("Count")))
            .Title("2D bins")
            .Build();
        Check(spec, pool, "stat-bin2d.png");
    }

    private static (double[] Values, string?[] Groups) DensityGroups(int seed)
    {
        var random = new Random(seed);
        var rows = Enumerable
            .Range(0, 1_500)
            .Select(i =>
            {
                var s = Enumerable.Range(0, 4).Sum(_ => random.NextDouble()) - 2;
                var g = i % 3;
                var v = g switch
                {
                    0 => 2 + (0.8 * s),
                    1 => 5 + (1.6 * s),
                    _ => i % 2 == 0 ? 7 + (0.5 * s) : 9.5 + (0.5 * s),
                };
                return (V: v, G: g);
            })
            .ToArray();
        return ([.. rows.Select(r => r.V)], [.. rows.Select(r => (string?)Groups[r.G])]);
    }

    private static void Check(PlotSpec spec, PlotWorkerPool pool, string baseline)
    {
        using var backend = D3D11Backend.CreateForTest(useWarp: true, createDevice: null, new TextEngine(new FakeTextBackend()));
        using var renderer = new HeadlessRenderer(backend, Width, Height, pool);
        renderer.Render(spec);

        GoldenImage.AssertMatches(renderer, GoldenImageTests.Baseline(baseline), GoldenImageTests.Tolerance, GoldenImageTests.Output);
    }
}
