using System.Numerics;
using HyperPlot.Interaction;
using HyperPlot.Rendering;
using HyperPlot.Testing;

namespace HyperPlot.Tests;

/// <summary>The tools, the bindings, and the default table of an engine (design §6.5, row I1a).</summary>
public sealed class ToolBindingTests
{
    /// <summary>
    /// Each row of the default table does what the table says (design §6.5), driven by an input script and read in the views
    /// and the history.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An engine over a line; animations off.
    ///
    /// Steps:
    /// - For each row: from the full view, run its input and read the view against the full one; for the back and forward
    ///   rows, zoom first.
    ///
    /// Expected:
    /// - Left drag on the panel: both axes move; on the x axis: x alone; on the y axis: y alone. Right drag and Control left
    ///   drag: the rectangle. Wheel on the panel: both narrow; on an axis: that axis. Pinch: both narrow. Scroll: both move.
    ///   Double click: the full view. Arrows: the view moves a tenth that way. Plus and Minus: narrower and wider by 1.25.
    ///   Home: full. Back and forward by the browser's keys, Alt and an arrow, and the extra buttons: the history moves.
    ///   Escape cancels a rectangle in progress, which changes no view.
    /// </remarks>
    [Fact]
    public void DefaultTable_EachRow()
    {
        using var f = new EngineFixture();
        f.Engine.Options.ZoomAnimation = TimeSpan.Zero;
        var full = f.View();
        var (left, top, width, height) = f.Rect;
        var m = f.Middle;
        var p = ScriptPoint.Pixel(m.X, m.Y);
        var belowX = ScriptPoint.Pixel(m.X, top + height + 8);
        var leftOfY = ScriptPoint.Pixel(left - 8, m.Y);
        (bool X, bool Y) Run(InputScript script)
        {
            f.Engine.Views.Reset(EngineFixture.Panel, animate: false);
            f.Engine.RenderFrame();
            script.Run(f.Engine, f.Time.Advance);
            var view = f.View();
            return (view.X != full.X, view.Y != full.Y);
        }

        Assert.Equal((true, true), Run(new InputScript().Drag(p, ScriptPoint.Pixel(m.X + 50, m.Y + 30))));
        Assert.Equal((true, false), Run(new InputScript().Drag(belowX, ScriptPoint.Pixel(m.X + 50, top + height + 30))));
        Assert.Equal((false, true), Run(new InputScript().Drag(leftOfY, ScriptPoint.Pixel(left - 20, m.Y + 30))));
        Assert.Equal((true, true), Run(new InputScript().Drag(p, ScriptPoint.Pixel(m.X + 50, m.Y + 30), PointerButtons.Right)));
        Assert.Equal((true, true), Run(new InputScript().Drag(p, ScriptPoint.Pixel(m.X + 50, m.Y + 30), PointerButtons.Left, ModifierKeys.Control)));
        Assert.True(Narrower(f, full, Run(new InputScript().Wheel(p, 1))));
        Assert.Equal((true, false), Run(new InputScript().Wheel(belowX, 1)));
        Assert.Equal((false, true), Run(new InputScript().Wheel(leftOfY, 1)));
        Assert.True(Narrower(f, full, Run(new InputScript().Pinch(p, 1.5))));
        Assert.Equal((true, true), Run(new InputScript().Scroll(p, new Vector2(20, 10))));
        Assert.Equal((false, false), Run(new InputScript().Wheel(p, 1).DoubleClick(p)));

        Assert.Equal((true, false), Run(new InputScript().Key(PlotKey.Left)));
        Assert.Equal(-0.1 * (full.X.Upper - full.X.Lower), f.View().X.LowValue - full.X.LowValue, 1e-6 * (full.X.Upper - full.X.Lower));
        Assert.Equal((false, true), Run(new InputScript().Key(PlotKey.Up)));
        Assert.Equal(0.1 * (full.Y.Upper - full.Y.Lower), f.View().Y.LowValue - full.Y.LowValue, 1e-6 * (full.Y.Upper - full.Y.Lower));
        _ = Run(new InputScript().Key(PlotKey.Plus));
        Assert.Equal((full.X.Upper - full.X.Lower) / 1.25, f.View().X.Upper - f.View().X.Lower, 1e-9);
        _ = Run(new InputScript().Key(PlotKey.Minus));
        Assert.Equal((full.X.Upper - full.X.Lower) * 1.25, f.View().X.Upper - f.View().X.Lower, 1e-9);
        Assert.Equal((false, false), Run(new InputScript().Wheel(p, 1).Key(PlotKey.Home)));

        foreach (
            var (back, forward) in new (InputScript Back, InputScript Forward)[]
            {
                (new InputScript().Key(PlotKey.BrowserBack), new InputScript().Key(PlotKey.BrowserForward)),
                (new InputScript().Key(PlotKey.Left, ModifierKeys.Alt), new InputScript().Key(PlotKey.Right, ModifierKeys.Alt)),
                (new InputScript().Click(p, PointerButtons.X1), new InputScript().Click(p, PointerButtons.X2)),
            }
        )
        {
            f.Engine.Views.Reset(EngineFixture.Panel, animate: false);
            f.Engine.Views.ZoomAt(EngineFixture.Panel, m, 2, animate: false);
            f.Engine.RenderFrame();
            var zoomed = f.View();
            back.Run(f.Engine, f.Time.Advance);
            Assert.Equal(full, f.View());
            forward.Run(f.Engine, f.Time.Advance);
            Assert.Equal(zoomed, f.View());
        }

        var before = f.View();
        new InputScript()
            .Press(p, PointerButtons.Right)
            .Move(ScriptPoint.Pixel(m.X + 60, m.Y + 40))
            .Key(PlotKey.Escape)
            .Release(ScriptPoint.Pixel(m.X + 60, m.Y + 40), PointerButtons.Right)
            .Run(f.Engine, f.Time.Advance);
        Assert.Equal(before, f.View());
        Assert.Null(f.Engine.Captured);
    }

    /// <summary>
    /// The engine tries the bindings in order, and a tool that captures the pointer gets every pointer event until it
    /// releases it, past the panel and the target (design §6.5); a pointer's cancel and Escape cancel it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An engine over a line; a recording tool inserted first for the left drag on the panel.
    ///
    /// Steps:
    /// - Drag from the panel's middle to a point outside the target; then press, move past the threshold, and pass a cancel;
    ///   then press, move, and Escape.
    ///
    /// Expected:
    /// - The tool, not the pan, got the press, every move (the one outside included), and the release; the view did not move.
    ///   The cancel and Escape each called the tool's Cancel, and nothing holds the pointer after.
    /// </remarks>
    [Fact]
    public void Tools_InOrderAndCaptured()
    {
        using var f = new EngineFixture();
        var tool = new RecordingTool();
        f.Engine.Bindings.Insert(0, new InputBinding(InputGesture.Drag(PointerButtons.Left), ModifierKeys.None, HitAreaKind.Panel, tool));
        var full = f.View();
        f.Drag(f.Middle, new Vector2(-50, -50));
        Assert.Equal(
            [PointerEventKind.Down, PointerEventKind.Move, PointerEventKind.Move, PointerEventKind.Move, PointerEventKind.Move, PointerEventKind.Up],
            tool.Events.Select(e => e.Kind)
        );
        Assert.Equal(new Vector2(-50, -50), tool.Events[^1].Position);
        Assert.Equal(full, f.View());

        f.Engine.OnPointer(f.Pointer(PointerEventKind.Down, f.Middle, PointerButtons.Left));
        f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, f.Middle + new Vector2(30, 0)) with { Buttons = PointerButtons.Left });
        Assert.Same(tool, f.Engine.Captured);
        f.Engine.OnPointer(f.Pointer(PointerEventKind.Cancel, f.Middle));
        f.Engine.OnPointer(f.Pointer(PointerEventKind.Down, f.Middle, PointerButtons.Left));
        f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, f.Middle + new Vector2(30, 0)) with { Buttons = PointerButtons.Left });
        f.Key(PlotKey.Escape);
        Assert.Equal(2, tool.Cancels);
        Assert.Null(f.Engine.Captured);
    }

    /// <summary>
    /// The thresholds (design §6.1, §6.5): a press that moves 4 DIPs is a click and one that moves past them a drag; a second
    /// click 500 ms and 2 DIPs from the first makes a double click, and 1 ms or 0.5 DIP more does not.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An engine over a line, zoomed by 2; animations off.
    ///
    /// Steps:
    /// - Press, move 4 pixels, release; press, move 4.5 pixels, release. Click twice 500 ms and 2 pixels apart; zoom again;
    ///   click twice 501 ms apart; click twice 2.5 pixels apart.
    ///
    /// Expected:
    /// - The first moves no view (a click), the second pans (a drag). The first pair shows the full view; the other two do
    ///   not.
    /// </remarks>
    [Fact]
    public void Thresholds_OfDragAndDoubleClick()
    {
        using var f = new EngineFixture();
        f.Engine.Options.ZoomAnimation = TimeSpan.Zero;
        var full = f.View();
        f.Engine.Views.ZoomAt(EngineFixture.Panel, f.Middle, 2);
        var zoomed = f.View();

        void Press(Vector2 from, Vector2 to)
        {
            f.Engine.OnPointer(f.Pointer(PointerEventKind.Down, from, PointerButtons.Left));
            f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, to) with { Buttons = PointerButtons.Left });
            f.Engine.OnPointer(f.Pointer(PointerEventKind.Up, to, PointerButtons.Left));
        }

        Press(f.Middle, f.Middle + new Vector2(4, 0));
        Assert.Equal(zoomed, f.View());
        Press(f.Middle, f.Middle + new Vector2(4.5f, 0));
        Assert.NotEqual(zoomed, f.View());

        void Pair(TimeSpan apart, float distance)
        {
            f.Engine.Views.ZoomAt(EngineFixture.Panel, f.Middle, 2);
            f.Time.Advance(TimeSpan.FromSeconds(2));
            Press(f.Middle, f.Middle);
            f.Time.Advance(apart);
            Press(f.Middle + new Vector2(distance, 0), f.Middle + new Vector2(distance, 0));
        }

        Pair(TimeSpan.FromMilliseconds(500), 2);
        Assert.Equal(full, f.View());
        Pair(TimeSpan.FromMilliseconds(501), 0);
        Assert.NotEqual(full, f.View());
        Pair(TimeSpan.FromMilliseconds(100), 2.5f);
        Assert.NotEqual(full, f.View());
    }

    /// <summary>
    /// A tool's overlay is drawn last, over the plot, while it holds the pointer (design §6.5): the rectangle zoom draws its
    /// rectangle, and nothing after the release.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An engine over a line with a backend that keeps the last list drawn.
    ///
    /// Steps:
    /// - Press the right button and move 60 × 40 pixels; draw; release; draw.
    ///
    /// Expected:
    /// - Two lists in the frame, the last the overlay: a fill and four outline rectangles spanning the drag; after the
    ///   release, one list.
    /// </remarks>
    [Fact]
    public void Overlay_DrawnLastWhileHeld()
    {
        using var f = new EngineFixture();
        f.Engine.OnPointer(f.Pointer(PointerEventKind.Down, f.Middle, PointerButtons.Right));
        f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, f.Middle + new Vector2(60, 40)) with { Buttons = PointerButtons.Right });
        f.Engine.RenderFrame();
        Assert.Equal(2, f.Backend.DrawsInFrame);
        var rects = f.Backend.LastList!.Rects.ToArray();
        Assert.Equal(5, rects.Length);
        Assert.Equal((f.Middle.X, f.Middle.Y, 60f, 40f), (rects[0].X, rects[0].Y, rects[0].Width, rects[0].Height));

        f.Engine.OnPointer(f.Pointer(PointerEventKind.Up, f.Middle + new Vector2(60, 40), PointerButtons.Right));
        f.Settle();
        Assert.Equal(1, f.Backend.DrawsInFrame);
    }

    private static bool Narrower(EngineFixture f, (ViewRange X, ViewRange Y) full, (bool X, bool Y) moved)
    {
        var view = f.View();
        return moved == (true, true)
            && view.X.Upper - view.X.Lower < full.X.Upper - full.X.Lower
            && view.Y.Upper - view.Y.Lower < full.Y.Upper - full.Y.Lower;
    }

    private sealed class RecordingTool : IPlotTool
    {
        public List<PointerEvent> Events { get; } = [];

        public int Cancels { get; private set; }

        public ToolResult OnPointer(in PointerEvent e, ToolContext context)
        {
            Events.Add(e);
            return e.Kind == PointerEventKind.Up ? ToolResult.Release : ToolResult.Capture;
        }

        public void Cancel(ToolContext context) => Cancels++;

        public void Overlay(RenderList list, ToolContext context) { }
    }
}
