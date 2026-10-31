using System.Buffers;
using System.Numerics;

namespace HyperPlot;

/// <summary>
/// The least and greatest y of each category of a categorical x (design §5.5.3, row G7b), made by the workers from one pass
/// over the categories' numbers and the Y column, a chunk a turn, for the Y column's version it was started at. A
/// category with no value that is not NaN keeps NaN at both ends.
/// </summary>
internal sealed class CategoryExtremes
{
    internal CategoryExtremes(int categories, long version)
    {
        Min = new double[categories];
        Max = new double[categories];
        Min.AsSpan().Fill(double.NaN);
        Max.AsSpan().Fill(double.NaN);
        Version = version;
    }

    /// <summary>Gets the least y of each category, by number.</summary>
    internal double[] Min { get; }

    /// <summary>Gets the greatest y of each category.</summary>
    internal double[] Max { get; }

    /// <summary>Gets the Y column's version the pass read.</summary>
    internal long Version { get; }

    /// <summary>Gets the task that ends when every chunk is in, or faults with the first chunk's exception.</summary>
    internal Task Completion => Source.Task;

    internal TaskCompletionSource Source { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Folds a chunk's extremes into the whole, under a lock: a category's extremes join by min and max.</summary>
    internal void Merge(ReadOnlySpan<double> min, ReadOnlySpan<double> max)
    {
        lock (Min)
        {
            for (var c = 0; c < min.Length; c++)
            {
                if (!double.IsNaN(min[c]))
                {
                    Min[c] = double.IsNaN(Min[c]) ? min[c] : Math.Min(Min[c], min[c]);
                    Max[c] = double.IsNaN(Max[c]) ? max[c] : Math.Max(Max[c], max[c]);
                }
            }
        }
    }
}

/// <summary>The pool's work of <see cref="CategoryExtremes"/> over a Y column of one element type.</summary>
internal sealed class CategoryExtremesWork<T> : PoolWork
    where T : unmanaged, INumber<T>
{
    private readonly PlotWorkerPool _pool;
    private readonly CategoricalColumn _x;
    private readonly Column<T> _y;
    private readonly CategoryExtremes _result;
    private int _next;
    private int _remaining;

    internal CategoryExtremesWork(PlotWorkerPool pool, CategoricalColumn x, Column<T> y, CategoryExtremes result)
    {
        (_pool, _x, _y, _result) = (pool, x, y, result);
        _remaining = y.ChunkCount;
    }

    internal override void OnPoolDisposed() => _result.Source.TrySetException(new ObjectDisposedException(nameof(PlotWorkerPool)));

    private protected override int TryTake(bool requestedOnly)
    {
        if (requestedOnly)
        {
            return -1;
        }

        var chunk = Interlocked.Increment(ref _next) - 1;
        return chunk < _y.ChunkCount ? chunk : -1;
    }

    private protected override void Build(int chunk)
    {
        var count = _x.CategoryCount;
        var min = ArrayPool<double>.Shared.Rent(count);
        var max = ArrayPool<double>.Shared.Rent(count);
        try
        {
            var low = min.AsSpan(0, count);
            var high = max.AsSpan(0, count);
            low.Fill(double.NaN);
            high.Fill(double.NaN);
            var codes = _x.GetCodes(chunk);
            var values = _y.GetChunk(chunk);
            for (var i = 0; i < values.Length; i++)
            {
                var code = codes[i];
                var value = double.CreateTruncating(values[i]);
                if (code < 0 || double.IsNaN(value))
                {
                    continue;
                }

                low[code] = double.IsNaN(low[code]) ? value : Math.Min(low[code], value);
                high[code] = double.IsNaN(high[code]) ? value : Math.Max(high[code], value);
            }

            _result.Merge(low, high);
        }
        catch (Exception exception)
        {
            _result.Source.TrySetException(exception);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(min);
            ArrayPool<double>.Shared.Return(max);
        }

        if (Interlocked.Decrement(ref _remaining) == 0)
        {
            _pool.Unregister(this);
            _result.Source.TrySetResult();
        }
    }
}
