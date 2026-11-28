using System.Numerics;
using System.Runtime.InteropServices;

namespace HyperPlot.Rendering;

/// <summary>
/// The tiles a backend keeps on the GPU (design §5.2, row G9): Core decides which tile goes in which slot, by a byte budget
/// and least-recently-used order, and writes the tiles a frame sends; the backend sends them and draws, as with the glyph
/// atlas of <see cref="TextEngine"/>. One per backend (<see cref="IGpuBackend.Images"/>). The density tiles of a spatial
/// snapshot (row R3b1) take slots of the <see cref="Format.Float"/> pool beside the raster tiles, and the pages of points
/// (row R3a) share the budget: one least-recently-used order over all three (design §5.5.2 "1 つの LRU", row R3b2).
/// </summary>
/// <remarks>
/// Row G9 makes it for the Direct3D 11 backend only; the members a backend reads are internal, and a public constructor
/// and those members come when another backend does. The budget is fixed for a backend.
/// </remarks>
public sealed class ResidencyCache
{
    /// <summary>The bytes of GPU memory the tiles and the pages of points take at most together: 268,435,456 B (256 MiB).</summary>
    public const long DefaultBudget = 256L << 20;

    /// <summary>
    /// The bytes a frame of the cache sends at most, every plan's that drew in it together (design §5.5.2 "1 tick に送る量"): 4
    /// MB. A headless render sends all it needs, its pages of points ten at a time.
    /// </summary>
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

    /// <summary>The slots the default budget holds over both formats. Tests only.</summary>
    internal const int SlotCapacity = (int)(DefaultBudget / FaceBytes);

    private readonly Pool[] _pools;
    private readonly List<ColorMap> _tables = [];
    private readonly List<(int Row, ColorMap Map)> _tableUploads = [];

    // The frame each table row was last used in: a full table texture takes the row used longest ago for a new map, so
    // that maps made on the fly (a layer's own color, row R3b1) do not fill it for good.
    private readonly long[] _tableUsed = new long[MaxColorTables];

    // The release (Releases) at which each table row last took a map, which a kept list checks (design §5.5.2 "予算").
    private readonly long[] _tableWritten = new long[MaxColorTables];
    private readonly List<Upload> _uploads = [];

    // The plans that draw with the cache (design §5.5.2 "キャッシュを分け合う計画"), in the order they registered.
    private readonly List<SharingPlan> _plans = [];
    private uint[] _staging = new uint[FrameUpload / 4];
    private int _stagingFaces;
    private long _frame;
    private long _lastPlan;

    // The bytes of tiles and pages staged in the current frame, every plan's (design §5.5.2 "1 tick に送る量"): set to 0 when
    // the frame moves on, and not when the backend has sent them.
    private long _staged;

    // The frame in which the cache found that nothing could be let go of (design §5.5.2 "予算が満ちたフレーム").
    private long _fullFrame = -1;

    // The tiles' slots used in the frame before this one, for the budget's sum (design §5.5.2 "予算").
    private int _tilesUsedLastFrame;

    internal ResidencyCache()
        : this(DefaultBudget) { }

    /// <summary>Initializes a new instance of the <see cref="ResidencyCache"/> class with another budget than <see cref="DefaultBudget"/>. Tests only.</summary>
    internal ResidencyCache(long budget)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget);
        Budget = budget;
        _pools = [new(this), new(this)];
        Points = new PointPages(this);
    }

    /// <summary>
    /// Gets the choice of the point layers' drawings (design §5.5.2 "予算", "キャッシュを分け合う計画"): one for the cache,
    /// which every plan that draws with it chooses by in turn.
    /// </summary>
    internal PointBudget PointBudget { get; } = new();

    /// <summary>Gets the plans registered with the cache, in the order they registered (design §5.5.2 "計画の登録と外し").</summary>
    internal ReadOnlySpan<SharingPlan> Plans => CollectionsMarshal.AsSpan(_plans);

    /// <summary>Gets the bytes the tiles and the pages of points take at most together: <see cref="DefaultBudget"/>, or less for a test.</summary>
    internal long Budget { get; }

    /// <summary>Gets the pages of points (row R3a), which share the budget, its least-recently-used order, and a frame's upload with the tiles.</summary>
    internal PointPages Points { get; }

    /// <summary>
    /// Gets the number of the current frame: a tick of the plans that draw with the cache (design §5.5.2 "tick の数え方").
    /// What is asked for in it is not let go of for another in it, whichever plan asked.
    /// </summary>
    internal long Frame => _frame;

    /// <summary>
    /// Gets the number of times the cache forgot every slot and table after the device was lost (<see cref="Reset"/>): a list
    /// kept from before one draws nothing that is there (design §5.5.2 "予算", Issue #636).
    /// </summary>
    internal long Resets { get; private set; }

    /// <summary>
    /// Gets the number of releases so far: each let-go of a tile's slot or a page's slot, and each new map a table row takes,
    /// counts one, and the slot or the row keeps its number. A list kept or recorded when the count was n draws what it
    /// drew while none of its slots and rows has a number above n (design §5.5.2 "予算", Issue #636), a let-go later in
    /// the same frame included.
    /// </summary>
    internal long Releases { get; private set; }

    /// <summary>Gets the bytes on the GPU or staged for it: the tiles' slots and the pages of points.</summary>
    internal long BytesInUse => ((long)SlotsInUse * FaceBytes) + Points.BytesInUse;

    /// <summary>
    /// Gets the bytes of the density and raster tiles' slots the frame before this one used (design §5.5.2 "予算"): the
    /// tiles a frame uses are known only once its layers have recorded, so the budget's sum reads the last frame's.
    /// </summary>
    internal long TileBytesLastFrame => (long)_tilesUsedLastFrame * FaceBytes;

    /// <summary>
    /// Gets a value indicating whether the cache found in this frame that nothing can be let go of for more (design §5.5.2
    /// "予算が満ちたフレーム"): every tile and page was used in this frame, and the budget has no room for a page. Tests and
    /// measurements only.
    /// </summary>
    internal bool FullThisFrame => _fullFrame == _frame;

    /// <summary>The format of a tile.</summary>
    internal enum Format
    {
        /// <summary>Numbers, as differences from an origin in single precision: R32_FLOAT.</summary>
        Float = 0,

        /// <summary>Premultiplied colors: R8G8B8A8_UNORM.</summary>
        Rgba = 1,
    }

    /// <summary>
    /// Gets or sets a value indicating whether a frame sends every tile it asks for, and every page of points the workers have
    /// read, past <see cref="FrameUpload"/>: a headless render.
    /// </summary>
    internal bool Unbounded { get; set; }

    /// <summary>Gets the tiles to send before the frame draws.</summary>
    internal ReadOnlySpan<Upload> Uploads => CollectionsMarshal.AsSpan(_uploads);

    /// <summary>Gets the color tables to send before the frame draws.</summary>
    internal ReadOnlySpan<(int Row, ColorMap Map)> TableUploads => CollectionsMarshal.AsSpan(_tableUploads);

    /// <summary>
    /// Gets the number of bytes staged since the backend last sent: tiles and pages of points. The upload of a frame is bounded
    /// by it and by <see cref="BytesThisTick"/> (<see cref="Admits"/>), so that what waits to be sent stays within
    /// <see cref="FrameUpload"/> when a frame moves on before its uploads were sent.
    /// </summary>
    internal long BytesThisFrame => ((long)_stagingFaces * FaceBytes) + Points.BytesThisFrame;

    /// <summary>
    /// Gets the number of bytes the current frame has staged to send, every plan's that drew in it (design §5.5.2 "1 tick に
    /// 送る量"): what <see cref="FrameUpload"/> bounds. A send (<see cref="Sent"/>) does not take it back; the next frame
    /// starts it at 0.
    /// </summary>
    internal long BytesThisTick => _staged;

    /// <summary>Gets the number of slots in use over both formats. Tests only.</summary>
    internal int SlotsInUse => _pools[0].InUse + _pools[1].InUse;

    /// <summary>Returns the number of pages of a format the backend holds a texture array for.</summary>
    internal int PageCount(Format format) => _pools[(int)format].Pages;

    /// <summary>Returns the staged texels of an upload: a face of 258 × 258.</summary>
    internal ReadOnlySpan<uint> Face(in Upload upload) => _staging.AsSpan(upload.Offset, FaceTexels);

    /// <summary>
    /// Registers a plan that draws with the cache (design §5.5.2 "計画の登録と外し"): a plot's plan alone or a figure's, when
    /// it is made. Its number counts from 1 in the order of the registrations and is never used again. Off the frame path:
    /// it allocates the plan's entry.
    /// </summary>
    /// <remarks>
    /// The plans of a cache draw on one thread, the backend's (design §5.2), and the registrations and the frames read the
    /// list of plans on that thread too; the cache does not check the thread. A plan made or disposed on another thread
    /// while a frame reads the list makes the frame's sum wrong, or its read of the list throw.
    /// </remarks>
    internal SharingPlan Register()
    {
        var plan = new SharingPlan(++_lastPlan);
        _plans.Add(plan);
        return plan;
    }

    /// <summary>Takes a plan off the cache when it is disposed (design §5.5.2 "計画の登録と外し"): no later choice counts it.</summary>
    internal void Unregister(SharingPlan plan) => _ = _plans.Remove(plan);

    /// <summary>
    /// Starts a plan's frame (design §5.5.2 "tick の数え方"): the plan joins the current frame of the cache, or, when it is in
    /// that frame already, the cache moves on to the next frame first (<see cref="BeginFrame()"/>), so that the plans drawn
    /// in turn on the same ticks share one frame a tick. The choice of the point layers begins the plan's (<see
    /// cref="PointBudget"/>). Searches nothing.
    /// </summary>
    internal void BeginFrame(SharingPlan plan)
    {
        // A plan starts in frame 0, so the cache's first frame moves on too, from the frame 0 every slot and read starts in.
        if (plan.Frame == _frame)
        {
            BeginFrame();
        }

        // The plan's raw bytes stay those of its last choice until this frame's (PointBudget.Decide), so that a Render
        // that throws before it chooses leaves the last value, not 0, in the other plans' sums.
        plan.Frame = _frame;
        PointBudget.Begin(plan, _frame);
    }

    /// <summary>
    /// Moves the cache to the next frame: tiles asked for from now on are this frame's, and are not let go of for another.
    /// The reads of pages the last frame did not ask for are withdrawn (design §5.5.2 "要らなくなった読み"), and the frame's
    /// upload starts at 0 (design §5.5.2 "1 tick に送る量"). A plan's frame begins with <see cref="BeginFrame(SharingPlan)"/>;
    /// a test of the cache alone calls this one.
    /// </summary>
    internal void BeginFrame()
    {
        _tilesUsedLastFrame = _pools[0].Used + _pools[1].Used;
        _pools[0].Used = 0;
        _pools[1].Used = 0;
        _staged = 0;
        _frame++;
        Points.BeginFrame();
    }

    /// <summary>
    /// Counts the bytes of a page of points staged in the current frame (<see cref="BytesThisTick"/>). A page staged and let
    /// go of before it was sent stays counted: the frame's upload never grows past <see cref="FrameUpload"/> by it.
    /// </summary>
    internal void Stage(long bytes) => _staged += bytes;

    /// <summary>
    /// Returns whether the frame's upload has room for more bytes (design §5.5.2 "1 tick に送る量"): a headless render's
    /// always does; otherwise both what every plan staged in the frame (<see cref="BytesThisTick"/>) and what waits to be sent
    /// (<see cref="BytesThisFrame"/>, a frame's that moved on before the backend sent it included) stay within
    /// <see cref="FrameUpload"/> with them.
    /// </summary>
    internal bool Admits(long bytes) => Unbounded || Math.Max(_staged, BytesThisFrame) + bytes <= FrameUpload;

    /// <summary>
    /// Returns whether no room can be made in this frame for the given bytes (design §5.5.2 "予算が満ちたフレーム"): the
    /// cache found so earlier in the frame, or the budget has no room for them and every tile and page was used in this
    /// frame, so none can be let go of. Reads counts, and searches nothing.
    /// </summary>
    internal bool IsFull(long bytes)
    {
        if (_fullFrame == _frame)
        {
            return true;
        }

        if (BytesInUse + bytes <= Budget || _pools[0].Used + _pools[1].Used + Points.UsedThisFrame < SlotsInUse + Points.PagesInUse)
        {
            return false;
        }

        _fullFrame = _frame;
        return true;
    }

    /// <summary>
    /// Makes room in the budget for the given bytes (design §5.5.2 "1 つの LRU", "予算が満ちたフレーム"): lets go of the tiles
    /// and the pages used longest ago before this frame, of any kind, until the bytes fit. False, and the frame is known
    /// full, when every one left was used in this frame and the bytes still do not fit.
    /// </summary>
    internal bool MakeRoom(long bytes)
    {
        while (BytesInUse + bytes > Budget)
        {
            if (!EvictOldest())
            {
                _fullFrame = _frame;
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Lets go of the tile or the page used longest ago before this frame, of any kind (design §5.5.2 "1 つの LRU"): a
    /// density or raster tile's slot of either format, or a page of points. False when every one was used in this frame.
    /// </summary>
    private bool EvictOldest()
    {
        var (kind, slot, when) = (-1, -1, _frame);
        for (var format = 0; format < _pools.Length; format++)
        {
            var at = _pools[format].Oldest(when, out var used);
            if (at >= 0)
            {
                (kind, slot, when) = (format, at, used);
            }
        }

        var page = Points.Oldest(when, out _);
        if (page >= 0)
        {
            Points.Evict(page);
            return true;
        }

        if (kind < 0)
        {
            return false;
        }

        _pools[kind].Free(slot, _frame);
        return true;
    }

    /// <summary>
    /// Lets the cache go (design §5.5.2 "ページの読み出し"): its page readers leave their pools, so that no worker reads for
    /// it again and a pool's works do not grow by a cache that is gone. The owner calls it when it is disposed; waits for no
    /// read.
    /// </summary>
    internal void Detach() => Points.Detach();

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
        _tilesUsedLastFrame = 0;
        Resets++;
        Points.Reset();
        Sent();
    }

    /// <summary>
    /// Returns whether a tile's slot still holds what it held when <see cref="Releases"/> was a count (design §5.5.2 "予算",
    /// Issue #636): in use, and not let go of after that release. Searches nothing.
    /// </summary>
    internal bool Holds(Format format, int slot, long since) => _pools[(int)format].Holds(slot, since);

    /// <summary>Counts a release and returns its number (<see cref="Releases"/>).</summary>
    internal long Release() => ++Releases;

    /// <summary>Marks a tile's slot in use as used in this frame, so that it is not let go of for another in it (design §5.5.2 "1 つの LRU").</summary>
    internal void Keep(Format format, int slot) => _pools[(int)format].Use(slot, _frame);

    /// <summary>
    /// Returns whether a table row still holds the map it held when <see cref="Releases"/> was a count: no map has taken it since. A row no map took
    /// (an image of numbers that reads no table) holds nothing to lose.
    /// </summary>
    internal bool HoldsTable(int row, long since) => row >= _tables.Count || _tableWritten[row] <= since;

    /// <summary>Marks a table row that holds a map as used in this frame, so that no new map takes it in it.</summary>
    internal void KeepTable(int row)
    {
        if (row < _tables.Count)
        {
            _tableUsed[row] = _frame;
        }
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
            pool.Use(slot, _frame);
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
    /// least-recently-used order with the raster tiles and the pages of points. A tile used this frame is never let go of for another this frame.
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
            pool.Use(slot, _frame);
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
            pool.Use(slot, _frame);
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
    internal void ForgetDensity(SpatialSnapshot snapshot) => _pools[(int)Format.Float].Forget(snapshot, _frame);

    /// <summary>
    /// Returns the slot of a tile that is there, or −1, sending nothing (row W1b3): a mipmap being updated is written over,
    /// so that only the tiles already sent are drawn (design §5.5.3, Issue #314 Decision 2).
    /// </summary>
    internal int Find(in TileKey key)
    {
        var pool = _pools[(int)key.Format];
        if (pool.Slots.TryGetValue(key, out var slot))
        {
            pool.Use(slot, _frame);
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
            _tableWritten[row] = Release();
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
            _tableWritten[row] = Release();
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
    /// upload, every plan's of the frame (design §5.5.2 "1 tick に送る量"), or the budget has no room for it.
    /// </summary>
    private int Take(Format format, out int offset)
    {
        offset = 0;
        if (!Admits(FaceBytes))
        {
            return -1;
        }

        // Room in the budget, then a slot of the format's pages: a free one, or a new page's within the slots the budget
        // leaves beside the pages of points and the other format's tiles (design §5.5.2 "1 つの LRU").
        var pool = _pools[(int)format];
        int slot;
        while (true)
        {
            if (!MakeRoom(FaceBytes))
            {
                return -1;
            }

            slot = pool.Take((int)((Budget - Points.BytesInUse) / FaceBytes) - _pools[1 - (int)format].InUse);
            if (slot >= 0)
            {
                break;
            }

            // The bytes fit and the format has no slot: its own tile used longest ago before this frame frees one in place;
            // without one, the oldest of any kind may leave room for another page of slots. Neither makes the frame full,
            // since the budget has room for its bytes.
            if (!pool.Evict(_frame) && !EvictOldest())
            {
                return -1;
            }
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
        _staged += FaceBytes;
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

    /// <summary>
    /// A plan that draws with the cache (design §5.5.2 "キャッシュを分け合う計画"): a plot's plan alone or a figure's. The
    /// cache keeps the frame it last joined, and the plan the bytes its last choice drew as raw points, which the choices of
    /// the other plans count while that frame is the current one or the one before.
    /// </summary>
    /// <param name="number">The number of the registration, from 1 (<see cref="Register"/>).</param>
    internal sealed class SharingPlan(long number)
    {
        /// <summary>
        /// Gets the number of the plan's registration, from 1, never used again (design §5.5.2 "同じ値のときの順"). No choice
        /// reads it: the budget's list holds the drawing plan's layers alone, so no two plans' layers are ever ordered or summed
        /// together (Issue #664's decision, detail 3). Tests and diagnostics read it.
        /// </summary>
        public long Number => number;

        /// <summary>Gets or sets the frame of the cache the plan last joined; 0 before it joins one, the frame the cache starts in.</summary>
        public long Frame { get; set; }

        /// <summary>
        /// Gets or sets the bytes the plan's last choice drew as raw points (design §5.5.2 "ほかの計画の最後の値"): its
        /// <see cref="PointDraw.Points"/> layers and its Auto layers on raw points. A frame's choice writes it; a Render that throws
        /// before its choice leaves the last one's.
        /// </summary>
        public long Raw { get; set; }
    }

    /// <summary>The slots of one format: a slot holds a raster tile or, in the <see cref="Format.Float"/> pool, a density tile.</summary>
    private sealed class Pool(ResidencyCache owner)
    {
        private TileKey?[] _keys = [];
        private DensityKey?[] _densityKeys = [];

        public Dictionary<TileKey, int> Slots { get; } = [];

        public Dictionary<DensityKey, int> DensitySlots { get; } = [];

        public long[] LastUsed { get; private set; } = [];

        /// <summary>Gets the release (<see cref="Releases"/>) at which each slot was last let go of; 0 for a slot never let go of.</summary>
        public long[] LastFreed { get; private set; } = [];

        public int Pages { get; private set; }

        public int InUse => Slots.Count + DensitySlots.Count;

        /// <summary>Gets or sets the number of slots in use that the current frame used; the cache sets it to 0 when a frame begins.</summary>
        public int Used { get; set; }

        public void Clear()
        {
            Slots.Clear();
            DensitySlots.Clear();
            Array.Clear(_keys);
            Array.Clear(_densityKeys);
            Pages = 0;
            Used = 0;
        }

        /// <summary>Marks a slot in use as used in a frame, counted once a frame.</summary>
        public void Use(int slot, long frame)
        {
            if (LastUsed[slot] != frame)
            {
                LastUsed[slot] = frame;
                Used++;
            }
        }

        /// <summary>Returns a free slot, or a new page's when the pages stay within a capacity in slots; −1 when neither is.</summary>
        public int Take(int capacity)
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
                var freed = LastFreed;
                Array.Resize(ref freed, Pages * PageSlots);
                LastFreed = freed;
                return count;
            }

            return -1;
        }

        /// <summary>Returns the slot in use used longest ago before a frame, and its frame; −1 when every slot in use was used since.</summary>
        public int Oldest(long before, out long when)
        {
            var oldest = -1;
            when = before;
            for (var slot = 0; slot < Pages * PageSlots; slot++)
            {
                if ((_keys[slot] is not null || _densityKeys[slot] is not null) && LastUsed[slot] < when)
                {
                    (oldest, when) = (slot, LastUsed[slot]);
                }
            }

            return oldest;
        }

        public void Assign(int slot, in TileKey key, long frame)
        {
            _keys[slot] = key;
            Slots[key] = slot;
            LastUsed[slot] = frame;
            Used++;
        }

        public void Assign(int slot, in DensityKey key, long frame)
        {
            _densityKeys[slot] = key;
            DensitySlots[key] = slot;
            LastUsed[slot] = frame;
            Used++;
        }

        /// <summary>Frees the slots of a snapshot's density tiles.</summary>
        public void Forget(SpatialSnapshot snapshot, long frame)
        {
            for (var slot = 0; slot < _densityKeys.Length; slot++)
            {
                if (_densityKeys[slot] is { } key && ReferenceEquals(key.Snapshot, snapshot))
                {
                    Free(slot, frame);
                }
            }
        }

        /// <summary>Frees the slot in use used longest ago before a frame; false when every slot in use was used since.</summary>
        public bool Evict(long frame)
        {
            var oldest = Oldest(frame, out _);
            if (oldest < 0)
            {
                return false;
            }

            Free(oldest, frame);
            return true;
        }

        /// <summary>Returns whether a slot is in use and was not let go of after a release.</summary>
        public bool Holds(int slot, long since) =>
            slot >= 0 && slot < Pages * PageSlots && (_keys[slot] is not null || _densityKeys[slot] is not null) && LastFreed[slot] <= since;

        /// <summary>Frees a slot in use; one the current frame used no longer counts as used.</summary>
        public void Free(int slot, long frame)
        {
            LastFreed[slot] = owner.Release();
            if (LastUsed[slot] == frame)
            {
                Used--;
            }

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
