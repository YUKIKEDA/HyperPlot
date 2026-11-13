using System.Numerics;
using System.Runtime.CompilerServices;

namespace HyperPlot;

/// <summary>
/// The quantization of one axis of a spatial tile index (design §3.4 "量子化"): a value's difference v from the range's
/// origin maps to the grid cell ⌊(v − lo) / (hi − lo) × 2^bits⌋, held to [0, 2^bits − 1]. The map is non-decreasing in v,
/// so a rectangle of values maps to a rectangle of the grid.
/// </summary>
internal readonly struct SpatialAxis
{
    private readonly long _origin;
    private readonly double _low;
    private readonly double _width;
    private readonly double _cells;
    private readonly double _last;
    private readonly bool _halves;

    /// <summary>Initializes a new instance of the <see cref="SpatialAxis"/> struct.</summary>
    /// <param name="range">The axis's range: finite ends, the lower no greater than the upper.</param>
    /// <param name="bits">The bits of the grid on the axis: 32 for two axes and 21 for three.</param>
    public SpatialAxis(ScaleRange range, int bits)
    {
        _origin = range.Origin;
        _low = range.Lower;
        _width = range.Upper - range.Lower;
        _cells = (double)(1UL << bits);
        _last = _cells - 1;
        if (!double.IsFinite(_width))
        {
            // The ends are so far apart that their difference is not a double: halve the value and the ends first. The
            // difference of the halves is at most double.MaxValue.
            _halves = true;
            _low = range.Lower / 2;
            _width = (range.Upper / 2) - _low;
        }

        if (_width == 0)
        {
            // hi = lo: every value is in cell 0. A width of 1 and no cells give 0 (or NaN, which the clamp turns to 0).
            _width = 1;
            _cells = 0;
        }
    }

    /// <summary>Gets the origin of the range, in the unit of the stored values.</summary>
    public long Origin => _origin;

    /// <summary>
    /// Returns the difference from the origin at which a grid cell begins: the lower corner of a cell of the table (design
    /// §5.5.2 "常駐のセルとページ").
    /// </summary>
    /// <param name="gridCell">The grid cell, from 0 to 2^bits − 1.</param>
    /// <returns>The difference. An axis whose two ends are one value answers that value for every cell.</returns>
    public double DifferenceOf(ulong gridCell)
    {
        var v = _cells == 0 ? _low : _low + (gridCell / _cells * _width);
        return _halves ? v * 2 : v;
    }

    /// <summary>Returns the width, in the unit of the stored values, of a cell of a level.</summary>
    /// <param name="level">The level, from 0 for the whole range.</param>
    /// <returns>The width: 0 on an axis whose two ends are one value.</returns>
    public double ExtentAt(int level)
    {
        var v = _cells == 0 ? 0 : _width / (1UL << level);
        return _halves ? v * 2 : v;
    }

    /// <summary>Returns the grid cell of a stored value.</summary>
    /// <typeparam name="T">The type of the stored value.</typeparam>
    /// <param name="value">The value: finite.</param>
    /// <returns>The grid cell on this axis.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint CellOf<T>(T value)
        where T : unmanaged, INumber<T> => CellOfDifference(ScaleSpace.Difference(value, _origin));

    /// <summary>Returns the grid cell of a difference from the origin.</summary>
    /// <param name="difference">The difference: not NaN.</param>
    /// <returns>The grid cell on this axis.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint CellOfDifference(double difference)
    {
        var v = _halves ? difference / 2 : difference;
        var cell = Math.Floor((v - _low) / _width * _cells);

        // The comparisons are false for NaN, which only the hi = lo axis makes (∞ × 0), so NaN is cell 0.
        return cell >= _last ? (uint)_last
            : cell > 0 ? (uint)cell
            : 0;
    }
}
