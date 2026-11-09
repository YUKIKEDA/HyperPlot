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
