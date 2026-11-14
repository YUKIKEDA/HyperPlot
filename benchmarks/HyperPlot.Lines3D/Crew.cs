using System.Diagnostics;

namespace HyperPlot.Lines3D;

/// <summary>The figures of one run of a variant, summed over its members.</summary>
internal readonly struct CrewResult
{
    public CrewResult(double ms, long vertices, long nodes, int stopped, int capped, long remaining, long bytes)
    {
        Ms = ms;
        Vertices = vertices;
        Nodes = nodes;
        Stopped = stopped;
        Capped = capped;
        Remaining = remaining;
        Bytes = bytes;
    }

    /// <summary>Gets the wall time from the start to the last member's end, the team's wake-up and wait included.</summary>
    public double Ms { get; }

    /// <summary>Gets the vertices written by all members; a vertex at the end of a member's run of segments counts in each.</summary>
    public long Vertices { get; }

    public long Nodes { get; }

    /// <summary>Gets the members that stopped by time or by nodes.</summary>
    public int Stopped { get; }

    /// <summary>Gets the members that filled their buffers.</summary>
    public int Capped { get; }

    /// <summary>Gets the nodes left in the frontiers, drawn as their chords.</summary>
    public long Remaining { get; }

    /// <summary>Gets the most bytes a member allocated in the run.</summary>
    public long Bytes { get; }
}

/// <summary>
/// The extractors, the buffers, and the team that run a <see cref="Variant"/> (SPIKE, row K0b, Issue #488 decision 2): dfs/T
/// takes blocks of segments in turn from a shared counter, each member writing into its own output; the level-order
/// variants deal the segments in fixed blocks (<see cref="LevelExtractor.Owns"/>). A run allocates nothing.
/// </summary>
internal sealed class Crew : IDisposable
{
    private readonly LineIndex _index;
    private readonly Team _team;
    private readonly Extractor[] _walkers;
    private readonly int[][] _walkOutputs;
    private readonly LevelExtractor[] _levelers;
    private readonly int[][] _levelOutputs;
    private readonly Member[] _members;
    private readonly Action<int> _walkJob;
    private readonly Action<int> _levelJob;
    private readonly int _block;
    private View _view;
    private float _tolerance;
    private int _nextBlock;
    private int _count;
    private long _nodeLimit;
    private long _deadline;

    /// <summary>Initializes a new instance of the <see cref="Crew"/> class: member 0 has room for capacity kept vertices, the others for a quarter.</summary>
    public Crew(LineIndex index, NodeLayout layout, int size, int capacity, int block)
    {
        _index = index;
        _block = block;
        _team = new Team(size);
        _walkers = new Extractor[size];
        _walkOutputs = new int[size][];
        _levelers = new LevelExtractor[size];
        _levelOutputs = new int[size][];
        _members = new Member[size];
        for (var m = 0; m < size; m++)
        {
            var room = m == 0 ? capacity : Math.Max(capacity / 4, 1024);
            _walkers[m] = new Extractor(index, layout);
            _walkOutputs[m] = new int[room + (2 * index.SegmentCount) + 16];
            _levelers[m] = new LevelExtractor(index, layout, room);
            _levelOutputs[m] = new int[2 * _levelers[m].Capacity];
        }

        _walkJob = WalkJob;
        _levelJob = LevelJob;
    }

    public int Size => _team.Size;

    public CrewResult Run(Variant variant, in View view, float tolerance)
    {
        var count = variant.Threads;
        _view = view;
        _tolerance = tolerance;
        _count = count;
        _nextBlock = -1;
        _nodeLimit = variant.LimitNodes > 0 ? Math.Max(1, variant.LimitNodes / count) : long.MaxValue;
        var start = Stopwatch.GetTimestamp();
        _deadline = variant.LimitMs > 0 ? start + (long)(variant.LimitMs * Stopwatch.Frequency / 1000) : long.MaxValue;
        _team.Run(variant.Levels ? _levelJob : _walkJob, count);
        var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        long vertices = 0;
        long nodes = 0;
        long remaining = 0;
        long bytes = 0;
        var stopped = 0;
        var capped = 0;
        for (var m = 0; m < count; m++)
        {
            var member = _members[m];
            vertices += member.Vertices;
            nodes += member.Nodes;
            remaining += member.Remaining;
            bytes = Math.Max(bytes, member.Bytes);
            stopped += member.Stop is StopReason.Nodes or StopReason.Time ? 1 : 0;
            capped += member.Stop == StopReason.Capacity ? 1 : 0;
        }

        return new CrewResult(ms, vertices, nodes, stopped, capped, remaining, bytes);
    }

    /// <summary>The largest screen error the last level-order run with this many members can have, in pixels. Not timed.</summary>
    public float AchievedPx(int count)
    {
        var max = _tolerance;
        for (var m = 0; m < count; m++)
        {
            max = MathF.Max(max, _levelers[m].AchievedPx(_view, _tolerance));
        }

        return max;
    }

    /// <summary>The entries a member wrote in the last run (vertex indices, -1 for a break).</summary>
    public ReadOnlySpan<int> Output(int member, bool levels) =>
        (levels ? _levelOutputs[member] : _walkOutputs[member]).AsSpan(0, _members[member].Length);

    public void Dispose() => _team.Dispose();

    private void WalkJob(int member)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var extractor = _walkers[member];
        var output = _walkOutputs[member];
        var segments = _index.SegmentCount;
        var blocks = (segments + _block - 1) / _block;
        var length = 0;
        var vertices = 0;
        var nodes = 0;
        var stop = StopReason.None;
        int b;
        while ((b = Interlocked.Increment(ref _nextBlock)) < blocks)
        {
            var first = b * _block;
            var end = Math.Min(first + _block, segments);
            var budget = output.Length - length - (end - first) - 8;
            if (budget <= 0)
            {
                stop = StopReason.Capacity;
                break;
            }

            var result = extractor.Extract(_view, _tolerance, budget, output, first, end, length);
            length = result.Length;
            vertices += result.Vertices;
            nodes += result.Nodes;
            if (result.Aborted)
            {
                stop = StopReason.Capacity;
                break;
            }
        }

        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        _members[member] = new Member(vertices, length, nodes, stop, 0, bytes);
    }

    private void LevelJob(int member)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = _levelers[member].Extract(
            _view,
            _tolerance,
            member,
            _count,
            _nodeLimit,
            _deadline,
            _levelOutputs[member]
        );
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        _members[member] = new Member(result.Vertices, result.Length, result.Nodes, result.Stop, result.Remaining, bytes);
    }

    /// <summary>A member's figures of the last run.</summary>
    private readonly struct Member(int vertices, int length, int nodes, StopReason stop, int remaining, long bytes)
    {
        public readonly int Vertices = vertices;
        public readonly int Length = length;
        public readonly int Nodes = nodes;
        public readonly StopReason Stop = stop;
        public readonly int Remaining = remaining;
        public readonly long Bytes = bytes;
    }
}
