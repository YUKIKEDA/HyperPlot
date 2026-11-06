using System.Numerics;

namespace HyperPlot.Interaction;

/// <summary>
/// The operations on an engine's views (design §6.2, row I1a): zoom, pan, a rectangle, a view set directly, the full view,
/// and the history. The built-in tools call these and nothing else; an application's buttons and menus call the same, so
/// that both go through the same rules: the origin moved again (§3.6), the constraints, the history, the animation, and
/// the notice.
/// </summary>
/// <remarks>
/// Call it from the engine's thread only. Before the engine has drawn its first frame of a plot, there is no layout to read
/// pixels against, and the operations throw <see cref="InvalidOperationException"/>. In row I1a a plot alone has views;
/// its band x axis and a polar plot keep the full view (row I1b).
/// </remarks>
public sealed class ViewOperations
{
    private readonly PlotEngine _engine;

    internal ViewOperations(PlotEngine engine) => _engine = engine;

    /// <summary>Gets a value indicating whether there is a view to go back to.</summary>
    public bool CanGoBack => _engine.CanGoBack;

    /// <summary>Gets a value indicating whether there is a view to go forward to.</summary>
    public bool CanGoForward => _engine.CanGoForward;

    /// <summary>Zooms a panel's view at a pixel: the value under the pixel stays under it.</summary>
    /// <param name="panel">The panel.</param>
    /// <param name="pixel">The pixel, in device pixels.</param>
    /// <param name="factor">The zoom: above 1 zooms in, below 1 out; finite and above 0.</param>
    /// <param name="axes">The axes it moves.</param>
    /// <param name="animate">Whether it animates; null for the default, which does.</param>
    /// <param name="group">
    /// Whether it joins the entry of the history that the zooms and pans just before made, as a wheel's turns do until a
    /// pause of <see cref="InteractionOptions.WheelGroupGap"/> (design §6.2).
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">The factor is not finite or not above 0, or the panel is not drawn.</exception>
    /// <exception cref="InvalidOperationException">The engine has drawn no frame of the plot, or draws a figure (row I1b).</exception>
    public void ZoomAt(PanelId panel, Vector2 pixel, double factor, ViewAxes axes = ViewAxes.Both, bool? animate = null, bool group = false)
    {
        if (!double.IsFinite(factor) || factor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "The factor is finite and above 0.");
        }

        _engine.ZoomAt(panel, pixel, factor, axes, animate ?? true, group);
    }

    /// <summary>Moves a panel's view by pixels: the data move with the pointer, as when a pointer drags them.</summary>
    /// <param name="panel">The panel.</param>
    /// <param name="pixels">The move, in device pixels.</param>
    /// <param name="axes">The axes it moves.</param>
    /// <param name="group">Whether it joins the entry of the history just before, as a scroll's turns do (design §6.2).</param>
    /// <exception cref="ArgumentOutOfRangeException">A move is not finite, or the panel is not drawn.</exception>
    /// <exception cref="InvalidOperationException">The engine has drawn no frame of the plot, or draws a figure (row I1b).</exception>
    public void Pan(PanelId panel, Vector2 pixels, ViewAxes axes = ViewAxes.Both, bool group = false)
    {
        if (!float.IsFinite(pixels.X) || !float.IsFinite(pixels.Y))
        {
            throw new ArgumentOutOfRangeException(nameof(pixels), pixels, "The move is finite.");
        }

        _engine.Pan(panel, pixels, axes, group);
    }

    /// <summary>Shows a rectangle of a panel, in device pixels, as its view.</summary>
    /// <param name="panel">The panel.</param>
    /// <param name="corner">One corner.</param>
    /// <param name="opposite">The opposite corner.</param>
    /// <param name="axes">The axes it moves: an axis whose side of the rectangle is under 4 DIPs keeps its range.</param>
    /// <param name="animate">Whether it animates; null for the default, which does.</param>
    /// <exception cref="ArgumentOutOfRangeException">A corner is not finite, or the panel is not drawn.</exception>
    /// <exception cref="InvalidOperationException">The engine has drawn no frame of the plot, or draws a figure (row I1b).</exception>
    public void ZoomToRect(PanelId panel, Vector2 corner, Vector2 opposite, ViewAxes axes = ViewAxes.Both, bool? animate = null)
    {
        if (!float.IsFinite(corner.X) || !float.IsFinite(corner.Y) || !float.IsFinite(opposite.X) || !float.IsFinite(opposite.Y))
        {
            throw new ArgumentOutOfRangeException(nameof(corner), (corner, opposite), "The corners are finite.");
        }

        _engine.ZoomToRect(panel, corner, opposite, axes, animate ?? true);
    }

    /// <summary>Sets a panel's view (requirement I-9).</summary>
    /// <param name="panel">The panel.</param>
    /// <param name="view">The view: a <see cref="PanelView2D"/>.</param>
    /// <param name="animate">Whether it animates; false by default.</param>
    /// <exception cref="ArgumentNullException"><paramref name="view"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The view is not two-dimensional, an end is not finite, a low end is not below its high end, or the plot's band axis or
    /// polar plot keeps its full view (row I1b).
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The panel is not drawn.</exception>
    /// <exception cref="InvalidOperationException">The engine has drawn no frame of the plot, or draws a figure (row I1b).</exception>
    public void SetView(PanelId panel, PanelView view, bool animate = false)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (view is not PanelView2D two)
        {
            throw new ArgumentException($"A {view.GetType().Name} is not a view of a two-dimensional panel.", nameof(view));
        }

        CheckRange(two.X, nameof(view));
        CheckRange(two.Y, nameof(view));
        _engine.SetView(panel, two.X.ToScale().Anchor(), two.Y.ToScale().Anchor(), animate);
    }

    /// <summary>Shows a panel's full view: what its spec draws with no view.</summary>
    /// <param name="panel">The panel.</param>
    /// <param name="animate">Whether it animates; null for the default, which does.</param>
    /// <exception cref="ArgumentOutOfRangeException">The panel is not drawn.</exception>
    /// <exception cref="InvalidOperationException">The engine has drawn no frame of the plot, or draws a figure (row I1b).</exception>
    public void Reset(PanelId panel, bool? animate = null) => _engine.Reset(panel, animate ?? true);

    /// <summary>Shows every panel's full view.</summary>
    /// <param name="animate">Whether it animates; null for the default, which does.</param>
    /// <exception cref="InvalidOperationException">The engine has drawn no frame of the plot, or draws a figure (row I1b).</exception>
    public void ResetAll(bool? animate = null) => _engine.Reset(new PanelId(0, 0), animate ?? true);

    /// <summary>Goes back to the view before in the history; nothing when there is none.</summary>
    /// <returns>Whether it went back.</returns>
    public bool Back() => _engine.MoveInHistory(-1);

    /// <summary>Goes forward to the view after in the history; nothing when there is none.</summary>
    /// <returns>Whether it went forward.</returns>
    public bool Forward() => _engine.MoveInHistory(+1);

    /// <summary>
    /// Starts a gesture: the views the operations set until <see cref="EndGesture"/> make one entry of the history, as a drag
    /// does.
    /// </summary>
    public void BeginGesture() => _engine.BeginGesture();

    /// <summary>Ends a gesture: the view it reached becomes one entry of the history.</summary>
    public void EndGesture() => _engine.EndGesture();

    internal static void CheckRange(in ViewRange range, string name)
    {
        if (!double.IsFinite(range.Lower) || !double.IsFinite(range.Upper) || !(range.Lower < range.Upper))
        {
            throw new ArgumentException($"The range {range} has an end that is not finite, or its low end is not below its high end.", name);
        }
    }
}
