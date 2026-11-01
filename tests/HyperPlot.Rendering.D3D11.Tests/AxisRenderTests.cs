using HyperPlot.Testing;
using HyperPlot.Tests;

namespace HyperPlot.Rendering.D3D11.Tests;

/// <summary>Row G6a's axes drawn on WARP (design §10.1 scenes 34 and 35), with the fake text backend.</summary>
public sealed class AxisRenderTests
{
    private const int Width = 640;
    private const int Height = 360;

    /// <summary>
    /// Secondary axes (§10.1 scene 34, row G6a): a temperature line on the left y, humidity bars on a secondary y on the
    /// right, and an axis of minutes on the top over the x of seconds.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 61 rows every 10 s; a seeded temperature and humidity; a secondary y [0, 100]; an axis of x / 60 on the top; the
    ///   fake text backend; Theme.Light; axes, titles, and a legend.
    ///
    /// Steps:
    /// - Render at 640 × 360; compare with <c>Golden/axes-secondary.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void Secondary_MatchesBaseline()
    {
        var random = new Random(34);
        var temperature = Enumerable.Range(0, 61).Select(i => (float)(20 + (5 * Math.Sin(i / 9.0)) + random.NextDouble())).ToArray();
        var humidity = Enumerable.Range(0, 61).Select(i => (float)(50 + (30 * Math.Cos(i / 13.0)) + (5 * random.NextDouble()))).ToArray();
        using var pool = new PlotWorkerPool(2);
        using var t = Column.Wrap(temperature);
        using var h = Column.Wrap(humidity);
        var table = DataTable.Create().Add("s", Column.Uniform(0, 10, 61)).Add("t", t).Add("h", h).Build();
        var spec = Plot.Create(table)
            .Bar(table.Col<double>("s"), table.Col<float>("h"), configure: b => b.Label("Humidity").SecondaryY())
            .Line(table.Col<double>("s"), table.Col<float>("t"), l => l.Label("Temperature").Width(2))
            .ScaleX(Scale.Linear().WithTitle("Seconds").WithSecondaryAxis(new Times(1.0 / 60), "Minutes"))
            .ScaleY(Scale.Linear().WithTitle("°C"))
            .ScaleY2(Scale.Linear().WithDomain(0, 100).WithTitle("%"))
            .Title("Two units")
            .Build();
        Check(spec, pool, "axes-secondary.png");
    }

    /// <summary>
    /// Axes on the top and the right, flipped (§10.1 scene 35, row G6a): the primary x on the top and y on the right, and an
    /// axis of y × 10 on y's other side; flipped, x is drawn on the right and y on the top, its transform's axis at the bottom.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 40 rows of a seeded walk; x on the top, y on the right with an axis of y × 10; Coord.Cartesian(flip: true); the fake
    ///   text backend; Theme.Light; axis titles.
    ///
    /// Steps:
    /// - Render at 640 × 360; compare with <c>Golden/axes-sides-flip.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void SidesFlipped_MatchesBaseline()
    {
        var random = new Random(35);
        var walk = new float[40];
        for (var i = 1; i < walk.Length; i++)
        {
            walk[i] = walk[i - 1] + (float)(random.NextDouble() - 0.45);
        }

        using var pool = new PlotWorkerPool(2);
        using var y = Column.Wrap(walk);
        var table = DataTable.Create().Add("x", Column.Uniform(0, 1, walk.Length)).Add("y", y).Build();
        var spec = Plot.Create(table)
            .Line(table.Col<double>("x"), table.Col<float>("y"), l => l.Width(2))
            .ScaleX(Scale.Linear().WithTitle("Step").WithAxisSide(AxisSide.Top))
            .ScaleY(Scale.Linear().WithTitle("Value").WithAxisSide(AxisSide.Right).WithSecondaryAxis(new Times(10), "Value × 10"))
            .Coord(Coord.Cartesian(flip: true))
            .Title("Sides, flipped")
            .Build();
        Check(spec, pool, "axes-sides-flip.png");
    }

    private static void Check(PlotSpec spec, PlotWorkerPool pool, string baseline)
    {
        using var backend = D3D11Backend.CreateForTest(useWarp: true, createDevice: null, new TextEngine(new FakeTextBackend()));
        using var renderer = new HeadlessRenderer(backend, Width, Height, pool);
        renderer.Render(spec);

        GoldenImage.AssertMatches(renderer, GoldenImageTests.Baseline(baseline), GoldenImageTests.Tolerance, GoldenImageTests.Output);
    }

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
}
