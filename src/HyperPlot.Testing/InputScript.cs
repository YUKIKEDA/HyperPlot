using System.Globalization;
using System.Numerics;
using HyperPlot.Interaction;

namespace HyperPlot.Testing;

/// <summary>
/// A place of an input script (design §6.6): a pixel, or a point of a panel's scale space that the script turns into a
/// pixel through the panel's current view when it runs.
/// </summary>
public readonly struct ScriptPoint
{
    private readonly Vector2 _pixel;
    private readonly PanelId _panel;
    private readonly double _x;
    private readonly double _y;
    private readonly bool _ofView;

    private ScriptPoint(Vector2 pixel, PanelId panel, double x, double y, bool ofView) =>
        (_pixel, _panel, _x, _y, _ofView) = (pixel, panel, x, y, ofView);

    /// <summary>Returns a pixel.</summary>
    /// <param name="x">The x, in device pixels from the left.</param>
    /// <param name="y">The y, in device pixels from the top.</param>
    /// <returns>The place.</returns>
    public static ScriptPoint Pixel(float x, float y) => new(new Vector2(x, y), default, 0, 0, ofView: false);

    /// <summary>
    /// Returns a point of a panel's scale space (the data's values on a linear scale), placed by the panel's view when the
    /// script reaches it.
    /// </summary>
    /// <param name="panel">The panel.</param>
    /// <param name="x">The x value.</param>
    /// <param name="y">The y value.</param>
    /// <returns>The place.</returns>
    public static ScriptPoint Data(PanelId panel, double x, double y) => new(default, panel, x, y, ofView: true);

    internal Vector2 Resolve(PlotEngine engine) =>
        _ofView ? engine.ViewToPixel(_panel, ViewRange.FromValues(_x, _x + 1), ViewRange.FromValues(_y, _y + 1)) : _pixel;
}

/// <summary>
/// A script of input for an engine (design §6.6, row I1a): the steps a user takes, written in pixels or in data, run with a
/// time the caller moves. Tests and applications use it to reproduce an interaction: the same script over the same plot
/// gives the same views and notices each time.
/// </summary>
/// <remarks>
/// Each step moves the time by <see cref="Step"/> (or by a <see cref="Wait(TimeSpan)"/>'s length) through the caller's
/// delegate, stamps its events with the engine's time, passes them, and draws a frame when the engine asks for one.
/// </remarks>
public sealed class InputScript
{
    private readonly List<Action<PlotEngine, Action<TimeSpan>, Func<bool>>> _steps = [];

    /// <summary>Gets or sets the time between two events: 10 ms by default.</summary>
    public TimeSpan Step { get; set; } = TimeSpan.FromMilliseconds(10);

    /// <summary>Gets or sets a value indicating whether the script draws the frames the engine asks for; true by default.</summary>
    public bool RendersFrames { get; set; } = true;

    /// <summary>Moves the pointer.</summary>
    /// <param name="at">Where to.</param>
    /// <returns>The script.</returns>
    public InputScript Move(ScriptPoint at) => Pointer(PointerEventKind.Move, at, PointerButtons.None, PointerButtons.None, ModifierKeys.None);

    /// <summary>Presses a button.</summary>
    /// <param name="at">Where.</param>
    /// <param name="button">The button.</param>
    /// <param name="modifiers">The modifier keys held.</param>
    /// <returns>The script.</returns>
    public InputScript Press(ScriptPoint at, PointerButtons button = PointerButtons.Left, ModifierKeys modifiers = ModifierKeys.None) =>
        Pointer(PointerEventKind.Down, at, button, button, modifiers);

    /// <summary>Releases a button.</summary>
    /// <param name="at">Where.</param>
    /// <param name="button">The button.</param>
    /// <param name="modifiers">The modifier keys held.</param>
    /// <returns>The script.</returns>
    public InputScript Release(ScriptPoint at, PointerButtons button = PointerButtons.Left, ModifierKeys modifiers = ModifierKeys.None) =>
        Pointer(PointerEventKind.Up, at, button, PointerButtons.None, modifiers);

    /// <summary>Drags a button from one place to another in steps.</summary>
    /// <param name="from">Where the press is.</param>
    /// <param name="to">Where the release is.</param>
    /// <param name="button">The button.</param>
    /// <param name="modifiers">The modifier keys held.</param>
    /// <param name="steps">The moves between, at least 1.</param>
    /// <returns>The script.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="steps"/> is below 1.</exception>
    public InputScript Drag(
        ScriptPoint from,
        ScriptPoint to,
        PointerButtons button = PointerButtons.Left,
        ModifierKeys modifiers = ModifierKeys.None,
        int steps = 8
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(steps, 1);
        _steps.Add(
            (engine, advance, frame) =>
            {
                // Both ends are placed when the drag starts, through the view as it is then.
                var (a, b) = (from.Resolve(engine), to.Resolve(engine));
                Send(engine, advance, frame, () => engine.OnPointer(Make(engine, PointerEventKind.Down, a, button, button, modifiers)));
                for (var i = 1; i <= steps; i++)
                {
                    var at = Vector2.Lerp(a, b, i / (float)steps);
                    Send(
                        engine,
                        advance,
                        frame,
                        () => engine.OnPointer(Make(engine, PointerEventKind.Move, at, PointerButtons.None, button, modifiers))
                    );
                }

                Send(engine, advance, frame, () => engine.OnPointer(Make(engine, PointerEventKind.Up, b, button, PointerButtons.None, modifiers)));
            }
        );
        return this;
    }

    /// <summary>Clicks a button: a press and a release at the same place.</summary>
    /// <param name="at">Where.</param>
    /// <param name="button">The button.</param>
    /// <returns>The script.</returns>
    public InputScript Click(ScriptPoint at, PointerButtons button = PointerButtons.Left) => Press(at, button).Release(at, button);

    /// <summary>Clicks a button twice, a step apart.</summary>
    /// <param name="at">Where.</param>
    /// <param name="button">The button.</param>
    /// <returns>The script.</returns>
    public InputScript DoubleClick(ScriptPoint at, PointerButtons button = PointerButtons.Left) => Click(at, button).Click(at, button);

    /// <summary>Turns the wheel.</summary>
    /// <param name="at">Where the pointer is.</param>
    /// <param name="notches">The notches: positive away from the user.</param>
    /// <param name="modifiers">The modifier keys held.</param>
    /// <returns>The script.</returns>
    public InputScript Wheel(ScriptPoint at, double notches, ModifierKeys modifiers = ModifierKeys.None)
    {
        _steps.Add(
            (engine, advance, frame) =>
                Send(
                    engine,
                    advance,
                    frame,
                    () => engine.OnWheel(new WheelEvent(at.Resolve(engine), 0, notches, modifiers, engine.Time.GetTimestamp()))
                )
        );
        return this;
    }

    /// <summary>Presses and releases a key.</summary>
    /// <param name="key">The key.</param>
    /// <param name="modifiers">The modifier keys held.</param>
    /// <returns>The script.</returns>
    public InputScript Key(PlotKey key, ModifierKeys modifiers = ModifierKeys.None)
    {
        _steps.Add(
            (engine, advance, frame) =>
            {
                Send(engine, advance, frame, () => engine.OnKey(new KeyEvent(KeyEventKind.Down, key, modifiers, false, engine.Time.GetTimestamp())));
                Send(engine, advance, frame, () => engine.OnKey(new KeyEvent(KeyEventKind.Up, key, modifiers, false, engine.Time.GetTimestamp())));
            }
        );
        return this;
    }

    /// <summary>Pinches at a place.</summary>
    /// <param name="at">The pinch's center.</param>
    /// <param name="scale">The scale since the event before: above 1 apart.</param>
    /// <returns>The script.</returns>
    public InputScript Pinch(ScriptPoint at, double scale)
    {
        _steps.Add(
            (engine, advance, frame) =>
                Send(
                    engine,
                    advance,
                    frame,
                    () => engine.OnGesture(new GestureEvent(GestureKind.Pinch, at.Resolve(engine), scale, Vector2.Zero, engine.Time.GetTimestamp()))
                )
        );
        return this;
    }

    /// <summary>Scrolls with two fingers.</summary>
    /// <param name="at">Where the fingers are.</param>
    /// <param name="delta">The move in device pixels.</param>
    /// <returns>The script.</returns>
    public InputScript Scroll(ScriptPoint at, Vector2 delta)
    {
        _steps.Add(
            (engine, advance, frame) =>
                Send(
                    engine,
                    advance,
                    frame,
                    () => engine.OnGesture(new GestureEvent(GestureKind.Scroll, at.Resolve(engine), 1, delta, engine.Time.GetTimestamp()))
                )
        );
        return this;
    }

    /// <summary>Lets time pass with no input, drawing the frames the engine asks for meanwhile (an animation's).</summary>
    /// <param name="time">How long, 0 or more.</param>
    /// <returns>The script.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="time"/> is negative.</exception>
    public InputScript Wait(TimeSpan time)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(time, TimeSpan.Zero);
        _steps.Add(
            (engine, advance, frame) =>
            {
                var left = time;
                while (left > TimeSpan.Zero)
                {
                    var step = left < Step ? left : Step;
                    advance(step);
                    left -= step;
                    frame();
                }
            }
        );
        return this;
    }

    /// <summary>Runs the script over an engine.</summary>
    /// <param name="engine">The engine, with its plot set.</param>
    /// <param name="advance">Moves the engine's time forward: the caller's fake time's <c>Advance</c>.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public void Run(PlotEngine engine, Action<TimeSpan> advance)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(advance);
        var asked = false;
        void Ask() => asked = true;
        bool Frame()
        {
            if (asked && RendersFrames)
            {
                asked = false;
                engine.RenderFrame();
                return true;
            }

            return false;
        }

        engine.FrameRequested += Ask;
        try
        {
            Frame();
            foreach (var step in _steps)
            {
                step(engine, advance, Frame);
            }
        }
        finally
        {
            engine.FrameRequested -= Ask;
        }
    }

    private static PointerEvent Make(
        PlotEngine engine,
        PointerEventKind kind,
        Vector2 at,
        PointerButtons button,
        PointerButtons buttons,
        ModifierKeys modifiers
    ) =>
        new(
            kind,
            at,
            kind is PointerEventKind.Down or PointerEventKind.Up ? button : PointerButtons.None,
            buttons,
            modifiers,
            PointerKind.Mouse,
            engine.Time.GetTimestamp()
        );

    private void Send(PlotEngine engine, Action<TimeSpan> advance, Func<bool> frame, Func<bool> send)
    {
        advance(Step);
        _ = send();
        _ = frame();
    }

    private InputScript Pointer(PointerEventKind kind, ScriptPoint at, PointerButtons button, PointerButtons buttons, ModifierKeys modifiers)
    {
        _steps.Add(
            (engine, advance, frame) =>
                Send(engine, advance, frame, () => engine.OnPointer(Make(engine, kind, at.Resolve(engine), button, buttons, modifiers)))
        );
        return this;
    }
}

/// <summary>
/// Plays back a recording of input (design §6.6): the lines <see cref="PlotEngine.StartRecording"/> wrote, the time between
/// two events kept, so that a user's interaction is reproduced from one file.
/// </summary>
public static class InputReplay
{
    /// <summary>Plays a recording over an engine.</summary>
    /// <param name="engine">The engine, with its plot set and drawn.</param>
    /// <param name="reader">The recording.</param>
    /// <param name="advance">Moves the engine's time forward: the caller's fake time's <c>Advance</c>.</param>
    /// <param name="rendersFrames">Whether to draw the frames the engine asks for; true by default.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">The recording is not one, or its target's size or scale differs from the engine's.</exception>
    /// <exception cref="FormatException">A line is not an event of a recording.</exception>
    public static void Run(PlotEngine engine, TextReader reader, Action<TimeSpan> advance, bool rendersFrames = true)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(advance);
        var header = reader.ReadLine() ?? throw new InvalidOperationException("The recording is empty.");
        if (!header.StartsWith("hyperplot-input 1 ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The recording starts with \"{header}\", not a recording of HyperPlot's input.");
        }

        var fields = Fields(header);

        var (width, height, scale) = (
            int.Parse(fields["width"], CultureInfo.InvariantCulture),
            int.Parse(fields["height"], CultureInfo.InvariantCulture),
            float.Parse(fields["scale"], CultureInfo.InvariantCulture)
        );
        if (width != engine.Width || height != engine.Height || scale != engine.Scale)
        {
            throw new InvalidOperationException(
                $"The recording's target is {width} × {height} at {scale}; the engine's is {engine.Width} × {engine.Height} at {engine.Scale}."
            );
        }

        var asked = false;
        void Ask() => asked = true;
        engine.FrameRequested += Ask;
        try
        {
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                var words = line.Split(' ');
                var f = Fields(line);
                advance(TimeSpan.FromMicroseconds(long.Parse(f["t"], CultureInfo.InvariantCulture)));
                var now = engine.Time.GetTimestamp();
                _ = words[1] switch
                {
                    "pointer" => engine.OnPointer(
                        new PointerEvent(
                            Enum.Parse<PointerEventKind>(words[2]),
                            new Vector2(Single(f, "x"), Single(f, "y")),
                            Enum.Parse<PointerButtons>(f["button"]),
                            Enum.Parse<PointerButtons>(f["buttons"]),
                            Enum.Parse<ModifierKeys>(f["mods"]),
                            Enum.Parse<PointerKind>(f["kind"]),
                            now
                        )
                    ),
                    "wheel" => engine.OnWheel(
                        new WheelEvent(
                            new Vector2(Single(f, "x"), Single(f, "y")),
                            Double(f, "dx"),
                            Double(f, "dy"),
                            Enum.Parse<ModifierKeys>(f["mods"]),
                            now
                        )
                    ),
                    "key" => engine.OnKey(
                        new KeyEvent(
                            Enum.Parse<KeyEventKind>(words[2]),
                            Enum.Parse<PlotKey>(f["key"]),
                            Enum.Parse<ModifierKeys>(f["mods"]),
                            bool.Parse(f["repeat"]),
                            now
                        )
                    ),
                    "gesture" => engine.OnGesture(
                        new GestureEvent(
                            Enum.Parse<GestureKind>(words[2]),
                            new Vector2(Single(f, "x"), Single(f, "y")),
                            Double(f, "scale"),
                            new Vector2(Single(f, "dx"), Single(f, "dy")),
                            now
                        )
                    ),
                    _ => throw new FormatException($"\"{line}\" is not an event of a recording."),
                };
                if (asked && rendersFrames)
                {
                    asked = false;
                    engine.RenderFrame();
                }
            }
        }
        finally
        {
            engine.FrameRequested -= Ask;
        }
    }

    private static Dictionary<string, string> Fields(string line)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var word in line.Split(' '))
        {
            var at = word.IndexOf('=', StringComparison.Ordinal);
            if (at > 0)
            {
                fields[word[..at]] = word[(at + 1)..];
            }
        }

        return fields;
    }

    private static float Single(Dictionary<string, string> fields, string name) => float.Parse(fields[name], CultureInfo.InvariantCulture);

    private static double Double(Dictionary<string, string> fields, string name) => double.Parse(fields[name], CultureInfo.InvariantCulture);
}
