using System.Diagnostics;
using HyperPlot.DataPath;

// The data-path scenes of design §9, run on a NativeAOT Release build (.cursor/rules/bench.mdc):
//   dotnet publish benchmarks/HyperPlot.DataPath -c Release -r win-x64
//   HyperPlot.DataPath generate --file D:\...\series.bin [--points 1000000000] [--seed N]
//   HyperPlot.DataPath hash --file ...
//   HyperPlot.DataPath build --file ... [--workers 8] [--cold]
//   HyperPlot.DataPath provisional --file ... [--zoom] [--cold]
//   HyperPlot.DataPath cold-frame --file ... [--reads-only]
//   HyperPlot.DataPath generate-points --file D:\...\points [--distribution uniform|clustered] [--points N] [--seed N]
//   HyperPlot.DataPath hash-points --file ...
//   HyperPlot.DataPath spatial --file ... [--distribution ...] [--workers 8] [--cold] [--rows-per-unit N] [--temp D:\...]
//   HyperPlot.DataPath allocate --file - [--points N] [--temp /mnt/c/...]
// The data files live outside the repository and are deleted after the run. The spatial scenes read <file>.x and <file>.y.
var options = Options.Parse(args);
using var process = Process.GetCurrentProcess();
process.PriorityClass = ProcessPriorityClass.High;
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
    default:
        Console.Error.WriteLine($"Unknown scene '{options.Scene}'.");
        return 2;
}

return 0;
