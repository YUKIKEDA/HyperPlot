namespace HyperPlot;

/// <summary>Which position scales the panels of a facet share (design §4.5, row G6b), as ggplot2's <c>scales</c> does.</summary>
public enum FacetScales
{
    /// <summary>Both x and y are shared: one domain learned from every panel.</summary>
    Fixed,

    /// <summary>x is learned from each panel's layers; y is shared.</summary>
    FreeX,

    /// <summary>y is learned from each panel's layers; x is shared.</summary>
    FreeY,

    /// <summary>Both x and y are learned from each panel's layers.</summary>
    Free,
}

/// <summary>
/// Splits a plot into panels by the categories of columns of its table (design §4.5, row G6b). Each panel draws the
/// layers over the rows of its categories; a category without rows makes no panel.
/// </summary>
/// <remarks>
/// Make one with <see cref="Facet"/>. A free direction learns its continuous domain from each panel; a band scale keeps every
/// category of its column in every panel, and a domain given to a scale applies to every panel. The legend is one for the
/// figure. A plot has at most 256 panels.
/// </remarks>
public abstract record FacetSpec : SpecNode
{
    /// <summary>Initializes a new instance of the <see cref="FacetSpec"/> class with a new <see cref="SpecNode.Id"/>.</summary>
    private protected FacetSpec() { }

    /// <summary>Gets which position scales the panels share: <see cref="FacetScales.Fixed"/> by default.</summary>
    public FacetScales Scales { get; init; }
}

/// <summary>
/// A panel for each category of one column, laid out left to right and wrapped into rows (design §4.5), as ggplot2's
/// <c>facet_wrap</c> does.
/// </summary>
public sealed record FacetWrapSpec : FacetSpec
{
    /// <summary>Gets the categorical column of the plot's table that splits the rows.</summary>
    public required ColumnRef By { get; init; }

    /// <summary>
    /// Gets the number of columns of panels: at least 1, or null to take R's <c>n2mfrow</c> of the number of panels.
    /// </summary>
    public int? ColumnCount { get; init; }
}

/// <summary>
/// A panel for each pair of a row category and a column category, laid out on a grid (design §4.5), as ggplot2's
/// <c>facet_grid</c> does. A pair without rows is an empty panel.
/// </summary>
public sealed record FacetGridSpec : FacetSpec
{
    /// <summary>Gets the categorical column whose categories make the rows of panels, or null for one row.</summary>
    public ColumnRef? Rows { get; init; }

    /// <summary>Gets the categorical column whose categories make the columns of panels, or null for one column.</summary>
    public ColumnRef? Columns { get; init; }
}

/// <summary>Makes the facets of a plot (design §4.5, row G6b).</summary>
public static class Facet
{
    /// <summary>Returns a facet that makes a panel for each category of a column and wraps the panels into rows.</summary>
    /// <param name="by">The categorical column of the plot's table.</param>
    /// <param name="columns">The number of columns of panels: at least 1, or null to take R's <c>n2mfrow</c>.</param>
    /// <param name="scales">Which position scales the panels share.</param>
    /// <returns>The facet.</returns>
    /// <exception cref="ArgumentException"><paramref name="by"/> is <see langword="default"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="columns"/> is below 1, or <paramref name="scales"/> is not a named value.
    /// </exception>
    public static FacetWrapSpec Wrap(CategoryRef by, int? columns = null, FacetScales scales = FacetScales.Fixed)
    {
        CheckDefault(by, nameof(by));
        if (columns is int count)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(count, 1, nameof(columns));
        }

        CheckScales(scales);
        return new FacetWrapSpec
        {
            By = by.ToColumnRef(),
            ColumnCount = columns,
            Scales = scales,
        };
    }

    /// <summary>Returns a facet that makes a panel for each pair of a row category and a column category, laid out on a grid.</summary>
    /// <param name="rows">The categorical column whose categories make the rows, or null for one row.</param>
    /// <param name="columns">The categorical column whose categories make the columns, or null for one column.</param>
    /// <param name="scales">Which position scales the panels share.</param>
    /// <returns>The facet.</returns>
    /// <exception cref="ArgumentException">
    /// Both <paramref name="rows"/> and <paramref name="columns"/> are null, or one is <see langword="default"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scales"/> is not a named value.</exception>
    public static FacetGridSpec Grid(CategoryRef? rows = null, CategoryRef? columns = null, FacetScales scales = FacetScales.Fixed)
    {
        if (rows is null && columns is null)
        {
            throw new ArgumentException("A grid splits by rows, by columns, or by both; both are null.", nameof(rows));
        }

        if (rows is CategoryRef r)
        {
            CheckDefault(r, nameof(rows));
        }

        if (columns is CategoryRef c)
        {
            CheckDefault(c, nameof(columns));
        }

        CheckScales(scales);
        return new FacetGridSpec
        {
            Rows = rows?.ToColumnRef(),
            Columns = columns?.ToColumnRef(),
            Scales = scales,
        };
    }

    private static void CheckDefault(CategoryRef reference, string name)
    {
        if (reference.ToColumnRef().IsDefault)
        {
            throw new ArgumentException("The reference is default and names no column.", name);
        }
    }

    private static void CheckScales(FacetScales scales)
    {
        if ((uint)scales > (uint)FacetScales.Free)
        {
            throw new ArgumentOutOfRangeException(nameof(scales), scales, "The scales are Fixed, FreeX, FreeY, or Free.");
        }
    }
}
