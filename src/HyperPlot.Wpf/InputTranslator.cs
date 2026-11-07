using System.Numerics;
using System.Windows;
using System.Windows.Input;
using HyperPlot.Interaction;

namespace HyperPlot.Wpf;

/// <summary>
/// The values of WPF's input as the engine takes them (design §6.1 "WPF の変換", row W1a): positions from DIPs to device
/// pixels, buttons, modifiers, keys, wheel notches, and the horizontal wheel's message. Pure functions, so that the tests
/// check each without a window.
/// </summary>
internal static class InputTranslator
{
    /// <summary>The wheel's delta of one notch (<c>WHEEL_DELTA</c>).</summary>
    public const double WheelDelta = 120;

    /// <summary>The message of the horizontal wheel, which WPF raises no event for.</summary>
    public const int WmMouseHWheel = 0x020E;

    /// <summary>Returns a position in DIPs from the element's top left as device pixels from the target's.</summary>
    public static Vector2 ToPixels(Point dips, double scaleX, double scaleY) => new((float)(dips.X * scaleX), (float)(dips.Y * scaleY));

    /// <summary>Returns a size in DIPs as whole device pixels: each side times its scale, halves rounded away from 0.</summary>
    public static (int Width, int Height) PixelSize(double width, double height, double scaleX, double scaleY) =>
        ((int)Math.Round(width * scaleX, MidpointRounding.AwayFromZero), (int)Math.Round(height * scaleY, MidpointRounding.AwayFromZero));

    /// <summary>Returns a wheel's delta in notches: <c>Delta</c> / 120, with fractions from a high-resolution wheel.</summary>
    public static double Notches(int delta) => delta / WheelDelta;

    /// <summary>Returns the button of a WPF mouse button.</summary>
    public static PointerButtons Button(MouseButton button) =>
        button switch
        {
            MouseButton.Left => PointerButtons.Left,
            MouseButton.Right => PointerButtons.Right,
            MouseButton.Middle => PointerButtons.Middle,
            MouseButton.XButton1 => PointerButtons.X1,
            _ => PointerButtons.X2,
        };

    /// <summary>Returns the buttons held from the states of WPF's five mouse buttons.</summary>
    public static PointerButtons Buttons(
        MouseButtonState left,
        MouseButtonState right,
        MouseButtonState middle,
        MouseButtonState x1,
        MouseButtonState x2
    ) =>
        (left == MouseButtonState.Pressed ? PointerButtons.Left : PointerButtons.None)
        | (right == MouseButtonState.Pressed ? PointerButtons.Right : PointerButtons.None)
        | (middle == MouseButtonState.Pressed ? PointerButtons.Middle : PointerButtons.None)
        | (x1 == MouseButtonState.Pressed ? PointerButtons.X1 : PointerButtons.None)
        | (x2 == MouseButtonState.Pressed ? PointerButtons.X2 : PointerButtons.None);

    /// <summary>Returns the modifier keys of WPF's: the Windows key is Meta.</summary>
    public static Interaction.ModifierKeys Modifiers(System.Windows.Input.ModifierKeys modifiers) =>
        ((modifiers & System.Windows.Input.ModifierKeys.Shift) != 0 ? Interaction.ModifierKeys.Shift : Interaction.ModifierKeys.None)
        | ((modifiers & System.Windows.Input.ModifierKeys.Control) != 0 ? Interaction.ModifierKeys.Control : Interaction.ModifierKeys.None)
        | ((modifiers & System.Windows.Input.ModifierKeys.Alt) != 0 ? Interaction.ModifierKeys.Alt : Interaction.ModifierKeys.None)
        | ((modifiers & System.Windows.Input.ModifierKeys.Windows) != 0 ? Interaction.ModifierKeys.Meta : Interaction.ModifierKeys.None);

    /// <summary>
    /// Returns the kind of a pointer from the stylus device a mouse event came from: none for a mouse, a touch device's
    /// type for a touch, a pen otherwise.
    /// </summary>
    public static PointerKind Kind(bool fromStylus, bool isTouch) =>
        !fromStylus ? PointerKind.Mouse
        : isTouch ? PointerKind.Touch
        : PointerKind.Pen;

    /// <summary>
    /// Returns the key the engine knows for a WPF key: the arrows, plus and minus (the main row's and the keypad's), Home,
    /// Escape, the browser's back and forward, letters, and digits (the main row's and the keypad's); <see cref="PlotKey.None"/>
    /// for the rest.
    /// </summary>
    public static PlotKey Key(Key key) =>
        key switch
        {
            System.Windows.Input.Key.Left => PlotKey.Left,
            System.Windows.Input.Key.Right => PlotKey.Right,
            System.Windows.Input.Key.Up => PlotKey.Up,
            System.Windows.Input.Key.Down => PlotKey.Down,
            System.Windows.Input.Key.OemPlus or System.Windows.Input.Key.Add => PlotKey.Plus,
            System.Windows.Input.Key.OemMinus or System.Windows.Input.Key.Subtract => PlotKey.Minus,
            System.Windows.Input.Key.Home => PlotKey.Home,
            System.Windows.Input.Key.Escape => PlotKey.Escape,
            System.Windows.Input.Key.BrowserBack => PlotKey.BrowserBack,
            System.Windows.Input.Key.BrowserForward => PlotKey.BrowserForward,
            >= System.Windows.Input.Key.A and <= System.Windows.Input.Key.Z => PlotKey.A + (key - System.Windows.Input.Key.A),
            >= System.Windows.Input.Key.D0 and <= System.Windows.Input.Key.D9 => PlotKey.D0 + (key - System.Windows.Input.Key.D0),
            >= System.Windows.Input.Key.NumPad0 and <= System.Windows.Input.Key.NumPad9 => PlotKey.D0 + (key - System.Windows.Input.Key.NumPad0),
            _ => PlotKey.None,
        };

    /// <summary>
    /// Reads <c>WM_MOUSEHWHEEL</c>: the delta in notches (positive tilts right) from the high word of wParam, and the pointer's
    /// screen position from lParam's two signed words.
    /// </summary>
    public static (double Notches, int ScreenX, int ScreenY) HorizontalWheel(nint wParam, nint lParam) =>
        (Notches((short)((wParam >> 16) & 0xFFFF)), (short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
}
