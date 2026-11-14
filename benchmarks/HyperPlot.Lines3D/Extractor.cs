namespace HyperPlot.Lines3D;

/// <summary>The result of one extraction.</summary>
internal readonly struct Extraction
{
    public Extraction(int vertices, int length, bool aborted, int reached, int nodes)
    {
        Vertices = vertices;
        Length = length;
        Aborted = aborted;
        Reached = reached;
        Nodes = nodes;
    }

    /// <summary>Gets the vertices written (breaks not counted).</summary>
    public int Vertices { get; }

    /// <summary>Gets the end of the entries written: vertex indices in order, and -1 where a culled segment breaks the line.</summary>
    public int Length { get; }

    /// <summary>Gets a value indicating whether the budget was passed and the extraction stopped.</summary>
    public bool Aborted { get; }

    /// <summary>Gets the last vertex written, to estimate the share of the line done when the extraction stopped.</summary>
    public int Reached { get; }

    /// <summary>Gets the nodes visited: each node whose error was tested against the view, pruned or not.</summary>
    public int Nodes { get; }
}

/// <summary>
/// Picks the vertices to draw for a view (SPIKE, row K0b): segment by segment, an in-order walk of each tree that does not
/// go below a node the view prunes. One per thread; allocates nothing.
/// </summary>
internal sealed class Extractor
{
    private readonly LineIndex _index;
    private readonly NodeLayout _layout;
    private readonly Frame[] _stack;

    /// <summary>Initializes a new instance of the <see cref="Extractor"/> class over the split arrays when the index has them.</summary>
    public Extractor(LineIndex index)
        : this(index, index.Errors is not null ? NodeLayout.Split : NodeLayout.Packed) { }

    /// <summary>Initializes a new instance of the <see cref="Extractor"/> class over one layout of the index.</summary>
    public Extractor(LineIndex index, NodeLayout layout)
    {
        if (layout == NodeLayout.Both)
        {
            throw new ArgumentException("An extractor reads one layout.", nameof(layout));
        }

        _index = index;
        _layout = layout;
        _stack = new Frame[(2 * index.SegmentSize) + 8];
    }

    /// <summary>
    /// Writes the vertices whose screen error is at or above the tolerance, in the line's order, into the output; stops as
    /// soon as more than the budget are written. The output holds the budget plus the segment count plus 4.
    /// </summary>
    public Extraction Extract(in View view, float tolerance, int budget, int[] output) =>
        Extract(view, tolerance, budget, output, 0, _index.SegmentCount, 0);

    /// <summary>
    /// Extracts the segments [first, end) into the output from <paramref name="start"/> on, after a break when the entry
    /// before does not end where the range starts. The output holds start plus the budget plus end - first plus 4.
    /// </summary>
    public Extraction Extract(in View view, float tolerance, int budget, int[] output, int first, int end, int start) =>
        _layout == NodeLayout.Packed
            ? Walk(_index.PackedReader(), view, tolerance, budget, output, first, end, start)
            : Walk(_index.SplitReader(), view, tolerance, budget, output, first, end, start);

    private Extraction Walk<TNodes>(
        TNodes nodes,
        in View view,
        float tolerance,
        int budget,
        int[] output,
        int first,
        int end,
        int start
    )
        where TNodes : struct, INodes
    {
        var data = _index.Data;
        var segments = _index.Segments;
        var stack = _stack;
        var tolerance2 = tolerance * tolerance;
        var length = start;
        var count = 0;
        var visited = 0;
        if (start > 0 && first < end && output[start - 1] >= 0 && output[start - 1] != _index.Start(first))
        {
            output[length++] = -1;
        }

        for (var s = first; s < end; s++)
        {
            var v0 = _index.Start(s);
            var v1 = _index.End(s);
            var segment = segments[s];
            if (view.Outside(segment.Lo, segment.Hi))
            {
                if (length > 0 && output[length - 1] >= 0)
                {
                    output[length++] = -1;
                }

                continue;
            }

            if (length == 0 || output[length - 1] != v0)
            {
                output[length++] = v0;
                count++;
            }

            var extent = segment.Hi - segment.Lo;
            var slots = v0 + 1;
            var sp = 0;
            if (v1 - v0 >= 2)
            {
                stack[sp++] = new Frame(0, v0, v1);
            }

            while (sp > 0)
            {
                var frame = stack[--sp];
                if (frame.Pos < 0)
                {
                    output[length++] = frame.A;
                    if (++count > budget)
                    {
                        return new Extraction(count, length, aborted: true, frame.A, visited);
                    }

                    continue;
                }

                visited++;
                var error = nodes.Error(slots + frame.Pos) * extent;
                if (view.Prune(data[frame.A], data[frame.B], error, tolerance2))
                {
                    continue;
                }

                // Right subtree, the vertex, then the left subtree: the stack gives them back in the line's order.
                var m = v0 + nodes.Offset(slots + frame.Pos);
                if (frame.B - m >= 2)
                {
                    stack[sp++] = new Frame(frame.Pos + (m - frame.A), m, frame.B);
                }

                stack[sp++] = new Frame(-1, m, 0);
                if (m - frame.A >= 2)
                {
                    stack[sp++] = new Frame(frame.Pos + 1, frame.A, m);
                }
            }

            output[length++] = v1;
            if (++count > budget)
            {
                return new Extraction(count, length, aborted: true, v1, visited);
            }
        }

        return new Extraction(count, length, aborted: false, end > first ? _index.End(end - 1) : 0, visited);
    }

    /// <summary>A node to visit (Pos ≥ 0: its preorder slot and its interval (A, B)) or a vertex to write (Pos -1, A).</summary>
    private readonly struct Frame(int pos, int a, int b)
    {
        public readonly int Pos = pos;
        public readonly int A = a;
        public readonly int B = b;
    }
}
