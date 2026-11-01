using HyperPlot.Testing;
using HyperPlot.Tests;

namespace HyperPlot.Rendering.D3D11.Tests;

/// <summary>Row G6b's facets drawn on WARP (design §10.1 scenes 36 and 37), with the fake text backend.</summary>
public sealed class FacetRenderTests
{
    private const int Width = 640;
    private const int Height = 360;

    /// <summary>
    /// A wrap (§10.1 scene 36, row G6b): five regions' lines, each split into two channels by color, on the shared axes of
    /// R's <c>n2mfrow</c> grid of 2 rows and 3 columns; the panel above the last row's missing one draws its own x axis.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Five regions' runs of 60 times, each time a row of the two channels in turn (mixed in the run); a seeded wave a
    ///   region and channel; the fake text backend; Theme.Light; axis titles, a title, and the channels' legend.
    ///
    /// Steps:
    /// - Render at 640 × 360; compare with <c>Golden/facet-wrap.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void Wrap_MatchesBaseline()
    {
        string[] regions = ["North", "South", "East", "West", "Center"];
        string[] channels = ["Online", "Store"];
        var random = new Random(36);
        var count = regions.Length * 60 * channels.Length;
        var (time, sales) = (new double[count], new float[count]);
        var (region, channel) = (new string?[count], new string?[count]);
        var row = 0;
        for (var r = 0; r < regions.Length; r++)
        {
            for (var t = 0; t < 60; t++)
            {
                for (var c = 0; c < channels.Length; c++)
                {
                    (time[row], region[row], channel[row]) = (t, regions[r], channels[c]);
                    sales[row] = (float)(10 + (r * 2) + (c * 4) + (3 * Math.Sin((t / (6.0 + r)) + c)) + random.NextDouble());
                    row++;
                }
            }
        }

        using var pool = new PlotWorkerPool(2);
        using var timeColumn = Column.Wrap(time);
        using var salesColumn = Column.Wrap(sales);
        var table = DataTable
            .Create()
            .Add("time", timeColumn)
            .Add("sales", salesColumn)
            .Add("region", Column.Categorical(region, regions))
            .Add("channel", Column.Categorical(channel, channels))
            .Build();
        var spec = Plot.Create(table)
            .Line(table.Col<double>("time"), table.Col<float>("sales"), l => l.ColorBy(table.Cat("channel")).Width(1.5f))
            .FacetWrap(table.Cat("region"))
            .ScaleColor(Scale.Ordinal().WithTitle("Channel"))
            .ScaleX(Scale.Linear().WithTitle("Week"))
            .ScaleY(Scale.Linear().WithTitle("Sales"))
            .Title("Sales by region")
            .Build();
        Check(spec, pool, "facet-wrap.png");
    }

    /// <summary>
    /// A grid (§10.1 scene 37, row G6b): stacked histograms of prices for two years by three shops, each row learning its
    /// own y; the row strips turned on the right, and one empty cell.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 2,000 seeded rows of a price, a year, a shop, and a kind, mixed (no run), with no row of 2024 at shop "C"; the
    ///   fake text backend; Theme.Light; axis titles, a title, and the kinds' legend.
    ///
    /// Steps:
    /// - Render at 640 × 360; compare with <c>Golden/facet-grid.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void Grid_MatchesBaseline()
    {
        string[] years = ["2023", "2024"];
        string[] shops = ["A", "B", "C"];
        string[] kinds = ["New", "Used"];
        var random = new Random(37);
        var prices = new List<float>();
        var (year, shop, kind) = (new List<string?>(), new List<string?>(), new List<string?>());
        while (prices.Count < 2000)
        {
            var (y, s, k) = (random.Next(2), random.Next(3), random.Next(2));
            if (y == 1 && s == 2)
            {
                continue;
            }

            // A sum of uniforms, near a normal, its center by the shop and the kind and its spread by the year.
            var noise = random.NextDouble() + random.NextDouble() + random.NextDouble() - 1.5;
            prices.Add((float)(50 + (s * 15) + (k * 10) + (noise * (12 + (y * 10)))));
            (year, shop, kind) = (Add(year, years[y]), Add(shop, shops[s]), Add(kind, kinds[k]));
        }

        using var pool = new PlotWorkerPool(2);
        using var price = Column.Wrap(prices.ToArray());
        var table = DataTable
            .Create()
            .Add("price", price)
            .Add("year", Column.Categorical(year.ToArray(), years))
            .Add("shop", Column.Categorical(shop.ToArray(), shops))
            .Add("kind", Column.Categorical(kind.ToArray(), kinds))
            .Build();
        var spec = Plot.Create(table)
            .Histogram(table.Col<float>("price"), configure: h => h.ColorBy(table.Cat("kind")))
            .FacetGrid(rows: table.Cat("year"), columns: table.Cat("shop"), scales: FacetScales.FreeY)
            .ScaleColor(Scale.Ordinal().WithTitle("Kind"))
            .ScaleX(Scale.Linear().WithTitle("Price"))
            .ScaleY(Scale.Linear().WithTitle("Count"))
            .Title("Prices by year and shop")
            .Build();
        Check(spec, pool, "facet-grid.png");

        static List<string?> Add(List<string?> list, string value)
        {
            list.Add(value);
            return list;
        }
    }

    private static void Check(PlotSpec spec, PlotWorkerPool pool, string baseline)
    {
        using var backend = D3D11Backend.CreateForTest(useWarp: true, createDevice: null, new TextEngine(new FakeTextBackend()));
        using var renderer = new HeadlessRenderer(backend, Width, Height, pool);
        renderer.Render(spec);

        GoldenImage.AssertMatches(renderer, GoldenImageTests.Baseline(baseline), GoldenImageTests.Tolerance, GoldenImageTests.Output);
    }
}
