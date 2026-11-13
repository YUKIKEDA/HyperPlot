using System.Runtime.InteropServices;

namespace HyperPlot;

/// <summary>
/// What a <see cref="SpatialIndex"/> knows at one moment (design §3.4 "スナップショット"): nothing, the counts of levels
/// 0 to 8, or the counts, the table, and the copy. A snapshot is not written after the index publishes it, so a query
/// reads it without a lock, without allocating, and in a time that does not grow with the number of points.
/// </summary>
/// <remarks>
/// <see cref="SpatialIndex.Acquire"/> and <see cref="Release"/> are called in pairs, and a snapshot is not touched after
/// its <see cref="Release"/>: the table and the copy are freed when the last borrower has released it. A borrowed
/// snapshot stays readable when the index replaces it and when the index is disposed.
/// </remarks>
internal abstract unsafe class SpatialSnapshot
{
    /// <summary>The deepest level whose counts a coarse snapshot holds.</summary>
    internal const int CoarseLevel = 8;

    /// <summary>The number of cells of levels 0 to <see cref="CoarseLevel"/>: (4^9 − 1) / 3.</summary>
    internal const int CoarseLength = 87_381;

    /// <summary>
    /// The most points of a resident cell that is not a leaf (design §5.5.2 "常駐のセルとページ"): a cell of the table with
    /// no more points whose parent has more.
    /// </summary>
    internal const int CellCapacity = 32_768;

    /// <summary>The units a resident cell's extent is cut into on each axis: a resident point's position is one of them.</summary>
    internal const double CellUnits = 4294967296.0;

    private const int DeepestLevel = 32;

    // A walk pops one cell and pushes its four children, so the stack grows by three a level: 1 + 3 × 32 cells.
    private const int StackLength = 1 + (3 * DeepestLevel);
    private const int CoarseStackLength = 1 + (3 * CoarseLevel);

    private readonly SpatialIndex _owner;
    private readonly SpatialAxis _xAxis;
    private readonly SpatialAxis _yAxis;
    private readonly long[]? _coarse;
    private readonly SpatialCopy? _copy;
    private readonly long _rowsRead;
    private readonly long _xVersion;
    private readonly long _yVersion;
    private readonly bool _disagreed;

    // Whether a query's rectangle is clipped to the range on each axis: on an axis whose range the index found itself
    // (design §3.4 "範囲で切る", row R3c, Issue #471 Decision 3).
    private readonly bool _clipsX;
    private readonly bool _clipsY;

    // The table, which this snapshot owns from the end of its constructor to Free. Every read goes through Rows.
    private SpatialRow* _rows;
    private int _rowCount;
    private int _references = 1;
    private int _freed;
    private DensityTiles? _tiles;
    private ResidentCell[] _cells = [];

    /// <summary>Initializes a new instance of the <see cref="SpatialSnapshot"/> class that holds nothing.</summary>
    /// <param name="owner">The index.</param>
    /// <param name="rowsRead">The number of rows the index reads, which <see cref="Count"/> answers as touching.</param>
    private protected SpatialSnapshot(SpatialIndex owner, long rowsRead)
    {
        _owner = owner;
        _rowsRead = rowsRead;
        _xVersion = owner.X.Version;
        _yVersion = owner.Y.Version;
        Stage = SpatialStage.None;
        XRange = ScaleRange.None;
        YRange = ScaleRange.None;
    }

    /// <summary>Initializes a new instance of the <see cref="SpatialSnapshot"/> class that holds the counts of levels 0 to 8.</summary>
    /// <param name="owner">The index.</param>
    /// <param name="contents">What the build counted.</param>
    private protected SpatialSnapshot(SpatialIndex owner, in Contents contents)
    {
        _owner = owner;
        _coarse = contents.Coarse;
        _xVersion = contents.XVersion;
        _yVersion = contents.YVersion;
        _disagreed = contents.Disagreed;
        _clipsX = owner.Input.XRange is null;
        _clipsY = owner.Input.YRange is null;
        _xAxis = new SpatialAxis(contents.XRange, DeepestLevel);
        _yAxis = new SpatialAxis(contents.YRange, DeepestLevel);
        Stage = SpatialStage.Coarse;
        XRange = contents.XRange;
        YRange = contents.YRange;
        PointCount = contents.PointCount;
        ExcludedCount = contents.ExcludedCount;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SpatialSnapshot"/> class that holds the counts, the table, and the copy.
    /// </summary>
    /// <param name="owner">The index.</param>
    /// <param name="contents">What the build counted.</param>
    /// <param name="builder">The builder of the table, after the writing ended. The snapshot takes the table from it.</param>
    /// <param name="copy">The copy, which the snapshot owns from here on, or <see langword="null"/> when the index reads no rows.</param>
    private protected SpatialSnapshot(SpatialIndex owner, in Contents contents, SpatialTableBuilder builder, SpatialCopy? copy)
        : this(owner, contents)
    {
        Stage = SpatialStage.Full;
        _copy = copy;

        // Taking the table is the last statement: when it throws, the builder still owns the rows and the build still
        // owns the copy, and each is freed once.
        _rows = builder.TakeTable(out _rowCount);
        try
        {
            _cells = MakeResidentCells();
        }
        catch
        {
            // The rows are this snapshot's now, and nothing else will free them.
            _rowCount = 0;
            NativeMemory.AlignedFree(_rows);
            _rows = null;
            throw;
        }
    }

    /// <summary>Gets how far the build had come when the snapshot was made.</summary>
    public SpatialStage Stage { get; }

    /// <summary>
    /// Gets a value indicating whether the snapshot may differ from the columns (design §3.4 "暫定", invariant 4): it is
    /// not <see cref="SpatialStage.Full"/>, its build saw the columns change between two passes, or a column's version
    /// has moved since its build began. Each read compares the versions again.
    /// </summary>
    public bool IsProvisional => Stage != SpatialStage.Full || _disagreed || !MatchesColumns;

    /// <summary>
    /// Gets the number of points the snapshot holds: the points of the copy of a full snapshot, the points the first
    /// count saw of a coarse one, and 0 of a snapshot that holds nothing.
    /// </summary>
    public long PointCount { get; }

    /// <summary>Gets the number of rows that were skipped because a stored value of theirs is not finite.</summary>
    public long ExcludedCount { get; }

    /// <summary>Gets the range of x in the scale space. A snapshot that holds nothing has <see cref="ScaleRange.None"/>.</summary>
    public ScaleRange XRange { get; }

    /// <summary>Gets the range of y in the scale space. A snapshot that holds nothing has <see cref="ScaleRange.None"/>.</summary>
    public ScaleRange YRange { get; }

    /// <summary>
    /// Gets the number of points of each cell of level 8: 256 rows of 256 cells, the row of the least y first. A snapshot
    /// that holds nothing has no cells.
    /// </summary>
    public ReadOnlySpan<long> CoarseDensity => _coarse == null ? default : _coarse.AsSpan(LevelOffset(CoarseLevel));

    /// <summary>
    /// Gets the density tiles, or <see langword="null"/> while the build has not made them (design §3.4 "密度タイル").
    /// Only a full snapshot gets them, once, after every tile is made.
    /// </summary>
    public DensityTiles? Tiles => Volatile.Read(ref _tiles);

    /// <summary>
    /// Gets the resident cells in the order of their codes, which is the order of their points in the copy (design §5.5.2
    /// "常駐のセルとページ"): they do not overlap and cover every point. A snapshot that is not
    /// <see cref="SpatialStage.Full"/> has none.
    /// </summary>
    public ReadOnlySpan<ResidentCell> ResidentCells => _cells;

    /// <summary>Gets a value indicating whether the versions the build recorded are the columns' versions now.</summary>
    internal bool MatchesColumns => _xVersion == _owner.X.Version && _yVersion == _owner.Y.Version;

    /// <summary>Gets a value indicating whether the table and the copy were freed. The tests count the frees with it.</summary>
    internal bool IsFreed => Volatile.Read(ref _freed) != 0;

    /// <summary>Gets or sets the snapshot that was queued for freeing before this one. Only the pool's queue uses it.</summary>
    internal SpatialSnapshot? NextFree { get; set; }

    /// <summary>Gets the first row the index reads: the row of the point whose original position is 0.</summary>
    private protected long FirstRow => _owner.Input.Rows.Start;

    /// <summary>Gets the rows of the table, the root first. A snapshot that is not <see cref="SpatialStage.Full"/> has none.</summary>
    /// <remarks>The block holds the rows from the constructor to the free, which empties the view first. The tests compare the bytes of two tables with it.</remarks>
    internal ReadOnlySpan<SpatialRow> Rows => new(_rows, _rowCount);

    /// <summary>
    /// Counts the points in a rectangle without reading the copy (design §3.4 "問い合わせ"). The points in the rectangle
    /// are no fewer than <see cref="SpatialCount.Inside"/> and no more than that plus <see cref="SpatialCount.Touching"/>.
    /// On an axis whose range the index found itself, the rectangle is first clipped to the range (design §3.4 "範囲で切る"):
    /// a rectangle that misses the range counts nothing.
    /// </summary>
    /// <param name="rect">The rectangle, as differences from the origins of <see cref="XRange"/> and <see cref="YRange"/>.</param>
    /// <param name="maxLevel">
    /// The deepest level to open, from 0 to 32. A coarse snapshot opens no level deeper than 8.
    /// </param>
    /// <returns>
    /// The counts. A snapshot that holds nothing answers 0 inside and every row the index reads touching.
    /// </returns>
    /// <exception cref="ArgumentException">An end of <paramref name="rect"/> is NaN, or a lower end is greater than its upper end.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLevel"/> is less than 0 or greater than 32.</exception>
    public SpatialCount Count(in SpatialRect rect, int maxLevel)
    {
        CheckQuery(rect, maxLevel);
        if (Stage == SpatialStage.None)
        {
            return new SpatialCount(0, _rowsRead);
        }

        var clipped = rect;
        if (!ClipToRanges(ref clipped))
        {
            return default;
        }

        var grid = new GridRect(clipped, _xAxis, _yAxis);
        return Stage == SpatialStage.Full ? CountRows(grid, maxLevel) : CountCoarse(grid, Math.Min(maxLevel, CoarseLevel));
    }

    /// <summary>
    /// Writes the cells that hold a point and touch a rectangle and that are leaves or lie at a level, in the order of
    /// their codes (design §3.4 "問い合わせ"). On an axis whose range the index found itself, the rectangle is first clipped
    /// to the range (design §3.4 "範囲で切る"): a rectangle that misses the range touches no cell.
    /// </summary>
    /// <param name="rect">The rectangle, as differences from the origins of <see cref="XRange"/> and <see cref="YRange"/>.</param>
    /// <param name="maxLevel">The deepest level to open, from 0 to 32.</param>
    /// <param name="destination">Where the cells go. The cells that do not fit are not written.</param>
    /// <returns>
    /// The number of cells that match, which may be greater than the length of <paramref name="destination"/>. A snapshot
    /// that is not <see cref="SpatialStage.Full"/> answers 0.
    /// </returns>
    /// <exception cref="ArgumentException">An end of <paramref name="rect"/> is NaN, or a lower end is greater than its upper end.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLevel"/> is less than 0 or greater than 32.</exception>
    public int GetCells(in SpatialRect rect, int maxLevel, Span<SpatialCell> destination)
    {
        CheckQuery(rect, maxLevel);
        var clipped = rect;
        if (Stage != SpatialStage.Full || !ClipToRanges(ref clipped))
        {
            return 0;
        }

        var grid = new GridRect(clipped, _xAxis, _yAxis);
        var rows = Rows;
        Span<int> stack = stackalloc int[StackLength];
        var top = 0;
        stack[top++] = 0;
        var count = 0;
        while (top > 0)
        {
            ref readonly var row = ref rows[stack[--top]];
            if (row.Count == 0 || grid.Cover(row.Code, row.Level) == Covering.None)
            {
                continue;
            }

            if (row.FirstChild >= 0 && row.Level < maxLevel)
            {
                // The children are pushed last first, so that they are popped in the order of their codes.
                for (var child = 3; child >= 0; child--)
                {
                    stack[top++] = row.FirstChild + child;
                }

                continue;
            }

            if (count < destination.Length)
            {
                destination[count] = new SpatialCell(row.Code, row.Level, row.First, row.Count, (SpatialCellKind)row.Kind);
            }

            count++;
        }

        return count;
    }

    /// <summary>Reads points of the copy and the rows they came from (design §3.4 "問い合わせ").</summary>
    /// <param name="first">The position in the copy of the first point to read.</param>
    /// <param name="x">Where the x values go, as differences from the origin of <see cref="XRange"/>.</param>
    /// <param name="y">Where the y values go, as differences from the origin of <see cref="YRange"/>.</param>
    /// <param name="rows">Where the row numbers in the columns go.</param>
    /// <exception cref="ArgumentException">The three spans are not of one length.</exception>
    /// <exception cref="InvalidOperationException">The snapshot is not <see cref="SpatialStage.Full"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The points to read are not all among the <see cref="PointCount"/> points.</exception>
    public void ReadPoints(long first, Span<double> x, Span<double> y, Span<long> rows)
    {
        if (x.Length != y.Length || x.Length != rows.Length)
        {
            ThrowHelper.ThrowSpatialSpansDiffer(x.Length, y.Length, rows.Length);
        }

        if (Stage != SpatialStage.Full)
        {
            ThrowHelper.ThrowSpatialSnapshotNotFull();
        }

        if (first < 0 || x.Length > PointCount - first)
        {
            ThrowHelper.ThrowSpatialPointsOutOfRange(first, x.Length, PointCount);
        }

        // A full snapshot with a point has a copy, so the copy is read only when there is something to read.
        if (x.Length > 0 && _copy != null)
        {
            ReadCore(_copy, first, x, y, rows);
        }
    }

    /// <summary>
    /// Reads points of the copy as the GPU holds them (design §5.5.2 "常駐のセルとページ"): each point's position in its
    /// resident cell, in units of 2^-32 of the cell's extent on each axis, and the cell's number. Neither allocates nor
    /// grows with the number of points of the index.
    /// </summary>
    /// <param name="first">The position in the copy of the first point to read.</param>
    /// <param name="points">Where the points go.</param>
    /// <exception cref="InvalidOperationException">The snapshot is not <see cref="SpatialStage.Full"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The points to read are not all among the <see cref="PointCount"/> points.</exception>
    public void ReadBlock(long first, Span<ResidentPoint> points)
    {
        if (Stage != SpatialStage.Full)
        {
            ThrowHelper.ThrowSpatialSnapshotNotFull();
        }

        if (first < 0 || points.Length > PointCount - first)
        {
            ThrowHelper.ThrowSpatialPointsOutOfRange(first, points.Length, PointCount);
        }

        if (points.IsEmpty || _copy == null)
        {
            return;
        }

        // The cell of the first point: the last cell that begins at or before it.
        var cells = ResidentCells;
        var (low, high) = (0, cells.Length - 1);
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (cells[middle].First <= first)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        const int Chunk = 256;
        Span<double> xs = stackalloc double[Chunk];
        Span<double> ys = stackalloc double[Chunk];
        var cell = low;
        for (var done = 0; done < points.Length; done += Chunk)
        {
            var count = Math.Min(Chunk, points.Length - done);
            ReadValues(_copy, first + done, xs[..count], ys[..count]);
            for (var i = 0; i < count; i++)
            {
                var position = first + done + i;
                while (position >= cells[cell].First + cells[cell].Count)
                {
                    cell++;
                }

                ref readonly var c = ref cells[cell];
                points[done + i] = new ResidentPoint(UnitOf(xs[i] - c.CornerX, c.ExtentX), UnitOf(ys[i] - c.CornerY, c.ExtentY), (uint)cell);
            }
        }

        // The position in a cell: the difference from its corner over its extent, in 2^32 units, kept in the cell. A point
        // at the upper edge, or one a rounding put outside, takes the nearest unit. An extent of 0 gives NaN, which is 0.
        static uint UnitOf(double difference, double extent)
        {
            var unit = Math.Floor(difference / extent * CellUnits);
            return unit >= CellUnits - 1 ? uint.MaxValue
                : unit > 0 ? (uint)unit
                : 0;
        }
    }

    /// <summary>
    /// Gives back what <see cref="SpatialIndex.Acquire"/> lent. The snapshot is not touched after this call. When the
    /// last borrower releases a snapshot that the index has replaced, a worker frees its table and its copy; when the
    /// pool is disposed, this call frees them.
    /// </summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref _references) == 0 && (_rows != null || _copy != null))
        {
            _owner.Pool.QueueFree(this);
        }
    }

    /// <summary>Adds a borrower when the snapshot still has one (design §3.4 "参照の数").</summary>
    /// <returns><see langword="false"/> when the last reference was given back, so that the caller reads the index's snapshot again.</returns>
    internal bool TryAddReference()
    {
        var seen = Volatile.Read(ref _references);
        while (seen > 0)
        {
            var was = Interlocked.CompareExchange(ref _references, seen + 1, seen);
            if (was == seen)
            {
                return true;
            }

            seen = was;
        }

        return false;
    }

    /// <summary>Frees the table, the copy, and the density tiles. Only the first call does it.</summary>
    internal void Free()
    {
        if (Interlocked.Exchange(ref _freed, 1) != 0)
        {
            return;
        }

        // Nothing reads the snapshot any more: its references are 0, or it was never published.
        _rowCount = 0;
        NativeMemory.AlignedFree(_rows);
        _rows = null;
        _copy?.Dispose();
        _tiles?.Dispose();
    }

    /// <summary>Gives the snapshot its density tiles, which it owns from here on. The build calls it once, while it borrows the snapshot.</summary>
    /// <param name="tiles">The tiles.</param>
    internal void AttachTiles(DensityTiles tiles) => Volatile.Write(ref _tiles, tiles);

    /// <summary>
    /// Returns a cell's corner and extent as differences from the ranges' origins (design §3.4): the cell of a code at a
    /// level, as the resident cells and the density's pieces place it (row R3b1).
    /// </summary>
    /// <param name="code">The cell's code at its level.</param>
    /// <param name="level">The level, from 0 to 32.</param>
    internal (double CornerX, double CornerY, double ExtentX, double ExtentY) CellBox(ulong code, int level)
    {
        var shift = DeepestLevel - level;
        return (
            _xAxis.DifferenceOf((ulong)SpatialCode.Axis2(code, 0) << shift),
            _yAxis.DifferenceOf((ulong)SpatialCode.Axis2(code, 1) << shift),
            _xAxis.ExtentAt(level),
            _yAxis.ExtentAt(level)
        );
    }

    /// <summary>Makes the codes of points of the copy again from their stored values (design §3.4 "ビンの値").</summary>
    /// <param name="first">The position of the first point: the points to read are in the copy.</param>
    /// <param name="codes">Where the codes go: one for each point to read.</param>
    internal void ReadCodes(long first, Span<ulong> codes) => EncodeCore(_copy!, first, in _xAxis, in _yAxis, codes);

    /// <summary>Returns where a level's counts begin in the counts of levels 0 to 8: (4^level − 1) / 3.</summary>
    /// <param name="level">The level, from 0 to <see cref="CoarseLevel"/>.</param>
    /// <returns>The index of the level's first cell.</returns>
    internal static int LevelOffset(int level) => ((1 << (2 * level)) - 1) / 3;

    /// <summary>Reads points of the copy as the stored types.</summary>
    /// <param name="copy">The copy.</param>
    /// <param name="first">The position of the first point: the points to read are in the copy.</param>
    /// <param name="x">Where the x values go.</param>
    /// <param name="y">Where the y values go: as long as <paramref name="x"/>.</param>
    /// <param name="rows">Where the row numbers go: as long as <paramref name="x"/>.</param>
    private protected abstract void ReadCore(SpatialCopy copy, long first, Span<double> x, Span<double> y, Span<long> rows);

    /// <summary>Reads the x and y of points of the copy as differences from the ranges' origins.</summary>
    /// <param name="copy">The copy.</param>
    /// <param name="first">The position of the first point: the points to read are in the copy.</param>
    /// <param name="x">Where the x values go.</param>
    /// <param name="y">Where the y values go: as long as <paramref name="x"/>.</param>
    private protected abstract void ReadValues(SpatialCopy copy, long first, Span<double> x, Span<double> y);

    /// <summary>Makes the codes of points of the copy from their values of the stored types.</summary>
    /// <param name="copy">The copy.</param>
    /// <param name="first">The position of the first point: the points to read are in the copy.</param>
    /// <param name="xAxis">The quantization of x.</param>
    /// <param name="yAxis">The quantization of y.</param>
    /// <param name="codes">Where the codes go: one for each point to read.</param>
    private protected abstract void EncodeCore(SpatialCopy copy, long first, in SpatialAxis xAxis, in SpatialAxis yAxis, Span<ulong> codes);

    private static void CheckQuery(in SpatialRect rect, int maxLevel)
    {
        // The negated comparisons are true for NaN.
        if (!(rect.XMin <= rect.XMax) || !(rect.YMin <= rect.YMax))
        {
            ThrowHelper.ThrowSpatialRectInvalid();
        }

        if ((uint)maxLevel > DeepestLevel)
        {
            ThrowHelper.ThrowSpatialLevelOutOfRange(maxLevel);
        }
    }

    /// <summary>
    /// Clips a rectangle to the range of each axis whose range the index found itself (design §3.4 "範囲で切る"): no
    /// finite point lies outside such a range, so the points counted stay the same. On an axis whose range was passed,
    /// the points outside it lie in the end grid cells, and the rectangle stays as it is.
    /// </summary>
    /// <param name="rect">The rectangle, clipped in place.</param>
    /// <returns><see langword="false"/> when the rectangle misses a clipped range: it then holds no point.</returns>
    private bool ClipToRanges(ref SpatialRect rect)
    {
        var (xMin, xMax, yMin, yMax) = (rect.XMin, rect.XMax, rect.YMin, rect.YMax);
        if (_clipsX)
        {
            var (lower, upper) = (XRange.Lower, XRange.Upper);
            if (xMax < lower || xMin > upper)
            {
                return false;
            }

            (xMin, xMax) = (Math.Max(xMin, lower), Math.Min(xMax, upper));
        }

        if (_clipsY)
        {
            var (lower, upper) = (YRange.Lower, YRange.Upper);
            if (yMax < lower || yMin > upper)
            {
                return false;
            }

            (yMin, yMax) = (Math.Max(yMin, lower), Math.Min(yMax, upper));
        }

        rect = new SpatialRect(xMin, xMax, yMin, yMax);
        return true;
    }

    // Walks the table from the root and stops at each cell with at most CellCapacity points, or at a leaf with more: the
    // cells come in the order of their codes, as GetCells gives them.
    private ResidentCell[] MakeResidentCells()
    {
        var rows = Rows;
        if (rows.IsEmpty)
        {
            return [];
        }

        var cells = new List<ResidentCell>();
        Span<int> stack = stackalloc int[StackLength];
        var top = 0;
        stack[top++] = 0;
        while (top > 0)
        {
            ref readonly var row = ref rows[stack[--top]];
            if (row.Count == 0)
            {
                continue;
            }

            if (row.Count > CellCapacity && row.FirstChild >= 0)
            {
                for (var child = 3; child >= 0; child--)
                {
                    stack[top++] = row.FirstChild + child;
                }

                continue;
            }

            var shift = DeepestLevel - row.Level;
            cells.Add(
                new ResidentCell(
                    row.First,
                    row.Count,
                    _xAxis.DifferenceOf((ulong)SpatialCode.Axis2(row.Code, 0) << shift),
                    _yAxis.DifferenceOf((ulong)SpatialCode.Axis2(row.Code, 1) << shift),
                    _xAxis.ExtentAt(row.Level),
                    _yAxis.ExtentAt(row.Level)
                )
            );
        }

        return [.. cells];
    }

    private SpatialCount CountRows(in GridRect grid, int maxLevel)
    {
        var rows = Rows;
        Span<int> stack = stackalloc int[StackLength];
        var top = 0;
        stack[top++] = 0;
        long inside = 0;
        long touching = 0;
        while (top > 0)
        {
            ref readonly var row = ref rows[stack[--top]];
            if (row.Count == 0)
            {
                continue;
            }

            var covering = grid.Cover(row.Code, row.Level);
            if (covering == Covering.Whole)
            {
                inside += row.Count;
            }
            else if (covering == Covering.Part)
            {
                if (row.FirstChild < 0 || row.Level >= maxLevel)
                {
                    touching += row.Count;
                }
                else
                {
                    for (var child = 0; child < 4; child++)
                    {
                        stack[top++] = row.FirstChild + child;
                    }
                }
            }
        }

        return new SpatialCount(inside, touching);
    }

    private SpatialCount CountCoarse(in GridRect grid, int maxLevel)
    {
        ReadOnlySpan<long> counts = _coarse;
        Span<ulong> stack = stackalloc ulong[CoarseStackLength];
        Span<byte> levels = stackalloc byte[CoarseStackLength];
        var top = 0;
        stack[top] = 0;
        levels[top++] = 0;
        long inside = 0;
        long touching = 0;
        while (top > 0)
        {
            var code = stack[--top];
            int level = levels[top];
            var x = SpatialCode.Axis2(code, 0);
            var y = SpatialCode.Axis2(code, 1);
            var count = counts[LevelOffset(level) + (int)((y << level) + x)];
            if (count == 0)
            {
                continue;
            }

            var covering = grid.Cover(code, level);
            if (covering == Covering.Whole)
            {
                inside += count;
            }
            else if (covering == Covering.Part)
            {
                if (level >= maxLevel)
                {
                    touching += count;
                }
                else
                {
                    for (var child = 0UL; child < 4; child++)
                    {
                        stack[top] = (code << 2) | child;
                        levels[top++] = (byte)(level + 1);
                    }
                }
            }
        }

        return new SpatialCount(inside, touching);
    }

    /// <summary>What a build hands to the snapshots it makes.</summary>
    /// <param name="XRange">The range of x.</param>
    /// <param name="YRange">The range of y.</param>
    /// <param name="Coarse">The counts of levels 0 to 8, level after level, each level row-major. The snapshots of one build share them.</param>
    /// <param name="PointCount">The number of points.</param>
    /// <param name="ExcludedCount">The number of rows skipped.</param>
    /// <param name="XVersion">The version of the x column when the build began.</param>
    /// <param name="YVersion">The version of the y column when the build began.</param>
    /// <param name="Disagreed">Whether the copy and the counts did not agree.</param>
    internal readonly record struct Contents(
        ScaleRange XRange,
        ScaleRange YRange,
        long[] Coarse,
        long PointCount,
        long ExcludedCount,
        long XVersion,
        long YVersion,
        bool Disagreed
    );

    private enum Covering
    {
        None,
        Part,
        Whole,
    }

    // A rectangle of the grid, both ends in it. The quantization is non-decreasing, so the grid cells of the rectangle's
    // ends bound the grid cells of every value in it.
    private readonly struct GridRect
    {
        private readonly ulong _xMin;
        private readonly ulong _xMax;
        private readonly ulong _yMin;
        private readonly ulong _yMax;

        public GridRect(in SpatialRect rect, in SpatialAxis xAxis, in SpatialAxis yAxis)
        {
            _xMin = xAxis.CellOfDifference(rect.XMin);
            _xMax = xAxis.CellOfDifference(rect.XMax);
            _yMin = yAxis.CellOfDifference(rect.YMin);
            _yMax = yAxis.CellOfDifference(rect.YMax);
        }

        public Covering Cover(ulong code, int level)
        {
            // A cell of a level is 2^(32 − level) grid cells wide. The shift is at most 32, so it stays inside a ulong.
            var shift = DeepestLevel - level;
            var x0 = (ulong)SpatialCode.Axis2(code, 0) << shift;
            var y0 = (ulong)SpatialCode.Axis2(code, 1) << shift;
            var last = (1UL << shift) - 1;
            var x1 = x0 + last;
            var y1 = y0 + last;
            if (x1 < _xMin || x0 > _xMax || y1 < _yMin || y0 > _yMax)
            {
                return Covering.None;
            }

            return x0 >= _xMin && x1 <= _xMax && y0 >= _yMin && y1 <= _yMax ? Covering.Whole : Covering.Part;
        }
    }
}
