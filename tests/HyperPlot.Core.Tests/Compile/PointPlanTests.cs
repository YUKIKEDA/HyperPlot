using System.Numerics;
using HyperPlot.Rendering;
using static HyperPlot.Tests.GeomTesting;

namespace HyperPlot.Tests;

/// <summary>The point layer on the CPU path: its builder, its checks, and its recording (design §4.1, §5.5.2, §10 R3a row).</summary>
public sealed class PointPlanTests
{
    private static readonly Color Series = Palettes.Tab10[0];
    private static readonly string[] Names = ["a", "b", "c"];
    private static readonly float[] SplitYs = [90, 70, 50, 80, 60];
    private static readonly int[] SplitColors = [0, 0, 0, 1, 1, 0, 1];
    private static readonly float[] NumberedYs = [90, 80, 60, 50];
    private static readonly double[] NumberedValues = [0, 250, 499, 100];

    /// <summary>
    /// The builder checks its references and the values of the configure, and the spec it adds holds them (design §4.9).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A table of "x" and "y" of 3 doubles and a category column "c"; another table with a column "x".
    ///
    /// Steps:
    /// - Call Point with a default reference and with the other table's reference.
    /// - In the configure, set sizes of 0, NaN, and 513, opacities of -0.1, 1.1, and NaN, and a shape of 3.
    /// - Build a point layer with every member set, and one with none.
    ///
    /// Expected:
    /// - ArgumentException naming x or y; ArgumentOutOfRangeException naming size, opacity, or shape.
    /// - The first spec holds the color, 4.5, Square, 0.25, the label, the category column, and both secondary scales; the
    ///   second holds no color, 6, Circle, 1, and no label.
    /// </remarks>
    [Fact]
    public void Builder_ChecksAndHolds()
    {
        using var xs = Column.Wrap(new double[] { 1, 2, 3 });
        using var ys = Column.Wrap(new double[] { 4, 5, 6 });
        using var other = Column.Wrap(new double[] { 1, 2, 3 });
        var table = DataTable.Create().Add("x", xs).Add("y", ys).Add("c", Column.Categorical(["a", "b", "a"])).Build();
        var foreign = DataTable.Create().Add("x", other).Build();
        var (x, y) = (table.Col<double>("x"), table.Col<double>("y"));

        Assert.Equal("x", Assert.Throws<ArgumentException>(() => Plot.Create(table).Point(default(ColumnRef<double>), y)).ParamName);
        Assert.Equal("y", Assert.Throws<ArgumentException>(() => Plot.Create(table).Point(x, default(ColumnRef<double>))).ParamName);
        Assert.Equal("x", Assert.Throws<ArgumentException>(() => Plot.Create(table).Point(foreign.Col<double>("x"), y)).ParamName);
        foreach (var size in new[] { 0f, float.NaN, 513f })
        {
            Assert.Equal("size", Assert.Throws<ArgumentOutOfRangeException>(() => Plot.Create(table).Point(x, y, p => p.Size(size))).ParamName);
        }

        foreach (var opacity in new[] { -0.1, 1.1, double.NaN })
        {
            Assert.Equal(
                "opacity",
                Assert.Throws<ArgumentOutOfRangeException>(() => Plot.Create(table).Point(x, y, p => p.Opacity(opacity))).ParamName
            );
        }

        Assert.Equal(
            "shape",
            Assert.Throws<ArgumentOutOfRangeException>(() => Plot.Create(table).Point(x, y, p => p.Shape((MarkerShape)3))).ParamName
        );

        var red = new Color(200, 0, 0, 255);
        var plot = Plot.Create(table)
            .ScaleX2(Scale.Linear())
            .ScaleY2(Scale.Linear())
            .Point(x, y, p => p.Size(4.5f).Shape(MarkerShape.Square).Opacity(0.25).Label("runs").ColorBy(table.Cat("c")).SecondaryX().SecondaryY())
            .Point(x, y, p => p.Color(red))
            .Point(x, y)
            .Build();
        var set = Assert.IsType<PointLayerSpec>(plot.Layers[0]);
        Assert.Equal((4.5f, MarkerShape.Square, 0.25, "runs"), (set.Size, set.Shape, set.Opacity, set.Label));
        Assert.Equal(table.Cat("c").ToColumnRef(), set.ColorBy);
        Assert.True(set.SecondaryX && set.SecondaryY);
        Assert.Equal(red, Assert.IsType<PointLayerSpec>(plot.Layers[1]).Color);
        var plain = Assert.IsType<PointLayerSpec>(plot.Layers[2]);
        Assert.Equal((null, 6f, MarkerShape.Circle, 1.0, null), (plain.Color, plain.Size, plain.Shape, plain.Opacity, plain.Label));
        Assert.Equal(((ColumnRef)x, (ColumnRef)y), (plain.X, plain.Y));
    }

    /// <summary>
    /// The compile refuses a point layer it does not draw (design §5.5.2 "上限と例外"): members out of range, more than
    /// 131,072 rows, a categorical x, a position, a stat, a FillBy, and a color beside a color channel.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Columns of 131,072 and 131,073 doubles, a category column, and a numeric column, in tables of each length.
    ///
    /// Steps:
    /// - Render point layers with a Size of 0 and of 513, an Opacity of 1.5 and of NaN, a Shape of 3, 131,073 rows (alone and
    ///   split by a category), a categorical x, a stack position, a bin stat, a FillBy, and a Color beside a ColorBy.
    /// - Render 131,072 rows.
    ///
    /// Expected:
    /// - ArgumentException for each but the rows, which throw InvalidOperationException naming 131073, 131072, and Issue #438.
    /// - 131,072 rows record 131,072 markers in one call.
    /// </remarks>
    [Fact]
    public void Validate_RefusesWhatItDoesNotDraw()
    {
        using var pool = new PlotWorkerPool(1);
        using var plan = TestPlans.Create(pool, Size, Size);
        var limit = LayerPlan.MaxElements;
        using var most = Column.Wrap(Enumerable.Range(0, limit).Select(i => (double)(i % 100)).ToArray());
        using var over = Column.Wrap(Enumerable.Range(0, limit + 1).Select(i => (double)(i % 100)).ToArray());
        var fits = DataTable
            .Create()
            .Add("v", most)
            .Add("c", Column.Categorical(Enumerable.Range(0, limit).Select(i => (string?)Names[i % 3]).ToArray()))
            .Build();
        var tooMany = DataTable
            .Create()
            .Add("v", over)
            .Add("c", Column.Categorical(Enumerable.Range(0, limit + 1).Select(i => (string?)Names[i % 3]).ToArray()))
            .Build();
        var v = fits.Col<double>("v");
        var c = fits.Cat("c").ToColumnRef();
        var point = new PointLayerSpec { X = v, Y = v };

        LayerSpec[] refused =
        [
            point with
            {
                Size = 0,
            },
            point with
            {
                Size = 513,
            },
            point with
            {
                Opacity = 1.5,
            },
            point with
            {
                Opacity = double.NaN,
            },
            point with
            {
                Shape = (MarkerShape)3,
            },
            point with
            {
                X = c,
            },
            point with
            {
                Position = Position.Stack,
            },
            point with
            {
                Stat = Stat.Bin(),
            },
            point with
            {
                FillBy = c,
            },
            point with
            {
                Color = Series,
                ColorBy = c,
            },
        ];
        foreach (var layer in refused)
        {
            _ = Assert.ThrowsAny<ArgumentException>(() => plan.Render(Spec(fits, layer)));
        }

        var big = tooMany.Col<double>("v");
        foreach (
            var layer in new[]
            {
                new PointLayerSpec { X = big, Y = big },
                new PointLayerSpec
                {
                    X = big,
                    Y = big,
                    ColorBy = tooMany.Cat("c").ToColumnRef(),
                },
            }
        )
        {
            var message = Assert.Throws<InvalidOperationException>(() => plan.Render(Spec(tooMany, layer))).Message;
            Assert.Contains("131073", message, StringComparison.Ordinal);
            Assert.Contains("131072", message, StringComparison.Ordinal);
            Assert.Contains("#438", message, StringComparison.Ordinal);
        }

        var list = plan.Render(Spec(fits, point));
        Assert.Equal(limit, list.Markers.Length);
        Assert.Equal(new MarkerCall(0, limit, new ClipRect(0, 0, Size, Size)), Assert.Single(list.MarkerCalls.ToArray()));
    }

    /// <summary>
    /// Every point is mapped to its pixel and recorded as a marker of the layer's look (design §5.5.2): the series color or
    /// the layer's with the opacity on its alpha, the size, the shape; a point with a value that does not map draws nothing.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Points (10, 20), (50, 50), (NaN, 30), (90, 5) on a 100 × 100 panel with domains [0, 100].
    ///
    /// Steps:
    /// - Render the layer plain; then with a half-transparent red, a size of 8, a cross, and an opacity of 0.5.
    ///
    /// Expected:
    /// - Markers at (10, 80), (50, 50), and (90, 95): discs of size 6 in the first series color, opaque, stroke 1.
    /// - The second: crosses of size 8 in the red with its alpha 200 halved to 100.
    /// </remarks>
    [Fact]
    public void Record_MapsEachPointToAMarker()
    {
        using var pool = new PlotWorkerPool(1);
        using var plan = TestPlans.Create(pool, Size, Size);
        using var xs = Column.Wrap(new double[] { 10, 50, double.NaN, 90 });
        using var ys = Column.Wrap(new double[] { 20, 50, 30, 5 });
        var table = DataTable.Create().Add("x", xs).Add("y", ys).Build();
        var point = new PointLayerSpec { X = table.Col<double>("x"), Y = table.Col<double>("y") };
        Vector2[] centers = [new(10, 80), new(50, 50), new(90, 95)];

        var plain = plan.Render(Spec(table, point));
        Assert.Equal(centers.Select(c => new MarkerInstance(c, 6, 0 | (16u << 8), Premultiplied(Series))), plain.Markers.ToArray());
        Assert.Equal(new MarkerCall(0, 3, new ClipRect(0, 0, Size, Size)), Assert.Single(plain.MarkerCalls.ToArray()));

        var red = new Color(200, 0, 0, 200);
        var styled = plan.Render(Spec(table, point with { Color = red, Size = 8, Shape = MarkerShape.Cross, Opacity = 0.5 }));
        Assert.Equal(centers.Select(c => new MarkerInstance(c, 8, 2 | (16u << 8), Premultiplied(red with { A = 100 }))), styled.Markers.ToArray());
    }

    /// <summary>
    /// A numeric ColorBy colors each point through the color scale and leaves out a point whose value is NaN; a categorical
    /// one splits the layer into a call a category in the palette's colors (design §5.5.2 "見た目").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Five points on the diagonal; a numeric column 0, 250, NaN, 499, 100; a category column a, b, a, b, a.
    ///
    /// Steps:
    /// - Render with the numbers and a sequential scale over [0, 499], at an opacity of 0.5.
    /// - Render with the categories and an ordinal scale.
    ///
    /// Expected:
    /// - Four markers, at the y pixels of rows 0, 1, 3, and 4, in the Viridis colors of 0, 250 / 499, 1, and 100 / 499 at
    ///   alpha 128, in one call.
    /// - Two calls: rows 0, 2, 4 in the palette's first color and rows 1, 3 in its second; then the legend's two samples,
    ///   a marker in each color.
    /// </remarks>
    [Fact]
    public void Record_ColorsByNumbersAndByCategories()
    {
        using var pool = new PlotWorkerPool(1);
        using var plan = TestPlans.Create(pool, Size, Size);
        using var xs = Column.Wrap(new double[] { 10, 20, 30, 40, 50 });
        using var values = Column.Wrap(new double[] { 0, 250, double.NaN, 499, 100 });
        var table = DataTable.Create().Add("x", xs).Add("v", values).Add("c", Column.Categorical(["a", "b", "a", "b", "a"])).Build();
        var x = table.Col<double>("x");
        var point = new PointLayerSpec { X = x, Y = x };

        var numbered = plan.Render(
            Spec(table, point with { ColorBy = table.Col<double>("v"), Opacity = 0.5 }) with
            {
                ScaleColor = Scale.Sequential(scale: Scale.Linear().WithDomain(0, 499)),
            }
        );

        // The color bar takes room beside the panel, so only the pixels down the target are the diagonal's.
        Assert.Equal(NumberedYs, numbered.Markers.ToArray().Select(m => m.Center.Y));
        Assert.Equal(
            NumberedValues.Select(v => Premultiplied(ColorMaps.Viridis.Map(v / 499) with { A = 128 })),
            numbered.Markers.ToArray().Select(m => m.Color)
        );
        Assert.All(numbered.Markers.ToArray(), m => Assert.Equal((6f, 16u << 8), (m.Size, m.Kind)));
        _ = Assert.Single(numbered.MarkerCalls.ToArray());

        var split = plan.Render(Spec(table, point with { ColorBy = table.Cat("c").ToColumnRef() }) with { ScaleColor = Scale.Ordinal() });

        // The legend of the categories takes room beside the panel and draws a marker a category after the layer's.
        Assert.Equal([(0, 3), (3, 2), (5, 1), (6, 1)], split.MarkerCalls.ToArray().Select(c => (c.Start, c.Count)));
        Assert.Equal(SplitYs, split.Markers.ToArray().Take(5).Select(m => m.Center.Y));
        Assert.Equal(SplitColors.Select(i => Premultiplied(Palettes.Tab10[i])), split.Markers.ToArray().Select(m => m.Color));
    }

    /// <summary>
    /// A flipped plot puts x down the target, and a polar one puts each point on its circle; a point outside the polar
    /// domain's radius draws nothing through the panel's clip (design §5.5.2 "CPU の道").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Points (10, 20) and (50, 50) on a 100 × 100 panel with domains [0, 100].
    ///
    /// Steps:
    /// - Render flipped; render in polar.
    ///
    /// Expected:
    /// - Flipped: the markers of the unflipped plot with x and y swapped.
    /// - Polar: two markers with finite centers inside the panel, at the radii the y values give from the panel's middle.
    /// </remarks>
    [Fact]
    public void Record_FlippedAndPolar()
    {
        using var pool = new PlotWorkerPool(1);
        using var plan = TestPlans.Create(pool, Size, Size);
        using var xs = Column.Wrap(new double[] { 10, 50 });
        using var ys = Column.Wrap(new double[] { 20, 50 });
        var table = DataTable.Create().Add("x", xs).Add("y", ys).Build();
        var spec = Spec(table, new PointLayerSpec { X = table.Col<double>("x"), Y = table.Col<double>("y") });

        var straight = plan.Render(spec).Markers.ToArray().Select(m => m.Center).ToArray();
        var flipped = plan.Render(spec with { Coord = Coord.Cartesian(flip: true) }).Markers.ToArray().Select(m => m.Center).ToArray();
        Assert.Equal(2, flipped.Length);
        for (var i = 0; i < 2; i++)
        {
            // Flipped, x runs up the target as y did and y runs to the right as x did.
            Assert.Equal(Size - straight[i].X, flipped[i].Y, 3);
            Assert.Equal(Size - straight[i].Y, flipped[i].X, 3);
        }

        var polar = plan.Render(spec with { Coord = Coord.Polar() }).Markers.ToArray().Select(m => m.Center).ToArray();
        Assert.Equal(2, polar.Length);
        var middle = new Vector2(Size / 2f, Size / 2f);
        Assert.Equal(0.2 * Size / 2, (polar[0] - middle).Length(), 2);
        Assert.Equal(0.5 * Size / 2, (polar[1] - middle).Length(), 2);
    }

    /// <summary>The legend's sample of a point layer is its marker, no larger than the row (design §5.5.2 "見た目").</summary>
    /// <remarks>
    /// Preconditions:
    /// - A point layer of one point, squares of size 40 at an opacity of 0.5, labeled, under the light theme.
    ///
    /// Steps:
    /// - Render.
    ///
    /// Expected:
    /// - Two markers: the point's of size 40, and the sample's, a square smaller than 40 in the same color.
    /// </remarks>
    [Fact]
    public void Legend_SampleIsTheMarker()
    {
        using var pool = new PlotWorkerPool(1);
        using var plan = TestPlans.Create(pool, 400, 300);
        using var xs = Column.Wrap(new double[] { 50 });
        var table = DataTable.Create().Add("x", xs).Build();
        var x = table.Col<double>("x");
        var list = plan.Render(
            Spec(
                table,
                new PointLayerSpec
                {
                    X = x,
                    Y = x,
                    Size = 40,
                    Shape = MarkerShape.Square,
                    Opacity = 0.5,
                    Label = "runs",
                }
            ) with
            {
                Theme = Theme.Light,
            }
        );

        var markers = list.Markers.ToArray();
        Assert.Equal(2, markers.Length);
        var sample = markers.Single(m => m.Size < 40);
        Assert.Equal(40, markers.Single(m => m.Size >= 40).Size);
        Assert.Equal(1u, sample.Kind & 0xFF);
        Assert.Equal(Premultiplied(Series with { A = 128 }), sample.Color);
    }

    private static Vector4 Premultiplied(Color color) =>
        new(
            HyperPlot.Rendering.Premultiplied.Multiply(color.R, color.A) / 255f,
            HyperPlot.Rendering.Premultiplied.Multiply(color.G, color.A) / 255f,
            HyperPlot.Rendering.Premultiplied.Multiply(color.B, color.A) / 255f,
            color.A / 255f
        );
}
