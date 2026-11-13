using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;

namespace HyperPlot.SurfaceLod;

/// <summary>The orbit scene and the image check (SPIKE, row K0c, Issue #489).</summary>
internal static partial class Scene
{
    private const int Width = 1920;
    private const int Height = 1080;

    /// <summary>The frames of one turn of the orbit.</summary>
    private const int Turn = 600;

    /// <summary>The vertical field of view, as row K0a's orbit (design §13.8, Issue #470's decision 16).</summary>
    private const float FieldOfView = MathF.PI / 4;

    /// <summary>The upload of a frame the residency cache allows (design §5.1).</summary>
    private const long UploadBudget = 4L * 1024 * 1024;

    /// <summary>Runs the orbit and prints its figures; returns the exit code.</summary>
    public static int Run(ReadOnlySpan<string> args)
    {
        var (tile, shape, uniform) = Positional(args);
        var options = Options.Parse(args[3..]);
        var data = new SurfaceData(shape, uniform, tile, options.Noise, options.Spikes, options.Seed);
        var (faces, budget) = Pool(data, options);
        using var gpu = new Gpu(options.Warp, Width, Height, options.Face, faces, data.NodeCount, data);
        var selector = new Selector(data);
        var residency = new Residency(data, gpu, options.Face, faces);
        PrintSetup(args, data, gpu, residency, options, faces, budget);
        if (gpu.StitchProblem is { } problem)
        {
            Console.Error.WriteLine($"stitch check failed: {problem}");
            return 1;
        }

        var total = options.Warmup + options.Frames;
        var record = new Record(total, data);
        var gpuTimes = new GpuTimes(total);
        TimedHandler take = gpuTimes.Take;
        var period = TimeSpan.FromMilliseconds(1000.0 / 60);
        var (zScale, _) = Selector.ZToBox(data);
        var factor = Pixels() * zScale / options.Error;

        // A wait of a few milliseconds needs the timer's finer step: by default a sleep ends on a 15.6 ms tick (.cursor/rules/bench.mdc).
        _ = TimeBeginPeriod(1);
        var framesFrom = DateTime.Now;
        var began = Stopwatch.GetTimestamp();
        var violations = 0;
        try
        {
            for (var f = 0; f < total; f++)
            {
                var start = Stopwatch.GetTimestamp();
                var constants = Constants(f, data);
                selector.Select(constants.Eye, constants.Transform, factor, budget, !options.NoStitch);
                var selected = Stopwatch.GetTimestamp();
                var set = gpu.Begin(f);
                residency.Prepare(selector, f);
                var prepared = Stopwatch.GetTimestamp();
                gpu.Draw(set, constants, residency.Instances.AsSpan(0, residency.Written), residency.Starts, residency.Counts);
                gpu.Poll(take, wait: false);
                var end = Stopwatch.GetTimestamp();
                record.Take(f, start, selected, prepared, end, selector, residency, gpu);

                // Not timed: the check that the restriction holds.
                violations += selector.Violations();

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
                $"paced={!options.BackToBack} frames={options.Frames} (after {options.Warmup}) frame_period_ms={seconds * 1000 / total:F2} restriction_violations={violations}"
            )
        );
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"frames ran {framesFrom:HH:mm:ss.fff} to {framesTo:HH:mm:ss.fff}"));
        record.Print(options.Warmup, gpuTimes, residency.TileBytes);
        if (options.Csv is { } csv)
        {
            record.Write(csv, gpuTimes);
        }

        return 0;
    }

    /// <summary>
    /// Draws one frame of the orbit and writes it as a BMP; counts the magenta pixels (the clear color) that have surface
    /// pixels on both sides along a row or a column, which is how a crack between two tiles shows. Returns 1 when the
    /// stitch check fails.
    /// </summary>
    public static int Image(ReadOnlySpan<string> args)
    {
        var (tile, shape, uniform) = Positional(args);
        var options = Options.Parse(args[3..]);
        var data = new SurfaceData(shape, uniform, tile, options.Noise, options.Spikes, options.Seed);
        var (faces, budget) = Pool(data, options);
        using var gpu = new Gpu(!options.Hardware, Width, Height, options.Face, faces, data.NodeCount, data);
        var selector = new Selector(data);
        var residency = new Residency(data, gpu, options.Face, faces);
        PrintSetup(args, data, gpu, residency, options, faces, budget);
        var (zScale, _) = Selector.ZToBox(data);
        var constants = Constants(options.Frame, data);
        selector.Select(constants.Eye, constants.Transform, Pixels() * zScale / options.Error, budget, !options.NoStitch);
        var set = gpu.Begin(options.Frame);
        residency.Prepare(selector, options.Frame);
        gpu.Draw(set, constants, residency.Instances.AsSpan(0, residency.Written), residency.Starts, residency.Counts);
        gpu.Poll(static (_, _) => { }, wait: true);
        var image = gpu.Read();
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"frame={options.Frame} stitch={!options.NoStitch} tiles={selector.Count} missing={residency.Missing} restriction_violations={selector.Violations()} enclosed_background_pixels={Enclosed(image)}"
            )
        );
        if (options.Out is { } path)
        {
            WriteBmp(path, image);
        }

        return gpu.StitchProblem is null ? 0 : 1;
    }

    private static (int Tile, Shape Shape, bool Uniform) Positional(ReadOnlySpan<string> args)
    {
        var tile = int.Parse(args[0], CultureInfo.InvariantCulture);
        var shape = args[1] switch
        {
            "smooth" => Shape.Smooth,
            "noisy" => Shape.Noisy,
            "spikes" => Shape.Spikes,
            _ => throw new ArgumentException($"Unknown data {args[1]}: smooth, noisy, or spikes."),
        };
        var uniform = args[2] switch
        {
            "uniform" => true,
            "nonuniform" => false,
            _ => throw new ArgumentException($"Unknown grid {args[2]}: uniform or nonuniform."),
        };
        return (tile, shape, uniform);
    }

    /// <summary>
    /// The faces the pool's bytes allow (at most 2,048, D3D11's array limit), and the tile budget of the selection: the
    /// option, or three quarters of the places, leaving room for the tiles the restriction adds.
    /// </summary>
    private static (int Faces, int Budget) Pool(SurfaceData data, Options options)
    {
        var perSide = options.Face / (data.Tile + 1);
        if (perSide == 0)
        {
            throw new ArgumentException($"A face of {options.Face} does not hold a tile of {data.Tile + 1} vertices.");
        }

        var faceBytes = (long)options.Face * options.Face * sizeof(float);
        var faces = (int)Math.Min(2048, options.PoolMegabytes * 1024L * 1024 / faceBytes);
        var places = faces * perSide * perSide;
        var budget = options.TileBudget > 0 ? options.TileBudget : places * 3 / 4;
        return (faces, Math.Min(budget, data.NodeCount));
    }

    // K of Ulrich's ρ = K·δ / D: pixels per unit of size at distance 1, H / (2·tan(FOVy / 2)).
    private static float Pixels() => Height / (2 * MathF.Tan(FieldOfView / 2));

    /// <summary>
    /// The constants of a frame of the orbit: the camera turns about the box's vertical axis (z), 360° every 600 frames, 30°
    /// above the horizon, at the distance where the box's bounding sphere (radius √3) fits the vertical field of view of
    /// 45° (design §13.8, decision 16), as row K0a's orbit. The near and far planes hold the sphere.
    /// </summary>
    private static FrameConstants Constants(int frame, SurfaceData data)
    {
        const float Elevation = MathF.PI / 6;
        var angle = 2 * MathF.PI * (frame % Turn) / Turn;
        var radius = MathF.Sqrt(3);
        var distance = radius / MathF.Sin(FieldOfView / 2);
        var eye = new Vector3(MathF.Cos(Elevation) * MathF.Cos(angle), MathF.Cos(Elevation) * MathF.Sin(angle), MathF.Sin(Elevation)) * distance;
        var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitZ);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, Width / (float)Height, distance - radius - 0.1f, distance + radius + 0.1f);
        var (zScale, zOffset) = Selector.ZToBox(data);
        return new FrameConstants
        {
            Transform = view * projection,
            Eye = eye,
            ZScale = zScale,
            Light = Vector3.Normalize(new Vector3(0.4f, 0.3f, 0.85f)),
            ZOffset = zOffset,
            XUniform = new Vector2(-1, 2f / SurfaceData.Cells),
            YUniform = new Vector2(-1, 2f / SurfaceData.Cells),
            Side = (uint)(data.Tile + 1),
            Uniform = data.Uniform ? 1u : 0u,
        };
    }

    private static void PrintSetup(ReadOnlySpan<string> args, SurfaceData data, Gpu gpu, Residency residency, Options options, int faces, int budget)
    {
        var faceBytes = (long)options.Face * options.Face * sizeof(float);
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"tile={data.Tile} data={args[1]} grid={args[2]} cells={SurfaceData.Cells}x{SurfaceData.Cells} samples={SurfaceData.Samples}x{SurfaceData.Samples} viewport={Width}x{Height} device={(gpu.IsWarp ? "WARP" : "hardware")} error_px={options.Error} noise={options.Noise} spikes={options.Spikes} seed={options.Seed} z=[{data.ZMin:G6}, {data.ZMax:G6}]"
            )
        );
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"build generate_s={data.GenerateSeconds:F2} levels_s={data.LevelSeconds:F2} errors_s={data.ErrorSeconds:F2} levels={data.TopLevel + 1} tiles={data.NodeCount} stitch_check={gpu.StitchProblem ?? "ok"} index_bytes={gpu.IndexBytes}"
            )
        );
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"pool face={options.Face}x{options.Face} tiles_per_face={residency.PerFace} face_use={(double)residency.PerFace * (data.Tile + 1) * (data.Tile + 1) / (options.Face * options.Face):P1} faces={faces} pool_bytes={faces * faceBytes} places={residency.Places} tile_budget={budget}"
            )
        );

        // The memory of the levels over the original grid (Issue #482, decision 2; the estimate is 1/4 + 1/16 + … ≈ 33 %).
        var original = (long)SurfaceData.Samples * SurfaceData.Samples * sizeof(float);
        long levels = 0;
        for (var k = 1; k <= data.TopLevel; k++)
        {
            levels += (long)SurfaceData.LevelWidth(k) * SurfaceData.LevelWidth(k) * sizeof(float);
        }

        long pyramid = 0;
        for (var k = 1; SurfaceData.LevelWidth(k) > 2; k++)
        {
            pyramid += (long)SurfaceData.LevelWidth(k) * SurfaceData.LevelWidth(k) * sizeof(float);
        }

        var vertexBytes = (long)(data.Tile + 1) * (data.Tile + 1) * sizeof(float);
        var level0Tiles = data.LevelSide[0] * data.LevelSide[0];
        var upperTiles = data.NodeCount - level0Tiles;
        var upperFaces = (upperTiles + residency.PerFace - 1) / residency.PerFace;
        var allFaces = (data.NodeCount + residency.PerFace - 1) / residency.PerFace;
        var table = (long)data.NodeCount * 3 * sizeof(float);
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"memory original_bytes={original} levels_bytes={levels} ({(double)levels / original:P2}, levels 1 to {data.TopLevel}) pyramid_bytes={pyramid} ({(double)pyramid / original:P2}, every level to 2 samples) upper_tiles_bytes={upperTiles * vertexBytes} ({(double)upperTiles * vertexBytes / original:P2}, tiles of levels 1 up with shared edges copied) upper_faces_bytes={upperFaces * faceBytes} ({(double)upperFaces * faceBytes / original:P2}) all_faces_bytes={allFaces * faceBytes} ({(double)allFaces * faceBytes / original:P2}, every level in faces) table_bytes={table} ({(double)table / original:P4})"
            )
        );
    }

    private static void Print(string name, ReadOnlySpan<double> values, string unit)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{name}: n={sorted.Length} p50={Percentile(sorted, 0.5):F3} p95={Percentile(sorted, 0.95):F3} p99={Percentile(sorted, 0.99):F3} max={(sorted.Length == 0 ? double.NaN : sorted[^1]):F3} {unit}"
            )
        );
    }

    private static double Percentile(double[] sorted, double share) =>
        sorted.Length == 0 ? double.NaN : sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * share))];

    // Magenta pixels with a surface pixel on both sides, left and right or above and below, within 3 pixels: a crack.
    // The background around the surface has surface on one side only.
    private static int Enclosed(byte[] image)
    {
        const uint Magenta = 0xFFFF00FF;
        var pixels = MemoryMarshal.Cast<byte, uint>(image);
        var enclosed = 0;
        for (var y = 3; y < Height - 3; y++)
        {
            for (var x = 3; x < Width - 3; x++)
            {
                if (pixels[(y * Width) + x] != Magenta)
                {
                    continue;
                }

                var row = Surface(pixels, x, y, 1, 0) && Surface(pixels, x, y, -1, 0);
                var column = Surface(pixels, x, y, 0, 1) && Surface(pixels, x, y, 0, -1);
                enclosed += row || column ? 1 : 0;
            }
        }

        return enclosed;

        static bool Surface(ReadOnlySpan<uint> pixels, int x, int y, int dx, int dy)
        {
            for (var s = 1; s <= 3; s++)
            {
                if (pixels[((y + (s * dy)) * Width) + x + (s * dx)] != Magenta)
                {
                    return true;
                }
            }

            return false;
        }
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

    /// <summary>The figures of each frame other than the GPU times. Taking a frame allocates nothing.</summary>
    private sealed class Record(int frames, SurfaceData data)
    {
        private readonly double[] _cpu = new double[frames];
        private readonly double[] _select = new double[frames];
        private readonly double[] _prepare = new double[frames];
        private readonly double[] _draw = new double[frames];
        private readonly int[] _tiles = new int[frames];
        private readonly int[] _before = new int[frames];
        private readonly int[] _splits = new int[frames];
        private readonly long[] _triangles = new long[frames];
        private readonly int[] _uploaded = new int[frames];
        private readonly int[] _missing = new int[frames];
        private readonly bool[] _budget = new bool[frames];
        private readonly long[] _levels = new long[(data.TopLevel + 1) * frames];
        private readonly int _levelCount = data.TopLevel + 1;
        private readonly byte[] _nodeLevel = data.NodeLevel;

        public void Take(int f, long start, long selected, long prepared, long end, Selector selector, Residency residency, Gpu gpu)
        {
            _cpu[f] = Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;
            _select[f] = Stopwatch.GetElapsedTime(start, selected).TotalMilliseconds;
            _prepare[f] = Stopwatch.GetElapsedTime(selected, prepared).TotalMilliseconds;
            _draw[f] = Stopwatch.GetElapsedTime(prepared, end).TotalMilliseconds;
            _tiles[f] = residency.Written;
            _before[f] = selector.BeforeBalance;
            _splits[f] = selector.BalanceSplits;
            _uploaded[f] = residency.Uploaded;
            _missing[f] = residency.Missing;
            _budget[f] = selector.BudgetReached;
            long triangles = 0;
            for (var shape = 0; shape < Stitch.Shapes; shape++)
            {
                triangles += (long)residency.Counts[shape] * gpu.Triangles(shape);
            }

            _triangles[f] = triangles;
            for (var k = 0; k < selector.Count; k++)
            {
                _levels[(f * _levelCount) + _nodeLevel[selector.Tiles[k]]]++;
            }
        }

        public void Print(int from, GpuTimes gpuTimes, int tileBytes)
        {
            var count = _cpu.Length - from;
            Scene.Print("cpu_frame", _cpu.AsSpan(from), "ms");
            Scene.Print("cpu_select", _select.AsSpan(from), "ms");
            Scene.Print("cpu_residency_upload", _prepare.AsSpan(from), "ms");
            Scene.Print("cpu_draw_record", _draw.AsSpan(from), "ms");
            string[] stages = ["upload", "clear", "draw"];
            for (var s = 0; s < stages.Length; s++)
            {
                Scene.Print($"gpu_{stages[s]}", gpuTimes.Stage(s, from), "ms");
            }

            Scene.Print("gpu_total", gpuTimes.Total(from), "ms");
            Scene.Print("cpu_plus_gpu", gpuTimes.PlusCpu(from, _cpu), "ms");
            Scene.Print("cpu_or_gpu_max", gpuTimes.MaxCpu(from, _cpu), "ms");
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"gpu frames timed: {gpuTimes.Timed(from)} of {count}"));
            Scene.Print("tiles_drawn", Doubles(_tiles.AsSpan(from), 1), "tiles");
            Scene.Print("tiles_before_restriction", Doubles(_before.AsSpan(from), 1), "tiles");
            Scene.Print("restriction_splits", Doubles(_splits.AsSpan(from), 1), "splits");
            Scene.Print("triangles", Doubles(_triangles.AsSpan(from)), "triangles");
            Scene.Print("upload", Doubles(_uploaded.AsSpan(from), tileBytes / (1024.0 * 1024)), "MiB");
            Scene.Print("missing_tiles", Doubles(_missing.AsSpan(from), 1), "tiles");
            var over = 0;
            var budget = 0;
            for (var f = from; f < _cpu.Length; f++)
            {
                over += (long)_uploaded[f] * tileBytes > UploadBudget ? 1 : 0;
                budget += _budget[f] ? 1 : 0;
            }

            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"frames_upload_over_4MiB={over} of {count} first_frame_upload_MiB={(double)_uploaded[0] * tileBytes / (1024 * 1024):F1} first_frame_tiles={_uploaded[0]} frames_at_tile_budget={budget} of {count}"
                )
            );
            var line = new System.Text.StringBuilder("tiles_by_level_mean:");
            for (var level = 0; level < _levelCount; level++)
            {
                long sum = 0;
                for (var f = from; f < _cpu.Length; f++)
                {
                    sum += _levels[(f * _levelCount) + level];
                }

                _ = line.Append(CultureInfo.InvariantCulture, $" L{level}={(double)sum / count:F1}");
            }

            Console.WriteLine(line.ToString());
        }

        public void Write(string path, GpuTimes gpuTimes)
        {
            using var writer = new StreamWriter(path);
            writer.WriteLine("frame,cpu_ms,select_ms,residency_ms,record_ms,tiles,before_restriction,restriction_splits,triangles,uploaded_tiles,missing,at_budget,gpu_upload_end_ms,gpu_clear_end_ms,gpu_draw_end_ms");
            for (var f = 0; f < _cpu.Length; f++)
            {
                writer.WriteLine(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{f},{_cpu[f]:F4},{_select[f]:F4},{_prepare[f]:F4},{_draw[f]:F4},{_tiles[f]},{_before[f]},{_splits[f]},{_triangles[f]},{_uploaded[f]},{_missing[f]},{(_budget[f] ? 1 : 0)},{gpuTimes.Row(f)}"
                    )
                );
            }
        }

        private static double[] Doubles(ReadOnlySpan<int> values, double scale)
        {
            var result = new double[values.Length];
            for (var i = 0; i < values.Length; i++)
            {
                result[i] = values[i] * scale;
            }

            return result;
        }

        private static double[] Doubles(ReadOnlySpan<long> values)
        {
            var result = new double[values.Length];
            for (var i = 0; i < values.Length; i++)
            {
                result[i] = values[i];
            }

            return result;
        }
    }

    /// <summary>The GPU times of the frames: milliseconds from each frame's first timestamp to each later one.</summary>
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

        /// <summary>The time of stage s (0 the uploads, 1 the clears, 2 the draws) of each timed frame from a frame on.</summary>
        public double[] Stage(int s, int from) => Collect(from, f => s == 0 ? _times[f * Stages] : _times[(f * Stages) + s] - _times[(f * Stages) + s - 1]);

        public double[] Total(int from) => Collect(from, f => _times[(f * Stages) + Stages - 1]);

        /// <summary>The CPU time of the frame plus its GPU time, for each timed frame: the frame if neither overlapped the other.</summary>
        public double[] PlusCpu(int from, double[] cpu) => Collect(from, f => cpu[f] + _times[(f * Stages) + Stages - 1]);

        /// <summary>The larger of the CPU and GPU times: the frame if they overlapped wholly.</summary>
        public double[] MaxCpu(int from, double[] cpu) => Collect(from, f => Math.Max(cpu[f], _times[(f * Stages) + Stages - 1]));

        public string Row(int f) =>
            _timed[f]
                ? string.Create(CultureInfo.InvariantCulture, $"{_times[f * Stages]:F4},{_times[(f * Stages) + 1]:F4},{_times[(f * Stages) + 2]:F4}")
                : ",,";

        private double[] Collect(int from, Func<int, double> value)
        {
            var values = new List<double>();
            for (var f = from; f < _timed.Length; f++)
            {
                if (_timed[f])
                {
                    values.Add(value(f));
                }
            }

            return [.. values];
        }
    }

    /// <summary>The options after the positional arguments.</summary>
    private sealed record Options
    {
        public int Frames { get; init; } = Turn;

        public int Warmup { get; init; } = 120;

        /// <summary>The bound of the screen error in pixels (requirement K-4: 1 pixel by default).</summary>
        public float Error { get; init; } = 1;

        public int PoolMegabytes { get; init; } = 256;

        public int Face { get; init; } = 258;

        /// <summary>The tiles the selection may draw before the restriction; 0 for three quarters of the pool's places.</summary>
        public int TileBudget { get; init; }

        /// <summary>The noise's amplitude in z's units (the smooth heights span about [-1, 1]).</summary>
        public float Noise { get; init; } = 0.02f;

        public int Spikes { get; init; } = 1000;

        public bool NoStitch { get; init; }

        public bool BackToBack { get; init; }

        public bool Warp { get; init; }

        public bool Hardware { get; init; }

        public string? Csv { get; init; }

        public string? Out { get; init; }

        public int Frame { get; init; } = 150;

        public int Seed { get; init; } = 1;

        public static Options Parse(ReadOnlySpan<string> args)
        {
            var options = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                options = args[i] switch
                {
                    "--frames" => options with { Frames = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--warmup" => options with { Warmup = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--error" => options with { Error = float.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--pool-mb" => options with { PoolMegabytes = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--face" => options with { Face = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--tile-budget" => options with { TileBudget = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--noise" => options with { Noise = float.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--spikes" => options with { Spikes = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--no-stitch" => options with { NoStitch = true },
                    "--back-to-back" => options with { BackToBack = true },
                    "--warp" => options with { Warp = true },
                    "--hardware" => options with { Hardware = true },
                    "--csv" => options with { Csv = args[++i] },
                    "--out" => options with { Out = args[++i] },
                    "--frame" => options with { Frame = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--seed" => options with { Seed = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                    _ => throw new ArgumentException($"Unknown option {args[i]}."),
                };
            }

            return options;
        }
    }
}
