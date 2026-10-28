using System.Diagnostics;
using HyperPlot;
using HyperPlot.Rendering;
using HyperPlot.Rendering.D3D11;
using HyperPlot.Samples;

// BillionSeries: series of 1e9 floats in memory-mapped files, their pyramids built on the worker pool, M4 across the pool,
// drawn headless into PNGs of the whole series and of a zoom into its middle (row S1).
//   BillionSeries --dir <folder for the data files> [--points 1000000000] [--series 2] [--warp]
// The data files (4 GB each at 1e9 points) are made from a seed when missing, and kept for the next run; delete them after.
string? directory = null;
long points = 1_000_000_000;
var seriesCount = 2;
var useWarp = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--dir":
            directory = args[++i];
            break;
        case "--points":
            points = long.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            break;
        case "--series":
            seriesCount = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            break;
        case "--warp":
            useWarp = true;
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

Directory.CreateDirectory(directory);
var pool = PlotWorkerPool.Shared;
var columns = new Column<float>[seriesCount];
var pyramids = new MinMaxPyramid<float>[seriesCount];
for (var s = 0; s < seriesCount; s++)
{
    var path = Path.Combine(directory, $"series{s}.f32");
    if (!File.Exists(path) || new FileInfo(path).Length != points * sizeof(float))
    {
        var made = Stopwatch.StartNew();
        Generate(path, points, seed: 0x48797065UL + (ulong)s);
        Console.WriteLine($"Made {path} in {made.Elapsed.TotalSeconds:F1} s.");
    }

    columns[s] = Column.MapFile<float>(path);
}

var build = Stopwatch.StartNew();
for (var s = 0; s < seriesCount; s++)
{
    pyramids[s] = pool.BuildPyramid(columns[s]);
}

foreach (var pyramid in pyramids)
{
    pyramid.Completion.Wait();
}

Console.WriteLine($"Built {seriesCount} pyramids of {points:N0} points in {build.Elapsed.TotalSeconds:F2} s.");

var area = new PlotArea(left: 40, top: 30, width: 1840, height: 1020);
var x = Column.Uniform(0, 1, points);
var batch = new M4Batch();
var outputs = new M4Point[seriesCount][];
for (var s = 0; s < seriesCount; s++)
{
    outputs[s] = new M4Point[M4.MaxPointCount(area.Width)];
    _ = batch.Add(x, pyramids[s], outputs[s]);
}

using var backend = D3D11Backend.Create(useWarp);
using var renderer = new HeadlessRenderer(backend, 1920, 1080);
var list = new RenderList();
Color[] colors = [new(31, 119, 180, 255), new(255, 127, 14, 255), new(44, 160, 44, 255), new(214, 39, 40, 255)];

// The whole series, then the middle millionth of it: 1,000 points at 1e9.
Draw("overview.png", 0, points - 1);
Draw("zoom.png", (points / 2.0) - (points / 2e6), (points / 2.0) + (points / 2e6));

foreach (var pyramid in pyramids)
{
    pyramid.Dispose();
}

foreach (var column in columns)
{
    column.Dispose();
}

return 0;

void Draw(string name, double from, double to)
{
    var boundaries = area.Boundaries(from, to);
    var extract = Stopwatch.StartNew();
    pool.Extract(batch, boundaries);
    var extractMs = extract.Elapsed.TotalMilliseconds;

    // Each series over its own Y range of the visible elements, from its pyramid (design §3.3): the random walks drift
    // apart by far more than they move inside a zoom.
    var first = (long)Math.Ceiling(from);
    var last = (long)Math.Floor(to);
    list.Clear();
    area.DrawGrid(list, step: 60);
    for (var s = 0; s < seriesCount; s++)
    {
        var range = pyramids[s].GetMinMax(first, last - first + 1);
        var drawn = outputs[s].AsSpan(0, batch.GetResult(s).Count);
        area.DrawSeries(list, drawn, from, to, range.Min, range.Max, new LineStyle(colors[s % colors.Length], 1.5f));
    }

    renderer.Clear(new Color(255, 255, 255, 255));
    renderer.Render(list);
    var path = Path.Combine(directory!, name);
    using (var file = File.Create(path))
    {
        renderer.WritePng(file);
    }

    Console.WriteLine($"Wrote {path}: M4 of {seriesCount} series in {extractMs:F2} ms ({(backend.IsWarp ? "WARP" : "hardware")}).");
}

// A seeded random walk of floats (xorshift64*), written in blocks of 4 MiB.
static void Generate(string path, long count, ulong seed)
{
    var state = seed;
    var y = 0.0;
    var block = new float[1 << 20];
    using var stream = File.Create(path);
    for (long written = 0; written < count; written += block.Length)
    {
        var length = (int)Math.Min(block.Length, count - written);
        for (var i = 0; i < length; i++)
        {
            state ^= state >> 12;
            state ^= state << 25;
            state ^= state >> 27;
            y += (((state * 0x2545F4914F6CDD1DUL) >> 11) * (1.0 / (1UL << 53))) - 0.5;
            block[i] = (float)y;
        }

        stream.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(block.AsSpan(0, length)));
    }
}
