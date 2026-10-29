using System.Numerics;
using System.Runtime.InteropServices;

namespace HyperPlot.Rendering;

/// <summary>
/// The text of one backend (design §5.6): the cache of shaped texts and the glyph atlas. A backend makes one from its
/// <see cref="ITextBackend"/> and hands it out as <see cref="IGpuBackend.Text"/>, so that measuring and drawing share what
/// was shaped and rasterized.
/// </summary>
/// <remarks>
/// The thread that draws owns it, with no lock, as it owns the backend. The cache keeps 4,096 texts and lets the one used
/// longest ago go first.
/// </remarks>
public sealed class TextEngine
{
    /// <summary>The largest em size: a glyph's box, about twice the em, still fits one atlas page (design §5.6).</summary>
    internal const float MaxSize = 512;

    /// <summary>The most texts the cache keeps.</summary>
    internal const int CacheCapacity = 4096;

    private readonly Dictionary<ShapeKey, LinkedListNode<CacheEntry>> _cache = [];
    private readonly LinkedList<CacheEntry> _order = new();
    private readonly GlyphAtlas _atlas;
    private GlyphInstance[] _instances = new GlyphInstance[256];
    private DrawStep[] _steps = new DrawStep[16];
    private int _instanceCount;
    private int _stepCount;

    /// <summary>Initializes a new instance of the <see cref="TextEngine"/> class.</summary>
    /// <param name="backend">What shapes and rasterizes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="backend"/> is null.</exception>
    public TextEngine(ITextBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        Backend = backend;
        _atlas = new GlyphAtlas(backend);
    }

    /// <summary>Gets what shapes and rasterizes.</summary>
    public ITextBackend Backend { get; }

    /// <summary>Gets the glyph atlas.</summary>
    internal GlyphAtlas Atlas => _atlas;

    /// <summary>Gets the number of texts in the cache.</summary>
    internal int CacheCount => _cache.Count;

    /// <summary>Gets the steps of the last <see cref="Prepare"/>, in the order of the list's commands.</summary>
    internal ReadOnlySpan<DrawStep> Steps => _steps.AsSpan(0, _stepCount);

    /// <summary>Gets the glyph records of the last <see cref="Prepare"/>; each glyph step names its range.</summary>
    internal ReadOnlySpan<GlyphInstance> Instances => _instances.AsSpan(0, _instanceCount);

    /// <summary>Measures a text as <see cref="RenderList.DrawText"/> would draw it, shaping it only if the cache lacks it.</summary>
    /// <param name="text">The text; a line feed starts a new line.</param>
    /// <param name="style">The style; only the font, the size, the weight, and the locale change the size.</param>
    /// <returns>The size in physical pixels.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/>, the font family, or the locale is null.</exception>
    /// <exception cref="ArgumentException">The font family or the locale is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The size is not finite or outside (0, 512], or the weight, an alignment, or the rotation is not a named value.
    /// </exception>
    public TextMetrics Measure(string text, in TextStyle style)
    {
        RenderList.ValidateText(text, style);
        return Shape(text, style).Metrics;
    }

    /// <summary>Gets a text shaped, from the cache or from the backend, and makes it the newest in the cache.</summary>
    internal ShapedText Shape(string text, in TextStyle style)
    {
        var key = new ShapeKey(text, style.FontFamily, style.Size, style.Weight, style.Locale);
        if (_cache.TryGetValue(key, out var node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            return node.Value.Shaped;
        }

        var shaped = Backend.Shape(text, style.FontFamily, style.Size, style.Weight, style.Locale);
        if (_cache.Count == CacheCapacity)
        {
            // The node of the text used longest ago carries the new one.
            node = _order.Last!;
            _order.RemoveLast();
            _cache.Remove(node.Value.Key);
            node.Value = new CacheEntry(key, shaped);
        }
        else
        {
            node = new LinkedListNode<CacheEntry>(new CacheEntry(key, shaped));
        }

        _order.AddFirst(node);
        _cache.Add(key, node);
        return shaped;
    }

    /// <summary>
    /// Turns a list's commands into steps for a backend's <c>Draw</c> (design §5.6): a polyline is a step of its own, and the
    /// glyphs of the texts that follow one another go into one step until the page or the clip changes. Shapes what the
    /// cache lacks, places in the atlas what it lacks, and starts a new <c>Draw</c> of the atlas.
    /// </summary>
    /// <param name="list">The commands.</param>
    /// <param name="width">The target's width in pixels.</param>
    /// <param name="height">The target's height in pixels.</param>
    /// <exception cref="InvalidOperationException">The glyphs of the list need more than 8 atlas pages, or one is larger than a page.</exception>
    internal void Prepare(RenderList list, int width, int height)
    {
        _stepCount = 0;
        _instanceCount = 0;
        _atlas.BeginDraw();
        var commands = list.Commands;
        var texts = list.Texts;
        for (var i = 0; i < commands.Length; i++)
        {
            var command = commands[i];
            if (command.Kind == RenderCommandKind.Polyline)
            {
                AddStep(new DrawStep(DrawStepKind.Polyline, command.Index, 1, 0, list.Polylines[command.Index].Clip));
            }
            else if (!TryPlace(texts[command.Index], width, height))
            {
                ThrowAtlasFull(list, i);
            }
        }
    }

    /// <summary>The line's origin offset of an alignment, in the block's own axes (u along the text, v down).</summary>
    internal static float AlignX(TextAlign align, float blockWidth, float lineWidth) =>
        align switch
        {
            TextAlign.Center => (blockWidth - lineWidth) / 2,
            TextAlign.Right => blockWidth - lineWidth,
            _ => 0,
        };

    /// <summary>The first baseline's offset below the position for a vertical alignment.</summary>
    internal static float AlignY(TextBaseline baseline, in TextMetrics metrics) =>
        baseline switch
        {
            TextBaseline.Top => metrics.Ascent,
            TextBaseline.Middle => metrics.Ascent - (metrics.Height / 2),
            TextBaseline.Bottom => metrics.Ascent - metrics.Height,
            _ => 0,
        };

    /// <summary>Turns a vector of the block's axes (u, v) to the screen's: y down, clockwise turns u toward +y.</summary>
    internal static (float X, float Y) Rotate(TextRotation rotation, float u, float v) =>
        rotation switch
        {
            TextRotation.Clockwise90 => (-v, u),
            TextRotation.CounterClockwise90 => (v, -u),
            _ => (u, v),
        };

    /// <summary>Rounds to a whole pixel, halves up: ⌊x + 0.5⌋, saturated to the range of an int.</summary>
    internal static long RoundPixel(float x) => (long)MathF.Floor(x + 0.5f);

    private static (long Left, long Top, int Width, int Height) GlyphRect(TextRotation rotation, long x, long y, GlyphBox box) =>
        rotation switch
        {
            // (u, v) → (−v, u): the box's u span [L, L + W) is rows, its v span [T, T + H) is columns read right to left.
            TextRotation.Clockwise90 => (x - box.Top - box.Height, y + box.Left, box.Height, box.Width),

            // (u, v) → (v, −u): the box's v span is columns, its u span is rows read bottom to top.
            TextRotation.CounterClockwise90 => (x + box.Top, y - box.Left - box.Width, box.Height, box.Width),
            _ => (x + box.Left, y + box.Top, box.Width, box.Height),
        };

    private bool TryPlace(in TextRecord record, int width, int height)
    {
        var style = record.Style;
        var shaped = Shape(record.Text, style);
        var metrics = shaped.Metrics;
        var glyphs = shaped.Glyphs;
        var top = AlignY(style.Vertical, metrics);
        var color = new Vector4(
            Premultiplied.Multiply(style.Color.R, style.Color.A) / 255f,
            Premultiplied.Multiply(style.Color.G, style.Color.A) / 255f,
            Premultiplied.Multiply(style.Color.B, style.Color.A) / 255f,
            style.Color.A / 255f
        );

        // The pixels a glyph can land on: the clip inside the target. A glyph's box is less than a page from its origin.
        var clipLeft = Math.Clamp(record.Clip.Left, 0, width);
        var clipTop = Math.Clamp(record.Clip.Top, 0, height);
        var clipRight = Math.Clamp(record.Clip.Right, 0, width);
        var clipBottom = Math.Clamp(record.Clip.Bottom, 0, height);
        if (clipLeft >= clipRight || clipTop >= clipBottom)
        {
            return true;
        }

        foreach (ref readonly var line in shaped.Lines)
        {
            // The line's start, aligned, turned, and rounded to a whole pixel (design §5.6).
            var (dx, dy) = Rotate(style.Rotation, AlignX(style.Horizontal, metrics.Width, line.Width), top + line.Baseline);
            var originX = RoundPixel(record.Position.X + dx);
            var originY = RoundPixel(record.Position.Y + dy);
            foreach (ref readonly var glyph in glyphs.Slice(line.FirstGlyph, line.GlyphCount))
            {
                // Along the text, quarter pixels: the whole part places the glyph, the fraction picks its raster.
                var quarters = RoundPixel(4 * glyph.Position.X);
                var along = quarters >> 2;
                var phase = (int)(quarters & 3);
                var across = RoundPixel(glyph.Position.Y - line.Baseline);
                var (ox, oy) = style.Rotation switch
                {
                    TextRotation.Clockwise90 => (originX - across, originY + along),
                    TextRotation.CounterClockwise90 => (originX + across, originY - along),
                    _ => (originX + along, originY + across),
                };
                if (
                    ox < clipLeft - GlyphAtlas.PageSize
                    || ox > clipRight + GlyphAtlas.PageSize
                    || oy < clipTop - GlyphAtlas.PageSize
                    || oy > clipBottom + GlyphAtlas.PageSize
                )
                {
                    continue;
                }

                if (!_atlas.TryGet(glyph.Face, glyph.Glyph, style.Size, phase, out var slot))
                {
                    return false;
                }

                if (slot.Page < 0)
                {
                    continue;
                }

                var (left, rectTop, rectWidth, rectHeight) = GlyphRect(style.Rotation, ox, oy, slot.Box);
                if (left >= clipRight || rectTop >= clipBottom || left + rectWidth <= clipLeft || rectTop + rectHeight <= clipTop)
                {
                    continue;
                }

                AddGlyph(
                    new GlyphInstance((int)left, (int)rectTop, rectWidth, rectHeight, slot.X, slot.Y, (uint)style.Rotation, 0, color),
                    slot.Page,
                    record.Clip
                );
            }
        }

        return true;
    }

    private void AddGlyph(in GlyphInstance instance, int page, ClipRect clip)
    {
        if (_instanceCount == _instances.Length)
        {
            Array.Resize(ref _instances, _instances.Length * 2);
        }

        _instances[_instanceCount++] = instance;

        // The glyph joins the last step when that step is glyphs on the same page under the same clip.
        if (_stepCount > 0)
        {
            ref var last = ref _steps[_stepCount - 1];
            if (last.Kind == DrawStepKind.Glyphs && last.Page == page && last.Clip == clip)
            {
                last = last with { Count = last.Count + 1 };
                return;
            }
        }

        AddStep(new DrawStep(DrawStepKind.Glyphs, _instanceCount - 1, 1, page, clip));
    }

    private void AddStep(in DrawStep step)
    {
        if (_stepCount == _steps.Length)
        {
            Array.Resize(ref _steps, _steps.Length * 2);
        }

        _steps[_stepCount++] = step;
    }

    /// <summary>
    /// Counts the pages the list's glyphs need, from the glyphs this <c>Draw</c> placed and the boxes of those it has not
    /// reached, and throws with the count.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private void ThrowAtlasFull(RenderList list, int failedCommand)
    {
        var cells = new List<(int Width, int Height)>();
        _atlas.AddUsedCells(cells);
        var seen = new HashSet<GlyphKey>();
        var commands = list.Commands;
        for (var i = failedCommand; i < commands.Length; i++)
        {
            if (commands[i].Kind != RenderCommandKind.Text)
            {
                continue;
            }

            ref readonly var record = ref list.Texts[commands[i].Index];
            var shaped = Shape(record.Text, record.Style);
            foreach (ref readonly var glyph in shaped.Glyphs)
            {
                var phase = (int)(RoundPixel(4 * glyph.Position.X) & 3);
                if (_atlas.Contains(glyph.Face, glyph.Glyph, record.Style.Size, phase))
                {
                    continue;
                }

                if (seen.Add(new GlyphKey(glyph.Face, glyph.Glyph, record.Style.Size, (byte)phase)))
                {
                    var box = _atlas.Measure(glyph.Face, glyph.Glyph, record.Style.Size, phase);
                    if (box.Width > 0 && box.Height > 0)
                    {
                        cells.Add((box.Width + 1, box.Height + 1));
                    }
                }
            }
        }

        var pages = Math.Max(GlyphAtlas.CountPages(cells), GlyphAtlas.MaxPages + 1);
        throw new InvalidOperationException(
            $"The text of one Draw needs {pages} glyph atlas pages of {GlyphAtlas.PageSize} × {GlyphAtlas.PageSize}; the most is {GlyphAtlas.MaxPages}."
        );
    }

    private readonly record struct ShapeKey(string Text, string FontFamily, float Size, FontWeight Weight, string Locale);

    private readonly record struct CacheEntry(ShapeKey Key, ShapedText Shaped);
}

/// <summary>What a step of a backend's <c>Draw</c> draws.</summary>
internal enum DrawStepKind : byte
{
    /// <summary>One polyline: the index is into <see cref="RenderList.Polylines"/>.</summary>
    Polyline,

    /// <summary>Glyphs of one atlas page under one clip: a range of <see cref="TextEngine.Instances"/>.</summary>
    Glyphs,
}

/// <summary>One step of a backend's <c>Draw</c>, in the order of the list's commands.</summary>
/// <param name="Kind">What it draws.</param>
/// <param name="Index">The polyline, or the first glyph record.</param>
/// <param name="Count">The number of glyph records; 1 for a polyline.</param>
/// <param name="Page">The atlas page of the glyphs.</param>
/// <param name="Clip">The clip of the glyphs or of the polyline.</param>
internal readonly record struct DrawStep(DrawStepKind Kind, int Index, int Count, int Page, ClipRect Clip);

/// <summary>
/// One glyph as the GPU reads it (design §5.6): its rectangle on the target in whole pixels, the top left of its coverage
/// on the atlas page, its rotation, and its premultiplied color. 48 bytes, laid out as the HLSL structure of Glyph.hlsl.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct GlyphInstance(
    int Left,
    int Top,
    int Width,
    int Height,
    int AtlasX,
    int AtlasY,
    uint Rotation,
    uint Padding,
    Vector4 Color
);
