using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HyperPlot.Interaction;
using HyperPlot.Rendering;
using HyperPlot.Rendering.D3D11;

namespace HyperPlot.E2E;

/// <summary>
/// Spike R3b2 (Issue #472, Decision O3 and O4): one point layer of the float files of
/// <c>HyperPlot.DataPath generate-points</c> (<c>&lt;prefix&gt;.x</c>, <c>&lt;prefix&gt;.y</c>, over [0, 10⁶]²) at
/// 1,920 × 1,080 on the hardware, a frame every 16.7 ms at the fastest, at High priority.
/// <list type="bullet">
/// <item><description>
/// <c>o4</c>: per frame, the resident cells, the time of the index path's <c>Record</c> (with and without the pages it
/// reads), and the bytes of the cells' records, over a zoom script and a still whole view.
/// </description></item>
/// <item><description>
/// <c>o3</c>: the copy is flushed and pushed out of the page cache (its pages put on the standby list, then memory taken
/// until little is available), and a still view and a pan are drawn while their pages fill, the frame's thread reading the
/// copy as on main; then the whole copy is read and the same is done on another band, warm. The cold run counts only
/// when its read time a page is at least 1.5 times the warm one (.cursor/rules/bench.mdc).
/// </description></item>
/// </list>
/// Set HP_PROBE_NO_RAW_LIMIT=1 (10⁹ points are past the raw limit) and the block switches of the sweep.
/// </summary>
internal static partial class PointsFileScene
{
    private const double Side = 1e6;
    private const double Span = 1e5;
    private const double FloorBytes = 2048.0 * 1048576;
    private static readonly TimeSpan Period = TimeSpan.FromMilliseconds(1000.0 / 60);

    /// <summary>Runs the scene and prints its figures; returns the exit code.</summary>
    /// <param name="args">The files' prefix, "o3" or "o4", and the diameter in DIPs (6 by default).</param>
    public static int Run(ReadOnlySpan<string> args)
    {
        if (args.Length < 2 || args[1] is not ("o3" or "o4"))
        {
            Console.Error.WriteLine("points-file <prefix> <o3|o4> [diameter]");
            return 2;
        }

        using (var self = Process.GetCurrentProcess())
        {
            self.PriorityClass = ProcessPriorityClass.High;
        }

        var prefix = args[0];
        var diameter = args.Length > 2 ? float.Parse(args[2], CultureInfo.InvariantCulture) : 6;
        using var x = Column.MapFile<float>(prefix + ".x");
        using var y = Column.MapFile<float>(prefix + ".y");
        var table = DataTable.Create().Add("x", x).Add("y", y).Build();
        var plot = Plot.Create(table)
            .Point(table.Col<float>("x"), table.Col<float>("y"), p => p.Size(diameter).Opacity(0.6))
            .ScaleX(Scale.Linear().WithDomain(0, Side))
            .ScaleY(Scale.Linear().WithDomain(0, Side))
            .Build();
        using var backend = D3D11Backend.Create();
        using var engine = new PlotEngine(backend, 1920, 1080);
        var panel = new PanelId(0, 0);

        // The index's build, then the first view's pages: V1, a square of a tenth of the side in the middle.
        var shown = Stopwatch.StartNew();
        engine.SetPlot(plot);
        engine.RenderFrame();
        engine.Views.SetView(panel, View(Side / 2, Side / 2, Span));
        engine.RenderFrame();
        var waited = 0;
        while (engine.IsProvisional)
        {
            Thread.Sleep(1);
            engine.RenderFrame();
            waited++;
        }

        var snapshot = engine.Plan!.Layers.OfType<IndexedPointPlan>().Single().ProbeSnapshot!;
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"points={x.Length} file={prefix} scene={args[1]} diameter={diameter}; {SplitProbe.Describe()}; first display {shown.Elapsed.TotalSeconds:F1} s over {waited} frames; resident cells {snapshot.ResidentCells.Length}; copy {(snapshot.ProbeCopy?.ByteCount ?? 0) / 1e9:F2} GB mapped={snapshot.ProbeCopy?.IsMapped}"
            )
        );

        _ = PointsScene.TimeBeginPeriod(1);
        try
        {
            // 2 seconds of frames before the measured ones (.cursor/rules/bench.mdc).
            for (var f = 0; f < 120; f++)
            {
                _ = Frame(engine, panel, View((Side / 2) + (Math.Sin(f / 20.0) * Span / 100), Side / 2, Span));
            }

            return args[1] == "o4" ? RunO4(engine, panel, snapshot) : RunO3(engine, panel, snapshot);
        }
        finally
        {
            _ = PointsScene.TimeEndPeriod(1);
        }
    }

    private static int RunO4(PlotEngine engine, PanelId panel, SpatialSnapshot snapshot)
    {
        // A zoom from the whole side to a tenth and out, around a drifting center, then the whole view still.
        var views = new List<(string Part, PanelView2D View)>();
        for (var f = 0; f < 300; f++)
        {
            var t = f / 300.0;
            var span = Side * Math.Pow(10, -Math.Sin(t * Math.PI));
            var (cx, cy) = ((Side / 2) + (0.2 * Side * Math.Sin(t * 2 * Math.PI)), (Side / 2) + (0.1 * Side * Math.Cos(t * 2 * Math.PI)));
            views.Add(("zoom", View(cx, cy, span)));
        }

        for (var f = 0; f < 300; f++)
        {
            views.Add(("whole", View((Side / 2) + (Math.Sin(f / 20.0) * Side / 100), Side / 2, Side * 1.02)));
        }

        var start = PointsScene.MachineTimes();
        var frames = views.Select(v => (v.Part, Sample: Frame(engine, panel, v.View))).ToArray();
        var end = PointsScene.MachineTimes();
        var recordBytes = Unsafe.SizeOf<ResidentCellRecord>();
        Console.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"o4: resident_cells={snapshot.ResidentCells.Length} record_bytes_per_cell={recordBytes}")
        );
        foreach (var part in new[] { "zoom", "whole" })
        {
            var kept = frames.Where(f => f.Part == part).Select(f => f.Sample).ToArray();
            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"o4 {part}: frames={kept.Length} cells_per_frame={kept.Average(s => s.Cells):F0} cell_bytes_per_frame={kept.Average(s => s.Cells) * recordBytes:F0}; record_ms {Stats(kept.Select(s => s.RecordMs))}; record_less_reads_ms {Stats(kept.Select(s => s.RecordMs - s.ReadMs))}; cpu_frame_ms {Stats(kept.Select(s => s.CpuMs))}; pages_read_per_frame={kept.Average(s => s.Pages):F2}; allocated={kept.Sum(s => s.Allocated)} B"
                )
            );
        }

        Console.WriteLine(PointsScene.MachineLine(start, end));
        return 0;
    }

    private static int RunO3(PlotEngine engine, PanelId panel, SpatialSnapshot snapshot)
    {
        if (snapshot.ProbeCopy is not { } copy || !copy.ProbeFlushAndTrim())
        {
            Console.Error.WriteLine("o3 needs a copy in a mapped file (more points than the index keeps in memory)");
            return 3;
        }

        // The copy's pages are on the standby list: memory taken until little is available reuses them.
        var evict = Stopwatch.StartNew();
        var (before, taken) = (Available(), Evict());
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"o3 evict: available {before / 1e9:F1} GB before, took {taken / 1e9:F1} GB to a floor of {FloorBytes / 1e9:F1} GB, available {Available() / 1e9:F1} GB after; {evict.Elapsed.TotalSeconds:F1} s"
            )
        );

        var start = PointsScene.MachineTimes();
        var cold = Phase(engine, panel, "cold", Side * 0.15);

        // The whole copy read in order: its rate shows how much of it was out of the page cache; then the warm band.
        var touch = Stopwatch.StartNew();
        var sum = copy.ProbeTouch();
        var seconds = touch.Elapsed.TotalSeconds;
        Console.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"o3 touch: {copy.ByteCount / 1e9:F2} GB in {seconds:F2} s, {copy.ByteCount / 1e9 / seconds:F2} GB/s (sum {sum})")
        );
        var warm = Phase(engine, panel, "warm", Side * 0.85);
        var end = PointsScene.MachineTimes();
        var ratio = cold / warm;
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"o3: read a page cold/warm = {ratio:F2}; the cold run {(ratio >= 1.5 ? "counts" : "does not count")} (bound 1.5)"
            )
        );
        Console.WriteLine(PointsScene.MachineLine(start, end));
        return 0;
    }

    // A still view until its pages are in (600 frames at most), then a pan of 120 frames, a twentieth of the span a frame,
    // along a band at height y. Returns the mean read time of a page.
    private static double Phase(PlotEngine engine, PanelId panel, string name, double y)
    {
        var still = new List<Sample>();
        var x0 = Side * 0.15;
        do
        {
            still.Add(Frame(engine, panel, View(x0, y, Span)));
        }
        while (engine.IsProvisional && still.Count < 600);

        var pan = new List<Sample>();
        for (var f = 1; f <= 120; f++)
        {
            pan.Add(Frame(engine, panel, View(x0 + (f * Span / 20), y, Span)));
        }

        var all = still.Concat(pan).ToArray();
        var pages = all.Sum(s => s.Pages);
        var perPage = all.Sum(s => s.ReadMs) / Math.Max(1, pages);
        foreach (var (part, samples) in new[] { ("still", still), ("pan", pan) })
        {
            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"o3 {name} {part}: frames={samples.Count} filled={(part == "still" ? !engine.IsProvisional || samples.Count < 600 : true)} cpu_frame_ms {Stats(samples.Select(s => s.CpuMs))}; frames_that_read={samples.Count(s => s.Pages > 0)} pages={samples.Sum(s => s.Pages)} read_ms_per_frame {Stats(samples.Where(s => s.Pages > 0).Select(s => s.ReadMs))}"
                )
            );
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"o3 {name}: read_ms_per_page={perPage:F3} over {pages} pages"));
        return perPage;
    }

    private static Sample Frame(PlotEngine engine, PanelId panel, PanelView2D view)
    {
        var (ticks, calls, cells, readTicks, pages) = (SplitProbe.RecordTicks, SplitProbe.RecordCalls, SplitProbe.RecordedCells, SplitProbe.PageReadTicks, SplitProbe.PagesRead);
        var bytes = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        engine.Views.SetView(panel, view);
        engine.RenderFrame();
        var cpu = Stopwatch.GetElapsedTime(start);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
        var sample = new Sample(
            cpu.TotalMilliseconds,
            (SplitProbe.RecordTicks - ticks) * 1e3 / Stopwatch.Frequency,
            (SplitProbe.PageReadTicks - readTicks) * 1e3 / Stopwatch.Frequency,
            SplitProbe.RecordedCells - cells,
            SplitProbe.PagesRead - pages,
            SplitProbe.RecordCalls - calls,
            allocated
        );
        var left = Period - cpu;
        if (left > TimeSpan.Zero)
        {
            Thread.Sleep(left);
        }

        return sample;
    }

    private static PanelView2D View(double cx, double cy, double span) =>
        new(ViewRange.FromValues(cx - (span / 2), cx + (span / 2)), ViewRange.FromValues(cy - (span / 2), cy + (span / 2)));

    private static string Stats(IEnumerable<double> values)
    {
        var kept = values.ToArray();
        return kept.Length == 0
            ? "none"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"p50={PointsScene.Percentile(kept, 0.5):F3} p95={PointsScene.Percentile(kept, 0.95):F3} max={kept.Max():F3}"
            );
    }

    // Takes memory in blocks of 256 MB, writing each page, until the available memory is at the floor or nothing more is
    // given; then gives it all back. Returns the bytes taken.
    private static unsafe long Evict()
    {
        const long Block = 256L << 20;
        var blocks = new List<nint>();
        long taken = 0;
        try
        {
            while (Available() > FloorBytes + Block)
            {
                byte* block;
                try
                {
                    block = (byte*)NativeMemory.Alloc((nuint)Block);
                }
                catch (OutOfMemoryException)
                {
                    break;
                }

                blocks.Add((nint)block);
                for (long at = 0; at < Block; at += 4096)
                {
                    block[at] = 1;
                }

                taken += Block;
            }
        }
        finally
        {
            foreach (var block in blocks)
            {
                NativeMemory.Free((void*)block);
            }
        }

        return taken;
    }

    private static double Available()
    {
        var status = new MemoryStatus { Length = (uint)Unsafe.SizeOf<MemoryStatus>() };
        _ = GlobalMemoryStatusEx(ref status);
        return status.AvailablePhysical;
    }

    [LibraryImport("kernel32.dll")]
    private static partial int GlobalMemoryStatusEx(ref MemoryStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    private readonly record struct Sample(double CpuMs, double RecordMs, double ReadMs, long Cells, long Pages, long Calls, long Allocated);
}
