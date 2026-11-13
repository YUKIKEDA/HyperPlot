using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using HyperPlot.Interaction;
using HyperPlot.Rendering.D3D11;

namespace HyperPlot.E2E;

/// <summary>
/// The scene of the marker annotations (design §9 "大きさと形のチャネル（行 R3f）", "注釈"): a <see cref="PlotEngine"/> over a
/// line of 1,000 points and 1,024 marker annotations at 1,920 × 1,080 on the hardware, zoomed and panned by a script, a
/// frame every 16.7 ms or more. Each frame's CPU time (the view's change and <see cref="PlotEngine.RenderFrame"/>), its
/// GPU time, and its allocations.
/// </summary>
internal static partial class AnnotationsScene
{
    private const int Warmup = 30;
    private const int Count = 1024;

    /// <summary>Runs the scene and prints its figures; returns the exit code.</summary>
    /// <param name="args">The number of shapes the markers take in turn, from the first of <see cref="MarkerShape"/>, and the frames measured (600 by default).</param>
    public static int Run(ReadOnlySpan<string> args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("annotations <shapes> [frames]");
            return 2;
        }

        var shapes = int.Parse(args[0], CultureInfo.InvariantCulture);
        var frames = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 600;
        var random = new Random(7);
        using var x = Column.Wrap(Enumerable.Range(0, 1000).Select(i => i / 10.0).ToArray());
        using var y = Column.Wrap(Enumerable.Range(0, 1000).Select(i => 50 + (Math.Sin(i / 40.0) * 30)).ToArray());
        var table = DataTable.Create().Add("x", x).Add("y", y).Build();
        var plot = Plot.Create(table)
            .Line(table.Col<double>("x"), table.Col<double>("y"))
            .ScaleX(Scale.Linear().WithDomain(0, 100))
            .ScaleY(Scale.Linear().WithDomain(0, 100));
        for (var i = 0; i < Count; i++)
        {
            var at = new AnnotationPoint(AnnotationValue.Data(random.NextDouble() * 100), AnnotationValue.Data(random.NextDouble() * 100));
            plot = plot.Annotate(Annotation.Marker(at) with { Shape = (MarkerShape)(i % shapes), Radius = 3 + (i % 4) });
        }

        using var backend = D3D11Backend.Create();
        using var engine = new PlotEngine(backend, 1920, 1080);
        engine.SetPlot(plot.Build());
        engine.RenderFrame();

        // The script of the points' scene: zoom in to a tenth of the side around a point that drifts, and out again.
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

        // A wait of a few milliseconds needs the timer's finer step (.cursor/rules/bench.mdc).
        _ = TimeBeginPeriod(1);
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

            // A frame a period at the fastest: back to back, the GPU's clock rises and its time reads low.
            var left = period - Stopwatch.GetElapsedTime(start);
            if (left > TimeSpan.Zero)
            {
                Thread.Sleep(left);
            }
        }

        _ = TimeEndPeriod(1);
        var kept = cpu.Skip(Warmup).ToArray();
        var gpuKept = gpu.ToArray();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"annotations={Count} shapes={shapes} frames={frames} (after {Warmup})"));
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

        // The script repeats every 300 frames: the last 300 show views that were drawn before, with no new tick label.
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"allocated after warm-up: {allocated.Skip(Warmup).Sum()} bytes over {frames} frames; in the last 300 frames: {allocated.TakeLast(300).Sum()} bytes"
            )
        );
        return 0;
    }

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint period);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static partial uint TimeEndPeriod(uint period);

    private static double Percentile(double[] values, double share)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? double.NaN : sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * share))];
    }
}
