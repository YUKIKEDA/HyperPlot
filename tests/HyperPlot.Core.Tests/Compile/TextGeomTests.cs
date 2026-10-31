using System.Globalization;
using System.Numerics;
using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>The text geom of row G8 (design §4.1, §4.8, §5.5.3, §10's G8 row).</summary>
public sealed class TextGeomTests : IDisposable
{
    private const int Size = GeomTesting.Size;
    private readonly PlotWorkerPool _pool = new(1);
    private readonly List<IDisposable> _columns = [];

    public void Dispose()
    {
        foreach (var column in _columns)
        {
            column.Dispose();
        }

        _pool.Dispose();
    }

    /// <summary>
    /// The builder's four overloads check their references and the configure's values (design §4.1), and make the spec's
    /// defaults.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A table of numbers and categories, and another table.
    ///
    /// Steps:
    /// - Call each overload with a default reference and one of the other table; configure sizes of 0, −1, NaN, ∞, and 513,
    ///   offsets of NaN and ∞, an alignment and a rotation that are not named, and a format on categorical labels.
    /// - Build a layer without a configure.
    ///
    /// Expected:
    /// - ArgumentException for the references and the format; ArgumentOutOfRangeException for the values.
    /// - The defaults: size 12, center and middle, offsets 0, no rotation, no color, no format.
    /// </remarks>
    [Fact]
    public void Builder_ChecksArgumentsAndMakesDefaults()
    {
        var table = Table([1, 2], [1, 2], ["a", "b"]);
        var other = Table([1, 2], [1, 2], ["a", "b"]);
        var (x, y, n, k) = (table.Col<double>("x"), table.Col<double>("y"), table.Col<double>("n"), table.Cat("k"));
        Action<PlotBuilder>[] bad =
        [
            b => b.Text(default(ColumnRef<double>), y, n),
            b => b.Text(x, other.Col<double>("y"), n),
            b => b.Text(x, y, other.Cat("k")),
            b => b.Text(other.Cat("k"), y, n),
            b => b.Text(k, y, default(CategoryRef)),
            b => b.Text(x, y, k, t => t.Format(static (v, d, out w) => v.TryFormat(d, out w, provider: CultureInfo.InvariantCulture))),
            b => b.Text(k, y, k, t => t.Format(static (v, d, out w) => v.TryFormat(d, out w, provider: CultureInfo.InvariantCulture))),
        ];
        foreach (var call in bad)
        {
            _ = Assert.Throws<ArgumentException>(() => call(Plot.Create(table)));
        }

        foreach (var size in new[] { 0f, -1f, float.NaN, float.PositiveInfinity, 513f })
        {
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => Plot.Create(table).Text(x, y, n, t => t.Size(size)));
        }

        foreach (var offset in new[] { float.NaN, float.PositiveInfinity })
        {
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => Plot.Create(table).Text(x, y, n, t => t.Offset(offset, 0)));
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => Plot.Create(table).Text(x, y, n, t => t.Offset(0, offset)));
        }

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Plot.Create(table).Text(x, y, n, t => t.Align((TextAlign)3, TextBaseline.Top)));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Plot.Create(table).Text(x, y, n, t => t.Align(TextAlign.Left, (TextBaseline)4)));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Plot.Create(table).Text(x, y, n, t => t.Rotation((TextRotation)3)));

        var layer = (TextLayerSpec)Plot.Create(table).Text(x, y, k).Build().Layers[0];
        Assert.Equal(
            (12f, TextAlign.Center, TextBaseline.Middle, 0f, 0f, TextRotation.None, (Color?)null),
            (layer.Size, layer.Horizontal, layer.Vertical, layer.OffsetX, layer.OffsetY, layer.Rotation, layer.Color)
        );
        Assert.Null(layer.Format);
        Assert.Equal(512f, ((TextLayerSpec)Plot.Create(table).Text(x, y, n, t => t.Size(512)).Build().Layers[0]).Size);
    }

    /// <summary>
    /// A spec made directly is checked by the compile with the same exceptions, and with the channels' kinds and lengths,
    /// the color channels, and the size at the target's scale (design §4.1, §4.8).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A table of 2 rows, and plans at the scales 1 and 1.5. A table's columns are of one length, which the table checks.
    ///
    /// Steps:
    /// - Render text layers with each bad value; with categories on y; with a numeric ColorBy; with a FillBy; with Color and ColorBy; with a format on categories; with a size of 400 at 1.5.
    ///
    /// Expected:
    /// - ArgumentOutOfRangeException for the values, ArgumentException for the rest, the size's naming the layer and its
    ///   value; a size of 400 draws at the scale 1.
    /// </remarks>
    [Fact]
    public void Compile_ChecksSpecs()
    {
        var table = Table([1, 2], [1, 2], ["a", "b"]);
        using var plan = Plan();
        var layer = Layer(table, table.Cat("k").ToColumnRef());
        foreach (
            var bad in new[]
            {
                layer with
                {
                    Size = 0,
                },
                layer with
                {
                    Size = float.NaN,
                },
                layer with
                {
                    Size = 513,
                },
                layer with
                {
                    OffsetX = float.NaN,
                },
                layer with
                {
                    OffsetY = float.PositiveInfinity,
                },
                layer with
                {
                    Horizontal = (TextAlign)3,
                },
                layer with
                {
                    Vertical = (TextBaseline)4,
                },
                layer with
                {
                    Rotation = (TextRotation)3,
                },
            }
        )
        {
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => plan.Render(GeomTesting.Spec(table, bad)));
        }

        foreach (
            var bad in new[]
            {
                layer with
                {
                    Y = table.Cat("k").ToColumnRef(),
                },
                layer with
                {
                    ColorBy = table.Col<double>("y"),
                },
                layer with
                {
                    FillBy = table.Cat("k").ToColumnRef(),
                },
                layer with
                {
                    ColorBy = table.Cat("k").ToColumnRef(),
                    Color = Colors.Red,
                },
                layer with
                {
                    Format = static (v, d, out w) => v.TryFormat(d, out w, provider: CultureInfo.InvariantCulture),
                },
            }
        )
        {
            _ = Assert.Throws<ArgumentException>(() => plan.Render(GeomTesting.Spec(table, bad)));
        }

        using var scaled = Plan(1.5f);
        var big = layer with { Size = 400 };
        var error = Assert.Throws<ArgumentException>(() => scaled.Render(GeomTesting.Spec(table, big)));
        Assert.Contains("layer 0", error.Message, StringComparison.Ordinal);
        Assert.Contains("400", error.Message, StringComparison.Ordinal);
        _ = plan.Render(GeomTesting.Spec(table, big));
    }

    /// <summary>
    /// The text layers of a plot draw at most 4,096 rows together (design §5.5.3): one layer, two layers, and a layer split
    /// by ColorBy, counted before the split.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Tables of 4,096, 4,097, 2,049, and 2,048 rows.
    ///
    /// Steps:
    /// - Render one layer of 4,096 and of 4,097; two layers of 2,049, then of 2,048; the 4,097 rows split by a category.
    ///
    /// Expected:
    /// - 4,096 draws; 4,097 throws InvalidOperationException naming the count and the cap; two of 2,049 throw naming the sum
    ///   and each layer's count; two of 2,048 draw; the split layer throws as one of 4,097.
    /// </remarks>
    [Fact]
    public void Cap_CountsTheRowsOfThePlot()
    {
        using var plan = Plan();
        var full = Numbers(4096);
        _ = plan.Render(GeomTesting.Spec(full, Layer(full, full.Col<double>("n"))));
        var over = Numbers(4097);
        var error = Assert.Throws<InvalidOperationException>(() => plan.Render(GeomTesting.Spec(over, Layer(over, over.Col<double>("n")))));
        Assert.Contains("4097", error.Message, StringComparison.Ordinal);
        Assert.Contains("4096", error.Message, StringComparison.Ordinal);

        var half = Numbers(2049);
        var two = GeomTesting.Spec(half, Layer(half, half.Col<double>("n")), Layer(half, half.Cat("k").ToColumnRef()));
        error = Assert.Throws<InvalidOperationException>(() => plan.Render(two));
        Assert.Contains("4098", error.Message, StringComparison.Ordinal);
        Assert.Contains("layer 0 2049, layer 1 2049", error.Message, StringComparison.Ordinal);
        var fits = Numbers(2048);
        _ = plan.Render(GeomTesting.Spec(fits, Layer(fits, fits.Col<double>("n")), Layer(fits, fits.Cat("k").ToColumnRef())));

        var split = Layer(over, over.Col<double>("n")) with { ColorBy = over.Cat("g").ToColumnRef() };
        _ = Assert.Throws<InvalidOperationException>(() => plan.Render(GeomTesting.Spec(over, split)));
    }

    /// <summary>
    /// Numeric labels are written by "G6" in the invariant culture or by the layer's format, under the axes' contract
    /// (design §4.1, §4.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A double column of 1234567, 0.1, and −0, and an int column of 42 and −7.
    ///
    /// Steps:
    /// - Render each without a format; with a format that writes "#" and the value; with formats that return false, write
    ///   65 characters, throw, and write nothing.
    ///
    /// Expected:
    /// - "1.23457E+06", "0.1", "-0"; "42", "-7"; "#42", "#-7"; InvalidOperationException naming the layer for false and 65;
    ///   the format's own exception; no label for nothing written.
    /// </remarks>
    [Fact]
    public void Format_WritesNumbers()
    {
        using var plan = Plan();
        var table = Table([10, 20, 30], [10, 20, 30], ["a", "b", "c"], numbers: [1234567, 0.1, -0.0]);
        Assert.Equal(["1.23457E+06", "0.1", "-0"], Texts(plan.Render(GeomTesting.Spec(table, Layer(table, table.Col<double>("n"))))));

        var ints = Column.Wrap(new[] { 42, -7 });
        _columns.Add(ints);
        var small = DataTable.Create().Add("x", Column.Uniform(10, 10, 2)).Add("i", ints).Build();
        var layer = new TextLayerSpec
        {
            X = small.Col<double>("x"),
            Y = small.Col<double>("x"),
            Text = small.Col<int>("i"),
        };
        Assert.Equal(["42", "-7"], Texts(plan.Render(GeomTesting.Spec(small, layer))));
        var hash = layer with { Format = static (v, d, out w) => d.TryWrite(CultureInfo.InvariantCulture, $"#{v}", out w) };
        Assert.Equal(["#42", "#-7"], Texts(plan.Render(GeomTesting.Spec(small, hash))));

        var refuse = layer with { Format = static (_, _, out w) => (w = 0) != 0 };
        Assert.Contains(
            "layer 0",
            Assert.Throws<InvalidOperationException>(() => plan.Render(GeomTesting.Spec(small, refuse))).Message,
            StringComparison.Ordinal
        );
        var long65 = layer with { Format = static (_, _, out w) => (w = 65) > 0 };
        _ = Assert.Throws<InvalidOperationException>(() => plan.Render(GeomTesting.Spec(small, long65)));
        var thrower = layer with { Format = static (_, _, out _) => throw new TimeoutException("format") };
        _ = Assert.Throws<TimeoutException>(() => plan.Render(GeomTesting.Spec(small, thrower)));
        var empty = layer with { Format = static (_, _, out w) => (w = 0) == 0 };
        Assert.Empty(Texts(plan.Render(GeomTesting.Spec(small, empty))));
    }

    /// <summary>
    /// A label's position is its point, moved by the offset, minus the alignment's share of its width along its reading
    /// direction, so that the rotated box's aligned point is on the point (design §5.5.3), at the scale 1.5.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A plan at the scale 1.5 over [0, 100]²; one label "abc" at (50, 50), offset (5, 7) DIPs.
    ///
    /// Steps:
    /// - Render with each of 3 × 4 alignments and 3 rotations.
    ///
    /// Expected:
    /// - Position (50 + 7.5 − a · w · ux, 50 − 10.5 − a · w · uy), w = 3 advances at 18 px, a = 0, 0.5, 1, u = (1, 0), (0, 1),
    ///   (0, −1); the style is the series color, 18 px, the theme's font and locale, normal, the alignments, and the rotation.
    /// </remarks>
    [Fact]
    public void Record_PutsTheAlignedPointOnThePoint()
    {
        var table = Table([50], [50], ["abc"]);
        using var plan = Plan(1.5f);
        var width = 3 * FakeTextBackend.Advance(18);
        foreach (var h in new[] { TextAlign.Left, TextAlign.Center, TextAlign.Right })
        {
            foreach (var v in new[] { TextBaseline.Top, TextBaseline.Middle, TextBaseline.Baseline, TextBaseline.Bottom })
            {
                foreach (var r in new[] { TextRotation.None, TextRotation.Clockwise90, TextRotation.CounterClockwise90 })
                {
                    var layer = Layer(table, table.Cat("k").ToColumnRef()) with
                    {
                        Horizontal = h,
                        Vertical = v,
                        Rotation = r,
                        OffsetX = 5,
                        OffsetY = 7,
                    };
                    var record = Assert.Single(plan.Render(GeomTesting.Spec(table, layer)).Labels());
                    var share = h switch
                    {
                        TextAlign.Center => 0.5,
                        TextAlign.Right => 1.0,
                        _ => 0.0,
                    };
                    var (ux, uy) = r switch
                    {
                        TextRotation.Clockwise90 => (0.0, 1.0),
                        TextRotation.CounterClockwise90 => (0.0, -1.0),
                        _ => (1.0, 0.0),
                    };
                    GeomTesting.Close(50 + 7.5 - (share * width * ux), record.Position.X);
                    GeomTesting.Close(50 - 10.5 - (share * width * uy), record.Position.Y);
                    var theme = TestPlans.Bare;
                    Assert.Equal(
                        new TextStyle(theme.Palette.Series(0), 18, theme.FontFamily, FontWeight.Normal, h, v, r, theme.Locale),
                        record.Style
                    );
                    Assert.Equal("abc", record.Text);
                }
            }
        }
    }

    /// <summary>
    /// Over categories of x, a label sits at the middle of its category's band, reversed too; a category label is the
    /// column's own name (design §5.5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 3 categories on a band scale, bars and labels of their names, left-aligned so that the position is the point.
    ///
    /// Steps:
    /// - Render; render with the band reversed.
    ///
    /// Expected:
    /// - Each label's x is the middle of its bar; each label's string is the column's category name, the same reference.
    /// </remarks>
    [Fact]
    public void Record_BandMiddleAndTheColumnsNames()
    {
        var table = Table([0, 0, 0], [5, 6, 7], ["a", "b", "c"]);
        using var plan = Plan();
        foreach (var reverse in new[] { false, true })
        {
            var spec = GeomTesting.Spec(
                table,
                new BarLayerSpec { X = table.Cat("k").ToColumnRef(), Y = table.Col<double>("y") },
                Layer(table, table.Cat("k").ToColumnRef()) with
                {
                    X = table.Cat("k").ToColumnRef(),
                    Horizontal = TextAlign.Left,
                }
            ) with
            {
                ScaleX = Scale.Band() with { Reverse = reverse },
            };
            var list = plan.Render(spec);
            var bars = GeomTesting.DataRects(list);
            var texts = list.Labels();
            var names = (CategoricalColumn)table.Cat("k").ToColumnRef().Column;
            for (var c = 0; c < 3; c++)
            {
                GeomTesting.Close(bars[c].X + (bars[c].Width / 2), texts[c].Position.X);
                Assert.Same(names.Categories[c], texts[c].Text);
            }
        }
    }

    /// <summary>
    /// Rows whose point is outside the panel or does not map, whose category is missing, or whose number is NaN draw
    /// nothing, without an exception; a point on the panel's edge draws (design §5.5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Rows at x 150, NaN, 50, 50, 50, and 100 with y 50 but a NaN on the third, numeric labels with a NaN on the fourth;
    ///   categories with a missing x, a missing name, and a missing ColorBy; a log y with a 0.
    ///
    /// Steps:
    /// - Render each.
    ///
    /// Expected:
    /// - Only "5" and "6"; only the rows whose categories are all there; only the row of y 10.
    /// </remarks>
    [Fact]
    public void Record_SkipsRowsThatDoNotDraw()
    {
        using var plan = Plan();
        var table = Table(
            [150, double.NaN, 50, 50, 50, 100],
            [50, 50, double.NaN, 50, 50, 50],
            ["a", "b", "c", "d", "e", "f"],
            numbers: [1, 2, 3, double.NaN, 5, 6]
        );
        Assert.Equal(["5", "6"], Texts(plan.Render(GeomTesting.Spec(table, Layer(table, table.Col<double>("n"))))));

        var missing = Table([10, 20, 30, 40], [10, 20, 30, 40], ["a", null, "b", "c"], groups: ["g", "g", null, "g"]);
        var names = Layer(missing, missing.Cat("k").ToColumnRef());
        Assert.Equal(["a", "b", "c"], Texts(plan.Render(GeomTesting.Spec(missing, names))));
        Assert.Equal(["a", "c"], Texts(plan.Render(GeomTesting.Spec(missing, names with { ColorBy = missing.Cat("g").ToColumnRef() }))));
        var band = GeomTesting.Spec(missing, names with { X = missing.Cat("g").ToColumnRef() }) with { ScaleX = Scale.Band() };
        Assert.Equal(["a", "c"], Texts(plan.Render(band)));

        var log = Table([10, 20], [0, 10], ["zero", "ten"]);
        var spec = GeomTesting.Spec(log, Layer(log, log.Cat("k").ToColumnRef())) with { ScaleY = Scale.Log().WithDomain(1, 100) };
        Assert.Equal(["ten"], Texts(plan.Render(spec)));
    }

    /// <summary>
    /// A label's color is the layer's, its series color, or its category's through the split layer, which draws the
    /// categories in their numbers' order and each category's rows in order (design §4.1, §5.5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 4 rows labeled "0" to "3", with categories a, a, b, b (in runs) and a, b, a, b (mixed).
    ///
    /// Steps:
    /// - Render with a constant color; as the second layer without one; split by each column of categories.
    ///
    /// Expected:
    /// - The constant; the palette's second color; the palette's color of each row's category, in the order 0, 1, 2, 3 and
    ///   0, 2, 1, 3.
    /// </remarks>
    [Fact]
    public void Record_Colors()
    {
        using var plan = Plan();
        var table = Table(
            [10, 20, 30, 40],
            [10, 20, 30, 40],
            ["a", "b", "c", "d"],
            numbers: [0, 1, 2, 3],
            groups: ["a", "a", "b", "b"],
            mixed: ["a", "b", "a", "b"]
        );
        var layer = Layer(table, table.Col<double>("n"));
        Assert.All(plan.Render(GeomTesting.Spec(table, layer with { Color = Colors.Red })).Labels(), t => Assert.Equal(Colors.Red, t.Style.Color));
        var second = plan.Render(GeomTesting.Spec(table, layer with { Color = Colors.Red }, layer)).Labels()[4..];
        Assert.All(second, t => Assert.Equal(TestPlans.Bare.Palette.Series(1), t.Style.Color));

        var palette = TestPlans.Bare.Palette;
        foreach (var (column, order) in new[] { ("g", new[] { "0", "1", "2", "3" }), ("m", new[] { "0", "2", "1", "3" }) })
        {
            var texts = plan.Render(GeomTesting.Spec(table, layer with { ColorBy = table.Cat(column).ToColumnRef() })).Labels();
            Assert.Equal(order, texts.Select(t => t.Text));
            var categories = (CategoricalColumn)table.Cat(column).ToColumnRef().Column;
            foreach (var text in texts)
            {
                var row = int.Parse(text.Text, CultureInfo.InvariantCulture);
                Assert.Equal(palette.Series(categories.GetCode(row)), text.Style.Color);
            }
        }
    }

    /// <summary>
    /// Flipped and polar, a label's point is the one a geom's frame maps the same values to, and the text is not rotated
    /// (design §5.5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A label "p" at (30, 40), left and top aligned, beside a geom that reads its frame's Map of (30, 40).
    ///
    /// Steps:
    /// - Render flipped; render polar.
    ///
    /// Expected:
    /// - The label's position is the mapped point; its rotation is none.
    /// </remarks>
    [Fact]
    public void Record_FlipAndPolarMapThePoint()
    {
        using var plan = Plan();
        var table = Table([30], [40], ["p"]);
        foreach (CoordSpec coord in new CoordSpec[] { Coord.Cartesian(flip: true), Coord.Polar() })
        {
            var probe = new MapProbe(30, 40);
            var layer = Layer(table, table.Cat("k").ToColumnRef()) with { Horizontal = TextAlign.Left, Vertical = TextBaseline.Top };
            var spec = GeomTesting.Spec(table, layer, new CustomLayerSpec(probe)) with { Coord = coord };
            var record = Assert.Single(plan.Render(spec).Labels());
            GeomTesting.Close(probe.X, record.Position.X);
            GeomTesting.Close(probe.Y, record.Position.Y);
            Assert.Equal(TextRotation.None, record.Style.Rotation);
        }
    }

    /// <summary>The domains are learned from the points alone, whatever the labels' size (design §5.5.3).</summary>
    /// <remarks>
    /// Preconditions:
    /// - Labels at x 10 and 90, y 20 and 80, on learned linear scales without nice.
    ///
    /// Steps:
    /// - Render at sizes 8 and 64.
    ///
    /// Expected:
    /// - The domains are [10, 90] and [20, 80] both times.
    /// </remarks>
    [Fact]
    public void Learning_ReadsThePointsOnly()
    {
        using var plan = Plan();
        var table = Table([10, 90], [20, 80], ["left", "right"]);
        foreach (var size in new[] { 8f, 64f })
        {
            var spec = GeomTesting.Spec(table, Layer(table, table.Cat("k").ToColumnRef()) with { Size = size }) with
            {
                ScaleX = Scale.Linear(),
                ScaleY = Scale.Linear(),
            };
            _ = plan.Render(spec);
            Assert.Equal((10.0, 90.0), GeomTesting.Ends(plan.Guides.X.FinalDomain));
            Assert.Equal((20.0, 80.0), GeomTesting.Ends(plan.Guides.Y.FinalDomain));
        }
    }

    /// <summary>
    /// Numeric labels are written again for a new version of their column or another format, and not for a new node of the
    /// same delegate, which allocates nothing (design §4.1, §5.5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A label column of 1 and 2 that the test writes and invalidates, and a static format.
    ///
    /// Steps:
    /// - Render; write 7 into the first row and invalidate; render with the format; render a new node of the same format,
    ///   warm, counting the bytes.
    ///
    /// Expected:
    /// - "1", "2"; then "7", "2"; then the format's; the writes count 1, 2, 3, 3; 0 bytes for the new node.
    /// </remarks>
    [Fact]
    public void Labels_WrittenForANewVersionOrFormatOnly()
    {
        using var plan = Plan();
        var values = new double[] { 1, 2 };
        var labels = Column.Wrap(values);
        _columns.Add(labels);
        var table = DataTable.Create().Add("x", Column.Uniform(10, 10, 2)).Add("n", labels).Build();
        var layer = new TextLayerSpec
        {
            X = table.Col<double>("x"),
            Y = table.Col<double>("x"),
            Text = table.Col<double>("n"),
        };
        Assert.Equal(["1", "2"], Texts(plan.Render(GeomTesting.Spec(table, layer))));
        var text = (TextPlan)plan.Layers[0];
        Assert.Equal(1, text.Writes);

        values[0] = 7;
        labels.Invalidate();
        Assert.Equal(["7", "2"], Texts(plan.Render(GeomTesting.Spec(table, layer))));
        Assert.Equal(2, text.Writes);

        TickFormatter format = static (v, d, out w) => d.TryWrite(CultureInfo.InvariantCulture, $"v{v}", out w);
        var formatted = GeomTesting.Spec(table, layer with { Format = format });
        Assert.Equal(["v7", "v2"], Texts(plan.Render(formatted)));
        Assert.Equal(3, text.Writes);

        _ = plan.Render(GeomTesting.Spec(table, layer with { Format = format }));
        var again = GeomTesting.Spec(table, layer with { Format = format });
        var before = GC.GetAllocatedBytesForCurrentThread();
        _ = plan.Render(again);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(3, text.Writes);
    }

    /// <summary>A change of the looks lays out and records again, and extracts nothing (design §4.8).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A drawn text layer.
    ///
    /// Steps:
    /// - Render nodes that change the color, the size, the alignment, the offset, and the rotation, one at a time. The label is left out: a legend that comes or goes changes the panel.
    ///
    /// Expected:
    /// - Each adds 1 layout, 0 extractions, and 1 recording.
    /// </remarks>
    [Fact]
    public void Diff_LooksLayOutAndRecordOnly()
    {
        using var plan = Plan();
        var table = Table([10, 20], [10, 20], ["a", "b"]);
        var layer = Layer(table, table.Cat("k").ToColumnRef());
        _ = plan.Render(GeomTesting.Spec(table, layer));
        foreach (
            var changed in new[]
            {
                layer with
                {
                    Color = Colors.Red,
                },
                layer with
                {
                    Size = 20,
                },
                layer with
                {
                    Horizontal = TextAlign.Right,
                },
                layer with
                {
                    OffsetX = 3,
                },
                layer with
                {
                    Rotation = TextRotation.Clockwise90,
                },
            }
        )
        {
            var (layouts, extractions, recordings) = (plan.Layouts, plan.Extractions, plan.Recordings);
            _ = plan.Render(GeomTesting.Spec(table, changed));
            Assert.Equal((layouts + 1, extractions, recordings + 1), (plan.Layouts, plan.Extractions, plan.Recordings));
        }
    }

    /// <summary>
    /// Another label column is a change of data (design §4.8): the layer is made again and draws the new column's labels,
    /// though both columns are at the same version.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A drawn text layer labeled by "n" (1, 2), and a column "y" (10, 20) that has never been invalidated either.
    ///
    /// Steps:
    /// - Render a node labeled by "y" instead.
    ///
    /// Expected:
    /// - "10", "20".
    /// </remarks>
    [Fact]
    public void Diff_AnotherLabelColumnIsData()
    {
        using var plan = Plan();
        var table = Table([30, 40], [10, 20], ["a", "b"], numbers: [1, 2]);
        var layer = Layer(table, table.Col<double>("n"));
        Assert.Equal(["1", "2"], Texts(plan.Render(GeomTesting.Spec(table, layer))));
        Assert.Equal(["10", "20"], Texts(plan.Render(GeomTesting.Spec(table, layer with { Text = table.Col<double>("y") }))));
    }

    /// <summary>
    /// A layer split by ColorBy passes another format on to its categories' series (design §4.8, §5.5.3): its labels are
    /// written again.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Numeric labels 1 and 2 split by two categories.
    ///
    /// Steps:
    /// - Render; render a node with a format that writes "#" and the value.
    ///
    /// Expected:
    /// - "1", "2"; then "#1", "#2".
    /// </remarks>
    [Fact]
    public void Split_AnotherFormatReachesTheSeries()
    {
        using var plan = Plan();
        var table = Table([30, 40], [10, 20], ["a", "b"], numbers: [1, 2], groups: ["u", "v"]);
        var layer = Layer(table, table.Col<double>("n")) with { ColorBy = table.Cat("g").ToColumnRef() };
        Assert.Equal(["1", "2"], Texts(plan.Render(GeomTesting.Spec(table, layer))));
        var hash = layer with { Format = static (v, d, out w) => d.TryWrite(CultureInfo.InvariantCulture, $"#{v}", out w) };
        Assert.Equal(["#1", "#2"], Texts(plan.Render(GeomTesting.Spec(table, hash))));
    }

    /// <summary>
    /// A text layer's legend sample is "a" in its color at the legend's size, in the middle of the swatch; split by a
    /// category, each category's row has one in its color (design §4.6, §5.5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The light theme; a labeled text layer of a constant color; then a layer split by 3 categories.
    ///
    /// Steps:
    /// - Render each and read the legend's texts.
    ///
    /// Expected:
    /// - An "a" of the layer's color and the legend's size whose center is half a swatch left of the gap before the label;
    ///   then 3 "a" in the palette's colors before the categories' names.
    /// </remarks>
    [Fact]
    public void Legend_SampleIsAnA()
    {
        using var plan = new PlotPlan(_pool, 640, 360, new TextEngine(new FakeTextBackend()), 1);
        var table = Table([10, 20, 30], [10, 20, 30], ["p", "q", "r"], groups: ["u", "v", "w"]);
        var theme = Theme.Light;
        var layer = Layer(table, table.Cat("k").ToColumnRef()) with { Label = "L", Color = Colors.Teal };
        var texts = plan.Render(GeomTesting.Spec(table, layer) with { Theme = theme }).Texts.ToArray();
        var sample = Assert.Single(texts, t => t.Text == "a");
        var label = Assert.Single(texts, t => t.Text == "L");
        Assert.Equal((Colors.Teal, theme.LegendSize), (sample.Style.Color, sample.Style.Size));
        var (gap, swatch) = (Math.Floor(theme.Spacing + 0.5), Math.Floor(theme.LegendSwatch + 0.5));
        GeomTesting.Close(label.Position.X - gap - (swatch / 2), sample.Position.X + (FakeTextBackend.Advance(theme.LegendSize) / 2));

        var split = layer with { Color = null, ColorBy = table.Cat("g").ToColumnRef() };
        texts = plan.Render(GeomTesting.Spec(table, split) with { Theme = theme }).Texts.ToArray();
        var samples = texts.Where(t => t.Text == "a").ToArray();
        Assert.Equal([theme.Palette.Series(0), theme.Palette.Series(1), theme.Palette.Series(2)], samples.Select(t => t.Style.Color));
    }

    /// <summary>
    /// 4,096 labels with the axes' texts, more than 4,096 texts, stay in the shaping cache: the second frame shapes nothing
    /// (design §5.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The light theme with axes; 4,096 rows labeled by distinct numbers.
    ///
    /// Steps:
    /// - Render twice, counting the texts the fake backend shapes.
    ///
    /// Expected:
    /// - More than 4,096 shaped by the first frame, and none by the second.
    /// </remarks>
    [Fact]
    public void Cache_HoldsTheLabelsAndTheGuides()
    {
        var backend = new FakeTextBackend();
        using var plan = new PlotPlan(_pool, 640, 360, new TextEngine(backend), 1);
        var table = Numbers(4096);
        var spec = GeomTesting.Spec(table, Layer(table, table.Col<double>("n"))) with { Theme = Theme.Light };
        _ = plan.Render(spec);
        var shaped = backend.Shaped.Count;
        Assert.True(shaped > 4096);
        _ = plan.Render(spec);
        Assert.Equal(shaped, backend.Shaped.Count);
    }

    /// <summary>Once warm, 4,096 labels, numbers and categories, drawn again allocate nothing (design §5.5.3).</summary>
    /// <remarks>
    /// Preconditions:
    /// - Two layers of 2,048 rows: numbers, and categories split by a ColorBy.
    ///
    /// Steps:
    /// - Render twice to warm; render five times.
    ///
    /// Expected:
    /// - 0 bytes on this thread.
    /// </remarks>
    [Fact]
    public void Warm_AllocatesNothing()
    {
        using var plan = Plan();
        var table = Numbers(2048);
        var spec = GeomTesting.Spec(
            table,
            Layer(table, table.Col<double>("n")),
            Layer(table, table.Cat("k").ToColumnRef()) with
            {
                ColorBy = table.Cat("g").ToColumnRef(),
            }
        );
        _ = plan.Render(spec);
        _ = plan.Render(spec);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 5; i++)
        {
            _ = plan.Render(spec);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static TextLayerSpec Layer(DataTable table, ColumnRef text) =>
        new()
        {
            X = table.Col<double>("x"),
            Y = table.Col<double>("y"),
            Text = text,
        };

    private static string[] Texts(RenderList list) => list.Labels().Select(t => t.Text).ToArray();

    private PlotPlan Plan(float scale = 1) => new(_pool, Size, Size, new TextEngine(new FakeTextBackend()), scale);

    /// <summary>Returns a table of x, y, categories "k", "g", and "m", and numbers "n".</summary>
    private DataTable Table(double[] x, double[] y, string?[] names, double[]? numbers = null, string?[]? groups = null, string?[]? mixed = null)
    {
        var columns = new[] { Column.Wrap(x), Column.Wrap(y), Column.Wrap(numbers ?? x) };
        _columns.AddRange(columns);
        return DataTable
            .Create()
            .Add("x", columns[0])
            .Add("y", columns[1])
            .Add("n", columns[2])
            .Add("k", Column.Categorical(names))
            .Add("g", Column.Categorical(groups ?? names.Select(_ => (string?)"g").ToArray()))
            .Add("m", Column.Categorical(mixed ?? names.Select(_ => (string?)"m").ToArray()))
            .Build();
    }

    /// <summary>Returns a table of n rows labeled by their numbers, with 7 categories of names and 3 of groups.</summary>
    private DataTable Numbers(int n)
    {
        var x = Enumerable.Range(0, n).Select(i => (i % 100) + 0.5).ToArray();
        var values = Enumerable.Range(0, n).Select(i => (double)i).ToArray();
        var columns = new[] { Column.Wrap(x), Column.Wrap(values) };
        _columns.AddRange(columns);
        return DataTable
            .Create()
            .Add("x", columns[0])
            .Add("y", columns[0])
            .Add("n", columns[1])
            .Add("k", Column.Categorical(values.Select(v => (string?)$"k{v % 7}").ToArray()))
            .Add("g", Column.Categorical(values.Select(v => (string?)$"g{v % 3}").ToArray()))
            .Build();
    }

    /// <summary>A geom that reads its frame's Map of one point.</summary>
    private sealed class MapProbe(double x, double y) : IGeom
    {
        public double X { get; private set; }

        public double Y { get; private set; }

        public void Learn(GeomRanges ranges) { }

        public void Record(GeomFrame frame, RenderList list)
        {
            frame.Map(x, y, out var px, out var py);
            (X, Y) = (px, py);
        }
    }
}

/// <summary>Reads the texts a layer recorded: those inside the panel's clip, not the guides', which have none.</summary>
internal static class TextLabels
{
    public static TextRecord[] Labels(this RenderList list) => list.Texts.ToArray().Where(t => t.Clip != ClipRect.All).ToArray();
}
