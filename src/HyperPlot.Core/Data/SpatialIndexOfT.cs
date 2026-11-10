using System.Numerics;
using System.Runtime.CompilerServices;

namespace HyperPlot;

/// <summary>A <see cref="SpatialIndex"/> over an x column of <typeparamref name="TX"/> and a y column of <typeparamref name="TY"/>.</summary>
/// <typeparam name="TX">The element type of the x column.</typeparam>
/// <typeparam name="TY">The element type of the y column.</typeparam>
/// <remarks>
/// An axis without a transform is read from its column and stored in the column's type. An axis with a transform is read
/// as <see cref="double"/> differences from 0, passed through <see cref="IScale.Forward"/>, and stored as
/// <see cref="float"/> for a column of <see cref="float"/> and as <see cref="double"/> otherwise; its codes, its range,
/// and whether a point is finite come from the value after the rounding to the stored type (design §3.4 "スケール空間").
/// </remarks>
internal sealed class SpatialIndex<TX, TY> : SpatialIndex
    where TX : unmanaged, INumber<TX>
    where TY : unmanaged, INumber<TY>
{
    private readonly Column<TX> _x;
    private readonly Column<TY> _y;

    /// <summary>Initializes a new instance of the <see cref="SpatialIndex{TX, TY}"/> class, which waits for a worker to build it.</summary>
    /// <param name="pool">The pool that builds the index.</param>
    /// <param name="x">The x column.</param>
    /// <param name="y">The y column: as long as <paramref name="x"/>.</param>
    /// <param name="input">The rows, the transforms, and the ranges.</param>
    /// <param name="tuning">The constants of the build.</param>
    /// <param name="allocator">What makes the copy.</param>
    internal SpatialIndex(
        PlotWorkerPool pool,
        Column<TX> x,
        Column<TY> y,
        in SpatialIndexInput input,
        SpatialIndexTuning tuning,
        SpatialCopyAllocator allocator
    )
        : base(pool, x, y, input, tuning, allocator, ShapeOf(x, y, input), static (owner, rows) => new SpatialSnapshot<TX, TY>(owner, rows))
    {
        _x = x;
        _y = y;
    }

    /// <inheritdoc />
    private protected override SpatialSnapshot MakeCoarse(in SpatialSnapshot.Contents contents) => new SpatialSnapshot<TX, TY>(this, contents);

    /// <inheritdoc />
    private protected override SpatialSnapshot MakeFull(in SpatialSnapshot.Contents contents, SpatialTableBuilder builder, SpatialCopy? copy)
    {
        // The snapshot reads the copy in the types the write stage stored.
        if (Input.XTransform is null)
        {
            return MakeFull<TX>(contents, builder, copy);
        }

        return typeof(TX) == typeof(float) ? MakeFull<float>(contents, builder, copy) : MakeFull<double>(contents, builder, copy);
    }

    /// <inheritdoc />
    private protected override void RunUnit(SpatialBuild build, int group, long unit)
    {
        // A unit lies in one chunk: its ends are multiples of the rows of a unit, which divide the length of a chunk.
        var (first, count) = build.RowsOf(unit);
        var chunk = (int)(first >> Column.ChunkShift);
        var local = (int)(first & (Column.ChunkLength - 1));
        var xs = _x.GetChunk(chunk).Slice(local, count);
        var ys = _y.GetChunk(chunk).Slice(local, count);
        if (build.UnitJob == SpatialJob.Range)
        {
            if (Input.XRange is null)
            {
                Extend(build, group, 0, _x, Input.XTransform, xs, chunk, local);
            }

            if (Input.YRange is null)
            {
                Extend(build, group, 1, _y, Input.YTransform, ys, chunk, local);
            }

            return;
        }

        if (Input.XTransform is not { } transform)
        {
            WithX<TX, TX>(build, group, first, xs, ys);
            return;
        }

        ReadOnlySpan<double> stored = Transform(xs, transform, build.Transformed(group, 0));
        if (typeof(TX) == typeof(float))
        {
            WithX<double, float>(build, group, first, stored, ys);
        }
        else
        {
            WithX<double, double>(build, group, first, stored, ys);
        }
    }

    private static Shape ShapeOf(Column<TX> x, Column<TY> y, in SpatialIndexInput input) =>
        new(
            Unsafe.SizeOf<TX>(),
            Unsafe.SizeOf<TY>(),
            input.XTransform is null && ScaleSpace.IsInteger<TX>(),
            input.YTransform is null && ScaleSpace.IsInteger<TY>(),
            StoredBytes<TX>(input.XTransform),
            StoredBytes<TY>(input.YTransform),
            x.StorageKind == ColumnStorageKind.MemoryMapped || y.StorageKind == ColumnStorageKind.MemoryMapped
        );

    private static int StoredBytes<T>(IScale? transform)
        where T : unmanaged, INumber<T> =>
        transform is null ? Unsafe.SizeOf<T>()
        : typeof(T) == typeof(float) ? sizeof(float)
        : sizeof(double);

    // Reads the differences of the values from 0, passes them through the transform, and rounds them to the stored type.
    private static Span<double> Transform<T>(ReadOnlySpan<T> values, IScale transform, double[] scratch)
        where T : unmanaged, INumber<T>
    {
        var stored = scratch.AsSpan(0, values.Length);
        for (var i = 0; i < values.Length; i++)
        {
            stored[i] = ScaleSpace.Difference(values[i], 0);
        }

        transform.Forward(stored, stored);
        if (typeof(T) == typeof(float))
        {
            // A value that is finite as a double may round to an infinity of float, which the index then skips.
            for (var i = 0; i < stored.Length; i++)
            {
                stored[i] = (float)stored[i];
            }
        }

        return stored;
    }

    // The range stage of one axis of one unit (design §3.4 "範囲の段").
    private static void Extend<T>(
        SpatialBuild build,
        int group,
        int axis,
        Column<T> column,
        IScale? transform,
        ReadOnlySpan<T> values,
        int chunk,
        int local
    )
        where T : unmanaged, INumber<T>
    {
        ref var extent = ref build.ExtentOf(group, axis);
        if (transform is not null)
        {
            Widen<double>(ref extent, Transform(values, transform, build.Transformed(group, axis)));
            return;
        }

        // A chunk that lies wholly in the rows answers from its statistics, which the unit at its first row takes in.
        var chunkFirst = (long)chunk << Column.ChunkShift;
        if (chunkFirst >= build.Start && chunkFirst + column.GetChunkLength(chunk) <= build.End)
        {
            var stats = column.GetStats(chunk);
            if (stats.Count == 0)
            {
                return;
            }

            // An infinity at either end hides the finite values next to it: such a chunk is scanned.
            if (T.IsFinite(stats.Min) && T.IsFinite(stats.Max))
            {
                if (local == 0)
                {
                    Widen<T>(ref extent, [stats.Min, stats.Max]);
                }

                return;
            }
        }

        Widen(ref extent, values);
    }

    private static void Widen<T>(ref SpatialBuild.Extent extent, ReadOnlySpan<T> values)
        where T : unmanaged, INumber<T>
    {
        if (ScaleSpace.IsInteger<T>())
        {
            var low = extent.Low;
            var high = extent.High;
            foreach (var value in values)
            {
                var v = long.CreateTruncating(value);
                low = Math.Min(low, v);
                high = Math.Max(high, v);
            }

            extent.Low = low;
            extent.High = high;
            return;
        }

        var least = extent.Least;
        var greatest = extent.Greatest;
        foreach (var value in values)
        {
            if (T.IsFinite(value))
            {
                var v = double.CreateTruncating(value);
                if (v < least)
                {
                    least = v;
                }

                if (v > greatest)
                {
                    greatest = v;
                }
            }
        }

        extent.Least = least;
        extent.Greatest = greatest;
    }

    // Counts or writes one unit: the values are read as TXRead and TYRead, and stored as TXStored and TYStored.
    private static void Run<TXRead, TXStored, TYRead, TYStored>(
        SpatialBuild build,
        int group,
        long first,
        ReadOnlySpan<TXRead> xs,
        ReadOnlySpan<TYRead> ys
    )
        where TXRead : unmanaged, INumber<TXRead>
        where TXStored : unmanaged, INumber<TXStored>
        where TYRead : unmanaged, INumber<TYRead>
        where TYStored : unmanaged, INumber<TYStored>
    {
        var builder = build.Builder!;
        var xAxis = build.XAxis;
        var yAxis = build.YAxis;
        var codes = build.Codes(group).AsSpan(0, xs.Length);
        codes = codes[..SpatialCode.Encode(xs, ys, in xAxis, in yAxis, codes)];
        switch (build.UnitJob)
        {
            case SpatialJob.First:
                builder.CountFirst(group, codes, xs.Length - codes.Length);
                break;
            case SpatialJob.Pass:
                builder.CountPass(group, codes);
                break;
            default:
                // The codes become the places of the points in the copy.
                builder.Place(group, codes);
                if (build.Copy is not { } copy)
                {
                    break;
                }

                if (copy.Width(copy.RegionCount - 1) == sizeof(uint))
                {
                    Write<TXRead, TXStored, TYRead, TYStored, uint>(copy, first - build.Start, xs, ys, codes);
                }
                else
                {
                    Write<TXRead, TXStored, TYRead, TYStored, long>(copy, first - build.Start, xs, ys, codes);
                }

                break;
        }
    }

    private static void Write<TXRead, TXStored, TYRead, TYStored, TPosition>(
        SpatialCopy copy,
        long offset,
        ReadOnlySpan<TXRead> xs,
        ReadOnlySpan<TYRead> ys,
        ReadOnlySpan<ulong> places
    )
        where TXRead : unmanaged, INumber<TXRead>
        where TXStored : unmanaged, INumber<TXStored>
        where TYRead : unmanaged, INumber<TYRead>
        where TYStored : unmanaged, INumber<TYStored>
        where TPosition : unmanaged, INumber<TPosition>
    {
        var xRegion = copy.GetRegion<TXStored>(0);
        var yRegion = copy.GetRegion<TYStored>(1);
        var positions = copy.GetRegion<TPosition>(2);
        ys = ys[..xs.Length];
        var taken = 0;

        // The places are in the order of the finite points. A column that changes under the build may show more finite
        // points here than the unit had codes: the loop ends with the places.
        for (var i = 0; i < xs.Length && taken < places.Length; i++)
        {
            var x = xs[i];
            var y = ys[i];
            if (!TXRead.IsFinite(x) || !TYRead.IsFinite(y))
            {
                continue;
            }

            // A dropped point has a place outside every region.
            var place = places[taken++];
            if (xRegion.TryWrite(place, TXStored.CreateTruncating(x)))
            {
                yRegion.TryWrite(place, TYStored.CreateTruncating(y));
                positions.TryWrite(place, TPosition.CreateTruncating(offset + i));
            }
        }
    }

    private SpatialSnapshot MakeFull<TXStored>(in SpatialSnapshot.Contents contents, SpatialTableBuilder builder, SpatialCopy? copy)
        where TXStored : unmanaged, INumber<TXStored>
    {
        if (Input.YTransform is null)
        {
            return new SpatialSnapshot<TXStored, TY>(this, contents, builder, copy);
        }

        return typeof(TY) == typeof(float)
            ? new SpatialSnapshot<TXStored, float>(this, contents, builder, copy)
            : new SpatialSnapshot<TXStored, double>(this, contents, builder, copy);
    }

    private void WithX<TXRead, TXStored>(SpatialBuild build, int group, long first, ReadOnlySpan<TXRead> xs, ReadOnlySpan<TY> ys)
        where TXRead : unmanaged, INumber<TXRead>
        where TXStored : unmanaged, INumber<TXStored>
    {
        if (Input.YTransform is not { } transform)
        {
            Run<TXRead, TXStored, TY, TY>(build, group, first, xs, ys);
            return;
        }

        ReadOnlySpan<double> stored = Transform(ys, transform, build.Transformed(group, 1));
        if (typeof(TY) == typeof(float))
        {
            Run<TXRead, TXStored, double, float>(build, group, first, xs, stored);
        }
        else
        {
            Run<TXRead, TXStored, double, double>(build, group, first, xs, stored);
        }
    }
}
