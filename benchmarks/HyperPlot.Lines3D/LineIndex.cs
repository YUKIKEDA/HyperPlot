using System.Numerics;
using System.Runtime.InteropServices;

namespace HyperPlot.Lines3D;

/// <summary>How Douglas–Peucker picks the split vertex of an interval.</summary>
internal enum SplitDistance
{
    /// <summary>The largest Euclidean distance from the chord in the normalized space.</summary>
    Perpendicular,

    /// <summary>The largest of the three axis components of the offset from the chord (L∞).</summary>
    AxisMax,
}

/// <summary>An error vector (ex, ey, ez) stored as three halves, each relative to its segment's extent on that axis.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct Half3
{
    public Half X;
    public Half Y;
    public Half Z;
}

/// <summary>A segment's bounding box in the normalized space; its maximum error is its root node's error.</summary>
internal struct Segment
{
    public Vector3 Lo;
    public Vector3 Hi;
}

/// <summary>Which arrays hold the nodes: the errors and the offsets apart (10 B a vertex), packed (8 B), or both.</summary>
internal enum NodeLayout
{
    /// <summary><see cref="Half3"/> errors and <see cref="uint"/> offsets in two arrays, 10 B a vertex.</summary>
    Split,

    /// <summary>One <see cref="PackedNode"/> a vertex, 8 B: the error and a 2-byte offset read together (Issue #488, decision 3).</summary>
    Packed,

    /// <summary>Both, to compare them in one process.</summary>
    Both,
}

/// <summary>A node packed in 8 B: its error as three halves and its split vertex as m - v0 (segments up to 2^16).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct PackedNode
{
    public Half X;
    public Half Y;
    public Half Z;
    public ushort Offset;
}

/// <summary>Reads a node's error (relative to its segment's extent) and its split offset; a struct, so that each walk is specialized.</summary>
internal interface INodes
{
    Vector3 Error(int slot);

    int Offset(int slot);
}

/// <summary>The nodes of <see cref="NodeLayout.Split"/>.</summary>
internal readonly struct SplitNodes : INodes
{
    private readonly Half3[] _errors;
    private readonly uint[] _offsets;

    public SplitNodes(Half3[] errors, uint[] offsets)
    {
        _errors = errors;
        _offsets = offsets;
    }

    public Vector3 Error(int slot)
    {
        var e = _errors[slot];
        return new Vector3((float)e.X, (float)e.Y, (float)e.Z);
    }

    public int Offset(int slot) => (int)_offsets[slot];
}

/// <summary>The nodes of <see cref="NodeLayout.Packed"/>.</summary>
internal readonly struct PackedNodes : INodes
{
    private readonly PackedNode[] _nodes;

    public PackedNodes(PackedNode[] nodes)
    {
        _nodes = nodes;
    }

    public Vector3 Error(int slot)
    {
        ref readonly var node = ref _nodes[slot];
        return new Vector3((float)node.X, (float)node.Y, (float)node.Z);
    }

    public int Offset(int slot) => _nodes[slot].Offset;
}

/// <summary>
/// The index of a line (SPIKE, row K0b): fixed segments of 2^n vertices, each with its own Douglas–Peucker tree whose nodes
/// are its interior vertices, stored in preorder (BLG tree, Issue #481 decision 2).
/// </summary>
/// <remarks>
/// Segment s covers the vertices v0 = s·S to v1 = min(v0 + S, N - 1), both kept always; the next segment starts at v1. Its
/// interior vertices are the nodes of a binary tree: the root splits (v0, v1) at m, its left subtree splits (v0, m), its
/// right subtree (m, v1). Node j of the preorder sits at slot v0 + 1 + j of <see cref="Errors"/> and
/// <see cref="Offsets"/>; its left child is at j + 1 and its right child at j + (m - a), where (a, b) is its interval,
/// so that no pointer is stored. <see cref="Offsets"/> holds m - v0.
/// <para>
/// A node's own error is, for each axis, the largest |p_i - q_i| over the interior vertices i of its interval, where q_i is
/// the point of the chord nearest to p_i in the normalized space, clamped to the chord. q_i does not depend on the view, and
/// a scaling by axis k maps the chord to the chord, so the distance of k⊙p_i from the scaled chord is at most
/// ‖k⊙(p_i - q_i)‖ ≤ ‖k⊙e‖ for every k. The stored error is the componentwise maximum over the node's subtree, so that it
/// does not grow down the tree (the DP errors themselves are not monotone, van Oosterom's Figure 6), rounded up to a half
/// after division by the segment's extent.
/// </para>
/// </remarks>
internal sealed class LineIndex
{
    public LineIndex(LineData data, int segmentLog2, SplitDistance split, NodeLayout layout)
    {
        if (layout != NodeLayout.Split && segmentLog2 > 16)
        {
            throw new ArgumentException("The packed layout keeps m - v0 in 2 bytes: segments of 2^16 vertices at most.");
        }

        Data = data;
        SegmentLog2 = segmentLog2;
        SegmentSize = 1 << segmentLog2;
        Split = split;
        SegmentCount = Math.Max(1, (data.Count - 1 + SegmentSize - 1) / SegmentSize);
        if (layout != NodeLayout.Packed)
        {
            Errors = new Half3[data.Count];
            Offsets = new uint[data.Count];
        }

        if (layout != NodeLayout.Split)
        {
            Packed = new PackedNode[data.Count];
        }

        Segments = new Segment[SegmentCount];
    }

    public LineData Data { get; }

    public int SegmentLog2 { get; }

    public int SegmentSize { get; }

    public int SegmentCount { get; }

    public SplitDistance Split { get; }

    /// <summary>Gets the errors of <see cref="NodeLayout.Split"/>, or null when the index holds only the packed nodes.</summary>
    public Half3[]? Errors { get; }

    /// <summary>Gets the offsets of <see cref="NodeLayout.Split"/>, or null when the index holds only the packed nodes.</summary>
    public uint[]? Offsets { get; }

    /// <summary>Gets the nodes of <see cref="NodeLayout.Packed"/>, or null when the index holds only the split arrays.</summary>
    public PackedNode[]? Packed { get; }

    public Segment[] Segments { get; }

    /// <summary>Gets the reader of the split arrays.</summary>
    public SplitNodes SplitReader() =>
        new(Errors ?? throw new InvalidOperationException("The index has no split arrays."), Offsets!);

    /// <summary>Gets the reader of the packed nodes.</summary>
    public PackedNodes PackedReader() => new(Packed ?? throw new InvalidOperationException("The index has no packed nodes."));

    /// <summary>Gets a node's error relative to its segment's extent, from whichever layout the index holds (not on a timed path).</summary>
    public Vector3 RelativeError(int slot) => Packed is not null ? PackedReader().Error(slot) : SplitReader().Error(slot);

    /// <summary>Gets the largest depth of a node of any segment's tree, after <see cref="Build"/>.</summary>
    public int MaxDepth { get; private set; }

    /// <summary>Gets the mean depth of the nodes (the root is 1), after <see cref="Build"/>.</summary>
    public double MeanDepth { get; private set; }

    public int Start(int segment) => segment * SegmentSize;

    public int End(int segment) => Math.Min((segment * SegmentSize) + SegmentSize, Data.Count - 1);

    /// <summary>Builds every segment with the workers, each taking the next segment not yet taken.</summary>
    public void Build(int workers)
    {
        var next = -1;
        var maxDepth = 0;
        long depthSum = 0;
        long nodes = 0;
        var gate = new Lock();
        var threads = new Thread[workers];
        for (var w = 0; w < workers; w++)
        {
            threads[w] = new Thread(() =>
            {
                var scratch = new Scratch(SegmentSize);
                int s;
                while ((s = Interlocked.Increment(ref next)) < SegmentCount)
                {
                    BuildSegment(s, scratch);
                }

                lock (gate)
                {
                    maxDepth = Math.Max(maxDepth, scratch.MaxDepth);
                    depthSum += scratch.DepthSum;
                    nodes += scratch.Nodes;
                }
            })
            {
                IsBackground = true,
            };
            threads[w].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        MaxDepth = maxDepth;
        MeanDepth = nodes > 0 ? (double)depthSum / nodes : 0;
    }

    private void BuildSegment(int s, Scratch scratch)
    {
        var v0 = Start(s);
        var v1 = End(s);
        var lo = Data[v0];
        var hi = lo;
        for (var i = v0 + 1; i <= v1; i++)
        {
            var p = Data[i];
            lo = Vector3.Min(lo, p);
            hi = Vector3.Max(hi, p);
        }

        Segments[s] = new Segment { Lo = lo, Hi = hi };
        var n = v1 - v0 - 1;
        if (n <= 0)
        {
            return;
        }

        // Douglas–Peucker over the segment, each interval written at the preorder slot its parent gave it.
        var stack = scratch.Stack;
        var sp = 0;
        stack[sp++] = (v0, v1, 0, 1);
        while (sp > 0)
        {
            var (a, b, pos, depth) = stack[--sp];
            var m = SplitInterval(a, b, out var error);
            scratch.A[pos] = a;
            scratch.B[pos] = b;
            scratch.M[pos] = m;
            scratch.E[pos] = error;
            scratch.DepthSum += depth;
            scratch.MaxDepth = Math.Max(scratch.MaxDepth, depth);
            if (m - a >= 2)
            {
                stack[sp++] = (a, m, pos + 1, depth + 1);
            }

            if (b - m >= 2)
            {
                stack[sp++] = (m, b, pos + (m - a), depth + 1);
            }
        }

        scratch.Nodes += n;

        // Children come after their parent in preorder, so a backward pass gives each node its subtree's maximum.
        var extent = hi - lo;
        for (var pos = n - 1; pos >= 0; pos--)
        {
            var a = scratch.A[pos];
            var b = scratch.B[pos];
            var m = scratch.M[pos];
            var error = scratch.E[pos];
            if (m - a >= 2)
            {
                error = Vector3.Max(error, scratch.E[pos + 1]);
            }

            if (b - m >= 2)
            {
                error = Vector3.Max(error, scratch.E[pos + (m - a)]);
            }

            scratch.E[pos] = error;
            var slot = v0 + 1 + pos;
            var half = new Half3 { X = Up(error.X, extent.X), Y = Up(error.Y, extent.Y), Z = Up(error.Z, extent.Z) };
            if (Errors is { } errors)
            {
                errors[slot] = half;
                Offsets![slot] = (uint)(m - v0);
            }

            if (Packed is { } packed)
            {
                packed[slot] = new PackedNode
                {
                    X = half.X,
                    Y = half.Y,
                    Z = half.Z,
                    Offset = (ushort)(m - v0),
                };
            }
        }
    }

    /// <summary>Returns the split vertex of (a, b) and the node's own error: per axis, the largest |p_i - q_i|.</summary>
    private int SplitInterval(int a, int b, out Vector3 error)
    {
        var x = Data.X;
        var y = Data.Y;
        var z = Data.Z;
        var ax = x[a];
        var ay = y[a];
        var az = z[a];
        var dx = x[b] - ax;
        var dy = y[b] - ay;
        var dz = z[b] - az;
        var dd = (dx * dx) + (dy * dy) + (dz * dz);
        var inverse = dd > 0 ? 1 / dd : 0;
        var perpendicular = Split == SplitDistance.Perpendicular;
        var mx = 0f;
        var my = 0f;
        var mz = 0f;
        var best = -1f;
        var m = a + 1;
        for (var i = a + 1; i < b; i++)
        {
            var px = x[i] - ax;
            var py = y[i] - ay;
            var pz = z[i] - az;
            var t = Math.Clamp(((px * dx) + (py * dy) + (pz * dz)) * inverse, 0f, 1f);
            var ex = MathF.Abs(px - (t * dx));
            var ey = MathF.Abs(py - (t * dy));
            var ez = MathF.Abs(pz - (t * dz));
            mx = MathF.Max(mx, ex);
            my = MathF.Max(my, ey);
            mz = MathF.Max(mz, ez);
            var d = perpendicular ? (ex * ex) + (ey * ey) + (ez * ez) : MathF.Max(ex, MathF.Max(ey, ez));
            if (d > best)
            {
                best = d;
                m = i;
            }
        }

        error = new Vector3(mx, my, mz);
        return m;
    }

    /// <summary>The error over the extent as a half, never below the value (a margin of 2^-20 covers the float rounding).</summary>
    private static Half Up(float value, float extent)
    {
        if (value <= 0 || extent <= 0)
        {
            return Half.Zero;
        }

        var ratio = value / extent * (1 + (1f / (1 << 20)));
        var half = (Half)ratio;
        if ((float)half < ratio)
        {
            half = BitConverter.UInt16BitsToHalf((ushort)(BitConverter.HalfToUInt16Bits(half) + 1));
        }

        return half;
    }

    /// <summary>A worker's buffers for one segment, reused from segment to segment.</summary>
    private sealed class Scratch(int size)
    {
        public readonly int[] A = new int[size];
        public readonly int[] B = new int[size];
        public readonly int[] M = new int[size];
        public readonly Vector3[] E = new Vector3[size];
        public readonly (int A, int B, int Pos, int Depth)[] Stack = new (int, int, int, int)[size + 1];
        public long DepthSum;
        public long Nodes;
        public int MaxDepth;
    }
}
