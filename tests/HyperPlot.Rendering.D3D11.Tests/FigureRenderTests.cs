using HyperPlot.Testing;
using HyperPlot.Tests;

namespace HyperPlot.Rendering.D3D11.Tests;

/// <summary>Row G6c's figures drawn on WARP (design §10.1 scenes 38 and 39), with the fake text backend.</summary>
public sealed class FigureRenderTests : IDisposable
{
    private const int Width = 640;
    private const int Height = 360;
    private readonly PlotWorkerPool _pool = new(2);
    private readonly Column<float> _wave;
    private readonly Column<float> _other;
    private readonly Column<float> _levels;
    private readonly DataTable _table;

    public FigureRenderTests()
    {
        var random = new Random(38);
        _wave = Column.Wrap(Enumerable.Range(0, 120).Select(i => (float)((Math.Sin(i / 12.0) * 5) + random.NextDouble())).ToArray());
        _other = Column.Wrap(Enumerable.Range(0, 120).Select(i => (float)((Math.Cos(i / 17.0) * 3) + random.NextDouble())).ToArray());
        _levels = Column.Wrap(Enumerable.Range(0, 120).Select(i => (float)(2 + (Math.Abs(Math.Sin(i / 9.0)) * 4))).ToArray());
        _table = DataTable
            .Create()
            .Add("t", Column.Uniform(0, 1, 120))
            .Add("wave", _wave)
            .Add("other", _other)
            .Add("level", _levels)
            .Add("region", Column.Categorical([.. Enumerable.Range(0, 120).Select(i => (string?)(i < 60 ? "East" : "West"))], ["East", "West"]))
            .Build();
    }

    public void Dispose()
    {
        _wave.Dispose();
        _other.Dispose();
        _levels.Dispose();
        _pool.Dispose();
    }

    /// <summary>
    /// A concatenation (§10.1 scene 38, row G6c): a line with a legend, bars, and a histogram in 2 columns, the last row's one
    /// on the left, under the figure's title.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A seeded wave, a second wave, and levels over 120 times; the fake text backend; the figure's Theme.Light and title;
    ///   each plot with axes and a title.
    ///
    /// Steps:
    /// - Render at 640 × 360; compare with <c>Golden/figure-concat.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void Concat_MatchesBaseline()
    {
        var figure = Figure.Concat(2, Line("Wave"), Bars(), Histogram(Theme.Light)) with { Title = "Three plots" };
        Check(figure, "figure-concat.png");
    }

    /// <summary>
    /// A grid (§10.1 scene 39, row G6c): a line spanning the top row's first 2 columns, a wrap of 2 regions on its right, a
    /// histogram in the dark theme and a detail line below, and the bottom right cell empty.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The same data; a grid of 2 × 3; the fake text backend; the figure's Theme.Light.
    ///
    /// Steps:
    /// - Render at 640 × 360; compare with <c>Golden/figure-grid.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void Grid_MatchesBaseline()
    {
        var regions = Plot.Create(_table)
            .Line(_table.Col<double>("t"), _table.Col<float>("other"))
            .FacetWrap(_table.Cat("region"))
            .Title("Regions")
            .Build();
        var detail = Plot.Create(_table)
            .Line(_table.Col<double>("t"), _table.Col<float>("wave"))
            .ScaleX(Scale.Linear().WithDomain(20, 50))
            .Title("Detail")
            .Build();
        var figure = Figure.Grid(
            2,
            3,
            Figure.Cell(Line("Overview"), 0, 0, columnSpan: 2),
            Figure.Cell(regions, 0, 2),
            Figure.Cell(Histogram(Theme.Dark), 1, 0),
            Figure.Cell(detail, 1, 1)
        );
        Check(figure, "figure-grid.png");
    }

    private PlotSpec Line(string title) =>
        Plot.Create(_table)
            .Line(_table.Col<double>("t"), _table.Col<float>("wave"), l => l.Label("wave"))
            .Line(_table.Col<double>("t"), _table.Col<float>("other"), l => l.Label("other"))
            .ScaleX(Scale.Linear().WithTitle("Time"))
            .Title(title)
            .Build();

    private PlotSpec Bars() =>
        Plot.Create(_table).Bar(_table.Col<double>("t"), _table.Col<float>("level")).ScaleX(Scale.Linear().WithTitle("Time")).Title("Levels").Build();

    private PlotSpec Histogram(Theme theme) =>
        Plot.Create(_table).Histogram(_table.Col<float>("wave")).ScaleX(Scale.Linear().WithTitle("Wave")).Title("Spread").Theme(theme).Build();

    private void Check(FigureSpec figure, string baseline)
    {
        using var backend = D3D11Backend.CreateForTest(useWarp: true, createDevice: null, new TextEngine(new FakeTextBackend()));
        using var renderer = new HeadlessRenderer(backend, Width, Height, _pool);
        renderer.Render(figure);

        GoldenImage.AssertMatches(renderer, GoldenImageTests.Baseline(baseline), GoldenImageTests.Tolerance, GoldenImageTests.Output);
    }
}
