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
            Clip: default
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

    /// <summary>Gives back the snapshot, lets its pages go, and disposes the index.</summary>
    public override void Release()
    {
        Drop();
        DropCoarse();
        _index?.Dispose();
        _index = null;
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

            return DensityMath.Normalized(largest, level);
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
                    top = Math.Max(top, DensityMath.Normalized(tile.Largest, level - at));
                    drawn = true;
                    break;
                }
            }

            if (!drawn)
            {
                // No tile above: the cell's points as one fill, a bin of the chosen level's share of them.
                var count = DensityMath.Normalized(cell.Count, level + DensityTiles.BinLevels - cell.Level);
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
                $"Layer {Index} draws a density with a color map past the {ResidencyCache.MaxColorTables} the backend holds (design §5.2)."
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
                    context.DrawRect(x.Pixel(cornerX), x.Pixel(cornerX + extentX), y.Pixel(cornerY), y.Pixel(cornerY + extentY), fill);
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
            var (kind, constant, high) = DensityMath.Paint(point.DensityNorm, top, shallower);
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
        }
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
            (_transform, _ofX, _clamps) = (transform, ofX, axis.Clamp);

            // The copy's values are differences from the snapshot's origin; the transform takes differences from the domain's.
            _shift = (double)(snapshotOrigin - axis.Domain.Origin);
            var (lower, upper) = (Math.Min(axis.Domain.Lower, axis.Domain.Upper), Math.Max(axis.Domain.Lower, axis.Domain.Upper));
            (_low, _high) = (lower - _shift, upper - _shift);
            var (atLower, atUpper) = (Map(lower), Map(upper));

            // The pixels of one unit of the scale space: the map is a line, taken over the whole view for its precision.
            _perUnit = upper > lower ? (atUpper - atLower) / (upper - lower) : 0;
            (ClampLow, ClampHigh) = axis.Clamp
                ? ((float)Math.Min(atLower, atUpper), (float)Math.Max(atLower, atUpper))
                : (float.MinValue, float.MaxValue);
        }

        public float ClampLow { get; }

        public float ClampHigh { get; }

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
