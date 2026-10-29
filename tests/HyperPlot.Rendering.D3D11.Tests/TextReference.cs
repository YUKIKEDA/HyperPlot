using System.Numerics;
using HyperPlot.Tests;

namespace HyperPlot.Rendering.D3D11.Tests;

/// <summary>
/// The CPU reference of the texts drawn with the fake text backend (design §5.6, §10), written apart from the engine: it
/// shapes with the fake, places each line's start and each glyph by the formulas of §5.6, reads each pixel's coverage from
/// the fake's coverage function through the turn, and puts the premultiplied color times the coverage over the image.
/// </summary>
internal static class TextReference
{
    /// <summary>Draws one text over an opaque image of RGBA bytes, inside a clip of whole pixels.</summary>
    internal static void Draw(
        byte[] image,
        int width,
        int height,
        FakeTextBackend fake,
        string text,
        Vector2 position,
        TextStyle style,
        (int X, int Y, int W, int H)? clip = null
    )
    {
        var shaped = fake.Shape(text, style.FontFamily, style.Size, style.Weight, style.Locale);
        fake.Shaped.RemoveAt(fake.Shaped.Count - 1);
        var m = shaped.Metrics;
        var firstBaseline = style.Vertical switch
        {
            TextBaseline.Top => m.Ascent,
            TextBaseline.Middle => m.Ascent - (m.Height / 2),
            TextBaseline.Bottom => m.Ascent - m.Height,
            _ => 0f,
        };
        var a = style.Color.A;
        float[] color = [Multiply(style.Color.R, a), Multiply(style.Color.G, a), Multiply(style.Color.B, a), a];
        foreach (var line in shaped.Lines)
        {
            var u = style.Horizontal switch
            {
                TextAlign.Center => (m.Width - line.Width) / 2,
                TextAlign.Right => m.Width - line.Width,
                _ => 0f,
            };
            var v = firstBaseline + line.Baseline;
            var (dx, dy) = style.Rotation switch
            {
                TextRotation.Clockwise90 => (-v, u),
                TextRotation.CounterClockwise90 => (v, -u),
                _ => (u, v),
            };
            var lineX = (int)MathF.Floor(position.X + dx + 0.5f);
            var lineY = (int)MathF.Floor(position.Y + dy + 0.5f);
            foreach (var glyph in shaped.Glyphs.Slice(line.FirstGlyph, line.GlyphCount))
            {
                var quarter = (int)MathF.Floor((4 * glyph.Position.X) + 0.5f);
                var whole = (int)Math.Floor(quarter / 4.0);
                var phase = (quarter - (4 * whole)) / 4f;
                var across = (int)MathF.Floor(glyph.Position.Y - line.Baseline + 0.5f);
                var box = fake.MeasureGlyph(glyph.Face, glyph.Glyph, style.Size, phase);
                for (var gy = 0; gy < box.Height; gy++)
                {
                    for (var gx = 0; gx < box.Width; gx++)
                    {
                        // The glyph's pixel (gx, gy) lies at (L + gx, T + gy) from its origin, upright; then turned. The origin
                        // lies `whole` along the line and `across` below its baseline.
                        var (px, py) = style.Rotation switch
                        {
                            TextRotation.Clockwise90 => (lineX - across - (box.Top + gy) - 1, lineY + whole + box.Left + gx),
                            TextRotation.CounterClockwise90 => (lineX + across + box.Top + gy, lineY - whole - (box.Left + gx) - 1),
                            _ => (lineX + whole + box.Left + gx, lineY + across + box.Top + gy),
                        };
                        if (px < 0 || py < 0 || px >= width || py >= height)
                        {
                            continue;
                        }

                        if (clip is { } c && (px < c.X || py < c.Y || px >= c.X + c.W || py >= c.Y + c.H))
                        {
                            continue;
                        }

                        Blend(image, ((py * width) + px) * 4, color, FakeTextBackend.Coverage(glyph.Glyph, phase, gx, gy) / 255f);
                    }
                }
            }
        }
    }

    /// <summary>Puts a premultiplied color times a coverage over an image's pixel, as the GPU's blend does.</summary>
    private static void Blend(byte[] image, int index, float[] color, float coverage)
    {
        var alpha = color[3] / 255 * coverage;
        for (var channel = 0; channel < 4; channel++)
        {
            image[index + channel] = (byte)MathF.Round((color[channel] * coverage) + (image[index + channel] * (1 - alpha)));
        }
    }

    private static float Multiply(byte c, byte a) => ((c * a) + 127) / 255;
}
