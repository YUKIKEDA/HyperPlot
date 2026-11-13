using System.Numerics;

namespace HyperPlot.SurfaceLod;

/// <summary>
/// SPIKE (row K0c, Issue #489): chooses the tiles of a frame. A tile's screen error is Ulrich's ρ = K·δ / D (Issue #470's
/// sources, design §13.5): δ is the tile's error in the box's units (its z error times the box's height over z's range),
/// D the distance from the eye to the tile's bounding box, K = H / (2·tan(FOVy / 2)) (the same as W / (2·tan(FOVx / 2))
/// for square pixels). From the 5 × 5 roots, the tile with the largest ρ above the bound is split first, until none is
/// above it or the tile budget is reached; a tile wholly outside the frustum is not split and not drawn. Then the
/// quadtree is restricted (Issue #482, decision 4): a drawn tile with a drawn neighbor two or more levels finer is split,
/// until none is. The edges of each drawn tile whose neighbor is one level coarser give its shape (Stitch). The frame
/// path allocates nothing: every array is made with the selector.
/// </summary>
internal sealed class Selector
{
    private const byte Drawn = 1;
    private const byte Culled = 2;
    private const byte Split = 3;
    private const int CulledBit = 0x100;
    private const int LevelBits = 0xFF;

    private readonly SurfaceData _data;
    private readonly byte[] _state;
    private readonly int[] _finest;
    private readonly int _side;
    private readonly int[] _stack;
    private readonly PriorityQueue<int, float> _heap;
    private readonly Vector4[] _planes = new Vector4[6];
    private readonly float _zScale;
    private readonly float _zOffset;
    private Vector3 _eye;
    private float _factor;

    public Selector(SurfaceData data)
    {
        _data = data;
        _state = new byte[data.NodeCount];
        _side = data.LevelSide[0];
        _finest = new int[_side * _side];
        _stack = new int[data.NodeCount];
        _heap = new PriorityQueue<int, float>(data.NodeCount);
        Tiles = new int[data.NodeCount];
        Masks = new byte[data.NodeCount];
        (_zScale, _zOffset) = ZToBox(data);
    }

    /// <summary>The drawn tiles of the last frame, Count of them.</summary>
    public int[] Tiles { get; }

    /// <summary>The edge shape of each drawn tile (Stitch's bits), or 0 for each when stitching is off.</summary>
    public byte[] Masks { get; }

    public int Count { get; private set; }

    /// <summary>The drawn tiles before the restriction.</summary>
    public int BeforeBalance { get; private set; }

    /// <summary>The tiles the restriction split.</summary>
    public int BalanceSplits { get; private set; }

    /// <summary>Whether a tile above the bound was left because of the tile budget.</summary>
    public bool BudgetReached { get; private set; }

    /// <summary>The box's z per unit of z, and the box's z of z = 0: z's range is mapped to [-1, 1].</summary>
    public static (float Scale, float Offset) ZToBox(SurfaceData data)
    {
        var scale = 2 / (data.ZMax - data.ZMin);
        return (scale, -1 - (data.ZMin * scale));
    }

    /// <summary>
    /// Chooses the frame's tiles for an eye and a transform (box to clip, row vectors). factor is K·(box z per z unit)
    /// over the bound in pixels, so that a tile is split when its error times factor is above its distance.
    /// </summary>
    public void Select(Vector3 eye, in Matrix4x4 transform, float factor, int budget, bool stitch)
    {
        _eye = eye;
        _factor = factor;
        Planes(transform);
        Array.Clear(_state);
        _heap.Clear();
        BudgetReached = false;
        BalanceSplits = 0;
        var data = _data;
        var top = data.TopLevel;
        var visible = 0;
        for (var y = 0; y < SurfaceData.RootsPerAxis; y++)
        {
            for (var x = 0; x < SurfaceData.RootsPerAxis; x++)
            {
                visible += Consider(data.Node(top, x, y), queue: true);
            }
        }

        while (_heap.TryDequeue(out var id, out _))
        {
            // Splitting a drawn tile adds up to 3; a child outside the frustum is not drawn.
            if (visible + 3 > budget)
            {
                BudgetReached = true;
                break;
            }

            visible += SplitNode(id, queue: true);
        }

        // Every finest cell starts culled; the drawn tiles are written over it.
        BeforeBalance = visible;
        _finest.AsSpan().Fill(CulledBit);
        var count = Gather();
        for (var k = 0; k < count; k++)
        {
            Fill(Tiles[k], _data.NodeLevel[Tiles[k]]);
        }

        Balance();
        Count = Gather();
        for (var k = 0; k < Count; k++)
        {
            Masks[k] = stitch ? Shape(Tiles[k]) : (byte)0;
        }
    }

    /// <summary>
    /// The drawn tiles that touch a drawn tile more than one level apart; 0 when the restriction holds. Not on the timed
    /// path.
    /// </summary>
    public int Violations()
    {
        var violations = 0;
        for (var k = 0; k < Count; k++)
        {
            var id = Tiles[k];
            var level = _data.NodeLevel[id];
            var size = 1 << level;
            var (fx0, fy0) = (_data.NodeX[id] << level, _data.NodeY[id] << level);
            var bad = false;
            for (var t = 0; t < size && !bad; t++)
            {
                bad =
                    Apart(fx0 + t, fy0 - 1, level)
                    || Apart(fx0 + t, fy0 + size, level)
                    || Apart(fx0 - 1, fy0 + t, level)
                    || Apart(fx0 + size, fy0 + t, level);
            }

            violations += bad ? 1 : 0;
        }

        return violations;
    }

    private bool Apart(int fx, int fy, int level)
    {
        var other = Level(fx, fy);
        return other >= 0 && Math.Abs(other - level) > 1;
    }

    // The level of the drawn tile over a finest cell, or -1 outside the grid or under a culled tile.
    private int Level(int fx, int fy)
    {
        if (fx < 0 || fy < 0 || fx >= _side || fy >= _side)
        {
            return -1;
        }

        var value = _finest[(fy * _side) + fx];
        return (value & CulledBit) != 0 ? -1 : value & LevelBits;
    }

    // Marks a tile drawn or culled; queues a drawn tile above the bound. Returns the drawn tiles it adds (0 or 1).
    private int Consider(int id, bool queue)
    {
        var data = _data;
        var level = data.NodeLevel[id];
        var span = data.Tile << level;
        var (x0, y0) = (data.NodeX[id] * span, data.NodeY[id] * span);
        var min = new Vector3(data.X[x0], data.Y[y0], (data.NodeZMin[id] * _zScale) + _zOffset);
        var max = new Vector3(data.X[x0 + span], data.Y[y0 + span], (data.NodeZMax[id] * _zScale) + _zOffset);
        if (!Inside(min, max))
        {
            _state[id] = Culled;
            return 0;
        }

        _state[id] = Drawn;
        if (queue && level > 0)
        {
            var nearest = Vector3.Clamp(_eye, min, max);
            var distance = Vector3.Distance(_eye, nearest);
            var rho = distance > 1e-6f ? data.Error[id] * _factor / distance : float.PositiveInfinity;
            if (rho > 1)
            {
                _heap.Enqueue(id, -rho);
            }
        }

        return 1;
    }

    // Splits a drawn tile into its four children; returns the change in drawn tiles.
    private int SplitNode(int id, bool queue)
    {
        var data = _data;
        var level = data.NodeLevel[id] - 1;
        var (cx, cy) = (2 * data.NodeX[id], 2 * data.NodeY[id]);
        _state[id] = Split;
        var added = -1;
        for (var c = 0; c < 4; c++)
        {
            added += Consider(data.Node(level, cx + (c & 1), cy + (c >> 1)), queue);
        }

        return added;
    }

    // Collects the drawn tiles into Tiles, depth first from the roots; returns their count.
    private int Gather()
    {
        var data = _data;
        var depth = 0;
        var count = 0;
        for (var r = 0; r < SurfaceData.RootsPerAxis * SurfaceData.RootsPerAxis; r++)
        {
            _stack[depth++] = data.LevelOffset[data.TopLevel] + r;
        }

        while (depth > 0)
        {
            var id = _stack[--depth];
            switch (_state[id])
            {
                case Drawn:
                    Tiles[count++] = id;
                    break;
                case Split:
                    var level = data.NodeLevel[id] - 1;
                    var (cx, cy) = (2 * data.NodeX[id], 2 * data.NodeY[id]);
                    for (var c = 0; c < 4; c++)
                    {
                        _stack[depth++] = data.Node(level, cx + (c & 1), cy + (c >> 1));
                    }

                    break;
                default:
                    break;
            }
        }

        return count;
    }

    // Writes a value (a drawn tile's level, or CulledBit) over a tile's finest cells.
    private void Fill(int id, int value)
    {
        var data = _data;
        var level = data.NodeLevel[id];
        var size = 1 << level;
        var (fx0, fy0) = (data.NodeX[id] << level, data.NodeY[id] << level);
        for (var fy = fy0; fy < fy0 + size; fy++)
        {
            _finest.AsSpan((fy * _side) + fx0, size).Fill(value);
        }
    }

    // Splits each drawn tile that has a drawn neighbor two or more levels finer, until none has.
    private void Balance()
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            var count = Gather();
            for (var k = 0; k < count; k++)
            {
                var id = Tiles[k];
                var level = _data.NodeLevel[id];
                if (level < 2 || !HasFinerByTwo(id))
                {
                    continue;
                }

                _ = SplitNode(id, queue: false);
                BalanceSplits++;
                changed = true;
                var child = level - 1;
                var (cx, cy) = (2 * _data.NodeX[id], 2 * _data.NodeY[id]);
                for (var c = 0; c < 4; c++)
                {
                    var node = _data.Node(child, cx + (c & 1), cy + (c >> 1));
                    Fill(node, _state[node] == Drawn ? child : CulledBit);
                }
            }
        }
    }

    private bool HasFinerByTwo(int id)
    {
        var data = _data;
        var level = data.NodeLevel[id];
        var size = 1 << level;
        var (fx0, fy0) = (data.NodeX[id] << level, data.NodeY[id] << level);
        for (var t = 0; t < size; t++)
        {
            if (
                Finer(fx0 + t, fy0 - 1, level)
                || Finer(fx0 + t, fy0 + size, level)
                || Finer(fx0 - 1, fy0 + t, level)
                || Finer(fx0 + size, fy0 + t, level)
            )
            {
                return true;
            }
        }

        return false;
    }

    private bool Finer(int fx, int fy, int level)
    {
        var other = Level(fx, fy);
        return other >= 0 && other <= level - 2;
    }

    // The edges whose neighbor is one level coarser. After the restriction, a coarser neighbor covers the whole edge, so
    // the cell next to the edge's first cell names it.
    private byte Shape(int id)
    {
        var data = _data;
        var level = data.NodeLevel[id];
        var size = 1 << level;
        var (fx0, fy0) = (data.NodeX[id] << level, data.NodeY[id] << level);
        var coarser = level + 1;
        var mask = 0;
        mask |= Level(fx0, fy0 - 1) == coarser ? 1 : 0;
        mask |= Level(fx0 + size, fy0) == coarser ? 2 : 0;
        mask |= Level(fx0, fy0 + size) == coarser ? 4 : 0;
        mask |= Level(fx0 - 1, fy0) == coarser ? 8 : 0;
        return (byte)mask;
    }

    // The six planes of the frustum from the columns of a row-vector transform (clip = p·M), D3D's 0 ≤ z ≤ w.
    private void Planes(in Matrix4x4 m)
    {
        var c0 = new Vector4(m.M11, m.M21, m.M31, m.M41);
        var c1 = new Vector4(m.M12, m.M22, m.M32, m.M42);
        var c2 = new Vector4(m.M13, m.M23, m.M33, m.M43);
        var c3 = new Vector4(m.M14, m.M24, m.M34, m.M44);
        _planes[0] = c3 + c0;
        _planes[1] = c3 - c0;
        _planes[2] = c3 + c1;
        _planes[3] = c3 - c1;
        _planes[4] = c2;
        _planes[5] = c3 - c2;
    }

    // False when the box is wholly behind one plane: its corner farthest along the plane's normal is behind it.
    private bool Inside(Vector3 min, Vector3 max)
    {
        foreach (var plane in _planes)
        {
            var corner = new Vector3(plane.X > 0 ? max.X : min.X, plane.Y > 0 ? max.Y : min.Y, plane.Z > 0 ? max.Z : min.Z);
            if ((plane.X * corner.X) + (plane.Y * corner.Y) + (plane.Z * corner.Z) + plane.W < 0)
            {
                return false;
            }
        }

        return true;
    }
}
