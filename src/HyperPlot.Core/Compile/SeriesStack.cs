namespace HyperPlot;

/// <summary>One group's rows of a stacked layer over a numeric x (row G5a): a run of the layer's own columns, or its gathered ones.</summary>
internal readonly record struct StackSeries(Column X, Column Y, long Start, long End);

/// <summary>
/// The groups of a layer over a numeric x, stacked into columns over the union of their x values (design §5.5.3, row
/// G5a): the union's x, and for each group the sums of the groups from it to the last, above 0 (P) and below (N). Group k's
/// pieces are [P(k + 1), P(k)] and [N(k), N(k + 1)]; under the last group, 0. Bars stack the rows at equal x; areas stack
/// each group's values interpolated along its own x, 0 outside its range, with a point where a group crosses 0 and two at
/// an x where a group starts, ends, or steps.
/// </summary>
/// <remarks>
/// The x range is cut into segments that the workers stack apart, each in two passes, one that counts its points and one
/// that writes them where the counts before it end. A segment starts from the state the rows before it leave: which groups
/// have started, their last row, and their values at the last x before it, for the crossings of 0 in the gap.
/// </remarks>
internal sealed class SeriesStack : IDisposable
{
    /// <summary>The rows a segment holds about, when there are workers: 2^20, a chunk.</summary>
    private const long SegmentRows = 1L << 20;

    /// <summary>The most segments: enough to keep the workers busy on uneven data.</summary>
    private const int MaxSegments = 256;

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
    /// as differences from <paramref name="origin"/>, exact for an integer x. Without a pool, one segment on this thread.
    /// </summary>
    /// <exception cref="InvalidOperationException">A group's x decreases or is NaN.</exception>
    public static SeriesStack Build(IReadOnlyList<StackSeries> series, bool area, bool fill, bool integer, long origin, PlotWorkerPool? pool = null)
    {
        var stack = new SeriesStack(series.Count);
        try
        {
            var bounds = Segments(series, integer, origin, pool);
            var segments = bounds.Length - 1;
            var counts = new Pass[segments];
            Run(
                segments,
                pool,
                j => (counts[j] = new Pass(stack, series, area, fill, integer, origin, bounds[j], bounds[j + 1], write: false, at: 0)).Run()
            );

            // The groups' sides come from every segment's count; the offsets from the counts before each.
            var offsets = new long[segments + 1];
            for (var j = 0; j < segments; j++)
            {
                offsets[j + 1] = offsets[j] + counts[j].Count;
                for (var s = 0; s < series.Count; s++)
                {
                    stack.HasPositive[s] |= counts[j].HasPositive[s];
                    stack.HasNegative[s] |= counts[j].HasNegative[s];
                }
            }

            stack.AnyNegative = stack.HasNegative.AsSpan().Contains(true);
            stack.Allocate(offsets[^1], integer);
            var writes = new Pass[segments];
            Run(
                segments,
                pool,
                j => (writes[j] = new Pass(stack, series, area, fill, integer, origin, bounds[j], bounds[j + 1], write: true, at: offsets[j])).Run()
            );
            var (min, max) = (double.PositiveInfinity, double.NegativeInfinity);
            foreach (var pass in writes)
            {
                (min, max) = (Math.Min(min, pass.Min), Math.Max(max, pass.Max));
            }

            stack.Range = min <= max ? (min, max) : null;
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

    /// <summary>Runs a pass a segment: on the workers, or in order on this thread.</summary>
    private static void Run(int segments, PlotWorkerPool? pool, Action<int> pass)
    {
        if (pool is null || segments == 1)
        {
            for (var j = 0; j < segments; j++)
            {
                pass(j);
            }

            return;
        }

        pool.RunChunks(segments, pass).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Returns the segments' bounds in x: −∞, then values evenly between the groups' least and greatest x, then +∞; a
    /// segment holds the x from its bound up to the next. Whole values for an integer x, so that a point is never split.
    /// </summary>
    private static double[] Segments(IReadOnlyList<StackSeries> series, bool integer, long origin, PlotWorkerPool? pool)
    {
        var rows = 0L;
        var (least, most) = (double.PositiveInfinity, double.NegativeInfinity);
        foreach (var group in series)
        {
            if (group.End > group.Start)
            {
                rows += group.End - group.Start;
                least = Math.Min(least, group.X.DifferenceAt(group.Start, integer ? origin : 0));
                most = Math.Max(most, group.X.DifferenceAt(group.End - 1, integer ? origin : 0));
            }
        }

        var count = pool is null || !(most > least) ? 1 : (int)Math.Clamp(rows / SegmentRows, 1, MaxSegments);
        var bounds = new double[count + 1];
        (bounds[0], bounds[count]) = (double.NegativeInfinity, double.PositiveInfinity);
        for (var j = 1; j < count; j++)
        {
            var bound = least + ((most - least) * j / count);
            bounds[j] = integer ? Math.Ceiling(bound) : bound;
        }

        return bounds;
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

    /// <summary>Writes a column in order, a chunk at a time; segments write apart ranges of it.</summary>
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

    /// <summary>One pass over a segment's rows: counts the union's points in it, or writes them from an offset.</summary>
    private sealed class Pass
    {
        /// <summary>The rows a group's window reads at a time: 64 KB of x and y a group.</summary>
        private const int Window = 4_096;

        private readonly SeriesStack _stack;
        private readonly IReadOnlyList<StackSeries> _series;
        private readonly bool _area;
        private readonly bool _fill;
        private readonly bool _integer;
        private readonly long _origin;
        private readonly double _from;
        private readonly double _to;
        private readonly bool _write;
        private readonly long[] _next;
        private readonly double[] _nextX;
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
        private readonly double[][] _xs;
        private readonly double[][] _ys;
        private readonly long[] _windows;
        private readonly int[] _lengths;
        private readonly Writer<long>? _longX;
        private readonly Writer<double>? _doubleX;
        private readonly Writer<double>?[] _positive;
        private readonly Writer<double>?[] _negative;
        private long _at;

        public Pass(
            SeriesStack stack,
            IReadOnlyList<StackSeries> series,
            bool area,
            bool fill,
            bool integer,
            long origin,
            double from,
            double to,
            bool write,
            long at
        )
        {
            (_stack, _series, _area, _fill, _integer, _origin) = (stack, series, area, fill, integer, origin);
            (_from, _to, _write, _at) = (from, to, write, at);
            var n = series.Count;
            (_next, _nextX, _previousX, _previousY, _started) = (new long[n], new double[n], new double[n], new double[n], new bool[n]);
            (_left, _right, _before, _values) = (new double[n], new double[n], new double[n], new double[n]);
            (_up, _down, _sumsUp, _sumsDown) = (new double[n], new double[n], new double[n], new double[n]);
            (HasPositive, HasNegative) = (new bool[n], new bool[n]);
            (_positive, _negative) = (new Writer<double>?[n], new Writer<double>?[n]);
            (_xs, _ys) = (new double[n][], new double[n][]);
            for (var s = 0; s < n; s++)
            {
                (_xs[s], _ys[s]) = (new double[Window], new double[Window]);
            }

            (_windows, _lengths) = (new long[n], new int[n]);
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

        /// <summary>Gets the number of the segment's points, once run.</summary>
        public long Count { get; private set; }

        /// <summary>Gets whether each group has a value above 0 in the segment.</summary>
        public bool[] HasPositive { get; }

        /// <summary>Gets whether each group has a value below 0 in the segment.</summary>
        public bool[] HasNegative { get; }

        /// <summary>Gets the least end of a piece that is not 0 the pass wrote, or +∞.</summary>
        public double Min { get; private set; } = double.PositiveInfinity;

        /// <summary>Gets the greatest end of a piece that is not 0 the pass wrote, or −∞.</summary>
        public double Max { get; private set; } = double.NegativeInfinity;

        /// <summary>Runs the pass over the segment.</summary>
        public void Run()
        {
            var previous = Start();
            while (true)
            {
                var u = double.PositiveInfinity;
                for (var s = 0; s < _series.Count; s++)
                {
                    u = Math.Min(u, _nextX[s]);
                }

                if (!(u < _to))
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
        }

        /// <summary>
        /// Puts each group at its first row of x at or past the segment's start, and returns the last x before it with each
        /// group's value there from the right (an area's), or NaN when no row lies before.
        /// </summary>
        private double Start()
        {
            var last = double.NaN;
            for (var s = 0; s < _series.Count; s++)
            {
                var group = _series[s];
                _next[s] = FirstAtOrPast(s);
                if (!_area)
                {
                    Refresh(s);
                    continue;
                }

                // An area's group: its last row before the segment that holds a value, and its next one that does.
                var before = _next[s] - 1;
                while (before >= group.Start && double.IsNaN(YAt(s, before)))
                {
                    before--;
                }

                SkipMissing(s);
                if (before >= group.Start)
                {
                    (_previousX[s], _previousY[s], _started[s]) = (XAt(s, before), YAt(s, before), true);
                    last = double.IsNaN(last) ? _previousX[s] : Math.Max(last, _previousX[s]);
                }
            }

            if (!_area || double.IsNaN(last))
            {
                return double.NaN;
            }

            // Each group's value at that x from the right, as the step there left it.
            for (var s = 0; s < _series.Count; s++)
            {
                var ended = _next[s] >= _series[s].End;
                _before[s] =
                    !_started[s] || ended ? 0
                    : _previousX[s] == last ? _previousY[s]
                    : Line(s, last);
            }

            return last;
        }

        /// <summary>Returns a group's first row whose x is at or past the segment's start, by a search of its rows.</summary>
        private long FirstAtOrPast(int s)
        {
            var group = _series[s];
            if (double.IsNegativeInfinity(_from))
            {
                return group.Start;
            }

            var (low, high) = (group.Start, group.End);
            while (low < high)
            {
                var middle = low + ((high - low) / 2);
                if (group.X.DifferenceAt(middle, _integer ? _origin : 0) < _from)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            return low;
        }

        /// <summary>The bars at x = u: each group's rows there, summed above 0 and below apart.</summary>
        private void StepBar(double u)
        {
            for (var s = 0; s < _series.Count; s++)
            {
                (_sumsUp[s], _sumsDown[s]) = (0, 0);
                while (_nextX[s] == u)
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
                    Refresh(s);
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
                if (_nextX[s] == u)
                {
                    var first = !_started[s];
                    var from = YAt(s, _next[s]);
                    var last = from;
                    _next[s]++;
                    SkipMissing(s);
                    while (_nextX[s] == u)
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
                    var value = Line(s, u);
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

        /// <summary>Returns a started group's value at x on the line from its last row to its next.</summary>
        private double Line(int s, double x)
        {
            var (x0, y0) = (_previousX[s], _previousY[s]);
            var (x1, y1) = (_nextX[s], YAt(s, _next[s]));
            return y0 + ((y1 - y0) * (x - x0) / (x1 - x0));
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
            Count++;
            if (!_write)
            {
                for (var s = 0; s < up.Length; s++)
                {
                    HasPositive[s] |= up[s] > 0;
                    HasNegative[s] |= down[s] < 0;
                }

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

                _positive[s]?.Write(_at, _up[s]);
                _negative[s]?.Write(_at, _down[s]);
                foreach (var value in (ReadOnlySpan<double>)[_up[s], _down[s]])
                {
                    if (value != 0)
                    {
                        (Min, Max) = (Math.Min(Min, value), Math.Max(Max, value));
                    }
                }
            }

            if (_longX is not null)
            {
                _longX.Write(_at, _origin + (long)x);
            }
            else
            {
                _doubleX!.Write(_at, x);
            }

            _at++;
        }

        /// <summary>Throws unless a group's next row lies past the x just read: its x is non-decreasing.</summary>
        private void CheckOrder(int s, double u)
        {
            if (_nextX[s] < u)
            {
                throw new InvalidOperationException(
                    "The X column of a stacked layer decreases within a group; a stack over a numeric x reads each group's x in order (design §5.5.3)."
                );
            }
        }

        /// <summary>Moves a group past its rows whose value is NaN, an area stacks the rows it has, and reads its next x.</summary>
        private void SkipMissing(int s)
        {
            while (_next[s] < _series[s].End && double.IsNaN(YAt(s, _next[s])))
            {
                _next[s]++;
            }

            Refresh(s);
        }

        /// <summary>Reads a group's next x: +∞ past its rows.</summary>
        private void Refresh(int s)
        {
            if (_next[s] >= _series[s].End)
            {
                _nextX[s] = double.PositiveInfinity;
                return;
            }

            var x = XAt(s, _next[s]);
            if (double.IsNaN(x))
            {
                throw new InvalidOperationException(
                    "The X column of a stacked layer holds NaN; a stack over a numeric x reads each group's x in order (design §5.5.3)."
                );
            }

            _nextX[s] = x;
        }

        private double XAt(int s, long row)
        {
            Load(s, row);
            return _xs[s][(int)(row - _windows[s])];
        }

        private double YAt(int s, long row)
        {
            Load(s, row);
            return _ys[s][(int)(row - _windows[s])];
        }

        /// <summary>
        /// Reads a group's rows from <paramref name="row"/> into its window unless the window holds it: up to
        /// <see cref="Window"/> rows, within the row's chunk. A pass reads a group's rows forward, a row back at its start.
        /// </summary>
        private void Load(int s, long row)
        {
            if (row >= _windows[s] && row < _windows[s] + _lengths[s])
            {
                return;
            }

            var series = _series[s];
            var chunk = (int)(row >> Column.ChunkShift);
            var offset = (int)(row - ((long)chunk << Column.ChunkShift));
            var length = (int)Math.Min(Math.Min(Window, series.End - row), Column.ChunkLength - offset);
            series.X.ReadChunk(chunk, offset, _xs[s].AsSpan(0, length), _integer ? _origin : 0);
            series.Y.ReadChunk(chunk, offset, _ys[s].AsSpan(0, length), 0);
            (_windows[s], _lengths[s]) = (row, length);
        }
    }
}
