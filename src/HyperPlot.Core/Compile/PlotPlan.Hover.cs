using System.Numerics;
using HyperPlot.Interaction;
using HyperPlot.Rendering;

namespace HyperPlot;

/// <content>What a hover draws from a plan's hits (design §6.5 "ホバー", row I2b, Issue #347).</content>
internal sealed partial class PlotPlan
{
    /// <summary>
    /// Returns the sum of the versions of every column the plot's layers read, rasters included (row I2b): it grows when one
    /// moves, so that a frame of the mouse alone is drawn again from the data. Allocates nothing.
    /// </summary>
    internal long ColumnVersions()
    {
        var sum = 0L;
        foreach (var panel in _panels)
        {
            foreach (var layer in panel.Layers)
            {
                foreach (var column in layer.Columns)
                {
                    sum += column.Version;
                }

                for (var r = 0; r < layer.Rasters.Count; r++)
                {
                    sum += layer.Rasters[r].Values.Version;
                }
            }
        }

        return sum;
    }

    /// <summary>
    /// Returns the pixel of a hit's x and y on its panel's axes as last drawn (row I2b): where the hover marks it. False when
    /// the hit's panel or layer is not drawn any more.
    /// </summary>
    internal bool HitPixel(in SeriesHit hit, out Vector2 pixel)
    {
        pixel = default;
        if (_spec is not { } spec || hit.Panel >= _panels.Length || hit.Layer >= spec.Layers.Count)
        {
            return false;
        }

        var frames = _panels[hit.Panel].Frames;
        var layer = spec.Layers[hit.Layer];
        var plans = _panels[hit.Panel].Layers;
        var x = layer.SecondaryX ? frames.X2 : frames.X;

        // A layer places its own: a dodged bar in its place, a category's in its band (row I2c).
        var along = hit.Layer < plans.Length ? plans[hit.Layer].HitAlong(x, hit) : HitScan.PixelOfValue(x, hit.X);
        var across = HitScan.PixelOfValue(layer.SecondaryY ? frames.Y2 : frames.Y, hit.Y);
        pixel = _guides.Flipped ? new Vector2((float)across, (float)along) : new Vector2((float)along, (float)across);
        return float.IsFinite(pixel.X) && float.IsFinite(pixel.Y);
    }

    /// <summary>
    /// Returns the rows of a layer a selection holds (design §6.4 "行の集合", row I3a): the rows of its points for points, else
    /// the layer's answer on its pair of axes; a facet's gathered panel's rows taken back to the plot's table.
    /// </summary>
    internal ValueTask<RowSelection> SelectRows(int panel, int layer, Selection2D selection, PlotWorkerPool pool)
    {
        if (_spec is not { } spec || panel >= _panels.Length || layer >= _panels[panel].Layers.Length)
        {
            return ValueTask.FromResult(RowSelection.Empty);
        }

        if (selection.Kind == SelectionKind.Points)
        {
            var (rows, derived) = (new List<long>(), false);
            foreach (var point in selection.Points.Span)
            {
                if (point.Layer == layer)
                {
                    rows.Add(point.Row);
                    derived |= point.Derived;
                }
            }

            return ValueTask.FromResult(RowSelection.OfRows(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(rows), derived));
        }

        var frames = _panels[panel].Frames;
        var layerSpec = spec.Layers[layer];
        var query = new SelectionQuery(selection, layerSpec.SecondaryX ? frames.X2 : frames.X, layerSpec.SecondaryY ? frames.Y2 : frames.Y, pool);
        var answer = _panels[panel].Layers[layer].SelectRows(query);
        if (_panels[panel].Data is { Map: not null } data && _facet is { } facet && !FacetData.Rowless(layerSpec))
        {
            ISourceRows rows = facet;
            var key = data.Key;
            return SelectionQuery.Union([(answer.AsTask(), row => rows.SourceRow(key, row))], query.Derived);
        }

        return answer;
    }

    /// <summary>
    /// Records a panel's lines again over the plot, twice as wide and clipped to a rectangle, for a selection's highlight
    /// (design §6.4, row I3a): a line, a step, and an area's outline as they were drawn last.
    /// </summary>
    internal void Highlight(int panel, RenderList list, int left, int top, int width, int height)
    {
        if (panel >= _panels.Length || width <= 0 || height <= 0)
        {
            return;
        }

        list.SetClip(left, top, width, height);
        foreach (var layer in _panels[panel].Layers)
        {
            layer.Highlight(list);
        }

        list.ResetClip();
    }

    /// <summary>Returns the color a hit's series is drawn in (row I2b): its line's, its fill's, or its category's.</summary>
    internal Color HitColor(in SeriesHit hit)
    {
        var panel = _panels[Math.Min(hit.Panel, _panels.Length - 1)];
        return hit.Layer < panel.Layers.Length ? panel.Layers[hit.Layer].HitColor(hit.Series) : new HyperPlot.Color(0, 0, 0, 255);
    }

    /// <summary>
    /// Writes a hover's default tooltip (design §6.5, Issue #347 Decision 4): a line a hit, "name: x, y", the name the layer's
    /// label or its geom, the values in the format of their axes' tick labels; a line feed between two. Returns the characters
    /// written, as many as fit. Allocates nothing.
    /// </summary>
    /// <summary>
    /// Returns a value of the scale space in data units (row I4b): through the axis's transform's inverse, else as it is.
    /// </summary>
    internal double DataValue(bool ofX, long origin, double difference)
    {
        var transform = ofX ? _xTransform : _yTransform;
        if (transform is null)
        {
            return origin + difference;
        }

        Span<double> one = [origin + difference];
        transform.Inverse(one, one);
        return one[0];
    }

    /// <summary>Returns the seconds of one unit of a time axis (row I4b's slope), or 1 for another axis.</summary>
    internal double SecondsPerUnit(bool ofX) =>
        (_spec is { } spec ? (ofX ? spec.ScaleX : spec.ScaleY) : null) is TimeScaleSpec time
            ? time.Unit switch
            {
                TimeUnit.Ticks => 1e-7,
                TimeUnit.Nanoseconds => 1e-9,
                TimeUnit.Microseconds => 1e-6,
                TimeUnit.Milliseconds => 1e-3,
                _ => 1,
            }
            : 1;

    /// <summary>
    /// Writes a measurement's lines (design §6.5 "計測", row I4b, Issue #390 Decisions 6 and 11): "Δx", "Δy", and "slope" for
    /// points, a time axis's Δx in seconds; a line a series for a range, its count, mean, least, and greatest in the y axis's
    /// format, and "… (n more)" for the series past the room. Returns the characters written.
    /// </summary>
    internal int WriteMeasurement(Measurement2D measurement, Span<char> destination)
    {
        if (_spec is not { } spec || measurement.Panel.Panel >= _guides.PanelCount)
        {
            return 0;
        }

        var guide = _guides.PanelAt(measurement.Panel.Panel);
        Span<char> line = stackalloc char[256];
        var written = 0;
        if (measurement.Kind == MeasureKind.Points)
        {
            written += Append("Δx: ", destination);
            if (spec.ScaleX is TimeScaleSpec)
            {
                written += Number(measurement.DeltaX * SecondsPerUnit(ofX: true), destination[written..]);
                written += Append(" s", destination[written..]);
            }
            else
            {
                written += guide.X.WriteValue(measurement.DeltaX, destination[written..]);
            }

            written += Append("\nΔy: ", destination[written..]);
            written += guide.Y.WriteValue(measurement.DeltaY, destination[written..]);
            written += Append("\nslope: ", destination[written..]);
            written += Number(measurement.Slope, destination[written..]);
            return written;
        }

        var statistics = measurement.Statistics.Span;
        for (var i = 0; i < statistics.Length; i++)
        {
            var s = statistics[i];
            var length = 0;
            var layer = s.Layer < spec.Layers.Count ? spec.Layers[s.Layer] : null;
            if (layer?.Label is { Length: > 0 } label)
            {
                length += Append(label, line);
            }
            else if (layer is not null)
            {
                length += Append(GeomWords(layer).Geom, line);
            }

            length += Append(": n=", line[length..]);
            length += Number(s.Count, line[length..]);
            length += Append(", mean=", line[length..]);
            length += guide.Y.WriteValue(s.Mean, line[length..]);
            length += Append(", min=", line[length..]);
            length += guide.Y.WriteValue(s.Min, line[length..]);
            length += Append(", max=", line[length..]);
            length += guide.Y.WriteValue(s.Max, line[length..]);

            // A line that leaves no room for the note of the rest is not written: the note takes its place.
            var rest = statistics.Length - i - 1;
            var needed = (written > 0 ? 1 : 0) + length + (rest > 0 ? 20 : 0);
            if (written + needed > destination.Length)
            {
                written += Append(written > 0 ? "\n… (" : "… (", destination[written..]);
                written += Number(statistics.Length - i, destination[written..]);
                written += Append(" more)", destination[written..]);
                return written;
            }

            if (written > 0)
            {
                written += Append("\n", destination[written..]);
            }

            written += Append(line[..length], destination[written..]);
        }

        return written;

        static int Number(double value, Span<char> to) =>
            value.TryFormat(to, out var count, "G6", System.Globalization.CultureInfo.InvariantCulture) ? count : 0;

        static int Append(ReadOnlySpan<char> text, Span<char> to)
        {
            var count = Math.Min(text.Length, to.Length);
            text[..count].CopyTo(to);
            return count;
        }
    }

    internal int WriteTooltip(ReadOnlySpan<SeriesHit> hits, Span<char> destination)
    {
        var written = 0;
        if (_spec is not { } spec)
        {
            return 0;
        }

        for (var i = 0; i < hits.Length; i++)
        {
            var hit = hits[i];
            if (hit.Layer >= spec.Layers.Count || hit.Panel >= _guides.PanelCount)
            {
                continue;
            }

            if (written > 0)
            {
                written += Append("\n", destination[written..]);
            }

            var layer = spec.Layers[hit.Layer];
            if (layer.Label is { Length: > 0 } label)
            {
                written += Append(label, destination[written..]);
            }
            else
            {
                var (geom, stat) = GeomWords(layer);
                written += Append(geom, destination[written..]);
                if (stat is not null)
                {
                    written += Append(" of ", destination[written..]);
                    written += Append(stat, destination[written..]);
                }
            }

            var guide = _guides.PanelAt(hit.Panel);
            written += Append(": ", destination[written..]);
            written += (layer.SecondaryX ? guide.X2 : guide.X).WriteValue(hit.X, destination[written..]);
            written += Append(", ", destination[written..]);
            written += (layer.SecondaryY ? guide.Y2 : guide.Y).WriteValue(hit.Y, destination[written..]);
        }

        return written;

        static int Append(ReadOnlySpan<char> text, Span<char> to)
        {
            var count = Math.Min(text.Length, to.Length);
            text[..count].CopyTo(to);
            return count;
        }
    }
}
