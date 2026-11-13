using System.Globalization;

namespace HyperPlot.Lines3D;

/// <summary>The command line of the spike.</summary>
internal sealed class Options
{
    private static readonly string[] _allScenes = ["orbit", "zoom-x", "zoom-z", "zoom-xyz"];

    public string Data { get; private set; } = "walk";

    public int Count { get; private set; }

    public int SegmentLog2 { get; private set; } = 16;

    public SplitDistance Split { get; private set; } = SplitDistance.Perpendicular;

    public int Workers { get; private set; } = 8;

    public int[] Budgets { get; private set; } = [262_144, 1_048_576, 4_194_304];

    public int Frames { get; private set; } = 600;

    public int Warmup { get; private set; } = 60;

    public string[] Scenes { get; private set; } = [.. _allScenes];

    public int FullEvery { get; private set; } = 5;

    public int FullCap { get; private set; } = 1 << 26;

    public int Every { get; private set; } = 10;

    public ulong Seed { get; private set; } = 1;

    public double Noise { get; private set; } = 1e-4;

    public double Turns { get; private set; } = 100;

    public string? Csv { get; private set; }

    public bool Pin { get; private set; } = true;

    public string SplitName => Split == SplitDistance.Perpendicular ? "perp" : "linf";

    /// <summary>Reads the options after the command; run measures 1e8 vertices by default, verify 1e6.</summary>
    public static Options Parse(string[] args, bool verify)
    {
        var options = new Options { Count = verify ? 1_000_000 : 100_000_000 };
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--data":
                    options.Data = Next(args, ref i) switch
                    {
                        "walk" => "walk",
                        "helix" => "helix",
                        var other => throw new ArgumentException($"Unknown data {other}: walk or helix."),
                    };
                    break;
                case "--count":
                    options.Count = (int)double.Parse(Next(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--segment":
                    options.SegmentLog2 = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--split":
                    options.Split = Next(args, ref i) switch
                    {
                        "perp" => SplitDistance.Perpendicular,
                        "linf" => SplitDistance.AxisMax,
                        var other => throw new ArgumentException($"Unknown split distance {other}: perp or linf."),
                    };
                    break;
                case "--workers":
                    options.Workers = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--budgets":
                    options.Budgets = [.. Next(args, ref i).Split(',').Select(b => (int)double.Parse(b, CultureInfo.InvariantCulture))];
                    break;
                case "--frames":
                    options.Frames = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--warmup":
                    options.Warmup = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--scenes":
                    options.Scenes = Next(args, ref i).Split(',');
                    break;
                case "--full-every":
                    options.FullEvery = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--full-cap":
                    options.FullCap = (int)double.Parse(Next(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--every":
                    options.Every = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--seed":
                    options.Seed = ulong.Parse(Next(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--noise":
                    options.Noise = double.Parse(Next(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--turns":
                    options.Turns = double.Parse(Next(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--csv":
                    options.Csv = Next(args, ref i);
                    break;
                case "--no-pin":
                    options.Pin = false;
                    break;
                default:
                    throw new ArgumentException($"Unknown option {args[i]}.");
            }
        }

        if (options.SegmentLog2 is < 2 or > 20)
        {
            throw new ArgumentException("--segment is the log2 of the vertices of a segment, 2 to 20 (a divisor of the 2^20 chunk).");
        }

        return options;
    }

    private static string Next(string[] args, ref int i) =>
        i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
}
