using System.Numerics;

namespace HyperPlot.Tests;

/// <summary>
/// A finished index over two arrays, with its pool, its columns, and its borrowed snapshot, for the tests that read a
/// built index. Disposing it gives the snapshot back and disposes the index before the columns and the pool.
/// </summary>
internal sealed class SpatialBuilt : IDisposable
{
    private readonly PlotWorkerPool _pool;
    private readonly IDisposable[] _owned;

    private SpatialBuilt(PlotWorkerPool pool, IDisposable[] owned, SpatialIndex index)
    {
        _pool = pool;
        _owned = owned;
        Index = index;
        Snapshot = index.Acquire();
    }

    /// <summary>Gets the index, whose first build has ended.</summary>
    public SpatialIndex Index { get; }

    /// <summary>Gets the snapshot that was current when the first build ended.</summary>
    public SpatialSnapshot Snapshot { get; }

    /// <summary>Wraps two arrays, builds their index with the constants of the tests, and waits for the end of the build.</summary>
    public static SpatialBuilt Of<TX, TY>(TX[] xs, TY[] ys, SpatialIndexInput input, int workers = 3, SpatialIndexTuning? tuning = null)
        where TX : unmanaged, INumber<TX>
        where TY : unmanaged, INumber<TY>
    {
        return Over(Column.Wrap(xs), Column.Wrap(ys), input, workers, tuning);
    }

    /// <summary>Builds the index of two columns, which the result owns, and waits for the end of the build.</summary>
    public static SpatialBuilt Over<TX, TY>(
        Column<TX> x,
        Column<TY> y,
        SpatialIndexInput input,
        int workers = 3,
        SpatialIndexTuning? tuning = null,
        SpatialCopyAllocator? allocator = null,
        SpatialProbe? probe = null
    )
        where TX : unmanaged, INumber<TX>
        where TY : unmanaged, INumber<TY>
    {
        var pool = new PlotWorkerPool(workers);
        return new SpatialBuilt(pool, [x, y], SpatialReference.Build(pool, x, y, input, tuning, allocator, probe));
    }

    /// <summary>Wraps two arrays and builds the index of every row.</summary>
    public static SpatialBuilt Of<TX, TY>(TX[] xs, TY[] ys, int workers = 3, SpatialIndexTuning? tuning = null)
        where TX : unmanaged, INumber<TX>
        where TY : unmanaged, INumber<TY> => Of(xs, ys, new SpatialIndexInput { Rows = RowRange.All }, workers, tuning);

    /// <inheritdoc />
    public void Dispose()
    {
        Snapshot.Release();
        Index.Dispose();
        foreach (var owned in _owned)
        {
            owned.Dispose();
        }

        _pool.Dispose();
    }
}
