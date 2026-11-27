using System.Diagnostics;
using System.Globalization;
using HyperPlot.DataPath;
using HyperPlot.Interaction;
using HyperPlot.Rendering;
using HyperPlot.Rendering.D3D11;

namespace HyperPlot.E2E;

/// <summary>
/// The scenes of row R3b2's fourth part (Issue #603, design §9 "重なりと予算（行 R3b2）"): the workers' reads of the pages
/// of points, the table of cells, and the limit of cells, on a <see cref="PlotEngine"/> at 1,920 × 1,080 on the hardware, a
/// frame every 16.7 ms or more. "fill" and "m4": a layer of the density scene's seeded <see cref="float"/> points mapped
/// from files, drawn as points (Draw is Points); a view of a tenth of the square is filled with its pages, then panned by a
/// twentieth of the view a frame for 120 frames, first with the index's copy in the page cache and then taken out of it
/// (<see cref="PageCache"/>); then the pool is disposed while pages are read. "m4" adds 16 line series, so that the frames
/// extract M4 while the workers read. "cells": a still view whose pages are all on the GPU, recorded every frame, for the
/// time and the bytes of the table of cells. "cap": seeded double points in clusters with halos, whose resident cells are
/// past 131,072, drawn at deep zooms into a cluster, with the limit or without.
/// </summary>
internal static class ReadScene
{
    // Two seconds at the frame period before a frame counts (.cursor/rules/bench.mdc).
    private const int Warmup = 120;

    // The frames of a pan (Issue #472, sweep 4: a twentieth of the view a frame for 120 frames).
    private const int PanFrames = 120;

    // The pages read on the scene's thread to time a page's read, warm and then cold.
    private const int Probes = 32;

    private static readonly TimeSpan Period = TimeSpan.FromMilliseconds(1000.0 / 60);
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(10);
    private static readonly PanelId Panel = new(0, 0);

    /// <summary>Runs a scene and prints its figures; returns the exit code.</summary>
    /// <param name="args">"fill", "m4", or "cells" and the folder of the data files, the count, and "uniform" or "cluster"; or "cap", the folder, the count, and "capped" or "free".</param>
    public static int Run(ReadOnlySpan<string> args)
    {
        const string Usage = "reads <fill|m4|cells> <dir> <count> <uniform|cluster> | reads cap <dir> <count> <capped|free>";
        if (args.Length < 4)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var mode = args[0];
        var directory = args[1];
        var count = (long)double.Parse(args[2], CultureInfo.InvariantCulture);
        _ = Directory.CreateDirectory(directory);
        _ = PointsScene.TimeBeginPeriod(1);
        try
        {
            return mode switch
            {
                "fill" or "m4" or "cells" when args[3] is "uniform" or "cluster" => Reads(
                    mode,
                    directory,
                    count,
                    args[3] == "uniform" ? "uniform" : "clustered"
                ),
                "cap" when args[3] is "capped" or "free" => Cap(directory, count, args[3] == "capped"),
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

    /// <summary>The scenes over the density scene's data: "fill", "m4", and "cells".</summary>
    private static int Reads(string mode, string directory, long count, string distribution)
    {
        var made = Stopwatch.StartNew();
        var (xPath, yPath, written) = DensityScene.Ensure(directory, count, distribution);
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"files: {xPath} and {yPath}, seed {DensityScene.Seed}, {(written ? $"written now in {made.Elapsed.TotalSeconds:F1} s" : "kept")}"
            )
        );
        using var x = Column.MapFile<float>(xPath);
        using var y = Column.MapFile<float>(yPath);
        var (lo, hi) = distribution == "uniform" ? (0.0, PointFiles.Side) : (-PointFiles.OutlierReach, PointFiles.OutlierReach);
        var builder = DataTable.Create().Add("x", x).Add("y", y);

        // "m4": 16 line series over the points' y and an x of one step a row across the same range, so that the frames
        // extract M4 for 16 series (one pyramid, over the mapped y) while the workers read pages.
        const int Series = 16;
        if (mode == "m4")
        {
            builder = builder.Add("t", Column.Uniform(lo, (hi - lo) / count, count));
        }

        var table = builder.Build();
        var plotBuilder = Plot.Create(table)
            .Point(table.Col<float>("x"), table.Col<float>("y"), p => p.Draw(PointDraw.Points))
            .ScaleX(Scale.Linear().WithDomain(lo, hi))
            .ScaleY(Scale.Linear().WithDomain(lo, hi));
        for (var s = 0; s < (mode == "m4" ? Series : 0); s++)
        {
            plotBuilder = plotBuilder.Line(table.Col<double>("t"), table.Col<float>("y"));
        }

        var plot = plotBuilder.Build();
        using var inner = D3D11Backend.Create();

        // The hardware's backend, which leaves the drawing out when asked: the frames of "cells" record and do not draw.
        var backend = new PointsScene.CountingBackend(inner);
        var pool = new PlotWorkerPool(Math.Max(1, Environment.ProcessorCount - 1));
        try
        {
            _ = DensityScene.Warm(inner);
            using var engine = new PlotEngine(backend, 1920, 1080, 1, pool);
            var side = (hi - lo) / 10;

            // The first view: a tenth of the square at the low corner of the uniform square (or of the outliers' square).
            var first = Square(lo, 0, 0, side);
            var shown = Stopwatch.StartNew();
            engine.SetPlot(plot);
            _ = Frame(engine, null);
            engine.Views.SetView(Panel, first);
            if (!Settle(engine, out var frames, out _))
            {
                Console.Error.WriteLine("first display: still provisional at the deadline");
                return 1;
            }

            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"first display: {shown.Elapsed.TotalSeconds:F1} s, {frames} frames; pages {backend.Images.Points.PagesInUse}"
                )
            );
            PointsScene.PrintCells(engine.Plan!);
            var part = (IndexedPointPlan)engine.Plan!.Layers[0];
            var snapshot = part.SpatialIndex!.Acquire();
            try
            {
                if (mode == "cells")
                {
                    Cells(engine, backend, distribution == "uniform" ? Square(lo, 4, 4, side) : Square(lo, 1, 8, side), snapshot);
                    return 0;
                }

                var copies = PageCache.Copies();
                Console.WriteLine(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"copy: {copies.Count} mapped regions of {copies.Sum(c => c.Size)} bytes ({string.Join(", ", copies.Select(c => c.Name).Distinct())})"
                    )
                );
                var (bytes, seconds) = PageCache.Touch(copies);
                Console.WriteLine(
                    string.Create(CultureInfo.InvariantCulture, $"touch: {bytes} bytes in {seconds:F2} s, {bytes / seconds / 1e9:F2} GB/s")
                );

                // A read before the probes, so that neither times a first call.
                _ = Probe(snapshot, lo, side, 7, "first");
                var warm = Probe(snapshot, lo, side, 8, "warm");

                Fill(engine, backend, Square(lo, 2, 0, side), "warm", side);

                PageCache.Evict(copies, keepFree: 256L << 20);
                var cold = Probe(snapshot, lo, side, 9, "cold");
                Console.WriteLine(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"cold over warm (p50 of a page's read): {cold / warm:F2}; the cold frames count: {cold / warm >= 1.5} (the bound is 1.5)"
                    )
                );
                Fill(engine, backend, Square(lo, 4, 0, side), "cold", side);
                if (mode == "m4")
                {
                    Still(engine, backend, side);
                }

                Dispose(engine, backend, pool, Square(lo, 6, 0, side));
                return 0;
            }
            finally
            {
                snapshot.Release();
            }
        }
        finally
        {
            pool.Dispose();
        }
    }

    // A square of a tenth of the side, the column and the row of which are counted in tenths from the low corner.
    private static PanelView2D Square(double lo, int column, int row, double side) =>
        new(ViewRange.FromValues(lo + (column * side), lo + ((column + 1) * side)), ViewRange.FromValues(lo + (row * side), lo + ((row + 1) * side)));

    /// <summary>Draws a frame a period until one is not provisional; false past the deadline.</summary>
    private static bool Settle(PlotEngine engine, out int frames, out List<FrameRow> rows)
    {
        frames = 0;
        rows = [];
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < Deadline)
        {
            var row = Frame(engine, null);
            rows.Add(row);
            frames++;
            if (!engine.IsProvisional)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Draws one frame, at a view if one is given, and paces it to the period; returns its figures.</summary>
    private static FrameRow Frame(PlotEngine engine, PanelView2D? view)
    {
        var cache = engine.Plan?.Images;
        var reads = cache?.Points.ReadsInUse ?? 0;
        var start = Stopwatch.GetTimestamp();
        if (view is { } next)
        {
            engine.Views.SetView(Panel, next);
        }

        var bytes = GC.GetAllocatedBytesForCurrentThread();
        engine.RenderFrame();
        bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        var cpu = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var plan = engine.Plan!;
        var row = new FrameRow(
            cpu,
            plan.ExtractTicks * 1000.0 / Stopwatch.Frequency,
            plan.ScanTicks * 1000.0 / Stopwatch.Frequency,
            reads,
            plan.Images.Points.ScheduledThisFrame,
            engine.IsProvisional,
            bytes,
            plan.Images.Points.PagesInUse
        );
        var left = Period - Stopwatch.GetElapsedTime(start);
        if (left > TimeSpan.Zero)
        {
            Thread.Sleep(left);
        }

        return row;
    }

    /// <summary>Fills a view with its pages and pans from it, printing the frames' CPU times (design §9 "読み出し").</summary>
    private static void Fill(PlotEngine engine, PointsScene.CountingBackend backend, PanelView2D view, string state, double side)
    {
        var load = MachineLoad.Now();
        var clock = Stopwatch.StartNew();
        engine.Views.SetView(Panel, view);
        var settled = Settle(engine, out var frames, out var fill);
        var seconds = clock.Elapsed.TotalSeconds;
        Print($"fill {state}", fill, settled ? null : "still provisional at the deadline");
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"fill {state}: {seconds:F2} s, {frames} frames, {fill.Sum(r => r.Sent)} pages sent; pages on the GPU {backend.Images.Points.PagesInUse}"
            )
        );
        var pan = new List<FrameRow>();
        for (var f = 1; f <= PanFrames; f++)
        {
            var shift = side * f / 20;
            pan.Add(
                Frame(
                    engine,
                    new PanelView2D(
                        ViewRange.FromValues(view.X.LowValue, view.X.HighValue),
                        ViewRange.FromValues(view.Y.LowValue + shift, view.Y.HighValue + shift)
                    )
                )
            );
        }

        Print($"pan {state}", pan, null);
        var (others, own) = MachineLoad.CoresSince(load);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{state}: other processes' cores {others:F2}, this process's {own:F2}"));
    }

    /// <summary>
    /// After the cold pan: the last pan's view until it has its pages, then 120 frames between it and a view a hundredth of
    /// the side away, which read no page: the M4 extraction without reads (design §9 "読み出し").
    /// </summary>
    private static void Still(PlotEngine engine, PointsScene.CountingBackend backend, double side)
    {
        _ = Settle(engine, out _, out _);
        var view = engine.CurrentView(Panel) is PanelView2D shown ? shown : throw new InvalidOperationException("No 2D view.");
        var near = new PanelView2D(
            ViewRange.FromValues(view.X.LowValue, view.X.HighValue),
            ViewRange.FromValues(view.Y.LowValue - (side / 100), view.Y.HighValue - (side / 100))
        );
        var rows = new List<FrameRow>();
        for (var f = 0; f < PanFrames; f++)
        {
            rows.Add(Frame(engine, f % 2 == 0 ? near : view));
        }

        Print("no reads", rows, null);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"no reads: pages on the GPU {backend.Images.Points.PagesInUse}"));
    }

    /// <summary>Disposes the pool on the frame after one that asked for pages, and prints how long the disposal took (invariant 7).</summary>
    private static void Dispose(PlotEngine engine, PointsScene.CountingBackend backend, PlotWorkerPool pool, PanelView2D view)
    {
        engine.Views.SetView(Panel, view);
        var pages = backend.Images.Points;
        for (var f = 0; f < 600; f++)
        {
            _ = Frame(engine, null);
            if (pages.ReadsInUse > 0)
            {
                break;
            }
        }

        var reads = pages.ReadsInUse;
        var start = Stopwatch.GetTimestamp();
        pool.Dispose();
        var took = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"pool dispose with {reads} reads in use: {took:F3} ms"));
    }

    /// <summary>
    /// Reads pages of a band of the copy on this thread, one at a time, and returns the p50 of a page's read in ms (Issue
    /// #472's "1 ページの読み"): the pages that hold the band's cells, a tenth of the square wide at the given column.
    /// </summary>
    private static double Probe(SpatialSnapshot snapshot, double lo, double side, int column, string state)
    {
        var (low, high) = (lo + (column * side) - snapshot.XRange.Origin, lo + ((column + 1) * side) - snapshot.XRange.Origin);
        var pages = new SortedSet<long>();
        foreach (var cell in snapshot.ResidentCells)
        {
            if (cell.CornerX >= low && cell.CornerX + cell.ExtentX <= high)
            {
                _ = pages.Add(cell.First / PointPages.PagePoints);
            }
        }

        var chosen = pages.Where((_, i) => i % Math.Max(1, pages.Count / Probes) == 0).Take(Probes).ToArray();
        var buffer = new ResidentPoint[PointPages.PagePoints];
        var times = new double[chosen.Length];
        for (var i = 0; i < chosen.Length; i++)
        {
            var first = chosen[i] * PointPages.PagePoints;
            var length = (int)Math.Min(PointPages.PagePoints, snapshot.PointCount - first);
            var start = Stopwatch.GetTimestamp();
            snapshot.ReadBlock(first, buffer.AsSpan(0, length));
            times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        var p50 = PointsScene.Percentile(times, 0.5);
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"page reads {state} (column {column}, {times.Length} of {pages.Count} pages): p50={p50:F3} p95={PointsScene.Percentile(times, 0.95):F3} max={times.Max():F3} ms"
            )
        );
        return p50;
    }

    /// <summary>
    /// A still view whose pages are all on the GPU, recorded every frame and not drawn, so that a frame's CPU time is the
    /// recording's: the table of cells (design §9 "セルの表"). The table's own time is the difference from a build that
    /// writes no table (the scan's records and the recording's copy taken out), run beside this one.
    /// </summary>
    private static void Cells(PlotEngine engine, PointsScene.CountingBackend backend, PanelView2D view, SpatialSnapshot snapshot)
    {
        engine.Views.SetView(Panel, view);
        if (!Settle(engine, out var frames, out _))
        {
            Console.WriteLine("cells: the view's pages do not fit; still provisional");
            return;
        }

        // Two views a millionth of the side apart: every frame records, with the same pages.
        var nudge = (view.X.HighValue - view.X.LowValue) * 1e-6;
        var other = new PanelView2D(
            ViewRange.FromValues(view.X.LowValue + nudge, view.X.HighValue + nudge),
            ViewRange.FromValues(view.Y.LowValue, view.Y.HighValue)
        );
        var load = MachineLoad.Now();
        var rows = new List<FrameRow>();
        backend.Skips = true;
        for (var f = 0; f < Warmup + 600; f++)
        {
            var row = Frame(engine, f % 2 == 0 ? other : view);
            if (f >= Warmup)
            {
                rows.Add(row);
            }
        }

        backend.Skips = false;

        var (others, own) = MachineLoad.CoresSince(load);
        var cells = snapshot.ResidentCells.Length;
        Print("cells", rows, null);
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"cells: {cells} resident cells, a table of {(long)cells * 24} bytes a frame; the list's records {engine.Plan!.List.ResidentCells.Length}; pages {backend.Images.Points.PagesInUse} after {frames} frames to fill; provisional frames {rows.Count(r => r.Provisional)}; other processes' cores {others:F2}, this process's {own:F2}"
            )
        );
    }

    /// <summary>Prints the CPU time, the scan, the extraction, the reads, and the pages sent of some frames.</summary>
    private static void Print(string name, List<FrameRow> rows, string? note)
    {
        if (rows.Count == 0)
        {
            Console.WriteLine($"{name}: no frame");
            return;
        }

        var cpu = rows.Select(r => r.Cpu).ToArray();
        var reading = rows.Where(r => r.Reads > 0 && r.Extract > 0).Select(r => r.Extract).ToArray();
        var idle = rows.Where(r => r.Reads == 0 && r.Extract > 0).Select(r => r.Extract).ToArray();
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{name}: frames={rows.Count} cpu p50={PointsScene.Percentile(cpu, 0.5):F3} p95={PointsScene.Percentile(cpu, 0.95):F3} max={cpu.Max():F3} ms; scan p50={PointsScene.Percentile([.. rows.Select(r => r.Scan)], 0.5):F4} ms; pages sent {rows.Sum(r => r.Sent)}, at most {rows.Max(r => r.Sent)} a frame; frames that began with reads {rows.Count(r => r.Reads > 0)}; extract with reads n={reading.Length} p50={Stat(reading, 0.5)} p95={Stat(reading, 0.95)} max={(reading.Length == 0 ? "-" : reading.Max().ToString("F3", CultureInfo.InvariantCulture))}, without n={idle.Length} p50={Stat(idle, 0.5)} p95={Stat(idle, 0.95)} max={(idle.Length == 0 ? "-" : idle.Max().ToString("F3", CultureInfo.InvariantCulture))} ms; frames over 16.7 ms {cpu.Count(c => c > 16.7)}; allocated {rows.Sum(r => r.Allocated)} bytes in {rows.Count(r => r.Allocated > 0)} frames ({rows.Count(r => r.Allocated > 0 && r.Allocated % 568 == 0)} of them multiples of 568, a new tick label's), {rows.Where(r => r.Sent > 0 || r.Reads > 0).Sum(r => r.Allocated)} bytes in the frames that read or sent{(note is null ? string.Empty : "; " + note)}"
            )
        );

        // The frames that allocated, past a new tick label's 568 bytes: their bytes and the pages on the GPU after them.
        foreach (var row in rows.Where(r => r.Allocated > 0 && r.Allocated % 568 != 0))
        {
            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {name} allocated {row.Allocated} bytes; pages on the GPU {row.Pages}, sent {row.Sent}"
                )
            );
        }

        static string Stat(double[] values, double share) =>
            values.Length == 0 ? "-" : PointsScene.Percentile(values, share).ToString("F3", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The limit of cells (design §9 "セルの上限"): seeded double points in clusters with halos, past 131,072 resident cells,
    /// with the limit or without it; at three deep zooms into a cluster, the points' offsets from where their values map in
    /// double, the points drawn against those in the view, and the GPU time.
    /// </summary>
    private static int Cap(string directory, long count, bool capped)
    {
        var (xPath, yPath) = HaloFiles(directory, count, out var center);
        using var x = Column.MapFile<double>(xPath);
        using var y = Column.MapFile<double>(yPath);
        var table = DataTable.Create().Add("x", x).Add("y", y).Build();
        var plot = Plot.Create(table)
            .Point(table.Col<double>("x"), table.Col<double>("y"), p => p.Draw(PointDraw.Points))
            .ScaleX(Scale.Linear().WithDomain(0, 1))
            .ScaleY(Scale.Linear().WithDomain(0, 1))
            .Build();
        using var backend = D3D11Backend.Create();
        _ = DensityScene.Warm(backend);
        using var engine = new PlotEngine(backend, 1920, 1080);
        engine.SetPlot(plot);
        if (!capped)
        {
            engine.Plan!.CellLimit = int.MaxValue;
        }

        _ = Frame(engine, null);

        // Three zooms into the first cluster's core, and one onto the halo's point at 2^-21 from it, a cell of one point
        // without the limit and the core's cell with it.
        var halo = (center.X + Math.ScaleB(1.0, -21), center.Y);
        (string Name, (double X, double Y) At, double Width)[] views =
        [
            ("core", center, 1e-6),
            ("core", center, 1e-9),
            ("core", center, 1e-12),
            ("halo", halo, 1e-8),
        ];
        engine.Views.SetView(Panel, Around(center, views[0].Width));
        var clock = Stopwatch.StartNew();
        while (engine.Plan?.Layers.Count is null or 0 || ((IndexedPointPlan)engine.Plan.Layers[0]).SpatialIndex is not { IsComplete: true })
        {
            if (clock.Elapsed > Deadline)
            {
                Console.Error.WriteLine("cap: the index was not built by the deadline");
                return 1;
            }

            _ = Frame(engine, null);
        }

        Console.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"cap {(capped ? "capped" : "free")}: index built in {clock.Elapsed.TotalSeconds:F1} s")
        );
        PointsScene.PrintCells(engine.Plan!);
        var part = (IndexedPointPlan)engine.Plan!.Layers[0];
        var snapshot = part.SpatialIndex!.Acquire();

        // The wall clock of the measured views, to match the GPU clock log of nvidia-smi to them.
        var from = DateTime.Now;
        try
        {
            foreach (var (name, at, width) in views)
            {
                var view = Around(at, width);
                engine.Views.SetView(Panel, view);
                _ = Settle(engine, out _, out _);
                var rendered = backend.LastFrameTime?.Frame ?? 0;
                var gpu = new List<double>();
                var nudge = width * 1e-6;
                var other = Around((at.X + nudge, at.Y), width);
                for (var f = 0; f < Warmup + 600; f++)
                {
                    _ = Frame(engine, f % 2 == 0 ? other : view);
                    if (f >= Warmup && backend.LastFrameTime is { } time && time.Frame > rendered)
                    {
                        gpu.Add(time.Duration.TotalMilliseconds);
                        rendered = time.Frame;
                    }
                }

                engine.Views.SetView(Panel, view);
                _ = Frame(engine, null);
                Offsets(engine, snapshot, view, name, width, gpu);
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"frames ran {from:HH:mm:ss.fff} to {DateTime.Now:HH:mm:ss.fff}"));
        }
        finally
        {
            snapshot.Release();
        }

        return 0;
    }

    private static PanelView2D Around((double X, double Y) center, double width) =>
        new(
            ViewRange.FromValues(center.X - (width / 2), center.X + (width / 2)),
            ViewRange.FromValues(center.Y - (width * 0.28125), center.Y + (width * 0.28125))
        );

    /// <summary>
    /// Prints, for the last frame, the points the layer drew against the points whose values lie in the view, and how far
    /// the vertex shader's place of each point in the view is from where its value maps in double, in physical pixels.
    /// </summary>
    private static void Offsets(PlotEngine engine, SpatialSnapshot snapshot, PanelView2D view, string name, double width, List<double> gpu)
    {
        var list = engine.Plan!.List;
        var runs = list.ResidentRuns;
        long drawn = 0;
        foreach (var run in runs)
        {
            drawn += run.Count;
        }

        // The view's pixels from two corners: a pixel of a value is low + (value − low end) × scale in double.
        var (x0, x1) = (view.X.LowValue, view.X.HighValue);
        var (y0, y1) = (view.Y.LowValue, view.Y.HighValue);
        var low = engine.ViewToPixel(Panel, ViewRange.FromValues(x0, x0), ViewRange.FromValues(y0, y0));
        var high = engine.ViewToPixel(Panel, ViewRange.FromValues(x1, x1), ViewRange.FromValues(y1, y1));
        var (scaleX, scaleY) = ((high.X - low.X) / (x1 - x0), (high.Y - low.Y) / (y1 - y0));
        var cells = snapshot.ResidentCells;
        var records = list.ResidentCells;
        var offset = runs.Length > 0 ? runs[0].Cells : 0;
        var errors = new List<double>();
        var (originX, originY) = (snapshot.XRange.Origin, snapshot.YRange.Origin);
        long inside = 0;
        var xs = new double[4096];
        var ys = new double[4096];
        var rows = new long[4096];
        var points = new ResidentPoint[4096];
        for (var c = 0; c < cells.Length; c++)
        {
            var cell = cells[c];
            if (
                cell.CornerX > x1 - originX
                || cell.CornerX + cell.ExtentX < x0 - originX
                || cell.CornerY > y1 - originY
                || cell.CornerY + cell.ExtentY < y0 - originY
            )
            {
                continue;
            }

            var record = records[offset + c];
            for (long first = cell.First; first < cell.First + cell.Count; first += xs.Length)
            {
                var length = (int)Math.Min(xs.Length, cell.First + cell.Count - first);
                snapshot.ReadPoints(first, xs.AsSpan(0, length), ys.AsSpan(0, length), rows.AsSpan(0, length));
                snapshot.ReadBlock(first, points.AsSpan(0, length));
                for (var i = 0; i < length; i++)
                {
                    var (vx, vy) = (originX + xs[i], originY + ys[i]);
                    if (vx < x0 || vx > x1 || vy < y0 || vy > y1)
                    {
                        continue;
                    }

                    inside++;
                    var exactX = low.X + ((vx - x0) * scaleX);
                    var exactY = low.Y + ((vy - y0) * scaleY);

                    // The vertex shader's place (Markers.hlsl): the base pixel and the difference of units in integers, then float.
                    var drawnX = record.PixelX + (Units(points[i].X, record.BaseX) * record.ScaleX);
                    var drawnY = record.PixelY + (Units(points[i].Y, record.BaseY) * record.ScaleY);
                    errors.Add(Math.Max(Math.Abs(drawnX - exactX), Math.Abs(drawnY - exactY)));
                }
            }
        }

        var sorted = errors.Order().ToArray();
        var gpuTimes = gpu.ToArray();
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"zoom {name} width {width:E0}: drawn {drawn} points, {inside} in the view, ratio {(inside == 0 ? double.NaN : (double)drawn / inside):F2}; offset p50={Percent(sorted, 0.5):F4} p99={Percent(sorted, 0.99):F4} max={(sorted.Length == 0 ? double.NaN : sorted[^1]):F4} px; gpu frames={gpuTimes.Length} p50={PointsScene.Percentile(gpuTimes, 0.5):F3} p95={PointsScene.Percentile(gpuTimes, 0.95):F3} max={(gpuTimes.Length == 0 ? double.NaN : gpuTimes.Max()):F3} ms; runs {runs.Length}"
            )
        );

        static double Percent(double[] sorted, double share) =>
            sorted.Length == 0 ? double.NaN : sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * share))];

        static float Units(uint position, uint origin) => position >= origin ? (float)(position - origin) : -(float)(origin - position);
    }

    /// <summary>
    /// Returns the x and y files of the clusters with halos, writing them when missing: clusters of 33,000 points within
    /// 1e-12 of a center and 3 points at each of 24 distances halving from 2^-6, so that every level of the table down to
    /// the core splits a row into three cells of a point and one that goes on; the center of the first cluster is near
    /// (0.0101, 0.0103).
    /// </summary>
    private static (string X, string Y) HaloFiles(string directory, long count, out (double X, double Y) center)
    {
        const int Core = 33_000;
        const int Levels = 24;
        const int PerCluster = Core + (3 * Levels);
        var clusters = (int)(count / PerCluster);
        var prefix = Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"halo-{count}"));
        var (xPath, yPath) = PointFiles.Paths(prefix);
        var random = new Random(5);
        var centers = new (double X, double Y)[clusters];
        for (var c = 0; c < clusters; c++)
        {
            centers[c] = c == 0 ? (0.0101, 0.0103) : (0.01 + (0.98 * random.NextDouble()), 0.01 + (0.98 * random.NextDouble()));
        }

        center = centers[0];
        var bytes = (long)clusters * PerCluster * sizeof(double);
        if (File.Exists(xPath) && File.Exists(yPath) && new FileInfo(xPath).Length == bytes && new FileInfo(yPath).Length == bytes)
        {
            return (xPath, yPath);
        }

        using var xs = new BufferedStream(File.Create(xPath), 1 << 20);
        using var ys = new BufferedStream(File.Create(yPath), 1 << 20);
        Span<byte> value = stackalloc byte[8];
        foreach (var (cx, cy) in centers)
        {
            for (var i = 0; i < Core; i++)
            {
                Write(xs, cx + (1e-12 * ((2 * random.NextDouble()) - 1)), value);
                Write(ys, cy + (1e-12 * ((2 * random.NextDouble()) - 1)), value);
            }

            for (var level = 0; level < Levels; level++)
            {
                var d = Math.ScaleB(1.0, -(6 + level));
                foreach (var (dx, dy) in new[] { (d, 0.0), (0.0, d), (d, d) })
                {
                    Write(xs, cx + dx, value);
                    Write(ys, cy + dy, value);
                }
            }
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"halo files written: {clusters} clusters of {PerCluster} points, seed 5"));
        return (xPath, yPath);

        static void Write(Stream stream, double v, Span<byte> buffer)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(buffer, v);
            stream.Write(buffer);
        }
    }

    /// <summary>A frame's figures: its CPU time, its extraction, its scan, the reads in use when it began, the pages it sent, and whether it was provisional.</summary>
    private readonly record struct FrameRow(
        double Cpu,
        double Extract,
        double Scan,
        int Reads,
        int Sent,
        bool Provisional,
        long Allocated,
        int Pages
    );
}
