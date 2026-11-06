using System.Numerics;
using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>
/// Text from characters (design §5.1, §5.6, row I1c): the span <c>DrawText</c>, the shaping cache looked up by characters
/// and reusing its oldest entry, and a backend that shapes into a reused <see cref="ShapedText"/>.
/// </summary>
public sealed class SpanTextTests
{
    private static readonly TextStyle Style = new(new Color(10, 20, 30, 255), 14);

    /// <summary>
    /// A span records what the same string records, with the same checks; an empty span draws nothing (design §5.1, row I1c).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two lists.
    ///
    /// Steps:
    /// - Record "Label 1" from a string in one and from a span of a reused buffer in the other, then overwrite the buffer;
    ///   record an empty span; record spans with a position that is not finite, a size of 0, a null font, and an empty locale.
    ///
    /// Expected:
    /// - Equal records with the characters "Label 1"; no record for the empty span; ArgumentOutOfRangeException twice,
    ///   ArgumentNullException, and ArgumentException, as for strings.
    /// </remarks>
    [Fact]
    public void DrawText_Span_RecordsAsTheString()
    {
        var fromString = new RenderList();
        var fromSpan = new RenderList();
        fromString.DrawText("Label 1", new Vector2(3, 4), Style);
        var buffer = "Label 1".ToCharArray();
        fromSpan.DrawText(buffer.AsSpan(), new Vector2(3, 4), Style);
        buffer.AsSpan().Fill('x');
        fromSpan.DrawText(ReadOnlySpan<char>.Empty, new Vector2(3, 4), Style);

        Assert.Equal(fromString.Texts.ToArray(), fromSpan.Texts.ToArray());
        Assert.Equal("Label 1", Assert.Single(fromSpan.Texts.ToArray()).Text);
        Assert.Equal(fromString.Commands.ToArray(), fromSpan.Commands.ToArray());

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => fromSpan.DrawText("a".AsSpan(), new Vector2(float.NaN, 0), Style));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => fromSpan.DrawText("a".AsSpan(), Vector2.Zero, Style with { Size = 0 }));
        _ = Assert.Throws<ArgumentNullException>(() => fromSpan.DrawText("a".AsSpan(), Vector2.Zero, Style with { FontFamily = null! }));
        _ = Assert.Throws<ArgumentException>(() => fromSpan.DrawText("a".AsSpan(), Vector2.Zero, Style with { Locale = string.Empty }));
    }

    /// <summary>
    /// The list's buffer grows when a text does not fit and keeps the texts recorded before; after Clear the same texts
    /// record the same (design §5.1, row I1c).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A list.
    ///
    /// Steps:
    /// - Record 40 texts of 10 characters each ("t00 ......" to "t39 ......"), 400 characters, more than the buffer's first
    ///   256; read them; Clear and record them again.
    ///
    /// Expected:
    /// - Each record holds its own text, before and after the Clear, and the records of the two passes are equal.
    /// </remarks>
    [Fact]
    public void DrawText_BufferGrows_KeepsEachText()
    {
        var list = new RenderList();
        var texts = Enumerable.Range(0, 40).Select(i => $"t{i:00} ......").ToArray();
        TextRecord[] Record()
        {
            foreach (var text in texts)
            {
                list.DrawText(text.AsSpan(), Vector2.One, Style);
            }

            return list.Texts.ToArray();
        }

        var first = Record();
        Assert.Equal(texts, first.Select(t => t.Text));
        list.Clear();
        var second = Record();
        Assert.Equal(texts, second.Select(t => t.Text));
        Assert.Equal(first, second);
    }

    /// <summary>
    /// The shaping cache is looked up by characters: a string and a span of the same characters are one entry, and a
    /// different style is another (design §5.6, row I1c).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An engine over the fake backend.
    ///
    /// Steps:
    /// - Measure "123" as a string, as a span of a buffer, as a slice of "x123y", and in bold.
    ///
    /// Expected:
    /// - Two calls of the backend: one for the three of the same style, one for bold; the same metrics for the three.
    /// </remarks>
    [Fact]
    public void Cache_SameCharacters_OneEntry()
    {
        var backend = new FakeTextBackend();
        var engine = new TextEngine(backend);
        var a = engine.Measure("123", Style);
        var b = engine.Measure("123".ToCharArray().AsSpan(), Style);
        var c = engine.Measure("x123y".AsSpan(1, 3), Style);
        _ = engine.Measure("123", Style with { Weight = FontWeight.Bold });

        Assert.Equal(2, backend.ShapeCount);
        Assert.Equal((a, a), (b, c));
    }

    /// <summary>
    /// A full cache drops the text used longest ago and gives its entry to the new one, with no allocation (design §5.6,
    /// row I1c).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An engine over the fake backend, recording no calls.
    ///
    /// Steps:
    /// - Measure 8,192 texts "0" to "8191"; measure "0" again (the newest now); measure 100 new texts "a0" to "a99", counting
    ///   the bytes; measure "0", then "1".
    ///
    /// Expected:
    /// - 8,192 entries all along; 0 bytes for the 100 new texts; "0" is still kept (no backend call) and "1", dropped first,
    ///   is shaped again.
    /// </remarks>
    [Fact]
    public void Cache_Full_ReusesTheOldestEntry()
    {
        var backend = new FakeTextBackend { RecordShapes = false };
        var engine = new TextEngine(backend);
        Span<char> buffer = stackalloc char[8];
        for (var i = 0; i < TextEngine.CacheCapacity; i++)
        {
            _ = i.TryFormat(buffer, out var written, default, System.Globalization.CultureInfo.InvariantCulture);
            _ = engine.Measure(buffer[..written], Style);
        }

        _ = engine.Measure("0", Style);
        buffer[0] = 'a';
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            _ = i.TryFormat(buffer[1..], out var written, default, System.Globalization.CultureInfo.InvariantCulture);
            _ = engine.Measure(buffer[..(written + 1)], Style);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(TextEngine.CacheCapacity, engine.CacheCount);
        var calls = backend.ShapeCount;
        _ = engine.Measure("0", Style);
        Assert.Equal(calls, backend.ShapeCount);
        _ = engine.Measure("1", Style);
        Assert.Equal(calls + 1, backend.ShapeCount);
    }

    /// <summary>
    /// A shaped text that is reused holds only the last text: its glyphs, lines, and size (design §5.6, row I1c).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The fake backend; one empty shaped text.
    ///
    /// Steps:
    /// - Shape "long text\nsecond" into it, then "ab".
    ///
    /// Expected:
    /// - The same glyphs, lines, and metrics as "ab" shaped into a new one: 2 glyphs and 1 line.
    /// </remarks>
    [Fact]
    public void ShapedText_Reused_HoldsOnlyTheLastText()
    {
        var backend = new FakeTextBackend();
        var reused = new ShapedText();
        backend.Shape("long text\nsecond", "Segoe UI", 14, FontWeight.Normal, "en-us", reused);
        backend.Shape("ab", "Segoe UI", 14, FontWeight.Normal, "en-us", reused);
        var fresh = backend.ShapeNew("ab", "Segoe UI", 14, FontWeight.Normal, "en-us");

        Assert.Equal(2, reused.Glyphs.Length);
        Assert.Equal(fresh.Glyphs.ToArray(), reused.Glyphs.ToArray());
        Assert.Equal(fresh.Lines.ToArray(), reused.Lines.ToArray());
        Assert.Equal(fresh.Metrics, reused.Metrics);
    }
}
