using System.Buffers;
using HyperPlot.Interaction;

namespace HyperPlot;

/// <summary>
/// The rows of a point layer on the index path that a selection holds, found through the index (design §6.4 "行の集合", row
/// R3c, Issue #471 Decision 4): the cells that touch the rectangle around the predicate, each read whole, read point by point,
/// or passed over. A rectangle's or an x range's cell whose grid cells lie off the predicate's boundary sets its rows without
/// reading their values; a lasso's cell sets them when the polygon misses its box and holds its middle, and is passed over
/// when the polygon misses it and does not hold its middle. The other cells' points are each checked by their values.
/// </summary>
internal static class SpatialSelection
{
    // The points a worker's piece reads at most: a large cell is read in pieces of this many.
    private const int PiecePoints = 1 << 16;

    /// <summary>What a cell is to a selection.</summary>
    private enum Cover
    {
        /// <summary>Every point of the cell lies in the selection.</summary>
        Inside,

        /// <summary>No point of the cell lies in the selection.</summary>
        Outside,

        /// <summary>The cell's points are each checked.</summary>
        Straddles,
    }

    /// <summary>
    /// Returns the rows of a full snapshot's points that a selection holds, as rows of the columns the index reads; the work
    /// runs on the pool's workers. The caller holds a reference to the snapshot until the task ends.
    /// </summary>
    /// <param name="snapshot">The full snapshot.</param>
    /// <param name="selection">A rectangle, an x range, or a lasso of the panel's scale space, the index's space.</param>
    /// <param name="length">The rows of the columns: the set's bits run over [0, length).</param>
    /// <param name="pool">The pool.</param>
    public static async ValueTask<RowSelection> Select(SpatialSnapshot snapshot, Selection2D selection, long length, PlotWorkerPool pool)
    {
        // A snapshot of no point has no range to judge a cell by.
        if (snapshot.PointCount == 0)
        {
            return RowSelection.Empty;
        }

        var test = new Test(snapshot, selection);
        var cells = Array.Empty<SpatialCell>();
        await pool.RunChunks(1, _ => cells = Cells(snapshot, test.Rect)).ConfigureAwait(false);

        // A piece is a cell's run of points, at most PiecePoints long.
        var pieces = new List<(int Cell, long First, int Count)>();
        for (var c = 0; c < cells.Length; c++)
        {
            for (var first = cells[c].First; first < cells[c].First + cells[c].Count; first += PiecePoints)
            {
                pieces.Add((c, first, (int)Math.Min(PiecePoints, cells[c].First + cells[c].Count - first)));
            }
        }

        var bits = new ulong[(length + 63) / 64];
        await pool.RunChunks(pieces.Count, p => Read(snapshot, test, cells[pieces[p].Cell], pieces[p].First, pieces[p].Count, bits))
            .ConfigureAwait(false);
        return RowSelection.OfBits(bits, length, derived: false);
    }

    /// <summary>Returns how many of the cells the rectangle around a selection touches lie inside it, outside it, and across its boundary. Tests only.</summary>
    internal static (int Inside, int Outside, int Straddles) Covers(SpatialSnapshot snapshot, Selection2D selection)
    {
        var test = new Test(snapshot, selection);
        var (inside, outside, straddles) = (0, 0, 0);
        foreach (var cell in Cells(snapshot, test.Rect))
        {
            switch (test.Of(cell))
            {
                case Cover.Inside:
                    inside++;
                    break;
                case Cover.Outside:
                    outside++;
                    break;
                default:
                    straddles++;
                    break;
            }
        }

        return (inside, outside, straddles);
    }

    /// <summary>Returns every cell that holds a point and touches the rectangle, down to the leaves.</summary>
    private static SpatialCell[] Cells(SpatialSnapshot snapshot, in SpatialRect rect)
    {
        if (!(rect.XMin <= rect.XMax) || !(rect.YMin <= rect.YMax))
        {
            return [];
        }

        var cells = new SpatialCell[256];
        var found = snapshot.GetCells(rect, SpatialSnapshot.DeepestLevel, cells);
        if (found > cells.Length)
        {
            cells = new SpatialCell[found];
            found = snapshot.GetCells(rect, SpatialSnapshot.DeepestLevel, cells);
        }

        return cells[..found];
    }

    /// <summary>Sets the bits of the rows of a piece of a cell that the selection holds.</summary>
    private static void Read(SpatialSnapshot snapshot, Test test, in SpatialCell cell, long first, int count, ulong[] bits)
    {
        var cover = test.Of(cell);
        if (cover == Cover.Outside)
        {
            return;
        }

        var rows = ArrayPool<long>.Shared.Rent(count);
        var xs = cover == Cover.Straddles ? ArrayPool<double>.Shared.Rent(count) : null;
        var ys = cover == Cover.Straddles ? ArrayPool<double>.Shared.Rent(count) : null;
        try
        {
            if (xs is null || ys is null)
            {
                snapshot.ReadRows(first, rows.AsSpan(0, count));
                for (var i = 0; i < count; i++)
                {
                    Set(bits, rows[i]);
                }

                return;
            }

            snapshot.ReadPoints(first, xs.AsSpan(0, count), ys.AsSpan(0, count), rows.AsSpan(0, count));
            for (var i = 0; i < count; i++)
            {
                if (test.Holds(xs[i], ys[i]))
                {
                    Set(bits, rows[i]);
                }
            }
        }
        finally
        {
            ArrayPool<long>.Shared.Return(rows);
            if (xs is not null)
            {
                ArrayPool<double>.Shared.Return(xs);
            }

            if (ys is not null)
            {
                ArrayPool<double>.Shared.Return(ys);
            }
        }
    }

    // The rows of two cells may share a word, and the pieces run on several workers.
    private static void Set(ulong[] bits, long row) => Interlocked.Or(ref bits[row >> 6], 1UL << (int)(row & 63));

    /// <summary>The selection as the snapshot's cells and points meet it.</summary>
    private sealed class Test
    {
        private readonly SpatialSnapshot _snapshot;
        private readonly Selection2D _selection;
        private readonly double _shiftX;
        private readonly double _shiftY;

        // The grid cells of the rectangle's ends, and whether an end lies outside the snapshot's range, where it bounds nothing.
        private readonly uint _lowX;
        private readonly uint _highX;
        private readonly uint _lowY;
        private readonly uint _highY;
        private readonly bool _openLowX;
        private readonly bool _openHighX;
        private readonly bool _openLowY;
        private readonly bool _openHighY;

        // A lasso's vertices, as differences from the snapshot's origins; and a grid cell's extent, the margin of a cell's box.
        private readonly (double X, double Y)[] _polygon = [];
        private readonly double _unitX;
        private readonly double _unitY;

        public Test(SpatialSnapshot snapshot, Selection2D selection)
        {
            // A point's difference from the selection's origins is its difference from the snapshot's plus the shifts.
            var shiftX = (double)(snapshot.XRange.Origin - selection.X.Origin);
            var shiftY = (double)(snapshot.YRange.Origin - selection.Y.Origin);
            (_snapshot, _selection, _shiftX, _shiftY) = (snapshot, selection, shiftX, shiftY);
            var (x, y) = (snapshot.XRange, snapshot.YRange);
            var xRange = selection.Kind == SelectionKind.XRange;
            var rect = new SpatialRect(
                selection.X.Lower - shiftX,
                selection.X.Upper - shiftX,
                xRange ? y.Lower : selection.Y.Lower - shiftY,
                xRange ? y.Upper : selection.Y.Upper - shiftY
            );
            Rect = rect;
            (_openLowX, _openHighX) = (rect.XMin < x.Lower, rect.XMax > x.Upper);
            (_openLowY, _openHighY) = (
                selection.Kind == SelectionKind.XRange || rect.YMin < y.Lower,
                selection.Kind == SelectionKind.XRange || rect.YMax > y.Upper
            );
            (_lowX, _lowY) = snapshot.GridAt(Math.Clamp(rect.XMin, x.Lower, x.Upper), Math.Clamp(rect.YMin, y.Lower, y.Upper));
            (_highX, _highY) = snapshot.GridAt(Math.Clamp(rect.XMax, x.Lower, x.Upper), Math.Clamp(rect.YMax, y.Lower, y.Upper));
            (_, _, _unitX, _unitY) = snapshot.CellBox(0, SpatialSnapshot.DeepestLevel);
            if (selection.Kind != SelectionKind.Lasso)
            {
                return;
            }

            var shares = selection.Polygon.Span;
            _polygon = new (double, double)[shares.Length];
            var (spanX, spanY) = (selection.X.Upper - selection.X.Lower, selection.Y.Upper - selection.Y.Lower);
            for (var i = 0; i < shares.Length; i++)
            {
                _polygon[i] = (selection.X.Lower + (shares[i].X * spanX) - shiftX, selection.Y.Lower + (shares[i].Y * spanY) - shiftY);
            }
        }

        /// <summary>Gets the rectangle around the predicate, as differences from the snapshot's origins: an x range's spans the snapshot's y.</summary>
        public SpatialRect Rect { get; }

        /// <summary>Returns whether a point, as differences from the snapshot's origins, lies in the selection: by its values.</summary>
        public bool Holds(double x, double y) => _selection.Holds(x + _shiftX, y + _shiftY);

        /// <summary>Returns what a cell is to the selection.</summary>
        public Cover Of(in SpatialCell cell)
        {
            if (_selection.Kind != SelectionKind.Lasso)
            {
                // The grid cells of the cell's points, from its first to its last on each axis: a point in a grid cell past an
                // end's lies past that end.
                var shift = SpatialSnapshot.DeepestLevel - cell.Level;
                var (fromX, fromY) = ((ulong)SpatialCode.Axis2(cell.Code, 0) << shift, (ulong)SpatialCode.Axis2(cell.Code, 1) << shift);
                var (toX, toY) = (fromX + (1UL << shift) - 1, fromY + (1UL << shift) - 1);
                var inside =
                    (_openLowX || fromX > _lowX) && (_openHighX || toX < _highX) && (_openLowY || fromY > _lowY) && (_openHighY || toY < _highY);
                return inside ? Cover.Inside : Cover.Straddles;
            }

            // A lasso: the cell's box, widened by a grid cell for the quantization, against the polygon's edges.
            var (cornerX, cornerY, extentX, extentY) = _snapshot.CellBox(cell.Code, cell.Level);
            var (x0, x1, y0, y1) = (cornerX - _unitX, cornerX + extentX + _unitX, cornerY - _unitY, cornerY + extentY + _unitY);
            for (int i = 0, j = _polygon.Length - 1; i < _polygon.Length; j = i++)
            {
                if (Crosses(_polygon[j], _polygon[i], x0, x1, y0, y1))
                {
                    return Cover.Straddles;
                }
            }

            return Holds(cornerX + (extentX / 2), cornerY + (extentY / 2)) ? Cover.Inside : Cover.Outside;
        }

        /// <summary>Returns whether a segment has a point in a box (Liang–Barsky).</summary>
        private static bool Crosses((double X, double Y) a, (double X, double Y) b, double x0, double x1, double y0, double y1)
        {
            var (dx, dy) = (b.X - a.X, b.Y - a.Y);
            var (enter, leave) = (0.0, 1.0);
            return Clip(-dx, a.X - x0, ref enter, ref leave)
                && Clip(dx, x1 - a.X, ref enter, ref leave)
                && Clip(-dy, a.Y - y0, ref enter, ref leave)
                && Clip(dy, y1 - a.Y, ref enter, ref leave);

            static bool Clip(double p, double q, ref double enter, ref double leave)
            {
                if (p == 0)
                {
                    return q >= 0;
                }

                var t = q / p;
                if (p < 0)
                {
                    enter = Math.Max(enter, t);
                }
                else
                {
                    leave = Math.Min(leave, t);
                }

                return enter <= leave;
            }
        }
    }
}
