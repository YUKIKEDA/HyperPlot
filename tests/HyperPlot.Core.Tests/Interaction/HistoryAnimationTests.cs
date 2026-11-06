using System.Numerics;
using HyperPlot.Interaction;

namespace HyperPlot.Tests;

/// <summary>The notices, the history, the animation, and the frame requests of an engine (design §6.2, row I1a).</summary>
public sealed class HistoryAnimationTests
{
    /// <summary>
    /// Each change is told once with its reason (design §6.2): input, code, history, and a new mapping of the spec, which
    /// goes back to the full view; an animated change is told once, with the view it ends at.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An engine over a line with y given as [−20, 20]; the notices kept.
    ///
    /// Steps:
    /// - Turn the wheel (animated) and draw frames through the animation; zoom from the code; go back; set the spec again
    ///   with y given as [−40, 40] and draw.
    ///
    /// Expected:
    /// - The notices Input (its view the one reached at the end), Code, History, Spec; after the Spec, the full view of the
    ///   new spec and no entry to go back to.
    /// </remarks>
    [Fact]
    public void Notices_OneAChangeWithItsReason()
    {
        using var f = new EngineFixture();
        f.Wheel(f.Middle, 1);
        Assert.Single(f.Changes);
        for (var i = 0; i < 20; i++)
        {
            f.Frame(TimeSpan.FromMilliseconds(16));
        }

        Assert.Single(f.Changes);
        _ = f.Changes[0].TryGet2D(out var x, out var y);
        Assert.Equal(f.View(), (x, y));

        f.Engine.Views.ZoomAt(EngineFixture.Panel, f.Middle, 2, animate: false);
        f.Engine.Views.Back();
        f.Engine.SetPlot(f.Spec with { ScaleY = Scale.Linear().WithDomain(-40, 40) });
        f.Engine.RenderFrame();

        Assert.Equal(
            [ViewChangeReason.Input, ViewChangeReason.Code, ViewChangeReason.History, ViewChangeReason.Spec],
            f.Changes.Select(c => c.Reason)
        );
        Assert.Equal((-40.0, 40.0), (f.View().Y.LowValue, f.View().Y.HighValue));
        Assert.False(f.Engine.Views.CanGoBack);
    }

    /// <summary>
    /// The history keeps an operation as one entry (design §6.2): a drag, a rectangle zoom, the full view, and the turns of a
    /// wheel until a pause of 300 ms (299 ms joins, 300 ms does not); at most 100 entries; a new operation after going back
    /// drops the entries ahead.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An engine over a line; animations off.
    ///
    /// Steps:
    /// - Drag; zoom to a rectangle by a right drag; Home; turn the wheel 3 times 299 ms apart, wait 300 ms, turn it again;
    ///   go back twice and drag; then 150 code zooms.
    ///
    /// Expected:
    /// - Entries counted 2, 3, 4, 5 (the turns one), 6 (the turn after the pause); after going back and dragging, the entries
    ///   ahead gone; 100 entries at the end, the newest last.
    /// </remarks>
    [Fact]
    public void History_OneEntryAnOperation()
    {
        using var f = new EngineFixture();
        f.Engine.Options.ZoomAnimation = TimeSpan.Zero;
        Assert.Equal((1, 0), f.Engine.History);
        f.Drag(f.Middle, f.Middle + new Vector2(80, 0));
        Assert.Equal((2, 1), f.Engine.History);
        f.Drag(f.Middle, f.Middle + new Vector2(60, 40), PointerButtons.Right);
        Assert.Equal((3, 2), f.Engine.History);
        f.Key(PlotKey.Home);
        Assert.Equal((4, 3), f.Engine.History);
        for (var i = 0; i < 3; i++)
        {
            f.Wheel(f.Middle, 1);
            f.Time.Advance(TimeSpan.FromMilliseconds(299));
        }

        Assert.Equal((5, 4), f.Engine.History);
        f.Time.Advance(TimeSpan.FromMilliseconds(1));
        f.Wheel(f.Middle, 1);
        Assert.Equal((6, 5), f.Engine.History);

        f.Engine.Views.Back();
        f.Engine.Views.Back();
        f.Engine.RenderFrame();
        f.Drag(f.Middle, f.Middle + new Vector2(0, 30));
        Assert.Equal((5, 4), f.Engine.History);
        Assert.False(f.Engine.Views.CanGoForward);

        for (var i = 0; i < 150; i++)
        {
            f.Engine.Views.ZoomAt(EngineFixture.Panel, f.Middle, 1.01);
        }

        Assert.Equal((100, 99), f.Engine.History);
    }

    /// <summary>
    /// The animation (design §6.2): a zoom moves each end over 150 ms with a cubic ease out, 1 − (1 − t)³; it ends at the
    /// view it goes to; a zoom in the middle goes on from where the first goes; a length of 0 has none; the engine asks for
    /// frames while it runs, and for none after.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An engine over a line; the frame requests counted.
    ///
    /// Steps:
    /// - Zoom by 2 at the middle; draw at 0, 50, 100, and 200 ms, reading the view and the requests; zoom by 2 at 50 ms of a
    ///   second zoom, then again; set the length to 0 and zoom.
    ///
    /// Expected:
    /// - At 50 and 100 ms each end where the ease puts it within 1e-9 of the width; at 200 ms the target, and no request
    ///   after the last frame; the two zooms in a row reach 4 times; with 0, the view at once.
    /// </remarks>
    [Fact]
    public void Animation_EasesToTheTarget()
    {
        using var f = new EngineFixture();
        var from = f.View().X;
        f.Engine.Views.ZoomAt(EngineFixture.Panel, f.Middle, 2);
        f.Engine.RenderFrame();
        var to = Changed(f);
        foreach (var ms in new[] { 50, 50 })
        {
            f.Frame(TimeSpan.FromMilliseconds(ms));
            var t = Math.Min(1, (f.Time.GetTimestamp() / (double)TimeSpan.TicksPerMillisecond) / 150.0);
            var eased = 1 - Math.Pow(1 - t, 3);
            Assert.Equal(from.LowValue + ((to.LowValue - from.LowValue) * eased), f.View().X.LowValue, 1e-9 * (from.Upper - from.Lower));
        }

        var requests = f.Requests;
        f.Frame(TimeSpan.FromMilliseconds(100));
        Assert.Equal((to.LowValue, to.HighValue), (f.View().X.LowValue, f.View().X.HighValue));
        Assert.Equal(requests, f.Requests);

        var start = f.View().X;
        f.Engine.Views.ZoomAt(EngineFixture.Panel, f.Middle, 2);
        f.Frame(TimeSpan.FromMilliseconds(50));
        f.Engine.Views.ZoomAt(EngineFixture.Panel, f.Middle, 2);
        f.Settle();
        Assert.Equal((start.Upper - start.Lower) / 4, f.View().X.Upper - f.View().X.Lower, 1e-9);

        f.Engine.Options.ZoomAnimation = TimeSpan.Zero;
        f.Engine.Views.ZoomAt(EngineFixture.Panel, f.Middle, 2);
        Assert.Equal(Changed(f), f.View().X);
    }

    /// <summary>The engine asks for one frame however many changes come before it draws (design §6.2, §7.1).</summary>
    /// <remarks>
    /// Preconditions:
    /// - An engine over a line, drawn; the frame requests counted.
    ///
    /// Steps:
    /// - Zoom from the code five times with no animation; draw; zoom once more.
    ///
    /// Expected:
    /// - One request for the five; one more after the frame.
    /// </remarks>
    [Fact]
    public void FrameRequest_OneUntilDrawn()
    {
        using var f = new EngineFixture();
        var before = f.Requests;
        for (var i = 0; i < 5; i++)
        {
            f.Engine.Views.ZoomAt(EngineFixture.Panel, f.Middle, 1.1, animate: false);
        }

        Assert.Equal(before + 1, f.Requests);
        f.Engine.RenderFrame();
        f.Engine.Views.ZoomAt(EngineFixture.Panel, f.Middle, 1.1, animate: false);
        Assert.Equal(before + 2, f.Requests);
    }

    /// <summary>
    /// A wheel turned at each frame zooms at each frame (design §6.2): a turn that comes while the animation runs starts from
    /// the view the animation has reached at its time, so the view moves toward each new target and does not wait for the
    /// turns to stop.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An engine over a line; the animation of 150 ms.
    ///
    /// Steps:
    /// - Twenty times: move the time 16 ms, turn the wheel a notch at the middle, draw; read the x width after each frame.
    ///
    /// Expected:
    /// - From the second frame on, each width is below the one before.
    /// </remarks>
    [Fact]
    public void Animation_WheelAtEachFrame_MovesEachFrame()
    {
        using var f = new EngineFixture();
        var widths = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            f.Time.Advance(TimeSpan.FromMilliseconds(16));
            f.Wheel(f.Middle, 1);
            f.Engine.RenderFrame();
            widths.Add(f.View().X.Upper - f.View().X.Lower);
        }

        for (var i = 2; i < widths.Count; i++)
        {
            Assert.True(widths[i] < widths[i - 1], $"Frame {i}: {widths[i]} after {widths[i - 1]}.");
        }
    }

    private static ViewRange Changed(EngineFixture f)
    {
        _ = f.Changes[^1].TryGet2D(out var x, out _);
        return x;
    }
}
