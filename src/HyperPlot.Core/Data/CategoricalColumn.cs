namespace HyperPlot;

/// <summary>
/// A column of categories (design §3.1, §4.2, row G7b): each element's category number, and the categories' names in their
/// order. In memory and immutable: its <see cref="Column.Version"/> never changes (appending comes with row C4). A band
/// scale reads it on x; it has no numeric values.
/// </summary>
public sealed class CategoricalColumn : Column
{
    private readonly int[] _codes;
    private readonly string[] _categories;

    /// <summary>Where each category's rows lie, found once (row G7c): the column never changes.</summary>
    internal CategoryRuns? Runs;

    private CategoricalColumn(int[] codes, string[] categories)
        : base(codes.Length) => (_codes, _categories) = (codes, categories);

    /// <summary>Gets the number of categories.</summary>
    public int CategoryCount => _categories.Length;

    /// <summary>Gets the categories' names, in the order of their numbers.</summary>
    public IReadOnlyList<string> Categories => _categories;

    /// <inheritdoc />
    internal override string KindName => nameof(CategoricalColumn);

    /// <summary>Returns an element's category number: at least 0 and below <see cref="CategoryCount"/>, or −1 for a missing value.</summary>
    /// <param name="index">The element.</param>
    /// <returns>The number.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the column.</exception>
    public int GetCode(long index)
    {
        if ((ulong)index >= (ulong)_codes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"The column has {_codes.Length} elements.");
        }

        return _codes[index];
    }

    /// <summary>Returns the category numbers of a chunk, as <see cref="Column{T}.GetChunk"/> reads values.</summary>
    internal ReadOnlySpan<int> GetCodes(int chunkIndex)
    {
        var start = (long)chunkIndex << ChunkShift;
        return _codes.AsSpan((int)start, GetChunkLength(chunkIndex));
    }

    /// <summary>Returns a column of other codes over the same categories (row G7c): a category's gathered rows.</summary>
    internal CategoricalColumn WithCodes(int[] codes) => new(codes, _categories);

    /// <summary>Returns whether another column has the same categories in the same order (design §4.2).</summary>
    internal bool SameCategories(CategoricalColumn other) => ReferenceEquals(this, other) || _categories.AsSpan().SequenceEqual(other._categories);

    /// <summary>Makes a column of values: the categories are the given ones, or the names sorted in ordinal order.</summary>
    internal static CategoricalColumn Create(ReadOnlySpan<string?> values, string[]? categories)
    {
        if (categories is null)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < values.Length; i++)
            {
                if (values[i] is { Length: 0 })
                {
                    throw new ArgumentException($"Value {i} is empty; a category has a name, and a missing value is null.", nameof(values));
                }

                if (values[i] is { } value)
                {
                    _ = names.Add(value);
                }
            }

            categories = [.. names];
            Array.Sort(categories, StringComparer.Ordinal);
        }

        var numbers = new Dictionary<string, int>(categories.Length, StringComparer.Ordinal);
        for (var i = 0; i < categories.Length; i++)
        {
            var name = categories[i];
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException($"Category {i} is {(name is null ? "null" : "empty")}; a category has a name.", nameof(categories));
            }

            if (!numbers.TryAdd(name, i))
            {
                throw new ArgumentException($"The category \"{name}\" is there twice.", nameof(categories));
            }
        }

        var codes = new int[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is not { } value)
            {
                codes[i] = -1;
            }
            else if (!numbers.TryGetValue(value, out codes[i]))
            {
                throw new ArgumentException(
                    value.Length == 0
                        ? $"Value {i} is empty; a category has a name, and a missing value is null."
                        : $"Value {i}, \"{value}\", is not one of the categories.",
                    nameof(values)
                );
            }
        }

        return new CategoricalColumn(codes, categories);
    }

    /// <inheritdoc />
    internal override bool TryGetRange(out ScaleEnd min, out ScaleEnd max)
    {
        // A category has no numeric value: the compile keeps it off a continuous scale (design §4.2).
        (min, max) = (default, default);
        return false;
    }

    /// <inheritdoc />
    internal override Task? PrecomputeStatsIfMissing(PlotWorkerPool pool) => null;

    /// <inheritdoc />
    internal override double DifferenceAt(long index, long origin) => _codes[index] - origin;

    /// <inheritdoc />
    internal override void ReadDifferences(Span<double> destination, long origin)
    {
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = _codes[i] - origin;
        }
    }

    /// <inheritdoc />
    internal override long LowerBound(double boundary, long origin) =>
        throw new InvalidOperationException("A categorical column has no order to search: the compile keeps it to a band scale's bars.");

    /// <inheritdoc />
    internal override bool IsNonDecreasing(PlotWorkerPool pool) => false;

    /// <inheritdoc />
    internal override int AddSeries<TY>(M4Batch batch, MinMaxPyramid<TY> y, M4Point[] destination, RowRange rows) =>
        throw new InvalidOperationException("A categorical column is not an X of M4: the compile keeps it to a band scale's bars.");
}
