namespace HyperPlot;

/// <summary>
/// A continuous color map (design §4.1, row G7c): 256 colors that a sequential or diverging color scale reads by a value
/// from 0 to 1. Immutable; two maps are equal when they hold the same colors.
/// </summary>
public sealed class ColorMap : IEquatable<ColorMap>
{
    /// <summary>The number of colors of every map: matplotlib's <c>image.lut</c>.</summary>
    internal const int Size = 256;

    private readonly Color[] _colors;

    /// <summary>
    /// Initializes a new instance of the <see cref="ColorMap"/> class from colors spread evenly from 0 to 1, interpolated
    /// linearly to 256 colors, as matplotlib's <c>LinearSegmentedColormap.from_list</c> does.
    /// </summary>
    /// <param name="colors">The colors: at least two.</param>
    /// <exception cref="ArgumentException"><paramref name="colors"/> has fewer than two colors.</exception>
    public ColorMap(params ReadOnlySpan<Color> colors)
    {
        if (colors.Length < 2)
        {
            throw new ArgumentException($"A color map has at least 2 colors; {colors.Length} were given.", nameof(colors));
        }

        var anchors = new double[colors.Length * 4];
        for (var i = 0; i < colors.Length; i++)
        {
            anchors[(4 * i) + 0] = colors[i].R / 255.0;
            anchors[(4 * i) + 1] = colors[i].G / 255.0;
            anchors[(4 * i) + 2] = colors[i].B / 255.0;
            anchors[(4 * i) + 3] = colors[i].A / 255.0;
        }

        _colors = Interpolate(anchors, colors.Length, 4);
    }

    private ColorMap(Color[] colors) => _colors = colors;

    /// <summary>Gets the number of colors: 256.</summary>
    public int Count => _colors.Length;

    /// <summary>Gets a color of the table.</summary>
    /// <param name="index">The index: at least 0 and below 256.</param>
    /// <returns>The color.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the table.</exception>
    public Color this[int index] =>
        (uint)index < Size ? _colors[index] : throw new ArgumentOutOfRangeException(nameof(index), index, "A color map has 256 colors.");

    /// <summary>
    /// Returns the color of a value from 0 to 1, as matplotlib's <c>Colormap.__call__</c> picks it: the color at ⌊t · 256⌋,
    /// the last one for 1, the first one below 0 and the last one above 1, and transparent for NaN (design §4.1).
    /// </summary>
    /// <param name="t">The value.</param>
    /// <returns>The color.</returns>
    public Color Map(double t)
    {
        if (double.IsNaN(t))
        {
            return default;
        }

        if (t < 0)
        {
            return _colors[0];
        }

        var position = t * Size;
        return position >= Size ? _colors[Size - 1] : _colors[(int)position];
    }

    /// <inheritdoc />
    public bool Equals(ColorMap? other) => other is not null && _colors.AsSpan().SequenceEqual(other._colors);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ColorMap);

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

    /// <summary>
    /// Makes a map from matplotlib's 256 listed colors, red, green, blue, each its number from 0 to 1 times 10^6: matplotlib
    /// writes 6 decimals, so n / 10^6 is the double it parses.
    /// </summary>
    internal static ColorMap Listed(ReadOnlySpan<int> millionths)
    {
        var colors = new Color[Size];
        for (var i = 0; i < Size; i++)
        {
            colors[i] = new Color(Byte(millionths[3 * i] / 1e6), Byte(millionths[(3 * i) + 1] / 1e6), Byte(millionths[(3 * i) + 2] / 1e6), 255);
        }

        return new ColorMap(colors);
    }

    /// <summary>Makes a map from matplotlib's ColorBrewer colors: red, green, blue, each its number from 0 to 1.</summary>
    internal static ColorMap Segmented(ReadOnlySpan<double> values) => new(Interpolate(values.ToArray(), values.Length / 3, 3));

    /// <summary>
    /// Interpolates colors spread evenly from 0 to 1 to 256, in the order of operations of matplotlib's
    /// <c>_create_lookup_table</c> over <c>np.linspace</c>, so that the doubles and the bytes are the same.
    /// </summary>
    private static Color[] Interpolate(double[] anchors, int count, int channels)
    {
        // np.linspace(0, 1, n): i · (1 / (n − 1)), and the last one 1. The anchors' x is that times N − 1.
        var x = new double[count];
        for (var i = 0; i < count; i++)
        {
            x[i] = (i == count - 1 ? 1.0 : i * (1.0 / (count - 1))) * (Size - 1);
        }

        var colors = new Color[Size];
        Span<byte> channel = stackalloc byte[4];
        for (var i = 0; i < Size; i++)
        {
            var at = (Size - 1) * (i == Size - 1 ? 1.0 : i * (1.0 / (Size - 1)));
            for (var c = 0; c < channels; c++)
            {
                double value;
                if (i == 0)
                {
                    value = anchors[c];
                }
                else if (i == Size - 1)
                {
                    value = anchors[((count - 1) * channels) + c];
                }
                else
                {
                    // np.searchsorted(x, at) with side "left": the first anchor at or past the position.
                    var k = 1;
                    while (x[k] < at)
                    {
                        k++;
                    }

                    var distance = (at - x[k - 1]) / (x[k] - x[k - 1]);
                    var low = anchors[((k - 1) * channels) + c];
                    value = (distance * (anchors[(k * channels) + c] - low)) + low;
                }

                channel[c] = Byte(Math.Clamp(value, 0.0, 1.0));
            }

            colors[i] = new Color(channel[0], channel[1], channel[2], channels == 4 ? channel[3] : (byte)255);
        }

        return colors;
    }

    /// <summary>Returns a byte as matplotlib's <c>bytes=True</c> makes it: (v · 255) truncated.</summary>
    private static byte Byte(double value) => (byte)(value * 255);
}
