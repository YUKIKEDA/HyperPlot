using System.Diagnostics;
using System.Globalization;
using HyperPlot.DataPath;
using HyperPlot.Interaction;
using HyperPlot.Rendering;
using HyperPlot.Rendering.D3D11;

namespace HyperPlot.E2E;

/// <summary>
/// The scene of the density and the switch from raw points (design §9 "密度と切り替え（行 R3b1）", requirements P-3 and
/// P-15): a <see cref="PlotEngine"/> over one point layer of seeded <see cref="float"/> x and y columns mapped from files,
/// at 1,920 × 1,080 on the hardware, a frame every 16.7 ms or more. Every mode first sets the plot and draws until the
/// frames are no longer provisional, and prints when the first density came, when the index published its coarse snapshot,
/// its full snapshot, and its density tiles, and when the last provisional frame came. Then "zoom"
/// zooms from the whole view into 1e-4 of the range and out again; "switch" runs the same script and prints the frames
/// where the layer changes between density and raw points; "categories" draws the layer split into 8 categories, whole
/// view; "first" stops after the first display. Each frame's CPU time (the view's change and
/// <see cref="PlotEngine.RenderFrame"/>), its GPU time, and its allocations, over all the frames and apart for the density
/// frames and the raw-point frames.
/// </summary>
internal static class DensityScene
{
    // Two seconds at the frame period before a frame counts: a run under that measures the GPU's clock rising (.cursor/rules/bench.mdc).
    private const int Warmup = 120;

    // The frames of a round of the zoom script.
    private const int Round = 300;

    // The categories of the "categories" mode (design §9: 1e8 points in 8 categories).
    private const int Categories = 8;

    // The largest layer the "categories" mode makes: its category column is made from an array of one string a row.
    private const long MaxCategoryRows = 100_000_000;

    // The seed of HyperPlot.DataPath's generate-points (SeriesFile.DefaultSeed), so that both write the same bytes.
    private const ulong Seed = 0x48_79_70_65_72_50_6C_6FUL;

    // The wait for the index and the tiles, past which the scene ends with exit code 1 (.cursor/rules/bench.mdc). Row C3a's
    // builds of 1e9 points took 48 to 82 s.
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan Period = TimeSpan.FromMilliseconds(1000.0 / 60);

    /// <summary>Runs the scene and prints its figures; returns the exit code.</summary>
    /// <param name="args">The folder of the data files, the count (1e8 or 1e9), "uniform" or "cluster", the mode ("zoom", "switch", "first", or "categories"), the frames measured (600 by default), and how the layer draws ("auto" by default, "points", or "density").</param>
    public static int Run(ReadOnlySpan<string> args)
    {
        const string Usage = "density <dir> <count> <uniform|cluster> <zoom|switch|first|categories> [frames] [auto|points|density]";
        if (args.Length < 4)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var directory = args[0];

        // "1e9" as well as "1000000000".
        var count = (long)double.Parse(args[1], CultureInfo.InvariantCulture);
        string? distribution = args[2] switch
        {
            "uniform" => "uniform",
            "cluster" or "clustered" => "clustered",
            _ => null,
        };
        var mode = args[3];
        var frames = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 600;
        PointDraw? drawn =
            args.Length > 5
                ? args[5] switch
                {
                    "auto" => PointDraw.Auto,
                    "points" => PointDraw.Points,
                    "density" => PointDraw.Density,
                    _ => null,
                }
                : PointDraw.Auto;
        if (
            distribution is null
            || drawn is not { } draw
            || mode is not ("zoom" or "switch" or "first" or "categories")
            || count < 1
            || frames < Round
        )
        {
            Console.Error.WriteLine(Usage);
            Console.Error.WriteLine("The frames are 300 or more: the allocations are read over the last round of the script.");
            return 2;
        }

        if (mode == "categories" && count > MaxCategoryRows)
        {
            Console.Error.WriteLine("The categories mode takes 1e8 points at most: its category column is made from an array of one string a row.");
            return 2;
        }

        _ = Directory.CreateDirectory(directory);
        var made = Stopwatch.StartNew();
        var (xPath, yPath, written) = Ensure(directory, count, distribution);
        var state = written
            ? string.Create(CultureInfo.InvariantCulture, $"written now in {made.Elapsed.TotalSeconds:F1} s, unbuffered (not in the page cache)")
            : "kept from an earlier run (in the page cache or not)";
        Console.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"files: {xPath} and {yPath}, {count * sizeof(float)} bytes each, seed {Seed}, {state}")
        );

        using var x = Column.MapFile<float>(xPath);
        using var y = Column.MapFile<float>(yPath);
        var category = mode == "categories" ? CategoriesOf(count) : null;
        var builder = DataTable.Create().Add("x", x).Add("y", y);
        if (category is not null)
        {
            builder = builder.Add("c", category);
        }

        // The domain is the range the generator fills: the uniform square, or the square of the outliers.
        var (lo, hi) = distribution == "uniform" ? (0.0, PointFiles.Side) : (-PointFiles.OutlierReach, PointFiles.OutlierReach);
        var table = builder.Build();
        var plot = Plot.Create(table)
            .Point(
                table.Col<float>("x"),
                table.Col<float>("y"),
                p =>
                {
                    _ = p.Draw(draw);
                    _ = category is not null ? p.ColorBy(table.Cat("c")) : p;
                }
            )
            .ScaleX(Scale.Linear().WithDomain(lo, hi))
            .ScaleY(Scale.Linear().WithDomain(lo, hi))
            .Build();

        using var backend = D3D11Backend.Create();

        // A wait of a few milliseconds needs the timer's finer step: by default a sleep ends on a 15.6 ms tick (.cursor/rules/bench.mdc).
        _ = PointsScene.TimeBeginPeriod(1);
        try
        {
            var rendered = Warm(backend);
            using var engine = new PlotEngine(backend, 1920, 1080);
            var shown = Show(engine, backend, plot);
            if (shown < 0)
            {
                return 1;
            }

            rendered += shown;
            if (mode == "first")
            {
                return 0;
            }

            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"density points={count} dist={distribution} mode={mode} draw={draw} frames={frames} (after {Warmup}) viewport=1920x1080"
                )
            );
            Measure(engine, backend, mode, rendered, frames, lo, hi);
            using var process = Process.GetCurrentProcess();
            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"memory: working set {process.WorkingSet64 / 1048576.0:F0} MB, private {process.PrivateMemorySize64 / 1048576.0:F0} MB"
                )
            );
            return 0;
        }
        finally
        {
            _ = PointsScene.TimeEndPeriod(1);
        }
    }

    /// <summary>
    /// Returns the paths of the x and y files of the count and distribution in the folder, writing them from <see cref="Seed"/>
    /// when either is missing or of another length, and whether it wrote them.
    /// </summary>
    private static (string X, string Y, bool Written) Ensure(string directory, long count, string distribution)
    {
        var prefix = Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"density-{distribution}-{count}"));
        var (x, y) = PointFiles.Paths(prefix);
        var bytes = count * sizeof(float);
        if (File.Exists(x) && File.Exists(y) && new FileInfo(x).Length == bytes && new FileInfo(y).Length == bytes)
        {
            return (x, y, false);
        }

        PointFiles.Write(prefix, count, Seed, distribution);
        return (x, y, true);
    }

    /// <summary>Returns a column of <see cref="Categories"/> categories, the row i in the category i mod 8.</summary>
    private static CategoricalColumn CategoriesOf(long count)
    {
        var names = new string[Categories];
        for (var k = 0; k < names.Length; k++)
        {
            names[k] = string.Create(CultureInfo.InvariantCulture, $"k{k}");
        }

        var values = new string?[count];
        for (var i = 0L; i < count; i++)
        {
            values[i] = names[i % Categories];
        }

        return Column.Categorical(values);
    }

    /// <summary>
    /// Draws a layer of 2^20 points as density until its frames are no longer provisional, so that the measured plot's
    /// first frames do not make the GPU's pipelines (.cursor/rules/bench.mdc); returns the frames it drew on the backend.
    /// </summary>
    private static int Warm(D3D11Backend backend)
    {
        const int Count = 1 << 20;
        var random = new Random(2);
        var (xs, ys) = (new float[Count], new float[Count]);
        for (var i = 0; i < Count; i++)
        {
            (xs[i], ys[i]) = ((float)random.NextDouble(), (float)random.NextDouble());
        }

        using var x = Column.Wrap(xs);
        using var y = Column.Wrap(ys);
        var table = DataTable.Create().Add("x", x).Add("y", y).Build();
        var plot = Plot.Create(table)
            .Point(
                table.Col<float>("x"),
                table.Col<float>("y"),
                p =>
                {
                    _ = p.Draw(PointDraw.Density);
                }
            )
            .Build();
        using var engine = new PlotEngine(backend, 1920, 1080);
        engine.SetPlot(plot);
        var waited = Stopwatch.StartNew();
        engine.RenderFrame();
        var frames = 1;
        while (engine.IsProvisional && waited.Elapsed < Deadline)
        {
            Thread.Sleep(1);
            engine.RenderFrame();
            frames++;
        }

        return frames;
    }

    /// <summary>
    /// Sets the plot and draws a frame a period until a frame is no longer provisional: the index is full and every density
    /// face or page of points the whole view draws is on the GPU. Prints when the first density came (the coarse density
    /// while the index is coarse), when every part's index published its coarse snapshot, its full snapshot (requirement
    /// P-15's build), and its density tiles (P-15's tiles after it), each seen at the end of a frame, and when the last
    /// provisional frame ended; returns the frames drawn, or −1 past the deadline.
    /// </summary>
    private static int Show(PlotEngine engine, D3D11Backend backend, PlotSpec plot)
    {
        var (coarse, coarseFrame, coarseImages) = (-1.0, -1, 0);
        var (frames, sending, mostFaces) = (0, 0, 0);

        // When the least advanced part's index first reached each stage (IndexStage), and the frame.
        var reached = new double[4];
        var reachedFrame = new int[4];
        Array.Fill(reached, -1.0);
        var slots = backend.Images.SlotsInUse;
        var shown = Stopwatch.StartNew();
        engine.SetPlot(plot);
        while (true)
        {
            if (shown.Elapsed > Deadline)
            {
                Console.Error.WriteLine(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"first display: still provisional after {shown.Elapsed.TotalSeconds:F0} s and {frames} frames; first density at {coarse:F3} s; {Stages(reached, reachedFrame)}"
                    )
                );
                return -1;
            }

            var start = Stopwatch.GetTimestamp();
            engine.RenderFrame();
            frames++;
            var images = engine.Plan!.List.Images.Length;
            if (images > 0 && coarse < 0)
            {
                (coarse, coarseFrame, coarseImages) = (shown.Elapsed.TotalSeconds, frames, images);
            }

            for (var stage = IndexStage(engine.Plan!); stage > 0 && reached[stage] < 0; stage--)
            {
                (reached[stage], reachedFrame[stage]) = (shown.Elapsed.TotalSeconds, frames);
            }

            // The faces a frame sent, as the growth of the slots in use: a slot let go of for another hides one.
            var grown = backend.Images.SlotsInUse - slots;
            slots += grown;
            sending += grown > 0 ? 1 : 0;
            mostFaces = Math.Max(mostFaces, grown);
            if (!engine.IsProvisional)
            {
                Console.WriteLine(
                    coarse < 0
                        ? "first density: none"
                        : string.Create(CultureInfo.InvariantCulture, $"first density: {coarse:F3} s (frame {coarseFrame}, {coarseImages} images)")
                );
                Console.WriteLine(Stages(reached, reachedFrame));
                Console.WriteLine(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"first frame not provisional: {shown.Elapsed.TotalSeconds:F3} s (frame {frames}, {images} images, {engine.Plan!.List.ResidentRuns.Length} runs of resident points); density faces on the GPU {slots}, frames that sent faces {sending}, at most {mostFaces} faces ({(long)mostFaces * ResidencyCache.FaceBytes} bytes) a frame; pages of points {backend.Images.Points.PagesInUse} ({backend.Images.Points.BytesInUse} bytes)"
                    )
                );
                return frames;
            }

            Pace(start);
        }
    }

    /// <summary>Runs the script of the mode, a frame a period, and prints each frame's figures.</summary>
    /// <param name="rendered">The frames drawn on the backend before the script: the GPU's frame number of the script's first frame.</param>
    private static void Measure(PlotEngine engine, D3D11Backend backend, string mode, int rendered, int frames, double lo, double hi)
    {
        var panel = new PanelId(0, 0);
        var total = frames + Warmup;
        var (views, spans) = Script(mode, lo, hi, total);
        var cpu = new double[total];
        var gpu = new double[total];
        Array.Fill(gpu, double.NaN);
        var allocated = new long[total];
        var kinds = new char[total];
        var provisional = new bool[total];
        var faces = new int[total];
        var slots = backend.Images.SlotsInUse;

        // The wall clock of the frames, to match the GPU clock log of nvidia-smi to them.
        var from = DateTime.Now;
        var began = Stopwatch.GetTimestamp();
        for (var f = 0; f < total; f++)
        {
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            engine.Views.SetView(panel, views[f]);
            engine.RenderFrame();
            cpu[f] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            allocated[f] = GC.GetAllocatedBytesForCurrentThread() - bytes;

            // A time arrives some frames after its frame (design §5.2); each goes to the frame it timed.
            if (backend.LastFrameTime is { } time && time.Frame >= rendered && time.Frame < rendered + total)
            {
                gpu[time.Frame - rendered] = time.Duration.TotalMilliseconds;
            }

            kinds[f] = KindOf(engine.Plan!.List);
            provisional[f] = engine.IsProvisional;
            var now = backend.Images.SlotsInUse;
            faces[f] = Math.Max(0, now - slots);
            slots = now;

            // A frame a period at the fastest: back to back, the GPU's clock rises and its time reads low (.cursor/rules/bench.mdc).
            var left = Period - Stopwatch.GetElapsedTime(start);
            if (left > TimeSpan.Zero)
            {
                Thread.Sleep(left);
            }
        }

        var seconds = Stopwatch.GetElapsedTime(began).TotalSeconds;
        var to = DateTime.Now;
        var kept = cpu[Warmup..];
        var gpuKept = gpu[Warmup..].Where(g => !double.IsNaN(g)).ToArray();
        var kindsKept = kinds[Warmup..];
        var facesKept = faces[Warmup..];
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"frames ran {from:HH:mm:ss.fff} to {to:HH:mm:ss.fff}; frame_period_ms={seconds * 1000 / total:F2}"
            )
        );
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"kinds after warm-up: density={kindsKept.Count(k => k == 'D')} points={kindsKept.Count(k => k == 'P')} both={kindsKept.Count(k => k == 'B')} none={kindsKept.Count(k => k == '-')}"
            )
        );
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"cpu_frame: p50={PointsScene.Percentile(kept, 0.5):F3} p95={PointsScene.Percentile(kept, 0.95):F3} p99={PointsScene.Percentile(kept, 0.99):F3} max={kept.Max():F3} ms"
            )
        );
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"gpu_frame: frames={gpuKept.Length} p50={PointsScene.Percentile(gpuKept, 0.5):F3} p95={PointsScene.Percentile(gpuKept, 0.95):F3} p99={PointsScene.Percentile(gpuKept, 0.99):F3} max={(gpuKept.Length == 0 ? double.NaN : gpuKept.Max()):F3} ms"
            )
        );

        // P-3 is the density's frames: a run with the switch also has raw-point frames (row R3b2's), read apart.
        foreach (var kind in "DPB")
        {
            ReportKind(kind, kindsKept, kept, gpu[Warmup..]);
        }

        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"provisional frames after warm-up: {provisional[Warmup..].Count(p => p)}, the longest run {LongestRun(provisional[Warmup..])} frames"
            )
        );
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"density faces sent after warm-up (the growth of the slots in use): frames that sent {facesKept.Count(n => n > 0)}, at most {facesKept.Max()} faces ({(long)facesKept.Max() * ResidencyCache.FaceBytes} bytes) a frame, the last at frame {Array.FindLastIndex(faces, n => n > 0)}; faces on the GPU at the end {slots}; pages of points {backend.Images.Points.PagesInUse}"
            )
        );
        Console.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"allocated after warm-up: {allocated[Warmup..].Sum()} bytes over {frames} frames")
        );

        // The script repeats every 300 frames: the last 300 show views that were drawn before, with no new tick label.
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"allocated in the last 300 frames: {allocated.TakeLast(Round).Sum()} bytes; frames that allocated: {allocated[Warmup..].Count(b => b > 0)}, the last at frame {Array.FindLastIndex(allocated, b => b > 0)}"
            )
        );

        if (mode == "switch")
        {
            ReportChanges(kinds, spans, cpu, gpu, hi - lo, total);
        }
    }

    /// <summary>
    /// Prints the CPU and GPU times of the measured frames of one kind (<see cref="KindOf"/>), and the p95 of their sum over
    /// the frames whose GPU time came; nothing when no frame was of the kind.
    /// </summary>
    private static void ReportKind(char kind, char[] kinds, double[] cpu, double[] gpu)
    {
        var frames = Enumerable.Range(0, kinds.Length).Where(f => kinds[f] == kind).ToArray();
        if (frames.Length == 0)
        {
            return;
        }

        var cpuOf = frames.Select(f => cpu[f]).ToArray();
        var timed = frames.Where(f => !double.IsNaN(gpu[f])).ToArray();
        var gpuOf = timed.Select(f => gpu[f]).ToArray();
        var sum = timed.Select(f => cpu[f] + gpu[f]).ToArray();
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"  {kind} frames={frames.Length}: cpu p50={PointsScene.Percentile(cpuOf, 0.5):F3} p95={PointsScene.Percentile(cpuOf, 0.95):F3} p99={PointsScene.Percentile(cpuOf, 0.99):F3} max={cpuOf.Max():F3} ms; gpu frames={gpuOf.Length} p50={PointsScene.Percentile(gpuOf, 0.5):F3} p95={PointsScene.Percentile(gpuOf, 0.95):F3} p99={PointsScene.Percentile(gpuOf, 0.99):F3} max={(gpuOf.Length == 0 ? double.NaN : gpuOf.Max()):F3} ms; cpu+gpu of a frame p95={PointsScene.Percentile(sum, 0.95):F3} ms"
            )
        );
    }

    /// <summary>Returns the most frames in a row that were provisional.</summary>
    private static int LongestRun(bool[] provisional)
    {
        var (longest, run) = (0, 0);
        foreach (var p in provisional)
        {
            run = p ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        return longest;
    }

    /// <summary>
    /// Prints each frame where the layer's kind changed (density, raw points, both, or nothing), the view's width then, and the
    /// CPU and GPU times of the 10 frames before it and of it and the 9 after; then the changes a round of the script.
    /// </summary>
    private static void ReportChanges(char[] kinds, double[] spans, double[] cpu, double[] gpu, double whole, int total)
    {
        var changes = 0;
        for (var f = 1; f < total; f++)
        {
            if (kinds[f] == kinds[f - 1])
            {
                continue;
            }

            changes++;
            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"change at frame {f}{(f < Warmup ? " (warm-up)" : string.Empty)}: {kinds[f - 1]} -> {kinds[f]}, view width {spans[f] / whole:E3} of the range"
                )
            );
            for (var k = Math.Max(0, f - 10); k < Math.Min(total, f + 10); k++)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  frame {k} {kinds[k]} cpu={cpu[k]:F3} gpu={gpu[k]:F3} ms"));
            }
        }

        // The script crosses the threshold twice a round, in and out: more changes mean the 0.8 band did not hold.
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"changes: {changes} over {(double)total / Round:F2} rounds (2 a round when the layer switches once each way)"
            )
        );
    }

    /// <summary>
    /// Returns the views of the script and their widths in data units. "categories": the whole range, moved by a hundredth of
    /// it, so that every frame draws every point. Otherwise: zoom from the whole range into 1e-4 of it and out again,
    /// 300 frames a round, toward the middle of the square the uniform points and the clusters' centers fill, the center
    /// drifting by a fifth and a tenth of that square's side as the view narrows.
    /// </summary>
    private static (PanelView2D[] Views, double[] Spans) Script(string mode, double lo, double hi, int total)
    {
        var whole = hi - lo;
        var middle = (lo + hi) / 2;
        var dense = PointFiles.Side / 2;
        var views = new PanelView2D[total];
        var spans = new double[total];
        for (var f = 0; f < total; f++)
        {
            var t = (f % Round) / (double)Round;
            var depth = Math.Sin(t * Math.PI);
            var (span, cx, cy) =
                mode == "categories"
                    ? (1.02 * whole, middle + (0.01 * whole * Math.Sin(t * 2 * Math.PI)), middle + (0.01 * whole * Math.Cos(t * 2 * Math.PI)))
                    : (
                        whole * Math.Pow(10, -4 * depth),
                        middle + ((dense + (0.2 * PointFiles.Side * Math.Sin(t * 2 * Math.PI)) - middle) * depth),
                        middle + ((dense + (0.1 * PointFiles.Side * Math.Cos(t * 2 * Math.PI)) - middle) * depth)
                    );
            spans[f] = span;
            views[f] = new PanelView2D(
                ViewRange.FromValues(cx - (span / 2), cx + (span / 2)),
                ViewRange.FromValues(cy - (span / 2), cy + (span / 2))
            );
        }

        return (views, spans);
    }

    /// <summary>
    /// Returns what the plot's last frame recorded for the layer: 'D' for density faces (images), 'P' for runs of resident
    /// points, 'B' for both, '-' for neither. The legend's sample is a marker, not a run, so it does not count.
    /// </summary>
    private static char KindOf(RenderList list) =>
        (list.Images.Length > 0, list.ResidentRuns.Length > 0) switch
        {
            (true, false) => 'D',
            (false, true) => 'P',
            (true, true) => 'B',
            _ => '-',
        };

    /// <summary>
    /// Returns the stage of the least advanced part of the plot's first layer: 0 while a part has no index or nothing counted,
    /// 1 when every part's snapshot is coarse or better, 2 when every part's is full, 3 when every full snapshot also has its
    /// density tiles (design §3.4 "公開"). Each part's index is lent its current snapshot and given it back.
    /// </summary>
    private static int IndexStage(PlotPlan plan)
    {
        if (plan.Layers.Count == 0)
        {
            return 0;
        }

        var layer = plan.Layers[0];
        IEnumerable<LayerPlan> parts = layer is SplitPlan split ? split.Parts : [layer];
        var least = 3;
        foreach (var part in parts)
        {
            if (part is not IndexedPointPlan { SpatialIndex: { } index })
            {
                return 0;
            }

            var snapshot = index.Acquire();
            var stage = snapshot.Stage switch
            {
                SpatialStage.Full => snapshot.Tiles is null ? 2 : 3,
                SpatialStage.Coarse => 1,
                _ => 0,
            };
            snapshot.Release();
            least = Math.Min(least, stage);
        }

        return least;
    }

    /// <summary>Returns the line of the times the index reached its stages (<see cref="IndexStage"/>), −1 for a stage not reached.</summary>
    private static string Stages(double[] reached, int[] frames) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"index of every part: coarse snapshot {reached[1]:F3} s (frame {frames[1]}), full snapshot {reached[2]:F3} s (frame {frames[2]}), density tiles {reached[3]:F3} s (frame {frames[3]}); tiles after the full snapshot {(reached[3] < 0 || reached[2] < 0 ? double.NaN : reached[3] - reached[2]):F3} s"
        );

    private static void Pace(long start)
    {
        var left = Period - Stopwatch.GetElapsedTime(start);
        if (left > TimeSpan.Zero)
        {
            Thread.Sleep(left);
        }
    }
}
