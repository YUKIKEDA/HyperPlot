using System.Numerics;
using HyperPlot.Interaction;

namespace HyperPlot.Tests;

/// <summary>
/// The selection of point layers (design §6.4 "散布の選択", §10 R3c row, Issue #471): their rows on both paths.
/// </summary>
[Collection("Large data")]
public sealed class PointSelectionTests
{
    private const int Cpu = LayerPlan.MaxElements;
    private const int Dense = 400_000;
    private static readonly PanelId Panel = new(0, 0);

    // A concave lasso: a square with a notch cut down from its top, as shares of its ranges.
    private static readonly Vector2[] Notched = [new(0, 0), new(1, 0), new(1, 1), new(0.5f, 0.3f), new(0, 1)];

    /// <summary>The kinds of predicate a test selects with.</summary>
    public enum Predicate
    {
        /// <summary>A rectangle.</summary>
        Rectangle,

        /// <summary>An x range.</summary>
        XRange,

        /// <summary>A lasso with a notch.</summary>
        Lasso,
    }

    /// <summary>
    /// A point layer answers <see cref="PlotEngine.SelectedRowsAsync"/> with the rows a loop finds by the predicate, on the CPU
    /// path from the workers' bits and on the index path from the index (design §6.4 "行の集合", Issue #471 Decision 4); on the
    /// index path, the cells meet the predicate inside it, across its boundary, and, for the lasso, outside it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Seeded points over [0, 100]²: 131,072 (the CPU path) or 400,000 (the index path), raw points at the view [0, 100]².
    ///
    /// Steps:
    /// - Select the rectangle [20.3, 61.7] × [30.1, 70.9], the x range [20.3, 61.7], or a lasso over [20, 80] × [15, 85] with
    ///   a notch cut down to 30 % of its height from its top. Ask the layer's rows.
    /// - Find by a loop the rows whose x and y the selection holds. On the index path, count the cells by how they meet it.
    ///
    /// Expected:
    /// - The rows are the loop's. On the index path, some cells lie inside and some across; for the lasso, some outside.
    /// </remarks>
    [Theory]
    [InlineData(Cpu, Predicate.Rectangle)]
    [InlineData(Cpu, Predicate.XRange)]
    [InlineData(Cpu, Predicate.Lasso)]
    [InlineData(Dense, Predicate.Rectangle)]
    [InlineData(Dense, Predicate.XRange)]
    [InlineData(Dense, Predicate.Lasso)]
    public void SelectedRows_MatchALoop_OnBothPaths(int points, Predicate predicate)
    {
        var (xs, ys, table) = Uniform(points, seed: 31);
        using var engine = Drawn(
            new NullBackend(),
            Plot.Create(table).Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Draw(PointDraw.Points))
        );
        var selection = predicate switch
        {
            Predicate.Rectangle => Selection2D.Rectangle(Panel, ViewRange.FromValues(20.3, 61.7), ViewRange.FromValues(30.1, 70.9)),
            Predicate.XRange => Selection2D.XRange(Panel, ViewRange.FromValues(20.3, 61.7)),
            _ => Selection2D.Lasso(Panel, ViewRange.FromValues(20.0, 80.0), ViewRange.FromValues(15.0, 85.0), Notched),
        };
        engine.Select(selection);
        var taken = (Selection2D)engine.Selection(0)!;

        var rows = Rows(engine);

        var expected = Enumerable.Range(0, points).Where(i => taken.Holds(xs[i] - taken.X.Origin, ys[i] - taken.Y.Origin)).ToArray();
        Assert.Equal(expected.Length, rows.Count);
        Assert.All(expected, i => Assert.True(rows.Contains(i)));
        if (points > Cpu)
        {
            var snapshot = ((IndexedPointPlan)engine.Plan!.Layers[0]).SpatialIndex!.Acquire();
            try
            {
                var (inside, outside, straddles) = SpatialSelection.Covers(snapshot, taken);
                Assert.True(inside > 0 && straddles > 0, $"Cells inside {inside}, across {straddles}.");
                Assert.True(predicate != Predicate.Lasso || outside > 0, $"Lasso cells outside {outside}.");
            }
            finally
            {
                snapshot.Release();
            }
        }
    }

    /// <summary>
    /// A split point layer and a facet's panel answer with the plot's table's rows, on the index path (design §6.4 "行の集合"):
    /// a part's gathered rows are taken back to the table's.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 300,000 seeded points over [0, 100]² and a category g of "a", "b", "c" by the row's remainder of 3; raw points.
    ///
    /// Steps:
    /// - Color the layer by g (three parts on the index path) and select the rectangle [10, 40] × [55, 90].
    /// - Facet the layer by g (three panels of 100,000 points, each on the index path) and select the same rectangle in
    ///   panel 1.
    ///
    /// Expected:
    /// - The split layer: the rows of every category the rectangle holds. The facet: the rows of b the rectangle holds.
    /// </remarks>
    [Fact]
    public void SelectedRows_OfSplitAndFacetedPoints_AreTheTablesRows()
    {
        const int Count = 300_000;
        var (xs, ys, points) = Uniform(Count, seed: 32);
        var table = DataTable
            .Create()
            .Add("x", points.Col<double>("x").Column)
            .Add("y", points.Col<double>("y").Column)
            .Add("g", Column.Categorical([.. Enumerable.Range(0, Count).Select(i => (string?)"abc"[i % 3].ToString())]))
            .Build();
        var (x, y) = (table.Col<double>("x"), table.Col<double>("y"));
        bool Holds(int i) => xs[i] is >= 10 and <= 40 && ys[i] is >= 55 and <= 90;

        using var split = Drawn(new NullBackend(), Plot.Create(table).Point(x, y, p => p.Draw(PointDraw.Points).ColorBy(table.Cat("g"))));
        Assert.All(((SplitPlan)split.Plan!.Layers[0]).Parts, part => Assert.IsType<IndexedPointPlan>(part));
        split.Select(Selection2D.Rectangle(Panel, ViewRange.FromValues(10.0, 40.0), ViewRange.FromValues(55.0, 90.0)));
        var all = Enumerable.Range(0, Count).Where(Holds).ToArray();
        var rows = Rows(split);
        Assert.Equal(all.Length, rows.Count);
        Assert.All(all, i => Assert.True(rows.Contains(i)));

        using var faceted = new PlotEngine(new NullBackend(), 640, 360) { WaitsForIndexes = true };
        faceted.SetPlot(
            Plot.Create(table)
                .Point(x, y, p => p.Draw(PointDraw.Points))
                .FacetWrap(table.Cat("g"))
                .ScaleX(Scale.Linear().WithDomain(0, 100))
                .ScaleY(Scale.Linear().WithDomain(0, 100))
                .Build()
        );
        faceted.RenderFrame();
        faceted.Select(Selection2D.Rectangle(new PanelId(0, 1), ViewRange.FromValues(10.0, 40.0), ViewRange.FromValues(55.0, 90.0)));
        var b = Enumerable.Range(0, Count).Where(i => i % 3 == 1 && Holds(i)).ToArray();
        var panelRows = Rows(faceted);
        Assert.Equal(b.Length, panelRows.Count);
        Assert.All(b, i => Assert.True(panelRows.Contains(i)));
    }

    /// <summary>
    /// Before the index is full, a layer's rows wait for it and are then exact; while a changed column's index is built
    /// again, they come from the last full snapshot without waiting (design §6.4 "索引ができる前の行の集合", Issue #471 Decision
    /// 13).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 200,000 seeded points over [0, 100]², raw points, on a pool of 3 workers, in an engine that does not wait for its
    ///   indexes; the x column's storage holds the reads of chunk 0.
    ///
    /// Steps:
    /// - Hold chunk 0, draw a frame, select the rectangle [20, 60] × [30, 70], and ask the rows; wait 300 ms. Release the
    ///   chunk; wait for the rows.
    /// - Draw until not provisional. Mirror every x (x → 100 − x), invalidate the column, draw a frame, which queues the new
    ///   build and draws the last full snapshot, and ask the rows. Draw until not provisional and ask them again.
    ///
    /// Expected:
    /// - The first rows have not ended after 300 ms; once the chunk is released, they are a loop's over the values.
    /// - The second rows are a loop's over the values before the mirror: the snapshot drawn, not the one being built. The third
    ///   are a loop's over the mirrored values.
    /// </remarks>
    [Fact]
    public void SelectedRows_WaitForTheFirstFullIndex_AndNotForARebuild()
    {
        const int Count = 200_000;
        var (values, ys, _) = Uniform(Count, seed: 33);
        using var x = TestStorage<double>.MakeColumn((double[])values.Clone(), out var storage);
        using var y = Column.Wrap(ys);
        var table = DataTable.Create().Add("x", x).Add("y", y).Build();
        var backend = new NullBackend();
        using var pool = new PlotWorkerPool(3);
        using var engine = new PlotEngine(backend, 640, 360, pool: pool);
        var rectangle = Selection2D.Rectangle(Panel, ViewRange.FromValues(20.0, 60.0), ViewRange.FromValues(30.0, 70.0));
        var before = Enumerable.Range(0, Count).Where(i => values[i] is >= 20 and <= 60 && ys[i] is >= 30 and <= 70).ToArray();

        storage.Hold(0);
        Task<RowSelection> first;
        try
        {
            engine.SetPlot(
                Plot.Create(table)
                    .Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Draw(PointDraw.Points))
                    .ScaleX(Scale.Linear().WithDomain(0, 100))
                    .ScaleY(Scale.Linear().WithDomain(0, 100))
                    .Build()
            );
            engine.RenderFrame();
            engine.Select(rectangle);
            first = engine.SelectedRowsAsync(0, 0).AsTask();
            Thread.Sleep(300);
            Assert.False(first.IsCompleted, "The rows ended before the index was built.");
        }
        finally
        {
            storage.Release(0);
        }

        Same(before, Done(first));

        Settle(engine, backend);
        for (var i = 0; i < Count; i++)
        {
            storage.Values[i] = 100 - values[i];
        }

        // The frame queues the build of the mirrored values and draws the last full snapshot, which the rows come from.
        x.Invalidate();
        engine.RenderFrame();
        Same(before, Done(engine.SelectedRowsAsync(0, 0).AsTask()));

        Settle(engine, backend);
        var after = Enumerable.Range(0, Count).Where(i => 100 - values[i] is >= 20 and <= 60 && ys[i] is >= 30 and <= 70).ToArray();
        Same(after, Done(engine.SelectedRowsAsync(0, 0).AsTask()));

        static void Same(int[] expected, RowSelection rows)
        {
            Assert.Equal(expected.Length, rows.Count);
            Assert.All(expected, i => Assert.True(rows.Contains(i)));
        }
    }

    private static RowSelection Rows(PlotEngine engine) => Done(engine.SelectedRowsAsync(0, 0).AsTask());

    // The rows once their task ends, waited for with a watchdog.
    private static RowSelection Done(Task<RowSelection> task)
    {
        Eventually.Completes(task);
        return task.Result;
    }

    private static void Settle(PlotEngine engine, NullBackend backend)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var provisional = true;
        while (provisional)
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), "The frames stayed provisional for 60 s.");
            backend.Images.Sent();
            engine.RenderFrame();
            Thread.Sleep(1);
            provisional = engine.IsProvisional;
        }
    }

    private static (double[] X, double[] Y, DataTable Table) Uniform(int count, int seed)
    {
        var random = new Random(seed);
        var xs = Enumerable.Range(0, count).Select(_ => random.NextDouble() * 100).ToArray();
        var ys = Enumerable.Range(0, count).Select(_ => random.NextDouble() * 100).ToArray();
        return (xs, ys, DataTable.Create().Add("x", Column.Wrap(xs)).Add("y", Column.Wrap(ys)).Build());
    }

    // An engine that waits for its indexes over a plot of domains [0, 100]², drawn until every page and face is on the GPU.
    private static PlotEngine Drawn(NullBackend backend, PlotBuilder builder)
    {
        var engine = new PlotEngine(backend, 640, 360) { WaitsForIndexes = true };
        engine.SetPlot(builder.ScaleX(Scale.Linear().WithDomain(0, 100)).ScaleY(Scale.Linear().WithDomain(0, 100)).Build());
        for (var i = 0; i < 200 && (i == 0 || engine.IsProvisional); i++)
        {
            backend.Images.Sent();
            engine.RenderFrame();
        }

        return engine;
    }
}
