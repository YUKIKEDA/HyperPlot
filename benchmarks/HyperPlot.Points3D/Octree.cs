using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace HyperPlot.Points3D;

/// <summary>
/// A node of the octree's table, in pre-order (SPIKE, row K0a, Issue #477). The node's own points are the copy's points
/// from <see cref="First"/>, <see cref="Count"/> of them, in the order of their Morton keys.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct OctreeNode
{
    /// <summary>The node's cube: the Morton code of its corner at its level, 3 × <see cref="Level"/> bits.</summary>
    public ulong Code;

    /// <summary>The node's first point in the copy.</summary>
    public long First;

    /// <summary>The node's own points.</summary>
    public int Count;

    /// <summary>The index in the table after the node's last descendant: the next node that is not below it.</summary>
    public int SubtreeEnd;

    /// <summary>The node's level: 0 for the root, whose cube is the box.</summary>
    public int Level;
}

/// <summary>
/// A point of the copy in the form of design §13.2 (Issue #470, decision 12): x, y, and z as 32-bit integers from the
/// corner of the node's cube, and the source row, 16 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CopyPoint
{
    public int X;
    public int Y;
    public int Z;
    public uint Row;
}

/// <summary>The time of each stage of a build, in milliseconds.</summary>
internal sealed class OctreeTimes
{
    public double Keys { get; set; }

    public double Sort { get; set; }

    public double Levels { get; set; }

    public double Table { get; set; }

    public double Copy { get; set; }

    public double Total => Keys + Sort + Levels + Table + Copy;
}

/// <summary>
/// SPIKE (row K0a, Issue #477): the additive octree of design §13.3 over the box [-1, 1]³. Each source point lies in
/// exactly one node (decision 6). A node keeps, in each cell of a grid of 2^GridBits cells a side laid over its cube, the
/// point of the least seeded priority among the points that reach it (decision 7: the first point of a seeded shuffle,
/// without shuffling); the others go down to its children. A node that more than <c>leaf</c> points reach keeps only
/// those, and a node that at most <c>leaf</c> points reach keeps them all and has no children. Not product code: the
/// arrays are in memory, the root's pass runs on one thread, and a bucket of the sort is sorted by comparison.
/// </summary>
internal sealed class Octree
{
    /// <summary>The bits of each axis in a point's Morton key: the deepest level.</summary>
    public const int Bits = 21;

    private const int Cells = 1 << Bits;
    private const float HalfCells = 1 << (Bits - 1);
    private const int Block = 1 << 20;
    private const int TopBits = 12;
    private const int Buckets = 1 << TopBits;
    private const int TopShift = (3 * Bits) - TopBits;

    /// <summary>The source x of each row.</summary>
    public required float[] X { get; init; }

    public required float[] Y { get; init; }

    public required float[] Z { get; init; }

    /// <summary>The table, in pre-order: a node's first child is the next node.</summary>
    public required OctreeNode[] Nodes { get; init; }

    /// <summary>The copy, in the order of the table: each node's own points together.</summary>
    public required CopyPoint[] Copy { get; init; }

    /// <summary>The source row of each point of the copy (kept for the spike: the copy holds it too).</summary>
    public required int[] Order { get; init; }

    /// <summary>The Morton key of each point of the copy (kept for the spike's hit test; the copy can give it).</summary>
    public required ulong[] Keys { get; init; }

    public required int GridBits { get; init; }

    public required int Leaf { get; init; }

    public required OctreeTimes Times { get; init; }

    /// <summary>Builds the octree of the source columns on at most <paramref name="workers"/> threads.</summary>
    public static Octree Build(float[] x, float[] y, float[] z, int gridBits, int leaf, int workers, int seed)
    {
        var count = x.Length;
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = workers };
        var times = new OctreeTimes();
        var watch = Stopwatch.StartNew();

        var keys = new ulong[count];
        var order = new int[count];
        Parallel.For(0, BlockCount(count), parallel, block => MakeKeys(block, x, y, z, keys, order));
        times.Keys = Lap(watch);

        var sortedKeys = new ulong[count];
        var sortedOrder = new int[count];
        SortByKey(keys, order, sortedKeys, sortedOrder, workers, parallel);
        times.Sort = Lap(watch);

        // The unsorted arrays become the scratch of the partitions.
        var context = new SplitContext
        {
            Keys = sortedKeys,
            Order = sortedOrder,
            ScratchKeys = keys,
            ScratchOrder = order,
            Marks = new byte[count],
            GridBits = gridBits,
            Leaf = leaf,
            SeedMix = SeedMix(seed),
        };
        var made = MakeNodes(context, count, parallel);
        times.Levels = Lap(watch);

        var nodes = MakeTable(made);
        times.Table = Lap(watch);

        var copy = new CopyPoint[count];
        Parallel.For(0, nodes.Length, parallel, n => CopyNode(nodes[n], sortedOrder, x, y, z, copy));
        times.Copy = Lap(watch);

        return new Octree
        {
            X = x,
            Y = y,
            Z = z,
            Nodes = nodes,
            Copy = copy,
            Order = sortedOrder,
            Keys = sortedKeys,
            GridBits = gridBits,
            Leaf = leaf,
            Times = times,
        };
    }

    /// <summary>The seeded priority of a row: the least in a cell is the first of the seeded shuffle (decision 7).</summary>
    public static ulong Priority(int row, ulong seedMix) => Mix((ulong)row ^ seedMix);

    public static ulong SeedMix(int seed) => Mix((ulong)seed);

    /// <summary>The center of a cube of the octree and half its side, in the box's coordinates.</summary>
    public static void Cube(ulong code, int level, out Vector3 center, out float half)
    {
        var side = 2.0 / (1L << level);
        half = (float)(side / 2);
        center = new Vector3(
            (float)(-1 + ((Compact(code) + 0.5) * side)),
            (float)(-1 + ((Compact(code >> 1) + 0.5) * side)),
            (float)(-1 + ((Compact(code >> 2) + 0.5) * side))
        );
    }

    /// <summary>The first index in [from, to) whose key, shifted right, is at least the prefix; the keys are sorted.</summary>
    public static int LowerBound(ulong[] keys, int from, int to, int shift, ulong prefix)
    {
        var low = from;
        var high = to;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if ((keys[middle] >> shift) < prefix)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>
    /// Chooses the nodes to draw (design §13.3): the largest on the screen first, a node's children after it, until the next
    /// node would pass the budget. The size on the screen is the radius in pixels of the sphere around the node's cube, seen
    /// from the eye at its center's distance. Every node is in view in the orbit (decision 16), so none is culled. Returns
    /// the points chosen.
    /// </summary>
    public long Select(Vector3 eye, float focal, long budget, List<int> selected, PriorityQueue<int, (float Size, int Node)> queue)
    {
        selected.Clear();
        queue.Clear();
        var nodes = Nodes;
        var total = 0L;
        queue.Enqueue(0, (-ScreenSize(nodes[0], eye, focal), 0));
        while (queue.TryDequeue(out var node, out _))
        {
            var count = nodes[node].Count;
            if (total + count > budget)
            {
                break;
            }

            total += count;
            selected.Add(node);
            for (var child = node + 1; child < nodes[node].SubtreeEnd; child = nodes[child].SubtreeEnd)
            {
                queue.Enqueue(child, (-ScreenSize(nodes[child], eye, focal), child));
            }
        }

        return total;
    }

    private static float ScreenSize(in OctreeNode node, Vector3 eye, float focal)
    {
        Cube(node.Code, node.Level, out var center, out var half);
        var radius = half * 1.7320508f;
        var distance = Vector3.Distance(eye, center);
        return distance <= radius ? float.MaxValue : focal * radius / distance;
    }

    private static int BlockCount(int count) => (int)(((long)count + Block - 1) / Block);

    private static double Lap(Stopwatch watch)
    {
        var milliseconds = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        return milliseconds;
    }

    private static void MakeKeys(int block, float[] x, float[] y, float[] z, ulong[] keys, int[] order)
    {
        var from = block * Block;
        var to = Math.Min(x.Length, from + Block);
        for (var i = from; i < to; i++)
        {
            keys[i] = Spread(Quantize(x[i])) | (Spread(Quantize(y[i])) << 1) | (Spread(Quantize(z[i])) << 2);
            order[i] = i;
        }
    }

    private static uint Quantize(float value) => (uint)Math.Clamp((int)((value + 1f) * HalfCells), 0, Cells - 1);

    // The 21 low bits of a value, spread to every third bit.
    private static ulong Spread(uint value)
    {
        ulong bits = value & 0x1FFFFFU;
        bits = (bits | (bits << 32)) & 0x1F00000000FFFFUL;
        bits = (bits | (bits << 16)) & 0x1F0000FF0000FFUL;
        bits = (bits | (bits << 8)) & 0x100F00F00F00F00FUL;
        bits = (bits | (bits << 4)) & 0x10C30C30C30C30C3UL;
        bits = (bits | (bits << 2)) & 0x1249249249249249UL;
        return bits;
    }

    // The inverse of Spread: every third bit, from bit 0, gathered.
    private static uint Compact(ulong value)
    {
        var bits = value & 0x1249249249249249UL;
        bits = (bits ^ (bits >> 2)) & 0x10C30C30C30C30C3UL;
        bits = (bits ^ (bits >> 4)) & 0x100F00F00F00F00FUL;
        bits = (bits ^ (bits >> 8)) & 0x1F0000FF0000FFUL;
        bits = (bits ^ (bits >> 16)) & 0x1F00000000FFFFUL;
        bits = (bits ^ (bits >> 32)) & 0x1FFFFFUL;
        return (uint)bits;
    }

    // SplitMix64's finalizer.
    private static ulong Mix(ulong value)
    {
        value += 0x9E3779B97F4A7C15UL;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    /// <summary>
    /// Sorts by key: the points go to 4,096 buckets by the key's top 12 bits, stably and in parallel blocks, and each bucket
    /// is then sorted. The scatter keeps the source order within a bucket for any number of blocks, so the result does not
    /// depend on the workers.
    /// </summary>
    private static void SortByKey(ulong[] keys, int[] order, ulong[] sortedKeys, int[] sortedOrder, int workers, ParallelOptions parallel)
    {
        var blocks = Math.Max(1, workers * 4);
        var counts = new int[blocks * Buckets];
        Parallel.For(0, blocks, parallel, block => CountBuckets(block, blocks, keys, counts));

        var starts = new int[blocks * Buckets];
        var bucketStarts = new int[Buckets + 1];
        var at = 0;
        for (var bucket = 0; bucket < Buckets; bucket++)
        {
            bucketStarts[bucket] = at;
            for (var block = 0; block < blocks; block++)
            {
                starts[(block * Buckets) + bucket] = at;
                at += counts[(block * Buckets) + bucket];
            }
        }

        bucketStarts[Buckets] = at;
        Parallel.For(0, blocks, parallel, block => Scatter(block, blocks, keys, order, starts, sortedKeys, sortedOrder));
        Parallel.For(0, Buckets, parallel, bucket => SortBucket(sortedKeys, sortedOrder, bucketStarts[bucket], bucketStarts[bucket + 1]));
    }

    private static int BlockStart(int block, int blocks, int count) => (int)((long)count * block / blocks);

    private static void CountBuckets(int block, int blocks, ulong[] keys, int[] counts)
    {
        var from = BlockStart(block, blocks, keys.Length);
        var to = BlockStart(block + 1, blocks, keys.Length);
        var row = block * Buckets;
        for (var i = from; i < to; i++)
        {
            counts[row + (int)(keys[i] >> TopShift)]++;
        }
    }

    private static void Scatter(int block, int blocks, ulong[] keys, int[] order, int[] starts, ulong[] sortedKeys, int[] sortedOrder)
    {
        var from = BlockStart(block, blocks, keys.Length);
        var to = BlockStart(block + 1, blocks, keys.Length);
        var row = block * Buckets;
        for (var i = from; i < to; i++)
        {
            var slot = row + (int)(keys[i] >> TopShift);
            var at = starts[slot];
            starts[slot] = at + 1;
            sortedKeys[at] = keys[i];
            sortedOrder[at] = order[i];
        }
    }

    private static void SortBucket(ulong[] keys, int[] order, int from, int to)
    {
        if (to - from > 1)
        {
            Array.Sort(keys, order, from, to - from);
        }
    }

    /// <summary>Splits the nodes level by level, the nodes of a level in parallel; returns every node made.</summary>
    private static List<MadeNode> MakeNodes(SplitContext context, int count, ParallelOptions parallel)
    {
        var made = new List<MadeNode>();
        var level = new List<PendingNode>
        {
            new()
            {
                Code = 0,
                Level = 0,
                Start = 0,
                End = count,
            },
        };
        while (level.Count > 0)
        {
            var pending = level.ToArray();
            var results = new SplitResult[pending.Length];
            Parallel.For(0, pending.Length, parallel, i => results[i] = Split(pending[i], context));
            var next = new List<PendingNode>();
            for (var i = 0; i < pending.Length; i++)
            {
                made.Add(
                    new MadeNode
                    {
                        Code = pending[i].Code,
                        Level = pending[i].Level,
                        Start = pending[i].Start,
                        Own = results[i].Own,
                        End = pending[i].End,
                    }
                );
                next.AddRange(results[i].Children);
            }

            level = next;
        }

        return made;
    }

    /// <summary>
    /// Keeps a node's points, one a cell of its grid, at the start of its range and the others after them, both in key
    /// order; the others are its children's ranges, child by child.
    /// </summary>
    private static SplitResult Split(PendingNode node, SplitContext context)
    {
        var count = node.End - node.Start;
        if (count <= context.Leaf || node.Level >= Bits)
        {
            return new SplitResult { Own = count, Children = [] };
        }

        var keys = context.Keys;
        var order = context.Order;
        var marks = context.Marks;
        var cellShift = 3 * Math.Max(0, Bits - (node.Level + context.GridBits));
        var own = 0;
        var i = node.Start;
        while (i < node.End)
        {
            var cell = keys[i] >> cellShift;
            var best = i;
            var bestPriority = Priority(order[i], context.SeedMix);
            var j = i + 1;
            while (j < node.End && (keys[j] >> cellShift) == cell)
            {
                var priority = Priority(order[j], context.SeedMix);
                if (priority < bestPriority || (priority == bestPriority && order[j] < order[best]))
                {
                    best = j;
                    bestPriority = priority;
                }

                j++;
            }

            marks[best] = 1;
            own++;
            i = j;
        }

        var ownAt = node.Start;
        var restAt = node.Start + own;
        for (var k = node.Start; k < node.End; k++)
        {
            int to;
            if (marks[k] != 0)
            {
                to = ownAt;
                ownAt++;
            }
            else
            {
                to = restAt;
                restAt++;
            }

            context.ScratchKeys[to] = keys[k];
            context.ScratchOrder[to] = order[k];
        }

        Array.Copy(context.ScratchKeys, node.Start, keys, node.Start, count);
        Array.Copy(context.ScratchOrder, node.Start, order, node.Start, count);
        Array.Clear(marks, node.Start, count);

        var children = new List<PendingNode>(8);
        var childShift = 3 * (Bits - (node.Level + 1));
        var from = node.Start + own;
        for (var digit = 0UL; digit < 8; digit++)
        {
            var prefix = (node.Code << 3) | digit;
            var end = LowerBound(keys, from, node.End, childShift, prefix + 1);
            if (end > from)
            {
                children.Add(
                    new PendingNode
                    {
                        Code = prefix,
                        Level = node.Level + 1,
                        Start = from,
                        End = end,
                    }
                );
            }

            from = end;
        }

        return new SplitResult { Own = own, Children = children.ToArray() };
    }

    /// <summary>
    /// Puts the nodes in pre-order: a node's own points come before its children's ranges, so the order of the first
    /// points is the pre-order. A node's subtree ends at the first later node that starts at or past its range's end.
    /// </summary>
    private static OctreeNode[] MakeTable(List<MadeNode> made)
    {
        var count = made.Count;
        var firsts = new int[count];
        var index = new int[count];
        for (var i = 0; i < count; i++)
        {
            firsts[i] = made[i].Start;
            index[i] = i;
        }

        Array.Sort(firsts, index);
        var nodes = new OctreeNode[count];
        var ends = new int[count];
        for (var i = 0; i < count; i++)
        {
            var node = made[index[i]];
            nodes[i] = new OctreeNode
            {
                Code = node.Code,
                First = node.Start,
                Count = node.Own,
                SubtreeEnd = count,
                Level = node.Level,
            };
            ends[i] = node.End;
        }

        var stack = new int[Bits + 2];
        var top = 0;
        for (var i = 0; i < count; i++)
        {
            while (top > 0 && ends[stack[top - 1]] <= nodes[i].First)
            {
                nodes[stack[top - 1]].SubtreeEnd = i;
                top--;
            }

            stack[top] = i;
            top++;
        }

        return nodes;
    }

    private static void CopyNode(OctreeNode node, int[] order, float[] x, float[] y, float[] z, CopyPoint[] copy)
    {
        var side = 2.0 / (1L << node.Level);
        var scale = int.MaxValue / side;
        var minX = -1 + (Compact(node.Code) * side);
        var minY = -1 + (Compact(node.Code >> 1) * side);
        var minZ = -1 + (Compact(node.Code >> 2) * side);
        var from = (int)node.First;
        var to = from + node.Count;
        for (var i = from; i < to; i++)
        {
            var row = order[i];
            copy[i] = new CopyPoint
            {
                X = (int)Math.Clamp((x[row] - minX) * scale, 0, int.MaxValue),
                Y = (int)Math.Clamp((y[row] - minY) * scale, 0, int.MaxValue),
                Z = (int)Math.Clamp((z[row] - minZ) * scale, 0, int.MaxValue),
                Row = (uint)row,
            };
        }
    }

    private struct PendingNode
    {
        public ulong Code;
        public int Level;
        public int Start;
        public int End;
    }

    private struct MadeNode
    {
        public ulong Code;
        public int Level;
        public int Start;
        public int Own;
        public int End;
    }

    private struct SplitResult
    {
        public int Own;
        public PendingNode[] Children;
    }

    private sealed class SplitContext
    {
        public required ulong[] Keys { get; init; }

        public required int[] Order { get; init; }

        public required ulong[] ScratchKeys { get; init; }

        public required int[] ScratchOrder { get; init; }

        public required byte[] Marks { get; init; }

        public required int GridBits { get; init; }

        public required int Leaf { get; init; }

        public required ulong SeedMix { get; init; }
    }
}

/// <summary>
/// SPIKE (row K0a, Issue #477): the ray hit test of requirement K-7 for the octree's points. A query finds the nearest point
/// (the least depth, then the least index in the copy) whose center lies within a radius in pixels of the cursor. A cube
/// is visited only when the sphere around it can reach the cursor on the screen; inside a node, the points are in key
/// order, so a node's cube is split into its octants by binary search, down to 32 points, which are projected one by one.
/// </summary>
internal sealed class HitTester(Octree tree, int width, int height)
{
    private const int ScanBelow = 32;
    private const float Sqrt3 = 1.7320508f;

    private Matrix4x4 _transform;
    private Matrix4x4 _view;
    private float _focal;
    private Vector2 _cursor;
    private float _radius;

    /// <summary>The index in the copy of the point found, or -1.</summary>
    public int Best { get; private set; } = -1;

    public float BestDepth { get; private set; } = float.MaxValue;

    /// <summary>The points projected by the query.</summary>
    public long Tested { get; private set; }

    public void Begin(Matrix4x4 transform, Matrix4x4 view, float focal, Vector2 cursor, float radius)
    {
        _transform = transform;
        _view = view;
        _focal = focal;
        _cursor = cursor;
        _radius = radius;
        Best = -1;
        BestDepth = float.MaxValue;
        Tested = 0;
    }

    /// <summary>Every point: the octree from the root to the leaves, a subtree skipped when its cube cannot reach the cursor.</summary>
    public void Full()
    {
        var nodes = tree.Nodes;
        var i = 0;
        while (i < nodes.Length)
        {
            var node = nodes[i];
            if (CubeHit(node.Code, node.Level))
            {
                Range((int)node.First, (int)node.First + node.Count, node.Code, node.Level);
                i++;
            }
            else
            {
                i = node.SubtreeEnd;
            }
        }
    }

    /// <summary>The points of the nodes drawn.</summary>
    public void Drawn(ReadOnlySpan<int> selected)
    {
        var nodes = tree.Nodes;
        foreach (var index in selected)
        {
            var node = nodes[index];
            if (CubeHit(node.Code, node.Level))
            {
                Range((int)node.First, (int)node.First + node.Count, node.Code, node.Level);
            }
        }
    }

    /// <summary>Every point, one by one: the reference the octree's answer is checked against.</summary>
    public void All()
    {
        for (var i = 0; i < tree.Order.Length; i++)
        {
            Scan(i);
        }
    }

    private void Range(int from, int to, ulong code, int level)
    {
        if (to - from <= ScanBelow || level >= Octree.Bits)
        {
            for (var i = from; i < to; i++)
            {
                Scan(i);
            }

            return;
        }

        var keys = tree.Keys;
        var shift = 3 * (Octree.Bits - (level + 1));
        var at = from;
        for (var digit = 0UL; digit < 8; digit++)
        {
            var prefix = (code << 3) | digit;
            var end = Octree.LowerBound(keys, at, to, shift, prefix + 1);
            if (end > at && CubeHit(prefix, level + 1))
            {
                Range(at, end, prefix, level + 1);
            }

            at = end;
        }
    }

    // Whether a point of the sphere around the cube can project within the radius of the cursor. For a point q of a sphere
    // of radius R around c, in view space with w the distance along the view: |q.xy / q.w - c.xy / c.w| ≤ R (1 + |c.xy| /
    // c.w) / (c.w - R), times the focal length in pixels.
    private bool CubeHit(ulong code, int level)
    {
        Octree.Cube(code, level, out var center, out var half);
        var radius = half * Sqrt3;
        var inView = Vector3.Transform(center, _view);
        var w = -inView.Z;
        if (w <= radius)
        {
            return true;
        }

        var clip = Vector4.Transform(new Vector4(center, 1), _transform);
        var dx = (((clip.X / clip.W * 0.5f) + 0.5f) * width) - _cursor.X;
        var dy = ((0.5f - (clip.Y / clip.W * 0.5f)) * height) - _cursor.Y;
        var off = MathF.Sqrt((inView.X * inView.X) + (inView.Y * inView.Y)) / w;
        var reach = (_focal * radius * (1 + off) / (w - radius)) + _radius;
        return (dx * dx) + (dy * dy) <= reach * reach;
    }

    private void Scan(int i)
    {
        Tested++;
        var row = tree.Order[i];
        var clip = Vector4.Transform(new Vector4(tree.X[row], tree.Y[row], tree.Z[row], 1), _transform);
        if (clip.W <= 0)
        {
            return;
        }

        var depth = clip.Z / clip.W;
        var dx = (((clip.X / clip.W * 0.5f) + 0.5f) * width) - _cursor.X;
        var dy = ((0.5f - (clip.Y / clip.W * 0.5f)) * height) - _cursor.Y;
        if ((dx * dx) + (dy * dy) > _radius * _radius || depth < 0 || depth > 1)
        {
            return;
        }

        if (depth < BestDepth || (depth == BestDepth && i < Best))
        {
            Best = i;
            BestDepth = depth;
        }
    }
}
