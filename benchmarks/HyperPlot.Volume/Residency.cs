using System.Numerics;

namespace HyperPlot.Volume;

/// <summary>
/// A view of the volume: the camera and the axis ranges (K0a decision 10: zoom and pan change the ranges, the axis box stays
/// [-1, 1]³ on screen, and data outside the ranges is cut). Ranges are in u, the data's [0, 1]³.
/// </summary>
internal readonly record struct View(Vector3 Eye, Matrix4x4 Transform, Vector3 RangeLow, Vector3 RangeHigh, int Width, int Height)
{
    public const float FieldOfView = MathF.PI / 4;

    /// <summary>Pixels per unit of box space at distance 1: the viewport's height over 2 tan(field of view / 2).</summary>
    public float PixelsPerUnit => Height / (2 * MathF.Tan(FieldOfView / 2));

    /// <summary>
    /// The camera of frame f of the orbit (as in row K0a's prototype): 360° about the box's vertical axis every turn frames,
    /// 30° above the horizon, at the distance where the box's bounding sphere (radius √3) fits the vertical field of view
    /// (decision 16 of Issue #470). The ranges are the data's [0, 1] narrowed by zoom about center.
    /// </summary>
    public static View Orbit(int frame, int turn, int width, int height, float zoom, Vector3 center)
    {
        const float Elevation = MathF.PI / 6;
        var angle = 2 * MathF.PI * (frame % turn) / turn;
        var radius = MathF.Sqrt(3);
        var distance = radius / MathF.Sin(FieldOfView / 2);
        var eye = new Vector3(MathF.Cos(Elevation) * MathF.Cos(angle), MathF.Cos(Elevation) * MathF.Sin(angle), MathF.Sin(Elevation)) * distance;
        var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitZ);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            FieldOfView,
            width / (float)height,
            distance - radius - 0.1f,
            distance + radius + 0.1f
        );
        var half = new Vector3(0.5f / zoom);
        var low = Vector3.Clamp(center - half, Vector3.Zero, Vector3.One - (2 * half));
        return new View(eye, view * projection, low, low + (2 * half), width, height);
    }
}

/// <summary>The counts of a frame's selection and upload.</summary>
internal struct FrameStats
{
    /// <summary>Bricks to draw that hold a visible value.</summary>
    public int Selected;

    /// <summary>Bricks whose level was chosen and whose own range the transfer function leaves empty: not sent, skipped.</summary>
    public int EmptySelected;

    /// <summary>Bricks cut with everything under them, by the view's ranges or by their tree's range.</summary>
    public int Culled;

    /// <summary>Selected bricks not resident before this frame's upload: the demand.</summary>
    public int Missing;

    public int Uploaded;

    /// <summary>A selected brick is drawn from a coarser one after the upload.</summary>
    public bool Provisional;

    /// <summary>The atlas had no slot left that this frame does not use.</summary>
    public bool Starved;

    public int Resident;
}

/// <summary>
/// The CPU's part (design §13.6 decision 2): chooses from the view, for each region, the coarsest level whose voxel projects
/// to 1 pixel or less, skips bricks the transfer function leaves empty, asks for the missing bricks nearest the screen's center
/// first within a byte budget a frame, keeps the atlas's slots by least recent use, and writes the table the shader reads (a
/// missing brick is drawn from its nearest resident ancestor, and the frame is provisional). The selection depends on the view
/// alone; the slots depend on what came before, and the image must not.
/// </summary>
internal sealed class Residency
{
    /// <summary>The slot of an entry that draws nothing; the entry's level says how far the shader may skip.</summary>
    public const uint EmptySlot = 0xFFFFFF;

    private const int LevelShift = 26;
    private const int IdMask = (1 << LevelShift) - 1;

    private readonly BrickStore _store;
    private readonly TransferFunction _transfer;
    private readonly int _root;
    private readonly int[] _slotOf;
    private readonly int[] _brickIn;
    private readonly long[] _lastUsed;
    private readonly int[] _free;
    private readonly int[] _stack;
    private readonly int[] _selected;
    private readonly int[] _missing;
    private readonly float[] _missingKeys;
    private readonly float[] _keyOf;
    private int _freeCount;
    private bool _rootPending = true;

    public Residency(BrickStore store, TransferFunction transfer, int capacity)
    {
        if (capacity < 1 || capacity >= (1 << 24) - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "The atlas holds 1 to 2^24 - 2 slots.");
        }

        _store = store;
        _transfer = transfer;
        Capacity = capacity;
        _root = store.Count - 1;
        _slotOf = new int[store.Count];
        Array.Fill(_slotOf, -1);
        _brickIn = new int[capacity];
        Array.Fill(_brickIn, -1);
        _lastUsed = new long[capacity];
        Array.Fill(_lastUsed, long.MinValue);
        _free = new int[capacity];
        for (var s = 0; s < capacity; s++)
        {
            _free[s] = capacity - 1 - s;
        }

        _freeCount = capacity;
        _stack = new int[store.Count];
        _selected = new int[store.Count];
        _missing = new int[store.Count];
        _missingKeys = new float[store.Count];
        _keyOf = new float[store.Count];
        Table = new uint[store.Grid * store.Grid * store.Grid];
        UploadBricks = new int[store.Count];
        UploadSlots = new int[store.Count];
        LevelCounts = new int[store.Levels];

        // The coarsest brick is always resident: every missing brick has an ancestor to be drawn from.
        _slotOf[_root] = TakeSlot(long.MaxValue);
        _brickIn[_slotOf[_root]] = _root;
    }

    public int Capacity { get; }

    /// <summary>The entries, Grid³ of them, x fastest: (level &lt;&lt; 24) | slot.</summary>
    public uint[] Table { get; }

    /// <summary>This frame's uploads, <see cref="UploadCount"/> of them: a brick and the slot it goes to.</summary>
    public int[] UploadBricks { get; }

    public int[] UploadSlots { get; }

    public int UploadCount { get; private set; }

    /// <summary>The selected bricks of each level in the last frame, empty ones included.</summary>
    public int[] LevelCounts { get; }

    /// <summary>Selects, uploads within budgetBytes (0 for no limit), and writes the table for the view.</summary>
    public FrameStats Select(in View view, long frame, long budgetBytes)
    {
        var stats = default(FrameStats);
        UploadCount = 0;
        if (_rootPending)
        {
            Add(_root, _slotOf[_root]);
            _rootPending = false;
        }

        Array.Fill(Table, EmptySlot);
        Array.Clear(LevelCounts);
        var store = _store;
        var size = (float)store.Size;
        var low = view.RangeLow;
        var extent = view.RangeHigh - view.RangeLow;
        var boxScale = 2 / MathF.Min(extent.X, MathF.Min(extent.Y, extent.Z));
        var pixelsPerUnit = view.PixelsPerUnit;
        var halfWidth = view.Width * 0.5f;
        var halfHeight = view.Height * 0.5f;
        var selected = 0;
        var top = 0;
        _stack[top++] = ((store.Levels - 1) << LevelShift) | _root;
        while (top > 0)
        {
            var item = _stack[--top];
            var id = item & IdMask;
            var level = item >> LevelShift;
            var (bx, by, bz) = store.Position(id, level);
            var span = (float)(store.Brick << level);
            var brickLow = new Vector3(bx, by, bz) * span / size;
            var brickHigh = Vector3.Min(brickLow + new Vector3(span / size), Vector3.One);
            var clippedLow = Vector3.Max(brickLow, low);
            var clippedHigh = Vector3.Min(brickHigh, view.RangeHigh);
            if (clippedLow.X >= clippedHigh.X || clippedLow.Y >= clippedHigh.Y || clippedLow.Z >= clippedHigh.Z)
            {
                stats.Culled++;
                continue;
            }

            // Empty with everything under it: the shader skips the whole brick in one step.
            if (_transfer.IsEmpty(store.TreeMin[id], store.TreeMax[id]))
            {
                stats.Culled++;
                Write(id, level, (uint)(level << 24) | EmptySlot);
                continue;
            }

            // The box-space box of the brick's part inside the ranges, and its voxel's projected size at its nearest point.
            var boxLow = (((clippedLow - low) / extent) * 2) - Vector3.One;
            var boxHigh = (((clippedHigh - low) / extent) * 2) - Vector3.One;
            var nearest = Vector3.Clamp(view.Eye, boxLow, boxHigh);
            var distance = MathF.Max(Vector3.Distance(view.Eye, nearest), 1e-6f);
            var voxel = (1 << level) / size * boxScale;
            if (level == 0 || voxel * pixelsPerUnit / distance <= 1)
            {
                LevelCounts[level]++;
                if (_transfer.IsEmpty(store.Min[id], store.Max[id]))
                {
                    stats.EmptySelected++;
                    Write(id, level, (uint)(level << 24) | EmptySlot);
                    continue;
                }

                var center = Vector4.Transform(new Vector4((boxLow + boxHigh) * 0.5f, 1), view.Transform);
                var x = center.X / center.W * halfWidth;
                var y = center.Y / center.W * halfHeight;
                _keyOf[id] = (x * x) + (y * y);
                _selected[selected++] = id;
                continue;
            }

            var finer = store.LevelBricks[level - 1];
            for (var cz = 2 * bz; cz < Math.Min((2 * bz) + 2, finer); cz++)
            {
                for (var cy = 2 * by; cy < Math.Min((2 * by) + 2, finer); cy++)
                {
                    for (var cx = 2 * bx; cx < Math.Min((2 * bx) + 2, finer); cx++)
                    {
                        _stack[top++] = ((level - 1) << LevelShift) | store.Id(level - 1, cx, cy, cz);
                    }
                }
            }
        }

        // The resident ones are used this frame; the missing ones are asked for, nearest the screen's center first.
        var missing = 0;
        for (var i = 0; i < selected; i++)
        {
            var id = _selected[i];
            if (_slotOf[id] >= 0)
            {
                _lastUsed[_slotOf[id]] = frame;
            }
            else
            {
                _missingKeys[missing] = _keyOf[id];
                _missing[missing++] = id;
            }
        }

        Array.Sort(_missingKeys, _missing, 0, missing);
        stats.Selected = selected;
        stats.Missing = missing;
        var uploaded = 0L;
        for (var i = 0; i < missing; i++)
        {
            if (budgetBytes > 0 && uploaded + store.BrickBytes > budgetBytes)
            {
                break;
            }

            var slot = TakeSlot(frame);
            if (slot < 0)
            {
                stats.Starved = true;
                break;
            }

            var id = _missing[i];
            _slotOf[id] = slot;
            _brickIn[slot] = id;
            _lastUsed[slot] = frame;
            Add(id, slot);
            uploaded += store.BrickBytes;
            stats.Uploaded++;
        }

        // The table: each selected brick's cells name its slot, or its nearest resident ancestor's.
        for (var i = 0; i < selected; i++)
        {
            var id = _selected[i];
            var level = store.LevelOf(id);
            var drawn = id;
            var drawnLevel = level;
            while (_slotOf[drawn] < 0)
            {
                drawn = store.Parent(drawn);
                drawnLevel++;
                stats.Provisional = true;
            }

            _lastUsed[_slotOf[drawn]] = Math.Max(_lastUsed[_slotOf[drawn]], frame);
            Write(id, level, (uint)(drawnLevel << 24) | (uint)_slotOf[drawn]);
        }

        stats.Resident = Capacity - _freeCount;
        return stats;
    }

    // Writes entry over the cells of level 0 that brick id of level covers.
    private void Write(int id, int level, uint entry)
    {
        var (bx, by, bz) = _store.Position(id, level);
        var grid = _store.Grid;
        var span = 1 << level;
        var (x0, y0, z0) = (bx * span, by * span, bz * span);
        var (x1, y1, z1) = (Math.Min(x0 + span, grid), Math.Min(y0 + span, grid), Math.Min(z0 + span, grid));
        for (var z = z0; z < z1; z++)
        {
            for (var y = y0; y < y1; y++)
            {
                Array.Fill(Table, entry, (((z * grid) + y) * grid) + x0, x1 - x0);
            }
        }
    }

    private void Add(int id, int slot)
    {
        UploadBricks[UploadCount] = id;
        UploadSlots[UploadCount] = slot;
        UploadCount++;
    }

    // A free slot, or the least recently used one that this frame does not use (the lowest slot among equals), or -1.
    private int TakeSlot(long frame)
    {
        if (_freeCount > 0)
        {
            return _free[--_freeCount];
        }

        var best = -1;
        for (var s = 0; s < Capacity; s++)
        {
            if (_brickIn[s] != _root && _lastUsed[s] < frame && (best < 0 || _lastUsed[s] < _lastUsed[best]))
            {
                best = s;
            }
        }

        if (best >= 0)
        {
            _slotOf[_brickIn[best]] = -1;
            _brickIn[best] = -1;
        }

        return best;
    }
}
