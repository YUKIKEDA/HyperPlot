namespace HyperPlot;

/// <summary>
/// An immutable sequence of colors (design §4.1, row G7a): the series colors of a theme (design §4.7). Two palettes are
/// equal when they hold the same colors in the same order.
/// </summary>
public sealed class Palette : IEquatable<Palette>
{
    private readonly Color[] _colors;

    /// <summary>Initializes a new instance of the <see cref="Palette"/> class with its colors.</summary>
    /// <param name="colors">The colors: at least one.</param>
    /// <exception cref="ArgumentException"><paramref name="colors"/> is empty.</exception>
    public Palette(params ReadOnlySpan<Color> colors)
    {
        if (colors.IsEmpty)
        {
            throw new ArgumentException("A palette has at least one color.", nameof(colors));
        }

        _colors = colors.ToArray();
    }

    /// <summary>Gets the number of colors.</summary>
    public int Count => _colors.Length;

    /// <summary>Gets a color.</summary>
    /// <param name="index">The index: at least 0 and below <see cref="Count"/>.</param>
    /// <returns>The color.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the palette.</exception>
    public Color this[int index] =>
        (uint)index < (uint)_colors.Length
            ? _colors[index]
            : throw new ArgumentOutOfRangeException(nameof(index), index, $"The palette has {_colors.Length} colors.");

    /// <inheritdoc />
    public bool Equals(Palette? other) => other is not null && _colors.AsSpan().SequenceEqual(other._colors);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as Palette);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        foreach (var color in _colors)
        {
            hash.Add(color);
        }

        return hash.ToHashCode();
    }

    /// <summary>Returns the series color of a layer: the color at its position, around the palette (design §4.1).</summary>
    internal Color Series(int layer) => _colors[layer % _colors.Length];
}

/// <summary>
/// Qualitative palettes (design §4.1, row G7a). The values come from the sources design §4.1 names: matplotlib's
/// <c>_tab10_data</c>, d3-scale-chromatic's <c>src/categorical/</c>, and R's <c>palette.colors</c> for Okabe-Ito.
/// </summary>
public static class Palettes
{
    /// <summary>Gets matplotlib's tab10, its default color cycle, the same as d3's category10: the themes' default.</summary>
    public static Palette Tab10 { get; } = Of("1f77b4ff7f0e2ca02cd627289467bd8c564be377c27f7f7fbcbd2217becf");

    /// <summary>Gets Tableau 10, as d3's <c>schemeTableau10</c>.</summary>
    public static Palette Tableau10 { get; } = Of("4e79a7f28e2ce1575976b7b259a14fedc949af7aa1ff9da79c755fbab0ab");

    /// <summary>Gets ColorBrewer's Set1, as d3's <c>schemeSet1</c>.</summary>
    public static Palette Set1 { get; } = Of("e41a1c377eb84daf4a984ea3ff7f00ffff33a65628f781bf999999");

    /// <summary>Gets ColorBrewer's Set2, as d3's <c>schemeSet2</c>.</summary>
    public static Palette Set2 { get; } = Of("66c2a5fc8d628da0cbe78ac3a6d854ffd92fe5c494b3b3b3");

    /// <summary>Gets ColorBrewer's Dark2, as d3's <c>schemeDark2</c>.</summary>
    public static Palette Dark2 { get; } = Of("1b9e77d95f027570b3e7298a66a61ee6ab02a6761d666666");

    /// <summary>Gets ColorBrewer's Paired, as d3's <c>schemePaired</c>.</summary>
    public static Palette Paired { get; } = Of("a6cee31f78b4b2df8a33a02cfb9a99e31a1cfdbf6fff7f00cab2d66a3d9affff99b15928");

    /// <summary>Gets Okabe and Ito's 8 colors, in the order of R's <c>palette.colors</c>, without the gray R adds.</summary>
    public static Palette OkabeIto { get; } = Of("000000e69f0056b4e9009e73f0e4420072b2d55e00cc79a7");

    /// <summary>Makes a palette from colors written as d3 writes them: 6 hexadecimal digits each, run together.</summary>
    private static Palette Of(string hex)
    {
        var colors = new Color[hex.Length / 6];
        for (var i = 0; i < colors.Length; i++)
        {
            var value = Convert.ToInt32(hex.Substring(i * 6, 6), 16);
            colors[i] = new Color((byte)(value >> 16), (byte)(value >> 8), (byte)value, 255);
        }

        return new Palette(colors);
    }
}
