namespace HyperPlot.Interaction;

/// <summary>What a measurement measures (design §6.5 "計測", row I4b).</summary>
public enum MeasureKind
{
    /// <summary>The difference between two points: Δx, Δy, and the slope.</summary>
    Points = 0,

    /// <summary>The statistics of each series over an x range: count, mean, least, and greatest.</summary>
    Range = 1,
}

/// <summary>
/// A series' statistics over an x range (design §6.5 "計測", requirement I-6, row I4b): of its y values that are numbers. With
/// a count of 0 the mean and the extremes are NaN.
/// </summary>
/// <param name="Layer">The layer's position in the plot's spec.</param>
/// <param name="Series">The series within the layer: 0, a band's high edge 1, a split layer's category's numbered as its hits.</param>
/// <param name="Count">The rows in the range whose y is a number.</param>
/// <param name="Mean">The mean of their y.</param>
/// <param name="Min">The least y.</param>
/// <param name="Max">The greatest y.</param>
/// <param name="Provisional">Whether a part of the range was answered without its index (invariant 4): a later answer may differ.</param>
public readonly record struct SeriesStatistics(int Layer, int Series, long Count, double Mean, double Min, double Max, bool Provisional);

/// <summary>
/// A measurement of a panel (design §6.5 "計測", requirement I-6, row I4b, Issue #390 Decision 7): an unchanging value, one
/// abstraction for every kind, so that the contract does not assume two dimensions (invariant 12); <see cref="Measurement2D"/>
/// is the kind of a 2D panel.
/// </summary>
public abstract class Measurement
{
    private protected Measurement(MeasureKind kind, PanelId panel) => (Kind, Panel) = (kind, panel);

    /// <summary>Gets what the measurement measures.</summary>
    public MeasureKind Kind { get; }

    /// <summary>Gets the panel it was made on.</summary>
    public PanelId Panel { get; }
}

/// <summary>
/// A measurement of a 2D panel (row I4b): two points of the scale space and their differences in data units, or an x range and
/// its series' statistics.
/// </summary>
public sealed class Measurement2D : Measurement
{
    private Measurement2D(
        MeasureKind kind,
        PanelId panel,
        (ViewRange X, ViewRange Y) from,
        (ViewRange X, ViewRange Y) to,
        ViewRange x,
        ReadOnlyMemory<SeriesStatistics> statistics
    )
        : base(kind, panel)
    {
        (From, To, X, Statistics) = (from, to, x, statistics);
    }

    /// <summary>Gets the first point of a measurement of points: its x and y, each a range of one point of the scale space.</summary>
    public (ViewRange X, ViewRange Y) From { get; }

    /// <summary>Gets the second point of a measurement of points.</summary>
    public (ViewRange X, ViewRange Y) To { get; }

    /// <summary>Gets the x range of a measurement of a range, in the scale space.</summary>
    public ViewRange X { get; }

    /// <summary>Gets the statistics of a measurement of a range, a series each in the order of the layers.</summary>
    public ReadOnlyMemory<SeriesStatistics> Statistics { get; }

    /// <summary>Gets the difference of x from the first point to the second, in data units (Issue #390 Decision 5); NaN for a range.</summary>
    public double DeltaX { get; private init; } = double.NaN;

    /// <summary>Gets the difference of y from the first point to the second, in data units; NaN for a range.</summary>
    public double DeltaY { get; private init; } = double.NaN;

    /// <summary>Gets Δy / Δx in data units (a time axis's x in seconds); NaN when Δx is 0 or for a range.</summary>
    public double Slope { get; private init; } = double.NaN;

    /// <summary>Returns a measurement of two points; the engine fills in the differences when it takes it (<see cref="PlotEngine.Measure"/>).</summary>
    /// <param name="panel">The panel.</param>
    /// <param name="from">The first point: an x and a y, each a range of one point of the scale space.</param>
    /// <param name="to">The second point.</param>
    /// <returns>The measurement.</returns>
    public static Measurement2D OfPoints(PanelId panel, (ViewRange X, ViewRange Y) from, (ViewRange X, ViewRange Y) to) =>
        new(MeasureKind.Points, panel, from, to, default, default);

    /// <summary>Returns a measurement of an x range; the engine fills in the statistics when it takes it (<see cref="PlotEngine.Measure"/>).</summary>
    /// <param name="panel">The panel.</param>
    /// <param name="x">The x range, in the scale space.</param>
    /// <returns>The measurement.</returns>
    public static Measurement2D OfRange(PanelId panel, ViewRange x) => new(MeasureKind.Range, panel, default, default, x, default);

    /// <summary>Returns the measurement of points with its differences.</summary>
    internal Measurement2D WithDeltas(double dx, double dy) =>
        new(Kind, Panel, From, To, X, Statistics)
        {
            DeltaX = dx,
            DeltaY = dy,
            Slope = dx == 0 ? double.NaN : dy / dx,
        };

    /// <summary>Returns the measurement with another slope: a time axis's, a y unit a second.</summary>
    internal Measurement2D WithSlope(double slope) =>
        new(Kind, Panel, From, To, X, Statistics)
        {
            DeltaX = DeltaX,
            DeltaY = DeltaY,
            Slope = slope,
        };

    /// <summary>Returns the measurement of a range with its statistics.</summary>
    internal Measurement2D WithStatistics(ReadOnlyMemory<SeriesStatistics> statistics) => new(Kind, Panel, From, To, X, statistics);

    /// <summary>Returns whether two lists of statistics hold the same values.</summary>
    internal static bool Same(ReadOnlySpan<SeriesStatistics> a, ReadOnlySpan<SeriesStatistics> b) => a.SequenceEqual(b);

    internal static bool Finite(in ViewRange range) => double.IsFinite(range.Lower) && double.IsFinite(range.Upper) && range.Lower <= range.Upper;
}

/// <summary>
/// Writes the text of a measurement's box (design §6.5 "計測", row I4b, Issue #390 Decision 11), as
/// <see cref="TooltipFormatter"/> does a tooltip's: into a span, so that a warm frame allocates nothing.
/// </summary>
/// <param name="measurement">The measurement.</param>
/// <param name="destination">Where the lines go, separated by line feeds.</param>
/// <param name="charsWritten">The characters written.</param>
/// <returns>Whether to draw the box: false hides it.</returns>
public delegate bool MeasureFormatter(Measurement measurement, Span<char> destination, out int charsWritten);
