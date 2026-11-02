using System.Numerics;
using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>
/// The figures of row G6c (design §4.5, §4.6, §4.8, §7.5, §10's G6c row): the factories and the checks, the cells' layout,
/// each plot drawn in its cell as alone, the backgrounds, the shared pyramids and the one extraction, the diff, the
/// switch between a plot and a figure, and the allocations, with the fake text backend.
/// </summary>
public sealed class FigureTests : IDisposable
{
    private const int Width = 640;
    private const int Height = 360;
    private const int Length = 200;
    private readonly PlotWorkerPool _pool = new(2);
    private readonly Column<float> _wave;
    private readonly Column<float> _other;
    private readonly DataTable _table;

    public FigureTests()
    {
        _wave = Column.Wrap(Enumerable.Range(0, Length).Select(i => (float)(Math.Sin(i / 15.0) * 4)).ToArray());
        _other = Column.Wrap(Enumerable.Range(0, Length).Select(i => (float)(Math.Cos(i / 11.0) * 2)).ToArray());
        _table = DataTable
            .Create()
            .Add("t", Column.Uniform(0, 1, Length))
            .Add("wave", _wave)
            .Add("other", _other)
            .Add(
                "region",
                Column.Categorical([.. Enumerable.Range(0, Length).Select(i => (string?)(i < Length / 2 ? "East" : "West"))], ["East", "West"])
            )
            .Build();
    }

    public void Dispose()
    {
        _wave.Dispose();
        _other.Dispose();
        _pool.Dispose();
    }

    /// <summary>The factories check their arguments and make the figures (design §4.1).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A line plot.
    ///
    /// Steps:
    /// - Call the factories with each wrong argument, then with right ones.
    ///
    /// Expected:
    /// - ArgumentOutOfRangeException, ArgumentNullException, ArgumentException; 256 plots pass; HConcat has no number of
    ///   columns and VConcat one; a cell's spans default to 1.
    /// </remarks>
    [Fact]
    public void Factories_CheckTheirArguments()
    {
        var plot = Line("wave");
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Figure.Concat(0, plot));
        _ = Assert.Throws<ArgumentNullException>(() => Figure.Concat(1, null!));
        _ = Assert.Throws<ArgumentNullException>(() => Figure.Concat(1, plot, null!));
        _ = Assert.Throws<ArgumentException>(() => Figure.Concat(1));
        _ = Assert.Throws<ArgumentException>(() => Figure.HConcat([.. Enumerable.Repeat(plot, 257)]));
        Assert.Equal(256, Figure.HConcat([.. Enumerable.Repeat(plot, 256)]).Plots.Count);
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Figure.Grid(0, 1, Figure.Cell(plot, 0, 0)));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Figure.Grid(1, 0, Figure.Cell(plot, 0, 0)));
        _ = Assert.Throws<ArgumentException>(() => Figure.Grid(1, 1, Figure.Cell(plot, 0, 1)));
        _ = Assert.Throws<ArgumentException>(() => Figure.Grid(1, 2, Figure.Cell(plot, 0, 0, columnSpan: 3)));
        _ = Assert.Throws<ArgumentException>(() => Figure.Grid(1, 2, Figure.Cell(plot, 0, 0, columnSpan: 2), Figure.Cell(plot, 0, 1)));
        _ = Assert.Throws<ArgumentException>(() => Figure.Grid(1, 1));
        _ = Assert.Throws<ArgumentNullException>(() => Figure.Grid(1, 1, null!));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Figure.Cell(plot, -1, 0));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Figure.Cell(plot, 0, 0, rowSpan: 0));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Figure.Cell(plot, 0, 0, columnSpan: 0));
        _ = Assert.Throws<ArgumentNullException>(() => Figure.Cell(null!, 0, 0));
        Assert.Null(Figure.HConcat(plot, plot).Columns);
        Assert.Equal(1, Figure.VConcat(plot, plot).Columns);
        Assert.Equal((1, 1), (Figure.Cell(plot, 0, 0).RowSpan, Figure.Cell(plot, 0, 0).ColumnSpan));
        Assert.Equal(20, Figure.HConcat(plot).Spacing);
    }

    /// <summary>
    /// The compile refuses a figure that does not check (design §4.5, §4.8), values set with <c>with</c> included: a number of
    /// columns, rows, or a span below 1, a negative place, no plot or cell, a cell outside the grid or over another, a spacing
    /// not finite or below 0, 257 plots, and 257 panels together; 256 panels together are drawn.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A line plot; a table of 129 categories and one of 128.
    ///
    /// Steps:
    /// - Render each figure.
    ///
    /// Expected:
    /// - ArgumentException each (ArgumentNullException for a null plot); the figure of two plots of 128 panels each draws.
    /// </remarks>
    [Fact]
    public void Compile_RefusesFiguresThatDoNotCheck()
    {
        var plot = Line("wave");
        var concat = Figure.HConcat(plot, plot);
        var grid = Figure.Grid(2, 2, Figure.Cell(plot, 0, 0));
        FigureSpec[] wrong =
        [
            concat with
            {
                Columns = 0,
            },
            concat with
            {
                Plots = [],
            },
            concat with
            {
                Plots = [.. Enumerable.Repeat(plot, 257)],
            },
            concat with
            {
                Spacing = -1,
            },
            concat with
            {
                Spacing = float.NaN,
            },
            grid with
            {
                Rows = 0,
            },
            grid with
            {
                Columns = 0,
            },
            grid with
            {
                Cells = [],
            },
            grid with
            {
                Cells = [Figure.Cell(plot, 0, 0) with { RowSpan = 0 }],
            },
            grid with
            {
                Cells = [Figure.Cell(plot, 0, 0) with { ColumnSpan = 0 }],
            },
            grid with
            {
                Cells = [Figure.Cell(plot, 0, 0) with { Row = -1 }],
            },
            grid with
            {
                Cells = [Figure.Cell(plot, 1, 1, rowSpan: 2)],
            },
            grid with
            {
                Cells = [Figure.Cell(plot, 0, 0, 2, 2), Figure.Cell(plot, 1, 1)],
            },
        ];
        foreach (var figure in wrong)
        {
            using var plan = Plan();
            _ = Assert.Throws<ArgumentException>(() => plan.Render(figure));
        }

        using var nullPlot = Plan();
        _ = Assert.Throws<ArgumentNullException>(() => nullPlot.Render(concat with { Plots = [plot, null!] }));

        foreach (var (count, refused) in new[] { (129, true), (128, false) })
        {
            var names = Enumerable.Range(0, count).Select(i => $"c{i}").ToArray();
            using var y = Column.Wrap(Enumerable.Range(0, count).Select(i => (float)i).ToArray());
            var table = DataTable.Create().Add("t", Column.Uniform(0, 1, count)).Add("y", y).Add("k", Column.Categorical(names, names)).Build();
            var faceted = Plot.Create(table).Line(table.Col<double>("t"), table.Col<float>("y")).FacetWrap(table.Cat("k")).Build();
            using var plan = new FigurePlan(_pool, 4_000, 4_000, new TextEngine(new FakeTextBackend()), 1);
            if (refused)
            {
                var error = Assert.Throws<ArgumentException>(() => plan.Render(Figure.HConcat(faceted, faceted)));
                Assert.Contains("258 panels", error.Message, StringComparison.Ordinal);
            }
            else
            {
                plan.Render(Figure.HConcat(faceted, faceted));
                Assert.Equal(2, plan.Drawn);
            }
        }
    }

    /// <summary>
    /// The cells follow §4.6's formulas: the figure's padding and title band, equal cells S apart with the pixels ⌊⌋ leaves on
    /// the right and the bottom; a concatenation's rows of its columns, at most its plots, the last row's on the left, or one
    /// row; a grid's spans over their cells and the spaces between.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Three line plots; the figure's Theme.Light at the scale 1.
    ///
    /// Steps:
    /// - Render a concatenation in 2 columns with a title, one in 5 columns, one with no number of columns, and a grid of 2 × 3
    ///   whose first cell spans 2 columns and second 2 rows.
    ///
    /// Expected:
    /// - Each cell's rectangle as computed.
    /// </remarks>
    [Fact]
    public void Layout_FollowsTheFormulas()
    {
        var theme = Theme.Light;
        var (p, g, s) = (10, 4, 20);
        var titleBand = (int)Math.Ceiling(FakeTextBackend.LineHeight(theme.TitleSize)) + g;
        var plots = new[] { Line("wave"), Line("other"), Line("wave") };

        using var concat = Plan();
        concat.Render(Figure.Concat(2, plots) with { Title = "Title" });
        var (w, h) = (Floor((Width - (2 * p) - s) / 2.0), Floor((Height - (2 * p) - titleBand - s) / 2.0));
        Assert.Equal([(p, p + titleBand, w, h), (p + w + s, p + titleBand, w, h), (p, p + titleBand + h + s, w, h)], concat.Cells.ToArray());

        using var wide = Plan();
        wide.Render(Figure.Concat(5, plots));
        var third = Floor((Width - (2 * p) - (2 * s)) / 3.0);
        Assert.Equal((p + (2 * (third + s)), p, third, Height - (2 * p)), wide.Cells[2]);

        using var row = Plan();
        row.Render(Figure.HConcat(plots));
        Assert.Equal(wide.Cells.ToArray(), row.Cells.ToArray());

        using var grid = Plan();
        grid.Render(
            Figure.Grid(2, 3, Figure.Cell(plots[0], 0, 0, columnSpan: 2), Figure.Cell(plots[1], 0, 2, rowSpan: 2), Figure.Cell(plots[2], 1, 0))
        );
        var (gw, gh) = (third, Floor((Height - (2 * p) - s) / 2.0));
        Assert.Equal([(p, p, (2 * gw) + s, gh), (p + (2 * (gw + s)), p, gw, (2 * gh) + s), (p, p + gh + s, gw, gh)], grid.Cells.ToArray());

        static int Floor(double v) => (int)Math.Floor(v);
    }

    /// <summary>
    /// A plot in a cell records what it records alone on a target of the cell's size, moved to the cell's corner (design
    /// §4.6 step 4): with a legend and titles, a facet, a polar system, and a color bar.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Four plots on a grid of 2 × 2 at 640 × 360.
    ///
    /// Steps:
    /// - Render the figure; render each plot alone on a plan of its cell's size.
    ///
    /// Expected:
    /// - Each plot's list in the figure equals its list alone, every place moved by the cell's corner (within 1e-3).
    /// </remarks>
    [Fact]
    public void Cells_DrawAsTheirPlotAlone()
    {
        PlotSpec[] plots =
        [
            Line("wave") with
            {
                Title = "Lines",
            },
            Plot.Create(_table).Line(_table.Col<double>("t"), _table.Col<float>("other")).FacetWrap(_table.Cat("region")).Build(),
            Line("wave") with
            {
                Coord = Coord.Polar(),
            },
            Plot.Create(_table).Bin2D(_table.Col<double>("t"), _table.Col<float>("wave")).Build(),
        ];
        using var figure = Plan();
        figure.Render(
            Figure.Grid(2, 2, Figure.Cell(plots[0], 0, 0), Figure.Cell(plots[1], 0, 1), Figure.Cell(plots[2], 1, 0), Figure.Cell(plots[3], 1, 1))
        );
        for (var i = 0; i < plots.Length; i++)
        {
            var (left, top, width, height) = figure.Cells[i];
            using var alone = new PlotPlan(_pool, width, height, new TextEngine(new FakeTextBackend()), 1);
            AssertMoved(alone.Render(plots[i]), figure.Plans[i].List, left, top);
        }
    }

    /// <summary>
    /// The figure's list fills each cell with its plot's background, and the title above the cells (design §4.6); a figure too
    /// small for a cell draws none, and a cell too small for its plot draws its background only.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A light plot and a dark one side by side with a title; the same at 30 × 30; a grid whose narrow column leaves its plot
    ///   too small.
    ///
    /// Steps:
    /// - Render each.
    ///
    /// Expected:
    /// - Two rectangles in the plots' backgrounds at the cells and the title centered at the padding; at 30 × 30 no plot drawn;
    ///   the narrow cell's rectangle with its plot's list empty.
    /// </remarks>
    [Fact]
    public void Backgrounds_AndTooSmall()
    {
        var dark = Line("other") with { Theme = Theme.Dark };
        using var plan = Plan();
        plan.Render(Figure.HConcat(Line("wave"), dark) with { Title = "Two" });
        var rects = plan.List.Rects.ToArray();
        Assert.Equal(2, rects.Length);
        Assert.Equal(plan.Cells[1], ((int)rects[1].X, (int)rects[1].Y, (int)rects[1].Width, (int)rects[1].Height));
        Assert.NotEqual(rects[0].Color, rects[1].Color);
        var title = Assert.Single(plan.List.Texts.ToArray());
        Assert.Equal(10 + ((Width - 20) / 2f) - (3 * FakeTextBackend.Advance(Theme.Light.TitleSize) / 2), title.Position.X, 1e-3);

        using var tiny = new FigurePlan(_pool, 30, 30, new TextEngine(new FakeTextBackend()), 1);
        tiny.Render(Figure.HConcat(Line("wave"), dark));
        Assert.Equal(0, tiny.Drawn);

        using var narrow = new FigurePlan(_pool, 80, 300, new TextEngine(new FakeTextBackend()), 1);
        narrow.Render(Figure.HConcat(Line("wave"), dark) with { Spacing = 0 });
        Assert.Equal(2, narrow.Drawn);
        Assert.Equal(0, narrow.Plans[1].List.Commands.Length);
        Assert.Equal(2, narrow.List.Rects.Length);
    }

    /// <summary>
    /// The plots that read one column share its pyramid, a pyramid no plot reads is released, and every plot is extracted in
    /// one extraction (design §4.5, §5.5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two plots of the same y column, then one of them on the other column.
    ///
    /// Steps:
    /// - Render the figure; render it with the second plot changed; render a figure of the first plot only.
    ///
    /// Expected:
    /// - One pyramid, one extraction, each plan extracted once; two pyramids; one pyramid.
    /// </remarks>
    [Fact]
    public void Pyramids_AreSharedAndExtractedOnce()
    {
        var (a, b) = (Line("wave"), Line("wave") with { Title = "B" });
        using var plan = Plan();
        plan.Render(Figure.HConcat(a, b));
        Assert.Equal(1, plan.Store.Count);
        Assert.Equal(1, plan.Extractions);
        Assert.All(plan.Plans, p => Assert.Equal(1, p.Extractions));

        plan.Render(Figure.HConcat(a, Line("other")));
        Assert.Equal(2, plan.Store.Count);
        plan.Render(Figure.HConcat(a));
        Assert.Equal(1, plan.Store.Count);
    }

    /// <summary>
    /// The diff of a figure (design §4.8): a plot's change takes its plan's stages alone; a new plot gets a new plan and the
    /// cells are placed again; a new spacing places every plot again; a new figure of the same plots and cells does nothing;
    /// one plot in two cells draws in both.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two line plots side by side.
    ///
    /// Steps:
    /// - Render; render with the first plot's y domain given; with a third plot; with a new spacing; a new figure of the same
    ///   plots; a grid of one plot in two cells.
    ///
    /// Expected:
    /// - The second plan's layouts and recordings stay while the first's grow; the old plans kept and a third made, all laid
    ///   out again; all laid out again; nothing; both cells drawn.
    /// </remarks>
    [Fact]
    public void Diff_TakesItsStages()
    {
        var (a, b) = (Line("wave"), Line("other"));
        using var plan = Plan();
        plan.Render(Figure.HConcat(a, b));
        var (first, second) = (plan.Plans[0], plan.Plans[1]);
        var (layouts, recordings) = (second.Layouts, second.Recordings);
        plan.Render(Figure.HConcat(a with { ScaleY = Scale.Linear().WithDomain(-10, 10) }, b));
        Assert.Equal((layouts, recordings), (second.Layouts, second.Recordings));
        Assert.Equal(2, first.Layouts);

        plan.Render(Figure.HConcat(a, b, a));
        Assert.Same(first, plan.Plans[0]);
        Assert.Same(second, plan.Plans[1]);
        Assert.Equal(3, plan.Plans.Count);
        Assert.Equal(layouts + 1, second.Layouts);

        plan.Render(Figure.HConcat(a, b, a) with { Spacing = 5 });
        Assert.Equal(layouts + 2, second.Layouts);

        (layouts, recordings) = (second.Layouts, second.Recordings);
        plan.Render(Figure.HConcat(a, b, a) with { Spacing = 5 });
        Assert.Equal((layouts, recordings), (second.Layouts, second.Recordings));

        using var twice = Plan();
        twice.Render(Figure.Grid(1, 2, Figure.Cell(a, 0, 0), Figure.Cell(a, 0, 1)));
        Assert.All(twice.Plans, p => Assert.NotEqual(0, p.List.Commands.Length));
    }

    /// <summary>
    /// A renderer that draws a plot alone and then a figure keeps the plan of the one it draws and releases the other's
    /// (design §7.5); a figure draws the figure's list and each plot's in one frame.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A backend that counts its draws; a line plot; a figure of two plots.
    ///
    /// Steps:
    /// - Render the plot, then the figure, then the plot.
    ///
    /// Expected:
    /// - A plot plan and no figure plan, then the reverse with three draws, then a plot plan and no figure plan.
    /// </remarks>
    [Fact]
    public void Renderer_SwitchesPlans()
    {
        var backend = new CountingBackend();
        using var renderer = new HeadlessRenderer(backend, Width, Height, _pool);
        renderer.Render(Line("wave"));
        Assert.NotNull(renderer.Plan);
        Assert.Null(renderer.Figure);
        var draws = backend.Draws;
        renderer.Render(Figure.HConcat(Line("wave"), Line("other")));
        Assert.Null(renderer.Plan);
        Assert.NotNull(renderer.Figure);
        Assert.Equal(draws + 3, backend.Draws);
        renderer.Render(Line("wave"));
        Assert.NotNull(renderer.Plan);
        Assert.Null(renderer.Figure);
    }

    /// <summary>
    /// Once warm, a concatenation and a grid drawn again over two y domains in turn allocate nothing (design §4.8), every node
    /// new each frame as a host makes them.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Figures of two line plots, made anew with a y domain of 5 or 10.
    ///
    /// Steps:
    /// - Render the first two a few times, then four ten times in turn.
    ///
    /// Expected:
    /// - 0 bytes on this thread.
    /// </remarks>
    [Fact]
    public void Warm_AllocatesNothing()
    {
        PlotSpec Ranged(string y, double high) => Line(y) with { ScaleY = Scale.Linear().WithDomain(-high, high) };
        FigureSpec Concat(double high) => Figure.HConcat(Ranged("wave", high), Ranged("other", high));
        FigureSpec Grid(double high) => Figure.Grid(1, 2, Figure.Cell(Ranged("wave", high), 0, 0), Figure.Cell(Ranged("other", high), 0, 1));
        foreach (var make in new Func<double, FigureSpec>[] { Concat, Grid })
        {
            var figures = new[] { make(5), make(10), make(5), make(10) };
            using var plan = Plan();
            for (var i = 0; i < 6; i++)
            {
                plan.Render(figures[i % 2]);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10; i++)
            {
                plan.Render(figures[i % 4]);
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
    }

    /// <summary>Asserts that a list equals another moved by a corner: every place, clip, and bound, within 1e-3.</summary>
    private static void AssertMoved(RenderList alone, RenderList moved, int left, int top)
    {
        var d = new Vector2(left, top);
        Assert.Equal(alone.Commands.ToArray(), moved.Commands.ToArray());
        Assert.Equal(alone.Segments.Length, moved.Segments.Length);
        for (var i = 0; i < alone.Segments.Length; i++)
        {
            Near(alone.Segments[i].Start + d, moved.Segments[i].Start);
            Near(alone.Segments[i].End + d, moved.Segments[i].End);
        }

        Assert.Equal(alone.Pieces.Length, moved.Pieces.Length);
        for (var i = 0; i < alone.Pieces.Length; i++)
        {
            Near(alone.Pieces[i].Point + d, moved.Pieces[i].Point);
        }

        Assert.Equal(alone.Polylines.Length, moved.Polylines.Length);
        for (var i = 0; i < alone.Polylines.Length; i++)
        {
            var (a, b) = (alone.Polylines[i], moved.Polylines[i]);
            Assert.Equal(
                (a.Style, a.SegmentStart, a.SegmentCount, a.PieceStart, a.PieceCount),
                (b.Style, b.SegmentStart, b.SegmentCount, b.PieceStart, b.PieceCount)
            );
            Assert.Equal(Moved(a.Clip, left, top), b.Clip);
        }

        Assert.Equal(alone.Texts.Length, moved.Texts.Length);
        for (var i = 0; i < alone.Texts.Length; i++)
        {
            var (a, b) = (alone.Texts[i], moved.Texts[i]);
            Assert.Equal((a.Text, a.Style, Moved(a.Clip, left, top)), (b.Text, b.Style, b.Clip));
            Near(a.Position + d, b.Position);
        }

        Assert.Equal(alone.Rects.Length, moved.Rects.Length);
        for (var i = 0; i < alone.Rects.Length; i++)
        {
            var (a, b) = (alone.Rects[i], moved.Rects[i]);
            Near(new Vector2(a.X + left, a.Y + top), new Vector2(b.X, b.Y));
            Assert.Equal((a.Width, a.Height, a.Color), (b.Width, b.Height, b.Color));
            Assert.Equal(Moved(alone.RectClips[i], left, top), moved.RectClips[i]);
        }

        Assert.Equal(alone.Columns.Length, moved.Columns.Length);
        for (var i = 0; i < alone.Columns.Length; i++)
        {
            var (a, b) = (alone.Columns[i], moved.Columns[i]);
            Near(new Vector2(a.Left + left, a.TopLeft + top), new Vector2(b.Left, b.TopLeft));
            Assert.Equal(Moved(alone.ColumnClips[i], left, top), moved.ColumnClips[i]);
        }

        static void Near(Vector2 expected, Vector2 actual)
        {
            Assert.InRange(actual.X, expected.X - 1e-3f, expected.X + 1e-3f);
            Assert.InRange(actual.Y, expected.Y - 1e-3f, expected.Y + 1e-3f);
        }

        static ClipRect Moved(ClipRect clip, int left, int top) =>
            clip == ClipRect.All ? clip : new ClipRect(clip.Left + left, clip.Top + top, clip.Right + left, clip.Bottom + top);
    }

    private PlotSpec Line(string y) =>
        Plot.Create(_table)
            .Line(_table.Col<double>("t"), _table.Col<float>(y), l => l.Label(y))
            .ScaleX(Scale.Linear().WithTitle("Time"))
            .ScaleY(Scale.Linear().WithTitle("Value"))
            .Build();

    private FigurePlan Plan() => new(_pool, Width, Height, new TextEngine(new FakeTextBackend()), 1);

    /// <summary>A backend that counts its draws and draws nothing.</summary>
    private sealed class CountingBackend : IGpuBackend
    {
        public bool IsWarp => true;

        public int Draws { get; private set; }

        public GpuFrameTime? LastFrameTime => null;

        public TextEngine Text { get; } = new(new FakeTextBackend());

        public ResidencyCache Images { get; } = new();

        public IRenderTarget CreateRenderTarget(in RenderTargetDescription description) => new Target(description);

        public FrameScope BeginFrame() => new(this, 1);

        public void Draw(IRenderTarget target, RenderList list) => Draws++;

        public void EndFrame(long token) { }

        public void Dispose() { }

        private sealed class Target(RenderTargetDescription description) : IRenderTarget
        {
            public RenderTargetDescription Description => description;

            public void Clear(Color color) { }

            public void ReadPixels(Span<byte> destination) { }

            public void Dispose() { }
        }
    }
}
