namespace HyperPlot;

/// <summary>
/// One build of a <see cref="SpatialIndex"/> (design §3.4 "構築の段", "状態"): its rows cut into units, the groups that
/// hand the units to the workers, the work areas, and what the stages made so far.
/// </summary>
/// <remarks>
/// A job enters the build before it reads anything of it and leaves it at its end. A stopped build is freed when the last
/// job has left, so the job that fails a build does not free the counters under the unit of another worker.
/// </remarks>
internal sealed class SpatialBuild
{
    private const int Groups = SpatialIndexTuning.GroupCount;

    private readonly Group[] _groups = new Group[Groups];
    private long _remaining;
    private int _single;

    // The job that makes the build is in it from the start.
    private int _active = 1;
    private int _stopped;
    private int _freed;

    /// <summary>Initializes a new instance of the <see cref="SpatialBuild"/> class.</summary>
    /// <param name="sequence">The number of the build among the builds of the index.</param>
    public SpatialBuild(long sequence)
    {
        Sequence = sequence;
        for (var group = 0; group < Groups; group++)
        {
            _groups[group] = new Group();
        }
    }

    /// <summary>Gets the number of the build among the builds of the index: the upper bits of the state of the index while the build runs.</summary>
    public long Sequence { get; }

    /// <summary>Gets or sets the version of the x column when the build began.</summary>
    public long XVersion { get; set; }

    /// <summary>Gets or sets the version of the y column when the build began.</summary>
    public long YVersion { get; set; }

    /// <summary>Gets the first row the build reads.</summary>
    public long Start { get; private set; }

    /// <summary>Gets the row after the last row the build reads.</summary>
    public long End { get; private set; }

    /// <summary>Gets the number of units: the rows from <see cref="Start"/> to <see cref="End"/> cut at the multiples of the rows of a unit.</summary>
    public long UnitCount { get; private set; }

    /// <summary>Gets or sets the builder of the table, which the build owns.</summary>
    public SpatialTableBuilder? Builder { get; set; }

    /// <summary>Gets or sets the copy, which the build owns until the full snapshot takes it.</summary>
    public SpatialCopy? Copy { get; set; }

    /// <summary>Gets or sets the counts of levels 0 to 8, which the snapshots of the build share.</summary>
    public long[]? Coarse { get; set; }

    /// <summary>Gets or sets the range of x.</summary>
    public ScaleRange XRange { get; set; }

    /// <summary>Gets or sets the range of y.</summary>
    public ScaleRange YRange { get; set; }

    /// <summary>Gets or sets the quantization of x.</summary>
    public SpatialAxis XAxis { get; set; }

    /// <summary>Gets or sets the quantization of y.</summary>
    public SpatialAxis YAxis { get; set; }

    /// <summary>Gets what the units do now.</summary>
    public SpatialJob UnitJob { get; private set; }

    /// <summary>Gets the single job that follows the units, or that is queued now.</summary>
    public SpatialJob SingleJob { get; private set; }

    /// <summary>Gets a value indicating whether the build has ended, failed, or been withdrawn: no job starts in it any more.</summary>
    public bool IsStopped => Volatile.Read(ref _stopped) != 0;

    private int RowsPerUnit { get; set; }

    private long FirstUnit { get; set; }

    /// <summary>Cuts the rows into units and gives each group its run of units (design §3.4: group i has the units ⌊i × U / 8⌋ to ⌊(i + 1) × U / 8⌋ − 1).</summary>
    /// <param name="start">The first row.</param>
    /// <param name="end">The row after the last row: no less than <paramref name="start"/>.</param>
    /// <param name="rowsPerUnit">The rows of a unit: a power of two no greater than a chunk.</param>
    public void Lay(long start, long end, int rowsPerUnit)
    {
        Start = start;
        End = end;
        RowsPerUnit = rowsPerUnit;
        FirstUnit = start / rowsPerUnit;
        UnitCount = end > start ? ((end - 1) / rowsPerUnit) - FirstUnit + 1 : 0;
        for (var group = 0; group < Groups; group++)
        {
            var cursor = _groups[group];
            cursor.Begin = (long)((Int128)group * UnitCount / Groups);
            cursor.End = (long)((Int128)(group + 1) * UnitCount / Groups);

            // Nothing is taken until a stage begins.
            cursor.Next = cursor.End;
        }
    }

    /// <summary>Allocates the work areas of the groups that have a unit: the codes, and the transformed values of each axis with a transform.</summary>
    /// <param name="x">Whether x has a transform.</param>
    /// <param name="y">Whether y has a transform.</param>
    public void AllocateScratch(bool x, bool y)
    {
        var length = (int)Math.Min(RowsPerUnit, End - Start);
        foreach (var group in _groups)
        {
            if (group.End > group.Begin)
            {
                group.Codes = new ulong[length];
                group.X = x ? new double[length] : null;
                group.Y = y ? new double[length] : null;
            }
        }
    }

    /// <summary>Returns the rows of a unit.</summary>
    /// <param name="unit">The unit, from 0.</param>
    /// <returns>The first row and the number of rows, which lie in one chunk.</returns>
    public (long First, int Count) RowsOf(long unit)
    {
        var first = Math.Max(Start, (FirstUnit + unit) * RowsPerUnit);
        var last = Math.Min(End, (FirstUnit + unit + 1) * RowsPerUnit);
        return (first, (int)(last - first));
    }

    /// <summary>Returns the work area of a group for codes.</summary>
    /// <param name="group">The group.</param>
    /// <returns>Room for the codes of one unit.</returns>
    public ulong[] Codes(int group) => _groups[group].Codes;

    /// <summary>Returns the work area of a group for the transformed values of an axis.</summary>
    /// <param name="group">The group.</param>
    /// <param name="axis">The axis: 0 for x and 1 for y.</param>
    /// <returns>Room for the values of one unit. The axis has a transform.</returns>
    public double[] Transformed(int group, int axis) => (axis == 0 ? _groups[group].X : _groups[group].Y)!;

    /// <summary>Returns what a group has seen of an axis in the range stage.</summary>
    /// <param name="group">The group.</param>
    /// <param name="axis">The axis: 0 for x and 1 for y.</param>
    /// <returns>The extent of the group, which only the worker that has the group writes.</returns>
    public ref Extent ExtentOf(int group, int axis)
    {
        var cursor = _groups[group];
        if (axis == 0)
        {
            return ref cursor.XExtent;
        }

        return ref cursor.YExtent;
    }

    /// <summary>Joins what the groups have seen of an axis.</summary>
    /// <param name="axis">The axis: 0 for x and 1 for y.</param>
    /// <returns>The extent of every unit.</returns>
    public Extent JoinExtents(int axis)
    {
        var joined = Extent.Empty;
        for (var group = 0; group < Groups; group++)
        {
            joined.Join(ExtentOf(group, axis));
        }

        return joined;
    }

    /// <summary>Enters the build, as every job does before it reads the build.</summary>
    /// <returns><see langword="false"/> when the build is stopped: the caller has left it again and does nothing.</returns>
    public bool Enter()
    {
        Interlocked.Increment(ref _active);
        if (IsStopped)
        {
            Leave();
            return false;
        }

        return true;
    }

    /// <summary>Leaves the build. The last job to leave a stopped build frees it.</summary>
    public void Leave()
    {
        if (Interlocked.Decrement(ref _active) == 0 && IsStopped)
        {
            Free();
        }
    }

    /// <summary>Stops the build: no job starts in it any more.</summary>
    /// <returns>Whether this call stopped it.</returns>
    public bool Stop() => Interlocked.Exchange(ref _stopped, 1) == 0;

    /// <summary>Frees the builder and the copy. Only the first call does it. No job is in the build.</summary>
    public void Free()
    {
        if (Interlocked.Exchange(ref _freed, 1) != 0)
        {
            return;
        }

        Builder?.Dispose();
        Copy?.Dispose();
    }

    /// <summary>
    /// Begins a stage of units, or, when the build has no unit, queues the single job that follows the stage (design §3.4
    /// rule 1). The caller is the one job that runs in the build, and this is the last thing it does to the build.
    /// </summary>
    /// <param name="units">What the units do.</param>
    /// <param name="next">The single job that follows the last unit.</param>
    public void BeginUnits(SpatialJob units, SpatialJob next)
    {
        UnitJob = units;
        SingleJob = next;
        if (UnitCount == 0)
        {
            Volatile.Write(ref _single, 1);
            return;
        }

        // The count is there before a group has a unit to take, so that no unit ends before it.
        Volatile.Write(ref _remaining, UnitCount);
        foreach (var group in _groups)
        {
            Volatile.Write(ref group.Next, group.Begin);
        }
    }

    /// <summary>Queues a single job. The caller is the one job that runs in the build, and this is the last thing it does to the build.</summary>
    /// <param name="job">The single job.</param>
    public void QueueSingle(SpatialJob job)
    {
        SingleJob = job;
        Volatile.Write(ref _single, 1);
    }

    /// <summary>Takes the queued single job.</summary>
    /// <returns>Whether the caller has it.</returns>
    public bool TryTakeSingle() => Volatile.Read(ref _single) != 0 && Interlocked.CompareExchange(ref _single, 0, 1) == 1;

    /// <summary>Takes a group that has a unit left and that no worker has (design §3.4: one worker at a time runs the units of a group, in the order of the rows).</summary>
    /// <returns>The group, or −1.</returns>
    public int TryTakeGroup()
    {
        for (var group = 0; group < Groups; group++)
        {
            var cursor = _groups[group];
            if (Volatile.Read(ref cursor.Next) < cursor.End && Interlocked.CompareExchange(ref cursor.Busy, 1, 0) == 0)
            {
                // Another worker may have run the last unit of the group between the read and the flag.
                if (Volatile.Read(ref cursor.Next) < cursor.End)
                {
                    return group;
                }

                Volatile.Write(ref cursor.Busy, 0);
            }
        }

        return -1;
    }

    /// <summary>Returns the next unit of a group the caller has taken.</summary>
    /// <param name="group">The group.</param>
    /// <returns>The unit.</returns>
    public long NextUnit(int group) => Volatile.Read(ref _groups[group].Next);

    /// <summary>Ends the unit of a group the caller has taken, gives the group back, and queues the single job after the last unit of the stage.</summary>
    /// <param name="group">The group.</param>
    /// <returns>Whether it was the last unit of the stage.</returns>
    public bool EndUnit(int group)
    {
        var cursor = _groups[group];
        Volatile.Write(ref cursor.Next, cursor.Next + 1);
        Volatile.Write(ref cursor.Busy, 0);
        if (Interlocked.Decrement(ref _remaining) != 0)
        {
            return false;
        }

        Volatile.Write(ref _single, 1);
        return true;
    }

    /// <summary>The least and the greatest finite value that the range stage has seen of one axis.</summary>
    internal struct Extent
    {
        /// <summary>The least value of an axis that holds integers and has no transform.</summary>
        public long Low;

        /// <summary>The greatest value of an axis that holds integers and has no transform.</summary>
        public long High;

        /// <summary>The least value of any other axis.</summary>
        public double Least;

        /// <summary>The greatest value of any other axis.</summary>
        public double Greatest;

        /// <summary>Gets the extent of no value.</summary>
        public static Extent Empty =>
            new()
            {
                Low = long.MaxValue,
                High = long.MinValue,
                Least = double.PositiveInfinity,
                Greatest = double.NegativeInfinity,
            };

        /// <summary>Gets a value indicating whether a finite value was seen.</summary>
        public readonly bool HasValue => Low <= High || Least <= Greatest;

        /// <summary>Takes in another extent.</summary>
        /// <param name="other">The other extent.</param>
        public void Join(in Extent other)
        {
            Low = Math.Min(Low, other.Low);
            High = Math.Max(High, other.High);
            Least = Math.Min(Least, other.Least);
            Greatest = Math.Max(Greatest, other.Greatest);
        }
    }

    private sealed class Group
    {
        public long Begin;
        public long End;
        public long Next;
        public int Busy;
        public ulong[] Codes = [];
        public double[]? X;
        public double[]? Y;
        public Extent XExtent = Extent.Empty;
        public Extent YExtent = Extent.Empty;
    }
}
