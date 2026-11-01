namespace HyperPlot;

/// <summary>A panel's place in a facet's grid and its strip (design §4.5, §4.6, row G6b): its key, −1 for an empty cell.</summary>
internal readonly record struct FacetCell(int Row, int Column, int Key, string? Strip);

/// <summary>
/// The arrangement of a facet's panels that the guides lay out (design §4.6, row G6b): a wrap's panels left to right and
/// wrapped into rows, or a grid's cells; the strips; and which position scales the panels share.
/// </summary>
internal sealed class FacetArrangement(
    bool wrap,
    int rows,
    int columns,
    FacetScales scales,
    FacetCell[] cells,
    string[] rowLabels,
    string[] columnLabels
)
{
    /// <summary>Gets a value indicating whether the panels wrap, each with its strip above it; else they lie on a grid.</summary>
    public bool Wrap => wrap;

    /// <summary>Gets the number of rows of panels.</summary>
    public int Rows => rows;

    /// <summary>Gets the number of columns of panels.</summary>
    public int Columns => columns;

    /// <summary>Gets which position scales the panels share.</summary>
    public FacetScales Scales => scales;

    /// <summary>Gets the panels, in their order: left to right, top to bottom.</summary>
    public FacetCell[] Cells => cells;

    /// <summary>Gets a grid's row strips, top to bottom; empty for a wrap or a grid of one row column.</summary>
    public string[] RowLabels => rowLabels;

    /// <summary>Gets a grid's column strips, left to right; empty for a wrap or a grid of one column column.</summary>
    public string[] ColumnLabels => columnLabels;

    /// <summary>Gets a value indicating whether each panel learns its own x (design §4.5).</summary>
    public bool FreeX => scales is FacetScales.FreeX or FacetScales.Free;

    /// <summary>Gets a value indicating whether each panel learns its own y.</summary>
    public bool FreeY => scales is FacetScales.FreeY or FacetScales.Free;

    /// <summary>Returns whether a panel lies at a row and a column: every cell of a grid, the first panels of a wrap's last row.</summary>
    public bool Has(int row, int column) =>
        row >= 0 && row < rows && column >= 0 && column < columns && (!wrap || (row * columns) + column < cells.Length);

    /// <summary>
    /// Returns the rows and columns of a wrap of n panels (design §4.5): the given columns and ⌈n / columns⌉ rows, or R's
    /// <c>n2mfrow</c> as ggplot2's <c>wrap_dims</c> takes it: 1 row up to 3, 2 rows up to 6, 3 rows up to 12, else
    /// ⌈√n⌉ columns.
    /// </summary>
    public static (int Rows, int Columns) WrapDims(int n, int? columns)
    {
        if (columns is int given)
        {
            return (Math.Max(1, (n + given - 1) / given), given);
        }

        if (n <= 3)
        {
            return (1, Math.Max(n, 1));
        }

        if (n <= 6)
        {
            return (2, (n + 1) / 2);
        }

        if (n <= 12)
        {
            return (3, (n + 2) / 3);
        }

        var wide = (int)Math.Ceiling(Math.Sqrt(n));
        return ((n + wide - 1) / wide, wide);
    }
}

/// <summary>
/// The data of a facet's panels (design §4.5, §5.5.3, row G6b): the panels' keys and their arrangement, and each panel's
/// rows, a run of the plot's table or its rows gathered into a table of its own, which the layers are remapped onto.
/// </summary>
/// <remarks>Made when the facet or the layers' columns change; a new version of a gathered column is gathered again by <see cref="Refresh"/>.</remarks>
internal sealed class FacetData : IDisposable
{
    /// <summary>The most panels a plot has (design §4.5): a 16 × 16 grid leaves panels of about 100 × 50 pixels at 1,920 × 1,080.</summary>
    public const int MaxPanels = 256;

    private readonly RowKeys? _keys;
    private readonly CategoryRuns? _runs;
    private readonly GatherChannel[] _gathers = [];
    private readonly Column[] _sources = [];
    private readonly long[] _versions = [];
    private long[] _starts = [];

    private FacetData(FacetArrangement arrangement, PanelData[] panels)
    {
        (Arrangement, Panels) = (arrangement, panels);
    }

    private FacetData(FacetArrangement arrangement, PanelData[] panels, RowKeys keys, CategoryRuns runs, GatherChannel[] gathers, Column[] sources)
        : this(arrangement, panels)
    {
        (_keys, _runs, _gathers, _sources) = (keys, runs, gathers, sources);
        _versions = new long[sources.Length];
    }

    /// <summary>Gets the panels' arrangement.</summary>
    public FacetArrangement Arrangement { get; private set; }

    /// <summary>Gets each panel's rows, in the arrangement's order.</summary>
    public PanelData[] Panels { get; }

    /// <summary>Gets the number of panels whose rows were gathered. Tests only.</summary>
    public int GatheredCount => Panels.Count(p => p.Map is not null);

    /// <summary>
    /// Throws unless a facet goes with the plot (design §4.5, §4.8): its columns are categorical columns of the plot's table,
    /// a grid splits by at least one, and its members are in range. Reads no data.
    /// </summary>
    public static void Check(PlotSpec plot)
    {
        switch (plot.Facet)
        {
            case null:
                return;
            case FacetWrapSpec wrap:
                CheckColumn(plot, wrap.By, "By");
                if (wrap.ColumnCount is < 1)
                {
                    throw new ArgumentException($"The facet's ColumnCount is {wrap.ColumnCount}; it is at least 1 (design §4.5).");
                }

                break;
            case FacetGridSpec grid:
                if (grid.Rows is null && grid.Columns is null)
                {
                    throw new ArgumentException(
                        "The facet grid's Rows and Columns are both null; a grid splits by one of them or by both (design §4.5)."
                    );
                }

                if (grid.Rows is { } rows)
                {
                    CheckColumn(plot, rows, "Rows");
                }

                if (grid.Columns is { } columns)
                {
                    CheckColumn(plot, columns, "Columns");
                }

                break;
        }

        if ((uint)plot.Facet.Scales > (uint)FacetScales.Free)
        {
            throw new ArgumentException($"The facet's Scales is {plot.Facet.Scales}; it is Fixed, FreeX, FreeY, or Free (design §4.5).");
        }
    }

    /// <summary>
    /// Finds the panels of a checked facet and their rows (design §4.5, §5.5.3): the used categories' runs, the pairs' runs
    /// of a grid of two columns on the workers, and the rows of the keys that do not lie in one run gathered. Throws when the
    /// facet makes more than <see cref="MaxPanels"/> panels.
    /// </summary>
    public static FacetData Build(PlotSpec plot, PlotWorkerPool pool)
    {
        var facet = plot.Facet!;
        var (rowsColumn, columnsColumn) = facet switch
        {
            FacetWrapSpec wrap => ((CategoricalColumn)wrap.By.Column, (CategoricalColumn?)null),
            FacetGridSpec grid => ((CategoricalColumn?)grid.Rows?.Column, (CategoricalColumn?)grid.Columns?.Column),
            _ => throw new InvalidOperationException($"A {facet.GetType().Name} is not a facet the plot lays out."),
        };

        // A grid of one column splits as a wrap does, on one row or one column.
        if (facet is FacetGridSpec && (rowsColumn is null || columnsColumn is null))
        {
            var one = (rowsColumn ?? columnsColumn)!;
            var runs = CategoryRuns.Of(one, pool);
            var used = Used(runs);
            CheckCount(used.Length);
            var byRows = rowsColumn is not null;
            var cells = new FacetCell[used.Length];
            for (var i = 0; i < used.Length; i++)
            {
                cells[i] = byRows ? new FacetCell(i, 0, used[i], null) : new FacetCell(0, i, used[i], null);
            }

            var labels = used.Select(c => one.Categories[c]).ToArray();
            var arrangement =
                used.Length == 0
                    ? Single(facet.Scales)
                    : new FacetArrangement(
                        false,
                        byRows ? used.Length : 1,
                        byRows ? 1 : used.Length,
                        facet.Scales,
                        cells,
                        byRows ? labels : [],
                        byRows ? [] : labels
                    );
            return Make(plot, arrangement, new ColumnKeys(one), runs, pool);
        }

        if (facet is FacetWrapSpec wrapped)
        {
            var runs = CategoryRuns.Of(rowsColumn!, pool);
            var used = Used(runs);
            CheckCount(used.Length);
            var (rows, columns) = FacetArrangement.WrapDims(used.Length, wrapped.ColumnCount);
            var cells = new FacetCell[used.Length];
            for (var i = 0; i < used.Length; i++)
            {
                cells[i] = new FacetCell(i / columns, i % columns, used[i], rowsColumn!.Categories[used[i]]);
            }

            var arrangement = used.Length == 0 ? Single(facet.Scales) : new FacetArrangement(true, rows, columns, facet.Scales, cells, [], []);
            return Make(plot, arrangement, new ColumnKeys(rowsColumn!), runs, pool);
        }

        // A grid of two columns: the used rows and columns, and a key a pair of them, whose runs one pass finds.
        var usedRows = Used(CategoryRuns.Of(rowsColumn!, pool));
        var usedColumns = Used(CategoryRuns.Of(columnsColumn!, pool));
        CheckCount((long)usedRows.Length * usedColumns.Length);
        var (rowPlaces, columnPlaces) = (Places(rowsColumn!, usedRows), Places(columnsColumn!, usedColumns));
        var keys = new PairKeys(rowsColumn!, rowPlaces, usedRows.Length, columnsColumn!, columnPlaces, usedColumns.Length);
        var pairs = CategoryRuns.Find(keys, pool, RowRange.All);
        var pairCells = new FacetCell[usedRows.Length * usedColumns.Length];
        for (var r = 0; r < usedRows.Length; r++)
        {
            for (var c = 0; c < usedColumns.Length; c++)
            {
                var key = (r * usedColumns.Length) + c;
                pairCells[key] = new FacetCell(r, c, pairs.Count[key] > 0 ? key : -1, null);
            }
        }

        var gridArrangement =
            pairCells.Length == 0
                ? Single(facet.Scales)
                : new FacetArrangement(
                    false,
                    usedRows.Length,
                    usedColumns.Length,
                    facet.Scales,
                    pairCells,
                    [.. usedRows.Select(c => rowsColumn!.Categories[c])],
                    [.. usedColumns.Select(c => columnsColumn!.Categories[c])]
                );
        return Make(plot, gridArrangement, keys, pairs, pool);
    }

    /// <summary>
    /// Arranges the same panels for a new node of the facet (design §4.8, row G6b): a wrap's new number of columns, or new
    /// scales. The panels and their rows stay.
    /// </summary>
    public void Rearrange(FacetSpec facet)
    {
        var old = Arrangement;
        if (!old.Wrap || old.Cells is [{ Key: < 0 }])
        {
            Arrangement = new FacetArrangement(old.Wrap, old.Rows, old.Columns, facet.Scales, old.Cells, old.RowLabels, old.ColumnLabels);
            return;
        }

        var n = old.Cells.Length;
        var (rows, columns) = FacetArrangement.WrapDims(n, (facet as FacetWrapSpec)?.ColumnCount);
        var cells = new FacetCell[n];
        for (var i = 0; i < n; i++)
        {
            cells[i] = old.Cells[i] with { Row = i / columns, Column = i % columns };
        }

        Arrangement = new FacetArrangement(true, rows, columns, facet.Scales, cells, [], []);
    }

    /// <summary>
    /// Returns a layer's spec with every column it reads through a map (row G6b): onto a panel's gathered table. An image and
    /// a user's geom read no rows and stay as they are.
    /// </summary>
    public static LayerSpec Remap(LayerSpec spec, Func<ColumnRef, ColumnRef> map)
    {
        ColumnRef M(ColumnRef r) => r.IsDefault ? r : map(r);
        ColumnRef? N(ColumnRef? r) => r is { } value ? M(value) : null;
        var colored = spec is ImageLayerSpec or CustomLayerSpec ? spec : spec with { ColorBy = N(spec.ColorBy), FillBy = N(spec.FillBy) };
        return colored switch
        {
            LineLayerSpec l => l with { X = M(l.X), Y = M(l.Y) },
            StepLayerSpec s => s with { X = M(s.X), Y = M(s.Y) },
            AreaLayerSpec a => a with { X = M(a.X), Y = M(a.Y) },
            BandLayerSpec b => b with { X = M(b.X), YMin = M(b.YMin), YMax = M(b.YMax) },
            BarLayerSpec b => b with { X = M(b.X), Y = M(b.Y) },
            ErrorBarLayerSpec e => e with { X = M(e.X), YMin = M(e.YMin), YMax = M(e.YMax) },
            RuleLayerSpec r => r with { X = M(r.X), Y = M(r.Y) },
            RectLayerSpec r => r with { XMin = M(r.XMin), XMax = M(r.XMax), YMin = M(r.YMin), YMax = M(r.YMax) },
            SegmentLayerSpec s => s with { X = M(s.X), Y = M(s.Y), XEnd = M(s.XEnd), YEnd = M(s.YEnd) },
            TextLayerSpec t => t with { X = M(t.X), Y = M(t.Y), Text = M(t.Text) },
            _ => colored,
        };
    }

    /// <summary>Returns whether a layer reads no rows (row G6b): an image or a user's geom, drawn in every panel.</summary>
    public static bool Rowless(LayerSpec spec) => spec is ImageLayerSpec or CustomLayerSpec;

    /// <summary>
    /// Gathers the gathered panels' rows again when a source column has a new version, and invalidates them (design §5.5.3):
    /// before the layers check whether they are stale, so that a stat over a gathered panel is made again in the same Render.
    /// </summary>
    /// <returns>Whether the rows were gathered again.</returns>
    public bool Refresh(PlotWorkerPool pool)
    {
        if (_gathers.Length == 0)
        {
            return false;
        }

        var changed = false;
        for (var s = 0; s < _sources.Length; s++)
        {
            changed |= _sources[s].Version != _versions[s];
        }

        if (!changed)
        {
            return false;
        }

        Gather(pool);
        foreach (var gather in _gathers)
        {
            gather.Invalidate();
        }

        return true;
    }

    public void Dispose()
    {
        foreach (var gather in _gathers)
        {
            gather.Dispose();
        }
    }

    /// <summary>Returns the arrangement of a facet that makes no panel: one empty panel without a strip.</summary>
    private static FacetArrangement Single(FacetScales scales) => new(false, 1, 1, scales, [new FacetCell(0, 0, -1, null)], [], []);

    /// <summary>Makes the panels' rows: a run of the plot's table, or a table of the gathered rows.</summary>
    private static FacetData Make(PlotSpec plot, FacetArrangement arrangement, RowKeys keys, CategoryRuns runs, PlotWorkerPool pool)
    {
        // The columns the layers read, once each, with their names in the plot's table.
        var sources = new List<Column>();
        var names = new List<string>();
        var references = new List<ColumnRef>();
        for (var i = 0; i < plot.Layers.Count; i++)
        {
            references.Clear();
            Refs(plot.Layers[i], references);
            foreach (var reference in references)
            {
                if (!sources.Contains(reference.Column))
                {
                    sources.Add(reference.Column);
                    names.Add(plot.Data.GetName(reference.Index));
                }
            }
        }

        var counts = new long[keys.Count];
        foreach (var cell in arrangement.Cells)
        {
            if (cell.Key >= 0 && !runs.IsRun(cell.Key))
            {
                counts[cell.Key] = runs.Count[cell.Key];
            }
        }

        var gathers = counts.AsSpan().ContainsAnyExcept(0) ? sources.Select(source => GatherChannel.For(source, counts)).ToArray() : [];
        var panels = new PanelData[arrangement.Cells.Length];
        for (var p = 0; p < panels.Length; p++)
        {
            var key = arrangement.Cells[p].Key;
            if (key < 0)
            {
                panels[p] = new PanelData(-1, default, plot.Data, null);
                continue;
            }

            if (runs.IsRun(key))
            {
                panels[p] = new PanelData(key, new RowRange(runs.First[key], runs.Last[key] + 1), plot.Data, null);
                continue;
            }

            // The panel's own table of its gathered columns, under the plot's names.
            var table = DataTable.Create();
            for (var s = 0; s < sources.Count; s++)
            {
                _ = table.Add(names[s], gathers[s].Target(key)!);
            }

            var built = table.Build();
            var columns = sources.ToArray();
            panels[p] = new PanelData(key, RowRange.All, built, r => new ColumnRef(built, Array.IndexOf(columns, r.Column)));
        }

        var data = new FacetData(arrangement, panels, keys, runs, gathers, [.. sources]);
        if (gathers.Length > 0)
        {
            data.Gather(pool);
        }

        return data;
    }

    /// <summary>Adds every column a layer reads (row G6b): its channels, its color columns, and a text layer's labels.</summary>
    private static void Refs(LayerSpec spec, List<ColumnRef> into)
    {
        void Add(ColumnRef r)
        {
            if (!r.IsDefault)
            {
                into.Add(r);
            }
        }

        if (Rowless(spec))
        {
            return;
        }

        // The remap visits every column the layer reads, its color columns included.
        _ = Remap(
            spec,
            r =>
            {
                Add(r);
                return r;
            }
        );
    }

    /// <summary>Returns the categories of a column with rows, in their numbers' order.</summary>
    private static int[] Used(CategoryRuns runs) => [.. Enumerable.Range(0, runs.Count.Length).Where(c => runs.Count[c] > 0)];

    /// <summary>Returns each category's place among the used ones, or −1 for an unused one.</summary>
    private static int[] Places(CategoricalColumn column, int[] used)
    {
        var places = new int[column.CategoryCount];
        Array.Fill(places, -1);
        for (var i = 0; i < used.Length; i++)
        {
            places[used[i]] = i;
        }

        return places;
    }

    private static void CheckCount(long panels)
    {
        if (panels > MaxPanels)
        {
            throw new ArgumentException(
                $"The facet makes {panels} panels; a plot has at most {MaxPanels} (design §4.5). Split by a column of fewer categories."
            );
        }
    }

    private static void CheckColumn(PlotSpec plot, ColumnRef reference, string name)
    {
        if (reference.IsDefault)
        {
            throw new ArgumentException($"The facet's {name} is default and names no column.");
        }

        if (!ReferenceEquals(reference.Table, plot.Data))
        {
            throw new ArgumentException($"The facet's {name} belongs to another table than the plot's (design §4.5).");
        }

        if (reference.Column is not CategoricalColumn)
        {
            throw new ArgumentException(
                $"The facet's {name} \"{plot.Data.GetName(reference.Index)}\" is a {reference.Column.KindName}; a facet splits by a CategoricalColumn (design §4.5)."
            );
        }
    }

    /// <summary>Writes the gathered panels' rows from the sources' current values.</summary>
    private void Gather(PlotWorkerPool pool)
    {
        for (var s = 0; s < _sources.Length; s++)
        {
            _versions[s] = _sources[s].Version;
        }

        SplitPlan.GatherRows(pool, _keys!, _runs!, _gathers, ref _starts, RowRange.All);
    }
}

/// <summary>
/// A panel's rows (row G6b): its key (−1 for an empty cell of a grid), the run of the plot's table it reads, and the table
/// its layers read with the map onto it: the plot's, or its own of gathered rows.
/// </summary>
internal sealed record PanelData(int Key, RowRange Rows, DataTable Data, Func<ColumnRef, ColumnRef>? Map)
{
    /// <summary>Gets a value indicating whether the panel has no rows: an empty cell of a grid.</summary>
    public bool Empty => Key < 0;
}

/// <summary>
/// A layer in an empty cell of a facet grid (design §4.5, row G6b): no rows, so it learns and draws nothing; the panel keeps
/// its background, grid, and axes.
/// </summary>
internal sealed class EmptyPlan : LayerPlan
{
    public EmptyPlan(LayerSpec spec)
        : base(spec) => SetColumns();

    public override void AddRanges(RangeSink sink, bool ofX, DataTable data) { }

    public override void Record(RecordContext context) { }
}
