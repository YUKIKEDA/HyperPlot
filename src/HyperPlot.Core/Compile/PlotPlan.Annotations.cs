using System.Numerics;
using HyperPlot.Rendering;

namespace HyperPlot;

/// <summary>
/// How far a pointer moved an annotation's points (design §6.5, row I4a): each coordinate's change, in its scale's space or as
/// a share of the panel, for the first and the second point (a line's ends, a rectangle's corners; one point's annotation uses
/// the first). The engine keeps it; the plan adds it to the spec's values when it records.
/// </summary>
internal readonly record struct AnnotationShift(double X0, double Y0, double X1, double Y1)
{
    public bool IsZero => X0 == 0 && Y0 == 0 && X1 == 0 && Y1 == 0;
}

/// <content>
/// The annotations of a plot (design §4.1, §5.4, §6.3, row I4a, Issue #382): their checks, their recording after the data
/// layers inside each panel, and the shapes a pointer hits.
/// </content>
internal sealed partial class PlotPlan
{
    /// <summary>The most annotations a plot takes (Issue #382 Decision 11).</summary>
    internal const int MaxAnnotations = 1024;

    // The draggable annotations' shapes of the last recording, in pixels, a panel's in their order (design §6.3).
    private readonly List<AnnotationHit> _annotationHits = [];
    private AnnotationShift[] _shifts = [];
    private bool _shiftsChanged;

    private enum HitShape : byte
    {
        Line,
        Box,
        Disc,
    }

    /// <summary>
    /// Sets how far the engine moved the annotations (design §6.5): an entry an annotation, none past the span. Another set
    /// records the plot again at the next compile. Allocates only for a new number of annotations.
    /// </summary>
    internal void SetAnnotationShifts(ReadOnlySpan<AnnotationShift> shifts)
    {
        var same = shifts.Length == _shifts.Length;
        for (var i = 0; same && i < shifts.Length; i++)
        {
            same = shifts[i] == _shifts[i];
        }

        if (same)
        {
            return;
        }

        if (_shifts.Length != shifts.Length)
        {
            _shifts = new AnnotationShift[shifts.Length];
        }

        shifts.CopyTo(_shifts);
        _shiftsChanged = true;
    }

    /// <summary>
    /// Returns the draggable annotation a pixel hits in a panel (design §6.3, Issue #382 Decision 9): the last one drawn whose
    /// shape lies within the radius, or −1. O(the annotations), over the last recording's shapes.
    /// </summary>
    internal int HitAnnotation(int panel, Vector2 pixel, float radius)
    {
        for (var i = _annotationHits.Count - 1; i >= 0; i--)
        {
            var hit = _annotationHits[i];
            if (hit.Panel == panel && hit.Hits(pixel, radius))
            {
                return hit.Index;
            }
        }

        return -1;
    }

    /// <summary>Returns the pixels of an annotation's first and second point in a panel, as the last recording drew them.</summary>
    internal bool AnnotationPixels(int index, int panel, out Vector2 first, out Vector2 second)
    {
        foreach (var hit in _annotationHits)
        {
            if (hit.Index == index && hit.Panel == panel)
            {
                (first, second) = (hit.First, hit.Second);
                return true;
            }
        }

        (first, second) = (default, default);
        return false;
    }

    /// <summary>Throws unless a spec's notes and annotations check (design §4.8 "検査（行 I4a）").</summary>
    private void CheckAnnotations(PlotSpec plot)
    {
        var annotations = plot.Annotations ?? throw new ArgumentNullException(nameof(plot), "The spec's Annotations must not be null.");
        if (annotations.Count > MaxAnnotations)
        {
            throw new ArgumentException(
                $"The spec has {annotations.Count} annotations; a plot takes at most {MaxAnnotations} (design §4.8).",
                nameof(plot)
            );
        }

        var polar = plot.Coord is PolarCoordSpec;
        for (var i = 0; i < annotations.Count; i++)
        {
            var annotation = annotations[i] ?? throw new ArgumentNullException(nameof(plot), $"Annotation {i} of the spec is null.");
            if (annotation.Panel < 0)
            {
                throw new ArgumentException($"Annotation {i}'s Panel is {annotation.Panel}; a panel's number is 0 or more.", nameof(plot));
            }

            switch (annotation)
            {
                case LineAnnotationSpec line:
                    Point(plot, i, line.From, polar);
                    Point(plot, i, line.To, polar);
                    Positive(i, line.Width, "Width");
                    break;
                case RectAnnotationSpec rect:
                    Point(plot, i, rect.Corner, polar);
                    Point(plot, i, rect.Opposite, polar);
                    Positive(i, rect.Width, "Width");
                    if (polar && rect.Corner.X.Coord != AnnotationCoord.Panel)
                    {
                        throw new ArgumentException(
                            $"Annotation {i} is a rectangle of the data in polar coordinates, where it would be a sector: a polar plot takes a rectangle of the panel's shares only (design §4.8).",
                            nameof(plot)
                        );
                    }

                    break;
                case TextAnnotationSpec text:
                    Point(plot, i, text.At, polar);
                    if (text.Text is null)
                    {
                        throw new ArgumentNullException(nameof(plot), $"Annotation {i}'s Text is null.");
                    }

                    if (text.Size is { } size)
                    {
                        Positive(i, size, "Size");
                        if (size * _scale > TextEngine.MaxSize)
                        {
                            throw new ArgumentException(
                                $"Annotation {i}'s Size is {size}; a text size is at most {TextEngine.MaxSize} pixels at the scale {_scale}.",
                                nameof(plot)
                            );
                        }
                    }

                    if (
                        text.Horizontal is not (TextAlign.Left or TextAlign.Center or TextAlign.Right)
                        || text.Vertical is not (TextBaseline.Top or TextBaseline.Middle or TextBaseline.Baseline or TextBaseline.Bottom)
                    )
                    {
                        throw new ArgumentOutOfRangeException(nameof(plot), $"Annotation {i}'s alignment is not a named value.");
                    }

                    break;
                case MarkerAnnotationSpec marker:
                    Point(plot, i, marker.At, polar);
                    Positive(i, marker.Radius, "Radius");
                    if (!MarkerStyle.IsShape(marker.Shape))
                    {
                        throw new ArgumentOutOfRangeException(nameof(plot), $"Annotation {i}'s Shape is not a named value.");
                    }

                    break;
            }
        }

        static void Positive(int i, float value, string name)
        {
            if (!(value > 0) || !float.IsFinite(value))
            {
                throw new ArgumentException($"Annotation {i}'s {name} is {value}; it is finite and above 0.", nameof(plot));
            }
        }

        static void Point(PlotSpec plot, int i, in AnnotationPoint point, bool polar)
        {
            Coordinate(plot, i, point.X, ofX: true);
            Coordinate(plot, i, point.Y, ofX: false);

            // A polar point is mapped through the transform, or placed on the panel's rectangle: not half of each.
            if (polar && (point.X.Coord == AnnotationCoord.Panel) != (point.Y.Coord == AnnotationCoord.Panel))
            {
                throw new ArgumentException(
                    $"Annotation {i} has a point of the data on one axis and of the panel's share on the other in polar coordinates: a polar point is one or the other (design §4.8).",
                    nameof(plot)
                );
            }
        }

        static void Coordinate(PlotSpec plot, int i, in AnnotationValue value, bool ofX)
        {
            // The named values are compared one by one, not with Enum.IsDefined: a drag checks the annotations on every
            // event, and with Enum.IsDefined the counted rounds of AnnotationTests.Frames_AllocateNothing allocated 992
            // bytes, 4 times the 248 bytes of MarkerStyle.IsShape, when the whole project ran in Debug (Issue #400).
            if (value.Coord is not (AnnotationCoord.Data or AnnotationCoord.Data2 or AnnotationCoord.Panel))
            {
                throw new ArgumentOutOfRangeException(nameof(plot), $"Annotation {i} has a coordinate whose Coord is not a named value.");
            }

            if (!double.IsFinite(value.Offset))
            {
                throw new ArgumentException($"Annotation {i} has a coordinate of {value.Offset}; a coordinate is finite.", nameof(plot));
            }

            if (value.Coord == AnnotationCoord.Data2 && (ofX ? plot.ScaleX2 : plot.ScaleY2) is null)
            {
                throw new ArgumentException(
                    $"Annotation {i} has a coordinate of the secondary {(ofX ? "x" : "y")} scale, and the spec has none (design §4.8).",
                    nameof(plot)
                );
            }
        }
    }

    /// <summary>Returns whether two specs have the same annotations: the same list, or the same nodes in the same order.</summary>
    private static bool SameAnnotations(PlotSpec before, PlotSpec after)
    {
        if (ReferenceEquals(before.Annotations, after.Annotations))
        {
            return true;
        }

        if (before.Annotations.Count != after.Annotations.Count)
        {
            return false;
        }

        for (var i = 0; i < after.Annotations.Count; i++)
        {
            if (!ReferenceEquals(before.Annotations[i], after.Annotations[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Records a panel's annotations after its data layers, inside its rectangle (design §5.4, Issue #382 Decisions 7, 8, 12, 13,
    /// 18): each point mapped axis by axis, moved by the engine's shift; and keeps the draggable ones' shapes for the hit test.
    /// </summary>
    private void RecordAnnotations(PlotSpec spec, int panel, PanelGuide guide, in AnnotationAxes axes)
    {
        var annotations = spec.Annotations;
        if (annotations.Count == 0)
        {
            return;
        }

        var theme = spec.Theme;
        _list.SetClip(guide.Left, guide.Top, guide.Width, guide.Height);
        for (var i = 0; i < annotations.Count; i++)
        {
            var annotation = annotations[i];
            if (annotation.Panel is { } only && only != panel)
            {
                continue;
            }

            var shift = i < _shifts.Length ? _shifts[i] : default;
            var color = annotation.Color ?? theme.Foreground;
            switch (annotation)
            {
                case LineAnnotationSpec line:
                {
                    var (a, b) = (Map(line.From, shift.X0, shift.Y0, guide, axes), Map(line.To, shift.X1, shift.Y1, guide, axes));
                    var width = line.Width * _scale;
                    _list.DrawPolyline([a, b], new LineStyle(color, width));
                    Keep(annotation, i, panel, HitShape.Line, a, b, width / 2);
                    break;
                }

                case RectAnnotationSpec rect:
                {
                    var (a, b) = (Map(rect.Corner, shift.X0, shift.Y0, guide, axes), Map(rect.Opposite, shift.X1, shift.Y1, guide, axes));
                    var (min, max) = (Vector2.Min(a, b), Vector2.Max(a, b));
                    if (rect.Fill is { } fill)
                    {
                        _list.DrawRect(min.X, min.Y, max.X - min.X, max.Y - min.Y, fill);
                    }

                    _list.DrawPolyline(
                        [min, new Vector2(max.X, min.Y), max, new Vector2(min.X, max.Y), min],
                        new LineStyle(color, rect.Width * _scale)
                    );
                    Keep(annotation, i, panel, HitShape.Box, min, max, 0, a, b);
                    break;
                }

                case TextAnnotationSpec text:
                {
                    var at = Map(text.At, shift.X0, shift.Y0, guide, axes);
                    var style = new TextStyle(
                        color,
                        (text.Size ?? theme.TickLabelSize) * _scale,
                        theme.FontFamily,
                        FontWeight.Normal,
                        text.Horizontal,
                        text.Vertical,
                        TextRotation.None,
                        theme.Locale
                    );
                    _list.DrawText(text.Text, at, style);
                    if (annotation.Draggable)
                    {
                        var size = _text.Measure(text.Text, style);
                        var dx = text.Horizontal switch
                        {
                            TextAlign.Center => size.Width / 2,
                            TextAlign.Right => size.Width,
                            _ => 0,
                        };
                        var dy = text.Vertical switch
                        {
                            TextBaseline.Middle => size.Height / 2,
                            TextBaseline.Baseline => size.Ascent,
                            TextBaseline.Bottom => size.Height,
                            _ => 0,
                        };
                        var (left, top) = (at.X - dx, at.Y - dy);
                        Keep(
                            annotation,
                            i,
                            panel,
                            HitShape.Box,
                            new Vector2(left, top),
                            new Vector2(left + size.Width, top + size.Height),
                            0,
                            at,
                            at
                        );
                    }

                    break;
                }

                case MarkerAnnotationSpec marker:
                {
                    var at = Map(marker.At, shift.X0, shift.Y0, guide, axes);
                    var radius = marker.Radius * _scale;
                    switch (marker.Shape)
                    {
                        case MarkerShape.Square:
                            _list.DrawRect(at.X - radius, at.Y - radius, 2 * radius, 2 * radius, color);
                            break;
                        case MarkerShape.Cross:
                            var line = new LineStyle(color, MathF.Max(1, MathF.Round(_scale)));
                            _list.DrawPolyline([at - new Vector2(radius, 0), at + new Vector2(radius, 0)], line);
                            _list.DrawPolyline([at - new Vector2(0, radius), at + new Vector2(0, radius)], line);
                            break;
                        default:
                            _list.DrawSectors([new SectorTrapezoid(0, MathF.Tau, radius, radius, 0, 0)], at, color);
                            break;
                    }

                    Keep(annotation, i, panel, HitShape.Disc, at, at, radius);
                    break;
                }
            }
        }

        _list.ResetClip();
    }

    /// <summary>Keeps a draggable annotation's shape for the hit test, and the pixels of its points for a drag.</summary>
    private void Keep(AnnotationSpec annotation, int index, int panel, HitShape shape, Vector2 a, Vector2 b, float size) =>
        Keep(annotation, index, panel, shape, a, b, size, a, b);

    private void Keep(
        AnnotationSpec annotation,
        int index,
        int panel,
        HitShape shape,
        Vector2 a,
        Vector2 b,
        float size,
        Vector2 first,
        Vector2 second
    )
    {
        if (annotation.Draggable)
        {
            _annotationHits.Add(new AnnotationHit(index, panel, shape, a, b, size, first, second));
        }
    }

    /// <summary>
    /// Maps an annotation's point to a pixel (Issue #382 Decisions 5, 13, 18): each coordinate along its own axis's direction,
    /// a scale's value through the panel's view, a share over the panel; under flip x runs up the screen. A polar point of the
    /// data goes through the polar transform, one of the panel's shares onto its rectangle.
    /// </summary>
    private static Vector2 Map(in AnnotationPoint point, double shiftX, double shiftY, PanelGuide guide, in AnnotationAxes axes)
    {
        if (guide.Polar is { } polar)
        {
            if (point.X.Coord == AnnotationCoord.Panel)
            {
                return new Vector2(
                    (float)(guide.Left + ((point.X.Offset + shiftX) * guide.Width)),
                    (float)(guide.Top + guide.Height - ((point.Y.Offset + shiftY) * guide.Height))
                );
            }

            return polar.Map(Along(axes.X, point.X, shiftX), Along(axes.Y, point.Y, shiftY));
        }

        // Each coordinate's pixel along its axis's side: across the screen for a horizontal axis, down it for a vertical one.
        var x = point.X.Coord switch
        {
            AnnotationCoord.Panel => Share(guide, point.X.Offset + shiftX, horizontal: !axes.Flipped),
            AnnotationCoord.Data2 => Along(axes.X2, point.X, shiftX),
            _ => Along(axes.X, point.X, shiftX),
        };
        var y = point.Y.Coord switch
        {
            AnnotationCoord.Panel => Share(guide, point.Y.Offset + shiftY, horizontal: axes.Flipped),
            AnnotationCoord.Data2 => Along(axes.Y2, point.Y, shiftY),
            _ => Along(axes.Y, point.Y, shiftY),
        };
        return axes.Flipped ? new Vector2((float)y, (float)x) : new Vector2((float)x, (float)y);

        static double Share(PanelGuide guide, double share, bool horizontal) =>
            horizontal ? guide.Left + (share * guide.Width) : guide.Top + guide.Height - (share * guide.Height);

        static double Along(in GeomFrame.Axis axis, in AnnotationValue value, double shift)
        {
            var span = axis.Domain.Upper - axis.Domain.Lower;
            var fraction = (ScaleSpace.Difference(value.Origin, axis.Domain.Origin) + value.Offset + shift - axis.Domain.Lower) / span;
            return axis.Descending ? axis.Offset + axis.Size - (fraction * axis.Size) : axis.Offset + (fraction * axis.Size);
        }
    }

    /// <summary>A panel's axes the annotations are mapped by: the primary and the secondary, and whether x runs up the screen.</summary>
    private readonly record struct AnnotationAxes(GeomFrame.Axis X, GeomFrame.Axis Y, GeomFrame.Axis X2, GeomFrame.Axis Y2, bool Flipped);

    /// <summary>A draggable annotation's shape in pixels (design §6.3): a segment, a box, or a disc, and its points' pixels.</summary>
    private readonly record struct AnnotationHit(
        int Index,
        int Panel,
        HitShape Shape,
        Vector2 A,
        Vector2 B,
        float Size,
        Vector2 First,
        Vector2 Second
    )
    {
        public bool Hits(Vector2 pixel, float radius)
        {
            switch (Shape)
            {
                case HitShape.Line:
                    var along = B - A;
                    var length = along.LengthSquared();
                    var t = length > 0 ? Math.Clamp(Vector2.Dot(pixel - A, along) / length, 0, 1) : 0;
                    var reach = radius + Size;
                    return Vector2.DistanceSquared(pixel, A + (t * along)) <= reach * reach;
                case HitShape.Box:
                    return pixel.X >= A.X - radius && pixel.X <= B.X + radius && pixel.Y >= A.Y - radius && pixel.Y <= B.Y + radius;
                default:
                    var disc = radius + Size;
                    return Vector2.DistanceSquared(pixel, A) <= disc * disc;
            }
        }
    }
}
