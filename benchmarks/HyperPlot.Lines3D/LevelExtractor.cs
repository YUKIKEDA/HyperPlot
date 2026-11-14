using System.Diagnostics;

namespace HyperPlot.Lines3D;

/// <summary>Why a level-order extraction stopped before the tolerance held everywhere.</summary>
internal enum StopReason
{
    /// <summary>It did not stop: every node under the tolerance or culled.</summary>
    None,

    /// <summary>The nodes visited reached the limit.</summary>
    Nodes,

    /// <summary>The deadline passed.</summary>
    Time,

    /// <summary>The frontier or the kept vertices filled their buffers.</summary>
    Capacity,
}

/// <summary>The result of one level-order extraction by one member.</summary>
internal readonly struct LevelExtraction
{
    public LevelExtraction(int vertices, int length, int nodes, StopReason stop, int remaining)
    {
        Vertices = vertices;
        Length = length;
        Nodes = nodes;
        Stop = stop;
        Remaining = remaining;
    }

    /// <summary>Gets the vertices written (breaks not counted).</summary>
    public int Vertices { get; }

    /// <summary>Gets the entries written: vertex indices in order, and -1 where the line breaks.</summary>
    public int Length { get; }

    /// <summary>Gets the nodes visited.</summary>
    public int Nodes { get; }

    /// <summary>Gets why it stopped, or <see cref="StopReason.None"/>.</summary>
    public StopReason Stop { get; }

    /// <summary>Gets the nodes left in the frontier when it stopped, drawn as their chords.</summary>
    public int Remaining { get; }
}

/// <summary>
/// Picks the vertices to draw for a view level by level over every segment a member owns (SPIKE, row K0b, Issue #488
/// decision 2): the roots of all its segments first, then all their children, and so on, so that a stop by time or by
/// nodes visited leaves a coarser line over the whole range, not a finished part and a missing rest. The kept vertices are
/// then sorted (radix, O(k)) into the line's order, with a break where no owned, unculled segment holds the chord.
/// </summary>
/// <remarks>
/// A member owns the segments of every members-th block of <see cref="BlockSegments"/> segments, so that the members share
/// the visible part of the line in a zoom too. A node in the frontier when the extraction stops is drawn as its chord: its
/// screen error is at most its bound (<see cref="View.Bound2"/>), which <see cref="AchievedPx"/> gives, outside the timing.
/// One per member; allocates nothing.
/// </remarks>
internal sealed class LevelExtractor
{
    /// <summary>The segments in a block dealt to one member.</summary>
    public const int BlockSegments = 4;

    private const int BlockLog2 = 2;

    private readonly LineIndex _index;
    private readonly NodeLayout _layout;
    private readonly bool[] _culled;
    private readonly int[] _kept;
    private readonly int[] _sorted;
    private readonly int[] _counts = new int[1 << 16];
    private readonly Entry[] _a;
    private readonly Entry[] _b;
    private Entry[] _remainCurrent;
    private int _remainFrom;
    private int _remainEnd;
    private Entry[] _remainNext;
    private int _remainNextCount;

    /// <summary>Initializes a new instance of the <see cref="LevelExtractor"/> class with room for this many kept vertices.</summary>
    public LevelExtractor(LineIndex index, NodeLayout layout, int capacity)
    {
        if (layout == NodeLayout.Both)
        {
            throw new ArgumentException("An extractor reads one layout.", nameof(layout));
        }

        _index = index;
        _layout = layout;
        Capacity = Math.Max(capacity, (2 * index.SegmentCount) + 16);
        _culled = new bool[index.SegmentCount];
        _kept = new int[Capacity];
        _sorted = new int[Capacity];
        _a = new Entry[Capacity];
        _b = new Entry[Capacity];
        _remainCurrent = _a;
        _remainNext = _b;
    }

    /// <summary>Gets the kept vertices it has room for; an output holds twice as many entries.</summary>
    public int Capacity { get; }

    public static bool Owns(int segment, int member, int members) => ((segment >> BlockLog2) % members) == member;

    /// <summary>
    /// Extracts the member's segments at the tolerance into the output, until the nodes visited reach the limit or the
    /// clock passes the deadline (<see cref="Stopwatch.GetTimestamp"/> ticks; checked every 256 nodes).
    /// </summary>
    public LevelExtraction Extract(
        in View view,
        float tolerance,
        int member,
        int members,
        long nodeLimit,
        long deadline,
        int[] output
    ) =>
        _layout == NodeLayout.Packed
            ? Walk(_index.PackedReader(), view, tolerance, member, members, nodeLimit, deadline, output)
            : Walk(_index.SplitReader(), view, tolerance, member, members, nodeLimit, deadline, output);

    /// <summary>
    /// The largest screen error the last extraction can have, in pixels: the tolerance, or the largest bound of a node it
    /// left in the frontier. Infinity when such a node reaches the near plane. Not timed.
    /// </summary>
    public float AchievedPx(in View view, float tolerance)
    {
        var max = tolerance * tolerance;
        max = MathF.Max(max, Largest(view, _remainCurrent, _remainFrom, _remainEnd));
        max = MathF.Max(max, Largest(view, _remainNext, 0, _remainNextCount));
        return MathF.Sqrt(max);
    }

    private float Largest(in View view, Entry[] entries, int from, int end)
    {
        var data = _index.Data;
        var segments = _index.Segments;
        var log2 = _index.SegmentLog2;
        var max = 0f;
        for (var i = from; i < end; i++)
        {
            var entry = entries[i];
            var segment = segments[entry.A >> log2];
            var error = _index.RelativeError(entry.Slot) * (segment.Hi - segment.Lo);
            max = MathF.Max(max, view.Bound2(data[entry.A], data[entry.B], error));
        }

        return max;
    }

    private LevelExtraction Walk<TNodes>(
        TNodes nodes,
        in View view,
        float tolerance,
        int member,
        int members,
        long nodeLimit,
        long deadline,
        int[] output
    )
        where TNodes : struct, INodes
    {
        var data = _index.Data;
        var segments = _index.Segments;
        var log2 = _index.SegmentLog2;
        var startMask = ~((1 << log2) - 1);
        var tolerance2 = tolerance * tolerance;
        var kept = _kept;
        var current = _a;
        var next = _b;
        var keptCount = 0;
        var count = 0;

        // The roots: every owned segment's ends are kept; a culled segment's are not, and it breaks the line.
        for (var block = member; (block << BlockLog2) < segments.Length; block += members)
        {
            var blockEnd = Math.Min((block + 1) << BlockLog2, segments.Length);
            for (var s = block << BlockLog2; s < blockEnd; s++)
            {
                var segment = segments[s];
                var culled = view.Outside(segment.Lo, segment.Hi);
                _culled[s] = culled;
                if (culled)
                {
                    continue;
                }

                var v0 = _index.Start(s);
                var v1 = _index.End(s);
                kept[keptCount++] = v0;
                kept[keptCount++] = v1;
                if (v1 - v0 >= 2)
                {
                    current[count++] = new Entry(v0 + 1, v0, v1);
                }
            }
        }

        var visited = 0;
        var stop = StopReason.None;
        var at = 0;
        var nextCount = 0;
        while (count > 0)
        {
            nextCount = 0;
            for (at = 0; at < count; at++)
            {
                if (visited >= nodeLimit)
                {
                    stop = StopReason.Nodes;
                    break;
                }

                if ((visited & 255) == 0 && Stopwatch.GetTimestamp() >= deadline)
                {
                    stop = StopReason.Time;
                    break;
                }

                if (nextCount + 2 > next.Length || keptCount >= kept.Length)
                {
                    stop = StopReason.Capacity;
                    break;
                }

                visited++;
                var entry = current[at];
                var segment = segments[entry.A >> log2];
                var error = nodes.Error(entry.Slot) * (segment.Hi - segment.Lo);
                if (view.Prune(data[entry.A], data[entry.B], error, tolerance2))
                {
                    continue;
                }

                var m = (entry.A & startMask) + nodes.Offset(entry.Slot);
                kept[keptCount++] = m;
                if (m - entry.A >= 2)
                {
                    next[nextCount++] = new Entry(entry.Slot + 1, entry.A, m);
                }

                if (entry.B - m >= 2)
                {
                    next[nextCount++] = new Entry(entry.Slot + (m - entry.A), m, entry.B);
                }
            }

            if (stop != StopReason.None)
            {
                break;
            }

            (current, next) = (next, current);
            count = nextCount;
        }

        _remainCurrent = current;
        _remainNext = next;
        if (stop == StopReason.None)
        {
            _remainFrom = 0;
            _remainEnd = 0;
            _remainNextCount = 0;
        }
        else
        {
            _remainFrom = at;
            _remainEnd = count;
            _remainNextCount = nextCount;
        }

        var sorted = Sort(keptCount);
        var length = 0;
        var vertices = 0;
        var previous = -1;
        var last = segments.Length - 1;
        for (var k = 0; k < keptCount; k++)
        {
            var v = sorted[k];
            if (v == previous)
            {
                continue;
            }

            if (previous >= 0)
            {
                var s = Math.Min(previous >> log2, last);
                if (!Owns(s, member, members) || _culled[s] || v > _index.End(s))
                {
                    output[length++] = -1;
                }
            }

            output[length++] = v;
            vertices++;
            previous = v;
        }

        var remaining = stop == StopReason.None ? 0 : count - at + nextCount;
        return new LevelExtraction(vertices, length, visited, stop, remaining);
    }

    /// <summary>Sorts the first n kept vertices by two passes of 16 bits (indices are under 2^31); returns the sorted array.</summary>
    private int[] Sort(int n)
    {
        var from = _kept;
        var to = _sorted;
        var counts = _counts;
        for (var shift = 0; shift < 32; shift += 16)
        {
            Array.Clear(counts);
            for (var k = 0; k < n; k++)
            {
                counts[(from[k] >> shift) & 0xFFFF]++;
            }

            var sum = 0;
            for (var c = 0; c < counts.Length; c++)
            {
                var size = counts[c];
                counts[c] = sum;
                sum += size;
            }

            for (var k = 0; k < n; k++)
            {
                var v = from[k];
                to[counts[(v >> shift) & 0xFFFF]++] = v;
            }

            (from, to) = (to, from);
        }

        return from;
    }

    /// <summary>A node to visit: its slot in the node arrays (v0 + 1 + its preorder position) and its interval (A, B).</summary>
    private readonly struct Entry(int slot, int a, int b)
    {
        public readonly int Slot = slot;
        public readonly int A = a;
        public readonly int B = b;
    }
}
