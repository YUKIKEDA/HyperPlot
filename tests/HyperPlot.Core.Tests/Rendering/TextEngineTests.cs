using System.Numerics;
using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>The text of row T1 in Core, with the fake text backend (design §5.1, §5.6, §10).</summary>
public sealed class TextEngineTests
{
    private static readonly TextStyle Style = new(new Color(20, 30, 40, 255), 16);

    private static readonly TextStyle[] OutOfRange =
    [
        Style with
        {
            Size = 0,
        },
        Style with
        {
            Size = -1,
        },
        Style with
        {
            Size = 512.01f,
        },
        Style with
        {
            Size = float.NaN,
        },
        Style with
        {
            Size = float.PositiveInfinity,
        },
        Style with
        {
            Weight = (FontWeight)2,
        },
        Style with
        {
            Horizontal = (TextAlign)3,
        },
        Style with
        {
            Vertical = (TextBaseline)4,
        },
        Style with
        {
            Rotation = (TextRotation)3,
        },
        Style with
        {
            Rotation = (TextRotation)(-1),
        },
    ];

    /// <summary>
    /// <c>DrawText</c> and <c>Measure</c> check their arguments the same way (design §5.1, §5.6): a null text, family, or
    /// locale; an empty family or locale; a size or an enumeration out of range. <c>DrawText</c> also checks the position.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A list and an engine over the fake backend.
    ///
    /// Steps:
    /// - Record and measure with each bad argument.
    ///
    /// Expected:
    /// - ArgumentNullException for the nulls, ArgumentException for the empty strings, ArgumentOutOfRangeException for the
    ///   rest, named after the argument; nothing recorded; the backend shapes nothing. Size 512 is accepted.
    /// </remarks>
    [Fact]
    public void Arguments_OutOfRange_ThrowInDrawAndMeasure()
    {
        var list = new RenderList();
        var fake = new FakeTextBackend();
        var engine = new TextEngine(fake);
        var at = new Vector2(10, 10);

        Assert.Equal("text", Assert.Throws<ArgumentNullException>(() => list.DrawText(null!, at, Style)).ParamName);
        Assert.Equal("text", Assert.Throws<ArgumentNullException>(() => engine.Measure(null!, Style)).ParamName);
        foreach (var style in new[] { Style with { FontFamily = null! }, Style with { Locale = null! } })
        {
            Assert.Equal("style", Assert.Throws<ArgumentNullException>(() => list.DrawText("a", at, style)).ParamName);
            Assert.Equal("style", Assert.Throws<ArgumentNullException>(() => engine.Measure("a", style)).ParamName);
        }

        foreach (var style in new[] { Style with { FontFamily = string.Empty }, Style with { Locale = string.Empty } })
        {
            Assert.Equal("style", Assert.Throws<ArgumentException>(() => list.DrawText("a", at, style)).ParamName);
            Assert.Equal("style", Assert.Throws<ArgumentException>(() => engine.Measure("a", style)).ParamName);
        }

        foreach (var style in OutOfRange)
        {
            Assert.Equal("style", Assert.Throws<ArgumentOutOfRangeException>(() => list.DrawText("a", at, style)).ParamName);
            Assert.Equal("style", Assert.Throws<ArgumentOutOfRangeException>(() => engine.Measure("a", style)).ParamName);
        }

        foreach (var position in new[] { new Vector2(float.NaN, 0), new Vector2(0, float.PositiveInfinity), new Vector2(float.NegativeInfinity, 1) })
        {
            Assert.Equal("position", Assert.Throws<ArgumentOutOfRangeException>(() => list.DrawText("a", position, Style)).ParamName);
        }

        Assert.True(list.Commands.IsEmpty);
        Assert.Empty(fake.Shaped);

        list.DrawText("a", at, Style with { Size = 512 });
        Assert.Equal(512, engine.Measure("a", Style with { Size = 512 }).Height / 1.25f);
        Assert.Equal(1, list.Commands.Length);
    }

    /// <summary>
    /// An empty text records nothing; polylines and texts keep the order they were recorded in, each text with the clip of
    /// its time; <c>Clear</c> drops them (design §5.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A list.
    ///
    /// Steps:
    /// - Record an empty text, a text, a polyline, a clip, a text, a polyline that draws nothing (one point), and a text;
    ///   then clear.
    ///
    /// Expected:
    /// - Commands text 0, polyline 0, text 1, text 2; the second and third texts carry the clip; after Clear no command
    ///   and no text.
    /// </remarks>
    [Fact]
    public void DrawText_Order_KeepsTheRecordingOrderAndTheClip()
    {
        var list = new RenderList();
        list.DrawText(string.Empty, new Vector2(1, 1), Style);
        list.DrawText("first", new Vector2(1, 1), Style);
        list.DrawPolyline([new Vector2(0, 0), new Vector2(5, 5)], new LineStyle(Style.Color, 1));
        list.SetClip(2, 3, 40, 50);
        list.DrawText("second", new Vector2(1, 1), Style);
        list.DrawPolyline([new Vector2(0, 0)], new LineStyle(Style.Color, 1));
        list.DrawText("third", new Vector2(1, 1), Style);

        Assert.Equal(
            [
                new RenderCommand(RenderCommandKind.Text, 0),
                new RenderCommand(RenderCommandKind.Polyline, 0),
                new RenderCommand(RenderCommandKind.Text, 1),
                new RenderCommand(RenderCommandKind.Text, 2),
            ],
            list.Commands.ToArray()
        );
        Assert.Equal(["first", "second", "third"], list.Texts.ToArray().Select(t => t.Text));
        Assert.Equal(ClipRect.All, list.Texts[0].Clip);
        Assert.Equal(new ClipRect(2, 3, 42, 53), list.Texts[2].Clip);

        list.Clear();
        Assert.True(list.Commands.IsEmpty);
        Assert.True(list.Texts.IsEmpty);
    }

    /// <summary>
    /// The cache shapes a text once for each font, size, weight, and locale; color, alignment, and rotation do not shape
    /// again; a measured text is drawn without shaping again (design §5.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An engine over the fake backend; a list.
    ///
    /// Steps:
    /// - Measure "Label" twice; record it with other colors, alignments, and a rotation and prepare the list; measure it
    ///   with the locale "ja-jp", with size 17, with bold, and in another family.
    ///
    /// Expected:
    /// - 5 shapes: one for the first measure and the draws, and one each for the locale, the size, the weight, and the
    ///   family; Measure returns the shaped text's metrics.
    /// </remarks>
    [Fact]
    public void Cache_SameKey_ShapesOnce()
    {
        var fake = new FakeTextBackend();
        var engine = new TextEngine(fake);
        var metrics = engine.Measure("Label", Style);
        Assert.Equal(engine.Shape("Label", Style).Metrics, metrics);
        _ = engine.Measure("Label", Style);
        var list = new RenderList();
        list.DrawText("Label", new Vector2(10, 40), Style with { Color = new Color(200, 0, 0, 128) });
        list.DrawText("Label", new Vector2(10, 40), Style with { Horizontal = TextAlign.Right, Vertical = TextBaseline.Top });
        list.DrawText("Label", new Vector2(10, 40), Style with { Rotation = TextRotation.CounterClockwise90 });
        engine.Prepare(list, 100, 100);
        Assert.Single(fake.Shaped);

        _ = engine.Measure("Label", Style with { Locale = "ja-jp" });
        _ = engine.Measure("Label", Style with { Size = 17 });
        _ = engine.Measure("Label", Style with { Weight = FontWeight.Bold });
        _ = engine.Measure("Label", Style with { FontFamily = "Consolas" });

        Assert.Equal(5, fake.Shaped.Count);
        Assert.Equal(("Label", "Segoe UI", 16f, FontWeight.Normal, "ja-jp"), fake.Shaped[1]);
    }

    /// <summary>
    /// The cache keeps 4,096 texts and lets go of the one used longest ago when the 4,097th comes (design §5.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An engine over the fake backend.
    ///
    /// Steps:
    /// - Measure "0" to "4095"; measure "0" again, so that "1" becomes the oldest; measure "4096"; then measure "0", "2",
    ///   and "1".
    ///
    /// Expected:
    /// - 4,096 texts after the first pass; "4096" shapes and the count stays 4,096; "0" and "2" are still there, and "1"
    ///   shapes again.
    /// </remarks>
    [Fact]
    public void Cache_Text4097_LetsGoOfTheOldest()
    {
        var fake = new FakeTextBackend();
        var engine = new TextEngine(fake);
        for (var i = 0; i < 4096; i++)
        {
            _ = engine.Measure(i.ToString(System.Globalization.CultureInfo.InvariantCulture), Style);
        }

        Assert.Equal(4096, engine.CacheCount);
        _ = engine.Measure("0", Style);
        _ = engine.Measure("4096", Style);
        Assert.Equal(4096, engine.CacheCount);
        Assert.Equal(4097, fake.Shaped.Count);

        _ = engine.Measure("0", Style);
        _ = engine.Measure("2", Style);
        Assert.Equal(4097, fake.Shaped.Count);
        _ = engine.Measure("1", Style);
        Assert.Equal(4098, fake.Shaped.Count);
        Assert.Equal("1", fake.Shaped[^1].Text);
    }

    /// <summary>A shaped text copies the arrays it is given: changing them later changes nothing (design §5.6).</summary>
    /// <remarks>
    /// Preconditions:
    /// - Arrays of two glyphs and one line.
    ///
    /// Steps:
    /// - Make a shaped text; overwrite both arrays.
    ///
    /// Expected:
    /// - The shaped text's glyphs and line are the ones it was made with; a line outside the glyphs, one with a negative
    ///   start or count, and no line throw.
    /// </remarks>
    [Fact]
    public void ShapedText_Arrays_AreCopied()
    {
        PlacedGlyph[] glyphs = [new(1, 2, new Vector2(3, 4)), new(5, 6, new Vector2(7, 8))];
        TextLine[] lines = [new(0, 2, 9, 0)];
        var shaped = new ShapedText(new TextMetrics(9, 10, 7, 3), glyphs, lines);

        glyphs[0] = default;
        lines[0] = default;

        Assert.Equal(new PlacedGlyph(1, 2, new Vector2(3, 4)), shaped.Glyphs[0]);
        Assert.Equal(new TextLine(0, 2, 9, 0), shaped.Lines[0]);
        Assert.Throws<ArgumentException>(() => new ShapedText(default, glyphs, [new TextLine(1, 2, 0, 0)]));
        Assert.Throws<ArgumentException>(() => new ShapedText(default, glyphs, []));
        Assert.Throws<ArgumentException>(() => new ShapedText(default, glyphs, [new TextLine(-1, 1, 0, 0)]));
        Assert.Throws<ArgumentException>(() => new ShapedText(default, glyphs, [new TextLine(0, -1, 0, 0)]));
    }

    /// <summary>
    /// The glyph records follow the placement of design §5.6: each line's start, aligned and turned, rounds to a whole
    /// pixel; a glyph's place along the text rounds to a quarter pixel, whose whole part places it and whose fraction picks
    /// its raster, and its offset across the line rounds to a whole pixel; the turned box reads the coverage through the turn.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An engine over the fake backend; a 400 × 400 target.
    ///
    /// Steps:
    /// - For each of the 12 alignments and the 3 rotations, prepare "A^B\nC" at size 13, "ABC^E\nFG\nH" at size 13,
    ///   and "ABC^E\nF^\nH" at size 14, at (200.3, 199.6); '^' sits raised off its baseline.
    ///
    /// Expected:
    /// - Each record equals the one computed here from the shaped text with the same formulas, written out apart from the
    ///   engine; the 4 phases all occur over the cases.
    /// </remarks>
    [Fact]
    public void Placement_AlignmentsAndRotations_FollowTheFormulas()
    {
        var fake = new FakeTextBackend();
        var engine = new TextEngine(fake);
        var phases = new HashSet<int>();
        var at = new Vector2(200.3f, 199.6f);
        foreach (var (text, size) in new[] { ("A^B\nC", 13f), ("ABC^E\nFG\nH", 13f), ("ABC^E\nF^\nH", 14f) })
        {
            foreach (var rotation in new[] { TextRotation.None, TextRotation.Clockwise90, TextRotation.CounterClockwise90 })
            {
                foreach (var horizontal in new[] { TextAlign.Left, TextAlign.Center, TextAlign.Right })
                {
                    foreach (var vertical in new[] { TextBaseline.Top, TextBaseline.Middle, TextBaseline.Baseline, TextBaseline.Bottom })
                    {
                        var style = Style with { Size = size, Horizontal = horizontal, Vertical = vertical, Rotation = rotation };
                        var list = new RenderList();
                        list.DrawText(text, at, style);
                        engine.Prepare(list, 400, 400);
                        var expected = Expected(fake, engine, text, at, style, phases);
                        Assert.Equal(expected, engine.Instances.ToArray().Select(i => (i.Left, i.Top, i.Width, i.Height, i.Rotation)));
                    }
                }
            }
        }

        Assert.Equal([0, 1, 2, 3], phases.Order());
    }

    /// <summary>
    /// Texts that follow one another share a step while the page and the clip stay; a polyline between them, or a new
    /// clip, starts a new step (design §5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An engine over the fake backend.
    ///
    /// Steps:
    /// - Prepare text, text, polyline, text, clip, text.
    ///
    /// Expected:
    /// - Steps: glyphs of the first two texts, the polyline, the third text's glyphs, the fourth text's glyphs.
    /// </remarks>
    [Fact]
    public void Steps_TextsInARow_ShareAStep()
    {
        var engine = new TextEngine(new FakeTextBackend());
        var list = new RenderList();
        list.DrawText("ab", new Vector2(10, 30), Style);
        list.DrawText("cd", new Vector2(10, 60), Style);
        list.DrawPolyline([new Vector2(0, 0), new Vector2(5, 5)], new LineStyle(Style.Color, 1));
        list.DrawText("ef", new Vector2(10, 90), Style);
        list.SetClip(0, 0, 50, 200);
        list.DrawText("gh", new Vector2(10, 120), Style);

        engine.Prepare(list, 200, 200);

        Assert.Equal(
            [(DrawStepKind.Glyphs, 0, 4), (DrawStepKind.Polyline, 0, 1), (DrawStepKind.Glyphs, 4, 2), (DrawStepKind.Glyphs, 6, 2)],
            engine.Steps.ToArray().Select(s => (s.Kind, s.Index, s.Count))
        );
    }

    /// <summary>
    /// Glyphs sit on shelves with 1 empty pixel to their right and below, and the page holds 0 in those pixels; the
    /// written rectangles cover the new glyphs only, and the next <c>Draw</c> of the same glyphs writes nothing (design §5.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An engine over the fake backend.
    ///
    /// Steps:
    /// - Prepare "aei" (glyphs of one height) at size 16, whose advance of 9 pixels puts every glyph at phase 0; read the
    ///   slots, the page, and the written rectangles; clear them; prepare it again.
    ///
    /// Expected:
    /// - Each glyph starts 1 pixel right of the end of the one before; the column right of each glyph and the row below it
    ///   hold 0; one rectangle covers the three cells; nothing is written the second time, and nothing is rasterized again.
    /// </remarks>
    [Fact]
    public void Atlas_Cells_KeepAnEmptyPixelAndWriteOnlyNewGlyphs()
    {
        var fake = new FakeTextBackend();
        var engine = new TextEngine(fake);
        var list = new RenderList();
        list.DrawText("aei", new Vector2(10, 40), Style);
        engine.Prepare(list, 200, 200);
        var atlas = engine.Atlas;
        var slots = "aei".Select(c => Slot(atlas, c, 16)).ToArray();
        var pixels = atlas.Pixels(0);

        var right = 0;
        foreach (var slot in slots)
        {
            Assert.Equal(right, slot.X);
            right = slot.X + slot.Box.Width + 1;
            for (var y = slot.Y; y <= slot.Y + slot.Box.Height; y++)
            {
                Assert.Equal(0, pixels[(y * GlyphAtlas.PageSize) + slot.X + slot.Box.Width]);
            }

            for (var x = slot.X; x <= slot.X + slot.Box.Width; x++)
            {
                Assert.Equal(0, pixels[((slot.Y + slot.Box.Height) * GlyphAtlas.PageSize) + x]);
            }

            Assert.NotEqual(0, pixels[(slot.Y * GlyphAtlas.PageSize) + slot.X]);
        }

        var height = slots.Max(s => s.Box.Height) + 1;
        Assert.Equal([new AtlasRect(0, 0, right, height)], atlas.Dirty(0).ToArray());

        atlas.ClearDirty();
        engine.Prepare(list, 200, 200);
        Assert.True(atlas.Dirty(0).IsEmpty);
        Assert.Equal(3, fake.Rasterized.Count);
    }

    /// <summary>
    /// When a page is full, the shelf with the oldest newest use goes first; glyphs of the current <c>Draw</c> stay where
    /// they are; a glyph that went is rasterized again when it comes back (design §5.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An engine over the fake backend whose glyphs are 1,023 pixels square: cells of 1,024 with the empty pixel, so that
    ///   a page holds exactly two shelves of two.
    ///
    /// Steps:
    /// - Draw 1: "ab". Draw 2: "cd". Draw 3: "a" (the shelf of "ab" is now newer). Draw 4: "ce": the page is full.
    ///   Draw 5: "d". Draw 6: "a".
    ///
    /// Expected:
    /// - In Draw 4 the shelf of "cd" holds "c", used now, so it stays, though its newest use before was older; the shelf
    ///   of "ab" goes; "c" keeps its place and "e" takes the place of "a". Draw 5 finds "d" where it was, and Draw 6
    ///   rasterizes "a" again. One page throughout.
    /// </remarks>
    [Fact]
    public void Atlas_Full_EmptiesTheOldestShelfNotUsedNow()
    {
        var fake = new FakeTextBackend { Box = static (_, _) => new GlyphBox(0, -1023, 1023, 1023) };
        var engine = new TextEngine(fake);
        Draw(engine, "ab");
        Draw(engine, "cd");
        Draw(engine, "a");
        var c = Slot(engine.Atlas, 'c', 16);
        var a = Slot(engine.Atlas, 'a', 16);

        Draw(engine, "ce");

        Assert.Equal(1, engine.Atlas.PageCount);
        Assert.Equal((c.X, c.Y), (Slot(engine.Atlas, 'c', 16).X, Slot(engine.Atlas, 'c', 16).Y));
        Assert.Equal((a.X, a.Y), (Slot(engine.Atlas, 'e', 16).X, Slot(engine.Atlas, 'e', 16).Y));
        Assert.False(engine.Atlas.Contains(0, 'a', 16, 0));
        Assert.False(engine.Atlas.Contains(0, 'b', 16, 0));

        Draw(engine, "d");
        Assert.Equal(5, fake.Rasterized.Count);

        Draw(engine, "a");
        Assert.Equal(6, fake.Rasterized.Count);
        Assert.Equal('a', fake.Rasterized[^1].Glyph);
    }

    /// <summary>
    /// The shelf whose newest use is oldest goes first, not the one placed first (design §5.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Glyphs 1,023 pixels square, as above.
    ///
    /// Steps:
    /// - Draw 1: "ab". Draw 2: "cd". Draw 3: "a". Draw 4: "e".
    ///
    /// Expected:
    /// - "e" takes the shelf of "cd" (newest use 2), and "a" and "b" stay (newest use 3).
    /// </remarks>
    [Fact]
    public void Atlas_Full_GoesByTheNewestUseOfAShelf()
    {
        var fake = new FakeTextBackend { Box = static (_, _) => new GlyphBox(0, -1023, 1023, 1023) };
        var engine = new TextEngine(fake);
        Draw(engine, "ab");
        Draw(engine, "cd");
        Draw(engine, "a");
        var c = Slot(engine.Atlas, 'c', 16);

        Draw(engine, "e");

        Assert.Equal((c.X, c.Y), (Slot(engine.Atlas, 'e', 16).X, Slot(engine.Atlas, 'e', 16).Y));
        Assert.True(engine.Atlas.Contains(0, 'a', 16, 0));
        Assert.True(engine.Atlas.Contains(0, 'b', 16, 0));
        Assert.False(engine.Atlas.Contains(0, 'c', 16, 0));
        Assert.False(engine.Atlas.Contains(0, 'd', 16, 0));
    }

    /// <summary>
    /// A <c>Draw</c> whose own glyphs fill a page adds a page, up to 8; one that needs 9 throws and says so (design §5.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Glyphs 1,023 pixels square: exactly 4 to a page.
    ///
    /// Steps:
    /// - One Draw of 5 glyphs; then one Draw of 31 others; then one Draw of 33 others; then, on a new engine, one Draw of 41.
    ///
    /// Expected:
    /// - 2 pages, the first Draw in two steps (4 glyphs on page 0, 1 on page 1); then 8 pages (the second Draw's first glyph
    ///   joins the shelf of the first Draw's last one, which then stays, so 31 glyphs take the 32 cells); the third throws
    ///   InvalidOperationException naming 9 pages, and the atlas stays at 8; the 41 glyphs need 11 pages.
    /// </remarks>
    [Fact]
    public void Atlas_DrawFillsPages_AddsUpToEight()
    {
        var fake = new FakeTextBackend { Box = static (_, _) => new GlyphBox(0, -1023, 1023, 1023) };
        var engine = new TextEngine(fake);
        Draw(engine, "abcde");
        Assert.Equal(2, engine.Atlas.PageCount);
        Assert.Equal([(0, 4), (1, 1)], engine.Steps.ToArray().Select(step => (step.Page, step.Count)));

        Draw(engine, new string([.. Enumerable.Range(0, 31).Select(i => (char)('A' + i))]));
        Assert.Equal(8, engine.Atlas.PageCount);

        var full = Assert.Throws<InvalidOperationException>(() =>
            Draw(engine, new string([.. Enumerable.Range(0, 33).Select(i => (char)(0x400 + i))]))
        );
        Assert.Contains("needs 9 glyph atlas pages", full.Message, StringComparison.Ordinal);
        Assert.Equal(8, engine.Atlas.PageCount);

        var fresh = new TextEngine(new FakeTextBackend { Box = fake.Box });
        var more = Assert.Throws<InvalidOperationException>(() =>
            Draw(fresh, new string([.. Enumerable.Range(0, 41).Select(i => (char)(0x400 + i))]))
        );
        Assert.Contains("needs 11 glyph atlas pages", more.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A glyph whose box is a page's side or more cannot be placed and throws; one a pixel smaller fills a page (design §5.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A fake backend whose glyph box is 2,048 pixels wide; another whose box is 2,047 square.
    ///
    /// Steps:
    /// - Prepare one glyph with each.
    ///
    /// Expected:
    /// - InvalidOperationException and no page; then one page.
    /// </remarks>
    [Fact]
    public void Atlas_GlyphAsLargeAsAPage_Throws()
    {
        var engine = new TextEngine(new FakeTextBackend { Box = static (_, _) => new GlyphBox(0, -10, 2048, 10) });
        Assert.Throws<InvalidOperationException>(() => Draw(engine, "a"));
        Assert.Equal(0, engine.Atlas.PageCount);

        var largest = new TextEngine(new FakeTextBackend { Box = static (_, _) => new GlyphBox(0, -2047, 2047, 2047) });
        Draw(largest, "a");
        Assert.Equal(1, largest.Atlas.PageCount);
    }

    /// <summary>
    /// Warm, recording and preparing the same 100 labels allocates nothing (design §5.6, invariant 3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An engine over the fake backend; 100 labels of 2 to 6 characters, prepared once.
    ///
    /// Steps:
    /// - Clear the list, record the labels, and prepare, 10 times, counting the bytes this thread allocates.
    ///
    /// Expected:
    /// - 0 bytes.
    /// </remarks>
    [Fact]
    public void Prepare_HundredLabelsWarm_AllocatesNothing()
    {
        var engine = new TextEngine(new FakeTextBackend());
        var labels = Enumerable.Range(0, 100).Select(i => (i * 1.25).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var list = new RenderList();
        Record(list, labels);
        engine.Prepare(list, 640, 480);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < 10; frame++)
        {
            Record(list, labels);
            engine.Prepare(list, 640, 480);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        static void Record(RenderList list, string[] labels)
        {
            list.Clear();
            for (var i = 0; i < labels.Length; i++)
            {
                list.DrawText(labels[i], new Vector2(20 + ((i % 10) * 60), 30 + ((i / 10) * 40)), Style);
            }
        }
    }

    /// <summary>
    /// A glyph outside the clip within the target is not recorded, but placed; one more than a page from the clip, or
    /// under an empty clip, is not even placed (design §5.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An engine over the fake backend; a 100 × 200 target.
    ///
    /// Steps:
    /// - In a clip 40 pixels wide, prepare "abcdefgh" from x 10 (an advance of 9), and "z" 5,000 pixels to the right; then
    ///   "y" under an empty clip.
    ///
    /// Expected:
    /// - The records are the glyphs whose box starts left of 40, in order; every glyph of "abcdefgh" is in the atlas; "z"
    ///   and "y" are not.
    /// </remarks>
    [Fact]
    public void Culling_OutsideTheClip_RecordsAndPlacesNothing()
    {
        var engine = new TextEngine(new FakeTextBackend());
        var list = new RenderList();
        list.SetClip(0, 0, 40, 200);
        list.DrawText("abcdefgh", new Vector2(10, 50), Style);
        list.DrawText("z", new Vector2(5010, 50), Style);
        list.SetClip(0, 0, 0, 0);
        list.DrawText("y", new Vector2(10, 50), Style);

        engine.Prepare(list, 100, 200);

        var inside = "abcdefgh".Select((c, k) => 10 + (9 * k) + FakeTextBackend.DefaultBox(c, 16, 0).Left).Where(left => left < 40).ToArray();
        Assert.Equal(inside, engine.Instances.ToArray().Select(i => i.Left));
        Assert.All("abcdefgh", c => Assert.True(engine.Atlas.Contains(0, c, 16, 0)));
        Assert.False(engine.Atlas.Contains(0, 'z', 16, 0));
        Assert.False(engine.Atlas.Contains(0, 'y', 16, 0));
    }

    /// <summary>
    /// A glyph goes on the lowest shelf in use that has room and is at least its height but not more than a quarter (or 2
    /// pixels) taller; else on a new shelf under the last (design §5.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Glyphs, as cells with the empty pixel: "W" 2,001 × 110, "a" 51 × 100, "f" 30 × 95, "b" 51 × 20, "d" 51 × 102.
    ///
    /// Steps:
    /// - One Draw of "Wafbd".
    ///
    /// Expected:
    /// - "W" at (0, 0); "a" at (0, 110) on a new shelf, since the shelf of "W" has 47 pixels left; "f" at (51, 110), on
    ///   the lower of the two shelves it fits; "b" at (0, 210), as both shelves are far taller; "d" at (0, 230), as the
    ///   shelf of "a" is shorter and the shelf of "W" too full.
    /// </remarks>
    [Fact]
    public void Atlas_Shelves_HoldGlyphsOfNearHeight()
    {
        var engine = Sized(('W', 2000, 109), ('a', 50, 99), ('f', 29, 94), ('b', 50, 19), ('d', 50, 101));

        Draw(engine, "Wafbd");

        Assert.Equal(
            [(0, 0), (0, 110), (51, 110), (0, 210), (0, 230)],
            "Wafbd".Select(c => (Slot(engine.Atlas, c, 16).X, Slot(engine.Atlas, c, 16).Y))
        );
    }

    /// <summary>
    /// An emptied shelf is cut to the height of the cell put on it, and the rest stays an empty shelf below; a glyph put
    /// where another was clears the empty pixels to its right and below (design §5.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Glyphs "T", "U", "V", "X" of 1,023 square (cells of 1,024); "s" of 1,000 × 511, "t" and "u" of 1,023 × 511.
    ///
    /// Steps:
    /// - Draw 1 "TU", Draw 2 "VX" (the page is full), Draw 3 "stu".
    ///
    /// Expected:
    /// - The shelf of "TU" goes; "s" at (0, 0) and "t" at (1,001, 0) on it, cut to 512; "u" at (0, 512) on the rest. One
    ///   page. Where "T" was, the column right of "s" and the row below it hold 0.
    /// </remarks>
    [Fact]
    public void Atlas_EmptyShelf_IsCutToTheCell()
    {
        var engine = Sized(
            ('T', 1023, 1023),
            ('U', 1023, 1023),
            ('V', 1023, 1023),
            ('X', 1023, 1023),
            ('s', 1000, 511),
            ('t', 1023, 511),
            ('u', 1023, 511)
        );
        Draw(engine, "TU");
        Draw(engine, "VX");

        Draw(engine, "stu");

        Assert.Equal([(0, 0), (1001, 0), (0, 512)], "stu".Select(c => (Slot(engine.Atlas, c, 16).X, Slot(engine.Atlas, c, 16).Y)));
        Assert.Equal(1, engine.Atlas.PageCount);
        var pixels = engine.Atlas.Pixels(0);
        for (var y = 0; y <= 511; y++)
        {
            Assert.Equal(0, pixels[(y * GlyphAtlas.PageSize) + 1000]);
        }

        Assert.True(pixels.Slice(511 * GlyphAtlas.PageSize, 1001).IndexOfAnyExcept((byte)0) < 0);
    }

    /// <summary>
    /// Shelves emptied next to each other join, so that a taller glyph fits where two short shelves were (design §5.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Glyphs "a" to "h" of 1,023 × 511 (cells of 1,024 × 512: a page holds four shelves of two); "T" of 1,023 square.
    ///
    /// Steps:
    /// - Draws "ab", "cd", "ef", "gh"; then "T".
    ///
    /// Expected:
    /// - The shelves of "ab" and "cd" go and join; "T" at (0, 0) on page 0; "e" to "h" stay.
    /// </remarks>
    [Fact]
    public void Atlas_EmptiedShelvesSideBySide_Join()
    {
        var engine = Sized(
            ('a', 1023, 511),
            ('b', 1023, 511),
            ('c', 1023, 511),
            ('d', 1023, 511),
            ('e', 1023, 511),
            ('f', 1023, 511),
            ('g', 1023, 511),
            ('h', 1023, 511),
            ('T', 1023, 1023)
        );
        foreach (var draw in new[] { "ab", "cd", "ef", "gh" })
        {
            Draw(engine, draw);
        }

        Draw(engine, "T");

        var t = Slot(engine.Atlas, 'T', 16);
        Assert.Equal((0, 0, 0), (t.Page, t.X, t.Y));
        Assert.Equal(1, engine.Atlas.PageCount);
        Assert.All("efgh", c => Assert.True(engine.Atlas.Contains(0, c, 16, 0)));
        Assert.False(engine.Atlas.Contains(0, 'a', 16, 0));
    }

    /// <summary>
    /// The last shelf of a page, emptied, gives its room back to the free part below, so that a glyph taller than it fits
    /// (design §5.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Glyphs "a" to "d" of 1,023 × 511 (cells 512 tall); "L" of 1,023 × 1,535 (a cell 1,536 tall).
    ///
    /// Steps:
    /// - Draws "ab" and "cd" (1,024 pixels of the page left free below); then "aL".
    ///
    /// Expected:
    /// - The shelf of "cd" goes, and "L" goes to (0, 512) on page 0.
    /// </remarks>
    [Fact]
    public void Atlas_LastShelfEmptied_GivesItsRoomBack()
    {
        var engine = Sized(('a', 1023, 511), ('b', 1023, 511), ('c', 1023, 511), ('d', 1023, 511), ('L', 1023, 1535));
        Draw(engine, "ab");
        Draw(engine, "cd");

        Draw(engine, "aL");

        var l = Slot(engine.Atlas, 'L', 16);
        Assert.Equal((0, 0, 512), (l.Page, l.X, l.Y));
        Assert.Equal(1, engine.Atlas.PageCount);
    }

    private static TextEngine Sized(params (char Glyph, int Width, int Height)[] glyphs)
    {
        var boxes = glyphs.ToDictionary(g => g.Glyph, g => new GlyphBox(0, -g.Height, g.Width, g.Height));
        return new TextEngine(new FakeTextBackend { Box = (glyph, _) => boxes[(char)glyph] });
    }

    private static void Draw(TextEngine engine, string text)
    {
        var list = new RenderList();
        list.DrawText(text, new Vector2(0, 100), Style);
        engine.Prepare(list, 100, 200);
    }

    private static GlyphSlot Slot(GlyphAtlas atlas, char glyph, float size)
    {
        Assert.True(atlas.TryFind(0, glyph, size, 0, out var slot));
        return slot;
    }

    /// <summary>The records of one text, from design §5.6 written out here: the reference the engine is held to.</summary>
    private static List<(int Left, int Top, int Width, int Height, uint Rotation)> Expected(
        FakeTextBackend fake,
        TextEngine engine,
        string text,
        Vector2 position,
        TextStyle style,
        HashSet<int> phases
    )
    {
        var shaped = engine.Shape(text, style);
        var m = shaped.Metrics;
        var firstBaseline = style.Vertical switch
        {
            TextBaseline.Top => m.Ascent,
            TextBaseline.Middle => m.Ascent - (m.Height / 2),
            TextBaseline.Bottom => m.Ascent - m.Height,
            _ => 0f,
        };
        var records = new List<(int, int, int, int, uint)>();
        foreach (var line in shaped.Lines)
        {
            var u = style.Horizontal switch
            {
                TextAlign.Center => (m.Width - line.Width) / 2,
                TextAlign.Right => m.Width - line.Width,
                _ => 0f,
            };
            var v = firstBaseline + line.Baseline;

            // Clockwise: the text's +u is the screen's +y, its +v the screen's −x. Counterclockwise: the opposite.
            var (dx, dy) = style.Rotation switch
            {
                TextRotation.Clockwise90 => (-v, u),
                TextRotation.CounterClockwise90 => (v, -u),
                _ => (u, v),
            };
            var lineX = (int)Math.Floor(position.X + dx + 0.5f);
            var lineY = (int)Math.Floor(position.Y + dy + 0.5f);
            foreach (var glyph in shaped.Glyphs.Slice(line.FirstGlyph, line.GlyphCount))
            {
                var quarter = (int)Math.Floor((4 * glyph.Position.X) + 0.5f);
                var whole = (int)Math.Floor(quarter / 4.0);
                var phase = quarter - (4 * whole);
                var across = (int)Math.Floor(glyph.Position.Y - line.Baseline + 0.5f);
                phases.Add(phase);
                var box = fake.MeasureGlyph(glyph.Face, glyph.Glyph, style.Size, phase / 4f);
                if (box.Width == 0)
                {
                    continue;
                }

                records.Add(
                    style.Rotation switch
                    {
                        TextRotation.Clockwise90 => (lineX - across - box.Top - box.Height, lineY + whole + box.Left, box.Height, box.Width, 1u),
                        TextRotation.CounterClockwise90 => (
                            lineX + across + box.Top,
                            lineY - whole - box.Left - box.Width,
                            box.Height,
                            box.Width,
                            2u
                        ),
                        _ => (lineX + whole + box.Left, lineY + across + box.Top, box.Width, box.Height, 0u),
                    }
                );
            }
        }

        return records;
    }

    /// <summary>
    /// Rectangles that follow one another under one clip are one step, and a polyline, a text, or another clip starts a new
    /// one (design §5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A list: two rectangles, a polyline, a rectangle, a clip, two rectangles, a text, a rectangle.
    ///
    /// Steps:
    /// - Prepare it.
    ///
    /// Expected:
    /// - Steps of rectangles (0, 2), a polyline, (2, 1), (3, 2) under the clip, glyphs, (5, 1).
    /// </remarks>
    [Fact]
    public void Prepare_RectsJoinUnderOneClip()
    {
        var engine = new TextEngine(new FakeTextBackend());
        var list = new RenderList();
        var black = new Color(0, 0, 0, 255);
        list.DrawRect(0, 0, 2, 2, black);
        list.DrawRect(3, 0, 2, 2, black);
        list.DrawPolyline([new(0, 0), new(5, 5)], new LineStyle(black, 1));
        list.DrawRect(6, 0, 2, 2, black);
        list.SetClip(0, 0, 50, 50);
        list.DrawRect(9, 0, 2, 2, black);
        list.DrawRect(12, 0, 2, 2, black);
        list.DrawText("A", new System.Numerics.Vector2(20, 20), new TextStyle(black, 12));
        list.DrawRect(15, 0, 2, 2, black);
        engine.Prepare(list, 100, 100);

        var steps = engine.Steps.ToArray();
        Assert.Equal(
            [
                (DrawStepKind.Rects, 0, 2),
                (DrawStepKind.Polyline, 0, 1),
                (DrawStepKind.Rects, 2, 1),
                (DrawStepKind.Rects, 3, 2),
                (DrawStepKind.Glyphs, 0, 1),
                (DrawStepKind.Rects, 5, 1),
            ],
            steps.Select(s => (s.Kind, s.Index, s.Count)).ToArray()
        );
        Assert.Equal(ClipRect.All, steps[2].Clip);
        Assert.Equal(new ClipRect(0, 0, 50, 50), steps[3].Clip);
    }
}
