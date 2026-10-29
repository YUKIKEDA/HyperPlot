namespace HyperPlot;

/// <summary>Adds the named columns of a <see cref="DataTable"/> (design §3.1). Make one with <see cref="DataTable.Create"/>.</summary>
public sealed class DataTableBuilder
{
    private readonly List<string> _names = [];
    private readonly List<Column> _columns = [];
    private bool _built;

    internal DataTableBuilder() { }

    /// <summary>Adds a column.</summary>
    /// <param name="name">The column's name: not empty, and not a name already added (compared ordinally).</param>
    /// <param name="column">The column. The table does not own it.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="column"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or already added.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> was called.</exception>
    public DataTableBuilder Add(string name, Column column)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(column);
        ThrowIfBuilt();
        if (name.Length == 0)
        {
            throw new ArgumentException("A column's name must not be empty.", nameof(name));
        }

        if (_names.Contains(name, StringComparer.Ordinal))
        {
            throw new ArgumentException($"The table already has a column named \"{name}\".", nameof(name));
        }

        _names.Add(name);
        _columns.Add(column);
        return this;
    }

    /// <summary>Makes the table. The builder cannot be used afterwards.</summary>
    /// <returns>The table.</returns>
    /// <exception cref="ArgumentException">The columns are not all of the same length.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> was called before.</exception>
    public DataTable Build()
    {
        ThrowIfBuilt();
        for (var i = 1; i < _columns.Count; i++)
        {
            if (_columns[i].Length != _columns[0].Length)
            {
                throw new ArgumentException(
                    $"The column \"{_names[i]}\" has {_columns[i].Length} elements and \"{_names[0]}\" has {_columns[0].Length}; a table's columns have one length."
                );
            }
        }

        _built = true;
        return new DataTable([.. _names], [.. _columns]);
    }

    private void ThrowIfBuilt()
    {
        if (_built)
        {
            throw new InvalidOperationException("The table was built; start another with DataTable.Create.");
        }
    }
}
