using System.Diagnostics;
using HyperPlot.Depth3D;

// SPIKE (row K1, Issue #509): how 3D polylines and markers composite with depth on D3D11 at 1,920 × 1,080, headless, against
// a CPU reference of the exact area with depth order. Not product code. Shaders/Depth3D.hlsl describes each method.
//   dotnet run --project tools/ShaderGen -- depth3d          (after a change to Shaders/Depth3D.hlsl)
//   dotnet publish benchmarks/HyperPlot.Depth3D -c Release -r win-x64 -o <dir>
//   <dir>/HyperPlot.Depth3D.exe run lines <none|test|prepass|postpass|capsules|uav|uavfrag> <knot|walk> [options]
//   <dir>/HyperPlot.Depth3D.exe run markers <none|quad|touched|half> <uniform|knot|walk> [options]
//     The orbit: the camera turns 360° about the box's vertical axis over 300 frames, 30° above the horizon, at the
//     distance where the box's bounding sphere fits the vertical field of view (45°). A frame every 16.7 ms at the fastest
//     unless --back-to-back. Prints the CPU time of each frame and the GPU time of each bucket (timestamps: the clears,
//     the surface, the shapes; with --pass-times also the segment, box, and marker passes), p50, p95, p99, and max, over
//     the frames after the warm-up.
//   <dir>/HyperPlot.Depth3D.exe determinism <lines|markers> <scene> [options] [--hardware] [--frame 75]
//     Draws one frame of the orbit, another, and the first again with every method on WARP (or the hardware), and compares
//     the two images of the same frame byte for byte. Prints a SHA-256 of each image, to compare between runs. Exits with
//     1 when a repeat differs.
//   <dir>/HyperPlot.Depth3D.exe compare <lines|markers> <scene> [options] [--warp] [--at 0,75,150,225] [--samples 16]
//     [--dump <folder for BMPs>] [--dump-methods postpass,none] [--trace <file.csv>]
//     The CPU reference and every method at each frame of --at; prints each method's error over the pixels near a shape:
//     mean, p99, max, and the pixels above 0.1, 0.25, and 0.5. --dump writes the reference, each method's image (or those
//     of --dump-methods), and a map of its error (white none, full red 0.5 or more). --trace (lines) writes, for each pixel
//     of postpass with an error above 0.5, each polyline there: the reference's shares and postpass replayed on the CPU
//     (Reference.TracePostpass), and prints how many pixels the replay matches.
//   run --back-to-back keeps the GPU busy with at most --in-flight 2 frames queued (its clock at P0); the run prints the
//     window of the measured frames with the date, to read the clock log inside it.
//   Options: --count 1e6 (segments, or markers) --polylines n (knot 8, walk 16) --width 2 (a line's, pixels) --diameter 4
//     (a marker's, pixels) --threshold 0.5 (the coverage from which depth is written) --plane none|before|after (the
//     surface z = 0 over [-1, 1]², opaque, depth test and write; before the shapes by default) --frames 300 --warmup 120
//     --back-to-back --in-flight 2 --warp --pass-times --csv <file> --seed 1
if (args.Length < 3 || args[0] is not ("run" or "determinism" or "compare") || (args[0] == "run" && args.Length < 4))
{
    Console.Error.WriteLine(
        "run <lines|markers> <method> <scene> [options] | determinism <lines|markers> <scene> [options] | compare <lines|markers> <scene> [options]"
    );
    return 2;
}

using var process = Process.GetCurrentProcess();
process.PriorityClass = ProcessPriorityClass.High;
return args[0] switch
{
    "run" => Scene.Run(args.AsSpan(1)),
    "determinism" => Scene.Determinism(args.AsSpan(1)),
    _ => Scene.Compare(args.AsSpan(1)),
};
