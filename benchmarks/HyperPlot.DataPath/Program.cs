using System.Diagnostics;
using HyperPlot.DataPath;

// The data-path scenes of design §9, run on a NativeAOT Release build (.cursor/rules/bench.mdc):
//   dotnet publish benchmarks/HyperPlot.DataPath -c Release -r win-x64
//   HyperPlot.DataPath generate --file D:\...\series.bin [--points 1000000000] [--seed N]
//   HyperPlot.DataPath hash --file ...
//   HyperPlot.DataPath build --file ... [--workers 8] [--cold]
//   HyperPlot.DataPath provisional --file ... [--zoom] [--cold]
//   HyperPlot.DataPath cold-frame --file ... [--reads-only]
// The data file lives outside the repository and is deleted after the run.
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
    default:
        Console.Error.WriteLine($"Unknown scene '{options.Scene}'.");
        return 2;
}

return 0;
