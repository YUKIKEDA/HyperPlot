using System.Diagnostics;
using System.Globalization;
using HyperPlot.Interaction;
using HyperPlot.Rendering.D3D11;
using HyperPlot.Samples;

namespace HyperPlot.E2E;

/// <summary>
/// The hover's scene (design §9 "ホバー（行 I2b）", requirement P-6): a <see cref="PlotEngine"/> over 1e9-point series at
/// 1,920 × 1,080 on the hardware, the whole view, the pointer moved by a script of 300 frames with the default hover. Each
/// frame's CPU time (the move and <see cref="PlotEngine.RenderFrame"/>), its GPU time, and its allocations. With
/// <c>--hover-whole</c>, the same frames with no move: every frame drawn whole, as main draws each.
/// </summary>
internal static class HoverScene
{
    private const int Warmup = 30;

    /// <summary>Runs the scene and prints its figures; returns the exit code.</summary>
    /// <param name="frames">The frames measured, after 30 to warm: 300 for design §9; more fill the shaping cache (8,192 texts).</param>
    public static int Run(string directory, long points, int seriesCount, int fileCount, bool whole, string? csv, int frames)
    {
        var files = Math.Min(fileCount, seriesCount);
        var columns = new Column<float>[seriesCount];
        var builder = DataTable.Create().Add("x", Column.Uniform(0, 1, points));
        for (var s = 0; s < seriesCount; s++)
        {
            _ = WalkFile.Ensure(directory, s % files, points, out var path);
            columns[s] = Column.MapFile<float>(path);
            builder = builder.Add($"y{s}", columns[s]);
        }

        var table = builder.Build();
        var plot = Plot.Create(table);
        for (var s = 0; s < seriesCount; s++)
        {
            plot = plot.Line(table.Col<double>("x"), table.Col<float>($"y{s}"));
        }

        using var backend = D3D11Backend.Create();
        using var engine = new PlotEngine(backend, 1920, 1080);
        engine.SetPlot(plot.Build());

        // The indexes are built off the frame: frames until one is not provisional.
        var built = Stopwatch.StartNew();
        engine.RenderFrame();
        while (engine.IsProvisional)
        {
            Thread.Sleep(5);
            engine.RenderFrame();
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"built and drawn in {built.Elapsed.TotalSeconds:F1} s"));
        var guide = Panel(engine);
        var cpu = new double[frames + Warmup];
        var allocated = new long[frames + Warmup];
        var gpu = new List<double>(frames);
        long lastTimed = -1;
        for (var f = 0; f < cpu.Length; f++)
        {
            // The script: across the panel from left to right, up and down along a sine, as a hand sweeps over the data.
            var t = ((f % 330) + 0.5) / 330;
            var at = new System.Numerics.Vector2(
                (float)(guide.Left + (guide.Width * (0.05 + (0.9 * t)))),
                (float)(guide.Top + (guide.Height * (0.5 + (0.35 * Math.Sin(t * 6 * Math.PI)))))
            );
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            if (!whole)
            {
                _ = engine.OnPointer(
                    new PointerEvent(
                        PointerEventKind.Move,
                        at,
                        PointerButtons.None,
                        PointerButtons.None,
                        ModifierKeys.None,
                        PointerKind.Mouse,
                        engine.Time.GetTimestamp()
                    )
                );
            }

            engine.RenderFrame();
            cpu[f] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            allocated[f] = GC.GetAllocatedBytesForCurrentThread() - bytes;
            if (f >= Warmup && backend.LastFrameTime is { } time && time.Frame > lastTimed)
            {
                lastTimed = time.Frame;
                gpu.Add(time.Duration.TotalMilliseconds);
            }
        }

        if (csv is not null)
        {
            using var writer = new StreamWriter(csv);
            writer.WriteLine("frame,cpu_ms,allocated");
            for (var f = 0; f < cpu.Length; f++)
            {
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{f},{cpu[f]:F4},{allocated[f]}"));
            }
        }

        var kept = cpu.Skip(Warmup).ToArray();
        var gpuKept = gpu.ToArray();
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"hover={(whole ? "whole" : "moves")} series={seriesCount} points={points} frames={frames} (after {Warmup})"
            )
        );
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"cpu_frame: p50={Percentile(kept, 0.5):F3} p95={Percentile(kept, 0.95):F3} max={kept.Max():F3} ms"
            )
        );
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"gpu_frame: frames={gpuKept.Length} p50={Percentile(gpuKept, 0.5):F3} p95={Percentile(gpuKept, 0.95):F3} max={gpuKept.Max():F3} ms"
            )
        );
        Console.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"allocated after warm-up: {allocated.Skip(Warmup).Sum()} bytes over {frames} frames")
        );
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"allocated in the last 300 frames: {allocated.TakeLast(300).Sum()} bytes"));
        foreach (var column in columns)
        {
            column.Dispose();
        }

        return 0;
    }

    /// <summary>Returns the panel's rectangle: the hit area of the target's middle row, searched from the left.</summary>
    private static (int Left, int Top, int Width, int Height) Panel(PlotEngine engine)
    {
        var (left, right, top, bottom) = (-1, -1, -1, -1);
        for (var x = 0; x < engine.Width; x++)
        {
            if (engine.HitArea(new System.Numerics.Vector2(x, engine.Height / 2)).Kind == HitAreaKind.Panel)
            {
                left = left < 0 ? x : left;
                right = x;
            }
        }

        for (var y = 0; y < engine.Height; y++)
        {
            if (engine.HitArea(new System.Numerics.Vector2(engine.Width / 2, y)).Kind == HitAreaKind.Panel)
            {
                top = top < 0 ? y : top;
                bottom = y;
            }
        }

        return (left, top, right - left + 1, bottom - top + 1);
    }

    private static double Percentile(double[] values, double share)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? double.NaN : sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * share))];
    }
}
