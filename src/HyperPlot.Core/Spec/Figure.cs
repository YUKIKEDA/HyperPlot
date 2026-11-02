namespace HyperPlot;

/// <summary>
/// A figure of several plots (design §4.5, row G6c): plots side by side in a concatenation or on a grid of cells, each
/// laid out in its cell as a plot alone. A cell holds a <see cref="PlotSpec"/> only; a figure does not nest.
/// </summary>
/// <remarks>
/// Make one with <see cref="Figure"/>; draw it with <see cref="Rendering.HeadlessRenderer.Render(FigureSpec)"/>. The plots
/// share nothing but their figure: their scales, legends, stats, and facets are their own. A figure has at most 256 plots,
/// and its plots at most 256 panels together.
/// </remarks>
public abstract record FigureSpec : SpecNode
{
    /// <summary>Initializes a new instance of the <see cref="FigureSpec"/> class with a new <see cref="SpecNode.Id"/>.</summary>
    private protected FigureSpec() { }

    /// <summary>Gets the title above the figure; null or empty for none.</summary>
    public string? Title { get; init; }

    /// <summary>
    /// Gets the figure's theme: the target's background, the figure's padding, and its title's text. Each plot draws with its
    /// own theme. <see cref="Theme.Light"/> by default.
    /// </summary>
    public Theme Theme { get; init; } = Theme.Light;

    /// <summary>Gets the space between cells, in DIPs: finite and at least 0; 20 by default, as Vega-Lite's spacing.</summary>
    public float Spacing { get; init; } = 20;
}

/// <summary>
/// Plots laid out left to right and top to bottom (design §4.5), as Vega-Lite's <c>concat</c>: a row of
/// <see cref="Columns"/> plots, the last row's plots on the left.
/// </summary>
public sealed record ConcatFigureSpec : FigureSpec
{
    /// <summary>Gets the plots, in their order: 1 to 256.</summary>
    public required IReadOnlyList<PlotSpec> Plots { get; init; }

    /// <summary>
    /// Gets the number of plots in a row: at least 1, or null for one row. More than the plots takes the number of plots.
    /// </summary>
    public int? Columns { get; init; }
}

/// <summary>
/// Plots on a grid of equal cells (design §4.5), each cell spanning one or more rows and columns, as matplotlib's
/// <c>GridSpec</c>. A place no cell covers stays the figure's background.
/// </summary>
public sealed record GridFigureSpec : FigureSpec
{
    /// <summary>Gets the number of rows: at least 1.</summary>
    public required int Rows { get; init; }

    /// <summary>Gets the number of columns: at least 1.</summary>
    public required int Columns { get; init; }

    /// <summary>Gets the cells: 1 to 256, inside the grid and apart from each other.</summary>
    public required IReadOnlyList<FigureCell> Cells { get; init; }
}

/// <summary>
/// A cell of a grid figure (design §4.1, row G6c): a plot, its first row and column from 0, and the rows and columns it
/// spans. A value, compared by its members: a new cell of the same values changes nothing.
/// </summary>
public sealed record FigureCell
{
    /// <summary>Gets the plot.</summary>
    public required PlotSpec Plot { get; init; }

    /// <summary>Gets the cell's first row, from 0.</summary>
    public required int Row { get; init; }

    /// <summary>Gets the cell's first column, from 0.</summary>
    public required int Column { get; init; }

    /// <summary>Gets the number of rows the cell spans: at least 1, 1 by default.</summary>
    public int RowSpan { get; init; } = 1;

    /// <summary>Gets the number of columns the cell spans: at least 1, 1 by default.</summary>
    public int ColumnSpan { get; init; } = 1;
}

/// <summary>Makes the figures of several plots (design §4.5, row G6c).</summary>
public static class Figure
{
    /// <summary>The most plots a figure holds, and the most panels its plots hold together (design §4.5).</summary>
    internal const int MaxPlots = 256;

    /// <summary>Returns a figure of plots left to right and top to bottom, a row of <paramref name="columns"/> plots.</summary>
    /// <param name="columns">The number of plots in a row: at least 1, or null for one row.</param>
    /// <param name="plots">The plots, in their order: 1 to 256.</param>
    /// <returns>The figure.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plots"/> or one of its plots is null.</exception>
    /// <exception cref="ArgumentException">There is no plot, or more than 256.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="columns"/> is below 1.</exception>
    public static ConcatFigureSpec Concat(int? columns, params PlotSpec[] plots)
    {
        if (columns is int count)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(count, 1, nameof(columns));
        }

        CheckPlots(plots);
        return new ConcatFigureSpec { Plots = [.. plots], Columns = columns };
    }

    /// <summary>Returns a figure of plots in one row: <see cref="Concat"/> with no number of columns.</summary>
    /// <param name="plots">The plots, left to right: 1 to 256.</param>
    /// <returns>The figure.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plots"/> or one of its plots is null.</exception>
    /// <exception cref="ArgumentException">There is no plot, or more than 256.</exception>
    public static ConcatFigureSpec HConcat(params PlotSpec[] plots) => Concat(null, plots);

    /// <summary>Returns a figure of plots in one column: <see cref="Concat"/> with one column.</summary>
    /// <param name="plots">The plots, top to bottom: 1 to 256.</param>
    /// <returns>The figure.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plots"/> or one of its plots is null.</exception>
    /// <exception cref="ArgumentException">There is no plot, or more than 256.</exception>
    public static ConcatFigureSpec VConcat(params PlotSpec[] plots) => Concat(1, plots);

    /// <summary>Returns a figure of cells on a grid of <paramref name="rows"/> × <paramref name="columns"/> equal cells.</summary>
    /// <param name="rows">The number of rows: at least 1.</param>
    /// <param name="columns">The number of columns: at least 1.</param>
    /// <param name="cells">The cells: 1 to 256, inside the grid and apart from each other.</param>
    /// <returns>The figure.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="cells"/> or one of its cells is null.</exception>
    /// <exception cref="ArgumentException">There is no cell or more than 256, or a cell lies outside the grid or over another.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rows"/> or <paramref name="columns"/> is below 1.</exception>
    public static GridFigureSpec Grid(int rows, int columns, params FigureCell[] cells)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentNullException.ThrowIfNull(cells);
        var figure = new GridFigureSpec
        {
            Rows = rows,
            Columns = columns,
            Cells = [.. cells],
        };
        CheckCells(figure);
        return figure;
    }

    /// <summary>Returns a cell of a grid figure.</summary>
    /// <param name="plot">The plot.</param>
    /// <param name="row">The cell's first row, from 0.</param>
    /// <param name="column">The cell's first column, from 0.</param>
    /// <param name="rowSpan">The number of rows the cell spans: at least 1.</param>
    /// <param name="columnSpan">The number of columns the cell spans: at least 1.</param>
    /// <returns>The cell.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plot"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A place is below 0 or a span below 1.</exception>
    public static FigureCell Cell(PlotSpec plot, int row, int column, int rowSpan = 1, int columnSpan = 1)
    {
        ArgumentNullException.ThrowIfNull(plot);
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfNegative(column);
        ArgumentOutOfRangeException.ThrowIfLessThan(rowSpan, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(columnSpan, 1);
        return new FigureCell
        {
            Plot = plot,
            Row = row,
            Column = column,
            RowSpan = rowSpan,
            ColumnSpan = columnSpan,
        };
    }

    /// <summary>
    /// Throws unless a grid's cells lie inside it and apart (design §4.5): the factory's check, and the compile's for cells set
    /// with <c>with</c>. The cells are compared two by two, at most 256, so that a check allocates nothing whatever the grid's
    /// size.
    /// </summary>
    internal static void CheckCells(GridFigureSpec grid)
    {
        if (grid.Cells is null)
        {
            throw new ArgumentNullException(nameof(grid), "The figure's Cells must not be null.");
        }

        CheckCount(grid.Cells.Count, "cell");
        for (var i = 0; i < grid.Cells.Count; i++)
        {
            var cell = grid.Cells[i] ?? throw new ArgumentNullException(nameof(grid), $"Cell {i} of the figure is null.");
            if (cell.Plot is null)
            {
                throw new ArgumentNullException(nameof(grid), $"The Plot of cell {i} is null.");
            }

            if (cell.Row < 0 || cell.Column < 0 || cell.RowSpan < 1 || cell.ColumnSpan < 1)
            {
                throw new ArgumentException(
                    $"Cell {i} lies at row {cell.Row} and column {cell.Column} over {cell.RowSpan} × {cell.ColumnSpan}; a place is at least 0 and a span at least 1 (design §4.5)."
                );
            }

            if ((long)cell.Row + cell.RowSpan > grid.Rows || (long)cell.Column + cell.ColumnSpan > grid.Columns)
            {
                throw new ArgumentException(
                    $"Cell {i} spans rows {cell.Row} to {(long)cell.Row + cell.RowSpan - 1} and columns {cell.Column} to {(long)cell.Column + cell.ColumnSpan - 1}, outside the grid of {grid.Rows} × {grid.Columns} (design §4.5)."
                );
            }

            for (var j = 0; j < i; j++)
            {
                var other = grid.Cells[j];
                if (
                    cell.Row < other.Row + other.RowSpan
                    && other.Row < cell.Row + cell.RowSpan
                    && cell.Column < other.Column + other.ColumnSpan
                    && other.Column < cell.Column + cell.ColumnSpan
                )
                {
                    throw new ArgumentException($"Cell {i} lies over cell {j} (design §4.5).");
                }
            }
        }
    }

    /// <summary>Throws unless a concatenation holds 1 to 256 plots, none null.</summary>
    internal static void CheckPlots(IReadOnlyList<PlotSpec>? plots)
    {
        ArgumentNullException.ThrowIfNull(plots);
        CheckCount(plots.Count, "plot");
        for (var i = 0; i < plots.Count; i++)
        {
            if (plots[i] is null)
            {
                throw new ArgumentNullException(nameof(plots), $"Plot {i} of the figure is null.");
            }
        }
    }

    private static void CheckCount(int count, string what)
    {
        if (count is < 1 or > MaxPlots)
        {
            throw new ArgumentException($"The figure has {count} {what}s; a figure has 1 to {MaxPlots} (design §4.5).");
        }
    }
}
