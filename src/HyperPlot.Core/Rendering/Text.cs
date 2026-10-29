using System.Numerics;

namespace HyperPlot.Rendering;

/// <summary>The weight of a font (design §5.6).</summary>
public enum FontWeight
{
    /// <summary>The regular weight.</summary>
    Normal = 0,

    /// <summary>The bold weight.</summary>
    Bold = 1,
}

/// <summary>Where each line of a text goes across the width of its block (design §5.6).</summary>
public enum TextAlign
{
    /// <summary>The line starts at the block's left edge.</summary>
    Left = 0,

    /// <summary>The line is centered in the block.</summary>
    Center = 1,

    /// <summary>The line ends at the block's right edge.</summary>
    Right = 2,
}

/// <summary>Which height of a text's block the position's y names (design §5.6).</summary>
public enum TextBaseline
{
    /// <summary>The top of the first line.</summary>
    Top = 0,

    /// <summary>The middle of the block's height.</summary>
    Middle = 1,

    /// <summary>The baseline of the first line.</summary>
    Baseline = 2,

    /// <summary>The bottom of the last line.</summary>
    Bottom = 3,
}

/// <summary>How a text's block turns around its position (design §5.6).</summary>
public enum TextRotation
{
    /// <summary>The text runs left to right.</summary>
    None = 0,

    /// <summary>The text runs top to bottom.</summary>
    Clockwise90 = 1,

    /// <summary>The text runs bottom to top, as the title of a y axis does.</summary>
    CounterClockwise90 = 2,
}

/// <summary>How a text is drawn (design §5.6).</summary>
/// <param name="Color">The color.</param>
/// <param name="Size">The em size in physical pixels: finite, above 0, and at most 512.</param>
/// <param name="FontFamily">The font family; a family the system lacks falls back as the backend's system does.</param>
/// <param name="Weight">The weight.</param>
/// <param name="Horizontal">Where each line goes across the block.</param>
/// <param name="Vertical">Which height of the block the position's y names.</param>
/// <param name="Rotation">How the block turns around the position.</param>
/// <param name="Locale">
/// The locale the text is shaped in, such as "ja-jp": the system fallback picks the font of Han characters by it.
/// </param>
public readonly record struct TextStyle(
    Color Color,
    float Size,
    string FontFamily = "Segoe UI",
    FontWeight Weight = FontWeight.Normal,
    TextAlign Horizontal = TextAlign.Left,
    TextBaseline Vertical = TextBaseline.Baseline,
    TextRotation Rotation = TextRotation.None,
    string Locale = "en-us"
);

/// <summary>The size of a shaped text, in physical pixels (design §5.6).</summary>
/// <param name="Width">The width of the widest line.</param>
/// <param name="Height">The sum of the lines' heights.</param>
/// <param name="Ascent">From the top of the first line to its baseline.</param>
/// <param name="Descent">From the baseline of the last line to its bottom.</param>
public readonly record struct TextMetrics(float Width, float Height, float Ascent, float Descent);

/// <summary>One glyph of a shaped text (design §5.6).</summary>
/// <param name="Face">The backend's number of the font face, the same for the same face while the backend lives.</param>
/// <param name="Glyph">The glyph's number in its face.</param>
/// <param name="Position">The glyph's origin, from the start of the first line's baseline, y down, in physical pixels.</param>
public readonly record struct PlacedGlyph(int Face, ushort Glyph, Vector2 Position);

/// <summary>One line of a shaped text (design §5.6).</summary>
/// <param name="FirstGlyph">The index of the line's first glyph.</param>
/// <param name="GlyphCount">The number of the line's glyphs.</param>
/// <param name="Width">The width of the line, trailing white space included, in physical pixels.</param>
/// <param name="Baseline">The y of the line's baseline below the first line's baseline: 0 for the first line.</param>
public readonly record struct TextLine(int FirstGlyph, int GlyphCount, float Width, float Baseline);

/// <summary>The box of a glyph's coverage, in whole pixels from the glyph's origin, y down (design §5.6).</summary>
/// <param name="Left">The left edge.</param>
/// <param name="Top">The top edge.</param>
/// <param name="Width">The width, at least 0.</param>
/// <param name="Height">The height, at least 0.</param>
public readonly record struct GlyphBox(int Left, int Top, int Width, int Height);

/// <summary>A text shaped by an <see cref="ITextBackend"/>: its glyphs, its lines, and its size (design §5.6). Immutable.</summary>
public sealed class ShapedText
{
    private readonly PlacedGlyph[] _glyphs;
    private readonly TextLine[] _lines;

    /// <summary>Initializes a new instance of the <see cref="ShapedText"/> class with copies of the arrays.</summary>
    /// <param name="metrics">The size.</param>
    /// <param name="glyphs">The glyphs, in the order of the lines.</param>
    /// <param name="lines">The lines, at least one; each names its range of <paramref name="glyphs"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="glyphs"/> or <paramref name="lines"/> is null.</exception>
    /// <exception cref="ArgumentException">There is no line, or a line's range lies outside the glyphs.</exception>
    public ShapedText(TextMetrics metrics, PlacedGlyph[] glyphs, TextLine[] lines)
    {
        ArgumentNullException.ThrowIfNull(glyphs);
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Length == 0)
        {
            throw new ArgumentException("A shaped text has at least one line.", nameof(lines));
        }

        foreach (var line in lines)
        {
            if (line.FirstGlyph < 0 || line.GlyphCount < 0 || line.FirstGlyph > glyphs.Length - line.GlyphCount)
            {
                throw new ArgumentException($"The line {line} lies outside the {glyphs.Length} glyphs.", nameof(lines));
            }
        }

        Metrics = metrics;
        _glyphs = (PlacedGlyph[])glyphs.Clone();
        _lines = (TextLine[])lines.Clone();
    }

    /// <summary>Gets the size.</summary>
    public TextMetrics Metrics { get; }

    /// <summary>Gets the glyphs, in the order of the lines.</summary>
    public ReadOnlySpan<PlacedGlyph> Glyphs => _glyphs;

    /// <summary>Gets the lines.</summary>
    public ReadOnlySpan<TextLine> Lines => _lines;
}

/// <summary>
/// What a backend implements for text (design §5.6): shaping, and the coverage of one glyph. The backend's
/// <see cref="TextEngine"/> keeps the results, so each text is shaped and each glyph rasterized once while it is kept.
/// </summary>
/// <remarks>The thread that draws calls it, one call at a time.</remarks>
public interface ITextBackend
{
    /// <summary>
    /// Shapes a text: lines split at '\n' with no wrapping, fonts falling back as the system does. Alignment, rotation, and
    /// color are not part of shaping; the engine applies them when it draws.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="fontFamily">The font family.</param>
    /// <param name="size">The em size in physical pixels.</param>
    /// <param name="weight">The weight.</param>
    /// <param name="locale">The locale.</param>
    /// <returns>The glyphs, the lines, and the size. An empty text has one empty line of the font's height.</returns>
    ShapedText Shape(string text, string fontFamily, float size, FontWeight weight, string locale);

    /// <summary>Gets the box of a glyph's coverage with its origin at the horizontal fraction <paramref name="phase"/>.</summary>
    /// <param name="face">The face's number, from <see cref="Shape"/>.</param>
    /// <param name="glyph">The glyph's number.</param>
    /// <param name="size">The em size in physical pixels.</param>
    /// <param name="phase">0, 0.25, 0.5, or 0.75.</param>
    /// <returns>The box; an empty one for a glyph with no ink.</returns>
    GlyphBox MeasureGlyph(int face, ushort glyph, float size, float phase);

    /// <summary>Writes a glyph's coverage, one byte a pixel from 0 to 255, rows top first, over the box of <see cref="MeasureGlyph"/>.</summary>
    /// <param name="face">The face's number.</param>
    /// <param name="glyph">The glyph's number.</param>
    /// <param name="size">The em size in physical pixels.</param>
    /// <param name="phase">0, 0.25, 0.5, or 0.75.</param>
    /// <param name="coverage">Width · Height bytes of the box.</param>
    void RasterizeGlyph(int face, ushort glyph, float size, float phase, Span<byte> coverage);
}
