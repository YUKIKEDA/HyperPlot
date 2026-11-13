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

    /// <summary>
    /// A layer of more than 131,072 rows on the index path (design §5.5.2 "索引の道"): its points stay on the GPU in pages and
    /// are placed from the cells' records, split by a category into parts with an index each.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 200,000 seeded points: half uniform over the panel, half in a normal blob; a category of two names by the half; the
    ///   fake text backend; Theme.Light.
    ///
    /// Steps:
    /// - Render at 640 × 360 discs of 2 DIPs at an opacity of 0.12 colored by the category; compare with
    ///   <c>Golden/points-indexed.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void Indexed_MatchesBaseline()
    {
        const int Count = 200_000;
        var random = new Random(13);
        var (xs, ys) = (new double[Count], new double[Count]);
        for (var i = 0; i < Count; i++)
        {
            if (i % 2 == 0)
            {
                (xs[i], ys[i]) = (random.NextDouble() * 100, random.NextDouble() * 10);
            }
            else
            {
                var r = 12 * Math.Sqrt(-2 * Math.Log(1 - random.NextDouble()));
                var a = 2 * Math.PI * random.NextDouble();
                (xs[i], ys[i]) = (60 + (r * Math.Cos(a)), 5 + (r * Math.Sin(a) / 10));
            }
        }

        string[] names = ["uniform", "blob"];
        using var pool = new PlotWorkerPool(2);
        using var x = Column.Wrap(xs);
        using var y = Column.Wrap(ys);
        var table = DataTable
            .Create()
            .Add("x", x)
            .Add("y", y)
            .Add("kind", Column.Categorical(Enumerable.Range(0, Count).Select(i => (string?)names[i % 2]).ToArray()))
            .Build();
        var spec = Plot.Create(table)
            .Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Size(2).Opacity(0.12).ColorBy(table.Cat("kind")))
            .ScaleX(Scale.Linear().WithDomain(0, 100).WithTitle("x"))
            .ScaleY(Scale.Linear().WithDomain(0, 10).WithTitle("Value"))
            .Title("Points from an index")
            .Build();

        Check(spec, pool, "points-indexed.png");
    }

    /// <summary>
    /// The resident points land where the CPU path puts the same points, to within half a pixel, when the view is zoomed
    /// deep into the index's range, alike on both axes and along x alone (design §5.5.2 "精度").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 12 points in a square a millionth of the range wide around (37, 61), and one point at each end of the range
    ///   [0, 100]²; the same 14 points again in columns of 150,000 rows whose other rows are NaN, so that the layer takes
    ///   the index path and the index holds the 14.
    /// - Three views: the whole range; the square on both axes; the square on x with the whole range on y.
    ///
    /// Steps:
    /// - For each view, render the 14-row layer (the CPU path) and the 150,000-row layer (the index path), opaque squares of
    ///   9 DIPs in one color, with the bare theme; read both.
    ///
    /// Expected:
    /// - The index path draws something in each view, and no channel of any pixel differs by more than 140 between the two:
    ///   a square moved by half a pixel changes its edge pixels by 128.
    /// </remarks>
    [Fact]
    public void Indexed_DeepAndUnevenZoom_MatchesTheCpuPath()
    {
        const int Rows = 150_000;
        const double Width = 100e-6;
        var random = new Random(17);
        var points = Enumerable
            .Range(0, 12)
            .Select(_ => (X: 37 + ((random.NextDouble() - 0.5) * Width), Y: 61 + ((random.NextDouble() - 0.5) * Width)))
            .Append((X: 0.0, Y: 0.0))
            .Append((X: 100.0, Y: 100.0))
            .ToArray();
        var (wideX, wideY) = (Enumerable.Repeat(double.NaN, Rows).ToArray(), Enumerable.Repeat(double.NaN, Rows).ToArray());
        for (var i = 0; i < points.Length; i++)
        {
            (wideX[i * 9_000], wideY[i * 9_000]) = points[i];
        }

        using var pool = new PlotWorkerPool(2);
        using var fewX = Column.Wrap(points.Select(p => p.X).ToArray());
        using var fewY = Column.Wrap(points.Select(p => p.Y).ToArray());
        using var manyX = Column.Wrap(wideX);
        using var manyY = Column.Wrap(wideY);
        var few = DataTable.Create().Add("x", fewX).Add("y", fewY).Build();
        var many = DataTable.Create().Add("x", manyX).Add("y", manyY).Build();
        var red = new Color(200, 30, 30, 255);
        ((double, double) X, (double, double) Y)[] views =
        [
            ((0, 100), (0, 100)),
            ((37 - Width, 37 + Width), (61 - Width, 61 + Width)),
            ((37 - Width, 37 + Width), (0, 100)),
        ];
        foreach (var view in views)
        {
            var cpu = Render(few);
            var indexed = Render(many);
            Assert.Contains(indexed.Chunk(4), p => p[0] != p[1]);
            var worst = 0;
            for (var i = 0; i < cpu.Length; i++)
            {
                worst = Math.Max(worst, Math.Abs(cpu[i] - indexed[i]));
            }

            Assert.True(worst <= 140, $"The view x {view.X}, y {view.Y}: a channel differs by {worst}.");

            byte[] Render(DataTable table)
            {
                var spec = Plot.Create(table)
                    .Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Shape(MarkerShape.Square).Size(9).Color(red))
                    .ScaleX(Scale.Linear().WithDomain(view.X.Item1, view.X.Item2))
                    .ScaleY(Scale.Linear().WithDomain(view.Y.Item1, view.Y.Item2))
                    .Build() with
                {
                    Theme = Theme.Light with { ShowAxes = false, ShowGrid = false, Padding = 0 },
                };
                using var backend = D3D11Backend.CreateForTest(useWarp: true, createDevice: null, new TextEngine(new FakeTextBackend()));
                using var renderer = new HeadlessRenderer(backend, 320, 240, pool);
                renderer.Render(spec);
                var pixels = new byte[320 * 240 * 4];
                renderer.ReadPixels(pixels);
                return pixels;
            }
        }
    }

    /// <summary>
    /// On an axis that clamps, the resident points outside the view are drawn at the view's end, where the CPU path draws
    /// the same points (design §5.5.2 "常駐のセルとページ").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 2,000 seeded points over [0, 100]²; the same points again in columns of 150,000 rows whose other rows are NaN, so
    ///   that the layer takes the index path.
    /// - The x domain is [200, 300] and clamps, so every point lies below it; the y domain is [0, 100].
    ///
    /// Steps:
    /// - Render the 2,000-row layer (the CPU path) and the 150,000-row layer (the index path), opaque squares of 9 DIPs in
    ///   one color, with the bare theme, 320 × 240; read both.
    ///
    /// Expected:
    /// - The index path draws something, and no channel of any pixel differs by more than 140 between the two.
    /// </remarks>
    [Fact]
    public void Indexed_ClampedAxis_MatchesTheCpuPath()
    {
        const int Rows = 150_000;
        const int Points = 2_000;
        var random = new Random(19);
        var (xs, ys) = (new double[Points], new double[Points]);
        var (wideX, wideY) = (Enumerable.Repeat(double.NaN, Rows).ToArray(), Enumerable.Repeat(double.NaN, Rows).ToArray());
        for (var i = 0; i < Points; i++)
        {
            (xs[i], ys[i]) = (random.NextDouble() * 100, random.NextDouble() * 100);
            (wideX[i * 70], wideY[i * 70]) = (xs[i], ys[i]);
        }

        using var pool = new PlotWorkerPool(2);
        using var fewX = Column.Wrap(xs);
        using var fewY = Column.Wrap(ys);
        using var manyX = Column.Wrap(wideX);
        using var manyY = Column.Wrap(wideY);
        var cpu = Render(DataTable.Create().Add("x", fewX).Add("y", fewY).Build());
        var indexed = Render(DataTable.Create().Add("x", manyX).Add("y", manyY).Build());

        Assert.Contains(indexed.Chunk(4), p => p[0] != p[1]);
        var worst = 0;
        for (var i = 0; i < cpu.Length; i++)
        {
            worst = Math.Max(worst, Math.Abs(cpu[i] - indexed[i]));
        }

        Assert.True(worst <= 140, $"A channel differs by {worst}.");

        byte[] Render(DataTable table)
        {
            var spec = Plot.Create(table)
                .Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Shape(MarkerShape.Square).Size(9).Color(new Color(200, 30, 30, 255)))
                .ScaleX(Scale.Linear().WithDomain(200, 300) with { Clamp = true })
                .ScaleY(Scale.Linear().WithDomain(0, 100))
                .Build() with
            {
                Theme = Theme.Light with { ShowAxes = false, ShowGrid = false, Padding = 0 },
            };
            using var backend = D3D11Backend.CreateForTest(useWarp: true, createDevice: null, new TextEngine(new FakeTextBackend()));
            using var renderer = new HeadlessRenderer(backend, 320, 240, pool);
            renderer.Render(spec);
            var pixels = new byte[320 * 240 * 4];
            renderer.ReadPixels(pixels);
            return pixels;
        }
    }

    private static void Check(PlotSpec spec, PlotWorkerPool pool, string baseline)
    {
        using var backend = D3D11Backend.CreateForTest(useWarp: true, createDevice: null, new TextEngine(new FakeTextBackend()));
        using var renderer = new HeadlessRenderer(backend, Width, Height, pool);
        renderer.Render(spec);

        GoldenImage.AssertMatches(renderer, GoldenImageTests.Baseline(baseline), GoldenImageTests.Tolerance, GoldenImageTests.Output);
    }
}
