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
/// of a hit test at seeded pixels in both modes. A layer of at most 131,072 rows takes the CPU path, and a larger one the index path.
/// </summary>
internal static partial class PointsScene
{
    // Spike R3b2: 2 seconds of frames before the measured ones (.cursor/rules/bench.mdc); HP_PROBE_WARMUP sets the count.
    private static readonly int Warmup = int.TryParse(Environment.GetEnvironmentVariable("HP_PROBE_WARMUP"), out var w) ? w : 120;

    // Spike R3b2: HP_PROBE_ZOOM=z makes the "whole" script show the middle 1/z of the data on each axis (its drift scaled
    // by 1/z too). It changes the view only: to keep the points in view comparable, the caller passes z² times the count.
    private static readonly double Zoom =
        double.TryParse(Environment.GetEnvironmentVariable("HP_PROBE_ZOOM"), NumberStyles.Float, CultureInfo.InvariantCulture, out var z) && z >= 1
            ? z
            : 1;

    // Spike R3b2 (Issue #472): HP_PROBE_READY_POLL=1, see the frame loop.
    private static readonly bool ReadyPoll = Environment.GetEnvironmentVariable("HP_PROBE_READY_POLL") == "1";

    /// <summary>Runs the scene and prints its figures; returns the exit code.</summary>
    /// <param name="args">The count, the diameter in DIPs, the opacity, "uniform" or "cluster", the frames measured (600 by default), the categories (0 by default), "whole" or "zoom", what the categories set ("color", "shape", "both"; "size" and "shapesize" size the points by a number too, row R3f), and the layer's shape.</param>
    public static int Run(ReadOnlySpan<string> args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine(
                "points <count> <diameter> <opacity> <uniform|cluster> [frames] [categories] [whole|zoom] [color|shape|both|size|shapesize] [shape]"
            );
            return 2;
        }

        // Spike R3b2: high priority, as the records' runs, so that a run started from Bash with its switches needs no wrapper.
        using (var self = Process.GetCurrentProcess())
        {
            self.PriorityClass = ProcessPriorityClass.High;
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

        // Spike R3b2: a scene that does not size the points wraps x again for the unused column, so that a run of 1.6e8
        // points does not make 1.3 GB it never reads.
        var sized = args.Length > 7 && args[7] is "size" or "shapesize";
        using var size = Column.Wrap(sized ? Enumerable.Range(0, count).Select(i => (double)(i % 101)).ToArray() : xs);
        var builder = DataTable.Create().Add("x", x).Add("y", y).Add("s", size);
        if (categories > 0)
        {
            var names = Enumerable.Range(0, categories).Select(i => $"k{i:D3}").ToArray();

            // Spike R3b2: HP_PROBE_CATEGORIES=region gives the categories bands of x of equal width (a part covers a band), and
            // otherwise row i takes category i mod n (every part covers the whole view), as on main.
            var region = Environment.GetEnvironmentVariable("HP_PROBE_CATEGORIES") == "region";
            builder = builder.Add(
                "c",
                Column.Categorical(
                    Enumerable
                        .Range(0, count)
                        .Select(i => (string?)names[region ? Math.Clamp((int)(xs[i] / 100 * categories), 0, categories - 1) : i % categories])
                        .ToArray()
                )
            );
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
        using var backend = D3D11Backend.Create();
        using var engine = new PlotEngine(backend, 1920, 1080);

        // The script: zoom in to a tenth of the side around a point that drifts, and out again, 300 frames a round. The
        // views are made before the frames: a view is an object of the scene's own.
        var panel = new PanelId(0, 0);
        var total = frames + Warmup;
        var views = new PanelView2D[total];
        for (var f = 0; f < total; f++)
        {
            var t = (f % 300) / 300.0;

            // "whole": the whole data stays in view, moved by a hundredth of the side, so that every frame draws every point.
            var span = whole ? 102 / Zoom : 100 * Math.Pow(10, -Math.Sin(t * Math.PI));
            var (cx, cy) = whole
                ? (50 + (Math.Sin(t * 2 * Math.PI) / Zoom), 50 + (Math.Cos(t * 2 * Math.PI) / Zoom))
                : (50 + (20 * Math.Sin(t * 2 * Math.PI)), 50 + (10 * Math.Cos(t * 2 * Math.PI)));
            views[f] = new PanelView2D(
                ViewRange.FromValues(cx - (span / 2), cx + (span / 2)),
                ViewRange.FromValues(cy - (span / 2), cy + (span / 2))
            );
        }

        // The first display: frames until the index is built and every page the whole view draws is on the GPU.
        var shown = Stopwatch.StartNew();
        engine.SetPlot(plot);
        engine.RenderFrame();

        // Spike only: at a zoom above 1, the first display waits for the pages of the zoomed view, not of the whole data,
        // which past about 2.2e7 points would never fit the page budget and would stay provisional.
        if (Zoom > 1)
        {
            engine.Views.SetView(panel, views[0]);
            engine.RenderFrame();
        }

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

        var cpu = new double[total];
        var allocated = new long[total];

        // Spike R3b2: per frame, the methods the JIT compiled on this thread inside the counted window, and the labels the
        // frame recorded that no earlier frame had (a new label's shaping allocates once, .cursor/rules/bench.mdc).
        var compiled = new long[total];
        var newLabels = new int[total];
        var labels = new List<(int Frame, string Text)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lookup = seen.GetAlternateLookup<ReadOnlySpan<char>>();
        long[] machineAtStart = [];

        // Spike R3b2 (Issue #472): per frame, the shaping cache's misses and the bytes of each step of them, the engine's
        // frame number, and the engine frame whose GPU time the scene took (-1: none).
        var shapeMisses = new long[total];
        var shapeBytes = new long[total, 5];
        var engineFrame = new long[total];
        var took = new long[total];
        var gpu = new List<double>(frames);
        var pass = new List<double>(frames);
        long lastTimed = -1;
        long lastPass = -1;

        // Spike R3b2 (Issue #472): HP_PROBE_PERIOD_MS sets the frame period (16.7 ms by default).
        var period = TimeSpan.FromMilliseconds(
            double.TryParse(Environment.GetEnvironmentVariable("HP_PROBE_PERIOD_MS"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)
            && ms > 0
                ? ms
                : 1000.0 / 60
        );

        // A wait of a few milliseconds needs the timer's finer step: by default a sleep ends on a 15.6 ms tick (.cursor/rules/bench.mdc).
        _ = TimeBeginPeriod(1);
        var began = Stopwatch.GetTimestamp();
        for (var f = 0; f < total; f++)
        {
            if (f == Warmup)
            {
                machineAtStart = MachineTimes();
            }

            var (m0, e0, s0, k0, a0, o0) = (
                HyperPlot.Rendering.SplitProbe.ShapeMisses,
                HyperPlot.Rendering.SplitProbe.ShapeEntryBytes,
                HyperPlot.Rendering.SplitProbe.ShapeSetBytes,
                HyperPlot.Rendering.SplitProbe.ShapeBackendBytes,
                HyperPlot.Rendering.SplitProbe.ShapeAddBytes,
                HyperPlot.Rendering.SplitProbe.ShapeOrderBytes
            );
            var methods = System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true);
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            engine.Views.SetView(panel, views[f]);
            engine.RenderFrame();
            cpu[f] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            allocated[f] = GC.GetAllocatedBytesForCurrentThread() - bytes;
            compiled[f] = System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true) - methods;
            shapeMisses[f] = HyperPlot.Rendering.SplitProbe.ShapeMisses - m0;
            shapeBytes[f, 0] = HyperPlot.Rendering.SplitProbe.ShapeEntryBytes - e0;
            shapeBytes[f, 1] = HyperPlot.Rendering.SplitProbe.ShapeSetBytes - s0;
            shapeBytes[f, 2] = HyperPlot.Rendering.SplitProbe.ShapeBackendBytes - k0;
            shapeBytes[f, 3] = HyperPlot.Rendering.SplitProbe.ShapeAddBytes - a0;
            shapeBytes[f, 4] = HyperPlot.Rendering.SplitProbe.ShapeOrderBytes - o0;
            engineFrame[f] = TimerProbe.Closing;
            took[f] = -1;
            foreach (ref readonly var text in engine.Plan!.List.Texts)
            {
                if (!lookup.Contains(text.Chars.Span))
                {
                    var label = text.Chars.ToString();
                    _ = seen.Add(label);
                    labels.Add((f, label));
                    newLabels[f]++;
                }
            }

            if (f >= Warmup && backend.LastFrameTime is { } time && time.Frame > lastTimed)
            {
                lastTimed = time.Frame;
                took[f] = time.Frame;
                gpu.Add(time.Duration.TotalMilliseconds);
            }

            if (f >= Warmup && backend.LastPassTime is { } passTime && passTime.Frame > lastPass)
            {
                lastPass = passTime.Frame;
                pass.Add(passTime.Duration.TotalMilliseconds);
            }

            // The probe's counts cover the measured frames only.
            if (f == Warmup - 1)
            {
                ResetProbe();
            }

            // A frame a period at the fastest: back to back, the GPU's clock rises and its time reads low (.cursor/rules/bench.mdc).
            // Spike R3b2 (Issue #472): HP_PROBE_READY_POLL=1 sleeps in steps of 1 ms and notes when each pending query set
            // reads as ready.
            var left = period - Stopwatch.GetElapsedTime(start);
            if (ReadyPoll)
            {
                backend.ProbeTimerReady();
                while ((left = period - Stopwatch.GetElapsedTime(start)) > TimeSpan.Zero)
                {
                    Thread.Sleep(1);
                    backend.ProbeTimerReady();
                }
            }
            else if (left > TimeSpan.Zero)
            {
                Thread.Sleep(left);
            }
        }

        var seconds = Stopwatch.GetElapsedTime(began).TotalSeconds;
        var machineAtEnd = MachineTimes();
        _ = TimeEndPeriod(1);
        var probeFrames = (double)frames;
        var probe = string.Create(
            CultureInfo.InvariantCulture,
            $"{HyperPlot.Rendering.SplitProbe.Describe()} categories_by={Environment.GetEnvironmentVariable("HP_PROBE_CATEGORIES") ?? "rows"} zoom={Zoom} warmup={Warmup}; per frame: groups={HyperPlot.Rendering.SplitProbe.Groups / probeFrames:F2} drawn_points={HyperPlot.Rendering.SplitProbe.DrawnPoints / probeFrames:F0} parts={HyperPlot.Rendering.SplitProbe.Parts / probeFrames:F2} pipeline_binds={HyperPlot.Rendering.SplitProbe.PipelineBinds / probeFrames:F2} draws={HyperPlot.Rendering.SplitProbe.Draws / probeFrames:F1} page_binds={HyperPlot.Rendering.SplitProbe.PageBinds / probeFrames:F1} constant_writes={HyperPlot.Rendering.SplitProbe.ConstantWrites / probeFrames:F1} color_changes={HyperPlot.Rendering.SplitProbe.ColorChanges / probeFrames:F1}"
        );

        PrintMaxBin(engine, panel, views, Warmup, diameter);

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
                $"gpu_frame: frames={gpuKept.Length} p50={Percentile(gpuKept, 0.5):F3} p95={Percentile(gpuKept, 0.95):F3} p99={Percentile(gpuKept, 0.99):F3} max={gpuKept.Max():F3} ms"
            )
        );
        var passKept = pass.ToArray();
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"gpu_points: frames={passKept.Length} p50={Percentile(passKept, 0.5):F3} p95={Percentile(passKept, 0.95):F3} p99={Percentile(passKept, 0.99):F3} max={(passKept.Length > 0 ? passKept.Max() : double.NaN):F3} ms"
            )
        );
        Console.WriteLine(probe);
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

        // Spike R3b2: each measured frame that allocated, with the JIT's methods and the new labels of that frame.
        var newAfterWarmup = 0;
        for (var f = Warmup; f < total; f++)
        {
            newAfterWarmup += newLabels[f] > 0 ? 1 : 0;
            if (allocated[f] > 0)
            {
                var texts = string.Join(" | ", labels.Where(l => l.Frame == f).Select(l => l.Text));
                Console.WriteLine(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"alloc_frame: frame={f} bytes={allocated[f]} jit_methods={compiled[f]} new_labels={newLabels[f]} [{texts}] shape_misses={shapeMisses[f]} shape_bytes: entry={shapeBytes[f, 0]} set={shapeBytes[f, 1]} backend={shapeBytes[f, 2]} cache_add={shapeBytes[f, 3]} order_add={shapeBytes[f, 4]} sum={shapeBytes[f, 0] + shapeBytes[f, 1] + shapeBytes[f, 2] + shapeBytes[f, 3] + shapeBytes[f, 4]} cache_count={HyperPlot.Rendering.SplitProbe.ShapeCacheCount}"
                    )
                );
            }
        }

        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"labels: measured frames with a new label: {newAfterWarmup}; of them allocated: {Enumerable.Range(Warmup, frames).Count(f => newLabels[f] > 0 && allocated[f] > 0)}; allocated with no new label: {Enumerable.Range(Warmup, frames).Count(f => newLabels[f] == 0 && allocated[f] > 0)}; jit methods after warm-up: {compiled.Skip(Warmup).Sum()}"
            )
        );

        // Spike R3b2 (Issue #472): the measured frames' misses of the shaping cache, and the timer's record per frame.
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"shape_misses after warm-up: {shapeMisses.Skip(Warmup).Sum()} in {shapeMisses.Skip(Warmup).Count(m => m > 0)} frames"
            )
        );
        WriteTimerLog(Warmup, total, engineFrame, took, cpu);

        // Spike R3b2 (.cursor/rules/bench.mdc, PR #541): the cores the other processes used over the measured frames: the
        // machine's busy time (GetSystemTimes: kernel, which holds the idle time, plus user, less idle) less this process's.
        var wall = (machineAtEnd[3] - machineAtStart[3]) / 1e7;
        var busy = ((machineAtEnd[1] - machineAtStart[1]) + (machineAtEnd[2] - machineAtStart[2]) - (machineAtEnd[0] - machineAtStart[0])) / 1e7;
        var own = (machineAtEnd[4] - machineAtStart[4]) / 1e7;
        using var current = Process.GetCurrentProcess();
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"machine: other_cores={(busy - own) / wall:F2} own_cores={own / wall:F2} busy_cores={busy / wall:F2} over {wall:F1} s of measured frames; logical processors {Environment.ProcessorCount}; priority {current.PriorityClass}"
            )
        );
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"hit_x: p50={Percentile(inX, 0.5):F4} p99={Percentile(inX, 0.99):F4} max={inX.Max():F4} ms; hit_xy: p50={Percentile(inXY, 0.5):F4} p99={Percentile(inXY, 0.99):F4} max={inXY.Max():F4} ms; found={found}"
            )
        );
        return 0;
    }

    // Spike R3b2 (Issue #472): HP_PROBE_TIMER_LOG=<file> writes a line per measured frame: what the timer did in the frame,
    // and whether the scene took a time; then a summary of the frames whose time no read gave the scene.
    private static void WriteTimerLog(int warmup, int total, long[] engineFrame, long[] took, double[] cpu)
    {
        var path = Environment.GetEnvironmentVariable("HP_PROBE_TIMER_LOG");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        static int At(long frame) => (int)(frame % TimerProbe.Capacity);
        static double Since(long later, long earlier) => later > 0 && earlier > 0 ? (later - earlier) * 1e3 / Stopwatch.Frequency : double.NaN;
        var frequency = (double)TimerProbe.Frequency;
        var lines = new List<string>
        {
            "f engine timed read_at_end pending_after stopped_at read_by took gpu_ms gpu_start_gap_ms cpu_ms ready_after_submit_ms read_after_submit_ms gpu_start_ticks gpu_end_ticks submit_qpc ready_qpc",
        };
        var (ringFull, readButDropped, unread, takenCount) = (0, 0, 0, 0);
        var readCounts = new int[5];
        ulong previousStart = 0;
        for (var f = warmup; f < total; f++)
        {
            var e = engineFrame[f];
            var i = At(e);
            var timed = TimerProbe.Timed[i] == 1;
            var start = TimerProbe.GpuStart[i];
            var gpuMs = start > 0 ? (TimerProbe.GpuEnd[i] - start) / frequency * 1e3 : double.NaN;
            var gap = start > 0 && previousStart > 0 ? (start - previousStart) / frequency * 1e3 : double.NaN;
            previousStart = start > 0 ? start : previousStart;
            readCounts[Math.Min(4, (int)TimerProbe.ReadAtEnd[i])]++;
            var taken = Array.IndexOf(took, e, warmup) >= 0;
            takenCount += taken ? 1 : 0;
            if (!timed)
            {
                ringFull++;
            }
            else if (TimerProbe.ReadBy[i] < 0)
            {
                unread++;
            }
            else if (!taken)
            {
                readButDropped++;
            }

            lines.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{f} {e} {(timed ? 1 : 0)} {TimerProbe.ReadAtEnd[i]} {TimerProbe.PendingAfterEnd[i]} {TimerProbe.StoppedAt[i]} {TimerProbe.ReadBy[i]} {(taken ? 1 : 0)} {gpuMs:F3} {gap:F3} {cpu[f]:F3} {Since(TimerProbe.ReadyQpc[i], TimerProbe.SubmitQpc[i]):F3} {(TimerProbe.ReadBy[i] >= 0 ? Since(TimerProbe.SubmitQpc[At(TimerProbe.ReadBy[i])], TimerProbe.SubmitQpc[i]) : double.NaN):F3} {start} {TimerProbe.GpuEnd[i]} {TimerProbe.SubmitQpc[i]} {TimerProbe.ReadyQpc[i]}"
                )
            );
        }

        lines.Add(string.Create(CultureInfo.InvariantCulture, $"# gpu_frequency={TimerProbe.Frequency} qpc_frequency={Stopwatch.Frequency}"));
        File.WriteAllLines(path, lines);
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"timer: frames={total - warmup} taken={takenCount} ring_full={ringFull} read_but_not_taken={readButDropped} timed_unread_at_end={unread}; closings that read 0/1/2/3/4 sets: {readCounts[0]}/{readCounts[1]}/{readCounts[2]}/{readCounts[3]}/{readCounts[4]}"
            )
        );
    }

    /// <summary>
    /// Spike R3b2 (Issue #472, O1): over the measured views (every 30th), the largest bin of the density tiles in view, in
    /// points per pixel, for each part and summed over the parts (an upper bound: the parts' densest bins may lie apart),
    /// and that times the marker's area in pixels: the points that cover the densest pixel, the figure a switch on
    /// "the largest bin × the marker's area" compares.
    /// </summary>
    internal static void PrintMaxBin(PlotEngine engine, PanelId panel, PanelView2D[] views, int from, float diameter)
    {
        var snapshots = engine
            .Plan!.Layers.SelectMany(l => l is SplitPlan split ? split.ProbeParts : [l])
            .OfType<IndexedPointPlan>()
            .Select(p => p.ProbeSnapshot)
            .Where(s => s is not null)
            .ToArray();
        var markerArea = Math.PI * Math.Pow(diameter * engine.Scale / 2, 2);
        var (largest, smallest, largestPart, sampled) = (0.0, double.MaxValue, 0.0, 0);
        var (count, level, tiles) = (0UL, -1, 0);
        for (var f = from; f < views.Length; f += 30)
        {
            var view = views[f];
            var (x0, x1) = (view.X.Origin + view.X.Lower, view.X.Origin + view.X.Upper);
            var (y0, y1) = (view.Y.Origin + view.Y.Lower, view.Y.Origin + view.Y.Upper);
            var low = engine.ViewToPixel(panel, ViewRange.FromValues(x0, x0), ViewRange.FromValues(y0, y0));
            var high = engine.ViewToPixel(panel, ViewRange.FromValues(x1, x1), ViewRange.FromValues(y1, y1));
            var (perX, perY) = (Math.Abs(high.X - low.X) / (x1 - x0), Math.Abs(high.Y - low.Y) / (y1 - y0));
            var sum = 0.0;
            foreach (var snapshot in snapshots)
            {
                var bin = snapshot!.ProbeMaxBin(x0, x1, y0, y1, perX, perY);
                sum += double.IsNaN(bin.PerPixel) ? 0 : bin.PerPixel;
                if (bin.PerPixel > largestPart)
                {
                    (largestPart, count, level) = (bin.PerPixel, bin.Count, bin.Level);
                }

                tiles += bin.Tiles;
            }

            (largest, smallest) = (Math.Max(largest, sum), Math.Min(smallest, sum));
            sampled++;
        }

        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"max_bin: parts={snapshots.Length} views={sampled} sum_over_parts_points_per_px: max={largest:F2} min={smallest:F2}; largest_part_bin: points_per_px={largestPart:F2} count={count} tile_level={level}; tiles_read={tiles}; marker_area_px={markerArea:F2}; max_bin_x_marker_area={largest * markerArea:F1}"
            )
        );
    }

    private static void ResetProbe()
    {
        HyperPlot.Rendering.SplitProbe.Groups = 0;
        HyperPlot.Rendering.SplitProbe.DrawnPoints = 0;
        HyperPlot.Rendering.SplitProbe.Parts = 0;
        HyperPlot.Rendering.SplitProbe.PipelineBinds = 0;
        HyperPlot.Rendering.SplitProbe.Draws = 0;
        HyperPlot.Rendering.SplitProbe.PageBinds = 0;
        HyperPlot.Rendering.SplitProbe.ConstantWrites = 0;
        HyperPlot.Rendering.SplitProbe.ColorChanges = 0;
    }

    // The machine's idle, kernel, and user times, the wall clock, and this process's kernel plus user time, in 100 ns.
    // Spike R3b2: the line of the cores the other processes used between two readings of MachineTimes.
    internal static string MachineLine(long[] start, long[] end)
    {
        var wall = (end[3] - start[3]) / 1e7;
        var busy = ((end[1] - start[1]) + (end[2] - start[2]) - (end[0] - start[0])) / 1e7;
        var own = (end[4] - start[4]) / 1e7;
        using var current = Process.GetCurrentProcess();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"machine: other_cores={(busy - own) / wall:F2} own_cores={own / wall:F2} busy_cores={busy / wall:F2} over {wall:F1} s; logical processors {Environment.ProcessorCount}; priority {current.PriorityClass}"
        );
    }

    internal static long[] MachineTimes()
    {
        _ = GetSystemTimes(out var idle, out var kernel, out var user);
        _ = GetProcessTimes(GetCurrentProcess(), out _, out _, out var processKernel, out var processUser);
        return [idle, kernel, user, DateTime.UtcNow.Ticks, processKernel + processUser];
    }

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    private static partial int GetSystemTimes(out long idle, out long kernel, out long user);

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    private static partial int GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [System.Runtime.InteropServices.LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    internal static partial uint TimeBeginPeriod(uint period);

    [System.Runtime.InteropServices.LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    internal static partial uint TimeEndPeriod(uint period);

    internal static double Percentile(double[] values, double share)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? double.NaN : sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * share))];
    }
}
