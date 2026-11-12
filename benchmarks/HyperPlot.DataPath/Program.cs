using System.Diagnostics;
using HyperPlot.DataPath;

// The data-path scenes of design §9, run on a NativeAOT Release build (.cursor/rules/bench.mdc):
//   dotnet publish benchmarks/HyperPlot.DataPath -c Release -r win-x64
//   HyperPlot.DataPath generate --file D:\...\series.bin [--points 1000000000] [--seed N]
//   HyperPlot.DataPath hash --file ...
//   HyperPlot.DataPath build --file ... [--workers 8] [--cold] [--yields on|off]
//   HyperPlot.DataPath provisional --file ... [--zoom] [--cold]
//   HyperPlot.DataPath cold-frame --file ... [--reads-only]
//   HyperPlot.DataPath generate-points --file D:\...\points [--distribution uniform|clustered] [--points N] [--seed N]
//   HyperPlot.DataPath hash-points --file ...
//   HyperPlot.DataPath spatial --file ... [--distribution ...] [--workers 8] [--cold] [--rows-per-unit N] [--density-split N] [--temp D:\...]
//   HyperPlot.DataPath allocate --file - [--points N] [--temp /mnt/c/...]
//   HyperPlot.DataPath touch --file - [--points N] [--temp D:\...]
//   HyperPlot.DataPath burn [--seconds N]
//   HyperPlot.DataPath yield --file ... [--yields on|off] [--most-rest N] [--workers 8] [--competitor] [--collects] [--seconds N]
// The data files live outside the repository and are deleted after the run. The spatial scenes read <file>.x and <file>.y.
var options = Options.Parse(args);
using var process = Process.GetCurrentProcess();
if (OperatingSystem.IsWindows() && options.Scene is not ("burn" or "yield"))
{
    // Raising the priority on Linux needs a privilege the allocation scene runs without. The scenes of row S4 run as an
    // app does, at the normal priority: they measure what the build leaves to a process beside it.
    process.PriorityClass = ProcessPriorityClass.High;
}

switch (options.Scene)
{
    case "generate":
        SeriesFile.Write(options.File, options.Points, options.Seed);
        Console.WriteLine($"generate points={options.Points} seed={options.Seed}");
        break;
    case "hash":
        Console.WriteLine($"hash sha256={SeriesFile.Hash(options.File)}");
        break;
    case "build":
        Scenes.Build(options);
        break;
    case "provisional":
        Scenes.Provisional(options);
        break;
    case "cold-frame":
        Scenes.ColdFrame(options with { Cold = true });
        break;
    case "generate-points":
        PointFiles.Write(options.File, options.Points, options.Seed, options.Distribution);
        Console.WriteLine($"generate-points distribution={options.Distribution} points={options.Points} seed={options.Seed}");
        break;
    case "hash-points":
        var (xHash, yHash) = PointFiles.Hash(options.File);
        Console.WriteLine($"hash-points x_sha256={xHash} y_sha256={yHash}");
        break;
    case "spatial":
        SpatialScenes.Build(options);
        break;
    case "spatial-dispose":
        SpatialScenes.DisposeWhileWriting(options);
        break;
    case "allocate":
        SpatialScenes.Allocate(options);
        break;
    case "touch":
        SpatialScenes.Touch(options);
        break;
    case "burn":
        YieldScenes.Burn(options);
        break;
    case "yield":
        SpatialScenes.Yield(options);
        break;
    default:
        Console.Error.WriteLine($"Unknown scene '{options.Scene}'.");
        return 2;
}

return 0;
