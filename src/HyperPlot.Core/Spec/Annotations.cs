using HyperPlot.Rendering;

namespace HyperPlot;

/// <summary>What an annotation's coordinate on one axis is fixed to (design §4.1, row I4a, Issue #382 Decisions 5 and 18).</summary>
public enum AnnotationCoord
{
    /// <summary>A value of the primary scale's space, as an origin and a difference (design §3.6): it moves with a zoom.</summary>
    Data = 0,

    /// <summary>A value of the secondary scale's space (row G6a): the axis needs a secondary scale.</summary>
    Data2 = 1,

    /// <summary>
    /// A share of the panel along the axis's direction on the screen: 0 at the left (a horizontal axis) or the bottom (a
    /// vertical one), 1 at the other end, whatever the scale's reverse. Under flip, x runs up the screen.
    /// </summary>
    Panel = 2,
}

/// <summary>
/// One coordinate of an annotation's point (design §4.1, row I4a): what it is fixed to, and its value as an origin and a
/// difference (design §3.6), so that a time axis's nanoseconds keep their precision.
/// </summary>
/// <param name="Coord">What the coordinate is fixed to.</param>
/// <param name="Origin">The origin of a scale's value; 0 for a share of the panel.</param>
/// <param name="Offset">The difference from the origin, or the share of the panel.</param>
public readonly record struct AnnotationValue(AnnotationCoord Coord, long Origin, double Offset)
{
    /// <summary>Returns a value of the primary scale's space, its origin chosen as a view's is (design §3.6).</summary>
    /// <param name="value">The value.</param>
    /// <returns>The coordinate.</returns>
    public static AnnotationValue Data(double value) => Of(AnnotationCoord.Data, value);

    /// <summary>Returns a value of the primary scale's space as an origin and a difference: exact for a time axis.</summary>
    /// <param name="origin">The origin.</param>
    /// <param name="offset">The difference from the origin.</param>
    /// <returns>The coordinate.</returns>
    public static AnnotationValue Data(long origin, double offset) => new(AnnotationCoord.Data, origin, offset);

    /// <summary>Returns a value of the secondary scale's space (row G6a).</summary>
    /// <param name="value">The value.</param>
    /// <returns>The coordinate.</returns>
    public static AnnotationValue Data2(double value) => Of(AnnotationCoord.Data2, value);

    /// <summary>Returns a share of the panel along the axis's direction on the screen: 0 at the left or the bottom.</summary>
    /// <param name="share">The share.</param>
    /// <returns>The coordinate.</returns>
    public static AnnotationValue Panel(double share) => new(AnnotationCoord.Panel, 0, share);

    private static AnnotationValue Of(AnnotationCoord coord, double value)
    {
        if (!double.IsFinite(value))
        {
            return new(coord, 0, value);
        }

        var range = ScaleRange.FromDouble(value, value);
        return new(coord, range.Origin, range.Lower);
    }
}

/// <summary>An annotation's point (design §4.1, row I4a): its x along the x scale's direction, its y along the y scale's.</summary>
/// <param name="X">The x coordinate.</param>
/// <param name="Y">The y coordinate.</param>
public readonly record struct AnnotationPoint(AnnotationValue X, AnnotationValue Y);

/// <summary>
/// The shape of a marker (rows I4a and R3f): of a <see cref="MarkerAnnotationSpec"/> and of the points of a
/// <see cref="PointLayerSpec"/>. Every shape fits in the square box whose side is the marker's size.
/// </summary>
public enum MarkerShape
{
    /// <summary>A filled disc.</summary>
    Circle = 0,

    /// <summary>A filled square.</summary>
    Square = 1,

    /// <summary>A horizontal and a vertical line that cross at the center: a plus sign.</summary>
    Cross = 2,

    /// <summary>A filled triangle that points up: its apex at the middle of the box's top side, its base the bottom side.</summary>
    Triangle = 3,

    /// <summary>A filled diamond: its corners at the middles of the box's sides.</summary>
    Diamond = 4,

    /// <summary>Two lines along the box's diagonals.</summary>
    X = 5,
}

/// <summary>
/// An annotation over a plot's panels (design §4.1, requirement I-5, row I4a): drawn after the data layers inside the panel,
/// fixed to the data or the panel axis by axis. Make one with <see cref="Annotation"/>.
/// </summary>
public abstract record AnnotationSpec : SpecNode
{
    private protected AnnotationSpec() { }

    /// <summary>Gets the panel the annotation is drawn on: null for every panel of a facet, or the panel's number, 0 or more.</summary>
    public int? Panel { get; init; }

    /// <summary>Gets the annotation's color: null for the theme's <see cref="Theme.Foreground"/>.</summary>
    public Color? Color { get; init; }

    /// <summary>Gets a value indicating whether a pointer may drag the annotation (design §6.5): false by default.</summary>
    public bool Draggable { get; init; }
}

/// <summary>A line between two points (row I4a).</summary>
/// <param name="From">The first end.</param>
/// <param name="To">The second end.</param>
public sealed record LineAnnotationSpec(AnnotationPoint From, AnnotationPoint To) : AnnotationSpec
{
    /// <summary>Gets the line's width in DIPs: 1 by default.</summary>
    public float Width { get; init; } = 1;
}

/// <summary>A rectangle between two corners (row I4a): its edges, and its inside when it has a fill.</summary>
/// <param name="Corner">A corner.</param>
/// <param name="Opposite">The opposite corner.</param>
public sealed record RectAnnotationSpec(AnnotationPoint Corner, AnnotationPoint Opposite) : AnnotationSpec
{
    /// <summary>Gets the edges' width in DIPs: 1 by default.</summary>
    public float Width { get; init; } = 1;

    /// <summary>Gets the inside's color: null for none.</summary>
    public Color? Fill { get; init; }
}

/// <summary>A text at a point (row I4a).</summary>
/// <param name="At">The point the text's block is aligned to.</param>
/// <param name="Text">The text.</param>
public sealed record TextAnnotationSpec(AnnotationPoint At, string Text) : AnnotationSpec
{
    /// <summary>Gets the text's size in DIPs: null for the theme's <see cref="Theme.TickLabelSize"/>.</summary>
    public float? Size { get; init; }

    /// <summary>Gets the point of the block's width the point is at: its left by default.</summary>
    public TextAlign Horizontal { get; init; } = TextAlign.Left;

    /// <summary>Gets the point of the block's height the point is at: its bottom by default.</summary>
    public TextBaseline Vertical { get; init; } = TextBaseline.Bottom;
}

/// <summary>A marker at a point (row I4a).</summary>
/// <param name="At">The point.</param>
public sealed record MarkerAnnotationSpec(AnnotationPoint At) : AnnotationSpec
{
    /// <summary>Gets the marker's shape: a circle by default.</summary>
    public MarkerShape Shape { get; init; } = MarkerShape.Circle;

    /// <summary>Gets the marker's radius in DIPs (half the side of the box the shape fits in): 4 by default.</summary>
    public float Radius { get; init; } = 4;
}

/// <summary>Makes annotations (design §4.1, row I4a).</summary>
public static class Annotation
{
    /// <summary>Returns a line between two points.</summary>
    /// <param name="from">The first end.</param>
    /// <param name="to">The second end.</param>
    /// <returns>The annotation.</returns>
    public static LineAnnotationSpec Line(AnnotationPoint from, AnnotationPoint to) => new(from, to);

    /// <summary>Returns a rectangle between two corners.</summary>
    /// <param name="corner">A corner.</param>
    /// <param name="opposite">The opposite corner.</param>
    /// <returns>The annotation.</returns>
    public static RectAnnotationSpec Rect(AnnotationPoint corner, AnnotationPoint opposite) => new(corner, opposite);

    /// <summary>Returns a text at a point.</summary>
    /// <param name="at">The point.</param>
    /// <param name="text">The text.</param>
    /// <returns>The annotation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public static TextAnnotationSpec Text(AnnotationPoint at, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new(at, text);
    }

    /// <summary>Returns a marker at a point.</summary>
    /// <param name="at">The point.</param>
    /// <returns>The annotation.</returns>
    public static MarkerAnnotationSpec Marker(AnnotationPoint at) => new(at);
}
