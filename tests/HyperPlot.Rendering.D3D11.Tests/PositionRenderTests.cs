using HyperPlot.Testing;
using HyperPlot.Tests;

namespace HyperPlot.Rendering.D3D11.Tests;

/// <summary>Row G5a's positions drawn on WARP (design §10.1 scenes 20 to 24), with the fake text backend.</summary>
public sealed class PositionRenderTests
{
    private const int Width = 640;
    private const int Height = 360;
    private static readonly string[] Groups = ["north", "south", "west"];

    /// <summary>
    /// Stacked bars over categories (§10.1 scene 20, row G5a): 6 categories in 3 groups, with negative pieces below 0 and a
    /// category without one group.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 6 categories × 3 groups of seeded values, two of them negative, group "south" without a row in the fourth category;
    ///   the fake text backend; Theme.Light; axes and a title.
    ///
    /// Steps:
    /// - Render at 640 × 360; compare with <c>Golden/position-stack.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void Stack_MatchesBaseline()
    {
        using var pool = new PlotWorkerPool(2);
        using var cells = Cells.Create(6, skip: (3, 1), negatives: [(1, 2), (4, 0)]);
        var spec = Plot.Create(cells.Table)
            .Bar(cells.Table.Cat("x"), cells.Table.Col<double>("y"), configure: b => b.ColorBy(cells.Table.Cat("g")))
            .ScaleX(Scale.Band().WithTitle("City"))
            .ScaleY(Scale.Linear().WithTitle("Sales"))
            .Title("Stacked")
            .Build();
        Check(spec, pool, "position-stack.png");
    }

    /// <summary>
    /// Stacked areas over a numeric x (§10.1 scene 21, row G5a): 3 groups at x values of their own, one over a shorter range,
    /// one crossing 0, so that pieces meet on 0 and start and end with vertical edges.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Group "north" at 41 x from 0 to 40, "south" at 27 x offset by a third from 5 to 31, "west" at 21 x offset by two
    ///   thirds that crosses 0; outlines; the fake text backend; Theme.Light.
    ///
    /// Steps:
    /// - Render at 640 × 360; compare with <c>Golden/position-area.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void Area_MatchesBaseline()
    {
        var rows = new List<(double X, int G, double Y)>();
        rows.AddRange(Enumerable.Range(0, 41).Select(i => ((double)i, 0, 2 + Math.Sin(i * 0.2))));
        rows.AddRange(Enumerable.Range(0, 27).Select(i => (5 + i + (1.0 / 3), 1, 1.5 + Math.Cos(i * 0.3))));
        rows.AddRange(Enumerable.Range(0, 21).Select(i => (10 + (1.5 * i) + (2.0 / 3), 2, 1.2 * Math.Sin(i * 0.35))));
        var ordered = rows.OrderBy(r => r.X).ToArray();
        using var pool = new PlotWorkerPool(2);
        using var x = Column.Wrap(ordered.Select(r => r.X).ToArray());
        using var y = Column.Wrap(ordered.Select(r => r.Y).ToArray());
        var table = DataTable
            .Create()
            .Add("x", x)
            .Add("y", y)
            .Add("g", Column.Categorical(ordered.Select(r => (string?)Groups[r.G]).ToArray(), Groups))
            .Build();
        var spec = Plot.Create(table)
            .Area(table.Col<double>("x"), table.Col<double>("y"), configure: a => a.ColorBy(table.Cat("g")).Outline(new StrokeSpec(null)))
            .ScaleX(Scale.Linear().WithTitle("Time"))
            .ScaleY(Scale.Linear().WithTitle("Load"))
            .Title("Stacked areas")
            .Build();
        Check(spec, pool, "position-area.png");
    }

    /// <summary>
    /// Dodged bars and error bars over categories (§10.1 scene 22, row G5a): 5 categories × 3 groups side by side with a
    /// padding of 0.1, the error bars in the same places, a category without one group leaving its place empty.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 5 categories × 3 groups of seeded means and spreads, group "west" without a row in the second category; the fake
    ///   text backend; Theme.Light.
    ///
    /// Steps:
    /// - Render at 640 × 360; compare with <c>Golden/position-dodge.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void Dodge_MatchesBaseline()
    {
        using var pool = new PlotWorkerPool(2);
        using var cells = Cells.Create(5, skip: (1, 2), negatives: []);
        var t = cells.Table;
        var spec = Plot.Create(t)
            .Bar(t.Cat("x"), t.Col<double>("y"), configure: b => b.ColorBy(t.Cat("g")).Position(Position.Dodge(0.1)))
            .ErrorBar(t.Cat("x"), t.Col<double>("low"), t.Col<double>("high"), 4, e => e.ColorBy(t.Cat("g")).Position(Position.Dodge(0.1)))
            .ScaleX(Scale.Band().WithTitle("Site"))
            .ScaleY(Scale.Linear().WithTitle("Mean"))
            .Title("Dodged")
            .Build();
        Check(spec, pool, "position-dodge.png");
    }

    /// <summary>Bars filled to one, flipped (§10.1 scene 23, row G5a): each category's groups as shares of a horizontal bar.</summary>
    /// <remarks>
    /// Preconditions:
    /// - The stacked scene's cells without negative values; a flipped cartesian system; the fake text backend; Theme.Light.
    ///
    /// Steps:
    /// - Render at 640 × 360; compare with <c>Golden/position-fill-flip.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void FillFlip_MatchesBaseline()
    {
        using var pool = new PlotWorkerPool(2);
        using var cells = Cells.Create(6, skip: (3, 1), negatives: []);
        var spec = Plot.Create(cells.Table)
            .Bar(cells.Table.Cat("x"), cells.Table.Col<double>("y"), configure: b => b.ColorBy(cells.Table.Cat("g")).Position(Position.Fill))
            .ScaleX(Scale.Band().WithTitle("City"))
            .ScaleY(Scale.Linear().WithTitle("Share"))
            .Coord(Coord.Cartesian(flip: true))
            .Title("Filled, flipped")
            .Build();
        Check(spec, pool, "position-fill-flip.png");
    }

    /// <summary>Stacked bars in polar (§10.1 scene 24, row G5a): the stacked scene's pieces as stacked sectors of a rose.</summary>
    /// <remarks>
    /// Preconditions:
    /// - The stacked scene's cells without negative values; a polar system with x as the angle; the fake text backend;
    ///   Theme.Light.
    ///
    /// Steps:
    /// - Render at 640 × 360; compare with <c>Golden/position-polar.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void Polar_MatchesBaseline()
    {
        using var pool = new PlotWorkerPool(2);
        using var cells = Cells.Create(6, skip: (3, 1), negatives: []);
        var spec = Plot.Create(cells.Table)
            .Bar(cells.Table.Cat("x"), cells.Table.Col<double>("y"), configure: b => b.ColorBy(cells.Table.Cat("g")))
            .ScaleX(Scale.Band())
            .ScaleY(Scale.Linear())
            .Coord(Coord.Polar())
            .Title("Stacked rose")
            .Build();
        Check(spec, pool, "position-polar.png");
    }

    private static void Check(PlotSpec spec, PlotWorkerPool pool, string baseline)
    {
        using var backend = D3D11Backend.CreateForTest(useWarp: true, createDevice: null, new TextEngine(new FakeTextBackend()));
        using var renderer = new HeadlessRenderer(backend, Width, Height, pool);
        renderer.Render(spec);

        GoldenImage.AssertMatches(renderer, GoldenImageTests.Baseline(baseline), GoldenImageTests.Tolerance, GoldenImageTests.Output);
    }

    /// <summary>
    /// Rows a cell of categories × 3 groups, two rows in some cells, with seeded values and a spread around each: one cell
    /// left out, and some cells negative.
    /// </summary>
    private sealed class Cells : IDisposable
    {
        private readonly Column<double> _y;
        private readonly Column<double> _low;
        private readonly Column<double> _high;

        private Cells(DataTable table, Column<double> y, Column<double> low, Column<double> high) => (Table, _y, _low, _high) = (table, y, low, high);

        public DataTable Table { get; }

        public static Cells Create(int categories, (int Category, int Group) skip, (int Category, int Group)[] negatives)
        {
            var random = new Random(categories);
            var rows = new List<(int X, int G, double Y)>();
            for (var c = 0; c < categories; c++)
            {
                for (var g = 0; g < Groups.Length; g++)
                {
                    if ((c, g) == skip)
                    {
                        continue;
                    }

                    var sign = negatives.Contains((c, g)) ? -1 : 1;
                    rows.Add((c, g, sign * (2 + (random.NextDouble() * 6))));
                    if ((c + g) % 4 == 0)
                    {
                        rows.Add((c, g, sign * (1 + (random.NextDouble() * 2))));
                    }
                }
            }

            string[] names = [.. Enumerable.Range(0, categories).Select(c => $"c{c + 1}")];
            var y = Column.Wrap(rows.Select(r => r.Y).ToArray());
            var low = Column.Wrap(rows.Select(r => r.Y - 0.8 - (0.1 * r.X)).ToArray());
            var high = Column.Wrap(rows.Select(r => r.Y + 0.6 + (0.1 * r.G)).ToArray());
            var table = DataTable
                .Create()
                .Add("x", Column.Categorical(rows.Select(r => (string?)names[r.X]).ToArray(), names))
                .Add("g", Column.Categorical(rows.Select(r => (string?)Groups[r.G]).ToArray(), Groups))
                .Add("y", y)
                .Add("low", low)
                .Add("high", high)
                .Build();
            return new Cells(table, y, low, high);
        }

        public void Dispose()
        {
            _y.Dispose();
            _low.Dispose();
            _high.Dispose();
        }
    }
}
