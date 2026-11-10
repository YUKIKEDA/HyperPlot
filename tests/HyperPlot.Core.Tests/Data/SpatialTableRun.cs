namespace HyperPlot.Tests;

/// <summary>
/// A table builder of a spatial tile index driven by hand the way the units and the single jobs of a build drive it,
/// with a copy of one region of <see cref="double"/> an axis, and the check of the table and the copy against plain
/// searches of the codes (design §10: brute force).
/// </summary>
internal sealed class SpatialTableRun : IDisposable
{
    private const int Groups = SpatialIndexTuning.GroupCount;

    private readonly SpatialLayout _layout;
    private readonly SpatialIndexTuning _tuning;
    private readonly ulong[] _codes;
    private readonly double[][] _values;
    private readonly List<int> _rowsAfterPass = [];

    private SpatialTableRun(SpatialLayout layout, SpatialIndexTuning tuning, ulong[] codes, double[][] values, long maxTableRows, int? shuffle)
    {
        _layout = layout;
        _tuning = tuning;
        _codes = codes;
        _values = values;
        var builder = new SpatialTableBuilder(layout, tuning, codes.Length, maxTableRows);
        Builder = builder;
        FirstBytes = builder.FirstBytes;

        Visit(shuffle, (group, first, count) => builder.CountFirst(group, codes.AsSpan(first, count), 0));
        var more = builder.EndFirst();
        FirstPassBytes = builder.PassBytes;
        Record();
        while (more)
        {
            Visit(shuffle, (group, first, count) => builder.CountPass(group, codes.AsSpan(first, count)));
            more = builder.EndPass();
            Record();
        }

        builder.BuildTable();
        SlotBytes = builder.SlotBytes;
        var copy = codes.Length > 0 ? SpatialCopy.Allocate(codes.Length, values.Select(_ => sizeof(double)).ToArray()) : null;
        Copy = copy;
        var places = (ulong[])codes.Clone();
        Visit(
            shuffle,
            (group, first, count) =>
            {
                var unit = places.AsSpan(first, count);
                builder.Place(group, unit);
                for (var axis = 0; axis < values.Length; axis++)
                {
                    var region = copy!.GetRegion<double>(axis);
                    for (var i = 0; i < unit.Length; i++)
                    {
                        region.TryWrite(unit[i], values[axis][first + i]);
                    }
                }

                var positions = copy!.GetRegion<uint>(values.Length);
                for (var i = 0; i < unit.Length; i++)
                {
                    positions.TryWrite(unit[i], (uint)(first + i));
                }
            }
        );
        builder.FinishWrite(copy);
        Rows = builder.Rows().ToArray();
    }

    /// <summary>Gets the builder, after the end of the writing.</summary>
    public SpatialTableBuilder Builder { get; }

    /// <summary>Gets the copy, or <see langword="null"/> when the run has no points.</summary>
    public SpatialCopy? Copy { get; }

    /// <summary>Gets the rows of the table after the end of the writing.</summary>
    public SpatialRow[] Rows { get; }

    /// <summary>Gets the number of rows of the table after the single job of each pass, the first count first.</summary>
    public IReadOnlyList<int> RowsAfterPass => _rowsAfterPass;

    /// <summary>Gets the bytes of the first counters before the first count.</summary>
    public long FirstBytes { get; }

    /// <summary>Gets the bytes of the counters and the extents of the pass after the first count.</summary>
    public long FirstPassBytes { get; }

    /// <summary>Gets the most bytes that the counters and the extents of one pass took.</summary>
    public long MaxPassBytes { get; private set; }

    /// <summary>Gets the most bytes that the records of the open cells and the lists of rows took.</summary>
    public long MaxOpenBytes { get; private set; }

    /// <summary>Gets the bytes of the group counts of the leaves when the table was built.</summary>
    public long SlotBytes { get; }

    /// <summary>
    /// Runs the stages of a build over the codes of the points in the order of their rows. The value of a point on an
    /// axis is its row times the axis's number plus one, unless <paramref name="values"/> gives the values.
    /// </summary>
    /// <param name="layout">The numbers of the number of axes.</param>
    /// <param name="codes">The codes of the points in the order of their rows.</param>
    /// <param name="tuning">The constants of the build.</param>
    /// <param name="maxTableRows">The most rows of the table.</param>
    /// <param name="values">The values of the points, one array an axis.</param>
    /// <param name="shuffle">
    /// The seed of the order in which the groups take their units. Without it, each group runs all its units before
    /// the next group starts.
    /// </param>
    public static SpatialTableRun Of(
        SpatialLayout layout,
        ulong[] codes,
        SpatialIndexTuning tuning,
        long maxTableRows = long.MaxValue,
        double[][]? values = null,
        int? shuffle = null
    )
    {
        values ??= Enumerable.Range(0, layout.Dimensions).Select(axis => codes.Select((_, row) => (double)row * (axis + 1)).ToArray()).ToArray();
        return new SpatialTableRun(layout, tuning, codes, values, maxTableRows, shuffle);
    }

    /// <summary>Reads the values of one axis from the copy, in the order of the copy.</summary>
    public double[] Values(int axis) => Copy is { } copy ? copy.GetRegion<double>(axis).Read(0, _codes.Length).ToArray() : [];

    /// <summary>Reads the rows of the points from the copy, in the order of the copy.</summary>
    public uint[] Positions() => Copy is { } copy ? copy.GetRegion<uint>(_values.Length).Read(0, _codes.Length).ToArray() : [];

    /// <summary>
    /// Checks the table and the copy against the codes: the copy holds each point once with the values of its row, and
    /// every row of the table is reached once from the root with the cell, the count, and the range of the brute force.
    /// </summary>
    /// <param name="capped">Whether the cap on the rows may have stopped a split.</param>
    /// <returns>The leaves in the order of their codes.</returns>
    public SpatialRow[] Check(bool capped = false)
    {
        var count = _codes.Length;
        Assert.Equal(count, Builder.CountedPoints);
        Assert.Equal(count, Builder.PointCount);
        Assert.Equal(0, Builder.ExcludedCount);
        Assert.False(Builder.Disagreed);
        Assert.Equal(capped, Builder.IsCapped);
        Assert.Equal(Rows.Length, Builder.RowCount);

        var positions = Positions();
        var seen = new bool[count];
        foreach (var position in positions)
        {
            if (position >= count || seen[position])
            {
                Assert.Fail($"The copy holds row {position} twice, or it is not a row.");
            }

            seen[position] = true;
        }

        for (var axis = 0; axis < _values.Length; axis++)
        {
            var copied = Values(axis);
            for (var i = 0; i < count; i++)
            {
                if (copied[i] != _values[axis][positions[i]])
                {
                    Assert.Fail($"The point at {i} of the copy holds {copied[i]:R} on axis {axis}, and its row {positions[i]} holds another value.");
                }
            }
        }

        var sorted = (ulong[])_codes.Clone();
        Array.Sort(sorted);
        var leaves = new List<SpatialRow>();
        long next = 0;
        var visited = 0;
        Walk(0, 0, 0, sorted, positions, capped, leaves, ref next, ref visited);
        Assert.Equal(count, next);
        Assert.Equal(Rows.Length, visited);
        return [.. leaves];
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Builder.Dispose();
        Copy?.Dispose();
    }

    private void Record()
    {
        _rowsAfterPass.Add(Builder.RowCount);
        MaxPassBytes = Math.Max(MaxPassBytes, Builder.PassBytes);
        MaxOpenBytes = Math.Max(MaxOpenBytes, Builder.OpenBytes);
    }

    // Group g has the units from g × U / 8 up to (g + 1) × U / 8 of the U units, and takes them in the order of the rows.
    private void Visit(int? shuffle, Action<int, int, int> run)
    {
        var unit = _tuning.RowsPerUnit;
        var units = (int)(((long)_codes.Length + unit - 1) / unit);
        var next = new int[Groups];
        var end = new int[Groups];
        for (var group = 0; group < Groups; group++)
        {
            next[group] = (int)((long)group * units / Groups);
            end[group] = (int)((long)(group + 1) * units / Groups);
        }

        var random = shuffle is { } seed ? new Random(seed) : null;
        for (var left = units; left > 0; left--)
        {
            var group = 0;
            if (random == null)
            {
                while (next[group] == end[group])
                {
                    group++;
                }
            }
            else
            {
                group = random.Next(Groups);
                while (next[group] == end[group])
                {
                    group = random.Next(Groups);
                }
            }

            var first = next[group]++ * unit;
            run(group, first, Math.Min(unit, _codes.Length - first));
        }
    }

    private ulong CellAt(ulong code, int level) => level == 0 ? 0 : code >> (_layout.TotalBits - (_layout.Dimensions * level));

    // The number of codes in a cell, by two searches of the sorted codes: the cell of a code grows with the code.
    private int CountOf(ulong[] sorted, ulong cell, int level)
    {
        var low = 0;
        var high = sorted.Length;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (CellAt(sorted[middle], level) < cell)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        var first = low;
        high = sorted.Length;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (CellAt(sorted[middle], level) <= cell)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low - first;
    }

    private void Walk(
        int index,
        ulong cell,
        int level,
        ulong[] sorted,
        uint[] positions,
        bool capped,
        List<SpatialRow> leaves,
        ref long next,
        ref int visited
    )
    {
        var row = Rows[index];
        visited++;
        var expected = CountOf(sorted, cell, level);
        if (row.Code != cell || row.Level != level || row.Reserved != 0 || row.First != next || row.Count != expected)
        {
            Assert.Fail(
                $"Row {index} is the cell {row.Code} of level {row.Level} with {row.Count} points from {row.First}: "
                    + $"expected the cell {cell} of level {level} with {expected} points from {next}."
            );
        }

        var capacity = _tuning.LeafCapacity;
        if (row.Kind == (byte)SpatialCellKind.Inner)
        {
            Assert.True(row.Count > capacity, $"Row {index} is split with {row.Count} points.");
            Assert.True(level < _layout.BitsPerAxis, $"Row {index} is split at the deepest level.");
            Assert.True(
                row.FirstChild > index && row.FirstChild + _layout.ChildCount <= Rows.Length,
                $"The children of row {index} are not rows after it."
            );
            for (var child = 0; child < _layout.ChildCount; child++)
            {
                Walk(
                    row.FirstChild + child,
                    (cell << _layout.Dimensions) | (uint)child,
                    level + 1,
                    sorted,
                    positions,
                    capped,
                    leaves,
                    ref next,
                    ref visited
                );
            }

            Assert.Equal(row.First + row.Count, next);
            return;
        }

        Assert.Equal(-1, row.FirstChild);
        var first = (int)row.First;
        var last = first + (int)row.Count;
        for (var i = first; i < last; i++)
        {
            if (CellAt(_codes[positions[i]], level) != cell)
            {
                Assert.Fail($"The point at {i} of the copy is not in the cell {cell} of level {level}.");
            }

            if (i > first && positions[i - 1] >= positions[i])
            {
                Assert.Fail($"The points of row {index} are not in the order of their rows at {i}.");
            }
        }

        switch ((SpatialCellKind)row.Kind)
        {
            case SpatialCellKind.Leaf:
                Assert.True(row.Count <= capacity, $"The leaf of row {index} holds {row.Count} points.");
                break;
            case SpatialCellKind.CoincidentLeaf:
                Assert.True(row.Count > capacity, $"The leaf of row {index} is within the capacity and marked coincident.");
                for (var i = first + 1; i < last; i++)
                {
                    if (_codes[positions[i]] != _codes[positions[first]])
                    {
                        Assert.Fail($"The coincident leaf of row {index} holds two codes.");
                    }
                }

                break;
            case SpatialCellKind.CappedLeaf:
                Assert.True(capped, $"The leaf of row {index} is capped though the table has room.");
                Assert.True(row.Count > capacity, $"The leaf of row {index} is within the capacity and marked capped.");
                break;
            default:
                Assert.Fail($"Row {index} has the kind {row.Kind} after the end of the writing.");
                break;
        }

        next += row.Count;
        leaves.Add(row);
    }
}
