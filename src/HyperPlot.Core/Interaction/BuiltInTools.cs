using System.Numerics;
using HyperPlot.Rendering;

namespace HyperPlot.Interaction;

/// <summary>
/// Pans by a drag (design §6.5): on a panel every scale, on an axis that axis's alone; and by a two-finger scroll. A drag is
/// one entry of the history; the turns of a scroll group as a wheel's do.
/// </summary>
public sealed class PanTool : IPlotTool
{
    private PanelId _panel;
    private ViewAxes _axes;
    private Vector2 _last;

    /// <inheritdoc />
    public ToolResult OnPointer(in PointerEvent e, ToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        switch (e.Kind)
        {
            case PointerEventKind.Down:
                var axes = Axes(context.Hit.Kind);
                if (axes == ViewAxes.None)
                {
                    return ToolResult.Ignored;
                }

                (_panel, _axes, _last) = (context.Hit.Panel, axes, e.Position);
                context.Views.BeginGesture();
                return ToolResult.Capture;
            case PointerEventKind.Move:
                context.Views.Pan(_panel, e.Position - _last, _axes);
                _last = e.Position;
                return ToolResult.Handled;
            case PointerEventKind.Up:
                context.Views.Pan(_panel, e.Position - _last, _axes);
                context.Views.EndGesture();
                return ToolResult.Release;
            default:
                return ToolResult.Handled;
        }
    }

    /// <inheritdoc />
    public ToolResult OnGesture(in GestureEvent e, ToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (e.Kind != GestureKind.Scroll || (context.Hit.Kind & HitAreaKind.Panel) == 0)
        {
            return ToolResult.Ignored;
        }

        // The content follows the fingers, as a drag; the turns group in the history (design §6.2).
        context.Views.Pan(context.Hit.Panel, e.Delta, ViewAxes.All, group: true);
        return ToolResult.Handled;
    }

    /// <inheritdoc />
    public void Cancel(ToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Views.EndGesture();
    }

    /// <summary>
    /// Returns the axes a drag on a place moves: both on a panel, one on its axis, none elsewhere. A place over an annotation is
    /// its panel too (row I4a).
    /// </summary>
    internal static ViewAxes Axes(HitAreaKind kind) =>
        (kind & ~HitAreaKind.Annotation) switch
        {
            HitAreaKind.Panel => ViewAxes.All,
            HitAreaKind.AxisX => ViewAxes.X,
            HitAreaKind.AxisY => ViewAxes.Y,
            HitAreaKind.AxisX2 => ViewAxes.X2,
            HitAreaKind.AxisY2 => ViewAxes.Y2,
            _ => ViewAxes.None,
        };
}

/// <summary>
/// Zooms to the rectangle a drag spans (design §6.5): it draws the rectangle over the plot while the drag goes on, and on
/// release shows it, an axis whose side is under 4 DIPs keeping its range.
/// </summary>
public sealed class RectangleZoomTool : IPlotTool
{
    private PanelId _panel;
    private Vector2 _start;
    private Vector2 _end;

    /// <inheritdoc />
    public ToolResult OnPointer(in PointerEvent e, ToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        switch (e.Kind)
        {
            case PointerEventKind.Down:
                if ((context.Hit.Kind & HitAreaKind.Panel) == 0)
                {
                    return ToolResult.Ignored;
                }

                (_panel, _start, _end) = (context.Hit.Panel, e.Position, e.Position);
                context.RequestFrame();
                return ToolResult.Capture;
            case PointerEventKind.Move:
                _end = e.Position;
                context.RequestFrame();
                return ToolResult.Handled;
            case PointerEventKind.Up:
                _end = e.Position;
                var (left, top, width, height) = context.PanelRectangle(_panel);
                var (a, b) = (Clamp(_start, left, top, width, height), Clamp(_end, left, top, width, height));
                context.Views.ZoomToRect(_panel, a, b);
                return ToolResult.Release;
            default:
                return ToolResult.Handled;
        }
    }

    /// <inheritdoc />
    public void Cancel(ToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RequestFrame();
    }

    /// <inheritdoc />
    public void Overlay(RenderList list, ToolContext context)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(context);
        var (left, top, width, height) = context.PanelRectangle(_panel);
        var (a, b) = (Clamp(_start, left, top, width, height), Clamp(_end, left, top, width, height));
        var (x, y) = (MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y));
        var (w, h) = (MathF.Abs(b.X - a.X), MathF.Abs(b.Y - a.Y));
        var color = context.Theme.Foreground;

        // A fill of 15 % of the foreground and an outline of 1 DIP (design §6.5).
        list.DrawRect(x, y, w, h, color with { A = (byte)Math.Round(color.A * 0.15) });
        var line = MathF.Max(1, MathF.Round(context.Scale));
        list.DrawRect(x, y, w, line, color);
        list.DrawRect(x, y + h - line, w, line, color);
        list.DrawRect(x, y, line, h, color);
        list.DrawRect(x + w - line, y, line, h, color);
    }

    private static Vector2 Clamp(Vector2 point, int left, int top, int width, int height) =>
        new(Math.Clamp(point.X, left, left + width), Math.Clamp(point.Y, top, top + height));
}

/// <summary>
/// Zooms by a wheel and by a pinch at the pointer (design §6.5): on a panel both axes, on an axis that axis alone. A wheel's
/// notch zooms by <see cref="InteractionOptions.WheelZoomFactor"/>; the turns until a pause make one entry of the history.
/// </summary>
public sealed class WheelZoomTool : IPlotTool
{
    /// <inheritdoc />
    public ToolResult OnWheel(in WheelEvent e, ToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var axes = PanTool.Axes(context.Hit.Kind);
        if (axes == ViewAxes.None || e.DeltaY == 0)
        {
            return ToolResult.Ignored;
        }

        context.Views.ZoomAt(context.Hit.Panel, e.Position, Math.Pow(context.Options.WheelZoomFactor, e.DeltaY), axes, group: true);
        return ToolResult.Handled;
    }

    /// <inheritdoc />
    public ToolResult OnGesture(in GestureEvent e, ToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (e.Kind != GestureKind.Pinch || (context.Hit.Kind & HitAreaKind.Panel) == 0 || !double.IsFinite(e.Scale) || e.Scale <= 0)
        {
            return ToolResult.Ignored;
        }

        context.Views.ZoomAt(context.Hit.Panel, e.Position, e.Scale, ViewAxes.All, animate: false, group: true);
        return ToolResult.Handled;
    }
}

/// <summary>
/// Selects (design §6.4, row I3a, Issue #364): a drag makes a rectangle, an x range (on an x axis), or a lasso; a click adds
/// the points the hit test finds within 8 DIPs, or, over a density where it finds none, selects the range of the bin drawn
/// under it as a rectangle (row R3c). While a drag goes on, the tool draws its frame or its line over the plot.
/// </summary>
public sealed class SelectionTool : IPlotTool
{
    private readonly List<Vector2> _path = [];
    private PanelId _panel;
    private Vector2 _start;
    private Vector2 _end;

    /// <summary>Initializes a new instance of the <see cref="SelectionTool"/> class.</summary>
    /// <param name="kind">What the tool selects.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a named <see cref="SelectionKind"/>.</exception>
    public SelectionTool(SelectionKind kind) =>
        Kind = Enum.IsDefined(kind) ? kind : throw new ArgumentOutOfRangeException(nameof(kind), kind, "The kind is a named SelectionKind value.");

    /// <summary>Gets what the tool selects.</summary>
    public SelectionKind Kind { get; }

    /// <inheritdoc />
    public ToolResult OnPointer(in PointerEvent e, ToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Kind == SelectionKind.Points)
        {
            return e.Kind == PointerEventKind.Up ? AddPoints(e.Position, context) : ToolResult.Ignored;
        }

        switch (e.Kind)
        {
            case PointerEventKind.Down:
                var area = context.Hit.Kind;

                // A place over an annotation is its panel too (row I4a): the panel's flag, not the whole kind.
                var panel = (area & HitAreaKind.Panel) != 0;
                var fits = Kind == SelectionKind.XRange ? panel || area == HitAreaKind.AxisX : panel;
                if (!fits || context.IsPolar(context.Hit.Panel))
                {
                    return ToolResult.Ignored;
                }

                (_panel, _start, _end) = (context.Hit.Panel, e.Position, e.Position);
                _path.Clear();
                _path.Add(e.Position);
                context.RequestFrame();
                return ToolResult.Capture;
            case PointerEventKind.Move:
                Extend(e.Position, context);
                context.RequestFrame();
                return ToolResult.Handled;
            case PointerEventKind.Up:
                Extend(e.Position, context);
                Finish(context);
                return ToolResult.Release;
            default:
                return ToolResult.Handled;
        }
    }

    /// <inheritdoc />
    public void Cancel(ToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _path.Clear();
        context.RequestFrame();
    }

    /// <inheritdoc />
    public void Overlay(RenderList list, ToolContext context)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(context);
        var color = context.Theme.Foreground;
        var line = MathF.Max(1, MathF.Round(context.Scale));
        if (Kind == SelectionKind.Lasso)
        {
            if (_path.Count > 1)
            {
                list.DrawPolyline(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_path), new LineStyle(color, line));
            }

            return;
        }

        var (left, top, width, height) = Bounds(context);
        list.DrawRect(left, top, width, height, color with { A = (byte)Math.Round(color.A * 0.15) });
    }

    /// <summary>Returns the frame the drag spans, clamped to the panel: an x range runs the panel's height.</summary>
    private (float Left, float Top, float Width, float Height) Bounds(ToolContext context)
    {
        var (left, top, width, height) = context.PanelRectangle(_panel);
        var (a, b) = (Clamp(_start, left, top, width, height), Clamp(_end, left, top, width, height));
        if (Kind == SelectionKind.XRange)
        {
            return context.Flipped(_panel)
                ? (left, MathF.Min(a.Y, b.Y), width, MathF.Abs(b.Y - a.Y))
                : (MathF.Min(a.X, b.X), top, MathF.Abs(b.X - a.X), height);
        }

        return (MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Abs(b.X - a.X), MathF.Abs(b.Y - a.Y));
    }

    /// <summary>Follows the drag: a lasso keeps the points 1 DIP or more from the last (Issue #364 Decision 11).</summary>
    private void Extend(Vector2 at, ToolContext context)
    {
        _end = at;
        if (Kind == SelectionKind.Lasso && Vector2.Distance(_path[^1], at) >= context.Scale)
        {
            _path.Add(at);
        }
    }

    /// <summary>Makes the selection the drag spans, and gives it to the engine.</summary>
    private void Finish(ToolContext context)
    {
        var (left, top, width, height) = context.PanelRectangle(_panel);
        if (Kind != SelectionKind.Lasso)
        {
            var (a, b) = (Clamp(_start, left, top, width, height), Clamp(_end, left, top, width, height));
            var (from, to) = (context.PixelToView(_panel, a), context.PixelToView(_panel, b));
            var x = Span(from.X, to.X);
            context.Select(Kind == SelectionKind.XRange ? Selection2D.XRange(_panel, x) : Selection2D.Rectangle(_panel, x, Span(from.Y, to.Y)));
            _path.Clear();
            return;
        }

        // A lasso: its vertices in the scale space, then as shares of the ranges they span; past 1,024, every other one goes.
        while (_path.Count > 1024)
        {
            for (var i = _path.Count - 2; i > 0; i -= 2)
            {
                _path.RemoveAt(i);
            }
        }

        if (_path.Count < 3)
        {
            _path.Clear();
            return;
        }

        var views = new (ViewRange X, ViewRange Y)[_path.Count];
        for (var i = 0; i < views.Length; i++)
        {
            views[i] = context.PixelToView(_panel, Clamp(_path[i], left, top, width, height));
        }

        var (xs, ys) = (views[0].X, views[0].Y);
        foreach (var (vx, vy) in views)
        {
            (xs, ys) = (Span(xs, vx), Span(ys, vy));
        }

        var shares = new Vector2[views.Length];
        for (var i = 0; i < shares.Length; i++)
        {
            shares[i] = new Vector2((float)Share(views[i].X.Lower, xs), (float)Share(views[i].Y.Lower, ys));
        }

        context.Select(Selection2D.Lasso(_panel, xs, ys, shares));
        _path.Clear();
    }

    /// <summary>Adds the points a click hits to the plot's selection of points, or starts one.</summary>
    private static ToolResult AddPoints(Vector2 at, ToolContext context)
    {
        if ((context.Hit.Kind & HitAreaKind.Panel) == 0 || context.IsPolar(context.Hit.Panel))
        {
            return ToolResult.Ignored;
        }

        Span<SeriesHit> hits = stackalloc SeriesHit[64];
        var found = Math.Min(context.HitTest(at, 8 * context.Scale, hits), hits.Length);

        // A density's bin (a row of −1) is no point to select: a click that hits no point selects the range of the bin drawn
        // under it as a rectangle, a large bin of an ancestor's tile or a leaf's fill whole (design §6.4 "密度のクリック",
        // Issue #471 Decisions 9 and 17). The bins come after the points, so the points are first.
        var rows = 0;
        while (rows < found && hits[rows].Row >= 0)
        {
            rows++;
        }

        var panel = context.Hit.Panel;
        if (rows == 0)
        {
            if (found > 0 && context.BinRange(hits[0], out var x, out var y))
            {
                context.Select(Selection2D.Rectangle(panel, x, y));
            }

            return ToolResult.Handled;
        }

        found = rows;
        var points = new List<SeriesHit>();
        if (context.Selection(panel.Plot) is { Kind: SelectionKind.Points } before && before.Panel == panel)
        {
            points.AddRange(before.Points.Span);
        }

        foreach (var hit in hits[..found])
        {
            if (!points.Exists(p => (p.Layer, p.Series, p.Row) == (hit.Layer, hit.Series, hit.Row)))
            {
                points.Add(hit);
            }
        }

        context.Select(Selection2D.OfPoints(panel, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(points)));
        return ToolResult.Handled;
    }

    private static ViewRange Span(in ViewRange a, in ViewRange b) => new(a.Origin, Math.Min(a.Lower, b.Lower), Math.Max(a.Upper, b.Upper));

    private static double Share(double value, in ViewRange range) =>
        range.Upper > range.Lower ? (value - range.Lower) / (range.Upper - range.Lower) : 0;

    private static Vector2 Clamp(Vector2 point, int left, int top, int width, int height) =>
        new(Math.Clamp(point.X, left, left + width), Math.Clamp(point.Y, top, top + height));
}

/// <summary>
/// Drags a draggable annotation (design §6.5, requirement I-5, row I4a): a press on one takes hold of it, the drag moves its
/// whole, each coordinate in its own kind (a scale's value, or a share of the panel), and the release keeps it and raises
/// <see cref="PlotEngine.AnnotationChanged"/> when it moved. Escape and a cancel put it back. In the default bindings for a
/// left drag over an annotation, before the pan.
/// </summary>
public sealed class AnnotationTool : IPlotTool
{
    /// <inheritdoc />
    public ToolResult OnPointer(in PointerEvent e, ToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        switch (e.Kind)
        {
            case PointerEventKind.Down:
                return (context.Hit.Kind & HitAreaKind.Annotation) != 0 && context.BeginAnnotationDrag(context.Hit, e.Position)
                    ? ToolResult.Capture
                    : ToolResult.Ignored;
            case PointerEventKind.Move:
                context.DragAnnotation(e.Position);
                return ToolResult.Handled;
            case PointerEventKind.Up:
                context.DragAnnotation(e.Position);
                context.EndAnnotationDrag(keep: true);
                return ToolResult.Release;
            default:
                return ToolResult.Handled;
        }
    }

    /// <inheritdoc />
    public void Cancel(ToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.EndAnnotationDrag(keep: false);
    }
}

/// <summary>
/// Measures with a drag (design §6.5 "計測", requirement I-6, row I4b, Issue #390): <see cref="MeasureKind.Points"/> from the
/// press to the pointer, Δx, Δy, and the slope; <see cref="MeasureKind.Range"/> over the x range the drag spans, each series'
/// count, mean, least, and greatest. The measurement follows the drag and stays after the release, until the next one, Escape,
/// or a new mapping. Not in the default bindings: an application adds it. A polar panel is not measured.
/// </summary>
public sealed class MeasureTool : IPlotTool
{
    private PanelId _panel;
    private System.Numerics.Vector2 _from;

    /// <summary>Initializes a new instance of the <see cref="MeasureTool"/> class.</summary>
    /// <param name="kind">What the tool measures.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a named value.</exception>
    public MeasureTool(MeasureKind kind = MeasureKind.Points)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "The kind is a named MeasureKind value.");
        }

        Kind = kind;
    }

    /// <summary>Gets what the tool measures.</summary>
    public MeasureKind Kind { get; }

    /// <summary>Gets or sets the writer of the measurement's box: null for the default lines (Issue #390 Decision 11).</summary>
    public MeasureFormatter? Format { get; set; }

    /// <inheritdoc />
    public ToolResult OnPointer(in PointerEvent e, ToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        switch (e.Kind)
        {
            case PointerEventKind.Down:
                if ((context.Hit.Kind & HitAreaKind.Panel) == 0 || context.IsPolar(context.Hit.Panel))
                {
                    return ToolResult.Ignored;
                }

                (_panel, _from) = (context.Hit.Panel, e.Position);
                context.MeasureAt(this, _panel, _from, e.Position);
                return ToolResult.Capture;
            case PointerEventKind.Move:
                context.MeasureAt(this, _panel, _from, e.Position);
                return ToolResult.Handled;
            case PointerEventKind.Up:
                context.MeasureAt(this, _panel, _from, e.Position);
                return ToolResult.Release;
            default:
                return ToolResult.Handled;
        }
    }

    /// <inheritdoc />
    public void Cancel(ToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.ClearMeasurement();
    }
}
