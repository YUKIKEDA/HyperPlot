using System.Diagnostics;
using HyperPlot;
using HyperPlot.Rendering;
using HyperPlot.Rendering.D3D11;
using HyperPlot.Samples;

// HelloPlot: 4 series of 1e6 points in memory, M4 across the worker pool, drawn headless into a PNG (design §7.5, row S1).
// Its NativeAOT publish is the engine's AOT check (.cursor/rules/aot.mdc):
//   dotnet publish samples/HelloPlot -c Release -r win-x64 -o <dir>
//   <dir>/HelloPlot.exe --auto [--out hello.png] [--warp]
// --auto runs to the end with no input; the sample takes none anyway, and the flag is what the AOT check passes.
var output = Path.Combine(AppContext.BaseDirectory, "hello.png");
var useWarp = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--out":
            output = args[++i];
            break;
        case "--warp":
            useWarp = true;
            break;
        case "--auto":
            break;
        default:
            Console.Error.WriteLine($"Unknown option {args[i]}.");
            return 2;
    }
}

const int Points = 1_000_000;
const int SeriesCount = 4;
var clock = Stopwatch.StartNew();
var pool = PlotWorkerPool.Shared;
var columns = new Column<float>[SeriesCount];
var pyramids = new MinMaxPyramid<float>[SeriesCount];
for (var s = 0; s < SeriesCount; s++)
{
    // A sine of its own frequency with seeded noise and a gap of NaN, so that the line breaks once.
    var random = new Random(s);
    var values = new float[Points];
    for (var i = 0; i < Points; i++)
    {
        values[i] = (float)(Math.Sin(i * (s + 1) * 2e-5) + (s * 2.5) + ((random.NextDouble() - 0.5) * 0.6));
    }

    values.AsSpan(400_000 + (s * 50_000), 20_000).Fill(float.NaN);
    columns[s] = Column.Wrap(values);
    pyramids[s] = pool.BuildPyramid(columns[s]);
}

foreach (var pyramid in pyramids)
{
    pyramid.Completion.Wait();
}

var area = new PlotArea(left: 40, top: 30, width: 1200, height: 660);
var x = Column.Uniform(0, 1, Points);
var batch = new M4Batch();
var outputs = new M4Point[SeriesCount][];
for (var s = 0; s < SeriesCount; s++)
{
    outputs[s] = new M4Point[M4.MaxPointCount(area.Width)];
    _ = batch.Add(x, pyramids[s], outputs[s]);
}

var boundaries = area.Boundaries(0, Points - 1);
pool.Extract(batch, boundaries);

// The Y range of every series, from the pyramids (design §3.3).
var low = double.PositiveInfinity;
var high = double.NegativeInfinity;
foreach (var pyramid in pyramids)
{
    var range = pyramid.GetMinMax(0, Points);
    low = Math.Min(low, range.Min);
    high = Math.Max(high, range.Max);
}

Color[] colors = [new(31, 119, 180, 255), new(255, 127, 14, 255), new(44, 160, 44, 255), new(214, 39, 40, 255)];
var list = new RenderList();
area.DrawGrid(list, step: 60);
for (var s = 0; s < SeriesCount; s++)
{
    var points = outputs[s].AsSpan(0, batch.GetResult(s).Count);
    area.DrawSeries(list, points, 0, Points - 1, low, high, new LineStyle(colors[s], 1.5f));
}

using var backend = D3D11Backend.Create(useWarp);
using var renderer = new HeadlessRenderer(backend, 1280, 720);
renderer.Clear(new Color(255, 255, 255, 255));
renderer.Render(list);
using (var file = File.Create(output))
{
    renderer.WritePng(file);
}

foreach (var pyramid in pyramids)
{
    pyramid.Dispose();
}

foreach (var column in columns)
{
    column.Dispose();
}

Console.WriteLine($"Wrote {output} ({(backend.IsWarp ? "WARP" : "hardware")}) in {clock.Elapsed.TotalMilliseconds:F0} ms.");
return 0;
