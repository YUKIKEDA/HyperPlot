using System.Numerics;
using HyperPlot.Rendering;

namespace HyperPlot;

/// <summary>
/// Where each category's rows lie in a categorical column (design §5.5.3, row G7c): its first and last row, its count, and
/// its count in each chunk. A category whose rows fill [first, last] alone lies in one run. The column is immutable, so this
/// is found once a column, by one pass of the workers over its numbers.
/// </summary>
internal sealed class CategoryRuns
{
    private CategoryRuns(int categories, int chunks)
    {
        First = new long[categories];
        Last = new long[categories];
        Count = new long[categories];
        ChunkCounts = new long[(long)categories * chunks];
        Array.Fill(First, long.MaxValue);
        Array.Fill(Last, -1);
    }

    /// <summary>Gets each category's first row, or <see cref="long.MaxValue"/> for an unused one.</summary>
    public long[] First { get; }

    /// <summary>Gets each category's last row, or −1 for an unused one.</summary>
    public long[] Last { get; }

    /// <summary>Gets each category's number of rows.</summary>
    public long[] Count { get; }

    /// <summary>Gets each category's number of rows in each chunk: chunk k, category c at k · n + c.</summary>
    public long[] ChunkCounts { get; }

    /// <summary>Returns whether a used category's rows lie in one run, with no other row among them.</summary>
    public bool IsRun(int category) => Count[category] > 0 && Last[category] - First[category] + 1 == Count[category];

    /// <summary>Returns the runs of a column, finding them on the workers the first time and waiting for them.</summary>
    public static CategoryRuns Of(CategoricalColumn column, PlotWorkerPool pool)
    {
        if (Volatile.Read(ref column.Runs) is { } known)
        {
            return known;
        }

        var runs = Find(column, pool, RowRange.All);
        return Interlocked.CompareExchange(ref column.Runs, runs, null) ?? runs;
    }

    /// <summary>
    /// Returns the runs of a column's categories inside a run of its rows (row G6b): a facet panel's. The whole column's are the
    /// column's, found once; a run's are found each time, by a pass over its chunks only, and the layer keeps them.
    /// </summary>
    public static CategoryRuns Of(CategoricalColumn column, PlotWorkerPool pool, RowRange rows) =>
        rows == RowRange.All ? Of(column, pool) : Find(column, pool, rows);

    /// <summary>Finds the runs over a run of a column's rows: a pass of the workers over the chunks it lies in.</summary>
    private static CategoryRuns Find(CategoricalColumn column, PlotWorkerPool pool, RowRange rows)
    {
        var n = column.CategoryCount;
        var runs = new CategoryRuns(n, column.ChunkCount);
        var sync = new object();
        var (firstChunk, endChunk) = StatWork.ChunksOf(column, rows);
        pool.RunChunks(
                endChunk - firstChunk,
                at =>
                {
                    var chunk = firstChunk + at;
                    var (lo, hi) = StatWork.PartOf(column, chunk, rows);
                    var codes = column.GetCodes(chunk)[lo..hi];
                    var start = ((long)chunk << Column.ChunkShift) + lo;
                    var counts = runs.ChunkCounts.AsSpan(chunk * n, n);
                    Span<long> first = n <= 256 ? stackalloc long[n] : new long[n];
                    Span<long> last = n <= 256 ? stackalloc long[n] : new long[n];
                    first.Fill(long.MaxValue);
                    last.Fill(-1);
                    for (var i = 0; i < codes.Length; i++)
                    {
                        var code = codes[i];
                        if (code >= 0)
                        {
                            counts[code]++;
                            first[code] = Math.Min(first[code], start + i);
                            last[code] = start + i;
                        }
                    }

                    lock (sync)
                    {
                        for (var c = 0; c < n; c++)
                        {
                            runs.Count[c] += counts[c];
                            runs.First[c] = Math.Min(runs.First[c], first[c]);
                            runs.Last[c] = Math.Max(runs.Last[c], last[c]);
                        }
                    }
                }
            )
            .GetAwaiter()
            .GetResult();
        return runs;
    }
}

/// <summary>
/// The rows of the categories that do not lie in one run, gathered from a source column into a native column a category
/// (design §5.5.3, row G7c), in the rows' order. The sizes are the categories' counts, which never change: a new version
/// of the source is written again into the same columns, which are then invalidated.
/// </summary>
internal abstract class GatherChannel : IDisposable
{
    /// <summary>Gets a category's column, or null for one not gathered.</summary>
    public abstract Column? Target(int category);

    /// <summary>
    /// Returns a channel for a source column, with a column for each category of a count above 0 in <paramref name="counts"/>.
    /// The column makes it (a virtual method, not a switch over the element types), so that NativeAOT compiles the gathers,
    /// pyramids, and M4 of the element types an application builds only (PR #173's review).
    /// </summary>
    public static GatherChannel For(Column source, ReadOnlySpan<long> counts) => source.MakeGather(counts);

    /// <summary>
    /// Writes a chunk's rows of the gathered categories at their positions, which it moves on: the rows from
    /// <paramref name="offset"/>, one a code.
    /// </summary>
    public abstract void Copy(int chunk, int offset, ReadOnlySpan<int> codes, Span<long> positions);

    /// <summary>Invalidates the gathered columns after a pass wrote them.</summary>
    public abstract void Invalidate();

    public abstract void Dispose();

    internal sealed class StoredGather<T> : GatherChannel
        where T : unmanaged, INumber<T>
    {
        private readonly Column<T> _source;
        private readonly NativeColumnStorage<T>?[] _storages;
        private readonly Column<T>?[] _columns;

        public StoredGather(Column<T> source, ReadOnlySpan<long> counts)
        {
            _source = source;
            (_storages, _columns) = (new NativeColumnStorage<T>?[counts.Length], new Column<T>?[counts.Length]);
            for (var c = 0; c < counts.Length; c++)
            {
                if (counts[c] > 0)
                {
                    _storages[c] = new NativeColumnStorage<T>(counts[c]);
                    _columns[c] = new Column<T>(_storages[c]!, counts[c]);
                }
            }
        }

        public override Column? Target(int category) => _columns[category];

        public override void Copy(int chunk, int offset, ReadOnlySpan<int> codes, Span<long> positions)
        {
            var values = _source.GetChunk(chunk)[offset..];
            for (var i = 0; i < codes.Length; i++)
            {
                var code = codes[i];
                if (code >= 0 && _storages[code] is { } storage)
                {
                    Write(storage, _columns[code]!.Length, positions[code]++, values[i]);
                }
            }
        }

        public override void Invalidate()
        {
            foreach (var column in _columns)
            {
                column?.Invalidate();
            }
        }

        public override void Dispose()
        {
            foreach (var column in _columns)
            {
                column?.Dispose();
            }
        }
    }

    internal sealed class UniformGather<T> : GatherChannel
        where T : unmanaged, INumber<T>
    {
        private readonly UniformColumn _source;
        private readonly NativeColumnStorage<T>?[] _storages;
        private readonly Column<T>?[] _columns;

        public UniformGather(UniformColumn source, ReadOnlySpan<long> counts)
        {
            _source = source;
            (_storages, _columns) = (new NativeColumnStorage<T>?[counts.Length], new Column<T>?[counts.Length]);
            for (var c = 0; c < counts.Length; c++)
            {
                if (counts[c] > 0)
                {
                    _storages[c] = new NativeColumnStorage<T>(counts[c]);
                    _columns[c] = new Column<T>(_storages[c]!, counts[c]);
                }
            }
        }

        public override Column? Target(int category) => _columns[category];

        public override void Copy(int chunk, int offset, ReadOnlySpan<int> codes, Span<long> positions)
        {
            var start = ((long)chunk << Column.ChunkShift) + offset;
            for (var i = 0; i < codes.Length; i++)
            {
                var code = codes[i];
                if (code >= 0 && _storages[code] is { } storage)
                {
                    // An integer column's values are exact longs; a floating one's are its doubles (design §3.1).
                    var value = _source.IsInteger
                        ? T.CreateTruncating(_source.IntegerAt(start + i))
                        : T.CreateTruncating(_source.GetValue(start + i));
                    Write(storage, _columns[code]!.Length, positions[code]++, value);
                }
            }
        }

        public override void Invalidate()
        {
            foreach (var column in _columns)
            {
                column?.Invalidate();
            }
        }

        public override void Dispose()
        {
            foreach (var column in _columns)
            {
                column?.Dispose();
            }
        }
    }

    internal sealed class CategoryGather : GatherChannel
    {
        private readonly CategoricalColumn _source;
        private readonly int[]?[] _codes;
        private readonly CategoricalColumn?[] _columns;

        public CategoryGather(CategoricalColumn source, ReadOnlySpan<long> counts)
        {
            _source = source;
            (_codes, _columns) = (new int[]?[counts.Length], new CategoricalColumn?[counts.Length]);
            for (var c = 0; c < counts.Length; c++)
            {
                if (counts[c] > 0)
                {
                    _codes[c] = new int[counts[c]];
                    _columns[c] = source.WithCodes(_codes[c]!);
                }
            }
        }

        public override Column? Target(int category) => _columns[category];

        public override void Copy(int chunk, int offset, ReadOnlySpan<int> codes, Span<long> positions)
        {
            var values = _source.GetCodes(chunk)[offset..];
            for (var i = 0; i < codes.Length; i++)
            {
                var code = codes[i];
                if (code >= 0 && _codes[code] is { } target)
                {
                    target[positions[code]++] = values[i];
                }
            }
        }

        // Categories never change: nothing to invalidate or free.
        public override void Invalidate() { }

        public override void Dispose() { }
    }

    private static void Write<T>(NativeColumnStorage<T> storage, long length, long position, T value)
        where T : unmanaged, INumber<T>
    {
        var chunk = (int)(position >> Column.ChunkShift);
        var first = (long)chunk << Column.ChunkShift;
        storage.GetWritableChunk(chunk, (int)Math.Min(Column.ChunkLength, length - first))[(int)(position - first)] = value;
    }
}

/// <summary>
/// A layer split into a series a category by a categorical <see cref="LayerSpec.ColorBy"/> or <see cref="LayerSpec.FillBy"/>
/// (design §5.5.3, row G7c): each category is a plan of the layer's own geom, over the run of its rows when they lie in one,
/// else over its rows gathered into columns of its own. Records each in its category's color; the plan sees one layer.
/// </summary>
internal sealed class SplitPlan : LayerPlan
{
    private readonly List<Part> _parts = [];
    private readonly GatherChannel[] _gathers = [];
    private readonly Column[] _sources = [];
    private readonly long[] _gatheredVersions = [];
    private readonly PlotPyramid[] _gatheredPyramids = [];
    private readonly CategoryRuns? _runs;
    private readonly CategoricalColumn? _categories;
    private long[] _starts = [];

    public SplitPlan(LayerSpec spec, PlanBuild build)
        : this(spec, build, RowRange.All) { }

    /// <summary>Initializes a new instance of the <see cref="SplitPlan"/> class over a run of rows (row G6b): a facet panel's.</summary>
    public SplitPlan(LayerSpec spec, PlanBuild build, RowRange rows)
        : base(spec, rows)
    {
        // Which column splits what: the fill by FillBy (or ColorBy), a fill geom's outline by ColorBy, a line by ColorBy.
        var fillBy = IsFill(spec) ? (spec.FillBy ?? spec.ColorBy) : null;
        var strokeBy = spec.ColorBy;
        var split = (fillBy ?? strokeBy)!.Value;
        _categories = (CategoricalColumn)split.Column;
        _runs = CategoryRuns.Of(_categories, build.Pool, rows);

        // The source columns: every channel's, once each, then the categories.
        var channels = new ColumnRef[4];
        var sources = new List<Column>();
        foreach (var ofX in (ReadOnlySpan<bool>)[true, false])
        {
            var count = AxisChannels(spec, ofX, channels);
            for (var c = 0; c < count; c++)
            {
                if (!sources.Contains(channels[c].Column))
                {
                    sources.Add(channels[c].Column);
                }
            }
        }

        // A text layer's labels go with its points (row G8).
        if (spec is TextLayerSpec { Text.Column: var labels } && !sources.Contains(labels))
        {
            sources.Add(labels);
        }

        _sources = [.. sources];
        SetColumns([.. sources, _categories]);

        // The categories that do not lie in one run are gathered, once, before their plans and pyramids are made.
        var counts = new long[_categories.CategoryCount];
        for (var c = 0; c < counts.Length; c++)
        {
            counts[c] = _runs.IsRun(c) ? 0 : _runs.Count[c];
        }

        if (counts.AsSpan().ContainsAnyExcept(0))
        {
            _gathers = new GatherChannel[_sources.Length];
            for (var s = 0; s < _sources.Length; s++)
            {
                _gathers[s] = GatherChannel.For(_sources[s], counts);
            }

            _gatheredVersions = new long[_sources.Length];
            Gather(build.Pool);
        }

        // An area's or a band's outline goes with each category's fill only when ColorBy splits both by the same column;
        // else it is drawn apart: split by its own column, or one line in the layer's color when only the fill is split.
        var pyramids = new List<PlotPyramid>();
        var outlineApart = OutlineApart(spec);
        var child = outlineApart ? WithoutOutline(spec) : spec;
        AddParts(child, build, pyramids);
        if (outlineApart && strokeBy is { } byStroke)
        {
            // The outline split by its own column (design §4.1): the outline's lines as lines of their own.
            var lines = OutlineLines(spec);
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                if (byStroke.Column == _categories)
                {
                    AddParts(line, build, pyramids);
                }
                else
                {
                    // Another column of the same categories: its own runs and gathers come with a split of its own.
                    _parts.Add(new Part(-1, new SplitPlan(line with { ColorBy = byStroke }, build, rows), null, MakeOutline(index, byStroke)));
                }
            }
        }
        else if (outlineApart)
        {
            // Only the fill is split: the outline is one line in the layer's series color.
            var lines = OutlineLines(spec);
            for (var index = 0; index < lines.Length; index++)
            {
                _parts.Add(new Part(-1, CreateGeom(lines[index], build, rows), null, MakeOutline(index, null)));
            }
        }

        _gatheredPyramids = [.. pyramids];
    }

    /// <summary>Gets or sets the palette of the categories' colors: the ordinal scale's, or the theme's (design §4.2).</summary>
    public Palette? Palette { get; set; }

    /// <summary>Gets the number of category series the layer is split into. Tests only.</summary>
    public int SeriesCount => _parts.Count(p => p.Category >= 0);

    /// <summary>Gets the number of categories whose rows were gathered. Tests only.</summary>
    public int GatheredCount => _parts.Count(p => p.Category >= 0 && p.Table is not null);

    /// <summary>Returns whether a layer is split: a categorical column in its color channels (row G7c).</summary>
    public static bool Splits(LayerSpec spec) =>
        spec.ColorBy is { Column: CategoricalColumn } || (IsFill(spec) && spec.FillBy is { Column: CategoricalColumn });

    /// <summary>Passes a new node of the same columns on to every part's own node.</summary>
    protected override void OnSpecChanged(LayerSpec before, LayerSpec after)
    {
        // The same node again, or a new node of the same looks (a host that builds its spec each frame): its parts' nodes
        // stand, and making them anew would allocate on every frame.
        if (ReferenceEquals(after, before) || SameLooks(before, after))
        {
            return;
        }

        foreach (var part in _parts)
        {
            part.Plan.Take(part.Make(after));
        }
    }

    public override void WaitForIndexes(PlotWorkerPool pool)
    {
        if (_gathers.Length > 0)
        {
            var changed = false;
            for (var s = 0; s < _sources.Length; s++)
            {
                changed |= _sources[s].Version != _gatheredVersions[s];
            }

            if (changed)
            {
                Gather(pool);
                foreach (var gather in _gathers)
                {
                    gather.Invalidate();
                }
            }

            foreach (var pyramid in _gatheredPyramids)
            {
                pyramid.WhenComplete().GetAwaiter().GetResult();
            }
        }

        foreach (var part in _parts)
        {
            part.Plan.WaitForIndexes(pool);
        }
    }

    public override void BeginLearning()
    {
        foreach (var part in _parts)
        {
            part.Plan.BeginLearning();
        }
    }

    public override void AddRanges(RangeSink sink, bool ofX, DataTable data)
    {
        foreach (var part in _parts)
        {
            part.Plan.AddRanges(sink, ofX, part.Table ?? data);
        }
    }

    public override void Record(RecordContext context)
    {
        var palette = Colors?.Palette ?? Palette!;
        var dodge = PositionOf(Spec) == PositionKind.Dodge;
        foreach (var part in _parts)
        {
            part.Plan.SeriesColor = part.Category >= 0 ? palette.Series(part.Category) : SeriesColor;
            part.Plan.Index = Index;
            part.Plan.Colors = Colors;
            part.Plan.DodgeSlot = dodge && part.Category >= 0 ? (part.Category, _categories!.CategoryCount) : null;
            if (part.Plan is SplitPlan inner)
            {
                inner.Palette = palette;
            }

            part.Plan.Record(context);
            context.Flush();
        }
    }

    public override void Release()
    {
        foreach (var part in _parts)
        {
            part.Plan.Release();
        }

        foreach (var gather in _gathers)
        {
            gather.Dispose();
        }
    }

    /// <summary>
    /// Returns whether two nodes of the same layer draw alike (row G7c): the same kind, label, and looks (stroke, fill, outline,
    /// baseline, width, mode, caps). Their channels are the same, or the diff would have made the layer again.
    /// </summary>
    internal static bool SameLooks(LayerSpec before, LayerSpec after) =>
        string.Equals(before.Label, after.Label, StringComparison.Ordinal)
        && PositionOf(before) == PositionOf(after)
        && DodgePadding(before).Equals(DodgePadding(after))
        && (before, after) switch
        {
            (LineLayerSpec b, LineLayerSpec a) => b.Style == a.Style,
            (StepLayerSpec b, StepLayerSpec a) => b.Style == a.Style && b.Mode == a.Mode,
            (AreaLayerSpec b, AreaLayerSpec a) => b.Fill == a.Fill && b.Outline == a.Outline && b.Baseline.Equals(a.Baseline),
            (BandLayerSpec b, BandLayerSpec a) => b.Fill == a.Fill && b.Outline == a.Outline,
            (BarLayerSpec b, BarLayerSpec a) => b.Fill == a.Fill && b.Baseline.Equals(a.Baseline) && b.Width == a.Width,
            (ErrorBarLayerSpec b, ErrorBarLayerSpec a) => b.Style == a.Style && b.CapWidth.Equals(a.CapWidth),
            (RuleLayerSpec b, RuleLayerSpec a) => b.Style == a.Style,
            (RectLayerSpec b, RectLayerSpec a) => b.Fill == a.Fill,
            (SegmentLayerSpec b, SegmentLayerSpec a) => b.Style == a.Style,
            (TextLayerSpec b, TextLayerSpec a) => b.Color == a.Color
                && b.Size.Equals(a.Size)
                && b.Horizontal == a.Horizontal
                && b.Vertical == a.Vertical
                && b.OffsetX.Equals(a.OffsetX)
                && b.OffsetY.Equals(a.OffsetY)
                && b.Rotation == a.Rotation
                && ReferenceEquals(b.Format, a.Format),
            _ => false,
        };

    private static bool IsFill(LayerSpec spec) => spec is AreaLayerSpec or BandLayerSpec or BarLayerSpec or RectLayerSpec;

    private static StrokeSpec? Outline(LayerSpec spec) =>
        spec switch
        {
            AreaLayerSpec a => a.Outline,
            BandLayerSpec b => b.Outline,
            _ => null,
        };

    private static LayerSpec WithoutOutline(LayerSpec spec) =>
        spec switch
        {
            AreaLayerSpec a => a with { Outline = null },
            BandLayerSpec b => b with { Outline = null },
            _ => spec,
        };

    /// <summary>Returns the lines an area's or a band's outline draws, as line layers of the outline's stroke.</summary>
    private static LineLayerSpec[] OutlineLines(LayerSpec spec) =>
        spec switch
        {
            AreaLayerSpec a =>
            [
                new LineLayerSpec
                {
                    X = a.X,
                    Y = a.Y,
                    Style = a.Outline!.Value,
                },
            ],
            BandLayerSpec b =>
            [
                new LineLayerSpec
                {
                    X = b.X,
                    Y = b.YMin,
                    Style = b.Outline!.Value,
                },
                new LineLayerSpec
                {
                    X = b.X,
                    Y = b.YMax,
                    Style = b.Outline!.Value,
                },
            ],
            _ => [],
        };

    /// <summary>Returns how a part's node is made from the layer's: its channels through a map, the outline apart or not.</summary>
    private static Func<LayerSpec, LayerSpec> MakeChild(LayerSpec first, Func<ColumnRef, ColumnRef> map)
    {
        var apart = OutlineApart(first);
        return layer => Remap(apart ? WithoutOutline(layer) : layer, map);
    }

    /// <summary>
    /// Returns whether a fill geom's outline is drawn apart from its categories' fills (design §4.1): it has one, and ColorBy
    /// does not split it by the fill's own column (no ColorBy, or a FillBy of another column).
    /// </summary>
    private static bool OutlineApart(LayerSpec spec) =>
        IsFill(spec) && Outline(spec) is not null && !(spec.ColorBy is { } by && (spec.FillBy ?? by) == by);

    /// <summary>Returns how an outline line's node is made from the layer's: the index-th line of its outline, split by a column or not.</summary>
    private static Func<LayerSpec, LayerSpec> MakeOutline(int index, ColumnRef? by) =>
        layer => by is { } column ? OutlineLines(layer)[index] with { ColorBy = column } : OutlineLines(layer)[index];

    /// <summary>Returns a layer's spec with its channels through a map and without color channels: a category's own layer.</summary>
    private static LayerSpec Remap(LayerSpec spec, Func<ColumnRef, ColumnRef> map)
    {
        var plain = spec with { ColorBy = null, FillBy = null };
        return plain switch
        {
            LineLayerSpec l => l with { X = map(l.X), Y = map(l.Y) },
            StepLayerSpec s => s with { X = map(s.X), Y = map(s.Y) },
            AreaLayerSpec a => a with { X = map(a.X), Y = map(a.Y) },
            BandLayerSpec b => b with { X = map(b.X), YMin = map(b.YMin), YMax = map(b.YMax) },
            BarLayerSpec b => b with { X = map(b.X), Y = map(b.Y) },
            ErrorBarLayerSpec e => e with { X = map(e.X), YMin = map(e.YMin), YMax = map(e.YMax) },
            RuleLayerSpec r => r with { X = r.X.IsDefault ? r.X : map(r.X), Y = r.Y.IsDefault ? r.Y : map(r.Y) },
            RectLayerSpec r => r with { XMin = map(r.XMin), XMax = map(r.XMax), YMin = map(r.YMin), YMax = map(r.YMax) },
            SegmentLayerSpec s => s with { X = map(s.X), Y = map(s.Y), XEnd = map(s.XEnd), YEnd = map(s.YEnd) },
            TextLayerSpec t => t with { X = map(t.X), Y = map(t.Y), Text = map(t.Text) },
            _ => throw new InvalidOperationException($"A {spec.GetType().Name} is not split by categories."),
        };
    }

    /// <summary>Adds a part a used category of a column: a plan over its run, or over its gathered rows.</summary>
    private void AddParts(LayerSpec spec, PlanBuild build, List<PlotPyramid> pyramids)
    {
        var runs = _runs!;
        for (var c = 0; c < _categories!.CategoryCount; c++)
        {
            if (runs.Count[c] == 0)
            {
                continue;
            }

            if (runs.IsRun(c))
            {
                _parts.Add(
                    new Part(c, CreateGeom(Remap(spec, r => r), build, new RowRange(runs.First[c], runs.Last[c] + 1)), null, MakeChild(spec, r => r))
                );
                continue;
            }

            // The category's own table of its gathered columns, in the sources' order.
            var table = DataTable.Create();
            for (var s = 0; s < _sources.Length; s++)
            {
                _ = table.Add($"{s}", _gathers[s].Target(c)!);
            }

            var built = table.Build();
            ColumnRef Map(ColumnRef r) => new(built, Array.IndexOf(_sources, r.Column));
            var plan = CreateGeom(Remap(spec, Map), build, RowRange.All);
            _parts.Add(new Part(c, plan, built, MakeChild(spec, Map)));
            foreach (var column in plan.Columns)
            {
                if (column is IPyramidColumn && build.Held(column) is { } pyramid)
                {
                    pyramids.Add(pyramid);
                }
            }
        }
    }

    /// <summary>Writes the gathered categories' rows from the sources' current values: a pass of the workers over the chunks.</summary>
    private void Gather(PlotWorkerPool pool)
    {
        for (var s = 0; s < _sources.Length; s++)
        {
            _gatheredVersions[s] = _sources[s].Version;
        }

        GatherRows(pool, _categories!, _runs!, _gathers, ref _starts, Rows);
    }

    /// <summary>
    /// Writes the rows of the categories that do not lie in one run into their gathered columns (design §5.5.3, rows G7c and
    /// G5a): a pass of the workers over the chunks, each category's rows in their order.
    /// </summary>
    internal static void GatherRows(
        PlotWorkerPool pool,
        CategoricalColumn categories,
        CategoryRuns runs,
        GatherChannel[] gathers,
        ref long[] chunkStarts,
        RowRange rows
    )
    {
        var n = categories.CategoryCount;

        // Each category's position at each chunk's start: its rows in the chunks before. The counts never change, so the sums
        // are made once, by the first pass.
        var starts = chunkStarts;
        if (starts.Length != runs.ChunkCounts.Length)
        {
            starts = chunkStarts = new long[runs.ChunkCounts.Length];
            for (var k = 1; k < categories.ChunkCount; k++)
            {
                for (var c = 0; c < n; c++)
                {
                    starts[(k * n) + c] = starts[((k - 1) * n) + c] + runs.ChunkCounts[((k - 1) * n) + c];
                }
            }
        }

        // Only the chunks the run lies in, and only its rows: the counts a chunk holds are the run's (row G6b).
        var (firstChunk, endChunk) = StatWork.ChunksOf(categories, rows);
        pool.RunChunks(
                endChunk - firstChunk,
                at =>
                {
                    var chunk = firstChunk + at;
                    var (lo, hi) = StatWork.PartOf(categories, chunk, rows);
                    var start = starts.AsSpan(chunk * n, n);
                    Span<long> positions = n <= 256 ? stackalloc long[n] : new long[n];
                    var codes = categories.GetCodes(chunk)[lo..hi];
                    foreach (var gather in gathers)
                    {
                        start.CopyTo(positions);
                        gather.Copy(chunk, lo, codes, positions);
                    }
                }
            )
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>One series: its category (−1 for the layer's own color), its plan, and its table when its rows were gathered.</summary>
    private readonly record struct Part(int Category, LayerPlan Plan, DataTable? Table, Func<LayerSpec, LayerSpec> Make);
}
