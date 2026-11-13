using HyperPlot.Testing;
using HyperPlot.Tests;

namespace HyperPlot.Rendering.D3D11.Tests;

/// <summary>
/// Tests that build columns and spatial indexes of up to 1e7 points, hundreds of megabytes; they run apart from the others
/// (.cursor/rules/testing.mdc).
/// </summary>
[CollectionDefinition(DensityRenderTests.Collection, DisableParallelization = true)]
public sealed class LargeDataGroup;

/// <summary>
/// Row R3b1's densities of point layers on the index path drawn through the whole engine on WARP (design §5.5.2 "行 R3b1 の
/// 密度と切り替え", §10's R3b1 row), with the fake text backend.
/// </summary>
[Collection(Collection)]
public sealed class DensityRenderTests
{
    /// <summary>The name of the collection of the tests of large data.</summary>
    internal const string Collection = "Large data";

    private const int Large = 10_000_000;

    /// <summary>
    /// A density of 1e7 points spread evenly, the whole view, through log and shades of the layer's color (design §5.5.2
    /// "描き方").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 1e7 seeded float points spread evenly over [0, 100]², generated a chunk at a time into native columns; Draw is
    ///   Density, the norm Log, no map; domains [0, 100] on both axes; the fake text backend; Theme.Light.
    ///
    /// Steps:
    /// - Render at 640 × 360 with a renderer that waits for the index; compare with <c>Golden/density-uniform.png</c>.
    ///
    /// Expected:
    /// - The layer drew a density from the root's tile: the plot area is under 512 pixels high, so the tile level is 0, and
    ///   every piece is the root read from its own tile.
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void Uniform_MatchesBaseline()
    {
        using var pool = new PlotWorkerPool(4);
        using var x = Column.CreateNative<float>(Large, (first, chunk) => Fill(chunk, first, Uniform, ofX: true));
        using var y = Column.CreateNative<float>(Large, (first, chunk) => Fill(chunk, first, Uniform, ofX: false));
        var table = DataTable.Create().Add("x", x).Add("y", y).Build();
        var spec = Plot.Create(table)
            .Point(table.Col<float>("x"), table.Col<float>("y"), p => p.Draw(PointDraw.Density))
            .ScaleX(Scale.Linear().WithDomain(0, 100).WithTitle("x"))
            .ScaleY(Scale.Linear().WithDomain(0, 100).WithTitle("y"))
            .Title("Density, 1e7 points spread evenly")
            .Build();

        Check(spec, pool, 640, 360, "density-uniform.png", Drew);

        static void Drew(HeadlessRenderer renderer)
        {
            var part = (IndexedPointPlan)renderer.Plan!.Layers[0];
            Assert.True(part.DrewDensity);
            Assert.Equal(0, part.TileLevel);
            var pieces = part.Pieces.ToArray();
            Assert.NotEmpty(pieces);
            Assert.All(pieces, piece => Assert.Equal((0, 0), (piece.CellLevel, piece.TileLevel)));
        }
    }

    /// <summary>
    /// A density of 1e7 points in two clusters over a thin background, the whole view, through log and shades of the
    /// layer's color, where the cells of level 1 with too few points for a tile of their own draw their part of the root's
    /// tile normalized by 4 (design §5.5.2 "水準の選び方", "広さで正規化する", "祖先のタイル").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 1e7 seeded float points over [0, 100]²: 60 % in a normal cluster at (28, 70) of deviations (8, 6), 35 % in one at
    ///   (72, 28) of deviations (6, 8), and 5 % spread evenly; a cluster's point outside [0, 100]² is taken evenly instead.
    ///   The lower-left and upper-right quarters of the range hold about 130,000 and 155,000 points, under the density
    ///   split of 262,144, so their cells of level 1 have no tile; the other two have one.
    /// - Draw is Density, the norm Log, no map; domains [0, 100]; the fake text backend; Theme.Light.
    ///
    /// Steps:
    /// - Render at 1,280 × 720 with a renderer that waits for the index; compare with <c>Golden/density-clustered.png</c>.
    ///
    /// Expected:
    /// - The layer drew a density at tile level 1 (the plot area is at least 512 pixels and under 1,024 high): some cells of
    ///   level 1 are read from their own tile and some from the root's tile of level 0.
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void Clustered_MatchesBaseline()
    {
        using var pool = new PlotWorkerPool(4);
        using var x = Column.CreateNative<float>(Large, (first, chunk) => Fill(chunk, first, Clustered, ofX: true));
        using var y = Column.CreateNative<float>(Large, (first, chunk) => Fill(chunk, first, Clustered, ofX: false));
        var table = DataTable.Create().Add("x", x).Add("y", y).Build();
        var spec = Plot.Create(table)
            .Point(table.Col<float>("x"), table.Col<float>("y"), p => p.Draw(PointDraw.Density))
            .ScaleX(Scale.Linear().WithDomain(0, 100).WithTitle("x"))
            .ScaleY(Scale.Linear().WithDomain(0, 100).WithTitle("y"))
            .Title("Density, 1e7 points in two clusters")
            .Build();

        Check(spec, pool, 1280, 720, "density-clustered.png", Drew);

        static void Drew(HeadlessRenderer renderer)
        {
            var part = (IndexedPointPlan)renderer.Plan!.Layers[0];
            Assert.True(part.DrewDensity);
            Assert.Equal(1, part.TileLevel);
            var pieces = part.Pieces.ToArray();
            Assert.Contains(pieces, piece => piece is { CellLevel: 1, TileLevel: 1 });
            Assert.Contains(pieces, piece => piece is { CellLevel: 1, TileLevel: 0 });
        }
    }

    /// <summary>
    /// A density of the same 1e7 clustered points through a color map, viridis, in place of the layer's shades (design
    /// §5.5.2 "描き方"): the empty bins stay transparent, not viridis's first color.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The clustered points of <see cref="Clustered_MatchesBaseline"/>; Draw is Density, the norm Log, the map viridis;
    ///   domains [0, 100]; the fake text backend; Theme.Light.
    ///
    /// Steps:
    /// - Render at 640 × 360 with a renderer that waits for the index; compare with <c>Golden/density-viridis.png</c>.
    ///
    /// Expected:
    /// - The layer drew a density.
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void Viridis_MatchesBaseline()
    {
        using var pool = new PlotWorkerPool(4);
        using var x = Column.CreateNative<float>(Large, (first, chunk) => Fill(chunk, first, Clustered, ofX: true));
        using var y = Column.CreateNative<float>(Large, (first, chunk) => Fill(chunk, first, Clustered, ofX: false));
        var table = DataTable.Create().Add("x", x).Add("y", y).Build();
        var spec = Plot.Create(table)
            .Point(table.Col<float>("x"), table.Col<float>("y"), p => p.Draw(PointDraw.Density).Density(DensityNorm.Log, ColorMaps.Viridis))
            .ScaleX(Scale.Linear().WithDomain(0, 100).WithTitle("x"))
            .ScaleY(Scale.Linear().WithDomain(0, 100).WithTitle("y"))
            .Title("Density through viridis")
            .Build();

        Check(spec, pool, 640, 360, "density-viridis.png", Drew);

        static void Drew(HeadlessRenderer renderer) => Assert.True(((IndexedPointPlan)renderer.Plan!.Layers[0]).DrewDensity);
    }

    /// <summary>
    /// A layer split by a category of three drawn as densities: each part from its own index and tiles, in shades of its
    /// category's color, drawn over each other in the parts' order, under one top (design §5.5.2 "分けた層", "濃さの上端 m").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 900,000 seeded float points, the categories "alpha", "beta", and "gamma" in runs of 300,000 rows, each a normal
    ///   cluster of deviation 12 at (35, 45), (55, 60), and (62, 35), overlapping; a point outside [0, 100]² is taken evenly
    ///   instead. 300,000 is a round count just above the density split of 262,144, so that each part's root has a tile;
    ///   1e7 points would only make the test slower.
    /// - Colored by the category; Draw is Density, the norm Log, no map; domains [0, 100]; the fake text backend;
    ///   Theme.Light.
    ///
    /// Steps:
    /// - Render at 640 × 360 with a renderer that waits for the indexes; compare with <c>Golden/density-split.png</c>.
    ///
    /// Expected:
    /// - Three parts, each of which drew a density with the root's tile among its pieces.
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void Split_MatchesBaseline()
    {
        const int Run = 300_000;
        const int Count = 3 * Run;
        string[] names = ["alpha", "beta", "gamma"];
        using var pool = new PlotWorkerPool(4);
        using var x = Column.CreateNative<float>(Count, (first, chunk) => Fill(chunk, first, row => Blobs(row, Run), ofX: true));
        using var y = Column.CreateNative<float>(Count, (first, chunk) => Fill(chunk, first, row => Blobs(row, Run), ofX: false));
        var table = DataTable
            .Create()
            .Add("x", x)
            .Add("y", y)
            .Add("kind", Column.Categorical(Enumerable.Range(0, Count).Select(i => (string?)names[i / Run]).ToArray()))
            .Build();
        var spec = Plot.Create(table)
            .Point(table.Col<float>("x"), table.Col<float>("y"), p => p.Draw(PointDraw.Density).ColorBy(table.Cat("kind")))
            .ScaleX(Scale.Linear().WithDomain(0, 100).WithTitle("x"))
            .ScaleY(Scale.Linear().WithDomain(0, 100).WithTitle("y"))
            .Title("Density split by a category")
            .Build();

        Check(spec, pool, 640, 360, "density-split.png", Drew);

        static void Drew(HeadlessRenderer renderer)
        {
            var parts = ((SplitPlan)renderer.Plan!.Layers[0]).Parts.Cast<IndexedPointPlan>().ToArray();
            Assert.Equal(3, parts.Length);
            foreach (var part in parts)
            {
                Assert.True(part.DrewDensity);
                Assert.Contains(part.Pieces.ToArray(), piece => piece.TileLevel == 0);
            }
        }
    }

    private static void Check(PlotSpec spec, PlotWorkerPool pool, int width, int height, string baseline, Action<HeadlessRenderer> drew)
    {
        using var backend = D3D11Backend.CreateForTest(useWarp: true, createDevice: null, new TextEngine(new FakeTextBackend()));
        using var renderer = new HeadlessRenderer(backend, width, height, pool);
        renderer.Render(spec);
        drew(renderer);

        GoldenImage.AssertMatches(renderer, GoldenImageTests.Baseline(baseline), GoldenImageTests.Tolerance, GoldenImageTests.Output);
    }

    /// <summary>
    /// Writes the x or the y of the rows of a chunk from a function of the row alone, so that a chunk holds the same values
    /// whichever order the chunks are written in.
    /// </summary>
    private static void Fill(Span<float> chunk, long first, Func<long, (double X, double Y)> point, bool ofX)
    {
        for (var i = 0; i < chunk.Length; i++)
        {
            var (x, y) = point(first + i);
            chunk[i] = (float)(ofX ? x : y);
        }
    }

    /// <summary>Returns a row's point spread evenly over [0, 100]².</summary>
    private static (double X, double Y) Uniform(long row) => (100 * Unit(row, 0), 100 * Unit(row, 1));

    /// <summary>Returns a row's point of the two clusters over a thin background (<see cref="Clustered_MatchesBaseline"/>).</summary>
    private static (double X, double Y) Clustered(long row)
    {
        var pick = Unit(row, 2);
        return pick < 0.60 ? Normal(row, 28, 70, 8, 6) : pick < 0.95 ? Normal(row, 72, 28, 6, 8) : Uniform(row);
    }

    /// <summary>Returns a row's point of the three overlapping clusters, one a run of rows (<see cref="Split_MatchesBaseline"/>).</summary>
    private static (double X, double Y) Blobs(long row, int run)
    {
        return (row / run) switch
        {
            0 => Normal(row, 35, 45, 12, 12),
            1 => Normal(row, 55, 60, 12, 12),
            _ => Normal(row, 62, 35, 12, 12),
        };
    }

    /// <summary>Returns a row's point of a normal cluster (Box–Muller), or one spread evenly when it falls outside [0, 100]².</summary>
    private static (double X, double Y) Normal(long row, double x, double y, double deviationX, double deviationY)
    {
        var radius = Math.Sqrt(-2 * Math.Log(1 - Unit(row, 3)));
        var angle = 2 * Math.PI * Unit(row, 4);
        var point = (X: x + (deviationX * radius * Math.Cos(angle)), Y: y + (deviationY * radius * Math.Sin(angle)));
        return point.X is >= 0 and <= 100 && point.Y is >= 0 and <= 100 ? point : Uniform(row);
    }

    /// <summary>Returns a seeded number in [0, 1) for a row and a stream: SplitMix64's mix of the pair, its top 53 bits.</summary>
    private static double Unit(long row, int stream)
    {
        var z = ((ulong)row * 8) + (ulong)stream + 0x9E3779B97F4A7C15;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
        z ^= z >> 31;
        return (z >> 11) * (1.0 / (1UL << 53));
    }
}
