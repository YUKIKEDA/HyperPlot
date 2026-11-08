using System.Numerics;

namespace HyperPlot;

/// <summary>A column that a pyramid can be built over: a <see cref="Column{T}"/>, the Y of a line layer (design §4.8).</summary>
internal interface IPyramidColumn
{
    /// <summary>Starts building a pyramid over the column.</summary>
    /// <param name="pool">The pool that builds it.</param>
    /// <returns>The pyramid, built in the background.</returns>
    PlotPyramid BuildPlotPyramid(PlotWorkerPool pool);
}

/// <summary>The pyramid over one Y column of a compiled plot, without its element type (design §4.8).</summary>
internal abstract class PlotPyramid : IDisposable
{
    /// <summary>Gets the column the pyramid is built over.</summary>
    public abstract Column Column { get; }

    /// <summary>Gets a value indicating whether the pyramid was disposed. Tests only.</summary>
    public abstract bool IsDisposed { get; }

    /// <summary>Returns a task that ends when every chunk is built for the column's current values (design §3.3).</summary>
    public abstract Task WhenComplete();

    /// <summary>Gets the first exception a chunk's build threw, or null.</summary>
    public abstract Exception? Fault { get; }

    /// <summary>
    /// Returns whether every chunk is built for the column's current values, without waiting (row W1b1); otherwise requests
    /// the stale chunks again. Does not allocate.
    /// </summary>
    public abstract bool RequestStale();

    /// <summary>Adds a series over this pyramid, with <paramref name="x"/> as its X, to a batch.</summary>
    public abstract int AddTo(M4Batch batch, Column x, M4Point[] destination, RowRange rows);

    /// <summary>Gets the least and the greatest value of a range of rows (design §3.3, row I1a: the y of the visible x).</summary>
    public abstract bool TryRange(long start, long length, out double min, out double max);

    /// <summary>
    /// Returns the first (or last) row of [start, end) whose value is a number, −1 when there is none, or
    /// <see cref="PyramidSearch.Unknown"/> when a chunk on the way is not built (row I2a); sets <paramref name="provisional"/>
    /// then. Does not allocate.
    /// </summary>
    public abstract long FindNumber(long start, long end, bool fromEnd, ref bool provisional);

    /// <summary>
    /// Gets the least and the greatest value of [start, end) and the rows of their first occurrences (row I2a); false when the
    /// range has no number. Sets <paramref name="provisional"/> when a chunk is not built. Does not allocate.
    /// </summary>
    public abstract bool TryExtremes(long start, long end, out double min, out long minRow, out double max, out long maxRow, ref bool provisional);

    /// <inheritdoc />
    public abstract void Dispose();
}

/// <summary>The pyramid over one Y column of a compiled plot.</summary>
/// <typeparam name="T">The element type of the column.</typeparam>
internal sealed class PlotPyramid<T>(MinMaxPyramid<T> pyramid) : PlotPyramid
    where T : unmanaged, INumber<T>
{
    /// <inheritdoc />
    public override Column Column => pyramid.Column;

    /// <summary>Gets the pyramid. Tests only.</summary>
    public MinMaxPyramid<T> Pyramid => pyramid;

    /// <inheritdoc />
    public override bool IsDisposed => pyramid.Work.IsDisposed;

    /// <inheritdoc />
    public override Task WhenComplete() => pyramid.WhenComplete();

    /// <inheritdoc />
    public override Exception? Fault => pyramid.Fault;

    /// <inheritdoc />
    public override bool RequestStale() => pyramid.RequestStale();

    /// <inheritdoc />
    public override int AddTo(M4Batch batch, Column x, M4Point[] destination, RowRange rows) => x.AddSeries(batch, pyramid, destination, rows);

    /// <inheritdoc />
    public override bool TryRange(long start, long length, out double min, out double max)
    {
        var range = pyramid.GetMinMax(start, length);
        (min, max) = (double.CreateTruncating(range.Min), double.CreateTruncating(range.Max));
        return range.Count > 0;
    }

    /// <inheritdoc />
    public override long FindNumber(long start, long end, bool fromEnd, ref bool provisional) =>
        pyramid.FindNumber(start, end, fromEnd, ref provisional);

    /// <inheritdoc />
    public override bool TryExtremes(long start, long end, out double min, out long minRow, out double max, out long maxRow, ref bool provisional)
    {
        pyramid.Query(start, end, wantCount: false, out var result);
        provisional |= result.IsProvisional;
        (min, minRow, max, maxRow) = (double.CreateTruncating(result.Min), result.MinIndex, double.CreateTruncating(result.Max), result.MaxIndex);
        return result.Found;
    }

    /// <inheritdoc />
    public override void Dispose() => pyramid.Dispose();
}
