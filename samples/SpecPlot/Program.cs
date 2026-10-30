using System.Diagnostics;
using HyperPlot;
using HyperPlot.Rendering;
using HyperPlot.Rendering.D3D11;

// SpecPlot: two series of 1e6 points declared with a PlotSpec, on a time axis from 2026-01-01 in nanoseconds, with axes, a
// title, and a legend (row G3, design §4.6, §7.5), written as two PNGs: the light theme and the dark one. A third PNG draws
// row G4's geoms from the same series: a band of a moving min and max, bars, a step, and error bars (design §5.5.3).
// Its NativeAOT publish is a check of the guides beside HelloPlot's (.cursor/rules/aot.mdc):
//   dotnet publish samples/SpecPlot -c Release -r win-x64 -o <dir>
//   <dir>/SpecPlot.exe --auto [--out <dir>] [--warp] [--scale 1.5]
// --auto runs to the end with no input; the sample takes none anyway, and the flag is what the AOT check passes.
var output = AppContext.BaseDirectory;
var useWarp = false;
var scale = 1f;
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
        case "--scale":
            scale = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            break;
        case "--auto":
            break;
        default:
            Console.Error.WriteLine($"Unknown option {args[i]}.");
            return 2;
    }
}

const int Points = 1_000_000;
const int Width = 1280;
const int Height = 720;
var clock = Stopwatch.StartNew();

// Two seeded sines with noise, one point a millisecond from 2026-01-01 (Unix nanoseconds).
var random = new Random(7);
var first = new float[Points];
var second = new float[Points];
for (var i = 0; i < Points; i++)
{
    var t = i / (double)Points;
    first[i] = (float)((Math.Sin(t * 12 * Math.PI) * 2.5) + (random.NextDouble() - 0.5));
    second[i] = (float)((Math.Cos(t * 5 * Math.PI) * 1.5) + 1 + ((random.NextDouble() - 0.5) * 0.5));
}

using var ch1 = Column.Wrap(first);
using var ch2 = Column.Wrap(second);
var start = (new DateTime(2026, 1, 1) - DateTime.UnixEpoch).Ticks * 100;
var table = DataTable.Create().Add("t", Column.UniformInteger(start: start, step: 1_000_000, length: Points)).Add("ch1", ch1).Add("ch2", ch2).Build();
var time = table.Col<long>("t");
var plot = Plot.Create(table)
    .Line(time, table.Col<float>("ch1"), l => l.Color(new Color(31, 119, 180, 255)).Label("ch1"))
    .Line(time, table.Col<float>("ch2"), l => l.Color(new Color(255, 127, 14, 255)).Dash(new LineDash(6, 3)).Label("ch2"))
    .ScaleX(Scale.Time(TimeUnit.Nanoseconds).WithTitle("Time"))
    .ScaleY(Scale.Linear().WithTitle("Value") with { Nice = true })
    .Title($"SpecPlot: 2 series of {Points:N0} points")
    .Build();

using var backend = D3D11Backend.Create(useWarp);
using var renderer = new HeadlessRenderer(backend, (int)(Width * scale), (int)(Height * scale), scale: scale);
foreach (var (name, theme) in new[] { ("spec-light.png", Theme.Light), ("spec-dark.png", Theme.Dark) })
{
    renderer.Render(plot with { Theme = theme });
    var path = Path.Combine(output, name);
    using var file = File.Create(path);
    renderer.WritePng(file);
    Console.WriteLine($"Wrote {path}.");
}

// The moving min and max of ch1 over 20,000 points centered on each, and error bars of ch2 every 25,000 points.
const int Window = 20_000;
var low = Moving(first, Window, Math.Min);
var high = Moving(first, Window, Math.Max);
var errorLow = new float[Points];
var errorHigh = new float[Points];
for (var i = 0; i < Points; i++)
{
    var marked = i % 25_000 == 12_500;
    (errorLow[i], errorHigh[i]) = marked ? (second[i] - 0.6f, second[i] + 0.6f) : (float.NaN, float.NaN);
}

using var lowColumn = Column.Wrap(low);
using var highColumn = Column.Wrap(high);
using var errorLowColumn = Column.Wrap(errorLow);
using var errorHighColumn = Column.Wrap(errorHigh);
var geoms = DataTable
    .Create()
    .Add("t", Column.UniformInteger(start: start, step: 1_000_000, length: Points))
    .Add("ch1", ch1)
    .Add("ch2", ch2)
    .Add("low", lowColumn)
    .Add("high", highColumn)
    .Add("errorLow", errorLowColumn)
    .Add("errorHigh", errorHighColumn)
    .Build();
var geomTime = geoms.Col<long>("t");
var geomPlot = Plot.Create(geoms)
    .Bar(geomTime, geoms.Col<float>("ch1"), configure: b => b.Fill(new Color(31, 119, 180, 60)).Label("ch1 bars"))
    .Band(geomTime, geoms.Col<float>("low"), geoms.Col<float>("high"), b => b.Fill(new Color(44, 160, 44, 90)).Label("ch1 moving range"))
    .Step(geomTime, geoms.Col<float>("ch2"), configure: l => l.Color(new Color(255, 127, 14, 255)).Label("ch2 step"))
    .ErrorBar(
        geomTime,
        geoms.Col<float>("errorLow"),
        geoms.Col<float>("errorHigh"),
        configure: l => l.Color(new Color(214, 39, 40, 255)).Width(2).Label("ch2 ± 0.6")
    )
    .ScaleX(Scale.Time(TimeUnit.Nanoseconds).WithTitle("Time"))
    .ScaleY(Scale.Linear().WithTitle("Value") with { Nice = true })
    .Title($"SpecPlot: geoms over {Points:N0} points")
    .Build();
renderer.Render(geomPlot);
var geomPath = Path.Combine(output, "spec-geoms.png");
using (var file = File.Create(geomPath))
{
    renderer.WritePng(file);
}

Console.WriteLine($"Wrote {geomPath}.");
Console.WriteLine($"Done ({(backend.IsWarp ? "WARP" : "hardware")}) in {clock.Elapsed.TotalMilliseconds:F0} ms.");
return 0;

// The extreme of each window of a width centered on each element, by blocks: an element's window spans at most two blocks,
// so the extremes from each block's start and from each block's end give it in O(n).
static float[] Moving(float[] values, int width, Func<float, float, float> pick)
{
    var n = values.Length;
    var fromStart = new float[n];
    var fromEnd = new float[n];
    for (var i = 0; i < n; i++)
    {
        fromStart[i] = i % width == 0 ? values[i] : pick(fromStart[i - 1], values[i]);
    }

    for (var i = n - 1; i >= 0; i--)
    {
        fromEnd[i] = i == n - 1 || (i + 1) % width == 0 ? values[i] : pick(fromEnd[i + 1], values[i]);
    }

    var moved = new float[n];
    for (var i = 0; i < n; i++)
    {
        var (from, to) = (Math.Max(0, i - (width / 2)), Math.Min(n - 1, i + (width / 2) - 1));
        moved[i] = pick(fromEnd[from], fromStart[to]);
    }

    return moved;
}
