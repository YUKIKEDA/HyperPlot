using HyperPlot.Interaction;

namespace HyperPlot;

/// <summary>What a layer's element is to a selection (design §6.4 "行の集合", row I3a): a point, a vertical span, a box.</summary>
internal enum ShapeKind
{
    /// <summary>A point (x, y): a line's, a step's, an area's row.</summary>
    Point,

    /// <summary>A vertical span at x, from a low end to a high end: a band's row, an error bar, a bar from its base.</summary>
    Span,

    /// <summary>A box, from four columns: a rectangle.</summary>
    Box,
}

/// <summary>
/// A layer's elements as a selection reads them (row I3a): their kind, their columns, the rows, and whether the x does not
/// decrease (an x range then answers with a run of rows). A null <see cref="Low"/> of a span is <see cref="Baseline"/>; a
/// categorical <see cref="X"/> stands at its band's middle.
/// </summary>
internal sealed record SelectionShape(
    ShapeKind Kind,
    Column X,
    Column? Low,
    Column High,
    RowRange Rows,
    bool Sorted,
    double Baseline = 0,
    Column? X1 = null,
    Column? Y1 = null
);

/// <summary>
/// One selection's question to a panel's layers (design §6.4, row I3a, Issue #364 Decisions 4, 9, 10): the predicate, the
/// layer's axes, and whether its rows are a derived table's. An x range over a layer whose x does not decrease answers with
/// a run of rows found by search; the rest answer with a bit a row the workers set, a chunk a turn.
/// </summary>
internal sealed class SelectionQuery(Selection2D selection, GeomFrame.Axis x, GeomFrame.Axis y, PlotWorkerPool pool)
{
    public Selection2D Selection => selection;

    public GeomFrame.Axis X => x;

    public GeomFrame.Axis Y => y;

    public PlotWorkerPool Pool => pool;

    /// <summary>Gets or sets a value indicating whether the rows asked are a derived table's: a stat's or a stack's.</summary>
    public bool Derived { get; set; }

    /// <summary>Returns the rows of a layer's elements the selection holds.</summary>
    public async ValueTask<RowSelection> Select(SelectionShape shape)
    {
        var length = Math.Min(shape.X.Length, shape.High.Length);
        var (start, end) = (Math.Min(shape.Rows.Start, length), shape.Rows.EndIn(length));
        if (start >= end)
        {
            return RowSelection.Empty;
        }

        if (selection.Kind == SelectionKind.XRange && shape.Sorted && shape.X is not CategoricalColumn)
        {
            // A run found by search (Issue #364 Decision 4): no bit a row.
            var rows = new RowRange(start, end);
            var (low, high) = (Boundary(selection.X.Lower), Boundary(selection.X.Upper));
            var first = shape.X.LowerBound(low.Difference, low.Origin, rows);
            var last = shape.X.LowerBound(Math.BitIncrement(high.Difference), high.Origin, rows);
            return last > first ? RowSelection.OfRanges([first], [last], Derived) : RowSelection.Empty;
        }

        var bits = new ulong[(end + 63) / 64];
        var chunks = (int)((end + Column.ChunkLength - 1) / Column.ChunkLength);
        await pool.RunChunks(
                chunks,
                chunk =>
                {
                    var from = Math.Max(start, (long)chunk << Column.ChunkShift);
                    var to = Math.Min(end, ((long)chunk + 1) << Column.ChunkShift);
                    for (var row = from; row < to; row++)
                    {
                        if (Holds(shape, row))
                        {
                            bits[row >> 6] |= 1UL << (int)(row & 63);
                        }
                    }
                }
            )
            .ConfigureAwait(false);
        return RowSelection.OfBits(bits, end, Derived);
    }

    /// <summary>Returns whether a vertical span of the data, at an x from one y to another, meets the selection (row I3a).</summary>
    public bool SpanHolds(double dataX, double low, double high)
    {
        var (dx, a, b) = (OfX(dataX), OfY(low), OfY(high));
        return Meets(dx, dx, Math.Min(a, b), Math.Max(a, b));
    }

    /// <summary>Returns whether a point of the data lies in the selection, both coordinates in data units.</summary>
    public bool HoldsData(double dataX, double dataY) => selection.Holds(OfX(dataX), OfY(dataY));

    /// <summary>
    /// Returns the union of the rows several parts answered, each part's rows taken back through a gather (a split's or a
    /// facet's part's rows to the layer's, all of them in one pass, row R3c), or as they are when it has none (row I3a): a
    /// facet's panels a link stands a selection on too (row I3b). Derived when the argument says so or a part is.
    /// </summary>
    public static async ValueTask<RowSelection> Union(List<(Task<RowSelection> Rows, (ISourceRows Gather, int Key)? Map)> parts, bool derived)
    {
        var rows = new List<long>();
        foreach (var (pending, map) in parts)
        {
            var part = await pending.ConfigureAwait(false);
            derived |= part.Derived;
            var first = rows.Count;
            foreach (var (start, end) in part.Ranges())
            {
                for (var row = start; row < end; row++)
                {
                    rows.Add(row);
                }
            }

            if (map is not { } gather)
            {
                continue;
            }

            // The part's rows in increasing order, as the ranges give them, each replaced by its source; −1 is none.
            var mapped = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(rows)[first..];
            gather.Gather.SourceRows(gather.Key, mapped.ToArray(), mapped);
            rows.RemoveAll(row => row < 0);
        }

        return RowSelection.OfRows(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(rows), derived);
    }

    /// <summary>Returns whether one element lies in the selection.</summary>
    private bool Holds(SelectionShape shape, long row)
    {
        var dx = shape.X is CategoricalColumn categories ? Category(categories.GetCode(row)) : OfX(shape.X, row);
        if (double.IsNaN(dx))
        {
            return false;
        }

        switch (shape.Kind)
        {
            case ShapeKind.Point:
                var dy = OfY(shape.High, row);
                return !double.IsNaN(dy) && selection.Holds(dx, dy);
            case ShapeKind.Span:
                var (a, b) = (shape.Low is { } low ? OfY(low, row) : OfY(shape.Baseline), OfY(shape.High, row));
                return !double.IsNaN(a) && !double.IsNaN(b) && Meets(dx, dx, Math.Min(a, b), Math.Max(a, b));
            default:
                var (x0, x1) = (dx, OfX(shape.X1!, row));
                var (y0, y1) = (OfY(shape.High, row), OfY(shape.Y1!, row));
                return !double.IsNaN(x1)
                    && !double.IsNaN(y0)
                    && !double.IsNaN(y1)
                    && Meets(Math.Min(x0, x1), Math.Max(x0, x1), Math.Min(y0, y1), Math.Max(y0, y1));
        }
    }

    /// <summary>
    /// Returns whether a box of the scale space meets the selection: a rectangle or an x range by overlapping it (an x range
    /// by x alone), a lasso by holding its middle.
    /// </summary>
    private bool Meets(double x0, double x1, double y0, double y1)
    {
        if (selection.Kind == SelectionKind.Lasso)
        {
            return selection.Holds((x0 + x1) / 2, (y0 + y1) / 2);
        }

        var alongX = x1 >= selection.X.Lower && x0 <= selection.X.Upper;
        return alongX && (selection.Kind == SelectionKind.XRange || (y1 >= selection.Y.Lower && y0 <= selection.Y.Upper));
    }

    /// <summary>Returns a band's middle as a difference from the selection's x origin, or NaN for a missing category.</summary>
    private double Category(int code) =>
        code < 0 || x.Band is not { } band ? double.NaN : band.Position(code) + (band.Width / 2) - selection.X.Origin;

    private double OfX(Column column, long row) => Scaled(x, column, row, selection.X.Origin);

    private double OfY(Column column, long row) => Scaled(y, column, row, selection.Y.Origin);

    private double OfX(double value) => Scaled(x, value, selection.X.Origin);

    private double OfY(double value) => Scaled(y, value, selection.Y.Origin);

    /// <summary>Returns a column's value as a difference from an origin of the scale space.</summary>
    private static double Scaled(in GeomFrame.Axis axis, Column column, long row, long origin) =>
        axis.Transform is null ? column.DifferenceAt(row, origin) : Scaled(axis, column.DifferenceAt(row, 0), origin);

    private static double Scaled(in GeomFrame.Axis axis, double value, long origin)
    {
        if (axis.Transform is not { } transform)
        {
            return value - origin;
        }

        Span<double> one = [value];
        transform.Forward(one, one);
        return one[0] - origin;
    }

    /// <summary>Returns an end of the selection's x range as a column's search takes it: an origin and a difference in data units.</summary>
    private (long Origin, double Difference) Boundary(double difference)
    {
        if (x.Transform is not { } transform)
        {
            return (selection.X.Origin, difference);
        }

        Span<double> one = [selection.X.Origin + difference];
        transform.Inverse(one, one);
        return (0, one[0]);
    }
}
