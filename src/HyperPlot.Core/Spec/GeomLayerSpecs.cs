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

    /// <summary>Gets the Y column: a <see cref="Column{T}"/>. NaN breaks the line. A layer binned by its stat has none (row G5b).</summary>
    public ColumnRef Y { get; init; }

    /// <summary>Gets the stroke, in DIPs: by default the layer's series color (design §4.1), 1 wide, solid.</summary>
    public StrokeSpec Style { get; init; } = new(null);

    /// <summary>Gets where the line turns between two points: <see cref="StepMode.After"/> by default.</summary>
    public StepMode Mode { get; init; }
}

/// <summary>The region between a line through two columns and a baseline, filled (design §4.1, §5.5.3).</summary>
public sealed record AreaLayerSpec : LayerSpec
{
    /// <summary>Gets the X column: a <see cref="UniformColumn"/>, or a non-decreasing <see cref="Column{T}"/> with no NaN.</summary>
    public required ColumnRef X { get; init; }

    /// <summary>Gets the Y column: a <see cref="Column{T}"/>. A layer binned by its stat has none (row G5b).</summary>
    public ColumnRef Y { get; init; }

    /// <summary>Gets the fill, or null for the layer's series color at alpha 128 (design §4.1): it lies over other layers.</summary>
    public Color? Fill { get; init; }

    /// <summary>Gets the y the fill reaches, in data units: finite, 0 by default. Mapped outside the panel, it is the panel's edge.</summary>
    public double Baseline { get; init; }

    /// <summary>Gets the line along y, in DIPs, or null for none. Without a color, it takes the layer's series color, opaque.</summary>
    public StrokeSpec? Outline { get; init; }
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

    /// <summary>Gets the fill, or null for the layer's series color at alpha 128 (design §4.1): it lies over other layers.</summary>
    public Color? Fill { get; init; }

    /// <summary>Gets the lines along both columns, in DIPs, or null for none. Without a color, they take the series color, opaque.</summary>
    public StrokeSpec? Outline { get; init; }
}

/// <summary>Bars from a baseline to the values of a column (design §4.1, §5.5.3).</summary>
public sealed record BarLayerSpec : LayerSpec
{
    /// <summary>Gets the X column: a <see cref="UniformColumn"/>, or a non-decreasing <see cref="Column{T}"/> with no NaN.</summary>
    public required ColumnRef X { get; init; }

    /// <summary>
    /// Gets the Y column: a <see cref="Column{T}"/>. A layer binned or counted by its stat has none; a summary's are the values
    /// it summarizes (row G5b).
    /// </summary>
    public ColumnRef Y { get; init; }

    /// <summary>Gets the fill, or null for the layer's series color, opaque (design §4.1).</summary>
    public Color? Fill { get; init; }

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
    /// <summary>
    /// Gets the column of the left edges. Not required since row G5c: a layer with 2D bins holds its x here; a layer without
    /// a stat has all four, or the compile throws.
    /// </summary>
    public ColumnRef XMin { get; init; }

    /// <summary>Gets the column of the right edges: the default or <see cref="XMin"/> with 2D bins.</summary>
    public ColumnRef XMax { get; init; }

    /// <summary>Gets the column of the lower edges. A layer with 2D bins holds its y here.</summary>
    public ColumnRef YMin { get; init; }

    /// <summary>Gets the column of the upper edges: the default or <see cref="YMin"/> with 2D bins.</summary>
    public ColumnRef YMax { get; init; }

    /// <summary>Gets the fill, or null for the layer's series color, opaque (design §4.1).</summary>
    public Color? Fill { get; init; }
}

/// <summary>Vertical lines at the values of an x column, or horizontal ones at a y column (design §4.1, §5.5.3).</summary>
public sealed record RuleLayerSpec : LayerSpec
{
    /// <summary>Gets the column of the vertical lines' x, non-decreasing; exactly one of X and Y is not default.</summary>
    public ColumnRef X { get; init; }

    /// <summary>Gets the column of the horizontal lines' y, non-decreasing; exactly one of X and Y is not default.</summary>
    public ColumnRef Y { get; init; }

    /// <summary>Gets the stroke, in DIPs: by default the layer's series color (design §4.1), 1 wide, solid.</summary>
    public StrokeSpec Style { get; init; } = new(null);
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

    /// <summary>Gets the stroke, in DIPs: by default the layer's series color (design §4.1), 1 wide, solid.</summary>
    public StrokeSpec Style { get; init; } = new(null);
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

    /// <summary>Gets the stroke, in DIPs: by default the layer's series color (design §4.1), 1 wide, solid.</summary>
    public StrokeSpec Style { get; init; } = new(null);

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

    /// <summary>
    /// Gets the geom. The plan does not see inside it: when its content changes, make a new node, of the same geom or a new
    /// one, and the plot learns and records it again.
    /// </summary>
    public IGeom Geom { get; }
}

/// <summary>
/// A label a row at the point of two columns (design §4.1, §5.5.3, row G8): a category's name or a number written by a
/// format. The text layers of a plot draw at most 4,096 rows together.
/// </summary>
public sealed record TextLayerSpec : LayerSpec
{
    /// <summary>Gets the column of the points' x: numbers, or categories put at the middle of their band. Unordered.</summary>
    public required ColumnRef X { get; init; }

    /// <summary>Gets the column of the points' y: numbers.</summary>
    public required ColumnRef Y { get; init; }

    /// <summary>
    /// Gets the column of the labels: categories, whose names are drawn, or numbers, written by <see cref="Format"/>. Integer
    /// columns are written as doubles: a long past 2^53 is rounded, and a long time is written as a number.
    /// </summary>
    public required ColumnRef Text { get; init; }

    /// <summary>
    /// Gets the format of numeric labels, or null for "G6" in the invariant culture; categories take none. A format is the
    /// same while it is the same delegate: pass a static lambda or a method group, since a new delegate each frame writes
    /// every label again each frame.
    /// </summary>
    public TickFormatter? Format { get; init; }

    /// <summary>Gets the labels' color, or null for the layer's series color (design §4.1).</summary>
    public Color? Color { get; init; }

    /// <summary>Gets the labels' size in DIPs: finite, above 0, and at most 512; 12 by default.</summary>
    public float Size { get; init; } = 12;

    /// <summary>Gets the point of the label's box put at its point, across: its center by default.</summary>
    public TextAlign Horizontal { get; init; } = TextAlign.Center;

    /// <summary>Gets the point of the label's box put at its point, down: its middle by default.</summary>
    public TextBaseline Vertical { get; init; } = TextBaseline.Middle;

    /// <summary>Gets how far the label moves right of its point, in DIPs: finite.</summary>
    public float OffsetX { get; init; }

    /// <summary>Gets how far the label moves up from its point, in DIPs: finite.</summary>
    public float OffsetY { get; init; }

    /// <summary>Gets the label's rotation.</summary>
    public TextRotation Rotation { get; init; }
}
