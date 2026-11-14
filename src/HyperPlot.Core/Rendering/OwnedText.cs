namespace HyperPlot.Rendering;

/// <summary>
/// A text shaped outside the shaping cache of a <see cref="TextEngine"/> (design §5.6, Issue #378): a text that is new in
/// most frames, such as the hover's tooltip or the measurement's readout. Its owner records it with
/// <see cref="RenderList.DrawText(ReadOnlySpan{char}, System.Numerics.Vector2, in TextStyle, ShapedText)"/>, once a list,
/// so that it takes no entry of the cache and puts out no text the cache keeps. It shapes again only when its characters or
/// the font, size, weight, or locale change; its buffers grow to its longest text and never shrink.
/// </summary>
/// <remarks>The thread that draws uses it, as it uses the engine.</remarks>
internal sealed class OwnedText
{
    private char[] _chars = [];
    private int _length;
    private TextEngine? _engine;
    private string _family = string.Empty;
    private float _size;
    private FontWeight _weight;
    private string _locale = string.Empty;

    /// <summary>Gets the shaped text: the last text <see cref="Shape"/> was given.</summary>
    public ShapedText Shaped { get; } = new();

    /// <summary>
    /// Returns the text shaped by the engine's backend, shaping it only when it differs from the last one in its characters,
    /// font, size, weight, or locale, or the engine differs.
    /// </summary>
    /// <param name="engine">The engine whose backend shapes.</param>
    /// <param name="text">The characters; a line feed starts a new line.</param>
    /// <param name="style">The style; only the font, the size, the weight, and the locale change the shape.</param>
    /// <returns><see cref="Shaped"/>.</returns>
    /// <exception cref="ArgumentNullException">The font family or the locale is null.</exception>
    /// <exception cref="ArgumentException">The font family or the locale is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The size is not finite or outside (0, 512], or the weight, an alignment, or the rotation is not a named value.
    /// </exception>
    public ShapedText Shape(TextEngine engine, ReadOnlySpan<char> text, in TextStyle style)
    {
        RenderList.ValidateStyle(style);
        if (
            ReferenceEquals(engine, _engine)
            && text.SequenceEqual(_chars.AsSpan(0, _length))
            && style.FontFamily == _family
            && style.Size == _size
            && style.Weight == _weight
            && style.Locale == _locale
        )
        {
            return Shaped;
        }

        // Unmatched until the backend has shaped it: an exception leaves no shape named by the old text.
        _engine = null;
        if (_chars.Length < text.Length)
        {
            _chars = new char[Math.Max(text.Length, _chars.Length * 2)];
        }

        text.CopyTo(_chars);
        engine.Backend.Shape(text, style.FontFamily, style.Size, style.Weight, style.Locale, Shaped);
        (_length, _family, _size, _weight, _locale, _engine) = (text.Length, style.FontFamily, style.Size, style.Weight, style.Locale, engine);
        return Shaped;
    }
}
