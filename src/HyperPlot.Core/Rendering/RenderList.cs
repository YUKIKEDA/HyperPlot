using System.Numerics;

namespace HyperPlot.Rendering;

/// <summary>
/// The commands of one frame, in buffers kept from frame to frame (design §5.1). Row R2 records polylines and clips, in
/// physical pixels of the render target, with the origin at the top left and pixel centers at +0.5.
/// </summary>
/// <remarks>One thread at a time uses a list. After the buffers have grown to a frame's size, recording allocates nothing.</remarks>
public sealed class RenderList
{
    private PolylineRecord[] _polylines = new PolylineRecord[4];
    private SegmentRecord[] _segments = new SegmentRecord[256];
    private int _polylineCount;
    private int _segmentCount;
    private ClipRect _clip = ClipRect.All;

    /// <summary>Gets the recorded polylines, in order.</summary>
    internal ReadOnlySpan<PolylineRecord> Polylines => _polylines.AsSpan(0, _polylineCount);

    /// <summary>Gets the segments of every polyline; each polyline names its range.</summary>
    internal ReadOnlySpan<SegmentRecord> Segments => _segments.AsSpan(0, _segmentCount);

    /// <summary>Removes every command and resets the clip to the whole target. Keeps the buffers.</summary>
    public void Clear()
    {
        _polylineCount = 0;
        _segmentCount = 0;
        _clip = ClipRect.All;
    }

    /// <summary>Clips the commands that follow to a rectangle of whole pixels; only its part inside the target is used.</summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width, at least 0; 0 draws nothing.</param>
    /// <param name="height">The height, at least 0; 0 draws nothing.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is below 0.</exception>
    public void SetClip(int x, int y, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        _clip = new ClipRect(x, y, (long)x + width, (long)y + height);
    }

    /// <summary>Clips the commands that follow to the whole target.</summary>
    public void ResetClip() => _clip = ClipRect.All;

    /// <summary>
    /// Records a polyline through <paramref name="points"/>, in physical pixels. A point with a coordinate that is not finite
    /// (NaN or ±∞) breaks the line there, and the dash pattern starts again after it. Fewer than two points in a run draw
    /// nothing; two equal points in a row draw a dot.
    /// </summary>
    /// <param name="points">The points.</param>
    /// <param name="style">The stroke.</param>
    /// <exception cref="ArgumentOutOfRangeException">The width or the dash pattern is out of its range (design §5.1).</exception>
    public void DrawPolyline(ReadOnlySpan<Vector2> points, in LineStyle style)
    {
        ValidateStyle(style);
        if (style.Width == 0)
        {
            return;
        }

        var first = _segmentCount;
        var min = new Vector2(float.PositiveInfinity);
        var max = new Vector2(float.NegativeInfinity);
        var along = 0f;
        var previous = default(Vector2);
        var hasPrevious = false;
        foreach (var point in points)
        {
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
            {
                hasPrevious = false;
                continue;
            }

            if (hasPrevious)
            {
                if (_segmentCount == _segments.Length)
                {
                    Array.Resize(ref _segments, _segments.Length * 2);
                }

                _segments[_segmentCount++] = new SegmentRecord(previous, point, along);
                along += Vector2.Distance(previous, point);
            }
            else
            {
                // A run starts here: the dash pattern starts again (design §5.1).
                along = 0;
            }

            // The box of every finite point (design §5.3); a lone point between breaks only widens it.
            min = Vector2.Min(min, point);
            max = Vector2.Max(max, point);
            previous = point;
            hasPrevious = true;
        }

        if (_segmentCount == first)
        {
            return;
        }

        if (_polylineCount == _polylines.Length)
        {
            Array.Resize(ref _polylines, _polylines.Length * 2);
        }

        _polylines[_polylineCount++] = new PolylineRecord(style, first, _segmentCount - first, _clip, min.X, min.Y, max.X, max.Y);
    }

    /// <summary>Throws unless a stroke is in range (design §5.1): what <see cref="DrawPolyline"/> and the spec's line layers check.</summary>
    internal static void ValidateStyle(in LineStyle style)
    {
        if (!float.IsFinite(style.Width) || style.Width < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(style), style.Width, "The width is finite and at least 0.");
        }

        if (style.Dash is { } dash)
        {
            var valid =
                float.IsFinite(dash.On)
                && float.IsFinite(dash.Off)
                && float.IsFinite(dash.Offset)
                && dash.On >= 0
                && dash.Off >= 0
                && dash.On + dash.Off > 0;
            if (!valid)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(style),
                    dash,
                    "A dash pattern has a finite On and Off of at least 0 with a positive sum, and a finite Offset."
                );
            }
        }
    }
}

/// <summary>A rectangle of whole pixels, edges included on the left and top and excluded on the right and bottom.</summary>
internal readonly record struct ClipRect(long Left, long Top, long Right, long Bottom)
{
    internal static ClipRect All => new(long.MinValue / 4, long.MinValue / 4, long.MaxValue / 4, long.MaxValue / 4);
}

/// <summary>One recorded polyline: its stroke, its run of segments, its clip, and the bounding box of its finite points.</summary>
internal readonly record struct PolylineRecord(
    LineStyle Style,
    int SegmentStart,
    int SegmentCount,
    ClipRect Clip,
    float MinX,
    float MinY,
    float MaxX,
    float MaxY
);

/// <summary>
/// One segment as the GPU reads it: its ends and the length along the polyline from the start of its run to its first end
/// (design §5.1). 20 bytes, laid out as the HLSL structure of the line shaders.
/// </summary>
internal readonly record struct SegmentRecord(Vector2 Start, Vector2 End, float Along);
