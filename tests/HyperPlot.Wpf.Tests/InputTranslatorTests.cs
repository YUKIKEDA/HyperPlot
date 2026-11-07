using System.Numerics;
using System.Windows;
using System.Windows.Input;
using HyperPlot.Interaction;

namespace HyperPlot.Wpf.Tests;

/// <summary>
/// The values of WPF's input as the engine takes them (design §6.1 "WPF の変換", §10, row W1a), and the fingers on a view.
/// </summary>
public sealed class InputTranslatorTests
{
    /// <summary>A position in DIPs becomes device pixels by the DPI's scale on each side (design §6.1, §7.2).</summary>
    /// <remarks>
    /// Preconditions:
    /// - None.
    ///
    /// Steps:
    /// - Convert (10, 20) DIPs at scales 1 and 1, 1.5 and 1.5, 1.25 and 2.
    ///
    /// Expected:
    /// - (10, 20), (15, 30), (12.5, 40) pixels.
    /// </remarks>
    [Theory]
    [InlineData(1.0, 1.0, 10f, 20f)]
    [InlineData(1.5, 1.5, 15f, 30f)]
    [InlineData(1.25, 2.0, 12.5f, 40f)]
    public void Position_ScalesToPixels(double scaleX, double scaleY, float x, float y) =>
        Assert.Equal(new Vector2(x, y), InputTranslator.ToPixels(new Point(10, 20), scaleX, scaleY));

    /// <summary>A view's pixels are its DIPs times the DPI's scale, rounded (design §7.2).</summary>
    /// <remarks>
    /// Preconditions:
    /// - None.
    ///
    /// Steps:
    /// - The pixels of 300.4 × 200.6 DIPs at 1 and 1, of 101 × 33 at 1.5 and 1.5 (151.5 and 49.5), and of 0.3 × 10 at 1.
    ///
    /// Expected:
    /// - 300 × 201; 152 × 50 (halves round away from 0); 0 × 10.
    /// </remarks>
    [Theory]
    [InlineData(300.4, 200.6, 1.0, 300, 201)]
    [InlineData(101, 33, 1.5, 152, 50)]
    [InlineData(0.3, 10, 1.0, 0, 10)]
    public void Size_RoundsThePixels(double width, double height, double scale, int pixelsWide, int pixelsTall) =>
        Assert.Equal((pixelsWide, pixelsTall), InputTranslator.PixelSize(width, height, scale, scale));

    /// <summary>A wheel's delta is in notches of 120, with fractions from a high-resolution wheel (design §6.1).</summary>
    /// <remarks>
    /// Preconditions:
    /// - None.
    ///
    /// Steps:
    /// - Convert 120, −240, 30, and 0.
    ///
    /// Expected:
    /// - 1, −2, 0.25, 0.
    /// </remarks>
    [Theory]
    [InlineData(120, 1.0)]
    [InlineData(-240, -2.0)]
    [InlineData(30, 0.25)]
    [InlineData(0, 0.0)]
    public void Wheel_InNotches(int delta, double notches) => Assert.Equal(notches, InputTranslator.Notches(delta));

    /// <summary>WM_MOUSEHWHEEL's values: the delta from wParam's high word, the screen point from lParam's signed words (design §6.1).</summary>
    /// <remarks>
    /// Preconditions:
    /// - None.
    ///
    /// Steps:
    /// - Read wParam 120 · 65,536 + 8 (a key state in the low word) with lParam (300, 40); wParam −60 · 65,536 with lParam
    ///   (−5, −7), a monitor left of and above the primary.
    ///
    /// Expected:
    /// - 1 notch at (300, 40); −0.5 notch at (−5, −7).
    /// </remarks>
    [Fact]
    public void HorizontalWheel_ReadsTheMessage()
    {
        Assert.Equal((1.0, 300, 40), InputTranslator.HorizontalWheel(((nint)120 << 16) | 8, (40 << 16) | 300));
        nint left = unchecked((ushort)-5) | (unchecked((ushort)-7) << 16);
        Assert.Equal((-0.5, -5, -7), InputTranslator.HorizontalWheel(unchecked((nint)(uint)((ushort)-60 << 16)), left));
    }

    /// <summary>Buttons, held buttons, modifiers, and kinds of device map one to one (design §6.1).</summary>
    /// <remarks>
    /// Preconditions:
    /// - None.
    ///
    /// Steps:
    /// - Map each WPF mouse button; the five button states with left and X2 pressed; the four WPF modifiers together and none;
    ///   a mouse, a pen, and a touch.
    ///
    /// Expected:
    /// - Left, Right, Middle, X1, X2; Left | X2; Shift | Control | Alt | Meta and None; Mouse, Pen, Touch.
    /// </remarks>
    [Fact]
    public void ButtonsModifiersAndDevices_Map()
    {
        Assert.Equal(
            [PointerButtons.Left, PointerButtons.Right, PointerButtons.Middle, PointerButtons.X1, PointerButtons.X2],
            new[] { MouseButton.Left, MouseButton.Right, MouseButton.Middle, MouseButton.XButton1, MouseButton.XButton2 }.Select(
                InputTranslator.Button
            )
        );
        var (up, down) = (MouseButtonState.Released, MouseButtonState.Pressed);
        Assert.Equal(PointerButtons.Left | PointerButtons.X2, InputTranslator.Buttons(down, up, up, up, down));
        Assert.Equal(
            Interaction.ModifierKeys.Shift | Interaction.ModifierKeys.Control | Interaction.ModifierKeys.Alt | Interaction.ModifierKeys.Meta,
            InputTranslator.Modifiers(
                System.Windows.Input.ModifierKeys.Shift
                    | System.Windows.Input.ModifierKeys.Control
                    | System.Windows.Input.ModifierKeys.Alt
                    | System.Windows.Input.ModifierKeys.Windows
            )
        );
        Assert.Equal(Interaction.ModifierKeys.None, InputTranslator.Modifiers(System.Windows.Input.ModifierKeys.None));
        Assert.Equal(
            [PointerKind.Mouse, PointerKind.Pen, PointerKind.Touch],
            [InputTranslator.Kind(false, false), InputTranslator.Kind(true, false), InputTranslator.Kind(true, true)]
        );
    }

    /// <summary>The keys the engine knows map from both rows and the keypad; others are none (design §6.1).</summary>
    /// <remarks>
    /// Preconditions:
    /// - None.
    ///
    /// Steps:
    /// - Map each key of the table.
    ///
    /// Expected:
    /// - The key in the table's second column.
    /// </remarks>
    [Theory]
    [InlineData(Key.Left, PlotKey.Left)]
    [InlineData(Key.Right, PlotKey.Right)]
    [InlineData(Key.Up, PlotKey.Up)]
    [InlineData(Key.Down, PlotKey.Down)]
    [InlineData(Key.OemPlus, PlotKey.Plus)]
    [InlineData(Key.Add, PlotKey.Plus)]
    [InlineData(Key.OemMinus, PlotKey.Minus)]
    [InlineData(Key.Subtract, PlotKey.Minus)]
    [InlineData(Key.Home, PlotKey.Home)]
    [InlineData(Key.Escape, PlotKey.Escape)]
    [InlineData(Key.BrowserBack, PlotKey.BrowserBack)]
    [InlineData(Key.BrowserForward, PlotKey.BrowserForward)]
    [InlineData(Key.A, PlotKey.A)]
    [InlineData(Key.Z, PlotKey.Z)]
    [InlineData(Key.D0, PlotKey.D0)]
    [InlineData(Key.D9, PlotKey.D9)]
    [InlineData(Key.NumPad0, PlotKey.D0)]
    [InlineData(Key.NumPad9, PlotKey.D9)]
    [InlineData(Key.F1, PlotKey.None)]
    [InlineData(Key.Tab, PlotKey.None)]
    public void Keys_Map(Key key, PlotKey expected) => Assert.Equal(expected, InputTranslator.Key(key));

    /// <summary>
    /// One finger is a pointer; a second cancels it, and the touch is a gesture until every finger is up (design §6.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A new tracker.
    ///
    /// Steps:
    /// - Finger 1 down, moves; finger 2 down; finger 1 moves; finger 3 down; fingers 1, 2, 3 up; finger 4 down, moves, up.
    ///
    /// Expected:
    /// - Pointer, Pointer; Cancel and gesturing; None; None; None, None, None and not gesturing after the last; Pointer,
    ///   Pointer, Pointer.
    /// </remarks>
    [Fact]
    public void Touch_OneFingerPointsTwoGesture()
    {
        var touch = new TouchTracker();
        Assert.Equal(TouchTracker.Action.Pointer, touch.Down(1));
        Assert.Equal(TouchTracker.Action.Pointer, touch.Move(1));
        Assert.Equal(TouchTracker.Action.Cancel, touch.Down(2));
        Assert.True(touch.Gesturing);
        Assert.Equal(TouchTracker.Action.None, touch.Move(1));
        Assert.Equal(TouchTracker.Action.None, touch.Down(3));
        Assert.Equal(TouchTracker.Action.None, touch.Up(1));
        Assert.Equal(TouchTracker.Action.None, touch.Up(2));
        Assert.True(touch.Gesturing);
        Assert.Equal(TouchTracker.Action.None, touch.Up(3));
        Assert.False(touch.Gesturing);
        Assert.Equal(TouchTracker.Action.Pointer, touch.Down(4));
        Assert.Equal(TouchTracker.Action.Pointer, touch.Move(4));
        Assert.Equal(TouchTracker.Action.Pointer, touch.Up(4));
        Assert.Equal(-1, touch.Pointer);
    }
}
