namespace HyperPlot;

/// <summary>
/// The rows [<see cref="Start"/>, <see cref="End"/>) of a column that a series reads (design §5.5.1, row G7c): the whole
/// column, or the one run that a category's rows lie in. <see cref="End"/> past the column's length means its end.
/// </summary>
internal readonly record struct RowRange(long Start, long End)
{
    /// <summary>Gets the whole column.</summary>
    public static RowRange All => new(0, long.MaxValue);

    /// <summary>Returns the first row past the range in a column of a length.</summary>
    public long EndIn(long length) => Math.Min(End, length);

    /// <summary>Returns a position held to the range in a column of a length.</summary>
    public long Hold(long position, long length) => Math.Clamp(position, Start, EndIn(length));
}
