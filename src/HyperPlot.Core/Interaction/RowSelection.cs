using System.Numerics;

namespace HyperPlot.Interaction;

/// <summary>
/// The rows of a layer a selection holds (design §6.4 "行の集合", row I3a, Issue #364 Decisions 4, 9, 10): runs of rows for
/// an x range over a layer whose x does not decrease, a bit a row for a rectangle or a lasso, or the rows a click hit. A
/// value: it does not change, so any thread may read it.
/// </summary>
public sealed class RowSelection
{
    private readonly long[] _starts;
    private readonly long[] _ends;
    private readonly ulong[]? _bits;
    private readonly long _length;

    private RowSelection(long[] starts, long[] ends, ulong[]? bits, long length, long count, bool derived)
    {
        (_starts, _ends, _bits, _length) = (starts, ends, bits, length);
        (Count, Derived) = (count, derived);
    }

    /// <summary>Gets an empty set: a layer that answers no selection (a rule, a segment, a text).</summary>
    public static RowSelection Empty { get; } = new([], [], null, 0, 0, derived: false);

    /// <summary>Gets the number of rows.</summary>
    public long Count { get; }

    /// <summary>
    /// Gets a value indicating whether the rows are a derived table's (a stat's or a stack's), as <see cref="SeriesHit.Derived"/>
    /// says: they are not rows of the plot's table.
    /// </summary>
    public bool Derived { get; }

    /// <summary>Returns whether a row is in the set.</summary>
    /// <param name="row">The row.</param>
    /// <returns>Whether it is.</returns>
    public bool Contains(long row)
    {
        if (_bits is { } bits)
        {
            return row >= 0 && row < _length && (bits[row >> 6] & (1UL << (int)(row & 63))) != 0;
        }

        var at = Array.BinarySearch(_starts, row);
        var run = at >= 0 ? at : ~at - 1;
        return run >= 0 && row < _ends[run];
    }

    /// <summary>Returns the runs of rows in order, each from its first row to the row after its last.</summary>
    /// <returns>The runs.</returns>
    public IEnumerable<(long Start, long End)> Ranges()
    {
        if (_bits is null)
        {
            for (var i = 0; i < _starts.Length; i++)
            {
                yield return (_starts[i], _ends[i]);
            }

            yield break;
        }

        var start = -1L;
        for (long row = 0; row < _length; row++)
        {
            var on = (_bits[row >> 6] & (1UL << (int)(row & 63))) != 0;
            if (on && start < 0)
            {
                start = row;
            }
            else if (!on && start >= 0)
            {
                yield return (start, row);
                start = -1;
            }
        }

        if (start >= 0)
        {
            yield return (start, _length);
        }
    }

    /// <summary>Returns a set of runs, each [start, end), in order and apart.</summary>
    internal static RowSelection OfRanges(long[] starts, long[] ends, bool derived)
    {
        var count = 0L;
        for (var i = 0; i < starts.Length; i++)
        {
            count += ends[i] - starts[i];
        }

        return new(starts, ends, null, 0, count, derived);
    }

    /// <summary>Returns a set of rows given one by one: sorted, without repeats.</summary>
    internal static RowSelection OfRows(ReadOnlySpan<long> rows, bool derived)
    {
        var sorted = rows.ToArray();
        Array.Sort(sorted);
        var (starts, ends) = (new List<long>(), new List<long>());
        foreach (var row in sorted)
        {
            if (ends.Count > 0 && ends[^1] >= row)
            {
                ends[^1] = Math.Max(ends[^1], row + 1);
                continue;
            }

            starts.Add(row);
            ends.Add(row + 1);
        }

        return OfRanges([.. starts], [.. ends], derived);
    }

    /// <summary>Returns a set of a bit a row over rows [0, length).</summary>
    internal static RowSelection OfBits(ulong[] bits, long length, bool derived)
    {
        var count = 0L;
        foreach (var word in bits)
        {
            count += BitOperations.PopCount(word);
        }

        return new([], [], bits, length, count, derived);
    }
}
