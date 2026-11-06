using System.Numerics;
using HyperPlot.Interaction;
using HyperPlot.Testing;
using HyperPlot.Tests;

namespace HyperPlot.Rendering.D3D11.Tests;

/// <summary>What the interaction draws over a plot, on WARP (design §6.5, §10.1, row I1a). Windows only.</summary>
public sealed class InteractionRenderTests
{
    private const int Width = 640;
    private const int Height = 360;

    /// <summary>
    /// Scene (40): a rectangle zoom in progress draws its rectangle over the plot, a fill of 15 % of the theme's foreground
    /// and an outline of 1 DIP, after the data and the axes (design §6.5, §10.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A line of 400 seeded points with titled axes, Theme.Light, the fake text backend, 640 × 360, WARP.
    ///
    /// Steps:
    /// - Draw; press the right button at (200, 100) and move it to (420, 230); draw.
    ///
    /// Expected:
    /// - The image matches Golden/interaction-rectangle.png within the golden images' tolerance.
    /// </remarks>
    [Fact]
    public void RectangleZoom_DrawsItsRectangle()
    {
        var random = new Random(40);
        var walk = new float[400];
        for (var i = 1; i < walk.Length; i++)
        {
            walk[i] = walk[i - 1] + (float)(random.NextDouble() - 0.5);
        }

        using var pool = new PlotWorkerPool(2);
        using var y = Column.Wrap(walk);
        var table = DataTable.Create().Add("x", Column.Uniform(0, 1, walk.Length)).Add("y", y).Build();
        var spec = Plot.Create(table)
            .Line(table.Col<double>("x"), table.Col<float>("y"), l => l.Width(2))
            .ScaleX(Scale.Linear().WithTitle("Step"))
            .ScaleY(Scale.Linear().WithTitle("Value"))
            .Title("Rectangle zoom")
            .Build();
        using var backend = D3D11Backend.CreateForTest(useWarp: true, createDevice: null, new TextEngine(new FakeTextBackend()));
        using var engine = new PlotEngine(backend, Width, Height, 1, pool);
        engine.SetPlot(spec);
        engine.RenderFrame();
        engine.OnPointer(
            new PointerEvent(
                PointerEventKind.Down,
                new Vector2(200, 100),
                PointerButtons.Right,
                PointerButtons.Right,
                ModifierKeys.None,
                PointerKind.Mouse,
                0
            )
        );
        engine.OnPointer(
            new PointerEvent(
                PointerEventKind.Move,
                new Vector2(420, 230),
                PointerButtons.None,
                PointerButtons.Right,
                ModifierKeys.None,
                PointerKind.Mouse,
                1
            )
        );
        engine.RenderFrame();

        var pixels = new byte[Width * Height * 4];
        engine.ReadPixels(pixels);
        GoldenImage.AssertMatches(
            pixels,
            Width,
            Height,
            GoldenImageTests.Baseline("interaction-rectangle.png"),
            GoldenImageTests.Tolerance,
            GoldenImageTests.Output
        );
    }
}
