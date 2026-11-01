namespace HyperPlot;

/// <summary>
/// What a layer draws from its columns (design §4.1, rows G5b and G5c): bins, counts, summaries, densities, moving
/// averages, or 2D bins, which the workers make once a version of the columns as a small derived table, drawn by the geom's
/// usual path and the layer's position. A layer with a stat takes its y from the stat; computed per group of its color
/// column, before its position.
/// </summary>
/// <remarks>Make one with <see cref="Stat"/>. The compile takes the six kinds of rows G5b and G5c and refuses another.</remarks>
public abstract record StatSpec : SpecNode
{
    /// <summary>Initializes a new instance of the <see cref="StatSpec"/> class with a new <see cref="SpecNode.Id"/>.</summary>
    private protected StatSpec() { }
}

/// <summary>What a stat's y is, or a 2D bin's cell value (design §4.1, rows G5b and G5c).</summary>
public enum StatOutput
{
    /// <summary>A bin's or a cell's number of values; a density times the group's number of values.</summary>
    Count,

    /// <summary>
    /// A density: a bin's count over the group's number of values and the bin's width in the x scale's space (the bars' areas
    /// sum to 1); a kernel density estimate; a cell's count over the number of values and the cell's area.
    /// </summary>
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

    /// <summary>Gets what the bins' y is: <see cref="StatOutput.Count"/> by default.</summary>
    public StatOutput Output { get; init; }
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

/// <summary>
/// A kernel density estimate of a numeric x (design §4.1, row G5c), by the steps of R's <c>density()</c>: the values spread
/// linearly onto a grid, convolved with a Gaussian kernel, and interpolated at <see cref="Points"/> points over the layer's
/// range, in the x scale's space. Lines and areas take it.
/// </summary>
public sealed record DensityStatSpec : StatSpec
{
    /// <summary>
    /// Gets the kernel's standard deviation in the x scale's space, or null for R's <c>bw.nrd0</c> of each group; finite
    /// and above 0.
    /// </summary>
    public double? Bandwidth { get; init; }

    /// <summary>Gets the factor the bandwidth is multiplied by: finite and above 0, 1 by default.</summary>
    public double Adjust { get; init; } = 1;

    /// <summary>Gets the number of points the density is drawn at: from 2 to 4,096, 512 by default.</summary>
    public int Points { get; init; } = 512;

    /// <summary>Gets what the y is: <see cref="StatOutput.Density"/> by default, or the density times the group's number of values.</summary>
    public StatOutput Output { get; init; } = StatOutput.Density;
}

/// <summary>
/// A moving average of a numeric y over a numeric x (design §4.1, row G5c): at <see cref="Points"/> points over the
/// layer's x range, the mean of the y values of the rows whose x lies in [point − <see cref="Window"/> / 2, point +
/// <see cref="Window"/> / 2) in the x scale's space; NaN where no row does. Lines take it.
/// </summary>
public sealed record SmoothStatSpec : StatSpec
{
    /// <summary>Gets the window's width in the x scale's space (a time scale's unit on a time x): finite and above 0.</summary>
    public required double Window { get; init; }

    /// <summary>Gets the number of points the average is drawn at: from 2 to 131,072, 512 by default.</summary>
    public int Points { get; init; } = 512;
}

/// <summary>
/// Bins of a numeric x and a numeric y (design §4.1, row G5c): each axis binned as <see cref="BinStatSpec"/> does, a rect
/// a cell with values, filled by its value through the color scale. A rect takes it, with x in its <c>XMin</c> and y in its
/// <c>YMin</c>.
/// </summary>
public sealed record Bin2DStatSpec : StatSpec
{
    /// <summary>Gets the x bins: <see cref="Stat.Bin"/>'s default by default. Its output is <see cref="StatOutput.Count"/>.</summary>
    public BinStatSpec X { get; init; } = Stat.Bin();

    /// <summary>Gets the y bins: <see cref="Stat.Bin"/>'s default by default. Its output is <see cref="StatOutput.Count"/>.</summary>
    public BinStatSpec Y { get; init; } = Stat.Bin();

    /// <summary>Gets what a cell's value is: <see cref="StatOutput.Count"/> by default.</summary>
    public StatOutput Output { get; init; }
}

/// <summary>Makes the stats of a layer (design §4.1, rows G5b and G5c).</summary>
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
    public static BinStatSpec Bin(int maxBins = 10, double? step = null, double? anchor = null, StatOutput output = StatOutput.Count)
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

        CheckOutput(output);
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

    /// <summary>Returns the stat of a kernel density estimate of a numeric x (row G5c).</summary>
    /// <param name="bandwidth">The kernel's standard deviation in the x scale's space, or null for each group's <c>bw.nrd0</c>: finite and above 0.</param>
    /// <param name="adjust">The factor of the bandwidth: finite and above 0.</param>
    /// <param name="points">The number of points: from 2 to 4,096.</param>
    /// <param name="output">What the y is.</param>
    /// <returns>The stat.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of its range, or the output is not a named value.</exception>
    public static DensityStatSpec Density(double? bandwidth = null, double adjust = 1, int points = 512, StatOutput output = StatOutput.Density)
    {
        if (bandwidth is { } b && (!double.IsFinite(b) || b <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(bandwidth), b, "A bandwidth is finite and above 0.");
        }

        if (!double.IsFinite(adjust) || adjust <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(adjust), adjust, "The adjust is finite and above 0.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(points, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(points, LayerPlan.MaxDensityPoints);
        CheckOutput(output);
        return new DensityStatSpec
        {
            Bandwidth = bandwidth,
            Adjust = adjust,
            Points = points,
            Output = output,
        };
    }

    /// <summary>Returns the stat of a moving average of a numeric y over a numeric x (row G5c).</summary>
    /// <param name="window">The window's width in the x scale's space: finite and above 0.</param>
    /// <param name="points">The number of points: from 2 to 131,072.</param>
    /// <returns>The stat.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of its range.</exception>
    public static SmoothStatSpec Smooth(double window, int points = 512)
    {
        if (!double.IsFinite(window) || window <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(window), window, "A window is finite and above 0.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(points, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(points, LayerPlan.MaxElements);
        return new SmoothStatSpec { Window = window, Points = points };
    }

    /// <summary>Returns the stat of bins of a numeric x and a numeric y (row G5c).</summary>
    /// <param name="x">The x bins, or null for <see cref="Bin"/>'s default.</param>
    /// <param name="y">The y bins, or null for <see cref="Bin"/>'s default.</param>
    /// <param name="output">What a cell's value is.</param>
    /// <returns>The stat.</returns>
    /// <exception cref="ArgumentException">An axis's bins have an output other than <see cref="StatOutput.Count"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The output is not a named value.</exception>
    public static Bin2DStatSpec Bin2D(BinStatSpec? x = null, BinStatSpec? y = null, StatOutput output = StatOutput.Count)
    {
        if (x is { Output: not StatOutput.Count })
        {
            throw new ArgumentException("An axis's bins count; a cell's value is the 2D bins' output.", nameof(x));
        }

        if (y is { Output: not StatOutput.Count })
        {
            throw new ArgumentException("An axis's bins count; a cell's value is the 2D bins' output.", nameof(y));
        }

        CheckOutput(output);
        return new Bin2DStatSpec
        {
            X = x ?? Bin(),
            Y = y ?? Bin(),
            Output = output,
        };
    }

    private static void CheckOutput(StatOutput output)
    {
        if ((uint)output > (uint)StatOutput.Density)
        {
            throw new ArgumentOutOfRangeException(nameof(output), output, "The output is a named value.");
        }
    }
}
