using System.Numerics;
using HyperPlot.Rendering;

namespace HyperPlot;

/// <summary>
/// The layout of a plot's guides and their recording (design §4.6): from the figure's rectangle, the title, the legend, the
/// x band, the y ticks, the y band, and the x ticks, in that order, leave the panel; the guides are then recorded around
/// the data, behind it (panel, grid) and over it (axes, labels, titles, legend). Lengths are DIPs times the scale.
/// </summary>
/// <remarks>One thread at a time uses the guides, the plan's. Allocates only for labels and shapes not seen before.</remarks>
internal sealed class PlotGuides
{
    private readonly TextEngine _text;
    private readonly float _scale;
    private readonly int _width;
    private readonly int _height;
    private readonly LabelTable _labels = new();
    private static readonly LineStyle CustomSample = new(new Color(0, 0, 0, 255), 1);
    private readonly Vector2[] _segment = new Vector2[2];

    internal PlotGuides(TextEngine text, float scale, int width, int height)
    {
        (_text, _scale, _width, _height) = (text, scale, width, height);
        X.Text = text;
        X.Labels = _labels;
        Y.Text = text;
        Y.Labels = _labels;
    }

    /// <summary>Gets the ticks of x.</summary>
    internal TickAxis X { get; } = new() { Horizontal = true };

    /// <summary>Gets the ticks of y.</summary>
    internal TickAxis Y { get; } = new() { Horizontal = false };

    /// <summary>Gets the panel's left edge, in whole pixels.</summary>
    internal int Left { get; private set; }

    /// <summary>Gets the panel's top edge.</summary>
    internal int Top { get; private set; }

    /// <summary>Gets the panel's width W; below 1 draws nothing but the background.</summary>
    internal int Width { get; private set; }

    /// <summary>Gets the panel's height H.</summary>
    internal int Height { get; private set; }

    /// <summary>Gets a value indicating whether the panel is too small to draw (design §4.6): only the background is drawn.</summary>
    internal bool Empty => Width < 1 || Height < 1;

    /// <summary>Gets the table of the labels' strings, for the tests of its size.</summary>
    internal LabelTable LabelTable => _labels;

    /// <summary>Rounds a length in DIPs to whole pixels: ⌊v · s + 0.5⌋ (design §4.6).</summary>
    internal int Round(float dips) => (int)MathF.Floor((dips * _scale) + 0.5f);

    /// <summary>
    /// Lays the guides out (design §4.6): the bands, the panel, the ticks of y then x, and the domains nice widens. The
    /// domains are the learned ones in the scale space; nice applies to a scale that asks for it and whose domain is learned.
    /// </summary>
    internal void Layout(PlotSpec spec, IScale? xTransform, ScaleRange x, IScale? yTransform, ScaleRange y)
    {
        var theme = spec.Theme;
        var (xScale, yScale) = ((ContinuousScaleSpec)spec.ScaleX, (ContinuousScaleSpec)spec.ScaleY);
        var padding = Round(theme.Padding);
        var gap = Round(theme.Spacing);
        var tick = Round(theme.TickLength);
        var (figureLeft, figureTop) = (padding, padding);
        var (figureRight, figureBottom) = (_width - padding, _height - padding);

        var titleBand = HasText(spec.Title) ? Ceiling(_text.Measure(spec.Title!, Style(theme, theme.TitleSize)).Height) + gap : 0;
        var legendBand = 0;
        var legendWidth = LegendWidth(spec, theme);
        if (legendWidth > 0)
        {
            legendBand = Ceiling(Round(theme.LegendSwatch) + gap + legendWidth) + gap;
        }

        var tickStyle = Style(theme, theme.TickLabelSize);
        var titleStyle = Style(theme, theme.AxisTitleSize);
        var lineHeight = _text.Measure("0", tickStyle).Height;
        var titleHeight = _text.Measure("0", titleStyle).Height;
        var xBand = 0;
        if (theme.ShowAxes)
        {
            xBand = tick + gap + Ceiling(lineHeight) + (HasText(xScale.Title) ? gap + Ceiling(titleHeight) : 0);
        }

        Top = figureTop + titleBand;
        Height = figureBottom - xBand - Top;
        Prepare(Y, yScale, yTransform, y, Height, theme.TickSpacingY, tickStyle, lineHeight, theme);
        var yBand = 0;
        if (theme.ShowAxes)
        {
            var widest = 0f;
            foreach (var label in Y.TickLabels)
            {
                widest = label.Length == 0 ? widest : MathF.Max(widest, _text.Measure(label, tickStyle).Width);
            }

            yBand = tick + gap + Ceiling(widest) + (HasText(yScale.Title) ? gap + Ceiling(titleHeight) : 0);
        }

        Left = figureLeft + yBand;
        Width = figureRight - legendBand - Left;
        if (Empty)
        {
            return;
        }

        Prepare(X, xScale, xTransform, x, Width, theme.TickSpacingX, tickStyle, lineHeight, theme);
    }

    /// <summary>Records what goes behind the data (design §4.6): the panel and the grid.</summary>
    internal void RecordBehind(RenderList list, PlotSpec spec)
    {
        var theme = spec.Theme;
        list.DrawRect(Left, Top, Width, Height, theme.PanelBackground);
        if (!theme.ShowGrid)
        {
            return;
        }

        list.SetClip(Left, Top, Width, Height);
        var grid = new LineStyle(theme.GridColor, theme.GridWidth * _scale);
        var width = Round(theme.GridWidth);
        foreach (var pixel in X.Pixels)
        {
            var at = Snap(XOf(spec, pixel), width);
            Line(list, new Vector2(at, Top), new Vector2(at, Top + Height), grid);
        }

        foreach (var pixel in Y.Pixels)
        {
            var at = Snap(YOf(spec, pixel), width);
            Line(list, new Vector2(Left, at), new Vector2(Left + Width, at), grid);
        }

        list.ResetClip();
    }

    /// <summary>
    /// Records what goes over the data (design §4.6): the axes, their ticks and labels, the titles, and the legend. A text's
    /// horizontal alignment aligns its lines inside its block, whose left edge is the position (design §5.6), so a label
    /// centered on a point starts half its measured width before it, and one ending at a point starts its width before.
    /// </summary>
    internal void RecordOver(RenderList list, PlotSpec spec)
    {
        var theme = spec.Theme;
        list.ResetClip();
        var gap = Round(theme.Spacing);
        var tick = Round(theme.TickLength);
        var padding = Round(theme.Padding);
        if (theme.ShowAxes)
        {
            RecordAxes(list, spec, theme, gap, tick, padding);
        }

        if (HasText(spec.Title))
        {
            list.DrawText(
                spec.Title!,
                new Vector2(Left + (Width / 2f) - (_text.Measure(spec.Title!, Style(theme, theme.TitleSize)).Width / 2), padding),
                Style(theme, theme.TitleSize) with
                {
                    Horizontal = TextAlign.Left,
                    Vertical = TextBaseline.Top,
                }
            );
        }

        RecordLegend(list, spec, theme, gap);
    }

    /// <summary>Returns a line style with its width and dashes in DIPs turned to pixels.</summary>
    internal LineStyle Scaled(in LineStyle style) => style with { Width = style.Width * _scale, Dash = style.Dash?.Scaled(_scale) };

    private static bool HasText(string? text) => !string.IsNullOrEmpty(text);

    private static int Ceiling(float value) => (int)MathF.Ceiling(value);

    /// <summary>Puts a line's position on pixels (design §4.6): ⌊p⌋ + 0.5 for an odd rounded width, ⌊p + 0.5⌋ for an even one.</summary>
    private static float Snap(double position, int width) => (float)(width % 2 == 1 ? Math.Floor(position) + 0.5 : Math.Floor(position + 0.5));

    private TextStyle Style(Theme theme, float size) =>
        new(
            theme.Foreground,
            size * _scale,
            theme.FontFamily,
            FontWeight.Normal,
            TextAlign.Left,
            TextBaseline.Baseline,
            TextRotation.None,
            theme.Locale
        );

    private float LegendWidth(PlotSpec spec, Theme theme)
    {
        var widest = 0f;
        var style = Style(theme, theme.LegendSize);
        for (var i = 0; i < spec.Layers.Count; i++)
        {
            if (spec.Layers[i].Label is { Length: > 0 } label)
            {
                widest = MathF.Max(widest, _text.Measure(label, style).Width);
            }
        }

        return widest;
    }

    private void Prepare(
        TickAxis axis,
        ContinuousScaleSpec scale,
        IScale? transform,
        ScaleRange domain,
        int length,
        float spacing,
        in TextStyle style,
        float lineHeight,
        Theme theme
    )
    {
        axis.Scale = scale;
        axis.Transform = transform;
        axis.Domain = domain;
        axis.Length = length;
        axis.Spacing = spacing * _scale;
        axis.Style = style;
        axis.LineHeight = lineHeight;
        axis.Em = style.Size;
        if (length < 1 || !(theme.ShowAxes || theme.ShowGrid))
        {
            axis.ClearCandidate();
            axis.SetFinalDomain(domain);
            return;
        }

        var learned = scale is TimeScaleSpec time ? time.Domain is null : scale.GivenDomain is null;
        axis.Choose(scale.Nice && learned);
    }

    /// <summary>The x pixel on the target of a pixel along the x axis: left to right, or right to left when reversed.</summary>
    private double XOf(PlotSpec spec, double pixel) => Left + (((ContinuousScaleSpec)spec.ScaleX).Reverse ? Width - pixel : pixel);

    /// <summary>The y pixel on the target of a pixel along the y axis: bottom to top, or top to bottom when reversed.</summary>
    private double YOf(PlotSpec spec, double pixel) => Top + (((ContinuousScaleSpec)spec.ScaleY).Reverse ? pixel : Height - pixel);

    private void RecordAxes(RenderList list, PlotSpec spec, Theme theme, int gap, int tick, int padding)
    {
        var axisPixels = Round(theme.AxisWidth);
        var axis = new LineStyle(theme.AxisColor, theme.AxisWidth * _scale);
        var ticks = new LineStyle(theme.AxisColor, theme.TickWidth * _scale);
        var tickPixels = Round(theme.TickWidth);
        var halfTick = theme.TickWidth * _scale / 2;

        // The axis lines' centers half their width outside the panel's edges, so that they do not cover the data (design §4.6).
        var xLine = Snap(Top + Height + (axisPixels / 2.0), axisPixels);
        var yLine = Snap(Left - (axisPixels / 2.0), axisPixels);
        Line(list, new Vector2(yLine, xLine), new Vector2(Left + Width, xLine), axis);
        Line(list, new Vector2(yLine, Top), new Vector2(yLine, xLine), axis);

        var labelStyle = Style(theme, theme.TickLabelSize);
        var xLabels = X.TickLabels;
        var xPixels = X.Pixels;
        for (var i = 0; i < xPixels.Length; i++)
        {
            var at = Snap(XOf(spec, xPixels[i]), tickPixels);
            Line(list, new Vector2(at, xLine), new Vector2(at, MathF.Max(xLine, Top + Height + tick - halfTick)), ticks);
            if (xLabels[i].Length > 0)
            {
                list.DrawText(
                    xLabels[i],
                    new Vector2(at - (X.Extents[i] / 2), Top + Height + tick + gap),
                    labelStyle with
                    {
                        Horizontal = TextAlign.Left,
                        Vertical = TextBaseline.Top,
                    }
                );
            }
        }

        var yLabels = Y.TickLabels;
        var yPixels = Y.Pixels;
        for (var i = 0; i < yPixels.Length; i++)
        {
            var at = Snap(YOf(spec, yPixels[i]), tickPixels);
            Line(list, new Vector2(yLine, at), new Vector2(MathF.Min(yLine, Left - tick + halfTick), at), ticks);
            if (yLabels[i].Length > 0)
            {
                list.DrawText(
                    yLabels[i],
                    new Vector2(Left - tick - gap - _text.Measure(yLabels[i], labelStyle).Width, at),
                    labelStyle with
                    {
                        Horizontal = TextAlign.Left,
                        Vertical = TextBaseline.Middle,
                    }
                );
            }
        }

        var titleStyle = Style(theme, theme.AxisTitleSize);
        if (((ContinuousScaleSpec)spec.ScaleX).Title is { Length: > 0 } xTitle)
        {
            list.DrawText(
                xTitle,
                new Vector2(Left + (Width / 2f) - (_text.Measure(xTitle, titleStyle).Width / 2), _height - padding),
                titleStyle with
                {
                    Horizontal = TextAlign.Left,
                    Vertical = TextBaseline.Bottom,
                }
            );
        }

        if (((ContinuousScaleSpec)spec.ScaleY).Title is { Length: > 0 } yTitle)
        {
            list.DrawText(
                yTitle,
                new Vector2(padding, Top + (Height / 2f) + (_text.Measure(yTitle, titleStyle).Width / 2)),
                titleStyle with
                {
                    Horizontal = TextAlign.Left,
                    Vertical = TextBaseline.Top,
                    Rotation = TextRotation.CounterClockwise90,
                }
            );
        }
    }

    private void RecordLegend(RenderList list, PlotSpec spec, Theme theme, int gap)
    {
        var style = Style(theme, theme.LegendSize);
        var lineHeight = _text.Measure("0", style).Height;
        var swatch = Round(theme.LegendSwatch);
        var x = Left + Width + gap;
        var y = (float)Top;
        for (var i = 0; i < spec.Layers.Count; i++)
        {
            // An index, not foreach: the interface's enumerator would be boxed on every recording.
            var layer = spec.Layers[i];
            if (layer.Label is not { Length: > 0 } label)
            {
                continue;
            }

            float row;
            if (Fill(layer) is { } fill)
            {
                // A fill geom's sample is a square of the row's height in its fill (design §4.6, row G4).
                row = lineHeight;
                list.DrawRect(x, y, row, row, fill);
            }
            else
            {
                var sample = Scaled(Stroke(layer));
                row = MathF.Max(lineHeight, sample.Width);
                var center = y + (row / 2);
                Line(list, new Vector2(x, center), new Vector2(x + swatch, center), sample);
            }

            var middle = y + (row / 2);
            list.DrawText(label, new Vector2(x + swatch + gap, middle), style with { Horizontal = TextAlign.Left, Vertical = TextBaseline.Middle });
            y += row;
        }
    }

    /// <summary>Returns the fill of a fill geom (area, band, bar, rect), or null for a geom drawn with lines.</summary>
    private static Color? Fill(LayerSpec layer) =>
        layer switch
        {
            AreaLayerSpec a => a.Fill,
            BandLayerSpec b => b.Fill,
            BarLayerSpec b => b.Fill,
            RectLayerSpec r => r.Fill,
            _ => null,
        };

    /// <summary>Returns the stroke of a line geom's sample: its style, or black 1 wide for a user's geom.</summary>
    private static LineStyle Stroke(LayerSpec layer) =>
        layer switch
        {
            LineLayerSpec l => l.Style,
            StepLayerSpec s => s.Style,
            RuleLayerSpec r => r.Style,
            SegmentLayerSpec s => s.Style,
            ErrorBarLayerSpec e => e.Style,
            _ => CustomSample,
        };

    private void Line(RenderList list, Vector2 from, Vector2 to, in LineStyle style)
    {
        _segment[0] = from;
        _segment[1] = to;
        list.DrawPolyline(_segment, style);
    }
}
