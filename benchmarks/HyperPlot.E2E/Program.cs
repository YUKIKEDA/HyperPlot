using System.Diagnostics;
using System.Globalization;
using HyperPlot;
using HyperPlot.Rendering;
using HyperPlot.Rendering.D3D11;
using HyperPlot.Samples;

// End-to-end frames of 1e9-point series (design §9, row S2): a script of view ranges for zoom and pan, each frame
// extracted across the pool, mapped to pixels, recorded, and drawn headless at 1,920 × 1,080. It prints the CPU time of
// each stage, the GPU time (IGpuBackend.LastFrameTime), the managed allocations a frame, and the memory.
//   dotnet publish benchmarks/HyperPlot.E2E -c Release -r win-x64 -o <dir>
//   <dir>/HyperPlot.E2E.exe --dir <folder for the data files> [--series 1] [--files 4] [--rounds 2] [--points 1000000000]
//     [--csv <file for one row a frame>] [--min-span <points: keep only the views at least this wide, for a profile>]
// The data files (4 GB each at 1e9 points) are made from a seed when missing and kept; delete them after.
string? directory = null;
string? csv = null;
var minSpan = 0.0;
long points = 1_000_000_000;
var seriesCount = 1;
var fileCount = 4;
var rounds = 2;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--dir":
            directory = args[++i];
            break;
        case "--points":
            points = long.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--series":
            seriesCount = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--files":
            fileCount = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--min-span":
            minSpan = double.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--csv":
            csv = args[++i];
            break;
        case "--rounds":
            rounds = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        default:
            Console.Error.WriteLine($"Unknown option {args[i]}.");
            return 2;
    }
}

if (directory is null)
{
    Console.Error.WriteLine("Name the folder for the data files with --dir (each file holds 4 bytes a point).");
    return 2;
}

using var process = Process.GetCurrentProcess();
process.PriorityClass = ProcessPriorityClass.High;
Directory.CreateDirectory(directory);

// Series beyond the number of files map the same files again, each with a pyramid of its own.
var files = Math.Min(fileCount, seriesCount);
var pool = PlotWorkerPool.Shared;
var columns = new Column<float>[seriesCount];
var pyramids = new MinMaxPyramid<float>[seriesCount];
for (var s = 0; s < seriesCount; s++)
{
    _ = WalkFile.Ensure(directory, s % files, points, out var path);
    columns[s] = Column.MapFile<float>(path);
    pyramids[s] = pool.BuildPyramid(columns[s]);
}

foreach (var pyramid in pyramids)
{
    pyramid.Completion.Wait();
}

var area = new PlotArea(left: 40, top: 30, width: 1840, height: 1020);
var x = Column.Uniform(0, 1, points);
var batch = new M4Batch();
var outputs = new M4Point[seriesCount][];
for (var s = 0; s < seriesCount; s++)
{
    outputs[s] = new M4Point[M4.MaxPointCount(area.Width)];
    _ = batch.Add(x, pyramids[s], outputs[s]);
}

using var backend = D3D11Backend.Create();
using var target = backend.CreateRenderTarget(new RenderTargetDescription(1920, 1080));
var list = new RenderList();
var boundaries = new double[area.Width + 1];
var ranges = new MinMax<float>[seriesCount];
var views = Array.FindAll(Script(points), v => v.To - v.From >= minSpan);
var frames = views.Length * rounds;
var extract = new double[frames];
var range = new double[frames];
var record = new double[frames];
var draw = new double[frames];
var total = new double[frames];
var allocated = new long[frames];
var gpu = new List<double>(frames);
long lastTimed = -1;
Color[] colors = [new(31, 119, 180, 255), new(255, 127, 14, 255), new(44, 160, 44, 255), new(214, 39, 40, 255)];

// The wall clock of the frames, to match the GPU clock log of nvidia-smi to them.
var framesFrom = DateTime.Now;
for (var f = 0; f < frames; f++)
{
    var (from, to) = views[f % views.Length];
    var bytes = GC.GetAllocatedBytesForCurrentThread();
    var start = Stopwatch.GetTimestamp();

    for (var k = 0; k <= area.Width; k++)
    {
        boundaries[k] = from + ((to - from) * k / area.Width);
    }

    pool.Extract(batch, default, boundaries);
    var t1 = Stopwatch.GetTimestamp();

    // Each series over its own Y range of the visible elements, as BillionSeries draws it (design §3.3).
    var first = (long)Math.Ceiling(from);
    var last = (long)Math.Floor(to);
    for (var s = 0; s < seriesCount; s++)
    {
        ranges[s] = pyramids[s].GetMinMax(first, last - first + 1);
    }

    var t2 = Stopwatch.GetTimestamp();
    list.Clear();
    area.DrawGrid(list, step: 60);
    for (var s = 0; s < seriesCount; s++)
    {
        var drawn = outputs[s].AsSpan(0, batch.GetResult(s).Count);
        area.DrawSeries(list, drawn, from, to, ranges[s].Min, ranges[s].Max, new LineStyle(colors[s % colors.Length], 1.5f));
    }

    var t3 = Stopwatch.GetTimestamp();
    using (var frame = backend.BeginFrame())
    {
        target.Clear(new Color(255, 255, 255, 255));
        backend.Draw(target, list);
    }

    var t4 = Stopwatch.GetTimestamp();
    extract[f] = Stopwatch.GetElapsedTime(start, t1).TotalMilliseconds;
    range[f] = Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds;
    record[f] = Stopwatch.GetElapsedTime(t2, t3).TotalMilliseconds;
    draw[f] = Stopwatch.GetElapsedTime(t3, t4).TotalMilliseconds;
    total[f] = Stopwatch.GetElapsedTime(start, t4).TotalMilliseconds;
    allocated[f] = GC.GetAllocatedBytesForCurrentThread() - bytes;

    // One GPU time a frame at most; a time arrives some frames after its frame (design §5.2).
    if (backend.LastFrameTime is { } time && time.Frame > lastTimed)
    {
        lastTimed = time.Frame;
        gpu.Add(time.Duration.TotalMilliseconds);
    }
}

var framesTo = DateTime.Now;
if (csv is not null)
{
    using var writer = new StreamWriter(csv);
    writer.WriteLine("frame,span,extract_ms,range_ms,record_ms,draw_ms,total_ms,allocated");
    for (var f = 0; f < frames; f++)
    {
        var (from, to) = views[f % views.Length];
        writer.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{f},{to - from:R},{extract[f]:F4},{range[f]:F4},{record[f]:F4},{draw[f]:F4},{total[f]:F4},{allocated[f]}"
            )
        );
    }
}

const int Warmup = 30;
var measured = frames - Warmup;
Console.WriteLine(
    string.Create(
        CultureInfo.InvariantCulture,
        $"series={seriesCount} points={points} frames={measured} (after {Warmup}) views={views.Length} rounds={rounds}"
    )
);
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"frames ran {framesFrom:HH:mm:ss.fff} to {framesTo:HH:mm:ss.fff}"));
Print("extract", extract);
Print("range", range);
Print("record", record);
Print("draw", draw);
Print("cpu_total", total);
var gpuMeasured = gpu.Skip(Math.Min(Warmup, gpu.Count / 2)).ToArray();
Console.WriteLine(
    string.Create(
        CultureInfo.InvariantCulture,
        $"gpu_total: frames={gpuMeasured.Length} p50={Percentile(gpuMeasured, 0.5):F3} p95={Percentile(gpuMeasured, 0.95):F3} max={gpuMeasured.Max():F3} ms"
    )
);
Console.WriteLine(
    string.Create(CultureInfo.InvariantCulture, $"allocated after warm-up: {allocated.Skip(Warmup).Sum()} bytes over {measured} frames")
);
for (var f = Warmup; f < frames; f++)
{
    if (allocated[f] != 0)
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  frame {f}: {allocated[f]} bytes"));
    }
}

process.Refresh();
Console.WriteLine(
    string.Create(
        CultureInfo.InvariantCulture,
        $"memory: working set {process.WorkingSet64 / 1048576.0:F0} MB, private {process.PrivateMemorySize64 / 1048576.0:F0} MB"
    )
);

foreach (var pyramid in pyramids)
{
    pyramid.Dispose();
}

foreach (var column in columns)
{
    column.Dispose();
}

return 0;

void Print(string name, double[] values)
{
    var kept = values.Skip(Warmup).ToArray();
    Console.WriteLine(
        string.Create(CultureInfo.InvariantCulture, $"{name}: p50={Percentile(kept, 0.5):F3} p95={Percentile(kept, 0.95):F3} max={kept.Max():F3} ms")
    );
}

static double Percentile(double[] values, double share)
{
    var sorted = values.Order().ToArray();
    return sorted.Length == 0 ? double.NaN : sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * share))];
}

// Zoom from the whole series into its middle, 2^(1/8) a frame, to a millionth (1,000 points at 1e9); pan at each power
// of 2 by a tenth of the span, 10 frames right; then zoom back out the same way (design §9).
static (double From, double To)[] Script(long points)
{
    var views = new List<(double, double)>();
    var whole = points - 1.0;
    var center = whole / 2;
    const int Steps = 160;
    for (var step = 0; step <= Steps; step++)
    {
        var span = whole / Math.Pow(2, step / 8.0);
        views.Add((center - (span / 2), center + (span / 2)));
        if (step % 8 == 0)
        {
            for (var pan = 1; pan <= 10; pan++)
            {
                var shift = span * pan / 10 / 2;
                var from = Math.Min(Math.Max(0, center - (span / 2) + shift), whole - span);
                views.Add((from, from + span));
            }
        }
    }

    for (var step = Steps - 1; step >= 0; step--)
    {
        var span = whole / Math.Pow(2, step / 8.0);
        views.Add((center - (span / 2), center + (span / 2)));
    }

    return [.. views];
}
