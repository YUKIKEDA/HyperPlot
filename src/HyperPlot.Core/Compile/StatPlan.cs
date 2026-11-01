using HyperPlot.Rendering;

namespace HyperPlot;

/// <summary>
/// A layer with a stat (design §4.3, §5.5.3, row G5b): the workers make the stat's result as a small derived table when
/// the layer is made, and an inner layer without the stat draws it by the geom's usual path and the layer's position. A
/// column's new version makes the layer again (<see cref="Stale"/>).
/// </summary>
internal sealed class StatPlan : LayerPlan
{
    private readonly List<IDisposable> _owned = [];
    private readonly long[] _built;
    private readonly LayerPlan _inner;
    private readonly DataTable _table;
    private readonly Func<LayerSpec, LayerSpec> _make;
    private readonly (double Min, double Max)? _xEnds;

    public StatPlan(LayerSpec spec, PlanBuild build)
        : base(spec)
    {
        var groups = SplitPlan.Splits(spec) ? (CategoricalColumn)(spec.FillBy ?? spec.ColorBy)!.Value.Column : null;
        var (x, y) = spec switch
        {
            BarLayerSpec b => (b.X.Column, b.Y.IsDefault ? null : b.Y.Column),
            AreaLayerSpec a => (a.X.Column, null),
            StepLayerSpec s => (s.X.Column, null),
            ErrorBarLayerSpec e => (e.X.Column, e.YMin.Column),
            _ => throw new InvalidOperationException($"A {spec.GetType().Name} takes no stat."),
        };
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
        _built = new long[Columns.Length];
        for (var i = 0; i < _built.Length; i++)
        {
            _built[i] = Columns[i].Version;
        }

        var made = spec.Stat switch
        {
            BinStatSpec bin => Bins(spec, bin, x, groups, build),
            CountStatSpec => Counts(spec, (CategoricalColumn)x, groups, build.Pool),
            _ => Summaries(spec, (SummaryStatSpec)spec.Stat!, (CategoricalColumn)x, groups, y!, build.Pool, build.Layer),
        };
        (_table, _make, _xEnds) = (made.Table, made.Make, made.XEnds);
        _inner = Create(_make(spec), build);
    }

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

        // Counts and bins start at 0 (design §4.2): a step has no baseline of its own. A log y leaves it out, as a baseline.
        if (!ofX && Spec.Stat is BinStatSpec or CountStatSpec)
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
        double[] edges;
        long origin = 0;
        var uniform = true;
        if (time is not null)
        {
            // Calendar steps in the column's unit, or the given step from the least value; differences from the first edge.
            if (!x.TryGetRange(out var low, out var high))
            {
                edges = [];
            }
            else if (bin.Step is null)
            {
                var calendar = TimeTicks.BinEdges(low.Integer, high.Integer, time, bin.MaxBins);
                origin = calendar.Length > 0 ? calendar[0] : 0;
                edges = [.. calendar.Select(e => (double)(e - origin))];
                uniform = false;
            }
            else
            {
                origin = low.Integer;
                edges = StatWork.Edges(0, high.Integer - low.Integer, bin with { Anchor = bin.Anchor - origin }, build.Layer);
            }
        }
        else
        {
            var (min, max) =
                transform is null && x.TryGetRange(out var low, out var high) && double.IsFinite(low.Value) && double.IsFinite(high.Value)
                    ? (low.Value, high.Value)
                    : StatWork.FiniteRange(x, transform, 0, pool);
            edges = min <= max ? StatWork.Edges(min, max, bin, build.Layer) : [];
        }

        var bins = Math.Max(edges.Length - 1, 0);
        var nk = groups?.CategoryCount ?? 1;
        if (spec is BarLayerSpec && (long)bins * nk > MaxElements)
        {
            throw new ArgumentException(
                $"Layer {build.Layer} has {bins} bins in {nk} groups; a binned bar draws at most {MaxElements} rects (design §5.5.3). Give fewer bins."
            );
        }

        var counts = bins == 0 ? [] : StatWork.Bin(x, groups, transform, origin, edges, uniform, pool);
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
                values[(k * bins) + i] = bin.Output == BinOutput.Density && c > 0 ? c / (n * (edges[i + 1] - edges[i])) : c;
            }
        }

        double Data(double s)
        {
            // A time x stays a difference from the first edge until its column adds the origin as a long.
            if (time is not null)
            {
                return s;
            }

            if (transform is null)
            {
                return s;
            }

            Span<double> one = [s];
            transform.Inverse(one, one);
            return one[0];
        }

        if (spec is BarLayerSpec bar)
        {
            return Rects(bar, edges, values, nk, groups, Data, time is not null, origin);
        }

        // Points at the bins' middles, the groups in turn at each (design §5.5.3).
        var xs = new double[bins * nk];
        var ys = new double[bins * nk];
        var codes = new int[bins * nk];
        for (var i = 0; i < bins; i++)
        {
            var middle = Data((edges[i] + edges[i + 1]) / 2);
            for (var k = 0; k < nk; k++)
            {
                (xs[(i * nk) + k], ys[(i * nk) + k], codes[(i * nk) + k]) = (middle, values[(k * bins) + i], k);
            }
        }

        var table = Table(("x", XColumn(xs, time is not null, origin)), ("y", Own(Column.Wrap(ys))), ("g", groups?.WithCodes(codes)));
        var (xRef, yRef, gRef) = (new ColumnRef(table, 0), new ColumnRef(table, 1), groups is null ? (ColumnRef?)null : new ColumnRef(table, 2));
        (double, double)? ends = bins == 0 ? null : (Data(edges[0]) + (time is null ? 0 : origin), Data(edges[^1]) + (time is null ? 0 : origin));
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
    private Made Counts(LayerSpec spec, CategoricalColumn x, CategoricalColumn? groups, PlotWorkerPool pool)
    {
        var counts = StatWork.Count(x, groups, pool);
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

        var moments = StatWork.Summarize(x, groups, y, customs, pool);
        var ranks = ps.Count == 0 ? [] : StatWork.Select(x, groups, y, moments.Count, ps, pool);
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
    /// Returns a derived x column (row G5b): longs for a time x, as its own columns (design §4.2, a time scale's columns are
    /// long), else doubles.
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
