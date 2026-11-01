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
    private PieceRecord[] _pieces = new PieceRecord[16];
    private TextRecord[] _texts = new TextRecord[4];
    private RenderCommand[] _commands = new RenderCommand[8];
    private RectInstance[] _rects = new RectInstance[4];
    private ClipRect[] _rectClips = new ClipRect[4];
    private ColumnInstance[] _columns = new ColumnInstance[16];
    private ClipRect[] _columnClips = new ClipRect[16];
    private SectorInstance[] _sectors = new SectorInstance[16];
    private SectorCall[] _sectorCalls = new SectorCall[4];
    private ImageInstance[] _images = new ImageInstance[4];
    private (int Page, ClipRect Clip)[] _imageCalls = new (int, ClipRect)[4];
    private int _polylineCount;
    private int _segmentCount;
    private int _pieceCount;
    private int _textCount;
    private int _rectCount;
    private int _columnCount;
    private int _sectorCount;
    private int _sectorCallCount;
    private int _imageCount;
    private int _commandCount;
    private ClipRect _clip = ClipRect.All;

    /// <summary>Gets the recorded polylines, in order.</summary>
    internal ReadOnlySpan<PolylineRecord> Polylines => _polylines.AsSpan(0, _polylineCount);

    /// <summary>Gets the segments of every polyline; each polyline names its range.</summary>
    internal ReadOnlySpan<SegmentRecord> Segments => _segments.AsSpan(0, _segmentCount);

    /// <summary>Gets the joins and caps of every polyline that is not round; each polyline names its range (design §5.1, row R4).</summary>
    internal ReadOnlySpan<PieceRecord> Pieces => _pieces.AsSpan(0, _pieceCount);

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

    /// <summary>Gets the pieces of every recorded sector call, as the GPU reads them; each call names its range (row G11).</summary>
    internal ReadOnlySpan<SectorInstance> Sectors => _sectors.AsSpan(0, _sectorCount);

    /// <summary>Gets the recorded sector calls, in order: each is one shape (design §5.1, row G11).</summary>
    internal ReadOnlySpan<SectorCall> SectorCalls => _sectorCalls.AsSpan(0, _sectorCallCount);

    /// <summary>Gets the recorded image tiles, in order, as the GPU reads them (design §5.1, row G9).</summary>
    internal ReadOnlySpan<ImageInstance> Images => _images.AsSpan(0, _imageCount);

    /// <summary>Gets the page of the residency cache each recorded image tile reads, and its clip.</summary>
    internal ReadOnlySpan<(int Page, ClipRect Clip)> ImageCalls => _imageCalls.AsSpan(0, _imageCount);

    /// <summary>Gets every recorded command in order: each names a polyline, a text, a rectangle, a column trapezoid, a sector call, or an image tile.</summary>
    internal ReadOnlySpan<RenderCommand> Commands => _commands.AsSpan(0, _commandCount);

    /// <summary>Removes every command and resets the clip to the whole target. Keeps the buffers.</summary>
    public void Clear()
    {
        _polylineCount = 0;
        _segmentCount = 0;
        _pieceCount = 0;
        _textCount = 0;
        _rectCount = 0;
        _columnCount = 0;
        _sectorCount = 0;
        _sectorCallCount = 0;
        _imageCount = 0;
        _commandCount = 0;
        _clip = ClipRect.All;
    }

    /// <summary>
    /// Records a tile of an image (design §5.1, row G9): its record and the residency cache's page it reads. Internal in row G9:
    /// a user's geom draws no image yet.
    /// </summary>
    internal void DrawImage(in ImageInstance image, int page)
    {
        if (_imageCount == _images.Length)
        {
            Array.Resize(ref _images, _images.Length * 2);
            Array.Resize(ref _imageCalls, _imageCalls.Length * 2);
        }

        AddCommand(new RenderCommand(RenderCommandKind.Image, _imageCount));
        _images[_imageCount] = image;
        _imageCalls[_imageCount++] = (page, _clip);
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
    /// nothing; a run of equal points draws a dot: a circle with a round cap, a square along the x axis with a square cap,
    /// nothing with a butt cap. Joins and caps that are not round, and patterns of more than one dash and gap, add pieces
    /// that the backend draws as a union of 32 samples a pixel (design §5.1, §5.3, row R4).
    /// </summary>
    /// <param name="points">The points.</param>
    /// <param name="style">The stroke.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The width, the dash pattern, the join, the cap, or a miter join's limit is out of its range (design §5.1).
    /// </exception>
    public void DrawPolyline(ReadOnlySpan<Vector2> points, in LineStyle style)
    {
        ValidateStyle(style);
        if (style.Width == 0)
        {
            return;
        }

        var first = _segmentCount;
        var firstPiece = _pieceCount;
        var (min, max) = UsesPieces(style) ? RecordPieces(points, new Stroke(style)) : RecordSegments(points);

        if (_segmentCount == first)
        {
            return;
        }

        if (_polylineCount == _polylines.Length)
        {
            Array.Resize(ref _polylines, _polylines.Length * 2);
        }

        AddCommand(new RenderCommand(RenderCommandKind.Polyline, _polylineCount));
        _polylines[_polylineCount++] = new PolylineRecord(
            style,
            first,
            _segmentCount - first,
            _clip,
            min.X,
            min.Y,
            max.X,
            max.Y,
            firstPiece,
            _pieceCount - firstPiece
        );
    }

    /// <summary>Records the segments of a round polyline (row R2) and returns the box of its finite points.</summary>
    private (Vector2 Min, Vector2 Max) RecordSegments(ReadOnlySpan<Vector2> points)
    {
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

        return (min, max);
    }

    /// <summary>Records the segments of a polyline that is not round, with its joins and caps (design §5.1, row R4).</summary>
    private (Vector2 Min, Vector2 Max) RecordPieces(ReadOnlySpan<Vector2> points, in Stroke stroke)
    {
        var min = new Vector2(float.PositiveInfinity);
        var max = new Vector2(float.NegativeInfinity);
        var along = 0f;
        var previous = default(Vector2);
        var hasPrevious = false;
        var run = default(RunState);
        foreach (var point in points)
        {
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
            {
                if (hasPrevious)
                {
                    EndRun(ref run, previous, along, stroke);
                }

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
                var length = AddSegmentPieces(ref run, previous, point, along, stroke);
                along += length;
            }
            else
            {
                along = 0;
                run = default;
            }

            min = Vector2.Min(min, point);
            max = Vector2.Max(max, point);
            previous = point;
            hasPrevious = true;
        }

        if (hasPrevious)
        {
            EndRun(ref run, previous, along, stroke);
        }

        return (min, max);
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
    /// With <see cref="ColumnAxis.Horizontal"/>, x and y swap: Left and Right are rows, and the edges are x values (row G10).
    /// </summary>
    /// <param name="columns">The trapezoids; they are meant not to overlap (an area or a band gives one a pixel column).</param>
    /// <param name="color">The color.</param>
    /// <param name="axis">Which way the trapezoids' sides run: <see cref="ColumnAxis.Vertical"/> by default.</param>
    public void DrawColumns(ReadOnlySpan<ColumnTrapezoid> columns, Color color, ColumnAxis axis = ColumnAxis.Vertical)
    {
        var horizontal = axis switch
        {
            ColumnAxis.Vertical => 0f,
            ColumnAxis.Horizontal => 1f,
            _ => throw new ArgumentOutOfRangeException(nameof(axis), axis, "The axis is a named ColumnAxis value."),
        };
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
                horizontal,
                0,
                premultiplied
            );
            _columnClips[_columnCount++] = _clip;
        }
    }

    /// <summary>
    /// Records polar trapezoids around a center, filled with one color as one shape, in physical pixels (design §5.1, row
    /// G11): each covers the angles from From to To and, at each angle, the radii between its inner and outer edges, the
    /// values at From and To joined linearly in the angle. Angles are radians from 12 o'clock, clockwise on the target (the
    /// point at angle θ and radius r is center + r · (sin θ, −cos θ)). A pixel is covered by the share of its 32 samples
    /// that lie in any of the trapezoids, so trapezoids that share an edge leave no seam, and one that overlaps another is
    /// painted once. A trapezoid with a value that is not finite, with From at or past To, or wider than a full turn draws
    /// nothing; so does a center that is not finite. Each call is its own shape: two calls that overlap are both painted.
    /// </summary>
    /// <param name="sectors">The trapezoids.</param>
    /// <param name="center">The center, in physical pixels.</param>
    /// <param name="color">The color.</param>
    public void DrawSectors(ReadOnlySpan<SectorTrapezoid> sectors, Vector2 center, Color color)
    {
        if (!float.IsFinite(center.X) || !float.IsFinite(center.Y))
        {
            return;
        }

        var first = _sectorCount;
        var min = new Vector2(float.PositiveInfinity);
        var max = new Vector2(float.NegativeInfinity);
        Span<Vector2> corners = stackalloc Vector2[4];
        foreach (var sector in sectors)
        {
            var finite =
                float.IsFinite(sector.From)
                && float.IsFinite(sector.To)
                && float.IsFinite(sector.OuterFrom)
                && float.IsFinite(sector.OuterTo)
                && float.IsFinite(sector.InnerFrom)
                && float.IsFinite(sector.InnerTo);
            var span = sector.To - sector.From;
            if (!finite || !(span > 0) || span > 2 * MathF.PI)
            {
                continue;
            }

            // Pieces of at most π / 16, so that each is drawn as a quad close to it (design §5.3); the union of the samples
            // makes the cuts invisible.
            var count = Math.Max(1, (int)MathF.Ceiling(span / MaxSectorAngle));
            for (var j = 0; j < count; j++)
            {
                var (s, e) = ((float)j / count, (float)(j + 1) / count);
                var (from, to) = (j == 0 ? sector.From : sector.From + (span * s), j == count - 1 ? sector.To : sector.From + (span * e));
                var piece = new SectorInstance(
                    from,
                    to,
                    Lerp(sector.OuterFrom, sector.OuterTo, s),
                    j == count - 1 ? sector.OuterTo : Lerp(sector.OuterFrom, sector.OuterTo, e),
                    Lerp(sector.InnerFrom, sector.InnerTo, s),
                    j == count - 1 ? sector.InnerTo : Lerp(sector.InnerFrom, sector.InnerTo, e),
                    Direction(from),
                    Direction(to),
                    0,
                    0
                );
                if (_sectorCount == _sectors.Length)
                {
                    Array.Resize(ref _sectors, _sectors.Length * 2);
                }

                _sectors[_sectorCount++] = piece;
                SectorQuad(piece, center, SectorMargin + 1, corners);
                foreach (var corner in corners)
                {
                    (min, max) = (Vector2.Min(min, corner), Vector2.Max(max, corner));
                }
            }
        }

        if (_sectorCount == first)
        {
            return;
        }

        if (_sectorCallCount == _sectorCalls.Length)
        {
            Array.Resize(ref _sectorCalls, _sectorCalls.Length * 2);
        }

        AddCommand(new RenderCommand(RenderCommandKind.Sectors, _sectorCallCount));
        _sectorCalls[_sectorCallCount++] = new SectorCall(
            first,
            _sectorCount - first,
            center,
            new Vector4(
                Premultiplied.Multiply(color.R, color.A) / 255f,
                Premultiplied.Multiply(color.G, color.A) / 255f,
                Premultiplied.Multiply(color.B, color.A) / 255f,
                color.A / 255f
            ),
            _clip,
            min,
            max
        );

        static float Lerp(float a, float b, float t) => a + ((b - a) * t);
    }

    /// <summary>
    /// Returns the direction of an angle, (sin θ, −cos θ), in double rounded to float: the GPU's sine and cosine err by more
    /// than a sample's distance from a side (WARP put a sample 0.006 pixels outside one inside it).
    /// </summary>
    internal static Vector2 Direction(float angle) => new((float)Math.Sin(angle), (float)-Math.Cos(angle));

    /// <summary>The widest piece a polar trapezoid is cut into: π / 16 (design §5.3).</summary>
    internal const float MaxSectorAngle = MathF.PI / 16;

    /// <summary>How far a piece's quad reaches past it, in pixels: past the √2 / 2 a sample lies from its pixel's center.</summary>
    internal const float SectorMargin = 1;

    /// <summary>
    /// Writes the quad Sectors.hlsl draws for a piece, in the order of its triangle strip (design §5.3): the piece's wedge
    /// between its sides' rays, cut by the line that touches its outer radius and the chord of its inner one, every side
    /// pushed out by <paramref name="margin"/>. A piece of at most π / 16 lies inside it.
    /// </summary>
    internal static void SectorQuad(in SectorInstance piece, Vector2 center, float margin, Span<Vector2> corners)
    {
        var low = MathF.Max(0, MathF.Min(MathF.Min(piece.OuterFrom, piece.OuterTo), MathF.Min(piece.InnerFrom, piece.InnerTo)));
        var high = MathF.Max(0, MathF.Max(MathF.Max(piece.OuterFrom, piece.OuterTo), MathF.Max(piece.InnerFrom, piece.InnerTo)));

        // The middle direction halves the piece; the sides' normals are the directions turned a quarter back.
        var axis = Vector2.Normalize(piece.Start + piece.End);
        var from = new Vector2(-piece.Start.Y, piece.Start.X);
        var to = new Vector2(-piece.End.Y, piece.End.X);
        var (near, far) = ((low * Vector2.Dot(axis, piece.Start)) - margin, high + margin);
        corners[0] = center + Meet(from, -margin, axis, near);
        corners[1] = center + Meet(from, -margin, axis, far);
        corners[2] = center + Meet(to, margin, axis, near);
        corners[3] = center + Meet(to, margin, axis, far);

        // The point p with dot(p, a) = s and dot(p, b) = q.
        static Vector2 Meet(Vector2 a, float s, Vector2 b, float q)
        {
            var determinant = (a.X * b.Y) - (a.Y * b.X);
            return new Vector2(((s * b.Y) - (a.Y * q)) / determinant, ((a.X * q) - (b.X * s)) / determinant);
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

    /// <summary>
    /// Returns whether a stroke is drawn as pieces and samples rather than as row R2's capsules (design §5.3, row R4): a join or
    /// a cap that is not round, or more than one dash and gap.
    /// </summary>
    internal static bool UsesPieces(in LineStyle style) => style.Join != LineJoin.Round || style.Cap != LineCap.Round || style.Dash is { Count: > 2 };

    /// <summary>Throws unless a stroke is in range (design §5.1): what <see cref="DrawPolyline"/> and the spec's line layers check.</summary>
    internal static void ValidateStyle(in LineStyle style)
    {
        if (!float.IsFinite(style.Width) || style.Width < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(style), style.Width, "The width is finite and at least 0.");
        }

        if (style.Dash is { } dash)
        {
            // default(LineDash) has no interval and a period of 0; a pattern made otherwise has 2 to 8.
            var valid = float.IsFinite(dash.Offset) && dash.Period > 0;
            for (var i = 0; valid && i < dash.Count; i++)
            {
                valid = float.IsFinite(dash[i]) && dash[i] >= 0;
            }

            // A sum of finite lengths can still overflow to infinity.
            if (!valid || !float.IsFinite(dash.Period))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(style),
                    dash,
                    "A dash pattern has 2 or more intervals, each finite and at least 0, with a finite positive sum, and a finite Offset."
                );
            }
        }

        if ((uint)style.Join > (uint)LineJoin.Bevel)
        {
            throw new ArgumentOutOfRangeException(nameof(style), style.Join, "The join is a named value.");
        }

        if ((uint)style.Cap > (uint)LineCap.Square)
        {
            throw new ArgumentOutOfRangeException(nameof(style), style.Cap, "The cap is a named value.");
        }

        // default(LineStyle) has a MiterLimit of 0, which a round or bevel join never reads.
        if (style.Join == LineJoin.Miter && !(float.IsFinite(style.MiterLimit) && style.MiterLimit >= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(style), style.MiterLimit, "The miter limit of a miter join is finite and at least 1.");
        }
    }

    /// <summary>
    /// Adds the pieces a segment brings to a run that is not round (design §5.1): the start cap before the run's first segment
    /// with a length, and the join with the segment before it otherwise. A segment of length 0 brings nothing.
    /// </summary>
    private float AddSegmentPieces(ref RunState run, Vector2 start, Vector2 end, float along, in Stroke style)
    {
        run.HasSegment = true;
        var delta = end - start;

        // The length RecordSegments adds, so that both kinds of line measure the dash pattern alike.
        var length = Vector2.Distance(start, end);
        if (length == 0)
        {
            return 0;
        }

        var direction = delta / length;
        if (!run.HasDirection)
        {
            Cap(start, -direction, along, startOfRun: true, style);
            run.HasDirection = true;
        }
        else
        {
            Join(start, run.Direction, direction, along, style);
        }

        run.Direction = direction;
        return length;
    }

    /// <summary>Adds the end cap of a run, or the dot of a run of segments of length 0 only (design §5.1).</summary>
    private void EndRun(ref RunState run, Vector2 point, float along, in Stroke style)
    {
        if (run.HasDirection)
        {
            Cap(point, run.Direction, along, startOfRun: false, style);
        }
        else if (run.HasSegment && DashAt(style, along) is (true, _, _) or (_, true, _) or (_, _, true))
        {
            // The direction of a path of length 0 is the positive x axis (SVG 2 §9.4): its square is axis-aligned.
            Dot(point, Vector2.UnitX, style);
        }

        run = default;
    }

    /// <summary>Adds the piece of a join, or the caps where a dash ends or starts exactly at the point (design §5.1).</summary>
    private void Join(Vector2 point, Vector2 incoming, Vector2 outgoing, float along, in Stroke style)
    {
        var (left, right, zero) = DashAt(style, along);
        if (left && right)
        {
            var dot = Vector2.Dot(incoming, outgoing);
            var cross = (incoming.X * outgoing.Y) - (incoming.Y * outgoing.X);

            // Nearly straight on: the piece would have no area, and the union of samples leaves no seam between the boxes (§5.3).
            if (MathF.Abs(cross) < 1e-4f && dot > 0)
            {
                return;
            }

            var kind = style.Join switch
            {
                LineJoin.Round => PieceKind.Disk,
                LineJoin.Bevel => PieceKind.Bevel,

                // 1 / cos(θ / 2) past the limit is a bevel, at it still a miter; a reversal, cos 0, always is a bevel.
                _ => MathF.Sqrt(MathF.Max(0, (1 + dot) / 2)) * style.MiterLimit >= 1 ? PieceKind.Miter : PieceKind.Bevel,
            };
            AddPiece(point, incoming, outgoing, kind);
            return;
        }

        if (left)
        {
            Cap(point, incoming, along, startOfRun: false, style);
        }
        else if (right)
        {
            Cap(point, -outgoing, along, startOfRun: true, style);
        }
        else if (zero)
        {
            Dot(point, outgoing, style);
        }
    }

    /// <summary>
    /// Adds a cap at a run's end or a dash's end at a joint (design §5.1): <paramref name="outward"/> points away from the line.
    /// At the start of a run the line must be on just after the point; at the end, just before it.
    /// </summary>
    private void Cap(Vector2 point, Vector2 outward, float along, bool startOfRun, in Stroke style)
    {
        var (left, right, zero) = DashAt(style, along);
        if (startOfRun ? right : left)
        {
            AddCap(point, outward, style.Cap);
        }
        else if (zero && !(startOfRun ? left : right))
        {
            Dot(point, -outward, style);
        }
    }

    private void Dot(Vector2 point, Vector2 direction, in Stroke style)
    {
        // Both caps of a dash of length 0 at one point (SVG 2 §13.5.7): a circle, or a square along the direction.
        if (style.Cap == LineCap.Round)
        {
            AddPiece(point, direction, direction, PieceKind.Disk);
        }
        else if (style.Cap == LineCap.Square)
        {
            AddPiece(point, direction, direction, PieceKind.Square);
        }
    }

    private void AddCap(Vector2 point, Vector2 outward, LineCap cap)
    {
        if (cap == LineCap.Round)
        {
            AddPiece(point, outward, outward, PieceKind.Disk);
        }
        else if (cap == LineCap.Square)
        {
            AddPiece(point, outward, outward, PieceKind.SquareCap);
        }
    }

    private void AddPiece(Vector2 point, Vector2 incoming, Vector2 outgoing, PieceKind kind)
    {
        if (_pieceCount == _pieces.Length)
        {
            Array.Resize(ref _pieces, _pieces.Length * 2);
        }

        // Made in its slot: a record built on the stack and copied in 16-byte halves that overlap stalled on the store
        // forwarding, half of a mitered line's recording (docs/benchmarks/r4-lines.md).
        _pieces[_pieceCount++] = new PieceRecord(point, incoming, outgoing, kind);
    }

    /// <summary>
    /// Returns whether a dash pattern is on just before and just after a length along the run, and whether a dash of length 0
    /// sits exactly there (design §5.1). A solid line is on everywhere.
    /// </summary>
    internal static (bool Left, bool Right, bool Zero) DashAt(in Stroke style, float along)
    {
        if (style.Count == 0)
        {
            return (true, true, false);
        }

        var period = style.Ends[style.Count - 1];

        // With a floor as the shader takes it (LineMask.hlsl), not % (fmodf, a quarter of a dashed line's recording).
        var position = along + style.Offset;
        var phase = position - (MathF.Floor(position / period) * period);
        phase =
            phase < 0 ? phase + period
            : phase >= period ? phase - period
            : phase;
        var (left, right, zero) = (false, false, false);
        var start = 0f;
        for (var i = 0; i < style.Count; i += 2)
        {
            var end = style.Ends[i];
            if (end == start)
            {
                zero |= start == phase || (phase == 0 && start == period);
            }
            else
            {
                // An interval [start, end] that ends at the period also reaches phase 0 from the left.
                left |= (start < phase && phase <= end) || (phase == 0 && end == period);
                right |= start <= phase && phase < end;
            }

            start = style.Ends[i + 1];
        }

        return (left, right, zero);
    }
}

/// <summary>
/// What recording a polyline that is not round reads of its style, read once (design §5.1): the join, the cap, the miter
/// limit, and the dash pattern as the ends of its intervals summed from its start (no pattern: a count of 0).
/// </summary>
internal readonly struct Stroke
{
    public readonly LineJoin Join;
    public readonly LineCap Cap;
    public readonly float MiterLimit;
    public readonly float Offset;
    public readonly int Count;
    public readonly Ends Ends;

    public Stroke(in LineStyle style)
    {
        (Join, Cap, MiterLimit) = (style.Join, style.Cap, style.MiterLimit);
        if (style.Dash is { } dash)
        {
            (Offset, Count) = (dash.Offset, dash.Count);
            var sum = 0f;
            for (var i = 0; i < dash.Count; i++)
            {
                sum += dash[i];
                Ends[i] = sum;
            }
        }
    }
}

/// <summary>The ends of up to 8 dash intervals.</summary>
[System.Runtime.CompilerServices.InlineArray(LineDash.MaxIntervals)]
internal struct Ends
{
    private float _element;
}

/// <summary>The state of a run while a polyline that is not round is recorded: its last direction and whether it has segments.</summary>
internal struct RunState
{
    public Vector2 Direction;
    public bool HasDirection;
    public bool HasSegment;
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

    /// <summary>One shape of polar trapezoids: the index is into <see cref="RenderList.SectorCalls"/> (row G11).</summary>
    Sectors,

    /// <summary>A tile of an image: the index is into <see cref="RenderList.Images"/> (row G9).</summary>
    Image,
}

/// <summary>
/// One tile of an image as the GPU reads it (design §5.1, §5.3, row G9): the target point of the tile's texel corner (0, 0)
/// and the target vectors of one texel along its columns and rows, which flip and a turned-over extent set; the range of
/// texels drawn; where they are read in the slot's face (an ancestor's part when the tile is not there yet); and how the
/// texel is painted. 96 bytes, laid out as the HLSL structure of Image.hlsl.
/// </summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
internal struct ImageInstance
{
    /// <summary>The target point of the tile's texel corner (0, 0), in physical pixels.</summary>
    public Vector2 Origin;

    /// <summary>The target vector of one texel along the tile's columns.</summary>
    public Vector2 AlongU;

    /// <summary>The target vector of one texel along the tile's rows.</summary>
    public Vector2 AlongV;

    /// <summary>The texels drawn: u0, v0, u1, v1, in the tile's texels, the ends past the image's edge cut.</summary>
    public Vector4 Range;

    /// <summary>The face texel (border left out) that tile texel (0, 0) reads.</summary>
    public Vector2 SampleOrigin;

    /// <summary>The face texels a tile texel spans: 1, or 1 / 2^k for an ancestor k levels up.</summary>
    public float SampleScale;

    /// <summary>The slot's slice in its page.</summary>
    public uint Slice;

    /// <summary>Bit 0 bilinear; bit 1 RGBA; bits 2 and 3 the transform (linear, log, symlog, pow); bit 4 diverging; bit 5 reversed.</summary>
    public uint Flags;

    /// <summary>The row of the color table.</summary>
    public uint Table;

    /// <summary>The symlog constant or the pow exponent.</summary>
    public float Constant;

    /// <summary>The low end of the color domain in the scale space, less the tile's origin.</summary>
    public float Low;

    /// <summary>The high end of the color domain.</summary>
    public float High;

    /// <summary>The diverging middle.</summary>
    public float Middle;

    /// <summary>The alpha the image is drawn with.</summary>
    public float Opacity;

    /// <summary>Pads the record to 96 bytes.</summary>
    public Vector3 Padding;
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

/// <summary>Which way a column trapezoid's sides run (design §5.1, row G10).</summary>
public enum ColumnAxis
{
    /// <summary>Left and Right are x values; the top and bottom edges are y values.</summary>
    Vertical,

    /// <summary>
    /// x and y swapped: Left and Right are y values (top to bottom), TopLeft and BottomLeft are the x ends of the row at
    /// Left, and TopRight and BottomRight those of the row at Right. A flipped area or band draws these.
    /// </summary>
    Horizontal,
}

/// <summary>
/// One column trapezoid as the GPU reads it (design §5.3): its sides, its edges' values at them, 1 when x and y are swapped
/// (<see cref="ColumnAxis.Horizontal"/>) or 0, an unused float, and the premultiplied color. 48 bytes, laid out as the
/// HLSL structure of Columns.hlsl.
/// </summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
internal readonly record struct ColumnInstance(
    float Left,
    float Right,
    float TopLeft,
    float TopRight,
    float BottomLeft,
    float BottomRight,
    float Horizontal,
    float Unused,
    Vector4 Color
);

/// <summary>
/// One trapezoid of a polar coordinate system (design §5.1, row G11): the angles it covers, in radians from 12 o'clock and
/// clockwise on the target, and its outer and inner radii at those angles, in physical pixels. Between From and To each
/// edge's radius runs linearly in the angle; which edge is inner does not matter.
/// </summary>
/// <param name="From">The first angle.</param>
/// <param name="To">The last angle, past <paramref name="From"/> by at most a full turn.</param>
/// <param name="OuterFrom">The outer radius at <paramref name="From"/>.</param>
/// <param name="OuterTo">The outer radius at <paramref name="To"/>.</param>
/// <param name="InnerFrom">The inner radius at <paramref name="From"/>.</param>
/// <param name="InnerTo">The inner radius at <paramref name="To"/>.</param>
public readonly record struct SectorTrapezoid(float From, float To, float OuterFrom, float OuterTo, float InnerFrom, float InnerTo);

/// <summary>
/// One piece of a polar trapezoid as the GPU reads it (design §5.3): a trapezoid of at most π / 16, the directions of its
/// sides, (sin θ, −cos θ) made on the CPU in double, and two unused floats. 48 bytes, laid out as the HLSL structure of
/// Sectors.hlsl.
/// </summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
internal readonly record struct SectorInstance(
    float From,
    float To,
    float OuterFrom,
    float OuterTo,
    float InnerFrom,
    float InnerTo,
    Vector2 Start,
    Vector2 End,
    float Unused0,
    float Unused1
);

/// <summary>
/// One recorded call of <see cref="RenderList.DrawSectors"/>: its range of pieces, its center, its premultiplied color, its
/// clip, and the box of its pieces' quads, which the color pass covers (design §5.3).
/// </summary>
internal readonly record struct SectorCall(int Start, int Count, Vector2 Center, Vector4 Color, ClipRect Clip, Vector2 Min, Vector2 Max);

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
    float MaxY,
    int PieceStart = 0,
    int PieceCount = 0
);

/// <summary>What a piece of a polyline that is not round is (design §5.1, §5.3, row R4).</summary>
internal enum PieceKind : uint
{
    /// <summary>A join cut straight across the two outer corners.</summary>
    Bevel,

    /// <summary>A join carried to where the outer edges meet.</summary>
    Miter,

    /// <summary>A circle the width of the line: a round join, a round cap, or a round dot.</summary>
    Disk,

    /// <summary>A half square past the point along <see cref="PieceRecord.In"/>: a square cap.</summary>
    SquareCap,

    /// <summary>A square the width of the line centered on the point, along <see cref="PieceRecord.In"/>: a square dot.</summary>
    Square,
}

/// <summary>
/// One join or cap of a polyline that is not round, as the GPU reads it (design §5.3): the point, the incoming and outgoing
/// directions of a join (for a cap, both point away from the line), and the kind. 32 bytes with 4 unused, laid out as the
/// HLSL structure: two aligned 16-byte halves, which the copies of a record of 28 bytes were not.
/// </summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Size = 32)]
internal readonly record struct PieceRecord(Vector2 Point, Vector2 In, Vector2 Out, PieceKind Kind);

/// <summary>
/// One segment as the GPU reads it: its ends and the length along the polyline from the start of its run to its first end
/// (design §5.1). 20 bytes, laid out as the HLSL structure of the line shaders.
/// </summary>
internal readonly record struct SegmentRecord(Vector2 Start, Vector2 End, float Along);
