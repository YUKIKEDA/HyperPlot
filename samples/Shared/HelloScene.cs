using HyperPlot.Rendering;

namespace HyperPlot.Samples;

/// <summary>
/// The scene of <c>HelloPlot</c>: 4 series in memory, each a sine of its own frequency with seeded noise and one gap of NaN,
/// their pyramids built and M4 run across the pool, recorded with a grid at 1,280 × 720. <c>HelloPlot</c> draws it with 1e6
/// points and the golden image test with 1e5 (design §10.1); the gaps and the frequencies are shares of the point count, so
/// both draw the same picture.
/// </summary>
internal static class HelloScene
{
    /// <summary>The width of the image, in pixels.</summary>
    public const int Width = 1280;

    /// <summary>The height of the image, in pixels.</summary>
    public const int Height = 720;

    private const int SeriesCount = 4;
    private const double Million = 1_000_000;

    /// <summary>Builds the series of <paramref name="points"/> points each and records the scene.</summary>
    /// <param name="points">The points of each series.</param>
    /// <param name="pool">The pool that builds the pyramids and runs M4.</param>
    /// <returns>The recorded scene.</returns>
    public static RenderList Record(int points, PlotWorkerPool pool)
    {
        var columns = new Column<float>[SeriesCount];
        var pyramids = new MinMaxPyramid<float>[SeriesCount];
        try
        {
            // At 1e6 points the frequency is 2e-5 a point and the gap of series s starts at 400,000 + 50,000 s and is 20,000
            // long, as HelloPlot drew it before the scene was shared.
            var frequency = 2e-5 * (Million / points);
            for (var s = 0; s < SeriesCount; s++)
            {
                var random = new Random(s);
                var values = new float[points];
                for (var i = 0; i < points; i++)
                {
                    values[i] = (float)(Math.Sin(i * (s + 1) * frequency) + (s * 2.5) + ((random.NextDouble() - 0.5) * 0.6));
                }

                var gapStart = (int)((long)points * (40 + (5 * s)) / 100);
                values.AsSpan(gapStart, (int)((long)points * 2 / 100)).Fill(float.NaN);
                columns[s] = Column.Wrap(values);
                pyramids[s] = pool.BuildPyramid(columns[s]);
            }

            foreach (var pyramid in pyramids)
            {
                pyramid.Completion.Wait();
            }

            var area = new PlotArea(left: 40, top: 30, width: 1200, height: 660);
            var x = Column.Uniform(0, 1, points);
            var batch = new M4Batch();
            var outputs = new M4Point[SeriesCount][];
            for (var s = 0; s < SeriesCount; s++)
            {
                outputs[s] = new M4Point[M4.MaxPointCount(area.Width)];
                _ = batch.Add(x, pyramids[s], outputs[s]);
            }

            pool.Extract(batch, default, area.Boundaries(0, points - 1));

            // The Y range of every series, from the pyramids (design §3.3).
            var low = double.PositiveInfinity;
            var high = double.NegativeInfinity;
            foreach (var pyramid in pyramids)
            {
                var range = pyramid.GetMinMax(0, points);
                low = Math.Min(low, range.Min);
                high = Math.Max(high, range.Max);
            }

            Color[] colors = [new(31, 119, 180, 255), new(255, 127, 14, 255), new(44, 160, 44, 255), new(214, 39, 40, 255)];
            var list = new RenderList();
            area.DrawGrid(list, step: 60);
            for (var s = 0; s < SeriesCount; s++)
            {
                var drawn = outputs[s].AsSpan(0, batch.GetResult(s).Count);
                area.DrawSeries(list, drawn, 0, points - 1, low, high, new LineStyle(colors[s], 1.5f));
            }

            return list;
        }
        finally
        {
            foreach (var pyramid in pyramids)
            {
                pyramid?.Dispose();
            }

            foreach (var column in columns)
            {
                column?.Dispose();
            }
        }
    }
}
