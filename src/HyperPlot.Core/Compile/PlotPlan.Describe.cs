using System.Globalization;
using System.Text;

namespace HyperPlot;

/// <content>The plot's summary in words (design §7.1 "要約", row W1c, Issue #323).</content>
internal sealed partial class PlotPlan
{
    /// <summary>
    /// Writes the summary of the plot as last drawn (requirement N-10): its kind (the layers' geoms), its title, each axis's
    /// title and the range of the current view, the series' labels and geoms with those not drawn marked, and a facet's
    /// number of panels. Allocates; off the frame path.
    /// </summary>
    internal void Describe(StringBuilder text, string name)
    {
        var spec = _spec;
        if (spec is null)
        {
            _ = text.Append(name).Append(": not drawn yet.");
            return;
        }

        _ = text.Append(name);
        if (spec.Title is { Length: > 0 } title)
        {
            _ = text.Append(" \"").Append(title).Append('"');
        }

        _ = text.Append(": ");
        var geoms = spec.Layers.Select(GeomOf).Distinct().ToArray();
        _ = geoms.Length == 0 ? text.Append("no layers") : text.Append(string.Join(", ", geoms)).Append(geoms.Length == 1 ? " layer" : " layers");
        if (spec.Facet is not null && _panels.Length > 0)
        {
            _ = text.Append(CultureInfo.InvariantCulture, $", in {_panels.Length} panels; the axes of the first");
        }

        _ = text.Append('.');
        if (_whole is null && _guides.PanelCount > 0)
        {
            DescribeAxes(text, spec, _guides.PanelAt(0));
        }

        _ = text.Append(" Series: ");
        for (var i = 0; i < spec.Layers.Count; i++)
        {
            var layer = spec.Layers[i];
            _ = text.Append(i == 0 ? string.Empty : ", ");
            if (layer.Label is { Length: > 0 } label)
            {
                _ = text.Append('"').Append(label).Append("\" (").Append(GeomOf(layer)).Append(Drawn(i) ? ")" : ", not drawn)");
            }
            else
            {
                _ = text.Append(GeomOf(layer)).Append(Drawn(i) ? string.Empty : " (not drawn)");
            }
        }

        _ = text.Append(spec.Layers.Count == 0 ? "none." : ".");

        // A layer that failed, or that is being made or waits for its workers' results, is not drawn (Issue #323 Decision 4).
        bool Drawn(int layer) =>
            _whole is null
            && !Failed(layer)
            && (_panels.Length == 0 || layer >= _panels[0].Layers.Length || _panels[0].Layers[layer].Readiness != LayerReadiness.Pending);
    }

    /// <summary>Returns the words of a layer's geom, with its stat.</summary>
    private static string GeomOf(LayerSpec layer)
    {
        var (geom, stat) = GeomWords(layer);
        return stat is null ? geom : $"{geom} of {stat}";
    }

    /// <summary>Returns the word of a layer's geom and the word of its stat, or null for none: no string is made (row I2b).</summary>
    private static (string Geom, string? Stat) GeomWords(LayerSpec layer)
    {
        var geom = layer switch
        {
            LineLayerSpec => "line",
            StepLayerSpec => "step",
            AreaLayerSpec => "area",
            BandLayerSpec => "band",
            BarLayerSpec => "bar",
            ErrorBarLayerSpec => "error bar",
            RuleLayerSpec => "rule",
            RectLayerSpec => "rectangle",
            SegmentLayerSpec => "segment",
            TextLayerSpec => "text",
            ImageLayerSpec => "image",
            _ => "custom",
        };
        var stat = layer.Stat switch
        {
            null => null,
            BinStatSpec => "bins",
            CountStatSpec => "counts",
            SummaryStatSpec => "summary",
            DensityStatSpec => "density",
            SmoothStatSpec => "smooth",
            Bin2DStatSpec => "2D bins",
            _ => "stat",
        };
        return (geom, stat);
    }

    /// <summary>
    /// Writes each axis's title and range (Issue #323 Decision 3): x and y by their names, flipped or not; the angle and the
    /// radius in polar; the secondary axes when the spec has them.
    /// </summary>
    private static void DescribeAxes(StringBuilder text, PlotSpec spec, PanelGuide panel)
    {
        var polar = spec.Coord as PolarCoordSpec;
        var (xName, yName) =
            polar is null ? ("X", "Y")
            : polar.Theta == PolarTheta.X ? ("Angle", "Radius")
            : ("Radius", "Angle");
        Axis(xName, spec.ScaleX, panel.X);
        Axis(yName, spec.ScaleY, panel.Y);
        if (spec.ScaleX2 is { } x2)
        {
            Axis("Secondary X", x2, panel.X2);
        }

        if (spec.ScaleY2 is { } y2)
        {
            Axis("Secondary Y", y2, panel.Y2);
        }

        void Axis(string name, ScaleSpec scale, TickAxis axis)
        {
            _ = text.Append(' ').Append(name).Append(" axis");
            if (TitleOf(scale) is { Length: > 0 } title)
            {
                _ = text.Append(" \"").Append(title).Append('"');
            }

            _ = text.Append(": ");
            axis.DescribeRange(text);
            _ = text.Append('.');
        }
    }

    private static string? TitleOf(ScaleSpec scale) =>
        scale switch
        {
            ContinuousScaleSpec continuous => continuous.Title,
            BandScaleSpec band => band.Title,
            _ => null,
        };
}
