using System.Numerics;
using System.Runtime.ExceptionServices;
using HyperPlot.Rendering;

namespace HyperPlot;

/// <summary>A column that a spatial tile index can be built over: a stored column, whose element type the build needs (row R3a).</summary>
internal interface ISpatialColumn
{
    /// <summary>Starts the index of this column as x and another stored column as y.</summary>
    /// <param name="pool">The pool that builds the index.</param>
    /// <param name="y">The y column: an <see cref="ISpatialColumn"/> too.</param>
    /// <param name="input">The rows and the transforms.</param>
    /// <returns>The index.</returns>
    SpatialIndex BuildSpatialIndex(PlotWorkerPool pool, Column y, in SpatialIndexInput input);

    /// <summary>Starts the index of a column as x and this column as y: the second half of the dispatch on both element types.</summary>
    /// <typeparam name="TX">The element type of the x column.</typeparam>
    /// <param name="pool">The pool that builds the index.</param>
    /// <param name="x">The x column.</param>
    /// <param name="input">The rows and the transforms.</param>
    /// <returns>The index.</returns>
    SpatialIndex BuildSpatialIndexAsY<TX>(PlotWorkerPool pool, Column<TX> x, in SpatialIndexInput input)
        where TX : unmanaged, INumber<TX>;
}

/// <summary>
/// What the point layers of one making of a plan's layers share (design §5.5.2 "索引の保管庫"): the index being built, so
/// that the parts' indexes are built one after another, and the number of parts each layer has on the index path.
/// </summary>
internal sealed class SpatialIndexing
{
    /// <summary>The most parts of one layer on the index path: each index holds about 0.7 MB whatever its points.</summary>
    internal const int MaxParts = 64;

    private readonly Dictionary<int, int> _parts = [];
    private readonly Dictionary<(PlanBuild Build, int Layer), DensitySwitch> _switches = [];

    /// <summary>Gets or sets the index started last: the next starts when its build has ended.</summary>
    public SpatialIndex? Building { get; set; }

    /// <summary>Counts one more part of a layer on the index path.</summary>
    /// <exception cref="InvalidOperationException">The layer has more than <see cref="MaxParts"/> parts.</exception>
    public void Enter(int layer)
    {
        _ = _parts.TryGetValue(layer, out var count);
        if (count == MaxParts)
        {
            throw new InvalidOperationException(
                $"Layer {layer} is a point layer of more than {LayerPlan.MaxElements} rows split into more than {MaxParts} parts; each part has a spatial index of its own (design §5.5.2). Row R3d draws such a layer from one index."
            );
        }

        _parts[layer] = count + 1;
    }

    /// <summary>
    /// Returns the switch the parts of a layer share in a panel (design §5.5.2 "行 R3b1 の密度と切り替え"): one drawing a
    /// layer, chosen from all its parts. A panel's build is made a panel, so the build and the layer name the panel's layer.
    /// </summary>
    public DensitySwitch SwitchOf(PlanBuild build, int layer)
    {
        if (!_switches.TryGetValue((build, layer), out var shared))
        {
            shared = new DensitySwitch();
            _switches[(build, layer)] = shared;
        }

        return shared;
    }
}

/// <summary>
/// Points on the index path (design §5.5.2, row R3a): a part of a layer of more than 131,072 rows. Its points are put in
/// Morton order by a spatial tile index, stay on the GPU in pages, and are placed there from the cells' records of each
/// frame. Nothing is drawn until the index is built.
/// </summary>
internal sealed class IndexedPointPlan : LayerPlan
{
    private readonly PlotWorkerPool _pool;
    private readonly IScale? _xTransform;
    private readonly IScale? _yTransform;
    private readonly SpatialIndexing _indexing;

    // The switch the layer's parts share (design §5.5.2 "行 R3b1 の密度と切り替え").
    private readonly DensitySwitch _switch;
    private SpatialIndex? _index;
    private SpatialSnapshot? _snapshot;

    // Ends with the index once Poll starts it, or with null once the part is released: a selection's rows asked before
    // wait for it (design §6.4 "索引ができる前の行の集合").
    private TaskCompletionSource<SpatialIndex?>? _started;

    // The coarse snapshot drawn as a density until the first full one is there (design §5.5.2 "索引ができるまで").
    private SpatialSnapshot? _coarse;
    private ResidencyCache? _cache;
    private bool _missing;

    // The density's pieces this frame (design §5.5.2 "水準の選び方"), planned by the switch before any part records.
    private DensityPiece[] _pieces = new DensityPiece[64];
    private SpatialCell[] _cells = new SpatialCell[256];
    private int _pieceCount;
    private int _tileLevel;
    private bool _drawsDensity;

    // The density's color table of the layer's own color, made again when the color changes.
    private ColorMap? _ramp;
    private Color _rampColor;

    // The markers' radius in physical pixels of the last recording, and the values and the position in the copy of the last
    // point a hit test answered, which a hover marks where it is drawn (row R3c). The values, not the row, tell the hit
    // again: a split's or a facet's gathered row is taken back to the table's after this part answered.
    private double _markerRadius;
    private double _lastX = double.NaN;
    private double _lastY = double.NaN;
    private long _lastPosition = -1;

    public IndexedPointPlan(PointLayerSpec spec, PlanBuild build, RowRange rows)
        : base(spec, rows)
    {
        SetColumns(spec.X.Column, spec.Y.Column);
        (_pool, _xTransform, _yTransform, _indexing) = (build.Pool, build.XTransform, build.YTransform, build.Indexing);
        _indexing.Enter(build.Layer);
        _switch = _indexing.SwitchOf(build, build.Layer);
        _switch.Add(this);
    }

    /// <summary>Gets a value indicating whether the last recording drew a density (row R3b1). Tests only.</summary>
    internal bool DrewDensity => _drawsDensity;

    /// <summary>Gets the switch the layer's parts share (row R3b1). Tests only.</summary>
    internal DensitySwitch Switch => _switch;

    /// <summary>Gets the level of the tiles the last recording chose (row R3b1). Tests only.</summary>
    internal int TileLevel => _tileLevel;

    /// <summary>Gets the pieces of the last recording's density (row R3b1). Tests only.</summary>
    internal ReadOnlySpan<DensityPiece> Pieces => _pieces.AsSpan(0, _pieceCount);

    /// <summary>Gets a value indicating whether the last recording asked for a page it did not get. Tests only.</summary>
    internal bool MissedPages => _missing;

    /// <summary>Gets the index, once its build was started. Tests only.</summary>
    internal SpatialIndex? SpatialIndex => _index;

    /// <inheritdoc />
    public override bool RecordedProvisional => _missing || (_snapshot is null && _coarse is not null);

    public override void AddRanges(RangeSink sink, bool ofX, DataTable data)
    {
        var point = (PointLayerSpec)Spec;
        var channel = ofX ? point.X : point.Y;
        AddColumn(sink, channel.Column, data.GetName(channel.Index));
    }

    /// <summary>
    /// Returns whether the part draws (design §5.5.2 "索引ができるまでと、失敗"): not until its index's first build ends,
    /// then the last full snapshot while a new build runs. The parts' builds start one after another. A failed build's
    /// exception is thrown as it was.
    /// </summary>
    public override LayerReadiness Poll(LayerIndexes indexes)
    {
        if (_index is null)
        {
            // The index waited for is not disposed before it ends while this part waits: a plan disposes its index when
            // it is released, a plan on the index path is never stale and so never made again alone, and all the layers
            // made together, which share the indexing, are released together.
            if (_indexing.Building is { IsComplete: false, Fault: null } other)
            {
                if (!indexes.Waits)
                {
                    return LayerReadiness.Pending;
                }

                other.WhenComplete().GetAwaiter().GetResult();
            }

            var point = (PointLayerSpec)Spec;
            var input = new SpatialIndexInput
            {
                Rows = Rows,
                XTransform = _xTransform,
                YTransform = _yTransform,
            };
            _index = ((ISpatialColumn)point.X.Column).BuildSpatialIndex(_pool, point.Y.Column, input);
            _indexing.Building = _index;
            _started?.TrySetResult(_index);
        }

        if (indexes.Waits)
        {
            _index.WhenComplete().GetAwaiter().GetResult();
        }
        else if (_index.Fault is { } fault)
        {
            ExceptionDispatchInfo.Throw(fault);
        }

        // The newest full snapshot is the one drawn; an older one stays until the next is full. Before the first full one,
        // the coarse one is drawn as a density (design §5.5.2 "索引ができるまで").
        var current = _index.Acquire();
        if (ReferenceEquals(current, _snapshot) || ReferenceEquals(current, _coarse))
        {
            current.Release();
        }
        else if (current.Stage == SpatialStage.Full)
        {
            Drop();
            DropCoarse();
            _snapshot = current;
        }
        else if (_snapshot is null && current.Stage == SpatialStage.Coarse)
        {
            DropCoarse();
            _coarse = current;
        }
        else
        {
            current.Release();
        }

        return _snapshot is null ? (_coarse is null ? LayerReadiness.Pending : LayerReadiness.Previous)
            : _snapshot.IsProvisional || !_index.IsComplete ? LayerReadiness.Previous
            : LayerReadiness.Ready;
    }

    public override void Record(RecordContext context)
    {
        _missing = false;
        var point = (PointLayerSpec)Spec;
        _cache = context.Images;
        _markerRadius = point.Size * context.Scale / 2;

        // The layer's drawing, chosen once a frame for all its parts; a part on its coarse snapshot draws a density anyway.
        _ = _switch.Prepare(context, point);
        if (_drawsDensity)
        {
            RecordDensity(context, point);
            return;
        }

        if (_snapshot is not { } snapshot)
        {
            return;
        }

        var cells = snapshot.ResidentCells;
        if (cells.IsEmpty)
        {
            return;
        }

        var x = new AxisMap(context.X, context.Transform, ofX: true, snapshot.XRange.Origin);
        var y = new AxisMap(context.Y, context.Transform, ofX: false, snapshot.YRange.Origin);
        var list = context.List;
        var offset = list.AddResidentCells(cells.Length, out var records);
        var color = point.Color ?? SeriesColor;
        var style = new ResidentMarkerRun(
            Slot: -1,
            Count: 0,
            Cells: offset,
            Size: point.Size * context.Scale,
            Kind: (uint)(SeriesShape ?? point.Shape) | ((uint)MathF.Round(MathF.Max(1, MathF.Round(context.Scale)) * 16) << 8),
            Color: RenderList.Premultiply(point.Opacity >= 1 ? color : color with { A = (byte)Math.Round(color.A * point.Opacity) }),
            Swap: context.Flipped,
            ClampLow: new Vector2(x.ClampLow, y.ClampLow),
            ClampHigh: new Vector2(x.ClampHigh, y.ClampHigh),
            Clip: default,
            Highlight: context.Highlight
        );
        var pages = context.Images.Points;
        var drawn = -1L;
        for (var c = 0; c < cells.Length; c++)
        {
            ref readonly var cell = ref cells[c];
            var (baseX, pixelX, scaleX) = x.Place(cell.CornerX, cell.ExtentX);
            var (baseY, pixelY, scaleY) = y.Place(cell.CornerY, cell.ExtentY);
            records[c] = new ResidentCellRecord(baseX, baseY, pixelX, pixelY, scaleX, scaleY);
            if (!x.Sees(cell.CornerX, cell.ExtentX) || !y.Sees(cell.CornerY, cell.ExtentY))
            {
                continue;
            }

            // The cell's points lie in one or more pages, and each page is drawn whole, once: the cells come in the
            // copy's order, so a page seen before is the last one drawn.
            var end = cell.First + cell.Count;
            for (var page = Math.Max(cell.First / PointPages.PagePoints, drawn + 1); page * PointPages.PagePoints < end; page++)
            {
                drawn = page;
                var slot = pages.Request(snapshot, (int)page);
                if (slot < 0)
                {
                    _missing = true;
                    continue;
                }

                list.DrawResidentMarkers(style with { Slot = slot, Count = PointPages.PointsOf(snapshot, (int)page) });
            }
        }
    }

    /// <summary>
    /// Answers a hit test with what the part draws (design §6.3 "散布の索引の道", row R3c, Issue #471): its raw points by the
    /// search over its index, measured where the vertex shader draws them, or the density's bin under the cursor. The
    /// drawing is the one the last recording chose, the switch's band included (Decision 11). A part on its coarse snapshot
    /// answers with the coarse density's bin (Decision 5); a part that draws its last full snapshot while a new one is
    /// built answers from it, provisionally, as the readiness says (Decision 5). Allocates nothing once the plot's plan holds
    /// the search's buffers.
    /// </summary>
    internal override void Hit(ref HitScan scan)
    {
        if (scan.Summing)
        {
            return;
        }

        if (_drawsDensity)
        {
            HitDensity(ref scan);
            return;
        }

        if (_snapshot is not { } snapshot || snapshot.ResidentCells.IsEmpty)
        {
            return;
        }

        var transform = CartesianTransform.Create(scan.X, scan.Y);
        var x = new AxisMap(scan.X, transform, ofX: true, snapshot.XRange.Origin);
        var y = new AxisMap(scan.Y, transform, ofX: false, snapshot.YRange.Origin);
        var screen = new DrawnScreen(x, y);
        var scratch = scan.Scratch ??= new SpatialNearestScratch();
        var found = snapshot.Nearest(ref screen, scan.Along, scan.Across, scan.Radius, _markerRadius, scan.Mode, scratch);
        if (!found.Found)
        {
            return;
        }

        // The x and y are the point's values in the copy, as differences from the snapshot's origins, taken back to data
        // units (invariant 6); the distance is the drawn one in both directions, as the CPU path's. A page not yet on the GPU leaves the frame provisional, and the answer with it
        // (Decision 14); so does a queue that overflowed (Decision 20).
        var (valueX, valueY) = (x.ValueOf(found.X), y.ValueOf(found.Y));
        (_lastX, _lastY, _lastPosition) = (valueX, valueY, found.Position);
        scan.Add(
            0,
            found.Row,
            valueX,
            valueY,
            scan.Distance(found.Along, found.Across),
            derived: false,
            provisional: found.Overflowed || _missing || snapshot.IsProvisional
        );
    }

    /// <summary>
    /// Returns where the vertex shader draws the point of the last answer (design §6.3 "描いた位置で測る"): its unit of its
    /// resident cell, placed from the cell's record of this view. Another hit (an older selection's point) is marked at its
    /// value. The point is known by its values, which a gathered part's remap of the row leaves as they are.
    /// </summary>
    internal override (double Along, double Across) HitPlace(in GeomFrame.Axis x, in GeomFrame.Axis y, in Interaction.SeriesHit hit)
    {
        if (hit.Row < 0 || hit.X != _lastX || hit.Y != _lastY || _snapshot is not { } snapshot || _lastPosition >= snapshot.PointCount)
        {
            return base.HitPlace(x, y, hit);
        }

        Span<ResidentPoint> one = stackalloc ResidentPoint[1];
        snapshot.ReadBlock(_lastPosition, one);
        var screen = Screen(x, y, snapshot);
        screen.Enter(snapshot.ResidentCells[(int)one[0].Cell]);
        return screen.Place(one[0].X, one[0].Y);
    }

    /// <summary>Returns the box of the density's bin a hit of this part stands for, from the bin at its center (row R3c).</summary>
    internal override bool HitBin(in GeomFrame.Axis x, in GeomFrame.Axis y, in Interaction.SeriesHit hit, out PixelBox box)
    {
        box = default;
        if (!TryHitBin(x, y, hit, out var mapX, out var mapY, out var bin))
        {
            return false;
        }

        var (a0, a1) = (mapX.Pixel(bin.CornerX), mapX.Pixel(bin.CornerX + bin.ExtentX));
        var (c0, c1) = (mapY.Pixel(bin.CornerY), mapY.Pixel(bin.CornerY + bin.ExtentY));
        box = new PixelBox(Math.Min(a0, a1), Math.Max(a0, a1), Math.Min(c0, c1), Math.Max(c0, c1));
        return true;
    }

    /// <summary>
    /// Returns the range of the scale space of the density's bin a hit of this part stands for, from the bin at its center
    /// (design §6.4 "密度のクリック", Issue #471 Decisions 9 and 17): the bin drawn there, an ancestor's bin or a leaf's fill
    /// whole, as the snapshot's origin and differences from it.
    /// </summary>
    internal override bool HitBinRange(
        in GeomFrame.Axis x,
        in GeomFrame.Axis y,
        in Interaction.SeriesHit hit,
        out Interaction.ViewRange rangeX,
        out Interaction.ViewRange rangeY
    )
    {
        (rangeX, rangeY) = (default, default);
        if (!TryHitBin(x, y, hit, out _, out _, out var bin))
        {
            return false;
        }

        var snapshot = (_snapshot ?? _coarse)!;
        rangeX = new Interaction.ViewRange(snapshot.XRange.Origin, bin.CornerX, bin.CornerX + bin.ExtentX);
        rangeY = new Interaction.ViewRange(snapshot.YRange.Origin, bin.CornerY, bin.CornerY + bin.ExtentY);
        return true;
    }

    /// <summary>Finds the bin a density's hit of this part stands for, from its center, and the axes' maps of the snapshot it was drawn from.</summary>
    private bool TryHitBin(
        in GeomFrame.Axis x,
        in GeomFrame.Axis y,
        in Interaction.SeriesHit hit,
        out AxisMap mapX,
        out AxisMap mapY,
        out DensityBin bin
    )
    {
        (mapX, mapY, bin) = (default, default, default);
        var snapshot = _snapshot ?? _coarse;
        if (hit.Row >= 0 || !_drawsDensity || snapshot is null)
        {
            return false;
        }

        var transform = CartesianTransform.Create(x, y);
        mapX = new AxisMap(x, transform, ofX: true, snapshot.XRange.Origin);
        mapY = new AxisMap(y, transform, ofX: false, snapshot.YRange.Origin);
        return TryBin(snapshot, mapX.DifferenceOfValue(hit.X), mapY.DifferenceOfValue(hit.Y), out bin);
    }

    /// <summary>
    /// Answers with the bin of the density drawn under the cursor (design §6.3 "密度で描いている層", Issue #471 Decisions 2
    /// and 10): a row of −1, the bin's center in data units, its points before their normalization, at a distance of 0,
    /// whatever the mode and the radius. A bin of no point, and a cursor outside the ranges, answer nothing.
    /// </summary>
    private void HitDensity(ref HitScan scan)
    {
        var snapshot = _snapshot ?? _coarse;
        if (snapshot is null)
        {
            return;
        }

        var transform = CartesianTransform.Create(scan.X, scan.Y);
        var x = new AxisMap(scan.X, transform, ofX: true, snapshot.XRange.Origin);
        var y = new AxisMap(scan.Y, transform, ofX: false, snapshot.YRange.Origin);
        if (!TryBin(snapshot, x.DifferenceAt(scan.Along), y.DifferenceAt(scan.Across), out var bin))
        {
            return;
        }

        scan.Add(
            0,
            -1,
            x.ValueOf(bin.CornerX + (bin.ExtentX / 2)),
            y.ValueOf(bin.CornerY + (bin.ExtentY / 2)),
            0,
            derived: false,
            provisional: _missing || snapshot.IsProvisional,
            count: bin.Count
        );
    }

    /// <summary>
    /// Finds the bin the last recording drew at a point given as differences from the snapshot's origins (design §5.5.2
    /// "水準の選び方", row R3c): on a coarse snapshot, the level-8 cell of the coarse density; on a full one, the cell of the
    /// chosen level (or the leaf above it) that holds the point, read from its own tile or its deepest ancestor's as the
    /// recording reads it, or the cell as one fill. False outside the ranges and for a bin of no point.
    /// </summary>
    private bool TryBin(SpatialSnapshot snapshot, double dx, double dy, out DensityBin bin)
    {
        bin = default;
        if (!(dx >= snapshot.XRange.Lower && dx <= snapshot.XRange.Upper && dy >= snapshot.YRange.Lower && dy <= snapshot.YRange.Upper))
        {
            return false;
        }

        var (gx, gy) = snapshot.GridAt(dx, dy);
        const int Bins = DensityTiles.BinLevels;
        if (snapshot.Stage != SpatialStage.Full)
        {
            // The coarse grid: the cells of level 8, the grid's upper 8 bits on each axis.
            var (cx, cy) = (gx >> (32 - Bins), gy >> (32 - Bins));
            var count = snapshot.CoarseDensity[(int)((cy << Bins) + cx)];
            return count > 0 && Make(snapshot, SpatialCode.Interleave(cx, cy), Bins, count, out bin);
        }

        Span<SpatialCell> one = stackalloc SpatialCell[1];
        if (snapshot.GetCells(new SpatialRect(dx, dx, dy, dy), _tileLevel, one) == 0)
        {
            return false;
        }

        // The same walk as the recording's pieces (PlanDensity): the cell's own tile, or its deepest ancestor's.
        var cell = one[0];
        var tiles = snapshot.Tiles;
        for (var at = Math.Min(cell.Level, DensityMath.DeepestLevel); at >= 0 && tiles is not null; at--)
        {
            if (tiles.TryGetTile(cell.Code >> (2 * (cell.Level - at)), at, out var tile))
            {
                // A bin of a tile of level `at` is a cell of level at + 8: the grid's upper at + 8 bits.
                var shift = 32 - (at + Bins);
                var (bx, by) = (gx >> shift, gy >> shift);
                var count = tile[(int)(bx & (DensityTiles.Side - 1)), (int)(by & (DensityTiles.Side - 1))];
                return count > 0 && Make(snapshot, SpatialCode.Interleave(bx, by), at + Bins, (long)count, out bin);
            }
        }

        return Make(snapshot, cell.Code, cell.Level, cell.Count, out bin);

        static bool Make(SpatialSnapshot snapshot, ulong code, int level, long count, out DensityBin made)
        {
            var (cornerX, cornerY, extentX, extentY) = snapshot.CellBox(code, level);
            made = new DensityBin(cornerX, cornerY, extentX, extentY, count);
            return true;
        }
    }

    // The screen of a hit test: the record's map of each axis, as the recording makes it for the same frame.
    private static DrawnScreen Screen(in GeomFrame.Axis x, in GeomFrame.Axis y, SpatialSnapshot snapshot)
    {
        var transform = CartesianTransform.Create(x, y);
        return new DrawnScreen(
            new AxisMap(x, transform, ofX: true, snapshot.XRange.Origin),
            new AxisMap(y, transform, ofX: false, snapshot.YRange.Origin)
        );
    }

    internal override Color HitColor(int series) => (((PointLayerSpec)Spec).Color ?? SeriesColor) with { A = 255 };

    /// <summary>
    /// Gets a value indicating whether the part highlights a selection, which it does (design §6.4 "生の点の強調", "密度の強調",
    /// row R3c): its runs and its density's tiles name the region of the panel's selection, whose outside the shaders fade.
    /// </summary>
    internal override bool HighlightsSelection => true;

    /// <summary>Gives back the snapshot, lets its pages go, and disposes the index.</summary>
    public override void Release()
    {
        Drop();
        DropCoarse();
        _index?.Dispose();
        _index = null;
        _started?.TrySetResult(null);
    }

    /// <summary>
    /// Returns the rows of the part a selection holds, found through its index on the workers (design §6.4 "行の集合" and
    /// "索引ができる前の行の集合", Issue #471 Decisions 4 and 13): from the full snapshot drawn, at once while a new one is
    /// built; before the first is full, once it is, so that the rows are exact. Points answer by their values, whatever is
    /// drawn. A part released before its index ends answers no rows.
    /// </summary>
    internal override ValueTask<Interaction.RowSelection> SelectRows(SelectionQuery query)
    {
        var length = Rows.EndIn(((PointLayerSpec)Spec).X.Column.Length);
        if (_snapshot is { } drawn && drawn.TryAddReference())
        {
            return Select(drawn, query, length);
        }

        return SelectWhenFull(query, length);
    }

    private static async ValueTask<Interaction.RowSelection> Select(SpatialSnapshot snapshot, SelectionQuery query, long length)
    {
        try
        {
            return await SpatialSelection.Select(snapshot, query.Selection, length, query.Pool).ConfigureAwait(false);
        }
        finally
        {
            snapshot.Release();
        }
    }

    private async ValueTask<Interaction.RowSelection> SelectWhenFull(SelectionQuery query, long length)
    {
        var index = _index ?? await (_started ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.ConfigureAwait(false);
        if (index is null)
        {
            return Interaction.RowSelection.Empty;
        }

        SpatialSnapshot snapshot;
        try
        {
            // The first build's end: its snapshot is full. A build that fails throws its exception here.
            await index.Completion.ConfigureAwait(false);
            snapshot = index.Acquire();
        }
        catch (ObjectDisposedException)
        {
            // The part was released: its layer is drawn no more.
            return Interaction.RowSelection.Empty;
        }

        return await Select(snapshot, query, length).ConfigureAwait(false);
    }

    private void DropCoarse()
    {
        if (_coarse is { } coarse)
        {
            _cache?.ForgetDensity(coarse);
            coarse.Release();
            _coarse = null;
        }
    }

    /// <summary>
    /// Returns the upper count of the points of the view (design §5.5.2 "画面内の点の数"): <c>Inside + Touching</c> at the
    /// level whose cells are about a physical pixel; 0 without a full snapshot.
    /// </summary>
    internal long UpperCount(RecordContext context)
    {
        if (_snapshot is not { } snapshot)
        {
            return 0;
        }

        var x = new AxisMap(context.X, context.Transform, ofX: true, snapshot.XRange.Origin);
        var y = new AxisMap(context.Y, context.Transform, ofX: false, snapshot.YRange.Origin);
        var (_, _, rangeX, rangeY) = snapshot.CellBox(0, 0);
        var count = snapshot.Count(new SpatialRect(x.Low, x.High, y.Low, y.High), DensityMath.PixelLevel(rangeX * x.PerUnit, rangeY * y.PerUnit));
        return count.Inside + count.Touching;
    }

    /// <summary>
    /// Plans the part's density for the frame (design §5.5.2 "水準の選び方", "広さで正規化する"): the tile level whose bins are
    /// at least a physical pixel, and a piece for each cell of the view at that level, from the cell's tile, its deepest
    /// ancestor's, or one fill. Returns the largest normalized count of the pieces, for the top of the scale; 0 when the part
    /// draws no density this frame.
    /// </summary>
    internal double PlanDensity(RecordContext context, bool density)
    {
        _pieceCount = 0;
        var snapshot = _snapshot;
        _drawsDensity = snapshot is null ? _coarse is not null : density;
        if (!_drawsDensity)
        {
            return 0;
        }

        snapshot ??= _coarse!;
        var x = new AxisMap(context.X, context.Transform, ofX: true, snapshot.XRange.Origin);
        var y = new AxisMap(context.Y, context.Transform, ofX: false, snapshot.YRange.Origin);
        var (_, _, rangeX, rangeY) = snapshot.CellBox(0, 0);
        var level = DensityMath.TileLevel(rangeX * x.PerUnit, rangeY * y.PerUnit);
        _tileLevel = level;
        var top = 0.0;

        // A coarse snapshot has one grid: the level-8 counts, drawn as the root's tile (design §5.5.2 "索引ができるまで").
        if (snapshot.Stage != SpatialStage.Full)
        {
            Add(new DensityPiece(0, 0, 0, 0, 0));
            var coarse = snapshot.CoarseDensity;
            long largest = 0;
            foreach (var bin in coarse)
            {
                largest = Math.Max(largest, bin);
            }

            return DensityMath.Normalized(largest, DensityMath.BinArea(rangeX, rangeY, x.PerUnit, y.PerUnit));
        }

        var rect = new SpatialRect(x.Low, x.High, y.Low, y.High);
        var found = snapshot.GetCells(rect, level, _cells);
        if (found > _cells.Length)
        {
            _cells = new SpatialCell[Math.Max(found, _cells.Length * 2)];
            found = snapshot.GetCells(rect, level, _cells);
        }

        var tiles = snapshot.Tiles;
        foreach (ref readonly var cell in _cells.AsSpan(0, found))
        {
            // The cell's own tile, or its deepest ancestor's: the ancestor's bins hold the cell's points too.
            var drawn = false;
            for (var at = Math.Min(cell.Level, DensityMath.DeepestLevel); at >= 0 && tiles is not null; at--)
            {
                var code = cell.Code >> (2 * (cell.Level - at));
                if (tiles.TryGetTile(code, at, out var tile))
                {
                    Add(new DensityPiece(cell.Code, cell.Level, code, at, 0));
                    var (_, _, tileX, tileY) = snapshot.CellBox(code, at);
                    top = Math.Max(top, DensityMath.Normalized(tile.Largest, DensityMath.BinArea(tileX, tileY, x.PerUnit, y.PerUnit)));
                    drawn = true;
                    break;
                }
            }

            if (!drawn)
            {
                // No tile above: the cell's points as one fill, normalized by the cell's area on the screen.
                var (_, _, cellX, cellY) = snapshot.CellBox(cell.Code, cell.Level);
                var count = DensityMath.Normalized(cell.Count, Math.Abs(cellX * x.PerUnit * cellY * y.PerUnit));
                Add(new DensityPiece(cell.Code, cell.Level, 0, -1, count));
                top = Math.Max(top, count);
            }
        }

        return top;
    }

    private void Add(in DensityPiece piece)
    {
        if (_pieceCount == _pieces.Length)
        {
            Array.Resize(ref _pieces, _pieces.Length * 2);
        }

        _pieces[_pieceCount++] = piece;
    }

    /// <summary>
    /// Records the part's density (design §5.5.2 "描き方"): each piece through the image command, its tile's face read over
    /// the cell's part with the count normalized to a bin of the chosen level; or one rectangle of a fill's color. A face not
    /// on the GPU yet leaves the frame provisional.
    /// </summary>
    private void RecordDensity(RecordContext context, PointLayerSpec point)
    {
        var top = _switch.Top;
        if (_pieceCount == 0 || !(top > 0))
        {
            return;
        }

        var snapshot = (_snapshot ?? _coarse)!;
        var x = new AxisMap(context.X, context.Transform, ofX: true, snapshot.XRange.Origin);
        var y = new AxisMap(context.Y, context.Transform, ofX: false, snapshot.YRange.Origin);
        var color = point.Color ?? SeriesColor;
        if (point.DensityMap is null && (_ramp is null || _rampColor != color))
        {
            (_ramp, _rampColor) = (DensityMath.Ramp(color), color);
        }

        var map = point.DensityMap ?? _ramp!;
        var table = context.Images.Table(map);
        if (table < 0)
        {
            throw new InvalidOperationException(
                $"Layer {Index} draws a density with a color map past the {ResidencyCache.MaxColorTables} the backend holds in one frame (design §5.2)."
            );
        }

        var level = _tileLevel;
        var bins = (double)(1 << DensityTiles.BinLevels);
        foreach (ref readonly var piece in _pieces.AsSpan(0, _pieceCount))
        {
            var (cornerX, cornerY, extentX, extentY) = snapshot.CellBox(piece.CellCode, piece.CellLevel);
            if (piece.TileLevel < 0)
            {
                // A fill: its color from the normalized count on the CPU, the same position the shader would take.
                var t = DensityMath.Position(point.DensityNorm, piece.Count, top);
                var fill = map.Map(t);
                fill = fill with { A = (byte)Math.Round(fill.A * point.Opacity) };
                if (fill.A > 0)
                {
                    context.DrawHighlighted(x.Pixel(cornerX), x.Pixel(cornerX + extentX), y.Pixel(cornerY), y.Pixel(cornerY + extentY), fill);
                }

                continue;
            }

            int slot;
            if (snapshot.Stage == SpatialStage.Full)
            {
                if (snapshot.Tiles is not { } tiles || !tiles.TryGetTile(piece.TileCode, piece.TileLevel, out var tile))
                {
                    continue;
                }

                slot = context.Images.RequestDensity(snapshot, piece.TileLevel, piece.TileCode, tile);
            }
            else
            {
                slot = context.Images.RequestCoarseDensity(snapshot);
            }

            if (slot < 0)
            {
                _missing = true;
                continue;
            }

            // The cell drawn in bins of the chosen level: 256 · 2^(level − cell level) a side, read from the tile's face
            // at its own scale, an ancestor's 2^(level − tile level) of them a face texel.
            var side = bins * Math.Pow(2, level - piece.CellLevel);
            var (tileX, tileY, tileExtentX, tileExtentY) = snapshot.CellBox(piece.TileCode, piece.TileLevel);
            var shallower = level - piece.TileLevel;
            var area = DensityMath.BinArea(tileExtentX, tileExtentY, x.PerUnit, y.PerUnit);
            var (kind, constant, high) = DensityMath.Paint(point.DensityNorm, top, area);
            var (alongX, alongY) = (x.PerUnit * extentX / side, y.PerUnit * extentY / side);
            var record = new ImageInstance
            {
                Origin = DensityMath.Screen(x.Pixel(cornerX), y.Pixel(cornerY), context.Flipped),
                AlongU = DensityMath.Screen(alongX, 0, context.Flipped),
                AlongV = DensityMath.Screen(0, alongY, context.Flipped),
                Range = new Vector4(0, 0, (float)side, (float)side),
                SampleOrigin = new Vector2((float)((cornerX - tileX) / tileExtentX * bins), (float)((cornerY - tileY) / tileExtentY * bins)),
                SampleScale = (float)Math.Pow(2, -shallower),
                Slice = (uint)(slot % ResidencyCache.PageSlots),
                Flags = (kind << 2) | ImageInstance.ZeroTransparent,
                Table = (uint)table,
                Constant = constant,
                Low = 0,
                High = high,
                Opacity = (float)point.Opacity,
                Highlight = (uint)context.Highlight,
            };
            context.List.DrawImage(record, ImagePlan.PageOf(rgba: false, slot));
        }
    }

    private void Drop()
    {
        if (_snapshot is { } snapshot)
        {
            _cache?.Points.Forget(snapshot);
            _cache?.ForgetDensity(snapshot);
            snapshot.Release();
            _snapshot = null;
            (_lastX, _lastY, _lastPosition) = (double.NaN, double.NaN, -1);
        }
    }

    /// <summary>A bin of a density a hit stands for: its box as differences from the snapshot's origins, and its points.</summary>
    private readonly record struct DensityBin(double CornerX, double CornerY, double ExtentX, double ExtentY, long Count);

    /// <summary>
    /// Where the frame draws the part's points (design §6.3 "描いた位置で測る"): each axis's map of the recording, and a
    /// resident point placed from its cell's record as the vertex shader places it, in floats, then held where the axis
    /// clamps (Markers.hlsl, ResidentMarkerVS).
    /// </summary>
    private struct DrawnScreen(AxisMap x, AxisMap y) : ISpatialScreen
    {
        private uint _baseX;
        private uint _baseY;
        private float _pixelX;
        private float _pixelY;
        private float _scaleX;
        private float _scaleY;

        public readonly double Along(double difference) => x.Held(x.Pixel(difference));

        public readonly double Across(double difference) => y.Held(y.Pixel(difference));

        public void Enter(in ResidentCell cell)
        {
            (_baseX, _pixelX, _scaleX) = x.Place(cell.CornerX, cell.ExtentX);
            (_baseY, _pixelY, _scaleY) = y.Place(cell.CornerY, cell.ExtentY);
        }

        public readonly (double Along, double Across) Place(uint unitX, uint unitY) =>
            (x.Held(_pixelX + (UnitsFrom(unitX, _baseX) * _scaleX)), y.Held(_pixelY + (UnitsFrom(unitY, _baseY) * _scaleY)));

        // The shader's UnitsFrom: the difference of two units taken in integers before it becomes a float.
        private static float UnitsFrom(uint position, uint origin) => position >= origin ? (float)(position - origin) : -(float)(origin - position);
    }

    /// <summary>
    /// One axis of a recording: from a difference from the snapshot's origin to the pixel along the axis, and the view's
    /// lower and upper ends as such differences.
    /// </summary>
    private readonly struct AxisMap
    {
        private readonly CartesianTransform _transform;
        private readonly bool _ofX;
        private readonly bool _clamps;
        private readonly double _shift;
        private readonly double _low;
        private readonly double _high;
        private readonly double _perUnit;
        private readonly double _atLow;
        private readonly long _origin;
        private readonly IScale? _scale;

        /// <summary>Gets the view's lower end as a difference from the snapshot's origin.</summary>
        public double Low => _low;

        /// <summary>Gets the view's upper end as a difference from the snapshot's origin.</summary>
        public double High => _high;

        /// <summary>Gets the physical pixels of one unit of the scale space, signed.</summary>
        public double PerUnit => _perUnit;

        /// <summary>Returns the pixel of a difference from the snapshot's origin along this axis.</summary>
        public double Pixel(double difference) => Map(difference + _shift);

        public AxisMap(in GeomFrame.Axis axis, CartesianTransform transform, bool ofX, long snapshotOrigin)
        {
            (_transform, _ofX, _clamps, _origin, _scale) = (transform, ofX, axis.Clamp, snapshotOrigin, axis.Transform);

            // The copy's values are differences from the snapshot's origin; the transform takes differences from the domain's.
            _shift = (double)(snapshotOrigin - axis.Domain.Origin);
            var (lower, upper) = (Math.Min(axis.Domain.Lower, axis.Domain.Upper), Math.Max(axis.Domain.Lower, axis.Domain.Upper));
            (_low, _high) = (lower - _shift, upper - _shift);
            var (atLower, atUpper) = (Map(lower), Map(upper));
            _atLow = atLower;

            // The pixels of one unit of the scale space: the map is a line, taken over the whole view for its precision.
            _perUnit = upper > lower ? (atUpper - atLower) / (upper - lower) : 0;
            (ClampLow, ClampHigh) = axis.Clamp
                ? ((float)Math.Min(atLower, atUpper), (float)Math.Max(atLower, atUpper))
                : (float.MinValue, float.MaxValue);
        }

        public float ClampLow { get; }

        public float ClampHigh { get; }

        /// <summary>Returns a pixel held where the axis clamps, as the vertex shader holds a center (row R3c).</summary>
        public double Held(double pixel) => Math.Clamp(pixel, ClampLow, ClampHigh);

        /// <summary>Returns the difference from the snapshot's origin at a pixel along this axis: the inverse of <see cref="Pixel"/> (row R3c).</summary>
        public double DifferenceAt(double pixel) => _low + ((pixel - _atLow) / _perUnit);

        /// <summary>Returns the difference from the snapshot's origin of a value in data units: through the scale's transform, if any.</summary>
        public double DifferenceOfValue(double value)
        {
            if (_scale is null)
            {
                return value - _origin;
            }

            Span<double> one = [value];
            _scale.Forward(one, one);
            return one[0] - _origin;
        }

        /// <summary>Returns the value in data units of a difference from the snapshot's origin: through the inverse of the scale's transform, if any.</summary>
        public double ValueOf(double difference)
        {
            if (_scale is null)
            {
                return _origin + difference;
            }

            Span<double> one = [_origin + difference];
            _scale.Inverse(one, one);
            return one[0];
        }

        /// <summary>
        /// Returns whether a cell's points are drawn on this axis: the cell reaches into the view, or the axis clamps, so
        /// that the points outside the view are drawn at its ends as the CPU path draws them.
        /// </summary>
        public bool Sees(double corner, double extent) => _clamps || (corner <= _high && corner + extent >= _low);

        /// <summary>
        /// Returns a cell's record on this axis (design §5.5.2 "セルの表"): the base, the view's lower end held in the cell,
        /// in units of 2^-32 of the extent; the base's pixel; and the pixels of one unit.
        /// </summary>
        public (uint Base, float Pixel, float Scale) Place(double corner, double extent)
        {
            if (!(extent > 0))
            {
                return (0, (float)Map(corner + _shift), 0);
            }

            var unit = Math.Floor((_low - corner) / extent * SpatialSnapshot.CellUnits);
            var at =
                unit >= SpatialSnapshot.CellUnits - 1 ? uint.MaxValue
                : unit > 0 ? (uint)unit
                : 0;
            var value = corner + (at / SpatialSnapshot.CellUnits * extent);
            return (at, (float)Map(value + _shift), (float)(_perUnit * extent / SpatialSnapshot.CellUnits));
        }

        private double Map(double difference) => _ofX ? _transform.MapX(difference) : _transform.MapY(difference);
    }
}
