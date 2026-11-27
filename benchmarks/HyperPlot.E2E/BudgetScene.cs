using System.Diagnostics;
using System.Globalization;
using HyperPlot.DataPath;
using HyperPlot.Interaction;
using HyperPlot.Rendering;
using HyperPlot.Rendering.D3D11;

namespace HyperPlot.E2E;

/// <summary>
/// The scenes of row R3b2's fifth part (Issue #627, design §9 "重なりと予算（行 R3b2）"), on a <see cref="PlotEngine"/> at
/// 1,920 × 1,080 on the hardware, a frame every 16.7 ms or more. "full": a Points layer of the density scene's seeded
/// uniform <see cref="float"/> points mapped from files, at the whole view, past the budget: once the pages that fit are on
/// the GPU, 600 frames are recorded and not drawn (two views a millionth of the side apart, so that each frame records),
/// for the time of a frame whose budget is full. "two": two Auto layers of 13,107,200 uniform points each (200 pages,
/// 157,286,400 bytes, 150 MiB) at the whole view, 1-DIP markers and a threshold of 2e7 points, so that only the budget's
/// sum decides: the frames until none is provisional, and which layer draws a density.
/// </summary>
internal static class BudgetScene
{
    private const int Warmup = 120;
    private const int Measured = 600;
    private static readonly TimeSpan Period = TimeSpan.FromMilliseconds(1000.0 / 60);
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(10);
    private static readonly PanelId Panel = new(0, 0);

    /// <summary>Runs a scene and prints its figures; returns the exit code.</summary>
    /// <param name="args">"full", the folder of the data files, the count, and a file for one row a measured frame, if any; or "two".</param>
    public static int Run(ReadOnlySpan<string> args)
    {
        const string Usage = "budget full <dir> <count> | budget two";
        _ = PointsScene.TimeBeginPeriod(1);
        try
        {
            return args.Length switch
            {
                >= 3 when args[0] == "full" => Full(
                    args[1],
                    (long)double.Parse(args[2], CultureInfo.InvariantCulture),
                    args.Length > 3 ? args[3] : null
                ),
                >= 1 when args[0] == "two" => Two(),
                _ => Fail(Usage),
            };
        }
        finally
        {
            _ = PointsScene.TimeEndPeriod(1);
        }
    }

    private static int Fail(string usage)
    {
        Console.Error.WriteLine(usage);
        return 2;
    }

    /// <summary>The full-budget frame (design §9 "予算が満ちたフレーム"): the frames of a Points layer past the budget, recorded and not drawn.</summary>
    private static int Full(string directory, long count, string? frameFile)
    {
        _ = Directory.CreateDirectory(directory);
        var made = Stopwatch.StartNew();
        var (xPath, yPath, written) = DensityScene.Ensure(directory, count, "uniform");
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"files: {xPath} and {yPath}, seed {DensityScene.Seed}, {(written ? $"written now in {made.Elapsed.TotalSeconds:F1} s" : "kept")}"
            )
        );
        using var x = Column.MapFile<float>(xPath);
        using var y = Column.MapFile<float>(yPath);
        var table = DataTable.Create().Add("x", x).Add("y", y).Build();
        var plot = Plot.Create(table)
            .Point(table.Col<float>("x"), table.Col<float>("y"), p => p.Draw(PointDraw.Points))
            .ScaleX(Scale.Linear().WithDomain(0, PointFiles.Side))
            .ScaleY(Scale.Linear().WithDomain(0, PointFiles.Side))
            .Build();
        using var inner = D3D11Backend.Create();
        var backend = new PointsScene.CountingBackend(inner);
        var pool = new PlotWorkerPool(Math.Max(1, Environment.ProcessorCount - 1));
        try
        {
            _ = DensityScene.Warm(inner);
            using var engine = new PlotEngine(backend, 1920, 1080, 1, pool);
            var view = new PanelView2D(ViewRange.FromValues(0, PointFiles.Side), ViewRange.FromValues(0, PointFiles.Side));
            var nudge = PointFiles.Side * 1e-6;
            var other = new PanelView2D(ViewRange.FromValues(nudge, PointFiles.Side + nudge), ViewRange.FromValues(0, PointFiles.Side));
            engine.SetPlot(plot);
            var shown = Stopwatch.StartNew();
            var pages = backend.Images.Points;

            // Until the index has its full snapshot and the pages that fit stay the same for 60 frames.
            var (steady, last, frames) = (0, -1, 0);
            while (steady < 60)
            {
                if (shown.Elapsed > Deadline)
                {
                    Console.Error.WriteLine("full: the pages did not settle before the deadline");
                    return 1;
                }

                _ = Frame(engine, null);
                frames++;
                var part = engine.Plan?.Layers is { Count: > 0 } layers ? layers[0] as IndexedPointPlan : null;
                var full = part?.SpatialIndex is { IsComplete: true };
                steady = full && pages.PagesInUse == last ? steady + 1 : 0;
                last = pages.PagesInUse;
            }

            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"settled: {shown.Elapsed.TotalSeconds:F1} s, {frames} frames; pages {pages.PagesInUse} ({pages.BytesInUse} bytes of a budget of {backend.Images.Budget})"
                )
            );
            var load = MachineLoad.Now();
            var (cpu, scan, searches, missed) = (new List<double>(), new List<double>(), new List<int>(), new List<int>());
            var (fullFrames, allocated) = (0, 0L);
            var rows = new List<string>(Measured);
            backend.Skips = true;
            for (var f = 0; f < Warmup + Measured; f++)
            {
                var collections = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
                var at = DateTime.Now;
                var (time, bytes) = Frame(engine, f % 2 == 0 ? other : view);
                if (f < Warmup)
                {
                    continue;
                }

                // One row a frame: its time of day, its CPU time, the scan, the recording of the layers, the extraction,
                // the rest of the frame, the collections of each generation during it, and its bytes.
                var plan = engine.Plan!;
                var (scanMs, recordMs, extractMs) = (Ms(plan.ScanTicks), Ms(plan.RecordTicks), Ms(plan.ExtractTicks));
                rows.Add(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{f - Warmup},{at:HH:mm:ss.fff},{time:F3},{scanMs:F3},{recordMs:F3},{extractMs:F3},{time - scanMs - recordMs - extractMs:F3},{GC.CollectionCount(0) - collections.Item1},{GC.CollectionCount(1) - collections.Item2},{GC.CollectionCount(2) - collections.Item3},{bytes}"
                    )
                );

                cpu.Add(time);
                scan.Add(engine.Plan!.ScanTicks * 1000.0 / Stopwatch.Frequency);
                searches.Add(pages.SearchesThisFrame);
                missed.Add(pages.MissedThisFrame);
                fullFrames += backend.Images.FullThisFrame ? 1 : 0;
                allocated += bytes;
            }

            backend.Skips = false;
            var (others, own) = MachineLoad.CoresSince(load);
            if (frameFile is not null)
            {
                File.WriteAllLines(frameFile, ["frame,time,cpu_ms,scan_ms,record_ms,extract_ms,rest_ms,gen0,gen1,gen2,bytes", .. rows]);
            }

            var records = rows.Select(r => double.Parse(r.Split(',')[4], CultureInfo.InvariantCulture)).ToArray();
            var times = cpu.ToArray();
            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"full frames: frames={times.Length} cpu p50={PointsScene.Percentile(times, 0.5):F3} p95={PointsScene.Percentile(times, 0.95):F3} max={times.Max():F3} ms; scan p50={PointsScene.Percentile([.. scan], 0.5):F3} ms; record p50={PointsScene.Percentile(records, 0.5):F3} p95={PointsScene.Percentile(records, 0.95):F3} max={records.Max():F3} ms; requests that missed a page p50={PointsScene.Percentile([.. missed.Select(m => (double)m)], 0.5):F0}; searches a frame p50={PointsScene.Percentile([.. searches.Select(s => (double)s)], 0.5):F0} max={searches.Max()}; frames known full {fullFrames}; allocated {allocated} bytes; pages {pages.PagesInUse}; other processes' cores {others:F2}, this process's {own:F2}"
                )
            );
            return 0;
        }
        finally
        {
            pool.Dispose();
        }
    }

    /// <summary>Two Auto layers of 150 MiB each, whose sum passes the budget (design §9 "予算"): the switch and the end of the provisional frames.</summary>
    private static int Two()
    {
        const int Count = 200 * PointPages.PagePoints;
        var builder = DataTable.Create();
        var columns = new List<IDisposable>();
        for (var layer = 0; layer < 2; layer++)
        {
            var random = new Random(627 + layer);
            var (xs, ys) = (new float[Count], new float[Count]);
            for (var i = 0; i < Count; i++)
            {
                (xs[i], ys[i]) = ((float)random.NextDouble(), (float)random.NextDouble());
            }

            var (x, y) = (Column.Wrap(xs), Column.Wrap(ys));
            columns.Add(x);
            columns.Add(y);
            builder = builder.Add($"x{layer}", x).Add($"y{layer}", y);
        }

        var table = builder.Build();
        var plot = Plot.Create(table)
            .Point(table.Col<float>("x0"), table.Col<float>("y0"), p => p.Size(1).Density(threshold: 20_000_000))
            .Point(table.Col<float>("x1"), table.Col<float>("y1"), p => p.Size(1).Density(threshold: 20_000_000))
            .ScaleX(Scale.Linear().WithDomain(0, 1))
            .ScaleY(Scale.Linear().WithDomain(0, 1))
            .Build();
        using var inner = D3D11Backend.Create();
        var backend = new PointsScene.CountingBackend(inner);
        var pool = new PlotWorkerPool(Math.Max(1, Environment.ProcessorCount - 1));
        try
        {
            _ = DensityScene.Warm(inner);
            using var engine = new PlotEngine(backend, 1920, 1080, 1, pool);
            var shown = Stopwatch.StartNew();
            engine.SetPlot(plot);
            var (frames, settled, provisional) = (0, -1.0, 0);
            var (cpu, allocated) = (new List<double>(), 0L);
            while (shown.Elapsed < Deadline)
            {
                var (time, bytes) = Frame(engine, null);
                frames++;
                cpu.Add(time);
                allocated += bytes;
                provisional += engine.IsProvisional ? 1 : 0;
                if (
                    !engine.IsProvisional
                    && engine.Plan!.Layers.Count == 2
                    && engine.Plan.Layers[0] is IndexedPointPlan { SpatialIndex.IsComplete: true }
                )
                {
                    settled = shown.Elapsed.TotalSeconds;
                    break;
                }
            }

            var plan = engine.Plan!;
            var (a, b) = ((IndexedPointPlan)plan.Layers[0], (IndexedPointPlan)plan.Layers[1]);
            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"two: {(settled < 0 ? "still provisional at the deadline" : $"not provisional after {settled:F2} s")}, {frames} frames ({provisional} provisional); layer 0 needs {a.Switch.Needed} bytes and draws {(a.DrewDensity ? "a density" : "raw points")}, layer 1 needs {b.Switch.Needed} bytes and draws {(b.DrewDensity ? "a density" : "raw points")}; predicted times {a.Switch.LastTime:F2} and {b.Switch.LastTime:F2} ms, counts {a.Switch.LastCount} and {b.Switch.LastCount}; the sum left {plan.Budget.LastSum} bytes of {backend.Images.Budget}; pages {backend.Images.Points.PagesInUse}, tiles' slots {backend.Images.SlotsInUse}; cpu p50={PointsScene.Percentile([.. cpu], 0.5):F3} max={cpu.Max():F3} ms; allocated {allocated} bytes"
                )
            );
            return settled < 0 ? 1 : 0;
        }
        finally
        {
            pool.Dispose();
            foreach (var column in columns)
            {
                column.Dispose();
            }
        }
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>Draws one frame, at a view if one is given, and paces it to the period; returns its CPU time in ms and its bytes.</summary>
    private static (double Cpu, long Bytes) Frame(PlotEngine engine, PanelView2D? view)
    {
        var start = Stopwatch.GetTimestamp();
        if (view is { } next)
        {
            engine.Views.SetView(Panel, next);
        }

        var bytes = GC.GetAllocatedBytesForCurrentThread();
        engine.RenderFrame();
        bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        var cpu = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var left = Period - Stopwatch.GetElapsedTime(start);
        if (left > TimeSpan.Zero)
        {
            Thread.Sleep(left);
        }

        return (cpu, bytes);
    }
}
