using System.Numerics;
using HyperPlot.Interaction;
using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>
/// <see cref="PlotEngine.Resize"/> and <see cref="PlotEngine.Target"/> (design §7.1, §10, row W1a): a host's element changes
/// its size or its DPI, and the plot is laid out again with its views, history, animations, and indexes kept.
/// </summary>
public sealed class ResizeTests
{
    /// <summary>The same size and scale do nothing (design §7.1): the target stays, and no frame is asked for.</summary>
    /// <remarks>
    /// Preconditions:
    /// - The fixture's engine at 640 × 360 and scale 1, drawn.
    ///
    /// Steps:
    /// - Resize to 640 × 360 at scale 1.
    ///
    /// Expected:
    /// - The same target object; no new frame request.
    /// </remarks>
    [Fact]
    public void SameValues_DoNothing()
    {
        using var f = new EngineFixture();
        var (target, requests) = (f.Engine.Target, f.Requests);
        f.Engine.Resize(EngineFixture.Width, EngineFixture.Height, 1);
        Assert.Same(target, f.Engine.Target);
        Assert.Equal(requests, f.Requests);
    }

    /// <summary>
    /// A new size makes a new target and lays the plot out again (design §7.1): the view the user zoomed to, the history, and
    /// the pyramids stay.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The fixture's engine, drawn; animations off; the panel zoomed by 2 at its middle.
    ///
    /// Steps:
    /// - Resize to 1,000 × 500 at scale 1; draw.
    ///
    /// Expected:
    /// - A new target of 1,000 × 500, and <see cref="PlotEngine.Width"/> and <see cref="PlotEngine.Height"/> say so; one
    ///   frame asked for. The panel wider and taller than before and inside the new target. The view, the history, and the
    ///   number of pyramids built as before.
    /// </remarks>
    [Fact]
    public void NewSize_MakesANewTargetAndKeepsTheView()
    {
        using var f = new EngineFixture();
        f.Engine.Options.ZoomAnimation = TimeSpan.Zero;
        f.Engine.Views.ZoomAt(EngineFixture.Panel, f.Middle, 2, animate: false);
        f.Engine.RenderFrame();
        var (target, rect, view, history, built, requests) = (
            f.Engine.Target,
            f.Rect,
            f.View(),
            f.Engine.History,
            f.Engine.Plan!.PyramidsBuilt,
            f.Requests
        );

        f.Engine.Resize(1000, 500, 1);
        Assert.Equal(requests + 1, f.Requests);
        f.Engine.RenderFrame();

        Assert.NotSame(target, f.Engine.Target);
        Assert.Equal(new RenderTargetDescription(1000, 500), f.Engine.Target.Description);
        Assert.Equal((1000, 500), (f.Engine.Width, f.Engine.Height));
        var resized = f.Rect;
        Assert.True(resized.Width > rect.Width && resized.Height > rect.Height, $"{rect} → {resized}");
        Assert.True(resized.Left >= 0 && resized.Top >= 0 && resized.Left + resized.Width <= 1000 && resized.Top + resized.Height <= 500);
        Assert.Equal(view, f.View());
        Assert.Equal(history, f.Engine.History);
        Assert.Equal(built, f.Engine.Plan.PyramidsBuilt);
    }

    /// <summary>
    /// A new scale alone keeps the target and lays out again with the theme's lengths at that scale (design §4.6, §7.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The fixture's engine at scale 1, drawn.
    ///
    /// Steps:
    /// - Resize to the same size at scale 2; draw.
    ///
    /// Expected:
    /// - The same target; <see cref="PlotEngine.Scale"/> 2; the panel's left and top margins larger than at scale 1, and the
    ///   same as a new engine's at scale 2.
    /// </remarks>
    [Fact]
    public void NewScale_KeepsTheTargetAndScalesTheLayout()
    {
        using var f = new EngineFixture();
        var (target, rect) = (f.Engine.Target, f.Rect);
        f.Engine.Resize(EngineFixture.Width, EngineFixture.Height, 2);
        f.Engine.RenderFrame();
        Assert.Same(target, f.Engine.Target);
        Assert.Equal(2f, f.Engine.Scale);
        Assert.True(f.Rect.Left > rect.Left && f.Rect.Top > rect.Top, $"{rect} → {f.Rect}");

        using var fresh = new PlotEngine(f.Backend, EngineFixture.Width, EngineFixture.Height, 2, f.Pool, f.Time);
        fresh.SetPlot(f.Spec);
        fresh.RenderFrame();
        var g = fresh.Plan!.Guides;
        Assert.Equal((g.Left, g.Top, g.Width, g.Height), f.Rect);
    }

    /// <summary>
    /// A target grown past what the layers' output was made for draws the same as a new engine of that size (row W1a): the
    /// layers are made again with room, from the pyramids built already.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The fixture's engine resized to 16 × 16 and drawn.
    ///
    /// Steps:
    /// - Resize to 1,920 × 1,080; draw. Draw the same spec on a new engine of 1,920 × 1,080.
    ///
    /// Expected:
    /// - The same polylines and segments in both lists; the pyramids built by the resized engine as before.
    /// </remarks>
    [Fact]
    public void Growth_DrawsAsANewEngine()
    {
        using var f = new EngineFixture();
        f.Engine.Resize(16, 16, 1);
        f.Engine.RenderFrame();
        var built = f.Engine.Plan!.PyramidsBuilt;
        f.Engine.Resize(1920, 1080, 1);
        f.Engine.RenderFrame();

        using var fresh = new PlotEngine(f.Backend, 1920, 1080, 1, f.Pool, f.Time);
        fresh.SetPlot(f.Spec);
        fresh.RenderFrame();
        var (grown, made) = (f.Engine.Plan.List, fresh.Plan!.List);
        Assert.Equal(made.Polylines.ToArray(), grown.Polylines.ToArray());
        Assert.Equal(made.Segments.ToArray(), grown.Segments.ToArray());
        Assert.Equal(built, f.Engine.Plan.PyramidsBuilt);
    }

    /// <summary>An animation goes on through a resize and ends at the view it was going to (design §7.1).</summary>
    /// <remarks>
    /// Preconditions:
    /// - The fixture's engine with the default animation (150 ms).
    ///
    /// Steps:
    /// - Zoom by 2 at the middle with an animation; 50 ms later, resize to 800 × 400 and draw; 1 s later, draw.
    ///
    /// Expected:
    /// - After 50 ms, the view is between the start and the end; at the end, the view the zoom went to.
    /// </remarks>
    [Fact]
    public void Animation_GoesOnThroughAResize()
    {
        using var f = new EngineFixture();
        var start = f.View();
        f.Engine.Views.ZoomAt(EngineFixture.Panel, f.Middle, 2, animate: true);

        // An animated change is told once, with the view it ends at (design §6.2).
        _ = f.Changes[^1].TryGet2D(out var end, out _);
        f.Frame(TimeSpan.FromMilliseconds(50));
        f.Engine.Resize(800, 400, 1);
        f.Engine.RenderFrame();
        var x = f.View().X;
        Assert.True(x.LowValue > start.X.LowValue && x.LowValue < end.LowValue, $"{start.X} {x} {end}");
        f.Settle();
        Assert.Equal(end, f.View().X);
    }

    /// <summary>A figure's plots are placed again in cells of the new target (design §4.6, §7.1); their views stay.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A figure of two plots side by side of the fixture's line; animations off; the second plot zoomed by 2.
    ///
    /// Steps:
    /// - Resize to 1,200 × 600; draw.
    ///
    /// Expected:
    /// - Each plot's area is its half of the new target, in its place: the second's left edge right of the first's right
    ///   edge, the second's right edge within 1,200. The second plot's view as zoomed.
    /// </remarks>
    [Fact]
    public void Figure_PlacesItsCellsAgain()
    {
        using var f = new EngineFixture();
        f.Engine.Options.ZoomAnimation = TimeSpan.Zero;
        f.Engine.SetFigure(Figure.HConcat(f.Spec, f.Spec));
        f.Engine.RenderFrame();
        var second = new PanelId(1, 0);
        var g = f.Engine.Figure!.Plans[1].Guides.PanelAt(0);
        f.Engine.Views.ZoomAt(second, new Vector2(g.Left + (g.Width / 2f), g.Top + (g.Height / 2f)), 2, animate: false);
        f.Engine.RenderFrame();
        var view = f.Engine.CurrentView(second);
        var before = f.Engine.Figure.Plans[1].Guides.Area;

        f.Engine.Resize(1200, 600, 1);
        f.Engine.RenderFrame();
        var (one, two) = (f.Engine.Figure.Plans[0].Guides.Area, f.Engine.Figure.Plans[1].Guides.Area);
        Assert.True(two.Width > before.Width && two.Height > before.Height, $"{before} → {two}");
        Assert.True(two.Left >= one.Left + one.Width && two.Left + two.Width <= 1200, $"{one} {two}");
        Assert.Equal(view, f.Engine.CurrentView(second));
    }

    /// <summary>A size or a scale out of range throws, and the engine keeps its target (design §7.1).</summary>
    /// <remarks>
    /// Preconditions:
    /// - The fixture's engine.
    ///
    /// Steps:
    /// - Resize with each value out of range.
    ///
    /// Expected:
    /// - ArgumentOutOfRangeException each time; the same target and size after.
    /// </remarks>
    [Theory]
    [InlineData(0, 100, 1f)]
    [InlineData(100, 0, 1f)]
    [InlineData(16385, 100, 1f)]
    [InlineData(100, 16385, 1f)]
    [InlineData(100, 100, 0f)]
    [InlineData(100, 100, -1f)]
    [InlineData(100, 100, float.NaN)]
    [InlineData(100, 100, float.PositiveInfinity)]
    public void OutOfRange_Throws(int width, int height, float scale)
    {
        using var f = new EngineFixture();
        var target = f.Engine.Target;
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => f.Engine.Resize(width, height, scale));
        Assert.Same(target, f.Engine.Target);
        Assert.Equal((EngineFixture.Width, EngineFixture.Height, 1f), (f.Engine.Width, f.Engine.Height, f.Engine.Scale));
    }
}
