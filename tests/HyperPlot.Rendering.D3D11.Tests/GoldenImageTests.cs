using System.Numerics;
using HyperPlot.Samples;
using HyperPlot.Testing;
using HyperPlot.Tests;

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

    /// <summary>
    /// One image of row R4's joins and caps matches its baseline (design §10.1 scene 10): the 3 joins × 3 caps, sharp turns
    /// around the miter limit, patterns of 2 to 8 intervals, a translucent line over itself, and a shallow line 1 wide.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend; a 640 × 360 target.
    ///
    /// Steps:
    /// - Record the lines below; clear to white and render; compare with <c>Golden/lines-joins.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline within the tolerance.
    /// </remarks>
    [Fact]
    public void LineJoins_Warp_MatchBaseline()
    {
        var list = new RenderList();
        LineJoin[] joins = [LineJoin.Round, LineJoin.Miter, LineJoin.Bevel];
        LineCap[] caps = [LineCap.Round, LineCap.Butt, LineCap.Square];

        // The 3 × 3 grid: a zigzag 12 wide in each cell, its centerline drawn thin over it.
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                var (x, y) = (24.3f + (column * 140), 24.6f + (row * 100));
                Vector2[] zigzag = [new(x, y + 50), new(x + 35, y), new(x + 70, y + 50), new(x + 105, y + 20)];
                list.DrawPolyline(zigzag, new LineStyle(new Color(31, 119, 180, 255), 12) { Join = joins[row], Cap = caps[column] });
                list.DrawPolyline(zigzag, new LineStyle(new Color(255, 255, 255, 255), 1));
            }
        }

        // Sharp turns of 116° and 124° around a miter limit of 2.
        foreach (var (degrees, top) in new[] { (116f, 30f), (124f, 90f) })
        {
            var corner = new Vector2(500.2f, top);
            var radians = degrees * MathF.PI / 180;
            list.DrawPolyline(
                [corner - new Vector2(60, 0), corner, corner + (new Vector2(MathF.Cos(radians), MathF.Sin(radians)) * 45)],
                new LineStyle(new Color(40, 40, 40, 255), 10)
                {
                    Join = LineJoin.Miter,
                    Cap = LineCap.Butt,
                    MiterLimit = 2,
                }
            );
        }

        // Patterns of 2, 4, 6, and 8 intervals, butt caps: square caps would close gaps narrower than the width.
        LineDash[] patterns = [new(12, 6), LineDash.Of(12, 4, 2, 4), LineDash.Of(12, 3, 3, 3, 3, 3), LineDash.Of(16, 3, 2, 3, 8, 3, 2, 3)];
        for (var i = 0; i < patterns.Length; i++)
        {
            var y = 165.4f + (i * 18);
            list.DrawPolyline(
                [new Vector2(440.3f, y), new Vector2(620.6f, y + 6)],
                new LineStyle(new Color(214, 39, 40, 255), 4, patterns[i]) { Cap = LineCap.Butt }
            );
        }

        // A translucent mitered line folding back over itself, painted once; a shallow line at a slope near 1/5.
        list.DrawPolyline(
            [new Vector2(450, 250), new Vector2(600, 270), new Vector2(470, 300), new Vector2(610, 330)],
            new LineStyle(new Color(148, 103, 189, 140), 9) { Join = LineJoin.Miter, Cap = LineCap.Square }
        );
        list.DrawPolyline(
            [new Vector2(20.2f, 330.4f), new Vector2(420.6f, 350.1f)],
            new LineStyle(new Color(0, 0, 0, 255), 1) { Join = LineJoin.Miter, Cap = LineCap.Butt }
        );

        using var backend = D3D11Backend.Create(useWarp: true);
        using var renderer = new HeadlessRenderer(backend, 640, 360);
        renderer.Clear(new Color(255, 255, 255, 255));
        renderer.Render(list);

        GoldenImage.AssertMatches(renderer, Baseline("lines-joins.png"), Tolerance, Output);
    }

    /// <summary>
    /// One image of texts drawn with the fake text backend matches its baseline: the 12 alignments, the 3 rotations, and
    /// blocks of 2 and 3 lines (design §10.1). DirectWrite's pixels follow the system's fonts, so they are not in it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend over the fake text backend; a 640 × 360 target.
    ///
    /// Steps:
    /// - Record a grid of the 12 alignments, each at a crosshair of thin lines, and the 3 rotations of a 3-line block;
    ///   clear to white and render; compare with <c>Golden/text.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline within the tolerance.
    /// </remarks>
    [Fact]
    public void TextKinds_Warp_MatchBaseline()
    {
        var list = new RenderList();
        var mark = new LineStyle(new Color(214, 39, 40, 255), 1);
        var cell = 0;
        foreach (var vertical in new[] { TextBaseline.Top, TextBaseline.Middle, TextBaseline.Baseline, TextBaseline.Bottom })
        {
            foreach (var horizontal in new[] { TextAlign.Left, TextAlign.Center, TextAlign.Right })
            {
                var at = new Vector2(70.5f + ((cell % 3) * 130), 40.5f + ((cell / 3) * 85));
                list.DrawPolyline([at - new Vector2(30, 0), at + new Vector2(30, 0)], mark);
                list.DrawPolyline([at - new Vector2(0, 20), at + new Vector2(0, 20)], mark);
                var style = new TextStyle(new Color(31, 119, 180, 255), 14, Horizontal: horizontal, Vertical: vertical);
                list.DrawText(cell % 2 == 0 ? "Ab\nCde" : "Abcd\nEf", at, style);
                cell++;
            }
        }

        var rotations = new[] { TextRotation.None, TextRotation.Clockwise90, TextRotation.CounterClockwise90 };
        for (var i = 0; i < rotations.Length; i++)
        {
            var style = new TextStyle(new Color(44, 160, 44, 200), 15.5f, Vertical: TextBaseline.Middle, Rotation: rotations[i]);
            list.DrawText("One\nTwo 2\nThree", new Vector2(395.25f + (i * 90), 180.75f), style);
        }

        using var backend = D3D11Backend.CreateForTest(useWarp: true, createDevice: null, new TextEngine(new FakeTextBackend()));
        using var renderer = new HeadlessRenderer(backend, 640, 360);
        renderer.Clear(new Color(255, 255, 255, 255));
        renderer.Render(list);

        GoldenImage.AssertMatches(renderer, Baseline("text.png"), Tolerance, Output);
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
