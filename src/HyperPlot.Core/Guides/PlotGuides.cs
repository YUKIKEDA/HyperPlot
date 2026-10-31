using System.Numerics;
using HyperPlot.Rendering;

namespace HyperPlot;

/// <summary>
/// The layout of a plot's guides and their recording (design §4.6): from the figure's rectangle, the title, the legend, the
/// bottom band, the left ticks, the left band, and the bottom ticks, in that order, leave the panel; the guides are then
/// recorded around the data, behind it (panel, grid) and over it (axes, labels, titles, legend). x is on the bottom and y
/// on the left, or the other way round when the coordinate system is flipped (row G10). A polar coordinate system (row G11)
/// puts the angle's axis around a circle in the panel's center and the radius's on its left. Lengths are DIPs times the
/// scale.
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
    private Vector2[] _curve = new Vector2[256];

    internal PlotGuides(TextEngine text, float scale, int width, int height)
    {
        (_text, _scale, _width, _height) = (text, scale, width, height);
        X.Text = text;
        X.Labels = _labels;
        Y.Text = text;
        Y.Labels = _labels;
    }

    /// <summary>Gets the ticks of x.</summary>
    internal TickAxis X { get; } = new();

    /// <summary>Gets the ticks of y.</summary>
    internal TickAxis Y { get; } = new();

    /// <summary>Gets a value indicating whether the last layout put x on the left and y on the bottom (row G10).</summary>
    internal bool Flipped { get; private set; }

    /// <summary>Gets the panel's left edge, in whole pixels.</summary>
    internal int Left { get; private set; }

    /// <summary>Gets the panel's top edge.</summary>
    internal int Top { get; private set; }

    /// <summary>Gets the panel's width W; below 1 draws nothing but the background.</summary>
    internal int Width { get; private set; }

    /// <summary>Gets the panel's height H.</summary>
    internal int Height { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the panel is too small to draw (design §4.6): only the background is drawn. Polar, also
    /// when the circle's ring is under a pixel.
    /// </summary>
    internal bool Empty => Width < 1 || Height < 1 || (Polar is { } polar && polar.Radius - polar.Inner < 1);

    /// <summary>Gets the polar transform of the last layout, or null when it was not polar (row G11).</summary>
    internal PolarTransform? Polar { get; private set; }

    /// <summary>Gets the band of the angle's labels outside the circle, Θ (design §4.6); 0 when not polar.</summary>
    internal int ThetaBand { get; private set; }

    /// <summary>Gets the table of the labels' strings, for the tests of its size.</summary>
    internal LabelTable LabelTable => _labels;

    /// <summary>Rounds a length in DIPs to whole pixels: ⌊v · s + 0.5⌋ (design §4.6).</summary>
    internal int Round(float dips) => (int)MathF.Floor((dips * _scale) + 0.5f);

    /// <summary>
    /// Lays the guides out (design §4.6): the bands, the panel, the ticks of the left axis then the bottom one, and the
    /// domains nice widens. The domains are the learned ones in the scale space; nice applies to a scale that asks for it
    /// and whose domain is learned. The tick spacings go with the sides: <see cref="Theme.TickSpacingX"/> on the bottom.
    /// </summary>
    internal void Layout(PlotSpec spec, IScale? xTransform, ScaleRange x, IScale? yTransform, ScaleRange y)
    {
        var theme = spec.Theme;
        Flipped = spec.Coord is CartesianCoordSpec { Flip: true };
        (Polar, ThetaBand, X.Circular, Y.Circular) = (null, 0, false, false);
        if (spec.Coord is PolarCoordSpec polar)
        {
            LayoutPolar(spec, polar, xTransform, x, yTransform, y);
            return;
        }

        var (bottom, bottomScale) = Bottom(spec);
        var (side, sideScale) = Side(spec);
        var (bottomTransform, bottomDomain, sideTransform, sideDomain) = Flipped ? (yTransform, y, xTransform, x) : (xTransform, x, yTransform, y);
        bottom.Horizontal = true;
        side.Horizontal = false;
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
        var bottomBand = 0;
        if (theme.ShowAxes)
        {
            bottomBand = tick + gap + Ceiling(lineHeight) + (HasText(bottomScale.Title) ? gap + Ceiling(titleHeight) : 0);
        }

        Top = figureTop + titleBand;
        Height = figureBottom - bottomBand - Top;
        Prepare(side, sideScale, sideTransform, sideDomain, Height, theme.TickSpacingY, tickStyle, lineHeight, theme);
        var sideBand = 0;
        if (theme.ShowAxes)
        {
            var widest = 0f;
            foreach (var label in side.TickLabels)
            {
                widest = label.Length == 0 ? widest : MathF.Max(widest, _text.Measure(label, tickStyle).Width);
            }

            sideBand = tick + gap + Ceiling(widest) + (HasText(sideScale.Title) ? gap + Ceiling(titleHeight) : 0);
        }

        Left = figureLeft + sideBand;
        Width = figureRight - legendBand - Left;
        if (Empty)
        {
            return;
        }

        Prepare(bottom, bottomScale, bottomTransform, bottomDomain, Width, theme.TickSpacingX, tickStyle, lineHeight, theme);
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

        if (Polar is { } polar)
        {
            RecordPolarGrid(list, spec, polar);
            return;
        }

        list.SetClip(Left, Top, Width, Height);
        var grid = new LineStyle(theme.GridColor, theme.GridWidth * _scale);
        var width = Round(theme.GridWidth);
        var (bottom, bottomScale) = Bottom(spec);
        foreach (var pixel in bottom.Pixels)
        {
            var at = Snap(HorizontalOf(bottomScale, pixel), width);
            Line(list, new Vector2(at, Top), new Vector2(at, Top + Height), grid);
        }

        var (side, sideScale) = Side(spec);
        foreach (var pixel in side.Pixels)
        {
            var at = Snap(VerticalOf(sideScale, pixel), width);
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
        if (theme.ShowAxes && Polar is { } polar)
        {
            RecordPolarAxes(list, spec, polar, theme, gap, tick);
            RecordTitles(list, spec, theme, padding);
        }
        else if (theme.ShowAxes)
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

    /// <summary>Returns the axis and the scale on the bottom: x, or y when flipped. Polar, the angle's.</summary>
    private (TickAxis Axis, ContinuousScaleSpec Scale) Bottom(PlotSpec spec) =>
        Flipped || spec.Coord is PolarCoordSpec { Theta: PolarTheta.Y }
            ? (Y, (ContinuousScaleSpec)spec.ScaleY)
            : (X, (ContinuousScaleSpec)spec.ScaleX);

    /// <summary>Returns the axis and the scale on the left: y, or x when flipped. Polar, the radius's.</summary>
    private (TickAxis Axis, ContinuousScaleSpec Scale) Side(PlotSpec spec) =>
        Flipped || spec.Coord is PolarCoordSpec { Theta: PolarTheta.Y }
            ? (X, (ContinuousScaleSpec)spec.ScaleX)
            : (Y, (ContinuousScaleSpec)spec.ScaleY);

    /// <summary>Returns ⌊v / 2⌋ for any sign.</summary>
    private static int Half(int value) => (int)Math.Floor(value / 2.0);

    /// <summary>
    /// Lays a polar plot out (design §4.6, row G11): the bottom band holds the angle's title only; the radius's ticks are
    /// chosen over a provisional radius from the panel's height, then the left band, then the angle's ticks over the
    /// provisional circle of the smaller side, and the outer radius leaves the band of the angle's labels outside the circle.
    /// </summary>
    private void LayoutPolar(PlotSpec spec, PolarCoordSpec polar, IScale? xTransform, ScaleRange x, IScale? yTransform, ScaleRange y)
    {
        var theme = spec.Theme;
        var (theta, thetaScale) = Bottom(spec);
        var (radius, radiusScale) = Side(spec);
        var (thetaTransform, thetaDomain, radiusTransform, radiusDomain) =
            polar.Theta == PolarTheta.X ? (xTransform, x, yTransform, y) : (yTransform, y, xTransform, x);
        (theta.Horizontal, theta.Circular, radius.Horizontal, radius.Circular) = (true, true, false, false);
        var padding = Round(theme.Padding);
        var gap = Round(theme.Spacing);
        var tick = Round(theme.TickLength);
        var (figureLeft, figureTop) = (padding, padding);
        var (figureRight, figureBottom) = (_width - padding, _height - padding);
        var titleBand = HasText(spec.Title) ? Ceiling(_text.Measure(spec.Title!, Style(theme, theme.TitleSize)).Height) + gap : 0;
        var legendWidth = LegendWidth(spec, theme);
        var legendBand = legendWidth > 0 ? Ceiling(Round(theme.LegendSwatch) + gap + legendWidth) + gap : 0;
        var tickStyle = Style(theme, theme.TickLabelSize);
        var titleStyle = Style(theme, theme.AxisTitleSize);
        var lineHeight = _text.Measure("0", tickStyle).Height;
        var titleHeight = _text.Measure("0", titleStyle).Height;

        // 1. The bottom band: the angle's title only; its labels go around the circle.
        var bottomBand = theme.ShowAxes && HasText(thetaScale.Title) ? gap + Ceiling(titleHeight) : 0;
        Top = figureTop + titleBand;
        Height = figureBottom - bottomBand - Top;

        // 2. The radius's ticks over a provisional radius from the height. The band ends with a gap, so that the label at 9
        // o'clock keeps apart from the radius's axis.
        var provisionalBand = theme.ShowAxes ? tick + gap + Ceiling(lineHeight) + gap : 0;
        var byHeight = Half(Height) - provisionalBand;
        Prepare(
            radius,
            radiusScale,
            radiusTransform,
            radiusDomain,
            byHeight - (int)Math.Floor(polar.InnerRadius * byHeight),
            theme.TickSpacingY,
            tickStyle,
            lineHeight,
            theme
        );

        // 3. The left band, as cartesian's y band.
        var sideBand = 0;
        if (theme.ShowAxes)
        {
            var widest = 0f;
            foreach (var label in radius.TickLabels)
            {
                widest = label.Length == 0 ? widest : MathF.Max(widest, _text.Measure(label, tickStyle).Width);
            }

            sideBand = tick + gap + Ceiling(widest) + (HasText(radiusScale.Title) ? gap + Ceiling(titleHeight) : 0);
        }

        Left = figureLeft + sideBand;
        Width = figureRight - legendBand - Left;
        if (Width < 1 || Height < 1)
        {
            return;
        }

        // 4. The angle's ticks around a provisional circle of the smaller side.
        var bySide = Half(Math.Min(Width, Height)) - provisionalBand;
        Prepare(
            theta,
            thetaScale,
            thetaTransform,
            thetaDomain,
            bySide > 0 ? PolarTransform.Turn(bySide) : 0,
            theme.TickSpacingX,
            tickStyle,
            lineHeight,
            theme
        );

        // 5. The band of the angle's labels, the outer radius, and the center.
        if (theme.ShowAxes)
        {
            var widest = 0f;
            foreach (var label in theta.TickLabels)
            {
                widest = label.Length == 0 ? widest : MathF.Max(widest, _text.Measure(label, tickStyle).Width);
            }

            ThetaBand = tick + gap + Math.Max(Ceiling(widest), Ceiling(lineHeight)) + gap;
        }

        var outer = Half(Math.Min(Width, Height)) - ThetaBand;
        var inner = (int)Math.Floor(polar.InnerRadius * outer);
        Polar = new PolarTransform(Left + (Width / 2.0), Top + (Height / 2.0), outer, inner, polar);
    }

    /// <summary>The share of a tick along its axis, from the scale's low end: reversed, from its high end.</summary>
    private static double Share(TickAxis axis, ContinuousScaleSpec scale, double pixel)
    {
        var share = axis.Length > 0 ? pixel / axis.Length : 0;
        return scale.Reverse ? 1 - share : share;
    }

    /// <summary>
    /// Records the polar grid (design §4.6): a circle at each radius tick and a line out from the inner radius to the outer
    /// one at each angle tick, but the last when it meets the first. Clipped to the panel.
    /// </summary>
    private void RecordPolarGrid(RenderList list, PlotSpec spec, PolarTransform polar)
    {
        var theme = spec.Theme;
        list.SetClip(Left, Top, Width, Height);
        var grid = new LineStyle(theme.GridColor, theme.GridWidth * _scale);
        var (theta, thetaScale) = Bottom(spec);
        var (radius, radiusScale) = Side(spec);
        var ring = polar.Radius - polar.Inner;
        foreach (var pixel in radius.Pixels)
        {
            var c = Share(radius, radiusScale, pixel) * ring;
            if (polar.RadiusOf(c) >= 1)
            {
                list.DrawPolyline(_curve.AsSpan(0, polar.Curve(0, c, polar.Circumference, c, ref _curve)), grid);
            }
        }

        var pixels = theta.Pixels;
        var count = theta.WrapsToFirst() ? pixels.Length - 1 : pixels.Length;
        for (var i = 0; i < count; i++)
        {
            var a = Share(theta, thetaScale, pixels[i]) * polar.Circumference;
            Line(list, polar.Point(a, 0), polar.Point(a, ring), grid);
        }

        list.ResetClip();
    }

    /// <summary>
    /// Records the polar axes (design §4.6): the outer circle with its ticks out and the angle's labels outside them, and the
    /// radius's axis left of the circle, past the labels' band, from the center's height to the outer circle's.
    /// </summary>
    private void RecordPolarAxes(RenderList list, PlotSpec spec, PolarTransform polar, Theme theme, int gap, int tick)
    {
        var axis = new LineStyle(theme.AxisColor, theme.AxisWidth * _scale);
        var ticks = new LineStyle(theme.AxisColor, theme.TickWidth * _scale);
        var axisPixels = Round(theme.AxisWidth);
        var tickPixels = Round(theme.TickWidth);
        var halfTick = theme.TickWidth * _scale / 2;
        var labelStyle = Style(theme, theme.TickLabelSize) with { Horizontal = TextAlign.Left, Vertical = TextBaseline.Top };
        var lineHeight = _text.Measure("0", labelStyle).Height;
        var (theta, thetaScale) = Bottom(spec);
        var (radius, radiusScale) = Side(spec);
        var ring = polar.Radius - polar.Inner;

        // The angle's axis: the outer circle, the ticks out from it, and the labels outside, pushed out by half their box.
        list.DrawPolyline(_curve.AsSpan(0, polar.Curve(0, ring, polar.Circumference, ring, ref _curve)), axis);
        var pixels = theta.Pixels;
        var labels = theta.TickLabels;
        var count = theta.WrapsToFirst() ? pixels.Length - 1 : pixels.Length;
        var reach = ring + Math.Max(0, tick - halfTick);
        for (var i = 0; i < count; i++)
        {
            var a = Share(theta, thetaScale, pixels[i]) * polar.Circumference;
            Line(list, polar.Point(a, ring), polar.Point(a, reach), ticks);
            if (labels[i].Length > 0)
            {
                var angle = polar.Theta(a);
                var (sin, cos) = ((float)Math.Sin(angle), (float)Math.Cos(angle));
                var width = _text.Measure(labels[i], labelStyle).Width;
                var at = polar.Point(a, ring + tick + gap);
                var center = new Vector2(at.X + (sin * width / 2), at.Y - (cos * lineHeight / 2));
                list.DrawText(labels[i], new Vector2(center.X - (width / 2), center.Y - (lineHeight / 2)), labelStyle);
            }
        }

        // The radius's axis: a vertical line at x = center − R − Θ, its ticks and labels on its left as cartesian's y axis.
        var edge = polar.CenterX - polar.Radius - ThetaBand;
        var line = Snap(edge - (axisPixels / 2.0), axisPixels);
        Line(list, new Vector2(line, (float)polar.CenterY), new Vector2(line, (float)(polar.CenterY - polar.Radius)), axis);
        var sideStyle = labelStyle with { Vertical = TextBaseline.Middle };
        var sidePixels = radius.Pixels;
        var sideLabels = radius.TickLabels;
        for (var i = 0; i < sidePixels.Length; i++)
        {
            var at = Snap(polar.CenterY - polar.RadiusOf(Share(radius, radiusScale, sidePixels[i]) * ring), tickPixels);
            Line(list, new Vector2(line, at), new Vector2(MathF.Min(line, (float)(edge - tick + halfTick)), at), ticks);
            if (sideLabels[i].Length > 0)
            {
                var width = _text.Measure(sideLabels[i], sideStyle).Width;
                list.DrawText(sideLabels[i], new Vector2((float)(edge - tick - gap - width), at), sideStyle);
            }
        }
    }

    /// <summary>The target's x pixel of a pixel along the bottom axis: left to right, or right to left when reversed.</summary>
    private double HorizontalOf(ContinuousScaleSpec scale, double pixel) => Left + (scale.Reverse ? Width - pixel : pixel);

    /// <summary>The target's y pixel of a pixel along the left axis: bottom to top, or top to bottom when reversed.</summary>
    private double VerticalOf(ContinuousScaleSpec scale, double pixel) => Top + (scale.Reverse ? pixel : Height - pixel);

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
        var (bottom, bottomScale) = Bottom(spec);
        var (side, sideScale) = Side(spec);
        var bottomLabels = bottom.TickLabels;
        var bottomPixels = bottom.Pixels;
        for (var i = 0; i < bottomPixels.Length; i++)
        {
            var at = Snap(HorizontalOf(bottomScale, bottomPixels[i]), tickPixels);
            Line(list, new Vector2(at, xLine), new Vector2(at, MathF.Max(xLine, Top + Height + tick - halfTick)), ticks);
            if (bottomLabels[i].Length > 0)
            {
                list.DrawText(
                    bottomLabels[i],
                    new Vector2(at - (bottom.Extents[i] / 2), Top + Height + tick + gap),
                    labelStyle with
                    {
                        Horizontal = TextAlign.Left,
                        Vertical = TextBaseline.Top,
                    }
                );
            }
        }

        var sideLabels = side.TickLabels;
        var sidePixels = side.Pixels;
        for (var i = 0; i < sidePixels.Length; i++)
        {
            var at = Snap(VerticalOf(sideScale, sidePixels[i]), tickPixels);
            Line(list, new Vector2(yLine, at), new Vector2(MathF.Min(yLine, Left - tick + halfTick), at), ticks);
            if (sideLabels[i].Length > 0)
            {
                list.DrawText(
                    sideLabels[i],
                    new Vector2(Left - tick - gap - _text.Measure(sideLabels[i], labelStyle).Width, at),
                    labelStyle with
                    {
                        Horizontal = TextAlign.Left,
                        Vertical = TextBaseline.Middle,
                    }
                );
            }
        }

        RecordTitles(list, spec, theme, padding);
    }

    /// <summary>
    /// Records the axes' titles (design §4.6): the bottom one centered under the panel, the left one turned and centered
    /// beside it. Polar, the angle's title is the bottom one and the radius's the left one.
    /// </summary>
    private void RecordTitles(RenderList list, PlotSpec spec, Theme theme, int padding)
    {
        var (_, bottomScale) = Bottom(spec);
        var (_, sideScale) = Side(spec);
        var titleStyle = Style(theme, theme.AxisTitleSize);
        if (bottomScale.Title is { Length: > 0 } bottomTitle)
        {
            list.DrawText(
                bottomTitle,
                new Vector2(Left + (Width / 2f) - (_text.Measure(bottomTitle, titleStyle).Width / 2), _height - padding),
                titleStyle with
                {
                    Horizontal = TextAlign.Left,
                    Vertical = TextBaseline.Bottom,
                }
            );
        }

        if (sideScale.Title is { Length: > 0 } sideTitle)
        {
            list.DrawText(
                sideTitle,
                new Vector2(padding, Top + (Height / 2f) + (_text.Measure(sideTitle, titleStyle).Width / 2)),
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
