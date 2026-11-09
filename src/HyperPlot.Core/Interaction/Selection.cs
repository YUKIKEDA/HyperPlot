using System.Numerics;

namespace HyperPlot.Interaction;

/// <summary>What a selection holds (design §6.4, row I3a, Issue #364 Decision 3).</summary>
public enum SelectionKind
{
    /// <summary>A rectangle: an x range and a y range.</summary>
    Rectangle,

    /// <summary>An x range alone (a brush): the y is the panel's whole.</summary>
    XRange,

    /// <summary>A polygon (a lasso).</summary>
    Lasso,

    /// <summary>The points a click hit, gathered with Shift.</summary>
    Points,
}

/// <summary>
/// A selection of a plot (design §6.4, row I3a, Issue #364): a predicate in the scale space of one panel, or the points a
/// click hit. An abstraction, as <see cref="PanelView"/> is, so that a 3D selection is another kind (invariant 12, Issue
/// #364 Decision 14); <see cref="Selection2D"/> is the 2D one. A value: it does not change.
/// </summary>
public abstract class Selection
{
    private protected Selection(SelectionKind kind, PanelId panel, ReadOnlyMemory<SeriesHit> points) => (Kind, Panel, Points) = (kind, panel, points);

    /// <summary>Gets what the selection holds.</summary>
    public SelectionKind Kind { get; }

    /// <summary>Gets the panel the selection was made in.</summary>
    public PanelId Panel { get; }

    /// <summary>Gets the points of a <see cref="SelectionKind.Points"/> selection, as the hit test answered them; empty for the others.</summary>
    public ReadOnlyMemory<SeriesHit> Points { get; }
}

/// <summary>
/// A selection of a 2D panel (design §6.4, row I3a): a rectangle, an x range, a lasso, or points. The ranges are in the
/// scale space with an origin and differences (design §3.6), so that a time axis keeps its precision.
/// </summary>
public sealed class Selection2D : Selection
{
    private Selection2D(
        SelectionKind kind,
        PanelId panel,
        ViewRange x,
        ViewRange y,
        ReadOnlyMemory<Vector2> polygon,
        ReadOnlyMemory<SeriesHit> points
    )
        : base(kind, panel, points) => (X, Y, Polygon) = (x, y, polygon);

    /// <summary>Gets the x range: a rectangle's or an x range's; a lasso's polygon's bounds; empty for points.</summary>
    public ViewRange X { get; }

    /// <summary>Gets the y range: a rectangle's; the panel's whole for an x range; a lasso's polygon's bounds; empty for points.</summary>
    public ViewRange Y { get; }

    /// <summary>
    /// Gets a lasso's vertices as shares of <see cref="X"/> and <see cref="Y"/>, from 0 to 1, so that a time axis keeps its
    /// precision; empty for the other kinds. A point lies inside by the even-odd rule.
    /// </summary>
    public ReadOnlyMemory<Vector2> Polygon { get; }

    /// <summary>Returns a rectangle's selection.</summary>
    /// <param name="panel">The panel.</param>
    /// <param name="x">The x range.</param>
    /// <param name="y">The y range.</param>
    /// <returns>The selection.</returns>
    public static Selection2D Rectangle(PanelId panel, ViewRange x, ViewRange y) => new(SelectionKind.Rectangle, panel, x, y, default, default);

    /// <summary>Returns an x range's selection: the y is the panel's whole, which the engine sets when it takes it.</summary>
    /// <param name="panel">The panel.</param>
    /// <param name="x">The x range.</param>
    /// <returns>The selection.</returns>
    public static Selection2D XRange(PanelId panel, ViewRange x) => new(SelectionKind.XRange, panel, x, default, default, default);

    /// <summary>Returns a lasso's selection.</summary>
    /// <param name="panel">The panel.</param>
    /// <param name="x">The x range the polygon spans.</param>
    /// <param name="y">The y range the polygon spans.</param>
    /// <param name="polygon">The vertices, as shares of the ranges: 3 to 1,024. The selection keeps a copy.</param>
    /// <returns>The selection.</returns>
    public static Selection2D Lasso(PanelId panel, ViewRange x, ViewRange y, ReadOnlySpan<Vector2> polygon) =>
        new(SelectionKind.Lasso, panel, x, y, polygon.ToArray(), default);

    /// <summary>Returns a selection of points.</summary>
    /// <param name="panel">The panel.</param>
    /// <param name="points">The points, as a hit test answers them: one or more. The selection keeps a copy.</param>
    /// <returns>The selection.</returns>
    public static Selection2D OfPoints(PanelId panel, ReadOnlySpan<SeriesHit> points) =>
        new(SelectionKind.Points, panel, default, default, default, points.ToArray());

    /// <summary>Returns the same selection over another y range: an x range's, once the engine knows the panel's.</summary>
    internal Selection2D WithY(ViewRange y) => new(Kind, Panel, X, y, Polygon, Points);

    /// <summary>Returns whether a point of the scale space, as differences from the ranges' origins, lies in the selection.</summary>
    internal bool Holds(double x, double y)
    {
        if (x < X.Lower || x > X.Upper || (Kind != SelectionKind.XRange && (y < Y.Lower || y > Y.Upper)))
        {
            return false;
        }

        if (Kind != SelectionKind.Lasso)
        {
            return true;
        }

        // The even-odd rule over the polygon's shares (Issue #364 Decision 11).
        var (u, v) = ((x - X.Lower) / (X.Upper - X.Lower), (y - Y.Lower) / (Y.Upper - Y.Lower));
        var inside = false;
        var vertices = Polygon.Span;
        for (int i = 0, j = vertices.Length - 1; i < vertices.Length; j = i++)
        {
            var (a, b) = (vertices[i], vertices[j]);
            if ((a.Y > v) != (b.Y > v) && u < ((b.X - a.X) * (v - a.Y) / (b.Y - a.Y)) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }
}
