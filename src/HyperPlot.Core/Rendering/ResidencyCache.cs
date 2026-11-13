using System.Numerics;
using System.Runtime.InteropServices;

namespace HyperPlot.Rendering;

/// <summary>
/// The tiles a backend keeps on the GPU (design §5.2, row G9): Core decides which tile goes in which slot, by a byte budget
/// and least-recently-used order, and writes the tiles a frame sends; the backend sends them and draws, as with the glyph
/// atlas of <see cref="TextEngine"/>. One per backend (<see cref="IGpuBackend.Images"/>). The density tiles of a spatial
/// snapshot (row R3b1) take slots of the <see cref="Format.Float"/> pool beside the raster tiles.
/// </summary>
/// <remarks>
/// Row G9 makes it for the Direct3D 11 backend only; the members a backend reads are internal, and a public constructor
/// and those members come when another backend does. The budget is fixed.
/// </remarks>
public sealed class ResidencyCache
{
    /// <summary>The bytes of GPU memory the tiles take at most: 256 MB.</summary>
    public const long DefaultBudget = 256L << 20;

    /// <summary>The bytes a frame sends at most: 4 MB. A headless render sends all it needs.</summary>
    public const int FrameUpload = 4 << 20;

    /// <summary>The side of a slot's face: a tile of 256 texels and a border of a texel copied from its neighbors.</summary>
    internal const int Side = RasterPyramid.TileSide + 2;

    /// <summary>The texels of a face.</summary>
    internal const int FaceTexels = Side * Side;

    /// <summary>The bytes of a face: 4 a texel, for both formats.</summary>
    internal const int FaceBytes = FaceTexels * 4;

    /// <summary>The slots of a page, a texture array the backend makes at once: about 17 MB.</summary>
    internal const int PageSlots = 64;

    /// <summary>The level of a <see cref="DensityKey"/> that stands for a snapshot's coarse density, not for one of its tiles.</summary>
    internal const int CoarseDensityLevel = -1;

    /// <summary>The color tables a backend's table texture holds, a row of 256 colors each.</summary>
    internal const int MaxColorTables = 64;

    /// <summary>The slots the budget holds over both formats.</summary>
    internal const int SlotCapacity = (int)(DefaultBudget / FaceBytes);

    private readonly Pool[] _pools = [new(), new()];
    private readonly List<ColorMap> _tables = [];
    private readonly List<(int Row, ColorMap Map)> _tableUploads = [];

    // The frame each table row was last used in: a full table texture takes the row used longest ago for a new map, so
    // that maps made on the fly (a layer's own color, row R3b1) do not fill it for good.
    private readonly long[] _tableUsed = new long[MaxColorTables];
    private readonly List<Upload> _uploads = [];
    private uint[] _staging = new uint[FrameUpload / 4];
    private int _stagingFaces;
    private long _frame;

    internal ResidencyCache() => Points = new PointPages(this);

    /// <summary>Gets the pages of points (row R3a), which share the budget and a frame's upload with the tiles.</summary>
    internal PointPages Points { get; }

    /// <summary>Gets the number of the current frame: what is asked for in it is not let go of for another in it.</summary>
    internal long Frame => _frame;

    /// <summary>Gets the bytes on the GPU or staged for it: the tiles' slots and the pages of points.</summary>
    internal long BytesInUse => ((long)SlotsInUse * FaceBytes) + Points.BytesInUse;

    /// <summary>The format of a tile.</summary>
    internal enum Format
    {
        /// <summary>Numbers, as differences from an origin in single precision: R32_FLOAT.</summary>
        Float = 0,

        /// <summary>Premultiplied colors: R8G8B8A8_UNORM.</summary>
        Rgba = 1,
    }

    /// <summary>Gets or sets a value indicating whether a frame sends every tile it asks for, past <see cref="FrameUpload"/>: a headless render.</summary>
    internal bool Unbounded { get; set; }

    /// <summary>Gets the tiles to send before the frame draws.</summary>
    internal ReadOnlySpan<Upload> Uploads => CollectionsMarshal.AsSpan(_uploads);

    /// <summary>Gets the color tables to send before the frame draws.</summary>
    internal ReadOnlySpan<(int Row, ColorMap Map)> TableUploads => CollectionsMarshal.AsSpan(_tableUploads);

    /// <summary>Gets the number of bytes the current frame sends: its tiles and its pages of points.</summary>
    internal long BytesThisFrame => ((long)_stagingFaces * FaceBytes) + Points.BytesThisFrame;

    /// <summary>Gets the number of slots in use over both formats. Tests only.</summary>
    internal int SlotsInUse => _pools[0].InUse + _pools[1].InUse;

    /// <summary>Returns the number of pages of a format the backend holds a texture array for.</summary>
    internal int PageCount(Format format) => _pools[(int)format].Pages;

    /// <summary>Returns the staged texels of an upload: a face of 258 × 258.</summary>
    internal ReadOnlySpan<uint> Face(in Upload upload) => _staging.AsSpan(upload.Offset, FaceTexels);

    /// <summary>Starts a frame: tiles asked for from now on are this frame's, and are not let go of for another.</summary>
    internal void BeginFrame() => _frame++;

    /// <summary>Says that the backend sent the uploads: the staging is free again.</summary>
    internal void Sent()
    {
        _uploads.Clear();
        _tableUploads.Clear();
        _stagingFaces = 0;
        Points.Sent();
    }

    /// <summary>Forgets every slot and table after the device was lost: they are sent again from Core (invariant 10).</summary>
    internal void Reset()
    {
        foreach (var pool in _pools)
        {
            pool.Clear();
        }

        _tables.Clear();
        Points.Reset();
        Sent();
    }

    /// <summary>
    /// Returns the slot of a tile, sending it when it is not there and the frame's budget allows: the slot, or −1 when it
    /// is not there and cannot be sent this frame. A tile used this frame is never let go of for another this frame.
    /// </summary>
    internal int Request(in TileKey key, RasterPyramid pyramid)
    {
        var pool = _pools[(int)key.Format];
        if (pool.Slots.TryGetValue(key, out var slot))
        {
            pool.LastUsed[slot] = _frame;
            return slot;
        }

        slot = Take(key.Format, out var offset);
        if (slot < 0)
        {
            return -1;
        }

        var face = _staging.AsSpan(offset, FaceTexels);
        if (key.Format == Format.Float)
        {
            pyramid.CopyTile(key.Level, key.Column, key.Row, key.Origin, MemoryMarshal.Cast<uint, float>(face));
        }
        else
        {
            pyramid.CopyTile(key.Level, key.Column, key.Row, face);
        }

        Staged(key.Format, slot, offset);
        pool.Assign(slot, key, _frame);
        return slot;
    }

    /// <summary>
    /// Returns the slot of a density tile of a full snapshot (design §5.1 "密度タイル", row R3b1), sending it when it is not
    /// there and the frame's budget allows: the slot, or −1 when it is not there and cannot be sent this frame. The tile
    /// takes a slot of the <see cref="Format.Float"/> pool and shares the budget, the frame's upload, and the
    /// least-recently-used order with the raster tiles. A tile used this frame is never let go of for another this frame.
    /// </summary>
    /// <remarks>
    /// The face holds the count of bin (x, y) as a <see cref="float"/> at face texel (x + 1, y + 1), the row of the least y
    /// first, and 0 in its border. A count above 2^24 is rounded to the nearest <see cref="float"/>.
    /// </remarks>
    /// <param name="snapshot">The full snapshot, which the caller borrows. A snapshot never changes, so it stands for the index and its version.</param>
    /// <param name="level">The level of the tile's cell, from 0 to <see cref="DensityTiles.DeepestTileLevel"/>.</param>
    /// <param name="code">The number of the tile's cell at its level.</param>
    /// <param name="tile">The tile that <see cref="DensityTiles.TryGetTile"/> of the snapshot's tiles found for the cell.</param>
    /// <returns>The slot, or −1.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is less than 0 or greater than 24.</exception>
    internal int RequestDensity(SpatialSnapshot snapshot, int level, ulong code, DensityTile tile)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, DensityTiles.DeepestTileLevel);
        var key = new DensityKey(snapshot, level, code);
        var pool = _pools[(int)Format.Float];
        if (pool.DensitySlots.TryGetValue(key, out var slot))
        {
            pool.LastUsed[slot] = _frame;
            return slot;
        }

        slot = Take(Format.Float, out var offset);
        if (slot < 0)
        {
            return -1;
        }

        var face = MemoryMarshal.Cast<uint, float>(_staging.AsSpan(offset, FaceTexels));
        switch (tile.BytesPerBin)
        {
            case sizeof(byte):
                FillDensity(tile.Bins, face);
                break;
            case sizeof(ushort):
                FillDensity(MemoryMarshal.Cast<byte, ushort>(tile.Bins), face);
                break;
            case sizeof(uint):
                FillDensity(MemoryMarshal.Cast<byte, uint>(tile.Bins), face);
                break;
            default:
                FillDensity(MemoryMarshal.Cast<byte, ulong>(tile.Bins), face);
                break;
        }

        Staged(Format.Float, slot, offset);
        pool.Assign(slot, key, _frame);
        return slot;
    }

    /// <summary>
    /// Returns the slot of a snapshot's coarse density (design §5.1 "密度タイル", row R3b1): the counts of its 256 × 256 cells
    /// of level 8 as one face, laid out as a density tile's (<see cref="RequestDensity"/>), sending it when it is not there
    /// and the frame's budget allows. The slot, or −1 when it is not there and cannot be sent this frame.
    /// </summary>
    /// <param name="snapshot">The snapshot, coarse or full, which the caller borrows.</param>
    /// <returns>The slot, or −1.</returns>
    /// <exception cref="ArgumentException">The snapshot holds nothing, so it has no coarse density.</exception>
    internal int RequestCoarseDensity(SpatialSnapshot snapshot)
    {
        var key = new DensityKey(snapshot, CoarseDensityLevel, 0);
        var pool = _pools[(int)Format.Float];
        if (pool.DensitySlots.TryGetValue(key, out var slot))
        {
            pool.LastUsed[slot] = _frame;
            return slot;
        }

        var counts = snapshot.CoarseDensity;
        if (counts.Length != DensityTiles.BinCount)
        {
            ThrowHelper.ThrowNoCoarseDensity(nameof(snapshot));
        }

        slot = Take(Format.Float, out var offset);
        if (slot < 0)
        {
            return -1;
        }

        FillDensity(counts, MemoryMarshal.Cast<uint, float>(_staging.AsSpan(offset, FaceTexels)));
        Staged(Format.Float, slot, offset);
        pool.Assign(slot, key, _frame);
        return slot;
    }

    /// <summary>
    /// Lets go of the density tiles and the coarse density of a snapshot the caller gives back (row R3b1): they are no longer
    /// read. A face of the snapshot staged this frame is still sent, into a slot that is free or that a later upload of the
    /// same frame writes over.
    /// </summary>
    internal void ForgetDensity(SpatialSnapshot snapshot) => _pools[(int)Format.Float].Forget(snapshot);

    /// <summary>
    /// Returns the slot of a tile that is there, or −1, sending nothing (row W1b3): a mipmap being updated is written over,
    /// so that only the tiles already sent are drawn (design §5.5.3, Issue #314 Decision 2).
    /// </summary>
    internal int Find(in TileKey key)
    {
        var pool = _pools[(int)key.Format];
        if (pool.Slots.TryGetValue(key, out var slot))
        {
            pool.LastUsed[slot] = _frame;
            return slot;
        }

        return -1;
    }

    /// <summary>
    /// Returns the row of a color map's table, staging it the first time. When the table texture is full, the row used
    /// longest ago, and not in this frame, takes the map; −1 when every row is used in this frame.
    /// </summary>
    internal int Table(ColorMap map)
    {
        var row = _tables.IndexOf(map);
        if (row < 0 && _tables.Count < MaxColorTables)
        {
            _tables.Add(map);
            row = _tables.Count - 1;
            _tableUploads.Add((row, map));
        }
        else if (row < 0)
        {
            // The least recently used row; a row used in this frame is drawn with in this frame and is not written over.
            for (var i = 0; i < _tables.Count; i++)
            {
                if (_tableUsed[i] < _frame && (row < 0 || _tableUsed[i] < _tableUsed[row]))
                {
                    row = i;
                }
            }

            if (row < 0)
            {
                return -1;
            }

            _tables[row] = map;
            _tableUploads.Add((row, map));
        }

        _tableUsed[row] = _frame;
        return row;
    }

    /// <summary>Writes the counts of 256 × 256 bins, the row of the least y first, into a face, and 0 into its border.</summary>
    private static void FillDensity<T>(ReadOnlySpan<T> bins, Span<float> face)
        where T : unmanaged, INumberBase<T>
    {
        face[..Side].Clear();
        face[^Side..].Clear();
        for (var y = 0; y < DensityTiles.Side; y++)
        {
            var line = face.Slice((y + 1) * Side, Side);
            var row = bins.Slice(y * DensityTiles.Side, DensityTiles.Side);
            line[0] = 0;
            line[Side - 1] = 0;
            for (var x = 0; x < row.Length; x++)
            {
                line[x + 1] = float.CreateTruncating(row[x]);
            }
        }
    }

    /// <summary>
    /// Returns a slot of a format for a face to send, and where the face's texels go in the staging; −1 when the frame's
    /// upload or the budget has no room for it.
    /// </summary>
    private int Take(Format format, out int offset)
    {
        offset = 0;
        if (!Unbounded && BytesThisFrame + FaceBytes > FrameUpload)
        {
            return -1;
        }

        // The slots the budget leaves beside the pages of points and the other format's tiles.
        var slot = _pools[(int)format].Take(_frame, (int)((DefaultBudget - Points.BytesInUse) / FaceBytes) - _pools[1 - (int)format].InUse);
        if (slot < 0)
        {
            return -1;
        }

        // The face's texels go to the staging, which the backend sends before it draws.
        offset = _stagingFaces * FaceTexels;
        if (offset + FaceTexels > _staging.Length)
        {
            Array.Resize(ref _staging, _staging.Length * 2);
        }

        return slot;
    }

    /// <summary>Counts a face written into the staging as this frame's, and lists it to send.</summary>
    private void Staged(Format format, int slot, int offset)
    {
        _stagingFaces++;
        _uploads.Add(new Upload(format, slot, offset));
    }

    /// <summary>What a slot holds (design §5.2): the mipmap, its level, the tile, the stamp of the tile's rows, and the origin.</summary>
    internal readonly record struct TileKey(RasterPyramid Pyramid, Format Format, int Level, int Column, int Row, long Stamp, double Origin);

    /// <summary>
    /// What a slot of a density tile holds (design §5.1 "密度タイル", row R3b1): the snapshot, which stands for the index and
    /// its version, and the level and the code of the tile's cell; the level <see cref="CoarseDensityLevel"/> is the
    /// snapshot's coarse density. A key of its own type, so that a density tile is never taken for a raster tile.
    /// </summary>
    internal readonly record struct DensityKey(SpatialSnapshot Snapshot, int Level, ulong Code);

    /// <summary>A tile to send: its format, its slot, and its texels' offset in the staging.</summary>
    internal readonly record struct Upload(Format Format, int Slot, int Offset);

    /// <summary>The slots of one format: a slot holds a raster tile or, in the <see cref="Format.Float"/> pool, a density tile.</summary>
    private sealed class Pool
    {
        private TileKey?[] _keys = [];
        private DensityKey?[] _densityKeys = [];

        public Dictionary<TileKey, int> Slots { get; } = [];

        public Dictionary<DensityKey, int> DensitySlots { get; } = [];

        public long[] LastUsed { get; private set; } = [];

        public int Pages { get; private set; }

        public int InUse => Slots.Count + DensitySlots.Count;

        public void Clear()
        {
            Slots.Clear();
            DensitySlots.Clear();
            Array.Clear(_keys);
            Array.Clear(_densityKeys);
            Pages = 0;
        }

        /// <summary>Returns a free slot, a new page's, or the one used longest ago before this frame; −1 when none is.</summary>
        public int Take(long frame, int capacity)
        {
            var count = Pages * PageSlots;
            for (var slot = 0; slot < count; slot++)
            {
                if (_keys[slot] is null && _densityKeys[slot] is null)
                {
                    return slot;
                }
            }

            if (count + PageSlots <= capacity)
            {
                Pages++;
                Array.Resize(ref _keys, Pages * PageSlots);
                Array.Resize(ref _densityKeys, Pages * PageSlots);
                var used = LastUsed;
                Array.Resize(ref used, Pages * PageSlots);
                LastUsed = used;
                return count;
            }

            var (oldest, when) = (-1, frame);
            for (var slot = 0; slot < count; slot++)
            {
                if (LastUsed[slot] < when)
                {
                    (oldest, when) = (slot, LastUsed[slot]);
                }
            }

            if (oldest >= 0)
            {
                Free(oldest);
            }

            return oldest;
        }

        public void Assign(int slot, in TileKey key, long frame)
        {
            _keys[slot] = key;
            Slots[key] = slot;
            LastUsed[slot] = frame;
        }

        public void Assign(int slot, in DensityKey key, long frame)
        {
            _densityKeys[slot] = key;
            DensitySlots[key] = slot;
            LastUsed[slot] = frame;
        }

        /// <summary>Frees the slots of a snapshot's density tiles.</summary>
        public void Forget(SpatialSnapshot snapshot)
        {
            for (var slot = 0; slot < _densityKeys.Length; slot++)
            {
                if (_densityKeys[slot] is { } key && ReferenceEquals(key.Snapshot, snapshot))
                {
                    Free(slot);
                }
            }
        }

        private void Free(int slot)
        {
            if (_keys[slot] is { } tile)
            {
                _ = Slots.Remove(tile);
                _keys[slot] = null;
            }
            else if (_densityKeys[slot] is { } density)
            {
                _ = DensitySlots.Remove(density);
                _densityKeys[slot] = null;
            }
        }
    }
}
