using System.Numerics;
using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>
/// The fake text backend of the T1 tests (design §10): one glyph a character, numbered by its code, with a fixed advance,
/// on the baseline but for '^', which sits raised; lines split at each line feed; a box and a coverage that follow from the glyph's number and phase, so that a wrong
/// glyph, phase, turn, or flip shows in the pixels. The Core and D3D11 tests share this file.
/// </summary>
internal sealed class FakeTextBackend : ITextBackend
{
    private readonly Dictionary<(string Family, FontWeight Weight, string Locale), int> _faces = [];

    /// <summary>Gets the calls of <see cref="Shape"/>, in order.</summary>
    public List<(string Text, string Family, float Size, FontWeight Weight, string Locale)> Shaped { get; } = [];

    /// <summary>Gets the glyphs rasterized, in order.</summary>
    public List<(int Face, ushort Glyph, float Size, float Phase)> Rasterized { get; } = [];

    /// <summary>Gets or sets the box of a glyph in place of the default one, for the tests of the atlas.</summary>
    public Func<ushort, float, GlyphBox>? Box { get; set; }

    /// <summary>The advance of a glyph at an em size: fractional, so that glyphs land on every quarter-pixel phase.</summary>
    public static float Advance(float size) => size * 0.5625f;

    /// <summary>The height of a line at an em size.</summary>
    public static float LineHeight(float size) => size * 1.25f;

    /// <summary>The ascent of a line at an em size.</summary>
    public static float Ascent(float size) => size * 0.875f;

    /// <summary>The offset of a glyph from its line's baseline, y down: '^' sits raised, as a superscript would.</summary>
    public static float Raise(char glyph, float size) => glyph == '^' ? -0.3f * size : 0;

    /// <summary>The default box of a glyph: its number and phase pick its size and its place by the origin.</summary>
    public static GlyphBox DefaultBox(ushort glyph, float size, float phase)
    {
        if (glyph == ' ')
        {
            return default;
        }

        var height = (int)(size * 0.6f) + (glyph % 4);
        var width = (int)(size * 0.35f) + (glyph % 3) + (phase > 0 ? 1 : 0);
        return new GlyphBox(glyph % 2, -height + (glyph % 3), width, height);
    }

    /// <summary>The coverage of a glyph's pixel: uneven across x and y, and different for each glyph and phase.</summary>
    public static byte Coverage(ushort glyph, float phase, int x, int y) =>
        (byte)(((x * 37) + (y * 59) + (glyph * 13) + ((int)(phase * 4) * 71) + 40) % 256);

    /// <inheritdoc />
    public ShapedText Shape(string text, string fontFamily, float size, FontWeight weight, string locale)
    {
        Shaped.Add((text, fontFamily, size, weight, locale));
        if (!_faces.TryGetValue((fontFamily, weight, locale), out var face))
        {
            face = _faces.Count;
            _faces.Add((fontFamily, weight, locale), face);
        }

        var lines = text.Split('\n');
        var glyphs = new List<PlacedGlyph>();
        var placed = new TextLine[lines.Length];
        var widest = 0f;
        for (var l = 0; l < lines.Length; l++)
        {
            var baseline = l * LineHeight(size);
            var first = glyphs.Count;
            for (var c = 0; c < lines[l].Length; c++)
            {
                glyphs.Add(new PlacedGlyph(face, lines[l][c], new Vector2(c * Advance(size), baseline + Raise(lines[l][c], size))));
            }

            var width = lines[l].Length * Advance(size);
            placed[l] = new TextLine(first, lines[l].Length, width, baseline);
            widest = MathF.Max(widest, width);
        }

        var metrics = new TextMetrics(widest, lines.Length * LineHeight(size), Ascent(size), LineHeight(size) - Ascent(size));
        return new ShapedText(metrics, [.. glyphs], placed);
    }

    /// <inheritdoc />
    public GlyphBox MeasureGlyph(int face, ushort glyph, float size, float phase) => Box?.Invoke(glyph, size) ?? DefaultBox(glyph, size, phase);

    /// <inheritdoc />
    public void RasterizeGlyph(int face, ushort glyph, float size, float phase, Span<byte> coverage)
    {
        Rasterized.Add((face, glyph, size, phase));
        var box = MeasureGlyph(face, glyph, size, phase);
        for (var y = 0; y < box.Height; y++)
        {
            for (var x = 0; x < box.Width; x++)
            {
                coverage[(y * box.Width) + x] = Coverage(glyph, phase, x, y);
            }
        }
    }
}
