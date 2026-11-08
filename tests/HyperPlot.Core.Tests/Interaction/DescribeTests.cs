using HyperPlot.Interaction;

namespace HyperPlot.Tests;

/// <summary>
/// The engine's summary of its plot for the accessibility API (design §7.1 "要約", §10 "アクセシビリティ（W1c）", requirement
/// N-10, row W1c, Issue #323): the plot's kind, its title, each axis's title and the current view's range in the format of its
/// tick labels, and the series.
/// </summary>
public sealed class DescribeTests
{
    private const int Rows = 100;
    private static readonly PanelId Panel = new(0, 0);

    /// <summary>
    /// A plot's summary names its geoms, its title, each axis's title and range in its ticks' format, and its series' labels
    /// and geoms; a moved view changes the ranges.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A line labelled "temperature" and bins of the same x, x titled "time" over [0, 100], y titled "value" over
    ///   [−1.5, 2.5]; titled "Weather".
    ///
    /// Steps:
    /// - Draw and describe; set the view to x [10.25, 30.5], draw, and describe.
    ///
    /// Expected:
    /// - "Plot "Weather": line, bar of bins layers. X axis "time": from 0 to 100. Y axis "value": from -1.5 to 2.5. Series:
    ///   "temperature" (line), bar of bins." Moved: the x range is the view's, written with the decimals of the new ticks.
    /// </remarks>
    [Fact]
    public void Plot_NamesItsKindTitleAxesAndSeries()
    {
        using var data = new Data();
        var t = data.Table;
        using var engine = new PlotEngine(new NullBackend(), 640, 360);
        engine.SetPlot(
            new PlotSpec
            {
                Data = t,
                Title = "Weather",
                Layers =
                [
                    new LineLayerSpec
                    {
                        X = t.Col<double>("x"),
                        Y = t.Col<double>("y"),
                        Label = "temperature",
                    },
                    new BarLayerSpec { X = t.Col<double>("x"), Stat = Stat.Bin() },
                ],
                ScaleX = Scale.Linear().WithDomain(0, 100) with { Title = "time" },
                ScaleY = Scale.Linear().WithDomain(-1.5, 2.5) with { Title = "value" },
            }
        );
        engine.WaitsForIndexes = true;
        engine.RenderFrame();

        Assert.Equal(
            "Plot \"Weather\": line, bar of bins layers. X axis \"time\": from 0 to 100. Y axis \"value\": from -1.5 to 2.5. Series: \"temperature\" (line), bar of bins.",
            engine.Describe()
        );

        engine.Views.SetView(Panel, new PanelView2D(ViewRange.FromValues(10.25, 30.5), ViewRange.FromValues(-1.5, 2.5)));
        engine.RenderFrame();
        Assert.Contains("X axis \"time\": from 10.25 to 30.50.", engine.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A band axis names its first and last visible categories and their count; a time axis writes its ends as its tick labels
    /// do; polar names the angle and the radius; flipped, the axes keep the names of x and y (Issue #323 Decision 3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Bars over 4 categories "a" to "d" on a band x. A line over a time x of 2026-01-01 to 2026-01-08 in seconds. The bars in
    ///   polar with theta = x. A line with x titled "across", flipped.
    ///
    /// Steps:
    /// - Draw each and describe.
    ///
    /// Expected:
    /// - "X axis: 4 categories, from "a" to "d"."; the time ends are the label the time format writes for 2026-01-01 and
    ///   2026-01-08 at the axis's finest tick boundary; "Angle axis" and "Radius axis"; "X axis "across"".
    /// </remarks>
    [Fact]
    public void Axes_AreWrittenAsTheyAreRead()
    {
        using var data = new Data();
        var t = data.Table;
        var bars = new PlotSpec
        {
            Data = t,
            Layers = [new BarLayerSpec { X = t.Cat("c").ToColumnRef(), Y = t.Col<double>("y") }],
            ScaleX = Scale.Band(),
        };
        Assert.Contains("X axis: 4 categories, from \"a\" to \"d\".", Describe(bars), StringComparison.Ordinal);

        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var time = new PlotSpec
        {
            Data = t,
            Layers = [new LineLayerSpec { X = t.Col<long>("day"), Y = t.Col<double>("y") }],
            ScaleX = Scale.Time(TimeUnit.Seconds, start).WithDomain(0, 7 * 86_400),
        };
        var text = Describe(time);
        var low = TimeLabel(start);
        var high = TimeLabel(start.AddDays(7));
        Assert.Contains($"X axis: from {low} to {high}.", text, StringComparison.Ordinal);

        Assert.Contains("Angle axis: 4 categories", Describe(bars with { Coord = Coord.Polar() }), StringComparison.Ordinal);
        Assert.Contains("Radius axis: from", Describe(bars with { Coord = Coord.Polar() }), StringComparison.Ordinal);

        var flipped = new PlotSpec
        {
            Data = t,
            Layers = [new LineLayerSpec { X = t.Col<double>("x"), Y = t.Col<double>("y") }],
            ScaleX = Scale.Linear() with { Title = "across" },
            Coord = Coord.Cartesian(flip: true),
        };
        Assert.Contains("X axis \"across\": from", Describe(flipped), StringComparison.Ordinal);

        // The finest boundary of the axis's ticks: days, here.
        static string TimeLabel(DateTime at)
        {
            var buffer = new char[64];
            Assert.True(TickFormat.TryWriteTime(at, 0, TimeInterval.Day, buffer, out var written));
            return new string(buffer, 0, written);
        }
    }

    /// <summary>A facet's summary gives its number of panels; a figure's names each plot in order (design §7.1).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A line in a wrap facet by 4 categories; a figure titled "Two" of a line and bars.
    ///
    /// Steps:
    /// - Draw and describe each.
    ///
    /// Expected:
    /// - The facet's: "in 4 panels". The figure's: "Figure "Two" of 2 plots. Plot 1: line layer." then "Plot 2: bar layer."
    /// </remarks>
    [Fact]
    public void FacetAndFigure_NameTheirPanelsAndPlots()
    {
        using var data = new Data();
        var t = data.Table;
        var line = new PlotSpec { Data = t, Layers = [new LineLayerSpec { X = t.Col<double>("x"), Y = t.Col<double>("y") }] };
        Assert.Contains(
            "line layer, in 4 panels; the axes of the first.",
            Describe(line with { Facet = Facet.Wrap(t.Cat("c")) }),
            StringComparison.Ordinal
        );

        var bars = new PlotSpec
        {
            Data = t,
            Layers = [new BarLayerSpec { X = t.Cat("c").ToColumnRef(), Y = t.Col<double>("y") }],
            ScaleX = Scale.Band(),
        };
        using var engine = new PlotEngine(new NullBackend(), 640, 360) { WaitsForIndexes = true };
        engine.SetFigure(Figure.HConcat(line, bars) with { Title = "Two" });
        engine.RenderFrame();
        var text = engine.Describe();
        Assert.StartsWith("Figure \"Two\" of 2 plots. Plot 1: line layer.", text, StringComparison.Ordinal);
        Assert.Contains(" Plot 2: bar layer.", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A layer that failed or waits for its workers is kept and marked "not drawn", and a provisional frame's summary ends
    /// with "(provisional)" (Issue #323 Decisions 2, 4).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread. A line labelled "bad" over a column whose chunk fails on the worker, beside a healthy line. A
    ///   vertical rule over an x column whose chunk is held on the worker.
    ///
    /// Steps:
    /// - Draw the first until WorkFailed came, then until not provisional; describe. Draw the second once; describe; release.
    ///
    /// Expected:
    /// - The first: "Series: line, "bad" (line, not drawn)." and no "(provisional)". The second: "rule (not drawn)", ending
    ///   with " (provisional)".
    /// </remarks>
    [Fact]
    public void LayersNotDrawn_AndProvisionalFrames_AreMarked()
    {
        using var pool = new PlotWorkerPool(1);
        using var bad = TestStorage<double>.MakeColumn([.. Enumerable.Range(0, Rows).Select(i => (double)i)], out var storage);
        using var y = Column.Wrap(Enumerable.Range(0, Rows).Select(i => (double)(i % 7)).ToArray());
        storage.FailOn(0, fail: true);
        var t = DataTable.Create().Add("u", Column.Uniform(0, 1, Rows)).Add("y", y).Add("bad", bad).Build();
        using (var engine = new PlotEngine(new NullBackend(), 640, 360, 1, pool))
        {
            var failed = false;
            engine.WorkFailed += (in WorkFailure _) => failed = true;
            engine.SetPlot(
                new PlotSpec
                {
                    Data = t,
                    Layers =
                    [
                        new LineLayerSpec { X = t.Col<double>("u"), Y = t.Col<double>("y") },
                        new LineLayerSpec
                        {
                            X = t.Col<double>("u"),
                            Y = t.Col<double>("bad"),
                            Label = "bad",
                        },
                    ],
                }
            );
            Eventually.True(
                () =>
                {
                    engine.RenderFrame();
                    return failed && !engine.IsProvisional;
                },
                "the failure"
            );
            var text = engine.Describe();
            Assert.EndsWith("Series: line, \"bad\" (line, not drawn).", text, StringComparison.Ordinal);
        }

        storage.FailOn(0, fail: false);
        storage.Hold(0);
        try
        {
            using var engine = new PlotEngine(new NullBackend(), 640, 360, 1, pool);
            engine.SetPlot(new PlotSpec { Data = t, Layers = [new RuleLayerSpec { X = t.Col<double>("bad") }] });
            engine.RenderFrame();
            var text = engine.Describe();
            Assert.Contains("Series: rule (not drawn).", text, StringComparison.Ordinal);
            Assert.EndsWith(" (provisional)", text, StringComparison.Ordinal);
        }
        finally
        {
            storage.Release(0);
        }
    }

    /// <summary>A summary needs a frame drawn (design §7.1): before it, Describe throws.</summary>
    /// <remarks>
    /// Preconditions:
    /// - An engine with a plot set and no frame drawn.
    ///
    /// Steps:
    /// - Describe.
    ///
    /// Expected:
    /// - InvalidOperationException.
    /// </remarks>
    [Fact]
    public void BeforeAFrame_Throws()
    {
        using var data = new Data();
        using var engine = new PlotEngine(new NullBackend(), 640, 360);
        engine.SetPlot(
            new PlotSpec { Data = data.Table, Layers = [new LineLayerSpec { X = data.Table.Col<double>("x"), Y = data.Table.Col<double>("y") }] }
        );
        _ = Assert.Throws<InvalidOperationException>(engine.Describe);
    }

    /// <summary>A summary is made off the frame path: the frames after it allocate nothing once warm (Issue #323 Decision 5).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A titled line plot with titled axes, drawn 10 frames.
    ///
    /// Steps:
    /// - Describe; count the bytes this thread allocates over 20 frames that follow.
    ///
    /// Expected:
    /// - 0 bytes.
    /// </remarks>
    [Fact]
    public void FramesAfterASummary_AllocateNothing()
    {
        using var data = new Data();
        var t = data.Table;
        using var engine = new PlotEngine(new NullBackend(), 640, 360) { WaitsForIndexes = true };
        engine.SetPlot(
            new PlotSpec
            {
                Data = t,
                Title = "Line",
                Layers = [new LineLayerSpec { X = t.Col<double>("x"), Y = t.Col<double>("y") }],
                ScaleX = Scale.Linear() with { Title = "x" },
            }
        );
        for (var i = 0; i < 10; i++)
        {
            engine.RenderFrame();
        }

        _ = engine.Describe();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 20; i++)
        {
            engine.RenderFrame();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static string Describe(PlotSpec spec)
    {
        using var engine = new PlotEngine(new NullBackend(), 640, 360) { WaitsForIndexes = true };
        engine.SetPlot(spec);
        engine.RenderFrame();
        return engine.Describe();
    }

    /// <summary>The tables the tests draw: a numeric x and y, 4 categories "a" to "d", and 7 days in seconds as longs.</summary>
    private sealed class Data : IDisposable
    {
        private readonly Column<double> _x = Column.Wrap(Enumerable.Range(0, Rows).Select(i => (double)i).ToArray());
        private readonly Column<double> _y = Column.Wrap(Enumerable.Range(0, Rows).Select(i => Math.Sin(i * 0.1)).ToArray());
        private readonly Column<long> _day = Column.Wrap(Enumerable.Range(0, Rows).Select(i => (long)i * 7 * 86_400 / (Rows - 1)).ToArray());

        public Data()
        {
            var names = new[] { "a", "b", "c", "d" };
            Table = DataTable
                .Create()
                .Add("x", _x)
                .Add("y", _y)
                .Add("day", _day)
                .Add("c", Column.Categorical([.. Enumerable.Range(0, Rows).Select(i => names[i % 4])], names))
                .Build();
        }

        public DataTable Table { get; }

        public void Dispose()
        {
            _x.Dispose();
            _y.Dispose();
            _day.Dispose();
        }
    }
}
