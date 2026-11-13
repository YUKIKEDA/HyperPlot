using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace HyperPlot.Lines3D;

/// <summary>The measured scenes and the check of the bound (SPIKE, row K0b, Issue #488).</summary>
internal static class Scene
{
    /// <summary>The screen error allowed, in pixels (requirement K-3).</summary>
    private const float Tolerance = 1;

    /// <summary>The passes of a budgeted extraction before it gives up (it is then counted as incomplete).</summary>
    private const int MaxPasses = 12;

    private const double Elevation = Math.PI / 6;
    private const double Azimuth = Math.PI / 6;
    private const double MaxZoom = 1024;

    /// <summary>Builds the index and runs the scenes; returns the exit code.</summary>
    public static int Run(Options options)
    {
        var (data, index) = Prepare(options);
        var extractor = new Extractor(index);
        var budgets = options.Budgets;
        var budgetBuffer = new int[budgets.Max() + index.SegmentCount + 4];
        var fullCapacity = Math.Min(data.Count, options.FullCap);
        var fullBuffer = new int[fullCapacity + index.SegmentCount + 4];
        var middle = data[data.Count / 2];
        Pin(options);

        // Warm-up: the orbit's first frames, not recorded.
        for (var f = 0; f < options.Warmup; f++)
        {
            var view = SceneView("orbit", f, options.Frames, middle);
            foreach (var budget in budgets)
            {
                _ = Budgeted(extractor, view, budget, budgetBuffer, data.Count);
            }

            _ = extractor.Extract(view, Tolerance, fullCapacity, fullBuffer);
        }

        GC.Collect();
        using var csv = options.Csv is { } path ? new StreamWriter(path) : null;
        csv?.WriteLine("scene,frame,kind,budget,ms,vertices,passes,tolerance,complete");
        var frames = options.Frames;
        var fullFrames = (frames + options.FullEvery - 1) / options.FullEvery;
        foreach (var scene in options.Scenes)
        {
            var times = NewTable(budgets.Length, frames);
            var vertices = NewTable(budgets.Length, frames);
            var passes = NewTable(budgets.Length, frames);
            var tolerances = NewTable(budgets.Length, frames);
            var incomplete = new int[budgets.Length];
            var fullTimes = new double[fullFrames];
            var fullVertices = new double[fullFrames];
            var capped = 0;
            long maxBytes = 0;
            var began = DateTime.Now;
            for (var f = 0; f < frames; f++)
            {
                var view = SceneView(scene, f, frames, middle);
                for (var b = 0; b < budgets.Length; b++)
                {
                    var result = Budgeted(extractor, view, budgets[b], budgetBuffer, data.Count);
                    times[b][f] = result.Ms;
                    vertices[b][f] = result.Vertices;
                    passes[b][f] = result.Passes;
                    tolerances[b][f] = result.Tolerance;
                    incomplete[b] += result.Complete ? 0 : 1;
                    maxBytes = Math.Max(maxBytes, result.Bytes);
                    csv?.WriteLine(
                        FormattableString.Invariant(
                            $"{scene},{f},budget,{budgets[b]},{result.Ms:F4},{result.Vertices},{result.Passes},{result.Tolerance:G5},{result.Complete}"
                        )
                    );
                }

                if (f % options.FullEvery == 0)
                {
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    var start = Stopwatch.GetTimestamp();
                    var full = extractor.Extract(view, Tolerance, fullCapacity, fullBuffer);
                    var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    maxBytes = Math.Max(maxBytes, GC.GetAllocatedBytesForCurrentThread() - before);
                    fullTimes[f / options.FullEvery] = ms;
                    fullVertices[f / options.FullEvery] = full.Vertices;
                    capped += full.Aborted ? 1 : 0;
                    csv?.WriteLine(FormattableString.Invariant($"{scene},{f},full,,{ms:F4},{full.Vertices},1,{Tolerance},{!full.Aborted}"));
                }
            }

            var label = Label(options, data.Count, scene);
            Line($"{label} frames={frames} ran {began:HH:mm:ss} to {DateTime.Now:HH:mm:ss}");
            for (var b = 0; b < budgets.Length; b++)
            {
                var name = string.Create(CultureInfo.InvariantCulture, $"{label} budget={budgets[b]}");
                Print($"{name} extract_ms", times[b]);
                Print($"{name} vertices", vertices[b]);
                Print($"{name} passes", passes[b]);
                Print($"{name} tolerance_px", tolerances[b]);
                Line($"{name} frames_over_budget_at_1px={passes[b].Count(p => p > 1)} incomplete={incomplete[b]}");
            }

            Print($"{label} full extract_ms", fullTimes);
            Print($"{label} full vertices", fullVertices);
            Line($"{label} full frames={fullFrames} capped_at={fullCapacity} capped={capped}");
            Line($"{label} alloc_max_bytes_per_call={maxBytes}");
        }

        return 0;
    }

    /// <summary>Checks the bound against brute force over every vertex; returns 1 when a vertex is past it.</summary>
    public static int Verify(Options options)
    {
        var (data, index) = Prepare(options);
        var extractor = new Extractor(index);
        var n = data.Count;
        var buffer = new int[n + index.SegmentCount + 4];
        var camera = new Vector3[n];
        var inside = new bool[n];
        var stack = new (int A, int B)[index.SegmentSize + 4];
        var middle = data[n / 2];
        var failed = false;
        foreach (var scene in options.Scenes)
        {
            var maxError = 0.0;
            long violations = 0;
            long uncovered = 0;
            long insideSum = 0;
            var selected = new List<double>();
            var oracle = new List<double>();
            var ratios = new List<double>();
            for (var f = 0; f < options.Frames; f += options.Every)
            {
                var view = SceneView(scene, f, options.Frames, middle);
                var insideCount = 0;
                for (var i = 0; i < n; i++)
                {
                    var box = view.ToBox(data[i]);
                    inside[i] = View.Inside(box);
                    insideCount += inside[i] ? 1 : 0;
                    camera[i] = view.ToCamera(box);
                }

                var result = extractor.Extract(view, Tolerance, n + 1, buffer);
                var check = Check(view, buffer.AsSpan(0, result.Length), camera, inside);
                var screenDp = ScreenDp(index, view, camera, inside, stack);
                maxError = Math.Max(maxError, check.MaxError);
                violations += check.Violations;
                uncovered += check.Uncovered;
                insideSum += insideCount;
                selected.Add(result.Vertices);
                oracle.Add(screenDp);
                ratios.Add(screenDp > 0 ? (double)result.Vertices / screenDp : double.NaN);
                Line(
                    $"verify scene={scene} frame={f} inside={insideCount} vertices={result.Vertices} screen_dp={screenDp} max_error_px={check.MaxError:F4} violations={check.Violations} uncovered={check.Uncovered}"
                );
            }

            var label = Label(options, n, scene);
            Line(
                $"{label} verify frames_checked={selected.Count} max_error_px={maxError:F4} violations={violations} uncovered_inside={uncovered} inside_mean={(double)insideSum / selected.Count:F0}"
            );
            Print($"{label} verify vertices", [.. selected]);
            Print($"{label} verify screen_dp_vertices", [.. oracle]);
            Print($"{label} verify ratio_to_screen_dp", [.. ratios.Where(r => !double.IsNaN(r))]);
            failed |= violations > 0 || uncovered > 0;
        }

        Line($"verify result={(failed ? "FAILED" : "ok")}");
        return failed ? 1 : 0;
    }

    /// <summary>The scene's view at a frame.</summary>
    private static View SceneView(string scene, int frame, int frames, Vector3 middle)
    {
        var zoom = (float)Math.Pow(MaxZoom, frames > 1 ? (double)frame / (frames - 1) : 0);
        var full = new Vector3(2);
        var half = new Vector3(0.5f);
        return scene switch
        {
            "orbit" => new View(half, full, 2 * Math.PI * frame / frames, Elevation),
            "zoom-x" => new View(new Vector3(middle.X, 0.5f, 0.5f), new Vector3(2 * zoom, 2, 2), Azimuth, Elevation),
            "zoom-z" => new View(new Vector3(0.5f, 0.5f, middle.Z), new Vector3(2, 2, 2 * zoom), Azimuth, Elevation),
            "zoom-xyz" => new View(middle, full * zoom, Azimuth, Elevation),
            _ => throw new ArgumentException($"Unknown scene {scene}: orbit, zoom-x, zoom-z, or zoom-xyz.", nameof(scene)),
        };
    }

    /// <summary>
    /// One frame's extraction under a budget: from 1 pixel, the tolerance is raised by the square root of the estimated
    /// overshoot (at least 1.25 times) until the vertices fit.
    /// </summary>
    private static (double Ms, int Vertices, int Passes, float Tolerance, bool Complete, long Bytes) Budgeted(
        Extractor extractor,
        in View view,
        int budget,
        int[] buffer,
        int count
    )
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        var tolerance = Tolerance;
        var passes = 0;
        Extraction result;
        while (true)
        {
            passes++;
            result = extractor.Extract(view, tolerance, budget, buffer);
            if (!result.Aborted || passes == MaxPasses)
            {
                break;
            }

            var done = Math.Max((double)result.Reached / count, 1e-3);
            var estimate = result.Vertices / done;
            tolerance *= (float)Math.Clamp(Math.Sqrt(estimate / budget) * 1.1, 1.25, 64);
        }

        var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        return (ms, result.Vertices, passes, tolerance, !result.Aborted, bytes);
    }

    /// <summary>
    /// The largest distance on the screen of a vertex inside the axis box from the drawn chord that skips it, the vertices
    /// past the tolerance, and the vertices inside the box that no chord covers (in a culled gap).
    /// </summary>
    private static (double MaxError, long Violations, long Uncovered) Check(
        in View view,
        ReadOnlySpan<int> output,
        Vector3[] camera,
        bool[] inside
    )
    {
        var max = 0.0;
        long violations = 0;
        long uncovered = 0;
        var accounted = 0;
        var previous = -1;
        foreach (var v in output)
        {
            if (v < 0)
            {
                previous = -1;
                continue;
            }

            if (previous >= 0 && Chord(view, camera[previous], camera[v], out var sa, out var sb))
            {
                for (var i = previous + 1; i < v; i++)
                {
                    if (inside[i])
                    {
                        var d = Distance(view.Screen(camera[i]), sa, sb);
                        max = Math.Max(max, d);
                        violations += d > Tolerance * (1 + 1e-3) ? 1 : 0;
                    }
                }
            }
            else
            {
                for (var i = accounted; i < v; i++)
                {
                    uncovered += inside[i] ? 1 : 0;
                }
            }

            accounted = v + 1;
            previous = v;
        }

        for (var i = accounted; i < inside.Length; i++)
        {
            uncovered += inside[i] ? 1 : 0;
        }

        return (max, violations, uncovered);
    }

    /// <summary>
    /// The vertices a Douglas–Peucker made for this one view keeps: over the same segments (culled ones left out), split
    /// at the vertex inside the axis box farthest on the screen from the chord, while that distance passes 1 pixel.
    /// </summary>
    private static int ScreenDp(LineIndex index, in View view, Vector3[] camera, bool[] inside, (int A, int B)[] stack)
    {
        var count = 0;
        var lastWritten = -1;
        for (var s = 0; s < index.SegmentCount; s++)
        {
            var segment = index.Segments[s];
            if (view.Outside(segment.Lo, segment.Hi))
            {
                continue;
            }

            var v0 = index.Start(s);
            var v1 = index.End(s);
            count += lastWritten == v0 ? 0 : 1;
            var sp = 0;
            stack[sp++] = (v0, v1);
            while (sp > 0)
            {
                var (a, b) = stack[--sp];
                if (b - a < 2)
                {
                    continue;
                }

                var drawn = Chord(view, camera[a], camera[b], out var sa, out var sb);
                var worst = 0.0;
                var m = -1;
                for (var i = a + 1; i < b; i++)
                {
                    if (inside[i])
                    {
                        var d = drawn ? Distance(view.Screen(camera[i]), sa, sb) : double.PositiveInfinity;
                        if (d > worst)
                        {
                            worst = d;
                            m = i;
                        }
                    }
                }

                if (m >= 0 && worst > Tolerance)
                {
                    count++;
                    stack[sp++] = (a, m);
                    stack[sp++] = (m, b);
                }
            }

            count++;
            lastWritten = v1;
        }

        return count;
    }

    /// <summary>The chord's ends on the screen, the chord cut at the near plane; false when it is wholly behind it.</summary>
    private static bool Chord(in View view, Vector3 a, Vector3 b, out Vector2 sa, out Vector2 sb)
    {
        sa = default;
        sb = default;
        if (a.Z < View.Near && b.Z < View.Near)
        {
            return false;
        }

        if (a.Z < View.Near)
        {
            a = Vector3.Lerp(a, b, (View.Near - a.Z) / (b.Z - a.Z));
        }
        else if (b.Z < View.Near)
        {
            b = Vector3.Lerp(b, a, (View.Near - b.Z) / (a.Z - b.Z));
        }

        sa = view.Screen(a);
        sb = view.Screen(b);
        return true;
    }

    private static double Distance(Vector2 p, Vector2 a, Vector2 b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double px = p.X - a.X;
        double py = p.Y - a.Y;
        var dd = (dx * dx) + (dy * dy);
        var t = dd > 0 ? Math.Clamp(((px * dx) + (py * dy)) / dd, 0, 1) : 0;
        var ex = px - (t * dx);
        var ey = py - (t * dy);
        return Math.Sqrt((ex * ex) + (ey * ey));
    }

    /// <summary>Generates the line and builds the index, and prints the build's time, CPU, and memory.</summary>
    private static (LineData Data, LineIndex Index) Prepare(Options options)
    {
        var start = Stopwatch.GetTimestamp();
        var data = LineData.Make(options.Data, options.Count, options.Seed, options.Noise, options.Turns);
        var generate = Stopwatch.GetElapsedTime(start).TotalSeconds;
        Line(
            $"data={options.Data} count={data.Count} seed={options.Seed} noise={options.Noise} turns={options.Turns} generate_s={generate:F2}"
        );

        var index = new LineIndex(data, options.SegmentLog2, options.Split);
        using var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        start = Stopwatch.GetTimestamp();
        index.Build(options.Workers);
        var wall = Stopwatch.GetElapsedTime(start).TotalSeconds;
        process.Refresh();
        var cpu = (process.TotalProcessorTime - cpuBefore).TotalSeconds;
        Line(
            $"build segment=2^{options.SegmentLog2} segments={index.SegmentCount} split={options.SplitName} workers={options.Workers} seconds={wall:F3} cpu_seconds={cpu:F2} cores={cpu / wall:F2} depth_mean={index.MeanDepth:F1} depth_max={index.MaxDepth}"
        );

        // Memory over the data: the x, y, z columns as float32 (12 B a vertex) and as float64 (24 B).
        long n = data.Count;
        var errorBytes = n * Unsafe.SizeOf<Half3>();
        var offsetBytes = n * sizeof(uint);
        var segmentBytes = (long)index.SegmentCount * Unsafe.SizeOf<Segment>();
        var total = errorBytes + offsetBytes + segmentBytes;
        var packedOffset = options.SegmentLog2 <= 16 ? 2 : 3;
        var packed = errorBytes + (n * packedOffset) + segmentBytes;
        Line(
            $"memory errors_bytes={errorBytes} offsets_bytes={offsetBytes} segments_bytes={segmentBytes} total_bytes={total} bytes_per_vertex={(double)total / n:F3} percent_of_f32_xyz={100.0 * total / (12 * n):F1} percent_of_f64_xyz={100.0 * total / (24 * n):F1}"
        );
        Line(
            $"memory_packed offset_bytes_each={packedOffset} total_bytes={packed} bytes_per_vertex={(double)packed / n:F3} percent_of_f32_xyz={100.0 * packed / (12 * n):F1} percent_of_f64_xyz={100.0 * packed / (24 * n):F1}"
        );
        Line($"process peak_working_set_mb={process.PeakWorkingSet64 / (1024.0 * 1024):F0}");
        return (data, index);
    }

    /// <summary>Pins the process to logical processor 2, a performance core, at high priority (.cursor/rules/bench.mdc).</summary>
    private static void Pin(Options options)
    {
        if (!options.Pin)
        {
            Line($"pinned=false");
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            Line($"pinned=false (not Windows)");
            return;
        }

        using var process = Process.GetCurrentProcess();
        process.ProcessorAffinity = 1 << 2;
        process.PriorityClass = ProcessPriorityClass.High;
        Line($"pinned=true affinity=0x4 priority=High");
    }

    private static string Label(Options options, int count, string scene) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"scene={scene} data={options.Data} count={count} segment=2^{options.SegmentLog2} split={options.SplitName}"
        );

    private static double[][] NewTable(int rows, int columns)
    {
        var table = new double[rows][];
        for (var r = 0; r < rows; r++)
        {
            table[r] = new double[columns];
        }

        return table;
    }

    private static void Print(string label, double[] values)
    {
        if (values.Length == 0)
        {
            Line($"{label} none");
            return;
        }

        var sorted = values.ToArray();
        Array.Sort(sorted);
        Line(
            $"{label} p50={Percentile(sorted, 0.50):G6} p95={Percentile(sorted, 0.95):G6} p99={Percentile(sorted, 0.99):G6} max={sorted[^1]:G6} mean={sorted.Average():G6} n={sorted.Length}"
        );
    }

    private static double Percentile(double[] sorted, double p) =>
        sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];

    private static void Line(FormattableString text) => Console.WriteLine(FormattableString.Invariant(text));
}
