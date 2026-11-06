using System.Globalization;
using System.Numerics;
using HyperPlot.Rendering;

namespace HyperPlot.Interaction;

/// <summary>
/// The engine of an interactive plot (design §6, §7.1, row I1a): it keeps a spec, the views, and the tools, takes the input
/// a host passes, and draws only when something changed. A host draws a frame with <see cref="RenderFrame"/> when
/// <see cref="FrameRequested"/> comes, at its own time to draw.
/// </summary>
/// <remarks>
/// <para>
/// Call it from the thread that made it, the host's UI thread; a call from another thread throws
/// <see cref="InvalidOperationException"/>. Input is handled at once on that thread, and time comes from the engine's
/// <see cref="TimeProvider"/> and the events' timestamps only (design §6.6), so that a sequence of input and a sequence of
/// time give the same views, notices, and frames each time.
/// </para>
/// <para>
/// In row I1a, <see cref="RenderFrame"/> waits for the plot's indexes as <see cref="HeadlessRenderer"/> does; row W1 draws
/// what is there and tells the host instead. The size of the target and its scale are fixed (row W1 changes them).
/// </para>
/// </remarks>
public sealed class PlotEngine : IDisposable
{
    private static readonly PanelId First = new(0, 0);

    private readonly HeadlessRenderer _renderer;
    private readonly int _thread;
    private readonly ToolContext _context;
    private readonly ViewProposal _proposal = new();
    private readonly RenderList _overlay = new();
    private readonly List<(ScaleRange X, ScaleRange Y)> _history = [];
    private PlotSpec? _plot;
    private FigureSpec? _figure;
    private bool _drawn;
    private bool _frameRequested;
    private ScaleRange _targetX = ScaleRange.None;
    private ScaleRange _targetY = ScaleRange.None;
    private ScaleRange _shownX = ScaleRange.None;
    private ScaleRange _shownY = ScaleRange.None;
    private ScaleRange _fromX;
    private ScaleRange _fromY;
    private long _animationStart;
    private bool _animating;
    private int _position;
    private bool _inGesture;
    private long _lastGroupTimestamp = long.MinValue;
    private bool _lastWasGroup;
    private ViewChangeReason _reason = ViewChangeReason.Code;
    private IPlotTool? _captured;
    private PointerEvent _press;
    private HitArea _pressArea;
    private bool _pressed;
    private bool _dragging;
    private PointerEvent _lastClick;
    private bool _hasLastClick;
    private TextWriter? _recording;
    private long _lastRecorded;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="PlotEngine"/> class with a target of its own.</summary>
    /// <param name="backend">The backend that draws.</param>
    /// <param name="width">The target's width in device pixels.</param>
    /// <param name="height">The target's height in device pixels.</param>
    /// <param name="scale">The scale from device-independent to device pixels, as <see cref="HeadlessRenderer"/>'s.</param>
    /// <param name="pool">The pool that builds the indexes; null for <see cref="PlotWorkerPool.Shared"/>.</param>
    /// <param name="time">The time of the animations; null for <see cref="TimeProvider.System"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="backend"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A size is out of range, or the scale is not finite and above 0.</exception>
    public PlotEngine(IGpuBackend backend, int width, int height, float scale = 1, PlotWorkerPool? pool = null, TimeProvider? time = null)
    {
        _renderer = new HeadlessRenderer(backend, width, height, pool, scale);
        Time = time ?? TimeProvider.System;
        _thread = Environment.CurrentManagedThreadId;
        _context = new ToolContext(this);
        Views = new ViewOperations(this);
        _history.Add((ScaleRange.None, ScaleRange.None));
    }

    /// <summary>Occurs when the engine needs a frame: once until <see cref="RenderFrame"/> draws it.</summary>
    public event Action? FrameRequested;

    /// <summary>Occurs when a view changes (design §6.2): once a change, with the view it goes to.</summary>
    public event ViewChangedHandler? ViewChanged;

    /// <summary>Gets the operations on the views.</summary>
    public ViewOperations Views { get; }

    /// <summary>Gets the bindings, in the order the engine tries them: the defaults to start with.</summary>
    public InputBindings Bindings { get; } = InputBindings.CreateDefault();

    /// <summary>Gets the application's constraints of the views, in the order the engine applies them (design §6.2).</summary>
    public List<IViewConstraint> Constraints { get; } = [];

    /// <summary>Gets the settings of the interaction.</summary>
    public InteractionOptions Options { get; } = new();

    /// <summary>Gets the time of the animations.</summary>
    public TimeProvider Time { get; }

    /// <summary>Gets the target's width in device pixels.</summary>
    public int Width => _renderer.Width;

    /// <summary>Gets the target's height in device pixels.</summary>
    public int Height => _renderer.Height;

    /// <summary>Gets the scale from device-independent to device pixels.</summary>
    public float Scale => _renderer.Scale;

    internal bool CanGoBack => _position > 0;

    internal bool CanGoForward => _position < _history.Count - 1;

    internal Theme CurrentTheme => _plot?.Theme ?? _figure?.Theme ?? Theme.Light;

    /// <summary>Gets the plan of the plot drawn. Tests only.</summary>
    internal PlotPlan? Plan => _renderer.Plan;

    /// <summary>Gets the tool that holds the pointer. Tests only.</summary>
    internal IPlotTool? Captured => _captured;

    /// <summary>Gets the number of entries of the history and the place in it. Tests only.</summary>
    internal (int Count, int Position) History => (_history.Count, _position);

    /// <summary>Sets the plot to draw (design §7.1). The spec is checked now; it is compiled at the next frame.</summary>
    /// <param name="plot">The plot.</param>
    /// <exception cref="ArgumentNullException"><paramref name="plot"/> or a member is null.</exception>
    /// <exception cref="ArgumentException">The plot does not check, as <see cref="HeadlessRenderer.Render(PlotSpec)"/> says.</exception>
    /// <exception cref="InvalidOperationException">The call comes from another thread than the engine's.</exception>
    /// <exception cref="ObjectDisposedException">The engine was disposed.</exception>
    public void SetPlot(PlotSpec plot)
    {
        CheckThread();
        _renderer.Check(plot);
        if (_figure is not null)
        {
            ResetState();
        }

        (_plot, _figure) = (plot, null);
        RequestFrame();
    }

    /// <summary>
    /// Sets a figure of several plots to draw (design §7.1, row G6c). In row I1a a figure is drawn and takes no view; its
    /// interaction is row I1b's. The figure is checked now (design §4.5); its plots are compiled and checked at the next frame.
    /// </summary>
    /// <param name="figure">The figure.</param>
    /// <exception cref="ArgumentNullException"><paramref name="figure"/>, a plot, or a cell is null.</exception>
    /// <exception cref="ArgumentException">The figure does not check (design §4.5).</exception>
    /// <exception cref="InvalidOperationException">The call comes from another thread than the engine's.</exception>
    /// <exception cref="ObjectDisposedException">The engine was disposed.</exception>
    public void SetFigure(FigureSpec figure)
    {
        CheckThread();
        _renderer.Check(figure);
        ResetState();
        (_plot, _figure) = (null, figure);
        RequestFrame();
    }

    /// <summary>
    /// Draws a frame (design §7.1): moves the animation to the engine's time, gives the views to the plan, draws, and draws
    /// what the tool that holds the pointer draws over it. While an animation runs, it asks for the next frame.
    /// </summary>
    /// <exception cref="InvalidOperationException">The call comes from another thread, or the plot fails as <see cref="HeadlessRenderer.Render(PlotSpec)"/> says.</exception>
    /// <exception cref="ArgumentException">The plot does not check.</exception>
    /// <exception cref="GpuDeviceLostException">The device was lost.</exception>
    /// <exception cref="ObjectDisposedException">The engine was disposed.</exception>
    public void RenderFrame()
    {
        CheckThread();
        _frameRequested = false;
        if (_figure is not null)
        {
            _renderer.Render(_figure);
            _drawn = true;
            return;
        }

        if (_plot is null)
        {
            return;
        }

        Animate();
        _overlay.Clear();
        _captured?.Overlay(_overlay, _context);
        _renderer.Render(_plot, _shownX, _shownY, _overlay);
        _drawn = true;
        if (_renderer.Plan!.ViewsDropped)
        {
            // A new mapping: the views over the old one go back to the full view, and the history with them (design §6.2).
            CancelCaptured();
            ResetState();
            Notify(ViewChangeReason.Spec, ScaleRange.None, ScaleRange.None);
        }

        if (_animating)
        {
            RequestFrame();
        }
    }

    /// <summary>Reads the last frame as RGBA, top row first, alpha not premultiplied (design §7.5).</summary>
    /// <param name="destination">At least Width · Height · 4 bytes.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short.</exception>
    /// <exception cref="InvalidOperationException">The call comes from another thread than the engine's.</exception>
    /// <exception cref="ObjectDisposedException">The engine was disposed.</exception>
    public void ReadPixels(Span<byte> destination)
    {
        CheckThread();
        _renderer.ReadPixels(destination);
    }

    /// <summary>Returns a panel's current view: the one drawn, mid-animation included.</summary>
    /// <param name="panel">The panel.</param>
    /// <returns>The view, as a new object.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The panel is not drawn.</exception>
    /// <exception cref="InvalidOperationException">The engine has drawn no frame of the plot, or the call comes from another thread.</exception>
    public PanelView CurrentView(PanelId panel)
    {
        CheckThread();
        CheckPanel(panel);
        var (x, y) = Shown();
        return new PanelView2D(ViewRange.FromScale(x), ViewRange.FromScale(y));
    }

    /// <summary>Returns what lies at a pixel (design §6.3): a panel, an axis band, the legend, the title, or nothing.</summary>
    /// <param name="pixel">The pixel, in device pixels.</param>
    /// <returns>The place; <see cref="HitAreaKind.None"/> before the first frame of a plot.</returns>
    /// <exception cref="InvalidOperationException">The call comes from another thread than the engine's.</exception>
    public HitArea HitArea(Vector2 pixel)
    {
        CheckThread();
        if (!_drawn || _plot is null || _renderer.Plan is not { } plan)
        {
            return default;
        }

        var g = plan.Guides;
        var (left, top, right, bottom) = (g.Left, g.Top, g.Left + g.Width, g.Top + g.Height);
        var (x, y) = (pixel.X, pixel.Y);
        if (x < 0 || y < 0 || x >= Width || y >= Height || g.Empty)
        {
            return default;
        }

        if (x >= left && x < right && y >= top && y < bottom)
        {
            return new HitArea(HitAreaKind.Panel, First);
        }

        var padding = MathF.Round(_plot.Theme.Padding * Scale);
        if (g.TitleBand > 0 && y < padding + g.TitleBand)
        {
            return new HitArea(HitAreaKind.Title, First);
        }

        if (g.LegendWidth > 0 && x >= Width - padding - g.LegendWidth)
        {
            return new HitArea(HitAreaKind.Legend, First);
        }

        var side =
            x >= left && x < right ? (y >= bottom ? PlotGuides.SideBottom : PlotGuides.SideTop)
            : y >= top && y < bottom ? (x < left ? PlotGuides.SideLeft : PlotGuides.SideRight)
            : -1;
        return side < 0 ? new HitArea(HitAreaKind.Figure, First) : new HitArea(AxisKind(g, g.SideAt(side).Axis), First);
    }

    /// <summary>
    /// Returns the pixel of a point of the scale space in a panel's current view (design §6.5): the inverse of the tools'
    /// <see cref="ToolContext.PixelToView"/>. A point outside the view gives a pixel outside the panel.
    /// </summary>
    /// <param name="panel">The panel.</param>
    /// <param name="x">The x value, a range of one point (its low end is read).</param>
    /// <param name="y">The y value, a range of one point.</param>
    /// <returns>The pixel, in device pixels.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The panel is not drawn.</exception>
    /// <exception cref="InvalidOperationException">The engine has drawn no frame of the plot, or the call comes from another thread.</exception>
    public Vector2 ViewToPixel(PanelId panel, ViewRange x, ViewRange y)
    {
        CheckPanel(panel);
        var (vx, vy) = Shown();
        var g = _renderer.Plan!.Guides;
        var fx = ((double)(x.Origin - vx.Origin) + x.Lower - vx.Lower) / (vx.Upper - vx.Lower);
        var fy = ((double)(y.Origin - vy.Origin) + y.Lower - vy.Lower) / (vy.Upper - vy.Lower);
        var (across, up) = g.Flipped ? (fy, fx) : (fx, fy);
        return new Vector2((float)(g.Left + (across * g.Width)), (float)(g.Top + g.Height - (up * g.Height)));
    }

    /// <summary>Takes a pointer's event (design §6.1).</summary>
    /// <param name="e">The event.</param>
    /// <returns>Whether the engine used it; one it did not use the host may pass on.</returns>
    /// <exception cref="InvalidOperationException">The call comes from another thread than the engine's.</exception>
    public bool OnPointer(in PointerEvent e)
    {
        CheckThread();
        Record(e);
        _reason = ViewChangeReason.Input;
        try
        {
            return Pointer(e);
        }
        finally
        {
            _reason = ViewChangeReason.Code;
        }
    }

    /// <summary>Takes a wheel's turn (design §6.1).</summary>
    /// <param name="e">The event.</param>
    /// <returns>Whether the engine used it.</returns>
    /// <exception cref="InvalidOperationException">The call comes from another thread than the engine's.</exception>
    public bool OnWheel(in WheelEvent e)
    {
        CheckThread();
        Record(e);
        _reason = ViewChangeReason.Input;
        try
        {
            var area = HitArea(e.Position);
            _context.Hit = area;
            foreach (var binding in Bindings)
            {
                if (binding.Gesture.Kind == InputGestureKind.Wheel && Matches(binding, e.Modifiers, area.Kind) && binding.Tool is { } tool)
                {
                    if (tool.OnWheel(e, _context) != ToolResult.Ignored)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        finally
        {
            _reason = ViewChangeReason.Code;
        }
    }

    /// <summary>Takes a key (design §6.1): the engine reads keys going down.</summary>
    /// <param name="e">The event.</param>
    /// <returns>Whether the engine used it.</returns>
    /// <exception cref="InvalidOperationException">The call comes from another thread than the engine's.</exception>
    public bool OnKey(in KeyEvent e)
    {
        CheckThread();
        Record(e);
        if (e.Kind != KeyEventKind.Down)
        {
            return false;
        }

        _reason = ViewChangeReason.Input;
        try
        {
            _context.Hit = new HitArea(HitAreaKind.None, First);
            foreach (var binding in Bindings)
            {
                if (binding.Gesture.Kind != InputGestureKind.Key || binding.Gesture.Key != e.Key || binding.Modifiers != e.Modifiers)
                {
                    continue;
                }

                if (binding.Tool is { } tool)
                {
                    if (tool.OnKey(e, _context) != ToolResult.Ignored)
                    {
                        return true;
                    }
                }
                else if (Run(binding.Command))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            _reason = ViewChangeReason.Code;
        }
    }

    /// <summary>Takes a gesture (design §6.1, requirement I-8).</summary>
    /// <param name="e">The event.</param>
    /// <returns>Whether the engine used it.</returns>
    /// <exception cref="InvalidOperationException">The call comes from another thread than the engine's.</exception>
    public bool OnGesture(in GestureEvent e)
    {
        CheckThread();
        Record(e);
        _reason = ViewChangeReason.Input;
        try
        {
            var area = HitArea(e.Position);
            _context.Hit = area;
            var kind = e.Kind == GestureKind.Pinch ? InputGestureKind.Pinch : InputGestureKind.Scroll;
            foreach (var binding in Bindings)
            {
                if (binding.Gesture.Kind == kind && Matches(binding, ModifierKeys.None, area.Kind) && binding.Tool is { } tool)
                {
                    if (tool.OnGesture(e, _context) != ToolResult.Ignored)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        finally
        {
            _reason = ViewChangeReason.Code;
        }
    }

    /// <summary>
    /// Starts writing every input the engine takes to a text, one event a line (design §6.6): the first line the target's
    /// size and scale, then each event with the time since the one before. <c>InputReplay</c> of <c>HyperPlot.Testing</c>
    /// plays it back. Writing allocates; it is on the input's path, not the frame's.
    /// </summary>
    /// <param name="writer">The text to write to; the caller disposes it after <see cref="StopRecording"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The call comes from another thread than the engine's.</exception>
    public void StartRecording(TextWriter writer)
    {
        CheckThread();
        ArgumentNullException.ThrowIfNull(writer);
        _recording = writer;
        _lastRecorded = Time.GetTimestamp();
        writer.WriteLine(InputLog.Header(Width, Height, Scale));
    }

    /// <summary>Stops writing the input.</summary>
    /// <exception cref="InvalidOperationException">The call comes from another thread than the engine's.</exception>
    public void StopRecording()
    {
        CheckThread();
        _recording?.Flush();
        _recording = null;
    }

    /// <summary>Releases the target and the plans. The backend, the pool, and the columns are the caller's.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _renderer.Dispose();
    }

    internal void RequestFrame()
    {
        if (_frameRequested)
        {
            return;
        }

        _frameRequested = true;
        FrameRequested?.Invoke();
    }

    /// <summary>Returns the y range the rows of a view of x show (row I1a): the work of <see cref="AutoRangeY"/>.</summary>
    internal bool TryVisibleY(PanelId panel, in ViewRange x, out ViewRange y)
    {
        y = default;
        if (_plot is null || !_drawn || _renderer.Plan is not { } plan || panel.Plot != 0 || !plan.TryVisibleY(x.ToScale(), out var range))
        {
            return false;
        }

        y = ViewRange.FromScale(range);
        return true;
    }

    internal (int Left, int Top, int Width, int Height) PanelRectangle(PanelId panel)
    {
        CheckPanel(panel);
        var g = _renderer.Plan!.Guides;
        return (g.Left, g.Top, g.Width, g.Height);
    }

    internal (ViewRange X, ViewRange Y) PixelToView(PanelId panel, Vector2 pixel)
    {
        CheckPanel(panel);
        var (x, y) = Shown();
        var (fx, fy) = Fractions(pixel);
        var vx = x.Lower + (fx * (x.Upper - x.Lower));
        var vy = y.Lower + (fy * (y.Upper - y.Lower));
        return (new ViewRange(x.Origin, vx, vx), new ViewRange(y.Origin, vy, vy));
    }

    internal void ZoomAt(PanelId panel, Vector2 pixel, double factor, ViewAxes axes, bool animate, bool grouped)
    {
        CheckPanel(panel);
        var (x, y) = Target();
        var (fx, fy) = Fractions(pixel);
        if (Moves(axes, ViewAxes.X, x: true))
        {
            x = Zoom(x, fx, factor);
        }

        if (Moves(axes, ViewAxes.Y, x: false))
        {
            y = Zoom(y, fy, factor);
        }

        Commit(panel, x, y, animate, grouped ? HistoryMode.Group : HistoryMode.Push);
    }

    internal void Pan(PanelId panel, Vector2 pixels, ViewAxes axes, bool grouped)
    {
        CheckPanel(panel);
        var (x, y) = Target();
        var g = _renderer.Plan!.Guides;
        var (alongX, alongY) = g.Flipped ? (pixels.Y / g.Height, -pixels.X / g.Width) : (-pixels.X / g.Width, pixels.Y / g.Height);
        if (Moves(axes, ViewAxes.X, x: true))
        {
            x = Shift(x, alongX * (x.Upper - x.Lower));
        }

        if (Moves(axes, ViewAxes.Y, x: false))
        {
            y = Shift(y, alongY * (y.Upper - y.Lower));
        }

        Commit(panel, x, y, animate: false, _inGesture ? HistoryMode.None : grouped ? HistoryMode.Group : HistoryMode.Push);
    }

    internal void ZoomToRect(PanelId panel, Vector2 corner, Vector2 opposite, ViewAxes axes, bool animate)
    {
        CheckPanel(panel);
        var (x, y) = Target();
        var g = _renderer.Plan!.Guides;
        var least = 4 * Scale;
        var (a, b) = (Fractions(corner), Fractions(opposite));
        var (wide, tall) = (Math.Abs(opposite.X - corner.X) >= least, Math.Abs(opposite.Y - corner.Y) >= least);
        var (xSide, ySide) = g.Flipped ? (tall, wide) : (wide, tall);
        if (xSide && Moves(axes, ViewAxes.X, x: true))
        {
            x = Part(x, Math.Clamp(Math.Min(a.X, b.X), 0, 1), Math.Clamp(Math.Max(a.X, b.X), 0, 1));
        }

        if (ySide && Moves(axes, ViewAxes.Y, x: false))
        {
            y = Part(y, Math.Clamp(Math.Min(a.Y, b.Y), 0, 1), Math.Clamp(Math.Max(a.Y, b.Y), 0, 1));
        }

        Commit(panel, x, y, animate, HistoryMode.Push);
    }

    internal void SetView(PanelId panel, in ScaleRange x, in ScaleRange y, bool animate)
    {
        CheckPanel(panel);
        var (fullX, fullY) = Full();
        if ((!MovesX() && !SameRange(x, fullX)) || (!MovesY() && !SameRange(y, fullY)))
        {
            throw new ArgumentException("The plot's band axis or polar coordinates keep the full view in row I1a (design §6.2).", nameof(x));
        }

        Commit(panel, x, y, animate, HistoryMode.Push);
    }

    internal void Reset(PanelId panel, bool animate)
    {
        CheckPanel(panel);
        var plan = _renderer.Plan!;
        if (plan.FullStale)
        {
            animate = false;
        }

        Commit(panel, ScaleRange.None, ScaleRange.None, animate, HistoryMode.Push);
    }

    internal bool MoveInHistory(int step)
    {
        CheckThread();
        var to = _position + step;
        if (_plot is null || !_drawn || to < 0 || to >= _history.Count)
        {
            return false;
        }

        _position = to;
        var (x, y) = _history[to];
        var reason = _reason;
        _reason = ViewChangeReason.History;
        try
        {
            Commit(First, x, y, animate: true, HistoryMode.None, constrain: false);
        }
        finally
        {
            _reason = reason;
        }

        return true;
    }

    internal void BeginGesture()
    {
        CheckThread();
        _inGesture = true;
        _lastWasGroup = false;
    }

    internal void EndGesture()
    {
        CheckThread();
        if (!_inGesture)
        {
            return;
        }

        _inGesture = false;
        Push(_targetX, _targetY);
    }

    private static bool SameRange(in ScaleRange a, in ScaleRange b) => (PlotPlan.IsNone(a) && PlotPlan.IsNone(b)) || a == b;

    private static ScaleRange Zoom(in ScaleRange range, double at, double factor)
    {
        var width = range.Upper - range.Lower;
        var lower = range.Lower + (at * width) - (at * width / factor);
        return new ScaleRange(range.Origin, lower, lower + (width / factor)).Anchor();
    }

    private static ScaleRange Shift(in ScaleRange range, double by) => new ScaleRange(range.Origin, range.Lower + by, range.Upper + by).Anchor();

    private static ScaleRange Part(in ScaleRange range, double from, double to)
    {
        var width = range.Upper - range.Lower;
        return new ScaleRange(range.Origin, range.Lower + (from * width), range.Lower + (to * width)).Anchor();
    }

    private static ScaleRange Lerp(in ScaleRange from, in ScaleRange to, double t)
    {
        // Both ends as differences from the destination's origin: the origins' difference is exact as a long.
        var shift = (double)(from.Origin - to.Origin);
        var lower = (from.Lower + shift) + ((to.Lower - (from.Lower + shift)) * t);
        var upper = (from.Upper + shift) + ((to.Upper - (from.Upper + shift)) * t);
        return new ScaleRange(to.Origin, lower, upper).Anchor();
    }

    private static HitAreaKind AxisKind(PlotGuides guides, TickAxis? axis) =>
        axis is null ? HitAreaKind.Figure
        : axis == guides.X ? HitAreaKind.AxisX
        : axis == guides.Y ? HitAreaKind.AxisY
        : axis == guides.X2 ? HitAreaKind.AxisX2
        : axis == guides.Y2 ? HitAreaKind.AxisY2
        : HitAreaKind.Figure;

    private static bool Matches(InputBinding binding, ModifierKeys modifiers, HitAreaKind area) =>
        binding.Modifiers == modifiers && (binding.Areas & area) != 0;

    private bool Pointer(in PointerEvent e)
    {
        if (_captured is { } captured)
        {
            if (e.Kind == PointerEventKind.Cancel)
            {
                CancelCaptured();
                return true;
            }

            _context.Hit = new HitArea(_pressArea.Kind, _pressArea.Panel);
            Release(captured.OnPointer(e, _context));
            if (e.Kind == PointerEventKind.Up)
            {
                _pressed = false;
            }

            return true;
        }

        switch (e.Kind)
        {
            case PointerEventKind.Down:
                (_press, _pressArea, _dragging) = (e, HitArea(e.Position), false);

                // A press is taken when a drag or a click of its button may follow on its place (design §6.1).
                _pressed = Bound(e.Button, e.Modifiers, _pressArea.Kind);
                return _pressed;
            case PointerEventKind.Move when _pressed && !_dragging:
                var threshold = Options.DragThreshold * Scale;
                if (Math.Abs(e.Position.X - _press.Position.X) <= threshold && Math.Abs(e.Position.Y - _press.Position.Y) <= threshold)
                {
                    return true;
                }

                _dragging = true;
                StartDrag(e);
                return true;
            case PointerEventKind.Up when _pressed:
                _pressed = false;
                if (!_dragging)
                {
                    return Click(e);
                }

                return true;
            case PointerEventKind.Cancel:
                var had = _pressed;
                _pressed = false;
                return had;
            default:
                return false;
        }
    }

    private void StartDrag(in PointerEvent move)
    {
        _context.Hit = _pressArea;
        var press = _press with { Kind = PointerEventKind.Down, Button = _press.Button };
        foreach (var binding in Bindings)
        {
            if (
                binding.Gesture.Kind != InputGestureKind.Drag
                || binding.Gesture.Button != _press.Button
                || !Matches(binding, _press.Modifiers, _pressArea.Kind)
                || binding.Tool is not { } tool
            )
            {
                continue;
            }

            var result = tool.OnPointer(press, _context);
            if (result == ToolResult.Ignored)
            {
                continue;
            }

            if (result == ToolResult.Capture)
            {
                _captured = tool;
                Release(tool.OnPointer(move, _context));
            }

            return;
        }
    }

    private bool Click(in PointerEvent up)
    {
        var distance = Options.DoubleClickDistance * Scale;
        var count =
            _hasLastClick
            && _lastClick.Button == up.Button
            && Time.GetElapsedTime(_lastClick.Timestamp, up.Timestamp) <= Options.DoubleClickTime
            && Math.Abs(up.Position.X - _lastClick.Position.X) <= distance
            && Math.Abs(up.Position.Y - _lastClick.Position.Y) <= distance
                ? 2
                : 1;
        (_lastClick, _hasLastClick) = (up, count == 1);
        _context.Hit = _pressArea;
        foreach (var binding in Bindings)
        {
            if (
                binding.Gesture.Kind != InputGestureKind.Click
                || binding.Gesture.Button != _press.Button
                || binding.Gesture.ClickCount != count
                || !Matches(binding, _press.Modifiers, _pressArea.Kind)
            )
            {
                continue;
            }

            if (binding.Tool is { } tool)
            {
                if (tool.OnPointer(up, _context) != ToolResult.Ignored)
                {
                    return true;
                }
            }
            else if (Run(binding.Command))
            {
                return true;
            }
        }

        return false;
    }

    private bool Bound(PointerButtons button, ModifierKeys modifiers, HitAreaKind area)
    {
        foreach (var binding in Bindings)
        {
            if (binding.Gesture.Kind is InputGestureKind.Drag or InputGestureKind.Click && binding.Gesture.Button == button && Matches(binding, modifiers, area))
            {
                return true;
            }
        }

        return false;
    }

    private void Release(ToolResult result)
    {
        if (result == ToolResult.Release)
        {
            _captured = null;
            RequestFrame();
        }
    }

    private void CancelCaptured()
    {
        if (_captured is { } tool)
        {
            _captured = null;
            tool.Cancel(_context);
            RequestFrame();
        }

        _pressed = false;
    }

    private bool Run(ViewCommand command)
    {
        if (command == ViewCommand.CancelTool)
        {
            var had = _captured is not null;
            CancelCaptured();
            return had;
        }

        if (_plot is null || !_drawn)
        {
            return false;
        }

        var g = _renderer.Plan!.Guides;
        var middle = new Vector2(g.Left + (g.Width / 2f), g.Top + (g.Height / 2f));
        var step = (float)Options.KeyPanFraction;
        switch (command)
        {
            case ViewCommand.Reset:
                Reset(First, animate: true);
                return true;
            case ViewCommand.Back:
                return MoveInHistory(-1);
            case ViewCommand.Forward:
                return MoveInHistory(+1);
            case ViewCommand.ZoomIn:
                ZoomAt(First, middle, Options.KeyZoomFactor, ViewAxes.Both, animate: true, grouped: false);
                return true;
            case ViewCommand.ZoomOut:
                ZoomAt(First, middle, 1 / Options.KeyZoomFactor, ViewAxes.Both, animate: true, grouped: false);
                return true;
            case ViewCommand.PanLeft or ViewCommand.PanRight or ViewCommand.PanUp or ViewCommand.PanDown:
                // A key moves the view, so the data move the other way, as when they are dragged.
                var (dx, dy) = command switch
                {
                    ViewCommand.PanLeft => (step * g.Width, 0f),
                    ViewCommand.PanRight => (-step * g.Width, 0f),
                    ViewCommand.PanUp => (0f, step * g.Height),
                    _ => (0f, -step * g.Height),
                };
                Pan(First, new Vector2(dx, dy), ViewAxes.Both, grouped: false);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Passes a proposal through the limits and the constraints, and takes it as the target (design §6.2).</summary>
    private void Commit(PanelId panel, ScaleRange x, ScaleRange y, bool animate, HistoryMode history, bool constrain = true)
    {
        if (constrain)
        {
            // The full view is no range to constrain: it is the spec's.
            var full = PlotPlan.IsNone(x) && PlotPlan.IsNone(y);
            if (!full)
            {
                var (fullX, fullY) = Full();
                (x, y) = (PlotPlan.IsNone(x) ? fullX : x, PlotPlan.IsNone(y) ? fullY : y);
                var g = _renderer.Plan!.Guides;
                _proposal.Start(panel, _reason, (g.Left, g.Top, g.Width, g.Height), x, y);
                foreach (var constraint in Constraints)
                {
                    if (!constraint.Constrain(_proposal))
                    {
                        return;
                    }
                }

                // The limits come last, so that no constraint takes a view past them.
                if (!ZoomLimits.Constrain(_proposal, g))
                {
                    return;
                }

                (x, y) = (_proposal.X, _proposal.Y);
            }
        }

        if (SameRange(x, _targetX) && SameRange(y, _targetY))
        {
            return;
        }

        var (shownX, shownY) = Shown();
        if (animate && Options.ZoomAnimation > TimeSpan.Zero)
        {
            (_fromX, _fromY, _animationStart, _animating) = (shownX, shownY, Time.GetTimestamp(), true);
        }
        else
        {
            _animating = false;
            (_shownX, _shownY) = (x, y);
        }

        (_targetX, _targetY) = (x, y);
        switch (history)
        {
            case HistoryMode.Push:
                Push(x, y);
                break;
            case HistoryMode.Group:
                var now = Time.GetTimestamp();
                if (_lastWasGroup && Time.GetElapsedTime(_lastGroupTimestamp, now) < Options.WheelGroupGap && _position == _history.Count - 1)
                {
                    _history[_position] = (x, y);
                }
                else
                {
                    Push(x, y);
                }

                (_lastGroupTimestamp, _lastWasGroup) = (now, true);
                break;
        }

        if (history != HistoryMode.Group)
        {
            _lastWasGroup = false;
        }

        Notify(_reason, x, y);
        RequestFrame();
    }

    private void Push(in ScaleRange x, in ScaleRange y)
    {
        if (_position < _history.Count && SameRange(_history[_position].X, x) && SameRange(_history[_position].Y, y))
        {
            return;
        }

        // Room for the whole depth at once, so that the history allocates once and not each time it doubles.
        if (_history.Capacity < Options.HistoryDepth + 1)
        {
            _history.Capacity = Options.HistoryDepth + 1;
        }

        _history.RemoveRange(_position + 1, _history.Count - _position - 1);
        _history.Add((x, y));
        while (_history.Count > Options.HistoryDepth)
        {
            _history.RemoveAt(0);
        }

        _position = _history.Count - 1;
    }

    private void Notify(ViewChangeReason reason, in ScaleRange x, in ScaleRange y)
    {
        if (ViewChanged is { } handler)
        {
            var (fullX, fullY) = PlotPlan.IsNone(x) || PlotPlan.IsNone(y) ? Full() : (x, y);
            handler(new ViewChange(First, reason, PlotPlan.IsNone(x) ? fullX : x, PlotPlan.IsNone(y) ? fullY : y));
        }
    }

    private void Animate()
    {
        if (!_animating)
        {
            return;
        }

        var t = Time.GetElapsedTime(_animationStart).TotalMilliseconds / Options.ZoomAnimation.TotalMilliseconds;
        if (t >= 1)
        {
            (_shownX, _shownY, _animating) = (_targetX, _targetY, false);
            return;
        }

        // A cubic ease out (design §6.2): fast at first, slowing to the end.
        var eased = 1 - Math.Pow(1 - Math.Max(t, 0), 3);
        var (fullX, fullY) = Full();
        var (toX, toY) = (PlotPlan.IsNone(_targetX) ? fullX : _targetX, PlotPlan.IsNone(_targetY) ? fullY : _targetY);
        var (fromX, fromY) = (PlotPlan.IsNone(_fromX) ? fullX : _fromX, PlotPlan.IsNone(_fromY) ? fullY : _fromY);
        (_shownX, _shownY) = (Lerp(fromX, toX, eased), Lerp(fromY, toY, eased));
    }

    private void ResetState()
    {
        (_targetX, _targetY, _shownX, _shownY, _animating) = (ScaleRange.None, ScaleRange.None, ScaleRange.None, ScaleRange.None, false);
        _history.Clear();
        _history.Add((ScaleRange.None, ScaleRange.None));
        (_position, _inGesture, _lastWasGroup) = (0, false, false);
    }

    private (ScaleRange X, ScaleRange Y) Full()
    {
        var plan = _renderer.Plan!;
        return (plan.FullX, plan.FullY);
    }

    private (ScaleRange X, ScaleRange Y) Target()
    {
        var (fullX, fullY) = Full();
        return (PlotPlan.IsNone(_targetX) ? fullX : _targetX, PlotPlan.IsNone(_targetY) ? fullY : _targetY);
    }

    private (ScaleRange X, ScaleRange Y) Shown()
    {
        var (fullX, fullY) = Full();
        return (PlotPlan.IsNone(_shownX) ? fullX : _shownX, PlotPlan.IsNone(_shownY) ? fullY : _shownY);
    }

    /// <summary>Returns a pixel's place along the x and the y scale over the panel, 0 at the low end and 1 at the high end.</summary>
    private (double X, double Y) Fractions(Vector2 pixel)
    {
        var g = _renderer.Plan!.Guides;
        var across = (pixel.X - g.Left) / (double)g.Width;
        var up = (g.Top + g.Height - pixel.Y) / (double)g.Height;
        return g.Flipped ? (up, across) : (across, up);
    }

    private bool Moves(ViewAxes axes, ViewAxes axis, bool x) => (axes & axis) != 0 && (x ? MovesX() : MovesY());

    private bool MovesX() => _plot is { Coord: CartesianCoordSpec } && _plot.ScaleX is not BandScaleSpec;

    private bool MovesY() => _plot is { Coord: CartesianCoordSpec };

    private void CheckPanel(PanelId panel)
    {
        CheckThread();
        if (_figure is not null)
        {
            throw new InvalidOperationException("The engine draws a figure; the views of a figure's plots are row I1b's (design §6.2).");
        }

        if (_plot is null || !_drawn || _renderer.Plan is null)
        {
            throw new InvalidOperationException("The engine has drawn no frame of its plot yet: a view needs the plot's layout.");
        }

        if (panel.Plot != 0 || panel.Panel < 0 || panel.Panel >= _renderer.Plan.PanelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(panel), panel, "The engine draws no such panel.");
        }
    }

    private void CheckThread()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Environment.CurrentManagedThreadId != _thread)
        {
            throw new InvalidOperationException("A PlotEngine is called from the thread that made it only (design §6.1).");
        }
    }

    private void Record<T>(in T e)
        where T : struct
    {
        if (_recording is not { } writer)
        {
            return;
        }

        var stamp = e switch
        {
            PointerEvent p => p.Timestamp,
            WheelEvent w => w.Timestamp,
            KeyEvent k => k.Timestamp,
            GestureEvent g => g.Timestamp,
            _ => Time.GetTimestamp(),
        };
        var elapsed = Time.GetElapsedTime(_lastRecorded, stamp);
        _lastRecorded = stamp;
        writer.WriteLine(InputLog.Line(elapsed, e));
    }

    private enum HistoryMode
    {
        None,
        Push,
        Group,
    }

    /// <summary>
    /// The limits every view keeps, which no application removes (design §6.2): an x range wide enough that the panel's
    /// boundaries increase strictly (§4.8), and a y range alike, both finite.
    /// </summary>
    private static class ZoomLimits
    {
        // 2^-48 of the panel's pixels: past the boundaries' rounding, which differences in [0, 1) bound by about 2^-52 each.
        private const double Least = 1.0 / (1L << 48);

        public static bool Constrain(ViewProposal proposal, PlotGuides guides)
        {
            var (x, y) = (proposal.X, proposal.Y);
            var (alongX, alongY) = guides.Flipped ? (guides.Height, guides.Width) : (guides.Width, guides.Height);
            return Wide(x, alongX) && Wide(y, alongY);
        }

        private static bool Wide(in ScaleRange range, int pixels) =>
            double.IsFinite(range.Lower) && double.IsFinite(range.Upper) && range.Upper - range.Lower > Least * Math.Max(pixels, 1) * Math.Max(1, Math.Abs(range.Upper));
    }
}

/// <summary>The lines of a recording of input (design §6.6): the header and one line an event.</summary>
internal static class InputLog
{
    public const string Magic = "hyperplot-input 1";

    public static string Header(int width, int height, float scale) =>
        string.Create(CultureInfo.InvariantCulture, $"{Magic} width={width} height={height} scale={scale:R}");

    public static string Line<T>(TimeSpan elapsed, in T e)
        where T : struct
    {
        var t = (long)Math.Round(elapsed.TotalMicroseconds);
        return e switch
        {
            PointerEvent p => string.Create(
                CultureInfo.InvariantCulture,
                $"t={t} pointer {p.Kind} x={p.Position.X:R} y={p.Position.Y:R} button={p.Button} buttons={p.Buttons} mods={p.Modifiers} kind={p.Device}"
            ),
            WheelEvent w => string.Create(CultureInfo.InvariantCulture, $"t={t} wheel x={w.Position.X:R} y={w.Position.Y:R} dx={w.DeltaX:R} dy={w.DeltaY:R} mods={w.Modifiers}"),
            KeyEvent k => string.Create(CultureInfo.InvariantCulture, $"t={t} key {k.Kind} key={k.Key} mods={k.Modifiers} repeat={k.IsRepeat}"),
            GestureEvent g => string.Create(
                CultureInfo.InvariantCulture,
                $"t={t} gesture {g.Kind} x={g.Position.X:R} y={g.Position.Y:R} scale={g.Scale:R} dx={g.Delta.X:R} dy={g.Delta.Y:R}"
            ),
            _ => throw new ArgumentException("Not an input event.", nameof(e)),
        };
    }
}
