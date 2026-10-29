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

    /// <summary>Adds a series over this pyramid, with <paramref name="x"/> as its X, to a batch.</summary>
    public abstract int AddTo(M4Batch batch, Column x, M4Point[] destination);

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
    public override int AddTo(M4Batch batch, Column x, M4Point[] destination) => x.AddSeries(batch, pyramid, destination);

    /// <inheritdoc />
    public override void Dispose() => pyramid.Dispose();
}
