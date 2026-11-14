using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace HyperPlot.Mesh3D;

/// <summary>The orbit scene, the determinism check, and the error of WBOIT against a sorted reference (SPIKE, row K0d, Issue #490).</summary>
internal static partial class Scene
{
    private const int Width = 1920;
    private const int Height = 1080;

    /// <summary>The frames of one turn of the orbit.</summary>
    private const int Turn = 600;

    /// <summary>The frame budget of requirement P-13's current hypothesis, 60 fps.</summary>
    private const double Budget = 1000.0 / 60;

    /// <summary>Runs the orbit and prints its figures; returns the exit code.</summary>
    public static int Run(ReadOnlySpan<string> args)
    {
        var mode = args[0] switch
        {
            "opaque" => Mode.Opaque,
            "oit" => Mode.Translucent,
            _ => throw new ArgumentException($"Unknown mode {args[0]}: opaque or oit."),
        };
        var kind = args[1];
        var options = Options.Parse(args[2..]);
        var mesh = MakeMesh(kind, options);
        using var gpu = new Gpu(options.Warp, Width, Height, mesh);
        var total = options.Warmup + options.Frames;
        var cpu = new double[total];
        var gpuTimes = new GpuTimes(total);
        TimedHandler take = gpuTimes.Take;
        var period = TimeSpan.FromMilliseconds(Budget);

        // A wait of a few milliseconds needs the timer's finer step: by default a sleep ends on a 15.6 ms tick (.cursor/rules/bench.mdc).
        _ = TimeBeginPeriod(1);
        var framesFrom = DateTime.Now;
        var began = Stopwatch.GetTimestamp();
        try
        {
            for (var f = 0; f < total; f++)
            {
                var start = Stopwatch.GetTimestamp();
                var constants = Constants(f, options);
                gpu.Draw(mode, constants, f);
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

        // The scene draws what it says: the last frame covers pixels (.cursor/rules/bench.mdc).
        var drawn = Drawn(gpu.Read());
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"mode={args[0]} mesh={kind} triangles={mesh.Triangles} vertices={mesh.Vertices.Length} order=grid viewport={Width}x{Height} device={(gpu.IsWarp ? "WARP" : "hardware")} alpha={options.Alpha} weight={WeightName(options.Weight)} scale={options.Scale.X},{options.Scale.Y},{options.Scale.Z} paced={!options.BackToBack} frames={options.Frames} (after {options.Warmup}) frame_period_ms={seconds * 1000 / total:F2} drawn_pixels_last_frame={drawn}"
            )
        );
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"frames ran {framesFrom:HH:mm:ss.fff} to {framesTo:HH:mm:ss.fff}"));
        Print("cpu_frame", cpu.AsSpan(options.Warmup));
        string[] stages = mode == Mode.Opaque ? ["clear", "mesh"] : ["clear", "accum", "composite"];
        for (var s = 0; s < stages.Length; s++)
        {
            Print($"gpu_{stages[s]}", gpuTimes.Stage(s, options.Warmup));
        }

        var gpuTotal = gpuTimes.Stage(stages.Length - 1, options.Warmup, cumulative: true);
        Print("gpu_total", gpuTotal);

        // CPU and GPU of the same frame added: a bound for a frame whose recording and drawing do not overlap.
        var sums = gpuTimes.Sum(cpu, stages.Length - 1, options.Warmup);
        var p95 = Print("cpu_plus_gpu", sums);
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"gpu frames timed: {gpuTimes.Timed(options.Warmup)} of {options.Frames}; p95 cpu_plus_gpu {p95:F3} ms against P-13's {Budget:F3} ms: {(p95 <= Budget ? "within" : "over")}"
            )
        );
        if (options.Csv is { } csv)
        {
            gpuTimes.Write(csv, cpu, stages);
        }

        return 0;
    }

    /// <summary>Checks that each mode draws the same frame to the same bytes; returns 1 when one does not.</summary>
    public static int Determinism(ReadOnlySpan<string> args)
    {
        var kind = args[0];
        var options = Options.Parse(args[1..]);
        var mesh = MakeMesh(kind, options);
        using var gpu = new Gpu(!options.Hardware, Width, Height, mesh);
        var failed = false;
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"mesh={kind} triangles={mesh.Triangles} device={(gpu.IsWarp ? "WARP" : "hardware")} frame={options.Frame} alpha={options.Alpha} weight={WeightName(options.Weight)}"
            )
        );
        foreach (var mode in (ReadOnlySpan<Mode>)[Mode.Opaque, Mode.Translucent])
        {
            var (first, firstAccum) = Draw(gpu, mode, Constants(options.Frame, options));

            // Another frame between the two, so that the second does not read what the first left behind.
            _ = Draw(gpu, mode, Constants(options.Frame + (Turn / 2), options));
            var (second, secondAccum) = Draw(gpu, mode, Constants(options.Frame, options));
            var same = first.AsSpan().SequenceEqual(second);
            var sameAccum = firstAccum.AsSpan().SequenceEqual(secondAccum);
            failed |= !same || !sameAccum;
            var accum =
                mode == Mode.Translucent
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $" accum_identical={sameAccum} accum_sha256={Convert.ToHexString(SHA256.HashData(firstAccum))}"
                    )
                    : string.Empty;
            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{(mode == Mode.Opaque ? "opaque" : "oit")}: repeat_identical={same} differing_pixels={Differing(first, second)} drawn_pixels={Drawn(first)} sha256={Convert.ToHexString(SHA256.HashData(first))}{accum}"
                )
            );
            if (options.Dump is { } folder)
            {
                _ = Directory.CreateDirectory(folder);
                var file = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{(mode == Mode.Opaque ? "opaque" : "oit")}-{(gpu.IsWarp ? "warp" : "hw")}-{kind}-f{options.Frame}.bmp"
                );
                WriteBmp(Path.Combine(folder, file), first);
            }
        }

        return failed ? 1 : 0;
    }

    /// <summary>
    /// Measures the color error of WBOIT against an exact sorted reference at frames of the orbit, and prints ΔE00 and ΔE76.
    /// </summary>
    /// <remarks>
    /// The reference is exact per pixel, not a per-triangle sort: every fragment the rasterizer makes in the translucent pass
    /// is captured (with the same shading as WBOIT's), each pixel's fragments are sorted by view depth on the CPU, back to
    /// front (a tie by the primitive's index, the later on top, as the pipeline's order would put it), and composited with
    /// "over" in double over the white background, then rounded to 8 bits. The capture draws the frame in bands of rows
    /// (a scissor), each band again in halves when its fragments overflow the buffer.
    /// Four images are compared with the reference or with each other, over the pixels with at least one fragment and over
    /// those with two or more: the GPU's WBOIT frame; WBOIT computed on the CPU in double from the same fragments (the
    /// method's error without RGBA16F, R8, and the blend's rounding); the GPU against that CPU WBOIT (the storage's error);
    /// and "over" in the order the triangles were submitted, unsorted (what plain alpha blending gives, for scale).
    /// </remarks>
    public static int Error(ReadOnlySpan<string> args)
    {
        CheckCiede2000();
        var kind = args[0];
        var options = Options.Parse(args[1..]);
        var mesh = MakeMesh(kind, options);
        using var gpu = new Gpu(options.Warp, Width, Height, mesh);
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"mesh={kind} triangles={mesh.Triangles} vertices={mesh.Vertices.Length} viewport={Width}x{Height} device={(gpu.IsWarp ? "WARP" : "hardware")} alpha={options.Alpha} weight={WeightName(options.Weight)} scale={options.Scale.X},{options.Scale.Y},{options.Scale.Z} capacity={options.Capacity} reference=exact-per-pixel-sort"
            )
        );
        var all = new ErrorStats();
        foreach (var frame in options.At)
        {
            var stats = new ErrorStats();
            var constants = Constants(frame, options);
            var (image, _) = Draw(gpu, Mode.Translucent, constants);
            var reference = options.Dump is null ? null : White();
            var band = options.Band;
            var top = 0;
            var captures = 0;
            while (top < Height)
            {
                var bottom = Math.Min(Height, top + band);
                var count = gpu.Capture(constants, options.Capacity, top, bottom);
                captures++;
                if (count > (uint)options.Capacity)
                {
                    if (band == 1)
                    {
                        throw new InvalidOperationException($"One row makes {count} fragments, over the capacity {options.Capacity}.");
                    }

                    band = Math.Max(1, band / 2);
                    continue;
                }

                var (heads, fragments) = gpu.ReadCapture(top, bottom, count);
                Compare(stats, image, reference, heads, fragments, top, bottom, options);
                top = bottom;
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"frame={frame} captures={captures} smallest_band={band}"));
            stats.Print($"frame={frame} ");
            all.Add(stats);
            if (options.Dump is { } folder && reference is not null)
            {
                _ = Directory.CreateDirectory(folder);
                var stem = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{kind}-{(gpu.IsWarp ? "warp" : "hw")}-{WeightName(options.Weight)}-f{frame}"
                );
                WriteBmp(Path.Combine(folder, stem + "-wboit.bmp"), image);
                WriteBmp(Path.Combine(folder, stem + "-reference.bmp"), reference);
                WriteBmp(Path.Combine(folder, stem + "-de00x25.bmp"), DifferenceMap(image, reference));
            }
        }

        all.Print("all_frames ");
        return all.Broken > 0 ? 1 : 0;
    }

    private static Mesh MakeMesh(string kind, Options options)
    {
        var started = Stopwatch.GetTimestamp();
        var mesh = MeshData.Make(kind, options.Triangles, options.Scale, options.Seed);
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"generate mesh={kind} triangles={mesh.Triangles} vertices={mesh.Vertices.Length} seed={options.Seed} seconds={Stopwatch.GetElapsedTime(started).TotalSeconds:F2}"
            )
        );
        return mesh;
    }

    private static (byte[] Image, byte[] Accum) Draw(Gpu gpu, Mode mode, in FrameConstants constants)
    {
        gpu.Draw(mode, constants, frame: -1);
        gpu.Poll(static (_, _) => { }, wait: true);
        return (gpu.Read(), mode == Mode.Translucent ? gpu.ReadAccum() : Array.Empty<byte>());
    }

    // The pixels of rows [top, bottom): each pixel's fragments composited four ways and compared.
    private static void Compare(
        ErrorStats stats,
        byte[] image,
        byte[]? reference,
        uint[] heads,
        Fragment[] fragments,
        int top,
        int bottom,
        Options options
    )
    {
        var list = new List<int>();
        Comparison<int> backToFront = (a, b) =>
        {
            var byDepth = fragments[b].ViewDepth.CompareTo(fragments[a].ViewDepth);
            return byDepth != 0 ? byDepth : fragments[a].Primitive.CompareTo(fragments[b].Primitive);
        };
        Comparison<int> submitted = (a, b) => fragments[a].Primitive.CompareTo(fragments[b].Primitive);
        for (var y = top; y < bottom; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                list.Clear();
                for (var i = heads[((y - top) * Width) + x]; i != Gpu.End; i = fragments[i].Next)
                {
                    if (i >= fragments.Length || list.Count > fragments.Length)
                    {
                        stats.Broken++;
                        break;
                    }

                    list.Add((int)i);
                }

                var at = ((y * Width) + x) * 4;
                var gpuColor = (image[at + 2], image[at + 1], image[at]);
                if (list.Count == 0)
                {
                    // No fragment: the WBOIT frame must be the white background here.
                    stats.DrawnWithoutFragments += gpuColor == (255, 255, 255) ? 0 : 1;
                    continue;
                }

                list.Sort(backToFront);
                var sorted = Quantize(Over(fragments, list));
                var cpuWboit = Quantize(Wboit(fragments, list, options.Weight));
                list.Sort(submitted);
                var unsorted = Quantize(Over(fragments, list));
                stats.Take(list.Count, gpuColor, sorted, cpuWboit, unsorted);
                if (reference is not null)
                {
                    (reference[at + 2], reference[at + 1], reference[at]) = sorted;
                }
            }
        }
    }

    // "Over", back to front, over white: c = C·α + c·(1 - α).
    private static (double R, double G, double B) Over(Fragment[] fragments, List<int> order)
    {
        var (r, g, b) = (1.0, 1.0, 1.0);
        foreach (var i in order)
        {
            var f = fragments[i];
            double alpha = f.Alpha;
            r = (f.Color.X * alpha) + (r * (1 - alpha));
            g = (f.Color.Y * alpha) + (g * (1 - alpha));
            b = (f.Color.Z * alpha) + (b * (1 - alpha));
        }

        return (r, g, b);
    }

    // WBOIT in double, as AccumPS and CompositePS compute it, over white.
    private static (double R, double G, double B) Wboit(Fragment[] fragments, List<int> order, int weightKind)
    {
        var (r, g, b, a, revealage) = (0.0, 0.0, 0.0, 0.0, 1.0);
        foreach (var i in order)
        {
            var f = fragments[i];
            double alpha = f.Alpha;
            var w = Weight(weightKind, f.ViewDepth, f.Depth, alpha);
            r += f.Color.X * alpha * w;
            g += f.Color.Y * alpha * w;
            b += f.Color.Z * alpha * w;
            a += alpha * w;
            revealage *= 1 - alpha;
        }

        var divisor = Math.Clamp(a, 1e-4, 5e4);
        return (
            (r / divisor * (1 - revealage)) + revealage,
            (g / divisor * (1 - revealage)) + revealage,
            (b / divisor * (1 - revealage)) + revealage
        );
    }

    // The weight of Mesh3D.hlsl's Weight, in double.
    private static double Weight(int kind, double z, double depth, double alpha)
    {
        double w;
        switch (kind)
        {
            case 7:
                w = 10 / (1e-5 + Math.Pow(z / 5, 2) + Math.Pow(z / 200, 6));
                break;
            case 8:
                w = 10 / (1e-5 + Math.Pow(z / 10, 3) + Math.Pow(z / 200, 6));
                break;
            case 9:
                w = 0.03 / (1e-5 + Math.Pow(z / 200, 4));
                break;
            case 10:
                w = 3e3 * Math.Pow(1 - depth, 3);
                break;
            default:
                return alpha;
        }

        return alpha * Math.Clamp(w, 1e-2, 3e3);
    }

    private static (byte R, byte G, byte B) Quantize((double R, double G, double B) color) => (ToByte(color.R), ToByte(color.G), ToByte(color.B));

    private static byte ToByte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);

    // Sharma et al. 2005, Table 1, pairs 1 and 2: the formula is checked before any figure comes from it.
    private static void CheckCiede2000()
    {
        var first = ColorDifference.Ciede2000((50.0, 2.6772, -79.7751), (50.0, 0.0, -82.7485));
        var second = ColorDifference.Ciede2000((50.0, 3.1571, -77.2803), (50.0, 0.0, -82.7485));
        if (Math.Abs(first - 2.0425) > 1e-4 || Math.Abs(second - 2.8615) > 1e-4)
        {
            throw new InvalidOperationException($"CIEDE2000 gives {first} and {second}, not 2.0425 and 2.8615.");
        }
    }

    /// <summary>
    /// The constants of a frame of the orbit: the camera turns about the box's vertical axis (z), 360° every 600 frames, 30°
    /// above the horizon, at the distance where the box's bounding sphere (radius √3) fits the vertical field of view of 45°
    /// (design §13.8, decision 16), as in row K0a's prototype. The near and far planes hold the sphere. The light comes from
    /// behind the camera, above and to the right, and turns with it.
    /// </summary>
    private static FrameConstants Constants(int frame, Options options)
    {
        const float FieldOfView = MathF.PI / 4;
        const float Elevation = MathF.PI / 6;
        var angle = 2 * MathF.PI * (frame % Turn) / Turn;
        var radius = MathF.Sqrt(3);
        var distance = radius / MathF.Sin(FieldOfView / 2);
        var toEye = new Vector3(MathF.Cos(Elevation) * MathF.Cos(angle), MathF.Cos(Elevation) * MathF.Sin(angle), MathF.Sin(Elevation));
        var eye = toEye * distance;
        var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitZ);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            FieldOfView,
            Width / (float)Height,
            distance - radius - 0.1f,
            distance + radius + 0.1f
        );
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, toEye));
        return new FrameConstants
        {
            ViewProjection = view * projection,
            View = view,
            Scale = options.Scale,
            Alpha = options.Alpha,
            ToLight = Vector3.Normalize(toEye + (0.6f * Vector3.UnitZ) + (0.4f * right)),
            Ambient = 0.3f,
            Eye = eye,
            WeightKind = (uint)options.Weight,
        };
    }

    private static string WeightName(int weight) => weight == 0 ? "none" : string.Create(CultureInfo.InvariantCulture, $"eq{weight}");

    private static double Print(string name, ReadOnlySpan<double> values)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        var p95 = Percentile(sorted, 0.95);
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{name}: n={sorted.Length} p50={Percentile(sorted, 0.5):F3} p95={p95:F3} p99={Percentile(sorted, 0.99):F3} max={(sorted.Length == 0 ? double.NaN : sorted[^1]):F3} ms"
            )
        );
        return p95;
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

    private static byte[] White()
    {
        var image = new byte[Width * Height * 4];
        image.AsSpan().Fill(0xFF);
        return image;
    }

    // ΔE00 × 25 as gray, black where the two agree.
    private static byte[] DifferenceMap(byte[] image, byte[] reference)
    {
        var map = White();
        for (var at = 0; at < image.Length; at += 4)
        {
            var difference = ColorDifference.Ciede2000(
                ColorDifference.Lab(image[at + 2], image[at + 1], image[at]),
                ColorDifference.Lab(reference[at + 2], reference[at + 1], reference[at])
            );
            var gray = (byte)Math.Min(255, Math.Round(difference * 25));
            (map[at], map[at + 1], map[at + 2]) = (gray, gray, gray);
        }

        return map;
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

    /// <summary>The color differences of the pixels compared, by comparison and by the pixels' layers.</summary>
    private sealed class ErrorStats
    {
        private static readonly string[] _names =
        [
            "wboit_gpu_vs_reference",
            "wboit_cpu_vs_reference",
            "wboit_gpu_vs_wboit_cpu",
            "unsorted_over_vs_reference",
        ];

        private readonly List<float>[] _covered =
        [
            [],
            [],
            [],
            [],
        ];

        private readonly List<float>[] _overlap =
        [
            [],
            [],
            [],
            [],
        ];

        private readonly List<float>[] _covered76 =
        [
            [],
            [],
            [],
            [],
        ];

        private readonly List<int> _layers = [];

        /// <summary>Pixels the WBOIT frame drew where the capture found no fragment: expected 0.</summary>
        public int DrawnWithoutFragments { get; set; }

        /// <summary>Lists that pointed outside the captured fragments or looped: expected 0.</summary>
        public int Broken { get; set; }

        public void Take(
            int layers,
            (byte R, byte G, byte B) gpu,
            (byte R, byte G, byte B) reference,
            (byte R, byte G, byte B) cpu,
            (byte R, byte G, byte B) unsorted
        )
        {
            _layers.Add(layers);
            var (labGpu, labReference, labCpu, labUnsorted) = (Lab(gpu), Lab(reference), Lab(cpu), Lab(unsorted));
            ReadOnlySpan<((double, double, double) First, (double, double, double) Second)> pairs =
            [
                (labGpu, labReference),
                (labCpu, labReference),
                (labGpu, labCpu),
                (labUnsorted, labReference),
            ];
            for (var c = 0; c < pairs.Length; c++)
            {
                var de00 = (float)ColorDifference.Ciede2000(pairs[c].First, pairs[c].Second);
                _covered[c].Add(de00);
                _covered76[c].Add((float)ColorDifference.Cie76(pairs[c].First, pairs[c].Second));
                if (layers >= 2)
                {
                    _overlap[c].Add(de00);
                }
            }
        }

        public void Add(ErrorStats other)
        {
            for (var c = 0; c < _names.Length; c++)
            {
                _covered[c].AddRange(other._covered[c]);
                _overlap[c].AddRange(other._overlap[c]);
                _covered76[c].AddRange(other._covered76[c]);
            }

            _layers.AddRange(other._layers);
            DrawnWithoutFragments += other.DrawnWithoutFragments;
            Broken += other.Broken;
        }

        public void Print(string prefix)
        {
            var layers = _layers.ToArray();
            Array.Sort(layers);
            var fragments = layers.Sum(l => (long)l);
            var overlapping = layers.Count(l => l >= 2);
            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{prefix}covered_pixels={layers.Length} fragments={fragments} layers_mean={(layers.Length == 0 ? 0 : (double)fragments / layers.Length):F2} layers_p99={(layers.Length == 0 ? 0 : layers[Math.Min(layers.Length - 1, (int)(layers.Length * 0.99))])} layers_max={(layers.Length == 0 ? 0 : layers[^1])} overlap_pixels={overlapping} drawn_without_fragments={DrawnWithoutFragments} broken_lists={Broken}"
                )
            );
            for (var c = 0; c < _names.Length; c++)
            {
                Line($"{prefix}{_names[c]} de00 covered", _covered[c]);
                Line($"{prefix}{_names[c]} de00 overlap", _overlap[c]);
                Line($"{prefix}{_names[c]} de76 covered", _covered76[c]);
            }
        }

        private static (double L, double A, double B) Lab((byte R, byte G, byte B) color) => ColorDifference.Lab(color.R, color.G, color.B);

        private static void Line(string name, List<float> values)
        {
            var sorted = values.ToArray();
            Array.Sort(sorted);
            if (sorted.Length == 0)
            {
                Console.WriteLine($"{name}: n=0");
                return;
            }

            float At(double share) => sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * share))];
            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{name}: n={sorted.Length} mean={sorted.Average():F3} p50={At(0.5):F3} p95={At(0.95):F3} p99={At(0.99):F3} max={sorted[^1]:F3} over_1={sorted.Count(v => v > 1) / (double)sorted.Length:P2} over_2.3={sorted.Count(v => v > 2.3f) / (double)sorted.Length:P2}"
                )
            );
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

        /// <summary>The CPU time of each timed frame plus its GPU time to the end of stage s.</summary>
        public double[] Sum(double[] cpu, int s, int from)
        {
            var values = new List<double>();
            for (var f = from; f < _timed.Length; f++)
            {
                if (_timed[f])
                {
                    values.Add(cpu[f] + _times[(f * Stages) + s]);
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

        public int Triangles { get; init; } = 10_000_000;

        public float Alpha { get; init; } = 0.5f;

        public int Weight { get; init; } = 7;

        public Vector3 Scale { get; init; } = new(1, 1, 0.6f);

        public bool BackToBack { get; init; }

        public bool Warp { get; init; }

        public bool Hardware { get; init; }

        public string? Csv { get; init; }

        public string? Dump { get; init; }

        public int Frame { get; init; } = 150;

        public int[] At { get; init; } = [0, 150, 300, 450];

        public int Capacity { get; init; } = 4_000_000;

        public int Band { get; init; } = 135;

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
                    "--triangles" => options with { Triangles = (int)double.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--alpha" => options with { Alpha = float.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--weight" => options with { Weight = ParseWeight(args[++i]) },
                    "--scale" => options with { Scale = ParseScale(args[++i]) },
                    "--back-to-back" => options with { BackToBack = true },
                    "--warp" => options with { Warp = true },
                    "--hardware" => options with { Hardware = true },
                    "--csv" => options with { Csv = args[++i] },
                    "--dump" => options with { Dump = args[++i] },
                    "--frame" => options with { Frame = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--at" => options with { At = [.. args[++i].Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture))] },
                    "--capacity" => options with { Capacity = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--band" => options with { Band = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--seed" => options with { Seed = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                    _ => throw new ArgumentException($"Unknown option {args[i]}."),
                };
            }

            // The fragments' buffer stays within the 128 MB that D3D11 guarantees a resource (32 bytes a fragment).
            if (options.Capacity is < 1 or > 4_194_304)
            {
                throw new ArgumentException("--capacity is 1 to 4,194,304 fragments (128 MiB of 32-byte fragments).");
            }

            return options;
        }

        private static int ParseWeight(string name) =>
            name switch
            {
                "none" => 0,
                "eq7" => 7,
                "eq8" => 8,
                "eq9" => 9,
                "eq10" => 10,
                _ => throw new ArgumentException($"Unknown weight {name}: none, eq7, eq8, eq9, or eq10."),
            };

        private static Vector3 ParseScale(string text)
        {
            var parts = text.Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            return parts.Length == 3 && parts.All(p => p > 0)
                ? new Vector3(parts[0], parts[1], parts[2])
                : throw new ArgumentException("--scale is three positive numbers: x,y,z.");
        }
    }
}
