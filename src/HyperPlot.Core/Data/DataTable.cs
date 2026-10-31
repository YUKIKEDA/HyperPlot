using System.Numerics;

namespace HyperPlot;

/// <summary>
/// Named columns of equal length that a plot reads (design §3.1). Make one with <see cref="Create"/>. The table holds the
/// columns but does not own them: their owner disposes them, after every renderer that drew them.
/// </summary>
public sealed class DataTable
{
    private readonly string[] _names;
    private readonly Column[] _columns;

    internal DataTable(string[] names, Column[] columns)
    {
        _names = names;
        _columns = columns;
        Length = columns.Length == 0 ? 0 : columns[0].Length;
    }

    /// <summary>Gets the length of every column, or 0 when the table has none.</summary>
    public long Length { get; }

    /// <summary>Gets the number of columns.</summary>
    public int ColumnCount => _columns.Length;

    /// <summary>Starts a table.</summary>
    /// <returns>A builder to add the columns to.</returns>
    public static DataTableBuilder Create() => new();

    /// <summary>Returns a column's name.</summary>
    /// <param name="index">The column, from 0 to <see cref="ColumnCount"/> - 1.</param>
    /// <returns>The name.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a column's.</exception>
    public string GetName(int index)
    {
        CheckIndex(index);
        return _names[index];
    }

    /// <summary>Returns a column.</summary>
    /// <param name="index">The column, from 0 to <see cref="ColumnCount"/> - 1.</param>
    /// <returns>The column.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a column's.</exception>
    public Column GetColumn(int index)
    {
        CheckIndex(index);
        return _columns[index];
    }

    /// <summary>
    /// Returns a typed reference to a column by its name, compared ordinally (case matters). A <see cref="Column{T}"/> is
    /// referred to with its own <typeparamref name="T"/>, and a <see cref="UniformColumn"/> with <see cref="double"/>, or with
    /// <see cref="long"/> when it is <see cref="UniformColumn.IsInteger"/>.
    /// </summary>
    /// <typeparam name="T">The element type of the column.</typeparam>
    /// <param name="name">The column's name.</param>
    /// <returns>The reference.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="ArgumentException">No column has the name, or its element type is not <typeparamref name="T"/>.</exception>
    public ColumnRef<T> Col<T>(string name)
        where T : unmanaged, INumber<T>
    {
        ArgumentNullException.ThrowIfNull(name);
        var index = Array.IndexOf(_names, name);
        if (index < 0)
        {
            throw new ArgumentException($"The table has no column named \"{name}\".", nameof(name));
        }

        var column = _columns[index];
        var uniform = column is UniformColumn { IsInteger: var integer } && typeof(T) == (integer ? typeof(long) : typeof(double));
        if (column is not Column<T> && !uniform)
        {
            throw new ArgumentException($"The column \"{name}\" is a {column.KindName}, not a column of {typeof(T).Name}.", nameof(name));
        }

        return new ColumnRef<T>(this, index);
    }

    /// <summary>Returns a reference to a categorical column by its name (design §3.1, row G7b). Names are compared ordinally.</summary>
    /// <param name="name">The column's name.</param>
    /// <returns>The reference.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="ArgumentException">No column has the name, or it is not a <see cref="CategoricalColumn"/>.</exception>
    public CategoryRef Cat(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var index = Array.IndexOf(_names, name);
        if (index < 0)
        {
            throw new ArgumentException($"The table has no column named \"{name}\".", nameof(name));
        }

        if (_columns[index] is not CategoricalColumn)
        {
            throw new ArgumentException($"The column \"{name}\" is a {_columns[index].KindName}, not a CategoricalColumn.", nameof(name));
        }

        return new CategoryRef(this, index);
    }

    private void CheckIndex(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _columns.Length);
    }
}
