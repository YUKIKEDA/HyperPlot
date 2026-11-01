namespace HyperPlot;

/// <summary>
/// What a layer draws from its columns (design §4.1, row G5b): bins, counts, or summaries, which the workers make once a
/// version of the columns as a small derived table, drawn by the geom's usual path and the layer's position. A layer with a
/// stat takes its y from the stat; computed per group of its color column, before its position.
/// </summary>
/// <remarks>Make one with <see cref="Stat"/>. The compile takes the three kinds of this row and refuses another.</remarks>
public abstract record StatSpec : SpecNode
{
    /// <summary>Initializes a new instance of the <see cref="StatSpec"/> class with a new <see cref="SpecNode.Id"/>.</summary>
    private protected StatSpec() { }
}

/// <summary>What a bin's y is (design §4.1).</summary>
public enum BinOutput
{
    /// <summary>The number of values in the bin.</summary>
    Count,

    /// <summary>The count over the group's number of values and the bin's width in the x scale's space: the bars' areas sum to 1.</summary>
    Density,
}

/// <summary>
/// Bins of a numeric x (design §4.1): Vega's nice steps of 10^k × 1, 2, or 5, at most <see cref="MaxBins"/>, in the x
/// scale's space (calendar steps on a time scale); [a, b), the last [a, b]. NaN and infinite values are not counted. Bars,
/// areas, and steps take it.
/// </summary>
public sealed record BinStatSpec : StatSpec
{
    /// <summary>Gets the most bins: from 1 to 10,000, 10 by default. Unused when <see cref="Step"/> is given.</summary>
    public int MaxBins { get; init; } = 10;

    /// <summary>Gets the bins' width in the x scale's space, or null to choose it; finite and above 0.</summary>
    public double? Step { get; init; }

    /// <summary>Gets a value the bins' edges go through, in the x scale's space, or null for multiples of the step; finite.</summary>
    public double? Anchor { get; init; }

    /// <summary>Gets what the bins' y is: <see cref="BinOutput.Count"/> by default.</summary>
    public BinOutput Output { get; init; }
}

/// <summary>The rows of each category of a categorical x (design §4.1): bars take it.</summary>
public sealed record CountStatSpec : StatSpec;

/// <summary>
/// A summary of the y values of each category of a categorical x (design §4.1): a bar of <see cref="Value"/>, or an error
/// bar from <see cref="Low"/> to <see cref="High"/>. NaN values are not counted; a category with none draws nothing.
/// </summary>
public sealed record SummaryStatSpec : StatSpec
{
    /// <summary>Gets a bar's value, or null for <see cref="SummaryOp.Mean"/>.</summary>
    public SummaryOp? Value { get; init; }

    /// <summary>Gets an error bar's lower end, or null for <see cref="SummaryOp.Min"/>.</summary>
    public SummaryOp? Low { get; init; }

    /// <summary>Gets an error bar's upper end, or null for <see cref="SummaryOp.Max"/>.</summary>
    public SummaryOp? High { get; init; }
}

/// <summary>The kinds of <see cref="SummaryOp"/>.</summary>
public enum SummaryKind
{
    /// <summary>The mean.</summary>
    Mean,

    /// <summary>The least value.</summary>
    Min,

    /// <summary>The greatest value.</summary>
    Max,

    /// <summary>A quantile, the exact type 7.</summary>
    Quantile,

    /// <summary>A user's <see cref="IStat"/>.</summary>
    Custom,
}

/// <summary>How a summary reduces a category's values (design §4.1).</summary>
public sealed record SummaryOp
{
    private SummaryOp(SummaryKind kind, double p, IStat? stat) => (Kind, P, Stat) = (kind, p, stat);

    /// <summary>Gets the mean.</summary>
    public static SummaryOp Mean { get; } = new(SummaryKind.Mean, 0, null);

    /// <summary>Gets the least value.</summary>
    public static SummaryOp Min { get; } = new(SummaryKind.Min, 0, null);

    /// <summary>Gets the greatest value.</summary>
    public static SummaryOp Max { get; } = new(SummaryKind.Max, 0, null);

    /// <summary>Gets the median: <see cref="Quantile"/> at 0.5.</summary>
    public static SummaryOp Median { get; } = new(SummaryKind.Quantile, 0.5, null);

    /// <summary>Gets the kind.</summary>
    public SummaryKind Kind { get; }

    /// <summary>Gets a quantile's p, or 0 for another kind.</summary>
    public double P { get; }

    /// <summary>Gets a custom summary's stat, or null for another kind.</summary>
    public IStat? Stat { get; }

    /// <summary>
    /// Returns the quantile at <paramref name="p"/>: the exact type 7 of Hyndman and Fan, as numpy's default, R's, d3's, and
    /// Vega's. Of the n values in order, with h = (n − 1) p, x(⌊h⌋ + 1) + (h − ⌊h⌋)(x(⌊h⌋ + 2) − x(⌊h⌋ + 1)).
    /// </summary>
    /// <param name="p">The probability: from 0 to 1.</param>
    /// <returns>The summary.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="p"/> is not from 0 to 1.</exception>
    /// <remarks>
    /// The workers find the values by a radix select: a pass over the rows a byte of the value type (4 for float, 8 for
    /// double), with 2 KB a category and rank (2 ranks a quantile), at most 256 MB in all.
    /// </remarks>
    public static SummaryOp Quantile(double p) =>
        p is >= 0 and <= 1
            ? new(SummaryKind.Quantile, p, null)
            : throw new ArgumentOutOfRangeException(nameof(p), p, "A quantile's p is from 0 to 1.");

    /// <summary>Returns a user's summary (design §4.1).</summary>
    /// <param name="stat">The stat.</param>
    /// <returns>The summary.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stat"/> is null.</exception>
    public static SummaryOp Custom(IStat stat)
    {
        ArgumentNullException.ThrowIfNull(stat);
        return new(SummaryKind.Custom, 0, stat);
    }
}

/// <summary>
/// A user's summary (design §4.1, requirement N-6): a state of <see cref="StateLength"/> doubles a cell, starting at 0, that
/// the workers fold the cell's values into and merge.
/// </summary>
/// <remarks>
/// <para>
/// How it is called: <see cref="Fold"/> any number of times for a cell, on spans of any length (one included); the number
/// of calls and the lengths mean nothing. <see cref="Merge"/> joins two states and is associative, and merging a state of
/// zeros changes nothing. <see cref="Result"/> is called once a cell, never for a cell with no value.
/// </para>
/// <para>
/// The calls come from the workers' threads, several at once for different states: an implementation reads and writes the
/// states it is given only. An exception it throws comes out of the render as the workers' failure.
/// </para>
/// </remarks>
public interface IStat
{
    /// <summary>Gets the number of doubles of a cell's state: from 1 to 64.</summary>
    int StateLength { get; }

    /// <summary>Folds some of a cell's values, NaN left out, into its state.</summary>
    /// <param name="values">The values.</param>
    /// <param name="state">The cell's state.</param>
    void Fold(ReadOnlySpan<double> values, Span<double> state);

    /// <summary>Merges another state of the same cell into a state.</summary>
    /// <param name="other">The other state.</param>
    /// <param name="state">The state merged into.</param>
    void Merge(ReadOnlySpan<double> other, Span<double> state);

    /// <summary>Returns a cell's value from its state.</summary>
    /// <param name="state">The cell's state.</param>
    /// <returns>The value.</returns>
    double Result(ReadOnlySpan<double> state);
}

/// <summary>Makes the stats of a layer (design §4.1, row G5b).</summary>
public static class Stat
{
    /// <summary>Gets the stat that counts the rows of each category.</summary>
    public static CountStatSpec Count { get; } = new();

    /// <summary>Returns the stat that bins a numeric x.</summary>
    /// <param name="maxBins">The most bins: from 1 to 10,000.</param>
    /// <param name="step">The bins' width in the x scale's space, or null to choose it: finite and above 0.</param>
    /// <param name="anchor">A value the edges go through, or null: finite.</param>
    /// <param name="output">What the bins' y is.</param>
    /// <returns>The stat.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of its range, or the output is not a named value.</exception>
    public static BinStatSpec Bin(int maxBins = 10, double? step = null, double? anchor = null, BinOutput output = BinOutput.Count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBins, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBins, 10_000);
        if (step is { } s && (!double.IsFinite(s) || s <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(step), s, "A bin's step is finite and above 0.");
        }

        if (anchor is { } a && !double.IsFinite(a))
        {
            throw new ArgumentOutOfRangeException(nameof(anchor), a, "A bin's anchor is finite.");
        }

        if ((uint)output > (uint)BinOutput.Density)
        {
            throw new ArgumentOutOfRangeException(nameof(output), output, "The output is a named value.");
        }

        return new BinStatSpec
        {
            MaxBins = maxBins,
            Step = step,
            Anchor = anchor,
            Output = output,
        };
    }

    /// <summary>Returns the stat of a bar of a summary of each category.</summary>
    /// <param name="value">The bar's value, or null for the mean.</param>
    /// <returns>The stat.</returns>
    public static SummaryStatSpec Summary(SummaryOp? value = null) => new() { Value = value };

    /// <summary>Returns the stat of an error bar between two summaries of each category.</summary>
    /// <param name="low">The lower end.</param>
    /// <param name="high">The upper end.</param>
    /// <returns>The stat.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="low"/> or <paramref name="high"/> is null.</exception>
    public static SummaryStatSpec Summary(SummaryOp low, SummaryOp high)
    {
        ArgumentNullException.ThrowIfNull(low);
        ArgumentNullException.ThrowIfNull(high);
        return new() { Low = low, High = high };
    }
}
