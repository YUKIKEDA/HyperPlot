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
/// <remarks>
/// It lives as long as the plans made with it: one making of the layers (the plan's, one off the frame, or one layer's
/// alone), whose plans are released together. A layer's number is its position in the spec of that making. A new spec
/// whose layer at a position names other columns (a reorder among them) makes every layer again with a new indexing, so
/// no remembered drawing passes to another layer's points. Layers over the same columns at both positions keep their
/// plans, each taking the new node as a change of looks: the drawing stays with the plan and its points, which are the
/// same, and a new <see cref="PointLayerSpec.Draw"/> or threshold forgets it (<see cref="DensitySwitch.Choose"/>).
/// </remarks>
internal sealed class SpatialIndexing
{
    /// <summary>The most parts of one layer on the index path: each index holds about 0.7 MB whatever its points.</summary>
    internal const int MaxParts = 64;

    private readonly Dictionary<int, int> _parts = [];
    private readonly Dictionary<int, long> _rows = [];
    private readonly Dictionary<int, DensitySwitch> _switches = [];
    private readonly Dictionary<(PlanBuild Build, int Layer), DensityPanel> _panels = [];

    /// <summary>Gets or sets the index started last: the next starts when its build has ended.</summary>
    public SpatialIndex? Building { get; set; }

    /// <summary>
    /// Gets the most resident cells of a layer's parts together (design §5.5.2 "セルの表"):
    /// <see cref="SpatialSnapshot.MaxResidentCells"/>, or fewer for a test.
    /// </summary>
    public int CellLimit { get; init; } = SpatialSnapshot.MaxResidentCells;

    /// <summary>Counts one more part of a layer on the index path, of the given number of rows.</summary>
    /// <exception cref="InvalidOperationException">The layer has more than <see cref="MaxParts"/> parts.</exception>
    public void Enter(int layer, long rows)
    {
        _ = _parts.TryGetValue(layer, out var count);
        if (count == MaxParts)
        {
            throw new InvalidOperationException(
                $"Layer {layer} is a point layer of more than {LayerPlan.MaxElements} rows split into more than {MaxParts} parts; each part has a spatial index of its own (design §5.5.2). Row R3d draws such a layer from one index."
            );
        }

        _parts[layer] = count + 1;
        _rows[layer] = _rows.GetValueOrDefault(layer) + rows;
    }

    /// <summary>
    /// Returns a part's share of its layer's resident cells (design §5.5.2 "セルの表", the owner's decision of 2026-11-22):
    /// 1 + ⌊(<see cref="CellLimit"/> − p) × the part's rows ÷ the rows of the layer's parts⌋, p being the layer's parts,
    /// fixed before the part's index is built. Every part keeps its root cell, and the shares together never pass the
    /// limit. Every part of a layer is counted before the first build starts, since the parts are made with the layer.
    /// </summary>
    public int CellShare(int layer, long rows)
    {
        // A column's length never changes (a new length is a new column, and a new plan), so the rows counted at Enter are
        // the rows each part asks its share for, and the shares together stay within the limit.
        var total = _rows.GetValueOrDefault(layer);
        if (total <= 0)
        {
            return CellLimit;
        }

        var spare = Math.Max(0, CellLimit - _parts.GetValueOrDefault(layer));
        return 1 + (int)((long)spare * rows / total);
    }

    /// <summary>
    /// Returns the switch the parts of a layer share (design §5.5.2 "`Auto` の切り替え"): one drawing a layer, chosen from
    /// all its parts, those of every facet panel too, since the makings of all panels pass one indexing.
    /// </summary>
    public DensitySwitch SwitchOf(int layer)
    {
        if (!_switches.TryGetValue(layer, out var shared))
        {
            shared = new DensitySwitch(layer);
            _switches[layer] = shared;
        }

        return shared;
    }

    /// <summary>
    /// Returns what the parts of a layer share in a panel (design §5.5.2 "ブロック", "濃さの上端 m"). A panel's build is made
    /// a panel, so the build and the layer name the panel's layer.
    /// </summary>
    public DensityPanel PanelOf(PlanBuild build, int layer)
    {
        if (!_panels.TryGetValue((build, layer), out var shared))
        {
            shared = new DensityPanel();
            _panels[(build, layer)] = shared;
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
    private readonly int _layer;

    // The switch the layer's parts share, in every panel, and what they share in this panel (design §5.5.2 "`Auto` の切り替え").
    private readonly DensitySwitch _switch;
    private readonly DensityPanel _panel;
    private SpatialIndex? _index;
    private SpatialSnapshot? _snapshot;

    // Ends with the index once Poll starts it, or with null once the part is released: a selection's rows asked before
    // wait for it (design §6.4 "索引ができる前の行の集合").
    private TaskCompletionSource<SpatialIndex?>? _started;

    // The coarse snapshot drawn as a density until the first full one is there (design §5.5.2 "索引ができるまで").
    private SpatialSnapshot? _coarse;
    private ResidencyCache? _cache;
    private bool _missing;

    // The density's pieces this frame (design §5.5.2 "水準の選び方") and their top, planned by the scan before any part records.
    private DensityPiece[] _pieces = new DensityPiece[64];
    private SpatialCell[] _cells = new SpatialCell[256];
    private int _pieceCount;
    private int _tileLevel;
    private double _top;
    private bool _drawsDensity;

    // What the scan before the recording found (design §5.5.2 "`Auto` の切り替え", Issue #472's decision of 2026-11-22): the
    // frame it ran in, whether it read a full snapshot, the upper count of the view, the largest bin, and the positions of
    // the resident cells whose points the view draws.
    private long _scanFrame = -1;
    private bool _scanFull;
    private long _scanCount;
    private double _largestBin;
    private double _estimate;
    private int[] _visible = new int[64];
    private int _visibleCount;

    // The bytes of the pages that hold a block in view, whole (design §5.5.2 "予算"): what drawing the part as raw points needs.
    private long _needed;

    // The cells' records of this frame's view (design §5.5.2 "セルの表"), written by the same walk that finds the visible
    // cells, and copied into the list when the part records raw points.
    private ResidentCellRecord[] _records = new ResidentCellRecord[64];

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
        _layer = build.Layer;
        _indexing.Enter(build.Layer, RowCount(spec, rows));
        _switch = _indexing.SwitchOf(build.Layer);
        _switch.Add(this);
        _panel = _indexing.PanelOf(build, build.Layer);
        _panel.Add(this);
    }

    /// <summary>Gets a value indicating whether the last recording drew a density (row R3b1). Tests only.</summary>
    internal bool DrewDensity => _drawsDensity;

    /// <summary>Gets the switch the layer's parts share, in every panel (row R3b2). Tests only.</summary>
    internal DensitySwitch Switch => _switch;

    /// <summary>Gets what the layer's parts share in the part's panel (row R3b2). Tests only.</summary>
    internal DensityPanel Panel => _panel;

    /// <summary>Gets the top of the part's pieces the last scan planned, in points a square physical pixel (row R3b2). Tests only.</summary>
    internal double PieceTop => _top;

    /// <summary>
    /// Gets the largest bin the last scan estimated from the resident cells in view (design §5.5.2 "密度タイルが付く前"),
    /// whether or not the tiles had come. Tests only.
    /// </summary>
    internal double Estimate => _estimate;

    /// <summary>Gets the positions of the resident cells the last scan found the view draws, in the copy's order. Tests only.</summary>
    internal ReadOnlySpan<int> VisibleCells => _visible.AsSpan(0, _visibleCount);

    /// <summary>Gets the upper count of the view the scan of this frame found, on a full snapshot; 0 otherwise.</summary>
    internal long ScannedCount => _scanCount;

    /// <summary>
    /// Gets the bytes the part needs to draw raw points in the view the scan of this frame found (design §5.5.2 "予算"): the
    /// whole contents of each page that holds a block in view, counted from the cells the recording draws, on a full
    /// snapshot; 0 otherwise, and for a <see cref="PointDraw.Density"/> layer.
    /// </summary>
    internal long NeededBytes => _needed;

    /// <summary>
    /// Gets the largest bin the scan of this frame found (design §5.5.2 "最大のビン"): the top of the part's pieces once the
    /// tiles have come, and before, the estimate from the resident cells in view.
    /// </summary>
    internal double ScannedLargestBin => _largestBin;

    /// <summary>Returns whether the part was scanned in a frame on a full snapshot: only such a part predicts a time.</summary>
    internal bool ScannedFull(long frame) => _scanFrame == frame && _scanFull;

    /// <inheritdoc />
    internal override bool Scans => true;

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
                CellLimit = _indexing.CellShare(_layer, RowCount(point, Rows)),
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

        // A worker's read of a page failed (design §5.5.2 "読みの例外"): the layer fails as for its build, the headless
        // render too.
        if (_index.ReadFault is { } read)
        {
            ExceptionDispatchInfo.Throw(read);
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

        // The drawing and the cells' records come from the scan before the recording (design §5.5.2 "記録の前の走査"),
        // which every recording runs first, the plot's, the annotations', and a figure's alike.
        if (_scanFrame != context.Images.Frame)
        {
            throw new InvalidOperationException("A point layer on the index path was recorded without the scan of its frame (design §5.5.2).");
        }

        // The layer's drawing, chosen once a frame for all its parts from their scans; a part on its coarse snapshot draws a
        // density anyway.
        _panel.Prepare(context, _switch.Choose(context, point));
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

        // The cells' records the scan before the recording wrote for this view (design §5.5.2 "記録の前の走査").
        var offset = list.AddResidentCells(cells.Length, out var records);
        _records.AsSpan(0, cells.Length).CopyTo(records);
        var color = point.Color ?? SeriesColor;
        var style = new ResidentMarkerRun(
            Slot: -1,
            First: 0,
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

        // The blocks of the part's pages, by the layer's parts in the panel (design §5.5.2 "ブロック"): the view does not
        // change them. The run being made is blocks of one page that follow one another, recorded when the next range does
        // not touch it (design §5.5.2 "見えるブロックだけを描く").
        var block = PointPages.BlockPoints(_panel.PartCount);
        var whole = pages.DrawsWholePages;

        // The cells whose points the view draws, found by the scan before the recording, in the copy's order.
        var (runPage, slot, runFrom, runTo) = (-1L, -1, 0, 0);
        foreach (var c in _visible.AsSpan(0, _visibleCount))
        {
            ref readonly var cell = ref cells[c];

            // The cell's points in each page they lie in, widened to the blocks' ends: a block's points are in steps, so
            // it is drawn whole. The cells come in the copy's order, so a range begins at or after the run's start.
            var end = cell.First + cell.Count;
            for (var page = cell.First / PointPages.PagePoints; page * PointPages.PagePoints < end; page++)
            {
                var pageStart = page * PointPages.PagePoints;
                var points = PointPages.PointsOf(snapshot, (int)page);
                var from = whole ? 0 : (int)(Math.Max(cell.First, pageStart) - pageStart) / block * block;
                var to = whole
                    ? points
                    : (int)Math.Min(points, (Math.Min(end, pageStart + PointPages.PagePoints) - pageStart + block - 1) / block * block);
                if (page == runPage && from <= runTo)
                {
                    runTo = Math.Max(runTo, to);
                    continue;
                }

                DrawRun(list, style, slot, runFrom, runTo);
                if (page != runPage)
                {
                    slot = pages.Request(snapshot, (int)page, block);
                    _missing |= slot < 0;
                }

                (runPage, runFrom, runTo) = (page, from, to);
            }
        }

        DrawRun(list, style, slot, runFrom, runTo);

        // A run of blocks that follow one another in a page, in one call. A page that is not on the GPU yet is recorded
        // too, with no slot, and draws nothing: the list and the backend's steps grow to what the view asks for in its
        // first frame, not later as its pages arrive.
        static void DrawRun(RenderList list, in ResidentMarkerRun style, int slot, int from, int to)
        {
            if (to > from)
            {
                list.DrawResidentMarkers(style with { Slot = slot, First = from, Count = to - from });
            }
        }
    }

    /// <summary>
    /// Answers a hit test with what the part draws (design §6.3 "散布の索引の道", row R3c, Issue #471): its raw points by the
    /// search over its index, measured where the vertex shader draws them and only those drawn inside the panel (Issue
    /// #569), or the density's bin under the cursor. The
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
        var found = snapshot.Nearest(ref screen, scan.PanelBox, scan.Along, scan.Across, scan.Radius, _markerRadius, scan.Mode, scratch);
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

        // The same walk as the recording's pieces (PlanPieces): the cell's own tile, or its deepest ancestor's.
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

    // The rows a part reads: its run of its columns.
    private static long RowCount(PointLayerSpec spec, RowRange rows) => Math.Max(0, rows.EndIn(spec.X.Column.Length) - rows.Start);

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
    /// Scans the part before any layer of the frame records (design §5.5.2 "`Auto` の切り替え", Issue #472's decision of
    /// 2026-11-22), once a frame, in the panel's context: on a coarse snapshot, the coarse density's piece; on a full one,
    /// the upper count of the view and the density's pieces and their top, for <see cref="PointDraw.Auto"/>, and one walk
    /// over the resident cells, which finds the cells whose points the view draws and the estimate of the largest bin from
    /// them. The largest bin is the pieces' top once the tiles have come, and the estimate before. A
    /// <see cref="PointDraw.Points"/> layer plans no piece and a <see cref="PointDraw.Density"/> one walks no cell.
    /// </summary>
    internal override void Scan(RecordContext context)
    {
        var point = (PointLayerSpec)Spec;
        (_scanFrame, _scanFull, _scanCount, _largestBin, _estimate, _needed) = (context.Images.Frame, false, 0, 0, 0, 0);
        _switch.Enter(context, point);
        (_pieceCount, _top, _visibleCount) = (0, 0, 0);
        var snapshot = _snapshot ?? _coarse;
        if (snapshot is null)
        {
            return;
        }

        var x = new AxisMap(context.X, context.Transform, ofX: true, snapshot.XRange.Origin);
        var y = new AxisMap(context.Y, context.Transform, ofX: false, snapshot.YRange.Origin);
        var (_, _, rangeX, rangeY) = snapshot.CellBox(0, 0);
        _tileLevel = DensityMath.TileLevel(rangeX * x.PerUnit, rangeY * y.PerUnit);
        if (snapshot.Stage != SpatialStage.Full)
        {
            _top = PlanCoarse(snapshot, x, y, rangeX, rangeY);
            return;
        }

        _scanFull = true;
        var view = new SpatialRect(x.Low, x.High, y.Low, y.High);
        if (point.Draw != PointDraw.Points)
        {
            _top = PlanPieces(snapshot, view, x, y);
        }

        if (point.Draw == PointDraw.Auto)
        {
            var count = snapshot.Count(view, DensityMath.PixelLevel(rangeX * x.PerUnit, rangeY * y.PerUnit));
            _scanCount = count.Inside + count.Touching;
        }

        if (point.Draw != PointDraw.Density)
        {
            var binArea = DensityMath.BinArea(Math.ScaleB(rangeX, -_tileLevel), Math.ScaleB(rangeY, -_tileLevel), x.PerUnit, y.PerUnit);
            ScanCells(snapshot.ResidentCells, x, y, point.Size * context.Scale, binArea);
            _needed = NeededOf(snapshot, snapshot.ResidentCells, _visible.AsSpan(0, _visibleCount));
        }

        _largestBin =
            point.Draw != PointDraw.Auto ? 0
            : snapshot.Tiles is null ? _estimate
            : _top;
    }

    /// <summary>
    /// Walks the resident cells once (design §5.5.2 "見えるブロックだけを描く", "密度タイルが付く前", "セルの表"): writes each
    /// cell's record of the view, keeps the positions of those that reach into the view widened by a marker's reach, whose
    /// points the recording draws, and takes the estimate of the largest bin, the greatest "points over the area on the
    /// screen" of the cells that touch the view itself, a cell smaller than a bin of the chosen level divided by the bin's
    /// area.
    /// </summary>
    private void ScanCells(ReadOnlySpan<ResidentCell> cells, in AxisMap x, in AxisMap y, float diameter, double binArea)
    {
        if (_visible.Length < cells.Length)
        {
            _visible = new int[Math.Max(cells.Length, _visible.Length * 2)];
            _records = new ResidentCellRecord[_visible.Length];
        }

        // A cell outside the view by less than a marker's reach has markers that cover pixels in it (design §5.5.2 "ページ"):
        // the reach of the vertex shader's box, half the diameter and half a pixel, in units of each axis.
        var reach = (diameter / 2) + 0.5;
        var (reachX, reachY) = (x.UnitsOf(reach), y.UnitsOf(reach));

        // A panel under a pixel wide or high is not scanned (PlotGuides.Empty: the plan records the background alone, and
        // a figure's cells under a pixel draw nothing), so PerUnit is positive here; the estimate is still 0 rather than NaN
        // or infinite for an area that is 0, NaN, or past a double (DensityMath.CellEstimate).
        var (estimate, count) = (0.0, 0);
        for (var c = 0; c < cells.Length; c++)
        {
            ref readonly var cell = ref cells[c];
            var (baseX, pixelX, scaleX) = x.Place(cell.CornerX, cell.ExtentX);
            var (baseY, pixelY, scaleY) = y.Place(cell.CornerY, cell.ExtentY);
            _records[c] = new ResidentCellRecord(baseX, baseY, pixelX, pixelY, scaleX, scaleY);
            if (x.Sees(cell.CornerX, cell.ExtentX, reachX) && y.Sees(cell.CornerY, cell.ExtentY, reachY))
            {
                _visible[count++] = c;
            }

            if (x.Touches(cell.CornerX, cell.ExtentX) && y.Touches(cell.CornerY, cell.ExtentY))
            {
                estimate = Math.Max(estimate, DensityMath.CellEstimate(cell.Count, cell.ExtentX, cell.ExtentY, x.PerUnit, y.PerUnit, binArea));
            }
        }

        (_visibleCount, _estimate) = (count, estimate);
    }

    /// <summary>
    /// Returns the bytes of the pages that hold a block of the cells the recording draws (design §5.5.2 "予算"): each page's
    /// whole contents, 12 bytes a point, once, as the residency cache holds pages whole. The cells come in the copy's order,
    /// so their pages do too.
    /// </summary>
    internal static long NeededOf(SpatialSnapshot snapshot, ReadOnlySpan<ResidentCell> cells, ReadOnlySpan<int> visible)
    {
        var (bytes, counted) = (0L, -1L);
        foreach (var c in visible)
        {
            ref readonly var cell = ref cells[c];
            if (cell.Count <= 0)
            {
                continue;
            }

            var last = (cell.First + cell.Count - 1) / PointPages.PagePoints;
            for (var page = Math.Max(counted + 1, cell.First / PointPages.PagePoints); page <= last; page++)
            {
                bytes += (long)PointPages.PointsOf(snapshot, (int)page) * PointPages.PointBytes;
            }

            counted = Math.Max(counted, last);
        }

        return bytes;
    }

    /// <summary>
    /// Sets whether the part draws a density this frame, and returns the top of its pieces, for the top of the scale: 0
    /// when it draws none. A part on its coarse snapshot draws the coarse density whatever the layer's drawing.
    /// </summary>
    internal double PlanDensity(bool density)
    {
        _drawsDensity = _snapshot is null ? _coarse is not null : density;
        return _drawsDensity ? _top : 0;
    }

    /// <summary>
    /// Plans the coarse snapshot's one piece (design §5.5.2 "索引ができるまで"): the level-8 counts, drawn as the root's tile.
    /// Returns its largest normalized count.
    /// </summary>
    private double PlanCoarse(SpatialSnapshot snapshot, in AxisMap x, in AxisMap y, double rangeX, double rangeY)
    {
        Add(new DensityPiece(0, 0, 0, 0, 0));
        long largest = 0;
        foreach (var bin in snapshot.CoarseDensity)
        {
            largest = Math.Max(largest, bin);
        }

        return DensityMath.Normalized(largest, DensityMath.BinArea(rangeX, rangeY, x.PerUnit, y.PerUnit));
    }

    /// <summary>
    /// Plans the part's density for the frame on a full snapshot (design §5.5.2 "水準の選び方", "広さで正規化する"): a piece
    /// for each cell of the view at the chosen level, from the cell's tile, its deepest ancestor's, or one fill. Returns the
    /// largest normalized count of the pieces.
    /// </summary>
    private double PlanPieces(SpatialSnapshot snapshot, in SpatialRect rect, in AxisMap x, in AxisMap y)
    {
        var level = _tileLevel;
        var top = 0.0;
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
        var top = _panel.Top;
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

        /// <summary>Returns the units of this axis that span the given physical pixels: 0 when the view has no width.</summary>
        public double UnitsOf(double pixels) => _perUnit != 0 ? pixels / Math.Abs(_perUnit) : 0;

        /// <summary>
        /// Returns whether a cell's points are drawn on this axis: the cell reaches into the view widened by a marker's reach
        /// on each side, so that a marker whose center is outside the view and whose shape covers pixels in it is drawn, as
        /// the CPU path draws it; or the axis clamps, so that the points outside the view are drawn at its ends.
        /// </summary>
        /// <param name="corner">The cell's lower corner on this axis, as a difference from the snapshot's origin.</param>
        /// <param name="extent">The cell's extent on this axis.</param>
        /// <param name="reach">A marker's reach on this axis, in its units (<see cref="UnitsOf"/>).</param>
        public bool Sees(double corner, double extent, double reach) => _clamps || (corner <= _high + reach && corner + extent >= _low - reach);

        /// <summary>
        /// Returns whether a cell touches the view on this axis, whether or not the axis clamps (design §5.5.2 "密度タイルが付く前"):
        /// the cells whose points the estimate of the largest bin reads, as the density reads only the points in the view.
        /// </summary>
        /// <param name="corner">The cell's lower corner on this axis, as a difference from the snapshot's origin.</param>
        /// <param name="extent">The cell's extent on this axis.</param>
        public bool Touches(double corner, double extent) => corner <= _high && corner + extent >= _low;

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
