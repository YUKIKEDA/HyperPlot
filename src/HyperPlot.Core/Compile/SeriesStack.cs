namespace HyperPlot;

/// <summary>One group's rows of a stacked layer over a numeric x (row G5a): a run of the layer's own columns, or its gathered ones.</summary>
internal readonly record struct StackSeries(Column X, Column Y, long Start, long End);

/// <summary>
/// The groups of a layer over a numeric x, stacked into columns over the union of their x values (design §5.5.3, row
/// G5a): the union's x, and for each group the sums of the groups from it to the last, above 0 (P) and below (N). Group k's
/// pieces are [P(k + 1), P(k)] and [N(k), N(k + 1)]; under the last group, 0. Bars stack the rows at equal x; areas stack
/// each group's values interpolated along its own x, 0 outside its range, with a point where a group crosses 0 and two at
/// an x where a group starts, ends, or steps. Made in two passes over the groups' rows, one that counts and one that writes.
/// </summary>
internal sealed class SeriesStack : IDisposable
{
    private readonly List<IDisposable> _owned = [];
    private NativeColumnStorage<long>? _longX;
    private NativeColumnStorage<double>? _doubleX;
    private NativeColumnStorage<double>?[] _positive = [];
    private NativeColumnStorage<double>?[] _negative = [];

    private SeriesStack(int groups)
    {
        Positive = new Column<double>?[groups + 1];
        Negative = new Column<double>?[groups + 1];
        HasPositive = new bool[groups];
        HasNegative = new bool[groups];
    }

    /// <summary>Gets the union's x: longs when the layer's x is integer (its origin added back), doubles otherwise.</summary>
    public Column X { get; private set; } = null!;

    /// <summary>Gets P(k) of each group, and null at the end for 0; a group with no positive value shares the next group's column.</summary>
    public Column<double>?[] Positive { get; }

    /// <summary>Gets N(k) of each group, and null at the end for 0; a group with no negative value shares the next group's column.</summary>
    public Column<double>?[] Negative { get; }

    /// <summary>Gets whether each group has a value above 0, so a piece above 0 to draw.</summary>
    public bool[] HasPositive { get; }

    /// <summary>Gets whether each group has a value below 0.</summary>
    public bool[] HasNegative { get; }

    /// <summary>Gets the least and the greatest end of a piece that is not 0, or null when every piece is 0.</summary>
    public (double Min, double Max)? Range { get; private set; }

    /// <summary>Gets a value indicating whether any group has a value below 0.</summary>
    public bool AnyNegative { get; private set; }

    /// <summary>Gets the number of points of the union. Tests only.</summary>
    public long Length => X.Length;

    /// <summary>
    /// Stacks groups (design §5.5.3): <paramref name="series"/> in the groups' order, the first outermost. X values are read
    /// as differences from <paramref name="origin"/>, exact for an integer x.
    /// </summary>
    /// <exception cref="InvalidOperationException">A group's x decreases or is NaN.</exception>
    public static SeriesStack Build(IReadOnlyList<StackSeries> series, bool area, bool fill, bool integer, long origin)
    {
        var stack = new SeriesStack(series.Count);
        try
        {
            var count = new Pass(stack, series, area, fill, integer, origin, write: false).Run();
            stack.Allocate(count, integer);
            _ = new Pass(stack, series, area, fill, integer, origin, write: true).Run();
            return stack;
        }
        catch
        {
            stack.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var column in _owned)
        {
            column.Dispose();
        }

        _owned.Clear();
    }

    /// <summary>Makes the union's column and a P and an N column for each group that has values on that side.</summary>
    private void Allocate(long length, bool integer)
    {
        if (integer)
        {
            _longX = new NativeColumnStorage<long>(length);
            X = Own(new Column<long>(_longX, length));
        }
        else
        {
            _doubleX = new NativeColumnStorage<double>(length);
            X = Own(new Column<double>(_doubleX, length));
        }

        (_positive, _negative) = (new NativeColumnStorage<double>?[HasPositive.Length], new NativeColumnStorage<double>?[HasPositive.Length]);
        for (var k = HasPositive.Length - 1; k >= 0; k--)
        {
            (Positive[k], _positive[k]) = HasPositive[k] ? Side(length) : (Positive[k + 1], null);
            (Negative[k], _negative[k]) = HasNegative[k] ? Side(length) : (Negative[k + 1], null);
        }
    }

    private (Column<double> Column, NativeColumnStorage<double> Storage) Side(long length)
    {
        var storage = new NativeColumnStorage<double>(length);
        return (Own(new Column<double>(storage, length)), storage);
    }

    private T Own<T>(T column)
        where T : IDisposable
    {
        _owned.Add(column);
        return column;
    }

    /// <summary>Writes a column in order, a chunk at a time.</summary>
    private sealed class Writer<T>(NativeColumnStorage<T> storage, long length)
        where T : unmanaged
    {
        public void Write(long index, T value)
        {
            var chunk = (int)(index >> Column.ChunkShift);
            var first = (long)chunk << Column.ChunkShift;
            storage.GetWritableChunk(chunk, (int)Math.Min(Column.ChunkLength, length - first))[(int)(index - first)] = value;
        }
    }

    /// <summary>One pass over the groups' rows: counts the union's points, or writes them.</summary>
    private sealed class Pass
    {
        private readonly SeriesStack _stack;
        private readonly IReadOnlyList<StackSeries> _series;
        private readonly bool _area;
        private readonly bool _fill;
        private readonly bool _integer;
        private readonly long _origin;
        private readonly bool _write;
        private readonly long[] _next;
        private readonly double[] _previousX;
        private readonly double[] _previousY;
        private readonly bool[] _started;
        private readonly double[] _left;
        private readonly double[] _right;
        private readonly double[] _before;
        private readonly double[] _values;
        private readonly double[] _up;
        private readonly double[] _down;
        private readonly double[] _sumsUp;
        private readonly double[] _sumsDown;
        private readonly List<double> _crossings = [];
        private readonly Writer<long>? _longX;
        private readonly Writer<double>? _doubleX;
        private readonly Writer<double>?[] _positive;
        private readonly Writer<double>?[] _negative;
        private double _min = double.PositiveInfinity;
        private double _max = double.NegativeInfinity;
        private long _count;

        public Pass(SeriesStack stack, IReadOnlyList<StackSeries> series, bool area, bool fill, bool integer, long origin, bool write)
        {
            (_stack, _series, _area, _fill, _integer, _origin, _write) = (stack, series, area, fill, integer, origin, write);
            var n = series.Count;
            (_next, _previousX, _previousY, _started) = (new long[n], new double[n], new double[n], new bool[n]);
            (_left, _right, _before, _values) = (new double[n], new double[n], new double[n], new double[n]);
            (_up, _down, _sumsUp, _sumsDown) = (new double[n], new double[n], new double[n], new double[n]);
            (_positive, _negative) = (new Writer<double>?[n], new Writer<double>?[n]);
            if (!write)
            {
                return;
            }

            if (integer)
            {
                _longX = new Writer<long>(stack._longX!, stack.X.Length);
            }
            else
            {
                _doubleX = new Writer<double>(stack._doubleX!, stack.X.Length);
            }

            for (var k = 0; k < n; k++)
            {
                _positive[k] = stack._positive[k] is { } up ? new Writer<double>(up, stack.X.Length) : null;
                _negative[k] = stack._negative[k] is { } down ? new Writer<double>(down, stack.X.Length) : null;
            }
        }

        /// <summary>Runs the pass and returns the number of the union's points.</summary>
        public long Run()
        {
            for (var s = 0; s < _series.Count; s++)
            {
                _next[s] = _series[s].Start;
                if (_area)
                {
                    SkipMissing(s);
                }
            }

            var previous = double.NaN;
            while (true)
            {
                var u = double.PositiveInfinity;
                for (var s = 0; s < _series.Count; s++)
                {
                    if (_next[s] < _series[s].End)
                    {
                        var x = XAt(s, _next[s]);
                        if (double.IsNaN(x))
                        {
                            throw new InvalidOperationException(
                                "The X column of a stacked layer holds NaN; a stack over a numeric x reads each group's x in order (design §5.5.3)."
                            );
                        }

                        u = Math.Min(u, x);
                    }
                }

                if (double.IsPositiveInfinity(u))
                {
                    break;
                }

                if (_area)
                {
                    StepArea(u, previous);
                }
                else
                {
                    StepBar(u);
                }

                previous = u;
            }

            if (_write)
            {
                _stack.Range = _min <= _max ? (_min, _max) : null;
            }

            return _count;
        }

        /// <summary>The bars at x = u: each group's rows there, summed above 0 and below apart.</summary>
        private void StepBar(double u)
        {
            for (var s = 0; s < _series.Count; s++)
            {
                (_sumsUp[s], _sumsDown[s]) = (0, 0);
                while (_next[s] < _series[s].End && XAt(s, _next[s]) == u)
                {
                    var y = YAt(s, _next[s]);
                    if (y > 0)
                    {
                        _sumsUp[s] += y;
                    }
                    else if (y < 0)
                    {
                        _sumsDown[s] += y;
                    }

                    _next[s]++;
                }

                CheckOrder(s, u);
            }

            Emit(u, _sumsUp, _sumsDown);
        }

        /// <summary>
        /// The areas at x = u: each group's value from the left and from the right, after a point where a group crosses 0
        /// between the previous x and this one.
        /// </summary>
        private void StepArea(double u, double previous)
        {
            for (var s = 0; s < _series.Count; s++)
            {
                if (_next[s] < _series[s].End && XAt(s, _next[s]) == u)
                {
                    var first = !_started[s];
                    var (from, last) = (YAt(s, _next[s]), 0.0);
                    last = from;
                    _next[s]++;
                    SkipMissing(s);
                    while (_next[s] < _series[s].End && XAt(s, _next[s]) == u)
                    {
                        last = YAt(s, _next[s]);
                        _next[s]++;
                        SkipMissing(s);
                    }

                    CheckOrder(s, u);
                    var end = _next[s] >= _series[s].End;
                    (_left[s], _right[s]) = (first ? 0 : from, end ? 0 : last);
                    (_previousX[s], _previousY[s], _started[s]) = (u, last, true);
                }
                else if (!_started[s] || _next[s] >= _series[s].End)
                {
                    (_left[s], _right[s]) = (0, 0);
                }
                else
                {
                    // Between two of the group's rows: the line through them.
                    var (x0, y0) = (_previousX[s], _previousY[s]);
                    var (x1, y1) = (XAt(s, _next[s]), YAt(s, _next[s]));
                    var value = y0 + ((y1 - y0) * (u - x0) / (x1 - x0));
                    (_left[s], _right[s]) = (value, value);
                }
            }

            if (!double.IsNaN(previous))
            {
                Crossings(previous, u);
            }

            var steps = !_left.AsSpan().SequenceEqual(_right);
            EmitValues(u, _left);
            if (steps)
            {
                EmitValues(u, _right);
            }

            _right.CopyTo(_before, 0);
        }

        /// <summary>Emits a point at each x between the previous x and u where a group's line crosses 0, in order.</summary>
        private void Crossings(double previous, double u)
        {
            _crossings.Clear();
            for (var s = 0; s < _series.Count; s++)
            {
                var (a, b) = (_before[s], _left[s]);
                if ((a > 0 && b < 0) || (a < 0 && b > 0))
                {
                    var x = previous + ((u - previous) * a / (a - b));

                    // An integer x keeps whole values: a crossing rounds to one strictly between the two.
                    x = _integer ? Math.Round(x) : x;
                    if (x > previous && x < u)
                    {
                        _crossings.Add(x);
                    }
                }
            }

            _crossings.Sort();
            var last = double.NaN;
            foreach (var x in _crossings)
            {
                if (x == last)
                {
                    continue;
                }

                var t = (x - previous) / (u - previous);
                for (var s = 0; s < _series.Count; s++)
                {
                    _values[s] = _before[s] + ((_left[s] - _before[s]) * t);
                }

                EmitValues(x, _values);
                last = x;
            }
        }

        /// <summary>Emits a point of the areas' values: each value's part above 0 and below 0.</summary>
        private void EmitValues(double x, double[] values)
        {
            for (var s = 0; s < values.Length; s++)
            {
                (_sumsUp[s], _sumsDown[s]) = (Math.Max(values[s], 0), Math.Min(values[s], 0));
            }

            Emit(x, _sumsUp, _sumsDown);
        }

        /// <summary>Counts a point, or accumulates it from the last group and writes it.</summary>
        private void Emit(double x, double[] up, double[] down)
        {
            if (!_write)
            {
                for (var s = 0; s < up.Length; s++)
                {
                    _stack.HasPositive[s] |= up[s] > 0;
                    _stack.HasNegative[s] |= down[s] < 0;
                }

                _stack.AnyNegative |= down.AsSpan().ContainsAnyExcept(0);
                _count++;
                return;
            }

            var (p, n) = (0.0, 0.0);
            for (var s = up.Length - 1; s >= 0; s--)
            {
                (p, n) = (p + up[s], n + down[s]);
                (_up[s], _down[s]) = (p, n);
            }

            for (var s = 0; s < up.Length; s++)
            {
                if (_fill)
                {
                    (_up[s], _down[s]) = (p == 0 ? 0 : _up[s] / p, n == 0 ? 0 : _down[s] / -n);
                }

                _positive[s]?.Write(_count, _up[s]);
                _negative[s]?.Write(_count, _down[s]);
                foreach (var value in (ReadOnlySpan<double>)[_up[s], _down[s]])
                {
                    if (value != 0)
                    {
                        (_min, _max) = (Math.Min(_min, value), Math.Max(_max, value));
                    }
                }
            }

            if (_longX is not null)
            {
                _longX.Write(_count, _origin + (long)x);
            }
            else
            {
                _doubleX!.Write(_count, x);
            }

            _count++;
        }

        /// <summary>Throws unless a group's next row lies past the x just read: its x is non-decreasing.</summary>
        private void CheckOrder(int s, double u)
        {
            if (_next[s] < _series[s].End && XAt(s, _next[s]) < u)
            {
                throw new InvalidOperationException(
                    "The X column of a stacked layer decreases within a group; a stack over a numeric x reads each group's x in order (design §5.5.3)."
                );
            }
        }

        /// <summary>Moves a group past its rows whose value is NaN: an area stacks the rows it has.</summary>
        private void SkipMissing(int s)
        {
            while (_next[s] < _series[s].End && double.IsNaN(YAt(s, _next[s])))
            {
                _next[s]++;
            }
        }

        private double XAt(int s, long row) => _series[s].X.DifferenceAt(row, _integer ? _origin : 0);

        private double YAt(int s, long row) => _series[s].Y.DifferenceAt(row, 0);
    }
}
