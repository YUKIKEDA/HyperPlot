using System.Numerics;
using System.Runtime.InteropServices;
using HyperPlot.Testing;

namespace HyperPlot.Rendering.D3D11.Tests;

/// <summary>Line plots drawn from a spec with <c>HeadlessRenderer.Render(PlotSpec)</c> on WARP (row G1, design §4.8, §7.5).</summary>
public sealed class PlotRenderTests
{
    private const int Width = 640;
    private const int Height = 360;
    private const int Points = 100_000;

    // The theme without guides of row G3 (design §4.8): the panel is the whole target, as rows G1 and G2 drew.
    private static readonly Theme Bare = Theme.Light with { ShowAxes = false, ShowGrid = false, Padding = 0 };

    private static readonly Color White = new(255, 255, 255, 255);

    /// <summary>
    /// A plot of two layers draws the pixels of M4Batch and RenderList put together by hand with the same boundaries and
    /// mapping, and matches its baseline (design §4.8, §10.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend; a pool of its own; 1e5 points: an evenly spaced X with float Y holding a gap of NaN, and a stored X
    ///   that starts lower and ends higher with double Y drawn dashed.
    ///
    /// Steps:
    /// - Clear to white and render the spec at 640 × 360; on a second renderer, draw the pyramids, M4 batch, and polylines
    ///   made by hand over the domains computed from the raw arrays; compare with <c>Golden/plot-lines.png</c>.
    ///
    /// Expected:
    /// - The two images are equal, byte for byte, and the first matches the baseline.
    /// </remarks>
    [Fact]
    public void Render_TwoLayers_EqualsHandBuiltPixelsAndBaseline()
    {
        var random = new Random(11);
        var a = new float[Points];
        var b = new double[Points];
        var xs = new double[Points];
        var step = 0.0;
        for (var i = 0; i < Points; i++)
        {
            a[i] = (float)(Math.Sin(i * 2e-4) + (random.NextDouble() * 0.3));
            b[i] = (Math.Cos(i * 7e-5) * 1.5) + (random.NextDouble() * 0.2);
            xs[i] = -3 + (step += 1 + (random.NextDouble() * 0.05));
        }

        a.AsSpan(40_000, 2_000).Fill(float.NaN);
        using var pool = new PlotWorkerPool(2);
        using var aColumn = Column.Wrap(a);
        using var bColumn = Column.Wrap(b);
        using var xsColumn = Column.Wrap(xs);
        var t = Column.Uniform(0, 1, Points);
        var table = DataTable.Create().Add("t", t).Add("a", aColumn).Add("xs", xsColumn).Add("b", bColumn).Build();
        var solid = new LineStyle(new Color(31, 119, 180, 255), 1.5f);
        var dashed = new LineStyle(new Color(214, 39, 40, 255), 1, new LineDash(6, 3));
        var spec = Plot.Create(table)
            .Line(table.Col<double>("t"), table.Col<float>("a"), l => l.Color(solid.Color).Width(solid.Width))
            .Line(table.Col<double>("xs"), table.Col<double>("b"), l => l.Color(dashed.Color).Width(dashed.Width).Dash(dashed.Dash))
            .Theme(Bare)
            .Build();

        using var backend = D3D11Backend.Create(useWarp: true);
        using var renderer = new HeadlessRenderer(backend, Width, Height, pool);
        renderer.Clear(White);
        renderer.Render(spec);
        var drawn = Read(renderer);

        var x = (xs[0], xs[^1]);
        var finite = a.Where(v => !float.IsNaN(v)).Select(v => (double)v).Concat(b).ToArray();
        var y = (finite.Min(), finite.Max());
        using var byHand = new HeadlessRenderer(backend, Width, Height);
        byHand.Clear(White);
        byHand.Render(ByHand(pool, x, y, [(t, aColumn, solid), (xsColumn, bColumn, dashed)]));

        Assert.True(drawn.AsSpan().SequenceEqual(Read(byHand)), "The plot's pixels differ from those put together by hand.");
        Assert.Contains(drawn.Chunk(4), p => p[0] != 255);
        GoldenImage.AssertMatches(renderer, GoldenImageTests.Baseline("plot-lines.png"), GoldenImageTests.Tolerance, GoldenImageTests.Output);
    }

    /// <summary>A line plot with a time x and a log y matches its baseline (row G2, design §4.2, §10.1).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend; a pool of its own; 1e5 points: a Column&lt;long&gt; X of 1970-based nanoseconds of 2026, 1 µs apart,
    ///   and float Y from 0.1 to about 1,000, with a gap of NaN.
    ///
    /// Steps:
    /// - Clear to white and render the spec with Scale.Time(Nanoseconds) for x and Scale.Log() for y at 640 × 360; compare
    ///   with <c>Golden/plot-scales.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void Render_TimeAndLog_MatchesBaseline()
    {
        var random = new Random(13);
        var x = new long[Points];
        var y = new float[Points];
        for (var i = 0; i < Points; i++)
        {
            x[i] = 1_793_232_000_000_000_000L + (1_000L * i);
            y[i] = (float)Math.Pow(10, (Math.Sin(i * 1e-4) * 2) + 1 + (random.NextDouble() * 0.2));
        }

        y.AsSpan(60_000, 3_000).Fill(float.NaN);
        using var pool = new PlotWorkerPool(2);
        using var xColumn = Column.Wrap(x);
        using var yColumn = Column.Wrap(y);
        var table = DataTable.Create().Add("t", xColumn).Add("v", yColumn).Build();
        var spec = Plot.Create(table)
            .Line(table.Col<long>("t"), table.Col<float>("v"), l => l.Color(new Color(31, 119, 180, 255)).Width(1.5f))
            .ScaleX(Scale.Time(TimeUnit.Nanoseconds))
            .ScaleY(Scale.Log())
            .Theme(Bare)
            .Build();

        using var backend = D3D11Backend.Create(useWarp: true);
        using var renderer = new HeadlessRenderer(backend, Width, Height, pool);
        renderer.Clear(White);
        renderer.Render(spec);

        GoldenImage.AssertMatches(renderer, GoldenImageTests.Baseline("plot-scales.png"), GoldenImageTests.Tolerance, GoldenImageTests.Output);
    }

    /// <summary>
    /// The example of row G1 in design §4.9 runs as written: a mapped file and an evenly spaced X, drawn, then drawn again
    /// with a thicker stroke.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A file of 1e5 floats of a sine in [-1, 1]; a WARP backend; the shared pool, as the example uses.
    ///
    /// Steps:
    /// - Run the example's statements at 640 × 360, reading the pixels after each Render.
    ///
    /// Expected:
    /// - The first image has drawn pixels; the second, with a stroke of width 2 in black, differs from the first.
    /// </remarks>
    [Fact]
    public void DesignExample_RowG1_Runs()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hyperplot-g1-{Guid.NewGuid():N}.f32");
        File.WriteAllBytes(path, MemoryMarshal.AsBytes(Enumerable.Range(0, Points).Select(i => MathF.Sin(i * 1e-4f)).ToArray()).ToArray());
        try
        {
            byte[] first;
            byte[] second;
            using (var ch1 = Column.MapFile<float>(path))
            {
                var table = DataTable.Create().Add("t", Column.Uniform(start: 0.0, step: 1e-6, length: ch1.Length)).Add("ch1", ch1).Build();
                var plot = Plot.Create(table)
                    .Line(x: table.Col<double>("t"), y: table.Col<float>("ch1"), l => l.Color(new Color(31, 119, 180, 255)).Width(1))
                    .ScaleY(Scale.Linear().WithDomain(-1, 1))
                    .Theme(Bare)
                    .Build();

                using var backend = D3D11Backend.Create(useWarp: true);
                using var renderer = new HeadlessRenderer(backend, Width, Height);
                renderer.Clear(White);
                renderer.Render(plot);
                first = Read(renderer);
                var thicker = plot with { Layers = [((LineLayerSpec)plot.Layers[0]) with { Style = new StrokeSpec(new Color(0, 0, 0, 255), 2) }] };
                renderer.Clear(White);
                renderer.Render(thicker);
                second = Read(renderer);
            }

            Assert.Contains(first.Chunk(4), p => p[0] != 255);
            Assert.False(first.AsSpan().SequenceEqual(second));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void Ends(Task task)
    {
        Assert.True(((IAsyncResult)task).AsyncWaitHandle.WaitOne(30_000), "The task did not end within the watchdog.");
        Assert.True(task.IsCompletedSuccessfully, $"The task ended {task.Status}.");
    }

    private static byte[] Read(HeadlessRenderer renderer)
    {
        var pixels = new byte[renderer.Width * renderer.Height * 4];
        renderer.ReadPixels(pixels);
        return pixels;
    }

    /// <summary>Puts the pyramids, the M4 batch, and the polylines together by hand, as design §4.8 says the plan does.</summary>
    private static RenderList ByHand(
        PlotWorkerPool pool,
        (double Min, double Max) x,
        (double Min, double Max) y,
        (Column X, Column Y, LineStyle Style)[] layers
    )
    {
        var batch = new M4Batch();
        var outputs = new M4Point[layers.Length][];
        var pyramids = new List<IDisposable>();
        try
        {
            for (var s = 0; s < layers.Length; s++)
            {
                outputs[s] = new M4Point[M4.MaxPointCount(Width)];
                _ = layers[s].Y switch
                {
                    Column<float> f => Add(pool.BuildPyramid(f), layers[s].X, outputs[s]),
                    Column<double> d => Add(pool.BuildPyramid(d), layers[s].X, outputs[s]),
                    _ => throw new ArgumentException("A Y of float or double.", nameof(layers)),
                };
            }

            // The plan's origins are the floors of the lower ends, and its domains the differences from them (design §3.6).
            var origin = new ScaleOrigin((long)Math.Floor(x.Min), (long)Math.Floor(y.Min));
            var (xLower, xUpper) = (x.Min - origin.X, x.Max - origin.X);
            var (yLower, yUpper) = (y.Min - origin.Y, y.Max - origin.Y);
            var boundaries = new double[Width + 1];
            for (var k = 0; k <= Width; k++)
            {
                boundaries[k] = xLower + ((xUpper - xLower) * k / Width);
            }

            pool.Extract(batch, origin, boundaries);
            var list = new RenderList();
            for (var s = 0; s < layers.Length; s++)
            {
                var points = new List<Vector2>();
                foreach (var point in outputs[s].AsSpan(0, batch.GetResult(s).Count))
                {
                    if (point.BreakBefore)
                    {
                        points.Add(new Vector2(float.NaN));
                    }

                    points.Add(
                        new Vector2(
                            (float)((point.X - xLower) / (xUpper - xLower) * Width),
                            (float)(Height - ((point.Y - yLower) / (yUpper - yLower) * Height))
                        )
                    );
                }

                list.DrawPolyline(points.ToArray(), layers[s].Style);
            }

            return list;
        }
        finally
        {
            foreach (var pyramid in pyramids)
            {
                pyramid.Dispose();
            }
        }

        int Add<T>(MinMaxPyramid<T> pyramid, Column column, M4Point[] output)
            where T : unmanaged, INumber<T>
        {
            pyramids.Add(pyramid);
            Ends(pyramid.Completion);
            if (column is Column<double> stored)
            {
                Ends(pool.PrecomputeStats(stored));
                return batch.Add(stored, pyramid, output);
            }

            return batch.Add((UniformColumn)column, pyramid, output);
        }
    }
}
