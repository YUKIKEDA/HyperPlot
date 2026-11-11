using System.Numerics;

namespace HyperPlot.Tests;

/// <summary>The stored values of one axis of an index, as the brute force reads them.</summary>
internal sealed class SpatialAxisData
{
    private readonly long[]? _integers;
    private readonly double[]? _reals;

    private SpatialAxisData(long[]? integers, double[]? reals)
    {
        _integers = integers;
        _reals = reals;
    }

    /// <summary>The stored values of a column: its values, or their transforms rounded to the stored type.</summary>
    public static SpatialAxisData Of<T>(T[] values, Func<double, double>? forward = null)
        where T : unmanaged, INumber<T>
    {
        var real = typeof(T) == typeof(float) || typeof(T) == typeof(double);
        if (forward is null && !real)
        {
            return new SpatialAxisData(Array.ConvertAll(values, long.CreateChecked), null);
        }

        var reals = new double[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var v = double.CreateChecked(values[i]);
            if (forward is not null)
            {
                v = forward(v);
                if (typeof(T) == typeof(float))
                {
                    v = (float)v;
                }
            }

            reals[i] = v;
        }

        return new SpatialAxisData(null, reals);
    }

    public bool IsFinite(long row) => _reals is null || double.IsFinite(_reals[row]);

    public double Difference(long row, long origin) => _integers is not null ? (double)((Int128)_integers[row] - origin) : _reals![row] - origin;

    /// <summary>The range of the finite stored values of the rows, with the origin of the design.</summary>
    public ScaleRange Range(RowRange rows)
    {
        if (_integers is not null)
        {
            if (rows.Start == rows.End)
            {
                return new ScaleRange(0, 0, 0);
            }

            var span = _integers.AsSpan((int)rows.Start, (int)(rows.End - rows.Start)).ToArray();
            var (min, max) = (span.Min(), span.Max());
            return new ScaleRange(min, 0, (double)((Int128)max - min));
        }

        var least = double.PositiveInfinity;
        var greatest = double.NegativeInfinity;
        for (var row = rows.Start; row < rows.End; row++)
        {
            var v = _reals![row];
            if (double.IsFinite(v))
            {
                least = Math.Min(least, v);
                greatest = Math.Max(greatest, v);
            }
        }

        if (least > greatest)
        {
            return new ScaleRange(0, 0, 0);
        }

        var origin = Math.Floor(least);
        return new ScaleRange((long)origin, least - origin, greatest - origin);
    }
}
