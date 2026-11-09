using System.Numerics;
using HyperPlot.Interaction;
using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>
/// The hit test of the element layers (design §6.3 "要素の層", §10 "ヒットテスト（I2c）", requirement I-2, row I2c, Issue
/// #355): bars, error bars, and rectangles, checked against every element mapped to the screen with
/// <see cref="PlotEngine.ViewToPixel"/>.
/// </summary>
public sealed class ElementHitTests
{
    private static readonly PanelId Panel = new(0, 0);

    /// <summary>
    /// A numeric x's bars answer in <see cref="HitMode.X"/> with the row whose x is nearest the cursor's, in the gaps between
    /// bars too, passing rows whose y is NaN, with fewer bars than 4W and with more (Issue #355 Decision 1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Bars over a uniform x of 300 rows and of 10,000 rows (past 4W), a y of sin(i · 0.37) · 10, NaN in rows 100 to 119.
    ///
    /// Steps:
    /// - Hit test 41 cursors along the panel's middle row, inside its ends.
    ///
    /// Expected:
    /// - One hit each, whose x pixel is as near the cursor's as the nearest row with a number (every row mapped); its row's x
    ///   and y; not derived.
    /// </remarks>
    [Theory]
    [InlineData(300)]
    [InlineData(10_000)]
    public void NumericBars_X_TakeTheNearestRow(int rows)
    {
        var ys = Enumerable.Range(0, rows).Select(i => i is >= 100 and < 120 ? double.NaN : Math.Sin(i * 0.37) * 10).ToArray();
        using var y = Column.Wrap(ys);
        var table = DataTable.Create().Add("x", Column.Uniform(0, 1, rows)).Add("y", y).Build();
        using var engine = Drawn(Plot.Create(table).Bar(table.Col<double>("x"), table.Col<double>("y")).Build());
        var hits = new SeriesHit[2];
        for (var k = 0; k <= 40; k++)
        {
            var cursor = Inside(engine, (k + 0.5) / 41, 0.5);
            var hit = hits[..engine.HitTest(cursor, 0, HitMode.X, hits)];
            var only = Assert.Single(hit);
            Assert.False(double.IsNaN(only.Y));
            Assert.Equal((double)only.Row, only.X, 9);
            Assert.Equal(ys[only.Row], only.Y);
            Assert.False(only.Derived);
            var nearest = Enumerable.Range(0, rows).Where(i => !double.IsNaN(ys[i])).Min(i => Math.Abs(Pixel(engine, i, 0).X - cursor.X));
            Assert.Equal(nearest, Math.Abs(Pixel(engine, only.X, 0).X - cursor.X), 3);
        }
    }

    /// <summary>
    /// A numeric x's bars answer in <see cref="HitMode.XY"/> with the bar whose rectangle lies nearest the cursor within the
    /// radius; where a pixel column holds more than 64 bars, the one reaching furthest to the cursor's side of the baseline
    /// stands for them, within a pixel of the nearest (Issue #355 Decision 2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Bars over a uniform x of 300 rows and of 100,000 rows (about 160 a pixel column), a y of sin(i · 0.37) · 10.
    ///
    /// Steps:
    /// - Hit test 40 seeded cursors in the panel with a radius of 15 pixels; compute every bar's distance on the screen.
    ///
    /// Expected:
    /// - A hit where some bar lies within the radius (less half a pixel), none where none does (more half a pixel). The hit's
    ///   distance is the brute force's least: exactly with 300 rows, within a pixel with 100,000.
    /// </remarks>
    [Theory]
    [InlineData(300, 0.5)]
    [InlineData(100_000, 1.0)]
    public void NumericBars_XY_TakeTheNearestBar(int rows, double within)
    {
        var ys = Enumerable.Range(0, rows).Select(i => Math.Sin(i * 0.37) * 10).ToArray();
        using var y = Column.Wrap(ys);
        var table = DataTable.Create().Add("x", Column.Uniform(0, 1, rows)).Add("y", y).Build();
        using var engine = Drawn(Plot.Create(table).Bar(table.Col<double>("x"), table.Col<double>("y")).Build());
        var random = new Random(7);
        var hits = new SeriesHit[2];
        const float Radius = 15;
        for (var k = 0; k < 40; k++)
        {
            var cursor = Inside(engine, random.NextDouble(), random.NextDouble());
            var best = Enumerable.Range(0, rows).Min(i => Box(cursor, Pixel(engine, i - 0.45, 0), Pixel(engine, i + 0.45, ys[i])));
            var found = engine.HitTest(cursor, Radius, HitMode.XY, hits);
            if (best > Radius + within)
            {
                Assert.Equal(0, found);
                continue;
            }

            if (best < Radius - within)
            {
                var hit = hits[0];
                Assert.Equal(1, found);
                Assert.Equal(ys[hit.Row], hit.Y);
                Assert.InRange(hit.Distance, best - 1e-3, best + within);
            }
        }
    }

    /// <summary>
    /// A category's bar answers with the end on the cursor's side of the baseline and the first row of that value; in a gap,
    /// the nearest category (Issue #355 Decision 3). The hit's x is the category's number.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Bars over 5 categories "a" to "e", 4 rows each, of values from −3 to 6; category "c" has its greatest, 6, in two rows.
    ///
    /// Steps:
    /// - Hit test in X mode above the baseline at each category's middle, below it, and in the gap after "b".
    ///
    /// Expected:
    /// - Above: the category's greatest value and its first row; "c" the first of its two rows of 6. Below: the least value
    ///   and its row, or the greatest for "c", whose bar does not reach below the baseline. In the gap: "b" or "c", the nearer. x is the category's number; not derived.
    /// </remarks>
    [Fact]
    public void CategoryBars_TakeTheEndOnTheCursorsSide()
    {
        string[] names = ["a", "b", "c", "d", "e"];
        var codes = Enumerable.Range(0, 20).Select(i => names[i % 5]).ToArray();
        var values = Enumerable.Range(0, 20).Select(i => (double)(((i * 7) % 10) - 3)).ToArray();
        values[2] = 6;
        values[7] = 6;
        using var y = Column.Wrap(values);
        var table = DataTable.Create().Add("c", Column.Categorical(codes!)).Add("y", y).Build();
        using var engine = Drawn(Plot.Create(table).Bar(table.Cat("c"), table.Col<double>("y")).Build());
        var hits = new SeriesHit[2];
        for (var c = 0; c < 5; c++)
        {
            var rows = Enumerable.Range(0, 20).Where(i => i % 5 == c).ToArray();
            var (max, min) = (rows.Max(i => values[i]), rows.Min(i => values[i]));

            // A category whose bar reaches one side of the baseline only answers with that end on either side.
            foreach (var (above, value) in new[] { (true, max > 0 ? max : min), (false, min < 0 ? min : max) })
            {
                var cursor = Pixel(engine, c + 0.45, above ? 5.5 : -2.5);
                var hit = Assert.Single(hits[..engine.HitTest(cursor, 0, HitMode.X, hits)]);
                Assert.Equal((double)c, hit.X);
                Assert.Equal(value, hit.Y);
                Assert.Equal(rows.First(i => values[i] == value), hit.Row);
                Assert.False(hit.Derived);
            }
        }

        var gap = Pixel(engine, 1.95, 1);
        var near = Assert.Single(hits[..engine.HitTest(gap, 0, HitMode.X, hits)]);
        Assert.InRange(near.X, 1.0, 2.0);
    }

    /// <summary>
    /// Bands narrower than a pixel answer with the pixel column's category of the extreme on the cursor's side, as drawn
    /// (Issue #355 Decision 10).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Bars over 3,000 categories (more than the panel's pixels), one row each, of seeded values in [−5, 5].
    ///
    /// Steps:
    /// - Hit test in X mode above the panel's middle row at 20 columns, and below it.
    ///
    /// Expected:
    /// - Above: the row of the greatest of the categories whose bands start in the cursor's pixel column; below: the least.
    /// </remarks>
    [Fact]
    public void CategoryBars_UnderAPixel_TakeTheColumnsExtreme()
    {
        const int Count = 3000;
        var random = new Random(11);
        var names = Enumerable.Range(0, Count).Select(i => $"k{i}").ToArray();
        var values = Enumerable.Range(0, Count).Select(_ => (random.NextDouble() * 10) - 5).ToArray();
        using var y = Column.Wrap(values);
        var table = DataTable.Create().Add("c", Column.Categorical(names!, names)).Add("y", y).Build();
        using var engine = Drawn(Plot.Create(table).Bar(table.Cat("c"), table.Col<double>("y")).Build());
        var hits = new SeriesHit[2];
        for (var k = 0; k < 20; k++)
        {
            var at = Inside(engine, (k + 0.5) / 20, 0.5);
            foreach (var above in new[] { true, false })
            {
                var cursor = at with { Y = Pixel(engine, 0, above ? 4.9 : -4.9).Y };
                var inColumn = Enumerable.Range(0, Count).Where(c => MathF.Floor(Pixel(engine, c, 0).X - 1e-4f) == MathF.Floor(cursor.X)).ToArray();
                if (inColumn.Length == 0)
                {
                    continue;
                }

                var expected = above ? inColumn.MaxBy(c => values[c]) : inColumn.MinBy(c => values[c]);
                var hit = Assert.Single(hits[..engine.HitTest(cursor, 0, HitMode.X, hits)]);
                Assert.Equal(expected, hit.Row);
                Assert.Equal(values[expected], hit.Y);
            }
        }
    }

    /// <summary>
    /// Stacked bars answer with each group's piece at its outer end, marked derived, numbered 2 a group: over a numeric x the
    /// stack's table's row, over categories the cell; a fill position gives the shares (Issue #355 Decision 4).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Numeric: 100 x values, 2 groups "a" and "b" of 1 each, stacked; and filled. Categories: 3 categories, 2 groups, each
    ///   cell 2 except "b" of category 1, −1.
    ///
    /// Steps:
    /// - Hit test in X mode at the panel's middle (numeric), and at category 1's middle.
    ///
    /// Expected:
    /// - Numeric stacked: series 0 and 2, y 2 and 1 (group 0 is stacked outermost); filled: 1 and 0.5; all derived. Categories: series 0 (y 2, row 0 · 3 + 1)
    ///   and series 3 (group b's negative piece, y −1, row 1 · 3 + 1); x 1; derived.
    /// </remarks>
    [Fact]
    public void StackedBars_TakeEachGroupsOuterEnd()
    {
        using var x = Column.Wrap(Enumerable.Range(0, 200).Select(i => (double)(i / 2)).ToArray());
        using var ones = Column.Wrap(Enumerable.Repeat(1.0, 200).ToArray());
        var groups = Column.Categorical([.. Enumerable.Range(0, 200).Select(i => i % 2 == 0 ? "a" : "b")]);
        var table = DataTable.Create().Add("x", x).Add("y", ones).Add("g", groups).Build();
        foreach (var (position, ends) in new (PositionSpec, double[])[] { (Position.Stack, [2, 1]), (Position.Fill, [1, 0.5]) })
        {
            using var engine = Drawn(
                Plot.Create(table)
                    .Bar(table.Col<double>("x"), table.Col<double>("y"), configure: b => b.ColorBy(table.Cat("g")).Position(position))
                    .Build()
            );
            var hits = new SeriesHit[4];
            var found = hits[..engine.HitTest(Inside(engine, 0.5, 0.5), 0, HitMode.X, hits)];
            Assert.Equal([0, 2], found.Select(h => h.Series).Order());
            Assert.Equal(ends, found.OrderBy(h => h.Series).Select(h => h.Y));
            Assert.All(found, h => Assert.True(h.Derived));
        }

        using var cells = Column.Wrap(new[] { 2.0, 2, 2, 2, -1, 2 });
        var categories = DataTable
            .Create()
            .Add("c", Column.Categorical(["k0", "k1", "k2", "k0", "k1", "k2"]))
            .Add("g", Column.Categorical(["a", "a", "a", "b", "b", "b"]))
            .Add("y", cells)
            .Build();
        using var stacked = Drawn(
            Plot.Create(categories)
                .Bar(categories.Cat("c"), categories.Col<double>("y"), configure: b => b.ColorBy(categories.Cat("g")).Position(Position.Stack))
                .Build()
        );
        var cellHits = new SeriesHit[8];
        var answer = cellHits[..stacked.HitTest(Pixel(stacked, 1.45, 1), 0, HitMode.X, cellHits)].OrderBy(h => h.Series).ToArray();
        Assert.Equal([0, 3], answer.Select(h => h.Series));
        Assert.Equal([2.0, -1.0], answer.Select(h => h.Y));
        Assert.Equal([1L, 4L], answer.Select(h => h.Row));
        Assert.All(answer, h => Assert.True(h.Derived));
        Assert.All(answer, h => Assert.Equal(1.0, h.X));
    }

    /// <summary>
    /// Dodged bars answer with the row of the plot's table and its data x; the hover's marker sits on the dodged bar's middle
    /// (Issue #355 Decision 5).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 40 rows: x = row / 2 (two rows an x), a category "g" alternating "a" and "b", y = 1 + row % 3; bars colored by "g" and
    ///   dodged; the default hover.
    ///
    /// Steps:
    /// - Hit test in X mode at the panel's middle; hover there and draw.
    ///
    /// Expected:
    /// - Two hits, of the two categories, whose rows hold their x and y in the table. Each marker's center lies at the pixel of
    ///   x + 0.9 · ((k + 0.5) / 2 − 0.5) for its category k.
    /// </remarks>
    [Fact]
    public void DodgedBars_KeepTheirRowsAndMarkTheirPlaces()
    {
        using var x = Column.Wrap(Enumerable.Range(0, 40).Select(i => i / 2.0).Select(Math.Floor).ToArray());
        using var y = Column.Wrap(Enumerable.Range(0, 40).Select(i => 1.0 + (i % 3)).ToArray());
        var table = DataTable
            .Create()
            .Add("x", x)
            .Add("y", y)
            .Add("g", Column.Categorical([.. Enumerable.Range(0, 40).Select(i => i % 2 == 0 ? "a" : "b")]))
            .Build();
        var backend = new NullBackend();
        using var engine = Drawn(
            Plot.Create(table)
                .Bar(table.Col<double>("x"), table.Col<double>("y"), width: 0.9, configure: b => b.ColorBy(table.Cat("g")).Position(Position.Dodge()))
                .Build(),
            backend
        );
        var hits = new SeriesHit[4];
        var cursor = Inside(engine, 0.5, 0.5);
        var found = hits[..engine.HitTest(cursor, 0, HitMode.X, hits)];
        Assert.Equal(2, found.Length);
        Assert.Equal([0, 1], found.Select(h => h.Series).Order());
        foreach (var hit in found)
        {
            Assert.Equal(Math.Floor(hit.Row / 2.0), hit.X);
            Assert.Equal(1.0 + (hit.Row % 3), hit.Y);
            Assert.Equal(hit.Series, (int)(hit.Row % 2));
        }

        engine.OnPointer(
            new PointerEvent(PointerEventKind.Move, cursor, PointerButtons.None, PointerButtons.None, ModifierKeys.None, PointerKind.Mouse, 0)
        );
        engine.RenderFrame();
        var discs = backend.LastList!.SectorCalls.ToArray();
        Assert.Equal(4, discs.Length);
        foreach (var hit in found)
        {
            var at = Array.IndexOf(engine.Hover.ToArray(), hit);
            var shift = 0.9 * (((hit.Series + 0.5) / 2) - 0.5);
            Assert.Equal(Pixel(engine, hit.X + shift, hit.Y).X, discs[(2 * at) + 1].Center.X, 2);
        }
    }

    /// <summary>
    /// Error bars answer with a row's low end (series 0) and high end (series 1): in X mode the row nearest in x, in XY the
    /// nearest line or cap; a pixel column of more than 64 rows stands in by its greatest high end above, its least low end
    /// below, and its row nearest along between (Issue #355 Decisions 6 and 11).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Error bars of 40 rows (x = row, from y − 1 to y + 1, y = row % 5) with caps of 6 DIPs; and of 100,000 rows of seeded
    ///   ends.
    ///
    /// Steps:
    /// - X mode at row 20's x. XY with a radius of 2 pixels at row 20's low cap, 1 pixel inside its end, and 5 pixels past it.
    ///   On the long one, XY with a radius of 40 at three points of one pixel column: above every high end of the column,
    ///   below every low end, and in the middle; compute every row's distance on the screen.
    ///
    /// Expected:
    /// - X: series 0 and 1 of row 20, with its two ends. XY at the cap: row 20; past it: nothing. The long one: both series of
    ///   one row, within a pixel of the brute force's least distance, at each of the three points.
    /// </remarks>
    [Fact]
    public void ErrorBars_AnswerBothEnds()
    {
        var mid = Enumerable.Range(0, 40).Select(i => (double)(i % 5)).ToArray();
        using var low = Column.Wrap(mid.Select(v => v - 1).ToArray());
        using var high = Column.Wrap(mid.Select(v => v + 1).ToArray());
        var table = DataTable.Create().Add("x", Column.Uniform(0, 1, 40)).Add("lo", low).Add("hi", high).Build();
        using var engine = Drawn(
            Plot.Create(table)
                .ErrorBar(table.Col<double>("x"), table.Col<double>("lo"), table.Col<double>("hi"))
                .ScaleY(Scale.Linear().WithDomain(-3, 7))
                .Build()
        );
        var hits = new SeriesHit[4];
        var at = Pixel(engine, 20, 0);
        var found = hits[..engine.HitTest(at, 0, HitMode.X, hits)];
        Assert.Equal([(0, 20L, -1.0), (1, 20L, 1.0)], found.Select(h => (h.Series, h.Row, h.Y)));

        var cap = Pixel(engine, 20, -1) + new Vector2(2, 0);
        Assert.Equal(20, hits[..engine.HitTest(cap, 2, HitMode.XY, hits)].Select(h => h.Row).Distinct().Single());
        Assert.Equal(0, engine.HitTest(Pixel(engine, 20, -1) + new Vector2(8, 0), 2, HitMode.XY, hits));

        const int Many = 100_000;
        var random = new Random(3);
        var centers = Enumerable.Range(0, Many).Select(_ => (random.NextDouble() * 10) - 5).ToArray();
        using var lows = Column.Wrap(centers.Select(v => v - 1).ToArray());
        using var highs = Column.Wrap(centers.Select(v => v + 1).ToArray());
        var long_ = DataTable.Create().Add("x", Column.Uniform(0, 1, Many)).Add("lo", lows).Add("hi", highs).Build();
        using var dense = Drawn(
            Plot.Create(long_)
                .ErrorBar(long_.Col<double>("x"), long_.Col<double>("lo"), long_.Col<double>("hi"))
                .ScaleY(Scale.Linear().WithDomain(-10, 10))
                .Build()
        );
        var middle = Inside(dense, 0.5, 0.5);
        var column = MathF.Floor(middle.X) + 0.5f;
        var rows = Enumerable.Range(0, Many).Where(i => MathF.Floor(Pixel(dense, i, 0).X) == MathF.Floor(column)).ToArray();
        var highest = rows.MaxBy(i => centers[i]);
        var lowest = rows.MinBy(i => centers[i]);
        var top = new Vector2(column, Pixel(dense, 0, centers[highest] + 1.5).Y);
        var bottom = new Vector2(column, Pixel(dense, 0, centers[lowest] - 1.5).Y);
        var answers = new SeriesHit[4];
        foreach (var cursor in new[] { top, bottom, new Vector2(column, middle.Y) })
        {
            // Every row's line on the screen: the stand-ins of the long runs lie within a pixel of the nearest.
            var best = Enumerable.Range(0, Many).Min(i => Box(cursor, Pixel(dense, i, centers[i] - 1), Pixel(dense, i, centers[i] + 1)));
            var near = answers[..dense.HitTest(cursor, 40, HitMode.XY, answers)];
            Assert.Equal([0, 1], near.Select(h => h.Series));
            Assert.Equal(near[0].Row, near[1].Row);
            Assert.InRange(near[0].Distance, best - 1e-3, best + 1.0);
        }
    }

    /// <summary>A category's error bar answers with the rows of its least low end and its greatest high end (Issue #355 Decision 6).</summary>
    /// <remarks>
    /// Preconditions:
    /// - Error bars over 3 categories, 4 rows each, of seeded ends.
    ///
    /// Steps:
    /// - Hit test in X mode at each category's middle.
    ///
    /// Expected:
    /// - Series 0: the category's least YMin and the first row of it; series 1: its greatest YMax and its first row. x is the
    ///   category's number.
    /// </remarks>
    [Fact]
    public void CategoryErrorBars_TakeTheExtremeRows()
    {
        var random = new Random(5);
        var codes = Enumerable.Range(0, 12).Select(i => $"k{i % 3}").ToArray();
        var mins = Enumerable.Range(0, 12).Select(_ => Math.Round(random.NextDouble() * 4)).ToArray();
        var maxs = mins.Select(v => v + 1 + Math.Round(random.NextDouble() * 3)).ToArray();
        using var low = Column.Wrap(mins);
        using var high = Column.Wrap(maxs);
        var table = DataTable.Create().Add("c", Column.Categorical(codes!)).Add("lo", low).Add("hi", high).Build();
        using var engine = Drawn(Plot.Create(table).ErrorBar(table.Cat("c"), table.Col<double>("lo"), table.Col<double>("hi")).Build());
        var hits = new SeriesHit[4];
        for (var c = 0; c < 3; c++)
        {
            var rows = Enumerable.Range(0, 12).Where(i => i % 3 == c).ToArray();
            var found = hits[..engine.HitTest(Pixel(engine, c + 0.45, 2), 0, HitMode.X, hits)];
            Assert.Equal(2, found.Length);
            Assert.Equal(rows.First(i => mins[i] == rows.Min(r => mins[r])), found[0].Row);
            Assert.Equal(rows.Min(r => mins[r]), found[0].Y);
            Assert.Equal(rows.First(i => maxs[i] == rows.Max(r => maxs[r])), found[1].Row);
            Assert.Equal(rows.Max(r => maxs[r]), found[1].Y);
            Assert.All(found, h => Assert.Equal((double)c, h.X));
        }
    }

    /// <summary>
    /// Rectangles answer from the pixels last drawn (Issue #355 Decision 7): in X mode, of those whose x span holds the
    /// cursor's, one whose y span holds it too, else the nearest across; in XY the nearest the radius reaches; equal distances
    /// keep the later row; the x and y are the center.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Three rectangles: [0, 4] × [0, 4], [1, 3] × [1, 3] (inside the first), and [6, 8] × [6, 8].
    ///
    /// Steps:
    /// - X mode at (2, 2), (2, 7), and (5, 2); XY with a radius of 10 pixels near the third's corner, and at (5, 5) with 1.
    ///
    /// Expected:
    /// - (2, 2): row 1 (both hold it; the later one), center (2, 2). (2, 7): row 0, the nearer across of the two whose x span
    ///   holds 2. (5, 2): nothing (no x span holds 5). XY near the corner: row 2; at (5, 5) with a radius of 1: nothing.
    /// </remarks>
    [Fact]
    public void Rects_TakeTheOnesTheCursorMeets()
    {
        using var x0 = Column.Wrap(new[] { 0.0, 1, 6 });
        using var x1 = Column.Wrap(new[] { 4.0, 3, 8 });
        using var y0 = Column.Wrap(new[] { 0.0, 1, 6 });
        using var y1 = Column.Wrap(new[] { 4.0, 3, 8 });
        var table = DataTable.Create().Add("x0", x0).Add("x1", x1).Add("y0", y0).Add("y1", y1).Build();
        using var engine = Drawn(
            Plot.Create(table)
                .Rect(table.Col<double>("x0"), table.Col<double>("x1"), table.Col<double>("y0"), table.Col<double>("y1"))
                .ScaleX(Scale.Linear().WithDomain(-1, 9))
                .ScaleY(Scale.Linear().WithDomain(-1, 9))
                .Build()
        );
        var hits = new SeriesHit[2];
        var inner = Assert.Single(hits[..engine.HitTest(Pixel(engine, 2, 2), 0, HitMode.X, hits)]);
        Assert.Equal((1L, 2.0, 2.0), (inner.Row, inner.X, inner.Y));
        Assert.Equal(0, Assert.Single(hits[..engine.HitTest(Pixel(engine, 2, 7), 0, HitMode.X, hits)]).Row);
        Assert.Equal(0, engine.HitTest(Pixel(engine, 5, 2), 0, HitMode.X, hits));
        Assert.Equal(2, Assert.Single(hits[..engine.HitTest(Pixel(engine, 8, 8) + new Vector2(5, -5), 10, HitMode.XY, hits)]).Row);
        Assert.Equal(0, engine.HitTest(Pixel(engine, 5, 5), 1, HitMode.XY, hits));
    }

    /// <summary>Flipped bars read their x along the screen's vertical, and a polar panel answers nothing (design §6.3).</summary>
    /// <remarks>
    /// Preconditions:
    /// - Bars over a uniform x of 50 rows; flipped; and in polar.
    ///
    /// Steps:
    /// - Flipped: X mode at 10 cursors down the panel's middle column. Polar: X mode at the panel's middle.
    ///
    /// Expected:
    /// - Flipped: each hit's x pixel, now vertical, is the nearest row's to the cursor's y. Polar: 0 hits.
    /// </remarks>
    [Fact]
    public void Bars_FlippedAndPolar()
    {
        using var y = Column.Wrap(Enumerable.Range(0, 50).Select(i => 1.0 + (i % 4)).ToArray());
        var table = DataTable.Create().Add("x", Column.Uniform(0, 1, 50)).Add("y", y).Build();
        using var flipped = Drawn(Plot.Create(table).Bar(table.Col<double>("x"), table.Col<double>("y")).Coord(Coord.Cartesian(flip: true)).Build());
        var hits = new SeriesHit[2];
        for (var k = 0; k < 10; k++)
        {
            var cursor = Inside(flipped, (k + 0.5) / 10, 0.5);
            var hit = Assert.Single(hits[..flipped.HitTest(cursor, 0, HitMode.X, hits)]);
            var nearest = Enumerable.Range(0, 50).Min(i => Math.Abs(Pixel(flipped, i, 0).Y - cursor.Y));
            Assert.Equal(nearest, Math.Abs(Pixel(flipped, hit.X, 0).Y - cursor.Y), 3);
        }

        using var polar = Drawn(Plot.Create(table).Bar(table.Col<double>("x"), table.Col<double>("y")).Coord(Coord.Polar()).Build());
        Assert.Equal(0, polar.HitTest(Inside(polar, 0.5, 0.5), 0, HitMode.X, hits));
    }

    /// <summary>
    /// The hover's tooltip writes a category axis's value as the category's name, and its markers take a bar's fill and an
    /// error bar's line color (Issue #355 Decision 8, row I2b's markers).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Bars over 3 categories filled half-transparent green, one row each of 2, 4, and 6; error bars over the same categories
    ///   in red; the default hover.
    ///
    /// Steps:
    /// - Hover at category 1's middle and draw.
    ///
    /// Expected:
    /// - The tooltip reads "bar: k1, 4\nerror bar: k1, 3\nerror bar: k1, 5"; the bar's disc is the green made opaque, and the
    ///   error bar's discs red.
    /// </remarks>
    [Fact]
    public void Hover_NamesTheCategoryAndTakesTheElementsColors()
    {
        using var values = Column.Wrap(new[] { 2.0, 4, 6 });
        using var lows = Column.Wrap(new[] { 1.0, 3, 5 });
        using var highs = Column.Wrap(new[] { 3.0, 5, 7 });
        var table = DataTable.Create().Add("c", Column.Categorical(["k0", "k1", "k2"])).Add("y", values).Add("lo", lows).Add("hi", highs).Build();
        var green = new Color(20, 160, 40, 128);
        var red = new Color(220, 20, 20, 255);
        var backend = new NullBackend();
        using var engine = Drawn(
            Plot.Create(table)
                .Bar(table.Cat("c"), table.Col<double>("y"), configure: b => b.Fill(green))
                .ErrorBar(table.Cat("c"), table.Col<double>("lo"), table.Col<double>("hi"), configure: l => l.Color(red))
                .Build(),
            backend
        );
        engine.OnPointer(
            new PointerEvent(
                PointerEventKind.Move,
                Pixel(engine, 1.45, 3.5),
                PointerButtons.None,
                PointerButtons.None,
                ModifierKeys.None,
                PointerKind.Mouse,
                0
            )
        );
        engine.RenderFrame();

        var list = backend.LastList!;
        Assert.Equal("bar: k1, 4\nerror bar: k1, 3\nerror bar: k1, 5", Assert.Single(list.Texts.ToArray()).Text);
        var discs = list.SectorCalls.ToArray();
        Assert.Equal(6, discs.Length);
        Assert.Equal(Premultiplied(green with { A = 255 }), discs[1].Color);
        Assert.Equal(Premultiplied(red), discs[3].Color);
        Assert.Equal(Premultiplied(red), discs[5].Color);
    }

    /// <summary>A warm hit test over each element layer allocates nothing (invariant 3).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A numeric bar, a category bar, a stacked category bar, a numeric error bar, a category error bar, and a rectangle layer.
    ///
    /// Steps:
    /// - Hit test each in both modes once to warm; then 50 times each, counting the thread's allocations.
    ///
    /// Expected:
    /// - 0 bytes.
    /// </remarks>
    [Fact]
    public void HitTest_AllocatesNothing()
    {
        using var y = Column.Wrap(Enumerable.Range(0, 30).Select(i => 1.0 + (i % 4)).ToArray());
        using var lo = Column.Wrap(Enumerable.Range(0, 30).Select(i => (double)(i % 4)).ToArray());
        var table = DataTable
            .Create()
            .Add("x", Column.Uniform(0, 1, 30))
            .Add("c", Column.Categorical([.. Enumerable.Range(0, 30).Select(i => $"k{i % 3}")]))
            .Add("g", Column.Categorical([.. Enumerable.Range(0, 30).Select(i => i % 2 == 0 ? "a" : "b")]))
            .Add("y", y)
            .Add("lo", lo)
            .Build();
        var specs = new[]
        {
            Plot.Create(table).Bar(table.Col<double>("x"), table.Col<double>("y")).Build(),
            Plot.Create(table).Bar(table.Cat("c"), table.Col<double>("y")).Build(),
            Plot.Create(table)
                .Bar(table.Cat("c"), table.Col<double>("y"), configure: b => b.ColorBy(table.Cat("g")).Position(Position.Stack))
                .Build(),
            Plot.Create(table).ErrorBar(table.Col<double>("x"), table.Col<double>("lo"), table.Col<double>("y")).Build(),
            Plot.Create(table).ErrorBar(table.Cat("c"), table.Col<double>("lo"), table.Col<double>("y")).Build(),
            Plot.Create(table).Rect(table.Col<double>("x"), table.Col<double>("y"), table.Col<double>("lo"), table.Col<double>("y")).Build(),
        };
        var hits = new SeriesHit[8];
        foreach (var spec in specs)
        {
            using var engine = Drawn(spec);
            var cursor = Inside(engine, 0.4, 0.5);
            _ = engine.HitTest(cursor, 20, HitMode.X, hits);
            _ = engine.HitTest(cursor, 20, HitMode.XY, hits);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var k = 0; k < 50; k++)
            {
                _ = engine.HitTest(cursor, 20, HitMode.X, hits);
                _ = engine.HitTest(cursor, 20, HitMode.XY, hits);
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
    }

    private static PlotEngine Drawn(PlotSpec spec, NullBackend? backend = null)
    {
        // Categories go on a band x.
        if (spec.Layers.Any(l => l is BarLayerSpec { X.Column: CategoricalColumn } or ErrorBarLayerSpec { X.Column: CategoricalColumn }))
        {
            spec = spec with { ScaleX = Scale.Band() };
        }

        var engine = new PlotEngine(backend ?? new NullBackend(), 640, 360) { WaitsForIndexes = true };
        engine.SetPlot(spec);
        engine.RenderFrame();
        return engine;
    }

    /// <summary>Returns a pixel inside the panel at fractions of its view's x and y ranges.</summary>
    private static Vector2 Inside(PlotEngine engine, double fx, double fy)
    {
        var view = (PanelView2D)engine.CurrentView(Panel);
        var x = view.X.LowValue + (fx * (view.X.HighValue - view.X.LowValue));
        var y = view.Y.LowValue + (fy * (view.Y.HighValue - view.Y.LowValue));
        return engine.ViewToPixel(Panel, ViewRange.FromValues(x, x), ViewRange.FromValues(y, y));
    }

    private static Vector2 Pixel(PlotEngine engine, double x, double y) =>
        engine.ViewToPixel(Panel, ViewRange.FromValues(x, x), ViewRange.FromValues(y, y));

    /// <summary>Returns the distance from a point to the box two corners span.</summary>
    private static double Box(Vector2 point, Vector2 a, Vector2 b)
    {
        var dx = Math.Max(Math.Max(Math.Min(a.X, b.X) - point.X, point.X - Math.Max(a.X, b.X)), 0);
        var dy = Math.Max(Math.Max(Math.Min(a.Y, b.Y) - point.Y, point.Y - Math.Max(a.Y, b.Y)), 0);
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static Vector4 Premultiplied(Color color) =>
        new(
            HyperPlot.Rendering.Premultiplied.Multiply(color.R, color.A) / 255f,
            HyperPlot.Rendering.Premultiplied.Multiply(color.G, color.A) / 255f,
            HyperPlot.Rendering.Premultiplied.Multiply(color.B, color.A) / 255f,
            color.A / 255f
        );
}
