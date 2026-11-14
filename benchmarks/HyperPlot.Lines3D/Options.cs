using System.Globalization;

namespace HyperPlot.Lines3D;

/// <summary>
/// An extraction the par and async commands measure: dfs/T, the in-order walk at 1 pixel over blocks of segments taken by T
/// threads; bfs/T, the level-order walk at 1 pixel with no limit; time/T/ms and nodes/T/count, the level-order walk stopped
/// at a deadline after its start or after that many nodes visited (split evenly between the T threads).
/// </summary>
internal sealed class Variant
{
    private Variant(string name, bool levels, int threads, double limitMs, long limitNodes)
    {
        Name = name;
        Levels = levels;
        Threads = threads;
        LimitMs = limitMs;
        LimitNodes = limitNodes;
    }

    public string Name { get; }

    /// <summary>Gets a value indicating whether it is the level-order walk.</summary>
    public bool Levels { get; }

    public int Threads { get; }

    /// <summary>Gets the time after which it stops, or 0.</summary>
    public double LimitMs { get; }

    /// <summary>Gets the nodes visited after which it stops, or 0.</summary>
    public long LimitNodes { get; }

    public static Variant Parse(string text)
    {
        var parts = text.Split('/');
        var threads = parts.Length >= 2 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        if (threads < 1 || threads > 64)
        {
            throw new ArgumentException($"Variant {text}: the threads are 1 to 64.");
        }

        return parts[0] switch
        {
            "dfs" when parts.Length == 2 => new Variant(text, false, threads, 0, 0),
            "bfs" when parts.Length == 2 => new Variant(text, true, threads, 0, 0),
            "time" when parts.Length == 3 => new Variant(
                text,
                true,
                threads,
                double.Parse(parts[2], CultureInfo.InvariantCulture),
                0
            ),
            "nodes" when parts.Length == 3 => new Variant(
                text,
                true,
                threads,
                0,
                (long)double.Parse(parts[2], CultureInfo.InvariantCulture)
            ),
            _ => throw new ArgumentException($"Unknown variant {text}: dfs/T, bfs/T, time/T/ms, or nodes/T/count."),
        };
    }
}

/// <summary>The command line of the spike.</summary>
internal sealed class Options
{
    private static readonly string[] _allScenes = ["orbit", "zoom-x", "zoom-z", "zoom-xyz"];

    private static readonly string[] _parVariants =
    [
        "dfs/1",
        "dfs/2",
        "dfs/4",
        "dfs/8",
        "bfs/8",
        "time/1/8",
        "time/8/8",
        "time/8/12",
        "nodes/8/262144",
        "nodes/8/524288",
    ];

    private static readonly string[] _asyncVariants = ["dfs/8", "dfs/4", "time/8/12", "time/8/8"];

    public string Command { get; private set; } = "run";

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

    /// <summary>Gets the node arrays the index holds; layout and check hold both, par and async the packed ones by default.</summary>
    public NodeLayout Layout { get; private set; } = NodeLayout.Split;

    public Variant[] Variants { get; private set; } = [];

    /// <summary>Gets the kept vertices member 0 of par and async has room for (the other members a quarter of it), and each layout's buffer of the layout command.</summary>
    public int ThreadCap { get; private set; } = 1 << 22;

    /// <summary>Gets the segments of a block that a thread of dfs/T takes at a time.</summary>
    public int Block { get; private set; } = 16;

    /// <summary>Gets the rounds of the async command: each round runs every variant once, in a rotated order.</summary>
    public int Rounds { get; private set; } = 3;

    /// <summary>Gets the frame period of the async command.</summary>
    public double PeriodMs { get; private set; } = 1000.0 / 60;

    public string SplitName => Split == SplitDistance.Perpendicular ? "perp" : "linf";

    public string LayoutName =>
        Layout switch
        {
            NodeLayout.Split => "split",
            NodeLayout.Packed => "packed",
            _ => "both",
        };

    /// <summary>Reads the options after the command; verify and check measure 1e6 vertices by default, the others 1e8.</summary>
    public static Options Parse(string[] args, string command)
    {
        var small = command is "verify" or "check";
        var options = new Options { Command = command, Count = small ? 1_000_000 : 100_000_000 };
        if (command is "par" or "async")
        {
            options.Layout = NodeLayout.Packed;
            options.Scenes = ["orbit"];
            options.SegmentLog2 = 14;
        }

        if (command is "layout")
        {
            options.Scenes = ["orbit"];
            options.SegmentLog2 = 14;
        }

        if (command is "check")
        {
            options.SegmentLog2 = 14;
        }

        string? variants = null;
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
                case "--layout":
                    options.Layout = Next(args, ref i) switch
                    {
                        "split" => NodeLayout.Split,
                        "packed" => NodeLayout.Packed,
                        "both" => NodeLayout.Both,
                        var other => throw new ArgumentException($"Unknown layout {other}: split, packed, or both."),
                    };
                    break;
                case "--variants":
                    variants = Next(args, ref i);
                    break;
                case "--thread-cap":
                    options.ThreadCap = (int)double.Parse(Next(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--block":
                    options.Block = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--rounds":
                    options.Rounds = int.Parse(Next(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--period-ms":
                    options.PeriodMs = double.Parse(Next(args, ref i), CultureInfo.InvariantCulture);
                    break;
                default:
                    throw new ArgumentException($"Unknown option {args[i]}.");
            }
        }

        if (options.SegmentLog2 is < 2 or > 20)
        {
            throw new ArgumentException("--segment is the log2 of the vertices of a segment, 2 to 20 (a divisor of the 2^20 chunk).");
        }

        // layout compares both in one process; check compares them and the parallel walks with the reference.
        if (command is "layout" or "check")
        {
            options.Layout = NodeLayout.Both;
        }

        if (options.Layout != NodeLayout.Split && options.SegmentLog2 > 16)
        {
            throw new ArgumentException("The packed layout keeps m - v0 in 2 bytes: --segment 16 at most.");
        }

        if (options.Block < 1 || options.Rounds < 1 || options.PeriodMs <= 0 || options.ThreadCap < 1024)
        {
            throw new ArgumentException("--block and --rounds are at least 1, --period-ms above 0, --thread-cap at least 1024.");
        }

        var names = variants is not null ? variants.Split(',') : command == "async" ? _asyncVariants : _parVariants;
        options.Variants = [.. names.Select(Variant.Parse)];
        return options;
    }

    private static string Next(string[] args, ref int i) =>
        i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
}
