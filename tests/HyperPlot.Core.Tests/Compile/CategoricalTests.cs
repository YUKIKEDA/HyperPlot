using System.Numerics;
using HyperPlot.Rendering;
using static HyperPlot.Tests.GeomTesting;

namespace HyperPlot.Tests;

/// <summary>The categorical column, the band scale, and the bars over categories of row G7b (design §3.1, §4.2, §4.6, §5.5.3, §10 G7b row).</summary>
public sealed class CategoricalTests
{
    /// <summary>
    /// A categorical column sorts its names in ordinal order or keeps a given order, reads a null value as missing, and
    /// refuses a value outside the categories and a category that is null, empty, or there twice (design §3.1, §4.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Values "b", "a", "B", null, "b".
    ///
    /// Steps:
    /// - Make the column from the values; with the categories ("b", "c", "a", "B"); with bad inputs; read codes.
    ///
    /// Expected:
    /// - Sorted: B, a, b (ordinal: upper case first); codes 2, 1, 0, −1, 2. Given: codes 0, 2, 3, −1, 0 and 4 categories.
    ///   ArgumentException for a value outside the categories, an empty value, and a null, empty, or repeated category;
    ///   ArgumentOutOfRangeException for an element outside the column. Cat refuses a missing name and a numeric column;
    ///   Col refuses the categorical one.
    /// </remarks>
    [Fact]
    public void Column_SortsOrKeepsTheCategories()
    {
        string?[] values = ["b", "a", "B", null, "b"];
        var sorted = Column.Categorical(values);
        Assert.Equal(["B", "a", "b"], sorted.Categories);
        Assert.Equal([2, 1, 0, -1, 2], Enumerable.Range(0, 5).Select(i => sorted.GetCode(i)));
        Assert.Equal(5, sorted.Length);

        var given = Column.Categorical(values, ["b", "c", "a", "B"]);
        Assert.Equal(4, given.CategoryCount);
        Assert.Equal([0, 2, 3, -1, 0], Enumerable.Range(0, 5).Select(i => given.GetCode(i)));

        _ = Assert.Throws<ArgumentException>(() => Column.Categorical(values, ["a", "b"]));
        _ = Assert.Throws<ArgumentException>(() => Column.Categorical(["a", string.Empty]));
        _ = Assert.Throws<ArgumentException>(() => Column.Categorical(["a"], ["a", null!]));
        _ = Assert.Throws<ArgumentException>(() => Column.Categorical(["a"], ["a", string.Empty]));
        _ = Assert.Throws<ArgumentException>(() => Column.Categorical(["a"], ["a", "a"]));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => sorted.GetCode(5));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => sorted.GetCode(-1));

        using var y = Column.Wrap(new double[] { 1, 2, 3, 4, 5 });
        var table = DataTable.Create().Add("c", sorted).Add("y", y).Build();
        Assert.Same(sorted, table.Cat("c").Column);
        Assert.Equal(table.Cat("c").ToColumnRef(), new ColumnRef(table, 0));
        _ = Assert.Throws<ArgumentException>(() => table.Cat("none"));
        _ = Assert.Throws<ArgumentException>(() => table.Cat("y"));
        _ = Assert.Throws<ArgumentException>(() => table.Col<int>("c"));
    }

    /// <summary>
    /// <c>Scale.Band</c> has Vega-Lite's bar defaults and checks its values; a scale made with <c>with</c> is checked when drawn
    /// (design §4.1, §4.8).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A plot of bars over 3 categories.
    ///
    /// Steps:
    /// - Read Scale.Band(); call it with −0.1, 1.1, and NaN for each value; render with each value set to 2 with <c>with</c>.
    ///
    /// Expected:
    /// - 0.1, 0.05, 0.5, not reversed, no title. ArgumentOutOfRangeException naming each parameter; ArgumentException naming
    ///   each member.
    /// </remarks>
    [Fact]
    public void Band_DefaultsAndChecks()
    {
        var band = Scale.Band();
        Assert.Equal((0.1, 0.05, 0.5, false, (string?)null), (band.PaddingInner, band.PaddingOuter, band.Align, band.Reverse, band.Title));
        Assert.Equal("G", band.WithTitle("G").Title);
        foreach (var bad in new[] { -0.1, 1.1, double.NaN })
        {
            Assert.Equal("paddingInner", Assert.Throws<ArgumentOutOfRangeException>(() => Scale.Band(paddingInner: bad)).ParamName);
            Assert.Equal("paddingOuter", Assert.Throws<ArgumentOutOfRangeException>(() => Scale.Band(paddingOuter: bad)).ParamName);
            Assert.Equal("align", Assert.Throws<ArgumentOutOfRangeException>(() => Scale.Band(align: bad)).ParamName);
        }

        using var pool = new PlotWorkerPool(1);
        using var data = Data.Create(3, 1);
        using var plan = TestPlans.Create(pool, Size, Size);
        foreach (
            var (scale, member) in new[]
            {
                (band with { PaddingInner = 2 }, "PaddingInner"),
                (band with { PaddingOuter = 2 }, "PaddingOuter"),
                (band with { Align = 2 }, "Align"),
            }
        )
        {
            var error = Assert.Throws<ArgumentException>(() => plan.Render(data.Bars() with { ScaleX = scale }));
            Assert.Contains(member, error.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Each category's band starts and is as wide as d3's band gives, over the panel's pixels, in every combination of count,
    /// paddings, alignment, and reverse; a user's geom reads them from its frame (design §4.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A 100 × 100 target without guides; bars over n = 1, 3, and 12 categories and a user's geom that reads its frame.
    ///
    /// Steps:
    /// - Render with paddings (0, 0) and (0.1, 0.05), align 0, 0.5, and 1, reversed and not; read BandX and BandWidth.
    ///
    /// Expected:
    /// - step = 100 / max(1, n − pi + 2 po), start = (100 − step · (n − pi)) · align; category i at start + step · j with
    ///   j = i, or n − 1 − i reversed; width step · (1 − pi); all within 1e-9. PixelX and Map throw InvalidOperationException.
    /// </remarks>
    [Fact]
    public void Band_MatchesD3()
    {
        using var pool = new PlotWorkerPool(1);
        using var plan = TestPlans.Create(pool, Size, Size);
        foreach (var n in new[] { 1, 3, 12 })
        {
            using var data = Data.Create(n, 1);
            foreach (var (pi, po) in new[] { (0.0, 0.0), (0.1, 0.05) })
            {
                foreach (var align in new[] { 0, 0.5, 1 })
                {
                    foreach (var reverse in new[] { false, true })
                    {
                        var geom = new ProbeGeom(n);
                        var spec = data.Bars() with
                        {
                            Layers = [data.Bars().Layers[0], new CustomLayerSpec(geom)],
                            ScaleX = Scale.Band(pi, po, align) with { Reverse = reverse },
                        };
                        _ = plan.Render(spec);
                        var step = Size / Math.Max(1, n - pi + (2 * po));
                        var start = (Size - (step * (n - pi))) * align;
                        for (var i = 0; i < n; i++)
                        {
                            var j = reverse ? n - 1 - i : i;
                            Assert.InRange(geom.Starts[i], start + (step * j) - 1e-9, start + (step * j) + 1e-9);
                        }

                        Assert.InRange(geom.Width, (step * (1 - pi)) - 1e-9, (step * (1 - pi)) + 1e-9);
                        Assert.True(geom.PixelThrew && geom.MapThrew);
                    }
                }
            }
        }
    }

    /// <summary>
    /// The compile refuses what row G7b does not draw: two layers of other categories, a band over numbers, categories on a
    /// continuous x, on a line, or on y, a bar width over categories, and a band on y. A horizontal rule and a user's geom
    /// may be on a band's plot (design §4.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two categorical columns of other categories and a numeric column, in one table.
    ///
    /// Steps:
    /// - Render each spec.
    ///
    /// Expected:
    /// - ArgumentException for each refused one; the rule and the geom draw.
    /// </remarks>
    [Fact]
    public void Checks_RefuseWhatG7bDoesNotDraw()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = Data.Create(3, 1);
        var t = data.Table;
        var (cat, other, y) = (t.Cat("c").ToColumnRef(), t.Cat("other").ToColumnRef(), t.Col<double>("y").ToColumnRef());
        var band = Scale.Band();
        using var plan = TestPlans.Create(pool, Size, Size);
        PlotSpec[] refused =
        [
            Spec(t, new BarLayerSpec { X = cat, Y = y }, new BarLayerSpec { X = other, Y = y }) with
            {
                ScaleX = band,
            },
            Spec(t, new LineLayerSpec { X = y, Y = y }) with
            {
                ScaleX = band,
            },
            Spec(t, new BarLayerSpec { X = cat, Y = y }),
            Spec(t, new LineLayerSpec { X = cat, Y = y }) with
            {
                ScaleX = band,
            },
            Spec(t, new BarLayerSpec { X = cat, Y = cat }) with
            {
                ScaleX = band,
            },
            Spec(
                t,
                new BarLayerSpec
                {
                    X = cat,
                    Y = y,
                    Width = 0.5,
                }
            ) with
            {
                ScaleX = band,
            },
            Spec(t, new BarLayerSpec { X = cat, Y = y }) with
            {
                ScaleX = band,
                ScaleY = band,
            },
        ];
        foreach (var spec in refused)
        {
            _ = Assert.Throws<ArgumentException>(() => plan.Render(spec));
        }

        var allowed = Spec(t, new BarLayerSpec { X = cat, Y = y }, new RuleLayerSpec { Y = y }, new CustomLayerSpec(new ProbeGeom(3))) with
        {
            ScaleX = band,
        };
        Assert.NotEmpty(plan.Render(allowed).Rects.ToArray());
    }

    /// <summary>
    /// Each category draws a band wide from the baseline to its greatest y and to its least, its missing and NaN rows left out;
    /// an unused category's band stays empty (design §5.5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A 100 × 100 target without guides, y domain [−10, 10], band (0.5, 0.25, 0.5): 4 categories a, b, c, d, a step of 25 pixels from 6.25 and bands 12.5 wide.
    ///   Rows: a 3, 5, 1; b −4, 6; c NaN, missing; d unused (given in the categories).
    ///
    /// Steps:
    /// - Render the bars.
    ///
    /// Expected:
    /// - a: one rect at x 6.25, 12.5 wide, from y 0 to 5 (pixels 50 to 25); b: at x 31.25, from 0 to 6 and from 0 to −4; none for c and d.
    /// </remarks>
    [Fact]
    public void Bars_FromTheBaselineToEachExtreme()
    {
        using var pool = new PlotWorkerPool(1);
        string?[] names = ["a", "a", "a", "b", "b", "c", null];
        using var y = Column.Wrap(new double[] { 3, 5, 1, -4, 6, double.NaN, 9 });
        var t = DataTable.Create().Add("c", Column.Categorical(names, ["a", "b", "c", "d"])).Add("y", y).Build();
        var spec = Spec(t, new BarLayerSpec { X = t.Cat("c").ToColumnRef(), Y = t.Col<double>("y") }) with
        {
            ScaleX = Scale.Band(0.5, 0.25),
            ScaleY = Scale.Linear().WithDomain(-10, 10),
        };
        using var plan = TestPlans.Create(pool, Size, Size);

        var rects = DataRects(plan.Render(spec));

        Assert.Equal([(6.25f, 25f, 12.5f, 25f), (31.25f, 20f, 12.5f, 30f), (31.25f, 50f, 12.5f, 20f)], rects);
    }

    /// <summary>
    /// Bands under a pixel join a pixel column at a time: at most two rects a column, each from the baseline to the extremes
    /// of the categories whose band starts in it, read from the pyramids' levels (design §5.5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A 100 × 100 target without guides, y domain [−10, 10], band (0, 0, 0.5); 3,000 categories with 2 seeded rows each,
    ///   so that a column holds 30 categories.
    ///
    /// Steps:
    /// - Render; for each column, find the extremes of its categories on the CPU.
    ///
    /// Expected:
    /// - At most 200 rects; each column's rects from the baseline to the CPU's greatest and least, within 1e-3 pixels.
    /// </remarks>
    [Fact]
    public void Bars_UnderAPixel_JoinByColumn()
    {
        const int Categories = 3000;
        using var pool = new PlotWorkerPool(2);
        var random = new Random(3);
        var names = Enumerable.Range(0, Categories * 2).Select(i => $"c{i / 2:0000}").ToArray<string?>();
        var values = Enumerable.Range(0, Categories * 2).Select(_ => (random.NextDouble() * 18) - 9).ToArray();
        using var y = Column.Wrap(values);
        var t = DataTable.Create().Add("c", Column.Categorical(names)).Add("y", y).Build();
        var spec = Spec(t, new BarLayerSpec { X = t.Cat("c").ToColumnRef(), Y = t.Col<double>("y") }) with
        {
            ScaleX = Scale.Band(0, 0),
            ScaleY = Scale.Linear().WithDomain(-10, 10),
        };
        using var plan = TestPlans.Create(pool, Size, Size);

        var rects = DataRects(plan.Render(spec));

        Assert.InRange(rects.Length, 1, 2 * Size);
        for (var column = 0; column < Size; column++)
        {
            var inside = Enumerable.Range(column * 30, 30).SelectMany(c => new[] { values[2 * c], values[(2 * c) + 1] }).ToArray();
            var (top, bottom) = (50 - (inside.Max() * 5), 50 - (inside.Min() * 5));
            var mine = rects.Where(r => r.X == column).ToArray();
            Assert.Contains(mine, r => Math.Abs(r.Y - top) < 1e-3 && Math.Abs(r.Y + r.Height - 50) < 1e-3);
            if (inside.Min() < 0)
            {
                Assert.Contains(mine, r => Math.Abs(r.Y - 50) < 1e-3 && Math.Abs(r.Y + r.Height - bottom) < 1e-3);
            }
        }
    }

    /// <summary>
    /// Flipped, the bands run up the left side; polar, they are angles of the circle with x as the angle and rings with y
    /// (design §4.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Bars over 4 categories of positive values on a 100 × 100 target without guides.
    ///
    /// Steps:
    /// - Render cartesian, flipped, polar with theta X, and polar with theta Y.
    ///
    /// Expected:
    /// - Flipped: each rect is the cartesian one with x and y swapped and mirrored up (the first category at the bottom).
    ///   Polar: one sector call of 4 trapezoids for theta X; for theta Y, one too, a ring a category.
    /// </remarks>
    [Fact]
    public void Band_FlippedAndPolar()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = Data.Create(4, 1, positive: true);
        var spec = data.Bars() with { ScaleY = Scale.Linear().WithDomain(0, 10) };
        using var plan = TestPlans.Create(pool, Size, Size);

        var straight = DataRects(plan.Render(spec));
        var flipped = DataRects(plan.Render(spec with { Coord = Coord.Cartesian(flip: true) }));
        Assert.Equal(straight.Length, flipped.Length);
        for (var i = 0; i < straight.Length; i++)
        {
            var (x, w) = (straight[i].X, straight[i].Width);
            Assert.Equal((Size - x - w, straight[i].Height), (flipped[i].Y, flipped[i].Width));
        }

        foreach (var theta in new[] { PolarTheta.X, PolarTheta.Y })
        {
            var list = plan.Render(spec with { Coord = Coord.Polar(theta) });
            Assert.Single(list.SectorCalls.ToArray());
            Assert.True(list.SectorCalls[0].Count >= 4);
        }
    }

    /// <summary>
    /// A band axis has a tick in the middle of each labeled category's band; when the labels overlap, every k-th category is
    /// labeled, k the one that keeps the labels apart, and its ticks and grid lines go with them. Reversed, the order turns
    /// round (design §4.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A 640 × 360 target with the light theme and the fake text backend; bars over 12 categories named "Category 01" to
    ///   "Category 12" (11 characters, 74.25 pixels at 12 px), over 4 named "A" to "D", and over as many of those names as
    ///   fit steps of 78.75 pixels (the width and 3/8 em), with the outer padding that makes the step that.
    ///
    /// Steps:
    /// - Render each; render the 12 reversed.
    ///
    /// Expected:
    /// - 4 categories: 4 ticks at the bands' middles, labels A to D in order. 12: the labels of categories 0, k, 2k, … with k
    ///   the least that keeps neighbors half an em apart (k · step ≥ 74.25 + 6); as many grid lines. Reversed: the first
    ///   label is on the right. The step under 80.25 pixels: every other category is labeled, though neighbors would not
    ///   overlap.
    /// </remarks>
    [Fact]
    public void Axis_TicksInTheMiddle_ThinnedWhenTheyOverlap()
    {
        using var pool = new PlotWorkerPool(1);
        using var few = Data.Create(4, 1, names: i => ((char)('A' + i)).ToString());
        using var plan = new PlotPlan(pool, 640, 360, new TextEngine(new FakeTextBackend()), 1);
        var fewSpec = few.Bars() with { Theme = Theme.Light };
        _ = plan.Render(fewSpec);
        var axis = plan.Guides.X;
        Assert.Equal(["A", "B", "C", "D"], axis.TickLabels.ToArray());
        var step = axis.Length / (axis.Domain.Upper - axis.Domain.Lower);
        for (var i = 0; i < 4; i++)
        {
            var middle = (i + 0.45 - axis.Domain.Lower) * step;
            Assert.InRange(axis.Pixels[i], middle - 1e-9, middle + 1e-9);
        }

        using var many = Data.Create(12, 1, names: i => $"Category {i + 1:00}");
        var manySpec = many.Bars() with { Theme = Theme.Light };
        var list = plan.Render(manySpec);
        axis = plan.Guides.X;
        step = axis.Length / (axis.Domain.Upper - axis.Domain.Lower);

        // The least k whose labels, held inside the figure (row G3b), keep half an em apart: here the first one moves right.
        var labelWidth = FakeTextBackend.Advance(12) * 11;
        var (lower, em) = (axis.Domain.Lower, axis.Em);
        var held = axis;
        var k = Enumerable
            .Range(1, 12)
            .First(n =>
            {
                var centers = Enumerable.Range(0, 12).Where(i => i % n == 0).Select(i => held.Hold((i + 0.45 - lower) * step, labelWidth)).ToArray();
                return centers.Zip(centers.Skip(1)).All(pair => Math.Abs(pair.Second - pair.First) - labelWidth >= em / 2);
            });
        Assert.Equal(Enumerable.Range(0, 12).Where(i => i % k == 0).Select(i => $"Category {i + 1:00}"), axis.TickLabels.ToArray());

        // The grid's x lines come first, then y's; after the two axis lines, x's ticks (GuideLayoutTests).
        var polylines = list.Polylines;
        var segments = list.Segments;
        var ticks = axis.Count + plan.Guides.Y.Count + 2;
        for (var i = 0; i < axis.Count; i++)
        {
            var (grid, tick) = (segments[polylines[i].SegmentStart], segments[polylines[ticks + i].SegmentStart]);
            Assert.Equal(grid.Start.X, grid.End.X);
            Assert.Equal(grid.Start.X, tick.Start.X);
            Assert.Equal(tick.Start.X, tick.End.X);
        }

        var firstY = segments[polylines[axis.Count].SegmentStart];
        Assert.Equal(firstY.Start.Y, firstY.End.Y);
        var afterTicks = segments[polylines[ticks + axis.Count].SegmentStart];
        Assert.NotEqual(afterTicks.Start.X, afterTicks.End.X);

        _ = plan.Render(manySpec with { ScaleX = Scale.Band() with { Reverse = true } });
        Assert.True(plan.Guides.X.Pixels[0] > plan.Guides.X.Pixels[1]);

        // With a step between the label's width and the width plus half an em, neighbors would not overlap but stand
        // closer than the gap: every other category is labeled.
        var width = FakeTextBackend.Advance(12) * 11;
        var steps = axis.Length / (width + (axis.Em * 3 / 8));
        var n = (int)Math.Floor(steps);
        using var close = Data.Create(n, 1, names: i => $"Category {i + 1:00}");
        _ = plan.Render(close.Bars() with { Theme = Theme.Light, ScaleX = Scale.Band(paddingOuter: (steps - n + 0.1) / 2) });
        axis = plan.Guides.X;
        step = axis.Length / (axis.Domain.Upper - axis.Domain.Lower);
        Assert.InRange(step, width + 0.1, width + (axis.Em / 2) - 0.1);
        Assert.Equal(Enumerable.Range(0, n).Where(i => i % 2 == 0).Select(i => $"Category {i + 1:00}"), axis.TickLabels.ToArray());
    }

    /// <summary>
    /// With 100,000 categories, the layout measures the labels it shows and those of the pairs that overlapped, not all the
    /// categories, and drawing the spec again measures nothing new and allocates nothing (design §4.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A 640 × 360 target with the light theme and a fake text backend that records each text it shapes; bars over 100,000
    ///   categories of names of one length.
    ///
    /// Steps:
    /// - Render; count the names shaped; render twice again and count the bytes.
    ///
    /// Expected:
    /// - The names shaped at most three times the labels shown, plus 3, far below the 100,000 categories: with names of
    ///   one length, k₀ takes the neighbor of the first label at its least size, so one k is cut short at its first pair;
    ///   the last label, held inside the figure since row G3b, can cut one more k short at the last pair, after a pass of a
    ///   smaller k (here 2, 12, and the 9 shown: 21, the first label shaped once of its three). The bound allows each of the
    ///   three terms (the two k cut short and the k chosen) the labels shown plus 1 in sum (design §10, row G7b). 0 bytes on
    ///   the redraws.
    /// </remarks>
    [Fact]
    public void Axis_ManyCategories_MeasuresTheShownLabels()
    {
        using var pool = new PlotWorkerPool(2);
        using var data = Data.Create(100_000, 1, names: i => $"n{i:000000}");
        var backend = new FakeTextBackend();
        using var plan = new PlotPlan(pool, 640, 360, new TextEngine(backend), 1);
        var spec = data.Bars() with { Theme = Theme.Light };

        _ = plan.Render(spec);
        var shapedNames = backend.Shaped.Count(s => s.Text.StartsWith('n'));
        Assert.InRange(shapedNames, plan.Guides.X.Count, (3 * plan.Guides.X.Count) + 3);

        _ = plan.Render(spec);
        var before = GC.GetAllocatedBytesForCurrentThread();
        _ = plan.Render(spec);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>
    /// A new categorical column is the data stage; a band's paddings the scale stage; its reverse and title only lay out and
    /// record (design §4.8).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Bars over 3 categories, drawn once.
    ///
    /// Steps:
    /// - Render with another column of 3 categories; with PaddingInner 0.2; with Reverse; with a title.
    ///
    /// Expected:
    /// - (layouts, extractions, recordings) changes (1, 1, 1), (1, 1, 1), (1, 0, 1), (1, 0, 1).
    /// </remarks>
    [Fact]
    public void Diff_StagesByMember()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = Data.Create(3, 1);
        var spec = data.Bars();
        using var plan = TestPlans.Create(pool, Size, Size);
        _ = plan.Render(spec);

        var other = spec with { Layers = [new BarLayerSpec { X = data.Table.Cat("other").ToColumnRef(), Y = data.Table.Col<double>("y") }] };
        Assert.Equal((1, 1, 1), Counts(other));
        Assert.Equal((1, 1, 1), Counts(other with { ScaleX = Scale.Band(paddingInner: 0.2) }));
        Assert.Equal((1, 0, 1), Counts(other with { ScaleX = Scale.Band(paddingInner: 0.2) with { Reverse = true } }));
        Assert.Equal((1, 0, 1), Counts(other with { ScaleX = Scale.Band(paddingInner: 0.2).WithTitle("T") with { Reverse = true } }));

        (int, int, int) Counts(PlotSpec next)
        {
            var before = (plan.Layouts, plan.Extractions, plan.Recordings);
            _ = plan.Render(next);
            return (plan.Layouts - before.Layouts, plan.Extractions - before.Extractions, plan.Recordings - before.Recordings);
        }
    }

    /// <summary>
    /// The workers' extremes match a count on the CPU, and are made again when the Y column changes (design §5.5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 2,000,000 seeded rows (two chunks) over 7 categories, some missing and NaN; a 100 × 100 target, y domain [−10, 10].
    ///
    /// Steps:
    /// - Render; change the rows and invalidate the column; render again.
    ///
    /// Expected:
    /// - Both times, each category's rects reach the CPU's greatest and least, within 1e-3 pixels.
    /// </remarks>
    [Fact]
    public void Extremes_MatchTheCpu_AndFollowTheColumn()
    {
        const int Rows = 2_000_000;
        using var pool = new PlotWorkerPool(2);
        var random = new Random(8);
        var names = Enumerable.Range(0, Rows).Select(i => i % 13 == 0 ? null : $"g{random.Next(7)}").ToArray();
        var values = Enumerable.Range(0, Rows).Select(i => i % 17 == 0 ? double.NaN : (random.NextDouble() * 18) - 9).ToArray();
        using var y = Column.Wrap(values);
        var t = DataTable.Create().Add("c", Column.Categorical(names)).Add("y", y).Build();
        var spec = Spec(t, new BarLayerSpec { X = t.Cat("c").ToColumnRef(), Y = t.Col<double>("y") }) with
        {
            ScaleX = Scale.Band(0, 0),
            ScaleY = Scale.Linear().WithDomain(-10, 10),
        };
        using var plan = TestPlans.Create(pool, Size, Size);

        Check();
        for (var i = 0; i < Rows; i += 5)
        {
            values[i] = values[i] * 0.5;
        }

        y.Invalidate(0, Rows);
        Check();

        void Check()
        {
            var rects = DataRects(plan.Render(spec));
            var categories = ((CategoricalColumn)t.GetColumn(0)).Categories;
            for (var c = 0; c < categories.Count; c++)
            {
                var name = categories[c];
                var mine = Enumerable.Range(0, Rows).Where(i => names[i] == name && !double.IsNaN(values[i])).Select(i => values[i]).ToArray();
                var left = c * (Size / (float)categories.Count);
                var ofCategory = rects.Where(r => Math.Abs(r.X - left) < 1e-3).ToArray();
                Assert.Contains(ofCategory, r => Math.Abs(r.Y - (50 - (mine.Max() * 5))) < 1e-3);
                Assert.Contains(ofCategory, r => Math.Abs(r.Y + r.Height - (50 - (mine.Min() * 5))) < 1e-3);
            }
        }
    }

    /// <summary>Once warm, bars over categories drawn again allocate nothing (design §4.8).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A 640 × 360 target with the light theme; bars over 50 categories and over 5,000 (bands under a pixel), drawn once.
    ///
    /// Steps:
    /// - Render each five times.
    ///
    /// Expected:
    /// - 0 bytes on this thread.
    /// </remarks>
    [Fact]
    public void Warm_AllocatesNothing()
    {
        using var pool = new PlotWorkerPool(1);
        foreach (var count in new[] { 50, 5_000 })
        {
            using var data = Data.Create(count, 2);
            using var plan = new PlotPlan(pool, 640, 360, new TextEngine(new FakeTextBackend()), 1);
            var spec = data.Bars() with { Theme = Theme.Light };
            _ = plan.Render(spec);
            _ = plan.Render(spec);

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 5; i++)
            {
                _ = plan.Render(spec);
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
    }

    /// <summary>A table of n categories with rows each, a second categorical column of other names, and seeded values.</summary>
    private sealed class Data : IDisposable
    {
        private readonly Column<double> _y;

        private Data(DataTable table, Column<double> y) => (Table, _y) = (table, y);

        public DataTable Table { get; }

        public static Data Create(int categories, int rows, bool positive = false, Func<int, string>? names = null)
        {
            names ??= i => $"k{i:000000}";
            var random = new Random(categories);
            var values = Enumerable
                .Range(0, categories * rows)
                .Select(_ => positive ? 1 + (random.NextDouble() * 8) : (random.NextDouble() * 18) - 9)
                .ToArray();
            var category = Enumerable.Range(0, categories * rows).Select(i => names(i / rows)).ToArray<string?>();
            var other = Enumerable.Range(0, categories * rows).Select(i => $"o{i / rows}").ToArray<string?>();
            var y = Column.Wrap(values);
            var table = DataTable.Create().Add("c", Column.Categorical(category)).Add("other", Column.Categorical(other)).Add("y", y).Build();
            return new Data(table, y);
        }

        /// <summary>Returns a spec of bars over the first column, on a band scale, without guides.</summary>
        public PlotSpec Bars() =>
            Spec(Table, new BarLayerSpec { X = Table.Cat("c").ToColumnRef(), Y = Table.Col<double>("y") }) with
            {
                ScaleX = Scale.Band(),
                ScaleY = Scale.Linear().WithDomain(-10, 10),
            };

        public void Dispose() => _y.Dispose();
    }

    /// <summary>A geom that reads its frame's bands and tries PixelX and Map.</summary>
    private sealed class ProbeGeom(int count) : IGeom
    {
        public double[] Starts { get; } = new double[count];

        public double Width { get; private set; }

        public bool PixelThrew { get; private set; }

        public bool MapThrew { get; private set; }

        public void Learn(GeomRanges ranges) { }

        public void Record(GeomFrame frame, RenderList list)
        {
            for (var i = 0; i < Starts.Length; i++)
            {
                Starts[i] = frame.BandX(i);
            }

            Width = frame.BandWidth;
            PixelThrew = Throws(() => frame.PixelX(1.0));
            MapThrew = Throws(() => frame.Map(1, 1, out _, out _));
        }

        private static bool Throws(Action action)
        {
            try
            {
                action();
                return false;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }
}
