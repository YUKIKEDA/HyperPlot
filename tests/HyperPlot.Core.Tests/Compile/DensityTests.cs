using HyperPlot.Interaction;
using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>The density of point layers on the index path and its switch from raw points (design §5.5.2 "行 R3b1 の密度と切り替え", §10 R3b1 row).</summary>
[Collection("Large data")]
public sealed class DensityTests
{
    private const int Over = LayerPlan.MaxElements + 1;
    private static readonly PanelId Panel = new(0, 0);

    /// <summary>
    /// The tile level is the deepest whose bins are at least a physical pixel on both axes, from 0 to 24; the counting level
    /// the shallowest whose cells are at most a pixel (design §5.5.2 "水準の選び方", "画面内の点の数").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - None.
    ///
    /// Steps:
    /// - Call TileLevel with ranges of 256, 511, 512, and 2^40 pixels, a range below 256, and two axes of 1,024 and 4,096;
    ///   PixelLevel with 1, 2, 3, and 1,000 pixels.
    ///
    /// Expected:
    /// - Tile levels 0, 0, 1, 24, 0, and 2 (the narrower axis decides). Pixel levels 0, 1, 2, and 10.
    /// </remarks>
    [Fact]
    public void Levels_FollowThePixels()
    {
        Assert.Equal(0, DensityMath.TileLevel(256, 256));
        Assert.Equal(0, DensityMath.TileLevel(511, 511));
        Assert.Equal(1, DensityMath.TileLevel(512, 512));
        Assert.Equal(24, DensityMath.TileLevel(Math.Pow(2, 40), Math.Pow(2, 40)));
        Assert.Equal(0, DensityMath.TileLevel(100, 100));
        Assert.Equal(2, DensityMath.TileLevel(4096, -1024));
        Assert.Equal([0, 1, 2, 10], new[] { 1.0, 2, 3, 1000 }.Select(p => DensityMath.PixelLevel(p, p)));
    }

    /// <summary>
    /// The image command's paint of a tile k levels shallower gives the position of c / 4^k under the top, for the three
    /// norms (design §5.5.2 "広さで正規化する"): the shader's formula, taken on the CPU, equals the CPU's position.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The shader's transforms as Image.hlsl writes them: none, symlog log10(1 + |v| / C), and pow |v|^C; t = (s − low) / (high − low).
    ///
    /// Steps:
    /// - For each norm, each k of 0, 1, and 3, and counts 0, 1, 7, and 1,000 against a top of 50: compute t through Paint and
    ///   through Position of c / 4^k.
    ///
    /// Expected:
    /// - Equal to within 1e-6, held to [0, 1].
    /// </remarks>
    [Fact]
    public void Paint_EqualsThePositionOfTheNormalizedCount()
    {
        foreach (var norm in Enum.GetValues<DensityNorm>())
        {
            foreach (var k in new[] { 0, 1, 3 })
            {
                foreach (var c in new[] { 0.0, 1, 7, 1000 })
                {
                    var (kind, constant, high) = DensityMath.Paint(norm, 50, k);
                    var s = kind switch
                    {
                        2 => Math.Log10(1 + (c / constant)),
                        3 => Math.Pow(c, constant),
                        _ => c,
                    };
                    var shader = Math.Clamp(s / high, 0, 1);
                    Assert.Equal(DensityMath.Position(norm, DensityMath.Normalized(c, k), 50), shader, 6);
                }
            }
        }
    }

    /// <summary>
    /// Auto draws a density when the upper count of the view passes the threshold, raw points when it falls under 0.8
    /// times it, and the last drawing between; Points and Density do not count (design §5.5.2 "入れ替わりを抑える").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 131,073 seeded points over [0, 100]² in an engine that waits for its indexes; a threshold of 10,000.
    ///
    /// Steps:
    /// - Draw the whole view; zoom to views holding about 9,000 (between 0.8 and 1 times), then about 5,000 points; zoom out
    ///   to about 9,000 again, then the whole view. Draw the whole view with Points and with Density.
    ///
    /// Expected:
    /// - Whole view: density. About 9,000: still density. About 5,000: raw points. About 9,000 again: still raw points. Whole
    ///   view: density. Points: raw points; Density: density, at any view.
    /// </remarks>
    [Fact]
    public void Auto_SwitchesWithABand()
    {
        var random = new Random(3);
        using var xs = Column.Wrap(Enumerable.Range(0, Over).Select(_ => random.NextDouble() * 100).ToArray());
        using var ys = Column.Wrap(Enumerable.Range(0, Over).Select(_ => random.NextDouble() * 100).ToArray());
        var table = DataTable.Create().Add("x", xs).Add("y", ys).Build();
        PlotSpec Spec(PointDraw draw) =>
            Plot.Create(table)
                .Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Draw(draw).Density(threshold: 10_000))
                .ScaleX(Scale.Linear().WithDomain(0, 100))
                .ScaleY(Scale.Linear().WithDomain(0, 100))
                .Build();

        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, 640, 360) { WaitsForIndexes = true };
        engine.SetPlot(Spec(PointDraw.Auto));
        bool Density(double side)
        {
            engine.Views.SetView(Panel, new PanelView2D(ViewRange.FromValues(10, 10 + side), ViewRange.FromValues(10, 10 + side)));
            backend.Images.Sent();
            engine.RenderFrame();
            return ((IndexedPointPlan)engine.Plan!.Layers[0]).DrewDensity;
        }

        // About 131,073 · (side / 100)² points: 9,000 at a side of 26.2, 5,000 at 19.5.
        Assert.True(Density(100));
        Assert.True(Density(26.2));
        Assert.False(Density(19.5));
        Assert.False(Density(26.2));
        Assert.True(Density(100));

        engine.SetPlot(Spec(PointDraw.Points));
        Assert.False(Density(100));
        engine.SetPlot(Spec(PointDraw.Density));
        Assert.True(Density(5));
    }

    /// <summary>
    /// A density records image instances, from the drawn tiles of the view, with a top that is the largest normalized bin
    /// and no resident run (design §5.5.2 "描き方").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 400,000 seeded points over [0, 100]² (tiles exist above the density split), Density, an engine that waits.
    ///
    /// Steps:
    /// - Draw the whole view until it is not provisional.
    ///
    /// Expected:
    /// - Image records and no resident run; the switch's top is above 0; every record's flags carry the zero-is-transparent
    ///   bit and the symlog transform (Log, the default).
    /// </remarks>
    [Fact]
    public void Density_RecordsImages()
    {
        const int Count = 400_000;
        var random = new Random(5);
        using var xs = Column.Wrap(Enumerable.Range(0, Count).Select(_ => random.NextDouble() * 100).ToArray());
        using var ys = Column.Wrap(Enumerable.Range(0, Count).Select(_ => random.NextDouble() * 100).ToArray());
        var table = DataTable.Create().Add("x", xs).Add("y", ys).Build();
        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, 640, 360) { WaitsForIndexes = true };
        engine.SetPlot(
            Plot.Create(table)
                .Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Draw(PointDraw.Density))
                .ScaleX(Scale.Linear().WithDomain(0, 100))
                .ScaleY(Scale.Linear().WithDomain(0, 100))
                .Build()
        );
        for (var i = 0; i < 50 && (i == 0 || engine.IsProvisional); i++)
        {
            backend.Images.Sent();
            engine.RenderFrame();
        }

        var list = backend.LastList!;
        Assert.Equal(0, list.ResidentRuns.Length);
        Assert.NotEqual(0, list.Images.Length);
        var plan = (IndexedPointPlan)engine.Plan!.Layers[0];
        Assert.True(plan.Switch.Top > 0);
        Assert.All(list.Images.ToArray(), image => Assert.Equal(ImageInstance.ZeroTransparent | (2u << 2), image.Flags & (ImageInstance.ZeroTransparent | (3u << 2))));
    }
}
