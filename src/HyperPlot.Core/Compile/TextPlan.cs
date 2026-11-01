using System.Globalization;
using System.Numerics;
using HyperPlot.Rendering;

namespace HyperPlot;

/// <summary>
/// Labels a row (design §5.5.3, row G8): every row, up to 4,096 for the plot's text layers together, at its point mapped
/// through the coordinate system, moved by the offset and put on the point by its alignment. Category labels are the
/// column's names; numeric ones are written once a version of their column or a format, never on the frame path.
/// </summary>
internal sealed class TextPlan : LayerPlan
{
    private string?[] _labels = [];
    private long _labelsVersion = -1;
    private TickFormatter? _labelsFormat;

    public TextPlan(TextLayerSpec spec, RowRange rows)
        : base(spec, rows) => SetColumns(spec.X.Column, spec.Y.Column, spec.Text.Column);

    /// <summary>Gets the number of times the numeric labels were written. Tests only.</summary>
    public int Writes { get; private set; }

    public override void AddRanges(RangeSink sink, bool ofX, DataTable data)
    {
        var text = (TextLayerSpec)Spec;
        var channel = ofX ? text.X : text.Y;

        // Categories of x are the band's: its domain comes from them (design §4.2).
        if (channel.Column is not CategoricalColumn)
        {
            AddColumn(sink, channel.Column, data.GetName(channel.Index));
        }
    }

    public override void Record(RecordContext context)
    {
        var text = (TextLayerSpec)Spec;
        var length = text.X.Column.Length;
        var (start, end) = (Rows.Start, Rows.EndIn(length));
        if (start >= end)
        {
            return;
        }

        var scale = context.Scale;
        var theme = context.Theme;
        var style = new TextStyle(
            text.Color ?? SeriesColor,
            text.Size * scale,
            theme.FontFamily,
            FontWeight.Normal,
            text.Horizontal,
            text.Vertical,
            text.Rotation,
            theme.Locale
        );

        // The point of the box put on the label's point: TextStyle aligns lines inside the block only, whose left edge is at
        // the position, so the geom moves the position back by a share of the block's width along the reading direction.
        var share = text.Horizontal switch
        {
            TextAlign.Center => 0.5,
            TextAlign.Right => 1.0,
            _ => 0.0,
        };
        var (ux, uy) = text.Rotation switch
        {
            TextRotation.Clockwise90 => (0.0, 1.0),
            TextRotation.CounterClockwise90 => (0.0, -1.0),
            _ => (1.0, 0.0),
        };
        var (dx, dy) = ((double)text.OffsetX * scale, -(double)text.OffsetY * scale);

        var names = text.Text.Column as CategoricalColumn;
        if (names is null)
        {
            WriteLabels(text, start, end);
        }

        // Categories of x sit at the middle of their band, whose pixels are linear in its position: a · j + b.
        var xCategories = text.X.Column as CategoricalColumn;
        var (a, b) = (0.0, 0.0);
        var xs = ReadOnlySpan<double>.Empty;
        if (xCategories is not null)
        {
            if (context.X.Band is not { } band)
            {
                return;
            }

            b = context.PixelX(0 - context.XOrigin);
            a = context.PixelX(1 - context.XOrigin) - b;
            b += a * band.Width / 2;
        }
        else
        {
            xs = context.ElementPixels(text.X.Column, ofX: true, 0);
        }

        var ys = context.ElementPixels(text.Y.Column, ofX: false, 1);
        var (left, top, width, height) = context.Panel;
        var engine = context.Text;
        for (var i = start; i < end; i++)
        {
            var label = names is null ? _labels[i - start] : Name(names, i);
            if (string.IsNullOrEmpty(label))
            {
                continue;
            }

            double along;
            if (xCategories is not null)
            {
                var code = xCategories.GetCode(i);
                if (code < 0)
                {
                    continue;
                }

                along = (a * context.X.Band!.Value.Position(code)) + b;
            }
            else
            {
                along = xs[(int)i];
            }

            var across = ys[(int)i];
            if (!double.IsFinite(along) || !double.IsFinite(across))
            {
                continue;
            }

            var (px, py) = Point(context, along, across);
            if (!(px >= left && px <= left + width && py >= top && py <= top + height))
            {
                // Outside the panel, or inside a polar hole (NaN).
                continue;
            }

            var shift = share > 0 ? share * engine.Measure(label, style).Width : 0;
            context.List.DrawText(label, new Vector2((float)(px + dx - (shift * ux)), (float)(py + dy - (shift * uy))), style);
        }
    }

    /// <summary>Returns the target's point of an along × across point: swapped when flipped, on the circle when polar.</summary>
    private static (double X, double Y) Point(RecordContext context, double along, double across)
    {
        if (context.Polar)
        {
            var point = context.PolarMap.Map(along, across);
            return (point.X, point.Y);
        }

        return context.Flipped ? (across, along) : (along, across);
    }

    /// <summary>Returns a row's category name, or null for a missing one.</summary>
    private static string? Name(CategoricalColumn names, long row)
    {
        var code = names.GetCode(row);
        return code < 0 ? null : names.Categories[code];
    }

    /// <summary>
    /// Writes the numeric labels of the rows from their column's values, when its version or the format changed since the
    /// last time (design §5.5.3): a NaN row has none, and an empty one draws nothing.
    /// </summary>
    private void WriteLabels(TextLayerSpec text, long start, long end)
    {
        var column = text.Text.Column;
        var format = text.Format;

        // The version is read before the values (PR #189's review), as the plan reads its columns' before the wait: an
        // Invalidate that comes while the labels are written leaves the version kept behind the column's, and the next
        // Render writes them again.
        var version = column.Version;
        if (_labelsVersion == version && ReferenceEquals(_labelsFormat, format) && _labels.Length == end - start)
        {
            return;
        }

        if (_labels.Length != end - start)
        {
            _labels = new string?[end - start];
        }

        Span<char> buffer = stackalloc char[64];
        for (var i = start; i < end; i++)
        {
            var value = column.DifferenceAt(i, 0);
            if (double.IsNaN(value))
            {
                _labels[i - start] = null;
                continue;
            }

            int written;
            if (format is null)
            {
                _ = value.TryFormat(buffer, out written, "G6", CultureInfo.InvariantCulture);
            }
            else if (!format(value, buffer, out written) || written < 0 || written > buffer.Length)
            {
                throw new InvalidOperationException(
                    $"The Format of layer {Index} returned false or a length of {written} for {value.ToString(CultureInfo.InvariantCulture)}; a label is written into 64 characters (design §4.6)."
                );
            }

            _labels[i - start] = written == 0 ? string.Empty : new string(buffer[..written]);
        }

        (_labelsVersion, _labelsFormat) = (version, format);
        Writes++;
    }
}
