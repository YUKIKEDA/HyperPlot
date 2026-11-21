using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using HyperPlot.Interaction;
using HyperPlot.Rendering;
using HyperPlot.Rendering.D3D11;

namespace HyperPlot.E2E;

/// <summary>
/// The scene of the raw points (design §9 "散布の生の点（行 R3a）", requirement P-4): a <see cref="PlotEngine"/> over one
/// point layer at 1,920 × 1,080 on the hardware, zoomed and panned by a script, a frame every 16.7 ms or more. Each frame's
/// CPU time (the view's change and <see cref="PlotEngine.RenderFrame"/>), its GPU time, and its allocations; then the time
/// of a hit test at seeded pixels in both modes. A layer of at most 131,072 rows takes the CPU path, and a larger one the index path.
/// Row R3b2 (design §9 "重なりと予算"): the runs and the points of the index path a frame, the blocks in view against whole
/// pages, the frames without their drawing, and the cores the other processes used.
/// </summary>
internal static partial class PointsScene
{
    // Two seconds of frames before the measured ones: a GPU scene is drawn that long before it is timed (.cursor/rules/bench.mdc).
    private const int Warmup = 120;

    /// <summary>Runs the scene and prints its figures; returns the exit code.</summary>
    /// <param name="args">The count, the diameter in DIPs, the opacity, "uniform" or "cluster", the frames measured (600 by default), the categories (0 by default), "whole" or "zoom", what the categories set ("color", "shape", "both"; "size" and "shapesize" size the points by a number too, row R3f), the layer's shape, "blocks" (the blocks in view, the product's form) or "pages" (every block of each page drawn, row R3b2), and "draw" or "record" (the measured frames record and do not draw, so that their CPU time is the recording's), and a file to write one row a frame to (its times and allocations).</param>
    public static int Run(ReadOnlySpan<string> args)
    {
        // Row R3b2: a file of one row a frame, when named after the other arguments.
        var csv = args.Length > 11 ? args[11] : null;
        if (args.Length < 4)
        {
            Console.Error.WriteLine(
                "points <count> <diameter> <opacity> <uniform|cluster> [frames] [categories] [whole|zoom] [color|shape|both|size|shapesize] [shape] [blocks|pages] [draw|record] [file of one row a frame]"
            );
            return 2;
        }

        var count = int.Parse(args[0], CultureInfo.InvariantCulture);
        var diameter = float.Parse(args[1], CultureInfo.InvariantCulture);
        var opacity = double.Parse(args[2], CultureInfo.InvariantCulture);
        var cluster = args[3] == "cluster";
        var frames = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 600;
        var categories = args.Length > 5 ? int.Parse(args[5], CultureInfo.InvariantCulture) : 0;
        var whole = args.Length > 6 && args[6] == "whole";

        // Row R3f: what the categories set (the color, the shape, or both), whether a number sizes the points, and the shape.
        var channel = args.Length > 7 ? args[7] : "color";
        var shape = args.Length > 8 ? Enum.Parse<MarkerShape>(args[8]) : MarkerShape.Circle;

        // Row R3b2: whole pages instead of the blocks in view, and frames that record without drawing.
        var pages = args.Length > 9 && args[9] == "pages";
        var recordOnly = args.Length > 10 && args[10] == "record";

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
        using var size = Column.Wrap(Enumerable.Range(0, count).Select(i => (double)(i % 101)).ToArray());
        var builder = DataTable.Create().Add("x", x).Add("y", y).Add("s", size);
        if (categories > 0)
        {
            var names = Enumerable.Range(0, categories).Select(i => $"k{i:D3}").ToArray();
            builder = builder.Add("c", Column.Categorical(Enumerable.Range(0, count).Select(i => (string?)names[i % categories]).ToArray()));
        }

        var table = builder.Build();
        var plot = Plot.Create(table)
            .Point(
                table.Col<double>("x"),
                table.Col<double>("y"),
                p =>
                {
                    _ = p.Size(diameter).Opacity(opacity).Shape(shape);
                    _ = categories > 0 && channel is "color" or "both" ? p.ColorBy(table.Cat("c")) : p;
                    _ = categories > 0 && channel is "shape" or "both" or "shapesize" ? p.ShapeBy(table.Cat("c")) : p;
                    _ = channel is "size" or "shapesize" ? p.SizeBy(table.Col<double>("s")) : p;
                }
            )
            .ScaleX(Scale.Linear().WithDomain(0, 100))
            .ScaleY(Scale.Linear().WithDomain(0, 100))
            .Build();
        using var device = D3D11Backend.Create();
        var backend = new CountingBackend(device);
        backend.Images.Points.DrawsWholePages = pages;
        using var engine = new PlotEngine(backend, 1920, 1080);

        // The first display: frames until the index is built and every page the whole view draws is on the GPU.
        var shown = Stopwatch.StartNew();
        engine.SetPlot(plot);
        engine.RenderFrame();
        var (waited, firstRuns, pageFrames) = (0, -1.0, 0);
        while (engine.IsProvisional)
        {
            Thread.Sleep(1);
            engine.RenderFrame();
            waited++;
            if (backend.Images.Points.PagesInUse > 0)
            {
                firstRuns = firstRuns < 0 ? shown.Elapsed.TotalSeconds : firstRuns;
                pageFrames++;
            }
        }

        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"first display: {shown.Elapsed.TotalSeconds:F3} s over {waited} frames; first pages at {firstRuns:F3} s; frames that sent pages: {pageFrames}; pages on the GPU: {backend.Images.Points.PagesInUse} ({backend.Images.Points.BytesInUse} bytes)"
            )
        );
        PrintCells(engine.Plan!);

        // The script: zoom in to a tenth of the side around a point that drifts, and out again, 300 frames a round. The
        // views are made before the frames: a view is an object of the scene's own.
        var panel = new PanelId(0, 0);
        var total = frames + Warmup;
        var views = new PanelView2D[total];
        for (var f = 0; f < total; f++)
        {
            var t = (f % 300) / 300.0;

            // "whole": the whole data stays in view, moved by a hundredth of the side, so that every frame draws every point.
            var span = whole ? 102 : 100 * Math.Pow(10, -Math.Sin(t * Math.PI));
            var (cx, cy) = whole
                ? (50 + Math.Sin(t * 2 * Math.PI), 50 + Math.Cos(t * 2 * Math.PI))
                : (50 + (20 * Math.Sin(t * 2 * Math.PI)), 50 + (10 * Math.Cos(t * 2 * Math.PI)));
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

        // A wait of a few milliseconds needs the timer's finer step: by default a sleep ends on a 15.6 ms tick (.cursor/rules/bench.mdc).
        _ = TimeBeginPeriod(1);
        var (runs, drawn) = (new int[total], new long[total]);

        // Each frame's allocations by stage (the view's change, the drawing, the rest of the frame), its GPU time put on the
        // frame it timed, and when it began, for a file of one row a frame (.cursor/rules/bench.mdc).
        var (inView, inDraw) = (new long[total], new long[total]);
        var (gpuOf, beganAt) = (new double[total], new double[total]);
        Array.Fill(gpuOf, double.NaN);
        backend.Skips = recordOnly;
        var (busyFrom, ownFrom, clockFrom) = (MachineBusy(), Environment.CpuUsage.TotalTime, DateTime.Now);
        var began = Stopwatch.GetTimestamp();
        var firstFrame = backend.Frames;
        for (var f = 0; f < total; f++)
        {
            beganAt[f] = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            engine.Views.SetView(panel, views[f]);
            inView[f] = GC.GetAllocatedBytesForCurrentThread() - bytes;
            engine.RenderFrame();
            cpu[f] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            allocated[f] = GC.GetAllocatedBytesForCurrentThread() - bytes;
            (runs[f], drawn[f], inDraw[f]) = (backend.Runs, backend.Points, backend.DrawBytes);
            if (backend.LastFrameTime is { } time && time.Frame > lastTimed)
            {
                lastTimed = time.Frame;
                var timed = time.Frame - firstFrame;
                if (timed >= 0 && timed < total)
                {
                    gpuOf[timed] = time.Duration.TotalMilliseconds;
                }

                if (f >= Warmup)
                {
                    gpu.Add(time.Duration.TotalMilliseconds);
                }
            }

            // A frame a period at the fastest: back to back, the GPU's clock rises and its time reads low (.cursor/rules/bench.mdc).
            var left = period - Stopwatch.GetElapsedTime(start);
            if (left > TimeSpan.Zero)
            {
                Thread.Sleep(left);
            }
        }

        var seconds = Stopwatch.GetElapsedTime(began).TotalSeconds;
        _ = TimeEndPeriod(1);

        // The cores the other processes used over the frames: the machine's busy time less this process's (.cursor/rules/bench.mdc).
        var others = ((MachineBusy() - busyFrom) - (Environment.CpuUsage.TotalTime - ownFrom)).TotalSeconds / seconds;
        var clockTo = DateTime.Now;
        backend.Skips = false;

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
        var gpuKept = gpu.Count > 0 ? gpu.ToArray() : [double.NaN];
        var (keptRuns, keptDrawn) = (runs.Skip(Warmup).ToArray(), drawn.Skip(Warmup).ToArray());
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"form={(pages ? "pages" : "blocks")} frames={(recordOnly ? "record" : "draw")} ran {Clock(clockFrom)} to {Clock(clockTo)} other_cores={others:F2}"
            )
        );
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"resident_runs: mean={keptRuns.Average():F1} max={keptRuns.Max()} a frame; resident_points: mean={keptDrawn.Average():F0} max={keptDrawn.Max()} a frame"
            )
        );
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"points={count} diameter={diameter} opacity={opacity} dist={args[3]} categories={categories} channel={channel} shape={shape} script={(whole ? "whole" : "zoom")} frames={frames} (after {Warmup}) frame_period_ms={seconds * 1000 / total:F2}"
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
                $"gpu_frame: frames={gpu.Count} p50={Percentile(gpuKept, 0.5):F3} p95={Percentile(gpuKept, 0.95):F3} p99={Percentile(gpuKept, 0.99):F3} max={gpuKept.Max():F3} ms"
            )
        );
        Console.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"allocated after warm-up: {allocated.Skip(Warmup).Sum()} bytes over {frames} frames")
        );

        // The script repeats every 300 frames: the last 300 show views that were drawn before, with no new tick label.
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"allocated in the last 300 frames: {allocated.TakeLast(300).Sum()} bytes; frames that allocated: {allocated.Skip(Warmup).Count(b => b > 0)}, the last at frame {Array.FindLastIndex(allocated, b => b > 0)}"
            )
        );
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"hit_x: p50={Percentile(inX, 0.5):F4} p99={Percentile(inX, 0.99):F4} max={inX.Max():F4} ms; hit_xy: p50={Percentile(inXY, 0.5):F4} p99={Percentile(inXY, 0.99):F4} max={inXY.Max():F4} ms; found={found}"
            )
        );

        // The frames after the warm-up that allocated, by stage: what the view's change, the drawing, and the rest of the
        // frame (the plan's layout, its guides, and the layers' recording) allocated.
        for (var f = Warmup; f < total; f++)
        {
            if (allocated[f] != 0)
            {
                Console.WriteLine(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"  allocated at frame {f}: {allocated[f]} B = view {inView[f]} + draw {inDraw[f]} + rest {allocated[f] - inView[f] - inDraw[f]}; runs {runs[f]}, the most before {(f == 0 ? 0 : runs[..f].Max())}"
                    )
                );
            }
        }

        if (csv is not null)
        {
            using var writer = new StreamWriter(csv);
            writer.WriteLine("frame,began_ms,cpu_ms,gpu_ms,runs,points,allocated,view_bytes,draw_bytes");
            for (var f = 0; f < total; f++)
            {
                writer.WriteLine(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{f},{beganAt[f]:F3},{cpu[f]:F4},{gpuOf[f]:F4},{runs[f]},{drawn[f]},{allocated[f]},{inView[f]},{inDraw[f]}"
                    )
                );
            }
        }

        return 0;
    }

    /// <summary>
    /// Prints the resident cells of the plot's first layer: their number over its parts on the index path, and the fewest
    /// and the most of a part, with the mean points of a cell (design §5.5.2 "セルの表").
    /// </summary>
    private static void PrintCells(PlotPlan plan)
    {
        var layer = plan.Layers[0];
        IEnumerable<LayerPlan> parts = layer is SplitPlan split ? split.Parts : [layer];
        var (cells, least, most, points, indexed) = (0L, int.MaxValue, 0, 0L, 0);
        foreach (var part in parts)
        {
            if (part is not IndexedPointPlan { SpatialIndex: { } index })
            {
                continue;
            }

            var snapshot = index.Acquire();
            var count = snapshot.ResidentCells.Length;
            (cells, least, most, points, indexed) = (
                cells + count,
                Math.Min(least, count),
                Math.Max(most, count),
                points + snapshot.PointCount,
                indexed + 1
            );
            snapshot.Release();
        }

        Console.WriteLine(
            indexed == 0
                ? "resident_cells: none (the CPU path)"
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"resident_cells: {cells} over {indexed} parts, {least} to {most} a part, {(double)points / cells:F0} points a cell"
                )
        );
    }

    [System.Runtime.InteropServices.LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    internal static partial uint TimeBeginPeriod(uint period);

    [System.Runtime.InteropServices.LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    internal static partial uint TimeEndPeriod(uint period);

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", EntryPoint = "GetSystemTimes")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out long idle, out long kernel, out long user);

    // The time every processor of the machine was busy since it started: the kernel time holds the idle time.
    private static TimeSpan MachineBusy() =>
        GetSystemTimes(out var idle, out var kernel, out var user) ? new TimeSpan(kernel + user - idle) : default;

    // A wall-clock time as nvidia-smi writes its samples' times of day, without a date format (.cursor/rules/aot.mdc).
    private static string Clock(DateTime time) =>
        string.Create(CultureInfo.InvariantCulture, $"{time.Hour:D2}:{time.Minute:D2}:{time.Second:D2}.{time.Millisecond:D3}");

    /// <summary>
    /// The backend of the scene: the hardware's, with the resident runs and their points of each drawn list counted, and the
    /// drawing left out when asked, so that a frame's CPU time is what the engine does before it draws.
    /// </summary>
    private sealed class CountingBackend(D3D11Backend inner) : IGpuBackend
    {
        public bool Skips { get; set; }

        public int Runs { get; private set; }

        public long Points { get; private set; }

        public bool IsWarp => inner.IsWarp;

        public long DeviceGeneration => inner.DeviceGeneration;

        public GpuFrameTime? LastFrameTime => inner.LastFrameTime;

        public TextEngine Text => inner.Text;

        public ResidencyCache Images => inner.Images;

        public IRenderTarget CreateRenderTarget(in RenderTargetDescription description) => inner.CreateRenderTarget(description);

        // The frames begun: the number the hardware's timer gives the next frame, as it numbers each frame it begins.
        public long Frames { get; private set; }

        // What the last drawing allocated on this thread, the counting included.
        public long DrawBytes { get; private set; }

        public FrameScope BeginFrame()
        {
            Frames++;
            return inner.BeginFrame();
        }

        public void Draw(IRenderTarget target, RenderList list)
        {
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            Count(list);
            if (!Skips)
            {
                inner.Draw(target, list);
            }

            DrawBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        }

        private void Count(RenderList list)
        {
            var runs = list.ResidentRuns;
            long points = 0;
            foreach (ref readonly var run in runs)
            {
                points += run.Count;
            }

            (Runs, Points) = (runs.Length, points);
        }

        public void Copy(IRenderTarget source, IRenderTarget destination) => inner.Copy(source, destination);

        public void EndFrame(long token) => inner.EndFrame(token);

        public void Dispose() => inner.Dispose();
    }

    internal static double Percentile(double[] values, double share)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? double.NaN : sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * share))];
    }
}
