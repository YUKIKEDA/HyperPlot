using System.Numerics;
using System.Runtime.InteropServices;

namespace HyperPlot;

/// <summary>
/// Builds the table of a spatial tile index from the codes of its points (design §3.4 "構築の段"): the first count at
/// the layout's first level, counting passes that go down where a cell still holds more than the leaf capacity, the
/// table, and the place of each point in the copy. It sees codes only, so it is the same for two axes and for three
/// (design §13).
/// </summary>
/// <remarks>
/// The units of a stage call <see cref="CountFirst"/>, <see cref="CountPass"/>, or <see cref="Place"/> with their group:
/// a group is one worker at a time, and two groups never touch the same counter. The single jobs between the stages
/// (<see cref="EndFirst"/>, <see cref="EndPass"/>, <see cref="BuildTable"/>, <see cref="FinishWrite"/>) run alone. A
/// group's points take the places after the earlier groups' points in each leaf, in the order the group passed them, so
/// the table and the copy do not depend on which worker ran which unit.
/// </remarks>
internal sealed unsafe class SpatialTableBuilder : IDisposable
{
    private const int Groups = SpatialIndexTuning.GroupCount;

    // Two kinds that only a table under construction has. An open cell waits for the next counting pass, and its
    // FirstChild is its place among the open cells. A pending cell is being split by the single job, and its FirstChild is
    // the open cell whose counters cover it. A leaf's FirstChild is its slot of group counts until FinishWrite.
    private const byte OpenKind = 4;
    private const byte PendingKind = 5;

    // The slot of every leaf without points: its groups have no room, so a point that turns up there is dropped.
    private const int EmptySlot = 0;

    // A group's totals are 8 longs apart so that two groups do not share a cache line.
    private const int Stride = 8;

    // A leaf's slot has two entries a group: while the cells are split, the group's count of the leaf's points and 0;
    // from BuildTable on, the next free position of the group's part of the leaf and the end of that part.
    private const int SlotEntries = 2 * Groups;

    private readonly SpatialLayout _layout;
    private readonly int _leafCapacity;
    private readonly long _capacity;
    private readonly int _maxRows;
    private readonly bool _wide;
    private readonly int _firstShift;
    private readonly ulong _childMask;
    private readonly FirstCounter*[] _first = new FirstCounter*[Groups];
    private readonly void*[] _counters = new void*[Groups];
    private readonly ulong*[] _extents = new ulong*[Groups];
    private readonly long[] _excluded = new long[Groups * Stride];
    private readonly long[] _dropped = new long[Groups * Stride];

    private SpatialRow* _rows;
    private long _rowCapacity;
    private int _rowCount;
    private int* _lookup;
    private void* _slots;
    private long _slotCapacity;
    private int _slotCount;
    private OpenCell* _open;
    private long _openCapacity;
    private int _openCount;
    private OpenCell* _nextOpen;
    private long _nextOpenCapacity;
    private long _counterCapacity;
    private int _counterCount;
    private long _extentCapacity;
    private IntList _frontier;
    private IntList _nextFrontier;
    private IntList _candidates;
    private int _pass;
    private bool _capped;
    private bool _disagreed;

    /// <summary>Initializes a new instance of the <see cref="SpatialTableBuilder"/> class and allocates the first counters.</summary>
    /// <param name="layout">The numbers of the index's number of axes.</param>
    /// <param name="tuning">The constants of the build.</param>
    /// <param name="capacity">The number of rows the index reads, n: the copy has room for this many points.</param>
    /// <param name="maxTableRows">The most rows the table may have.</param>
    internal SpatialTableBuilder(SpatialLayout layout, SpatialIndexTuning tuning, long capacity, long maxTableRows)
    {
        _layout = layout;
        _leafCapacity = tuning.LeafCapacity;
        _capacity = capacity;

        // A count of 2^32 does not fit a 4-byte counter, so the wide counters start at that many rows.
        _wide = capacity >= tuning.WideRows;
        _maxRows = (int)Math.Clamp(maxTableRows, 1, int.MaxValue - layout.ChildCount);
        _firstShift = layout.BitsBelow(layout.FirstLevel);
        _childMask = (ulong)layout.ChildCount - 1;
        try
        {
            var cells = layout.FirstCellCount;
            for (var group = 0; group < Groups; group++)
            {
                _first[group] = (FirstCounter*)NativeMemory.AlignedAlloc((nuint)cells * (nuint)sizeof(FirstCounter), 64);
                First(group).Fill(new FirstCounter { Min = ulong.MaxValue });
            }

            _lookup = (int*)NativeMemory.AlignedAlloc((nuint)cells * sizeof(int), 64);
            Reserve(ref _rows, ref _rowCapacity, Math.Min(_maxRows, 1024));
            ReserveSlots(1);
            _slotCount = 1;
            if (_wide)
            {
                SlotOf<long>(EmptySlot).Clear();
            }
            else
            {
                SlotOf<uint>(EmptySlot).Clear();
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Gets the number of points the first count saw: the points whose stored values are all finite.</summary>
    internal long CountedPoints { get; private set; }

    /// <summary>Gets the number of rows the first count skipped because a stored value was not finite.</summary>
    internal long ExcludedCount { get; private set; }

    /// <summary>Gets the number of points of the table. <see cref="BuildTable"/> sets it, and <see cref="FinishWrite"/> may clear it.</summary>
    internal long PointCount { get; private set; }

    /// <summary>Gets the number of rows of the table so far.</summary>
    internal int RowCount => _rowCount;

    /// <summary>Gets the number of counting passes so far, the first count too.</summary>
    internal int PassCount => _pass;

    /// <summary>Gets a value indicating whether the cap on the table's rows kept a cell from being split.</summary>
    internal bool IsCapped => _capped;

    /// <summary>
    /// Gets a value indicating whether the stages saw different points: a leaf was clipped, a point was dropped, or a
    /// place was filled. Only a column that changes under the build makes it so.
    /// </summary>
    internal bool Disagreed => _disagreed;

    /// <summary>Gets the bytes of the counters and the extents of the current counting pass, all groups together.</summary>
    internal long PassBytes => Groups * (((long)_counterCount * CounterSize) + (_openCount * 2L * sizeof(ulong)));

    /// <summary>Gets the bytes of the group counts of the leaves.</summary>
    internal long SlotBytes => (long)_slotCount * SlotEntries * CounterSize;

    /// <summary>Gets the bytes of the first counters, all groups together, and 0 once the counting has ended.</summary>
    internal long FirstBytes => _first[0] == null ? 0 : (long)Groups * _layout.FirstCellCount * sizeof(FirstCounter);

    /// <summary>
    /// Gets the bytes of the blocks that hold the records of the open cells, of this pass and of the next one, and the
    /// lists of rows that the single job after a pass goes through.
    /// </summary>
    internal long OpenBytes =>
        ((_openCapacity + _nextOpenCapacity) * sizeof(OpenCell))
        + ((_frontier.Capacity + _nextFrontier.Capacity + _candidates.Capacity) * sizeof(int));

    private int CounterSize => _wide ? sizeof(long) : sizeof(uint);

    // The views below are how the builder reads and writes its blocks: an index is checked against the block's size
    // (.cursor/rules/interop.mdc). Each block is at least as long as its view, for the count beside it grows only after
    // Reserve has made the room, and a freed block's view is empty. The blocks live until Dispose or until the stage that
    // frees them, and the index runs no unit of a builder after either.
    private Span<SpatialRow> Table => new(_rows, _rowCount);

    private ReadOnlySpan<int> Lookup => new(_lookup, _lookup == null ? 0 : _layout.FirstCellCount);

    private Span<OpenCell> Open => new(_open, _openCount);

    /// <summary>Counts one unit's points in the first counters of a group.</summary>
    /// <param name="group">The group of the unit.</param>
    /// <param name="codes">The codes of the unit's points with finite values.</param>
    /// <param name="excluded">The number of the unit's rows that were skipped.</param>
    internal void CountFirst(int group, ReadOnlySpan<ulong> codes, int excluded)
    {
        var counters = First(group);
        var shift = _firstShift;
        foreach (var code in codes)
        {
            // A code has no bit above the layout's total, so the shifted code is a cell of the first level.
            ref var counter = ref counters[(int)(code >> shift)];
            counter.Count++;
            if (code < counter.Min)
            {
                counter.Min = code;
            }

            if (code > counter.Max)
            {
                counter.Max = code;
            }
        }

        _excluded[group * Stride] += excluded;
    }

    /// <summary>Counts one unit's points in a group's counters of the open cells, and keeps each open cell's least and greatest code.</summary>
    /// <param name="group">The group of the unit.</param>
    /// <param name="codes">The codes of the unit's points with finite values.</param>
    internal void CountPass(int group, ReadOnlySpan<ulong> codes)
    {
        if (_wide)
        {
            CountPass(Counters<long>(group), Extents(group), codes);
        }
        else
        {
            CountPass(Counters<uint>(group), Extents(group), codes);
        }
    }

    /// <summary>
    /// Replaces each code of one unit with the position of its point in the copy, or with <see cref="ulong.MaxValue"/>
    /// when the group's part of the leaf is full and the point is dropped.
    /// </summary>
    /// <param name="group">The group of the unit.</param>
    /// <param name="codes">The codes of the unit's points with finite values, in the order of the rows.</param>
    internal void Place(int group, Span<ulong> codes)
    {
        _dropped[group * Stride] += _wide ? Place<long>(group, codes) : Place<uint>(group, codes);
    }

    /// <summary>
    /// Ends the first count: totals the groups, makes the rows of the levels down to the first level, and opens the cells
    /// of the first level that still hold more than the leaf capacity.
    /// </summary>
    /// <returns>Whether a counting pass has to follow.</returns>
    internal bool EndFirst()
    {
        var cells = _layout.FirstCellCount;
        long excluded = 0;
        for (var group = 0; group < Groups; group++)
        {
            excluded += _excluded[group * Stride];
        }

        CountedPoints = Sum(0, cells);
        ExcludedCount = excluded;

        // The first counters are the counters of one cell that is open at the root and goes down to the first level.
        _rowCount = 1;
        Table[0] = new SpatialRow { Kind = PendingKind };
        Reserve(ref _open, ref _openCapacity, 1);
        _openCount = 1;
        Open[0] = new OpenCell { Levels = (byte)_layout.FirstLevel, Mask = (ulong)cells - 1 };
        var more = Resolve();

        ReadOnlySpan<SpatialRow> rows = Table;
        var lookup = new Span<int>(_lookup, cells);
        for (var cell = 0; cell < cells; cell++)
        {
            var row = 0;
            var level = 0;
            while (rows[row].Kind == (byte)SpatialCellKind.Inner && level < _layout.FirstLevel)
            {
                level++;
                var child = (int)(((ulong)cell >> (_layout.Dimensions * (_layout.FirstLevel - level))) & _childMask);
                row = rows[row].FirstChild + child;
            }

            lookup[cell] = row;
        }

        return more;
    }

    /// <summary>Ends a counting pass: splits the open cells by their counters, and opens the cells that still hold too many points.</summary>
    /// <returns>Whether another counting pass has to follow.</returns>
    internal bool EndPass() => Resolve();

    /// <summary>
    /// Ends the counting: gives each leaf its range of the copy in the order of the codes, each split cell the joined
    /// range of its children, and each group its part of each leaf. A range that would pass the copy's end is clipped.
    /// </summary>
    internal void BuildTable()
    {
        long position = 0;
        Number(Table, 0, ref position);
        PointCount = position;
        if (position != CountedPoints)
        {
            _disagreed = true;
        }

        FreeCounting();
    }

    /// <summary>
    /// Ends the writing: a place that no point took gets the point before it (the places before the first written point
    /// get that point), and a table none of whose places was taken becomes the table of no points.
    /// </summary>
    /// <param name="copy">The copy the points were written to, or <see langword="null"/> when the index reads no rows.</param>
    internal void FinishWrite(SpatialCopy? copy)
    {
        for (var group = 0; group < Groups; group++)
        {
            if (_dropped[group * Stride] != 0)
            {
                _disagreed = true;
            }
        }

        long firstWritten = -1;
        Fill(Table, 0, copy, ref firstWritten);
        if (firstWritten < 0 && PointCount > 0)
        {
            _rowCount = 1;
            Table[0] = new SpatialRow { Kind = (byte)SpatialCellKind.Leaf, FirstChild = -1 };
            PointCount = 0;
            _disagreed = true;
        }

        NativeMemory.AlignedFree(_slots);
        _slots = null;
        _slotCount = 0;
        NativeMemory.AlignedFree(_lookup);
        _lookup = null;
    }

    /// <summary>Hands the table over. The caller frees it with <see cref="NativeMemory.AlignedFree"/>.</summary>
    /// <param name="rowCount">The number of rows of the table.</param>
    /// <returns>The rows.</returns>
    internal SpatialRow* TakeTable(out int rowCount)
    {
        rowCount = _rowCount;
        var rows = (SpatialRow*)NativeMemory.AlignedRealloc(_rows, (nuint)rowCount * (nuint)sizeof(SpatialRow), 64);
        _rows = null;
        _rowCount = 0;
        return rows;
    }

    /// <summary>Returns the points that the first count saw in one cell of the first level, all groups together.</summary>
    /// <param name="cell">The cell's number at the first level.</param>
    /// <returns>The number of points.</returns>
    internal long FirstCount(int cell)
    {
        long count = 0;
        for (var group = 0; group < Groups; group++)
        {
            count += First(group)[cell].Count;
        }

        return count;
    }

    /// <summary>Returns a view of the table under construction, for the tests.</summary>
    /// <returns>The rows so far.</returns>
    internal ReadOnlySpan<SpatialRow> Rows() => Table;

    /// <inheritdoc />
    public void Dispose()
    {
        FreeCounting();
        NativeMemory.AlignedFree(_rows);
        _rows = null;
        _rowCount = 0;
        NativeMemory.AlignedFree(_lookup);
        _lookup = null;
        NativeMemory.AlignedFree(_slots);
        _slots = null;
        _slotCount = 0;
    }

    private static void Reserve<T>(ref T* block, ref long capacity, long count)
        where T : unmanaged
    {
        if (count <= capacity)
        {
            return;
        }

        var grown = Math.Max(count, capacity * 2);
        block = (T*)NativeMemory.AlignedRealloc(block, checked((nuint)grown * (nuint)sizeof(T)), 64);
        capacity = grown;
    }

    private void ReserveSlots(int count)
    {
        if (count <= _slotCapacity)
        {
            return;
        }

        var grown = Math.Max(count, _slotCapacity * 2);
        _slots = NativeMemory.AlignedRealloc(_slots, checked((nuint)grown * (nuint)(SlotEntries * CounterSize)), 64);
        _slotCapacity = grown;
    }

    private Span<FirstCounter> First(int group) => new(_first[group], _first[group] == null ? 0 : _layout.FirstCellCount);

    // T is the width of the counters: uint, or long when the index reads 2^32 rows or more.
    private Span<T> Counters<T>(int group)
        where T : unmanaged => new(_counters[group], _counterCount);

    private Span<ulong> Extents(int group) => new(_extents[group], 2 * _openCount);

    private Span<T> SlotOf<T>(int slot)
        where T : unmanaged
    {
        if ((uint)slot >= (uint)_slotCount)
        {
            ThrowHelper.ThrowIndexOutOfBlock(slot, _slotCount);
        }

        // The slots are one block of _slotCount × SlotEntries counters, and the slot is one of them.
        return new Span<T>((T*)_slots + ((nint)slot * SlotEntries), SlotEntries);
    }

    private long GetSlot(int slot, int entry) => _wide ? SlotOf<long>(slot)[entry] : SlotOf<uint>(slot)[entry];

    private void SetSlot(int slot, int entry, long value)
    {
        if (_wide)
        {
            SlotOf<long>(slot)[entry] = value;
        }
        else
        {
            SlotOf<uint>(slot)[entry] = (uint)value;
        }
    }

    // Returns the row that a code's walk from the first level ends in: a leaf, or an open cell during the counting.
    private int Descend(ReadOnlySpan<SpatialRow> rows, ReadOnlySpan<int> lookup, ulong code)
    {
        var index = lookup[(int)(code >> _firstShift)];
        while (rows[index].Kind == (byte)SpatialCellKind.Inner)
        {
            // A split cell's children are one level down, so the shift is below 64.
            index = rows[index].FirstChild + (int)((code >> _layout.BitsBelow(rows[index].Level + 1)) & _childMask);
        }

        return index;
    }

    private void CountPass<T>(Span<T> counters, Span<ulong> extents, ReadOnlySpan<ulong> codes)
        where T : unmanaged, IBinaryInteger<T>
    {
        ReadOnlySpan<SpatialRow> rows = Table;
        var lookup = Lookup;
        ReadOnlySpan<OpenCell> open = Open;
        foreach (var code in codes)
        {
            ref readonly var row = ref rows[Descend(rows, lookup, code)];
            if (row.Kind != OpenKind)
            {
                continue;
            }

            ref readonly var cell = ref open[row.FirstChild];

            // The pass before saw the cell's points in the base cell. A point outside it came with a change of the column.
            if ((code >> cell.BaseShift) != cell.Prefix)
            {
                continue;
            }

            counters[cell.Offset + (int)((code >> cell.CounterShift) & cell.Mask)]++;
            var extent = extents.Slice(2 * row.FirstChild, 2);
            if (code < extent[0])
            {
                extent[0] = code;
            }

            if (code > extent[1])
            {
                extent[1] = code;
            }
        }
    }

    private int Place<T>(int group, Span<ulong> codes)
        where T : unmanaged, IBinaryInteger<T>
    {
        ReadOnlySpan<SpatialRow> rows = Table;
        var lookup = Lookup;
        var dropped = 0;
        for (var i = 0; i < codes.Length; i++)
        {
            var parts = SlotOf<T>(rows[Descend(rows, lookup, codes[i])].FirstChild).Slice(2 * group, 2);
            var next = parts[0];
            if (next < parts[1])
            {
                parts[0] = next + T.One;
                codes[i] = ulong.CreateTruncating(next);
            }
            else
            {
                codes[i] = ulong.MaxValue;
                dropped++;
            }
        }

        return dropped;
    }

    // The single job after a pass. The open cells come in the order of their levels and, in a level, of their codes; the
    // rows they split into are made a level at a time in the order of the codes, so that the cap on the table's rows
    // stops the deep cells first (design §3.4 "表の上限").
    private bool Resolve()
    {
        _frontier.Count = 0;
        _nextFrontier.Count = 0;
        _candidates.Count = 0;
        var entering = 0;
        var level = _openCount > 0 ? Table[Open[0].Row].Level : 0;
        while (entering < _openCount || _frontier.Count > 0)
        {
            while (entering < _openCount && Table[Open[entering].Row].Level == level)
            {
                Enter(entering);
                entering++;
            }

            var frontier = _frontier.AsSpan();
            frontier.Sort(new CodeOrder(_rows, _rowCount));
            for (var i = 0; i < frontier.Length; i++)
            {
                Split(frontier[i]);
            }

            (_frontier, _nextFrontier) = (_nextFrontier, _frontier);
            _nextFrontier.Count = 0;
            level++;
        }

        if (_capped)
        {
            // No cell can be split any more, so the counting stops and the cells that waited for a pass stay as they are.
            foreach (var rowIndex in _candidates.AsSpan())
            {
                ref var row = ref Table[rowIndex];
                Range(row, Open[row.FirstChild], out var offset, out var length);
                MakeLeaf(ref row, SpatialCellKind.CappedLeaf, offset, length);
            }

            _candidates.Count = 0;
        }

        OpenCandidates();
        _pass++;
        return _openCount > 0;
    }

    // Totals an open cell's counters. The cell is a leaf when it has few points or all its points have one code.
    private void Enter(int index)
    {
        ref var open = ref Open[index];
        ref var row = ref Table[open.Row];
        var length = (int)open.Mask + 1;
        var min = ulong.MaxValue;
        var max = 0UL;
        if (_pass == 0)
        {
            Extent(open.Offset, length, out min, out max);
        }
        else
        {
            for (var group = 0; group < Groups; group++)
            {
                var extent = Extents(group).Slice(2 * index, 2);
                min = Math.Min(min, extent[0]);
                max = Math.Max(max, extent[1]);
            }
        }

        open.Seen = Sum(open.Offset, length);
        open.Min = min;
        open.Max = max;
        row.Count = open.Seen;
        if (row.Count <= _leafCapacity)
        {
            MakeLeaf(ref row, SpatialCellKind.Leaf, open.Offset, length);
        }
        else if (min == max)
        {
            MakeLeaf(ref row, SpatialCellKind.CoincidentLeaf, open.Offset, length);
        }
        else
        {
            row.Kind = PendingKind;
            row.FirstChild = index;
            _frontier.Add(open.Row);
        }
    }

    // Splits one pending cell into its children, or leaves it for the next pass when the counters end at its level.
    private void Split(int rowIndex)
    {
        ref var row = ref Table[rowIndex];
        var openIndex = row.FirstChild;
        var open = Open[openIndex];
        int level = row.Level;
        var code = row.Code;
        Range(row, open, out var offset, out var length);
        if (level == open.BaseLevel + open.Levels)
        {
            if (level == _layout.BitsPerAxis)
            {
                // One grid cell: its points have one code.
                MakeLeaf(ref row, SpatialCellKind.CoincidentLeaf, offset, length);
            }
            else
            {
                _candidates.Add(rowIndex);
            }

            return;
        }

        var children = _layout.ChildCount;
        if (_capped || _rowCount + children > _maxRows)
        {
            _capped = true;
            MakeLeaf(ref row, SpatialCellKind.CappedLeaf, offset, length);
            return;
        }

        // The rows may move here: `row` is not read below, and the views are taken again.
        var first = _rowCount;
        Reserve(ref _rows, ref _rowCapacity, first + children);
        _rowCount += children;
        var rows = Table;
        for (var c = 0; c < children; c++)
        {
            ref var child = ref rows[first + c];
            child = new SpatialRow { Code = (code << _layout.Dimensions) | (uint)c, Level = (byte)(level + 1) };
            if (Range(child, open, out offset, out length))
            {
                child.Count = Sum(offset, length);
            }

            if (child.Count <= _leafCapacity)
            {
                MakeLeaf(ref child, SpatialCellKind.Leaf, offset, length);
            }
            else if (_pass == 0 && IsCoincident(offset, length))
            {
                // Only the first counters know the codes of a cell below the open cell.
                MakeLeaf(ref child, SpatialCellKind.CoincidentLeaf, offset, length);
            }
            else
            {
                child.Kind = PendingKind;
                child.FirstChild = openIndex;
                _nextFrontier.Add(first + c);
            }
        }

        rows[rowIndex].Kind = (byte)SpatialCellKind.Inner;
        rows[rowIndex].FirstChild = first;
    }

    // Opens the cells that wait for a pass, and makes the counters of that pass.
    private void OpenCandidates()
    {
        var candidates = _candidates.AsSpan();
        Reserve(ref _nextOpen, ref _nextOpenCapacity, candidates.Length);
        var next = new Span<OpenCell>(_nextOpen, candidates.Length);
        var rows = Table;
        long total = 0;
        for (var i = 0; i < candidates.Length; i++)
        {
            ref var row = ref rows[candidates[i]];
            ref readonly var open = ref Open[row.FirstChild];
            int baseLevel = row.Level;
            var prefix = row.Code;
            var min = open.Min;
            var max = open.Max;
            var exact = row.Count == open.Seen;
            if (_pass == 0)
            {
                Range(row, open, out var offset, out var length);
                Extent(offset, length, out min, out max);
                exact = true;
            }

            if (exact)
            {
                // The least and the greatest code of the cell's points are known: start from the deepest cell that holds
                // both, the level above the one where the points first part. The codes differ, or the cell would be a leaf
                // of points at one position, so that cell is above the deepest level.
                baseLevel = (BitOperations.LeadingZeroCount(min ^ max) - (64 - _layout.TotalBits)) / _layout.Dimensions;
                prefix = _layout.CellOf(min, baseLevel);
            }

            var levels = 1;
            while (levels < _layout.MaxDescent && ((long)_leafCapacity << (_layout.Dimensions * levels)) < row.Count)
            {
                levels++;
            }

            levels = Math.Min(levels, _layout.BitsPerAxis - baseLevel);
            var counters = 1 << (_layout.Dimensions * levels);

            // An open cell of a counting pass is at the first level or below, so both shifts are below 64.
            next[i] = new OpenCell
            {
                Prefix = prefix,
                Offset = checked((int)total),
                Mask = (ulong)counters - 1,
                Row = candidates[i],
                BaseLevel = (byte)baseLevel,
                Levels = (byte)levels,
                BaseShift = (byte)_layout.BitsBelow(baseLevel),
                CounterShift = (byte)_layout.BitsBelow(baseLevel + levels),
            };
            total += counters;
            row.Kind = OpenKind;
            row.FirstChild = i;
        }

        var count = checked((int)total);
        _openCount = 0;
        _counterCount = 0;
        var swapped = _open;
        _open = _nextOpen;
        _nextOpen = swapped;
        (_openCapacity, _nextOpenCapacity) = (_nextOpenCapacity, _openCapacity);
        if (candidates.Length == 0)
        {
            return;
        }

        var size = (nuint)CounterSize;
        for (var group = 0; group < Groups; group++)
        {
            // A block is freed and forgotten before the next one is made, so that Dispose is right after a failure here.
            if (count > _counterCapacity)
            {
                NativeMemory.AlignedFree(_counters[group]);
                _counters[group] = null;
                _counters[group] = NativeMemory.AlignedAlloc((nuint)count * size, 64);
            }

            NativeMemory.Clear(_counters[group], (nuint)count * size);
            if (candidates.Length > _extentCapacity)
            {
                NativeMemory.AlignedFree(_extents[group]);
                _extents[group] = null;
                _extents[group] = (ulong*)NativeMemory.AlignedAlloc((nuint)candidates.Length * 2 * sizeof(ulong), 64);
            }
        }

        _counterCapacity = Math.Max(_counterCapacity, count);
        _extentCapacity = Math.Max(_extentCapacity, candidates.Length);
        _counterCount = count;
        _openCount = candidates.Length;
        for (var group = 0; group < Groups; group++)
        {
            var extents = Extents(group);
            for (var i = 0; i < extents.Length; i += 2)
            {
                extents[i] = ulong.MaxValue;
                extents[i + 1] = 0;
            }
        }
    }

    // Returns the counters of an open cell that lie in a row's cell. False when the row lies outside the open cell's base
    // cell and so has no points.
    private bool Range(in SpatialRow row, in OpenCell open, out int offset, out int length)
    {
        var dimensions = _layout.Dimensions;
        int level = row.Level;
        if (level <= open.BaseLevel)
        {
            // An ancestor of the base cell, or the base cell: all the counters. Another cell at those levels: none. The
            // base cell is above the deepest level and the row is the open cell's or below it, so the shift is below 64.
            if ((open.Prefix >> (dimensions * (open.BaseLevel - level))) != row.Code)
            {
                offset = 0;
                length = 0;
                return false;
            }

            offset = open.Offset;
            length = (int)open.Mask + 1;
            return true;
        }

        var below = dimensions * (open.BaseLevel + open.Levels - level);
        var within = row.Code - (open.Prefix << (dimensions * (level - open.BaseLevel)));
        offset = open.Offset + (int)(within << below);
        length = 1 << below;
        return true;
    }

    private long Sum(int offset, int length)
    {
        long sum = 0;
        for (var group = 0; group < Groups; group++)
        {
            sum += GroupSum(group, offset, length);
        }

        return sum;
    }

    private long GroupSum(int group, int offset, int length)
    {
        long sum = 0;
        if (_pass == 0)
        {
            foreach (ref readonly var counter in First(group).Slice(offset, length))
            {
                sum += counter.Count;
            }
        }
        else if (_wide)
        {
            foreach (var counter in Counters<long>(group).Slice(offset, length))
            {
                sum += counter;
            }
        }
        else
        {
            foreach (var counter in Counters<uint>(group).Slice(offset, length))
            {
                sum += counter;
            }
        }

        return sum;
    }

    // The least and the greatest code that the first count saw in a run of cells of the first level.
    private void Extent(int offset, int length, out ulong min, out ulong max)
    {
        min = ulong.MaxValue;
        max = 0;
        for (var group = 0; group < Groups; group++)
        {
            foreach (ref readonly var counter in First(group).Slice(offset, length))
            {
                min = Math.Min(min, counter.Min);
                max = Math.Max(max, counter.Max);
            }
        }
    }

    private bool IsCoincident(int offset, int length)
    {
        Extent(offset, length, out var min, out var max);
        return min == max;
    }

    // Makes a row a leaf and keeps each group's count of its points in a slot. BuildTable turns the counts into places.
    private void MakeLeaf(ref SpatialRow row, SpatialCellKind kind, int offset, int length)
    {
        row.Kind = (byte)kind;
        if (row.Count == 0)
        {
            row.FirstChild = EmptySlot;
            return;
        }

        ReserveSlots(_slotCount + 1);
        var slot = _slotCount++;
        for (var group = 0; group < Groups; group++)
        {
            SetSlot(slot, 2 * group, GroupSum(group, offset, length));
            SetSlot(slot, (2 * group) + 1, 0);
        }

        row.FirstChild = slot;
    }

    // Walks the table in the order of the codes. The depth is at most the deepest level.
    private void Number(Span<SpatialRow> rows, int rowIndex, ref long position)
    {
        ref var row = ref rows[rowIndex];
        var first = position;
        if (row.Kind == (byte)SpatialCellKind.Inner)
        {
            for (var c = 0; c < _layout.ChildCount; c++)
            {
                Number(rows, row.FirstChild + c, ref position);
            }

            row.First = first;
            row.Count = position - first;
            return;
        }

        // The passes counted at different times. When the column changed between them, the leaves can hold more points
        // than the copy has room for: clip them at the copy's end.
        var count = Math.Min(row.Count, _capacity - first);
        if (count != row.Count)
        {
            _disagreed = true;
        }

        row.First = first;
        row.Count = count;
        position = first + count;
        if (row.FirstChild == EmptySlot)
        {
            return;
        }

        var start = first;
        for (var group = 0; group < Groups; group++)
        {
            var stop = Math.Min(start + GetSlot(row.FirstChild, 2 * group), position);
            SetSlot(row.FirstChild, 2 * group, start);
            SetSlot(row.FirstChild, (2 * group) + 1, stop);
            start = stop;
        }
    }

    // Walks the leaves in the order of the copy and fills the places that no point took.
    private void Fill(Span<SpatialRow> rows, int rowIndex, SpatialCopy? copy, ref long firstWritten)
    {
        ref var row = ref rows[rowIndex];
        if (row.Kind == (byte)SpatialCellKind.Inner)
        {
            for (var c = 0; c < _layout.ChildCount; c++)
            {
                Fill(rows, row.FirstChild + c, copy, ref firstWritten);
            }

            return;
        }

        var slot = row.FirstChild;
        row.FirstChild = -1;
        if (slot == EmptySlot || copy == null)
        {
            return;
        }

        var start = row.First;
        for (var group = 0; group < Groups; group++)
        {
            var next = GetSlot(slot, 2 * group);
            var stop = GetSlot(slot, (2 * group) + 1);
            if (next > start && firstWritten < 0)
            {
                // The first written point of the copy: the places before it take it.
                firstWritten = start;
                copy.Fill(start, 0, start);
            }

            if (next < stop)
            {
                _disagreed = true;
                if (firstWritten >= 0)
                {
                    // The place before the gap is written or already filled, because the walk is in the copy's order.
                    copy.Fill(next - 1, next, stop - next);
                }
            }

            start = stop;
        }
    }

    private void FreeCounting()
    {
        _openCount = 0;
        _counterCount = 0;
        for (var group = 0; group < Groups; group++)
        {
            NativeMemory.AlignedFree(_first[group]);
            _first[group] = null;
            NativeMemory.AlignedFree(_counters[group]);
            _counters[group] = null;
            NativeMemory.AlignedFree(_extents[group]);
            _extents[group] = null;
        }

        _counterCapacity = 0;
        _extentCapacity = 0;
        NativeMemory.AlignedFree(_open);
        _open = null;
        _openCapacity = 0;
        NativeMemory.AlignedFree(_nextOpen);
        _nextOpen = null;
        _nextOpenCapacity = 0;
        _frontier.Free();
        _nextFrontier.Free();
        _candidates.Free();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FirstCounter
    {
        public long Count;
        public ulong Min;
        public ulong Max;
    }

    // A cell whose points a counting pass counts, Levels levels below the base cell: the deepest cell that is known to
    // hold all the cell's points.
    private struct OpenCell
    {
        public ulong Prefix;
        public long Seen;
        public ulong Min;
        public ulong Max;
        public ulong Mask;
        public int Offset;
        public int Row;
        public byte BaseLevel;
        public byte Levels;
        public byte BaseShift;
        public byte CounterShift;
    }

    private struct IntList
    {
        public int* Items;
        public int Count;
        public long Capacity;

        public void Add(int value)
        {
            Reserve(ref Items, ref Capacity, Count + 1L);
            Count++;
            AsSpan()[^1] = value;
        }

        // Reserve made room for Count items before Count grew.
        public readonly Span<int> AsSpan() => new(Items, Count);

        public void Free()
        {
            NativeMemory.AlignedFree(Items);
            this = default;
        }
    }

    private readonly struct CodeOrder(SpatialRow* rows, int count) : IComparer<int>
    {
        // The table's rows, which do not move while the frontier of one level is sorted.
        private readonly SpatialRow* _rows = rows;
        private readonly int _count = count;

        public int Compare(int x, int y)
        {
            var rows = new ReadOnlySpan<SpatialRow>(_rows, _count);
            return rows[x].Code.CompareTo(rows[y].Code);
        }
    }
}
