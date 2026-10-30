using HyperPlot.Rendering;

namespace HyperPlot;

/// <summary>Where a step layer turns between two points (design §5.5.3).</summary>
public enum StepMode
{
    /// <summary>Across to the next point's x first, then up or down to its y.</summary>
    After,

    /// <summary>Up or down to the next point's y first, then across to its x.</summary>
    Before,

    /// <summary>Across to the middle of the two points' x, up or down, then across.</summary>
    Middle,
}

/// <summary>A step line through the points of two columns, drawn through M4 (design §4.1, §5.5.3).</summary>
public sealed record StepLayerSpec : LayerSpec
{
    /// <summary>Gets the X column: a <see cref="UniformColumn"/>, or a non-decreasing <see cref="Column{T}"/> with no NaN.</summary>
    public required ColumnRef X { get; init; }

    /// <summary>Gets the Y column: a <see cref="Column{T}"/>. NaN breaks the line.</summary>
    public required ColumnRef Y { get; init; }

    /// <summary>Gets the stroke, in DIPs: by default black, 1 wide, solid.</summary>
    public LineStyle Style { get; init; } = new(new Color(0, 0, 0, 255), 1);

    /// <summary>Gets where the line turns between two points: <see cref="StepMode.After"/> by default.</summary>
    public StepMode Mode { get; init; }
}

/// <summary>The region between a line through two columns and a baseline, filled (design §4.1, §5.5.3).</summary>
public sealed record AreaLayerSpec : LayerSpec
{
    /// <summary>Gets the X column: a <see cref="UniformColumn"/>, or a non-decreasing <see cref="Column{T}"/> with no NaN.</summary>
    public required ColumnRef X { get; init; }

    /// <summary>Gets the Y column: a <see cref="Column{T}"/>.</summary>
    public required ColumnRef Y { get; init; }

    /// <summary>Gets the fill: black at half alpha (0, 0, 0, 128) until row G7's palette.</summary>
    public Color Fill { get; init; } = new(0, 0, 0, 128);

    /// <summary>Gets the y the fill reaches, in data units: finite, 0 by default. Mapped outside the panel, it is the panel's edge.</summary>
    public double Baseline { get; init; }

    /// <summary>Gets the line along y, in DIPs, or null for none.</summary>
    public LineStyle? Outline { get; init; }
}

/// <summary>The region between two columns, filled (design §4.1, §5.5.3).</summary>
public sealed record BandLayerSpec : LayerSpec
{
    /// <summary>Gets the X column: a <see cref="UniformColumn"/>, or a non-decreasing <see cref="Column{T}"/> with no NaN.</summary>
    public required ColumnRef X { get; init; }

    /// <summary>Gets the lower column: a <see cref="Column{T}"/>.</summary>
    public required ColumnRef YMin { get; init; }

    /// <summary>Gets the upper column: a <see cref="Column{T}"/>.</summary>
    public required ColumnRef YMax { get; init; }

    /// <summary>Gets the fill: black at half alpha until row G7's palette.</summary>
    public Color Fill { get; init; } = new(0, 0, 0, 128);

    /// <summary>Gets the lines along both columns, in DIPs, or null for none.</summary>
    public LineStyle? Outline { get; init; }
}

/// <summary>Bars from a baseline to the values of a column (design §4.1, §5.5.3).</summary>
public sealed record BarLayerSpec : LayerSpec
{
    /// <summary>Gets the X column: a <see cref="UniformColumn"/>, or a non-decreasing <see cref="Column{T}"/> with no NaN.</summary>
    public required ColumnRef X { get; init; }

    /// <summary>Gets the Y column: a <see cref="Column{T}"/>.</summary>
    public required ColumnRef Y { get; init; }

    /// <summary>Gets the fill: black at half alpha until row G7's palette.</summary>
    public Color Fill { get; init; } = new(0, 0, 0, 128);

    /// <summary>Gets the y the bars start at, in data units: finite, 0 by default.</summary>
    public double Baseline { get; init; }

    /// <summary>
    /// Gets the bars' width in data units, finite and above 0, or null for 0.9 times the step of a uniform X or the mean
    /// spacing of a stored one (design §5.5.3).
    /// </summary>
    public double? Width { get; init; }
}

/// <summary>Rectangles whose corners are the values of four columns (design §4.1, §5.5.3).</summary>
public sealed record RectLayerSpec : LayerSpec
{
    /// <summary>Gets the column of the left edges.</summary>
    public required ColumnRef XMin { get; init; }

    /// <summary>Gets the column of the right edges.</summary>
    public required ColumnRef XMax { get; init; }

    /// <summary>Gets the column of the lower edges.</summary>
    public required ColumnRef YMin { get; init; }

    /// <summary>Gets the column of the upper edges.</summary>
    public required ColumnRef YMax { get; init; }

    /// <summary>Gets the fill: black at half alpha until row G7's palette.</summary>
    public Color Fill { get; init; } = new(0, 0, 0, 128);
}

/// <summary>Vertical lines at the values of an x column, or horizontal ones at a y column (design §4.1, §5.5.3).</summary>
public sealed record RuleLayerSpec : LayerSpec
{
    /// <summary>Gets the column of the vertical lines' x, non-decreasing; exactly one of X and Y is not default.</summary>
    public ColumnRef X { get; init; }

    /// <summary>Gets the column of the horizontal lines' y, non-decreasing; exactly one of X and Y is not default.</summary>
    public ColumnRef Y { get; init; }

    /// <summary>Gets the stroke, in DIPs: by default black, 1 wide, solid.</summary>
    public LineStyle Style { get; init; } = new(new Color(0, 0, 0, 255), 1);
}

/// <summary>Segments from (X, Y) to (XEnd, YEnd) (design §4.1, §5.5.3).</summary>
public sealed record SegmentLayerSpec : LayerSpec
{
    /// <summary>Gets the column of the starts' x.</summary>
    public required ColumnRef X { get; init; }

    /// <summary>Gets the column of the starts' y.</summary>
    public required ColumnRef Y { get; init; }

    /// <summary>Gets the column of the ends' x.</summary>
    public required ColumnRef XEnd { get; init; }

    /// <summary>Gets the column of the ends' y.</summary>
    public required ColumnRef YEnd { get; init; }

    /// <summary>Gets the stroke, in DIPs: by default black, 1 wide, solid.</summary>
    public LineStyle Style { get; init; } = new(new Color(0, 0, 0, 255), 1);
}

/// <summary>Vertical bars from YMin to YMax at each x, with caps (design §4.1, §5.5.3).</summary>
public sealed record ErrorBarLayerSpec : LayerSpec
{
    /// <summary>Gets the X column: a <see cref="UniformColumn"/>, or a non-decreasing <see cref="Column{T}"/> with no NaN.</summary>
    public required ColumnRef X { get; init; }

    /// <summary>Gets the lower column: a <see cref="Column{T}"/>.</summary>
    public required ColumnRef YMin { get; init; }

    /// <summary>Gets the upper column: a <see cref="Column{T}"/>.</summary>
    public required ColumnRef YMax { get; init; }

    /// <summary>Gets the stroke, in DIPs: by default black, 1 wide, solid.</summary>
    public LineStyle Style { get; init; } = new(new Color(0, 0, 0, 255), 1);

    /// <summary>Gets the width of the caps in DIPs: finite and at least 0, 6 by default; 0 draws none.</summary>
    public float CapWidth { get; init; } = 6;
}

/// <summary>A layer that a user's <see cref="IGeom"/> learns and records (design §4.1, §5.5.3).</summary>
public sealed record CustomLayerSpec : LayerSpec
{
    /// <summary>Initializes a new instance of the <see cref="CustomLayerSpec"/> class with a new <see cref="SpecNode.Id"/>.</summary>
    /// <param name="geom">The geom.</param>
    /// <exception cref="ArgumentNullException"><paramref name="geom"/> is null.</exception>
    public CustomLayerSpec(IGeom geom)
    {
        ArgumentNullException.ThrowIfNull(geom);
        Geom = geom;
    }

    /// <summary>Gets the geom. A new geom, or a geom whose content changed, is a new node: the plan does not see inside it.</summary>
    public IGeom Geom { get; }
}
