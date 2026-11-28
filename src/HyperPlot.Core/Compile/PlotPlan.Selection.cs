using System.Numerics;
using HyperPlot.Interaction;
using HyperPlot.Rendering;

namespace HyperPlot;

/// <content>
/// The plot's selection as its point layers highlight it in the data layer (design §6.4 "散布の選択", row R3c, Issue #471
/// Decisions 6, 7, and 12): the region of the selection's predicate on each panel it stands on, which the layers' records
/// name, and whether the plot has a layer that draws it.
/// </content>
internal sealed partial class PlotPlan
{
    private Selection2D? _selection;
    private bool[] _selects = [];

    // Whether the selection or its panels changed since the last recording, which then records again (row R3c).
    private bool _selectionChanged;

    /// <summary>
    /// Gets a value indicating whether a layer of the plot highlights a selection where it draws (row R3c): a point layer, whose
    /// data layer is drawn again when the selection changes, not copied (design §5.4 "残し方と見分け方").
    /// </summary>
    internal bool HighlightsSelection
    {
        get
        {
            foreach (var panel in _panels)
            {
                foreach (var layer in panel.Layers)
                {
                    if (layer.HighlightsSelection)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Sets the plot's selection the next recording highlights (row R3c): on the panel it was made on and on the panels a link
    /// stands it on too (row I3b). A selection of points is highlighted by the overlay's markers, not here. Another selection
    /// makes the next frame record again, even with nothing else changed.
    /// </summary>
    /// <param name="selection">The selection, or null.</param>
    /// <param name="applied">For each panel, whether a link stands the selection on it too.</param>
    internal void SetSelection(Selection2D? selection, ReadOnlySpan<bool> applied)
    {
        selection = selection is { Kind: not SelectionKind.Points } ? selection : null;
        if (_selects.Length < applied.Length)
        {
            _selects = new bool[applied.Length];
        }

        var same =
            ReferenceEquals(selection, _selection)
            && applied.SequenceEqual(_selects.AsSpan(0, applied.Length))
            && !_selects.AsSpan(applied.Length).Contains(true);
        if (same)
        {
            return;
        }

        _selection = selection;
        Array.Clear(_selects);
        applied.CopyTo(_selects);
        _selectionChanged = HighlightsSelection;
    }

    /// <summary>
    /// Adds to the list the region of the selection on a panel, if it stands there (design §6.4 "生の点の強調", row R3c): a
    /// rectangle's or an x range's box of pixels, or a lasso's mask of the panel's pixels whose centers its polygon holds by
    /// the even-odd rule. Returns its number, or 0. Allocates nothing once the list's buffers have grown.
    /// </summary>
    private int AddHighlight(int panel, in GeomFrame.Axis x, in GeomFrame.Axis y, bool flipped, (int Left, int Top, int Width, int Height) rect)
    {
        if (_selection is not { } selection || (panel != selection.Panel.Panel && !(panel < _selects.Length && _selects[panel])))
        {
            return 0;
        }

        if (selection.Kind != SelectionKind.Lasso)
        {
            var (along0, along1) = (Pixel(x, selection.X.Origin, selection.X.Lower), Pixel(x, selection.X.Origin, selection.X.Upper));
            var (across0, across1) =
                selection.Kind == SelectionKind.XRange
                    ? (float.MinValue, float.MaxValue)
                    : (Pixel(y, selection.Y.Origin, selection.Y.Lower), Pixel(y, selection.Y.Origin, selection.Y.Upper));
            var (alongLow, alongHigh) = (MathF.Min(along0, along1), MathF.Max(along0, along1));
            var (acrossLow, acrossHigh) = (MathF.Min(across0, across1), MathF.Max(across0, across1));
            return flipped
                ? _list.AddHighlight(acrossLow, alongLow, acrossHigh, alongHigh)
                : _list.AddHighlight(alongLow, acrossLow, alongHigh, acrossHigh);
        }

        // The polygon's vertices on the target, from their shares of the ranges.
        var shares = selection.Polygon.Span;
        Span<Vector2> pixels = stackalloc Vector2[shares.Length];
        var (spanX, spanY) = (selection.X.Upper - selection.X.Lower, selection.Y.Upper - selection.Y.Lower);
        for (var i = 0; i < shares.Length; i++)
        {
            var along = Pixel(x, selection.X.Origin, selection.X.Lower + (shares[i].X * spanX));
            var across = Pixel(y, selection.Y.Origin, selection.Y.Lower + (shares[i].Y * spanY));
            pixels[i] = flipped ? new Vector2(across, along) : new Vector2(along, across);
        }

        // The mask's box is the panel's clip (the caller's rect), which RenderList.DrawRectCutByMask checks.
        var number = _list.AddHighlight(rect.Left, rect.Top, rect.Width, rect.Height, out var mask);
        var stride = (rect.Width / 32) + 1;
        Span<float> crossings = stackalloc float[shares.Length];
        for (var row = 0; row < rect.Height; row++)
        {
            // The edges the row's centers' line crosses, in order: a pixel whose center lies between the 2k-th and the next is in.
            var (center, n) = (rect.Top + row + 0.5f, 0);
            for (int i = 0, j = pixels.Length - 1; i < pixels.Length; j = i++)
            {
                var (a, b) = (pixels[i], pixels[j]);
                if ((a.Y > center) != (b.Y > center))
                {
                    crossings[n++] = a.X + ((center - a.Y) * (b.X - a.X) / (b.Y - a.Y));
                }
            }

            SpanSort.Ascending(crossings[..n]);
            var words = mask.Slice(row * stride, stride);
            for (var c = 0; c + 1 < n; c += 2)
            {
                // The columns whose centers lie in [from, to): column k's center is rect.Left + k + 0.5.
                var first = (int)Math.Clamp(MathF.Ceiling(crossings[c] - rect.Left - 0.5f), 0, rect.Width);
                var end = (int)Math.Clamp(MathF.Ceiling(crossings[c + 1] - rect.Left - 0.5f), 0, rect.Width);
                SetColumns(words, first, end);
            }
        }

        return number;

        // The pixel along an axis of a difference of the scale space from an origin.
        static float Pixel(in GeomFrame.Axis axis, long origin, double difference)
        {
            var d = (double)(origin - axis.Domain.Origin) + difference;
            var t = (d - axis.Domain.Lower) / (axis.Domain.Upper - axis.Domain.Lower);
            return (float)(axis.Offset + ((axis.Descending ? 1 - t : t) * axis.Size));
        }
    }

    /// <summary>
    /// Sets the bits of a lasso mask's row for the columns from one to another, a word at a time (Issue #591): the two end
    /// words once each, under a mask of their columns, and every word between them whole. Bit <c>k &amp; 31</c> of word
    /// <c>k &gt;&gt; 5</c> is column k. Leaves the row as it is when the run is empty.
    /// </summary>
    /// <remarks>
    /// The caller keeps a run that is not empty inside the row: <c>0 &lt;= first</c> and <c>end &lt;= 32 × words.Length</c>.
    /// The lasso clamps both to the panel's width. The method does not check them, and a run outside the row throws.
    /// </remarks>
    /// <param name="words">The row's words.</param>
    /// <param name="first">The first column of the run.</param>
    /// <param name="end">The column after the run's last.</param>
    internal static void SetColumns(Span<uint> words, int first, int end)
    {
        if (first >= end)
        {
            return;
        }

        var (low, high) = (first >> 5, (end - 1) >> 5);
        var head = uint.MaxValue << (first & 31);
        var tail = uint.MaxValue >> (31 - ((end - 1) & 31));
        if (low == high)
        {
            words[low] |= head & tail;
            return;
        }

        words[low] |= head;
        words[(low + 1)..high].Fill(uint.MaxValue);
        words[high] |= tail;
    }
}
