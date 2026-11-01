using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>
/// The facets of row G6b (design §4.5, §4.8, §5.5.3, §10's G6b row): the spec, the checks, the panels and their rows, the
/// shared and free domains, the layers over a run against the same layers over gathered rows, the diff, and the
/// allocations, with the fake text backend.
/// </summary>
public sealed class FacetTests : IDisposable
{
    private const int Width = 640;
    private const int Height = 360;
    private const int Regions = 5;
    private const int Times = 60;
    private static readonly string[] RegionNames = ["North", "South", "East", "West", "Center"];
    private static readonly string[] ChannelNames = ["Online", "Store"];
    private readonly PlotWorkerPool _pool = new(2);
    private readonly List<IDisposable> _owned = [];

    // The same rows twice: in runs of their region (each region's rows in turn), and mixed (the regions in turn at each
    // time). A region's rows keep their order in both, so that a mixed panel's gathered table is its run.
    private readonly DataTable _runs;
    private readonly DataTable _mixed;

    // The runs with rows of no region between them, of values far outside the others'.
    private readonly DataTable _missing;

    public FacetTests()
    {
        _runs = Table(RunOrder(), missing: false);
        _mixed = Table(MixedOrder(), missing: false);
        _missing = Table(RunOrder(), missing: true);
    }

    public void Dispose()
    {
        foreach (var owned in _owned)
        {
            owned.Dispose();
        }

        _pool.Dispose();
    }

    /// <summary>The factories and the builders check their arguments and make the facet (design §4.1).</summary>
    /// <remarks>
    /// Preconditions:
    /// - The run table, and another table of the same columns.
    ///
    /// Steps:
    /// - Call Facet.Wrap and Facet.Grid with columns 0, scales out of range, both null, and default references; call the
    ///   builders with another table's columns; build a wrap and a grid.
    ///
    /// Expected:
    /// - ArgumentOutOfRangeException, ArgumentException; the built specs hold the facets with their members.
    /// </remarks>
    [Fact]
    public void Factories_CheckTheirArguments()
    {
        var region = _runs.Cat("region");
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Facet.Wrap(region, columns: 0));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Facet.Wrap(region, scales: (FacetScales)4));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Facet.Grid(region, scales: (FacetScales)(-1)));
        _ = Assert.Throws<ArgumentException>(() => Facet.Grid());
        _ = Assert.Throws<ArgumentException>(() => Facet.Wrap(default));
        _ = Assert.Throws<ArgumentException>(() => Facet.Grid(rows: default(CategoryRef)));
        _ = Assert.Throws<ArgumentException>(() => Plot.Create(_runs).FacetWrap(_mixed.Cat("region")));
        _ = Assert.Throws<ArgumentException>(() => Plot.Create(_runs).FacetGrid(columns: _mixed.Cat("channel")));
        _ = Assert.Throws<ArgumentException>(() => Plot.Create(_runs).FacetGrid());

        var wrap = Facet.Wrap(region, columns: 3, scales: FacetScales.FreeY);
        Assert.Equal((region.ToColumnRef(), 3, FacetScales.FreeY), (wrap.By, wrap.ColumnCount!.Value, wrap.Scales));
        var grid = Facet.Grid(rows: region, columns: _runs.Cat("channel"), scales: FacetScales.Free);
        Assert.Equal(_runs.Cat("channel").ToColumnRef(), grid.Columns);
        Assert.Equal(FacetScales.Fixed, Facet.Grid(columns: region).Scales);
        var built = Plot.Create(_runs).Line(_runs.Col<double>("t"), _runs.Col<float>("y")).FacetGrid(rows: region).Build();
        Assert.Equal(region.ToColumnRef(), ((FacetGridSpec)built.Facet!).Rows);
        Assert.Null(((FacetGridSpec)built.Facet!).Columns);
        Assert.Null(Plot.Create(_runs).Build().Facet);
    }

    /// <summary>
    /// The compile refuses a facet that does not go with the plot (design §4.5, §4.8): a numeric column, another table's
    /// column, a default reference, a ColumnCount below 1 or scales out of range set with <c>with</c>, a grid of no column,
    /// and more than 256 panels; 256 panels are drawn.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The run table; a table of 257 categories.
    ///
    /// Steps:
    /// - Render each spec.
    ///
    /// Expected:
    /// - ArgumentException each, naming what is wrong; the spec of 256 used categories renders.
    /// </remarks>
    [Fact]
    public void Compile_RefusesFacetsThatDoNotGoTogether()
    {
        var plain = Line(_runs);
        FacetSpec[] facets =
        [
            new FacetWrapSpec { By = _runs.Col<double>("t") },
            new FacetWrapSpec { By = _mixed.Cat("region").ToColumnRef() },
            new FacetWrapSpec { By = default },
            Facet.Wrap(_runs.Cat("region")) with
            {
                ColumnCount = 0,
            },
            Facet.Wrap(_runs.Cat("region")) with
            {
                Scales = (FacetScales)9,
            },
            new FacetGridSpec(),
            new FacetGridSpec { Rows = _runs.Col<float>("y") },
        ];
        foreach (var facet in facets)
        {
            using var plan = Plan();
            _ = Assert.Throws<ArgumentException>(() => plan.Render(plain with { Facet = facet }));
        }

        foreach (var (count, refused) in (ReadOnlySpan<(int, bool)>)[(257, true), (256, false)])
        {
            var names = Enumerable.Range(0, count).Select(i => $"c{i}").ToArray();
            using var y = Own(Column.Wrap(Enumerable.Range(0, count).Select(i => (float)i).ToArray()));
            var table = DataTable.Create().Add("t", Column.Uniform(0, 1, count)).Add("y", y).Add("k", Column.Categorical(names, names)).Build();
            var spec = Line(table) with { Facet = Facet.Wrap(table.Cat("k")) };
            using var plan = new PlotPlan(_pool, 4_000, 4_000, new TextEngine(new FakeTextBackend()), 1);
            if (refused)
            {
                var error = Assert.Throws<ArgumentException>(() => plan.Render(spec));
                Assert.Contains("257 panels", error.Message, StringComparison.Ordinal);
                Assert.Contains("256", error.Message, StringComparison.Ordinal);
            }
            else
            {
                _ = plan.Render(spec);
                Assert.Equal(256, plan.Panels.Count);
            }
        }
    }

    /// <summary>A wrap's rows and columns follow R's <c>n2mfrow</c> as ggplot2's <c>wrap_dims</c> takes it, or a given number of columns (design §4.5).</summary>
    /// <remarks>
    /// Preconditions:
    /// - None.
    ///
    /// Steps:
    /// - Ask the dimensions of 1 to 13 and 30 panels, and of 5 panels in 4 and in 1 columns.
    ///
    /// Expected:
    /// - (1, n) up to 3, (2, ⌈n / 2⌉) up to 6, (3, ⌈n / 3⌉) up to 12, ⌈√n⌉ columns and ⌈n / ⌈√n⌉⌉ rows beyond (13 in 4 × 4,
    ///   30 in 5 × 6); 5 in 4 columns takes 2 rows, in 1 column 5 rows.
    /// </remarks>
    [Fact]
    public void WrapDims_FollowN2mfrow()
    {
        (int Rows, int Columns)[] expected = [(1, 1), (1, 2), (1, 3), (2, 2), (2, 3), (2, 3), (3, 3), (3, 3), (3, 3), (3, 4), (3, 4), (3, 4), (4, 4)];
        for (var n = 1; n <= 13; n++)
        {
            Assert.Equal(expected[n - 1], FacetArrangement.WrapDims(n, null));
        }

        Assert.Equal((5, 6), FacetArrangement.WrapDims(30, null));
        Assert.Equal((2, 4), FacetArrangement.WrapDims(5, 4));
        Assert.Equal((5, 1), FacetArrangement.WrapDims(5, 1));
    }

    /// <summary>
    /// The panels follow the categories' numbers and leave out a category without rows (design §4.5): a wrap's, and a grid's
    /// rows and columns; a pair of a grid without rows is an empty panel; a missing value is in no panel; the pairs' runs; a
    /// new node of the same columns keeps the panels' rows.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A table of 7 rows: categories a, c (b unused) and x, y, z (y unused), one row missing a, and pair (c, z) without rows.
    ///
    /// Steps:
    /// - Render a wrap by the first column and a grid by both; render a new node of the grid's same columns.
    ///
    /// Expected:
    /// - The wrap's panels a and c; the grid's 2 × 2 cells of a and c by x and z, (c, z) empty; the rows each holds; the
    ///   pair (a, x) a run; the same facet data after the new node; a row category whose rows all miss the column category
    ///   left out of the grid.
    /// </remarks>
    [Fact]
    public void Panels_FollowTheCategoriesAndLeaveOutTheUnused()
    {
        string?[] first = ["a", "a", "c", null, "a", "c", "a"];
        string?[] second = ["x", "x", "x", "z", "z", "x", "z"];
        using var y = Own(Column.Wrap(new float[] { 1, 2, 3, 4, 5, 6, 7 }));
        var table = DataTable
            .Create()
            .Add("t", Column.Uniform(0, 1, 7))
            .Add("y", y)
            .Add("f", Column.Categorical(first, ["a", "b", "c"]))
            .Add("g", Column.Categorical(second, ["x", "y", "z"]))
            .Build();
        var spec = Line(table);

        using var wrap = Plan();
        _ = wrap.Render(spec with { Facet = Facet.Wrap(table.Cat("f")) });
        var cells = wrap.Facet!.Arrangement.Cells;
        Assert.Equal(["a", "c"], cells.Select(c => c.Strip));
        Assert.Equal((1, 2), (wrap.Facet.Arrangement.Rows, wrap.Facet.Arrangement.Columns));

        using var grid = Plan();
        var gridSpec = spec with { Facet = Facet.Grid(rows: table.Cat("f"), columns: table.Cat("g")) };
        _ = grid.Render(gridSpec);
        var arrangement = grid.Facet!.Arrangement;
        Assert.Equal(["a", "c"], arrangement.RowLabels);
        Assert.Equal(["x", "z"], arrangement.ColumnLabels);
        Assert.Equal([0, 1, 2, -1], arrangement.Cells.Select(c => c.Key));
        var panels = grid.Facet.Panels;
        Assert.True(panels[3].Empty);
        Assert.Equal(new RowRange(0, 2), panels[0].Rows);
        Assert.Null(panels[0].Map);

        // (a, z) holds rows 4 and 6 apart: gathered, without the missing row 3.
        Assert.NotNull(panels[1].Map);
        Assert.Equal([5f, 7f], RowsOf(panels[1].Data, "y"));
        Assert.Equal([3f, 6f], RowsOf(panels[2].Data, "y"));
        Assert.IsType<EmptyPlan>(grid.Panels[3].Layers[0]);

        var kept = grid.Facet;
        _ = grid.Render(gridSpec with { Facet = Facet.Grid(rows: table.Cat("f"), columns: table.Cat("g")) });
        Assert.Same(kept, grid.Facet);

        // "d" has rows only where g is missing: no pair, so no row of panels.
        using var three = Column.Wrap(new float[] { 1, 2, 3 });
        var lonely = DataTable
            .Create()
            .Add("t", Column.Uniform(0, 1, 3))
            .Add("y", three)
            .Add("f", Column.Categorical(["a", "d", "a"], ["a", "d"]))
            .Add("g", Column.Categorical(["x", null, "x"], ["x"]))
            .Build();
        using var dropped = Plan();
        _ = dropped.Render(Line(lonely) with { Facet = Facet.Grid(rows: lonely.Cat("f"), columns: lonely.Cat("g")) });
        Assert.Equal(["a"], dropped.Facet!.Arrangement.RowLabels);

        static float[] RowsOf(DataTable data, string name) => [.. ((Column<float>)data.Col<float>(name).Column).GetChunk(0).ToArray()];
    }

    /// <summary>
    /// A panel's M4 points are its category's own (design §5.5.3): a run's, read without copying, and a mixed category's
    /// gathered rows', as M4 of the category's rows alone over the panel's boundaries.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The run table and the mixed table; a line of y over t; a wrap by region with shared axes.
    ///
    /// Steps:
    /// - Render each; extract M4 of each region's rows alone with the panel's boundaries and origin.
    ///
    /// Expected:
    /// - Each panel's points equal those, in both tables; the mixed table's panels are gathered and the runs' are not.
    /// </remarks>
    [Fact]
    public async Task PanelPoints_AreTheCategorysOwn()
    {
        foreach (var (table, gathered) in new[] { (_runs, 0), (_mixed, Regions) })
        {
            using var plan = Plan();
            _ = plan.Render(Line(table) with { Facet = Facet.Wrap(table.Cat("region")) });
            Assert.Equal(gathered, plan.Facet!.GatheredCount);
            for (var p = 0; p < Regions; p++)
            {
                var panel = plan.Panels[p];
                var (t, y) = Region(p);
                using var x = Column.Wrap(t);
                using var values = Column.Wrap(y);
                using var pyramid = _pool.BuildPyramid(values);
                await pyramid.WhenComplete();
                await _pool.PrecomputeStats(x);
                var expected = new M4Point[M4.MaxPointCount(panel.Boundaries.Length - 1)];
                var count = M4.Extract(x, pyramid, new ScaleOrigin(panel.X.Origin, panel.Y.Origin), panel.Boundaries, expected).Count;
                Assert.Equal(Values(expected.AsSpan(0, count)), Values(((LinePlan)panel.Layers[0]).Points(panel.Batches[0])));
            }
        }
    }

    /// <summary>
    /// A shared direction learns one domain from every panel, a free one a panel each (design §4.5): Fixed, FreeX, FreeY, and
    /// Free; a given domain holds in every panel; a band x keeps all its categories whatever the scales.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The run table; a line of y over u, whose regions lie at different heights and places; a wrap by region.
    ///
    /// Steps:
    /// - Render with each of the four scales, with a y domain given under Free, and a count over a band x under FreeX.
    ///
    /// Expected:
    /// - The learned y of every panel is the union under Fixed and FreeX, and each region's own range under FreeY and Free;
    ///   x likewise; with nice, one rounded y in every panel under Fixed and each panel's own under FreeY; the given domain in
    ///   every panel; the band's five categories in every panel, and with a band x, a secondary x shared under FreeX.
    /// </remarks>
    [Fact]
    public void SharedAndFree_LearnTheirDomains()
    {
        var all = Enumerable.Range(0, Regions).SelectMany(r => Region(r).Y).ToArray();
        foreach (var scales in (ReadOnlySpan<FacetScales>)[FacetScales.Fixed, FacetScales.FreeX, FacetScales.FreeY, FacetScales.Free])
        {
            using var plan = Plan();
            var spec = Plot.Create(_runs).Line(_runs.Col<double>("u"), _runs.Col<float>("y")).Build();
            _ = plan.Render(spec with { Facet = Facet.Wrap(_runs.Cat("region"), scales: scales) });
            var (freeX, freeY) = (scales is FacetScales.FreeX or FacetScales.Free, scales is FacetScales.FreeY or FacetScales.Free);
            for (var p = 0; p < Regions; p++)
            {
                var own = Region(p).Y;
                Assert.Equal(freeY ? (own.Min(), own.Max()) : (all.Min(), all.Max()), Ends(plan.Panels[p].LearnedY));
                Assert.Equal(
                    freeX ? (p * 100.0, (p * 100.0) + Times - 1) : (0.0, ((Regions - 1) * 100.0) + Times - 1),
                    Ends(plan.Panels[p].LearnedX)
                );
            }
        }

        foreach (var free in (ReadOnlySpan<bool>)[false, true])
        {
            using var nice = Plan();
            _ = nice.Render(
                Plot.Create(_runs)
                    .Line(_runs.Col<double>("u"), _runs.Col<float>("y"))
                    .ScaleY(Scale.Linear() with { Nice = true })
                    .FacetWrap(_runs.Cat("region"), scales: free ? FacetScales.FreeY : FacetScales.Fixed)
                    .Build()
            );
            for (var p = 0; p < Regions; p++)
            {
                var own = Region(p).Y;
                var (low, high) = Ends(nice.Panels[p].Y);
                Assert.True(low <= (free ? own.Min() : all.Min()) && high >= (free ? own.Max() : all.Max()));
                Assert.True(p == 0 || free == (nice.Panels[p].Y != nice.Panels[0].Y));
                Assert.Equal(Math.Round(low), low);
            }
        }

        using var second = Plan();
        _ = second.Render(
            Plot.Create(_runs)
                .Count(_runs.Cat("channel"))
                .HRule(_runs.Col<double>("t"), l => l.SecondaryX())
                .ScaleX(Scale.Band())
                .ScaleX2(Scale.Linear().WithDomain(0, 10))
                .FacetWrap(_runs.Cat("region"), scales: FacetScales.FreeX)
                .Build()
        );
        Assert.False(second.Facet!.Arrangement.FreeXOf(Scale.Band()));
        Assert.All(Enumerable.Range(0, Regions), p => Assert.Same(second.Guides.PanelAt(0).X2, second.Guides.PanelAt(p).X2));

        using var given = Plan();
        _ = given.Render(
            Line(_runs) with
            {
                Facet = Facet.Wrap(_runs.Cat("region"), scales: FacetScales.Free),
                ScaleY = Scale.Linear().WithDomain(-100, 100),
            }
        );
        Assert.All(given.Panels, p => Assert.Equal((-100.0, 100.0), Ends(p.Y)));

        using var band = Plan();
        _ = band.Render(
            Plot.Create(_runs).Count(_runs.Cat("channel")).ScaleX(Scale.Band()).FacetWrap(_runs.Cat("region"), scales: FacetScales.FreeX).Build()
        );
        Assert.All(band.Panels, p => Assert.Equal(band.Panels[0].LearnedX, p.LearnedX));
    }

    /// <summary>
    /// Every kind of layer over a panel's run draws as over its rows gathered into a table of their own (design §4.5, §5.5.3):
    /// a line, a split line, stacked areas and bars over a numeric x, bins, counts, summaries, densities, moving averages, and
    /// 2D bins, under shared and free scales; and the same with rows of no region between the runs.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The run table, the mixed table (whose panels are gathered tables of the same rows), and the run table with rows of
    ///   no region between the runs.
    ///
    /// Steps:
    /// - Render each layer's wrap by region over the three tables, under Fixed and Free.
    ///
    /// Expected:
    /// - The three lists hold the same commands and records, the texts included.
    /// </remarks>
    [Fact]
    public void RunPanels_DrawAsTheirGatheredRows()
    {
        foreach (var scales in (ReadOnlySpan<FacetScales>)[FacetScales.Fixed, FacetScales.Free])
        {
            foreach (var make in Specs())
            {
                using var runs = Plan();
                using var mixed = Plan();
                using var missing = Plan();
                var expected = runs.Render(make(_runs) with { Facet = Facet.Wrap(_runs.Cat("region"), scales: scales) });
                AssertSame(expected, mixed.Render(make(_mixed) with { Facet = Facet.Wrap(_mixed.Cat("region"), scales: scales) }));
                AssertSame(expected, missing.Render(make(_missing) with { Facet = Facet.Wrap(_missing.Cat("region"), scales: scales) }));
            }
        }

        static IEnumerable<Func<DataTable, PlotSpec>> Specs()
        {
            yield return Line;
            yield return t => Plot.Create(t).Line(t.Col<double>("t"), t.Col<float>("y"), l => l.ColorBy(t.Cat("channel"))).Build();
            yield return t => Plot.Create(t).Area(t.Col<double>("t"), t.Col<float>("y"), configure: a => a.ColorBy(t.Cat("channel"))).Build();
            yield return t => Plot.Create(t).Bar(t.Col<double>("t"), t.Col<float>("y"), configure: b => b.ColorBy(t.Cat("channel"))).Build();
            yield return t => Plot.Create(t).Histogram(t.Col<float>("y"), configure: h => h.ColorBy(t.Cat("channel"))).Build();
            yield return t => Plot.Create(t).Count(t.Cat("channel")).ScaleX(Scale.Band()).Build();
            yield return t => Plot.Create(t).Summary(t.Cat("channel"), t.Col<float>("y"), SummaryOp.Quantile(0.5)).ScaleX(Scale.Band()).Build();
            yield return t => Plot.Create(t).Density(t.Col<float>("y"), configure: d => d.ColorBy(t.Cat("channel"))).Build();
            yield return t => Plot.Create(t).Smooth(t.Col<double>("t"), t.Col<float>("y"), 8).Build();
            yield return t => Plot.Create(t).Bin2D(t.Col<double>("t"), t.Col<float>("y")).Build();
        }
    }

    /// <summary>
    /// A stat's edges are the same in every panel along a shared direction, and a panel's own along a free one (design §4.5):
    /// the bins over the union of the panels' rows, or over each panel's run; the groups are the pairs of a panel and a color.
    /// A bar's x over a run reaches half a bar past the run's ends, its width from the run's spacing (Issue #243).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The run table; a histogram of y split by channel; then bars of y over t; a wrap by region.
    ///
    /// Steps:
    /// - Render the histogram under Fixed and FreeX, and the bars under FreeX; read each panel's derived table and domain.
    ///
    /// Expected:
    /// - Fixed: every panel's edges among Vega's edges of all the regions' y; FreeX: among those of the panel's own y; two
    ///   groups in a panel; the bars' x from the run's first t less half a bar to its last plus half, a bar 0.9 times the
    ///   run's mean spacing.
    /// </remarks>
    [Fact]
    public void Stats_ShareTheirEdgesAlongASharedDirection()
    {
        var all = Enumerable.Range(0, Regions).SelectMany(r => Region(r).Y).ToArray();
        var bin = Stat.Bin();
        foreach (var free in (ReadOnlySpan<bool>)[false, true])
        {
            using var plan = Plan();
            _ = plan.Render(
                Plot.Create(_runs)
                    .Histogram(_runs.Col<float>("y"), configure: h => h.ColorBy(_runs.Cat("channel")))
                    .FacetWrap(_runs.Cat("region"), scales: free ? FacetScales.FreeX : FacetScales.Fixed)
                    .Build()
            );
            for (var p = 0; p < Regions; p++)
            {
                var own = Region(p).Y;
                var edges = free ? StatWork.Edges(own.Min(), own.Max(), bin, 0) : StatWork.Edges(all.Min(), all.Max(), bin, 0);
                var derived = ((StatPlan)plan.Panels[p].Layers[0]).Derived;
                var lefts = ((Column<double>)derived.GetColumn(0)).GetChunk(0).ToArray();
                Assert.All(lefts, left => Assert.Contains(left, edges));
                Assert.Equal([0, 1], ((CategoricalColumn)derived.GetColumn(derived.ColumnCount - 1)).GetCodes(0).ToArray().Distinct().Order());
            }
        }

        using var bars = Plan();
        _ = bars.Render(
            Plot.Create(_runs).Bar(_runs.Col<double>("t"), _runs.Col<float>("y")).FacetWrap(_runs.Cat("region"), scales: FacetScales.FreeX).Build()
        );
        var half = (Times - 1.0) / ((Times * 2) - 1) * 0.9 / 2;
        foreach (var panel in bars.Panels)
        {
            GeomTesting.Close(-half, Ends(panel.LearnedX).Low);
            GeomTesting.Close(Times - 1 + half, Ends(panel.LearnedX).High);
        }
    }

    /// <summary>
    /// Runs that cross chunks read their own rows only (design §5.5.3): the statistics of the chunks a run covers and its rows
    /// in the chunks at its ends, for its range, its X's check and search, and a stat's passes.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 3 runs of 1,200,000 rows (chunks of 1,048,576), x restarting at 0 in each run, y a run's own wave at its own height.
    ///
    /// Steps:
    /// - Render a line under a wrap, Free; and a histogram under the wrap, FreeX.
    ///
    /// Expected:
    /// - Each panel's learned x and y its run's own; each panel's bins count its run's rows.
    /// </remarks>
    [Fact]
    public void Runs_AcrossChunks_ReadTheirOwnRows()
    {
        const int Run = 1_200_000;
        string[] names = ["a", "b", "c"];
        var (t, y) = (new double[3 * Run], new float[3 * Run]);
        for (var i = 0; i < t.Length; i++)
        {
            var (run, at) = (i / Run, i % Run);
            (t[i], y[i]) = (at, (float)((run * 10) + Math.Sin(at / 1000.0)));
        }

        using var tColumn = Column.Wrap(t);
        using var yColumn = Column.Wrap(y);
        var table = DataTable
            .Create()
            .Add("t", tColumn)
            .Add("y", yColumn)
            .Add("k", Column.Categorical([.. Enumerable.Range(0, 3 * Run).Select(i => (string?)names[i / Run])], names))
            .Build();
        using var plan = Plan();
        _ = plan.Render(Line(table) with { Facet = Facet.Wrap(table.Cat("k"), scales: FacetScales.Free) });
        for (var p = 0; p < 3; p++)
        {
            var own = y.AsSpan(p * Run, Run).ToArray();
            Assert.Equal((0.0, Run - 1.0), Ends(plan.Panels[p].LearnedX));
            Assert.Equal((own.Min(), own.Max()), Ends(plan.Panels[p].LearnedY));
        }

        using var bins = Plan();
        _ = bins.Render(Plot.Create(table).Histogram(table.Col<float>("y")).FacetWrap(table.Cat("k"), scales: FacetScales.FreeX).Build());
        foreach (var panel in bins.Panels)
        {
            var derived = ((StatPlan)panel.Layers[0]).Derived;
            var highs = ((Column<double>)derived.GetColumn(3)).GetChunk(0).ToArray();
            Assert.Equal(Run, highs.Sum());
        }
    }

    /// <summary>
    /// A run's ranges are read once a version of its columns (design §5.5.3, Issue #243): learning again over a new domain
    /// reads nothing; a new version of the y column reads it again.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A copy of the run table's y; a line over it; a wrap by region under Free.
    ///
    /// Steps:
    /// - Render; render with a new x domain; invalidate y and render.
    ///
    /// Expected:
    /// - Each panel's line reads x and y once (2), still 2 after the new domain, and 3 after the new version.
    /// </remarks>
    [Fact]
    public void RunRanges_AreReadOnceAVersion()
    {
        var values = Enumerable.Range(0, Regions).SelectMany(r => Region(r).Y).ToArray();
        using var y = Column.Wrap(values);
        var table = DataTable.Create().Add("t", Own(RunTimes())).Add("y", y).Add("region", RegionColumn(RunOrder())).Build();
        var spec = Plot.Create(table)
            .Line(table.Col<double>("t"), table.Col<float>("y"))
            .FacetWrap(table.Cat("region"), scales: FacetScales.Free)
            .Build();
        using var plan = Plan();
        _ = plan.Render(spec);
        Assert.All(plan.Panels, p => Assert.Equal(2, p.Layers[0].RunRangeReads));
        _ = plan.Render(spec with { ScaleY = Scale.Linear().WithDomain(0, 50) });
        _ = plan.Render(spec);
        Assert.All(plan.Panels, p => Assert.Equal(2, p.Layers[0].RunRangeReads));
        y.Invalidate();
        _ = plan.Render(spec);
        Assert.All(plan.Panels, p => Assert.Equal(3, p.Layers[0].RunRangeReads));
    }

    /// <summary>
    /// The diff of a facet (design §4.8): its kind or columns make the panels again (the data stage), its scales learn and
    /// extract again (the scale stage), its number of columns lays out again, and a new node of the same values does nothing.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The run table; a line; a wrap by region.
    ///
    /// Steps:
    /// - Render, then a new node of the same facet, new scales, a new number of columns, and a grid by region's rows.
    ///
    /// Expected:
    /// - The same facet data and no new layout for the same values; new scales extract again with the same facet data; new
    ///   columns lay out again with the same facet data in 1 row; the grid makes new facet data.
    /// </remarks>
    [Fact]
    public void Diff_TakesItsStage()
    {
        var spec = Line(_runs) with { Facet = Facet.Wrap(_runs.Cat("region")) };
        using var plan = Plan();
        _ = plan.Render(spec);
        var (data, layouts, extractions) = (plan.Facet, plan.Layouts, plan.Extractions);
        _ = plan.Render(spec with { Facet = Facet.Wrap(_runs.Cat("region")) });
        Assert.Same(data, plan.Facet);
        Assert.Equal((layouts, extractions), (plan.Layouts, plan.Extractions));

        _ = plan.Render(spec with { Facet = Facet.Wrap(_runs.Cat("region"), scales: FacetScales.FreeY) });
        Assert.Same(data, plan.Facet);
        Assert.Equal(extractions + 1, plan.Extractions);
        Assert.True(plan.Facet!.Arrangement.FreeY);

        _ = plan.Render(spec with { Facet = Facet.Wrap(_runs.Cat("region"), columns: 5, scales: FacetScales.FreeY) });
        Assert.Same(data, plan.Facet);
        Assert.Equal(layouts + 2, plan.Layouts);
        Assert.Equal((1, 5), (plan.Facet!.Arrangement.Rows, plan.Facet.Arrangement.Columns));

        _ = plan.Render(spec with { Facet = Facet.Grid(rows: _runs.Cat("region")) });
        Assert.NotSame(data, plan.Facet);
        Assert.Equal((Regions, 1), (plan.Facet!.Arrangement.Rows, plan.Facet.Arrangement.Columns));
    }

    /// <summary>
    /// Once warm, a wrap and a grid drawn again over two y domains in turn allocate nothing (design §4.8), their specs made
    /// anew each frame as a host makes them: a new facet node of the same values does nothing.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The mixed table (gathered panels); a split line under a wrap of 3 columns, and a histogram under a grid of free y;
    ///   four specs each, every node new.
    ///
    /// Steps:
    /// - Render the first two a few times, then the four ten times in turn.
    ///
    /// Expected:
    /// - 0 bytes on this thread.
    /// </remarks>
    [Fact]
    public void Warm_AllocatesNothing()
    {
        // A host that makes its spec each frame: every node new, of the same values but the y domain.
        PlotSpec Wrap(double high) =>
            Plot.Create(_mixed)
                .Line(_mixed.Col<double>("t"), _mixed.Col<float>("y"), l => l.ColorBy(_mixed.Cat("channel")))
                .ScaleY(Scale.Linear().WithDomain(0, high))
                .FacetWrap(_mixed.Cat("region"), columns: 3)
                .Build();
        PlotSpec Grid(double high) =>
            Plot.Create(_mixed)
                .Histogram(_mixed.Col<float>("y"))
                .ScaleY(Scale.Linear().WithDomain(-5, high))
                .FacetGrid(rows: _mixed.Cat("channel"), columns: _mixed.Cat("region"), scales: FacetScales.FreeY)
                .Build();
        foreach (var make in new Func<double, PlotSpec>[] { Wrap, Grid })
        {
            var specs = new[] { make(40), make(60), make(40), make(60) };
            using var plan = Plan();
            for (var i = 0; i < 6; i++)
            {
                _ = plan.Render(specs[i % 2]);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10; i++)
            {
                _ = plan.Render(specs[i % 4]);
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
    }

    private static (double X, double Y, M4Roles Roles)[] Values(ReadOnlySpan<M4Point> points) =>
        [.. points.ToArray().Select(p => (p.X, p.Y, p.Roles))];

    private static PlotSpec Line(DataTable table) => Plot.Create(table).Line(table.Col<double>("t"), table.Col<float>("y")).Build();

    private static (double Low, double High) Ends(ScaleRange domain) => (domain.Origin + domain.Lower, domain.Origin + domain.Upper);

    private static void AssertSame(RenderList expected, RenderList actual)
    {
        GeomTesting.AssertSameList(expected, actual);
        Assert.Equal(expected.Texts.ToArray().Select(t => (t.Text, t.Position)), actual.Texts.ToArray().Select(t => (t.Text, t.Position)));
    }

    /// <summary>Returns a region's times and values, in their order: 60 times, the two channels at each.</summary>
    private static (double[] T, float[] Y) Region(int region)
    {
        var (t, y) = (new double[Times * 2], new float[Times * 2]);
        for (var i = 0; i < Times * 2; i++)
        {
            (t[i], y[i]) = (i / 2, Value(region, i / 2, i % 2));
        }

        return (t, y);
    }

    /// <summary>Returns a row's value: a wave a region and channel, the regions at different heights.</summary>
    private static float Value(int region, int time, int channel) =>
        (float)((region * 6) + (channel * 2) + (3 * Math.Sin((time / (5.0 + region)) + channel)));

    /// <summary>Returns the rows in runs of their region: (region, time, channel) a row.</summary>
    private static (int Region, int Time, int Channel)[] RunOrder() =>
        [.. Enumerable.Range(0, Regions).SelectMany(r => Enumerable.Range(0, Times * 2).Select(i => (r, i / 2, i % 2)))];

    /// <summary>Returns the rows mixed: at each time and channel, the regions in turn.</summary>
    private static (int Region, int Time, int Channel)[] MixedOrder() =>
        [.. Enumerable.Range(0, Times * 2).SelectMany(i => Enumerable.Range(0, Regions).Select(r => (r, i / 2, i % 2)))];

    private static CategoricalColumn RegionColumn((int Region, int Time, int Channel)[] rows) =>
        Column.Categorical([.. rows.Select(r => (string?)RegionNames[r.Region])], RegionNames);

    private static Column<double> RunTimes() => Column.Wrap(RunOrder().Select(r => (double)r.Time).ToArray());

    /// <summary>
    /// Makes a table of the rows in an order: t, u (t past 100 times the region, so that the regions' x differ), y, the region,
    /// and the channel; with rows of no region between the runs.
    /// </summary>
    private DataTable Table((int Region, int Time, int Channel)[] rows, bool missing)
    {
        var list = new List<(int Region, int Time, int Channel)>();
        foreach (var row in rows)
        {
            // A row of no region before each region's first.
            if (missing && row.Time == 0 && row.Channel == 0)
            {
                list.Add((-1, 0, 0));
            }

            list.Add(row);
        }

        var t = Own(Column.Wrap(list.Select(r => r.Region < 0 ? 1e6 : r.Time).ToArray()));
        var u = Own(Column.Wrap(list.Select(r => r.Region < 0 ? 1e6 : (r.Region * 100.0) + r.Time).ToArray()));
        var y = Own(Column.Wrap(list.Select(r => r.Region < 0 ? 1e6f : Value(r.Region, r.Time, r.Channel)).ToArray()));
        return DataTable
            .Create()
            .Add("t", t)
            .Add("u", u)
            .Add("y", y)
            .Add("region", Column.Categorical([.. list.Select(r => r.Region < 0 ? null : RegionNames[r.Region])], RegionNames))
            .Add("channel", Column.Categorical([.. list.Select(r => (string?)ChannelNames[r.Channel])], ChannelNames))
            .Build();
    }

    private T Own<T>(T column)
        where T : IDisposable
    {
        _owned.Add(column);
        return column;
    }

    private PlotPlan Plan() => new(_pool, Width, Height, new TextEngine(new FakeTextBackend()), 1);
}
