using System.Globalization;

namespace HyperPlot.Tests;

/// <summary>The color scales and the color maps of row G7c (design §4.1, §4.2, §10 G7c row).</summary>
public sealed class ColorScaleTests
{
    /// <summary>
    /// The three color scales have their defaults, and a diverging middle that is not finite is refused (design §4.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - None.
    ///
    /// Steps:
    /// - Make Scale.Ordinal(), Scale.Sequential(), Scale.Diverging(); make Scale.Diverging with NaN and ∞ as the middle.
    ///
    /// Expected:
    /// - Ordinal: no palette, no title. Sequential: viridis over a linear scale. Diverging: RdBu, no middle, a linear scale.
    ///   ArgumentOutOfRangeException naming mid for NaN and ∞.
    /// </remarks>
    [Fact]
    public void Scales_HaveTheirDefaults()
    {
        var ordinal = Scale.Ordinal();
        Assert.Null(ordinal.Palette);
        Assert.Null(ordinal.Title);
        Assert.Equal("Sensor", ordinal.WithTitle("Sensor").Title);
        var sequential = Scale.Sequential();
        Assert.Same(ColorMaps.Viridis, sequential.Map);
        Assert.IsType<LinearScaleSpec>(sequential.Scale);
        var diverging = Scale.Diverging();
        Assert.Same(ColorMaps.RdBu, diverging.Map);
        Assert.Null(diverging.Mid);
        Assert.IsType<LinearScaleSpec>(diverging.Scale);
        foreach (var mid in new[] { double.NaN, double.PositiveInfinity })
        {
            Assert.Equal("mid", Assert.Throws<ArgumentOutOfRangeException>(() => Scale.Diverging(mid: mid)).ParamName);
        }
    }

    /// <summary>
    /// A color map takes two colors or more, has 256, refuses an index outside them, and equals another of the same colors
    /// (design §4.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - None.
    ///
    /// Steps:
    /// - Make maps of 0, 1, and 2 colors; read the indices −1 and 256; compare maps.
    ///
    /// Expected:
    /// - ArgumentException for 0 and 1 colors; 256 colors from black to white for 2, each channel equal and from 0 to 255;
    ///   ArgumentOutOfRangeException for −1 and 256; two maps of the same colors equal, with the same hash; viridis and
    ///   magma differ.
    /// </remarks>
    [Fact]
    public void ColorMap_ChecksAndCompares()
    {
        _ = Assert.Throws<ArgumentException>(() => new ColorMap());
        _ = Assert.Throws<ArgumentException>(() => new ColorMap(Colors.Black));
        var gray = new ColorMap(Colors.Black, Colors.White);
        Assert.Equal(256, gray.Count);
        Assert.Equal(new Color(0, 0, 0, 255), gray[0]);
        Assert.Equal(new Color(255, 255, 255, 255), gray[255]);
        for (var i = 0; i < 256; i++)
        {
            Assert.Equal(gray[i].R, gray[i].G);
            Assert.Equal(gray[i].R, gray[i].B);
            Assert.True(i == 0 || gray[i].R >= gray[i - 1].R);
        }

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => gray[-1]);
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => gray[256]);
        var again = new ColorMap(Colors.Black, Colors.White);
        Assert.Equal(gray, again);
        Assert.Equal(gray.GetHashCode(), again.GetHashCode());
        Assert.NotEqual(ColorMaps.Viridis, ColorMaps.Magma);
        Assert.False(gray.Equals(null));
    }

    /// <summary>
    /// Each of the 12 maps holds the bytes matplotlib gives for its 256 entries (design §4.1): the listed maps' numbers and
    /// the ColorBrewer maps' interpolation, both to bytes as <c>bytes=True</c>.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The reference that matplotlib 3.10.8 wrote with <c>cmap(np.arange(256), bytes=True)</c>, the script at its head.
    ///
    /// Steps:
    /// - Read each map's 256 colors.
    ///
    /// Expected:
    /// - Every byte is the reference's.
    /// </remarks>
    [Fact]
    public void ColorMaps_MatchMatplotlib()
    {
        var reference = Reference();
        var maps = new Dictionary<string, ColorMap>
        {
            ["viridis"] = ColorMaps.Viridis,
            ["plasma"] = ColorMaps.Plasma,
            ["inferno"] = ColorMaps.Inferno,
            ["magma"] = ColorMaps.Magma,
            ["cividis"] = ColorMaps.Cividis,
            ["Blues"] = ColorMaps.Blues,
            ["Greens"] = ColorMaps.Greens,
            ["Oranges"] = ColorMaps.Oranges,
            ["Reds"] = ColorMaps.Reds,
            ["RdBu"] = ColorMaps.RdBu,
            ["BrBG"] = ColorMaps.BrBG,
            ["PuOr"] = ColorMaps.PuOr,
        };
        Assert.Equal(maps.Keys.Order(), reference.Keys.Where(k => maps.ContainsKey(k)).Order());
        foreach (var (name, map) in maps)
        {
            var expected = reference[name];
            Assert.Equal(256, expected.Length);
            Assert.Equal(expected, Enumerable.Range(0, 256).Select(i => map[i]).ToArray());
        }
    }

    /// <summary>
    /// A map picks the color of a value as matplotlib's <c>Colormap.__call__</c> does (design §4.1): ⌊t · 256⌋, the last
    /// for 1, the ends outside [0, 1], and transparent for NaN.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The reference's values of t and matplotlib's viridis(t, bytes=True) for them: −0.5, 0, 0.001, 0.5, 0.999, 1, 1.5,
    ///   NaN, 1 / 256, 255 / 256, 0.25.
    ///
    /// Steps:
    /// - Map each t through ColorMaps.Viridis.
    ///
    /// Expected:
    /// - The reference's colors.
    /// </remarks>
    [Fact]
    public void Map_PicksAsMatplotlib()
    {
        var lines = ReferenceLines();
        var ts = lines
            .Single(l => l.StartsWith("t ", StringComparison.Ordinal))[2..]
            .Split(' ')
            .Select(t => t == "nan" ? double.NaN : double.Parse(t, CultureInfo.InvariantCulture))
            .ToArray();
        var expected = Hex(lines.Single(l => l.StartsWith("viridis-t ", StringComparison.Ordinal))["viridis-t ".Length..]);
        Assert.Equal(expected, ts.Select(ColorMaps.Viridis.Map).ToArray());
    }

    /// <summary>
    /// A sequential scale maps a value through its inner scale's transform and its domain, reversed when asked; a diverging
    /// one maps each side of its middle to its half of the map; values outside the domain take the end colors (design §4.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Compiled color scales over a domain of [1, 1000]: sequential linear, log, and reversed; diverging with a middle of 10
    ///   on a log scale, and with none on a linear one.
    ///
    /// Steps:
    /// - Map 1, 10, 100, 1000, 0.5, 2000, and NaN.
    ///
    /// Expected:
    /// - Linear: t = (v − 1) / 999. Log: t = log10(v) / 3. Reversed: 1 − t. Diverging log: 1 at 0, 10 at 0.5, 100 at 0.75,
    ///   1000 at 1. Diverging linear: the middle 500.5 at 0.5. 0.5 and 2000 take the ends; NaN is transparent.
    /// </remarks>
    [Fact]
    public void Scale_MapsThroughItsTransformAndDomain()
    {
        var linear = Compiled(Scale.Sequential(ColorMaps.Viridis, Scale.Linear().WithDomain(1, 1000)));
        Assert.Equal(ColorMaps.Viridis.Map(0), linear.Of(1));
        Assert.Equal(ColorMaps.Viridis.Map(99.0 / 999), linear.Of(100));
        Assert.Equal(ColorMaps.Viridis.Map(1), linear.Of(1000));
        Assert.Equal(ColorMaps.Viridis[0], linear.Of(0.5));
        Assert.Equal(ColorMaps.Viridis[255], linear.Of(2000));
        Assert.Equal(default, linear.Of(double.NaN));

        var log = Compiled(Scale.Sequential(ColorMaps.Magma, Scale.Log().WithDomain(1, 1000)));
        Assert.Equal(ColorMaps.Magma.Map(1.0 / 3), log.Of(10));
        Assert.Equal(ColorMaps.Magma.Map(2.0 / 3), log.Of(100));

        var reversed = Compiled(Scale.Sequential(ColorMaps.Viridis, Scale.Linear().WithDomain(1, 1000) with { Reverse = true }));
        Assert.Equal(ColorMaps.Viridis[255], reversed.Of(1));
        Assert.Equal(ColorMaps.Viridis.Map(1 - (99.0 / 999)), reversed.Of(100));

        var diverging = Compiled(Scale.Diverging(ColorMaps.RdBu, 10, Scale.Log().WithDomain(1, 1000)));
        Assert.Equal(ColorMaps.RdBu.Map(0), diverging.Of(1));
        Assert.Equal(ColorMaps.RdBu.Map(0.5), diverging.Of(10));
        Assert.Equal(ColorMaps.RdBu.Map(0.75), diverging.Of(100));
        Assert.Equal(ColorMaps.RdBu.Map(0.25), diverging.Of(Math.Sqrt(10)));
        Assert.Equal(ColorMaps.RdBu[255], diverging.Of(1000));

        var middle = Compiled(Scale.Diverging(ColorMaps.BrBG, null, Scale.Linear().WithDomain(1, 1000)));
        Assert.Equal(ColorMaps.BrBG.Map(0.5), middle.Of(500.5));
    }

    /// <summary>Returns a compiled scale over its inner scale's given domain, as the plan sets it up.</summary>
    internal static ColorScale Compiled(ColorScaleSpec spec)
    {
        var scale = new ColorScale();
        scale.Set(spec, Palettes.Tab10, null);
        var (min, max) = scale.Inner!.GivenDomain!.Value;
        var low = scale.Forward(min);
        var high = scale.Forward(max);
        scale.Domain = ScaleRange.FromDouble(low, high);
        scale.CheckMiddle();
        return scale;
    }

    private static string[] ReferenceLines()
    {
        using var stream = typeof(ColorScaleTests).Assembly.GetManifestResourceStream("ColorMapReference.txt")!;
        using var reader = new StreamReader(stream);
        return reader
            .ReadToEnd()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith('#'))
            .ToArray();
    }

    private static Dictionary<string, Color[]> Reference() =>
        ReferenceLines()
            .Where(l => !l.StartsWith("t ", StringComparison.Ordinal) && !l.StartsWith("viridis-t", StringComparison.Ordinal))
            .Select(l => l.Split(' '))
            .ToDictionary(p => p[0], p => Hex(p[1]));

    private static Color[] Hex(string hex) =>
        Enumerable
            .Range(0, hex.Length / 8)
            .Select(i => new Color(Byte(hex, (8 * i) + 0), Byte(hex, (8 * i) + 2), Byte(hex, (8 * i) + 4), Byte(hex, (8 * i) + 6)))
            .ToArray();

    private static byte Byte(string hex, int at) => byte.Parse(hex.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
