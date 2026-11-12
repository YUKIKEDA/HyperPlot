using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using HyperPlot.Interaction;
using HyperPlot.Rendering.D3D11;

namespace HyperPlot.E2E;

/// <summary>
/// The scene of the raw points (design §9 "散布の生の点（行 R3a）", requirement P-4): a <see cref="PlotEngine"/> over one
/// point layer at 1,920 × 1,080 on the hardware, zoomed and panned by a script, a frame every 16.7 ms or more. Each frame's
/// CPU time (the view's change and <see cref="PlotEngine.RenderFrame"/>), its GPU time, and its allocations; then the time
/// of a hit test at seeded pixels in both modes. In the first part of row R3a a layer has at most 131,072 rows (the CPU path).
/// </summary>
internal static class PointsScene
{
    private const int Warmup = 30;

    /// <summary>Runs the scene and prints its figures; returns the exit code.</summary>
    /// <param name="args">The count, the diameter in DIPs, the opacity, "uniform" or "cluster", and the frames measured (600 by default).</param>
    public static int Run(ReadOnlySpan<string> args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("points <count> <diameter> <opacity> <uniform|cluster> [frames]");
            return 2;
        }

        var count = int.Parse(args[0], CultureInfo.InvariantCulture);
        var diameter = float.Parse(args[1], CultureInfo.InvariantCulture);
        var opacity = double.Parse(args[2], CultureInfo.InvariantCulture);
        var cluster = args[3] == "cluster";
        var frames = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 600;

        // Seeded: uniform over [0, 100]², or a normal blob of a tenth of the side around the middle.
        var random = new Random(1);
        var (xs, ys) = (new double[count], new double[count]);
        for (var i = 0; i < count; i++)
        {
            if (cluster)
            {
                var r = 10 * Math.Sqrt(-2 * Math.Log(1 - random.NextDouble()));
                var a = 2 * Math.PI * random.NextDouble();
                (xs[i], ys[i]) = (50 + (r * Math.Cos(a)), 50 + (r * Math.Sin(a)));
            }
            else
            {
                (xs[i], ys[i]) = (random.NextDouble() * 100, random.NextDouble() * 100);
            }
        }

        using var x = Column.Wrap(xs);
        using var y = Column.Wrap(ys);
        var table = DataTable.Create().Add("x", x).Add("y", y).Build();
        var plot = Plot.Create(table)
            .Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Size(diameter).Opacity(opacity))
            .ScaleX(Scale.Linear().WithDomain(0, 100))
            .ScaleY(Scale.Linear().WithDomain(0, 100))
            .Build();
        using var backend = D3D11Backend.Create();
        using var engine = new PlotEngine(backend, 1920, 1080);
        engine.SetPlot(plot);
        engine.RenderFrame();
        while (engine.IsProvisional)
        {
            Thread.Sleep(5);
            engine.RenderFrame();
        }

        // The script: zoom in to a tenth of the side around a point that drifts, and out again, 300 frames a round. The
        // views are made before the frames: a view is an object of the scene's own.
        var panel = new PanelId(0, 0);
        var total = frames + Warmup;
        var views = new PanelView2D[total];
        for (var f = 0; f < total; f++)
        {
            var t = (f % 300) / 300.0;
            var span = 100 * Math.Pow(10, -Math.Sin(t * Math.PI));
            var (cx, cy) = (50 + (20 * Math.Sin(t * 2 * Math.PI)), 50 + (10 * Math.Cos(t * 2 * Math.PI)));
            views[f] = new PanelView2D(
                ViewRange.FromValues(cx - (span / 2), cx + (span / 2)),
                ViewRange.FromValues(cy - (span / 2), cy + (span / 2))
            );
        }

        var cpu = new double[total];
        var allocated = new long[total];
        var gpu = new List<double>(frames);
        long lastTimed = -1;
        var period = TimeSpan.FromMilliseconds(1000.0 / 60);
        var began = Stopwatch.GetTimestamp();
        for (var f = 0; f < total; f++)
        {
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            engine.Views.SetView(panel, views[f]);
            engine.RenderFrame();
            cpu[f] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            allocated[f] = GC.GetAllocatedBytesForCurrentThread() - bytes;
            if (f >= Warmup && backend.LastFrameTime is { } time && time.Frame > lastTimed)
            {
                lastTimed = time.Frame;
                gpu.Add(time.Duration.TotalMilliseconds);
            }

            // A frame a period at the fastest: back to back, the GPU's clock rises and its time reads low (.cursor/rules/bench.mdc).
            var left = period - Stopwatch.GetElapsedTime(start);
            if (left > TimeSpan.Zero)
            {
                Thread.Sleep(left);
            }
        }

        var seconds = Stopwatch.GetElapsedTime(began).TotalSeconds;

        // Hit tests at seeded pixels of the whole view, each timed alone.
        engine.Views.SetView(panel, new PanelView2D(ViewRange.FromValues(0, 100), ViewRange.FromValues(0, 100)));
        engine.RenderFrame();
        var hits = new SeriesHit[4];
        var cursors = new Vector2[1000];
        for (var i = 0; i < cursors.Length; i++)
        {
            var (px, py) = (5 + (random.NextDouble() * 90), 5 + (random.NextDouble() * 90));
            cursors[i] = engine.ViewToPixel(panel, ViewRange.FromValues(px, px), ViewRange.FromValues(py, py));
        }

        var radius = 8 * engine.Scale;
        var (inX, inXY) = (new double[cursors.Length], new double[cursors.Length]);
        var found = 0;
        for (var round = 0; round < 2; round++)
        {
            for (var i = 0; i < cursors.Length; i++)
            {
                var start = Stopwatch.GetTimestamp();
                found += engine.HitTest(cursors[i], radius, HitMode.X, hits);
                var middle = Stopwatch.GetTimestamp();
                found += engine.HitTest(cursors[i], radius, HitMode.XY, hits);
                inX[i] = Stopwatch.GetElapsedTime(start, middle).TotalMilliseconds;
                inXY[i] = Stopwatch.GetElapsedTime(middle).TotalMilliseconds;
            }
        }

        var kept = cpu.Skip(Warmup).ToArray();
        var gpuKept = gpu.ToArray();
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"points={count} diameter={diameter} opacity={opacity} dist={args[3]} frames={frames} (after {Warmup}) frame_period_ms={seconds * 1000 / total:F2}"
            )
        );
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"cpu_frame: p50={Percentile(kept, 0.5):F3} p95={Percentile(kept, 0.95):F3} p99={Percentile(kept, 0.99):F3} max={kept.Max():F3} ms"
            )
        );
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"gpu_frame: frames={gpuKept.Length} p50={Percentile(gpuKept, 0.5):F3} p95={Percentile(gpuKept, 0.95):F3} p99={Percentile(gpuKept, 0.99):F3} max={gpuKept.Max():F3} ms"
            )
        );
        Console.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"allocated after warm-up: {allocated.Skip(Warmup).Sum()} bytes over {frames} frames")
        );
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"hit_x: p50={Percentile(inX, 0.5):F4} p99={Percentile(inX, 0.99):F4} max={inX.Max():F4} ms; hit_xy: p50={Percentile(inXY, 0.5):F4} p99={Percentile(inXY, 0.99):F4} max={inXY.Max():F4} ms; found={found}"
            )
        );
        return 0;
    }

    private static double Percentile(double[] values, double share)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? double.NaN : sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * share))];
    }
}
