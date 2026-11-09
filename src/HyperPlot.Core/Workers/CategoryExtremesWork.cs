using System.Buffers;
using System.Numerics;

namespace HyperPlot;

/// <summary>
/// The least and greatest y of each category of a categorical x (design §5.5.3, row G7b), made by the workers from one pass
/// over the categories' numbers and the Y column, a chunk a turn, for the Y column's version it was started at. A
/// category with no value that is not NaN keeps NaN at both ends, and −1 for their rows. Each end keeps the row it came
/// from, the first of the rows of the same value (row I2c, Issue #355 Decision 3): a hit test answers with the row drawn.
/// </summary>
internal sealed class CategoryExtremes
{
    internal CategoryExtremes(int categories, long version)
    {
        Min = new double[categories];
        Max = new double[categories];
        MinRow = new long[categories];
        MaxRow = new long[categories];
        Min.AsSpan().Fill(double.NaN);
        Max.AsSpan().Fill(double.NaN);
        MinRow.AsSpan().Fill(-1);
        MaxRow.AsSpan().Fill(-1);
        Version = version;
    }

    /// <summary>Gets the least y of each category, by number.</summary>
    internal double[] Min { get; }

    /// <summary>Gets the greatest y of each category.</summary>
    internal double[] Max { get; }

    /// <summary>Gets the row of each category's least y, or −1.</summary>
    internal long[] MinRow { get; }

    /// <summary>Gets the row of each category's greatest y, or −1.</summary>
    internal long[] MaxRow { get; }

    /// <summary>Gets the Y column's version the pass read.</summary>
    internal long Version { get; }

    /// <summary>Gets the task that ends when every chunk is in, or faults with the first chunk's exception.</summary>
    internal Task Completion => Source.Task;

    internal TaskCompletionSource Source { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Folds a chunk's extremes and their rows into the whole, under a lock: a category's extremes join by min and max, and
    /// of two equal ones the earlier row stays, whatever order the chunks come in.
    /// </summary>
    internal void Merge(ReadOnlySpan<double> min, ReadOnlySpan<double> max, ReadOnlySpan<long> minRow, ReadOnlySpan<long> maxRow)
    {
        lock (Min)
        {
            for (var c = 0; c < min.Length; c++)
            {
                if (double.IsNaN(min[c]))
                {
                    continue;
                }

                if (double.IsNaN(Min[c]) || min[c] < Min[c] || (min[c] == Min[c] && minRow[c] < MinRow[c]))
                {
                    (Min[c], MinRow[c]) = (min[c], minRow[c]);
                }

                if (double.IsNaN(Max[c]) || max[c] > Max[c] || (max[c] == Max[c] && maxRow[c] < MaxRow[c]))
                {
                    (Max[c], MaxRow[c]) = (max[c], maxRow[c]);
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
    private readonly RowRange _rows;
    private int _next;
    private int _remaining;

    internal CategoryExtremesWork(PlotWorkerPool pool, CategoricalColumn x, Column<T> y, CategoryExtremes result, RowRange rows)
    {
        (_pool, _x, _y, _result, _rows) = (pool, x, y, result, rows);
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
        var minRows = ArrayPool<long>.Shared.Rent(count);
        var maxRows = ArrayPool<long>.Shared.Rent(count);
        try
        {
            var low = min.AsSpan(0, count);
            var high = max.AsSpan(0, count);
            var lowRow = minRows.AsSpan(0, count);
            var highRow = maxRows.AsSpan(0, count);
            low.Fill(double.NaN);
            high.Fill(double.NaN);
            var codes = _x.GetCodes(chunk);
            var values = _y.GetChunk(chunk);

            // The rows of the chunk inside the layer's run (row G7c): all of them unless split by color.
            var start = (long)chunk << Column.ChunkShift;
            var (from, to) = (
                (int)Math.Clamp(_rows.Start - start, 0, values.Length),
                (int)Math.Clamp(_rows.EndIn(_y.Length) - start, 0, values.Length)
            );
            for (var i = from; i < to; i++)
            {
                var code = codes[i];
                var value = double.CreateTruncating(values[i]);
                if (code < 0 || double.IsNaN(value))
                {
                    continue;
                }

                // The rows go up within a chunk: a strict comparison keeps the first of equal values.
                if (double.IsNaN(low[code]) || value < low[code])
                {
                    (low[code], lowRow[code]) = (value, start + i);
                }

                if (double.IsNaN(high[code]) || value > high[code])
                {
                    (high[code], highRow[code]) = (value, start + i);
                }
            }

            _result.Merge(low, high, lowRow, highRow);
        }
        catch (Exception exception)
        {
            _result.Source.TrySetException(exception);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(min);
            ArrayPool<double>.Shared.Return(max);
            ArrayPool<long>.Shared.Return(minRows);
            ArrayPool<long>.Shared.Return(maxRows);
        }

        if (Interlocked.Decrement(ref _remaining) == 0)
        {
            _pool.Unregister(this);
            _result.Source.TrySetResult();
        }
    }
}
