using System.Numerics;
using HyperPlot.Samples;
using HyperPlot.Testing;

namespace HyperPlot.Rendering.D3D11.Tests;

/// <summary>
/// The WARP golden images of row S3 (design §10.1): the whole path from recording to read back, compared with the baselines in
/// <c>Golden/</c>. A run with <c>HYPERPLOT_UPDATE_GOLDEN=1</c> rewrites them; a failed run leaves the image drawn and the
/// difference in <c>golden-output/</c> beside the test binary, which CI uploads.
/// </summary>
public sealed class GoldenImageTests
{
    // Measured between this machine's WARP and the CI runner's (docs/benchmarks/s3-golden.md).
    internal static readonly ImageTolerance Tolerance = ImageTolerance.Exact;

    internal static readonly string Output = Path.Combine(AppContext.BaseDirectory, "golden-output");

    /// <summary>
    /// The scene of <c>HelloPlot</c>, drawn with 1e5 points, matches its baseline (design §10.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend; a pool of its own; the scene recorded by the code HelloPlot draws with (<c>samples/Shared</c>).
    ///
    /// Steps:
    /// - Record the scene with 1e5 points; clear to white and render at 1,280 × 720; compare with <c>Golden/hello.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline within the tolerance.
    /// </remarks>
    [Fact]
    public void HelloScene_Warp_MatchesBaseline()
    {
        RenderList list;
        using (var pool = new PlotWorkerPool(4))
        {
            list = HelloScene.Record(points: 100_000, pool);
        }

        using var backend = D3D11Backend.Create(useWarp: true);
        using var renderer = new HeadlessRenderer(backend, HelloScene.Width, HelloScene.Height);
        renderer.Clear(new Color(255, 255, 255, 255));
        renderer.Render(list);

        GoldenImage.AssertMatches(renderer, Baseline("hello.png"), Tolerance, Output);
    }

    /// <summary>
    /// One image of the R2 line kinds matches its baseline: widths 0.5 to 3, a dash across a join, a clip, and translucent
    /// lines over each other and over themselves (design §10.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend; a 320 × 240 target.
    ///
    /// Steps:
    /// - Record the lines below; clear to white and render; compare with <c>Golden/lines.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline within the tolerance.
    /// </remarks>
    [Fact]
    public void LineKinds_Warp_MatchBaseline()
    {
        var list = new RenderList();
        var black = new Color(0, 0, 0, 255);

        // Widths, each a slanted line at a fractional position.
        float[] widths = [0.5f, 1f, 1.5f, 3f];
        for (var i = 0; i < widths.Length; i++)
        {
            var y = 12.3f + (i * 14);
            list.DrawPolyline([new Vector2(10.4f, y), new Vector2(150.7f, y + 9)], new LineStyle(black, widths[i]));
        }

        // A dash that goes on across the joins of a zigzag.
        list.DrawPolyline(
            [new Vector2(170, 15), new Vector2(210, 60), new Vector2(250, 15), new Vector2(300, 60)],
            new LineStyle(new Color(31, 119, 180, 255), 2, new LineDash(9, 5, 2))
        );

        // A clip: the circle-ish polyline is drawn only inside the rectangle.
        list.SetClip(20, 90, 100, 60);
        Span<Vector2> ring = stackalloc Vector2[25];
        for (var k = 0; k < ring.Length; k++)
        {
            var angle = k * MathF.Tau / (ring.Length - 1);
            ring[k] = new Vector2(70 + (55 * MathF.Cos(angle)), 120 + (45 * MathF.Sin(angle)));
        }

        list.DrawPolyline(ring, new LineStyle(new Color(214, 39, 40, 255), 2.5f));
        list.ResetClip();

        // Translucent: two thick lines over each other, and one that folds back over itself with sharp joins.
        list.DrawPolyline([new Vector2(150, 100), new Vector2(300, 220)], new LineStyle(new Color(255, 127, 14, 128), 8));
        list.DrawPolyline([new Vector2(150, 220), new Vector2(300, 100)], new LineStyle(new Color(44, 160, 44, 128), 8));
        list.DrawPolyline(
            [new Vector2(20, 200), new Vector2(120, 170), new Vector2(30, 185), new Vector2(125, 225)],
            new LineStyle(new Color(148, 103, 189, 140), 5)
        );

        using var backend = D3D11Backend.Create(useWarp: true);
        using var renderer = new HeadlessRenderer(backend, 320, 240);
        renderer.Clear(new Color(255, 255, 255, 255));
        renderer.Render(list);

        GoldenImage.AssertMatches(renderer, Baseline("lines.png"), Tolerance, Output);
    }

    // The baselines are read from and written to the source tree, so that a rewrite lands in git.
    internal static string Baseline(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HyperPlot.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "tests", "HyperPlot.Rendering.D3D11.Tests", "Golden", name);
    }
}
