using System.Numerics;
using HyperPlot.Rendering;

namespace HyperPlot;

/// <summary>
/// Builds a <see cref="PlotSpec"/> (design §4.1). It makes the immutable spec only and reads no data. Make one with
/// <see cref="Plot.Create(DataTable)"/>.
/// </summary>
public sealed class PlotBuilder
{
    private readonly DataTable _data;
    private readonly List<LayerSpec> _layers = [];
    private ScaleSpec? _scaleX;
    private ScaleSpec? _scaleY;
    private string? _title;
    private Theme? _theme;
    private bool _built;

    internal PlotBuilder(DataTable data) => _data = data;

    /// <summary>Adds a line layer, drawn after the layers added before it.</summary>
    /// <typeparam name="TX">The element type of the X column.</typeparam>
    /// <typeparam name="TY">The element type of the Y column.</typeparam>
    /// <param name="x">The X column of the plot's table: evenly spaced, or stored, non-decreasing, and without NaN.</param>
    /// <param name="y">The Y column of the plot's table: a stored <see cref="Column{T}"/>.</param>
    /// <param name="configure">Sets the stroke; called once, here.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">
    /// A reference is <see langword="default"/> or names another table, <paramref name="y"/> is not a <see cref="Column{T}"/>,
    /// or the stroke is out of range (design §5.1).
    /// </exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> was called.</exception>
    public PlotBuilder Line<TX, TY>(ColumnRef<TX> x, ColumnRef<TY> y, Action<LineBuilder>? configure = null)
        where TX : unmanaged, INumber<TX>
        where TY : unmanaged, INumber<TY>
    {
        ThrowIfBuilt();
        CheckReference(x, nameof(x));
        CheckReference(y, nameof(y));
        if (y.Column is not Column<TY>)
        {
            throw new ArgumentException(
                $"The Y column \"{_data.GetName(y.Index)}\" is a {y.Column.KindName}; a line's Y is a stored column.",
                nameof(y)
            );
        }

        var style = new LineBuilder();
        configure?.Invoke(style);
        RenderList.ValidateStyle(style.Style);
        _layers.Add(
            new LineLayerSpec
            {
                X = x,
                Y = y,
                Style = style.Style,
                Label = style.LabelText,
            }
        );
        return this;
    }

    /// <summary>Adds a step layer, drawn after the layers added before it (design §5.5.3).</summary>
    /// <typeparam name="TX">The element type of the X column.</typeparam>
    /// <typeparam name="TY">The element type of the Y column.</typeparam>
    /// <param name="x">The X column of the plot's table: evenly spaced, or stored, non-decreasing, and without NaN.</param>
    /// <param name="y">The Y column of the plot's table: a stored <see cref="Column{T}"/>.</param>
    /// <param name="mode">Where the line turns between two points.</param>
    /// <param name="configure">Sets the stroke and the label; called once, here.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">
    /// A reference is <see langword="default"/> or names another table, <paramref name="y"/> is not a <see cref="Column{T}"/>,
    /// or the stroke is out of range (design §5.1).
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not a named value.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> was called.</exception>
    public PlotBuilder Step<TX, TY>(ColumnRef<TX> x, ColumnRef<TY> y, StepMode mode = StepMode.After, Action<LineBuilder>? configure = null)
        where TX : unmanaged, INumber<TX>
        where TY : unmanaged, INumber<TY>
    {
        ThrowIfBuilt();
        CheckReference(x, nameof(x));
        CheckStored(y, nameof(y), "a step's Y");
        if ((uint)mode > (uint)StepMode.Middle)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "The mode is a named value.");
        }

        var style = Stroke(configure);
        _layers.Add(
            new StepLayerSpec
            {
                X = x,
                Y = y,
                Style = style.Style,
                Mode = mode,
                Label = style.LabelText,
            }
        );
        return this;
    }

    /// <summary>Adds an area layer: the region between y and a baseline, filled (design §5.5.3).</summary>
    /// <typeparam name="TX">The element type of the X column.</typeparam>
    /// <typeparam name="TY">The element type of the Y column.</typeparam>
    /// <param name="x">The X column: evenly spaced, or stored, non-decreasing, and without NaN.</param>
    /// <param name="y">The Y column: a stored <see cref="Column{T}"/>.</param>
    /// <param name="baseline">The y the fill reaches, in data units: finite.</param>
    /// <param name="configure">Sets the fill, the outline, and the label; called once, here.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">
    /// A reference is <see langword="default"/> or names another table, <paramref name="y"/> is not a <see cref="Column{T}"/>,
    /// or the outline is out of range (design §5.1).
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="baseline"/> is not finite.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> was called.</exception>
    public PlotBuilder Area<TX, TY>(ColumnRef<TX> x, ColumnRef<TY> y, double baseline = 0, Action<AreaBuilder>? configure = null)
        where TX : unmanaged, INumber<TX>
        where TY : unmanaged, INumber<TY>
    {
        ThrowIfBuilt();
        CheckReference(x, nameof(x));
        CheckStored(y, nameof(y), "an area's Y");
        CheckFinite(baseline, nameof(baseline));
        var area = Filled(configure);
        _layers.Add(
            new AreaLayerSpec
            {
                X = x,
                Y = y,
                Baseline = baseline,
                Fill = area.FillColor,
                Outline = area.OutlineStyle,
                Label = area.LabelText,
            }
        );
        return this;
    }

    /// <summary>Adds a band layer: the region between two columns, filled (design §5.5.3).</summary>
    /// <typeparam name="TX">The element type of the X column.</typeparam>
    /// <typeparam name="TY">The element type of the two columns.</typeparam>
    /// <param name="x">The X column: evenly spaced, or stored, non-decreasing, and without NaN.</param>
    /// <param name="yMin">The lower column: a stored <see cref="Column{T}"/>.</param>
    /// <param name="yMax">The upper column: a stored <see cref="Column{T}"/>.</param>
    /// <param name="configure">Sets the fill, the outline, and the label; called once, here.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">
    /// A reference is <see langword="default"/> or names another table, a Y column is not a <see cref="Column{T}"/>, or the
    /// outline is out of range (design §5.1).
    /// </exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> was called.</exception>
    public PlotBuilder Band<TX, TY>(ColumnRef<TX> x, ColumnRef<TY> yMin, ColumnRef<TY> yMax, Action<AreaBuilder>? configure = null)
        where TX : unmanaged, INumber<TX>
        where TY : unmanaged, INumber<TY>
    {
        ThrowIfBuilt();
        CheckReference(x, nameof(x));
        CheckStored(yMin, nameof(yMin), "a band's YMin");
        CheckStored(yMax, nameof(yMax), "a band's YMax");
        var area = Filled(configure);
        _layers.Add(
            new BandLayerSpec
            {
                X = x,
                YMin = yMin,
                YMax = yMax,
                Fill = area.FillColor,
                Outline = area.OutlineStyle,
                Label = area.LabelText,
            }
        );
        return this;
    }

    /// <summary>Adds a bar layer: bars from a baseline to y (design §5.5.3).</summary>
    /// <typeparam name="TX">The element type of the X column.</typeparam>
    /// <typeparam name="TY">The element type of the Y column.</typeparam>
    /// <param name="x">The X column: evenly spaced, or stored, non-decreasing, and without NaN.</param>
    /// <param name="y">The Y column: a stored <see cref="Column{T}"/>.</param>
    /// <param name="baseline">The y the bars start at, in data units: finite.</param>
    /// <param name="width">The bars' width in data units, finite and above 0, or null for the default (design §5.5.3).</param>
    /// <param name="configure">Sets the fill and the label; called once, here.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">
    /// A reference is <see langword="default"/> or names another table, or <paramref name="y"/> is not a <see cref="Column{T}"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="baseline"/> is not finite, or <paramref name="width"/> is not finite and above 0.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> was called.</exception>
    public PlotBuilder Bar<TX, TY>(ColumnRef<TX> x, ColumnRef<TY> y, double baseline = 0, double? width = null, Action<FillBuilder>? configure = null)
        where TX : unmanaged, INumber<TX>
        where TY : unmanaged, INumber<TY>
    {
        ThrowIfBuilt();
        CheckReference(x, nameof(x));
        CheckStored(y, nameof(y), "a bar's Y");
        CheckFinite(baseline, nameof(baseline));
        if (width is { } w && (!double.IsFinite(w) || w <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(width), w, "The width is finite and above 0.");
        }

        var fill = new FillBuilder();
        configure?.Invoke(fill);
        _layers.Add(
            new BarLayerSpec
            {
                X = x,
                Y = y,
                Baseline = baseline,
                Width = width,
                Fill = fill.FillColor,
                Label = fill.LabelText,
            }
        );
        return this;
    }

    /// <summary>Adds a rect layer: rectangles from four columns (design §5.5.3).</summary>
    /// <typeparam name="TX">The element type of the x columns.</typeparam>
    /// <typeparam name="TY">The element type of the y columns.</typeparam>
    /// <param name="xMin">The column of the left edges.</param>
    /// <param name="xMax">The column of the right edges.</param>
    /// <param name="yMin">The column of the lower edges.</param>
    /// <param name="yMax">The column of the upper edges.</param>
    /// <param name="configure">Sets the fill and the label; called once, here.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">A reference is <see langword="default"/> or names another table.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> was called.</exception>
    public PlotBuilder Rect<TX, TY>(
        ColumnRef<TX> xMin,
        ColumnRef<TX> xMax,
        ColumnRef<TY> yMin,
        ColumnRef<TY> yMax,
        Action<FillBuilder>? configure = null
    )
        where TX : unmanaged, INumber<TX>
        where TY : unmanaged, INumber<TY>
    {
        ThrowIfBuilt();
        CheckReference(xMin, nameof(xMin));
        CheckReference(xMax, nameof(xMax));
        CheckReference(yMin, nameof(yMin));
        CheckReference(yMax, nameof(yMax));
        var fill = new FillBuilder();
        configure?.Invoke(fill);
        _layers.Add(
            new RectLayerSpec
            {
                XMin = xMin,
                XMax = xMax,
                YMin = yMin,
                YMax = yMax,
                Fill = fill.FillColor,
                Label = fill.LabelText,
            }
        );
        return this;
    }

    /// <summary>Adds vertical lines at the values of an x column (design §5.5.3).</summary>
    /// <typeparam name="T">The element type of the column.</typeparam>
    /// <param name="x">The column: evenly spaced, or stored, non-decreasing, and without NaN.</param>
    /// <param name="configure">Sets the stroke and the label; called once, here.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">The reference is <see langword="default"/> or names another table, or the stroke is out of range.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> was called.</exception>
    public PlotBuilder VRule<T>(ColumnRef<T> x, Action<LineBuilder>? configure = null)
        where T : unmanaged, INumber<T>
    {
        ThrowIfBuilt();
        CheckReference(x, nameof(x));
        var style = Stroke(configure);
        _layers.Add(
            new RuleLayerSpec
            {
                X = x,
                Style = style.Style,
                Label = style.LabelText,
            }
        );
        return this;
    }

    /// <summary>Adds horizontal lines at the values of a y column (design §5.5.3).</summary>
    /// <typeparam name="T">The element type of the column.</typeparam>
    /// <param name="y">The column: evenly spaced, or stored, non-decreasing, and without NaN.</param>
    /// <param name="configure">Sets the stroke and the label; called once, here.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">The reference is <see langword="default"/> or names another table, or the stroke is out of range.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> was called.</exception>
    public PlotBuilder HRule<T>(ColumnRef<T> y, Action<LineBuilder>? configure = null)
        where T : unmanaged, INumber<T>
    {
        ThrowIfBuilt();
        CheckReference(y, nameof(y));
        var style = Stroke(configure);
        _layers.Add(
            new RuleLayerSpec
            {
                Y = y,
                Style = style.Style,
                Label = style.LabelText,
            }
        );
        return this;
    }

    /// <summary>Adds segments from (x, y) to (xEnd, yEnd) (design §5.5.3).</summary>
    /// <typeparam name="TX">The element type of the x columns.</typeparam>
    /// <typeparam name="TY">The element type of the y columns.</typeparam>
    /// <param name="x">The column of the starts' x.</param>
    /// <param name="y">The column of the starts' y.</param>
    /// <param name="xEnd">The column of the ends' x.</param>
    /// <param name="yEnd">The column of the ends' y.</param>
    /// <param name="configure">Sets the stroke and the label; called once, here.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">A reference is <see langword="default"/> or names another table, or the stroke is out of range.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> was called.</exception>
    public PlotBuilder Segment<TX, TY>(
        ColumnRef<TX> x,
        ColumnRef<TY> y,
        ColumnRef<TX> xEnd,
        ColumnRef<TY> yEnd,
        Action<LineBuilder>? configure = null
    )
        where TX : unmanaged, INumber<TX>
        where TY : unmanaged, INumber<TY>
    {
        ThrowIfBuilt();
        CheckReference(x, nameof(x));
        CheckReference(y, nameof(y));
        CheckReference(xEnd, nameof(xEnd));
        CheckReference(yEnd, nameof(yEnd));
        var style = Stroke(configure);
        _layers.Add(
            new SegmentLayerSpec
            {
                X = x,
                Y = y,
                XEnd = xEnd,
                YEnd = yEnd,
                Style = style.Style,
                Label = style.LabelText,
            }
        );
        return this;
    }

    /// <summary>Adds error bars from yMin to yMax at each x, with caps (design §5.5.3).</summary>
    /// <typeparam name="TX">The element type of the X column.</typeparam>
    /// <typeparam name="TY">The element type of the two columns.</typeparam>
    /// <param name="x">The X column: evenly spaced, or stored, non-decreasing, and without NaN.</param>
    /// <param name="yMin">The lower column: a stored <see cref="Column{T}"/>.</param>
    /// <param name="yMax">The upper column: a stored <see cref="Column{T}"/>.</param>
    /// <param name="capWidth">The width of the caps in DIPs: finite and at least 0; 0 draws none.</param>
    /// <param name="configure">Sets the stroke and the label; called once, here.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">
    /// A reference is <see langword="default"/> or names another table, a Y column is not a <see cref="Column{T}"/>, or the
    /// stroke is out of range.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capWidth"/> is not finite and at least 0.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> was called.</exception>
    public PlotBuilder ErrorBar<TX, TY>(
        ColumnRef<TX> x,
        ColumnRef<TY> yMin,
        ColumnRef<TY> yMax,
        float capWidth = 6,
        Action<LineBuilder>? configure = null
    )
        where TX : unmanaged, INumber<TX>
        where TY : unmanaged, INumber<TY>
    {
        ThrowIfBuilt();
        CheckReference(x, nameof(x));
        CheckStored(yMin, nameof(yMin), "an error bar's YMin");
        CheckStored(yMax, nameof(yMax), "an error bar's YMax");
        if (!float.IsFinite(capWidth) || capWidth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capWidth), capWidth, "The cap width is finite and at least 0.");
        }

        var style = Stroke(configure);
        _layers.Add(
            new ErrorBarLayerSpec
            {
                X = x,
                YMin = yMin,
                YMax = yMax,
                CapWidth = capWidth,
                Style = style.Style,
                Label = style.LabelText,
            }
        );
        return this;
    }

    /// <summary>Adds a user's geom (design §5.5.3).</summary>
    /// <param name="geom">The geom.</param>
    /// <param name="label">The legend's label; null or empty leaves the layer out of the legend.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="geom"/> is null.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> was called.</exception>
    public PlotBuilder Custom(IGeom geom, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(geom);
        ThrowIfBuilt();
        _layers.Add(new CustomLayerSpec(geom) { Label = label });
        return this;
    }

    /// <summary>Sets the scale of x.</summary>
    /// <param name="scale">The scale.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scale"/> is null.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> was called.</exception>
    public PlotBuilder ScaleX(ScaleSpec scale)
    {
        ArgumentNullException.ThrowIfNull(scale);
        ThrowIfBuilt();
        _scaleX = scale;
        return this;
    }

    /// <summary>Sets the scale of y.</summary>
    /// <param name="scale">The scale.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scale"/> is null.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> was called.</exception>
    public PlotBuilder ScaleY(ScaleSpec scale)
    {
        ArgumentNullException.ThrowIfNull(scale);
        ThrowIfBuilt();
        _scaleY = scale;
        return this;
    }

    /// <summary>Sets the title above the plot.</summary>
    /// <param name="title">The title; null or empty for none.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> was called.</exception>
    public PlotBuilder Title(string? title)
    {
        ThrowIfBuilt();
        _title = title;
        return this;
    }

    /// <summary>Sets the theme of the guides.</summary>
    /// <param name="theme">The theme.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="theme"/> is null.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> was called.</exception>
    public PlotBuilder Theme(Theme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ThrowIfBuilt();
        _theme = theme;
        return this;
    }

    /// <summary>Makes the spec. A plot of no layers draws nothing. The builder cannot be used afterwards.</summary>
    /// <returns>The spec.</returns>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> was called before.</exception>
    public PlotSpec Build()
    {
        ThrowIfBuilt();
        _built = true;
        return new PlotSpec
        {
            Data = _data,
            Layers = [.. _layers],
            ScaleX = _scaleX ?? Scale.Linear(),
            ScaleY = _scaleY ?? Scale.Linear(),
            Title = _title,
            Theme = _theme ?? HyperPlot.Theme.Light,
        };
    }

    private static void CheckFinite(double value, string name)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(name, value, "The value is finite.");
        }
    }

    private static LineBuilder Stroke(Action<LineBuilder>? configure)
    {
        var style = new LineBuilder();
        configure?.Invoke(style);
        RenderList.ValidateStyle(style.Style);
        return style;
    }

    private static AreaBuilder Filled(Action<AreaBuilder>? configure)
    {
        var area = new AreaBuilder();
        configure?.Invoke(area);
        if (area.OutlineStyle is { } outline)
        {
            RenderList.ValidateStyle(outline);
        }

        return area;
    }

    private void CheckStored<T>(ColumnRef<T> reference, string name, string what)
        where T : unmanaged, INumber<T>
    {
        CheckReference(reference, name);
        if (reference.Column is not Column<T>)
        {
            throw new ArgumentException(
                $"The column \"{_data.GetName(reference.Index)}\" is a {reference.Column.KindName}; {what} is a stored column.",
                name
            );
        }
    }

    private void CheckReference(ColumnRef reference, string name)
    {
        if (reference.IsDefault)
        {
            throw new ArgumentException("The reference is default and names no column.", name);
        }

        if (!ReferenceEquals(reference.Table, _data))
        {
            throw new ArgumentException("The column belongs to another table than the plot's.", name);
        }
    }

    private void ThrowIfBuilt()
    {
        if (_built)
        {
            throw new InvalidOperationException("The plot was built; start another with Plot.Create.");
        }
    }
}
