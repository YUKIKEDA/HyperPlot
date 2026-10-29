using System.Runtime.InteropServices;

namespace HyperPlot.Rendering;

/// <summary>
/// The glyph atlas of one <see cref="TextEngine"/> (design §5.6): pages of 2,048 × 2,048 bytes of coverage on the CPU,
/// which the backend copies into textures of the same size. Glyphs sit on shelves, with 1 empty pixel to their right and
/// below. The unit of use is one <c>Draw</c>: a shelf remembers the newest <c>Draw</c> that used one of its glyphs.
/// </summary>
/// <remarks>
/// When a glyph does not fit, the atlas empties the shelf used longest ago among those the current <c>Draw</c> has not
/// used, and tries again; a glyph the current <c>Draw</c> placed never moves, since the records written before it name its
/// place. When no such shelf is left, it adds a page, up to <see cref="MaxPages"/>. The thread that draws owns it.
/// </remarks>
internal sealed class GlyphAtlas
{
    /// <summary>The side of a page in pixels.</summary>
    internal const int PageSize = 2048;

    /// <summary>The most pages: 32 MB on the CPU and as much on the GPU.</summary>
    internal const int MaxPages = 8;

    private readonly ITextBackend _backend;
    private readonly List<Page> _pages = [];
    private readonly Dictionary<GlyphKey, GlyphSlot> _glyphs = [];
    private byte[] _scratch = new byte[1024];
    private long _draw;

    internal GlyphAtlas(ITextBackend backend) => _backend = backend;

    /// <summary>Gets the number of pages.</summary>
    internal int PageCount => _pages.Count;

    /// <summary>Gets the number of the current <c>Draw</c>.</summary>
    internal long DrawNumber => _draw;

    /// <summary>Gets the number of glyphs rasterized since the atlas was made, for the tests of eviction.</summary>
    internal long Rasterized { get; private set; }

    /// <summary>Starts a <c>Draw</c>: the glyphs it uses from now on are the ones that do not move.</summary>
    internal void BeginDraw() => _draw++;

    /// <summary>Gets a page's coverage, rows of <see cref="PageSize"/> bytes, top first.</summary>
    internal ReadOnlySpan<byte> Pixels(int page) => _pages[page].Pixels;

    /// <summary>Gets the rectangles of a page written since <see cref="ClearDirty"/>, in the order they were written.</summary>
    internal ReadOnlySpan<AtlasRect> Dirty(int page) => CollectionsMarshal.AsSpan(_pages[page].Dirty);

    /// <summary>Forgets the written rectangles of every page, once the backend has sent them.</summary>
    internal void ClearDirty()
    {
        foreach (var page in CollectionsMarshal.AsSpan(_pages))
        {
            page.Dirty.Clear();
        }
    }

    /// <summary>
    /// Finds a glyph, placing and rasterizing it if it is not there, and marks it used by the current <c>Draw</c>.
    /// </summary>
    /// <returns>False when the glyph needs room that only glyphs of the current <c>Draw</c> hold, on every page there can be.</returns>
    /// <exception cref="InvalidOperationException">The glyph's box is larger than a page.</exception>
    internal bool TryGet(int face, ushort glyph, float size, int phase, out GlyphSlot slot)
    {
        var key = new GlyphKey(face, glyph, size, (byte)phase);
        ref var found = ref CollectionsMarshal.GetValueRefOrNullRef(_glyphs, key);
        if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref found))
        {
            if (found.Shelf is { } shelf)
            {
                shelf.LastUsed = _draw;
            }

            slot = found;
            return true;
        }

        return TryAdd(key, out slot);
    }

    /// <summary>Gets the box of a glyph without placing it: from the atlas when it is there, else from the backend.</summary>
    internal GlyphBox Measure(int face, ushort glyph, float size, int phase)
    {
        var key = new GlyphKey(face, glyph, size, (byte)phase);
        return _glyphs.TryGetValue(key, out var slot) ? slot.Box : _backend.MeasureGlyph(face, glyph, size, phase / 4f);
    }

    /// <summary>Returns whether a glyph is in the atlas, placed or known to be empty.</summary>
    internal bool Contains(int face, ushort glyph, float size, int phase) => _glyphs.ContainsKey(new GlyphKey(face, glyph, size, (byte)phase));

    /// <summary>Finds a glyph without placing it or marking it used: for the tests of eviction.</summary>
    internal bool TryFind(int face, ushort glyph, float size, int phase, out GlyphSlot slot) =>
        _glyphs.TryGetValue(new GlyphKey(face, glyph, size, (byte)phase), out slot);

    /// <summary>Gets the boxes of the glyphs on shelves the current <c>Draw</c> used, padded, for counting the pages it needs.</summary>
    internal void AddUsedCells(List<(int Width, int Height)> cells)
    {
        foreach (var (_, slot) in _glyphs)
        {
            if (slot.Shelf is { } shelf && shelf.LastUsed == _draw)
            {
                cells.Add((slot.Box.Width + 1, slot.Box.Height + 1));
            }
        }
    }

    /// <summary>
    /// Counts the pages that cells need when shelf-packed from empty pages, the cells sorted by height, tallest first.
    /// </summary>
    internal static int CountPages(List<(int Width, int Height)> cells)
    {
        cells.Sort(static (a, b) => b.Height.CompareTo(a.Height));
        var pages = 0;
        var bottom = PageSize;
        var x = PageSize;
        foreach (var (width, height) in cells)
        {
            if (x + width > PageSize)
            {
                // A new shelf under the last one, or on a new page.
                if (bottom + height > PageSize)
                {
                    pages++;
                    bottom = 0;
                }

                bottom += height;
                x = 0;
            }

            x += width;
        }

        return pages;
    }

    private static bool Fits(Shelf shelf, int width, int height) =>
        shelf.Glyphs.Count > 0 && shelf.Height >= height && shelf.Height - height <= Math.Max(2, height / 4) && shelf.X + width <= PageSize;

    private bool TryAdd(GlyphKey key, out GlyphSlot slot)
    {
        var box = _backend.MeasureGlyph(key.Face, key.Glyph, key.Size, key.Phase / 4f);
        if (box.Width <= 0 || box.Height <= 0)
        {
            // No ink (a space): known, so that the backend is not asked again, and never drawn.
            slot = new GlyphSlot(box with { Width = 0, Height = 0 }, -1, 0, 0, null);
            _glyphs[key] = slot;
            return true;
        }

        if (box.Width >= PageSize || box.Height >= PageSize)
        {
            throw new InvalidOperationException(
                $"A glyph of {box.Width} × {box.Height} pixels does not fit an atlas page of {PageSize} × {PageSize}."
            );
        }

        // The cell holds the glyph and the empty pixel to its right and below.
        var width = box.Width + 1;
        var height = box.Height + 1;
        if (!TryPlace(width, height, out var page, out var shelf, out var x))
        {
            slot = default;
            return false;
        }

        var pixels = page.Pixels;
        var area = box.Width * box.Height;
        if (_scratch.Length < area)
        {
            _scratch = new byte[Math.Max(area, _scratch.Length * 2)];
        }

        var coverage = _scratch.AsSpan(0, area);
        _backend.RasterizeGlyph(key.Face, key.Glyph, key.Size, key.Phase / 4f, coverage);
        Rasterized++;
        for (var row = 0; row < box.Height; row++)
        {
            var at = ((shelf.Y + row) * PageSize) + x;
            coverage.Slice(row * box.Width, box.Width).CopyTo(pixels.AsSpan(at));
            pixels[at + box.Width] = 0;
        }

        pixels.AsSpan(((shelf.Y + box.Height) * PageSize) + x, width).Clear();
        AddDirty(page, new AtlasRect(x, shelf.Y, width, height));
        shelf.Glyphs.Add(key);
        shelf.LastUsed = _draw;
        slot = new GlyphSlot(box, page.Index, x, shelf.Y, shelf);
        _glyphs[key] = slot;
        return true;
    }

    private static void AddDirty(Page page, AtlasRect rect)
    {
        // A glyph written right after the last one on the same shelf widens its rectangle.
        var dirty = page.Dirty;
        if (dirty.Count > 0 && dirty[^1] is var last && last.Y == rect.Y && last.X + last.Width == rect.X)
        {
            dirty[^1] = last with { Width = last.Width + rect.Width, Height = Math.Max(last.Height, rect.Height) };
            return;
        }

        dirty.Add(rect);
    }

    private bool TryPlace(int width, int height, out Page page, out Shelf shelf, out int x)
    {
        while (true)
        {
            if (TryFit(width, height, out page, out shelf))
            {
                x = shelf.X;
                shelf.X += width;
                return true;
            }

            if (TryEvictOne())
            {
                continue;
            }

            if (_pages.Count < MaxPages)
            {
                _pages.Add(new Page(_pages.Count));
                continue;
            }

            x = 0;
            return false;
        }
    }

    /// <summary>
    /// Finds room: a shelf in use of about the cell's height, then the lowest empty shelf tall enough (cut to the cell's
    /// height), then a new shelf under the last one of a page.
    /// </summary>
    private bool TryFit(int width, int height, out Page page, out Shelf shelf)
    {
        Shelf? best = null;
        Page? bestPage = null;
        foreach (var candidatePage in CollectionsMarshal.AsSpan(_pages))
        {
            foreach (var candidate in CollectionsMarshal.AsSpan(candidatePage.Shelves))
            {
                if (Fits(candidate, width, height) && (best is null || candidate.Height < best.Height))
                {
                    (best, bestPage) = (candidate, candidatePage);
                }
            }
        }

        if (best is null)
        {
            foreach (var candidatePage in CollectionsMarshal.AsSpan(_pages))
            {
                foreach (var candidate in CollectionsMarshal.AsSpan(candidatePage.Shelves))
                {
                    if (candidate.Glyphs.Count == 0 && candidate.Height >= height && (best is null || candidate.Height < best.Height))
                    {
                        (best, bestPage) = (candidate, candidatePage);
                    }
                }
            }

            if (best is not null && best.Height > height)
            {
                // The rest of the empty shelf stays empty below the part this cell takes.
                var shelves = bestPage!.Shelves;
                shelves.Insert(shelves.IndexOf(best) + 1, new Shelf(best.Y + height, best.Height - height));
                best.Height = height;
            }
        }

        if (best is null)
        {
            foreach (var candidatePage in CollectionsMarshal.AsSpan(_pages))
            {
                var shelves = candidatePage.Shelves;
                var bottom = shelves.Count == 0 ? 0 : shelves[^1].Y + shelves[^1].Height;
                if (PageSize - bottom >= height)
                {
                    best = new Shelf(bottom, height);
                    shelves.Add(best);
                    bestPage = candidatePage;
                    break;
                }
            }
        }

        page = bestPage!;
        shelf = best!;
        return best is not null;
    }

    /// <summary>
    /// Empties the shelf whose newest use is oldest, among those the current <c>Draw</c> has not used; joins it with the
    /// empty shelves beside it, and gives the page's bottom back when it is the last shelf.
    /// </summary>
    private bool TryEvictOne()
    {
        Shelf? oldest = null;
        Page? oldestPage = null;
        foreach (var page in CollectionsMarshal.AsSpan(_pages))
        {
            foreach (var shelf in CollectionsMarshal.AsSpan(page.Shelves))
            {
                if (shelf.Glyphs.Count > 0 && shelf.LastUsed < _draw && (oldest is null || shelf.LastUsed < oldest.LastUsed))
                {
                    (oldest, oldestPage) = (shelf, page);
                }
            }
        }

        if (oldest is null)
        {
            return false;
        }

        foreach (var key in CollectionsMarshal.AsSpan(oldest.Glyphs))
        {
            _glyphs.Remove(key);
        }

        oldest.Glyphs.Clear();
        oldest.X = 0;
        var shelves = oldestPage!.Shelves;
        for (var i = shelves.Count - 1; i > 0; i--)
        {
            if (shelves[i].Glyphs.Count == 0 && shelves[i - 1].Glyphs.Count == 0)
            {
                shelves[i - 1].Height += shelves[i].Height;
                shelves.RemoveAt(i);
            }
        }

        if (shelves[^1].Glyphs.Count == 0)
        {
            shelves.RemoveAt(shelves.Count - 1);
        }

        return true;
    }

    /// <summary>One page: its coverage, its shelves from the top, and the rectangles written since the backend last read them.</summary>
    private sealed class Page(int index)
    {
        internal int Index { get; } = index;

        internal byte[] Pixels { get; } = new byte[PageSize * PageSize];

        internal List<Shelf> Shelves { get; } = [];

        internal List<AtlasRect> Dirty { get; } = [];
    }
}

/// <summary>The key of a glyph in the atlas: its face, its number, the em size, and the quarter-pixel phase from 0 to 3.</summary>
internal readonly record struct GlyphKey(int Face, ushort Glyph, float Size, byte Phase);

/// <summary>Where a glyph is: its box, its page (−1 for a glyph with no ink), the top left of its coverage, and its shelf.</summary>
internal readonly record struct GlyphSlot(GlyphBox Box, int Page, int X, int Y, Shelf? Shelf);

/// <summary>A rectangle of an atlas page, in pixels.</summary>
internal readonly record struct AtlasRect(int X, int Y, int Width, int Height);

/// <summary>A row of an atlas page: its top, its height, the left of its free part, its glyphs, and their newest use.</summary>
internal sealed class Shelf(int y, int height)
{
    internal int Y { get; } = y;

    internal int Height { get; set; } = height;

    internal int X { get; set; }

    internal List<GlyphKey> Glyphs { get; } = [];

    internal long LastUsed { get; set; }
}
