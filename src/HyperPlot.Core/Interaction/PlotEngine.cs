using System.Globalization;
using System.Numerics;
using System.Text;
using HyperPlot.Rendering;

namespace HyperPlot.Interaction;

/// <summary>
/// The engine of an interactive plot or figure (design §6, §7.1, rows I1a and I1b): it keeps a spec, the views, and the
/// tools, takes the input a host passes, and draws only when something changed. A host draws a frame with
/// <see cref="RenderFrame"/> when <see cref="FrameRequested"/> comes, at its own time to draw.
/// </summary>
/// <remarks>
/// <para>
/// Call it from the thread that made it, the host's UI thread; a call from another thread throws
/// <see cref="InvalidOperationException"/>. Input is handled at once on that thread, and time comes from the engine's
/// <see cref="TimeProvider"/> and the events' timestamps only (design §6.6), so that a sequence of input and a sequence of
/// time give the same views, notices, and frames each time.
/// </para>
/// <para>
/// Each panel has a view of its x and y scales and of its secondary x and y scales (row I1b). A facet's panels share the
/// view of a shared axis; each plot of a figure has its own views and history. A pointer's press picks the plot a drag goes
/// to, a wheel goes to the plot under the cursor, and a key to the plot last operated on (design §6.2).
/// </para>
/// <para>
/// <see cref="RenderFrame"/> waits for the plot's indexes as <see cref="HeadlessRenderer"/> does; row W1 draws what is
/// there and tells the host instead. The size of the target and its scale are fixed (row W1 changes them).
/// </para>
/// </remarks>
public sealed partial class PlotEngine : IDisposable
{
    private readonly HeadlessRenderer _renderer;
    private readonly int _thread;
    private readonly ToolContext _context;
    private readonly ViewProposal _proposal = new();
    private readonly RenderList _overlay = new();
    private readonly char[] _tooltip = new char[HoverTool.TooltipLength];

    // The tooltip shaped outside the shaping cache: it is new in most frames of a hover (Issue #378).
    private readonly OwnedText _tooltipShaped = new();
    private readonly List<(float X, float Y, float Width, float Height)> _lassoFill = [];
    private readonly List<(PanelId Panel, Selection2D Selection, int Start, int Count)> _lassoKeys = [];
    private PlotState[] _states = [];
    private PanelDomains[][] _shownViews = [];
    private PlotSpec? _plot;
    private FigureSpec? _figure;
    private bool _drawn;
    private bool _frameRequested;
    private bool _inGesture;
    private PanelId _active;
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
    private bool _quietLinks;

    // The hover (design §6.5, row I2b): the tool that hovers, where, and the hits; a buffer for the next hits and the tooltip.
    private SeriesHit[] _hover = new SeriesHit[16];
    private SeriesHit[] _hits = new SeriesHit[16];
    private int _hoverCount;
    private HoverTool? _hoverTool;
    private Vector2 _hoverPixel;
    private bool _hovered;

    // The selections (design §6.4, row I3a): one a plot, and each lasso's fill as rects of the last layout, a panel's run.
    private Selection2D?[] _selections = [];

    // A number a plot that grows each time its selection changes, or a link stands it on more of its panels (row R3c).
    private long[] _selectionVersions = [];

    // The kept layer (design §5.4, row I2b): what the plot drew last, kept by the renderer, and what it was drawn from. The
    // selections' versions count only for the plots whose point layers highlight a selection in it (row R3c).
    private bool _kept;
    private object? _keptSpec;
    private float _keptScale;
    private long _keptVersions;
    private long _keptSelections;
    private long _drawnSelections;
    private PanelDomains[][] _keptViews = [];

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
        // The engine's frame does not wait for the indexes or the workers (invariant 7, row W1b1).
        _renderer = new HeadlessRenderer(backend, width, height, pool, scale) { Waits = false };
        Time = time ?? TimeProvider.System;
        _thread = Environment.CurrentManagedThreadId;
        _context = new ToolContext(this);
        Views = new ViewOperations(this);
        Links = new LinkGroups(this);
    }

    /// <summary>Occurs when the engine needs a frame: once until <see cref="RenderFrame"/> draws it.</summary>
    public event Action? FrameRequested;

    /// <summary>Occurs when a view changes (design §6.2): once a panel and change, with the view it goes to.</summary>
    public event ViewChangedHandler? ViewChanged;

    /// <summary>
    /// Occurs at the end of a <see cref="RenderFrame"/> whose frame is provisional when the one before was not, or the other
    /// way (design §4.8, invariant 4, row W1b1): once each time <see cref="IsProvisional"/> changes.
    /// </summary>
    public event Action? ProvisionalChanged;

    /// <summary>
    /// Occurs when the hover's hits change (design §6.5, row I2b): a move to another row or series, the pointer leaving the
    /// panel or the target, a tool taking the pointer, or a frame drawn again that finds other hits at the same pixel. The
    /// same hits at another distance do not make it occur.
    /// </summary>
    public event Action? HoverChanged;

    /// <summary>
    /// Occurs in a <see cref="RenderFrame"/> that finds a layer's work failed (design §4.8, row W1b1): once a failure. The
    /// frame does not throw it; the layer is not drawn until one of its columns' versions moves, and is then tried again.
    /// </summary>
    public event WorkFailedHandler? WorkFailed;

    /// <summary>
    /// Gets a value indicating whether the last frame was provisional (design §4.8, row W1b1): drawn before the indexes and
    /// the workers' results were all there, so that a coarser picture stood in for them. While it is, each frame asks for the
    /// next. A layer that failed does not make the frame provisional.
    /// </summary>
    public bool IsProvisional { get; private set; }

    /// <summary>
    /// Gets or sets a value indicating whether the frames wait for the indexes and the workers' results, as a headless render
    /// does (design §4.8). Tests only: the tests of the interaction draw what a frame that waited would, whatever the
    /// workers' speed.
    /// </summary>
    internal bool WaitsForIndexes
    {
        get => _renderer.Waits;
        set => _renderer.Waits = value;
    }

    /// <summary>Gets the operations on the views.</summary>
    public ViewOperations Views { get; }

    /// <summary>
    /// Gets the hits of the hover (design §6.5, row I2b): what <see cref="HitTest"/> answered at the pointer with the
    /// <see cref="HoverTool"/>'s settings, at the last move or the last frame drawn again; empty when no tool hovers or the
    /// pointer is outside a panel. Valid until the next input or frame; read it on the engine's thread. Allocates nothing.
    /// </summary>
    public ReadOnlySpan<SeriesHit> Hover => _hover.AsSpan(0, _hoverCount);

    /// <summary>
    /// Occurs when a plot's selection changes (design §6.4, row I3a): a new one, points added, cleared, or dropped for a spec
    /// whose mapping changed. The argument is the plot's number.
    /// </summary>
    public event Action<int>? SelectionChanged;

    /// <summary>Gets the bindings, in the order the engine tries them: the defaults to start with.</summary>
    public InputBindings Bindings { get; } = InputBindings.CreateDefault();

    /// <summary>Gets the application's constraints of the views, in the order the engine applies them (design §6.2).</summary>
    public List<IViewConstraint> Constraints { get; } = [];

    /// <summary>Gets the settings of the interaction.</summary>
    public InteractionOptions Options { get; } = new();

    /// <summary>Gets the time of the animations.</summary>
    public TimeProvider Time { get; }

    /// <summary>
    /// Gets the render target the engine draws to (design §7.1): a host copies it to its display after each
    /// <see cref="RenderFrame"/>. <see cref="Resize"/> replaces it when the size changes.
    /// </summary>
    public IRenderTarget Target => _renderer.Target;

    /// <summary>Gets the target's width in device pixels.</summary>
    public int Width => _renderer.Width;

    /// <summary>Gets the target's height in device pixels.</summary>
    public int Height => _renderer.Height;

    /// <summary>Gets the scale from device-independent to device pixels.</summary>
    public float Scale => _renderer.Scale;

    internal bool CanGoBack => Active() is { } state && state.Position > 0;

    internal bool CanGoForward => Active() is { } state && state.Position < state.Count - 1;

    internal Theme CurrentTheme => _plot?.Theme ?? _figure?.Theme ?? Theme.Light;

    /// <summary>Gets the plan of the plot drawn. Tests only.</summary>
    internal PlotPlan? Plan => _plot is null ? null : _renderer.Plan;

    /// <summary>Gets the plan of the figure drawn. Tests only.</summary>
    internal FigurePlan? Figure => _figure is null ? null : _renderer.Figure;

    /// <summary>Gets the tool that holds the pointer. Tests only.</summary>
    internal IPlotTool? Captured => _captured;

    /// <summary>Gets the number of entries of the history of the plot last operated on and the place in it. Tests only.</summary>
    internal (int Count, int Position) History => Active() is { } state ? (state.Count, state.Position) : (1, 0);

    /// <summary>Gets the panel last operated on: the one keys go to (row I1b). Tests only.</summary>
    internal PanelId ActivePanel => _active;

    /// <summary>
    /// Gets the number of plots drawn: 1 for a plot, a figure's plots when its cells are drawn, 0 before the first frame.
    /// </summary>
    private int PlotCount =>
        !_drawn ? 0
        : _plot is not null ? 1
        : _renderer.Figure?.Drawn ?? 0;

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
        if (_figure is not null || _states.Length != 1)
        {
            NewStates(1);
        }

        (_plot, _figure) = (plot, null);
        _kept = false;
        RequestFrame();
    }

    /// <summary>
    /// Sets a figure of several plots to draw (design §7.1, rows G6c and I1b): each plot keeps views and a history of its
    /// own. The figure is checked now (design §4.5); its plots are compiled and checked at the next frame. A figure of as many
    /// plots as the one before keeps their views where the plots' mappings stay, as <see cref="SetPlot"/> does.
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
        var count = FigurePlan.PlotCount(figure);
        if (_plot is not null || _states.Length != count)
        {
            NewStates(count);
        }

        (_plot, _figure) = (null, figure);
        _kept = false;
        RequestFrame();
    }

    /// <summary>
    /// Changes the target's size and the scale (design §7.1, row W1a): a host calls it when its element's size or DPI
    /// changes. The target is made again only for a new size (<see cref="Target"/> is then a new object); the plot is laid
    /// out again at the next frame, with its views, animations, and history kept, and its indexes kept. The same values do
    /// nothing; others ask for a frame. A text the new scale makes too large fails at the next <see cref="RenderFrame"/>,
    /// as <see cref="SetPlot"/> would.
    /// </summary>
    /// <param name="width">The target's width in device pixels, from 1 to 16,384.</param>
    /// <param name="height">The target's height in device pixels, from 1 to 16,384.</param>
    /// <param name="scale">The scale from device-independent to device pixels: finite and above 0.</param>
    /// <exception cref="ArgumentOutOfRangeException">A size is out of range, or the scale is not finite and above 0.</exception>
    /// <exception cref="InvalidOperationException">The call comes from another thread than the engine's.</exception>
    /// <exception cref="ObjectDisposedException">The engine was disposed.</exception>
    /// <exception cref="GpuDeviceLostException">The device was lost while the new target was made.</exception>
    public void Resize(int width, int height, float scale)
    {
        CheckThread();
        if (width == Width && height == Height && scale == Scale)
        {
            return;
        }

        _renderer.Resize(width, height, scale);
        _kept = false;
        RequestFrame();
    }

    /// <summary>
    /// Draws a frame (design §7.1): moves the animations to the engine's time, gives the views to the plans, draws, and draws
    /// the hover and what the tool that holds the pointer draws over it. It does not wait for the indexes or the workers
    /// (design §4.8, invariant 7): it draws what is there, and a layer's work that failed is reported through
    /// <see cref="WorkFailed"/>, not thrown. While an animation runs or the frame is provisional, it asks for the next frame.
    /// A frame that only the hover or a tool's overlay changes copies the plot it kept from the last frame and draws those
    /// over it (design §5.4, row I2b); a frame drawn again hits again at the hover's pixel.
    /// </summary>
    /// <exception cref="InvalidOperationException">The call comes from another thread, or the plot fails as <see cref="HeadlessRenderer.Render(PlotSpec)"/> says for a cause other than a layer's work, or a <see cref="TooltipFormatter"/> wrote a length outside 0 to its span's.</exception>
    /// <exception cref="ArgumentException">The plot does not check.</exception>
    /// <exception cref="GpuDeviceLostException">The device was lost.</exception>
    /// <exception cref="ObjectDisposedException">The engine was disposed.</exception>
    public void RenderFrame()
    {
        CheckThread();
        _frameRequested = false;
        if (_plot is null && _figure is null)
        {
            return;
        }

        var count = PlotCount;
        for (var k = 0; k < count; k++)
        {
            Animate(k);
        }

        using var frame = _renderer.BeginFrame();

        // The kept plot when nothing it was drawn from moved (design §5.4): the data layer is not recorded again.
        var full = !CanRestore() || !_renderer.TryRestore();
        if (full)
        {
            DrawPlot();
        }

        _overlay.Clear();
        DrawSelections(_overlay);
        DrawMeasurement(_overlay);
        DrawHover(_overlay);
        _captured?.Overlay(_overlay, _context);
        if (full)
        {
            // A frame with something over the plot keeps it for the frames of the mouse alone; a provisional one is drawn
            // again each frame anyway.
            _kept = false;
            if (_overlay.Commands.Length > 0 && !IsProvisional)
            {
                _renderer.Keep();
                KeepState();
            }
        }

        _renderer.DrawOver(_overlay);
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

    /// <summary>
    /// Returns an English summary of the current spec and view (design §7.1, requirement N-10, row W1c): the plot's kind
    /// (its layers' geoms), its title, each axis's title and the range of the current view in the format of its tick labels,
    /// and the series' labels and geoms, those not drawn marked; a figure's plots in order, and a facet's number of panels.
    /// A provisional frame's summary ends with "(provisional)". Made when called, off the frame path.
    /// </summary>
    /// <returns>The summary.</returns>
    /// <exception cref="InvalidOperationException">The engine has drawn no frame, or the call comes from another thread.</exception>
    /// <exception cref="ObjectDisposedException">The engine was disposed.</exception>
    public string Describe()
    {
        CheckThread();
        if (!_drawn)
        {
            throw new InvalidOperationException("The engine has drawn no frame of its plot yet: the summary needs the plot's layout.");
        }

        var text = new StringBuilder();
        if (_plot is not null)
        {
            Plan!.Describe(text, "Plot");
        }
        else
        {
            var plans = _renderer.Figure!.Plans;
            _ = text.Append("Figure");
            if (_figure!.Title is { Length: > 0 } title)
            {
                _ = text.Append(" \"").Append(title).Append('"');
            }

            _ = text.Append(CultureInfo.InvariantCulture, $" of {plans.Count} plots.");
            for (var i = 0; i < plans.Count; i++)
            {
                _ = text.Append(' ');
                plans[i].Describe(text, $"Plot {i + 1}");
            }
        }

        if (IsProvisional)
        {
            _ = text.Append(" (provisional)");
        }

        return text.ToString();
    }

    /// <summary>
    /// Answers a hit test at a pixel (design §6.3, requirement I-2, row I2a): in the panel under the pixel, each series' row
    /// nearest the cursor's x (<see cref="HitMode.X"/>), or its point nearest the cursor within the radius
    /// (<see cref="HitMode.XY"/>). The hits are written in the spec's layer order; outside a panel there are none. Waits for no
    /// worker: a series whose index is being built answers provisionally. Allocates nothing.
    /// </summary>
    /// <param name="pixel">The cursor, in device pixels.</param>
    /// <param name="radius">The radius of <see cref="HitMode.XY"/>, in device pixels; 0 or more.</param>
    /// <param name="mode">What each series looks for.</param>
    /// <param name="results">Where the hits go: as many as fit.</param>
    /// <returns>The number of hits found: more than <paramref name="results"/> holds when it was too short.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is negative, NaN, or infinite, or <paramref name="mode"/> is not a named value.</exception>
    /// <exception cref="InvalidOperationException">The engine has drawn no frame, or the call comes from another thread.</exception>
    /// <exception cref="ObjectDisposedException">The engine was disposed.</exception>
    public int HitTest(Vector2 pixel, float radius, HitMode mode, Span<SeriesHit> results)
    {
        CheckThread();
        if (PlotCount == 0)
        {
            throw new InvalidOperationException("The engine has drawn no frame of its plot yet: a hit test needs the plot's layout.");
        }

        if (!(radius >= 0) || float.IsInfinity(radius))
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "The radius is 0 or more pixels, and finite.");
        }

        if (mode is not (HitMode.X or HitMode.XY))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "The mode is a named HitMode value.");
        }

        var area = Hit(pixel, out _);
        return area.Kind == HitAreaKind.Panel ? PlanOf(area.Panel.Plot).HitTest(area.Panel.Plot, area.Panel.Panel, pixel, radius, mode, results) : 0;
    }

    /// <summary>
    /// Returns the range of the scale space of the density's bin a hit stands for (design §6.4 "密度のクリック", row R3c): false
    /// for a hit of a row, and when its plot, panel, or layer is not drawn any more.
    /// </summary>
    internal bool BinRange(in SeriesHit hit, out ViewRange x, out ViewRange y)
    {
        (x, y) = (default, default);
        return hit.Plot >= 0 && hit.Plot < PlotCount && PlanOf(hit.Plot).HitBinRange(hit, out x, out y);
    }

    /// <summary>Returns a plot's selection (design §6.4, row I3a), or null when it has none.</summary>
    /// <param name="plot">The plot: 0 for a plot alone, its number in a figure.</param>
    /// <returns>The selection.</returns>
    /// <exception cref="InvalidOperationException">The call comes from another thread than the engine's.</exception>
    public Selection? Selection(int plot)
    {
        CheckThread();
        return plot >= 0 && plot < _selections.Length ? _selections[plot] : null;
    }

    /// <summary>
    /// Sets a plot's selection from code (design §6.4, row I3a, requirement I-9): as the selection tool does, it replaces the
    /// plot's selection and tells <see cref="SelectionChanged"/>. An x range takes the panel's current y.
    /// </summary>
    /// <param name="selection">The selection: a <see cref="Selection2D"/> of a drawn panel that is not polar.</param>
    /// <exception cref="ArgumentNullException"><paramref name="selection"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The selection's panel is not drawn.</exception>
    /// <exception cref="ArgumentException">
    /// The selection is not a <see cref="Selection2D"/>, its panel is polar, or its content does not fit its kind: a range
    /// not finite or reversed, a lasso of fewer than 3 or more than 1,024 vertices or of shares not finite, points none.
    /// </exception>
    /// <exception cref="InvalidOperationException">The engine has drawn no frame, or the call comes from another thread.</exception>
    public void Select(Selection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        CheckPanel(selection.Panel);
        if (selection is not Selection2D flat)
        {
            throw new ArgumentException("A 2D panel takes a Selection2D.", nameof(selection));
        }

        if (PanelOf(selection.Panel).Polar is not null)
        {
            throw new ArgumentException("A polar panel takes no selection: its pixels do not run along its axes (design §6.4).", nameof(selection));
        }

        static bool Range(in ViewRange r) => double.IsFinite(r.Lower) && double.IsFinite(r.Upper) && r.Lower <= r.Upper;
        var good = flat.Kind switch
        {
            SelectionKind.Rectangle => Range(flat.X) && Range(flat.Y),
            SelectionKind.XRange => Range(flat.X),
            SelectionKind.Lasso => Range(flat.X) && Range(flat.Y) && flat.Polygon.Length is >= 3 and <= 1024 && AllFinite(flat.Polygon.Span),
            SelectionKind.Points => flat.Points.Length > 0,
            _ => false,
        };
        if (!good)
        {
            throw new ArgumentException($"The {flat.Kind} selection's content does not fit its kind (design §6.4).", nameof(selection));
        }

        if (flat.Kind == SelectionKind.XRange)
        {
            flat = flat.WithY(CurrentView(selection.Panel) is PanelView2D view ? view.Y : default);
        }

        SetSelection(selection.Panel.Plot, flat);

        static bool AllFinite(ReadOnlySpan<Vector2> points)
        {
            foreach (var point in points)
            {
                if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Returns the rows of a layer a plot's selection holds (design §6.4 "行の集合", row I3a, Issue #364 Decisions 4, 9, 10):
    /// for an x range over a layer whose x does not decrease, a run of rows found at once; for a rectangle or a lasso, a bit a
    /// row the workers set, made only now; for points, the rows they hit. A split layer's and a facet's rows are the plot's
    /// table's; a stat's and a stack's are their derived table's (<see cref="RowSelection.Derived"/>). A rule, a segment, a
    /// text, and a user's geom answer an empty set, as does a plot without a selection.
    /// </summary>
    /// <param name="plot">The plot: 0 for a plot alone, its number in a figure.</param>
    /// <param name="layer">The layer's position in the plot's spec.</param>
    /// <returns>
    /// The rows: at once for a run or points, else when the workers are done, which may complete on a worker's thread. The
    /// result does not change, so any thread may read it.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">The plot or the layer is not drawn.</exception>
    /// <exception cref="InvalidOperationException">The call comes from another thread than the engine's.</exception>
    public ValueTask<RowSelection> SelectedRowsAsync(int plot, int layer)
    {
        CheckThread();
        if (plot < 0 || plot >= PlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(plot), plot, "The plot is drawn.");
        }

        if (layer < 0 || layer >= SpecOf(plot).Layers.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(layer), layer, "The layer is in the plot's spec.");
        }

        if (_selections[plot] is not { } selection)
        {
            return ValueTask.FromResult(RowSelection.Empty);
        }

        // A link that also stands the selection on more of the plot's panels (a facet's) adds their rows (row I3b).
        var (plan, origin) = (PlanOf(plot), selection.Panel.Panel);
        var answer = plan.SelectRows(origin, layer, selection, _renderer.Pool);
        List<(Task<RowSelection> Rows, (ISourceRows Gather, int Key)? Map)>? parts = null;
        for (var p = 0; p < plan.PanelCount; p++)
        {
            if (p != origin && selection.Kind != SelectionKind.Points && Applied(plot, p))
            {
                parts ??= [(answer.AsTask(), null)];
                parts.Add((plan.SelectRows(p, layer, selection, _renderer.Pool).AsTask(), null));
            }
        }

        return parts is null ? answer : SelectionQuery.Union(parts, derived: false);
    }

    /// <summary>Clears every plot's selection (design §6.4, row I3a): tells <see cref="SelectionChanged"/> for each plot that had one.</summary>
    /// <exception cref="InvalidOperationException">The call comes from another thread than the engine's.</exception>
    public void ClearSelection()
    {
        CheckThread();
        _ = ClearSelections();
    }

    /// <summary>Returns a panel's current view: the one drawn, mid-animation included.</summary>
    /// <param name="panel">The panel.</param>
    /// <returns>The view, as a new object.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The panel is not drawn.</exception>
    /// <exception cref="InvalidOperationException">The engine has drawn no frame, or the call comes from another thread.</exception>
    public PanelView CurrentView(PanelId panel)
    {
        CheckPanel(panel);
        var shown = Resolve(panel.Plot, panel.Panel, _states[panel.Plot].Shown[panel.Panel]);
        return new PanelView2D(ViewRange.FromScale(shown.X), ViewRange.FromScale(shown.Y));
    }

    /// <summary>
    /// Returns what lies at a pixel (design §6.3): a panel, an axis band, the legend, the title, the figure's space, or
    /// nothing; with the plot and the panel (row I1b).
    /// </summary>
    /// <param name="pixel">The pixel, in device pixels.</param>
    /// <returns>The place; <see cref="HitAreaKind.None"/> before the first frame.</returns>
    /// <exception cref="InvalidOperationException">The call comes from another thread than the engine's.</exception>
    public HitArea HitArea(Vector2 pixel)
    {
        CheckThread();
        return Place(pixel, out _);
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
    /// <exception cref="InvalidOperationException">The engine has drawn no frame, or the call comes from another thread.</exception>
    public Vector2 ViewToPixel(PanelId panel, ViewRange x, ViewRange y)
    {
        CheckPanel(panel);
        var shown = Resolve(panel.Plot, panel.Panel, _states[panel.Plot].Shown[panel.Panel]);
        var fx = Fraction(x.ToScale(), shown.X);
        var fy = Fraction(y.ToScale(), shown.Y);
        var (spec, guide, flipped) = (SpecOf(panel.Plot), PanelOf(panel), PlanOf(panel.Plot).Guides.Flipped);
        if (Reversed(spec.ScaleX))
        {
            fx = 1 - fx;
        }

        if (Reversed(spec.ScaleY))
        {
            fy = 1 - fy;
        }

        if (guide.Polar is { } polar)
        {
            return polar.Map(fx * polar.AlongSize, fy * polar.AcrossSize);
        }

        var (across, up) = flipped ? (fy, fx) : (fx, fy);
        return new Vector2((float)(guide.Left + (across * guide.Width)), (float)(guide.Top + guide.Height - (up * guide.Height)));
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

    /// <summary>Takes a wheel's turn (design §6.1): it goes to the plot under the cursor.</summary>
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
            var area = Place(e.Position, out var inPlot);
            _context.Hit = area;
            if (inPlot)
            {
                _active = area.Panel;
            }

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

    /// <summary>Takes a key (design §6.1): the engine reads keys going down, for the plot last operated on.</summary>
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
            _context.Hit = new HitArea(HitAreaKind.None, _active);
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

    /// <summary>Takes a gesture (design §6.1, requirement I-8): it goes to the plot under its place.</summary>
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
            var area = Place(e.Position, out var inPlot);
            _context.Hit = area;
            if (inPlot)
            {
                _active = area.Panel;
            }

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

    /// <summary>
    /// Returns the y range the rows of a view of x show (row I1a): the work of <see cref="AutoRangeY"/>. A facet's panel keeps
    /// its y: its rows are a part of the columns, which the pyramids do not answer for (row I1b).
    /// </summary>
    internal bool TryVisibleY(PanelId panel, in ViewRange x, out ViewRange y)
    {
        y = default;
        if (panel.Plot < 0 || panel.Plot >= PlotCount || PlanOf(panel.Plot) is not { Facet: null } plan || panel.Panel != 0)
        {
            return false;
        }

        if (!plan.TryVisibleY(x.ToScale(), out var range))
        {
            return false;
        }

        y = ViewRange.FromScale(range);
        return true;
    }

    internal bool IsPolar(PanelId panel) => PanelOf(panel).Polar is not null;

    internal bool Flipped(PanelId panel) => PlanOf(panel.Plot).Guides.Flipped;

    internal (int Left, int Top, int Width, int Height) PanelRectangle(PanelId panel)
    {
        CheckPanel(panel);
        var guide = PanelOf(panel);
        return (guide.Left, guide.Top, guide.Width, guide.Height);
    }

    internal (ViewRange X, ViewRange Y) PixelToView(PanelId panel, Vector2 pixel)
    {
        CheckPanel(panel);
        var shown = Resolve(panel.Plot, panel.Panel, _states[panel.Plot].Shown[panel.Panel]);
        var (fx, fy) = Fractions(panel, pixel, secondary: false);
        if (PanelOf(panel).Polar is { } polar)
        {
            // The angle as a share of the turn from the start, along its scale's direction.
            var theta = Math.Atan2(pixel.X - polar.CenterX, polar.CenterY - pixel.Y);
            var share = (theta - polar.Theta(0)) / (polar.Theta(1) - polar.Theta(0)) / polar.Circumference;
            share -= Math.Floor(share);
            var spec = SpecOf(panel.Plot);
            if (polar.ThetaIsX)
            {
                fx = Reversed(spec.ScaleX) ? 1 - share : share;
            }
            else
            {
                fy = Reversed(spec.ScaleY) ? 1 - share : share;
            }
        }

        var vx = shown.X.Lower + (fx * (shown.X.Upper - shown.X.Lower));
        var vy = shown.Y.Lower + (fy * (shown.Y.Upper - shown.Y.Lower));
        return (new ViewRange(shown.X.Origin, vx, vx), new ViewRange(shown.Y.Origin, vy, vy));
    }

    internal void ZoomAt(PanelId panel, Vector2 pixel, double factor, ViewAxes axes, bool animate, bool grouped)
    {
        CheckPanel(panel);
        _active = panel;
        var moves = Movable(panel.Plot, axes);
        var next = Resolve(panel.Plot, panel.Panel, _states[panel.Plot].Target[panel.Panel]);
        var (fx, fy) = Fractions(panel, pixel, secondary: false);
        var (fx2, fy2) = Fractions(panel, pixel, secondary: true);
        next = new PanelDomains(
            (moves & ViewAxes.X) != 0 ? Zoom(next.X, fx, factor) : next.X,
            (moves & ViewAxes.Y) != 0 ? Zoom(next.Y, fy, factor) : next.Y,
            (moves & ViewAxes.X2) != 0 ? Zoom(next.X2, fx2, factor) : next.X2,
            (moves & ViewAxes.Y2) != 0 ? Zoom(next.Y2, fy2, factor) : next.Y2
        );
        Commit(panel, next, moves, animate, grouped ? HistoryMode.Group : HistoryMode.Push);
    }

    internal void Pan(PanelId panel, Vector2 pixels, ViewAxes axes, bool grouped)
    {
        CheckPanel(panel);
        _active = panel;
        var moves = Movable(panel.Plot, axes);
        var next = Resolve(panel.Plot, panel.Panel, _states[panel.Plot].Target[panel.Panel]);
        var (sx, sy) = PanShares(panel, pixels, secondary: false);
        var (sx2, sy2) = PanShares(panel, pixels, secondary: true);
        next = new PanelDomains(
            (moves & ViewAxes.X) != 0 ? Shift(next.X, sx) : next.X,
            (moves & ViewAxes.Y) != 0 ? Shift(next.Y, sy) : next.Y,
            (moves & ViewAxes.X2) != 0 ? Shift(next.X2, sx2) : next.X2,
            (moves & ViewAxes.Y2) != 0 ? Shift(next.Y2, sy2) : next.Y2
        );
        if (_inGesture)
        {
            _states[panel.Plot].Touched = true;
        }

        var history =
            _inGesture ? HistoryMode.None
            : grouped ? HistoryMode.Group
            : HistoryMode.Push;
        Commit(panel, next, moves, animate: false, history);
    }

    internal void ZoomToRect(PanelId panel, Vector2 corner, Vector2 opposite, ViewAxes axes, bool animate)
    {
        CheckPanel(panel);
        _active = panel;
        var guide = PanelOf(panel);
        var flipped = PlanOf(panel.Plot).Guides.Flipped;
        var least = 4 * Scale;
        var (wide, tall) = (Math.Abs(opposite.X - corner.X) >= least, Math.Abs(opposite.Y - corner.Y) >= least);
        var (xSide, ySide) = flipped ? (tall, wide) : (wide, tall);
        if (guide.Polar is { } polar)
        {
            // The radius's side: the rectangle's span of distances from the center, which a drag spans when it is wide or tall.
            (xSide, ySide) = polar.ThetaIsX ? (false, wide || tall) : (wide || tall, false);
        }

        var moves = Movable(panel.Plot, axes);
        var next = Resolve(panel.Plot, panel.Panel, _states[panel.Plot].Target[panel.Panel]);
        var (a, b) = (Fractions(panel, corner, secondary: false), Fractions(panel, opposite, secondary: false));
        var (a2, b2) = (Fractions(panel, corner, secondary: true), Fractions(panel, opposite, secondary: true));
        if (guide.Polar is { } radial)
        {
            var (near, far) = Distances(radial, corner, opposite);
            var spec = SpecOf(panel.Plot);
            var reverse = Reversed(radial.ThetaIsX ? spec.ScaleY : spec.ScaleX);
            (near, far) = reverse ? (1 - far, 1 - near) : (near, far);
            (a, b) = radial.ThetaIsX ? ((a.X, near), (b.X, far)) : ((near, a.Y), (far, b.Y));
        }

        next = new PanelDomains(
            xSide && (moves & ViewAxes.X) != 0 ? Part(next.X, a.X, b.X) : next.X,
            ySide && (moves & ViewAxes.Y) != 0 ? Part(next.Y, a.Y, b.Y) : next.Y,
            xSide && (moves & ViewAxes.X2) != 0 ? Part(next.X2, a2.X, b2.X) : next.X2,
            ySide && (moves & ViewAxes.Y2) != 0 ? Part(next.Y2, a2.Y, b2.Y) : next.Y2
        );
        Commit(panel, next, moves, animate, HistoryMode.Push);
    }

    internal void SetView(PanelId panel, in ScaleRange x, in ScaleRange y, bool animate)
    {
        CheckPanel(panel);
        _active = panel;
        var spec = SpecOf(panel.Plot);
        var full = Full(panel.Plot, panel.Panel);
        if (spec.Coord is PolarCoordSpec { Theta: var theta } && !SameRange(theta == PolarTheta.X ? x : y, theta == PolarTheta.X ? full.X : full.Y))
        {
            throw new ArgumentException("The angle of polar coordinates keeps its full turn (design §6.2, row I1b).", nameof(x));
        }

        // The secondary scales follow their primaries by the same shares, so that the lines over both keep their overlap.
        var now = Resolve(panel.Plot, panel.Panel, _states[panel.Plot].Target[panel.Panel]);
        var next = new PanelDomains(
            x,
            y,
            spec.ScaleX2 is null ? now.X2 : Follow(now.X, x, now.X2, Reversed(spec.ScaleX) == Reversed(spec.ScaleX2)),
            spec.ScaleY2 is null ? now.Y2 : Follow(now.Y, y, now.Y2, Reversed(spec.ScaleY) == Reversed(spec.ScaleY2))
        );
        Commit(panel, next, ViewAxes.All, animate, HistoryMode.Push);
    }

    internal void Reset(PanelId panel, bool animate)
    {
        CheckPanel(panel);
        _active = panel;
        var state = _states[panel.Plot];
        state.Target.CopyTo(state.Work, 0);
        state.Work[panel.Panel] = PanelDomains.None;
        Share(panel.Plot, panel.Panel, state.Work);
        Apply(panel.Plot, animate && !PlanOf(panel.Plot).FullStale, HistoryMode.Push);
    }

    internal void ResetAll(bool animate)
    {
        CheckThread();
        if (PlotCount == 0)
        {
            throw new InvalidOperationException("The engine has drawn no frame yet: a view needs the plot's layout.");
        }

        // Each plot goes to its own full view: a link copies none of them to the others (row I3b).
        _quietLinks = true;
        try
        {
            for (var k = 0; k < PlotCount; k++)
            {
                _states[k].Work.AsSpan().Fill(PanelDomains.None);
                Apply(k, animate && !PlanOf(k).FullStale, HistoryMode.Push);
            }
        }
        finally
        {
            _quietLinks = false;
        }
    }

    internal bool MoveInHistory(int step)
    {
        CheckThread();
        if (Active() is not { } state)
        {
            return false;
        }

        var to = state.Position + step;
        if (to < 0 || to >= state.Count)
        {
            return false;
        }

        state.Position = to;
        state.Entry(to).CopyTo(state.Work);
        var reason = _reason;
        _reason = ViewChangeReason.History;
        try
        {
            Apply(_active.Plot, animate: true, HistoryMode.None);
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
        foreach (var state in _states)
        {
            state.LastWasGroup = false;
        }
    }

    internal void EndGesture()
    {
        CheckThread();
        if (!_inGesture)
        {
            return;
        }

        _inGesture = false;
        foreach (var state in _states)
        {
            if (state.Touched)
            {
                state.Touched = false;
                Push(state);
            }
        }
    }

    private static bool SameRange(in ScaleRange a, in ScaleRange b) => (PlotPlan.IsNone(a) && PlotPlan.IsNone(b)) || a == b;

    private static bool Same(in PanelDomains a, in PanelDomains b) =>
        SameRange(a.X, b.X) && SameRange(a.Y, b.Y) && SameRange(a.X2, b.X2) && SameRange(a.Y2, b.Y2);

    private static bool SameViews(ReadOnlySpan<PanelDomains> a, ReadOnlySpan<PanelDomains> b)
    {
        for (var p = 0; p < a.Length; p++)
        {
            if (!Same(a[p], b[p]))
            {
                return false;
            }
        }

        return a.Length == b.Length;
    }

    private static bool Reversed(ScaleSpec? scale) => scale is ContinuousScaleSpec { Reverse: true };

    private static bool SameKept(PanelDomains[] shown, PanelDomains[] kept)
    {
        if (shown.Length != kept.Length)
        {
            return false;
        }

        for (var p = 0; p < shown.Length; p++)
        {
            if (!Same(shown[p], kept[p]) || shown[p].Viewed != kept[p].Viewed)
            {
                return false;
            }
        }

        return true;
    }

    private static ScaleRange Zoom(in ScaleRange range, double at, double factor)
    {
        if (PlotPlan.IsNone(range))
        {
            return range;
        }

        var width = range.Upper - range.Lower;
        var lower = range.Lower + (at * width) - (at * width / factor);
        return new ScaleRange(range.Origin, lower, lower + (width / factor)).Anchor();
    }

    private static ScaleRange Shift(in ScaleRange range, double share) =>
        PlotPlan.IsNone(range)
            ? range
            : new ScaleRange(
                range.Origin,
                range.Lower + (share * (range.Upper - range.Lower)),
                range.Upper + (share * (range.Upper - range.Lower))
            ).Anchor();

    private static ScaleRange Part(in ScaleRange range, double a, double b)
    {
        if (PlotPlan.IsNone(range))
        {
            return range;
        }

        var (from, to) = (Math.Clamp(Math.Min(a, b), 0, 1), Math.Clamp(Math.Max(a, b), 0, 1));
        var width = range.Upper - range.Lower;
        return new ScaleRange(range.Origin, range.Lower + (from * width), range.Lower + (to * width)).Anchor();
    }

    /// <summary>Returns a point's share of a range: 0 at its low end, 1 at its high end.</summary>
    private static double Fraction(in ScaleRange point, in ScaleRange range) =>
        ((double)(point.Origin - range.Origin) + point.Lower - range.Lower) / (range.Upper - range.Lower);

    /// <summary>
    /// Returns a secondary range moved as its primary moved, by the same shares of each (row I1b): the primary's new ends as
    /// shares of its old range, taken over the secondary's, mirrored when one of the two is reversed and the other not.
    /// </summary>
    private static ScaleRange Follow(in ScaleRange from, in ScaleRange to, in ScaleRange second, bool sameDirection)
    {
        if (PlotPlan.IsNone(from) || PlotPlan.IsNone(to) || PlotPlan.IsNone(second) || SameRange(from, to))
        {
            return second;
        }

        var (low, high) = (
            Fraction(new ScaleRange(to.Origin, to.Lower, to.Lower), from),
            Fraction(new ScaleRange(to.Origin, to.Upper, to.Upper), from)
        );
        (low, high) = sameDirection ? (low, high) : (1 - high, 1 - low);
        var width = second.Upper - second.Lower;
        return new ScaleRange(second.Origin, second.Lower + (low * width), second.Lower + (high * width)).Anchor();
    }

    private static ScaleRange Lerp(in ScaleRange from, in ScaleRange to, in ScaleRange full, double t)
    {
        if (PlotPlan.IsNone(from) && PlotPlan.IsNone(to))
        {
            return ScaleRange.None;
        }

        var (a, b) = (PlotPlan.IsNone(from) ? full : from, PlotPlan.IsNone(to) ? full : to);
        if (PlotPlan.IsNone(a) || PlotPlan.IsNone(b))
        {
            return to;
        }

        // Both ends as differences from the destination's origin: the origins' difference is exact as a long.
        var shift = (double)(a.Origin - b.Origin);
        var lower = (a.Lower + shift) + ((b.Lower - (a.Lower + shift)) * t);
        var upper = (a.Upper + shift) + ((b.Upper - (a.Upper + shift)) * t);
        return new ScaleRange(b.Origin, lower, upper).Anchor();
    }

    /// <summary>
    /// Returns the radius's shares at a rectangle's nearest and farthest points from a polar panel's center, 0 at the inner
    /// edge and 1 at the outer one.
    /// </summary>
    private static (double Near, double Far) Distances(in PolarTransform polar, Vector2 a, Vector2 b)
    {
        var (left, right, top, bottom) = (Math.Min(a.X, b.X), Math.Max(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y));
        var (cx, cy) = (polar.CenterX, polar.CenterY);
        var near = Distance(Math.Clamp(cx, left, right) - cx, Math.Clamp(cy, top, bottom) - cy);
        var far = Math.Max(
            Math.Max(Distance(left - cx, top - cy), Distance(right - cx, top - cy)),
            Math.Max(Distance(left - cx, bottom - cy), Distance(right - cx, bottom - cy))
        );
        var span = Math.Max(polar.Radius - polar.Inner, 1);
        return ((near - polar.Inner) / span, (far - polar.Inner) / span);

        static double Distance(double dx, double dy) => Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static HitAreaKind AxisKind(PanelGuide guide, TickAxis? axis) =>
        axis is null ? HitAreaKind.Figure
        : axis == guide.X ? HitAreaKind.AxisX
        : axis == guide.Y ? HitAreaKind.AxisY
        : axis == guide.X2 ? HitAreaKind.AxisX2
        : axis == guide.Y2 ? HitAreaKind.AxisY2
        : HitAreaKind.Figure;

    private static bool Matches(InputBinding binding, ModifierKeys modifiers, HitAreaKind area) =>
        binding.Modifiers == modifiers && (binding.Areas & area) != 0;

    /// <summary>
    /// Returns what lies at a pixel (design §6.3, rows I1a and I1b): the plot whose area holds it, then its panel, an axis band
    /// of a panel beside it, its title, or its legend. <paramref name="inPlot"/> tells whether the pixel lies in a plot's area.
    /// </summary>
    private HitArea Hit(Vector2 pixel, out bool inPlot)
    {
        inPlot = false;
        var count = PlotCount;
        var (x, y) = (pixel.X, pixel.Y);
        if (count == 0 || x < 0 || y < 0 || x >= Width || y >= Height)
        {
            return default;
        }

        for (var k = 0; k < count; k++)
        {
            var guides = PlanOf(k).Guides;
            var (left, top, width, height) = guides.Area;
            if (x < left || y < top || x >= left + width || y >= top + height)
            {
                continue;
            }

            inPlot = true;
            return guides.Empty ? new HitArea(HitAreaKind.Figure, new PanelId(k, 0)) : HitInPlot(k, guides, x, y);
        }

        return new HitArea(HitAreaKind.Figure, new PanelId(0, 0));
    }

    /// <summary>
    /// Returns what lies at a pixel for the bindings and <see cref="HitArea"/> (design §6.3, row I4a): what <see cref="Hit"/>
    /// returns, and over a draggable annotation of a panel <c>Panel | Annotation</c> with its number (Issue #382 Decision 17).
    /// </summary>
    private HitArea Place(Vector2 pixel, out bool inPlot)
    {
        var area = Hit(pixel, out inPlot);
        if (area.Kind != HitAreaKind.Panel)
        {
            return area;
        }

        var index = PlanOf(area.Panel.Plot).HitAnnotation(area.Panel.Panel, pixel, AnnotationRadius * Scale);
        return index < 0 ? area : new HitArea(HitAreaKind.Panel | HitAreaKind.Annotation, area.Panel, index);
    }

    private HitArea HitInPlot(int plot, PlotGuides guides, float x, float y)
    {
        var panels = guides.PanelCount;
        for (var p = 0; p < panels; p++)
        {
            var g = guides.PanelAt(p);
            if (x >= g.Left && x < g.Left + g.Width && y >= g.Top && y < g.Top + g.Height)
            {
                return new HitArea(HitAreaKind.Panel, new PanelId(plot, p));
            }
        }

        var (left, top, width, _) = guides.Area;
        var padding = MathF.Round(SpecOf(plot).Theme.Padding * Scale);
        if (guides.TitleBand > 0 && y < top + padding + guides.TitleBand)
        {
            return new HitArea(HitAreaKind.Title, new PanelId(plot, 0));
        }

        if (guides.LegendWidth > 0 && x >= left + width - padding - guides.LegendWidth)
        {
            return new HitArea(HitAreaKind.Legend, new PanelId(plot, 0));
        }

        // An axis band: beside the nearest panel along the pixel's row or column, on a side that panel draws an axis on.
        var (above, below, before, after) = (-1, -1, -1, -1);
        for (var p = 0; p < panels; p++)
        {
            var g = guides.PanelAt(p);
            if (x >= g.Left && x < g.Left + g.Width)
            {
                if (g.Top + g.Height <= y && (above < 0 || g.Top + g.Height > guides.PanelAt(above).Top + guides.PanelAt(above).Height))
                {
                    above = p;
                }

                if (g.Top > y && (below < 0 || g.Top < guides.PanelAt(below).Top))
                {
                    below = p;
                }
            }

            if (y >= g.Top && y < g.Top + g.Height)
            {
                if (g.Left + g.Width <= x && (before < 0 || g.Left + g.Width > guides.PanelAt(before).Left + guides.PanelAt(before).Width))
                {
                    before = p;
                }

                if (g.Left > x && (after < 0 || g.Left < guides.PanelAt(after).Left))
                {
                    after = p;
                }
            }
        }

        if (Side(above, PlotGuides.SideBottom) is { } bottom)
        {
            return bottom;
        }

        if (Side(below, PlotGuides.SideTop) is { } upper)
        {
            return upper;
        }

        if (Side(after, PlotGuides.SideLeft) is { } leftSide)
        {
            return leftSide;
        }

        if (Side(before, PlotGuides.SideRight) is { } rightSide)
        {
            return rightSide;
        }

        return new HitArea(HitAreaKind.Figure, new PanelId(plot, 0));

        // A panel's side, past its strip (row G6b): the axis it draws there, if any.
        HitArea? Side(int p, int side)
        {
            if (p < 0)
            {
                return null;
            }

            var g = guides.PanelAt(p);
            var distance = side switch
            {
                PlotGuides.SideBottom => y - (g.Top + g.Height),
                PlotGuides.SideTop => g.Top - y,
                PlotGuides.SideLeft => g.Left - x,
                _ => x - (g.Left + g.Width),
            };
            if (!g.Draws[side] || g.Sides[side].Axis is not { } axis || distance < g.Offsets[side])
            {
                return null;
            }

            var kind = AxisKind(g, axis);
            return kind == HitAreaKind.Figure ? null : new HitArea(kind, new PanelId(plot, p));
        }
    }

    /// <summary>
    /// Draws the plots over the views shown (design §7.1): records and draws them, then sizes the views to the panels drawn,
    /// tells their failures and whether the frame is provisional, and hits again at the hover's pixel (row I2b).
    /// </summary>
    private void DrawPlot()
    {
        KeepAnnotationShifts();
        _kept = false;
        ClearLassos();

        // The selections the plots' point layers highlight as they record (row R3c).
        for (var k = 0; k < PlotCount && k < _selections.Length; k++)
        {
            PlanOf(k).SetSelection(_selections[k], k < _applied.Length ? _applied[k] : []);
        }

        // The versions the recording draws: a selection the frame drops after it (a new mapping) is not in the kept layer.
        _drawnSelections = SelectionVersions();

        if (_plot is not null)
        {
            _renderer.Render(_plot, _states[0].Shown, null);
        }
        else
        {
            for (var k = 0; k < _states.Length; k++)
            {
                _shownViews[k] = _states[k].Shown;
            }

            _renderer.Render(_figure!, _shownViews, null);
        }

        _drawn = true;
        var (dropped, animating) = (false, false);
        var count = PlotCount;
        for (var k = 0; k < count; k++)
        {
            var (state, plan) = (_states[k], PlanOf(k));
            if (plan.ViewsDropped || state.Panels != plan.PanelCount)
            {
                // A new mapping, or other panels: the views over the old ones go back to the full view, and the history with
                // them (design §6.2).
                var told = plan.ViewsDropped;
                state.Size(plan.PanelCount);
                if (told)
                {
                    dropped = true;
                    for (var p = 0; p < state.Panels; p++)
                    {
                        Notify(k, p, ViewChangeReason.Spec);
                    }
                }
            }

            // A new mapping: the measurement's points and range meant the old one too (Issue #390 Decision 9).
            if (plan.MappingChanged && _measurement is { } measured && measured.Panel.Plot == k)
            {
                _ = ClearMeasured();
            }

            // A new mapping: the selection's predicate meant the old one (Issue #364 Decision 8). It is not copied: the
            // members keep theirs until the next change (Issue #373 Decision 15).
            if (plan.MappingChanged)
            {
                SetSelection(k, null, linked: true);
            }

            animating |= state.Animating;
        }

        CheckLinkLayout();

        if (dropped)
        {
            CancelCaptured();
        }

        // The plots' failures and whether the frame is provisional (row W1b1). An index, not foreach: no enumerator is boxed.
        var provisional = false;
        for (var k = 0; k < count; k++)
        {
            var plan = PlanOf(k);
            provisional |= plan.IsProvisional;
            var failures = plan.Failures;
            for (var f = 0; f < failures.Count; f++)
            {
                WorkFailed?.Invoke(new WorkFailure(k, failures[f].Layer, failures[f].Error));
            }
        }

        if (provisional != IsProvisional)
        {
            IsProvisional = provisional;
            ProvisionalChanged?.Invoke();
        }

        if (animating || provisional)
        {
            RequestFrame();
        }

        // A frame drawn again finds a range's statistics again: new data, or a provisional answer made final (Issue #390 Decision 9).
        MeasureAgain();

        // A frame drawn again (a zoom, new data, a provisional answer made final) hits again where the pointer is, so that no
        // older hit stays under it (design §6.5); a tool taken out of the bindings hovers no more.
        if (_hoverTool is { } hover)
        {
            if (Bound(hover))
            {
                HitAgain();
            }
            else
            {
                ClearHover(request: false);
            }
        }
    }

    /// <summary>
    /// Returns whether the kept plot is what the frame would draw (design §5.4, row I2b): kept from a frame that was not
    /// provisional, of the same spec, scale, views, and column versions. The renderer checks the size and the device.
    /// </summary>
    private bool CanRestore()
    {
        if (!_kept || IsProvisional || !ReferenceEquals(_keptSpec, (object?)_plot ?? _figure) || _keptScale != Scale)
        {
            return false;
        }

        var count = PlotCount;
        if (_keptViews.Length != count)
        {
            return false;
        }

        var versions = 0L;
        for (var k = 0; k < count; k++)
        {
            if (!SameKept(_states[k].Shown, _keptViews[k]))
            {
                return false;
            }

            versions += PlanOf(k).ColumnVersions();
        }

        return versions == _keptVersions && SelectionVersions() == _keptSelections;
    }

    /// <summary>
    /// Returns the sum of the selections' versions of the plots whose point layers highlight a selection in the kept layer
    /// (design §5.4, row R3c, Issue #471 Decision 6): a new selection draws such a plot again. Allocates nothing.
    /// </summary>
    private long SelectionVersions()
    {
        var sum = 0L;
        for (var k = 0; k < PlotCount && k < _selectionVersions.Length; k++)
        {
            if (PlanOf(k).HighlightsSelection)
            {
                sum += _selectionVersions[k];
            }
        }

        return sum;
    }

    /// <summary>Keeps what the kept plot was drawn from. Allocates only for a new number of plots or panels.</summary>
    private void KeepState()
    {
        var count = PlotCount;
        if (_keptViews.Length != count)
        {
            _keptViews = new PanelDomains[count][];
        }

        var versions = 0L;
        for (var k = 0; k < count; k++)
        {
            var shown = _states[k].Shown;
            if (_keptViews[k] is not { } views || views.Length != shown.Length)
            {
                _keptViews[k] = views = new PanelDomains[shown.Length];
            }

            shown.CopyTo(views, 0);
            versions += PlanOf(k).ColumnVersions();
        }

        (_kept, _keptSpec, _keptScale, _keptVersions) = (true, (object?)_plot ?? _figure, Scale, versions);
        _keptSelections = _drawnSelections;
    }

    /// <summary>
    /// Sets a plot's selection, tells it, and asks for a frame (row I3a); a selection made here, not a link's copy or a drop,
    /// is then copied to the panels its selection groups join (row I3b).
    /// </summary>
    internal void SetSelection(int plot, Selection2D? selection, bool linked = false)
    {
        if (plot < 0 || plot >= _selections.Length || ReferenceEquals(_selections[plot], selection))
        {
            return;
        }

        _selections[plot] = selection;
        _selectionVersions[plot]++;
        ClearApplied(plot);
        ClearLassos();
        RequestFrame();
        if (linked || selection is null)
        {
            SelectionChanged?.Invoke(plot);
            return;
        }

        QueueSelection(plot);
        var outer = !_draining;
        _draining = true;
        try
        {
            SelectionChanged?.Invoke(plot);
            if (outer)
            {
                Drain();
            }
        }
        finally
        {
            if (outer)
            {
                _draining = false;
            }
        }
    }

    /// <summary>Forgets the lassos' fills: a selection or the layout changed.</summary>
    private void ClearLassos()
    {
        _lassoKeys.Clear();
        _lassoFill.Clear();
    }

    /// <summary>Clears every plot's selection; returns whether one had any.</summary>
    private bool ClearSelections()
    {
        var had = false;
        for (var k = 0; k < _selections.Length; k++)
        {
            if (_selections[k] is not null)
            {
                had = true;
                SetSelection(k, null);
            }
        }

        return had;
    }

    /// <summary>Hovers at a pixel with a tool's settings (design §6.5, row I2b): called by the tool on a move.</summary>
    internal void HoverAt(HoverTool tool, Vector2 pixel)
    {
        _hovered = true;
        (_hoverTool, _hoverPixel) = (tool, pixel);
        HitAgain();

        // The crosshair follows the pointer even over the same hits.
        RequestFrame();
    }

    /// <summary>Takes a move with no button and no tool holding the pointer (row I2b): the bindings of a move, and the hover.</summary>
    private bool Move(in PointerEvent e)
    {
        _hovered = false;
        var used = false;
        if (e.Buttons == PointerButtons.None)
        {
            var area = Place(e.Position, out _);
            _context.Hit = area;
            foreach (var binding in Bindings)
            {
                if (binding.Gesture.Kind == InputGestureKind.Move && Matches(binding, e.Modifiers, area.Kind) && binding.Tool is { } tool)
                {
                    if (tool.OnPointer(e, _context) != ToolResult.Ignored)
                    {
                        used = true;
                        break;
                    }
                }
            }
        }

        // No hover tool took the move (outside its places, other modifiers, a button held): nothing is hovered.
        if (!_hovered)
        {
            ClearHover(request: true);
        }

        return used;
    }

    /// <summary>Hits again at the hover's pixel with its tool's settings, and keeps the hits (row I2b).</summary>
    private void HitAgain()
    {
        var count = 0;
        if (_hoverTool is { } tool && PlotCount > 0 && Hit(_hoverPixel, out _) is { Kind: HitAreaKind.Panel } area)
        {
            var (plan, radius) = (PlanOf(area.Panel.Plot), tool.Radius * Scale);
            count = plan.HitTest(area.Panel.Plot, area.Panel.Panel, _hoverPixel, radius, tool.Mode, _hits);
            if (count > _hits.Length)
            {
                // More series than ever before: the buffer grows once, and the hits are asked again.
                _hits = new SeriesHit[count];
                count = plan.HitTest(area.Panel.Plot, area.Panel.Panel, _hoverPixel, radius, tool.Mode, _hits);
            }
        }

        SetHover(_hits.AsSpan(0, count));
    }

    /// <summary>Takes the hover's hits, and tells when they are other hits than the ones before: not only other distances.</summary>
    private void SetHover(ReadOnlySpan<SeriesHit> hits)
    {
        var same = hits.Length == _hoverCount;
        for (var i = 0; same && i < hits.Length; i++)
        {
            same = hits[i] with { Distance = 0 } == _hover[i] with { Distance = 0 };
        }

        if (_hover.Length < hits.Length)
        {
            _hover = new SeriesHit[hits.Length];
        }

        hits.CopyTo(_hover);
        _hoverCount = hits.Length;
        if (!same)
        {
            HoverChanged?.Invoke();
        }
    }

    /// <summary>Ends the hover (row I2b): the pointer left, another tool took it, or no hover tool reads it.</summary>
    private void ClearHover(bool request)
    {
        if (_hoverTool is null && _hoverCount == 0)
        {
            return;
        }

        _hoverTool = null;
        if (request)
        {
            RequestFrame();
        }

        SetHover([]);
    }

    /// <summary>
    /// Records the hover over the plot (design §6.5, row I2b): the crosshair through the pointer, a disc at each hit in its
    /// series' color, and the tooltip at the pointer's lower right, on the other side where it would leave the panel.
    /// Allocates nothing once the list's buffers and the tooltip's shaped text have held the longest tooltip.
    /// </summary>
    private void DrawHover(RenderList list)
    {
        if (_hoverTool is not { } tool || PlotCount == 0 || Hit(_hoverPixel, out _) is not { Kind: HitAreaKind.Panel } area)
        {
            return;
        }

        var (guide, theme, plan) = (PanelOf(area.Panel), SpecOf(area.Panel.Plot).Theme, PlanOf(area.Panel.Plot));
        var (left, top, width, height) = (guide.Left, guide.Top, guide.Width, guide.Height);
        var line = MathF.Max(1, MathF.Round(Scale));
        var foreground = theme.Foreground;
        var faint = foreground with { A = (byte)Math.Round(foreground.A * 0.5) };
        var (x, y) = (MathF.Floor(_hoverPixel.X), MathF.Floor(_hoverPixel.Y));
        if (tool.Crosshair is HoverCrosshair.Vertical or HoverCrosshair.Both)
        {
            list.DrawRect(x, top, line, height, faint);
        }

        if (tool.Crosshair is HoverCrosshair.Horizontal or HoverCrosshair.Both)
        {
            list.DrawRect(left, y, width, line, faint);
        }

        var hits = Hover;
        if (tool.Markers)
        {
            // A disc of 3.5 DIPs in the series' color, in a ring of the panel's background, so that it shows on its line.
            var (radius, ring) = (3.5f * Scale, 4.5f * Scale);
            ReadOnlySpan<SectorTrapezoid> outer = [new SectorTrapezoid(0, MathF.Tau, ring, ring, 0, 0)];
            ReadOnlySpan<SectorTrapezoid> inner = [new SectorTrapezoid(0, MathF.Tau, radius, radius, 0, 0)];
            for (var i = 0; i < hits.Length; i++)
            {
                // A density's bin is marked by its frame, not a disc: no point lies at its center (row R3c, Issue #471 Decision 10).
                if (plan.HitBin(hits[i], out var bin))
                {
                    var color = plan.HitColor(hits[i]) with { A = 255 };
                    list.DrawRect(bin.Left, bin.Top, bin.Width, line, color);
                    list.DrawRect(bin.Left, bin.Top + bin.Height - line, bin.Width, line, color);
                    list.DrawRect(bin.Left, bin.Top, line, bin.Height, color);
                    list.DrawRect(bin.Left + bin.Width - line, bin.Top, line, bin.Height, color);
                    continue;
                }

                if (plan.HitPixel(hits[i], out var at) && at.X >= left && at.X <= left + width && at.Y >= top && at.Y <= top + height)
                {
                    list.DrawSectors(outer, at, theme.PanelBackground);
                    list.DrawSectors(inner, at, plan.HitColor(hits[i]));
                }
            }
        }

        if (tool.Tooltip && hits.Length > 0)
        {
            DrawTooltip(list, tool, plan, theme, (left, top, width, height), line);
        }

        DrawLinkedCursor(list, tool, area.Panel);
    }

    /// <summary>
    /// Records each plot's selection over the plot (design §6.4, row I3a, Issue #364 Decision 5): its range, or its lasso's
    /// polygon, in 15 % of the foreground; the lines of its panel within its x range again, twice as wide; and a marker at each
    /// of a selection's points. A link that stands it on more of the plot's panels draws it there too (row I3b). Allocates
    /// nothing once warm: a lasso's fill is made again only when a selection or the layout changes.
    /// </summary>
    private void DrawSelections(RenderList list)
    {
        var count = PlotCount;
        for (var k = 0; k < count && k < _selections.Length; k++)
        {
            var panels = PlanOf(k).PanelCount;
            if (_selections[k] is not { } selection || selection.Panel.Panel >= panels)
            {
                continue;
            }

            DrawSelection(list, k, selection, selection.Panel);
            for (var p = 0; p < panels && selection.Kind != SelectionKind.Points; p++)
            {
                if (p != selection.Panel.Panel && Applied(k, p))
                {
                    DrawSelection(list, k, selection, new PanelId(k, p));
                }
            }
        }
    }

    /// <summary>Records a plot's selection on one of its panels: the one it was made on, or one a link stands it on.</summary>
    private void DrawSelection(RenderList list, int k, Selection2D selection, PanelId panel)
    {
        var (guide, theme, plan) = (PanelOf(panel), SpecOf(k).Theme, PlanOf(k));
        var (left, top, width, height) = (guide.Left, guide.Top, guide.Width, guide.Height);
        var faint = theme.Foreground with { A = (byte)Math.Round(theme.Foreground.A * 0.15) };
        if (selection.Kind == SelectionKind.Points)
        {
            foreach (var point in selection.Points.Span)
            {
                if (point.Panel == panel.Panel && plan.HitPixel(point, out var at))
                {
                    Marker(list, at, plan.HitColor(point), theme);
                }
            }

            return;
        }

        // The x range's span along, and its frame clamped to the panel; an x range runs the panel across.
        var flipped = plan.Guides.Flipped;
        var low = ViewToPixel(panel, selection.X with { Upper = selection.X.Lower }, selection.Y with { Upper = selection.Y.Lower });
        var high = ViewToPixel(panel, selection.X with { Lower = selection.X.Upper }, selection.Y with { Lower = selection.Y.Upper });
        var (x0, x1) = (Math.Clamp(MathF.Min(low.X, high.X), left, left + width), Math.Clamp(MathF.Max(low.X, high.X), left, left + width));
        var (y0, y1) = (Math.Clamp(MathF.Min(low.Y, high.Y), top, top + height), Math.Clamp(MathF.Max(low.Y, high.Y), top, top + height));
        if (selection.Kind == SelectionKind.XRange)
        {
            (x0, x1, y0, y1) = flipped ? ((float)left, (float)(left + width), y0, y1) : (x0, x1, (float)top, (float)(top + height));
        }

        if (selection.Kind == SelectionKind.Lasso)
        {
            FillLasso(list, panel, selection, faint, (left, top, width, height));
        }
        else
        {
            list.DrawRect(x0, y0, x1 - x0, y1 - y0, faint);
        }

        var (alongFrom, alongTo) = flipped ? (y0, y1) : (x0, x1);
        var (clipLeft, clipTop, clipWidth, clipHeight) = flipped
            ? (left, (int)MathF.Floor(alongFrom), width, (int)MathF.Ceiling(alongTo - MathF.Floor(alongFrom)))
            : ((int)MathF.Floor(alongFrom), top, (int)MathF.Ceiling(alongTo - MathF.Floor(alongFrom)), height);
        plan.Highlight(panel.Panel, list, clipLeft, clipTop, clipWidth, clipHeight);
    }

    /// <summary>
    /// Records a lasso's polygon on a panel filled by the even-odd rule, a rect a pixel row a run, from rects made again when a
    /// selection or the layout changes (the frames of the mouse alone reuse them).
    /// </summary>
    private void FillLasso(RenderList list, PanelId on, Selection2D selection, Color color, (int Left, int Top, int Width, int Height) panel)
    {
        var (start, count) = (-1, 0);
        foreach (var key in _lassoKeys)
        {
            if (key.Panel == on && ReferenceEquals(key.Selection, selection))
            {
                (start, count) = (key.Start, key.Count);
                break;
            }
        }

        if (start < 0)
        {
            start = _lassoFill.Count;
            var shares = selection.Polygon.Span;
            Span<Vector2> pixels = shares.Length <= 1024 ? stackalloc Vector2[shares.Length] : new Vector2[shares.Length];
            var (top, bottom) = (float.PositiveInfinity, float.NegativeInfinity);
            for (var i = 0; i < shares.Length; i++)
            {
                var x = selection.X.Lower + (shares[i].X * (selection.X.Upper - selection.X.Lower));
                var y = selection.Y.Lower + (shares[i].Y * (selection.Y.Upper - selection.Y.Lower));
                pixels[i] = ViewToPixel(on, new ViewRange(selection.X.Origin, x, x), new ViewRange(selection.Y.Origin, y, y));
                (top, bottom) = (MathF.Min(top, pixels[i].Y), MathF.Max(bottom, pixels[i].Y));
            }

            Span<float> crossings = stackalloc float[1025];
            for (var row = MathF.Max(MathF.Floor(top), panel.Top); row < MathF.Min(MathF.Ceiling(bottom), panel.Top + panel.Height); row++)
            {
                var (y, n) = (row + 0.5f, 0);
                for (int i = 0, j = pixels.Length - 1; i < pixels.Length; j = i++)
                {
                    var (a, b) = (pixels[i], pixels[j]);
                    if ((a.Y > y) != (b.Y > y) && n < crossings.Length)
                    {
                        crossings[n++] = a.X + ((y - a.Y) * (b.X - a.X) / (b.Y - a.Y));
                    }
                }

                crossings[..n].Sort();
                for (var c = 0; c + 1 < n; c += 2)
                {
                    var (from, to) = (
                        Math.Clamp(crossings[c], panel.Left, panel.Left + panel.Width),
                        Math.Clamp(crossings[c + 1], panel.Left, panel.Left + panel.Width)
                    );
                    if (to > from)
                    {
                        _lassoFill.Add((from, row, to - from, 1));
                    }
                }
            }

            count = _lassoFill.Count - start;
            _lassoKeys.Add((on, selection, start, count));
        }

        for (var i = start; i < start + count; i++)
        {
            var (x, y, width, height) = _lassoFill[i];
            list.DrawRect(x, y, width, height, color);
        }
    }

    /// <summary>Records a marker: a disc of 3.5 DIPs in a color, in a ring of the panel's background (rows I2b and I3a).</summary>
    private void Marker(RenderList list, Vector2 at, Color color, Theme theme)
    {
        var (radius, ring) = (3.5f * Scale, 4.5f * Scale);
        ReadOnlySpan<SectorTrapezoid> outer = [new SectorTrapezoid(0, MathF.Tau, ring, ring, 0, 0)];
        ReadOnlySpan<SectorTrapezoid> inner = [new SectorTrapezoid(0, MathF.Tau, radius, radius, 0, 0)];
        list.DrawSectors(outer, at, theme.PanelBackground);
        list.DrawSectors(inner, at, color);
    }

    /// <summary>Records the tooltip's box and lines (Issue #347 Decision 4): the default lines or the tool's formatter's.</summary>
    private void DrawTooltip(
        RenderList list,
        HoverTool tool,
        PlotPlan plan,
        Theme theme,
        (int Left, int Top, int Width, int Height) panel,
        float line
    )
    {
        var length = tool.TooltipFormat is null ? plan.WriteTooltip(Hover, _tooltip) : 0;
        if (tool.TooltipFormat is { } format)
        {
            if (!format(Hover, _tooltip, out length))
            {
                return;
            }

            if (length < 0 || length > _tooltip.Length)
            {
                throw new InvalidOperationException($"The TooltipFormat wrote {length} characters: a length from 0 to {_tooltip.Length}.");
            }
        }

        if (length == 0)
        {
            return;
        }

        DrawBox(list, _tooltip.AsSpan(0, length), _tooltipShaped, _hoverPixel, theme, panel, line);
    }

    /// <summary>
    /// Records a box of text at an anchor (rows I2b and I4b): at its lower right, on the other side where it would leave the
    /// panel; the theme's background with a border of 25 % of the foreground. The text is shaped into <paramref name="owned"/>,
    /// not into the shaping cache (Issue #378): a box's text is new in most frames, and in the cache each would put out a text
    /// the plot draws.
    /// </summary>
    private void DrawBox(
        RenderList list,
        ReadOnlySpan<char> text,
        OwnedText owned,
        Vector2 anchor,
        Theme theme,
        (int Left, int Top, int Width, int Height) panel,
        float line
    )
    {
        var style = new TextStyle(theme.Foreground, theme.TickLabelSize * Scale, theme.FontFamily, Vertical: TextBaseline.Top, Locale: theme.Locale);
        var shaped = owned.Shape(_renderer.Text, text, style);
        var size = shaped.Metrics;
        var (pad, offset) = (MathF.Round(4 * Scale), MathF.Round(12 * Scale));
        var (w, h) = (MathF.Ceiling(size.Width) + (2 * pad), MathF.Ceiling(size.Height) + (2 * pad));

        // At the anchor's lower right; across a side of the panel it would pass, on the other side of the anchor.
        var bx = anchor.X + offset + w > panel.Left + panel.Width ? anchor.X - offset - w : anchor.X + offset;
        var by = anchor.Y + offset + h > panel.Top + panel.Height ? anchor.Y - offset - h : anchor.Y + offset;
        (bx, by) = (MathF.Round(bx), MathF.Round(by));
        var border = theme.Foreground with { A = (byte)Math.Round(theme.Foreground.A * 0.25) };
        list.DrawRect(bx, by, w, h, theme.Background);
        list.DrawRect(bx, by, w, line, border);
        list.DrawRect(bx, by + h - line, w, line, border);
        list.DrawRect(bx, by, line, h, border);
        list.DrawRect(bx + w - line, by, line, h, border);
        list.DrawText(text, new Vector2(bx + pad, by + pad), style, shaped);
    }

    /// <summary>Returns whether a hover tool is still bound to a move.</summary>
    private bool Bound(HoverTool tool)
    {
        foreach (var binding in Bindings)
        {
            if (binding.Gesture.Kind == InputGestureKind.Move && binding.Tool == tool)
            {
                return true;
            }
        }

        return false;
    }

    private bool Pointer(in PointerEvent e)
    {
        // The pointer left the target: no tool reads it, and the hover ends (design §7.2, row I2b).
        if (e.Kind == PointerEventKind.Leave)
        {
            ClearHover(request: true);
            return false;
        }

        if (_captured is { } captured)
        {
            if (e.Kind == PointerEventKind.Cancel)
            {
                CancelCaptured();
                return true;
            }

            // The plot and the place of the press, wherever the pointer goes (design §6.2, row I1b).
            _context.Hit = _pressArea;
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
                (_press, _pressArea, _dragging) = (e, Place(e.Position, out var inPlot), false);
                if (inPlot)
                {
                    _active = _pressArea.Panel;
                }

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
            case PointerEventKind.Move when _pressed:
                return true;
            case PointerEventKind.Move:
                return Move(e);
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
                // The hover ends while a tool holds the pointer, so that no crosshair stays over the data it moves (row I2b).
                ClearHover(request: true);
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
            if (
                binding.Gesture.Kind is InputGestureKind.Drag or InputGestureKind.Click
                && binding.Gesture.Button == button
                && Matches(binding, modifiers, area)
            )
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

        // Escape's second entry: with no tool to cancel, the selections go (design §6.5, row I3a).
        if (command == ViewCommand.ClearSelection)
        {
            return ClearSelections();
        }

        // Escape's third entry (row I4b): with no selection to clear, the measurement goes.
        if (command == ViewCommand.ClearMeasurement)
        {
            return ClearMeasured();
        }

        // The plot and the panel last operated on (design §6.2, row I1b).
        var panel = _active;
        if (panel.Plot >= PlotCount || panel.Panel >= _states[panel.Plot].Panels)
        {
            return false;
        }

        var g = PanelOf(panel);
        var middle = new Vector2(g.Left + (g.Width / 2f), g.Top + (g.Height / 2f));
        var step = (float)Options.KeyPanFraction;
        switch (command)
        {
            case ViewCommand.Reset:
                Reset(panel, animate: true);
                return true;
            case ViewCommand.Back:
                return MoveInHistory(-1);
            case ViewCommand.Forward:
                return MoveInHistory(+1);
            case ViewCommand.ZoomIn:
                ZoomAt(panel, middle, Options.KeyZoomFactor, ViewAxes.All, animate: true, grouped: false);
                return true;
            case ViewCommand.ZoomOut:
                ZoomAt(panel, middle, 1 / Options.KeyZoomFactor, ViewAxes.All, animate: true, grouped: false);
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
                Pan(panel, new Vector2(dx, dy), ViewAxes.All, grouped: false);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Passes a proposal for a panel through the constraints and the limits, gives the shared axes to the panels that share
    /// them, and takes it as the target (design §6.2, row I1b). <paramref name="next"/> holds every axis of the panel, those
    /// that do not move at their target's value.
    /// </summary>
    private void Commit(PanelId panel, PanelDomains next, ViewAxes moves, bool animate, HistoryMode history)
    {
        var (plot, p) = (panel.Plot, panel.Panel);
        var state = _states[plot];
        var spec = SpecOf(plot);
        var guide = PanelOf(panel);
        var now = Resolve(plot, p, state.Target[p]);
        _proposal.Start(panel, _reason, (guide.Left, guide.Top, guide.Width, guide.Height), next.X, next.Y);
        foreach (var constraint in Constraints)
        {
            if (!constraint.Constrain(_proposal))
            {
                return;
            }
        }

        // A secondary scale that moved with its primary follows what the constraints did to the primary.
        var (x, y) = (_proposal.X, _proposal.Y);
        var (x2, y2) = (next.X2, next.Y2);
        if ((moves & ViewAxes.X2) != 0 && (moves & ViewAxes.X) != 0)
        {
            x2 = Follow(next.X, x, x2, Reversed(spec.ScaleX) == Reversed(spec.ScaleX2));
        }

        if ((moves & ViewAxes.Y2) != 0 && (moves & ViewAxes.Y) != 0)
        {
            y2 = Follow(next.Y, y, y2, Reversed(spec.ScaleY) == Reversed(spec.ScaleY2));
        }

        // The limits come last, so that no constraint takes a view past them.
        var flipped = PlanOf(plot).Guides.Flipped;
        var (alongX, alongY) = flipped ? (guide.Height, guide.Width) : (guide.Width, guide.Height);
        if (!ZoomLimits.Wide(x, alongX) || !ZoomLimits.Wide(y, alongY) || !ZoomLimits.Wide(x2, alongX) || !ZoomLimits.Wide(y2, alongY))
        {
            return;
        }

        // An axis the proposal leaves where it was keeps its target: a full view stays the spec's, which follows the data.
        var current = state.Target[p];
        var view = new PanelDomains(
            SameRange(x, now.X) ? current.X : x,
            SameRange(y, now.Y) ? current.Y : y,
            SameRange(x2, now.X2) ? current.X2 : x2,
            SameRange(y2, now.Y2) ? current.Y2 : y2
        );
        state.Target.CopyTo(state.Work, 0);
        state.Work[p] = view;
        Share(plot, p, state.Work);
        Apply(plot, animate, history);
    }

    /// <summary>Gives a panel's shared axes to every panel of a facet that shares them (design §6.2, row I1b).</summary>
    private void Share(int plot, int panel, PanelDomains[] views)
    {
        if (views.Length < 2 || PlanOf(plot).Facet is not { } facet)
        {
            return;
        }

        var spec = SpecOf(plot);
        var (shareX, shareY) = (!facet.Arrangement.FreeXOf(spec.ScaleX), !facet.Arrangement.FreeY);
        var source = views[panel];
        for (var q = 0; q < views.Length; q++)
        {
            var view = views[q];
            views[q] = new PanelDomains(
                shareX ? source.X : view.X,
                shareY ? source.Y : view.Y,
                shareX ? source.X2 : view.X2,
                shareY ? source.Y2 : view.Y2
            );
        }
    }

    /// <summary>
    /// Takes a plot's work views as its target (design §6.2): animates to them or shows them, writes the history, and tells
    /// each panel whose view changed.
    /// </summary>
    private void Apply(int plot, bool animate, HistoryMode history)
    {
        var state = _states[plot];
        var changed = false;
        for (var p = 0; p < state.Panels && !changed; p++)
        {
            changed = !Same(state.Work[p], state.Target[p]);
        }

        if (!changed)
        {
            return;
        }

        // The view drawn now, the animation moved to this time: a change that comes while one runs starts from where the
        // view is now, not where the last frame drew it. Otherwise a wheel turned at each frame would restart the animation at
        // its start each time, and the view would not move until the turns stop.
        Animate(plot);
        if (animate && Options.ZoomAnimation > TimeSpan.Zero)
        {
            state.Shown.CopyTo(state.From, 0);
            (state.AnimationStart, state.Animating) = (Time.GetTimestamp(), true);
        }
        else
        {
            state.Animating = false;
            state.Work.CopyTo(state.Shown, 0);
        }

        // The old target stays in the work views to tell which panels changed.
        (state.Target, state.Work) = (state.Work, state.Target);
        switch (history)
        {
            case HistoryMode.Push:
                Push(state);
                break;
            case HistoryMode.Group:
                var now = Time.GetTimestamp();
                if (
                    state.LastWasGroup
                    && Time.GetElapsedTime(state.LastGroupTimestamp, now) < Options.WheelGroupGap
                    && state.Position == state.Count - 1
                )
                {
                    state.Target.CopyTo(state.Entry(state.Position));
                }
                else
                {
                    Push(state);
                }

                (state.LastGroupTimestamp, state.LastWasGroup) = (now, true);
                break;
        }

        if (history != HistoryMode.Group)
        {
            state.LastWasGroup = false;
        }

        RequestFrame();

        // A link's commit tells its changes after it, in the order of the panels (row I3b, Issue #373 Decision 10).
        if (_linkCommit)
        {
            for (var p = 0; p < state.Panels; p++)
            {
                if (!Same(state.Work[p], state.Target[p]))
                {
                    _linkNotices.Add(new PanelId(plot, p));
                }
            }

            return;
        }

        // The panels that moved wait to be copied before anyone is told, so that a handler's change is copied after them.
        for (var p = 0; p < state.Panels && !_quietLinks; p++)
        {
            var (before, after) = (state.Work[p], state.Target[p]);

            // A link copies the primary scales (Issue #373 Decision 2): a change of a secondary scale alone copies nothing.
            var axes = (SameRange(before.X, after.X) ? ViewAxes.None : ViewAxes.X) | (SameRange(before.Y, after.Y) ? ViewAxes.None : ViewAxes.Y);
            QueueView(plot, p, axes, animate);
        }

        var outer = !_draining;
        _draining = true;
        try
        {
            for (var p = 0; p < state.Panels; p++)
            {
                if (!Same(state.Work[p], state.Target[p]))
                {
                    Notify(plot, p, _reason);
                }
            }

            if (outer)
            {
                Drain();
            }
        }
        finally
        {
            if (outer)
            {
                _draining = false;
            }
        }
    }

    private void Push(PlotState state)
    {
        if (SameViews(state.Entry(state.Position), state.Target))
        {
            return;
        }

        state.Push(state.Target, Options.HistoryDepth);
    }

    private void Notify(int plot, int panel, ViewChangeReason reason)
    {
        if (ViewChanged is { } handler)
        {
            var view = Resolve(plot, panel, _states[plot].Target[panel]);
            handler(new ViewChange(new PanelId(plot, panel), reason, view.X, view.Y));
        }
    }

    private void Animate(int plot)
    {
        var state = _states[plot];
        if (!state.Animating)
        {
            return;
        }

        var t = Time.GetElapsedTime(state.AnimationStart).TotalMilliseconds / Options.ZoomAnimation.TotalMilliseconds;
        if (t >= 1)
        {
            state.Target.CopyTo(state.Shown, 0);
            state.Animating = false;
            return;
        }

        // A cubic ease out (design §6.2): fast at first, slowing to the end.
        var eased = 1 - Math.Pow(1 - Math.Max(t, 0), 3);
        for (var p = 0; p < state.Panels; p++)
        {
            var (from, to, full) = (state.From[p], state.Target[p], Full(plot, p));
            state.Shown[p] = new PanelDomains(
                Lerp(from.X, to.X, full.X, eased),
                Lerp(from.Y, to.Y, full.Y, eased),
                Lerp(from.X2, to.X2, full.X2, eased),
                Lerp(from.Y2, to.Y2, full.Y2, eased)
            );
        }
    }

    private void NewStates(int count)
    {
        _states = new PlotState[count];
        for (var k = 0; k < count; k++)
        {
            _states[k] = new PlotState();
        }

        // Another number of plots: the selections of the plots before go (row I3a), and the links' slots are built again.
        _ = ClearSelections();
        _selections = new Selection2D?[count];
        _selectionVersions = new long[count];
        _applied = [];
        _linksDirty = true;

        _shownViews = new PanelDomains[count][];
        (_active, _inGesture) = (default, false);
        (_shifts, _sources, _dragPlot) = (new AnnotationShift[count][], new AnnotationSpec[count][], -1);
    }

    private PlotState? Active() => _active.Plot < PlotCount && _states[_active.Plot].Panels > 0 ? _states[_active.Plot] : null;

    private PlotPlan PlanOf(int plot) => _plot is not null ? _renderer.Plan! : _renderer.Figure!.Plans[plot];

    private PlotSpec SpecOf(int plot) => _plot ?? FigurePlan.PlotAt(_figure!, plot);

    private PanelGuide PanelOf(PanelId panel) => PlanOf(panel.Plot).Guides.PanelAt(panel.Panel);

    private PanelDomains Full(int plot, int panel)
    {
        var full = PlanOf(plot).Full;
        return panel < full.Length ? full[panel] : PanelDomains.None;
    }

    /// <summary>Returns a view with each axis that has none at the full view's range.</summary>
    private PanelDomains Resolve(int plot, int panel, in PanelDomains view)
    {
        var full = Full(plot, panel);
        return new PanelDomains(
            PlotPlan.IsNone(view.X) ? full.X : view.X,
            PlotPlan.IsNone(view.Y) ? full.Y : view.Y,
            PlotPlan.IsNone(view.X2) ? full.X2 : view.X2,
            PlotPlan.IsNone(view.Y2) ? full.Y2 : view.Y2
        );
    }

    /// <summary>
    /// Returns the axes an operation may move on a plot (design §6.2, row I1b): of those asked, a cartesian plot's x, y, and
    /// secondary scales, a polar plot's radius alone. A second axis with no secondary scale is a transform's, which follows
    /// its primary: asking for it moves the primary.
    /// </summary>
    private ViewAxes Movable(int plot, ViewAxes axes)
    {
        var spec = SpecOf(plot);
        if (spec.ScaleX2 is null && (axes & ViewAxes.X2) != 0)
        {
            axes = (axes & ~ViewAxes.X2) | ViewAxes.X;
        }

        if (spec.ScaleY2 is null && (axes & ViewAxes.Y2) != 0)
        {
            axes = (axes & ~ViewAxes.Y2) | ViewAxes.Y;
        }

        return spec.Coord is PolarCoordSpec { Theta: var theta } ? axes & (theta == PolarTheta.X ? ViewAxes.Y : ViewAxes.X) : axes;
    }

    /// <summary>
    /// Returns a pixel's share along the x and the y scale over a panel, 0 at the low end and 1 at the high end, each scale's
    /// direction taken (row I1b): of the primary scales, or of the secondary ones. In polar coordinates the radius's share is
    /// the distance from the center's, 0 at the inner edge.
    /// </summary>
    private (double X, double Y) Fractions(PanelId panel, Vector2 pixel, bool secondary)
    {
        var (spec, g) = (SpecOf(panel.Plot), PanelOf(panel));
        var (scaleX, scaleY) = secondary ? ((ScaleSpec?)spec.ScaleX2, (ScaleSpec?)spec.ScaleY2) : (spec.ScaleX, spec.ScaleY);
        double x,
            y;
        if (g.Polar is { } polar)
        {
            var (dx, dy) = (pixel.X - polar.CenterX, pixel.Y - polar.CenterY);
            var radius = (Math.Sqrt((dx * dx) + (dy * dy)) - polar.Inner) / Math.Max(polar.Radius - polar.Inner, 1);
            (x, y) = polar.ThetaIsX ? (0.0, radius) : (radius, 0.0);
        }
        else
        {
            var across = (pixel.X - g.Left) / (double)g.Width;
            var up = (g.Top + g.Height - pixel.Y) / (double)g.Height;
            (x, y) = PlanOf(panel.Plot).Guides.Flipped ? (up, across) : (across, up);
        }

        return (Reversed(scaleX) ? 1 - x : x, Reversed(scaleY) ? 1 - y : y);
    }

    /// <summary>
    /// Returns the shares of the x and the y range a move of pixels shifts the views by, so that the data follow the pointer
    /// (row I1b): each scale's direction taken. In polar coordinates a move up moves the radius's data outward.
    /// </summary>
    private (double X, double Y) PanShares(PanelId panel, Vector2 pixels, bool secondary)
    {
        var (spec, g) = (SpecOf(panel.Plot), PanelOf(panel));
        var (scaleX, scaleY) = secondary ? ((ScaleSpec?)spec.ScaleX2, (ScaleSpec?)spec.ScaleY2) : (spec.ScaleX, spec.ScaleY);
        double x,
            y;
        if (g.Polar is { } polar)
        {
            var outward = -pixels.Y / (double)Math.Max(polar.Radius - polar.Inner, 1);
            (x, y) = polar.ThetaIsX ? (0.0, outward) : (outward, 0.0);
        }
        else
        {
            var (across, up) = (pixels.X / (double)g.Width, -pixels.Y / (double)g.Height);
            (x, y) = PlanOf(panel.Plot).Guides.Flipped ? (up, across) : (across, up);
        }

        return (Reversed(scaleX) ? x : -x, Reversed(scaleY) ? y : -y);
    }

    private void CheckPanel(PanelId panel)
    {
        CheckThread();
        if (PlotCount == 0)
        {
            throw new InvalidOperationException("The engine has drawn no frame of its plot yet: a view needs the plot's layout.");
        }

        if (panel.Plot < 0 || panel.Plot >= PlotCount || panel.Panel < 0 || panel.Panel >= _states[panel.Plot].Panels)
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
    /// The views of a plot (design §6.2, row I1b): the target, what is drawn, where an animation started, each a panel's
    /// domains, and the history, whose entries hold every panel's views.
    /// </summary>
    private sealed class PlotState
    {
        private PanelDomains[] _history = [];

        public PanelDomains[] Target { get; set; } = [];

        public PanelDomains[] Shown { get; private set; } = [];

        public PanelDomains[] From { get; private set; } = [];

        /// <summary>Gets or sets the views a change builds before it is taken; then the target before it.</summary>
        public PanelDomains[] Work { get; set; } = [];

        public int Panels { get; private set; }

        public int Count { get; private set; } = 1;

        public int Position { get; set; }

        public long AnimationStart { get; set; }

        public bool Animating { get; set; }

        public long LastGroupTimestamp { get; set; } = long.MinValue;

        public bool LastWasGroup { get; set; }

        /// <summary>Gets or sets a value indicating whether a gesture moved the plot's views.</summary>
        public bool Touched { get; set; }

        /// <summary>Starts over with a number of panels: no view, a history of the full view alone.</summary>
        public void Size(int panels)
        {
            if (panels != Panels)
            {
                (Target, Shown, From, Work) = (
                    new PanelDomains[panels],
                    new PanelDomains[panels],
                    new PanelDomains[panels],
                    new PanelDomains[panels]
                );
                _history = new PanelDomains[panels];
                Panels = panels;
            }

            Target.AsSpan().Fill(PanelDomains.None);
            Shown.AsSpan().Fill(PanelDomains.None);
            _history.AsSpan().Fill(PanelDomains.None);
            (Count, Position, Animating, LastWasGroup, Touched) = (1, 0, false, false, false);
        }

        public Span<PanelDomains> Entry(int index) => _history.AsSpan(index * Panels, Panels);

        /// <summary>
        /// Adds an entry after the current one, dropping those after it and the oldest past the depth. The entries' room is
        /// taken once, for the whole depth, at the first push, so that the history does not allocate each time it grows.
        /// </summary>
        public void Push(ReadOnlySpan<PanelDomains> views, int depth)
        {
            var room = (depth + 1) * Panels;
            if (_history.Length < room)
            {
                var grown = new PanelDomains[room];
                _history.AsSpan(0, Count * Panels).CopyTo(grown);
                _history = grown;
            }

            var count = Position + 2;
            views.CopyTo(Entry(count - 1));
            while (count > Math.Max(depth, 1))
            {
                _history.AsSpan(Panels, (count - 1) * Panels).CopyTo(_history);
                count--;
            }

            (Count, Position) = (count, count - 1);
        }
    }

    /// <summary>
    /// The limits every view keeps, which no application removes (design §6.2): a range wide enough that the panel's
    /// boundaries increase strictly (§4.8), with both ends finite.
    /// </summary>
    private static class ZoomLimits
    {
        // 2^-48 of the panel's pixels: past the boundaries' rounding, which differences in [0, 1) bound by about 2^-52 each.
        private const double Least = 1.0 / (1L << 48);

        /// <summary>Returns whether a range keeps the limits; an axis with no range does.</summary>
        public static bool Wide(in ScaleRange range, int pixels) =>
            PlotPlan.IsNone(range)
            || (
                double.IsFinite(range.Lower)
                && double.IsFinite(range.Upper)
                && range.Upper - range.Lower > Least * Math.Max(pixels, 1) * Math.Max(1, Math.Abs(range.Upper))
            );
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
            WheelEvent w => string.Create(
                CultureInfo.InvariantCulture,
                $"t={t} wheel x={w.Position.X:R} y={w.Position.Y:R} dx={w.DeltaX:R} dy={w.DeltaY:R} mods={w.Modifiers}"
            ),
            KeyEvent k => string.Create(CultureInfo.InvariantCulture, $"t={t} key {k.Kind} key={k.Key} mods={k.Modifiers} repeat={k.IsRepeat}"),
            GestureEvent g => string.Create(
                CultureInfo.InvariantCulture,
                $"t={t} gesture {g.Kind} x={g.Position.X:R} y={g.Position.Y:R} scale={g.Scale:R} dx={g.Delta.X:R} dy={g.Delta.Y:R}"
            ),
            _ => throw new ArgumentException("Not an input event.", nameof(e)),
        };
    }
}
