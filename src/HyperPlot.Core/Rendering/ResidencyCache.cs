using System.Runtime.InteropServices;

namespace HyperPlot.Rendering;

/// <summary>
/// The tiles a backend keeps on the GPU (design §5.2, row G9): Core decides which tile goes in which slot, by a byte budget
/// and least-recently-used order, and writes the tiles a frame sends; the backend sends them and draws, as with the glyph
/// atlas of <see cref="TextEngine"/>. One per backend (<see cref="IGpuBackend.Images"/>).
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

    /// <summary>The color tables a backend's table texture holds, a row of 256 colors each.</summary>
    internal const int MaxColorTables = 64;

    /// <summary>The slots the budget holds over both formats.</summary>
    internal const int SlotCapacity = (int)(DefaultBudget / FaceBytes);

    private readonly Pool[] _pools = [new(), new()];
    private readonly List<ColorMap> _tables = [];
    private readonly List<(int Row, ColorMap Map)> _tableUploads = [];
    private readonly List<Upload> _uploads = [];
    private uint[] _staging = new uint[FrameUpload / 4];
    private int _stagingFaces;
    private long _frame;

    internal ResidencyCache() { }

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

    /// <summary>Gets the number of bytes the current frame sends. Tests only.</summary>
    internal long BytesThisFrame => (long)_stagingFaces * FaceBytes;

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
    }

    /// <summary>Forgets every slot and table after the device was lost: they are sent again from Core (invariant 10).</summary>
    internal void Reset()
    {
        foreach (var pool in _pools)
        {
            pool.Clear();
        }

        _tables.Clear();
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

        if (!Unbounded && (long)(_stagingFaces + 1) * FaceBytes > FrameUpload)
        {
            return -1;
        }

        slot = pool.Take(_frame, SlotCapacity - _pools[1 - (int)key.Format].InUse);
        if (slot < 0)
        {
            return -1;
        }

        // The tile's texels go to the staging, which the backend sends before it draws.
        var offset = _stagingFaces * FaceTexels;
        if (offset + FaceTexels > _staging.Length)
        {
            Array.Resize(ref _staging, _staging.Length * 2);
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

        _stagingFaces++;
        _uploads.Add(new Upload(key.Format, slot, offset));
        pool.Assign(slot, key, _frame);
        return slot;
    }

    /// <summary>Returns the row of a color map's table, staging it the first time; −1 when the table texture is full.</summary>
    internal int Table(ColorMap map)
    {
        var row = _tables.IndexOf(map);
        if (row >= 0 || _tables.Count == MaxColorTables)
        {
            return row;
        }

        _tables.Add(map);
        _tableUploads.Add((_tables.Count - 1, map));
        return _tables.Count - 1;
    }

    /// <summary>What a slot holds (design §5.2): the mipmap, its level, the tile, the stamp of the tile's rows, and the origin.</summary>
    internal readonly record struct TileKey(RasterPyramid Pyramid, Format Format, int Level, int Column, int Row, long Stamp, double Origin);

    /// <summary>A tile to send: its format, its slot, and its texels' offset in the staging.</summary>
    internal readonly record struct Upload(Format Format, int Slot, int Offset);

    /// <summary>The slots of one format.</summary>
    private sealed class Pool
    {
        private TileKey?[] _keys = [];

        public Dictionary<TileKey, int> Slots { get; } = [];

        public long[] LastUsed { get; private set; } = [];

        public int Pages { get; private set; }

        public int InUse => Slots.Count;

        public void Clear()
        {
            Slots.Clear();
            Array.Clear(_keys);
            Pages = 0;
        }

        /// <summary>Returns a free slot, a new page's, or the one used longest ago before this frame; −1 when none is.</summary>
        public int Take(long frame, int capacity)
        {
            var count = Pages * PageSlots;
            for (var slot = 0; slot < count; slot++)
            {
                if (_keys[slot] is null)
                {
                    return slot;
                }
            }

            if (count + PageSlots <= capacity)
            {
                Pages++;
                Array.Resize(ref _keys, Pages * PageSlots);
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
                _ = Slots.Remove(_keys[oldest]!.Value);
                _keys[oldest] = null;
            }

            return oldest;
        }

        public void Assign(int slot, in TileKey key, long frame)
        {
            _keys[slot] = key;
            Slots[key] = slot;
            LastUsed[slot] = frame;
        }
    }
}
