using System.Diagnostics;
using HyperPlot.Volume;

// SPIKE (row K0e, Issue #491): a bricked volume ray marched at 1,920 × 1,080 on D3D11, headless. Not product code.
//   dotnet publish benchmarks/HyperPlot.Volume -c Release -r win-x64 -o <dir>
//   <dir>/HyperPlot.Volume.exe generate <size> <smooth|noisy> <file> [--seed 1]
//     Writes N³ R16_UNORM codes normalized by the build's minimum and maximum (code 65,535 kept for NaN).
//   <dir>/HyperPlot.Volume.exe run <file> [--brick 64] [--tf translucent|opaque] [--threshold 0.99] [--filter sample|load]
//     [--frames 600] [--warmup 120] [--upload-mb 4] [--atlas-mb 6144] [--period-ms 33.3] [--back-to-back] [--warp] [--csv <file>]
//     The orbit of row K0a's prototype (360° in 600 frames, 30° above the horizon), one frame every 33.3 ms at the fastest
//     (P-14's 30 fps). Before it, the first frame's bricks are all sent. Prints the memory of the levels and ghosts, the CPU
//     time (selection, upload, recording) and the GPU time of each pass, p50, p95, p99, max, and the bytes asked and sent a frame.
//   <dir>/HyperPlot.Volume.exe zoom <file> [--factors 2,4,8] [--angle-frame 0] [--upload-mb 4] [--max-frames 2000] [...]
//     The frames until a view is not provisional: from nothing to the whole volume (cold), and from the whole volume to each
//     zoom about the center, sending at most the budget a frame.
//   <dir>/HyperPlot.Volume.exe determinism <file> [--frame 150] [--hardware] [--filter sample|load] [--dump <folder>] [...]
//     On WARP (or the hardware): frame f, another, f again in the same cache, and f after two other frames in a fresh cache,
//     where its bricks take other slots. Compares the images byte for byte; exits with 1 when one differs.
if (args.Length < 2 || args[0] is not ("generate" or "run" or "zoom" or "determinism"))
{
    Console.Error.WriteLine("generate <size> <smooth|noisy> <file> | run <file> [options] | zoom <file> [options] | determinism <file> [options]");
    return 2;
}

using var process = Process.GetCurrentProcess();
process.PriorityClass = ProcessPriorityClass.High;
return args[0] switch
{
    "generate" => Scene.Generate(args.AsSpan(1)),
    "run" => Scene.Run(args.AsSpan(1)),
    "zoom" => Scene.Zoom(args.AsSpan(1)),
    _ => Scene.Determinism(args.AsSpan(1)),
};
