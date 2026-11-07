namespace HyperPlot.Interaction;

/// <summary>
/// A range of a scale's space (design §3.6, §6.2, row I1a): an integer origin and the differences of the low and the high
/// end from it. The origin keeps values past 2^53 exact, as the nanoseconds of a time axis: read <see cref="Origin"/>,
/// <see cref="Lower"/>, and <see cref="Upper"/> and pass them back to keep a view as it is.
/// </summary>
/// <param name="Origin">The origin, an integer of the scale's space.</param>
/// <param name="Lower">The low end, as a difference from <see cref="Origin"/>.</param>
/// <param name="Upper">The high end, as a difference from <see cref="Origin"/>.</param>
public readonly record struct ViewRange(long Origin, double Lower, double Upper)
{
    /// <summary>Gets the low end as a value, rounded to a <see cref="double"/>.</summary>
    public double LowValue => Origin + Lower;

    /// <summary>Gets the high end as a value, rounded to a <see cref="double"/>.</summary>
    public double HighValue => Origin + Upper;

    /// <summary>Returns the range of two values given as doubles.</summary>
    /// <param name="low">The low end.</param>
    /// <param name="high">The high end.</param>
    /// <returns>The range, its origin the floor of <paramref name="low"/>.</returns>
    public static ViewRange FromValues(double low, double high) => FromScale(ScaleRange.FromDouble(low, high));

    /// <summary>
    /// Returns the range of two integers, exact: the origin is <paramref name="low"/> and the differences are 0 and
    /// <paramref name="high"/> − <paramref name="low"/>. Use it for a time axis's ticks or nanoseconds and long columns.
    /// </summary>
    /// <param name="low">The low end.</param>
    /// <param name="high">The high end.</param>
    /// <returns>The range.</returns>
    public static ViewRange FromValues(long low, long high) => FromScale(ScaleRange.FromInteger(low, high));

    internal static ViewRange FromScale(in ScaleRange range) => new(range.Origin, range.Lower, range.Upper);

    internal ScaleRange ToScale() => new(Origin, Lower, Upper);
}

/// <summary>
/// A panel of what an engine draws (design §6.2, row I1a): the plot's number (0 for a plot alone, the place in a figure) and
/// the panel's number within the plot (0 for a plot with no facet).
/// </summary>
/// <param name="Plot">The plot's number.</param>
/// <param name="Panel">The panel's number within the plot.</param>
public readonly record struct PanelId(int Plot, int Panel);

/// <summary>
/// A panel's view (design §6.2, invariant 12): what part of the scales the panel shows. One abstraction for every kind, so
/// that the contract does not assume two dimensions; <see cref="PanelView2D"/> is the kind of row I1a.
/// </summary>
public abstract record PanelView
{
    private protected PanelView() { }
}

/// <summary>A two-dimensional panel's view (design §6.2, row I1a): a range of the x scale and one of the y scale.</summary>
/// <param name="X">The x scale's range.</param>
/// <param name="Y">The y scale's range.</param>
public sealed record PanelView2D(ViewRange X, ViewRange Y) : PanelView;

/// <summary>
/// The scales an operation moves (design §6.2, rows I1a and I1b): each flag one scale. An operation in a panel moves them all,
/// a secondary scale by the same shares of its range as its primary; one on an axis moves that axis's scale alone.
/// </summary>
[Flags]
public enum ViewAxes
{
    /// <summary>No axis.</summary>
    None = 0,

    /// <summary>The x scale.</summary>
    X = 1,

    /// <summary>The y scale.</summary>
    Y = 2,

    /// <summary>The x and the y scale, not the secondary ones.</summary>
    Both = X | Y,

    /// <summary>
    /// The secondary x scale (row G6a). With none, x's second axis is a transform's, which follows x: the flag moves x.
    /// </summary>
    X2 = 4,

    /// <summary>The secondary y scale; with none, as <see cref="X2"/>, y.</summary>
    Y2 = 8,

    /// <summary>Every scale: x, y, and the secondary x and y scales, as an operation in a panel moves them.</summary>
    All = X | Y | X2 | Y2,
}

/// <summary>What changed a view (design §6.2).</summary>
public enum ViewChangeReason
{
    /// <summary>An input through a tool or a command.</summary>
    Input,

    /// <summary>The application's code, through <see cref="ViewOperations"/>.</summary>
    Code,

    /// <summary>A move back or forward in the history.</summary>
    History,

    /// <summary>A linked panel (row I3).</summary>
    Link,

    /// <summary>A new spec whose position scales map otherwise: the view went back to the full view.</summary>
    Spec,
}

/// <summary>
/// A change of a panel's view (design §6.2): the panel, the reason, and the view the change goes to. A change that animates
/// is told once, with the view it ends at. A value: reading it allocates nothing but <see cref="View"/>.
/// </summary>
public readonly struct ViewChange
{
    private readonly ScaleRange _x;
    private readonly ScaleRange _y;

    internal ViewChange(PanelId panel, ViewChangeReason reason, in ScaleRange x, in ScaleRange y) => (Panel, Reason, _x, _y) = (panel, reason, x, y);

    /// <summary>Gets the panel.</summary>
    public PanelId Panel { get; }

    /// <summary>Gets what changed the view.</summary>
    public ViewChangeReason Reason { get; }

    /// <summary>Gets the view the change goes to, as a new object.</summary>
    public PanelView View => new PanelView2D(ViewRange.FromScale(_x), ViewRange.FromScale(_y));

    /// <summary>Gets the two ranges of a two-dimensional view with no allocation.</summary>
    /// <param name="x">The x scale's range.</param>
    /// <param name="y">The y scale's range.</param>
    /// <returns>True: the views of row I1a are two-dimensional.</returns>
    public bool TryGet2D(out ViewRange x, out ViewRange y)
    {
        (x, y) = (ViewRange.FromScale(_x), ViewRange.FromScale(_y));
        return true;
    }
}

/// <summary>Handles a change of a view.</summary>
/// <param name="change">The change.</param>
public delegate void ViewChangedHandler(in ViewChange change);

/// <summary>
/// A view an operation proposes, before it takes effect (design §6.2): constraints read it, may change it, or refuse it. The
/// engine keeps one and reuses it, so a constraint allocates nothing to read or change it.
/// </summary>
public sealed class ViewProposal
{
    private ScaleRange _x;
    private ScaleRange _y;

    internal ViewProposal() { }

    /// <summary>Gets the panel.</summary>
    public PanelId Panel { get; private set; }

    /// <summary>Gets what proposes the change.</summary>
    public ViewChangeReason Reason { get; private set; }

    /// <summary>Gets the panel's rectangle in device pixels, for constraints that limit by pixels.</summary>
    public (int Left, int Top, int Width, int Height) PanelRectangle { get; private set; }

    /// <summary>Gets the two ranges of a two-dimensional view.</summary>
    /// <param name="x">The x scale's range.</param>
    /// <param name="y">The y scale's range.</param>
    /// <returns>True: the views of row I1a are two-dimensional.</returns>
    public bool TryGet2D(out ViewRange x, out ViewRange y)
    {
        (x, y) = (ViewRange.FromScale(_x), ViewRange.FromScale(_y));
        return true;
    }

    /// <summary>Changes the two ranges of a two-dimensional view.</summary>
    /// <param name="x">The x scale's range.</param>
    /// <param name="y">The y scale's range.</param>
    /// <exception cref="ArgumentException">An end is not finite, or a range's low end is not below its high end.</exception>
    public void Set2D(ViewRange x, ViewRange y)
    {
        ViewOperations.CheckRange(x, nameof(x));
        ViewOperations.CheckRange(y, nameof(y));
        (_x, _y) = (x.ToScale().Anchor(), y.ToScale().Anchor());
    }

    internal ScaleRange X => _x;

    internal ScaleRange Y => _y;

    internal void Start(
        PanelId panel,
        ViewChangeReason reason,
        (int Left, int Top, int Width, int Height) rectangle,
        in ScaleRange x,
        in ScaleRange y
    ) => (Panel, Reason, PanelRectangle, _x, _y) = (panel, reason, rectangle, x, y);
}

/// <summary>
/// Limits the views the engine takes (design §6.2): an application's bounds of zoom, a range a view may move in, an axis that
/// does not move. The engine passes each proposal through its constraints in order before the view changes.
/// </summary>
public interface IViewConstraint
{
    /// <summary>Reads a proposed view, and changes it or refuses it.</summary>
    /// <param name="proposal">The proposal; <see cref="ViewProposal.Set2D"/> changes it.</param>
    /// <returns>True to go on with the proposal as it now is; false to refuse the change, which then is not told.</returns>
    bool Constrain(ViewProposal proposal);
}
