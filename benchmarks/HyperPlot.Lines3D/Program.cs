using HyperPlot.Lines3D;

// SPIKE (row K0b, Issue #488): 3D line simplification by a per-vertex additive importance (a BLG tree per segment), CPU
// only. Not product code.
//   dotnet publish benchmarks/HyperPlot.Lines3D -c Release -r win-x64 -o <dir>
//   <dir>/HyperPlot.Lines3D.exe run --data walk|helix [--count 1e8] [--segment 16] [--split perp|linf] [--workers 8]
//     [--budgets 262144,1048576,4194304] [--frames 600] [--warmup 60] [--scenes orbit,zoom-x,zoom-z,zoom-xyz]
//     [--full-every 5] [--full-cap 67108864] [--seed 1] [--noise 1e-4] [--turns 100] [--csv <file>] [--no-pin]
//     Generates the line, builds the index with the workers (time, CPU time, memory over the data), then pins the process
//     to logical processor 2 and runs each scene: per frame, one extraction under each vertex budget (the tolerance is
//     raised from 1 pixel until the budget holds) and, every --full-every frames, one extraction at 1 pixel with no budget
//     (what a view that stopped draws). Prints p50, p95, p99, max of the time, the vertices, the passes, the tolerance.
//   <dir>/HyperPlot.Lines3D.exe verify --data walk|helix [--count 1e6] [--segment 14] [--split perp|linf] [--every 10]
//     For every --every-th frame of each scene, extracts at 1 pixel with no budget and measures, by brute force over every
//     vertex inside the axis box, its distance on the screen from the drawn chord; also runs a Douglas–Peucker in screen
//     space at 1 pixel over the same segments (what a simplification made for this one view keeps) and gives the ratio of
//     the vertices. Exits with 1 when a vertex is farther than the tolerance or is not covered by any chord.
// Scenes: orbit (600 frames, 360° about the box's vertical axis, 30° above the horizon, all axes at their full range);
// zoom-x, zoom-z, zoom-xyz (600 frames, the range of those axes shrinks from the full range to 1/1024 of it about the
// middle vertex, geometrically, camera still at 30° and 30°). Viewport 1,920 × 1,080, vertical field of view 45°.
if (args.Length < 1 || args[0] is not ("run" or "verify"))
{
    Console.Error.WriteLine("run --data walk|helix [options] | verify --data walk|helix [options]");
    return 2;
}

var options = Options.Parse(args[1..], verify: args[0] == "verify");
return args[0] == "run" ? Scene.Run(options) : Scene.Verify(options);
