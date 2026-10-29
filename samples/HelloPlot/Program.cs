using System.Diagnostics;
using HyperPlot;
using HyperPlot.Rendering;
using HyperPlot.Rendering.D3D11;
using HyperPlot.Samples;

// HelloPlot: 4 series of 1e6 points in memory, M4 across the worker pool, drawn headless into a PNG (design §7.5, row S1),
// under a heading drawn with DirectWrite (row T1).
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

// The scene is shared with the golden image test, which draws it with 1e5 points (design §10.1).
var clock = Stopwatch.StartNew();
const int Points = 1_000_000;
var list = HelloScene.Record(Points, PlotWorkerPool.Shared);

// The heading goes after the scene, so that the golden image of the scene does not hold DirectWrite's pixels (design §7.5).
list.DrawText(
    $"HelloPlot: {HelloScene.SeriesCount} series of {Points:N0} points",
    new System.Numerics.Vector2(HelloScene.Width / 2f, 6),
    new TextStyle(new Color(40, 40, 40, 255), 16, Horizontal: TextAlign.Center, Vertical: TextBaseline.Top)
);
using var backend = D3D11Backend.Create(useWarp);
using var renderer = new HeadlessRenderer(backend, HelloScene.Width, HelloScene.Height);
renderer.Clear(new Color(255, 255, 255, 255));
renderer.Render(list);
using (var file = File.Create(output))
{
    renderer.WritePng(file);
}

Console.WriteLine($"Wrote {output} ({(backend.IsWarp ? "WARP" : "hardware")}) in {clock.Elapsed.TotalMilliseconds:F0} ms.");
return 0;
