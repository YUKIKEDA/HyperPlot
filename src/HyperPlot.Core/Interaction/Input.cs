using System.Numerics;

namespace HyperPlot.Interaction;

/// <summary>What happened to a pointer (design §6.1).</summary>
public enum PointerEventKind
{
    /// <summary>The pointer moved.</summary>
    Move,

    /// <summary>A button went down.</summary>
    Down,

    /// <summary>A button went up.</summary>
    Up,

    /// <summary>The host lost the pointer (a capture taken away, a touch cancelled): a tool in progress is cancelled.</summary>
    Cancel,

    /// <summary>The pointer left the target (row I2b): the hover is cleared. No tool reads it.</summary>
    Leave,
}

/// <summary>The buttons of a pointer (design §6.1).</summary>
[Flags]
public enum PointerButtons
{
    /// <summary>No button.</summary>
    None = 0,

    /// <summary>The left button, or a pen's or a touch's contact.</summary>
    Left = 1,

    /// <summary>The right button.</summary>
    Right = 2,

    /// <summary>The middle button.</summary>
    Middle = 4,

    /// <summary>The first extra button (back).</summary>
    X1 = 8,

    /// <summary>The second extra button (forward).</summary>
    X2 = 16,
}

/// <summary>What kind of device a pointer is (design §6.1).</summary>
public enum PointerKind
{
    /// <summary>A mouse.</summary>
    Mouse,

    /// <summary>A pen.</summary>
    Pen,

    /// <summary>A touch.</summary>
    Touch,
}

/// <summary>The modifier keys held (design §6.1).</summary>
[Flags]
public enum ModifierKeys
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>Shift.</summary>
    Shift = 1,

    /// <summary>Control.</summary>
    Control = 2,

    /// <summary>Alt.</summary>
    Alt = 4,

    /// <summary>The Windows key, or Command on a Mac.</summary>
    Meta = 8,
}

/// <summary>
/// An event of a pointer (design §6.1, row I1a): a value the host makes from its own input and passes to
/// <see cref="PlotEngine.OnPointer"/>.
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="Position">Where, in device pixels from the target's top left.</param>
/// <param name="Button">The button that went down or up; <see cref="PointerButtons.None"/> for a move or a cancel.</param>
/// <param name="Buttons">The buttons held after the event.</param>
/// <param name="Modifiers">The modifier keys held.</param>
/// <param name="Device">The kind of device.</param>
/// <param name="Timestamp">When, as <see cref="TimeProvider.GetTimestamp"/> of the engine's time.</param>
public readonly record struct PointerEvent(
    PointerEventKind Kind,
    Vector2 Position,
    PointerButtons Button,
    PointerButtons Buttons,
    ModifierKeys Modifiers,
    PointerKind Device,
    long Timestamp
);

/// <summary>
/// A turn of a wheel (design §6.1, row I1a): its amount in notches, 1 a notch (WPF's <c>Delta</c> / 120), with fractions
/// from a high-resolution wheel. A positive vertical amount turns away from the user.
/// </summary>
/// <param name="Position">Where the pointer is, in device pixels.</param>
/// <param name="DeltaX">The horizontal amount in notches.</param>
/// <param name="DeltaY">The vertical amount in notches.</param>
/// <param name="Modifiers">The modifier keys held.</param>
/// <param name="Timestamp">When, as <see cref="TimeProvider.GetTimestamp"/> of the engine's time.</param>
public readonly record struct WheelEvent(Vector2 Position, double DeltaX, double DeltaY, ModifierKeys Modifiers, long Timestamp);

/// <summary>Whether a key went down or up (design §6.1).</summary>
public enum KeyEventKind
{
    /// <summary>The key went down, or repeats.</summary>
    Down,

    /// <summary>The key went up.</summary>
    Up,
}

/// <summary>The keys the engine reads (design §6.1): the host maps its own keys to these; others it does not pass.</summary>
public enum PlotKey
{
    /// <summary>No key the engine knows.</summary>
    None,

    /// <summary>The left arrow.</summary>
    Left,

    /// <summary>The right arrow.</summary>
    Right,

    /// <summary>The up arrow.</summary>
    Up,

    /// <summary>The down arrow.</summary>
    Down,

    /// <summary>Plus, or the keypad's add.</summary>
    Plus,

    /// <summary>Minus, or the keypad's subtract.</summary>
    Minus,

    /// <summary>Home.</summary>
    Home,

    /// <summary>Escape.</summary>
    Escape,

    /// <summary>The browser's back key.</summary>
    BrowserBack,

    /// <summary>The browser's forward key.</summary>
    BrowserForward,

    /// <summary>The letter A.</summary>
    A,

    /// <summary>The letter B.</summary>
    B,

    /// <summary>The letter C.</summary>
    C,

    /// <summary>The letter D.</summary>
    D,

    /// <summary>The letter E.</summary>
    E,

    /// <summary>The letter F.</summary>
    F,

    /// <summary>The letter G.</summary>
    G,

    /// <summary>The letter H.</summary>
    H,

    /// <summary>The letter I.</summary>
    I,

    /// <summary>The letter J.</summary>
    J,

    /// <summary>The letter K.</summary>
    K,

    /// <summary>The letter L.</summary>
    L,

    /// <summary>The letter M.</summary>
    M,

    /// <summary>The letter N.</summary>
    N,

    /// <summary>The letter O.</summary>
    O,

    /// <summary>The letter P.</summary>
    P,

    /// <summary>The letter Q.</summary>
    Q,

    /// <summary>The letter R.</summary>
    R,

    /// <summary>The letter S.</summary>
    S,

    /// <summary>The letter T.</summary>
    T,

    /// <summary>The letter U.</summary>
    U,

    /// <summary>The letter V.</summary>
    V,

    /// <summary>The letter W.</summary>
    W,

    /// <summary>The letter X.</summary>
    X,

    /// <summary>The letter Y.</summary>
    Y,

    /// <summary>The letter Z.</summary>
    Z,

    /// <summary>The digit 0, of the main keys or the keypad.</summary>
    D0,

    /// <summary>The digit 1, of the main keys or the keypad.</summary>
    D1,

    /// <summary>The digit 2, of the main keys or the keypad.</summary>
    D2,

    /// <summary>The digit 3, of the main keys or the keypad.</summary>
    D3,

    /// <summary>The digit 4, of the main keys or the keypad.</summary>
    D4,

    /// <summary>The digit 5, of the main keys or the keypad.</summary>
    D5,

    /// <summary>The digit 6, of the main keys or the keypad.</summary>
    D6,

    /// <summary>The digit 7, of the main keys or the keypad.</summary>
    D7,

    /// <summary>The digit 8, of the main keys or the keypad.</summary>
    D8,

    /// <summary>The digit 9, of the main keys or the keypad.</summary>
    D9,
}

/// <summary>An event of a key (design §6.1, row I1a).</summary>
/// <param name="Kind">Whether the key went down or up.</param>
/// <param name="Key">The key.</param>
/// <param name="Modifiers">The modifier keys held.</param>
/// <param name="IsRepeat">Whether a held key repeats.</param>
/// <param name="Timestamp">When, as <see cref="TimeProvider.GetTimestamp"/> of the engine's time.</param>
public readonly record struct KeyEvent(KeyEventKind Kind, PlotKey Key, ModifierKeys Modifiers, bool IsRepeat, long Timestamp);

/// <summary>What a gesture is (design §6.1).</summary>
public enum GestureKind
{
    /// <summary>Two fingers move apart or together.</summary>
    Pinch,

    /// <summary>Two fingers move together across a touchpad, or the host's inertia after them.</summary>
    Scroll,
}

/// <summary>
/// A gesture a host can pass (design §6.1, requirement I-8, row I1a): a pinch's scale since the event before, or a scroll's
/// amount in pixels. The host's inertia comes as more scrolls; the engine has none of its own.
/// </summary>
/// <param name="Kind">What the gesture is.</param>
/// <param name="Position">Its center, in device pixels.</param>
/// <param name="Scale">A pinch's scale since the event before: above 1 as the fingers move apart; 1 for a scroll.</param>
/// <param name="Delta">A scroll's amount in device pixels; zero for a pinch.</param>
/// <param name="Timestamp">When, as <see cref="TimeProvider.GetTimestamp"/> of the engine's time.</param>
public readonly record struct GestureEvent(GestureKind Kind, Vector2 Position, double Scale, Vector2 Delta, long Timestamp);
