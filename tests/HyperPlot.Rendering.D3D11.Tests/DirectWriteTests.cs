namespace HyperPlot.Rendering.D3D11.Tests;

/// <summary>
/// The DirectWrite text backend, by properties rather than pixels: its pixels follow the system's fonts and DirectWrite's
/// version (design §5.6, §10, invariant 11). Windows only.
/// </summary>
public sealed class DirectWriteTests
{
    /// <summary>
    /// The advances of a line add up to the width the layout reports: without trailing white space to <c>width</c>, with it
    /// to <c>widthIncludingTrailingWhitespace</c> (design §5.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A DirectWrite backend.
    ///
    /// Steps:
    /// - Shape "Sample 123" and "Sample 123   " at 17.3 pixels; read the layout's metrics of each.
    ///
    /// Expected:
    /// - The one line's width equals the layout's width, and the width with trailing white space, within 1e-3.
    /// </remarks>
    [Fact]
    public void LineWidth_SumOfAdvances_MatchesTheLayout()
    {
        using var text = new DWriteTextBackend();
        var plain = text.Shape("Sample 123", "Segoe UI", 17.3f, FontWeight.Normal, "en-us");
        var trailing = text.Shape("Sample 123   ", "Segoe UI", 17.3f, FontWeight.Normal, "en-us");

        Assert.Equal(text.LayoutWidths("Sample 123", "Segoe UI", 17.3f).Width, plain.Lines[0].Width, 1e-3f);
        Assert.Equal(text.LayoutWidths("Sample 123   ", "Segoe UI", 17.3f).WithTrailingWhitespace, trailing.Lines[0].Width, 1e-3f);
        Assert.True(trailing.Lines[0].Width > plain.Lines[0].Width);
        Assert.Equal(plain.Lines[0].Width, plain.Metrics.Width);
    }

    /// <summary>
    /// A right-to-left run goes left from its start: the first character of an Arabic word is the glyph at the line's right
    /// end (design §5.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A DirectWrite backend.
    ///
    /// Steps:
    /// - Shape the Arabic word "سلام" at 24 pixels.
    ///
    /// Expected:
    /// - The first glyph lies right of every other glyph, and its origin plus its advance stays within the line's width.
    /// </remarks>
    [Fact]
    public void RightToLeft_Arabic_FirstCharacterIsAtTheRight()
    {
        using var text = new DWriteTextBackend();
        var shaped = text.Shape("سلام", "Segoe UI", 24, FontWeight.Normal, "ar-eg");
        var glyphs = shaped.Glyphs;

        Assert.True(glyphs.Length >= 2);
        for (var i = 1; i < glyphs.Length; i++)
        {
            Assert.True(glyphs[0].Position.X > glyphs[i].Position.X, $"Glyph {i} lies at {glyphs[i].Position.X}, right of the first.");
        }

        Assert.InRange(glyphs[0].Position.X, 0, shaped.Lines[0].Width);
    }

    /// <summary>
    /// Japanese in Segoe UI, which has no Han glyphs, falls back to a font that has them, and the glyphs have ink; the
    /// locale picks the fallback font (design §5.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A DirectWrite backend.
    ///
    /// Steps:
    /// - Shape "Aa日本語" in Segoe UI with "ja-jp", and "日本語" with "zh-cn"; rasterize each glyph.
    ///
    /// Expected:
    /// - The two Latin glyphs share a face, and the Han glyphs come from another; shaping a Han character again gives the
    ///   same face number; every Han glyph has a box and coverage above 0; the Han glyphs of "ja-jp" and "zh-cn" come
    ///   from different faces.
    /// </remarks>
    [Fact]
    public void Fallback_JapaneseInSegoeUi_DrawsWithTheLocalesFont()
    {
        using var text = new DWriteTextBackend();
        var japanese = text.Shape("Aa日本語", "Segoe UI", 20, FontWeight.Normal, "ja-jp");
        var chinese = text.Shape("日本語", "Segoe UI", 20, FontWeight.Normal, "zh-cn");

        Assert.Equal(5, japanese.Glyphs.Length);
        Assert.Equal(japanese.Glyphs[0].Face, japanese.Glyphs[1].Face);
        Assert.NotEqual(japanese.Glyphs[0].Face, japanese.Glyphs[2].Face);
        Assert.Equal(japanese.Glyphs[2].Face, text.Shape("本", "Segoe UI", 20, FontWeight.Normal, "ja-jp").Glyphs[0].Face);
        for (var i = 2; i < 5; i++)
        {
            var glyph = japanese.Glyphs[i];
            Assert.True(Ink(text, glyph.Face, glyph.Glyph, 20, 0) > 0);
        }

        Assert.NotEqual(japanese.Glyphs[2].Face, chinese.Glyphs[0].Face);
    }

    /// <summary>
    /// The grayscale coverage of <c>DWRITE_TEXTURE_ALIASED_1x1</c> has levels between 0 and 255, and a quarter-pixel phase
    /// changes it: the two facts the design takes from Skia and checks here (design §5.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A DirectWrite backend; the glyph of "O" in Segoe UI at 15 pixels.
    ///
    /// Steps:
    /// - Rasterize it at phase 0 and 0.25.
    ///
    /// Expected:
    /// - Phase 0 has a byte that is neither 0 nor 255; the coverages of the two phases differ.
    /// </remarks>
    [Fact]
    public void Coverage_Grayscale_HasLevelsAndFollowsThePhase()
    {
        using var text = new DWriteTextBackend();
        var glyph = text.Shape("O", "Segoe UI", 15, FontWeight.Normal, "en-us").Glyphs[0];

        var whole = Rasterize(text, glyph.Face, glyph.Glyph, 15, 0);
        var quarter = Rasterize(text, glyph.Face, glyph.Glyph, 15, 0.25f);

        Assert.Contains(whole.Coverage, b => b is > 0 and < 255);
        Assert.False(whole.Box == quarter.Box && whole.Coverage.SequenceEqual(quarter.Coverage));
    }

    /// <summary>
    /// The glyph offsets of a run place its marks (design §5.6): a fatha over its Arabic letter, in a right-to-left run,
    /// and an acute accent over a "q", in a left-to-right one, each over the ink of its letter, just above it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A DirectWrite backend; Segoe UI at 40 pixels.
    ///
    /// Steps:
    /// - Shape "\u0633\u064E" (seen and fatha) and "q\u0301x" (q, combining acute, x); take each mark's ink box and its letter's at their
    ///   positions.
    ///
    /// Expected:
    /// - Each mark's ink lies across inside its letter's, and its bottom is above the letter's top by 0 to 10 pixels
    ///   (a quarter of the em). A sign turned on an advance offset moves the fatha out of its letter's span (7.7 pixels
    ///   left of it), and one on an ascender offset lifts it about 28 pixels.
    /// </remarks>
    [Fact]
    public void Marks_GlyphOffsets_PlaceThemOverTheirLetters()
    {
        using var text = new DWriteTextBackend();
        foreach (var (word, locale) in new[] { ("\u0633\u064E", "ar-eg"), ("q\u0301x", "en-us") })
        {
            var shaped = text.Shape(word, "Segoe UI", 40, FontWeight.Normal, locale);
            var (letterLeft, letterTop, letterRight, _) = Ink(text, shaped.Glyphs[0]);
            var (markLeft, _, markRight, markBottom) = Ink(text, shaped.Glyphs[1]);

            Assert.InRange(markLeft, letterLeft, letterRight);
            Assert.InRange(markRight, letterLeft, letterRight);
            Assert.InRange(letterTop - markBottom, 0, 10);
        }

        static (float Left, float Top, float Right, float Bottom) Ink(DWriteTextBackend text, PlacedGlyph glyph)
        {
            var box = text.MeasureGlyph(glyph.Face, glyph.Glyph, 40, 0);
            var left = glyph.Position.X + box.Left;
            var top = glyph.Position.Y + box.Top;
            return (left, top, left + box.Width, top + box.Height);
        }
    }

    /// <summary>Bold has more ink than normal, and a family the system lacks still draws through the fallback (design §5.6).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A DirectWrite backend.
    ///
    /// Steps:
    /// - Shape "Heading" normal and bold in Segoe UI, and in a family named "No Such Font 7f3a"; add up each one's coverage.
    ///
    /// Expected:
    /// - The bold sum is above the normal one; the missing family's sum is above 0.
    /// </remarks>
    [Fact]
    public void WeightAndMissingFamily_Ink_FollowTheStyle()
    {
        using var text = new DWriteTextBackend();
        var normal = Ink(text, text.Shape("Heading", "Segoe UI", 18, FontWeight.Normal, "en-us"), 18);
        var bold = Ink(text, text.Shape("Heading", "Segoe UI", 18, FontWeight.Bold, "en-us"), 18);
        var missing = Ink(text, text.Shape("Heading", "No Such Font 7f3a", 18, FontWeight.Normal, "en-us"), 18);

        Assert.True(bold > normal, $"Bold has {bold}, normal {normal}.");
        Assert.True(missing > 0);
    }

    /// <summary>A line feed makes two lines, and two lines are about twice as tall as one (design §5.6).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A DirectWrite backend.
    ///
    /// Steps:
    /// - Shape "Top" and "Top\nBottom" at 16 pixels.
    ///
    /// Expected:
    /// - One line, then two; the second line's baseline is one line's height below the first; the height of two is twice
    ///   the height of one within 1 pixel; ascent and descent add up to a line's height; the glyphs of "Top" sit on the
    ///   first baseline (y 0) and those of "Bottom" on the second.
    /// </remarks>
    [Fact]
    public void LineFeed_TwoLines_TwiceTheHeight()
    {
        using var text = new DWriteTextBackend();
        var one = text.Shape("Top", "Segoe UI", 16, FontWeight.Normal, "en-us");
        var two = text.Shape("Top\nBottom", "Segoe UI", 16, FontWeight.Normal, "en-us");

        Assert.Single(one.Lines.ToArray());
        Assert.Equal(2, two.Lines.Length);
        Assert.Equal(0, two.Lines[0].Baseline);
        Assert.Equal(one.Metrics.Height, two.Lines[1].Baseline, 1e-3f);
        Assert.Equal(2 * one.Metrics.Height, two.Metrics.Height, 1f);
        Assert.Equal(one.Metrics.Ascent, two.Metrics.Ascent);
        Assert.Equal(one.Metrics.Height, one.Metrics.Ascent + one.Metrics.Descent, 1e-3f);
        Assert.Equal(two.Metrics.Height - two.Lines[1].Baseline - two.Metrics.Ascent, two.Metrics.Descent, 1e-3f);
        foreach (var glyph in two.Glyphs[..two.Lines[0].GlyphCount])
        {
            Assert.Equal(0, glyph.Position.Y, 1e-3f);
        }

        var second = two.Lines[1];
        foreach (var glyph in two.Glyphs.Slice(second.FirstGlyph, second.GlyphCount))
        {
            Assert.Equal(second.Baseline, glyph.Position.Y, 1e-3f);
        }
    }

    /// <summary>
    /// An exception in the renderer's callback does not cross into DirectWrite: the callback returns E_FAIL, and the same
    /// exception comes out after <c>Draw</c> returns (design §5.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A DirectWrite backend whose glyph-run hook throws a given exception.
    ///
    /// Steps:
    /// - Shape a text; then take the hook away and shape it again.
    ///
    /// Expected:
    /// - The given exception object comes out of the first; the second shapes.
    /// </remarks>
    [Fact]
    public void Callback_Throws_TheSameExceptionComesOutAfterDraw()
    {
        using var text = new DWriteTextBackend();
        var thrown = new FormatException("From the hook.");
        text.GlyphRunHook = () => throw thrown;

        var caught = Assert.Throws<FormatException>(() => text.Shape("Fails", "Segoe UI", 12, FontWeight.Normal, "en-us"));
        Assert.Same(thrown, caught);

        text.GlyphRunHook = null;
        Assert.Equal(5, text.Shape("Fails", "Segoe UI", 12, FontWeight.Normal, "en-us").Glyphs.Length);
    }

    /// <summary>Shaping and rasterizing work on a thread that never initialized COM itself (design §5.6).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A new thread that calls no CoInitialize.
    ///
    /// Steps:
    /// - On it, make a DirectWrite backend, shape "Thread", and rasterize its glyphs.
    ///
    /// Expected:
    /// - No exception, and ink above 0.
    /// </remarks>
    [Fact]
    public void Thread_WithoutCoInitialize_ShapesAndRasterizes()
    {
        Exception? failure = null;
        long ink = 0;
        var thread = new Thread(() =>
        {
            try
            {
                using var text = new DWriteTextBackend();
                ink = Ink(text, text.Shape("Thread", "Segoe UI", 14, FontWeight.Normal, "en-us"), 14);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.Start();
        thread.Join();

        Assert.Null(failure);
        Assert.True(ink > 0);
    }

    private static (GlyphBox Box, byte[] Coverage) Rasterize(DWriteTextBackend text, int face, ushort glyph, float size, float phase)
    {
        var box = text.MeasureGlyph(face, glyph, size, phase);
        var coverage = new byte[box.Width * box.Height];
        text.RasterizeGlyph(face, glyph, size, phase, coverage);
        return (box, coverage);
    }

    private static long Ink(DWriteTextBackend text, int face, ushort glyph, float size, float phase) =>
        Rasterize(text, face, glyph, size, phase).Coverage.Sum(b => (long)b);

    private static long Ink(DWriteTextBackend text, ShapedText shaped, float size)
    {
        long sum = 0;
        foreach (var glyph in shaped.Glyphs)
        {
            sum += Ink(text, glyph.Face, glyph.Glyph, size, 0);
        }

        return sum;
    }
}
