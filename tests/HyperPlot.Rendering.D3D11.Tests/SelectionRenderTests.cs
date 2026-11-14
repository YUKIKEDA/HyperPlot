using System.Numerics;
using HyperPlot.Interaction;
using HyperPlot.Testing;
using HyperPlot.Tests;

namespace HyperPlot.Rendering.D3D11.Tests;

/// <summary>
/// Row R3c's highlight of a selection over point layers on the index path, drawn on WARP (design §6.4 "散布の選択", §10's R3c
/// row, §10.1 (55) and (56)), with the fake text backend.
/// </summary>
[Collection(DensityRenderTests.Collection)]
public sealed class SelectionRenderTests
{
    private const int Width = 640;
    private const int Height = 360;
    private static readonly PanelId Panel = new(0, 0);
    private static readonly Theme Bare = Theme.Light with { ShowAxes = false, ShowGrid = false, Padding = 0 };

    // A lasso of five vertices with a notch from its top, as shares of its ranges.
    private static readonly Vector2[] Notched = [new(0, 0), new(1, 0), new(1, 1), new(0.5f, 0.3f), new(0, 1)];

    /// <summary>The kinds of predicate a test selects with.</summary>
    public enum Predicate
    {
        /// <summary>A rectangle: four constants.</summary>
        Rectangle,

        /// <summary>A lasso: the panel's mask.</summary>
        Lasso,
    }

    /// <summary>
    /// The vertex shader fades the resident markers whose drawn center lies outside the selection's region to 0.2 of their
    /// opacity, the same markers the CPU finds with the shader's float placing and the list's region, whole and uncut (design
    /// §6.4 "生の点の強調", Issue #471 Decisions 6, 12, and 18).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 131,073 seeded points over [0, 100]², opaque black squares of 3 pixels, raw points; a theme without axes, grid, or
    ///   padding; domains [0, 10]², so that about 1,300 points are in view; 640 × 360 on WARP, a renderer that waits.
    ///
    /// Steps:
    /// - Render and read the pixels. Give the plan the rectangle [2.3, 7.1] × [3.2, 6.6], or the notched lasso over [1.5, 8.5]
    ///   × [1, 9]; render and read again.
    /// - Find each point's drawn center from its resident cell's record and its units, as the vertex shader does in floats;
    ///   judge it with the list's region. Keep the points 8 pixels from any other and from the target's sides, and those not
    ///   within a thousandth of a pixel of the region's edge.
    /// - For each, sum the darkness of the red channel over the 7 × 7 pixels around its center in both images.
    ///
    /// Expected:
    /// - Each marker's darkness with the selection over its darkness without is 1 where the region holds its center and 0.2
    ///   where it does not, within 0.05; both kinds are met, more than 20 points each.
    /// </remarks>
    [Theory]
    [InlineData(Predicate.Rectangle)]
    [InlineData(Predicate.Lasso)]
    public void ResidentMarkers_FadeOutsideTheRegion_AsTheCpuJudges(Predicate predicate)
    {
        const int Count = LayerPlan.MaxElements + 1;
        var random = new Random(41);
        using var x = Column.Wrap(Enumerable.Range(0, Count).Select(_ => random.NextDouble() * 100).ToArray());
        using var y = Column.Wrap(Enumerable.Range(0, Count).Select(_ => random.NextDouble() * 100).ToArray());
        var table = DataTable.Create().Add("x", x).Add("y", y).Build();
        var spec = Plot.Create(table)
            .Point(
                table.Col<double>("x"),
                table.Col<double>("y"),
                p => p.Draw(PointDraw.Points).Size(3).Shape(MarkerShape.Square).Color(new Color(0, 0, 0, 255))
            )
            .ScaleX(Scale.Linear().WithDomain(0, 10))
            .ScaleY(Scale.Linear().WithDomain(0, 10))
            .Build() with
        {
            Theme = Bare,
        };
        using var pool = new PlotWorkerPool(4);
        using var backend = D3D11Backend.CreateForTest(useWarp: true, createDevice: null, new TextEngine(new FakeTextBackend()));
        using var renderer = new HeadlessRenderer(backend, Width, Height, pool);
        var (plain, selected) = (new byte[Width * Height * 4], new byte[Width * Height * 4]);
        renderer.Render(spec);
        renderer.ReadPixels(plain);
        var selection =
            predicate == Predicate.Rectangle
                ? Selection2D.Rectangle(Panel, ViewRange.FromValues(2.3, 7.1), ViewRange.FromValues(3.2, 6.6))
                : Selection2D.Lasso(Panel, ViewRange.FromValues(1.5, 8.5), ViewRange.FromValues(1.0, 9.0), Notched);
        renderer.Plan!.SetSelection(selection, []);
        renderer.Render(spec);
        renderer.ReadPixels(selected);

        var list = renderer.Plan.List;
        var centers = DrawnCenters(renderer, list);
        var background = Bare.PanelBackground.R;
        var (kept, faded) = (0, 0);
        foreach (var center in centers)
        {
            if (!Alone(center, centers) || NearEdge(list, center))
            {
                continue;
            }

            var (with, without) = (Darkness(selected, center, background), Darkness(plain, center, background));
            var inside = list.Holds(1, center);
            Assert.True(without > 1000, $"The marker at {center} is not drawn: {without}.");
            Assert.InRange(with / without, inside ? 0.95 : 0.15, inside ? 1.05 : 0.25);
            (kept, faded) = inside ? (kept + 1, faded) : (kept, faded + 1);
        }

        Assert.True(kept > 20 && faded > 20, $"{kept} markers kept and {faded} faded.");

        bool Alone(Vector2 center, List<Vector2> all) =>
            center.X >= 8
            && center.Y >= 8
            && center.X < Width - 8
            && center.Y < Height - 8
            && all.Count(other => Math.Abs(other.X - center.X) < 8 && Math.Abs(other.Y - center.Y) < 8) == 1;
    }

    /// <summary>
    /// Golden image (55) of §10.1: raw points on the index path with a rectangle selected in the middle of the panel, the
    /// points outside it faded to 0.2 of their opacity and the range's fill over them (design §6.4 "見た目").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 200,000 seeded points spread evenly over [0, 100]², points of 2 DIP at opacity 0.6; domains [0, 100]; the fake text
    ///   backend; Theme.Light; 640 × 360 on WARP; an engine that waits for its indexes.
    ///
    /// Steps:
    /// - Draw, select the rectangle [30, 70] × [25, 75], draw until not provisional, read the pixels; compare with
    ///   <c>Golden/points-selection.png</c>.
    ///
    /// Expected:
    /// - The layer drew raw points; the image matches the baseline.
    /// </remarks>
    [Fact]
    public void PointsSelection_MatchesBaseline()
    {
        const int Count = 200_000;
        var random = new Random(42);
        using var x = Column.Wrap(Enumerable.Range(0, Count).Select(_ => random.NextDouble() * 100).ToArray());
        using var y = Column.Wrap(Enumerable.Range(0, Count).Select(_ => random.NextDouble() * 100).ToArray());
        var table = DataTable.Create().Add("x", x).Add("y", y).Build();
        var spec = Plot.Create(table)
            .Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Draw(PointDraw.Points).Size(2).Opacity(0.6))
            .ScaleX(Scale.Linear().WithDomain(0, 100).WithTitle("x"))
            .ScaleY(Scale.Linear().WithDomain(0, 100).WithTitle("y"))
            .Title("Points of a rectangle selected")
            .Build();

        var pixels = Selected(spec, Selection2D.Rectangle(Panel, ViewRange.FromValues(30.0, 70.0), ViewRange.FromValues(25.0, 75.0)), out var drew);
        Assert.False(drew);
        GoldenImage.AssertMatches(
            pixels,
            Width,
            Height,
            GoldenImageTests.Baseline("points-selection.png"),
            GoldenImageTests.Tolerance,
            GoldenImageTests.Output
        );
    }

    /// <summary>
    /// Golden image (56) of §10.1: a density on the index path with a lasso of five vertices selected, the density inside the
    /// polygon as it is, outside it faded to 0.2 of its opacity, cut at the pixels, and the polygon's fill over it (design §6.4
    /// "密度の強調").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 200,000 seeded points in two clusters, of deviation 9 at (32, 62) and of deviation 7 at (68, 35), a cluster's point
    ///   outside [0, 100]² taken evenly instead; Draw is Density; domains [0, 100]; the fake text backend; Theme.Light;
    ///   640 × 360 on WARP; an engine that waits for its indexes.
    ///
    /// Steps:
    /// - Draw, select the notched lasso over [15, 85] × [15, 90], draw until not provisional, read the pixels; compare with
    ///   <c>Golden/density-selection.png</c>.
    ///
    /// Expected:
    /// - The layer drew a density; the image matches the baseline.
    /// </remarks>
    [Fact]
    public void DensitySelection_MatchesBaseline()
    {
        const int Count = 200_000;
        var random = new Random(43);
        var points = Enumerable.Range(0, Count).Select(i => Cluster(random, i % 2 == 0)).ToArray();
        using var x = Column.Wrap(points.Select(p => p.X).ToArray());
        using var y = Column.Wrap(points.Select(p => p.Y).ToArray());
        var table = DataTable.Create().Add("x", x).Add("y", y).Build();
        var spec = Plot.Create(table)
            .Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Draw(PointDraw.Density))
            .ScaleX(Scale.Linear().WithDomain(0, 100).WithTitle("x"))
            .ScaleY(Scale.Linear().WithDomain(0, 100).WithTitle("y"))
            .Title("A density of a lasso selected")
            .Build();

        var pixels = Selected(
            spec,
            Selection2D.Lasso(Panel, ViewRange.FromValues(15.0, 85.0), ViewRange.FromValues(15.0, 90.0), Notched),
            out var drew
        );
        Assert.True(drew);
        GoldenImage.AssertMatches(
            pixels,
            Width,
            Height,
            GoldenImageTests.Baseline("density-selection.png"),
            GoldenImageTests.Tolerance,
            GoldenImageTests.Output
        );

        static (double X, double Y) Cluster(Random random, bool first)
        {
            var (cx, cy, deviation) = first ? (32.0, 62.0, 9.0) : (68.0, 35.0, 7.0);
            var radius = Math.Sqrt(-2 * Math.Log(1 - random.NextDouble()));
            var angle = 2 * Math.PI * random.NextDouble();
            var point = (X: cx + (deviation * radius * Math.Cos(angle)), Y: cy + (deviation * radius * Math.Sin(angle)));
            return point.X is >= 0 and <= 100 && point.Y is >= 0 and <= 100 ? point : (random.NextDouble() * 100, random.NextDouble() * 100);
        }
    }

    // Draws a plot through an engine on WARP with a selection, until a frame is not provisional, and reads it.
    private static byte[] Selected(PlotSpec spec, Selection2D selection, out bool density)
    {
        using var pool = new PlotWorkerPool(4);
        using var backend = D3D11Backend.CreateForTest(useWarp: true, createDevice: null, new TextEngine(new FakeTextBackend()));
        using var engine = new PlotEngine(backend, Width, Height, 1, pool) { WaitsForIndexes = true };
        engine.SetPlot(spec);
        engine.RenderFrame();
        engine.Select(selection);
        for (var i = 0; i < 200 && (i == 0 || engine.IsProvisional); i++)
        {
            engine.RenderFrame();
        }

        Assert.False(engine.IsProvisional);
        density = ((IndexedPointPlan)engine.Plan!.Layers[0]).DrewDensity;
        var pixels = new byte[Width * Height * 4];
        engine.ReadPixels(pixels);
        return pixels;
    }

    // The center of each point of the plan's one part as the vertex shader places it: its cell's record of this frame plus its
    // units from the record's base times the record's scale, in floats (Markers.hlsl, ResidentMarkerVS); no axis clamps.
    private static List<Vector2> DrawnCenters(HeadlessRenderer renderer, RenderList list)
    {
        var plan = (IndexedPointPlan)renderer.Plan!.Layers[0];
        var snapshot = plan.SpatialIndex!.Acquire();
        try
        {
            var points = new ResidentPoint[snapshot.PointCount];
            snapshot.ReadBlock(0, points);
            var records = list.ResidentCells;
            var first = list.ResidentRuns[0].Cells;
            var centers = new List<Vector2>(points.Length);
            foreach (var point in points)
            {
                var record = records[first + (int)point.Cell];
                centers.Add(
                    new Vector2(
                        record.PixelX + (UnitsFrom(point.X, record.BaseX) * record.ScaleX),
                        record.PixelY + (UnitsFrom(point.Y, record.BaseY) * record.ScaleY)
                    )
                );
            }

            return centers;
        }
        finally
        {
            snapshot.Release();
        }

        static float UnitsFrom(uint position, uint origin) => position >= origin ? position - origin : -(float)(origin - position);
    }

    // Whether the region's judgment changes within a thousandth of a pixel of a center: a float the GPU rounds otherwise may
    // fall on the other side (Issue #471 Decision 18).
    private static bool NearEdge(RenderList list, Vector2 center)
    {
        const float Step = 1e-3f;
        var holds = list.Holds(1, center);
        return list.Holds(1, center + new Vector2(Step, 0)) != holds
            || list.Holds(1, center - new Vector2(Step, 0)) != holds
            || list.Holds(1, center + new Vector2(0, Step)) != holds
            || list.Holds(1, center - new Vector2(0, Step)) != holds;
    }

    // The red channel's darkness against the panel's background, summed over the 7 × 7 pixels around a center.
    private static double Darkness(byte[] pixels, Vector2 center, byte background)
    {
        var (cx, cy) = ((int)MathF.Floor(center.X), (int)MathF.Floor(center.Y));
        var sum = 0.0;
        for (var py = cy - 3; py <= cy + 3; py++)
        {
            for (var px = cx - 3; px <= cx + 3; px++)
            {
                sum += background - pixels[((py * Width) + px) * 4];
            }
        }

        return sum;
    }
}
