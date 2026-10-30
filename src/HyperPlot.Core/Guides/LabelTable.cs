namespace HyperPlot;

/// <summary>
/// The strings of the tick labels a plan has written (design §4.6): looked up by their characters, so that a label written
/// again makes no string. Least recently used first out, at <see cref="Capacity"/> entries, the size of the shaping cache.
/// </summary>
/// <remarks>One thread at a time uses a table, the plan's.</remarks>
internal sealed class LabelTable
{
    /// <summary>The number of strings kept.</summary>
    public const int Capacity = 4096;

    private readonly Dictionary<string, LinkedListNode<string>> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinkedListNode<string>>.AlternateLookup<ReadOnlySpan<char>> _lookup;
    private readonly LinkedList<string> _order = [];

    public LabelTable() => _lookup = _entries.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>Gets the number of strings kept.</summary>
    public int Count => _entries.Count;

    /// <summary>Returns the string of some characters: the one kept, or a new one, which may push the oldest out.</summary>
    public string Get(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return string.Empty;
        }

        if (_lookup.TryGetValue(text, out var node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            return node.Value;
        }

        string value;
        if (_entries.Count == Capacity)
        {
            // The oldest entry's node is reused for the new string.
            node = _order.Last!;
            _order.RemoveLast();
            _ = _entries.Remove(node.Value);
            value = text.ToString();
            node.Value = value;
        }
        else
        {
            value = text.ToString();
            node = new LinkedListNode<string>(value);
        }

        _order.AddFirst(node);
        _entries.Add(value, node);
        return value;
    }
}
