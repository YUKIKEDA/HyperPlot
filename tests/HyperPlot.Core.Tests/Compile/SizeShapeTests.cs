using System.Numerics;
using HyperPlot.Interaction;
using HyperPlot.Rendering;
using static HyperPlot.Tests.GeomTesting;

namespace HyperPlot.Tests;

/// <summary>The size and shape channels of point layers, their scales, and their legends (design §4.2, §4.6, §5.5.2, §10 R3f row).</summary>
[Collection("Large data")]
public sealed class SizeShapeTests
{
    private const int Over = LayerPlan.MaxElements + 1;
    private static readonly PanelId Panel = new(0, 0);
    private static readonly string[] Three = ["a", "b", "c"];
    private static readonly MarkerShape[] Default3 = [MarkerShape.Circle, MarkerShape.Square, MarkerShape.Triangle];

    /// <summary>The factories' defaults and the arguments of WithRange, WithShapes, and WithTitle (design §4.1, §4.2).</summary>
    /// <remarks>
    /// Preconditions:
    /// - None.
    ///
    /// Steps:
    /// - Make Scale.Size() and Scale.Shape(); call WithRange with 0, NaN, a min above the max, and a max of 513; WithShapes with
    ///   none, an unnamed shape, and a shape twice; the three with a null scale; then valid calls.
    ///
    /// Expected:
    /// - A linear inner scale, 2 to 16 DIPs; the six shapes Circle, Square, Triangle, Diamond, Cross, X. ArgumentOutOfRangeException
    ///   for the ranges and the unnamed shape, ArgumentException for none and twice, ArgumentNullException for null. The valid
    ///   calls hold their values with a new node.
    /// </remarks>
    [Fact]
    public void Scales_DefaultsAndArguments()
    {
        var size = Scale.Size();
        Assert.IsType<LinearScaleSpec>(size.Scale);
        Assert.Equal((2f, 16f), (size.RangeMin, size.RangeMax));
        Assert.Equal(
            [MarkerShape.Circle, MarkerShape.Square, MarkerShape.Triangle, MarkerShape.Diamond, MarkerShape.Cross, MarkerShape.X],
            Scale.Shape().Shapes
        );

        foreach (var (min, max) in new[] { (0f, 4f), (float.NaN, 4f), (5f, 4f), (2f, 513f), (2f, float.PositiveInfinity) })
        {
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => size.WithRange(min, max));
        }

        _ = Assert.Throws<ArgumentException>(() => Scale.Shape().WithShapes());
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Scale.Shape().WithShapes(MarkerShape.Circle, (MarkerShape)6));
        _ = Assert.Throws<ArgumentException>(() => Scale.Shape().WithShapes(MarkerShape.X, MarkerShape.Circle, MarkerShape.X));
        _ = Assert.Throws<ArgumentNullException>(() => ((SizeScaleSpec)null!).WithRange(2, 4));
        _ = Assert.Throws<ArgumentNullException>(() => ((ShapeScaleSpec)null!).WithShapes(MarkerShape.X));
        _ = Assert.Throws<ArgumentNullException>(() => ((ShapeScaleSpec)null!).WithTitle("t"));

        var ranged = size.WithRange(3, 3);
        Assert.Equal((3f, 3f), (ranged.RangeMin, ranged.RangeMax));
        Assert.NotEqual(size.Id, ranged.Id);
        var shapes = Scale.Shape().WithShapes(MarkerShape.X, MarkerShape.Circle).WithTitle("Kind");
        Assert.Equal([MarkerShape.X, MarkerShape.Circle], shapes.Shapes);
        Assert.Equal("Kind", shapes.Title);
    }

    /// <summary>The compile's checks of the size and shape channels (design §4.8 "検査（行 R3f）").</summary>
    /// <remarks>
    /// Preconditions:
    /// - Columns of 131,073 rows: numbers, a category of three names, one of six and one of seven, one of three other names;
    ///   and a column of another table.
    ///
    /// Steps:
    /// - Render point layers of 131,072 rows and of 131,073 rows with each fault below, and with each valid channel.
    ///
    /// Expected:
    /// - ArgumentException: a categorical SizeBy, a numeric ShapeBy, a default reference, another table's column, 7
    ///   categories, two layers whose shape columns' categories differ, a scale with a range of 0, a null inner scale,
    ///   no shapes, a shape twice. ArgumentNullException: null shapes. InvalidOperationException naming row R3d: SizeBy on
    ///   131,073 rows. 6 categories, a SizeBy of 131,072 rows, and a ShapeBy of 131,073 rows render.
    /// </remarks>
    [Fact]
    public void Validate_RefusesWhatTheChannelsDoNotDraw()
    {
        using var pool = new PlotWorkerPool(2);
        using var plan = TestPlans.Create(pool, Size, Size);
        var random = new Random(3);
        var xValues = Enumerable.Range(0, Over).Select(_ => random.NextDouble() * 100).ToArray();
        var yValues = Enumerable.Range(0, Over).Select(_ => random.NextDouble() * 100).ToArray();
        using var xs = Column.Wrap(xValues);
        using var ys = Column.Wrap(yValues);
        using var smallX = Column.Wrap(xValues[..LayerPlan.MaxElements]);
        using var smallY = Column.Wrap(yValues[..LayerPlan.MaxElements]);
        using var foreignColumn = Column.Wrap(new double[Over]);
        var table = DataTable
            .Create()
            .Add("x", xs)
            .Add("y", ys)
            .Add("c", Column.Categorical(Enumerable.Range(0, Over).Select(i => (string?)Three[i % 3]).ToArray()))
            .Add("six", Column.Categorical(Enumerable.Range(0, Over).Select(i => (string?)$"k{i % 6}").ToArray()))
            .Add("seven", Column.Categorical(Enumerable.Range(0, Over).Select(i => (string?)$"k{i % 7}").ToArray()))
            .Add("other", Column.Categorical(Enumerable.Range(0, Over).Select(i => (string?)$"o{i % 3}").ToArray()))
            .Build();
        var foreign = DataTable.Create().Add("f", foreignColumn).Build();
        var small = DataTable.Create().Add("x", smallX).Add("y", smallY).Build();
        var point = new PointLayerSpec { X = table.Col<double>("x"), Y = table.Col<double>("y") };
        var c = table.Cat("c").ToColumnRef();

        void Fails<TException>(PlotSpec spec)
            where TException : Exception => Assert.Throws<TException>(() => plan.Render(spec));

        Fails<ArgumentException>(Spec(table, point with { SizeBy = c }));
        Fails<ArgumentException>(Spec(table, point with { ShapeBy = table.Col<double>("x") }));
        Fails<ArgumentException>(Spec(table, point with { ShapeBy = default(ColumnRef) }));
        Fails<ArgumentException>(Spec(table, point with { SizeBy = foreign.Col<double>("f") }));
        Fails<ArgumentException>(Spec(table, point with { ShapeBy = table.Cat("seven").ToColumnRef() }));
        Fails<ArgumentException>(Spec(table, point with { ShapeBy = c }, point with { ShapeBy = table.Cat("other").ToColumnRef() }));
        Fails<ArgumentException>(Spec(table, point with { ShapeBy = c }) with { ScaleShape = Scale.Shape() with { Shapes = [] } });
        Fails<ArgumentException>(
            Spec(table, point with { ShapeBy = c }) with
            {
                ScaleShape = Scale.Shape() with { Shapes = [MarkerShape.X, MarkerShape.X] },
            }
        );
        Fails<ArgumentNullException>(Spec(table, point with { ShapeBy = c }) with { ScaleShape = Scale.Shape() with { Shapes = null! } });
        var smallPoint = new PointLayerSpec
        {
            X = small.Col<double>("x"),
            Y = small.Col<double>("y"),
            SizeBy = small.Col<double>("x"),
        };
        Fails<ArgumentException>(Spec(small, smallPoint) with { ScaleSize = Scale.Size() with { RangeMin = 0 } });
        Fails<ArgumentNullException>(Spec(small, smallPoint) with { ScaleSize = Scale.Size() with { Scale = null! } });
        var refused = Assert.Throws<InvalidOperationException>(() => plan.Render(Spec(table, point with { SizeBy = table.Col<double>("x") })));
        Assert.Contains("R3d", refused.Message, StringComparison.Ordinal);

        Assert.Equal(LayerPlan.MaxElements, plan.Render(Spec(small, smallPoint)).Markers.Length - plan.Guides.SizeRows);
        Assert.Equal(
            Over,
            plan.Render(Spec(table, point with { ShapeBy = table.Cat("six").ToColumnRef() })).ResidentRuns.ToArray().Sum(r => r.Count)
        );
    }

    /// <summary>
    /// A point's diameter is √(min² + t(max² − min²)) DIPs times the scale, t its place in the domain held to [0, 1]; the
    /// domain is the size columns' range, learned again when a column's values change (design §4.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Seven points of sizes 10, 20, 30, NaN, 10, 30, 20 on two layers that share the scale, the second's sizes 0 to 40;
    ///   a range of 4 to 20 DIPs; a target at a scale of 1.
    ///
    /// Steps:
    /// - Render. Render with the domain given as [20, 30], with a log inner scale over [10, 30], and with sizes all 7. Change
    ///   the second layer's sizes to 0 to 80 and render.
    ///
    /// Expected:
    /// - The diameters follow the formula over the learned domain [0, 40]; the NaN point is not recorded. With [20, 30], 10
    ///   takes 4 and 30 takes 20. With log, 10 takes 4, 30 takes 20, and the middle in the log space takes √((16 + 400) / 2).
    ///   With one value, every point takes √((16 + 400) / 2). After the change, the domain is [0, 80].
    /// </remarks>
    [Fact]
    public void Sizes_FollowTheAreaOverTheDomain()
    {
        using var pool = new PlotWorkerPool(2);
        using var plan = TestPlans.Create(pool, Size, Size);
        using var xs = Column.Wrap(new[] { 1.0, 2, 3, 4, 5, 6, 7 });
        using var ys = Column.Wrap(new[] { 1.0, 2, 3, 4, 5, 6, 7 });
        using var sizes = Column.Wrap(new[] { 10.0, 20, 30, double.NaN, 10, 30, 20 });
        var otherValues = new[] { 0.0, 40, 0, 40, 0, 40, 0 };
        using var others = Column.Wrap(otherValues);
        using var same = Column.Wrap(Enumerable.Repeat(7.0, 7).ToArray());
        var table = DataTable.Create().Add("x", xs).Add("y", ys).Add("s", sizes).Add("o", others).Add("same", same).Build();
        var first = new PointLayerSpec
        {
            X = table.Col<double>("x"),
            Y = table.Col<double>("y"),
            SizeBy = table.Col<double>("s"),
        };
        var second = first with { SizeBy = table.Col<double>("o") };
        var range = Scale.Size().WithRange(4, 20);
        static float D(double t) => (float)Math.Sqrt(16 + (t * (400 - 16)));

        var list = plan.Render(Spec(table, first, second) with { ScaleSize = range });
        var drawn = list.Markers.ToArray().Select(m => m.Size).ToArray();
        Assert.Equal(13, drawn.Length - plan.Guides.SizeRows);
        Assert.Equal([D(0.25), D(0.5), D(0.75), D(0.25), D(0.75), D(0.5)], drawn[..6], new Near());

        list = plan.Render(Spec(table, first) with { ScaleSize = Scale.Size(Scale.Linear().WithDomain(20, 30)).WithRange(4, 20) });
        Assert.Equal([4f, D(0), 20f, 4f, 20f, D(0)], list.Markers.ToArray()[..6].Select(m => m.Size), new Near());

        list = plan.Render(Spec(table, first) with { ScaleSize = Scale.Size(Scale.Log().WithDomain(10, 30)).WithRange(4, 20) });
        var middle = Math.Log10(20) - 1;
        var t = middle / (Math.Log10(30) - 1);
        Assert.Equal([4f, D(t), 20f, 4f, 20f, D(t)], list.Markers.ToArray()[..6].Select(m => m.Size), new Near());

        list = plan.Render(Spec(table, first with { SizeBy = table.Col<double>("same") }) with { ScaleSize = range });
        Assert.All(list.Markers.ToArray()[..6], m => Assert.Equal(D(0.5), m.Size, 3));

        _ = plan.Render(Spec(table, first, second) with { ScaleSize = range });
        otherValues[1] = 80;
        others.Invalidate();
        _ = plan.Render(Spec(table, first, second) with { ScaleSize = range });
        var domain = plan.Size!.Domain;
        Assert.Equal((0.0, 80.0), (domain.Origin + domain.Lower, domain.Origin + domain.Upper));
    }

    /// <summary>
    /// A point of category i takes the shape scale's shape i, by the column's numbers, an unused category keeping its place;
    /// with a color split by another column, each part's points keep their own shapes and sizes (design §4.2, §5.5.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Six points; a shape column of categories ["a", "b", "c"] of which "b" is unused; a color column of two names; sizes.
    ///
    /// Steps:
    /// - Render shaped, with the default scale and with WithShapes(X, Diamond, Square). Render shaped, sized, and colored by
    ///   the other column. Render a facet wrapped by the color column, shaped.
    ///
    /// Expected:
    /// - "a" takes Circle and "c" Triangle (the third of the default); then "a" X and "c" Square. Split by color, the points
    ///   of each part have their shapes and their sizes. In the facet, each panel's points have their shapes.
    /// </remarks>
    [Fact]
    public void Shapes_FollowTheCategoriesInEveryPart()
    {
        using var pool = new PlotWorkerPool(2);
        using var plan = TestPlans.Create(pool, Size, Size);
        using var xs = Column.Wrap(new[] { 1.0, 2, 3, 4, 5, 6 });
        using var ys = Column.Wrap(new[] { 1.0, 2, 3, 4, 5, 6 });
        using var sizes = Column.Wrap(new[] { 0.0, 1, 2, 3, 4, 5 });
        var shapes = new[] { "a", "c", "a", "c", "c", "a" };
        var colors = new[] { "p", "q", "q", "p", "q", "p" };
        var table = DataTable
            .Create()
            .Add("x", xs)
            .Add("y", ys)
            .Add("s", sizes)
            .Add("k", Column.Categorical(shapes.Select(s => (string?)s).ToArray(), Three))
            .Add("g", Column.Categorical(colors.Select(s => (string?)s).ToArray()))
            .Build();
        var point = new PointLayerSpec
        {
            X = table.Col<double>("x"),
            Y = table.Col<double>("y"),
            ShapeBy = table.Cat("k").ToColumnRef(),
        };

        // The six points come first; the shape legend's marks follow them.
        static MarkerShape[] Kinds(RenderList list) => [.. list.Markers.ToArray()[..6].Select(m => (MarkerShape)(m.Kind & 0xFF))];
        MarkerShape[] Expected(MarkerShape a, MarkerShape c) => [.. shapes.Select(s => s == "a" ? a : c)];

        Assert.Equal(Expected(MarkerShape.Circle, MarkerShape.Triangle), Kinds(plan.Render(Spec(table, point))));
        Assert.Equal(
            Expected(MarkerShape.X, MarkerShape.Square),
            Kinds(
                plan.Render(Spec(table, point) with { ScaleShape = Scale.Shape().WithShapes(MarkerShape.X, MarkerShape.Diamond, MarkerShape.Square) })
            )
        );

        var split = plan.Render(
            Spec(table, point with { SizeBy = table.Col<double>("s"), ColorBy = table.Cat("g").ToColumnRef() }) with
            {
                ScaleColor = Scale.Ordinal(),
            }
        );
        for (var i = 0; i < 6; i++)
        {
            var diameter = (float)Math.Sqrt(4 + (i / 5.0 * (256 - 4)));
            var marker = split.Markers.ToArray()[..6].Single(m => Math.Abs(m.Size - diameter) < 1e-3);
            Assert.Equal(shapes[i] == "a" ? MarkerShape.Circle : MarkerShape.Triangle, (MarkerShape)(marker.Kind & 0xFF));
        }

        var faceted = plan.Render(Spec(table, point) with { Facet = new FacetWrapSpec { By = table.Cat("g").ToColumnRef() } });
        Assert.Equal(6 + 3, faceted.Markers.Length);
        Assert.Equal(3, Kinds(faceted).Count(k => k == MarkerShape.Circle));
        Assert.Equal(3, Kinds(faceted).Count(k => k == MarkerShape.Triangle));
    }

    /// <summary>
    /// On the index path a ShapeBy splits the layer: a part a shape, or a part a color and shape pair that has rows when the
    /// color is another column; more than 64 parts throw (design §5.5.2 "大きさと形のチャネル").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 131,073 seeded points; a shape column of three names; a color column of four names whose rows meet only 6 of the 12
    ///   pairs; a color column of 22 names whose pairs with the shapes are 66.
    ///
    /// Steps:
    /// - Render shaped; shaped and colored by the shape's column; shaped and colored by the four names; shaped and colored by
    ///   the 22 names. Each on a plan that waits for its indexes.
    ///
    /// Expected:
    /// - Shaped: no marker but the shape legend's three; runs of the three shapes, the default's first three, in the series color; their points add up.
    ///   Colored by the same column: runs in the palette's three colors, each with its category's shape. By the four names: 8
    ///   pairs of a color and a shape. By the 22 names: InvalidOperationException naming row R3d.
    /// </remarks>
    [Fact]
    public void IndexPath_SplitsByShapeAndByPairs()
    {
        using var pool = new PlotWorkerPool(2);
        using var plan = TestPlans.Create(pool, Size, Size);
        var random = new Random(5);
        using var xs = Column.Wrap(Enumerable.Range(0, Over).Select(_ => random.NextDouble() * 100).ToArray());
        using var ys = Column.Wrap(Enumerable.Range(0, Over).Select(_ => random.NextDouble() * 100).ToArray());
        string[] four = ["w", "x", "y", "z"];
        var table = DataTable
            .Create()
            .Add("x", xs)
            .Add("y", ys)
            .Add("k", Column.Categorical(Enumerable.Range(0, Over).Select(i => (string?)Three[i % 3]).ToArray()))
            .Add("four", Column.Categorical(Enumerable.Range(0, Over).Select(i => (string?)four[(i % 3 == 0 ? 0 : 2) + ((i / 7) % 2)]).ToArray()))
            .Add("many", Column.Categorical(Enumerable.Range(0, Over).Select(i => (string?)$"m{(i / 3) % 22}").ToArray()))
            .Build();
        var point = new PointLayerSpec
        {
            X = table.Col<double>("x"),
            Y = table.Col<double>("y"),
            ShapeBy = table.Cat("k").ToColumnRef(),
        };
        static (MarkerShape Shape, Vector4 Color)[] Looks(RenderList list) =>
            [.. list.ResidentRuns.ToArray().Select(r => ((MarkerShape)(r.Kind & 0xFF), r.Color)).Distinct()];

        var shaped = plan.Render(Spec(table, point));

        // No point is mapped on the CPU: the three markers are the shape legend's.
        Assert.Equal(3, shaped.Markers.Length);
        Assert.Equal(Over, shaped.ResidentRuns.ToArray().Sum(r => r.Count));
        Assert.Equal([MarkerShape.Circle, MarkerShape.Square, MarkerShape.Triangle], Looks(shaped).Select(l => l.Shape).Distinct().Order());
        Assert.Single(Looks(shaped).Select(l => l.Color).Distinct());

        var same = plan.Render(Spec(table, point with { ColorBy = table.Cat("k").ToColumnRef() }) with { ScaleColor = Scale.Ordinal() });
        Assert.Equal(Enumerable.Range(0, 3).Select(i => (Default3[i], RenderList.Premultiply(Palettes.Tab10[i]))).Order(), Looks(same).Order());

        var pairs = plan.Render(Spec(table, point with { ColorBy = table.Cat("four").ToColumnRef() }) with { ScaleColor = Scale.Ordinal() });
        Assert.Equal(6, Looks(pairs).Length);
        Assert.Equal(Over, pairs.ResidentRuns.ToArray().Sum(r => r.Count));

        var refused = Assert.Throws<InvalidOperationException>(() =>
            plan.Render(Spec(table, point with { ColorBy = table.Cat("many").ToColumnRef() }) with { ScaleColor = Scale.Ordinal() })
        );
        Assert.Contains("R3d", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>In XY mode a sized point is reached within the cursor's radius plus its own (design §6.3, §5.5.2).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A large point (size 40, diameter 16 DIPs) at (2, 5) and a small one (size 0, diameter 2 DIPs) at (8, 5), domains
    ///   [0, 10], 640 × 360; a NaN size at (5, 5).
    ///
    /// Steps:
    /// - XY with a radius of 0: 7 pixels right of the large point, 7 pixels right of the small one, and at the NaN one.
    ///
    /// Expected:
    /// - The large point is hit; the small one and the NaN one are not.
    /// </remarks>
    [Fact]
    public void Hit_TakesEachPointsOwnRadius()
    {
        using var xs = Column.Wrap(new[] { 2.0, 8, 5 });
        using var ys = Column.Wrap(new[] { 5.0, 5, 5 });
        using var sizes = Column.Wrap(new[] { 40.0, 0, double.NaN });
        var table = DataTable.Create().Add("x", xs).Add("y", ys).Add("s", sizes).Build();
        var spec = Plot.Create(table)
            .Point(table.Col<double>("x"), table.Col<double>("y"), p => p.SizeBy(table.Col<double>("s")))
            .ScaleX(Scale.Linear().WithDomain(0, 10))
            .ScaleY(Scale.Linear().WithDomain(0, 10))
            .Build();
        using var engine = new PlotEngine(new NullBackend(), 640, 360) { WaitsForIndexes = true };
        engine.SetPlot(spec);
        engine.RenderFrame();
        var hits = new SeriesHit[2];
        Vector2 At(double x) => engine.ViewToPixel(Panel, ViewRange.FromValues(x, x), ViewRange.FromValues(5, 5));

        Assert.Equal(0, Assert.Single(hits[..engine.HitTest(At(2) + new Vector2(7 * engine.Scale, 0), 0, HitMode.XY, hits)]).Row);
        Assert.Equal(0, engine.HitTest(At(8) + new Vector2(7 * engine.Scale, 0), 0, HitMode.XY, hits));
        Assert.Equal(0, engine.HitTest(At(5), 0, HitMode.XY, hits));
    }

    /// <summary>
    /// The legends (design §4.6 "形と大きさの凡例"): a shape legend of the categories' marks; the same column's colors and
    /// shapes in one legend; another column's in two; a size legend of 3 to 5 circles of the scale's diameters, filled by the
    /// value's color when the same column colors the points.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Points shaped by three names; a color column of other names; a size column from 0 to 100; the fake text backend.
    ///
    /// Steps:
    /// - Render shaped, titled "Kind"; shaped and colored by the same column; shaped and colored by the other; sized, titled
    ///   "Mass"; sized and colored by the same numbers.
    ///
    /// Expected:
    /// - Shaped: "Kind", then three rows of marks with the default's first three shapes in the foreground. Same column: the
    ///   color rows' marks have the shapes and no other row names a category twice. Other column: both columns' names.
    ///   Sized: "Mass" and 3 to 5 labels, each circle's size the scale's diameter of its label's value, the circles in the
    ///   foreground; with the same numbers coloring the points, each circle in its value's color and the color bar still there.
    /// </remarks>
    [Fact]
    public void Legends_ShowShapesAndSizes()
    {
        using var pool = new PlotWorkerPool(2);
        using var plan = TestPlans.Create(pool, 640, 360);
        using var xs = Column.Wrap(Enumerable.Range(0, 30).Select(i => (double)i).ToArray());
        using var ys = Column.Wrap(Enumerable.Range(0, 30).Select(i => (double)(i % 7)).ToArray());
        using var mass = Column.Wrap(Enumerable.Range(0, 30).Select(i => i * 100 / 29.0).ToArray());
        var table = DataTable
            .Create()
            .Add("x", xs)
            .Add("y", ys)
            .Add("m", mass)
            .Add("k", Column.Categorical(Enumerable.Range(0, 30).Select(i => (string?)Three[i % 3]).ToArray()))
            .Add("g", Column.Categorical(Enumerable.Range(0, 30).Select(i => (string?)(i % 2 == 0 ? "even" : "odd")).ToArray()))
            .Build();
        var point = new PointLayerSpec { X = table.Col<double>("x"), Y = table.Col<double>("y") };
        var theme = Theme.Light;
        var ink = RenderList.Premultiply(theme.Foreground);
        PlotSpec WithTheme(PlotSpec spec) => spec with { Theme = theme };
        static string[] Texts(RenderList list) => [.. list.Texts.ToArray().Select(t => t.Text)];

        var shaped = plan.Render(
            WithTheme(Spec(table, point with { ShapeBy = table.Cat("k").ToColumnRef() }) with { ScaleShape = Scale.Shape().WithTitle("Kind") })
        );
        Assert.Contains("Kind", Texts(shaped));
        var legendMarks = shaped.Markers.ToArray()[30..];
        Assert.Equal([MarkerShape.Circle, MarkerShape.Square, MarkerShape.Triangle], legendMarks.Select(m => (MarkerShape)(m.Kind & 0xFF)));
        Assert.All(legendMarks, m => Assert.Equal(ink, m.Color));

        var same = plan.Render(
            WithTheme(
                Spec(table, point with { ShapeBy = table.Cat("k").ToColumnRef(), ColorBy = table.Cat("k").ToColumnRef() }) with
                {
                    ScaleColor = Scale.Ordinal(),
                }
            )
        );
        Assert.Equal(1, Texts(same).Count(t => t == "a"));
        var colored = same.Markers.ToArray()[^3..];
        Assert.Equal([MarkerShape.Circle, MarkerShape.Square, MarkerShape.Triangle], colored.Select(m => (MarkerShape)(m.Kind & 0xFF)));
        Assert.Equal(Enumerable.Range(0, 3).Select(i => RenderList.Premultiply(theme.Palette.Series(i))), colored.Select(m => m.Color));

        var other = plan.Render(
            WithTheme(
                Spec(table, point with { ShapeBy = table.Cat("k").ToColumnRef(), ColorBy = table.Cat("g").ToColumnRef() }) with
                {
                    ScaleColor = Scale.Ordinal(),
                }
            )
        );
        Assert.Contains("even", Texts(other));
        Assert.Contains("a", Texts(other));

        var sized = plan.Render(
            WithTheme(Spec(table, point with { SizeBy = table.Col<double>("m") }) with { ScaleSize = Scale.Size(Scale.Linear().WithTitle("Mass")) })
        );
        Assert.Contains("Mass", Texts(sized));
        var circles = sized.Markers.ToArray()[30..];
        Assert.InRange(circles.Length, 3, 5);
        Assert.Equal(circles.Length, plan.Guides.SizeRows);
        var labels = plan.Guides.SizeAxis.TickLabels.ToArray();
        var size = plan.Size!;
        foreach (var circle in circles)
        {
            Assert.Equal(ink, circle.Color);
            Assert.Contains(
                labels,
                label => Math.Abs(size.Diameter(double.Parse(label, System.Globalization.CultureInfo.InvariantCulture)) - circle.Size) < 1e-3
            );
        }

        var both = plan.Render(WithTheme(Spec(table, point with { SizeBy = table.Col<double>("m"), ColorBy = table.Col<double>("m") })));
        var tinted = both.Markers.ToArray()[30..];
        Assert.All(tinted, m => Assert.NotEqual(ink, m.Color));
        Assert.True(plan.Guides.ColorBarLength > 0);
    }

    /// <summary>
    /// The legend's band holds the size legend (design §4.6 "凡例" 3, "大きさの凡例"): it is at least the largest circle and
    /// a gap wide, and widens with the circle.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 30 points sized by values from 0 to 100; the fake text backend; a plan of 640 × 360 at a scale of 1.
    ///
    /// Steps:
    /// - Render with the range of 2 to 16 DIPs, and with 2 to 60.
    ///
    /// Expected:
    /// - The largest circle is the range's top. Each band is at least that circle + the gap wide; the band of 60 is wider than
    ///   the band of 16 by at least 60 − max(16, the swatch).
    /// </remarks>
    [Fact]
    public void LegendBand_HoldsTheSizeLegend()
    {
        using var pool = new PlotWorkerPool(2);
        using var plan = TestPlans.Create(pool, 640, 360);
        using var xs = Column.Wrap(Enumerable.Range(0, 30).Select(i => (double)i).ToArray());
        using var mass = Column.Wrap(Enumerable.Range(0, 30).Select(i => i * 100 / 29.0).ToArray());
        var table = DataTable.Create().Add("x", xs).Add("m", mass).Build();
        var point = new PointLayerSpec
        {
            X = table.Col<double>("x"),
            Y = table.Col<double>("x"),
            SizeBy = table.Col<double>("m"),
        };
        var theme = Theme.Light;
        int Band(float max)
        {
            var list = plan.Render(Spec(table, point) with { Theme = theme, ScaleSize = Scale.Size().WithRange(2, max) });
            var widest = list.Markers.ToArray()[30..].Max(m => m.Size);
            Assert.Equal(max, widest, 3);
            var gap = plan.Guides.Round(theme.Spacing);
            Assert.True(plan.Guides.LegendWidth >= widest + gap, $"The band {plan.Guides.LegendWidth} is narrower than {widest} + {gap}.");
            return plan.Guides.LegendWidth;
        }

        Assert.True(Band(60) - Band(16) >= 60 - Math.Max(16, plan.Guides.Round(theme.LegendSwatch)));
    }

    /// <summary>
    /// A change of the size scale draws again with it (design §4.2 "差分"): the range alone is a change of looks, the domain
    /// one of the scale, and another size column one of the data.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Three points of sizes 0, 50, and 100, and another column of 100, 50, and 0.
    ///
    /// Steps:
    /// - Render with the range of 2 to 16 DIPs; then 4 to 8; then the domain [0, 50]; then the other column.
    ///
    /// Expected:
    /// - The diameters are 2, √130, 16; then 4, √40, 8; then 4, 8, 8; then 8, √40, 4.
    /// </remarks>
    [Fact]
    public void Changes_DrawWithTheNewScale()
    {
        using var pool = new PlotWorkerPool(2);
        using var plan = TestPlans.Create(pool, Size, Size);
        using var xs = Column.Wrap(new[] { 1.0, 2, 3 });
        using var sizes = Column.Wrap(new[] { 0.0, 50, 100 });
        using var others = Column.Wrap(new[] { 100.0, 50, 0 });
        var table = DataTable.Create().Add("x", xs).Add("s", sizes).Add("o", others).Build();
        var point = new PointLayerSpec
        {
            X = table.Col<double>("x"),
            Y = table.Col<double>("x"),
            SizeBy = table.Col<double>("s"),
        };
        float[] Drawn(PlotSpec spec) => [.. plan.Render(spec).Markers.ToArray()[..3].Select(m => m.Size)];

        Assert.Equal([2f, (float)Math.Sqrt(130), 16f], Drawn(Spec(table, point) with { ScaleSize = Scale.Size() }), new Near());
        Assert.Equal([4f, (float)Math.Sqrt(40), 8f], Drawn(Spec(table, point) with { ScaleSize = Scale.Size().WithRange(4, 8) }), new Near());
        Assert.Equal(
            [4f, 8f, 8f],
            Drawn(Spec(table, point) with { ScaleSize = Scale.Size(Scale.Linear().WithDomain(0, 50)).WithRange(4, 8) }),
            new Near()
        );
        Assert.Equal(
            [8f, (float)Math.Sqrt(40), 4f],
            Drawn(Spec(table, point with { SizeBy = table.Col<double>("o") }) with { ScaleSize = Scale.Size().WithRange(4, 8) }),
            new Near()
        );
    }

    /// <summary>
    /// On the index path, the parts of a layer split by a color and a shape column are counted by the pairs that have rows:
    /// 64 render and 65 throw (design §5.5.2 "上限と例外").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 131,073 seeded points; a shape column of two names taking turns; a color column of 32 names over the rows, and one
    ///   of 33 names whose last is on one row only.
    ///
    /// Steps:
    /// - Render shaped and colored by the 32 names, then by the 33, on a plan that waits for its indexes.
    ///
    /// Expected:
    /// - 32 × 2 = 64 pairs render as 64 parts, whose runs hold every point; 65 pairs throw InvalidOperationException naming
    ///   row R3d.
    /// </remarks>
    [Fact]
    public void IndexPath_SixtyFourPairsAndNoMore()
    {
        using var pool = new PlotWorkerPool(2);
        using var plan = TestPlans.Create(pool, Size, Size);
        var random = new Random(7);
        using var xs = Column.Wrap(Enumerable.Range(0, Over).Select(_ => random.NextDouble() * 100).ToArray());
        using var ys = Column.Wrap(Enumerable.Range(0, Over).Select(_ => random.NextDouble() * 100).ToArray());
        var table = DataTable
            .Create()
            .Add("x", xs)
            .Add("y", ys)
            .Add("k", Column.Categorical(Enumerable.Range(0, Over).Select(i => (string?)(i % 2 == 0 ? "a" : "b")).ToArray()))
            .Add("c32", Column.Categorical(Enumerable.Range(0, Over).Select(i => (string?)$"c{(i / 2) % 32:D2}").ToArray()))
            .Add("c33", Column.Categorical(Enumerable.Range(0, Over).Select(i => (string?)(i == 0 ? "c32" : $"c{(i / 2) % 32:D2}")).ToArray()))
            .Build();
        var point = new PointLayerSpec
        {
            X = table.Col<double>("x"),
            Y = table.Col<double>("y"),
            ShapeBy = table.Cat("k").ToColumnRef(),
        };

        var pairs = plan.Render(Spec(table, point with { ColorBy = table.Cat("c32").ToColumnRef() }) with { ScaleColor = Scale.Ordinal() });
        Assert.Equal(64, ((SplitPlan)plan.Layers[0]).SeriesCount);
        Assert.Equal(Over, pairs.ResidentRuns.ToArray().Sum(r => r.Count));

        var refused = Assert.Throws<InvalidOperationException>(() =>
            plan.Render(Spec(table, point with { ColorBy = table.Cat("c33").ToColumnRef() }) with { ScaleColor = Scale.Ordinal() })
        );
        Assert.Contains("R3d", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>Frames of the size and shape channels allocate nothing in steady state (invariant 3).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A layer of 2,000 points sized by a number and shaped and colored by a category on the CPU path, with its legends;
    ///   a layer of 131,073 points shaped by a category on the index path. Each drawn with five views twice.
    ///
    /// Steps:
    /// - Collect, then draw the five views again, counting the thread's allocations.
    ///
    /// Expected:
    /// - 0 bytes for each.
    /// </remarks>
    [Fact]
    public void Frames_AllocateNothing()
    {
        var random = new Random(9);
        var xValues = Enumerable.Range(0, Over).Select(_ => random.NextDouble() * 100).ToArray();
        var yValues = Enumerable.Range(0, Over).Select(_ => random.NextDouble() * 100).ToArray();
        using var xs = Column.Wrap(xValues);
        using var ys = Column.Wrap(yValues);
        using var small = Column.Wrap(xValues[..2000]);
        using var smallY = Column.Wrap(yValues[..2000]);
        var big = DataTable
            .Create()
            .Add("x", xs)
            .Add("y", ys)
            .Add("k", Column.Categorical(Enumerable.Range(0, Over).Select(i => (string?)Three[i % 3]).ToArray()))
            .Build();
        var few = DataTable
            .Create()
            .Add("x", small)
            .Add("y", smallY)
            .Add("k", Column.Categorical(Enumerable.Range(0, 2000).Select(i => (string?)Three[i % 3]).ToArray()))
            .Build();
        Count(
            Plot.Create(few)
                .Point(few.Col<double>("x"), few.Col<double>("y"), p => p.SizeBy(few.Col<double>("y")).ShapeBy(few.Cat("k")).ColorBy(few.Cat("k")))
                .ScaleSize(Scale.Size(Scale.Linear().WithTitle("Size")))
                .ScaleX(Scale.Linear().WithDomain(0, 100))
                .ScaleY(Scale.Linear().WithDomain(0, 100))
                .Build()
        );
        Count(
            Plot.Create(big)
                .Point(big.Col<double>("x"), big.Col<double>("y"), p => p.ShapeBy(big.Cat("k")))
                .ScaleX(Scale.Linear().WithDomain(0, 100))
                .ScaleY(Scale.Linear().WithDomain(0, 100))
                .Build()
        );

        static void Count(PlotSpec spec)
        {
            var backend = new NullBackend();
            using var engine = new PlotEngine(backend, 640, 360) { WaitsForIndexes = true };
            engine.SetPlot(spec);
            engine.RenderFrame();
            var views = Enumerable
                .Range(0, 5)
                .Select(i => new PanelView2D(ViewRange.FromValues(10 + i, 90), ViewRange.FromValues(10, 90 - i)))
                .ToArray();
            for (var round = 0; round < 2; round++)
            {
                foreach (var view in views)
                {
                    Draw(view);
                }
            }

            // An allocation that follows a collection is counted in every run (.cursor/rules/bench.mdc).
            GC.Collect();
            var before = GC.GetAllocatedBytesForCurrentThread();
            foreach (var view in views)
            {
                Draw(view);
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

            void Draw(PanelView2D view)
            {
                backend.Images.Sent();
                engine.Views.SetView(Panel, view);
                engine.RenderFrame();
            }
        }
    }

    private sealed class Near : IEqualityComparer<float>
    {
        public bool Equals(float x, float y) => Math.Abs(x - y) < 1e-3;

        public int GetHashCode(float obj) => 0;
    }
}
