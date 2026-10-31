namespace HyperPlot;

/// <summary>
/// A reference to a categorical column of a <see cref="DataTable"/>, made by <see cref="DataTable.Cat(string)"/> (design
/// §3.1, row G7b). Two references are equal when they name the same position of the same table.
/// </summary>
public readonly struct CategoryRef : IEquatable<CategoryRef>
{
    private readonly ColumnRef _reference;

    internal CategoryRef(DataTable table, int index) => _reference = new ColumnRef(table, index);

    /// <summary>Gets the table.</summary>
    /// <exception cref="InvalidOperationException">The reference is <see langword="default"/>.</exception>
    public DataTable Table => _reference.Table;

    /// <summary>Gets the position of the column in its table.</summary>
    public int Index => _reference.Index;

    /// <summary>Gets the column: <c>Table.GetColumn(Index)</c>.</summary>
    /// <exception cref="InvalidOperationException">The reference is <see langword="default"/>.</exception>
    public CategoricalColumn Column => (CategoricalColumn)_reference.Column;

    /// <summary>Returns whether two references name the same position of the same table.</summary>
    /// <param name="left">The first reference.</param>
    /// <param name="right">The second reference.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    public static bool operator ==(CategoryRef left, CategoryRef right) => left.Equals(right);

    /// <summary>Returns whether two references differ in their table or their position.</summary>
    /// <param name="left">The first reference.</param>
    /// <param name="right">The second reference.</param>
    /// <returns><see langword="true"/> when they differ.</returns>
    public static bool operator !=(CategoryRef left, CategoryRef right) => !left.Equals(right);

    /// <summary>Returns the reference without its kind, as a spec holds it.</summary>
    /// <returns>The reference.</returns>
    public ColumnRef ToColumnRef() => _reference;

    /// <inheritdoc />
    public bool Equals(CategoryRef other) => _reference.Equals(other._reference);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is CategoryRef other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _reference.GetHashCode();
}
