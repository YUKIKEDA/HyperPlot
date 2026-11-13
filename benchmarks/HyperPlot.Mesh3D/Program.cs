using System.Diagnostics;
using HyperPlot.Mesh3D;

// SPIKE (row K0d, Issue #490): a triangle mesh at 1,920 × 1,080 on D3D11, headless, opaque and through WBOIT. Not product code.
//   dotnet run --project tools/ShaderGen -- mesh3d          (after a change to Shaders/Mesh3D.hlsl)
//   dotnet publish benchmarks/HyperPlot.Mesh3D -c Release -r win-x64 -o <dir>
//   Meshes: "blob", a closed lumpy torus; "surface", an open noisy height field (MeshData.Make). --triangles 1e7 by default.
//   Common options: [--triangles 1e7] [--alpha 0.5] [--weight none|eq7|eq8|eq9|eq10] [--scale 1,1,0.6] [--seed 1]
//   <dir>/HyperPlot.Mesh3D.exe run <opaque|oit> <blob|surface> [--frames 600] [--warmup 120] [--back-to-back] [--warp]
//     [--csv <file>]
//     The orbit: the camera turns 360° about the box's vertical axis over 600 frames, 30° above the horizon, at the
//     distance where the box's bounding sphere fits the vertical field of view (45°). A frame every 16.7 ms at the
//     fastest unless --back-to-back. Prints the CPU time of each frame (recording and sending its commands), the GPU
//     time of each pass (timestamps), and their sum a frame, p50, p95, p99, and max over the frames after the warm-up,
//     and the p95 of the sum against P-13's 16.7 ms.
//   <dir>/HyperPlot.Mesh3D.exe determinism <blob|surface> [--frame 150] [--hardware] [--dump <folder for BMPs>]
//     Draws one frame of the orbit, another frame, and the first again, opaque and through WBOIT, on WARP (or the
//     hardware), and compares the two images of the same frame byte for byte (WBOIT's accum too). Prints a SHA-256 of
//     each, to compare between runs. Exits with 1 when a repeat differs.
//   <dir>/HyperPlot.Mesh3D.exe error <blob|surface> [--at 0,150,300,450] [--warp] [--capacity 4000000] [--band 135]
//     [--dump <folder for BMPs>]
//     The color error of WBOIT against an exact reference: every fragment captured, sorted per pixel on the CPU, and
//     composited back to front (Scene.Error). Prints ΔE00 and ΔE76, mean, p50, p95, p99, and max, over the covered pixels
//     and over the pixels of two or more layers. Exits with 1 when a fragment list is broken.
if (args.Length < 2 || args[0] is not ("run" or "determinism" or "error") || (args[0] == "run" && args.Length < 3))
{
    Console.Error.WriteLine("run <opaque|oit> <blob|surface> [options] | determinism <blob|surface> [options] | error <blob|surface> [options]");
    return 2;
}

using var process = Process.GetCurrentProcess();
process.PriorityClass = ProcessPriorityClass.High;
return args[0] switch
{
    "run" => Scene.Run(args.AsSpan(1)),
    "determinism" => Scene.Determinism(args.AsSpan(1)),
    _ => Scene.Error(args.AsSpan(1)),
};
