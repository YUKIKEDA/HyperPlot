using System.Numerics;

namespace HyperPlot;

/// <summary>
/// A typed reference to a column of a <see cref="DataTable"/>, made by <see cref="DataTable.Col{T}(string)"/> (design §3.1).
/// Two references are equal when they name the same position of the same table.
/// </summary>
/// <typeparam name="T">The element type: that of a <see cref="Column{T}"/>, or <see cref="double"/> for a <see cref="UniformColumn"/> (<see cref="long"/> for an integer one).</typeparam>
public readonly struct ColumnRef<T> : IEquatable<ColumnRef<T>>
    where T : unmanaged, INumber<T>
{
    private readonly ColumnRef _reference;

    internal ColumnRef(DataTable table, int index) => _reference = new ColumnRef(table, index);

    /// <summary>Gets the table.</summary>
    /// <exception cref="InvalidOperationException">The reference is <see langword="default"/>.</exception>
    public DataTable Table => _reference.Table;

    /// <summary>Gets the position of the column in its table.</summary>
    public int Index => _reference.Index;

    /// <summary>Gets the column: <c>Table.GetColumn(Index)</c>.</summary>
    /// <exception cref="InvalidOperationException">The reference is <see langword="default"/>.</exception>
    public Column Column => _reference.Column;

    /// <summary>Returns the reference without its element type.</summary>
    /// <param name="reference">The typed reference.</param>
    public static implicit operator ColumnRef(ColumnRef<T> reference) => reference._reference;

    /// <summary>Returns whether two references name the same position of the same table.</summary>
    /// <param name="left">The first reference.</param>
    /// <param name="right">The second reference.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    public static bool operator ==(ColumnRef<T> left, ColumnRef<T> right) => left.Equals(right);

    /// <summary>Returns whether two references differ in their table or their position.</summary>
    /// <param name="left">The first reference.</param>
    /// <param name="right">The second reference.</param>
    /// <returns><see langword="true"/> when they differ.</returns>
    public static bool operator !=(ColumnRef<T> left, ColumnRef<T> right) => !left.Equals(right);

    /// <summary>Returns the reference without its element type: the named form of the implicit conversion.</summary>
    /// <returns>The reference.</returns>
    public ColumnRef ToColumnRef() => _reference;

    /// <inheritdoc />
    public bool Equals(ColumnRef<T> other) => _reference.Equals(other._reference);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ColumnRef<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _reference.GetHashCode();
}
