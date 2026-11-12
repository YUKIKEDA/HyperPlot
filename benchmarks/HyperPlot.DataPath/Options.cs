using System.Globalization;

namespace HyperPlot.DataPath;

/// <summary>The command line of a scene.</summary>
internal sealed record Options(
    string Scene,
    string File,
    long Points,
    ulong Seed,
    int Workers,
    bool Cold,
    bool Zoom,
    bool ReadsOnly,
    string Distribution,
    int RowsPerUnit,
    long DensitySplit,
    bool Yields,
    int MostRest,
    bool Competitor,
    bool Collects,
    int Seconds,
    string? Temp
)
{
    internal static Options Parse(string[] args)
    {
        if (args.Length == 0)
        {
            throw new ArgumentException(
                "Name a scene: generate, hash, build, provisional, cold-frame, generate-points, hash-points, spatial, allocate, touch, burn, or yield."
            );
        }

        var options = new Options(
            args[0],
            string.Empty,
            1_000_000_000,
            SeriesFile.DefaultSeed,
            0,
            false,
            false,
            false,
            "uniform",
            0,
            0,
            true,
            0,
            false,
            false,
            0,
            null
        );
        for (var i = 1; i < args.Length; i++)
        {
            options = args[i] switch
            {
                "--file" => options with { File = args[++i] },
                "--points" => options with { Points = long.Parse(args[++i], CultureInfo.InvariantCulture) },
                "--seed" => options with { Seed = ulong.Parse(args[++i], CultureInfo.InvariantCulture) },
                "--workers" => options with { Workers = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                "--cold" => options with { Cold = true },
                "--zoom" => options with { Zoom = true },
                "--reads-only" => options with { ReadsOnly = true },
                "--distribution" => options with { Distribution = args[++i] },
                "--rows-per-unit" => options with { RowsPerUnit = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                "--density-split" => options with { DensitySplit = long.Parse(args[++i], CultureInfo.InvariantCulture) },
                "--yields" => options with { Yields = args[++i] == "on" },
                "--most-rest" => options with { MostRest = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                "--competitor" => options with { Competitor = true },
                "--collects" => options with { Collects = true },
                "--seconds" => options with { Seconds = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                "--temp" => options with { Temp = args[++i] },
                _ => throw new ArgumentException($"Unknown option '{args[i]}'."),
            };
        }

        return options.File.Length == 0 && options.Scene != "burn" ? throw new ArgumentException("--file is required.") : options;
    }
}
