using System.Runtime.InteropServices;

namespace HyperPlot;

/// <summary>
/// The density tiles of a full <see cref="SpatialSnapshot"/> (design §3.4 "密度タイル"): for each split cell with more
/// than the density split of points and a level of 24 or less, the number of points of each of its 256 × 256 bins,
/// which are the cells 8 levels below it. A tile's bins are 1, 2, 4, or 8 bytes wide, by its largest bin, so no count
/// is saturated.
/// </summary>
/// <remarks>
/// The tiles are blocks of native memory that the snapshot frees with its table and its copy: a tile is read only
/// between <see cref="SpatialIndex.Acquire"/> and <see cref="SpatialSnapshot.Release"/> of its snapshot.
/// </remarks>
internal sealed unsafe class DensityTiles : IDisposable
{
    /// <summary>The bins on one side of a tile.</summary>
    internal const int Side = 256;

    /// <summary>The bins of a tile.</summary>
    internal const int BinCount = Side * Side;

    /// <summary>The levels between a tile's cell and its bins.</summary>
    internal const int BinLevels = 8;

    /// <summary>The deepest level of a cell that has a tile: its bins are the grid cells of level 32.</summary>
    internal const int DeepestTileLevel = 24;

    /// <summary>The bytes of one tile's line in the directory: its code, its level, its width, and where its bins are.</summary>
    internal const int EntryBytes = 24;

    // The directory, in the order of the level and then of the code. The blocks are owned from the constructor to Dispose.
    private readonly Entry[] _entries;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="DensityTiles"/> class, which owns the blocks from here on.</summary>
    /// <param name="entries">The tiles in the order of the level and then of the code.</param>
    internal DensityTiles(Entry[] entries)
    {
        _entries = entries;
        long bytes = (long)entries.Length * EntryBytes;
        foreach (var entry in entries)
        {
            bytes += (long)BinCount * entry.BytesPerBin;
        }

        ByteCount = bytes;
    }

    /// <summary>Gets the number of tiles.</summary>
    public int TileCount => _entries.Length;

    /// <summary>Gets the bytes of the tiles and of their directory.</summary>
    public long ByteCount { get; }

    /// <summary>Returns the bytes of one bin of a tile whose largest bin holds a number of points.</summary>
    /// <param name="largest">The points of the largest bin.</param>
    /// <returns>1 up to 255 points, 2 up to 65,535, 4 up to 4,294,967,295, and 8 above.</returns>
    public static int BytesPerBinFor(ulong largest) =>
        largest <= byte.MaxValue ? sizeof(byte)
        : largest <= ushort.MaxValue ? sizeof(ushort)
        : largest <= uint.MaxValue ? sizeof(uint)
        : sizeof(ulong);

    /// <summary>Finds the tile of a cell. Neither allocates nor waits; its time grows with the logarithm of the number of tiles.</summary>
    /// <param name="code">The cell's number at its level: the upper 2 × <paramref name="level"/> bits of its points' codes.</param>
    /// <param name="level">The cell's level, from 0 to 32.</param>
    /// <param name="tile">The tile, when the cell has one.</param>
    /// <returns>Whether the cell has a tile.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is less than 0 or greater than 32.</exception>
    public bool TryGetTile(ulong code, int level, out DensityTile tile)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 32);
        var low = 0;
        var high = _entries.Length - 1;
        while (low <= high)
        {
            var middle = (int)(((uint)low + (uint)high) >> 1);
            ref readonly var entry = ref _entries[middle];
            var order = entry.Level != level ? entry.Level.CompareTo((byte)level) : entry.Code.CompareTo(code);
            if (order == 0)
            {
                // The block holds BinCount bins of the entry's width until Dispose (see the remarks of the type).
                tile = new DensityTile(new ReadOnlySpan<byte>((void*)entry.Bins, BinCount * entry.BytesPerBin), entry.BytesPerBin);
                return true;
            }

            if (order < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        tile = default;
        return false;
    }

    /// <summary>Frees the tiles. Only the first call does it. Nothing reads a tile any more.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        for (var i = 0; i < _entries.Length; i++)
        {
            NativeMemory.AlignedFree((void*)_entries[i].Bins);
            _entries[i].Bins = 0;
        }
    }

    /// <summary>One tile of the directory.</summary>
    internal struct Entry
    {
        /// <summary>The number of the tile's cell at its level.</summary>
        public ulong Code;

        /// <summary>The block of the tile's bins: <see cref="BinCount"/> bins of <see cref="BytesPerBin"/> bytes, the row of the least y first.</summary>
        public nint Bins;

        /// <summary>The level of the tile's cell.</summary>
        public byte Level;

        /// <summary>The bytes of one bin: 1, 2, 4, or 8.</summary>
        public byte BytesPerBin;
    }
}

/// <summary>One density tile: the number of points of each of the 256 × 256 bins of a cell.</summary>
internal readonly ref struct DensityTile
{
    /// <summary>Initializes a new instance of the <see cref="DensityTile"/> struct.</summary>
    /// <param name="bins">The bins.</param>
    /// <param name="bytesPerBin">The bytes of one bin.</param>
    internal DensityTile(ReadOnlySpan<byte> bins, int bytesPerBin)
    {
        Bins = bins;
        BytesPerBin = bytesPerBin;
    }

    /// <summary>Gets the bytes of one bin: 1, 2, 4, or 8.</summary>
    public int BytesPerBin { get; }

    /// <summary>
    /// Gets the bins as unsigned integers of <see cref="BytesPerBin"/> bytes in the byte order of the machine (little-endian
    /// on every target of the engine): 256 rows of 256 bins, the row of the least y first, so the bin of column x and row
    /// y is at y × 256 + x.
    /// </summary>
    public ReadOnlySpan<byte> Bins { get; }

    /// <summary>Returns the number of points of one bin.</summary>
    /// <param name="x">The bin's column, from 0 to 255.</param>
    /// <param name="y">The bin's row, from 0 to 255.</param>
    /// <returns>The number of points.</returns>
    public ulong this[int x, int y]
    {
        get
        {
            var index = (y * DensityTiles.Side) + x;
            return BytesPerBin switch
            {
                sizeof(byte) => Bins[index],
                sizeof(ushort) => MemoryMarshal.Cast<byte, ushort>(Bins)[index],
                sizeof(uint) => MemoryMarshal.Cast<byte, uint>(Bins)[index],
                _ => MemoryMarshal.Cast<byte, ulong>(Bins)[index],
            };
        }
    }
}
