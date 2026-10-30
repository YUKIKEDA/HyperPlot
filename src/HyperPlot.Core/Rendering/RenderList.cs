using System.Numerics;

namespace HyperPlot.Rendering;

/// <summary>
/// The commands of one frame, in buffers kept from frame to frame (design §5.1): polylines, texts, filled rectangles, and clips, in physical
/// pixels of the render target, with the origin at the top left and pixel centers at +0.5. A backend draws the commands in
/// the order they were recorded, so a later command lies over an earlier one.
/// </summary>
/// <remarks>One thread at a time uses a list. After the buffers have grown to a frame's size, recording allocates nothing.</remarks>
public sealed class RenderList
{
    private PolylineRecord[] _polylines = new PolylineRecord[4];
    private SegmentRecord[] _segments = new SegmentRecord[256];
    private TextRecord[] _texts = new TextRecord[4];
    private RenderCommand[] _commands = new RenderCommand[8];
    private RectInstance[] _rects = new RectInstance[4];
    private ClipRect[] _rectClips = new ClipRect[4];
    private ColumnInstance[] _columns = new ColumnInstance[16];
    private ClipRect[] _columnClips = new ClipRect[16];
    private int _polylineCount;
    private int _segmentCount;
    private int _textCount;
    private int _rectCount;
    private int _columnCount;
    private int _commandCount;
    private ClipRect _clip = ClipRect.All;

    /// <summary>Gets the recorded polylines, in order.</summary>
    internal ReadOnlySpan<PolylineRecord> Polylines => _polylines.AsSpan(0, _polylineCount);

    /// <summary>Gets the segments of every polyline; each polyline names its range.</summary>
    internal ReadOnlySpan<SegmentRecord> Segments => _segments.AsSpan(0, _segmentCount);

    /// <summary>Gets the recorded texts, in order.</summary>
    internal ReadOnlySpan<TextRecord> Texts => _texts.AsSpan(0, _textCount);

    /// <summary>Gets the recorded rectangles, in order, as the GPU reads them.</summary>
    internal ReadOnlySpan<RectInstance> Rects => _rects.AsSpan(0, _rectCount);

    /// <summary>Gets the clip of each recorded rectangle.</summary>
    internal ReadOnlySpan<ClipRect> RectClips => _rectClips.AsSpan(0, _rectCount);

    /// <summary>Gets the recorded column trapezoids, in order, as the GPU reads them.</summary>
    internal ReadOnlySpan<ColumnInstance> Columns => _columns.AsSpan(0, _columnCount);

    /// <summary>Gets the clip of each recorded column trapezoid.</summary>
    internal ReadOnlySpan<ClipRect> ColumnClips => _columnClips.AsSpan(0, _columnCount);

    /// <summary>Gets every recorded command in order: each names a polyline, a text, a rectangle, or a column trapezoid.</summary>
    internal ReadOnlySpan<RenderCommand> Commands => _commands.AsSpan(0, _commandCount);

    /// <summary>Removes every command and resets the clip to the whole target. Keeps the buffers.</summary>
    public void Clear()
    {
        _polylineCount = 0;
        _segmentCount = 0;
        _textCount = 0;
        _rectCount = 0;
        _columnCount = 0;
        _commandCount = 0;
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

        AddCommand(new RenderCommand(RenderCommandKind.Polyline, _polylineCount));
        _polylines[_polylineCount++] = new PolylineRecord(style, first, _segmentCount - first, _clip, min.X, min.Y, max.X, max.Y);
    }

    /// <summary>
    /// Records a text at <paramref name="position"/>, in physical pixels (design §5.1, §5.6). The list keeps the reference
    /// and shapes nothing: the backend shapes when it draws, through its <see cref="TextEngine"/>. An empty text draws
    /// nothing.
    /// </summary>
    /// <param name="text">The text; a line feed starts a new line.</param>
    /// <param name="position">The point that the style's alignment and rotation place the text's block at.</param>
    /// <param name="style">The color, font, alignment, and rotation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/>, the font family, or the locale is null.</exception>
    /// <exception cref="ArgumentException">The font family or the locale is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The position is not finite, the size is not finite or outside (0, 512], or the weight, an alignment, or the rotation
    /// is not a named value.
    /// </exception>
    public void DrawText(string text, Vector2 position, in TextStyle style)
    {
        ValidateText(text, style);
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y))
        {
            throw new ArgumentOutOfRangeException(nameof(position), position, "The position is finite.");
        }

        if (text.Length == 0)
        {
            return;
        }

        if (_textCount == _texts.Length)
        {
            Array.Resize(ref _texts, _texts.Length * 2);
        }

        AddCommand(new RenderCommand(RenderCommandKind.Text, _textCount));
        _texts[_textCount++] = new TextRecord(text, position, style, _clip);
    }

    /// <summary>
    /// Records a filled rectangle, in physical pixels (design §5.1): each pixel is covered by the area it shares with the
    /// rectangle, so whole-pixel edges cover a pixel fully or not at all and a fractional edge covers it in part. Rectangles
    /// that overlap are each blended over what is below.
    /// </summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width: finite and at least 0; 0 draws nothing.</param>
    /// <param name="height">The height: finite and at least 0; 0 draws nothing.</param>
    /// <param name="color">The color.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="x"/> or <paramref name="y"/> is not finite, or <paramref name="width"/> or <paramref name="height"/> is
    /// not finite or below 0.
    /// </exception>
    public void DrawRect(float x, float y, float width, float height, Color color)
    {
        if (!float.IsFinite(x))
        {
            throw new ArgumentOutOfRangeException(nameof(x), x, "The left edge is finite.");
        }

        if (!float.IsFinite(y))
        {
            throw new ArgumentOutOfRangeException(nameof(y), y, "The top edge is finite.");
        }

        if (!float.IsFinite(width) || width < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "The width is finite and at least 0.");
        }

        if (!float.IsFinite(height) || height < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "The height is finite and at least 0.");
        }

        if (width == 0 || height == 0)
        {
            return;
        }

        if (_rectCount == _rects.Length)
        {
            Array.Resize(ref _rects, _rects.Length * 2);
            Array.Resize(ref _rectClips, _rectClips.Length * 2);
        }

        AddCommand(new RenderCommand(RenderCommandKind.Rect, _rectCount));
        _rects[_rectCount] = new RectInstance(
            x,
            y,
            width,
            height,
            new Vector4(
                Premultiplied.Multiply(color.R, color.A) / 255f,
                Premultiplied.Multiply(color.G, color.A) / 255f,
                Premultiplied.Multiply(color.B, color.A) / 255f,
                color.A / 255f
            )
        );
        _rectClips[_rectCount++] = _clip;
    }

    /// <summary>
    /// Records column trapezoids filled with one color, in physical pixels (design §5.1): each covers x from Left to Right
    /// and y between its top and bottom edges, the straight lines through their values at the left and right sides. A
    /// pixel is covered by the area it shares with the region between the two edges, in either order, so edges that cross
    /// paint a bow tie. A trapezoid with a coordinate that is not finite, or with Left at or past Right, draws nothing.
    /// </summary>
    /// <param name="columns">The trapezoids; they are meant not to overlap (an area or a band gives one a pixel column).</param>
    /// <param name="color">The color.</param>
    public void DrawColumns(ReadOnlySpan<ColumnTrapezoid> columns, Color color)
    {
        var premultiplied = new Vector4(
            Premultiplied.Multiply(color.R, color.A) / 255f,
            Premultiplied.Multiply(color.G, color.A) / 255f,
            Premultiplied.Multiply(color.B, color.A) / 255f,
            color.A / 255f
        );
        foreach (var column in columns)
        {
            var finite =
                float.IsFinite(column.Left)
                && float.IsFinite(column.Right)
                && float.IsFinite(column.TopLeft)
                && float.IsFinite(column.TopRight)
                && float.IsFinite(column.BottomLeft)
                && float.IsFinite(column.BottomRight);
            if (!finite || !(column.Left < column.Right))
            {
                continue;
            }

            if (_columnCount == _columns.Length)
            {
                Array.Resize(ref _columns, _columns.Length * 2);
                Array.Resize(ref _columnClips, _columnClips.Length * 2);
            }

            AddCommand(new RenderCommand(RenderCommandKind.Columns, _columnCount));
            _columns[_columnCount] = new ColumnInstance(
                column.Left,
                column.Right,
                column.TopLeft,
                column.TopRight,
                column.BottomLeft,
                column.BottomRight,
                0,
                0,
                premultiplied
            );
            _columnClips[_columnCount++] = _clip;
        }
    }

    /// <summary>Throws unless a text and its style are in range (design §5.1): what <see cref="DrawText"/> and <see cref="TextEngine.Measure"/> check.</summary>
    internal static void ValidateText(string text, in TextStyle style)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!float.IsFinite(style.Size) || style.Size <= 0 || style.Size > TextEngine.MaxSize)
        {
            throw new ArgumentOutOfRangeException(nameof(style), style.Size, "The size is finite, above 0, and at most 512.");
        }

        if ((uint)style.Weight > (uint)FontWeight.Bold)
        {
            throw new ArgumentOutOfRangeException(nameof(style), style.Weight, "The weight is a named value.");
        }

        if ((uint)style.Horizontal > (uint)TextAlign.Right || (uint)style.Vertical > (uint)TextBaseline.Bottom)
        {
            throw new ArgumentOutOfRangeException(nameof(style), (style.Horizontal, style.Vertical), "The alignments are named values.");
        }

        if ((uint)style.Rotation > (uint)TextRotation.CounterClockwise90)
        {
            throw new ArgumentOutOfRangeException(nameof(style), style.Rotation, "The rotation is a named value.");
        }

        ArgumentException.ThrowIfNullOrEmpty(style.FontFamily, nameof(style));
        ArgumentException.ThrowIfNullOrEmpty(style.Locale, nameof(style));
    }

    private void AddCommand(RenderCommand command)
    {
        if (_commandCount == _commands.Length)
        {
            Array.Resize(ref _commands, _commands.Length * 2);
        }

        _commands[_commandCount++] = command;
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

/// <summary>What a recorded command draws.</summary>
internal enum RenderCommandKind : byte
{
    /// <summary>A polyline: the index is into <see cref="RenderList.Polylines"/>.</summary>
    Polyline,

    /// <summary>A text: the index is into <see cref="RenderList.Texts"/>.</summary>
    Text,

    /// <summary>A filled rectangle: the index is into <see cref="RenderList.Rects"/>.</summary>
    Rect,

    /// <summary>A filled column trapezoid: the index is into <see cref="RenderList.Columns"/>.</summary>
    Columns,
}

/// <summary>One recorded command: its kind and the index of its record.</summary>
internal readonly record struct RenderCommand(RenderCommandKind Kind, int Index);

/// <summary>One recorded text: the reference, the position, the style, and the clip when it was recorded.</summary>
internal readonly record struct TextRecord(string Text, Vector2 Position, TextStyle Style, ClipRect Clip);

/// <summary>
/// One filled rectangle as the GPU reads it (design §5.1, §5.3): its top left, width, and height in physical pixels, and its
/// premultiplied color. 32 bytes, laid out as the HLSL structure of Rect.hlsl.
/// </summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
internal readonly record struct RectInstance(float X, float Y, float Width, float Height, Vector4 Color);

/// <summary>
/// One trapezoid of a pixel column (design §5.1): its left and right sides, and its top and bottom edges by their values at
/// those sides, in physical pixels of the target, y down.
/// </summary>
/// <param name="Left">The left side.</param>
/// <param name="Right">The right side, past <paramref name="Left"/>.</param>
/// <param name="TopLeft">The top edge at the left side.</param>
/// <param name="TopRight">The top edge at the right side.</param>
/// <param name="BottomLeft">The bottom edge at the left side.</param>
/// <param name="BottomRight">The bottom edge at the right side.</param>
public readonly record struct ColumnTrapezoid(float Left, float Right, float TopLeft, float TopRight, float BottomLeft, float BottomRight);

/// <summary>
/// One column trapezoid as the GPU reads it (design §5.3): its sides, its edges' values at them, two unused floats, and the
/// premultiplied color. 48 bytes, laid out as the HLSL structure of Columns.hlsl.
/// </summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
internal readonly record struct ColumnInstance(
    float Left,
    float Right,
    float TopLeft,
    float TopRight,
    float BottomLeft,
    float BottomRight,
    float Unused0,
    float Unused1,
    Vector4 Color
);

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
