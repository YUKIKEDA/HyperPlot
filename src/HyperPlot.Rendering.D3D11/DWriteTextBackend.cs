using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace HyperPlot.Rendering.D3D11;

/// <summary>
/// Text through DirectWrite (design §5.6): an <see cref="IDWriteTextLayout"/> with no wrapping shapes, a renderer of our own
/// collects its glyph runs, and a glyph run analysis rasterizes one glyph in grayscale, one byte a pixel.
/// </summary>
/// <remarks>
/// The factory is DirectWrite's shared one, which needs no COM initialization. The thread that draws calls it, one call at a
/// time: a text format "may not be thread-safe", and the renderer's callbacks run on the calling thread inside
/// <c>IDWriteTextLayout::Draw</c>.
/// </remarks>
internal sealed unsafe class DWriteTextBackend : ITextBackend, IDisposable
{
    // Wide enough that no line wraps; a layout of this width has no other cost.
    private const float LayoutExtent = 1e7f;

    private static readonly void** _vtable = MakeVtable();

    private readonly Dictionary<nint, int> _faceNumbers = [];
    private readonly List<nint> _faces = [];
    private readonly Collector _collector = new();
    private IDWriteFactory3* _factory;
    private TextRenderer* _renderer;
    private GCHandle _collectorHandle;
    private IDWriteGlyphRunAnalysis* _analysis;
    private AnalysisKey _analyzed;
    private bool _disposed;

    internal DWriteTextBackend()
    {
        IDWriteFactory3* factory;
        Check(
            DirectX.DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED, __uuidof<IDWriteFactory3>(), (IUnknown**)&factory),
            "DWriteCreateFactory"
        );
        _factory = factory;
        _renderer = (TextRenderer*)NativeMemory.AllocZeroed((nuint)sizeof(TextRenderer));
        _renderer->Vtable = _vtable;
        _collectorHandle = GCHandle.Alloc(_collector);
    }

    /// <summary>Gets or sets a test hook that the renderer's <c>DrawGlyphRun</c> calls first, to make the callback throw.</summary>
    internal Action? GlyphRunHook
    {
        get => _collector.Hook;
        set => _collector.Hook = value;
    }

    /// <summary>Gets the number of faces numbered so far.</summary>
    internal int FaceCount => _faces.Count;

    /// <inheritdoc />
    public ShapedText Shape(string text, string fontFamily, float size, FontWeight weight, string locale)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var layout = MakeLayout(text, fontFamily, size, weight, locale);
        try
        {
            uint lineCount;
            _ = layout->GetLineMetrics(null, 0, &lineCount);
            var lines = new DWRITE_LINE_METRICS[lineCount];
            fixed (DWRITE_LINE_METRICS* metrics = lines)
            {
                Check(layout->GetLineMetrics(metrics, lineCount, &lineCount), "IDWriteTextLayout::GetLineMetrics");
            }

            _collector.Clear();
            Check(
                layout->Draw((void*)GCHandle.ToIntPtr(_collectorHandle), (IDWriteTextRenderer*)_renderer, 0, 0),
                "IDWriteTextLayout::Draw",
                _collector
            );
            return Build(lines);
        }
        finally
        {
            _ = layout->Release();
        }
    }

    /// <summary>
    /// Gets the widths <c>IDWriteTextLayout::GetMetrics</c> gives a one-line text, with and without its trailing white
    /// space: what the tests compare the shaped advances with (design §10).
    /// </summary>
    internal (float Width, float WithTrailingWhitespace) LayoutWidths(string text, string fontFamily, float size)
    {
        var layout = MakeLayout(text, fontFamily, size, FontWeight.Normal, "en-us");
        try
        {
            DWRITE_TEXT_METRICS metrics;
            Check(layout->GetMetrics(&metrics), "IDWriteTextLayout::GetMetrics");
            return (metrics.width, metrics.widthIncludingTrailingWhitespace);
        }
        finally
        {
            _ = layout->Release();
        }
    }

    /// <inheritdoc />
    public GlyphBox MeasureGlyph(int face, ushort glyph, float size, float phase)
    {
        var bounds = Analyze(face, glyph, size, phase);
        return new GlyphBox(bounds.left, bounds.top, bounds.right - bounds.left, bounds.bottom - bounds.top);
    }

    /// <inheritdoc />
    public void RasterizeGlyph(int face, ushort glyph, float size, float phase, Span<byte> coverage)
    {
        var bounds = Analyze(face, glyph, size, phase);
        var length = (bounds.right - bounds.left) * (bounds.bottom - bounds.top);
        if (length <= 0)
        {
            return;
        }

        if (coverage.Length < length)
        {
            throw new ArgumentException($"The coverage holds {coverage.Length} bytes; the glyph needs {length}.", nameof(coverage));
        }

        // The span holds at least the box's bytes, which is what the texture of 1 byte a pixel fills.
        fixed (byte* bytes = coverage)
        {
            Check(
                _analysis->CreateAlphaTexture(DWRITE_TEXTURE_TYPE.DWRITE_TEXTURE_ALIASED_1x1, &bounds, bytes, (uint)length),
                "IDWriteGlyphRunAnalysis::CreateAlphaTexture"
            );
        }
    }

    /// <summary>Releases the faces, the analysis, the renderer, and the factory.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var face in _faces)
        {
            _ = ((IDWriteFontFace*)face)->Release();
        }

        _faces.Clear();
        _faceNumbers.Clear();
        if (_analysis != null)
        {
            _ = _analysis->Release();
            _analysis = null;
        }

        NativeMemory.Free(_renderer);
        _renderer = null;
        _collectorHandle.Free();
        _ = _factory->Release();
        _factory = null;
    }

    private static void Check(int result, string call, Collector? collector = null)
    {
        // An exception the renderer caught comes out as it was, in place of the E_FAIL that Draw returned for it.
        collector?.Error?.Throw();
        if (result < 0)
        {
            throw new InvalidOperationException($"{call} failed with 0x{result:X8}.") { HResult = result };
        }
    }

    private static void** MakeVtable()
    {
        // IUnknown, IDWritePixelSnapping, then IDWriteTextRenderer: 10 entries, alive as long as the type.
        var vtable = (void**)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(DWriteTextBackend), sizeof(void*) * 10);
        vtable[0] = (delegate* unmanaged<TextRenderer*, Guid*, void**, int>)&QueryInterface;
        vtable[1] = (delegate* unmanaged<TextRenderer*, uint>)&AddRef;
        vtable[2] = (delegate* unmanaged<TextRenderer*, uint>)&Release;
        vtable[3] = (delegate* unmanaged<TextRenderer*, void*, BOOL*, int>)&IsPixelSnappingDisabled;
        vtable[4] = (delegate* unmanaged<TextRenderer*, void*, DWRITE_MATRIX*, int>)&GetCurrentTransform;
        vtable[5] = (delegate* unmanaged<TextRenderer*, void*, float*, int>)&GetPixelsPerDip;
        delegate* unmanaged<
            TextRenderer*,
            void*,
            float,
            float,
            DWRITE_MEASURING_MODE,
            DWRITE_GLYPH_RUN*,
            DWRITE_GLYPH_RUN_DESCRIPTION*,
            IUnknown*,
            int> drawGlyphRun = &DrawGlyphRun;
        vtable[6] = drawGlyphRun;
        vtable[7] = (delegate* unmanaged<TextRenderer*, void*, float, float, DWRITE_UNDERLINE*, IUnknown*, int>)&DrawUnderline;
        vtable[8] = (delegate* unmanaged<TextRenderer*, void*, float, float, DWRITE_STRIKETHROUGH*, IUnknown*, int>)&DrawStrikethrough;
        vtable[9] = (delegate* unmanaged<TextRenderer*, void*, float, float, IDWriteInlineObject*, BOOL, BOOL, IUnknown*, int>)&DrawInlineObject;
        return vtable;
    }

    // The entries below other than DrawGlyphRun only read and write through their pointer arguments, call nothing that can
    // throw, and allocate nothing, so they cannot throw a managed exception and have no catch (.cursor/rules/interop.mdc).
    [UnmanagedCallersOnly]
    private static int QueryInterface(TextRenderer* self, Guid* riid, void** result)
    {
        if (*riid == __uuidof<IUnknown>() || *riid == __uuidof<IDWritePixelSnapping>() || *riid == __uuidof<IDWriteTextRenderer>())
        {
            self->References++;
            *result = self;
            return S.S_OK;
        }

        *result = null;
        return E.E_NOINTERFACE;
    }

    // The backend owns the renderer's memory; the count is kept only to answer as COM expects.
    [UnmanagedCallersOnly]
    private static uint AddRef(TextRenderer* self) => ++self->References;

    [UnmanagedCallersOnly]
    private static uint Release(TextRenderer* self) => --self->References;

    // The engine rounds positions itself (design §5.6).
    [UnmanagedCallersOnly]
    private static int IsPixelSnappingDisabled(TextRenderer* self, void* context, BOOL* disabled)
    {
        *disabled = BOOL.TRUE;
        return S.S_OK;
    }

    [UnmanagedCallersOnly]
    private static int GetCurrentTransform(TextRenderer* self, void* context, DWRITE_MATRIX* transform)
    {
        *transform = new DWRITE_MATRIX { m11 = 1, m22 = 1 };
        return S.S_OK;
    }

    // A physical pixel is a DIP: the size is passed in pixels (design §5.6).
    [UnmanagedCallersOnly]
    private static int GetPixelsPerDip(TextRenderer* self, void* context, float* pixelsPerDip)
    {
        *pixelsPerDip = 1;
        return S.S_OK;
    }

    [UnmanagedCallersOnly]
    private static int DrawGlyphRun(
        TextRenderer* self,
        void* context,
        float baselineOriginX,
        float baselineOriginY,
        DWRITE_MEASURING_MODE measuringMode,
        DWRITE_GLYPH_RUN* glyphRun,
        DWRITE_GLYPH_RUN_DESCRIPTION* description,
        IUnknown* effect
    )
    {
        Collector? collector = null;
        try
        {
            collector = (Collector)GCHandle.FromIntPtr((nint)context).Target!;
            collector.AddRun(baselineOriginX, baselineOriginY, glyphRun, description);
            return S.S_OK;
        }
        catch (Exception exception)
        {
            // No managed exception crosses into DirectWrite: the shaping throws it again after Draw returns.
            if (collector is not null)
            {
                collector.Error ??= ExceptionDispatchInfo.Capture(exception);
            }

            return E.E_FAIL;
        }
    }

    [UnmanagedCallersOnly]
    private static int DrawUnderline(TextRenderer* self, void* context, float x, float y, DWRITE_UNDERLINE* underline, IUnknown* effect) => S.S_OK;

    [UnmanagedCallersOnly]
    private static int DrawStrikethrough(
        TextRenderer* self,
        void* context,
        float x,
        float y,
        DWRITE_STRIKETHROUGH* strikethrough,
        IUnknown* effect
    ) => S.S_OK;

    [UnmanagedCallersOnly]
    private static int DrawInlineObject(
        TextRenderer* self,
        void* context,
        float x,
        float y,
        IDWriteInlineObject* inlineObject,
        BOOL isSideways,
        BOOL isRightToLeft,
        IUnknown* effect
    ) => S.S_OK;

    /// <summary>Makes the layout of a text with no wrapping; the caller releases it.</summary>
    private IDWriteTextLayout* MakeLayout(string text, string fontFamily, float size, FontWeight weight, string locale)
    {
        IDWriteTextFormat* format = null;
        IDWriteTextLayout* layout = null;
        try
        {
            // The strings are pinned for the calls, and a pinned string ends with a 0 character.
            fixed (
                char* family = fontFamily,
                    localeName = locale,
                    chars = text
            )
            {
                var fontWeight =
                    weight == FontWeight.Bold ? DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_BOLD : DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL;
                Check(
                    _factory->CreateTextFormat(
                        family,
                        null,
                        fontWeight,
                        DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL,
                        DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL,
                        size,
                        localeName,
                        &format
                    ),
                    "IDWriteFactory::CreateTextFormat"
                );
                Check(
                    _factory->CreateTextLayout(chars, (uint)text.Length, format, LayoutExtent, LayoutExtent, &layout),
                    "IDWriteFactory::CreateTextLayout"
                );
            }

            Check(layout->SetWordWrapping(DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_NO_WRAP), "IDWriteTextLayout::SetWordWrapping");
            var made = layout;
            layout = null;
            return made;
        }
        finally
        {
            if (layout != null)
            {
                _ = layout->Release();
            }

            // The layout holds what it needs of the format.
            if (format != null)
            {
                _ = format->Release();
            }
        }
    }

    private int FaceNumber(IDWriteFontFace* face)
    {
        if (_faceNumbers.TryGetValue((nint)face, out var number))
        {
            return number;
        }

        // The face is kept until the backend is disposed, so that its pointer names the same face all along.
        _ = face->AddRef();
        number = _faces.Count;
        _faces.Add((nint)face);
        _faceNumbers.Add((nint)face, number);
        return number;
    }

    /// <summary>Puts the collected runs on their lines and makes the shaped text (design §5.6).</summary>
    private ShapedText Build(DWRITE_LINE_METRICS[] lines)
    {
        // A run belongs to the line whose characters hold its first one.
        var runs = _collector.Runs;
        var lineOfRun = new int[runs.Count];
        for (var r = 0; r < runs.Count; r++)
        {
            var end = 0u;
            var line = 0;
            while (line < lines.Length - 1 && runs[r].TextPosition >= end + lines[line].length)
            {
                end += lines[line].length;
                line++;
            }

            lineOfRun[r] = line;
        }

        var glyphs = new PlacedGlyph[_collector.Glyphs.Count];
        var textLines = new TextLine[lines.Length];
        var firstBaseline = lines[0].baseline;
        var top = 0f;
        var count = 0;
        var width = 0f;
        for (var line = 0; line < lines.Length; line++)
        {
            var first = count;
            var lineWidth = 0f;
            for (var r = 0; r < runs.Count; r++)
            {
                if (lineOfRun[r] != line)
                {
                    continue;
                }

                var run = runs[r];
                for (var g = 0; g < run.Count; g++)
                {
                    var (face, glyph, position) = _collector.Glyphs[run.First + g];
                    glyphs[count++] = new PlacedGlyph(FaceNumber((IDWriteFontFace*)face), glyph, position - new Vector2(0, firstBaseline));
                }

                lineWidth += run.Advance;
            }

            textLines[line] = new TextLine(first, count - first, lineWidth, top + lines[line].baseline - firstBaseline);
            top += lines[line].height;
            width = MathF.Max(width, lineWidth);
        }

        var last = lines[^1];
        return new ShapedText(new TextMetrics(width, top, firstBaseline, last.height - last.baseline), glyphs, textLines);
    }

    /// <summary>Gets the alpha-texture bounds of one glyph at a phase, keeping the analysis for the rasterization that follows.</summary>
    private RECT Analyze(int face, ushort glyph, float size, float phase)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)face >= (uint)_faces.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(face), face, "The face is not one this backend numbered.");
        }

        var key = new AnalysisKey(face, glyph, size, phase);
        if (_analysis == null || key != _analyzed)
        {
            if (_analysis != null)
            {
                _ = _analysis->Release();
                _analysis = null;
            }

            var index = glyph;
            var advance = 0f;
            var run = new DWRITE_GLYPH_RUN
            {
                fontFace = (IDWriteFontFace*)_faces[face],
                fontEmSize = size,
                glyphCount = 1,
                glyphIndices = &index,
                glyphAdvances = &advance,
            };
            IDWriteGlyphRunAnalysis* analysis;
            Check(
                _factory->CreateGlyphRunAnalysis(
                    &run,
                    null,
                    DWRITE_RENDERING_MODE1.DWRITE_RENDERING_MODE1_NATURAL_SYMMETRIC,
                    DWRITE_MEASURING_MODE.DWRITE_MEASURING_MODE_NATURAL,
                    DWRITE_GRID_FIT_MODE.DWRITE_GRID_FIT_MODE_DISABLED,
                    DWRITE_TEXT_ANTIALIAS_MODE.DWRITE_TEXT_ANTIALIAS_MODE_GRAYSCALE,
                    phase,
                    0,
                    &analysis
                ),
                "IDWriteFactory3::CreateGlyphRunAnalysis"
            );
            _analysis = analysis;
            _analyzed = key;
        }

        RECT bounds;
        Check(
            _analysis->GetAlphaTextureBounds(DWRITE_TEXTURE_TYPE.DWRITE_TEXTURE_ALIASED_1x1, &bounds),
            "IDWriteGlyphRunAnalysis::GetAlphaTextureBounds"
        );
        return bounds;
    }

    /// <summary>The renderer DirectWrite calls: a pointer to the vtable, then the reference count.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct TextRenderer
    {
        public void** Vtable;
        public uint References;
    }

    /// <summary>One collected run: its first character, its glyphs in the collector, and the sum of its advances.</summary>
    private readonly record struct Run(uint TextPosition, int First, int Count, float Advance);

    /// <summary>What the renderer's callbacks collect during one <c>IDWriteTextLayout::Draw</c>.</summary>
    private sealed class Collector
    {
        internal List<Run> Runs { get; } = [];

        internal List<(nint Face, ushort Glyph, Vector2 Position)> Glyphs { get; } = [];

        internal ExceptionDispatchInfo? Error { get; set; }

        internal Action? Hook { get; set; }

        internal void Clear()
        {
            Runs.Clear();
            Glyphs.Clear();
            Error = null;
        }

        /// <summary>
        /// Adds a run's glyphs at their origins (design §5.6): left to right from the start, right to left back from it, each
        /// moved by its advance offset along the run's direction and up by its ascender offset.
        /// </summary>
        internal void AddRun(float originX, float originY, DWRITE_GLYPH_RUN* run, DWRITE_GLYPH_RUN_DESCRIPTION* description)
        {
            Hook?.Invoke();

            // DirectWrite gives the run's three arrays glyphCount elements each, and only glyphOffsets may be null. They live
            // until DrawGlyphRun returns, and the spans do not outlive this call.
            var count = (int)run->glyphCount;
            var indices = new ReadOnlySpan<ushort>(run->glyphIndices, count);
            var advances = new ReadOnlySpan<float>(run->glyphAdvances, count);
            var offsets = run->glyphOffsets == null ? default : new ReadOnlySpan<DWRITE_GLYPH_OFFSET>(run->glyphOffsets, count);
            var rightToLeft = (run->bidiLevel & 1) != 0;
            var first = Glyphs.Count;
            var x = originX;
            var sum = 0f;
            for (var i = 0; i < count; i++)
            {
                var offset = offsets.IsEmpty ? default : offsets[i];
                float glyphX;
                if (rightToLeft)
                {
                    x -= advances[i];
                    glyphX = x - offset.advanceOffset;
                }
                else
                {
                    glyphX = x + offset.advanceOffset;
                    x += advances[i];
                }

                sum += advances[i];
                Glyphs.Add(((nint)run->fontFace, indices[i], new Vector2(glyphX, originY - offset.ascenderOffset)));
            }

            Runs.Add(new Run(description->textPosition, first, count, sum));
        }
    }

    private readonly record struct AnalysisKey(int Face, ushort Glyph, float Size, float Phase);
}
