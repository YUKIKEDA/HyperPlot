namespace HyperPlot;

/// <summary>
/// The colors, the font, the text sizes, the line widths, and the spacing of a plot's guides (design §4.7). Start from
/// <see cref="Light"/> or <see cref="Dark"/> and change what differs with <c>with</c>; every member has a value. Lengths and
/// text sizes are in DIPs (1/96 inch), times the renderer's scale (design §4.6). The compile checks the values (design §4.8).
/// </summary>
public sealed record Theme
{
    private Theme() { }

    /// <summary>Gets the light theme: dark text and lines on white.</summary>
    public static Theme Light { get; } =
        new()
        {
            Background = new Color(255, 255, 255, 255),
            PanelBackground = new Color(255, 255, 255, 255),
            Foreground = new Color(34, 34, 34, 255),
            AxisColor = new Color(34, 34, 34, 255),
            GridColor = new Color(229, 229, 229, 255),
        };

    /// <summary>Gets the dark theme: light text and lines on a dark gray.</summary>
    public static Theme Dark { get; } =
        Light with
        {
            Background = new Color(24, 24, 27, 255),
            PanelBackground = new Color(24, 24, 27, 255),
            Foreground = new Color(230, 230, 230, 255),
            AxisColor = new Color(200, 200, 200, 255),
            GridColor = new Color(60, 60, 64, 255),
        };

    /// <summary>Gets the color of the whole target behind the plot.</summary>
    public Color Background { get; init; }

    /// <summary>Gets the color of the panel, where the data is drawn.</summary>
    public Color PanelBackground { get; init; }

    /// <summary>Gets the color of the title, the labels, and the legend's text.</summary>
    public Color Foreground { get; init; }

    /// <summary>Gets the color of the axis lines and the ticks.</summary>
    public Color AxisColor { get; init; }

    /// <summary>Gets the color of the grid lines.</summary>
    public Color GridColor { get; init; }

    /// <summary>Gets the font family of every text: not null or empty.</summary>
    public string FontFamily { get; init; } = "Segoe UI";

    /// <summary>Gets the locale of every text, as <see cref="Rendering.TextStyle.Locale"/>: not null or empty.</summary>
    public string Locale { get; init; } = "en-us";

    /// <summary>Gets the size of the plot's title, in DIPs: finite and above 0.</summary>
    public float TitleSize { get; init; } = 16;

    /// <summary>Gets the size of the axis titles, in DIPs.</summary>
    public float AxisTitleSize { get; init; } = 13;

    /// <summary>Gets the size of the tick labels, in DIPs.</summary>
    public float TickLabelSize { get; init; } = 12;

    /// <summary>Gets the size of the legend's labels, in DIPs.</summary>
    public float LegendSize { get; init; } = 12;

    /// <summary>Gets the width of the axis lines, in DIPs: finite and at least 0.</summary>
    public float AxisWidth { get; init; } = 1;

    /// <summary>Gets the width of the grid lines, in DIPs.</summary>
    public float GridWidth { get; init; } = 1;

    /// <summary>Gets the width of the ticks, in DIPs.</summary>
    public float TickWidth { get; init; } = 1;

    /// <summary>Gets the length of the ticks outside the panel, in DIPs.</summary>
    public float TickLength { get; init; } = 5;

    /// <summary>Gets the space between the target's edges and the plot, in DIPs.</summary>
    public float Padding { get; init; } = 10;

    /// <summary>Gets the space between the title, the legend, the axes, and the labels, in DIPs.</summary>
    public float Spacing { get; init; } = 4;

    /// <summary>Gets the length of a legend entry's line sample, in DIPs.</summary>
    public float LegendSwatch { get; init; } = 24;

    /// <summary>Gets the target spacing of the x ticks, in DIPs: finite and above 0 (design §4.6).</summary>
    public float TickSpacingX { get; init; } = 100;

    /// <summary>Gets the target spacing of the y ticks, in DIPs.</summary>
    public float TickSpacingY { get; init; } = 60;

    /// <summary>Gets a value indicating whether the axis lines, ticks, tick labels, and axis titles are drawn.</summary>
    public bool ShowAxes { get; init; } = true;

    /// <summary>Gets a value indicating whether the grid lines are drawn at the ticks.</summary>
    public bool ShowGrid { get; init; } = true;

    /// <summary>Throws unless every member is in range for a renderer of a scale (design §4.7).</summary>
    internal void Validate(float scale)
    {
        CheckText(TitleSize, scale, nameof(TitleSize));
        CheckText(AxisTitleSize, scale, nameof(AxisTitleSize));
        CheckText(TickLabelSize, scale, nameof(TickLabelSize));
        CheckText(LegendSize, scale, nameof(LegendSize));
        CheckLength(AxisWidth, nameof(AxisWidth));
        CheckLength(GridWidth, nameof(GridWidth));
        CheckLength(TickWidth, nameof(TickWidth));
        CheckLength(TickLength, nameof(TickLength));
        CheckLength(Padding, nameof(Padding));
        CheckLength(Spacing, nameof(Spacing));
        CheckLength(LegendSwatch, nameof(LegendSwatch));
        CheckSpacing(TickSpacingX, nameof(TickSpacingX));
        CheckSpacing(TickSpacingY, nameof(TickSpacingY));
        if (string.IsNullOrEmpty(FontFamily))
        {
            throw new ArgumentException("The theme's FontFamily is null or empty.");
        }

        if (string.IsNullOrEmpty(Locale))
        {
            throw new ArgumentException("The theme's Locale is null or empty.");
        }
    }

    private static void CheckText(float size, float scale, string name)
    {
        if (!float.IsFinite(size) || size <= 0 || size * scale > Rendering.TextEngine.MaxSize)
        {
            throw new ArgumentException(
                $"The theme's {name} is {size}; a text size is finite, above 0, and at most {Rendering.TextEngine.MaxSize} pixels at the scale {scale}."
            );
        }
    }

    private static void CheckLength(float length, string name)
    {
        if (!float.IsFinite(length) || length < 0)
        {
            throw new ArgumentException($"The theme's {name} is {length}; a width or a length is finite and at least 0.");
        }
    }

    private static void CheckSpacing(float spacing, string name)
    {
        if (!float.IsFinite(spacing) || spacing <= 0)
        {
            throw new ArgumentException($"The theme's {name} is {spacing}; a tick spacing is finite and above 0.");
        }
    }
}
