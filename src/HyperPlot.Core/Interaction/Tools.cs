using System.Numerics;
using HyperPlot.Rendering;

namespace HyperPlot.Interaction;

/// <summary>What lies at a pixel of what an engine draws (design §6.3). Flags, so that a binding names a set of them.</summary>
[Flags]
public enum HitAreaKind
{
    /// <summary>Nothing the plot draws: outside the target, or a place no other kind names.</summary>
    None = 0,

    /// <summary>A panel, where the data are drawn.</summary>
    Panel = 1,

    /// <summary>The x scale's axis band.</summary>
    AxisX = 2,

    /// <summary>The y scale's axis band.</summary>
    AxisY = 4,

    /// <summary>The secondary x scale's axis band (row G6a).</summary>
    AxisX2 = 8,

    /// <summary>The secondary y scale's axis band (row G6a).</summary>
    AxisY2 = 16,

    /// <summary>The legend.</summary>
    Legend = 32,

    /// <summary>The plot's title.</summary>
    Title = 64,

    /// <summary>A figure's padding or the space between its cells (row G6c).</summary>
    Figure = 128,

    /// <summary>Every axis band.</summary>
    Axes = AxisX | AxisY | AxisX2 | AxisY2,

    /// <summary>Every kind but <see cref="None"/>.</summary>
    All = Panel | Axes | Legend | Title | Figure,
}

/// <summary>What lies at a pixel (design §6.3, row I1a): its kind and the panel it belongs to.</summary>
/// <param name="Kind">The kind, one flag of <see cref="HitAreaKind"/>.</param>
/// <param name="Panel">The panel the place belongs to: a panel, or the panel an axis band runs along.</param>
public readonly record struct HitArea(HitAreaKind Kind, PanelId Panel);

/// <summary>What a tool did with an event (design §6.5).</summary>
public enum ToolResult
{
    /// <summary>The tool did not use the event: the engine offers it to the next binding.</summary>
    Ignored,

    /// <summary>The tool used the event.</summary>
    Handled,

    /// <summary>The tool used the event and takes the pointer: every pointer event goes to it until it releases it.</summary>
    Capture,

    /// <summary>The tool used the event and gives the pointer back.</summary>
    Release,
}

/// <summary>
/// A tool of the engine (design §6.5, row I1a): it reads events with a context, keeps the state of what it does (a drag), and
/// changes views through <see cref="ToolContext.Views"/> only. The built-in tools are tools of this kind too.
/// </summary>
public interface IPlotTool
{
    /// <summary>Reads a pointer's event. A drag's tool gets the press first, once the pointer has moved past the threshold.</summary>
    /// <param name="e">The event.</param>
    /// <param name="context">The engine's context.</param>
    /// <returns>What the tool did.</returns>
    ToolResult OnPointer(in PointerEvent e, ToolContext context) => ToolResult.Ignored;

    /// <summary>Reads a wheel's turn.</summary>
    /// <param name="e">The event.</param>
    /// <param name="context">The engine's context.</param>
    /// <returns>What the tool did.</returns>
    ToolResult OnWheel(in WheelEvent e, ToolContext context) => ToolResult.Ignored;

    /// <summary>Reads a key.</summary>
    /// <param name="e">The event.</param>
    /// <param name="context">The engine's context.</param>
    /// <returns>What the tool did.</returns>
    ToolResult OnKey(in KeyEvent e, ToolContext context) => ToolResult.Ignored;

    /// <summary>Reads a gesture.</summary>
    /// <param name="e">The event.</param>
    /// <param name="context">The engine's context.</param>
    /// <returns>What the tool did.</returns>
    ToolResult OnGesture(in GestureEvent e, ToolContext context) => ToolResult.Ignored;

    /// <summary>
    /// Drops what the tool is doing: on Escape, a pointer's cancel, a new spec whose scales map otherwise, or the tool's
    /// removal. The engine releases the pointer the tool held.
    /// </summary>
    /// <param name="context">The engine's context.</param>
    void Cancel(ToolContext context) { }

    /// <summary>Records what the tool draws over the plot, after the data and the axes, each frame while it holds the pointer.</summary>
    /// <param name="list">The list to record into, in device pixels.</param>
    /// <param name="context">The engine's context.</param>
    void Overlay(RenderList list, ToolContext context) { }
}

/// <summary>
/// What a tool reads and calls (design §6.5): the place under the pointer, the panel's rectangle, the conversion between
/// pixels and the scale space, the views, the time, and the theme. The engine keeps one and reuses it.
/// </summary>
public sealed class ToolContext
{
    private readonly PlotEngine _engine;

    internal ToolContext(PlotEngine engine) => _engine = engine;

    /// <summary>Gets what lies under the pointer of the event the tool reads.</summary>
    public HitArea Hit { get; internal set; }

    /// <summary>Gets the operations on the views.</summary>
    public ViewOperations Views => _engine.Views;

    /// <summary>Gets the engine's time.</summary>
    public TimeProvider Time => _engine.Time;

    /// <summary>Gets the engine's settings of the interaction.</summary>
    public InteractionOptions Options => _engine.Options;

    /// <summary>Gets the theme of the plot drawn: the colors a tool's overlay takes.</summary>
    public Theme Theme => _engine.CurrentTheme;

    /// <summary>Gets the scale from device-independent to device pixels.</summary>
    public float Scale => _engine.Scale;

    /// <summary>Returns a panel's rectangle in device pixels.</summary>
    /// <param name="panel">The panel.</param>
    /// <returns>Its left, top, width, and height.</returns>
    public (int Left, int Top, int Width, int Height) PanelRectangle(PanelId panel) => _engine.PanelRectangle(panel);

    /// <summary>Returns the value of the scale space at a pixel of a panel's current view, x then y.</summary>
    /// <param name="panel">The panel.</param>
    /// <param name="pixel">The pixel, in device pixels.</param>
    /// <returns>The x and y values as ranges of one point: origin and difference.</returns>
    public (ViewRange X, ViewRange Y) PixelToView(PanelId panel, Vector2 pixel) => _engine.PixelToView(panel, pixel);

    /// <summary>Asks for a frame: a tool that changes what it draws over the plot calls it.</summary>
    public void RequestFrame() => _engine.RequestFrame();
}

/// <summary>A command a binding runs instead of a tool (design §6.5): an operation with no state of its own.</summary>
public enum ViewCommand
{
    /// <summary>Shows the full view.</summary>
    Reset,

    /// <summary>Goes back in the history.</summary>
    Back,

    /// <summary>Goes forward in the history.</summary>
    Forward,

    /// <summary>Zooms in at the view's middle by <see cref="InteractionOptions.KeyZoomFactor"/>.</summary>
    ZoomIn,

    /// <summary>Zooms out at the view's middle by <see cref="InteractionOptions.KeyZoomFactor"/>.</summary>
    ZoomOut,

    /// <summary>Moves the view toward the low x by <see cref="InteractionOptions.KeyPanFraction"/> of its width.</summary>
    PanLeft,

    /// <summary>Moves the view toward the high x.</summary>
    PanRight,

    /// <summary>Moves the view toward the high y.</summary>
    PanUp,

    /// <summary>Moves the view toward the low y.</summary>
    PanDown,

    /// <summary>Cancels the tool that holds the pointer.</summary>
    CancelTool,
}

/// <summary>What shape of input a binding matches (design §6.5).</summary>
public enum InputGestureKind
{
    /// <summary>A press of a button and a move past the drag threshold.</summary>
    Drag,

    /// <summary>A press and a release of a button within the threshold, a number of times in a row.</summary>
    Click,

    /// <summary>A wheel's turn.</summary>
    Wheel,

    /// <summary>A key going down.</summary>
    Key,

    /// <summary>A pinch.</summary>
    Pinch,

    /// <summary>A two-finger scroll.</summary>
    Scroll,
}

/// <summary>A shape of input (design §6.5): a drag or a click of a button, a wheel, a key, a pinch, or a scroll.</summary>
/// <param name="Kind">The shape.</param>
/// <param name="Button">The button of a drag or a click.</param>
/// <param name="ClickCount">The clicks in a row of a click: 1 or 2.</param>
/// <param name="Key">The key of a key.</param>
public readonly record struct InputGesture(InputGestureKind Kind, PointerButtons Button = PointerButtons.None, int ClickCount = 0, PlotKey Key = PlotKey.None)
{
    /// <summary>Returns a drag of a button.</summary>
    /// <param name="button">The button.</param>
    /// <returns>The shape.</returns>
    public static InputGesture Drag(PointerButtons button) => new(InputGestureKind.Drag, button);

    /// <summary>Returns a click of a button, a number of times in a row.</summary>
    /// <param name="button">The button.</param>
    /// <param name="count">1 or 2.</param>
    /// <returns>The shape.</returns>
    public static InputGesture Click(PointerButtons button, int count = 1) => new(InputGestureKind.Click, button, count);

    /// <summary>Gets a wheel's turn.</summary>
    public static InputGesture Wheel => new(InputGestureKind.Wheel);

    /// <summary>Returns a key going down.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The shape.</returns>
    public static InputGesture KeyDown(PlotKey key) => new(InputGestureKind.Key, Key: key);

    /// <summary>Gets a pinch.</summary>
    public static InputGesture Pinch => new(InputGestureKind.Pinch);

    /// <summary>Gets a two-finger scroll.</summary>
    public static InputGesture Scroll => new(InputGestureKind.Scroll);
}

/// <summary>
/// One entry of the bindings (design §6.5): a shape of input, the modifier keys held (exactly), the kinds of place it applies
/// to, and the tool or the command it runs.
/// </summary>
public sealed record InputBinding
{
    /// <summary>Initializes a new instance of the <see cref="InputBinding"/> class that runs a tool.</summary>
    /// <param name="gesture">The shape of input.</param>
    /// <param name="modifiers">The modifier keys held.</param>
    /// <param name="areas">The kinds of place.</param>
    /// <param name="tool">The tool.</param>
    /// <exception cref="ArgumentNullException"><paramref name="tool"/> is null.</exception>
    public InputBinding(InputGesture gesture, ModifierKeys modifiers, HitAreaKind areas, IPlotTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        (Gesture, Modifiers, Areas, Tool) = (gesture, modifiers, areas, tool);
    }

    /// <summary>Initializes a new instance of the <see cref="InputBinding"/> class that runs a command.</summary>
    /// <param name="gesture">The shape of input.</param>
    /// <param name="modifiers">The modifier keys held.</param>
    /// <param name="areas">The kinds of place.</param>
    /// <param name="command">The command.</param>
    public InputBinding(InputGesture gesture, ModifierKeys modifiers, HitAreaKind areas, ViewCommand command) =>
        (Gesture, Modifiers, Areas, Command) = (gesture, modifiers, areas, command);

    /// <summary>Gets the shape of input.</summary>
    public InputGesture Gesture { get; }

    /// <summary>Gets the modifier keys that must be held, exactly.</summary>
    public ModifierKeys Modifiers { get; }

    /// <summary>Gets the kinds of place the binding applies to; a key applies wherever the pointer is.</summary>
    public HitAreaKind Areas { get; }

    /// <summary>Gets the tool, or null for a command.</summary>
    public IPlotTool? Tool { get; }

    /// <summary>Gets the command when <see cref="Tool"/> is null.</summary>
    public ViewCommand Command { get; }
}

/// <summary>
/// The bindings of an engine (design §6.5): the entries in the order the engine tries them. An application clears, adds,
/// inserts, or removes entries; with none, the engine uses no input.
/// </summary>
public sealed class InputBindings : List<InputBinding>
{
    /// <summary>
    /// Returns the default bindings (design §6.5, after Plotly's and matplotlib's): a left drag pans (one axis on an axis), a
    /// right drag or a Control left drag zooms to a rectangle, the wheel and a pinch zoom at the pointer (one axis on an
    /// axis), a two-finger scroll pans, a double click shows the full view; the arrows pan, Plus and Minus zoom, Home shows the
    /// full view, the browser's keys, Alt and an arrow, and the extra buttons go back and forward, and Escape cancels a tool.
    /// </summary>
    /// <returns>A new list of the default bindings, with new built-in tools.</returns>
    public static InputBindings CreateDefault()
    {
        var pan = new PanTool();
        var rectangle = new RectangleZoomTool();
        var wheel = new WheelZoomTool();
        const HitAreaKind PanelOrAxes = HitAreaKind.Panel | HitAreaKind.Axes;
        return
        [
            new(InputGesture.Drag(PointerButtons.Left), ModifierKeys.Control, HitAreaKind.Panel, rectangle),
            new(InputGesture.Drag(PointerButtons.Left), ModifierKeys.None, PanelOrAxes, pan),
            new(InputGesture.Drag(PointerButtons.Right), ModifierKeys.None, HitAreaKind.Panel, rectangle),
            new(InputGesture.Wheel, ModifierKeys.None, PanelOrAxes, wheel),
            new(InputGesture.Pinch, ModifierKeys.None, HitAreaKind.Panel, wheel),
            new(InputGesture.Scroll, ModifierKeys.None, HitAreaKind.Panel, pan),
            new(InputGesture.Click(PointerButtons.Left, 2), ModifierKeys.None, PanelOrAxes, ViewCommand.Reset),
            new(InputGesture.Click(PointerButtons.X1), ModifierKeys.None, HitAreaKind.All, ViewCommand.Back),
            new(InputGesture.Click(PointerButtons.X2), ModifierKeys.None, HitAreaKind.All, ViewCommand.Forward),
            new(InputGesture.KeyDown(PlotKey.Left), ModifierKeys.Alt, HitAreaKind.All, ViewCommand.Back),
            new(InputGesture.KeyDown(PlotKey.Right), ModifierKeys.Alt, HitAreaKind.All, ViewCommand.Forward),
            new(InputGesture.KeyDown(PlotKey.BrowserBack), ModifierKeys.None, HitAreaKind.All, ViewCommand.Back),
            new(InputGesture.KeyDown(PlotKey.BrowserForward), ModifierKeys.None, HitAreaKind.All, ViewCommand.Forward),
            new(InputGesture.KeyDown(PlotKey.Left), ModifierKeys.None, HitAreaKind.All, ViewCommand.PanLeft),
            new(InputGesture.KeyDown(PlotKey.Right), ModifierKeys.None, HitAreaKind.All, ViewCommand.PanRight),
            new(InputGesture.KeyDown(PlotKey.Up), ModifierKeys.None, HitAreaKind.All, ViewCommand.PanUp),
            new(InputGesture.KeyDown(PlotKey.Down), ModifierKeys.None, HitAreaKind.All, ViewCommand.PanDown),
            new(InputGesture.KeyDown(PlotKey.Plus), ModifierKeys.None, HitAreaKind.All, ViewCommand.ZoomIn),
            new(InputGesture.KeyDown(PlotKey.Minus), ModifierKeys.None, HitAreaKind.All, ViewCommand.ZoomOut),
            new(InputGesture.KeyDown(PlotKey.Home), ModifierKeys.None, HitAreaKind.All, ViewCommand.Reset),
            new(InputGesture.KeyDown(PlotKey.Escape), ModifierKeys.None, HitAreaKind.All, ViewCommand.CancelTool),
        ];
    }
}

/// <summary>
/// The settings of an engine's interaction (design §6.5), with their defaults: Windows's for the double click and the drag,
/// chosen values for the rest (design §6.5 says why).
/// </summary>
public sealed class InteractionOptions
{
    private TimeSpan _zoomAnimation = TimeSpan.FromMilliseconds(150);
    private double _wheelZoomFactor = 1.2;
    private double _keyPanFraction = 0.1;
    private double _keyZoomFactor = 1.25;
    private int _historyDepth = 100;
    private TimeSpan _wheelGroupGap = TimeSpan.FromMilliseconds(300);
    private TimeSpan _doubleClickTime = TimeSpan.FromMilliseconds(500);
    private float _doubleClickDistance = 2;
    private float _dragThreshold = 4;

    /// <summary>Gets or sets the length of a zoom's animation: 0 or more; 0 for none. 150 ms by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public TimeSpan ZoomAnimation
    {
        get => _zoomAnimation;
        set => _zoomAnimation = value >= TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The animation is 0 or more.");
    }

    /// <summary>Gets or sets the zoom of one wheel notch: finite and above 1. 1.2 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not finite or not above 1.</exception>
    public double WheelZoomFactor
    {
        get => _wheelZoomFactor;
        set => _wheelZoomFactor = double.IsFinite(value) && value > 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The factor is finite and above 1.");
    }

    /// <summary>Gets or sets the share of the view's width an arrow key pans: finite, above 0, at most 1. 0.1 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not in (0, 1].</exception>
    public double KeyPanFraction
    {
        get => _keyPanFraction;
        set => _keyPanFraction = value is > 0 and <= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The fraction is in (0, 1].");
    }

    /// <summary>Gets or sets the zoom of Plus and Minus: finite and above 1. 1.25 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not finite or not above 1.</exception>
    public double KeyZoomFactor
    {
        get => _keyZoomFactor;
        set => _keyZoomFactor = double.IsFinite(value) && value > 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The factor is finite and above 1.");
    }

    /// <summary>Gets or sets the most entries the history keeps: at least 1. 100 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is below 1.</exception>
    public int HistoryDepth
    {
        get => _historyDepth;
        set => _historyDepth = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The depth is at least 1.");
    }

    /// <summary>Gets or sets the pause after which a wheel's turns start a new entry of the history: above 0. 300 ms by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not above 0.</exception>
    public TimeSpan WheelGroupGap
    {
        get => _wheelGroupGap;
        set => _wheelGroupGap = value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The gap is above 0.");
    }

    /// <summary>Gets or sets the most time between the clicks of a double click: above 0. 500 ms by default, as Windows's.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not above 0.</exception>
    public TimeSpan DoubleClickTime
    {
        get => _doubleClickTime;
        set => _doubleClickTime = value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The time is above 0.");
    }

    /// <summary>Gets or sets how far, in DIPs on each axis, the second click of a double click may lie from the first: 0 or more. 2 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not finite or negative.</exception>
    public float DoubleClickDistance
    {
        get => _doubleClickDistance;
        set => _doubleClickDistance = float.IsFinite(value) && value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The distance is finite and 0 or more.");
    }

    /// <summary>Gets or sets how far, in DIPs on each axis, a pressed pointer moves before a drag starts: 0 or more. 4 by default, as Windows's.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not finite or negative.</exception>
    public float DragThreshold
    {
        get => _dragThreshold;
        set => _dragThreshold = float.IsFinite(value) && value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The threshold is finite and 0 or more.");
    }
}
