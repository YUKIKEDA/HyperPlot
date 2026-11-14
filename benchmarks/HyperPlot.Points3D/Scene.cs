using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace HyperPlot.Points3D;

/// <summary>The orbit scene and the determinism check (SPIKE, row K0a, Issue #477).</summary>
internal static partial class Scene
{
    private const int Width = 1920;
    private const int Height = 1080;

    /// <summary>The frames of one turn of the orbit.</summary>
    private const int Turn = 600;

    /// <summary>Runs the orbit and prints its figures; returns the exit code.</summary>
    public static int Run(ReadOnlySpan<string> args)
    {
        var method = args[0] switch
        {
            "markers" => Method.Markers,
            "compute" => Method.Compute,
            _ => throw new ArgumentException($"Unknown method {args[0]}: markers or compute."),
        };
        var count = (int)double.Parse(args[1], CultureInfo.InvariantCulture);
        var cluster = Distribution(args[2]);
        var options = Options.Parse(args[3..], method);
        var points = PointData.Make(count, cluster, options.Cells, options.Seed);
        using var gpu = new Gpu(options.Warp, Width, Height, points);
        var total = options.Warmup + options.Frames;
        var cpu = new double[total];
        var gpuTimes = new GpuTimes(total);
        TimedHandler take = gpuTimes.Take;
        var period = TimeSpan.FromMilliseconds(1000.0 / 60);

        // A wait of a few milliseconds needs the timer's finer step: by default a sleep ends on a 15.6 ms tick (.cursor/rules/bench.mdc).
        _ = TimeBeginPeriod(1);
        var framesFrom = DateTime.Now;
        var began = Stopwatch.GetTimestamp();
        try
        {
            for (var f = 0; f < total; f++)
            {
                var start = Stopwatch.GetTimestamp();
                var constants = Constants(f, count, options, gpu.Groups.X);
                gpu.Draw(method, constants, f);
                gpu.Poll(take, wait: false);
                cpu[f] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

                // A frame a period at the fastest: back to back, the GPU's clock rises and its time reads low (.cursor/rules/bench.mdc).
                var left = period - Stopwatch.GetElapsedTime(start);
                if (!options.BackToBack && left > TimeSpan.Zero)
                {
                    Thread.Sleep(left);
                }
            }
        }
        finally
        {
            _ = TimeEndPeriod(1);
        }

        var seconds = Stopwatch.GetElapsedTime(began).TotalSeconds;
        var framesTo = DateTime.Now;
        gpu.Poll(take, wait: true);

        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"method={args[0]} points={count} dist={args[2]} order={(options.Cells ? "cells" : "random")} viewport={Width}x{Height} device={(gpu.IsWarp ? "WARP" : "hardware")} diameter={options.Diameter} footprint={options.Footprint} paced={!options.BackToBack} frames={options.Frames} (after {options.Warmup}) frame_period_ms={seconds * 1000 / total:F2}"
            )
        );
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"frames ran {framesFrom:HH:mm:ss.fff} to {framesTo:HH:mm:ss.fff}"));
        Print("cpu_frame", cpu.AsSpan(options.Warmup));
        string[] stages = method == Method.Markers ? ["clear", "draw"] : ["clear", "depth_pass", "index_pass", "resolve"];
        for (var s = 0; s < stages.Length; s++)
        {
            Print($"gpu_{stages[s]}", gpuTimes.Stage(s, options.Warmup));
        }

        Print("gpu_points", gpuTimes.Points(options.Warmup, stages.Length));
        Print("gpu_total", gpuTimes.Stage(stages.Length - 1, options.Warmup, cumulative: true));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"gpu frames timed: {gpuTimes.Timed(options.Warmup)} of {options.Frames}"));
        if (options.Csv is { } csv)
        {
            gpuTimes.Write(csv, cpu, stages);
        }

        return 0;
    }

    /// <summary>Checks that each method draws the same frame to the same bytes; returns 1 when one does not.</summary>
    public static int Determinism(ReadOnlySpan<string> args)
    {
        var count = (int)double.Parse(args[0], CultureInfo.InvariantCulture);
        var cluster = Distribution(args[1]);
        var options = Options.Parse(args[2..], Method.Compute);
        var points = PointData.Make(count, cluster, options.Cells, options.Seed);
        using var gpu = new Gpu(!options.Hardware, Width, Height, points);
        var square = Math.Max(1u, options.Footprint);
        (string Name, Method Method, uint Footprint)[] cases =
        [
            ("markers_disc", Method.Markers, 0),
            ("markers_square", Method.Markers, square),
            ("compute", Method.Compute, square),
        ];
        var images = new byte[cases.Length][];
        var failed = false;
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"points={count} dist={args[1]} order={(options.Cells ? "cells" : "random")} device={(gpu.IsWarp ? "WARP" : "hardware")} frame={options.Frame} diameter={options.Diameter} square={square}"
            )
        );
        for (var c = 0; c < cases.Length; c++)
        {
            var (name, method, footprint) = cases[c];
            var caseOptions = options with { Footprint = footprint };
            var first = Draw(gpu, method, Constants(options.Frame, count, caseOptions, gpu.Groups.X));

            // Another frame between the two, so that the second does not read what the first left behind.
            _ = Draw(gpu, method, Constants(options.Frame + (Turn / 2), count, caseOptions, gpu.Groups.X));
            var second = Draw(gpu, method, Constants(options.Frame, count, caseOptions, gpu.Groups.X));
            var same = first.AsSpan().SequenceEqual(second);
            failed |= !same;
            images[c] = first;
            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{name}: repeat_identical={same} differing_pixels={Differing(first, second)} drawn_pixels={Drawn(first)} magenta={Magenta(first)} sha256={Convert.ToHexString(SHA256.HashData(first))}"
                )
            );
            if (options.Dump is { } folder)
            {
                _ = Directory.CreateDirectory(folder);
                var file = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{name}-{(gpu.IsWarp ? "warp" : "hw")}-{count}-{args[1]}-f{options.Frame}.bmp"
                );
                WriteBmp(Path.Combine(folder, file), first);
            }
        }

        // The markers' squares and compute cover the same pixels, and both keep the first of equal depths: they differ only
        // where the rasterizer's depth and the compute shader's differ in their last bits.
        Console.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"markers_square_vs_compute: differing_pixels={Differing(images[1], images[2])}")
        );
        return failed ? 1 : 0;
    }

    private static byte[] Draw(Gpu gpu, Method method, in FrameConstants constants)
    {
        gpu.Draw(method, constants, frame: -1);
        gpu.Poll(static (_, _) => { }, wait: true);
        return gpu.Read();
    }

    private static bool Distribution(string name) =>
        name switch
        {
            "uniform" => false,
            "cluster" => true,
            _ => throw new ArgumentException($"Unknown distribution {name}: uniform or cluster."),
        };

    /// <summary>The constants of a frame of the orbit (<see cref="OrbitCamera"/>).</summary>
    private static FrameConstants Constants(int frame, int count, Options options, uint groupsX)
    {
        var camera = new OrbitCamera(frame);
        return new FrameConstants
        {
            Transform = camera.Transform,
            Size = new Vector2(Width, Height),
            InverseSize = new Vector2(1f / Width, 1f / Height),
            Diameter = options.Diameter,
            Footprint = options.Footprint,
            Count = (uint)count,
            GroupsX = groupsX,
        };
    }

    private static void Print(string name, ReadOnlySpan<double> values)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{name}: n={sorted.Length} p50={Percentile(sorted, 0.5):F3} p95={Percentile(sorted, 0.95):F3} p99={Percentile(sorted, 0.99):F3} max={(sorted.Length == 0 ? double.NaN : sorted[^1]):F3} ms"
            )
        );
    }

    private static double Percentile(double[] sorted, double share) =>
        sorted.Length == 0 ? double.NaN : sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * share))];

    private static int Differing(byte[] a, byte[] b)
    {
        var left = MemoryMarshal.Cast<byte, uint>(a);
        var right = MemoryMarshal.Cast<byte, uint>(b);
        var differing = 0;
        for (var i = 0; i < left.Length; i++)
        {
            differing += left[i] != right[i] ? 1 : 0;
        }

        return differing;
    }

    private static int Drawn(byte[] image)
    {
        var pixels = MemoryMarshal.Cast<byte, uint>(image);
        var drawn = 0;
        foreach (var pixel in pixels)
        {
            drawn += pixel != 0xFFFFFFFF ? 1 : 0;
        }

        return drawn;
    }

    // B's resolve paints a pixel magenta (BGRA FF 00 FF FF) when it has a depth and no index.
    private static int Magenta(byte[] image)
    {
        var pixels = MemoryMarshal.Cast<byte, uint>(image);
        var magenta = 0;
        foreach (var pixel in pixels)
        {
            magenta += pixel == 0xFFFF00FF ? 1 : 0;
        }

        return magenta;
    }

    // A 32-bit BMP, top-down: the BGRA bytes as they are.
    private static void WriteBmp(string path, byte[] pixels)
    {
        using var file = File.Create(path);
        using var writer = new BinaryWriter(file);
        writer.Write((ushort)0x4D42);
        writer.Write(14 + 40 + pixels.Length);
        writer.Write(0);
        writer.Write(14 + 40);
        writer.Write(40);
        writer.Write(Width);
        writer.Write(-Height);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(pixels.Length);
        writer.Write(2835);
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);
        writer.Write(pixels);
    }

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint period);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static partial uint TimeEndPeriod(uint period);

    /// <summary>The GPU times of the frames, by frame: milliseconds from each frame's first timestamp to each later one.</summary>
    private sealed class GpuTimes(int frames)
    {
        private const int Stages = Gpu.MaxStamps - 1;

        private readonly double[] _times = new double[frames * Stages];
        private readonly bool[] _timed = new bool[frames];

        public void Take(long frame, ReadOnlySpan<double> times)
        {
            if (frame < 0 || frame >= _timed.Length)
            {
                return;
            }

            _timed[frame] = true;
            for (var s = 0; s < times.Length; s++)
            {
                _times[(frame * Stages) + s] = times[s];
            }
        }

        public int Timed(int from) => _timed.AsSpan(from).Count(true);

        /// <summary>The time of stage s (0 the clears, then each pass) of each timed frame from a frame on; with cumulative, from the frame's start.</summary>
        public double[] Stage(int s, int from, bool cumulative = false)
        {
            var values = new List<double>();
            for (var f = from; f < _timed.Length; f++)
            {
                if (_timed[f])
                {
                    values.Add(cumulative || s == 0 ? _times[(f * Stages) + s] : _times[(f * Stages) + s] - _times[(f * Stages) + s - 1]);
                }
            }

            return [.. values];
        }

        /// <summary>The time of the point passes together: from the end of the clears to the end of the last pass.</summary>
        public double[] Points(int from, int stages)
        {
            var values = new List<double>();
            for (var f = from; f < _timed.Length; f++)
            {
                if (_timed[f])
                {
                    values.Add(_times[(f * Stages) + stages - 1] - _times[(f * Stages) + 0]);
                }
            }

            return [.. values];
        }

        public void Write(string path, double[] cpu, string[] stages)
        {
            using var writer = new StreamWriter(path);
            writer.WriteLine($"frame,cpu_ms,{string.Join(',', stages.Select(s => $"gpu_{s}_end_ms"))}");
            for (var f = 0; f < _timed.Length; f++)
            {
                var gpu = _timed[f]
                    ? string.Join(
                        ',',
                        Enumerable.Range(0, stages.Length).Select(s => _times[(f * Stages) + s].ToString("F4", CultureInfo.InvariantCulture))
                    )
                    : new string(',', stages.Length - 1);
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{f},{cpu[f]:F4},{gpu}"));
            }
        }
    }

    /// <summary>The options after the positional arguments.</summary>
    private sealed record Options
    {
        public int Frames { get; init; } = Turn;

        public int Warmup { get; init; } = 120;

        public float Diameter { get; init; } = 2;

        public uint Footprint { get; init; }

        public bool Cells { get; init; }

        public bool BackToBack { get; init; }

        public bool Warp { get; init; }

        public bool Hardware { get; init; }

        public string? Csv { get; init; }

        public string? Dump { get; init; }

        public int Frame { get; init; } = 150;

        public int Seed { get; init; } = 1;

        public static Options Parse(ReadOnlySpan<string> args, Method method)
        {
            var options = new Options { Footprint = method == Method.Compute ? 1u : 0u };
            for (var i = 0; i < args.Length; i++)
            {
                options = args[i] switch
                {
                    "--frames" => options with { Frames = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--warmup" => options with { Warmup = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--diameter" => options with { Diameter = float.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--footprint" => options with { Footprint = uint.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--order" => options with { Cells = args[++i] == "cells" },
                    "--back-to-back" => options with { BackToBack = true },
                    "--warp" => options with { Warp = true },
                    "--hardware" => options with { Hardware = true },
                    "--csv" => options with { Csv = args[++i] },
                    "--dump" => options with { Dump = args[++i] },
                    "--frame" => options with { Frame = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--seed" => options with { Seed = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                    _ => throw new ArgumentException($"Unknown option {args[i]}."),
                };
            }

            if (method == Method.Compute && options.Footprint == 0)
            {
                throw new ArgumentException("compute draws squares: --footprint 1 or more.");
            }

            return options;
        }
    }
}
