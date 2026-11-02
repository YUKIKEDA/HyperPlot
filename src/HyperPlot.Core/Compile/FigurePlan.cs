using System.Numerics;
using HyperPlot.Rendering;

namespace HyperPlot;

/// <summary>
/// A compiled figure of several plots (design §4.5, §4.6, §4.8, row G6c): the cells' rectangles, a plan a plot laid out in
/// its cell, the pyramids the plots share, and the figure's own list of the cells' backgrounds and the title. Each plot's
/// plan records into its own list, which stays when the plot does not change; the renderer draws the figure's list and the
/// plots' lists in one frame, copying none.
/// </summary>
/// <remarks>One thread at a time uses a figure's plan. Plots are matched to the last figure's by their places.</remarks>
internal sealed class FigurePlan : IDisposable
{
    private readonly PlotWorkerPool _pool;
    private readonly TextEngine _text;
    private readonly float _scale;
    private readonly int _width;
    private readonly int _height;
    private readonly ResidencyCache _images;
    private readonly bool _ownImages;
    private readonly PyramidStore _store = new();
    private readonly RenderList _list = new();
    private PlotPlan[] _plans = [];
    private (int Left, int Top, int Width, int Height)[] _cells = [];

    // The batches of the plots that extract this Render, gathered for one extraction; grown, never shrunk.
    private M4Batch[] _batches = [];
    private ScaleOrigin[] _origins = [];
    private double[][] _boundaries = [];

    internal FigurePlan(PlotWorkerPool pool, int width, int height, TextEngine text, float scale, ResidencyCache? images = null)
    {
        (_pool, _width, _height, _text, _scale) = (pool, width, height, text, scale);
        (_images, _ownImages) = (images ?? new ResidencyCache(), images is null);
    }

    /// <summary>Gets the figure's own list: the cells' backgrounds and the title, drawn before the plots' lists.</summary>
    internal RenderList List => _list;

    /// <summary>Gets the plots' plans, in the figure's order; a plan's list is drawn after the figure's.</summary>
    internal IReadOnlyList<PlotPlan> Plans => _plans;

    /// <summary>Gets the number of the plans' lists to draw: none when the cells are too small (design §4.6).</summary>
    internal int Drawn { get; private set; }

    /// <summary>Gets the cells' rectangles of the last layout, in the plots' order. Tests only.</summary>
    internal ReadOnlySpan<(int Left, int Top, int Width, int Height)> Cells => _cells;

    /// <summary>Gets the number of extractions, one a Render that any plot extracts in, for the tests of the joint extraction.</summary>
    internal int Extractions { get; private set; }

    /// <summary>Gets the store of the pyramids the plots share. Tests only.</summary>
    internal PyramidStore Store => _store;

    /// <summary>
    /// Compiles a figure against the last one drawn (design §4.8): checks it, lays its cells out (§4.6), compiles each plot in
    /// its cell, checks the panels of all the plots, waits, learns, and lays each plot out, extracts all the plots' batches
    /// at once, and records the plots that changed and the figure's list. Allocates nothing for the figure drawn last once
    /// warm.
    /// </summary>
    /// <exception cref="ArgumentNullException">The figure or a member, a plot, or a cell is null.</exception>
    /// <exception cref="ArgumentException">The figure does not check (design §4.5), or a plot does not (§4.8).</exception>
    /// <exception cref="InvalidOperationException">A plot's compile fails as <see cref="PlotPlan.Render(PlotSpec)"/> does.</exception>
    internal void Render(FigureSpec figure)
    {
        var count = Check(figure);
        Layout(figure, count);
        if (Drawn == 0)
        {
            return;
        }

        // Every plot is compiled before any waits, so that the figure's checks and each plot's come before the workers' passes.
        var panels = 0;
        for (var i = 0; i < count; i++)
        {
            _plans[i].Compile(PlotAt(figure, i));
            panels += _plans[i].PanelCount;
        }

        if (panels > Figure.MaxPlots)
        {
            throw new ArgumentException($"The figure's plots have {panels} panels together; a figure has at most {Figure.MaxPlots} (design §4.5).");
        }

        // The residency cache's frame is the figure's: its budget holds for all the plots (design §4.5).
        if (_ownImages)
        {
            _images.Sent();
        }

        _images.BeginFrame();
        var total = 0;
        for (var i = 0; i < count; i++)
        {
            if (_plans[i].Prepare())
            {
                total = Gather(_plans[i], total);
            }
        }

        if (total > 0)
        {
            _pool.ExtractTogether(_batches, _origins, _boundaries, total);
            Extractions++;
        }

        for (var i = 0; i < count; i++)
        {
            _plans[i].Finish();
        }

        RecordFigure(figure, count);
    }

    /// <summary>Releases the plans and the pyramids.</summary>
    public void Dispose()
    {
        foreach (var plan in _plans)
        {
            plan.Dispose();
        }

        _plans = [];
        _store.Dispose();
    }

    /// <summary>Returns the plot at a place of a figure: a concatenation's in its order, a grid's in its cells' order.</summary>
    private static PlotSpec PlotAt(FigureSpec figure, int index) =>
        figure is ConcatFigureSpec concat ? concat.Plots[index] : ((GridFigureSpec)figure).Cells[index].Plot;

    /// <summary>
    /// Throws unless the figure checks (design §4.5): its kind, its members, its plots or cells, and its theme; and returns its
    /// number of plots. A value set with <c>with</c> is checked here as the factories check theirs.
    /// </summary>
    private int Check(FigureSpec figure)
    {
        ArgumentNullException.ThrowIfNull(figure);
        if (figure.Theme is null)
        {
            throw new ArgumentNullException(nameof(figure), "The figure's Theme must not be null.");
        }

        figure.Theme.Validate(_scale);
        if (!float.IsFinite(figure.Spacing) || figure.Spacing < 0)
        {
            throw new ArgumentException($"The figure's Spacing is {figure.Spacing}; it is finite and at least 0 (design §4.5).");
        }

        switch (figure)
        {
            case ConcatFigureSpec concat:
                Figure.CheckPlots(concat.Plots);
                if (concat.Columns is < 1)
                {
                    throw new ArgumentException($"The figure's Columns is {concat.Columns}; it is at least 1 (design §4.5).");
                }

                return concat.Plots.Count;
            case GridFigureSpec grid:
                if (grid.Rows < 1 || grid.Columns < 1)
                {
                    throw new ArgumentException($"The figure's grid is {grid.Rows} × {grid.Columns}; each is at least 1 (design §4.5).");
                }

                Figure.CheckCells(grid);
                return grid.Cells.Count;
            default:
                throw new ArgumentException($"A {figure.GetType().Name} is not a figure the renderer lays out (design §4.5).", nameof(figure));
        }
    }

    /// <summary>
    /// Lays the cells out (design §4.6): the figure's padding and title band, equal cells S apart, the pixels ⌊⌋ leaves on the
    /// right and the bottom, a span its cells and the spaces between; and places each plot's plan in its cell. Makes the plans
    /// the figure's places need and releases those it no longer has.
    /// </summary>
    private void Layout(FigureSpec figure, int count)
    {
        var theme = figure.Theme;
        var padding = Round(theme.Padding);
        var gap = Round(theme.Spacing);
        var spacing = Round(figure.Spacing);
        var titleBand = string.IsNullOrEmpty(figure.Title) ? 0 : (int)MathF.Ceiling(_text.Measure(figure.Title, TitleStyle(theme)).Height) + gap;
        var (left, top) = (padding, padding + titleBand);
        var (areaWidth, areaHeight) = (_width - (2 * padding), _height - (2 * padding) - titleBand);
        var (rows, columns) = figure switch
        {
            ConcatFigureSpec concat => Dims(concat, count),
            GridFigureSpec grid => (grid.Rows, grid.Columns),
            _ => (1, 1),
        };
        var cellWidth = (int)Math.Floor((areaWidth - ((columns - 1) * (double)spacing)) / columns);
        var cellHeight = (int)Math.Floor((areaHeight - ((rows - 1) * (double)spacing)) / rows);
        if (_cells.Length != count)
        {
            _cells = new (int, int, int, int)[count];
        }

        Resize(count);
        Drawn = cellWidth < 1 || cellHeight < 1 ? 0 : count;
        for (var i = 0; i < count; i++)
        {
            var (row, column, rowSpan, columnSpan) = figure is GridFigureSpec g
                ? (g.Cells[i].Row, g.Cells[i].Column, g.Cells[i].RowSpan, g.Cells[i].ColumnSpan)
                : (i / columns, i % columns, 1, 1);
            var cell = (
                left + (column * (cellWidth + spacing)),
                top + (row * (cellHeight + spacing)),
                (columnSpan * cellWidth) + ((columnSpan - 1) * spacing),
                (rowSpan * cellHeight) + ((rowSpan - 1) * spacing)
            );

            // A new place's cell starts empty, so that its plan is placed.
            if (_cells[i] != cell)
            {
                _plans[i].Place(cell.Item1, cell.Item2, cell.Item3, cell.Item4);
            }

            _cells[i] = cell;
        }
    }

    /// <summary>Returns a concatenation's rows and columns (design §4.6): ⌈n / c⌉ rows of c = the columns given, at most n, or one row.</summary>
    private static (int Rows, int Columns) Dims(ConcatFigureSpec concat, int count)
    {
        var columns = Math.Min(concat.Columns ?? count, count);
        return ((count + columns - 1) / columns, columns);
    }

    /// <summary>Keeps the plans of the places the figure still has, makes the new places' plans, and disposes the rest.</summary>
    private void Resize(int count)
    {
        if (_plans.Length == count)
        {
            return;
        }

        var plans = new PlotPlan[count];
        for (var i = 0; i < Math.Max(count, _plans.Length); i++)
        {
            if (i < count)
            {
                plans[i] = i < _plans.Length ? _plans[i] : new PlotPlan(_pool, _width, _height, _text, _scale, _images, _store);
            }
            else
            {
                _plans[i].Dispose();
            }
        }

        _plans = plans;
    }

    /// <summary>Adds a plan's batches, origins, and boundaries after the ones gathered, growing the arrays when they are short.</summary>
    private int Gather(PlotPlan plan, int total)
    {
        var (batches, origins, boundaries) = plan.Extraction;
        if (_batches.Length < total + batches.Length)
        {
            var length = Math.Max(total + batches.Length, _batches.Length * 2);
            Array.Resize(ref _batches, length);
            Array.Resize(ref _origins, length);
            Array.Resize(ref _boundaries, length);
        }

        batches.CopyTo(_batches, total);
        origins.CopyTo(_origins, total);
        boundaries.CopyTo(_boundaries, total);
        return total + batches.Length;
    }

    /// <summary>Records the figure's list (design §4.6): each cell in its plot's background, then the title above the cells.</summary>
    private void RecordFigure(FigureSpec figure, int count)
    {
        _list.Clear();
        for (var i = 0; i < count; i++)
        {
            var (left, top, width, height) = _cells[i];
            _list.DrawRect(left, top, width, height, PlotAt(figure, i).Theme.Background);
        }

        if (string.IsNullOrEmpty(figure.Title))
        {
            return;
        }

        var theme = figure.Theme;
        var padding = Round(theme.Padding);
        var style = TitleStyle(theme);
        var middle = padding + ((_width - (2 * padding)) / 2f);
        _list.DrawText(figure.Title, new Vector2(middle - (_text.Measure(figure.Title, style).Width / 2), padding), style);
    }

    /// <summary>Returns the style of the figure's title: the theme's title size and foreground, aligned at its top left.</summary>
    private TextStyle TitleStyle(Theme theme) =>
        new(
            theme.Foreground,
            theme.TitleSize * _scale,
            theme.FontFamily,
            FontWeight.Normal,
            TextAlign.Left,
            TextBaseline.Top,
            TextRotation.None,
            theme.Locale
        );

    private int Round(float dips) => (int)MathF.Floor((dips * _scale) + 0.5f);
}
