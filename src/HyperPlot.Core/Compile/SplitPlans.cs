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

        var runs = Find(new ColumnKeys(column), pool, RowRange.All);
        return Interlocked.CompareExchange(ref column.Runs, runs, null) ?? runs;
    }

    /// <summary>
    /// Returns the runs of a column's categories inside a run of its rows (row G6b): a facet panel's. The whole column's are the
    /// column's, found once; a run's are found each time, by a pass over its chunks only, and the layer keeps them.
    /// </summary>
    public static CategoryRuns Of(CategoricalColumn column, PlotWorkerPool pool, RowRange rows) =>
        rows == RowRange.All ? Of(column, pool) : Find(new ColumnKeys(column), pool, rows);

    /// <summary>
    /// Finds the runs of the keys of rows over a run of them (rows G6b and G7c): a pass of the workers over the chunks it
    /// lies in. A facet grid's keys are the pairs of its two columns' categories.
    /// </summary>
    public static CategoryRuns Find(RowKeys keys, PlotWorkerPool pool, RowRange rows)
    {
        var column = keys.Column;
        var n = keys.Count;
        var runs = new CategoryRuns(n, column.ChunkCount);
        var sync = new object();
        var (firstChunk, endChunk) = StatWork.ChunksOf(column, rows);
        pool.RunChunks(
                endChunk - firstChunk,
                at =>
                {
                    var chunk = firstChunk + at;
                    var (lo, hi) = StatWork.PartOf(column, chunk, rows);
                    var scratch = keys.Scratch();
                    var codes = keys.Codes(chunk, scratch)[lo..hi];
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

                    keys.Return(scratch);
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
/// A point layer on the index path is split by its <see cref="PointLayerSpec.ShapeBy"/> too (design §5.5.2 "大きさと形の
/// チャネル", row R3f): by the shape's column alone, or by the pairs of a color category and a shape category that have rows.
/// </summary>
internal sealed class SplitPlan : LayerPlan, ISourceRows
{
    private readonly List<Part> _parts = [];
    private readonly GatherChannel[] _gathers = [];
    private readonly Column[] _sources = [];
    private readonly long[] _gatheredVersions = [];
    private readonly PlotPyramid[] _gatheredPyramids = [];
    private readonly CategoryRuns? _runs;
    private readonly CategoricalColumn? _categories;

    // What a part's number is a key of: the categories' column, or the pairs of a color and a shape category (row R3f).
    private readonly RowKeys? _keys;

    // The shape categories a key is taken modulo, when the keys are pairs; 0 when they are not.
    private readonly int _shapeCount;

    // Whether a key gives the part its color, and its shape (row R3f).
    private readonly bool _colored = true;
    private readonly bool _shaped;
    private long[] _starts = [];

    // The gather of the sources' newer versions into the same columns, on the store's queue in a frame that does not wait
    // (row W1b2, Issue #297 Decision 4).
    private GatherMaking? _regather;

    public SplitPlan(LayerSpec spec, PlanBuild build)
        : this(spec, build, RowRange.All) { }

    /// <summary>Initializes a new instance of the <see cref="SplitPlan"/> class over a run of rows (row G6b): a facet panel's.</summary>
    public SplitPlan(LayerSpec spec, PlanBuild build, RowRange rows)
        : base(spec, rows)
    {
        // Which column splits what: the fill by FillBy (or ColorBy), a fill geom's outline by ColorBy, a line by ColorBy.
        var fillBy = IsFill(spec) ? (spec.FillBy ?? spec.ColorBy) : null;
        var strokeBy = spec.ColorBy;
        var color = (fillBy ?? strokeBy) is { Column: CategoricalColumn } by ? (CategoricalColumn)by.Column : null;
        var shape = spec is PointLayerSpec { Indexed: true, ShapeBy.Column: CategoricalColumn shapes } ? shapes : null;
        if (shape is not null && color is not null && !ReferenceEquals(shape, color))
        {
            // The pairs of a color category and a shape category: the key is the color's number times the shapes, plus the shape's.
            _categories = color;
            (_shaped, _shapeCount) = (true, shape.CategoryCount);
            _keys = new PairKeys(
                color,
                [.. Enumerable.Range(0, color.CategoryCount)],
                color.CategoryCount,
                shape,
                [.. Enumerable.Range(0, shape.CategoryCount)],
                shape.CategoryCount
            );
            _runs = CategoryRuns.Find(_keys, build.Pool, rows);
        }
        else
        {
            _categories = shape ?? color!;
            (_colored, _shaped) = (color is not null, shape is not null);
            _keys = new ColumnKeys(_categories);
            _runs = CategoryRuns.Of(_categories, build.Pool, rows);
        }

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

        // A point layer's size and shape columns go with its points on the CPU path (row R3f).
        if (spec is PointLayerSpec { Indexed: false } marked)
        {
            foreach (var channel in (ReadOnlySpan<ColumnRef?>)[marked.SizeBy, marked.ShapeBy])
            {
                if (channel is { } reference && !sources.Contains(reference.Column))
                {
                    sources.Add(reference.Column);
                }
            }
        }

        // A text layer's labels go with its points (row G8).
        if (spec is TextLayerSpec { Text.Column: var labels } && !sources.Contains(labels))
        {
            sources.Add(labels);
        }

        _sources = [.. sources];
        SetColumns(_shapeCount > 0 ? [.. sources, _categories, shape!] : [.. sources, _categories]);

        // The categories that do not lie in one run are gathered, once, before their plans and pyramids are made.
        var counts = new long[_keys.Count];
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

    /// <summary>Gets the plans of the parts in their order. Tests only.</summary>
    internal IEnumerable<LayerPlan> Parts => _parts.Select(p => p.Plan);

    /// <summary>Gets the number of category series the layer is split into. Tests only.</summary>
    public int SeriesCount => _parts.Count(p => p.Category >= 0);

    /// <summary>Gets the number of categories whose rows were gathered. Tests only.</summary>
    public int GatheredCount => _parts.Count(p => p.Category >= 0 && p.Table is not null);

    /// <summary>Returns whether a layer is split: a categorical column in its color channels (row G7c).</summary>
    public static bool Splits(LayerSpec spec) =>
        spec.ColorBy is { Column: CategoricalColumn }
        || (IsFill(spec) && spec.FillBy is { Column: CategoricalColumn })
        || spec is PointLayerSpec { Indexed: true, ShapeBy.Column: CategoricalColumn };

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

    /// <summary>Gets a value indicating whether a part's last recording left out what the frame's budget did not send: a part's pages of points (row R3a).</summary>
    public override bool RecordedProvisional
    {
        get
        {
            foreach (var part in _parts)
            {
                if (part.Plan.RecordedProvisional)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public override LayerReadiness Poll(LayerIndexes indexes)
    {
        if (_gathers.Length > 0)
        {
            var changed = false;
            for (var s = 0; s < _sources.Length; s++)
            {
                changed |= _sources[s].Version != _gatheredVersions[s];
            }

            if (_regather is { IsDone: true } done)
            {
                // The gather ended: the columns hold the new values, and their pyramids are built for them.
                _regather = null;
                if (done.Error is { } error)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
                }

                foreach (var gather in _gathers)
                {
                    gather.Invalidate();
                }
            }

            if (changed && _regather is null && (indexes.Waits || indexes.Makings is null))
            {
                Gather(indexes.Pool);
                foreach (var gather in _gathers)
                {
                    gather.Invalidate();
                }
            }
            else if (changed && _regather is null)
            {
                // Gathered into the same columns off the frame (Issue #297 Decision 4): until it ends the parts draw what the
                // columns hold, the frame provisional.
                for (var s = 0; s < _sources.Length; s++)
                {
                    _gatheredVersions[s] = _sources[s].Version;
                }

                _regather = new GatherMaking(this, indexes.Pool);
                indexes.Makings!.Post(_regather);
            }

            // The gathered series' pyramids: built while M4 reads what is there (row W1b1).
            foreach (var pyramid in _gatheredPyramids)
            {
                _ = indexes.Complete(pyramid);
            }
        }

        var readiness = _regather is null ? LayerReadiness.Ready : LayerReadiness.Previous;
        foreach (var part in _parts)
        {
            readiness = LayerIndexes.Join(readiness, part.Plan.Poll(indexes));
        }

        return readiness;
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

    /// <summary>
    /// Answers for each category's part, numbered by its category (row I2a); a gathered part's rows are taken back to the rows
    /// they were gathered from (Issue #337 Decision 3). The outline parts repeat the parts' series and do not answer.
    /// </summary>
    internal override void Hit(ref HitScan scan)
    {
        var (base0, width) = (scan.SeriesBase, scan.SeriesWidth);
        foreach (var part in _parts)
        {
            if (part.Category < 0)
            {
                continue;
            }

            (scan.SeriesBase, scan.SeriesWidth) = (part.Category, part.Plan.HitWidth);
            var from = scan.Found;
            part.Plan.Hit(ref scan);
            if (part.Table is not null)
            {
                scan.Remap(from, this, part.Category);
            }
        }

        (scan.SeriesBase, scan.SeriesWidth) = (base0, width);
    }

    /// <summary>Returns the color of a category's part (row I2b): its series are numbered by its category, as <see cref="Hit"/> does.</summary>
    internal override Color HitColor(int series)
    {
        foreach (var part in _parts)
        {
            if (part.Category >= 0 && part.Category == series / part.Plan.HitWidth)
            {
                return part.Plan.HitColor(series % part.Plan.HitWidth);
            }
        }

        return SeriesColor;
    }

    internal override double HitAlong(in GeomFrame.Axis x, in Interaction.SeriesHit hit)
    {
        foreach (var part in _parts)
        {
            if (part.Category >= 0 && part.Category == hit.Series / part.Plan.HitWidth)
            {
                return part.Plan.HitAlong(x, hit);
            }
        }

        return HitScan.PixelOfValue(x, hit.X);
    }

    internal override (double Along, double Across) HitPlace(in GeomFrame.Axis x, in GeomFrame.Axis y, in Interaction.SeriesHit hit)
    {
        foreach (var part in _parts)
        {
            if (part.Category >= 0 && part.Category == hit.Series / part.Plan.HitWidth)
            {
                return part.Plan.HitPlace(x, y, hit);
            }
        }

        return base.HitPlace(x, y, hit);
    }

    internal override bool HitBin(in GeomFrame.Axis x, in GeomFrame.Axis y, in Interaction.SeriesHit hit, out PixelBox box)
    {
        foreach (var part in _parts)
        {
            if (part.Category >= 0 && part.Category == hit.Series / part.Plan.HitWidth)
            {
                return part.Plan.HitBin(x, y, hit, out box);
            }
        }

        box = default;
        return false;
    }

    /// <summary>
    /// Answers with the layer's rows its categories' parts hold (row I3a): a gathered part's rows taken back to the rows they
    /// were gathered from (Issue #337 Decision 3).
    /// </summary>
    internal override ValueTask<Interaction.RowSelection> SelectRows(SelectionQuery query)
    {
        var parts = new List<(Task<Interaction.RowSelection>, Func<long, long>?)>();
        foreach (var part in _parts)
        {
            if (part.Category < 0)
            {
                continue;
            }

            var category = part.Category;
            parts.Add((part.Plan.SelectRows(query).AsTask(), part.Table is null ? null : row => ((ISourceRows)this).SourceRow(category, row)));
        }

        return SelectionQuery.Union(parts, query.Derived);
    }

    internal override void Highlight(RenderList list)
    {
        foreach (var part in _parts)
        {
            part.Plan.Highlight(list);
        }
    }

    /// <summary>Returns the row of the layer's rows a category's gathered row came from (row I2a).</summary>
    long ISourceRows.SourceRow(int key, long gathered)
    {
        return SourceRow(_keys!, _runs!, _starts, Rows, key, gathered);
    }

    /// <summary>
    /// Returns the row a key's gathered row came from, or −1 (row I2a): the chunk where the key's gathered position lies, then
    /// the key's codes counted in it, as <see cref="GatherRows(PlotWorkerPool, RowKeys, CategoryRuns, GatherChannel[], ref long[], RowRange)"/> wrote them. Reads one chunk's keys at most.
    /// </summary>
    internal static long SourceRow(RowKeys keys, CategoryRuns runs, long[] starts, RowRange rows, int key, long gathered)
    {
        var categories = keys.Column;
        var n = keys.Count;
        var (firstChunk, endChunk) = StatWork.ChunksOf(categories, rows);
        for (var chunk = firstChunk; chunk < endChunk; chunk++)
        {
            var (start, count) = (starts[(chunk * n) + key], runs.ChunkCounts[(chunk * n) + key]);
            if (gathered >= start + count)
            {
                continue;
            }

            var (lo, hi) = StatWork.PartOf(categories, chunk, rows);
            var scratch = keys.Scratch();
            try
            {
                var codes = keys.Codes(chunk, scratch)[lo..hi];
                var left = gathered - start;
                for (var i = 0; i < codes.Length; i++)
                {
                    if (codes[i] == key && left-- == 0)
                    {
                        return ((long)chunk << Column.ChunkShift) + lo + i;
                    }
                }
            }
            finally
            {
                keys.Return(scratch);
            }

            return -1;
        }

        return -1;
    }

    public override void Record(RecordContext context)
    {
        var palette = Colors?.Palette ?? Palette!;
        var dodge = PositionOf(Spec) == PositionKind.Dodge;
        foreach (var part in _parts)
        {
            // A key is a category, or a pair: the color's number times the shapes, plus the shape's (row R3f).
            var colorOf = _shapeCount > 0 ? part.Category / _shapeCount : part.Category;
            part.Plan.SeriesColor = part.Category >= 0 && _colored ? palette.Series(colorOf) : SeriesColor;
            part.Plan.SeriesShape = part.Category >= 0 && _shaped ? Shapes!.Of(_shapeCount > 0 ? part.Category % _shapeCount : part.Category) : null;
            part.Plan.Index = Index;
            part.Plan.Colors = Colors;
            (part.Plan.Sizes, part.Plan.Shapes) = (Sizes, Shapes);
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

        // A gather that still writes the columns frees them when it ends (row W1b2): nothing waits for it.
        if (_regather is { } regather)
        {
            regather.Abandon();
            return;
        }

        DisposeGathers();
    }

    private void DisposeGathers()
    {
        foreach (var gather in _gathers)
        {
            gather.Dispose();
        }
    }

    /// <summary>
    /// Returns whether two nodes of the same layer draw alike (row G7c): the same kind, label, and looks (stroke, fill, outline,
    /// baseline, width, mode, caps, and a point layer's density, row R3b1). Their channels are the same, or the diff would have
    /// made the layer again.
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
            (PointLayerSpec b, PointLayerSpec a) => b.Color == a.Color
                && b.Size.Equals(a.Size)
                && b.Shape == a.Shape
                && b.Opacity.Equals(a.Opacity)
                && b.Draw == a.Draw
                && b.DensityThreshold == a.DensityThreshold
                && b.DensityNorm == a.DensityNorm
                && Equals(b.DensityMap, a.DensityMap),
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

    /// <summary>
    /// Returns a layer's spec with its channels through a map and without color channels: a category's own layer. A point
    /// part on the index path has one shape, set at its recording; one on the CPU path reads its rows' sizes and shapes.
    /// </summary>
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
            PointLayerSpec { Indexed: true } p => p with { X = map(p.X), Y = map(p.Y), ShapeBy = null },
            PointLayerSpec p => p with { X = map(p.X), Y = map(p.Y), SizeBy = Mapped(p.SizeBy, map), ShapeBy = Mapped(p.ShapeBy, map) },
            _ => throw new InvalidOperationException($"A {spec.GetType().Name} is not split by categories."),
        };
    }

    private static ColumnRef? Mapped(ColumnRef? reference, Func<ColumnRef, ColumnRef> map) => reference is { } r ? map(r) : null;

    /// <summary>Adds a part a used category of a column: a plan over its run, or over its gathered rows.</summary>
    private void AddParts(LayerSpec spec, PlanBuild build, List<PlotPyramid> pyramids)
    {
        var runs = _runs!;
        for (var c = 0; c < _keys!.Count; c++)
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
    /// <summary>A split's gather of its sources' newer versions into the same columns, on the store's queue (row W1b2).</summary>
    private sealed class GatherMaking(SplitPlan plan, PlotWorkerPool pool) : Making
    {
        protected override void Work() => GatherRows(pool, plan._keys!, plan._runs!, plan._gathers, ref plan._starts, plan.Rows);

        // An abandoned gather's split was released: the columns go once it no longer writes them.
        protected override void Free() => plan.DisposeGathers();
    }

    private void Gather(PlotWorkerPool pool)
    {
        for (var s = 0; s < _sources.Length; s++)
        {
            _gatheredVersions[s] = _sources[s].Version;
        }

        GatherRows(pool, _keys!, _runs!, _gathers, ref _starts, Rows);
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
    ) => GatherRows(pool, new ColumnKeys(categories), runs, gathers, ref chunkStarts, rows);

    /// <summary>
    /// Writes the rows of the keys that do not lie in one run into their gathered columns (rows G6b and G7c): a pass of the
    /// workers over the chunks, each key's rows in their order.
    /// </summary>
    internal static void GatherRows(
        PlotWorkerPool pool,
        RowKeys keys,
        CategoryRuns runs,
        GatherChannel[] gathers,
        ref long[] chunkStarts,
        RowRange rows
    )
    {
        var categories = keys.Column;
        var n = keys.Count;

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
                    var scratch = keys.Scratch();
                    var codes = keys.Codes(chunk, scratch)[lo..hi];
                    foreach (var gather in gathers)
                    {
                        start.CopyTo(positions);
                        gather.Copy(chunk, lo, codes, positions);
                    }

                    keys.Return(scratch);
                }
            )
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>One series: its category (−1 for the layer's own color), its plan, and its table when its rows were gathered.</summary>
    private readonly record struct Part(int Category, LayerPlan Plan, DataTable? Table, Func<LayerSpec, LayerSpec> Make);
}

/// <summary>
/// The key of each row a split reads (rows G6b and G7c): a categorical column's numbers, or a facet grid's pairs of two
/// columns' categories. −1 is no key.
/// </summary>
internal abstract class RowKeys
{
    /// <summary>Gets the number of keys.</summary>
    public abstract int Count { get; }

    /// <summary>Gets a column of the rows, whose length and chunks the keys follow.</summary>
    public abstract Column Column { get; }

    /// <summary>Returns a buffer a chunk's keys may be written into, or an empty one when they are read as they are.</summary>
    public virtual int[] Scratch() => [];

    /// <summary>Returns a buffer <see cref="Scratch"/> gave.</summary>
    public virtual void Return(int[] scratch) { }

    /// <summary>Returns a chunk's keys, one a row from its first.</summary>
    public abstract ReadOnlySpan<int> Codes(int chunk, int[] scratch);
}

/// <summary>A categorical column's numbers as keys.</summary>
internal sealed class ColumnKeys(CategoricalColumn column) : RowKeys
{
    public override int Count => column.CategoryCount;

    public override Column Column => column;

    public override ReadOnlySpan<int> Codes(int chunk, int[] scratch) => column.GetCodes(chunk);
}

/// <summary>
/// A facet grid's keys (row G6b): the row category's place among the used ones times the number of used column categories,
/// plus the column category's place; −1 when either is missing or unused.
/// </summary>
internal sealed class PairKeys(CategoricalColumn rows, int[] rowPlaces, int rowCount, CategoricalColumn columns, int[] columnPlaces, int columnCount)
    : RowKeys
{
    public override int Count => rowCount * columnCount;

    public override Column Column => rows;

    public override int[] Scratch() => System.Buffers.ArrayPool<int>.Shared.Rent(Column.ChunkLength);

    public override void Return(int[] scratch) => System.Buffers.ArrayPool<int>.Shared.Return(scratch);

    public override ReadOnlySpan<int> Codes(int chunk, int[] scratch)
    {
        var r = rows.GetCodes(chunk);
        var c = columns.GetCodes(chunk);
        var keys = scratch.AsSpan(0, r.Length);
        for (var i = 0; i < keys.Length; i++)
        {
            var (row, column) = (r[i] < 0 ? -1 : rowPlaces[r[i]], c[i] < 0 ? -1 : columnPlaces[c[i]]);
            keys[i] = row < 0 || column < 0 ? -1 : (row * columnCount) + column;
        }

        return keys;
    }
}
