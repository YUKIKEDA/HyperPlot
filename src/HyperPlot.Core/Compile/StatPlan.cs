using HyperPlot.Rendering;

namespace HyperPlot;

/// <summary>
/// A layer with a stat (design §4.3, §5.5.3, rows G5b and G5c): the workers make the stat's result as a small derived
/// table when the layer is made, and an inner layer without the stat draws it by the geom's usual path and the layer's
/// position. A column's new version makes the layer again (<see cref="Stale"/>).
/// </summary>
internal sealed class StatPlan : LayerPlan
{
    private readonly List<IDisposable> _owned = [];
    private readonly long[] _built;
    private readonly LayerPlan _inner;
    private readonly DataTable _table;
    private readonly Func<LayerSpec, LayerSpec> _make;
    private readonly (double Min, double Max)? _xEnds;
    private readonly StatSource _source;

    public StatPlan(LayerSpec spec, PlanBuild build)
        : this(spec, build, RowRange.All, null) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="StatPlan"/> class over a run of rows (row G6b): a facet panel's. The bins'
    /// edges and a density's or a moving average's range come from <paramref name="source"/>: the whole layer's columns when the
    /// panels share the axis, else the run itself (design §4.5).
    /// </summary>
    public StatPlan(LayerSpec spec, PlanBuild build, RowRange rows, StatSource? source)
        : base(spec, rows)
    {
        var groups = SplitPlan.Splits(spec) ? (CategoricalColumn)(spec.FillBy ?? spec.ColorBy)!.Value.Column : null;
        var (x, y) = StatColumns(spec);
        var columns = new List<Column> { x };
        if (y is not null)
        {
            columns.Add(y);
        }

        if (groups is not null)
        {
            columns.Add(groups);
        }

        SetColumns([.. columns]);
        _source = source ?? StatSource.Own(x, y, rows);
        _built = new long[Columns.Length];
        for (var i = 0; i < _built.Length; i++)
        {
            _built[i] = Columns[i].Version;
        }

        var made = spec.Stat switch
        {
            BinStatSpec bin => Bins(spec, bin, x, groups, build),
            CountStatSpec => Counts((CategoricalColumn)x, groups, build.Pool),
            DensityStatSpec density => Densities(spec, density, x, groups, build),
            SmoothStatSpec smooth => Smooths(spec, smooth, x, y!, groups, build),
            Bin2DStatSpec cells => Cells(cells, x, y!, build),
            _ => Summaries(spec, (SummaryStatSpec)spec.Stat!, (CategoricalColumn)x, groups, y!, build.Pool, build.Layer),
        };
        (_table, _make, _xEnds) = (made.Table, made.Make, made.XEnds);
        _inner = Create(_make(spec), build);
    }

    /// <summary>Returns the columns a layer's stat reads: its x, and its y or none (rows G5b and G5c).</summary>
    internal static (Column X, Column? Y) StatColumns(LayerSpec spec) =>
        spec switch
        {
            BarLayerSpec b => (b.X.Column, b.Y.IsDefault ? null : b.Y.Column),
            AreaLayerSpec a => (a.X.Column, null),
            StepLayerSpec s => (s.X.Column, null),
            ErrorBarLayerSpec e => (e.X.Column, e.YMin.Column),
            LineLayerSpec l => (l.X.Column, l.Y.IsDefault ? null : l.Y.Column),
            RectLayerSpec r => (r.XMin.Column, r.YMin.Column),
            _ => throw new InvalidOperationException($"A {spec.GetType().Name} takes no stat."),
        };

    /// <summary>Gets the derived table the inner layer draws (rows G5b and G5c).</summary>
    public DataTable Derived => _table;

    /// <summary>Gets each group's bandwidth of a density, in the x scale's space (row G5c): empty for other stats.</summary>
    public double[] Bandwidths { get; private set; } = [];

    /// <summary>Gets the column of the values of 2D bins' cells, which the color scale learns from (row G5c), or null.</summary>
    public Column? CellValues { get; private set; }

    public override bool Stale
    {
        get
        {
            for (var i = 0; i < _built.Length; i++)
            {
                if (Columns[i].Version != _built[i])
                {
                    return true;
                }
            }

            return false;
        }
    }

    public override void WaitForIndexes(PlotWorkerPool pool) => _inner.WaitForIndexes(pool);

    public override void BeginLearning() => _inner.BeginLearning();

    public override void AddRanges(RangeSink sink, bool ofX, DataTable data)
    {
        _inner.AddRanges(sink, ofX, _table);

        // Counts, bins, and densities start at 0 (design §4.2): a step or a line has no baseline of its own. A log y leaves it
        // out, as a baseline.
        if (!ofX && Spec.Stat is BinStatSpec or CountStatSpec or DensityStatSpec)
        {
            sink.AddBaseline(0);
        }

        // An area's or a step's bins put their points at the bins' middles: x spans the bins' edges (design §4.2).
        if (ofX && _xEnds is { } ends)
        {
            sink.Add(ends.Min, ends.Max, data.GetName(((AreaLayerSpec?)(Spec as AreaLayerSpec))?.X.Index ?? ((StepLayerSpec)Spec).X.Index));
        }
    }

    public override void Record(RecordContext context)
    {
        _inner.SeriesColor = SeriesColor;
        _inner.Index = Index;
        _inner.Colors = Colors;
        _inner.Record(context);
    }

    public override void Release()
    {
        _inner.Release();
        foreach (var owned in _owned)
        {
            owned.Dispose();
        }
    }

    /// <summary>Passes a new node of the same columns and stat on to the inner layer: its looks and label.</summary>
    protected override void OnSpecChanged(LayerSpec before, LayerSpec after)
    {
        if (!ReferenceEquals(before, after) && !SplitPlan.SameLooks(before, after))
        {
            _inner.Take(_make(after));
        }
    }

    /// <summary>Returns the value of a summary of a cell (design §4.1): the type 7 quantile from the two ranks the select found.</summary>
    private static double Value(
        SummaryOp op,
        int cell,
        StatWork.Moments moments,
        double[] ranks,
        int targets,
        List<double> ps,
        IReadOnlyList<IStat> customs
    )
    {
        var n = moments.Count[cell];
        switch (op.Kind)
        {
            case SummaryKind.Mean:
                return moments.Sum[cell] / n;
            case SummaryKind.Min:
                return moments.Min[cell];
            case SummaryKind.Max:
                return moments.Max[cell];
            case SummaryKind.Quantile:
                var q = ps.IndexOf(op.P);
                var h = (n - 1) * op.P;
                var (low, high) = (ranks[(cell * targets) + (2 * q)], ranks[(cell * targets) + (2 * q) + 1]);
                return low + ((h - Math.Floor(h)) * (high - low));
            default:
                var offset = 0;
                foreach (var custom in customs)
                {
                    if (ReferenceEquals(custom, op.Stat))
                    {
                        break;
                    }

                    offset += custom.StateLength;
                }

                return op.Stat!.Result(moments.State(cell, offset, op.Stat.StateLength));
        }
    }

    /// <summary>The bins of a numeric x (design §4.1, §5.5.3): rects of a bar's pieces, or points at the middles of an area or a step.</summary>
    private Made Bins(LayerSpec spec, BinStatSpec bin, Column x, CategoricalColumn? groups, PlanBuild build)
    {
        var pool = build.Pool;
        var time = build.XScale as TimeScaleSpec;
        var transform = time is null ? build.XTransform : null;
        var (edges, origin, uniform) = BinEdges(_source.X, bin, time, transform, build);

        var bins = Math.Max(edges.Length - 1, 0);
        var nk = groups?.CategoryCount ?? 1;
        if (spec is BarLayerSpec && (long)bins * nk > MaxElements)
        {
            throw new ArgumentException(
                $"Layer {build.Layer} has {bins} bins in {nk} groups; a binned bar draws at most {MaxElements} rects (design §5.5.3). Give fewer bins."
            );
        }

        var counts = bins == 0 ? [] : StatWork.Bin(x, groups, transform, origin, edges, uniform, pool, Rows);
        var values = new double[counts.Length];
        for (var k = 0; k < nk; k++)
        {
            var n = 0L;
            for (var i = 0; i < bins; i++)
            {
                n += counts[(k * bins) + i];
            }

            for (var i = 0; i < bins; i++)
            {
                var c = counts[(k * bins) + i];
                values[(k * bins) + i] = bin.Output == StatOutput.Density && c > 0 ? c / (n * (edges[i + 1] - edges[i])) : c;
            }
        }

        var toData = ToData(transform);
        if (spec is BarLayerSpec bar)
        {
            return Rects(bar, edges, values, nk, groups, toData, time is not null, origin);
        }

        // Points at the bins' middles, the groups in turn at each (design §5.5.3).
        var xs = new double[bins * nk];
        var ys = new double[bins * nk];
        var codes = new int[bins * nk];
        for (var i = 0; i < bins; i++)
        {
            var middle = toData((edges[i] + edges[i + 1]) / 2);
            for (var k = 0; k < nk; k++)
            {
                (xs[(i * nk) + k], ys[(i * nk) + k], codes[(i * nk) + k]) = (middle, values[(k * bins) + i], k);
            }
        }

        var table = Table(("x", XColumn(xs, time is not null, origin)), ("y", Own(Column.Wrap(ys))), ("g", groups?.WithCodes(codes)));
        var (xRef, yRef, gRef) = (new ColumnRef(table, 0), new ColumnRef(table, 1), groups is null ? (ColumnRef?)null : new ColumnRef(table, 2));
        (double, double)? ends = bins == 0 ? null : (toData(edges[0]) + (time is null ? 0 : origin), toData(edges[^1]) + (time is null ? 0 : origin));
        return new Made(
            table,
            layer =>
                layer switch
                {
                    AreaLayerSpec area => new AreaLayerSpec
                    {
                        X = xRef,
                        Y = yRef,
                        Fill = area.Fill,
                        Outline = area.Outline,
                        Label = area.Label,
                        Position = area.Position,
                        FillBy = area.FillBy is null ? null : gRef,
                        ColorBy = area.ColorBy is null ? null : gRef,
                    },
                    StepLayerSpec step => new StepLayerSpec
                    {
                        X = xRef,
                        Y = yRef,
                        Style = step.Style,
                        Mode = step.Mode,
                        Label = step.Label,
                        ColorBy = step.ColorBy is null ? null : gRef,
                    },
                    _ => throw new InvalidOperationException($"A {layer.GetType().Name} is not binned as points."),
                },
            ends
        );
    }

    /// <summary>
    /// Returns the edges of the bins of a column in its scale's space (design §4.1, rows G5b and G5c): calendar steps in the
    /// column's unit on a time scale, as differences from the first edge (the origin), or Vega's steps over the finite range.
    /// </summary>
    private static (double[] Edges, long Origin, bool Uniform) BinEdges(
        StatRange x,
        BinStatSpec bin,
        TimeScaleSpec? time,
        IScale? transform,
        PlanBuild build
    )
    {
        if (time is not null)
        {
            // Calendar steps in the column's unit, or the given step from the least value; differences from the first edge.
            if (!x.TryGetRange(out var low, out var high))
            {
                return ([], 0, true);
            }

            if (bin.Step is null)
            {
                var calendar = TimeTicks.BinEdges(low.Integer, high.Integer, time, bin.MaxBins);
                var origin = calendar.Length > 0 ? calendar[0] : 0;
                return ([.. calendar.Select(e => (double)(e - origin))], origin, false);
            }

            return (StatWork.Edges(0, high.Integer - low.Integer, bin with { Anchor = bin.Anchor - low.Integer }, build.Layer), low.Integer, true);
        }

        var (min, max) = x.FiniteRange(transform, build.Pool);
        return (min <= max ? StatWork.Edges(min, max, bin, build.Layer) : [], 0, true);
    }

    /// <summary>
    /// Returns the range [from, to] of a density or a moving average in the scale space (design §4.1, row G5c), and the
    /// origin a time column's values are read as differences from: its least value.
    /// </summary>
    private static (double From, double To, long Origin) RangeOf(StatRange x, bool time, IScale? transform, PlotWorkerPool pool)
    {
        if (time)
        {
            return x.TryGetRange(out var low, out var high) ? (0, high.Integer - low.Integer, low.Integer) : (1, 0, 0);
        }

        var (min, max) = x.FiniteRange(transform, pool);
        return (min, max, 0);
    }

    /// <summary>Returns how a value of the scale space goes back to the data: the transform's inverse, or itself (a time column adds its origin as a long).</summary>
    private static Func<double, double> ToData(IScale? transform) =>
        transform is null
            ? s => s
            : s =>
            {
                Span<double> one = [s];
                transform.Inverse(one, one);
                return one[0];
            };

    /// <summary>
    /// A kernel density estimate of each group (design §4.1, §5.5.3, row G5c), by R's <c>density()</c>: the bandwidth (given,
    /// or each group's <c>bw.nrd0</c>), the values spread linearly onto a grid, the grid convolved with a Gaussian kernel, and
    /// the result interpolated at the points over the layer's range. A line or an area draws it.
    /// </summary>
    private Made Densities(LayerSpec spec, DensityStatSpec density, Column x, CategoricalColumn? groups, PlanBuild build)
    {
        var pool = build.Pool;
        var time = build.XScale is TimeScaleSpec;
        var transform = time ? null : build.XTransform;
        var nk = groups?.CategoryCount ?? 1;
        var h = new double[nk];
        var drawn = new bool[nk];
        double from,
            to;
        long origin;
        StatWork.Spread? spread = null;
        if (density.Bandwidth is { } given)
        {
            (from, to, origin) = RangeOf(_source.X, time, transform, pool);
            h.AsSpan().Fill(given * density.Adjust);
            drawn.AsSpan().Fill(true);
        }
        else
        {
            // The bandwidths come from the layer's own rows; a range the panels share comes from the whole layer (row G6b).
            var own = _source.X.IsOwn(x, Rows);
            origin = time && _source.X.TryGetRange(out var low, out _) ? low.Integer : 0;
            spread = StatWork.SpreadOf(x, groups, transform, origin, pool, Rows);
            if (own)
            {
                (from, to) = (spread.Min, spread.Max);
            }
            else
            {
                (from, to, _) = RangeOf(_source.X, time, transform, pool);
            }

            ChooseBandwidths(x, Rows, groups, spread, time, transform, origin, density.Adjust, h, drawn, pool);
        }

        Bandwidths = h;
        var points = density.Points;
        if (!(from <= to) || !drawn.Contains(true))
        {
            return Curves(spec, [], [], [], groups, time, origin);
        }

        // A range of one value spans R's default of 3 bandwidths each side (design §4.1).
        if (from == to)
        {
            var widest = 0.0;
            for (var k = 0; k < nk; k++)
            {
                widest = drawn[k] ? Math.Max(widest, h[k]) : widest;
            }

            (from, to) = (from - (3 * widest), to + (3 * widest));
        }

        // R's grid: max(n, 512) points, a power of 2 above 512, from 4 bandwidths below the range to 4 above.
        var m = points <= 512 ? 512 : 1 << (int)Math.Ceiling(Math.Log2(points));
        var (lo, delta) = (new double[nk], new double[nk]);
        for (var k = 0; k < nk; k++)
        {
            (lo[k], delta[k]) = drawn[k] ? (from - (4 * h[k]), (to - from + (8 * h[k])) / (m - 1)) : (double.NaN, double.NaN);
        }

        var weights = StatWork.SpreadOnGrids(x, groups, transform, origin, lo, delta, m, pool, Rows);
        var values = new double[nk * points];
        pool.RunChunks(
                nk,
                k =>
                {
                    var row = weights.AsSpan(k * m, m);
                    var n = spread?.Count[k] ?? Sum(row);
                    if (!drawn[k] || n == 0)
                    {
                        drawn[k] = false;
                        return;
                    }

                    for (var i = 0; i < m; i++)
                    {
                        row[i] /= n;
                    }

                    var grid = StatWork.Convolve(row, delta[k], h[k]);
                    var up = to + (4 * h[k]);
                    for (var t = 0; t < points; t++)
                    {
                        var at = from + (t * (to - from) / (points - 1));
                        values[(k * points) + t] = Interpolate(grid, lo[k], up, at) * (density.Output == StatOutput.Count ? n : 1);
                    }
                }
            )
            .GetAwaiter()
            .GetResult();

        var data = ToData(transform);
        var xs = new List<double>();
        var ys = new List<double>();
        var codes = new List<int>();
        for (var t = 0; t < points; t++)
        {
            var at = data(from + (t * (to - from) / (points - 1)));
            for (var k = 0; k < nk; k++)
            {
                if (drawn[k])
                {
                    xs.Add(at);
                    ys.Add(values[(k * points) + t]);
                    codes.Add(k);
                }
            }
        }

        return Curves(spec, [.. xs], [.. ys], [.. codes], groups, time, origin);

        static double Sum(ReadOnlySpan<double> row)
        {
            var sum = 0.0;
            foreach (var w in row)
            {
                sum += w;
            }

            return sum;
        }
    }

    /// <summary>
    /// Sets each group's bandwidth by R's <c>bw.nrd0</c> (design §4.1, row G5c): 0.9 · min(s, IQR / 1.34) · n^(−1/5), or s, or
    /// the first value's magnitude, or 1 where the one before is 0; times the adjust. A group of fewer than 2 values is not
    /// drawn. The quartiles are the exact type 7 of the values the transform maps to finite numbers.
    /// </summary>
    private static void ChooseBandwidths(
        Column x,
        RowRange rows,
        CategoricalColumn? groups,
        StatWork.Spread spread,
        bool time,
        IScale? transform,
        long origin,
        double adjust,
        double[] h,
        bool[] drawn,
        PlotWorkerPool pool
    )
    {
        var (low, high) = time || spread.RawMin > spread.RawMax ? (0UL, ulong.MaxValue) : (x.KeyOfValue(spread.RawMin), x.KeyOfValue(spread.RawMax));
        var ranks = StatWork.Select(null, groups, x, spread.Count, [0.25, 0.75], pool, rows, low, high);
        var data = ToData(null);
        for (var k = 0; k < h.Length; k++)
        {
            var n = spread.Count[k];
            if (n < 2)
            {
                continue;
            }

            var quartiles = new double[2];
            for (var q = 0; q < 2; q++)
            {
                var p = q == 0 ? 0.25 : 0.75;
                var at = (n - 1) * p;
                var (a, b) = (Space(ranks[(k * 4) + (2 * q)]), Space(ranks[(k * 4) + (2 * q) + 1]));
                quartiles[q] = a + ((at - Math.Floor(at)) * (b - a));
            }

            var s = Math.Sqrt(spread.Squares[k] / (n - 1));
            var least = Math.Min(s, (quartiles[1] - quartiles[0]) / 1.34);
            if (!(least > 0))
            {
                least =
                    s > 0 ? s
                    : Math.Abs(spread.First[k]) > 0 ? Math.Abs(spread.First[k])
                    : 1;
            }

            (h[k], drawn[k]) = (0.9 * least * Math.Pow(n, -0.2) * adjust, true);
        }

        double Space(double value)
        {
            if (time)
            {
                return value - origin;
            }

            if (transform is null)
            {
                return data(value);
            }

            Span<double> one = [value];
            transform.Forward(one, one);
            return one[0];
        }
    }

    /// <summary>Returns the grid's value at a point by linear interpolation, as R's <c>approx</c> does: the grid's m points span [lo, up].</summary>
    private static double Interpolate(double[] grid, double lo, double up, double at)
    {
        var m = grid.Length;
        var step = (up - lo) / (m - 1);
        var i = Math.Clamp((int)Math.Floor((at - lo) / step), 0, m - 2);
        var (left, right) = (lo + (i * step), lo + ((i + 1) * step));
        return grid[i] + ((at - left) / (right - left) * (grid[i + 1] - grid[i]));
    }

    /// <summary>
    /// A moving average of each group (design §4.1, §5.5.3, row G5c): the windows' 2 · points edges sorted, each row's y added
    /// to the interval of edges its x lies in, and each point's mean the sums of its window's intervals over their numbers;
    /// NaN where no row lies. The intervals' running sums are kept in two doubles, so that a window's difference keeps its
    /// digits.
    /// </summary>
    private Made Smooths(LayerSpec spec, SmoothStatSpec smooth, Column x, Column y, CategoricalColumn? groups, PlanBuild build)
    {
        var pool = build.Pool;
        var time = build.XScale is TimeScaleSpec;
        var transform = time ? null : build.XTransform;
        var nk = groups?.CategoryCount ?? 1;
        var (from, to, origin) = RangeOf(_source.X, time, transform, pool);
        var points = smooth.Points;
        if (!(from <= to))
        {
            return Curves(spec, [], [], [], groups, time, origin);
        }

        var middles = new double[points];
        for (var t = 0; t < points; t++)
        {
            middles[t] = from + (t * (to - from) / (points - 1));
        }

        var half = smooth.Window / 2;
        var edges = middles.Select(v => v - half).Concat(middles.Select(v => v + half)).Order().Distinct().ToArray();
        var intervals = edges.Length - 1;
        var sums = StatWork.WindowSums(x, y, groups, transform, origin, edges, pool, Rows);
        var data = ToData(transform);
        var xs = new List<double>();
        var ys = new List<double>();
        var codes = new List<int>();
        var (high, low, count) = (new double[intervals + 1], new double[intervals + 1], new double[intervals + 1]);
        var means = new double[nk * points];
        var any = new bool[nk];
        for (var k = 0; k < nk; k++)
        {
            for (var r = 0; r < intervals; r++)
            {
                var at = ((k * intervals) + r) * 2;
                (high[r + 1], low[r + 1]) = TwoSum(high[r], low[r], sums[at]);
                count[r + 1] = count[r] + sums[at + 1];
            }

            any[k] = count[intervals] > 0;
            for (var t = 0; t < points; t++)
            {
                var (a, b) = (Array.BinarySearch(edges, middles[t] - half), Array.BinarySearch(edges, middles[t] + half));
                var n = count[b] - count[a];
                means[(k * points) + t] = n > 0 ? ((high[b] - high[a]) + (low[b] - low[a])) / n : double.NaN;
            }
        }

        for (var t = 0; t < points; t++)
        {
            var at = data(middles[t]);
            for (var k = 0; k < nk; k++)
            {
                if (any[k])
                {
                    xs.Add(at);
                    ys.Add(means[(k * points) + t]);
                    codes.Add(k);
                }
            }
        }

        return Curves(spec, [.. xs], [.. ys], [.. codes], groups, time, origin);

        // A running sum in two doubles: the sum and the rounding it lost (Knuth's TwoSum).
        static (double High, double Low) TwoSum(double high, double low, double add)
        {
            var sum = high + add;
            var back = sum - high;
            var error = (high - (sum - back)) + (add - back);
            return (sum, low + error);
        }
    }

    /// <summary>
    /// A curve a group at the same points (row G5c): the long table of x, y, and group, drawn by a line or an area over it.
    /// </summary>
    private Made Curves(LayerSpec spec, double[] xs, double[] ys, int[] codes, CategoricalColumn? groups, bool time, long origin)
    {
        var table = Table(("x", XColumn(xs, time, origin)), ("y", Own(Column.Wrap(ys))), ("g", groups?.WithCodes(codes)));
        var (xRef, yRef, gRef) = (new ColumnRef(table, 0), new ColumnRef(table, 1), groups is null ? (ColumnRef?)null : new ColumnRef(table, 2));
        return new Made(
            table,
            layer =>
                layer switch
                {
                    LineLayerSpec line => new LineLayerSpec
                    {
                        X = xRef,
                        Y = yRef,
                        Style = line.Style,
                        Label = line.Label,
                        ColorBy = line.ColorBy is null ? null : gRef,
                    },
                    AreaLayerSpec area => new AreaLayerSpec
                    {
                        X = xRef,
                        Y = yRef,
                        Fill = area.Fill,
                        Outline = area.Outline,
                        Label = area.Label,
                        Position = area.Position,
                        FillBy = area.FillBy is null ? null : gRef,
                        ColorBy = area.ColorBy is null ? null : gRef,
                    },
                    _ => throw new InvalidOperationException($"A {layer.GetType().Name} does not draw a curve of a stat."),
                },
            null
        );
    }

    /// <summary>
    /// The cells of 2D bins (design §4.1, §5.5.3, row G5c): each axis binned in its scale's space, a rect a cell with values,
    /// filled by its count or density through the color scale.
    /// </summary>
    /// <exception cref="ArgumentException">More cells than a rect layer draws.</exception>
    private Made Cells(Bin2DStatSpec cells, Column x, Column y, PlanBuild build)
    {
        var (timeX, timeY) = (build.XScale as TimeScaleSpec, build.YScale as TimeScaleSpec);
        var (tx, ty) = (timeX is null ? build.XTransform : null, timeY is null ? build.YTransform : null);
        var (ex, ox, ux) = BinEdges(_source.X, cells.X, timeX, tx, build);
        var (ey, oy, uy) = BinEdges(_source.Y!, cells.Y, timeY, ty, build);
        var (bx, by) = (Math.Max(ex.Length - 1, 0), Math.Max(ey.Length - 1, 0));
        if ((long)bx * by > MaxElements)
        {
            throw new ArgumentException(
                $"Layer {build.Layer} has {bx} x bins and {by} y bins; 2D bins draw at most {MaxElements} cells (design §4.1). Give fewer bins."
            );
        }

        var counts = bx * by == 0 ? [] : StatWork.Bin2D(x, y, new(ex, ux, ox, tx), new(ey, uy, oy, ty), build.Pool, Rows);
        var n = 0L;
        foreach (var c in counts)
        {
            n += c;
        }

        var (dataX, dataY) = (ToData(tx), ToData(ty));
        var cellRows = new List<(double XMin, double XMax, double YMin, double YMax, double Value)>();
        for (var j = 0; j < by; j++)
        {
            for (var i = 0; i < bx; i++)
            {
                var c = counts[(j * bx) + i];
                if (c > 0)
                {
                    var value = cells.Output == StatOutput.Density ? c / (n * (ex[i + 1] - ex[i]) * (ey[j + 1] - ey[j])) : c;
                    cellRows.Add((dataX(ex[i]), dataX(ex[i + 1]), dataY(ey[j]), dataY(ey[j + 1]), value));
                }
            }
        }

        var values = Own(Column.Wrap(cellRows.Select(r => r.Value).ToArray()));
        CellValues = values;
        var table = Table(
            ("xmin", XColumn([.. cellRows.Select(r => r.XMin)], timeX is not null, ox)),
            ("xmax", XColumn([.. cellRows.Select(r => r.XMax)], timeX is not null, ox)),
            ("ymin", XColumn([.. cellRows.Select(r => r.YMin)], timeY is not null, oy)),
            ("ymax", XColumn([.. cellRows.Select(r => r.YMax)], timeY is not null, oy)),
            ("value", values)
        );
        var refs = (new ColumnRef(table, 0), new ColumnRef(table, 1), new ColumnRef(table, 2), new ColumnRef(table, 3), new ColumnRef(table, 4));
        return new Made(
            table,
            layer => new RectLayerSpec
            {
                XMin = refs.Item1,
                XMax = refs.Item2,
                YMin = refs.Item3,
                YMax = refs.Item4,
                FillBy = refs.Item5,
                Label = layer.Label,
            },
            null
        );
    }

    /// <summary>
    /// A binned bar's pieces as rects (design §5.5.3): a rect a group and bin, placed by the layer's position in the stat (a
    /// stack from the last group, a fill to 1, a dodge in the bin's width), none for a count of 0.
    /// </summary>
    private Made Rects(
        BarLayerSpec bar,
        double[] edges,
        double[] values,
        int nk,
        CategoricalColumn? groups,
        Func<double, double> data,
        bool time,
        long origin
    )
    {
        var bins = Math.Max(edges.Length - 1, 0);
        var kind = PositionOf(bar);
        var padding = DodgePadding(bar);
        var rows = new List<(double XMin, double XMax, double YMin, double YMax, int Group)>();
        for (var k = 0; k < nk; k++)
        {
            for (var i = 0; i < bins; i++)
            {
                var value = values[(k * bins) + i];
                if (value == 0)
                {
                    continue;
                }

                var (left, right) = (edges[i], edges[i + 1]);
                var (low, high) = (0.0, value);
                if (Stacks(kind))
                {
                    var (below, total) = (0.0, 0.0);
                    for (var t = 0; t < nk; t++)
                    {
                        total += values[(t * bins) + i];
                        below += t > k ? values[(t * bins) + i] : 0;
                    }

                    (low, high) = kind == PositionKind.Fill ? (below / total, (below + value) / total) : (below, below + value);
                }
                else if (kind == PositionKind.Dodge && groups is not null)
                {
                    var place = (right - left) / nk;
                    left += (place * k) + (place * padding / 2);
                    right = left + (place * (1 - padding));
                }

                rows.Add((data(left), data(right), low, high, k));
            }
        }

        var table = Table(
            ("xmin", XColumn([.. rows.Select(r => r.XMin)], time, origin)),
            ("xmax", XColumn([.. rows.Select(r => r.XMax)], time, origin)),
            ("ymin", Own(Column.Wrap(rows.Select(r => r.YMin).ToArray()))),
            ("ymax", Own(Column.Wrap(rows.Select(r => r.YMax).ToArray()))),
            ("g", groups?.WithCodes([.. rows.Select(r => r.Group)]))
        );
        var refs = (new ColumnRef(table, 0), new ColumnRef(table, 1), new ColumnRef(table, 2), new ColumnRef(table, 3));
        ColumnRef? gRef = groups is null ? null : new ColumnRef(table, 4);
        return new Made(
            table,
            layer => new RectLayerSpec
            {
                XMin = refs.Item1,
                XMax = refs.Item2,
                YMin = refs.Item3,
                YMax = refs.Item4,
                Fill = ((BarLayerSpec)layer).Fill,
                Label = layer.Label,
                ColorBy = gRef,
            },
            null
        );
    }

    /// <summary>The count of each cell of categories (design §5.5.3): a bar a cell with rows, by the categorical bar's path.</summary>
    private Made Counts(CategoricalColumn x, CategoricalColumn? groups, PlotWorkerPool pool)
    {
        var counts = StatWork.Count(x, groups, pool, Rows);
        var cells = new List<(int X, int G, double Y)>();
        for (var c = 0; c < counts.Length; c++)
        {
            if (counts[c] > 0)
            {
                cells.Add((c % x.CategoryCount, c / x.CategoryCount, counts[c]));
            }
        }

        return CellBars(x, groups, cells);
    }

    /// <summary>The summaries of each cell (design §5.5.3): a bar of a value, or an error bar between two.</summary>
    private Made Summaries(
        LayerSpec spec,
        SummaryStatSpec summary,
        CategoricalColumn x,
        CategoricalColumn? groups,
        Column y,
        PlotWorkerPool pool,
        int layer
    )
    {
        var ops =
            spec is ErrorBarLayerSpec ? new[] { summary.Low ?? SummaryOp.Min, summary.High ?? SummaryOp.Max } : [summary.Value ?? SummaryOp.Mean];
        var customs = ops.Where(o => o.Kind == SummaryKind.Custom).Select(o => o.Stat!).Distinct().ToList();
        var ps = ops.Where(o => o.Kind == SummaryKind.Quantile).Select(o => o.P).Distinct().ToList();
        var cellCount = (long)(groups?.CategoryCount ?? 1) * x.CategoryCount;
        var targets = 2 * ps.Count;
        if (cellCount * targets * 256 * 8 > StatWork.QuantileBytes)
        {
            throw new ArgumentException(
                $"Layer {layer} asks {ps.Count} quantiles of {cellCount} cells; a quantile's select holds 2 KB a cell and rank, at most {StatWork.QuantileBytes >> 20} MB (design §5.5.3), so {StatWork.QuantileBytes / (targets * 2048L)} cells."
            );
        }

        var moments = StatWork.Summarize(x, groups, y, customs, pool, Rows);
        var ranks = ps.Count == 0 ? [] : StatWork.Select(x, groups, y, moments.Count, ps, pool, Rows);
        var cells = new List<(int X, int G, double Y)>();
        var highs = new List<double>();
        for (var c = 0; c < moments.Count.Length; c++)
        {
            if (moments.Count[c] == 0)
            {
                continue;
            }

            cells.Add((c % x.CategoryCount, c / x.CategoryCount, Value(ops[0], c, moments, ranks, targets, ps, customs)));
            if (ops.Length > 1)
            {
                highs.Add(Value(ops[1], c, moments, ranks, targets, ps, customs));
            }
        }

        if (spec is not ErrorBarLayerSpec)
        {
            return CellBars(x, groups, cells);
        }

        var table = Table(
            ("x", x.WithCodes([.. cells.Select(c => c.X)])),
            ("ymin", Own(Column.Wrap(cells.Select(c => c.Y).ToArray()))),
            ("ymax", Own(Column.Wrap(highs.ToArray()))),
            ("g", groups?.WithCodes([.. cells.Select(c => c.G)]))
        );
        var (xRef, minRef, maxRef) = (new ColumnRef(table, 0), new ColumnRef(table, 1), new ColumnRef(table, 2));
        ColumnRef? gRef = groups is null ? null : new ColumnRef(table, 3);
        return new Made(
            table,
            layer =>
            {
                var bars = (ErrorBarLayerSpec)layer;
                return new ErrorBarLayerSpec
                {
                    X = xRef,
                    YMin = minRef,
                    YMax = maxRef,
                    Style = bars.Style,
                    CapWidth = bars.CapWidth,
                    Label = bars.Label,
                    Position = bars.Position,
                    ColorBy = gRef,
                };
            },
            null
        );
    }

    /// <summary>A bar a cell over categories, by the categorical bar's path and the layer's position (rows G7b and G5a).</summary>
    private Made CellBars(CategoricalColumn x, CategoricalColumn? groups, List<(int X, int G, double Y)> cells)
    {
        var table = Table(
            ("x", x.WithCodes([.. cells.Select(c => c.X)])),
            ("y", Own(Column.Wrap(cells.Select(c => c.Y).ToArray()))),
            ("g", groups?.WithCodes([.. cells.Select(c => c.G)]))
        );
        var (xRef, yRef) = (new ColumnRef(table, 0), new ColumnRef(table, 1));
        ColumnRef? gRef = groups is null ? null : new ColumnRef(table, 2);
        return new Made(
            table,
            layer =>
            {
                var bar = (BarLayerSpec)layer;
                return new BarLayerSpec
                {
                    X = xRef,
                    Y = yRef,
                    Fill = bar.Fill,
                    Baseline = bar.Baseline,
                    Label = bar.Label,
                    Position = bar.Position,
                    ColorBy = gRef,
                };
            },
            null
        );
    }

    /// <summary>Returns a derived table of named columns, the group column last when there is one.</summary>
    private static DataTable Table(params (string Name, Column? Column)[] columns)
    {
        var table = DataTable.Create();
        foreach (var (name, column) in columns)
        {
            if (column is not null)
            {
                _ = table.Add(name, column);
            }
        }

        return table.Build();
    }

    /// <summary>
    /// Returns a derived position column (rows G5b and G5c): longs for a time axis, as its own columns (design §4.2, a time
    /// scale's columns are long), else doubles.
    /// </summary>
    private Column XColumn(double[] values, bool time, long origin)
    {
        if (!time)
        {
            return Own(Column.Wrap(values));
        }

        var longs = Column.Wrap(values.Select(v => origin + (long)Math.Round(v)).ToArray());
        _owned.Add(longs);
        return longs;
    }

    private Column<double> Own(Column<double> column)
    {
        _owned.Add(column);
        return column;
    }

    /// <summary>A stat's derived table, how the inner layer's node is made from the layer's, and an area's or a step's x ends.</summary>
    private readonly record struct Made(DataTable Table, Func<LayerSpec, LayerSpec> Make, (double Min, double Max)? XEnds);
}

/// <summary>
/// Where a stat's bins' edges and a density's or a moving average's range come from (design §4.5, row G6b): its x's and,
/// for 2D bins, its y's. A facet panel's own rows along a free direction; every panel's rows along a shared one.
/// </summary>
internal sealed class StatSource(StatRange x, StatRange? y)
{
    /// <summary>Gets the range of x.</summary>
    public StatRange X => x;

    /// <summary>Gets the range of y, or null for a stat of x only.</summary>
    public StatRange? Y => y;

    /// <summary>Returns the source of a layer's own rows: the whole columns, or a run of them.</summary>
    public static StatSource Own(Column x, Column? y, RowRange rows) =>
        new(new StatRange([(x, rows)]), y is null ? null : new StatRange([(y, rows)]));
}

/// <summary>
/// The rows an axis's range comes from (row G6b): pieces of columns, each over a run of rows; their union, found once and
/// kept, so that the panels that share it read it once.
/// </summary>
internal sealed class StatRange(IReadOnlyList<(Column Column, RowRange Rows)> pieces)
{
    private bool _ranged;
    private bool _found;
    private ScaleEnd _low;
    private ScaleEnd _high;
    private bool _finiteKnown;
    private IScale? _finiteTransform;
    private (double Min, double Max) _finite;

    /// <summary>Returns whether the range is a column's own over the rows a plan reads: a stat's of its own rows.</summary>
    public bool IsOwn(Column column, RowRange rows) => pieces.Count == 1 && ReferenceEquals(pieces[0].Column, column) && pieces[0].Rows == rows;

    /// <summary>Returns the union of the pieces' ranges of values that are not NaN; false when there is none.</summary>
    public bool TryGetRange(out ScaleEnd low, out ScaleEnd high)
    {
        if (!_ranged)
        {
            foreach (var (column, rows) in pieces)
            {
                if (!column.TryGetRange(rows, out var min, out var max))
                {
                    continue;
                }

                (_low, _high) = _found ? (Less(min, _low) ? min : _low, Less(_high, max) ? max : _high) : (min, max);
                _found = true;
            }

            _ranged = true;
        }

        (low, high) = (_low, _high);
        return _found;
    }

    /// <summary>
    /// Returns the least and greatest finite value of the pieces in the scale space (design §4.1): from their statistics when
    /// they are finite and no transform maps them, else by a pass of the workers; (+∞, −∞) for none.
    /// </summary>
    public (double Min, double Max) FiniteRange(IScale? transform, PlotWorkerPool pool)
    {
        if (_finiteKnown && ReferenceEquals(_finiteTransform, transform))
        {
            return _finite;
        }

        var (least, greatest) = (double.PositiveInfinity, double.NegativeInfinity);
        foreach (var (column, rows) in pieces)
        {
            var (min, max) =
                transform is null && column.TryGetRange(rows, out var low, out var high) && double.IsFinite(low.Value) && double.IsFinite(high.Value)
                    ? (low.Value, high.Value)
                    : StatWork.FiniteRange(column, transform, 0, pool, rows);
            (least, greatest) = (Math.Min(least, min), Math.Max(greatest, max));
        }

        (_finiteKnown, _finiteTransform, _finite) = (true, transform, (least, greatest));
        return _finite;
    }

    private static bool Less(ScaleEnd a, ScaleEnd b) => a.IsInteger && b.IsInteger ? a.Integer < b.Integer : a.Value < b.Value;
}
