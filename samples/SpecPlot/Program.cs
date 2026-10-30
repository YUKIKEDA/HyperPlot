using System.Diagnostics;
using HyperPlot;
using HyperPlot.Rendering;
using HyperPlot.Rendering.D3D11;

// SpecPlot: two series of 1e6 points declared with a PlotSpec, on a time axis from 2026-01-01 in nanoseconds, with axes, a
// title, and a legend (row G3, design §4.6, §7.5), written as two PNGs: the light theme and the dark one.
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

Console.WriteLine($"Done ({(backend.IsWarp ? "WARP" : "hardware")}) in {clock.Elapsed.TotalMilliseconds:F0} ms.");
return 0;
