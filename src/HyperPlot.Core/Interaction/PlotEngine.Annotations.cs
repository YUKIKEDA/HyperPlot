using System.Numerics;

namespace HyperPlot.Interaction;

/// <content>
/// The annotations a pointer drags (design §6.5, requirement I-5, row I4a, Issue #382): how far each moved, the values it reads,
/// and the notice when one is let go.
/// </content>
public sealed partial class PlotEngine
{
    /// <summary>The radius of an annotation's hit test, in DIPs (Issue #382 Decision 9).</summary>
    private const float AnnotationRadius = 4;

    // How far the pointer moved each plot's annotations, and the spec's annotations they moved: a new node puts one back.
    private AnnotationShift[][] _shifts = [];
    private AnnotationSpec[][] _sources = [];

    // The annotation a drag holds: its plot (−1 for none), number, and panel, where the drag began and the shift then, and the
    // pixels of its points then.
    private int _dragPlot = -1;
    private int _dragIndex;
    private int _dragPanel;
    private Vector2 _dragStart;
    private AnnotationShift _dragShift;
    private Vector2 _dragFirst;
    private Vector2 _dragSecond;

    /// <summary>
    /// Occurs when a drag let go of an annotation it moved (design §6.5, row I4a): once a drag, not while it moves, not for a
    /// cancel. The arguments are the plot's number and the annotation's number in its spec; read the new value with
    /// <see cref="Annotation"/>.
    /// </summary>
    public event Action<int, int>? AnnotationChanged;

    /// <summary>
    /// Returns an annotation as the engine draws it (design §6.5, row I4a): where a drag moved it, else the spec's. A new spec
    /// keeps a moved annotation while its node is the same object; another node puts it back to the spec's value.
    /// </summary>
    /// <param name="plot">The plot: 0 for a plot alone, its number in a figure.</param>
    /// <param name="index">The annotation's number in the plot's spec.</param>
    /// <returns>The annotation: the spec's node itself when it has not moved, else a new one at its place.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The plot is not set, or the number is outside its spec's annotations.</exception>
    /// <exception cref="InvalidOperationException">The call comes from another thread than the engine's.</exception>
    public AnnotationSpec Annotation(int plot, int index)
    {
        CheckThread();
        if (plot < 0 || plot >= _states.Length || (_plot is null && _figure is null))
        {
            throw new ArgumentOutOfRangeException(nameof(plot), plot, "The plot is set.");
        }

        var annotations = SpecOf(plot).Annotations;
        if (index < 0 || index >= annotations.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "The annotation is in the plot's spec.");
        }

        var annotation = annotations[index];
        return ShiftOf(plot, index) is { IsZero: false } shift ? Moved(annotation, shift) : annotation;
    }

    /// <summary>Takes hold of the draggable annotation at a place (row I4a); false when the place has none.</summary>
    internal bool BeginAnnotationDrag(HitArea area, Vector2 pixel)
    {
        if (area.Annotation < 0 || area.Panel.Plot >= PlotCount)
        {
            return false;
        }

        var (plot, panel) = (area.Panel.Plot, area.Panel.Panel);
        if (!PlanOf(plot).AnnotationPixels(area.Annotation, panel, out var first, out var second))
        {
            return false;
        }

        (_dragPlot, _dragIndex, _dragPanel, _dragStart) = (plot, area.Annotation, panel, pixel);
        (_dragShift, _dragFirst, _dragSecond) = (ShiftOf(plot, area.Annotation), first, second);
        return true;
    }

    /// <summary>
    /// Moves the held annotation by the pointer's move since the drag began (design §6.5, Issue #382 Decisions 9 and 18): a
    /// scale's coordinate by the change of the value under its point, a panel's share by the move over the panel's length
    /// along the axis's direction. The plot is drawn again whole (design §5.4).
    /// </summary>
    internal void DragAnnotation(Vector2 pixel)
    {
        if (_dragPlot < 0 || _dragPlot >= PlotCount || _dragIndex >= SpecOf(_dragPlot).Annotations.Count)
        {
            return;
        }

        var annotation = SpecOf(_dragPlot).Annotations[_dragIndex];
        var (first, second) = annotation switch
        {
            LineAnnotationSpec line => (line.From, line.To),
            RectAnnotationSpec rect => (rect.Corner, rect.Opposite),
            TextAnnotationSpec text => (text.At, text.At),
            MarkerAnnotationSpec marker => (marker.At, marker.At),
            _ => (default(AnnotationPoint), default(AnnotationPoint)),
        };
        var move = pixel - _dragStart;
        var panel = new PanelId(_dragPlot, _dragPanel);
        var (x0, y0) = Change(panel, first, _dragFirst, move);
        var (x1, y1) = Change(panel, second, _dragSecond, move);
        var shift = new AnnotationShift(_dragShift.X0 + x0, _dragShift.Y0 + y0, _dragShift.X1 + x1, _dragShift.Y1 + y1);
        if (shift == ShiftOf(_dragPlot, _dragIndex))
        {
            return;
        }

        SetShift(_dragPlot, _dragIndex, shift);
    }

    /// <summary>Lets the held annotation go: keeps where it is and tells it when it moved, or puts it back (row I4a).</summary>
    internal void EndAnnotationDrag(bool keep)
    {
        if (_dragPlot < 0)
        {
            return;
        }

        var (plot, index) = (_dragPlot, _dragIndex);
        _dragPlot = -1;
        if (plot >= _shifts.Length)
        {
            return;
        }

        if (!keep)
        {
            SetShift(plot, index, _dragShift);
            return;
        }

        if (ShiftOf(plot, index) != _dragShift)
        {
            AnnotationChanged?.Invoke(plot, index);
        }
    }

    /// <summary>Returns an annotation's node at its moved place: each point's coordinates plus the shift.</summary>
    private static AnnotationSpec Moved(AnnotationSpec annotation, AnnotationShift shift) =>
        annotation switch
        {
            LineAnnotationSpec line => line with { From = Move(line.From, shift.X0, shift.Y0), To = Move(line.To, shift.X1, shift.Y1) },
            RectAnnotationSpec rect => rect with
            {
                Corner = Move(rect.Corner, shift.X0, shift.Y0),
                Opposite = Move(rect.Opposite, shift.X1, shift.Y1),
            },
            TextAnnotationSpec text => text with { At = Move(text.At, shift.X0, shift.Y0) },
            MarkerAnnotationSpec marker => marker with { At = Move(marker.At, shift.X0, shift.Y0) },
            _ => annotation,
        };

    private static AnnotationPoint Move(in AnnotationPoint point, double x, double y) =>
        new(point.X with { Offset = point.X.Offset + x }, point.Y with { Offset = point.Y.Offset + y });

    private AnnotationShift ShiftOf(int plot, int index) =>
        plot < _shifts.Length && _shifts[plot] is { } shifts && index < shifts.Length ? shifts[index] : default;

    private void SetShift(int plot, int index, AnnotationShift shift)
    {
        KeepAnnotationShifts();
        if (plot >= _shifts.Length || index >= _shifts[plot].Length)
        {
            return;
        }

        _shifts[plot][index] = shift;

        // The kept plot holds the old place: the next frame draws the plot again (design §5.4, Issue #382 Decision 8).
        _kept = false;
        RequestFrame();
    }

    /// <summary>
    /// Returns how a point's coordinates change for a move of the pointer (Issue #382 Decisions 9 and 18): a scale's value by
    /// the difference of the values under the point's pixel before and after; a share by the move over the panel along the
    /// axis's direction on the screen (x runs up it under flip).
    /// </summary>
    private (double X, double Y) Change(PanelId panel, in AnnotationPoint point, Vector2 at, Vector2 move)
    {
        var guide = PanelOf(panel);
        var flipped = guide.Polar is null && PlanOf(panel.Plot).Guides.Flipped;
        var (across, up) = (move.X / guide.Width, -move.Y / guide.Height);
        var (before, after) = (PixelToView(panel, at), PixelToView(panel, at + move));
        var x = point.X.Coord switch
        {
            AnnotationCoord.Panel => flipped ? up : across,
            AnnotationCoord.Data2 => Secondary(panel, at, move, ofX: true),
            _ => Difference(before.X, after.X),
        };
        var y = point.Y.Coord switch
        {
            AnnotationCoord.Panel => flipped ? across : up,
            AnnotationCoord.Data2 => Secondary(panel, at, move, ofX: false),
            _ => Difference(before.Y, after.Y),
        };
        return (x, y);

        static double Difference(in ViewRange before, in ViewRange after) =>
            ScaleSpace.Difference(after.Origin, before.Origin) + after.Lower - before.Lower;
    }

    /// <summary>Returns the change of a secondary scale's value under a pixel for a move (row G6a's scales).</summary>
    private double Secondary(PanelId panel, Vector2 at, Vector2 move, bool ofX)
    {
        var shown = Resolve(panel.Plot, panel.Panel, _states[panel.Plot].Shown[panel.Panel]);
        var (before, after) = (Fractions(panel, at, secondary: true), Fractions(panel, at + move, secondary: true));
        return ofX ? (after.X - before.X) * (shown.X2.Upper - shown.X2.Lower) : (after.Y - before.Y) * (shown.Y2.Upper - shown.Y2.Lower);
    }

    /// <summary>
    /// Keeps each plot's shifts beside its spec's annotations, and gives them to the plans (Issue #382 Decision 10): a node that
    /// is not the one moved puts its annotation back. Allocates only for a new number of annotations.
    /// </summary>
    private void KeepAnnotationShifts()
    {
        if (_plot is null && _figure is null)
        {
            return;
        }

        for (var k = 0; k < _states.Length && k < _shifts.Length; k++)
        {
            var annotations = SpecOf(k).Annotations;
            if (_shifts[k] is not { } shifts || shifts.Length != annotations.Count)
            {
                var kept = new AnnotationShift[annotations.Count];
                var sources = new AnnotationSpec[annotations.Count];
                for (var i = 0; i < annotations.Count; i++)
                {
                    sources[i] = annotations[i];
                    if (_shifts[k] is { } old && i < old.Length && ReferenceEquals(_sources[k][i], annotations[i]))
                    {
                        kept[i] = old[i];
                    }
                }

                (_shifts[k], _sources[k]) = (kept, sources);
            }
            else
            {
                for (var i = 0; i < annotations.Count; i++)
                {
                    if (!ReferenceEquals(_sources[k][i], annotations[i]))
                    {
                        (_sources[k][i], shifts[i]) = (annotations[i], default);
                    }
                }
            }

            if (k < PlotCount)
            {
                PlanOf(k).SetAnnotationShifts(_shifts[k]);
            }
        }
    }
}
