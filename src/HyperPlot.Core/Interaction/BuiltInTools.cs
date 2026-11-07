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
        if (e.Kind != GestureKind.Scroll || context.Hit.Kind != HitAreaKind.Panel)
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

    /// <summary>Returns the axes a drag on a place moves: both on a panel, one on its axis, none elsewhere.</summary>
    internal static ViewAxes Axes(HitAreaKind kind) =>
        kind switch
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
                if (context.Hit.Kind != HitAreaKind.Panel)
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
        if (e.Kind != GestureKind.Pinch || context.Hit.Kind != HitAreaKind.Panel || !double.IsFinite(e.Scale) || e.Scale <= 0)
        {
            return ToolResult.Ignored;
        }

        context.Views.ZoomAt(context.Hit.Panel, e.Position, e.Scale, ViewAxes.All, animate: false, group: true);
        return ToolResult.Handled;
    }
}
