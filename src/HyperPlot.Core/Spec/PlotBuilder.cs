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
