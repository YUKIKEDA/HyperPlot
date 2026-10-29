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

// The scene is shared with the golden image test, which draws it with 1e5 points (design §10.1).
var clock = Stopwatch.StartNew();
var list = HelloScene.Record(points: 1_000_000, PlotWorkerPool.Shared);
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
