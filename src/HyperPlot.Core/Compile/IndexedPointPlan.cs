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
    private SpatialIndex? _index;
    private SpatialSnapshot? _snapshot;
    private ResidencyCache? _cache;
    private bool _missing;

    public IndexedPointPlan(PointLayerSpec spec, PlanBuild build, RowRange rows)
        : base(spec, rows)
    {
        SetColumns(spec.X.Column, spec.Y.Column);
        (_pool, _xTransform, _yTransform, _indexing) = (build.Pool, build.XTransform, build.YTransform, build.Indexing);
        _indexing.Enter(build.Layer);
    }

    /// <summary>Gets a value indicating whether the last recording asked for a page it did not get. Tests only.</summary>
    internal bool MissedPages => _missing;

    /// <summary>Gets the index, once its build was started. Tests only.</summary>
    internal SpatialIndex? SpatialIndex => _index;

    /// <inheritdoc />
    public override bool RecordedProvisional => _missing;

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

        // The newest full snapshot is the one drawn; an older one stays until the next is full.
        var current = _index.Acquire();
        if (ReferenceEquals(current, _snapshot) || current.Stage != SpatialStage.Full)
        {
            current.Release();
        }
        else
        {
            Drop();
            _snapshot = current;
        }

        return _snapshot is null ? LayerReadiness.Pending
            : _snapshot.IsProvisional || !_index.IsComplete ? LayerReadiness.Previous
            : LayerReadiness.Ready;
    }

    public override void Record(RecordContext context)
    {
        _missing = false;
        if (_snapshot is not { } snapshot)
        {
            return;
        }

        var point = (PointLayerSpec)Spec;
        var cells = snapshot.ResidentCells;
        if (cells.IsEmpty)
        {
            return;
        }

        _cache = context.Images;
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

        // Spike R3b2 (HP_PROBE_VISIBLE_BLOCKS): the run being made of consecutive blocks in view of one page, drawn when
        // the next range does not touch it.
        var block = SplitProbe.VisibleBlocks ? SplitProbe.BlockPoints : 0;
        var (runPage, runSlot, runFrom, runTo) = (-1L, -1, 0, 0);
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
            if (block > 0)
            {
                // Spike R3b2: the cell's points in each page, widened to the blocks' ends; the cells come in the copy's
                // order, so a range starts at or after the run's start.
                for (var page = cell.First / PointPages.PagePoints; page * PointPages.PagePoints < end; page++)
                {
                    var pageStart = page * PointPages.PagePoints;
                    var from = (int)(Math.Max(cell.First, pageStart) - pageStart) / block * block;
                    var rounded = ((Math.Min(end, pageStart + PointPages.PagePoints) - pageStart + block - 1) / block) * block;
                    var to = (int)Math.Min(PointPages.PointsOf(snapshot, (int)page), rounded);
                    if (page == runPage && from <= runTo)
                    {
                        runTo = Math.Max(runTo, to);
                        continue;
                    }

                    DrawRun(list, style, runSlot, runFrom, runTo);
                    if (page != runPage)
                    {
                        runSlot = pages.Request(snapshot, (int)page);
                        _missing |= runSlot < 0;
                    }

                    (runPage, runFrom, runTo) = (page, from, to);
                }

                continue;
            }

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

        DrawRun(list, style, runSlot, runFrom, runTo);
    }

    // Spike R3b2: draws a run of consecutive blocks of a page in one call; nothing when its page is not there.
    private static void DrawRun(RenderList list, in ResidentMarkerRun style, int slot, int from, int to)
    {
        if (slot >= 0 && to > from)
        {
            list.DrawResidentMarkers(style with { Slot = slot, Count = to - from, First = from });
        }
    }

    /// <summary>Gives back the snapshot, lets its pages go, and disposes the index.</summary>
    public override void Release()
    {
        Drop();
        _index?.Dispose();
        _index = null;
    }

    private void Drop()
    {
        if (_snapshot is { } snapshot)
        {
            _cache?.Points.Forget(snapshot);
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
