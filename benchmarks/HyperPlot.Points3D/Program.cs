using System.Diagnostics;
using HyperPlot.Points3D;

// SPIKE (row K0a, Issue #477): two ways to draw N points in 3D at 1,920 × 1,080 on D3D11, headless. Not product code.
//   dotnet run --project tools/ShaderGen -- points3d          (after a change to Shaders/Points3D.hlsl)
//   dotnet publish benchmarks/HyperPlot.Points3D -c Release -r win-x64 -o <dir>
//   <dir>/HyperPlot.Points3D.exe run <markers|compute> <count> <uniform|cluster> [--frames 600] [--warmup 120]
//     [--diameter 2] [--footprint k] [--order random|cells] [--back-to-back] [--warp] [--csv <file>] [--seed 1]
//     The orbit: the camera turns 360° about the box's vertical axis over 600 frames, 30° above the horizon, at the
//     distance where the box's bounding sphere fits the vertical field of view (45°). A frame every 16.7 ms at the
//     fastest unless --back-to-back. Prints the CPU time of each frame (recording and sending its commands) and the GPU
//     time of each pass (timestamps), p50, p95, p99, and max, over the frames after the warm-up.
//     markers: footprint 0 (the default) draws discs of the diameter in pixels, k ≥ 1 squares of k × k pixels.
//     compute: squares of k × k pixels (1 by default).
//   <dir>/HyperPlot.Points3D.exe determinism <count> <uniform|cluster> [--frame 150] [--hardware] [--diameter 2]
//     [--footprint 1] [--order random|cells] [--dump <folder for BMPs>] [--seed 1]
//     Draws one frame of the orbit, another frame, and the first again, with each method on WARP (or the hardware), and
//     compares the two images of the same frame byte for byte. Prints a SHA-256 of each image, to compare between runs.
//     Also compares the markers' k × k squares with compute's, pixel by pixel. Exits with 1 when a repeat differs.
//   The octree (hypotheses 2 to 4 of Issue #477, drawn with A by the owner's decision of 2026-11-14):
//   <dir>/HyperPlot.Points3D.exe octree-build <count> <uniform|cluster> [--grid 128] [--leaf 10000] [--workers 8]
//     [--seed 1] [--hash] [--hits 50] [--hit-frames 20] [--hit-radius 4] [--hit-budget 1e7] [--hit-check 10] [--pin]
//     Makes the columns, builds the octree (Octree.cs) and prints the time of each stage, the nodes by level and by size,
//     and the memory of the copy and the table; then the hit tests at hits × hit-frames cursors over the orbit, all points
//     and the nodes the budget draws, and checks the octree's answer against every point. Exits with 1 when one differs.
//   <dir>/HyperPlot.Points3D.exe octree-orbit <count> <uniform|cluster> [--grid 128] [--leaf 10000] [--workers 8]
//     [--variants budget:diameter:morton|priority,...] [--reverse] [--frames 600] [--warmup 120] [--back-to-back] [--warp]
//     Builds the octree, then runs the orbit for each variant: each frame chooses the nodes under the budget and draws
//     A's discs of the diameter, one draw a node, in the points' Morton order inside each node or their seeded priority's.
var valid = args.Length switch
{
    < 3 => false,
    _ => args[0] switch
    {
        "run" => args.Length >= 4,
        "determinism" or "octree-build" or "octree-orbit" => true,
        _ => false,
    },
};
if (!valid)
{
    Console.Error.WriteLine(
        "run <markers|compute> <count> <uniform|cluster> [options] | determinism|octree-build|octree-orbit <count> <uniform|cluster> [options]"
    );
    return 2;
}

using var process = Process.GetCurrentProcess();
process.PriorityClass = ProcessPriorityClass.High;
return args[0] switch
{
    "run" => Scene.Run(args.AsSpan(1)),
    "determinism" => Scene.Determinism(args.AsSpan(1)),
    "octree-build" => Scene.OctreeBuild(args.AsSpan(1)),
    _ => Scene.OctreeOrbit(args.AsSpan(1)),
};
