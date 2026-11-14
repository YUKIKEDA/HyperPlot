using System.Diagnostics;
using HyperPlot.SurfaceLod;

// SPIKE (row K0c, Issue #489): the LOD of a surface of 10,240 × 10,240 cells at 1,920 × 1,080 on D3D11, headless. Not
// product code.
//   dotnet run --project tools/ShaderGen -- surface          (after a change to Shaders/Surface.hlsl)
//   dotnet publish benchmarks/HyperPlot.SurfaceLod -c Release -r win-x64 -o <dir>
//   <dir>/HyperPlot.SurfaceLod.exe run <64|128|256> <smooth|noisy|spikes> <uniform|nonuniform> [--frames 600]
//     [--warmup 120] [--error 1] [--pool-mb 256] [--face 258] [--tile-budget n] [--noise 0.02] [--spikes 1000]
//     [--no-stitch] [--back-to-back] [--warp] [--csv <file>] [--seed 1]
//     Builds the levels and the tiles' errors, then the orbit of row K0a: the camera turns 360° about the box's vertical
//     axis over 600 frames, 30° above the horizon, at the distance where the box's bounding sphere fits the vertical field
//     of view (45°). Each frame chooses the tiles by screen error (1 pixel) with the restriction that neighbors differ by
//     a level at most, makes them resident in the pool (uploading the new ones), and draws them, one draw per edge shape.
//     A frame every 16.7 ms at the fastest unless --back-to-back. Prints the memory of the levels over the original, the
//     CPU time of each frame and its parts, the GPU time of the uploads, the clears, and the draws (timestamps), the
//     tiles, the triangles, the restriction's splits, and the upload per frame, p50, p95, p99, and max, over the frames
//     after the warm-up.
//   <dir>/HyperPlot.SurfaceLod.exe image <64|128|256> <smooth|noisy|spikes> <uniform|nonuniform> [--frame 150]
//     [--out <file.bmp>] [--no-stitch] [--hardware] [options of run]
//     Draws one frame of the orbit on WARP (or the hardware), counts the background pixels whose 8 neighbors are all
//     surface (a crack, Issue #489 decision 4), and writes the image. Exits with 1 when an index list fails its check.
//   <dir>/HyperPlot.SurfaceLod.exe cracks <64|128|256> <smooth|noisy|spikes> <uniform|nonuniform>
//     [--at 0,50,...,550] [--csv <file>] [--pixels <file>] [--dump <folder>] [--hardware] [options of run]
//     The same count at each frame of --at, stitched and not stitched, on WARP (or the hardware), with the totals.
if (args.Length < 4 || args[0] is not ("run" or "image" or "cracks"))
{
    Console.Error.WriteLine("run|image|cracks <64|128|256> <smooth|noisy|spikes> <uniform|nonuniform> [options]");
    return 2;
}

using var process = Process.GetCurrentProcess();
process.PriorityClass = ProcessPriorityClass.High;
return args[0] switch
{
    "run" => Scene.Run(args.AsSpan(1)),
    "image" => Scene.Image(args.AsSpan(1)),
    _ => Scene.Cracks(args.AsSpan(1)),
};
