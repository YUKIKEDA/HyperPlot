using System.Numerics;
using HyperPlot.Interaction;

namespace HyperPlot.Tests;

/// <summary>
/// The input of an engine and the places under the pointer (design §6.1, §6.3, row I1a), with no GPU: a backend that draws
/// nothing and the fake text backend, so that Linux runs them (design §6.6).
/// </summary>
public sealed class EngineInputTests
{
    /// <summary>The input events carry what the host passed: value types of their members (design §6.1).</summary>
    /// <remarks>
    /// Preconditions:
    /// - None.
    ///
    /// Steps:
    /// - Make one event of each type and read its members; compare one with a copy.
    ///
    /// Expected:
    /// - The members as given; a copy equal to the event.
    /// </remarks>
    [Fact]
    public void Events_CarryTheirValues()
    {
        var pointer = new PointerEvent(
            PointerEventKind.Down,
            new Vector2(3, 4),
            PointerButtons.Right,
            PointerButtons.Right,
            ModifierKeys.Control,
            PointerKind.Pen,
            42
        );
        var wheel = new WheelEvent(new Vector2(1, 2), 0.5, -1.25, ModifierKeys.Shift, 7);
        var key = new KeyEvent(KeyEventKind.Up, PlotKey.Q, ModifierKeys.Alt, true, 9);
        var gesture = new GestureEvent(GestureKind.Pinch, new Vector2(5, 6), 1.5, new Vector2(7, 8), 11);

        Assert.Equal(
            (PointerEventKind.Down, new Vector2(3, 4), PointerButtons.Right, PointerKind.Pen, 42L),
            (pointer.Kind, pointer.Position, pointer.Button, pointer.Device, pointer.Timestamp)
        );
        Assert.Equal((0.5, -1.25, ModifierKeys.Shift), (wheel.DeltaX, wheel.DeltaY, wheel.Modifiers));
        Assert.Equal((KeyEventKind.Up, PlotKey.Q, true), (key.Kind, key.Key, key.IsRepeat));
        Assert.Equal((GestureKind.Pinch, 1.5, new Vector2(7, 8)), (gesture.Kind, gesture.Scale, gesture.Delta));
        Assert.Equal(pointer, pointer with { });
    }

    /// <summary>A call from another thread than the engine's throws (design §6.1, Issue #266's decision 8).</summary>
    /// <remarks>
    /// Preconditions:
    /// - An engine over a line, drawn once.
    ///
    /// Steps:
    /// - From a task: draw a frame, pass a wheel's turn, set the plot, read the place under a pixel.
    ///
    /// Expected:
    /// - InvalidOperationException each time.
    /// </remarks>
    [Fact]
    public void OtherThread_Throws()
    {
        using var f = new EngineFixture();
        var calls = new Action[]
        {
            f.Engine.RenderFrame,
            () => f.Wheel(f.Middle, 1),
            () => f.Engine.SetPlot(f.Spec),
            () => f.Engine.HitArea(f.Middle),
        };
        foreach (var call in calls)
        {
            var task = Task.Run(call, TestContext.Current.CancellationToken);
            Assert.IsType<InvalidOperationException>(Eventually.Faults(task));
        }
    }

    /// <summary>
    /// The places under the pointer (design §6.3): the panel, the four axis bands, the legend, the title, a figure's space, and
    /// nothing outside; the edges of the panel count as the panel's from its left and top only. Flip swaps the axes' bands.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A line with a title and a label (a legend), x with a secondary axis on top, y with one on the right; the same flipped.
    ///
    /// Steps:
    /// - Read the place at the panel's middle, its top-left pixel, the pixel past its right and bottom ends, 5 pixels outside
    ///   each side, the title's middle, the legend's middle, a corner of the target, and outside the target.
    ///
    /// Expected:
    /// - Panel; Panel; AxisY2 and AxisX; AxisX, AxisX2, AxisY, AxisY2 below, above, left, and right; Title; Legend; Figure;
    ///   None. Flipped: the bottom and left bands are y's and x's.
    /// </remarks>
    [Fact]
    public void HitArea_NamesEachPlace()
    {
        static PlotBuilder Sides(PlotBuilder b) =>
            b.Title("Places").ScaleX(Scale.Linear().WithSecondaryAxis()).ScaleY(Scale.Linear().WithDomain(-20, 20).WithSecondaryAxis());
        using var f = new EngineFixture(b => Sides(b));
        var (left, top, width, height) = f.Rect;
        var (right, bottom) = (left + width, top + height);
        var e = f.Engine;
        HitAreaKind At(float x, float y) => e.HitArea(new Vector2(x, y)).Kind;

        Assert.Equal(HitAreaKind.Panel, At(left + (width / 2f), top + (height / 2f)));
        Assert.Equal(HitAreaKind.Panel, At(left, top));
        Assert.Equal(HitAreaKind.AxisY2, At(right, top + 5));
        Assert.Equal(HitAreaKind.AxisX, At(left + 5, bottom));
        Assert.Equal(HitAreaKind.AxisX, At(left + 5, bottom + 5));
        Assert.Equal(HitAreaKind.AxisX2, At(left + 5, top - 5));
        Assert.Equal(HitAreaKind.AxisY, At(left - 5, top + 5));
        Assert.Equal(HitAreaKind.AxisY2, At(right + 5, top + 5));
        Assert.Equal(HitAreaKind.Title, At(EngineFixture.Width / 2f, 12));
        Assert.Equal(HitAreaKind.Figure, At(1, EngineFixture.Height - 1));
        Assert.Equal(HitAreaKind.None, At(-1, 10));
        Assert.Equal(HitAreaKind.None, At(EngineFixture.Width, 10));

        using var legend = new EngineFixture(b => b.Title("Legend"));
        var lineWithLabel = Plot.Create(legend.Table)
            .Line(legend.Table.Col<double>("x"), legend.Table.Col<float>("y"), l => l.Label("sine"))
            .ScaleY(Scale.Linear().WithDomain(-20, 20))
            .Build();
        legend.Engine.SetPlot(lineWithLabel);
        legend.Engine.RenderFrame();
        Assert.Equal(HitAreaKind.Legend, legend.Engine.HitArea(new Vector2(EngineFixture.Width - 20, legend.Middle.Y)).Kind);

        using var flipped = new EngineFixture(b => b.Coord(Coord.Cartesian(flip: true)));
        var r = flipped.Rect;
        Assert.Equal(HitAreaKind.AxisY, flipped.Engine.HitArea(new Vector2(r.Left + 5, r.Top + r.Height + 5)).Kind);
        Assert.Equal(HitAreaKind.AxisX, flipped.Engine.HitArea(new Vector2(r.Left - 5, r.Top + 5)).Kind);
    }

    /// <summary>
    /// The engine says whether it used an input (design §6.1): with no binding it uses none, and the host may pass it on.
    /// Before a frame, there is no place to read.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An engine over a line, drawn once; a second engine with no frame drawn.
    ///
    /// Steps:
    /// - Pass a wheel's turn and a Home; clear the bindings and pass a press, a wheel's turn, a key, and a pinch; read the place
    ///   under a pixel of the engine with no frame.
    ///
    /// Expected:
    /// - True twice; false for each with no binding, and the view did not move; None.
    /// </remarks>
    [Fact]
    public void Bindings_None_UseNoInput()
    {
        using var f = new EngineFixture();
        Assert.True(f.Wheel(f.Middle, 1));
        Assert.True(f.Key(PlotKey.Home));
        f.Settle();
        var before = f.View();
        f.Engine.Bindings.Clear();

        Assert.False(f.Engine.OnPointer(f.Pointer(PointerEventKind.Down, f.Middle, PointerButtons.Left)));
        Assert.False(f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, f.Middle + new Vector2(50, 0))));
        Assert.False(f.Engine.OnPointer(f.Pointer(PointerEventKind.Up, f.Middle, PointerButtons.Left)));
        Assert.False(f.Wheel(f.Middle, 1));
        Assert.False(f.Key(PlotKey.Home));
        Assert.False(f.Engine.OnGesture(new GestureEvent(GestureKind.Pinch, f.Middle, 2, Vector2.Zero, f.Time.GetTimestamp())));
        f.Settle();
        Assert.Equal(before, f.View());

        using var fresh = new PlotEngine(new NullBackend(), 100, 100, 1, f.Pool, f.Time);
        Assert.Equal(HitAreaKind.None, fresh.HitArea(new Vector2(50, 50)).Kind);
    }

    /// <summary>
    /// A spec is checked when it is set, and a figure too (design §7.1); a figure is drawn and its plots take views (row I1b); an
    /// operation before the first frame has no layout to read and throws.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An engine over a line, drawn; a fresh engine with no frame.
    ///
    /// Steps:
    /// - Set a spec with a null theme; set a figure with a spacing of −1; set a figure of two plots and draw it; zoom its
    ///   second plot; zoom a third plot; on the fresh engine, zoom.
    ///
    /// Expected:
    /// - ArgumentNullException and ArgumentException at the set, the engine drawing the line still; the figure's frame
    ///   draws three lists (the figure's and two plots'); the second plot's view narrower (row I1b);
    ///   ArgumentOutOfRangeException for the third plot; InvalidOperationException for the fresh engine's zoom.
    /// </remarks>
    [Fact]
    public void Specs_CheckedWhenSet_FigureDrawn()
    {
        using var f = new EngineFixture();
        _ = Assert.Throws<ArgumentNullException>(() => f.Engine.SetPlot(f.Spec with { Theme = null! }));
        _ = Assert.Throws<ArgumentException>(() => f.Engine.SetFigure(Figure.HConcat(f.Spec, f.Spec) with { Spacing = -1 }));
        f.Engine.SetPlot(f.Spec);
        f.Engine.RenderFrame();

        f.Engine.SetFigure(Figure.HConcat(f.Spec, f.Spec));
        f.Engine.RenderFrame();
        Assert.Equal(3, f.Backend.DrawsInFrame);
        var second = new PanelId(1, 0);
        var before = ((PanelView2D)f.Engine.CurrentView(second)).X;
        f.Engine.Views.ZoomAt(second, new Vector2(EngineFixture.Width * 0.75f, EngineFixture.Height / 2f), 2, animate: false);
        var after = ((PanelView2D)f.Engine.CurrentView(second)).X;
        Assert.True(after.HighValue - after.LowValue < before.HighValue - before.LowValue);
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => f.Engine.Views.ZoomAt(new PanelId(2, 0), new Vector2(100, 100), 2));

        using var fresh = new PlotEngine(new NullBackend(), 100, 100, 1, f.Pool, f.Time);
        fresh.SetPlot(f.Spec);
        _ = Assert.Throws<InvalidOperationException>(() => fresh.Views.ZoomAt(EngineFixture.Panel, new Vector2(50, 50), 2));
    }
}
