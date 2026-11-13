using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace HyperPlot.Volume;

/// <summary>The orbit, the zoom, and the determinism check (SPIKE, row K0e, Issue #491).</summary>
internal static partial class Scene
{
    private const int Width = 1920;
    private const int Height = 1080;

    /// <summary>The frames of one turn of the orbit.</summary>
    private const int Turn = 600;

    /// <summary>The upload budget of a frame the design gives the residency cache (design §5.2, ResidencyCache.FrameUpload).</summary>
    private const long FrameUpload = 4 << 20;

    private static readonly Vector3 Center = new(0.5f);

    /// <summary>Writes a seeded field to a file.</summary>
    public static int Generate(ReadOnlySpan<string> args)
    {
        var size = int.Parse(args[0], CultureInfo.InvariantCulture);
        var field = args[1] switch
        {
            "smooth" => Field.Smooth,
            "noisy" => Field.Noisy,
            _ => throw new ArgumentException($"Unknown field {args[1]}: smooth or noisy."),
        };
        var seed = 1UL;
        for (var i = 3; i < args.Length; i++)
        {
            seed =
                args[i] == "--seed"
                    ? ulong.Parse(args[++i], CultureInfo.InvariantCulture)
                    : throw new ArgumentException($"Unknown option {args[i]}.");
        }

        VolumeFile.Generate(args[2], size, field, seed);
        return 0;
    }

    /// <summary>Runs the orbit and prints its figures; returns the exit code.</summary>
    public static int Run(ReadOnlySpan<string> args)
    {
        var options = Options.Parse(args[1..]);
        using var setup = Setup.Load(args[0], options, warp: options.Warp);
        var (store, gpu, residency) = (setup.Store, setup.Gpu, setup.Residency);
        var total = options.Warmup + options.Frames;
        var cpu = new double[total];
        var select = new double[total];
        var stats = new FrameStats[total];
        var sent = new long[total];
        var gpuTimes = new GpuTimes(total);
        TimedHandler take = gpuTimes.Take;
        var constants = gpu.Fixed(options.Threshold);

        // The first frame's bricks all come before the orbit, as a headless draw waits for them (decision 2 of Issue #484).
        var first = View.Orbit(0, Turn, Width, Height, 1, Center);
        _ = residency.Select(first, 0, 0);
        _ = gpu.Draw(With(constants, first), residency, -1);
        gpu.Poll(static (_, _) => { }, wait: true);

        var period = TimeSpan.FromMilliseconds(options.PeriodMs);

        // A wait of a few milliseconds needs the timer's finer step: by default a sleep ends on a 15.6 ms tick (.cursor/rules/bench.mdc).
        _ = TimeBeginPeriod(1);
        var framesFrom = DateTime.Now;
        var began = Stopwatch.GetTimestamp();
        try
        {
            for (var f = 0; f < total; f++)
            {
                var start = Stopwatch.GetTimestamp();
                var view = View.Orbit(f, Turn, Width, Height, 1, Center);
                stats[f] = residency.Select(view, f + 1, options.UploadBytes);
                select[f] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                sent[f] = gpu.Draw(With(constants, view), residency, f).Bricks;
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

        PrintHeader("orbit", setup, options, gpu);
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"frames={options.Frames} (after {options.Warmup}) paced={!options.BackToBack} period_ms={options.PeriodMs:F1} frame_period_ms={seconds * 1000 / total:F2} ran {framesFrom:HH:mm:ss.fff} to {framesTo:HH:mm:ss.fff}"
            )
        );
        PrintMemory(setup, stats.AsSpan(options.Warmup));
        PrintLevels(residency, store);
        Print("cpu_frame", cpu.AsSpan(options.Warmup));
        Print("cpu_select", select.AsSpan(options.Warmup));
        string[] stages = ["clear", "upload", "march"];
        for (var s = 0; s < stages.Length; s++)
        {
            Print($"gpu_{stages[s]}", gpuTimes.Stage(s, options.Warmup));
        }

        var gpuTotal = gpuTimes.Stage(stages.Length - 1, options.Warmup, cumulative: true);
        Print("gpu_total", gpuTotal);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"gpu frames timed: {gpuTimes.Timed(options.Warmup)} of {options.Frames}"));
        var p95 = Percentile(Sorted(gpuTotal), 0.95);
        var cpu95 = Percentile(Sorted(cpu.AsSpan(options.Warmup)), 0.95);
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"p14: target_ms=33.3 gpu_total_p95={p95:F3} cpu_frame_p95={cpu95:F3} within={Math.Max(p95, cpu95) <= 1000.0 / 30}"
            )
        );
        PrintUploads(stats.AsSpan(options.Warmup), sent.AsSpan(options.Warmup), store.BrickBytes);
        if (options.Csv is { } csv)
        {
            gpuTimes.Write(csv, cpu, select, stats, sent, stages);
        }

        return 0;
    }

    /// <summary>
    /// Counts the frames until the view is no longer provisional, within the upload budget: from nothing to the whole volume
    /// (cold), and from the whole volume resident to each zoom about the center.
    /// </summary>
    public static int Zoom(ReadOnlySpan<string> args)
    {
        var options = Options.Parse(args[1..]);
        using var setup = Setup.Load(args[0], options, warp: options.Warp);
        var (store, gpu) = (setup.Store, setup.Gpu);
        var constants = gpu.Fixed(options.Threshold);
        PrintHeader("zoom", setup, options, gpu);
        var period = TimeSpan.FromMilliseconds(options.PeriodMs);
        var gpuTimes = new GpuTimes(options.MaxFrames + 1);
        TimedHandler take = gpuTimes.Take;
        _ = TimeBeginPeriod(1);
        try
        {
            var cases = new List<(string Name, float Zoom)> { ("cold", 1) };
            foreach (var factor in options.Factors)
            {
                cases.Add((string.Create(CultureInfo.InvariantCulture, $"zoom{factor}"), factor));
            }

            foreach (var (name, zoom) in cases)
            {
                // A fresh cache each case; the atlas's old contents are never read, as the table names only what was sent.
                var residency = new Residency(store, setup.Transfer, setup.Capacity);
                var whole = View.Orbit(options.AngleFrame, Turn, Width, Height, 1, Center);
                long frame = 1;
                if (name != "cold")
                {
                    _ = residency.Select(whole, frame++, 0);
                    _ = gpu.Draw(With(constants, whole), residency, -1);
                    gpu.Poll(static (_, _) => { }, wait: true);
                }

                var view = View.Orbit(options.AngleFrame, Turn, Width, Height, zoom, Center);
                gpuTimes.Clear();
                var (frames, bytes, demand, selectedBytes) = (0, 0L, 0L, 0L);
                var provisional = true;
                while (provisional && frames < options.MaxFrames)
                {
                    var start = Stopwatch.GetTimestamp();
                    var stats = residency.Select(view, frame++, options.UploadBytes);
                    if (frames == 0)
                    {
                        demand = stats.Missing * store.BrickBytes;
                        selectedBytes = stats.Selected * store.BrickBytes;
                    }

                    bytes += gpu.Draw(With(constants, view), residency, frames).Bricks;
                    gpu.Poll(take, wait: false);
                    frames++;
                    provisional = stats.Provisional;
                    var left = period - Stopwatch.GetElapsedTime(start);
                    if (!options.BackToBack && left > TimeSpan.Zero)
                    {
                        Thread.Sleep(left);
                    }
                }

                gpu.Poll(take, wait: true);
                var times = gpuTimes.Stage(2, 0, cumulative: true);
                Console.WriteLine(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{name}: frames_until_final={(provisional ? "none" : frames.ToString(CultureInfo.InvariantCulture))} (the final frame counted; max {options.MaxFrames}) selected_mb={selectedBytes / 1048576.0:F1} demand_mb={demand / 1048576.0:F1} sent_mb={bytes / 1048576.0:F1} budget_mb={options.UploadBytes / 1048576.0:F1} gpu_total_max={(times.Length == 0 ? double.NaN : times.Max()):F3} ms"
                    )
                );
            }
        }
        finally
        {
            _ = TimeEndPeriod(1);
        }

        return 0;
    }

    /// <summary>
    /// Draws one frame after two histories that put its bricks in different slots, and compares the images byte for byte:
    /// A draws frame f, another, then f again (the same slots); B draws two other frames first (other slots). Returns 1 when
    /// an image differs.
    /// </summary>
    public static int Determinism(ReadOnlySpan<string> args)
    {
        var options = Options.Parse(args[1..]);
        using var setup = Setup.Load(args[0], options, warp: !options.Hardware);
        var gpu = setup.Gpu;
        var constants = gpu.Fixed(options.Threshold);
        PrintHeader("determinism", setup, options, gpu);
        var f = options.Frame;

        var a = new Residency(setup.Store, setup.Transfer, setup.Capacity);
        var (a1, tableA) = Draw(gpu, a, constants, f, 1);
        _ = Draw(gpu, a, constants, f + (Turn / 2), 2);
        var (a2, _) = Draw(gpu, a, constants, f, 3);

        var b = new Residency(setup.Store, setup.Transfer, setup.Capacity);
        _ = Draw(gpu, b, constants, f + (Turn / 2), 1);
        _ = Draw(gpu, b, constants, f + (Turn / 4), 2);
        var (b1, tableB) = Draw(gpu, b, constants, f, 3);

        var repeat = a1.AsSpan().SequenceEqual(a2);
        var history = a1.AsSpan().SequenceEqual(b1);
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"frame={f} repeat_identical={repeat} repeat_differing_pixels={Differing(a1, a2)} other_history_identical={history} other_history_differing_pixels={Differing(a1, b1)} slots_differ={!tableA.AsSpan().SequenceEqual(tableB)} drawn_pixels={Drawn(a1)}"
            )
        );
        Console.WriteLine($"sha256 A={Convert.ToHexString(SHA256.HashData(a1))} B={Convert.ToHexString(SHA256.HashData(b1))}");
        if (options.Dump is { } folder)
        {
            _ = Directory.CreateDirectory(folder);
            var file = string.Create(
                CultureInfo.InvariantCulture,
                $"{Path.GetFileNameWithoutExtension(args[0])}-{options.Brick}-{options.Transfer}-{options.Filter}-{(gpu.IsWarp ? "warp" : "hw")}-f{f}.bmp"
            );
            WriteBmp(Path.Combine(folder, file), a1);
        }

        return repeat && history ? 0 : 1;
    }

    // Draws frame f of the orbit with every brick resident (no upload budget), waits, and reads the image and the table.
    private static (byte[] Image, uint[] Table) Draw(Gpu gpu, Residency residency, in FrameConstants constants, int f, long frame)
    {
        var view = View.Orbit(f, Turn, Width, Height, 1, Center);
        var stats = residency.Select(view, frame, 0);
        if (stats.Provisional)
        {
            throw new InvalidOperationException($"Frame {f} is provisional with no upload budget: the atlas is too small.");
        }

        _ = gpu.Draw(With(constants, view), residency, -1);
        gpu.Poll(static (_, _) => { }, wait: true);
        return (gpu.Read(), [.. residency.Table]);
    }

    private static FrameConstants With(in FrameConstants constants, in View view)
    {
        _ = Matrix4x4.Invert(view.Transform, out var inverse);
        var result = constants;
        result.InverseTransform = inverse;
        result.Eye = new Vector4(view.Eye, 1);
        result.RangeLow = new Vector4(view.RangeLow, 0);
        result.RangeHigh = new Vector4(view.RangeHigh, 0);
        return result;
    }

    private static void PrintHeader(string scene, Setup setup, Options options, Gpu gpu) =>
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"scene={scene} size={setup.Store.Size} field={setup.Field} seed={setup.Seed} brick={options.Brick} ghost=1 levels={setup.Store.Levels} bricks={setup.Store.Count} tf={options.Transfer} threshold={options.Threshold} filter={options.Filter} viewport={Width}x{Height} device={(gpu.IsWarp ? "WARP" : "hardware")} upload_budget_mb={options.UploadBytes / 1048576.0:F1} atlases={gpu.Layout.Atlases} slots={gpu.Layout.Capacity} load_seconds={setup.LoadSeconds:F1} build_seconds={setup.BuildSeconds:F1}"
            )
        );

    // The memory of the levels and the ghosts against the data (requirement P-8), and the GPU's.
    private static void PrintMemory(Setup setup, ReadOnlySpan<FrameStats> stats)
    {
        var store = setup.Store;
        var raw = (long)store.Size * store.Size * store.Size * sizeof(ushort);
        var visible = 0;
        for (var id = 0; id < store.Count; id++)
        {
            visible += setup.Transfer.IsEmpty(store.Min[id], store.Max[id]) ? 0 : 1;
        }

        var (selectedMax, residentMax) = (0, 0);
        foreach (var s in stats)
        {
            selectedMax = Math.Max(selectedMax, s.Selected);
            residentMax = Math.Max(residentMax, s.Resident);
        }

        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"memory: raw_mb={raw / 1048576.0:F1} levels_mb={store.LevelBytes / 1048576.0:F1} ({100.0 * store.LevelBytes / raw:F1} % of raw) bricked_mb={store.BrickedBytes / 1048576.0:F1} ({100.0 * store.BrickedBytes / raw:F1} % of raw, levels and ghosts) visible_bricks={visible} of {store.Count} visible_mb={visible * store.BrickBytes / 1048576.0:F1} ({100.0 * visible * store.BrickBytes / raw:F1} % of raw) gpu_volume_mb={setup.Gpu.VolumeBytes / 1048576.0:F1} selected_max_mb={selectedMax * store.BrickBytes / 1048576.0:F1} resident_max_mb={residentMax * store.BrickBytes / 1048576.0:F1}"
            )
        );
    }

    private static void PrintLevels(Residency residency, BrickStore store)
    {
        var counts = string.Join(
            ' ',
            residency.LevelCounts.Select(
                (count, level) =>
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"L{level}={count}/{store.LevelBricks[level] * store.LevelBricks[level] * store.LevelBricks[level]}"
                    )
            )
        );
        Console.WriteLine($"levels selected in the last frame (empty ones included): {counts}");
    }

    private static void PrintUploads(ReadOnlySpan<FrameStats> stats, ReadOnlySpan<long> sent, long brickBytes)
    {
        var demand = new double[stats.Length];
        var uploaded = new double[stats.Length];
        var (provisional, starved, overBudget) = (0, 0, 0);
        for (var f = 0; f < stats.Length; f++)
        {
            demand[f] = stats[f].Missing * brickBytes / 1048576.0;
            uploaded[f] = sent[f] / 1048576.0;
            provisional += stats[f].Provisional ? 1 : 0;
            starved += stats[f].Starved ? 1 : 0;
            overBudget += stats[f].Missing * brickBytes > FrameUpload ? 1 : 0;
        }

        Print("upload_demand_mb", demand, "MB");
        Print("upload_sent_mb", uploaded, "MB");
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"frames: provisional={provisional} starved={starved} demand_over_4mb={overBudget} of {stats.Length}; brick_kb={brickBytes / 1024.0:F1}"
            )
        );
    }

    private static void Print(string name, ReadOnlySpan<double> values, string unit = "ms")
    {
        var sorted = Sorted(values);
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{name}: n={sorted.Length} p50={Percentile(sorted, 0.5):F3} p95={Percentile(sorted, 0.95):F3} p99={Percentile(sorted, 0.99):F3} max={(sorted.Length == 0 ? double.NaN : sorted[^1]):F3} {unit}"
            )
        );
    }

    private static double[] Sorted(ReadOnlySpan<double> values)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        return sorted;
    }

    private static double Percentile(double[] sorted, double share) =>
        sorted.Length == 0 ? double.NaN : sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * share))];

    private static int Differing(byte[] a, byte[] b)
    {
        var (left, right) = (MemoryMarshal.Cast<byte, uint>(a), MemoryMarshal.Cast<byte, uint>(b));
        var differing = 0;
        for (var i = 0; i < left.Length; i++)
        {
            differing += left[i] != right[i] ? 1 : 0;
        }

        return differing;
    }

    private static int Drawn(byte[] image)
    {
        var drawn = 0;
        foreach (var pixel in MemoryMarshal.Cast<byte, uint>(image))
        {
            drawn += pixel != 0xFFFFFFFF ? 1 : 0;
        }

        return drawn;
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

    /// <summary>The volume read, cut into bricks, and put on a device with an atlas and a residency.</summary>
    private sealed class Setup : IDisposable
    {
        private Setup(BrickStore store, Gpu gpu, TransferFunction transfer, int capacity)
        {
            (Store, Gpu, Transfer, Capacity) = (store, gpu, transfer, capacity);
            Residency = new Residency(store, transfer, capacity);
        }

        public BrickStore Store { get; }

        public Gpu Gpu { get; }

        public TransferFunction Transfer { get; }

        public int Capacity { get; }

        public Residency Residency { get; }

        public Field Field { get; private init; }

        public ulong Seed { get; private init; }

        public double LoadSeconds { get; private init; }

        public double BuildSeconds { get; private init; }

        public static Setup Load(string path, Options options, bool warp)
        {
            var started = Stopwatch.GetTimestamp();
            Field field;
            ulong seed;
            BrickStore store;
            double loadSeconds;
            using (var volume = VolumeFile.Read(path, out field, out seed))
            {
                loadSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
                started = Stopwatch.GetTimestamp();
                store = BrickStore.Build(volume, options.Brick);
            }

            var buildSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            try
            {
                var transfer = TransferFunction.Named(options.Transfer);
                var wanted = (int)Math.Min(store.Count, options.AtlasBytes / store.BrickBytes);
                var layout = AtlasLayout.For(store.Side, store.BrickBytes, wanted);
                var shader = Path.Combine(AppContext.BaseDirectory, "Shaders", "Volume.hlsl");
                var gpu = new Gpu(warp, Width, Height, store, transfer, layout, options.Filter == "load", shader);
                return new Setup(store, gpu, transfer, Math.Min(layout.Capacity, (1 << 24) - 2))
                {
                    Field = field,
                    Seed = seed,
                    LoadSeconds = loadSeconds,
                    BuildSeconds = buildSeconds,
                };
            }
            catch
            {
                store.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Gpu.Dispose();
            Store.Dispose();
        }
    }

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

        public void Clear() => Array.Clear(_timed);

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

        public void Write(string path, double[] cpu, double[] select, FrameStats[] stats, long[] sent, string[] stages)
        {
            using var writer = new StreamWriter(path);
            writer.WriteLine(
                $"frame,cpu_ms,select_ms,selected,missing,uploaded,sent_bytes,provisional,{string.Join(',', stages.Select(s => $"gpu_{s}_end_ms"))}"
            );
            for (var f = 0; f < _timed.Length; f++)
            {
                var gpu = _timed[f]
                    ? string.Join(
                        ',',
                        Enumerable.Range(0, stages.Length).Select(s => _times[(f * Stages) + s].ToString("F4", CultureInfo.InvariantCulture))
                    )
                    : new string(',', stages.Length - 1);
                var s = stats[f];
                writer.WriteLine(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{f},{cpu[f]:F4},{select[f]:F4},{s.Selected},{s.Missing},{s.Uploaded},{sent[f]},{s.Provisional},{gpu}"
                    )
                );
            }
        }
    }

    /// <summary>The options after the positional arguments.</summary>
    private sealed record Options
    {
        public int Brick { get; init; } = 64;

        public string Transfer { get; init; } = "translucent";

        public float Threshold { get; init; } = 0.99f;

        public string Filter { get; init; } = "sample";

        public int Frames { get; init; } = Turn;

        public int Warmup { get; init; } = 120;

        public long UploadBytes { get; init; } = FrameUpload;

        public long AtlasBytes { get; init; } = 6144L << 20;

        public double PeriodMs { get; init; } = 1000.0 / 30;

        public bool BackToBack { get; init; }

        public bool Warp { get; init; }

        public bool Hardware { get; init; }

        public string? Csv { get; init; }

        public string? Dump { get; init; }

        public int Frame { get; init; } = 150;

        public int AngleFrame { get; init; }

        public int MaxFrames { get; init; } = 2000;

        public float[] Factors { get; init; } = [2, 4, 8];

        public static Options Parse(ReadOnlySpan<string> args)
        {
            var options = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                options = args[i] switch
                {
                    "--brick" => options with { Brick = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--tf" => options with { Transfer = args[++i] },
                    "--threshold" => options with { Threshold = float.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--filter" => options with
                    {
                        Filter = args[++i] is "sample" or "load" ? args[i] : throw new ArgumentException("--filter sample or load."),
                    },
                    "--frames" => options with { Frames = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--warmup" => options with { Warmup = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--upload-mb" => options with { UploadBytes = (long)(double.Parse(args[++i], CultureInfo.InvariantCulture) * 1048576) },
                    "--atlas-mb" => options with { AtlasBytes = long.Parse(args[++i], CultureInfo.InvariantCulture) << 20 },
                    "--period-ms" => options with { PeriodMs = double.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--back-to-back" => options with { BackToBack = true },
                    "--warp" => options with { Warp = true },
                    "--hardware" => options with { Hardware = true },
                    "--csv" => options with { Csv = args[++i] },
                    "--dump" => options with { Dump = args[++i] },
                    "--frame" => options with { Frame = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--angle-frame" => options with { AngleFrame = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--max-frames" => options with { MaxFrames = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--factors" => options with { Factors = [.. args[++i].Split(',').Select(f => float.Parse(f, CultureInfo.InvariantCulture))] },
                    _ => throw new ArgumentException($"Unknown option {args[i]}."),
                };
            }

            if (options.Brick is not (16 or 32 or 64 or 128))
            {
                throw new ArgumentException("--brick 16, 32, 64, or 128.");
            }

            _ = TransferFunction.Named(options.Transfer);
            return options;
        }
    }
}
