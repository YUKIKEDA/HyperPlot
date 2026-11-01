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
    /// <summary>The legend's sample of a text layer (design §5.5.3, row G8).</summary>
    private const string SampleText = "a";

    // The panel's sides, the places of the axes (row G6a).
    private const int SideBottom = 0;
    private const int SideTop = 1;
    private const int SideLeft = 2;
    private const int SideRight = 3;

    private readonly TextEngine _text;
    private readonly float _scale;
    private readonly int _width;
    private readonly int _height;
    private readonly LabelTable _labels = new();
    private readonly Vector2[] _segment = new Vector2[2];

    // The panels' guides (row G6b): one without a facet. The layout and the recording work on the current one.
    private readonly List<PanelGuide> _panels = [];
    private PanelGuide _current;
    private Vector2[] _curve = new Vector2[256];
    private CategoricalColumn? _categories;
    private ColorScale? _color;

    // The scales the secondary axes of a transform format their labels with, kept while their format is the same (row G6a).
    private LinearScaleSpec? _formatX;
    private LinearScaleSpec? _formatY;

    // The bands of the last layout that the recording reads (row G6a): the title's, the right axis's, and the legend's.
    private int _titleBand;
    private int _rightBand;
    private int _legendBand;

    /// <summary>Gets the text engine the guides measure with.</summary>
    internal TextEngine Text => _text;

    internal PlotGuides(TextEngine text, float scale, int width, int height)
    {
        (_text, _scale, _width, _height) = (text, scale, width, height);
        _current = new PanelGuide(text, _labels);
        _panels.Add(_current);
        ColorBar.Text = text;
        ColorBar.Labels = _labels;
    }

    /// <summary>Gets the ticks of the color bar (row G7c): along its length, from the domain's low end at the bottom.</summary>
    internal TickAxis ColorBar { get; } = new();

    /// <summary>Gets the color bar's length in pixels, Lc (design §4.6); 0 without a continuous color scale.</summary>
    internal int ColorBarLength { get; private set; }

    /// <summary>Gets the ticks of x: the current panel's (row G6b), the first after a layout.</summary>
    internal TickAxis X => _current.X;

    /// <summary>Gets the ticks of y.</summary>
    internal TickAxis Y => _current.Y;

    /// <summary>
    /// Gets the ticks of x's second axis (row G6a): the secondary x scale's, over its own domain, or those of a transform of x
    /// on the side opposite x.
    /// </summary>
    internal TickAxis X2 => _current.X2;

    /// <summary>Gets the ticks of y's second axis (row G6a).</summary>
    internal TickAxis Y2 => _current.Y2;

    /// <summary>Gets a value indicating whether the last layout put x on the left and y on the bottom (row G10).</summary>
    internal bool Flipped { get; private set; }

    /// <summary>Gets the panel's left edge, in whole pixels.</summary>
    internal int Left
    {
        get => _current.Left;
        private set => _current.Left = value;
    }

    /// <summary>Gets the panel's top edge.</summary>
    internal int Top
    {
        get => _current.Top;
        private set => _current.Top = value;
    }

    /// <summary>Gets the panel's width W; below 1 draws nothing but the background.</summary>
    internal int Width
    {
        get => _current.Width;
        private set => _current.Width = value;
    }

    /// <summary>Gets the panel's height H.</summary>
    internal int Height
    {
        get => _current.Height;
        private set => _current.Height = value;
    }

    /// <summary>
    /// Gets a value indicating whether the panel is too small to draw (design §4.6): only the background is drawn. Polar, also
    /// when the circle's ring is under a pixel.
    /// </summary>
    internal bool Empty => Width < 1 || Height < 1 || (Polar is { } polar && polar.Radius - polar.Inner < 1);

    /// <summary>Gets the polar transform of the last layout, or null when it was not polar (row G11).</summary>
    internal PolarTransform? Polar
    {
        get => _current.Polar;
        private set => _current.Polar = value;
    }

    /// <summary>Gets the band of the angle's labels outside the circle, Θ (design §4.6); 0 when not polar.</summary>
    internal int ThetaBand
    {
        get => _current.ThetaBand;
        private set => _current.ThetaBand = value;
    }

    /// <summary>Gets the axes on the current panel's four sides (row G6a): a side with no axis holds a null axis.</summary>
    private SideAxis[] Sides => _current.Sides;

    /// <summary>Gets the table of the labels' strings, for the tests of its size.</summary>
    internal LabelTable LabelTable => _labels;

    /// <summary>Rounds a length in DIPs to whole pixels: ⌊v · s + 0.5⌋ (design §4.6).</summary>
    internal int Round(float dips) => (int)MathF.Floor((dips * _scale) + 0.5f);

    /// <summary>
    /// Lays the guides out (design §4.6): the bands, the panel, the ticks of the left axis then the bottom one, and the
    /// domains nice widens. The domains are the learned ones in the scale space; nice applies to a scale that asks for it
    /// and whose domain is learned. The tick spacings go with the sides: <see cref="Theme.TickSpacingX"/> on the bottom.
    /// </summary>
    internal void Layout(
        PlotSpec spec,
        IScale? xTransform,
        ScaleRange x,
        IScale? yTransform,
        ScaleRange y,
        CategoricalColumn? categories,
        ColorScale? color = null,
        SecondaryDomains secondary = default
    )
    {
        _categories = categories;
        _color = color;
        var theme = spec.Theme;
        Flipped = spec.Coord is CartesianCoordSpec { Flip: true };
        (Polar, ThetaBand, X.Circular, Y.Circular, _titleBand, _rightBand, _legendBand) = (null, 0, false, false, 0, 0, 0);
        if (spec.Coord is PolarCoordSpec polar)
        {
            LayoutPolar(spec, polar, xTransform, x, yTransform, y);
            return;
        }

        LayoutSides(spec, theme, xTransform, x, yTransform, y, secondary);
    }

    /// <summary>
    /// Lays a cartesian plot out on its four sides (design §4.6, row G6a): the top and bottom bands, which do not depend on
    /// ticks; the ticks of the left and right axes over the panel's height, their bands; the ticks of the bottom and top axes
    /// over its width. Each axis's ticks are chosen once, a primary axis before the axis of a transform of it. With the axes
    /// only on the bottom and the left, the steps are those of row G3.
    /// </summary>
    private void LayoutSides(
        PlotSpec spec,
        Theme theme,
        IScale? xTransform,
        ScaleRange x,
        IScale? yTransform,
        ScaleRange y,
        SecondaryDomains secondary
    )
    {
        AssignSides(spec);
        var (horizontal, vertical) = Flipped ? (Y, X) : (X, Y);
        var (horizontalSecond, verticalSecond) = Flipped ? (Y2, X2) : (X2, Y2);
        (horizontal.Horizontal, vertical.Horizontal, horizontalSecond.Horizontal, verticalSecond.Horizontal) = (true, false, true, false);
        X2.Circular = Y2.Circular = false;
        var (bottomScale0, sideScale0) = (new AxisScale(Flipped ? spec.ScaleY : spec.ScaleX), new AxisScale(Flipped ? spec.ScaleX : spec.ScaleY));
        var (horizontalTransform, horizontalDomain, verticalTransform, verticalDomain) = Flipped
            ? (yTransform, y, xTransform, x)
            : (xTransform, x, yTransform, y);
        var padding = Round(theme.Padding);
        var gap = Round(theme.Spacing);
        var tick = Round(theme.TickLength);
        var (figureLeft, figureTop) = (padding, padding);
        var (figureRight, figureBottom) = (_width - padding, _height - padding);

        var titleBand = HasText(spec.Title) ? Ceiling(_text.Measure(spec.Title!, Style(theme, theme.TitleSize)).Height) + gap : 0;
        var tickStyle = Style(theme, theme.TickLabelSize);
        var titleStyle = Style(theme, theme.AxisTitleSize);
        var lineHeight = _text.Measure("0", tickStyle).Height;
        var titleHeight = _text.Measure("0", titleStyle).Height;
        var (bottomBand, topBand) = (0, 0);
        if (theme.ShowAxes)
        {
            bottomBand = HorizontalBand(Sides[SideBottom], tick, gap, lineHeight, titleHeight);
            topBand = HorizontalBand(Sides[SideTop], tick, gap, lineHeight, titleHeight);
        }

        // The horizontal bands do not depend on ticks, so the panel's height is known before the legend: the color bar's
        // length comes from it, and its ticks' labels from that (design §4.6, row G7c).
        _titleBand = titleBand;
        Top = figureTop + titleBand + topBand;
        Height = figureBottom - bottomBand - Top;
        _legendBand = LegendBand(spec, theme, gap, tick, Height, tickStyle, lineHeight);

        // The vertical axes run up from the panel's bottom, down when reversed: below them the bottom band, above them the
        // title's and the top one.
        var (below, above) = (figureBottom - (Top + Height), Top - figureTop);
        Prepare(
            vertical,
            sideScale0,
            verticalTransform,
            verticalDomain,
            Height,
            theme.TickSpacingY,
            tickStyle,
            lineHeight,
            theme,
            room: sideScale0.Reverse ? (above, below) : (below, above)
        );
        PrepareSecond(spec, horizontal: false, Height, theme, tickStyle, lineHeight, (below, above), secondary);
        var (leftBand, rightBand) = (0, 0);
        if (theme.ShowAxes)
        {
            leftBand = VerticalBand(Sides[SideLeft], tick, gap, tickStyle, titleHeight);
            rightBand = VerticalBand(Sides[SideRight], tick, gap, tickStyle, titleHeight);
        }

        _rightBand = rightBand;
        Left = figureLeft + leftBand;
        Width = figureRight - _legendBand - rightBand - Left;
        if (Empty)
        {
            return;
        }

        // The horizontal axes run right from the panel's left, left when reversed: the left band before them, the right one
        // and the legend's after.
        var (before, after) = (Left - figureLeft, figureRight - (Left + Width));
        Prepare(
            horizontal,
            bottomScale0,
            horizontalTransform,
            horizontalDomain,
            Width,
            theme.TickSpacingX,
            tickStyle,
            lineHeight,
            theme,
            room: bottomScale0.Reverse ? (after, before) : (before, after)
        );
        PrepareSecond(spec, horizontal: true, Width, theme, tickStyle, lineHeight, (before, after), secondary);
    }

    /// <summary>
    /// Puts each axis on its side (design §4.1, row G6a): a primary scale on its <c>Side</c> (the bottom and the left by
    /// default; flipped, x's bottom is the left and its top the right, y's left the bottom and its right the top), and its
    /// second axis, a secondary scale or a transform's, on the opposite side.
    /// </summary>
    private void AssignSides(PlotSpec spec)
    {
        Array.Clear(Sides);
        Family(spec.ScaleX, X, spec.ScaleX2, X2, horizontal: !Flipped);
        Family(spec.ScaleY, Y, spec.ScaleY2, Y2, horizontal: Flipped);

        void Family(ScaleSpec primary, TickAxis axis, ContinuousScaleSpec? secondScale, TickAxis second, bool horizontal)
        {
            var far = primary switch
            {
                ContinuousScaleSpec c => c.Side is AxisSide.Top or AxisSide.Right,
                BandScaleSpec b => b.Side is AxisSide.Top,
                _ => false,
            };
            var (near, farSide) = horizontal ? (SideBottom, SideTop) : (SideLeft, SideRight);
            Sides[far ? farSide : near] = new SideAxis(axis, new AxisScale(primary), Primary: true);
            if (secondScale is not null)
            {
                Sides[far ? near : farSide] = new SideAxis(second, new AxisScale(secondScale), Primary: false);
            }
            else if (primary is ContinuousScaleSpec { SecondaryAxis: { } derived })
            {
                Sides[far ? near : farSide] = new SideAxis(second, new AxisScale(primary, derived), Primary: false);
            }
        }
    }

    /// <summary>
    /// Chooses the ticks of a direction's second axis, if it has one (row G6a): a secondary scale's over its own domain, as a
    /// primary's; or a transform's over the primary's final domain, through the transform, without nice.
    /// </summary>
    private void PrepareSecond(
        PlotSpec spec,
        bool horizontal,
        int length,
        Theme theme,
        in TextStyle style,
        float lineHeight,
        (double Before, double After) room,
        SecondaryDomains secondary
    )
    {
        var (near, far) = horizontal ? (SideBottom, SideTop) : (SideLeft, SideRight);
        var at =
            Sides[near] is { Primary: false, Axis: not null } ? near
            : Sides[far] is { Primary: false, Axis: not null } ? far
            : -1;
        if (at < 0)
        {
            return;
        }

        var side = Sides[at];
        var ofX = side.Axis == X2;
        var spacing = horizontal ? theme.TickSpacingX : theme.TickSpacingY;
        if (side.Scale.Derived is not { } derived)
        {
            var (transform, domain) = ofX ? (secondary.XTransform, secondary.X) : (secondary.YTransform, secondary.Y);
            Prepare(
                side.Axis!,
                side.Scale,
                transform,
                domain,
                length,
                spacing,
                style,
                lineHeight,
                theme,
                room: side.Scale.Reverse ? (room.After, room.Before) : room
            );
            return;
        }

        // The axis of a transform of the primary: its ticks over the transformed ends, placed where they map back.
        var primary = ofX ? X : Y;
        var primaryScale = (ContinuousScaleSpec)side.Scale.Spec;
        var final = primary.FinalDomain;
        var (low, high) = (final.Origin + final.Lower, final.Origin + final.Upper);
        var (a, b) = (primary.Inverse(low), primary.Inverse(high));
        var (ta, tb) = (Apply(derived.Transform, a, forward: true), Apply(derived.Transform, b, forward: true));
        var magnitude = Math.Max(Math.Abs(a), Math.Abs(b));
        CheckRoundTrip(derived.Transform, a, ta, magnitude, ofX);
        CheckRoundTrip(derived.Transform, b, tb, magnitude, ofX);
        var decreasing = tb < ta;
        var composed = ofX ? _current.SecondaryX : _current.SecondaryY;
        composed.Set(primary.Transform, derived.Transform, decreasing);
        var reverse = primaryScale.Reverse != decreasing;
        Sides[at] = side with { Scale = side.Scale with { Flip = decreasing } };
        var axis = side.Axis!;
        (axis.Before, axis.After) = reverse ? (room.After, room.Before) : room;
        axis.Scale = FormatScale(derived.TickFormat, ofX);
        axis.SetBand(null, null);
        axis.Transform = composed;
        axis.Domain = decreasing ? ScaleRange.FromDouble(-high, -low) : final;
        axis.Length = length;
        axis.Spacing = spacing * _scale;
        axis.Style = style;
        axis.LineHeight = lineHeight;
        axis.Em = style.Size;
        if (length < 1 || !theme.ShowAxes)
        {
            axis.ClearCandidate();
            axis.SetFinalDomain(axis.Domain);
            return;
        }

        // A copy of the axis (no transform) shows the primary's ticks as they are, whatever its kind (ggplot2's dup_axis).
        if (derived.Transform is null)
        {
            axis.CopyTicks(primary);
            return;
        }

        axis.Choose(nice: false);

        static double Apply(IScale? transform, double value, bool forward)
        {
            if (transform is null)
            {
                return value;
            }

            Span<double> one = [value];
            if (forward)
            {
                transform.Forward(one, one);
            }
            else
            {
                transform.Inverse(one, one);
            }

            return one[0];
        }

        // Within 1e-9 of the larger end's magnitude: an end at 0 is the most usual one, and a transform that offsets and scales
        // brings it back a few ulps of the offset away.
        static void CheckRoundTrip(IScale? transform, double value, double mapped, double magnitude, bool ofX)
        {
            var back = Apply(transform, mapped, forward: false);
            if (!double.IsFinite(mapped) || !(Math.Abs(back - value) <= 1e-9 * magnitude))
            {
                throw new ArgumentException(
                    $"The {(ofX ? "ScaleX" : "ScaleY")}'s secondary axis maps the domain's end {value} to {mapped} and back to {back}; its transform is strictly monotonic over the domain (design §4.1)."
                );
            }
        }
    }

    /// <summary>Returns the scale a transform's axis formats its labels with: kept while the format is the same, so that a layout allocates nothing.</summary>
    private LinearScaleSpec FormatScale(TickFormatter? format, bool ofX)
    {
        ref var kept = ref ofX ? ref _formatX : ref _formatY;
        if (kept is null || !Equals(kept.TickFormat, format))
        {
            kept = Scale.Linear() with { TickFormat = format };
        }

        return kept;
    }

    /// <summary>Returns the height of a horizontal side's band: the ticks, the labels' line, and the title's (design §4.6).</summary>
    private static int HorizontalBand(in SideAxis side, int tick, int gap, float lineHeight, float titleHeight) =>
        side.Axis is null ? 0 : tick + gap + Ceiling(lineHeight) + (HasText(side.Scale.Title) ? gap + Ceiling(titleHeight) : 0);

    /// <summary>Returns the width of a vertical side's band: the ticks, the widest label, and the title's height (design §4.6).</summary>
    private int VerticalBand(in SideAxis side, int tick, int gap, in TextStyle style, float titleHeight)
    {
        if (side.Axis is null)
        {
            return 0;
        }

        var widest = 0f;
        foreach (var label in side.Axis.TickLabels)
        {
            widest = label.Length == 0 ? widest : MathF.Max(widest, _text.Measure(label, style).Width);
        }

        return tick + gap + Ceiling(widest) + (HasText(side.Scale.Title) ? gap + Ceiling(titleHeight) : 0);
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

    /// <summary>
    /// Returns where a label's center goes on the target (design §4.6, row G3b): its tick's place put on pixels when it does
    /// not cross F, else its place held inside F, so that its box ends at F's edge.
    /// </summary>
    private float Place(bool horizontal, AxisScale scale, TickAxis axis, int index, float tick)
    {
        var center = axis.LabelCenter(index);
        if (center == axis.Pixels[index])
        {
            return tick;
        }

        return (float)(horizontal ? HorizontalOf(scale, center) : VerticalOf(scale, center));
    }

    /// <summary>Returns a line style with its width and dashes in DIPs turned to pixels.</summary>
    internal LineStyle Scaled(in LineStyle style) => style with { Width = style.Width * _scale, Dash = style.Dash?.Scaled(_scale) };

    private static bool HasText(string? text) => !string.IsNullOrEmpty(text);

    /// <summary>
    /// Returns the label of a layer's own row in the legend, or null for none (design §4.6): a layer with a label, not split by
    /// categories (its rows are theirs, row G7c), and not colored by its values, an image (row G9) or 2D bins (row G5c), whose
    /// legend is the color bar.
    /// </summary>
    private static string? RowLabel(LayerSpec layer) =>
        layer.Label is { Length: > 0 } label && !SplitPlan.Splits(layer) && layer is not ImageLayerSpec && layer.Stat is not Bin2DStatSpec
            ? label
            : null;

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

    /// <summary>
    /// Returns the legend's band on the right (design §4.6): the swatch, a gap, and the widest label of the layers' rows and
    /// the color rows, or the color bar's width with its ticks' labels, the wider, and a gap; 0 without a legend.
    /// </summary>
    private int LegendBand(PlotSpec spec, Theme theme, int gap, int tick, int panelHeight, in TextStyle tickStyle, float lineHeight)
    {
        var widest = 0f;
        var style = Style(theme, theme.LegendSize);
        for (var i = 0; i < spec.Layers.Count; i++)
        {
            if (RowLabel(spec.Layers[i]) is { } label)
            {
                widest = MathF.Max(widest, _text.Measure(label, style).Width);
            }
        }

        ColorBarLength = 0;
        var bar = 0;
        if (_color is { } color && Categories(spec) is { } categories)
        {
            // The categories' rows, and the scale's title above them (design §4.6, row G7c).
            widest = TitleWidth(((OrdinalScaleSpec)color.Spec).Title, style, widest);
            for (var c = 0; c < categories.CategoryCount; c++)
            {
                widest = MathF.Max(widest, _text.Measure(categories.Categories[c], style).Width);
            }
        }
        else if (_color is { Inner: { } inner } continuous)
        {
            // The color bar: as long as the panel, held between half its length and its length (Vega's 100 to 200).
            ColorBarLength = Math.Clamp(panelHeight, Round(theme.ColorBarLength / 2), Math.Max(1, Round(theme.ColorBarLength)));
            Prepare(
                ColorBar,
                new AxisScale(inner),
                continuous.Transform,
                continuous.Domain,
                ColorBarLength,
                theme.TickSpacingY,
                tickStyle,
                lineHeight,
                theme,
                legend: true
            );
            continuous.Domain = ColorBar.FinalDomain;
            var labels = 0f;
            foreach (var label in ColorBar.TickLabels)
            {
                labels = label.Length == 0 ? labels : MathF.Max(labels, _text.Measure(label, tickStyle).Width);
            }

            bar = Math.Max(Round(theme.ColorBarThickness) + tick + gap + Ceiling(labels), Ceiling(TitleWidth(inner.Title, style, 0)));
        }

        var rows = widest > 0 ? Ceiling(Round(theme.LegendSwatch) + gap + widest) : 0;
        var band = Math.Max(rows, bar);
        return band > 0 ? band + gap : 0;
    }

    private float TitleWidth(string? title, in TextStyle style, float widest) =>
        HasText(title) ? MathF.Max(widest, _text.Measure(title!, style).Width) : widest;

    /// <summary>Returns the categories the legend lists: the ordinal scale's, when a layer is split by them; else null.</summary>
    private CategoricalColumn? Categories(PlotSpec spec)
    {
        if (_color is not { Categorical: true, Categories: { } categories })
        {
            return null;
        }

        for (var i = 0; i < spec.Layers.Count; i++)
        {
            if (SplitPlan.Splits(spec.Layers[i]))
            {
                return categories;
            }
        }

        return null;
    }

    private void Prepare(
        TickAxis axis,
        AxisScale scale,
        IScale? transform,
        ScaleRange domain,
        int length,
        float spacing,
        in TextStyle style,
        float lineHeight,
        Theme theme,
        bool legend = false,
        (double Before, double After)? room = null
    )
    {
        // The room from the axis's ends to the figure's edge, which its end labels are held inside (design §4.6, row G3b).
        (axis.Before, axis.After) = room ?? (double.PositiveInfinity, double.PositiveInfinity);
        var continuous = scale.Spec as ContinuousScaleSpec;
        axis.Scale = continuous!;
        axis.SetBand(scale.Spec as BandScaleSpec, axis == X ? _categories : null);
        axis.Transform = transform;
        axis.Domain = domain;
        axis.Length = length;
        axis.Spacing = spacing * _scale;
        axis.Style = style;
        axis.LineHeight = lineHeight;
        axis.Em = style.Size;

        // The color bar is the legend's (row G7c): its ticks stand whether the axes and the grid are shown or not.
        if (length < 1 || !(legend || theme.ShowAxes || theme.ShowGrid))
        {
            axis.ClearCandidate();
            axis.SetFinalDomain(domain);
            return;
        }

        var learned = continuous is TimeScaleSpec time ? time.Domain is null : continuous?.GivenDomain is null;
        axis.Choose(continuous is { Nice: true } && learned);
    }

    /// <summary>Returns the axis and the scale on the bottom: x, or y when flipped. Polar, the angle's.</summary>
    private (TickAxis Axis, AxisScale Scale) Bottom(PlotSpec spec) =>
        Flipped || spec.Coord is PolarCoordSpec { Theta: PolarTheta.Y } ? (Y, new(spec.ScaleY)) : (X, new(spec.ScaleX));

    /// <summary>Returns the axis and the scale on the left: y, or x when flipped. Polar, the radius's.</summary>
    private (TickAxis Axis, AxisScale Scale) Side(PlotSpec spec) =>
        Flipped || spec.Coord is PolarCoordSpec { Theta: PolarTheta.Y } ? (X, new(spec.ScaleX)) : (Y, new(spec.ScaleY));

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
        var legendBand = LegendBand(
            spec,
            theme,
            gap,
            tick,
            figureBottom - figureTop - titleBand,
            Style(theme, theme.TickLabelSize),
            _text.Measure("0", Style(theme, theme.TickLabelSize)).Height
        );
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
    private static double Share(TickAxis axis, AxisScale scale, double pixel)
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
    private double HorizontalOf(AxisScale scale, double pixel) => Left + (scale.Reverse ? Width - pixel : pixel);

    /// <summary>The target's y pixel of a pixel along the left axis: bottom to top, or top to bottom when reversed.</summary>
    private double VerticalOf(AxisScale scale, double pixel) => Top + (scale.Reverse ? pixel : Height - pixel);

    /// <summary>
    /// Records the cartesian axes on their sides (design §4.6, row G6a): a line along each side with an axis, half its width
    /// outside the panel and meeting the lines beside it; the ticks out from it; the labels beyond them; and the titles.
    /// The bottom and the left are row G3's; the top is the bottom's mirror and the right the left's.
    /// </summary>
    private void RecordAxes(RenderList list, PlotSpec spec, Theme theme, int gap, int tick, int padding)
    {
        var axisPixels = Round(theme.AxisWidth);
        var axis = new LineStyle(theme.AxisColor, theme.AxisWidth * _scale);
        var ticks = new LineStyle(theme.AxisColor, theme.TickWidth * _scale);
        var tickPixels = Round(theme.TickWidth);
        var halfTick = theme.TickWidth * _scale / 2;

        // The axis lines' centers half their width outside the panel's edges, so that they do not cover the data (design §4.6).
        var bottomLine = Snap(Top + Height + (axisPixels / 2.0), axisPixels);
        var topLine = Snap(Top - (axisPixels / 2.0), axisPixels);
        var leftLine = Snap(Left - (axisPixels / 2.0), axisPixels);
        var rightLine = Snap(Left + Width + (axisPixels / 2.0), axisPixels);
        var (bottom, top, left, right) = (Sides[SideBottom], Sides[SideTop], Sides[SideLeft], Sides[SideRight]);
        var (from, to) = (left.Axis is null ? Left : leftLine, right.Axis is null ? Left + Width : rightLine);
        var (high, low) = (top.Axis is null ? Top : topLine, bottom.Axis is null ? Top + Height : bottomLine);
        if (bottom.Axis is not null)
        {
            Line(list, new Vector2(from, bottomLine), new Vector2(to, bottomLine), axis);
        }

        if (top.Axis is not null)
        {
            Line(list, new Vector2(from, topLine), new Vector2(to, topLine), axis);
        }

        if (left.Axis is not null)
        {
            Line(list, new Vector2(leftLine, high), new Vector2(leftLine, low), axis);
        }

        if (right.Axis is not null)
        {
            Line(list, new Vector2(rightLine, high), new Vector2(rightLine, low), axis);
        }

        var labelStyle = Style(theme, theme.TickLabelSize);
        if (bottom.Axis is { } bottomAxis)
        {
            var labels = bottomAxis.TickLabels;
            var pixels = bottomAxis.Pixels;
            for (var i = 0; i < pixels.Length; i++)
            {
                var at = Snap(HorizontalOf(bottom.Scale, pixels[i]), tickPixels);
                Line(list, new Vector2(at, bottomLine), new Vector2(at, MathF.Max(bottomLine, Top + Height + tick - halfTick)), ticks);
                if (labels[i].Length > 0)
                {
                    list.DrawText(
                        labels[i],
                        new Vector2(
                            Place(horizontal: true, bottom.Scale, bottomAxis, i, at) - (bottomAxis.Extents[i] / 2),
                            Top + Height + tick + gap
                        ),
                        labelStyle with
                        {
                            Horizontal = TextAlign.Left,
                            Vertical = TextBaseline.Top,
                        }
                    );
                }
            }
        }

        if (top.Axis is { } topAxis)
        {
            var labels = topAxis.TickLabels;
            var pixels = topAxis.Pixels;
            for (var i = 0; i < pixels.Length; i++)
            {
                var at = Snap(HorizontalOf(top.Scale, pixels[i]), tickPixels);
                Line(list, new Vector2(at, topLine), new Vector2(at, MathF.Min(topLine, Top - tick + halfTick)), ticks);
                if (labels[i].Length > 0)
                {
                    list.DrawText(
                        labels[i],
                        new Vector2(Place(horizontal: true, top.Scale, topAxis, i, at) - (topAxis.Extents[i] / 2), Top - tick - gap),
                        labelStyle with
                        {
                            Horizontal = TextAlign.Left,
                            Vertical = TextBaseline.Bottom,
                        }
                    );
                }
            }
        }

        if (left.Axis is { } leftAxis)
        {
            var labels = leftAxis.TickLabels;
            var pixels = leftAxis.Pixels;
            for (var i = 0; i < pixels.Length; i++)
            {
                var at = Snap(VerticalOf(left.Scale, pixels[i]), tickPixels);
                Line(list, new Vector2(leftLine, at), new Vector2(MathF.Min(leftLine, Left - tick + halfTick), at), ticks);
                if (labels[i].Length > 0)
                {
                    list.DrawText(
                        labels[i],
                        new Vector2(
                            Left - tick - gap - _text.Measure(labels[i], labelStyle).Width,
                            Place(horizontal: false, left.Scale, leftAxis, i, at)
                        ),
                        labelStyle with
                        {
                            Horizontal = TextAlign.Left,
                            Vertical = TextBaseline.Middle,
                        }
                    );
                }
            }
        }

        if (right.Axis is { } rightAxis)
        {
            var labels = rightAxis.TickLabels;
            var pixels = rightAxis.Pixels;
            for (var i = 0; i < pixels.Length; i++)
            {
                var at = Snap(VerticalOf(right.Scale, pixels[i]), tickPixels);
                Line(list, new Vector2(rightLine, at), new Vector2(MathF.Max(rightLine, Left + Width + tick - halfTick), at), ticks);
                if (labels[i].Length > 0)
                {
                    list.DrawText(
                        labels[i],
                        new Vector2(Left + Width + tick + gap, Place(horizontal: false, right.Scale, rightAxis, i, at)),
                        labelStyle with
                        {
                            Horizontal = TextAlign.Left,
                            Vertical = TextBaseline.Middle,
                        }
                    );
                }
            }
        }

        RecordSideTitles(list, theme, padding);
    }

    /// <summary>
    /// Records the cartesian axes' titles (design §4.6, row G6a): the bottom one at the figure's bottom, the top one under the
    /// plot's title, both centered on the panel; the left one turned counterclockwise at the figure's left, the right one
    /// turned clockwise against the legend's band, so that it reads top to bottom, both centered beside the panel.
    /// </summary>
    private void RecordSideTitles(RenderList list, Theme theme, int padding)
    {
        var titleStyle = Style(theme, theme.AxisTitleSize);
        if (Sides[SideBottom].Scale.Title is { Length: > 0 } bottomTitle && Sides[SideBottom].Axis is not null)
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

        if (Sides[SideTop].Scale.Title is { Length: > 0 } topTitle && Sides[SideTop].Axis is not null)
        {
            list.DrawText(
                topTitle,
                new Vector2(Left + (Width / 2f) - (_text.Measure(topTitle, titleStyle).Width / 2), padding + _titleBand),
                titleStyle with
                {
                    Horizontal = TextAlign.Left,
                    Vertical = TextBaseline.Top,
                }
            );
        }

        if (Sides[SideLeft].Scale.Title is { Length: > 0 } leftTitle && Sides[SideLeft].Axis is not null)
        {
            list.DrawText(
                leftTitle,
                new Vector2(padding, Top + (Height / 2f) + (_text.Measure(leftTitle, titleStyle).Width / 2)),
                titleStyle with
                {
                    Horizontal = TextAlign.Left,
                    Vertical = TextBaseline.Top,
                    Rotation = TextRotation.CounterClockwise90,
                }
            );
        }

        if (Sides[SideRight].Scale.Title is { Length: > 0 } rightTitle && Sides[SideRight].Axis is not null)
        {
            list.DrawText(
                rightTitle,
                new Vector2(_width - padding - _legendBand, Top + (Height / 2f) - (_text.Measure(rightTitle, titleStyle).Width / 2)),
                titleStyle with
                {
                    Horizontal = TextAlign.Left,
                    Vertical = TextBaseline.Top,
                    Rotation = TextRotation.Clockwise90,
                }
            );
        }
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
        var x = Left + Width + _rightBand + gap;
        var y = (float)Top;
        for (var i = 0; i < spec.Layers.Count; i++)
        {
            // An index, not foreach: the interface's enumerator would be boxed on every recording.
            var layer = spec.Layers[i];
            if (RowLabel(layer) is not { } label)
            {
                // A split layer's rows are its categories' (design §4.6, row G7c).
                continue;
            }

            // The sample takes the color the layer is drawn in: the spec's, or the series color (design §4.1, row G7a).
            var series = theme.Palette.Series(i);
            var row = Sample(list, layer, series, x, y, swatch, lineHeight, style);
            var middle = y + (row / 2);
            list.DrawText(label, new Vector2(x + swatch + gap, middle), style with { Horizontal = TextAlign.Left, Vertical = TextBaseline.Middle });
            y += row;
        }

        if (Categories(spec) is { } categories)
        {
            RecordCategories(list, spec, theme, categories, x, y, swatch, gap, lineHeight, style);
        }
        else if (_color is { Inner: { } inner } && ColorBarLength > 0)
        {
            RecordColorBar(list, theme, inner, x, y, gap, lineHeight, style);
        }
    }

    /// <summary>
    /// Records the rows of the categories (design §4.6, row G7c): the ordinal scale's title, then a swatch and a name a
    /// category, in their numbers' order. The swatch is the first split layer's, in the category's color.
    /// </summary>
    private void RecordCategories(
        RenderList list,
        PlotSpec spec,
        Theme theme,
        CategoricalColumn categories,
        float x,
        float y,
        int swatch,
        int gap,
        float lineHeight,
        in TextStyle style
    )
    {
        var color = _color!;
        if (((OrdinalScaleSpec)color.Spec).Title is { Length: > 0 } title)
        {
            list.DrawText(title, new Vector2(x, y + (lineHeight / 2)), style with { Horizontal = TextAlign.Left, Vertical = TextBaseline.Middle });
            y += lineHeight;
        }

        LayerSpec? first = null;
        for (var i = 0; i < spec.Layers.Count && first is null; i++)
        {
            first = SplitPlan.Splits(spec.Layers[i]) ? spec.Layers[i] : null;
        }

        for (var c = 0; c < categories.CategoryCount; c++)
        {
            var row = Sample(list, first!, color.Palette.Series(c), x, y, swatch, lineHeight, style);
            list.DrawText(
                categories.Categories[c],
                new Vector2(x + swatch + gap, y + (row / 2)),
                style with
                {
                    Horizontal = TextAlign.Left,
                    Vertical = TextBaseline.Middle,
                }
            );
            y += row;
        }
    }

    /// <summary>
    /// Records the color bar (design §4.6, row G7c): its title, then a bar of Lc rows of a pixel, each in the color of its
    /// center's value, the domain's low end at the bottom; ticks on its right, and their labels.
    /// </summary>
    private void RecordColorBar(
        RenderList list,
        Theme theme,
        ContinuousScaleSpec inner,
        float x,
        float y,
        int gap,
        float lineHeight,
        in TextStyle style
    )
    {
        var color = _color!;
        if (HasText(inner.Title))
        {
            list.DrawText(
                inner.Title!,
                new Vector2(x, y + (lineHeight / 2)),
                style with
                {
                    Horizontal = TextAlign.Left,
                    Vertical = TextBaseline.Middle,
                }
            );
            y += lineHeight;
        }

        var length = ColorBarLength;
        var thickness = Round(theme.ColorBarThickness);
        var (low, high) = (color.Domain.Origin + color.Domain.Lower, color.Domain.Origin + color.Domain.Upper);
        for (var r = 0; r < length; r++)
        {
            // Row r from the bottom, at its center's value of the scale's space.
            var s = low + ((high - low) * (r + 0.5) / length);
            list.DrawRect(x, y + length - r - 1, thickness, 1, color.Map!.Map(color.Position(s)));
        }

        var tick = Round(theme.TickLength);
        var tickStyle = Style(theme, theme.TickLabelSize) with { Horizontal = TextAlign.Left, Vertical = TextBaseline.Middle };
        var stroke = new LineStyle(theme.AxisColor, theme.TickWidth * _scale);
        var width = Round(theme.TickWidth);
        var pixels = ColorBar.Pixels;
        var labels = ColorBar.TickLabels;
        for (var i = 0; i < pixels.Length; i++)
        {
            var at = Snap(y + length - pixels[i], width);
            Line(list, new Vector2(x + thickness, at), new Vector2(x + thickness + tick, at), stroke);
            if (labels[i].Length > 0)
            {
                list.DrawText(labels[i], new Vector2(x + thickness + tick + gap, at), tickStyle);
            }
        }
    }

    /// <summary>
    /// Records a layer's sample at the start of a legend row and returns the row's height (design §4.6, §5.5.3): a fill
    /// geom's square of the row's height in its fill (row G4), a text layer's "a" in its color at the legend's size in the
    /// middle of the swatch (row G8), else a line of the swatch's length in the layer's stroke.
    /// </summary>
    private float Sample(RenderList list, LayerSpec layer, Color series, float x, float y, int swatch, float lineHeight, in TextStyle style)
    {
        if (layer is TextLayerSpec text)
        {
            var sample = style with { Color = text.Color ?? series, Horizontal = TextAlign.Left, Vertical = TextBaseline.Middle };
            list.DrawText(SampleText, new Vector2(x + ((swatch - _text.Measure(SampleText, sample).Width) / 2), y + (lineHeight / 2)), sample);
            return lineHeight;
        }

        if (Fill(layer, series, out var fill))
        {
            list.DrawRect(x, y, lineHeight, lineHeight, fill);
            return lineHeight;
        }

        var stroke = Scaled(Stroke(layer, series));
        var row = MathF.Max(lineHeight, stroke.Width);
        Line(list, new Vector2(x, y + (row / 2)), new Vector2(x + swatch, y + (row / 2)), stroke);
        return row;
    }

    /// <summary>
    /// Returns whether a layer is a fill geom (area, band, bar, rect), and its fill as drawn: the spec's, or the series color,
    /// at alpha 128 for an area or a band (design §4.1).
    /// </summary>
    private static bool Fill(LayerSpec layer, Color series, out Color fill)
    {
        (var found, fill) = layer switch
        {
            AreaLayerSpec a => (true, a.Fill ?? series with { A = 128 }),
            BandLayerSpec b => (true, b.Fill ?? series with { A = 128 }),
            BarLayerSpec b => (true, b.Fill ?? series),
            RectLayerSpec r => (true, r.Fill ?? series),
            _ => (false, default),
        };
        return found;
    }

    /// <summary>Returns the stroke of a line geom's sample as drawn, or the series color 1 wide for a user's geom (design §4.1).</summary>
    private static LineStyle Stroke(LayerSpec layer, Color series) =>
        layer switch
        {
            LineLayerSpec l => l.Style.Resolve(series),
            StepLayerSpec s => s.Style.Resolve(series),
            RuleLayerSpec r => r.Style.Resolve(series),
            SegmentLayerSpec s => s.Style.Resolve(series),
            ErrorBarLayerSpec e => e.Style.Resolve(series),
            _ => new LineStyle(series, 1),
        };

    private void Line(RenderList list, Vector2 from, Vector2 to, in LineStyle style)
    {
        _segment[0] = from;
        _segment[1] = to;
        list.DrawPolyline(_segment, style);
    }

    /// <summary>
    /// An axis's scale as the guides read it (row G7b): its title, and whether its ticks run from the far end. A band scale
    /// reverses its categories' positions itself (design §4.2), so its ticks come already in place.
    /// </summary>
    /// <remarks>
    /// The axis of a transform of a primary scale (row G6a) reads its title from <see cref="Derived"/>, and its ticks run the
    /// other way when the transform decreases (<see cref="Flip"/>).
    /// </remarks>
    internal readonly record struct AxisScale(ScaleSpec Spec, SecondaryAxisSpec? Derived = null, bool Flip = false)
    {
        public string? Title =>
            Derived is { } derived
                ? derived.Title
                : Spec switch
                {
                    ContinuousScaleSpec continuous => continuous.Title,
                    BandScaleSpec band => band.Title,
                    _ => null,
                };

        public bool Reverse => (Spec is ContinuousScaleSpec { Reverse: true }) != Flip;
    }

    /// <summary>An axis on a side of the panel (row G6a): its ticks, its scale, and whether it is its direction's primary, which draws the grid.</summary>
    internal readonly record struct SideAxis(TickAxis? Axis, AxisScale Scale, bool Primary);
}
