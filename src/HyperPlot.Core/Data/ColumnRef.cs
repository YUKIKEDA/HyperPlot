namespace HyperPlot;

/// <summary>
/// A reference to a column of a <see cref="DataTable"/>, without its element type: what the spec types hold (design §3.1).
/// Two references are equal when they name the same position of the same table. The <see langword="default"/> reference
/// names no column, and a spec that holds it is refused.
/// </summary>
public readonly struct ColumnRef : IEquatable<ColumnRef>
{
    private readonly DataTable? _table;

    internal ColumnRef(DataTable table, int index)
    {
        _table = table;
        Index = index;
    }

    /// <summary>Gets the table.</summary>
    /// <exception cref="InvalidOperationException">The reference is <see langword="default"/>.</exception>
    public DataTable Table => _table ?? throw new InvalidOperationException("The default reference names no table.");

    /// <summary>Gets the position of the column in its table.</summary>
    public int Index { get; }

    /// <summary>Gets the column: <c>Table.GetColumn(Index)</c>.</summary>
    /// <exception cref="InvalidOperationException">The reference is <see langword="default"/>.</exception>
    public Column Column => Table.GetColumn(Index);

    /// <summary>Gets a value indicating whether this is the <see langword="default"/> reference, which names no table.</summary>
    internal bool IsDefault => _table is null;

    /// <summary>Gets the table, or null for the default reference.</summary>
    internal DataTable? TableOrNull => _table;

    /// <summary>Returns whether two references name the same position of the same table.</summary>
    /// <param name="left">The first reference.</param>
    /// <param name="right">The second reference.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    public static bool operator ==(ColumnRef left, ColumnRef right) => left.Equals(right);

    /// <summary>Returns whether two references differ in their table or their position.</summary>
    /// <param name="left">The first reference.</param>
    /// <param name="right">The second reference.</param>
    /// <returns><see langword="true"/> when they differ.</returns>
    public static bool operator !=(ColumnRef left, ColumnRef right) => !left.Equals(right);

    /// <inheritdoc />
    public bool Equals(ColumnRef other) => ReferenceEquals(_table, other._table) && Index == other.Index;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ColumnRef other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_table, Index);
}
