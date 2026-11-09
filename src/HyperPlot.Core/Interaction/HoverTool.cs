namespace HyperPlot.Interaction;

/// <summary>Which lines of a hover's crosshair are drawn through the pointer (design §6.5, row I2b): along the screen's sides.</summary>
public enum HoverCrosshair
{
    /// <summary>No crosshair.</summary>
    None,

    /// <summary>A vertical line through the pointer, the panel's height.</summary>
    Vertical,

    /// <summary>A horizontal line through the pointer, the panel's width.</summary>
    Horizontal,

    /// <summary>Both lines.</summary>
    Both,
}

/// <summary>
/// Writes a hover's tooltip (design §6.5, row I2b): its lines, a line feed between two, for the hits of the hover. Called in
/// the frame, so that it writes into the span and allocates nothing.
/// </summary>
/// <param name="hits">The hits, in the spec's layer order: the engine's <see cref="PlotEngine.Hover"/>.</param>
/// <param name="destination">Where the text goes: <see cref="HoverTool.TooltipLength"/> characters.</param>
/// <param name="charsWritten">The number of characters written.</param>
/// <returns>Whether to draw a tooltip: false draws none.</returns>
public delegate bool TooltipFormatter(ReadOnlySpan<SeriesHit> hits, Span<char> destination, out int charsWritten);

/// <summary>
/// Hovers (design §6.5, row I2b): a move with no button held asks <see cref="PlotEngine.HitTest"/> at the pointer, and the
/// engine keeps the hits as <see cref="PlotEngine.Hover"/> and draws a crosshair, a marker at each hit, and a tooltip over
/// the plot from the tool's settings. The tool reads moves and lets them go on (<see cref="ToolResult.Ignored"/>), so the
/// tools after it in the bindings get them too. Removed from the bindings, it hovers no more.
/// </summary>
public sealed class HoverTool : IPlotTool
{
    /// <summary>The characters a <see cref="TooltipFormatter"/> may write.</summary>
    public const int TooltipLength = 1024;

    private HitMode _mode = HitMode.X;
    private float _radius = 8;
    private HoverCrosshair _crosshair = HoverCrosshair.Vertical;

    /// <summary>Gets or sets what each series looks for: <see cref="HitMode.X"/> by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a named <see cref="HitMode"/>.</exception>
    public HitMode Mode
    {
        get => _mode;
        set => _mode = Check(value, value is HitMode.X or HitMode.XY, "The mode is a named HitMode value.");
    }

    /// <summary>Gets or sets the radius of <see cref="HitMode.XY"/>, in DIPs: finite and 0 or more. 8 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not finite or negative.</exception>
    public float Radius
    {
        get => _radius;
        set => _radius = Check(value, float.IsFinite(value) && value >= 0, "The radius is finite and 0 or more.");
    }

    /// <summary>Gets or sets the crosshair's lines: <see cref="HoverCrosshair.Vertical"/> by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a named <see cref="HoverCrosshair"/>.</exception>
    public HoverCrosshair Crosshair
    {
        get => _crosshair;
        set => _crosshair = Check(value, Enum.IsDefined(value), "The crosshair is a named HoverCrosshair value.");
    }

    /// <summary>Gets or sets a value indicating whether a small circle marks each hit, in its series' color: true by default.</summary>
    public bool Markers { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the tooltip is drawn: true by default.</summary>
    public bool Tooltip { get; set; } = true;

    /// <summary>
    /// Gets or sets what writes the tooltip, or null for the default: a line a hit, "name: x, y", the name the layer's label
    /// or its geom, the values in the format of their axes' tick labels. Pass a static lambda or a method group.
    /// </summary>
    public TooltipFormatter? TooltipFormat { get; set; }

    /// <inheritdoc />
    public ToolResult OnPointer(in PointerEvent e, ToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (e.Kind == PointerEventKind.Move)
        {
            context.Hover(this, e.Position);
        }

        // A hover only looks: the move goes on to the tools after it (design §6.5).
        return ToolResult.Ignored;
    }

    private static T Check<T>(T value, bool good, string message) =>
        good ? value : throw new ArgumentOutOfRangeException(nameof(value), value, message);
}
