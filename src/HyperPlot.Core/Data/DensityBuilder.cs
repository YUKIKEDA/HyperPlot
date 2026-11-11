using System.Runtime.InteropServices;

namespace HyperPlot;

/// <summary>
/// Builds the density tiles of a full snapshot from its table and its copy (design §3.4 "密度タイル"), one level at a
/// time from the deepest. A unit of work is one quadrant of one tile, or one run of the copy of a quadrant that holds
/// more points than the density split; the units of one tile belong to one group, which counts them in a work tile of
/// 8-byte bins and then packs the tile at the width of its largest bin.
/// </summary>
/// <remarks>
/// The builder owns the packed tiles until <see cref="Finish"/> hands them over. A group is run by one worker at a
/// time, and the levels come one after the other, so a unit reads only tiles that are packed.
/// </remarks>
internal sealed unsafe class DensityBuilder : IDisposable
{
    private const int Groups = SpatialIndexTuning.GroupCount;
    private const int Half = DensityTiles.Side / 2;
    private const int Alignment = 64;
    private const int DeepestLevel = 32;

    private readonly SpatialSnapshot _snapshot;
    private readonly int _rowsPerUnit;
    private readonly ulong[][] _work = new ulong[Groups][];
    private readonly ulong[][] _codes = new ulong[Groups][];

    // The row of each tile's cell in the table, in the order of the level and then of the code, and where each level begins.
    private readonly int[] _tileRows;
    private readonly int[] _levelStarts;

    // The tile of each row of the table, or −1.
    private readonly int[] _tileOfRow;

    // The packed bins of each tile and their width: 0 until the tile is packed.
    private readonly nint[] _bins;
    private readonly byte[] _widths;
    private readonly long[] _groupStarts = new long[Groups + 1];
    private Unit[] _units = [];
    private int _level;

    /// <summary>Initializes a new instance of the <see cref="DensityBuilder"/> class and chooses the cells that get a tile.</summary>
    /// <param name="snapshot">The full snapshot, which stays borrowed while the builder runs.</param>
    /// <param name="densitySplit">The points above which a split cell has a tile.</param>
    /// <param name="rowsPerUnit">The most points of the copy that one unit reads of a quadrant with more points than <paramref name="densitySplit"/>.</param>
    /// <param name="sourceBytes">The bytes of the rows that the index reads in the two columns.</param>
    /// <param name="totalShare">The share of <paramref name="sourceBytes"/> that the table and the tiles may take together.</param>
    public DensityBuilder(SpatialSnapshot snapshot, long densitySplit, int rowsPerUnit, long sourceBytes, double totalShare)
    {
        _snapshot = snapshot;
        _rowsPerUnit = rowsPerUnit;
        var rows = snapshot.Rows;
        _tileOfRow = new int[rows.Length];
        _tileOfRow.AsSpan().Fill(-1);

        // The cells with a tile are closed upwards: a parent holds no fewer points than its child. Each level is listed
        // in the order of the codes, since the children of the level above are listed in that order.
        var tiles = new List<int>();
        var starts = new List<int> { 0 };
        var budget = (long)Math.Min(Math.Floor(totalShare * sourceBytes), long.MaxValue / 2) - ((long)rows.Length * sizeof(SpatialRow));
        long estimated = 0;
        var from = 0;
        if (HasTile(rows[0], densitySplit))
        {
            tiles.Add(0);
        }

        while (from < tiles.Count)
        {
            // The level is kept when the tiles down to it fit the budget at their estimated widths (decision 12).
            var to = tiles.Count;
            long level = 0;
            for (var tile = from; tile < to; tile++)
            {
                var bound = LargestBinBound(rows, tiles[tile], rows[tiles[tile]].Level + DensityTiles.BinLevels);
                level += DensityTiles.EntryBytes + ((long)DensityTiles.BinCount * DensityTiles.BytesPerBinFor(bound));
            }

            if (estimated + level > budget)
            {
                StoppedLevel = rows[tiles[from]].Level;
                tiles.RemoveRange(from, to - from);
                break;
            }

            estimated += level;
            starts.Add(to);
            for (var tile = from; tile < to; tile++)
            {
                var first = rows[tiles[tile]].FirstChild;
                for (var child = first; child < first + 4; child++)
                {
                    if (HasTile(rows[child], densitySplit))
                    {
                        tiles.Add(child);
                    }
                }
            }

            from = to;
        }

        EstimatedBytes = estimated;
        _tileRows = [.. tiles];
        _levelStarts = [.. starts];
        for (var tile = 0; tile < _tileRows.Length; tile++)
        {
            _tileOfRow[_tileRows[tile]] = tile;
        }

        _bins = new nint[_tileRows.Length];
        _widths = new byte[_tileRows.Length];
        _level = LevelCount;
        if (_tileRows.Length > 0)
        {
            for (var group = 0; group < Groups; group++)
            {
                _work[group] = new ulong[DensityTiles.BinCount];
                _codes[group] = new ulong[rowsPerUnit];
            }
        }
    }

    /// <summary>Gets the number of tiles.</summary>
    public int TileCount => _tileRows.Length;

    /// <summary>Gets the number of levels that have a tile: the levels 0 to this number − 1.</summary>
    public int LevelCount => _levelStarts.Length - 1;

    /// <summary>Gets the bytes of the tiles and their directory at the widths estimated from the table, which the real widths do not exceed.</summary>
    public long EstimatedBytes { get; }

    /// <summary>Gets the first level whose tiles the budget left out, or −1 when every cell that may have a tile has one.</summary>
    public int StoppedLevel { get; } = -1;

    /// <summary>Gets the number of units of the level that <see cref="NextLevel"/> began.</summary>
    public long UnitCount => _units.Length;

    /// <summary>Begins the next level, from the deepest to level 0.</summary>
    /// <returns><see langword="false"/> when every level is done.</returns>
    public bool NextLevel()
    {
        if (_level == 0)
        {
            _units = [];
            return false;
        }

        _level--;
        var rows = _snapshot.Rows;
        var first = _levelStarts[_level];
        var count = _levelStarts[_level + 1] - first;
        var units = new List<Unit>(4 * count);
        var next = 0;
        for (var group = 0; group < Groups; group++)
        {
            // A group has whole tiles: tile ⌊group × count / 8⌋ of the level to the tile before the next group's first.
            _groupStarts[group] = units.Count;
            var end = first + (int)((long)(group + 1) * count / Groups);
            for (; first + next < end; next++)
            {
                AddUnits(units, rows, first + next);
            }
        }

        _groupStarts[Groups] = units.Count;
        _units = [.. units];
        return true;
    }

    /// <summary>Returns the first unit of a group in the level that <see cref="NextLevel"/> began.</summary>
    /// <param name="group">The group, or the number of groups for the end of the last one.</param>
    /// <returns>The unit.</returns>
    public long GroupStart(int group) => _groupStarts[group];

    /// <summary>Runs one unit, and packs its tile when it is the tile's last.</summary>
    /// <param name="group">The group of the unit, which the calling worker has.</param>
    /// <param name="unit">The unit.</param>
    /// <param name="build">The build, whose stop ends a unit that reads the copy.</param>
    public void RunUnit(int group, long unit, SpatialBuild build)
    {
        var rows = _snapshot.Rows;
        ref readonly var work = ref _units[unit];
        var bins = _work[group].AsSpan();
        ref readonly var cell = ref rows[_tileRows[work.Tile]];
        var child = cell.FirstChild + work.Quadrant;
        if (_tileOfRow[child] is var below and >= 0)
        {
            AddChildTile(below, work.Quadrant, bins);
        }
        else
        {
            Count(rows, child, work.First, work.Count, cell.Level + DensityTiles.BinLevels, bins, _codes[group], build);
        }

        if (work.Last)
        {
            Pack(work.Tile, bins);
        }
    }

    /// <summary>Hands the tiles over. Every level is done.</summary>
    /// <returns>The tiles, which own the blocks from here on.</returns>
    public DensityTiles Finish()
    {
        var rows = _snapshot.Rows;
        var entries = new DensityTiles.Entry[_tileRows.Length];
        for (var tile = 0; tile < entries.Length; tile++)
        {
            ref readonly var cell = ref rows[_tileRows[tile]];
            entries[tile] = new DensityTiles.Entry
            {
                Code = cell.Code,
                Level = cell.Level,
                Bins = _bins[tile],
                BytesPerBin = _widths[tile],
            };
            _bins[tile] = 0;
        }

        return new DensityTiles(entries);
    }

    /// <summary>Frees the tiles that <see cref="Finish"/> has not handed over. No unit runs.</summary>
    public void Dispose()
    {
        for (var tile = 0; tile < _bins.Length; tile++)
        {
            NativeMemory.AlignedFree((void*)Interlocked.Exchange(ref _bins[tile], 0));
        }
    }

    private static bool HasTile(in SpatialRow row, long densitySplit) =>
        row.Kind == (byte)SpatialCellKind.Inner && row.Count > densitySplit && row.Level <= DensityTiles.DeepestTileLevel;

    // The most points that one bin of a tile can hold, read from the table alone: the points of a cell at the level of
    // the bins, and the points of a leaf that ends above that level, which may all lie in one bin.
    private static ulong LargestBinBound(ReadOnlySpan<SpatialRow> rows, int row, int binLevel)
    {
        ref readonly var cell = ref rows[row];
        if (cell.FirstChild < 0 || cell.Level >= binLevel)
        {
            return (ulong)cell.Count;
        }

        ulong largest = 0;
        for (var child = cell.FirstChild; child < cell.FirstChild + 4; child++)
        {
            largest = Math.Max(largest, LargestBinBound(rows, child, binLevel));
        }

        return largest;
    }

    // The bin of a cell at the level of a tile's bins: the lower 16 bits of its number are its place in the tile, x in
    // the even bits and y in the odd ones.
    private static int BinOf(ulong cell) => (int)((SpatialCode.Axis2(cell & 0xFFFF, 1) << 8) | SpatialCode.Axis2(cell & 0xFFFF, 0));

    private void AddUnits(List<Unit> units, ReadOnlySpan<SpatialRow> rows, int tile)
    {
        ref readonly var cell = ref rows[_tileRows[tile]];
        var before = units.Count;
        for (var quadrant = 0; quadrant < 4; quadrant++)
        {
            ref readonly var child = ref rows[cell.FirstChild + quadrant];
            if (child.Count == 0)
            {
                continue;
            }

            // A quadrant with a tile of its own is summed, and a coincident leaf is one read: neither grows with its points.
            var whole = _tileOfRow[cell.FirstChild + quadrant] >= 0 || child.Kind == (byte)SpatialCellKind.CoincidentLeaf;
            var step = whole || child.Count <= _rowsPerUnit ? child.Count : _rowsPerUnit;
            for (var first = child.First; first < child.First + child.Count; first += step)
            {
                units.Add(new Unit(tile, (byte)quadrant, first, Math.Min(step, child.First + child.Count - first), Last: false));
            }
        }

        if (units.Count == before)
        {
            // A cell with a tile holds points, so this does not happen; a unit of no point still packs the tile.
            units.Add(new Unit(tile, 0, 0, 0, Last: false));
        }

        units[^1] = units[^1] with { Last = true };
    }

    // Adds the 2 × 2 sums of a child's tile to the quadrant of its parent's work tile.
    private void AddChildTile(int child, int quadrant, Span<ulong> bins)
    {
        var width = _widths[child];

        // The child was packed in the level before this one: BinCount bins of its width.
        var packed = new ReadOnlySpan<byte>((void*)_bins[child], DensityTiles.BinCount * width);
        var tile = new DensityTile(packed, width);
        var left = (quadrant & 1) * Half;
        var top = (quadrant >> 1) * Half;
        for (var y = 0; y < Half; y++)
        {
            var row = bins.Slice(((top + y) * DensityTiles.Side) + left, Half);
            for (var x = 0; x < Half; x++)
            {
                row[x] += tile[2 * x, 2 * y] + tile[(2 * x) + 1, 2 * y] + tile[2 * x, (2 * y) + 1] + tile[(2 * x) + 1, (2 * y) + 1];
            }
        }
    }

    // Counts the points at the positions first to first + count − 1 of the copy, which lie in the cell of the row, into
    // the bins of the level binLevel.
    private void Count(
        ReadOnlySpan<SpatialRow> rows,
        int row,
        long first,
        long count,
        int binLevel,
        Span<ulong> bins,
        Span<ulong> codes,
        SpatialBuild build
    )
    {
        // A walk pops one cell and pushes its four children, so the stack grows by three a level.
        Span<int> stack = stackalloc int[1 + (3 * DeepestLevel)];
        var depth = 0;
        stack[depth++] = row;
        var end = first + count;
        var shift = 2 * (DeepestLevel - binLevel);
        while (depth > 0)
        {
            ref readonly var cell = ref rows[stack[--depth]];
            var from = Math.Max(first, cell.First);
            var to = Math.Min(end, cell.First + cell.Count);
            if (to <= from)
            {
                continue;
            }

            if (cell.Level >= binLevel)
            {
                // The cell lies in one bin: the table has its points.
                bins[BinOf(cell.Code >> (2 * (cell.Level - binLevel)))] += (ulong)(to - from);
                continue;
            }

            if (cell.FirstChild >= 0)
            {
                for (var child = cell.FirstChild + 3; child >= cell.FirstChild; child--)
                {
                    stack[depth++] = child;
                }

                continue;
            }

            if (cell.Kind == (byte)SpatialCellKind.CoincidentLeaf)
            {
                // Every point of the leaf lies in the grid cell of its first.
                _snapshot.ReadCodes(cell.First, codes[..1]);
                bins[BinOf(codes[0] >> shift)] += (ulong)(to - from);
                continue;
            }

            // A leaf that ends above the bins: the codes are made again from the stored values.
            while (from < to)
            {
                // A stopped build publishes no tile, and Dispose waits for this unit.
                if (build.IsStopped)
                {
                    return;
                }

                var read = codes[..(int)Math.Min(codes.Length, to - from)];
                _snapshot.ReadCodes(from, read);
                foreach (var code in read)
                {
                    bins[BinOf(code >> shift)]++;
                }

                from += read.Length;
            }
        }
    }

    // Writes the work tile at the width of its largest bin, and empties it for the group's next tile.
    private void Pack(int tile, Span<ulong> bins)
    {
        ulong largest = 0;
        foreach (var bin in bins)
        {
            largest = Math.Max(largest, bin);
        }

        var width = DensityTiles.BytesPerBinFor(largest);
        var block = NativeMemory.AlignedAlloc((nuint)(DensityTiles.BinCount * width), Alignment);

        // The block holds BinCount bins of the width.
        var packed = new Span<byte>(block, DensityTiles.BinCount * width);
        switch (width)
        {
            case sizeof(byte):
                for (var i = 0; i < bins.Length; i++)
                {
                    packed[i] = (byte)bins[i];
                }

                break;
            case sizeof(ushort):
                var shorts = MemoryMarshal.Cast<byte, ushort>(packed);
                for (var i = 0; i < bins.Length; i++)
                {
                    shorts[i] = (ushort)bins[i];
                }

                break;
            case sizeof(uint):
                var ints = MemoryMarshal.Cast<byte, uint>(packed);
                for (var i = 0; i < bins.Length; i++)
                {
                    ints[i] = (uint)bins[i];
                }

                break;
            default:
                bins.CopyTo(MemoryMarshal.Cast<byte, ulong>(packed));
                break;
        }

        _widths[tile] = (byte)width;
        _bins[tile] = (nint)block;
        bins.Clear();
    }

    // One unit: a quadrant of a tile, or the positions first to first + count − 1 of the copy in a quadrant.
    private readonly record struct Unit(int Tile, byte Quadrant, long First, long Count, bool Last);
}
