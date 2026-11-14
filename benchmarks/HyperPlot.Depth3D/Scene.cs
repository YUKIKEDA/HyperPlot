using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace HyperPlot.Depth3D;

/// <summary>The orbit, the determinism check, and the comparison with the CPU reference (SPIKE, row K1, Issue #509).</summary>
internal static partial class Scene
{
    private const int Width = Orbit.Width;
    private const int Height = Orbit.Height;

    private static readonly string[] _lineMethods = ["none", "test", "prepass", "postpass", "capsules", "uav", "uavfrag"];
    private static readonly string[] _markerMethods = ["none", "quad", "touched", "half"];
    private static readonly string[] _bucketNames = ["clear", "plane", "shapes", "segment_passes", "bounds_passes", "marker_passes", "total"];
    private static readonly double[] _errorLevels = [0.1, 0.25, 0.5];

    /// <summary>run lines|markers method scene [options]: the orbit with GPU timestamps; returns the exit code.</summary>
    public static int Run(ReadOnlySpan<string> args)
    {
        var lines = Kind(args[0]);
        var method = args[1];
        var options = Options.Parse(args[3..]);
        var scene = Make(lines, args[2], options);
        using var gpu = new Gpu(options.Warp, Width, Height, scene);
        var total = options.Warmup + options.Frames;
        var cpu = new double[total];
        var gpuTimes = new GpuTimes(total);
        TimedHandler take = gpuTimes.Take;
        var period = TimeSpan.FromMilliseconds(1000.0 / 60);

        // A wait of a few milliseconds needs the timer's finer step: by default a sleep ends on a 15.6 ms tick (.cursor/rules/bench.mdc).
        _ = TimeBeginPeriod(1);
        var framesFrom = DateTime.Now;
        var measuredFrom = framesFrom;
        var began = Stopwatch.GetTimestamp();
        try
        {
            for (var f = 0; f < total; f++)
            {
                if (f == options.Warmup)
                {
                    measuredFrom = DateTime.Now;
                }

                var start = Stopwatch.GetTimestamp();
                var settings = Settings(f, options);
                Draw(gpu, lines, method, settings, f);
                if (options.BackToBack)
                {
                    // Back to back, the GPU is kept busy (its clock at P0) with at most --in-flight frames queued: the
                    // timestamps of 8 frames can be pending, and a frame recorded with all 8 pending is not timed.
                    cpu[f] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    gpu.Poll(take, wait: true, keep: options.InFlight);
                    continue;
                }

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
                $"{Describe(lines, method, scene, options)} device={(gpu.IsWarp ? "WARP" : "hardware")} paced={!options.BackToBack} in_flight={(options.BackToBack ? options.InFlight : 0)} pass_times={options.PassTimes} frames={options.Frames} (after {options.Warmup}) frame_period_ms={seconds * 1000 / total:F2}"
            )
        );
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"frames ran {framesFrom:HH:mm:ss.fff} to {framesTo:HH:mm:ss.fff}"));

        // The window of the frames after the warm-up, with the date, to read the clock log (nvidia-smi) inside it.
        Console.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"measured frames ran {measuredFrom:yyyy-MM-dd HH:mm:ss.fff} to {framesTo:yyyy-MM-dd HH:mm:ss.fff}")
        );
        Print("cpu_frame", cpu.AsSpan(options.Warmup));
        for (var b = 0; b < _bucketNames.Length; b++)
        {
            Print($"gpu_{_bucketNames[b]}", gpuTimes.Times(b, options.Warmup));
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"gpu frames timed: {gpuTimes.Timed(options.Warmup)} of {options.Frames}"));
        if (options.Csv is { } csv)
        {
            gpuTimes.Write(csv, cpu, _bucketNames);
        }

        return 0;
    }

    /// <summary>
    /// determinism lines|markers scene [options]: draws one frame of the orbit, another, and the first again with each method
    /// on WARP (or the hardware), and compares the two images of the same frame byte for byte; returns 1 when one differs.
    /// </summary>
    public static int Determinism(ReadOnlySpan<string> args)
    {
        var lines = Kind(args[0]);
        var options = Options.Parse(args[2..]);
        var scene = Make(lines, args[1], options);
        using var gpu = new Gpu(!options.Hardware, Width, Height, scene);
        var failed = false;
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{Describe(lines, "all", scene, options)} device={(gpu.IsWarp ? "WARP" : "hardware")} frame={options.Frame}"
            )
        );
        foreach (var method in lines ? _lineMethods : _markerMethods)
        {
            var first = DrawAndRead(gpu, lines, method, Settings(options.Frame, options));

            // Another frame between the two, so that the second does not read what the first left behind.
            _ = DrawAndRead(gpu, lines, method, Settings(options.Frame + (Orbit.Turn / 2), options));
            var second = DrawAndRead(gpu, lines, method, Settings(options.Frame, options));
            var same = first.AsSpan().SequenceEqual(second);
            failed |= !same;
            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{method}: repeat_identical={same} differing_pixels={Differing(first, second)} sha256={Convert.ToHexString(SHA256.HashData(first))}"
                )
            );
        }

        return failed ? 1 : 0;
    }

    /// <summary>
    /// compare lines|markers scene [options]: at each frame of --at, the CPU reference (exact area with depth order, by
    /// supersampling) and each method on the hardware (or WARP); prints the error of each method over the pixels near a
    /// shape. The error of a pixel is the largest of |GPU − reference| over R, G, and B, in 0..1.
    /// </summary>
    public static int Compare(ReadOnlySpan<string> args)
    {
        var lines = Kind(args[0]);
        var options = Options.Parse(args[2..]);
        var scene = Make(lines, args[1], options);
        using var gpu = new Gpu(options.Warp, Width, Height, scene);
        var methods = lines ? _lineMethods : _markerMethods;
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{Describe(lines, "all", scene, options)} device={(gpu.IsWarp ? "WARP" : "hardware")} samples={options.Samples}x{options.Samples}"
            )
        );
        using var trace = lines && options.Trace is { } tracePath ? new StreamWriter(tracePath) : null;
        trace?.WriteLine(
            "frame,x,y,error,gpu_r,gpu_g,gpu_b,ref_r,ref_g,ref_b,replayed_r,replayed_g,replayed_b,replayed_vs_gpu,plane_covers,plane_depth,"
                + "ref_plane_share,ref_background_share,front_polyline,later_behind_painted,polyline,segments,ref_share,ref_coverage,"
                + "coverage_max,coverage_passed,depth_min,depth_before,depth_after,wrote_depth"
        );
        foreach (var frame in options.At)
        {
            var settings = Settings(frame, options);
            var started = Stopwatch.GetTimestamp();
            var plane = options.Plane != PlaneOrder.None;
            var reference = lines
                ? Reference.Lines(scene, settings.Transform, Width, Height, settings.HalfWidth, plane, options.Samples)
                : Reference.Markers(scene, settings.Transform, Width, Height, settings.Radius, plane, options.Samples);
            var touched = reference.Touched.AsSpan().Count(true);
            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"frame {frame}: reference in {Stopwatch.GetElapsedTime(started).TotalSeconds:F1} s, touched_pixels={touched}"
                )
            );
            if (options.Dump is { } folder)
            {
                _ = Directory.CreateDirectory(folder);
                WriteBmp(Path.Combine(folder, Name(scene, lines, options, frame, "reference")), ToBytes(reference.Rgb));
            }

            var images = new byte[methods.Length][];
            for (var m = 0; m < methods.Length; m++)
            {
                images[m] = DrawAndRead(gpu, lines, methods[m], settings);
                var error = Errors(images[m], reference);
                Console.WriteLine(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"frame {frame} {methods[m]}: mean={error.Mean:F4} p99={error.P99:F3} max={error.Max:F3} over_0.1={error.Over[0]} over_0.25={error.Over[1]} over_0.5={error.Over[2]} of {touched} max_elsewhere={error.MaxElsewhere:F3}"
                    )
                );
                if (options.Dump is { } dump && (options.DumpMethods is null || Array.IndexOf(options.DumpMethods, methods[m]) >= 0))
                {
                    WriteBmp(Path.Combine(dump, Name(scene, lines, options, frame, methods[m])), images[m]);
                    WriteBmp(Path.Combine(dump, Name(scene, lines, options, frame, methods[m] + "-error")), ErrorMap(images[m], reference));
                }

                if (trace is not null && methods[m] == "postpass")
                {
                    TracePostpass(trace, scene, settings, options, frame, images[m], reference);
                }
            }

            if (lines)
            {
                // uavfrag and postpass test the same fragments and write the depth where the coverage reaches the threshold,
                // of the union in one and of each segment in the other.
                Console.WriteLine(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"frame {frame}: differing_pixels uavfrag_vs_postpass={Differing(images[6], images[3])} uav_vs_uavfrag={Differing(images[5], images[6])} test_vs_none={Differing(images[1], images[0])}"
                    )
                );
            }
        }

        return 0;
    }

    /// <summary>
    /// Writes a row for each polyline at each pixel of postpass whose error is above 0.5 (over the pixels near a shape, as
    /// over_0.5 counts them), with the reference's shares and postpass replayed at the pixel (Reference.TracePostpass), and
    /// prints how many pixels the replay matches within 2 levels of 255. front_polyline is the polyline that wins the most
    /// of the reference's samples; later_behind_painted counts the polylines drawn after it, nearer to the back at the
    /// pixel's center, whose coverage pass painted the pixel.
    /// </summary>
    private static void TracePostpass(
        StreamWriter trace,
        SceneData scene,
        in DrawSettings settings,
        Options options,
        int frame,
        byte[] image,
        ReferenceImage reference
    )
    {
        var pixels = new List<int>();
        var errors = new Dictionary<int, double>();
        for (var pixel = 0; pixel < Width * Height; pixel++)
        {
            var error = PixelError(image, reference.Rgb, pixel);
            if (reference.Touched[pixel] && error > 0.5)
            {
                pixels.Add(pixel);
                errors[pixel] = error;
            }
        }

        var started = Stopwatch.GetTimestamp();
        var traced = Reference.TracePostpass(
            scene,
            settings.Transform,
            Width,
            Height,
            settings.HalfWidth,
            settings.Threshold,
            options.Plane,
            options.Samples,
            pixels
        );
        var (matched, withPlane, overpainted) = (0, 0, 0);
        foreach (var t in traced)
        {
            var p = t.Pixel;
            var gpu = new System.Numerics.Vector3(image[(p * 4) + 2], image[(p * 4) + 1], image[p * 4]);
            var replayed = t.Replayed;
            var gap = MathF.Max(MathF.Abs(gpu.X - replayed.X), MathF.Max(MathF.Abs(gpu.Y - replayed.Y), MathF.Abs(gpu.Z - replayed.Z)));
            matched += gap <= 2 ? 1 : 0;
            withPlane += t.PlaneCovers ? 1 : 0;
            TracedPolyline? front = null;
            foreach (var polyline in t.Polylines)
            {
                if (front is null || polyline.ReferenceShare > front.ReferenceShare)
                {
                    front = polyline;
                }
            }

            var later = 0;
            foreach (var polyline in t.Polylines)
            {
                if (
                    front is not null
                    && polyline.Polyline > front.Polyline
                    && polyline.CoveragePassed > 0
                    && polyline.DepthMin > front.DepthMin
                )
                {
                    later++;
                }
            }

            overpainted += later > 0 ? 1 : 0;
            var head = string.Create(
                CultureInfo.InvariantCulture,
                $"{frame},{p % Width},{p / Width},{errors[p]:F3},{gpu.X:F0},{gpu.Y:F0},{gpu.Z:F0},{reference.Rgb[p * 3]:F1},{reference.Rgb[(p * 3) + 1]:F1},{reference.Rgb[(p * 3) + 2]:F1},{replayed.X:F1},{replayed.Y:F1},{replayed.Z:F1},{gap:F1},{(t.PlaneCovers ? 1 : 0)},{t.PlaneDepth:G9},{t.ReferencePlaneShare:F4},{t.ReferenceBackgroundShare:F4},{front?.Polyline ?? -1},{later}"
            );
            if (t.Polylines.Length == 0)
            {
                trace.WriteLine(head + ",,,,,,,,,,");
            }

            foreach (var polyline in t.Polylines)
            {
                trace.WriteLine(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{head},{polyline.Polyline},{polyline.Segments.Count},{polyline.ReferenceShare:F4},{polyline.ReferenceCoverage:F4},{polyline.CoverageMax:F4},{polyline.CoveragePassed:F4},{polyline.DepthMin:G9},{polyline.DepthBefore:G9},{polyline.DepthAfter:G9},{(polyline.WroteDepth ? 1 : 0)}"
                    )
                );
            }
        }

        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"frame {frame} trace postpass: pixels_over_0.5={pixels.Count} replay_within_2_of_255={matched} plane_covers={withPlane} later_behind_painted={overpainted} in {Stopwatch.GetElapsedTime(started).TotalSeconds:F1} s"
            )
        );
    }

    private static bool Kind(string name) =>
        name switch
        {
            "lines" => true,
            "markers" => false,
            _ => throw new ArgumentException($"Unknown kind {name}: lines or markers."),
        };

    private static SceneData Make(bool lines, string name, Options options) =>
        lines ? SceneData.Lines(name, options.Count, options.Polylines, options.Seed) : SceneData.Markers(name, options.Count, options.Seed);

    private static string Describe(bool lines, string method, SceneData scene, Options options) =>
        lines
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"kind=lines method={method} scene={scene.Name} segments={scene.Segments.Length} polylines={scene.Polylines.Length} width_px={options.Width} threshold={options.Threshold} plane={options.Plane} viewport={Width}x{Height} seed={options.Seed}"
            )
            : string.Create(
                CultureInfo.InvariantCulture,
                $"kind=markers method={method} scene={scene.Name} markers={scene.Vertices.Length} order=data diameter_px={options.Diameter} threshold={options.Threshold} plane={options.Plane} viewport={Width}x{Height} seed={options.Seed}"
            );

    private static DrawSettings Settings(int frame, Options options) =>
        new()
        {
            Transform = Orbit.Transform(frame),
            HalfWidth = options.Width * 0.5f,
            Radius = options.Diameter * 0.5f,
            Threshold = options.Threshold,
            Plane = options.Plane,
            PassTimes = options.PassTimes,
        };

    private static void Draw(Gpu gpu, bool lines, string method, in DrawSettings settings, long frame)
    {
        if (lines)
        {
            gpu.DrawLines(LineMethodOf(method), settings, frame);
        }
        else
        {
            gpu.DrawMarkers(MarkerMethodOf(method), settings, frame);
        }
    }

    private static LineMethod LineMethodOf(string name) =>
        name switch
        {
            "none" => LineMethod.None,
            "test" => LineMethod.Test,
            "prepass" => LineMethod.Prepass,
            "postpass" => LineMethod.Postpass,
            "capsules" => LineMethod.Capsules,
            "uav" => LineMethod.Uav,
            "uavfrag" => LineMethod.UavFragment,
            _ => throw new ArgumentException($"Unknown line method {name}: {string.Join(", ", _lineMethods)}."),
        };

    private static MarkerMethod MarkerMethodOf(string name) =>
        name switch
        {
            "none" => MarkerMethod.None,
            "quad" => MarkerMethod.Quad,
            "touched" => MarkerMethod.Touched,
            "half" => MarkerMethod.Half,
            _ => throw new ArgumentException($"Unknown marker method {name}: {string.Join(", ", _markerMethods)}."),
        };

    private static byte[] DrawAndRead(Gpu gpu, bool lines, string method, in DrawSettings settings)
    {
        Draw(gpu, lines, method, settings, frame: -1);
        gpu.Poll(static (_, _) => { }, wait: true);
        return gpu.Read();
    }

    private static string Name(SceneData scene, bool lines, Options options, int frame, string what) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{(lines ? "lines" : "markers")}-{scene.Name}-{(lines ? options.Width : options.Diameter)}px-plane{options.Plane}-f{frame}-{what}.bmp"
        );

    /// <summary>The errors of a GPU image (BGRA8) against the reference over the pixels near a shape.</summary>
    private static ErrorStats Errors(byte[] image, ReferenceImage reference)
    {
        const int Bins = 1024;
        var histogram = new int[Bins];
        var (sum, max, elsewhere, count) = (0.0, 0.0, 0.0, 0);
        var over = new int[_errorLevels.Length];
        for (var pixel = 0; pixel < Width * Height; pixel++)
        {
            var error = PixelError(image, reference.Rgb, pixel);
            if (!reference.Touched[pixel])
            {
                elsewhere = Math.Max(elsewhere, error);
                continue;
            }

            sum += error;
            max = Math.Max(max, error);
            count++;
            histogram[Math.Min(Bins - 1, (int)(error * Bins))]++;
            for (var level = 0; level < _errorLevels.Length; level++)
            {
                over[level] += error > _errorLevels[level] ? 1 : 0;
            }
        }

        // The 99th percentile to the bin's upper edge, 1/1024.
        var (seen, p99) = (0, 0.0);
        for (var bin = 0; bin < Bins; bin++)
        {
            seen += histogram[bin];
            if (seen >= count * 0.99)
            {
                p99 = (bin + 1) / (double)Bins;
                break;
            }
        }

        return new ErrorStats(count == 0 ? 0 : sum / count, p99, max, over, elsewhere);
    }

    // The largest of |GPU − reference| over R, G, and B in 0..1; the GPU's bytes are B, G, R, A.
    private static double PixelError(byte[] image, float[] rgb, int pixel)
    {
        var r = Math.Abs(image[(pixel * 4) + 2] - rgb[pixel * 3]);
        var g = Math.Abs(image[(pixel * 4) + 1] - rgb[(pixel * 3) + 1]);
        var b = Math.Abs(image[pixel * 4] - rgb[(pixel * 3) + 2]);
        return Math.Max(r, Math.Max(g, b)) / 255.0;
    }

    // White where the method agrees with the reference, red as the error grows, full red from 0.5.
    private static byte[] ErrorMap(byte[] image, ReferenceImage reference)
    {
        var map = new byte[Width * Height * 4];
        for (var pixel = 0; pixel < Width * Height; pixel++)
        {
            var other = (byte)(255 * (1 - Math.Min(1, 2 * PixelError(image, reference.Rgb, pixel))));
            map[pixel * 4] = other;
            map[(pixel * 4) + 1] = other;
            map[(pixel * 4) + 2] = 255;
            map[(pixel * 4) + 3] = 255;
        }

        return map;
    }

    // The reference as BGRA8, each channel rounded.
    private static byte[] ToBytes(float[] rgb)
    {
        var image = new byte[Width * Height * 4];
        for (var pixel = 0; pixel < Width * Height; pixel++)
        {
            image[pixel * 4] = (byte)MathF.Round(rgb[(pixel * 3) + 2]);
            image[(pixel * 4) + 1] = (byte)MathF.Round(rgb[(pixel * 3) + 1]);
            image[(pixel * 4) + 2] = (byte)MathF.Round(rgb[pixel * 3]);
            image[(pixel * 4) + 3] = 255;
        }

        return image;
    }

    private static void Print(string name, ReadOnlySpan<double> values)
    {
        var sorted = values.ToArray().Where(v => !double.IsNaN(v)).ToArray();
        if (sorted.Length == 0)
        {
            return;
        }

        Array.Sort(sorted);
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{name}: n={sorted.Length} p50={Percentile(sorted, 0.5):F3} p95={Percentile(sorted, 0.95):F3} p99={Percentile(sorted, 0.99):F3} max={sorted[^1]:F3} ms"
            )
        );
    }

    private static double Percentile(double[] sorted, double share) => sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * share))];

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

    /// <summary>The error of a method against the reference over the pixels near a shape.</summary>
    private sealed class ErrorStats(double mean, double p99, double max, int[] over, double maxElsewhere)
    {
        public double Mean { get; } = mean;

        public double P99 { get; } = p99;

        public double Max { get; } = max;

        /// <summary>The pixels whose error is above 0.1, 0.25, and 0.5.</summary>
        public int[] Over { get; } = over;

        /// <summary>The largest error away from every shape (a check of the mask: it should be 1/255 or less).</summary>
        public double MaxElsewhere { get; } = maxElsewhere;
    }

    /// <summary>The GPU times of the frames, by frame and bucket.</summary>
    private sealed class GpuTimes(int frames)
    {
        private const int Buckets = (int)Bucket.Total + 1;

        private readonly double[] _times = new double[frames * Buckets];
        private readonly bool[] _timed = new bool[frames];

        public void Take(long frame, ReadOnlySpan<double> buckets)
        {
            if (frame < 0 || frame >= _timed.Length)
            {
                return;
            }

            _timed[frame] = true;
            buckets.CopyTo(_times.AsSpan((int)frame * Buckets, Buckets));
        }

        public int Timed(int from) => _timed.AsSpan(from).Count(true);

        /// <summary>The time of a bucket in each timed frame from a frame on; NaN where the frame did not time it.</summary>
        public double[] Times(int bucket, int from)
        {
            var values = new List<double>();
            for (var f = from; f < _timed.Length; f++)
            {
                if (_timed[f])
                {
                    values.Add(_times[(f * Buckets) + bucket]);
                }
            }

            return [.. values];
        }

        public void Write(string path, double[] cpu, string[] names)
        {
            using var writer = new StreamWriter(path);
            writer.WriteLine($"frame,cpu_ms,{string.Join(',', names.Select(n => $"gpu_{n}_ms"))}");
            for (var f = 0; f < _timed.Length; f++)
            {
                var gpu = _timed[f]
                    ? string.Join(
                        ',',
                        Enumerable.Range(0, Buckets).Select(b => _times[(f * Buckets) + b].ToString("F4", CultureInfo.InvariantCulture))
                    )
                    : new string(',', Buckets - 1);
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{f},{cpu[f]:F4},{gpu}"));
            }
        }
    }

    /// <summary>The options after the positional arguments.</summary>
    private sealed record Options
    {
        public int Count { get; init; } = 1_000_000;

        /// <summary>The polylines the segments are split into; 0 for the scene's default (knot 8, walk 16).</summary>
        public int Polylines { get; init; }

        public float Width { get; init; } = 2;

        public float Diameter { get; init; } = 4;

        public float Threshold { get; init; } = 0.5f;

        public PlaneOrder Plane { get; init; } = PlaneOrder.Before;

        public int Frames { get; init; } = Orbit.Turn;

        public int Warmup { get; init; } = 120;

        public bool BackToBack { get; init; }

        /// <summary>Back to back, the frames left queued on the GPU after each frame is recorded.</summary>
        public int InFlight { get; init; } = 2;

        /// <summary>compare: the CSV that traces each pixel of postpass with an error above 0.5.</summary>
        public string? Trace { get; init; }

        /// <summary>compare --dump: the methods whose image and error map are written; all when null.</summary>
        public string[]? DumpMethods { get; init; }

        public bool Warp { get; init; }

        public bool Hardware { get; init; }

        public bool PassTimes { get; init; }

        public string? Csv { get; init; }

        public string? Dump { get; init; }

        public int Frame { get; init; } = 75;

        public int[] At { get; init; } = [0, 75, 150, 225];

        public int Samples { get; init; } = 16;

        public int Seed { get; init; } = 1;

        public static Options Parse(ReadOnlySpan<string> args)
        {
            var options = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                options = args[i] switch
                {
                    "--count" => options with { Count = (int)double.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--polylines" => options with { Polylines = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--width" => options with { Width = float.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--diameter" => options with { Diameter = float.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--threshold" => options with { Threshold = float.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--plane" => options with { Plane = PlaneOf(args[++i]) },
                    "--frames" => options with { Frames = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--warmup" => options with { Warmup = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--back-to-back" => options with { BackToBack = true },
                    "--in-flight" => options with { InFlight = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--trace" => options with { Trace = args[++i] },
                    "--dump-methods" => options with { DumpMethods = args[++i].Split(',') },
                    "--warp" => options with { Warp = true },
                    "--hardware" => options with { Hardware = true },
                    "--pass-times" => options with { PassTimes = true },
                    "--csv" => options with { Csv = args[++i] },
                    "--dump" => options with { Dump = args[++i] },
                    "--frame" => options with { Frame = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--at" => options with { At = [.. args[++i].Split(',').Select(a => int.Parse(a, CultureInfo.InvariantCulture))] },
                    "--samples" => options with { Samples = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--seed" => options with { Seed = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                    _ => throw new ArgumentException($"Unknown option {args[i]}."),
                };
            }

            if (options.PassTimes && options.Polylines > Gpu.MaxTimedPolylines)
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"--pass-times holds {Gpu.MaxTimedPolylines} polylines at most.")
                );
            }

            return options;
        }

        private static PlaneOrder PlaneOf(string name) =>
            name switch
            {
                "none" => PlaneOrder.None,
                "before" => PlaneOrder.Before,
                "after" => PlaneOrder.After,
                _ => throw new ArgumentException($"Unknown plane order {name}: none, before, or after."),
            };
    }
}
