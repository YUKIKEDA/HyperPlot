using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>The named colors, the palettes, and the spec's stroke of row G7a (design §4.1, §10 G7a row).</summary>
public sealed class ColorTests
{
    /// <summary>
    /// A stroke in a spec has <see cref="LineStyle"/>'s defaults and no color, and takes the color it is resolved with only
    /// when it has none (design §4.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - <c>new StrokeSpec(null)</c>, and one with a color, a width, a dash, a miter join, a square cap, and a limit of 2.
    ///
    /// Steps:
    /// - Read the first; resolve both with a series color.
    ///
    /// Expected:
    /// - No color, 1 wide, solid, round join and cap, limit 4. The first resolves to the series color with those members;
    ///   the second keeps its color and every member.
    /// </remarks>
    [Fact]
    public void StrokeSpec_DefaultsAndResolve()
    {
        var auto = new StrokeSpec(null);
        Assert.Equal(
            ((Color?)null, 1f, (LineDash?)null, LineJoin.Round, LineCap.Round, 4f),
            (auto.Color, auto.Width, auto.Dash, auto.Join, auto.Cap, auto.MiterLimit)
        );
        var series = new Color(10, 20, 30, 255);
        Assert.Equal(new LineStyle(series, 1), auto.Resolve(series));

        var red = new Color(200, 0, 0, 255);
        var given = new StrokeSpec(red, 2.5f, new LineDash(4, 2))
        {
            Join = LineJoin.Miter,
            Cap = LineCap.Square,
            MiterLimit = 2,
        };
        var expected = new LineStyle(red, 2.5f, new LineDash(4, 2))
        {
            Join = LineJoin.Miter,
            Cap = LineCap.Square,
            MiterLimit = 2,
        };
        Assert.Equal(expected, given.Resolve(series));
    }

    /// <summary>
    /// A palette holds at least one color, reads by index inside its count, and equals another with the same colors in the
    /// same order, with the same hash (design §4.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Palettes of (red, blue), (red, blue) again, (blue, red), and (red).
    ///
    /// Steps:
    /// - Make an empty one; read −1, 0, 1, and 2 of the first; compare.
    ///
    /// Expected:
    /// - ArgumentException for the empty one; ArgumentOutOfRangeException for −1 and 2, red and blue for 0 and 1. The first
    ///   two equal, with one hash; the others differ from the first, and so does null.
    /// </remarks>
    [Fact]
    public void Palette_CheckedReadAndCompared()
    {
        _ = Assert.Throws<ArgumentException>(() => new Palette());
        var palette = new Palette(Colors.Red, Colors.Blue);
        Assert.Equal(2, palette.Count);
        Assert.Equal((Colors.Red, Colors.Blue), (palette[0], palette[1]));
        Assert.Equal("index", Assert.Throws<ArgumentOutOfRangeException>(() => palette[-1]).ParamName);
        Assert.Equal("index", Assert.Throws<ArgumentOutOfRangeException>(() => palette[2]).ParamName);

        var same = new Palette(Colors.Red, Colors.Blue);
        Assert.True(palette.Equals(same));
        Assert.True(palette.Equals((object)same));
        Assert.Equal(palette.GetHashCode(), same.GetHashCode());
        Assert.False(palette.Equals(new Palette(Colors.Blue, Colors.Red)));
        Assert.False(palette.Equals(new Palette(Colors.Red)));
        Assert.False(palette.Equals(null));
        Assert.Same(Palettes.Tab10, Theme.Light.Palette);
        Assert.Same(Palettes.Tab10, Theme.Dark.Palette);
    }

    /// <summary>
    /// The palettes hold the values of the sources design §4.1 names: matplotlib's tab10, d3-scale-chromatic's categorical
    /// schemes, and R's Okabe-Ito without its added gray.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The sources' colors, as d3 writes them: 6 hexadecimal digits each, run together, copied from the files.
    ///
    /// Steps:
    /// - Compare each palette with its source, color by color.
    ///
    /// Expected:
    /// - The same count and colors, opaque.
    /// </remarks>
    [Fact]
    public void Palettes_MatchTheirSources()
    {
        Check(Palettes.Tab10, "1f77b4ff7f0e2ca02cd627289467bd8c564be377c27f7f7fbcbd2217becf");
        Check(Palettes.Tableau10, "4e79a7f28e2ce1575976b7b259a14fedc949af7aa1ff9da79c755fbab0ab");
        Check(Palettes.Set1, "e41a1c377eb84daf4a984ea3ff7f00ffff33a65628f781bf999999");
        Check(Palettes.Set2, "66c2a5fc8d628da0cbe78ac3a6d854ffd92fe5c494b3b3b3");
        Check(Palettes.Dark2, "1b9e77d95f027570b3e7298a66a61ee6ab02a6761d666666");
        Check(Palettes.Paired, "a6cee31f78b4b2df8a33a02cfb9a99e31a1cfdbf6fff7f00cab2d66a3d9affff99b15928");
        Check(Palettes.OkabeIto, "000000E69F0056B4E9009E73F0E4420072B2D55E00CC79A7");

        static void Check(Palette palette, string source)
        {
            Assert.Equal(source.Length / 6, palette.Count);
            for (var i = 0; i < palette.Count; i++)
            {
                Assert.Equal(Hex(source.Substring(i * 6, 6)), palette[i]);
            }
        }
    }

    /// <summary>
    /// The named colors are the 148 of CSS Color Module Level 4's table, in its order, named as the table names them in
    /// Pascal case (design §4.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The table's names and values, copied from <c>css-color-4/Overview.bs</c>.
    ///
    /// Steps:
    /// - Compare each property with the table's value.
    ///
    /// Expected:
    /// - 148 entries, all opaque and equal to the table; the 7 grays under both spellings.
    /// </remarks>
    [Fact]
    public void Colors_MatchTheCssTable()
    {
        (Color Property, string Name, string Hex)[] table =
        [
            (Colors.AliceBlue, "aliceblue", "f0f8ff"),
            (Colors.AntiqueWhite, "antiquewhite", "faebd7"),
            (Colors.Aqua, "aqua", "00ffff"),
            (Colors.Aquamarine, "aquamarine", "7fffd4"),
            (Colors.Azure, "azure", "f0ffff"),
            (Colors.Beige, "beige", "f5f5dc"),
            (Colors.Bisque, "bisque", "ffe4c4"),
            (Colors.Black, "black", "000000"),
            (Colors.BlanchedAlmond, "blanchedalmond", "ffebcd"),
            (Colors.Blue, "blue", "0000ff"),
            (Colors.BlueViolet, "blueviolet", "8a2be2"),
            (Colors.Brown, "brown", "a52a2a"),
            (Colors.BurlyWood, "burlywood", "deb887"),
            (Colors.CadetBlue, "cadetblue", "5f9ea0"),
            (Colors.Chartreuse, "chartreuse", "7fff00"),
            (Colors.Chocolate, "chocolate", "d2691e"),
            (Colors.Coral, "coral", "ff7f50"),
            (Colors.CornflowerBlue, "cornflowerblue", "6495ed"),
            (Colors.Cornsilk, "cornsilk", "fff8dc"),
            (Colors.Crimson, "crimson", "dc143c"),
            (Colors.Cyan, "cyan", "00ffff"),
            (Colors.DarkBlue, "darkblue", "00008b"),
            (Colors.DarkCyan, "darkcyan", "008b8b"),
            (Colors.DarkGoldenrod, "darkgoldenrod", "b8860b"),
            (Colors.DarkGray, "darkgray", "a9a9a9"),
            (Colors.DarkGreen, "darkgreen", "006400"),
            (Colors.DarkGrey, "darkgrey", "a9a9a9"),
            (Colors.DarkKhaki, "darkkhaki", "bdb76b"),
            (Colors.DarkMagenta, "darkmagenta", "8b008b"),
            (Colors.DarkOliveGreen, "darkolivegreen", "556b2f"),
            (Colors.DarkOrange, "darkorange", "ff8c00"),
            (Colors.DarkOrchid, "darkorchid", "9932cc"),
            (Colors.DarkRed, "darkred", "8b0000"),
            (Colors.DarkSalmon, "darksalmon", "e9967a"),
            (Colors.DarkSeaGreen, "darkseagreen", "8fbc8f"),
            (Colors.DarkSlateBlue, "darkslateblue", "483d8b"),
            (Colors.DarkSlateGray, "darkslategray", "2f4f4f"),
            (Colors.DarkSlateGrey, "darkslategrey", "2f4f4f"),
            (Colors.DarkTurquoise, "darkturquoise", "00ced1"),
            (Colors.DarkViolet, "darkviolet", "9400d3"),
            (Colors.DeepPink, "deeppink", "ff1493"),
            (Colors.DeepSkyBlue, "deepskyblue", "00bfff"),
            (Colors.DimGray, "dimgray", "696969"),
            (Colors.DimGrey, "dimgrey", "696969"),
            (Colors.DodgerBlue, "dodgerblue", "1e90ff"),
            (Colors.FireBrick, "firebrick", "b22222"),
            (Colors.FloralWhite, "floralwhite", "fffaf0"),
            (Colors.ForestGreen, "forestgreen", "228b22"),
            (Colors.Fuchsia, "fuchsia", "ff00ff"),
            (Colors.Gainsboro, "gainsboro", "dcdcdc"),
            (Colors.GhostWhite, "ghostwhite", "f8f8ff"),
            (Colors.Gold, "gold", "ffd700"),
            (Colors.Goldenrod, "goldenrod", "daa520"),
            (Colors.Gray, "gray", "808080"),
            (Colors.Green, "green", "008000"),
            (Colors.GreenYellow, "greenyellow", "adff2f"),
            (Colors.Grey, "grey", "808080"),
            (Colors.Honeydew, "honeydew", "f0fff0"),
            (Colors.HotPink, "hotpink", "ff69b4"),
            (Colors.IndianRed, "indianred", "cd5c5c"),
            (Colors.Indigo, "indigo", "4b0082"),
            (Colors.Ivory, "ivory", "fffff0"),
            (Colors.Khaki, "khaki", "f0e68c"),
            (Colors.Lavender, "lavender", "e6e6fa"),
            (Colors.LavenderBlush, "lavenderblush", "fff0f5"),
            (Colors.LawnGreen, "lawngreen", "7cfc00"),
            (Colors.LemonChiffon, "lemonchiffon", "fffacd"),
            (Colors.LightBlue, "lightblue", "add8e6"),
            (Colors.LightCoral, "lightcoral", "f08080"),
            (Colors.LightCyan, "lightcyan", "e0ffff"),
            (Colors.LightGoldenrodYellow, "lightgoldenrodyellow", "fafad2"),
            (Colors.LightGray, "lightgray", "d3d3d3"),
            (Colors.LightGreen, "lightgreen", "90ee90"),
            (Colors.LightGrey, "lightgrey", "d3d3d3"),
            (Colors.LightPink, "lightpink", "ffb6c1"),
            (Colors.LightSalmon, "lightsalmon", "ffa07a"),
            (Colors.LightSeaGreen, "lightseagreen", "20b2aa"),
            (Colors.LightSkyBlue, "lightskyblue", "87cefa"),
            (Colors.LightSlateGray, "lightslategray", "778899"),
            (Colors.LightSlateGrey, "lightslategrey", "778899"),
            (Colors.LightSteelBlue, "lightsteelblue", "b0c4de"),
            (Colors.LightYellow, "lightyellow", "ffffe0"),
            (Colors.Lime, "lime", "00ff00"),
            (Colors.LimeGreen, "limegreen", "32cd32"),
            (Colors.Linen, "linen", "faf0e6"),
            (Colors.Magenta, "magenta", "ff00ff"),
            (Colors.Maroon, "maroon", "800000"),
            (Colors.MediumAquamarine, "mediumaquamarine", "66cdaa"),
            (Colors.MediumBlue, "mediumblue", "0000cd"),
            (Colors.MediumOrchid, "mediumorchid", "ba55d3"),
            (Colors.MediumPurple, "mediumpurple", "9370db"),
            (Colors.MediumSeaGreen, "mediumseagreen", "3cb371"),
            (Colors.MediumSlateBlue, "mediumslateblue", "7b68ee"),
            (Colors.MediumSpringGreen, "mediumspringgreen", "00fa9a"),
            (Colors.MediumTurquoise, "mediumturquoise", "48d1cc"),
            (Colors.MediumVioletRed, "mediumvioletred", "c71585"),
            (Colors.MidnightBlue, "midnightblue", "191970"),
            (Colors.MintCream, "mintcream", "f5fffa"),
            (Colors.MistyRose, "mistyrose", "ffe4e1"),
            (Colors.Moccasin, "moccasin", "ffe4b5"),
            (Colors.NavajoWhite, "navajowhite", "ffdead"),
            (Colors.Navy, "navy", "000080"),
            (Colors.OldLace, "oldlace", "fdf5e6"),
            (Colors.Olive, "olive", "808000"),
            (Colors.OliveDrab, "olivedrab", "6b8e23"),
            (Colors.Orange, "orange", "ffa500"),
            (Colors.OrangeRed, "orangered", "ff4500"),
            (Colors.Orchid, "orchid", "da70d6"),
            (Colors.PaleGoldenrod, "palegoldenrod", "eee8aa"),
            (Colors.PaleGreen, "palegreen", "98fb98"),
            (Colors.PaleTurquoise, "paleturquoise", "afeeee"),
            (Colors.PaleVioletRed, "palevioletred", "db7093"),
            (Colors.PapayaWhip, "papayawhip", "ffefd5"),
            (Colors.PeachPuff, "peachpuff", "ffdab9"),
            (Colors.Peru, "peru", "cd853f"),
            (Colors.Pink, "pink", "ffc0cb"),
            (Colors.Plum, "plum", "dda0dd"),
            (Colors.PowderBlue, "powderblue", "b0e0e6"),
            (Colors.Purple, "purple", "800080"),
            (Colors.RebeccaPurple, "rebeccapurple", "663399"),
            (Colors.Red, "red", "ff0000"),
            (Colors.RosyBrown, "rosybrown", "bc8f8f"),
            (Colors.RoyalBlue, "royalblue", "4169e1"),
            (Colors.SaddleBrown, "saddlebrown", "8b4513"),
            (Colors.Salmon, "salmon", "fa8072"),
            (Colors.SandyBrown, "sandybrown", "f4a460"),
            (Colors.SeaGreen, "seagreen", "2e8b57"),
            (Colors.SeaShell, "seashell", "fff5ee"),
            (Colors.Sienna, "sienna", "a0522d"),
            (Colors.Silver, "silver", "c0c0c0"),
            (Colors.SkyBlue, "skyblue", "87ceeb"),
            (Colors.SlateBlue, "slateblue", "6a5acd"),
            (Colors.SlateGray, "slategray", "708090"),
            (Colors.SlateGrey, "slategrey", "708090"),
            (Colors.Snow, "snow", "fffafa"),
            (Colors.SpringGreen, "springgreen", "00ff7f"),
            (Colors.SteelBlue, "steelblue", "4682b4"),
            (Colors.Tan, "tan", "d2b48c"),
            (Colors.Teal, "teal", "008080"),
            (Colors.Thistle, "thistle", "d8bfd8"),
            (Colors.Tomato, "tomato", "ff6347"),
            (Colors.Turquoise, "turquoise", "40e0d0"),
            (Colors.Violet, "violet", "ee82ee"),
            (Colors.Wheat, "wheat", "f5deb3"),
            (Colors.White, "white", "ffffff"),
            (Colors.WhiteSmoke, "whitesmoke", "f5f5f5"),
            (Colors.Yellow, "yellow", "ffff00"),
            (Colors.YellowGreen, "yellowgreen", "9acd32"),
        ];

        Assert.Equal(148, table.Length);
        Assert.All(table, entry => Assert.Equal(Hex(entry.Hex), entry.Property));
        Assert.Equal(7, table.Count(entry => entry.Name.Contains("grey", StringComparison.Ordinal)));
        Assert.Equal(Colors.DarkSlateGray, Colors.DarkSlateGrey);
    }

    private static Color Hex(string hex)
    {
        var value = Convert.ToInt32(hex, 16);
        return new Color((byte)(value >> 16), (byte)(value >> 8), (byte)value, 255);
    }
}
