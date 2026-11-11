using System.Numerics;

namespace HyperPlot;

/// <summary>A <see cref="SpatialSnapshot"/> whose copy holds x as <typeparamref name="TX"/> and y as <typeparamref name="TY"/>.</summary>
/// <typeparam name="TX">The stored type of x: the column's type, or the type a transformed value is rounded to.</typeparam>
/// <typeparam name="TY">The stored type of y.</typeparam>
internal sealed class SpatialSnapshot<TX, TY> : SpatialSnapshot
    where TX : unmanaged, INumber<TX>
    where TY : unmanaged, INumber<TY>
{
    /// <summary>Initializes a new instance of the <see cref="SpatialSnapshot{TX, TY}"/> class that holds nothing.</summary>
    /// <param name="owner">The index.</param>
    /// <param name="rowsRead">The number of rows the index reads.</param>
    internal SpatialSnapshot(SpatialIndex owner, long rowsRead)
        : base(owner, rowsRead) { }

    /// <summary>Initializes a new instance of the <see cref="SpatialSnapshot{TX, TY}"/> class that holds the counts of levels 0 to 8.</summary>
    /// <param name="owner">The index.</param>
    /// <param name="contents">What the build counted.</param>
    internal SpatialSnapshot(SpatialIndex owner, in Contents contents)
        : base(owner, contents) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="SpatialSnapshot{TX, TY}"/> class that holds the counts, the table, and
    /// the copy.
    /// </summary>
    /// <param name="owner">The index.</param>
    /// <param name="contents">What the build counted.</param>
    /// <param name="builder">The builder of the table, after the writing ended.</param>
    /// <param name="copy">The copy, or <see langword="null"/> when the index reads no rows.</param>
    internal SpatialSnapshot(SpatialIndex owner, in Contents contents, SpatialTableBuilder builder, SpatialCopy? copy)
        : base(owner, contents, builder, copy) { }

    /// <inheritdoc />
    private protected override void EncodeCore(SpatialCopy copy, long first, in SpatialAxis xAxis, in SpatialAxis yAxis, Span<ulong> codes)
    {
        // Every point of the copy has finite values, so each gets a code.
        var xs = copy.GetRegion<TX>(0).Read(first, codes.Length);
        var ys = copy.GetRegion<TY>(1).Read(first, codes.Length);
        _ = SpatialCode.Encode(xs, ys, in xAxis, in yAxis, codes);
    }

    /// <inheritdoc />
    private protected override void ReadCore(SpatialCopy copy, long first, Span<double> x, Span<double> y, Span<long> rows)
    {
        var xs = copy.GetRegion<TX>(0).Read(first, x.Length);
        var xOrigin = XRange.Origin;
        for (var i = 0; i < xs.Length; i++)
        {
            x[i] = ScaleSpace.Difference(xs[i], xOrigin);
        }

        var ys = copy.GetRegion<TY>(1).Read(first, y.Length);
        var yOrigin = YRange.Origin;
        for (var i = 0; i < ys.Length; i++)
        {
            y[i] = ScaleSpace.Difference(ys[i], yOrigin);
        }

        // The last region holds each point's row as an offset from the first row the index reads, 4 or 8 bytes wide.
        var region = copy.RegionCount - 1;
        var firstRow = FirstRow;
        if (copy.Width(region) == sizeof(uint))
        {
            var positions = copy.GetRegion<uint>(region).Read(first, rows.Length);
            for (var i = 0; i < positions.Length; i++)
            {
                rows[i] = firstRow + positions[i];
            }
        }
        else
        {
            var positions = copy.GetRegion<long>(region).Read(first, rows.Length);
            for (var i = 0; i < positions.Length; i++)
            {
                rows[i] = firstRow + positions[i];
            }
        }
    }
}
