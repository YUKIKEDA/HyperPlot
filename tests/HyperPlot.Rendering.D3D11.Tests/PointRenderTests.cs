using HyperPlot.Testing;
using HyperPlot.Tests;

namespace HyperPlot.Rendering.D3D11.Tests;

/// <summary>Row R3a's point layers on the CPU path drawn on WARP (design §5.5.2, §10 R3a row), with the fake text backend.</summary>
public sealed class PointRenderTests
{
    private const int Width = 640;
    private const int Height = 360;
    private const int Points = 240;

    /// <summary>
    /// The three shapes, opaque and half-transparent, and a layer split by a category with its legend (design §5.5.2 "見た目").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 240 seeded points in three bands of y; a category of three names; the fake text backend; Theme.Light.
    ///
    /// Steps:
    /// - Render at 640 × 360 a layer of opaque discs of 6 DIPs, one of squares of 9 DIPs at an opacity of 0.4, one of crosses
    ///   of 10 DIPs in a set color, and one of discs of 7 DIPs colored by the category; compare with <c>Golden/points.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void Shapes_MatchBaseline()
    {
        var random = new Random(7);
        double[] Band(double middle) => Enumerable.Range(0, Points).Select(_ => middle + ((random.NextDouble() - 0.5) * 1.6)).ToArray();
        var xs = Enumerable.Range(0, Points).Select(_ => random.NextDouble() * 100).ToArray();
        string[] names = ["north", "south", "west"];
        using var pool = new PlotWorkerPool(2);
        using var x = Column.Wrap(xs);
        using var discs = Column.Wrap(Band(1));
        using var squares = Column.Wrap(Band(3));
        using var crosses = Column.Wrap(Band(5));
        using var split = Column.Wrap(Band(7));
        var table = DataTable
            .Create()
            .Add("x", x)
            .Add("discs", discs)
            .Add("squares", squares)
            .Add("crosses", crosses)
            .Add("split", split)
            .Add("region", Column.Categorical(Enumerable.Range(0, Points).Select(i => (string?)names[i % 3]).ToArray()))
            .Build();
        var rx = table.Col<double>("x");
        var spec = Plot.Create(table)
            .Point(rx, table.Col<double>("discs"), p => p.Label("discs"))
            .Point(rx, table.Col<double>("squares"), p => p.Shape(MarkerShape.Square).Size(9).Opacity(0.4).Label("squares"))
            .Point(rx, table.Col<double>("crosses"), p => p.Shape(MarkerShape.Cross).Size(10).Color(new Color(214, 39, 40, 255)).Label("crosses"))
            .Point(rx, table.Col<double>("split"), p => p.Size(7).ColorBy(table.Cat("region")))
            .ScaleY(Scale.Linear().WithDomain(0, 8).WithTitle("Value"))
            .ScaleX(Scale.Linear().WithTitle("x"))
            .Title("Points")
            .Build();

        Check(spec, pool, "points.png");
    }

    /// <summary>A flipped layer of points colored by a number, with the color bar (design §5.5.2 "CPU の道").</summary>
    /// <remarks>
    /// Preconditions:
    /// - 240 seeded points; a value column of a sine of x; the fake text backend; Theme.Light.
    ///
    /// Steps:
    /// - Render at 640 × 360, flipped, discs of 8 DIPs at an opacity of 0.8 colored through Viridis; compare with
    ///   <c>Golden/points-color-flip.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void ColorAndFlip_MatchBaseline()
    {
        var random = new Random(11);
        var xs = Enumerable.Range(0, Points).Select(_ => random.NextDouble() * 100).ToArray();
        var ys = xs.Select(v => (Math.Sin(v / 12) * 3) + (random.NextDouble() * 2)).ToArray();
        using var pool = new PlotWorkerPool(2);
        using var x = Column.Wrap(xs);
        using var y = Column.Wrap(ys);
        using var value = Column.Wrap(xs.Select(v => Math.Sin(v / 12) * 10).ToArray());
        var table = DataTable.Create().Add("x", x).Add("y", y).Add("v", value).Build();
        var spec = Plot.Create(table)
            .Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Size(8).Opacity(0.8).ColorBy(table.Col<double>("v")))
            .ScaleColor(Scale.Sequential(ColorMaps.Viridis, Scale.Linear().WithTitle("Value")))
            .Coord(Coord.Cartesian(flip: true))
            .Title("Points by value, flipped")
            .Build();

        Check(spec, pool, "points-color-flip.png");
    }

    /// <summary>Points in polar coordinates: each on its angle and radius (design §5.5.2 "CPU の道").</summary>
    /// <remarks>
    /// Preconditions:
    /// - 60 points on a spiral of x from 0 to 100 and y from 1 to 9; the fake text backend; Theme.Light.
    ///
    /// Steps:
    /// - Render at 640 × 360 in polar coordinates, squares of 6 DIPs; compare with <c>Golden/points-polar.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void Polar_MatchesBaseline()
    {
        using var pool = new PlotWorkerPool(2);
        const int Count = 60;
        using var y = Column.Wrap(Enumerable.Range(0, Count).Select(i => 1 + (8.0 * i / Count)).ToArray());
        var table = DataTable.Create().Add("x", Column.Uniform(0, 100.0 / Count, Count)).Add("y", y).Build();
        var spec = Plot.Create(table)
            .Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Shape(MarkerShape.Square).Size(6))
            .ScaleY(Scale.Linear().WithDomain(0, 10))
            .Coord(Coord.Polar())
            .Title("Points, polar")
            .Build();

        Check(spec, pool, "points-polar.png");
    }

    private static void Check(PlotSpec spec, PlotWorkerPool pool, string baseline)
    {
        using var backend = D3D11Backend.CreateForTest(useWarp: true, createDevice: null, new TextEngine(new FakeTextBackend()));
        using var renderer = new HeadlessRenderer(backend, Width, Height, pool);
        renderer.Render(spec);

        GoldenImage.AssertMatches(renderer, GoldenImageTests.Baseline(baseline), GoldenImageTests.Tolerance, GoldenImageTests.Output);
    }
}
