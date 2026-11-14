using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace HyperPlot.Points3D;

/// <summary>
/// The octree scenes (SPIKE, row K0a, Issue #477, hypotheses 2 to 4): the build, its nodes and memory, the hit test of
/// requirement K-7, and the orbit drawn through the octree under a point budget, with drawing method A (Issue #477's
/// decision of 2026-11-14).
/// </summary>
internal static partial class Scene
{
    private const string DefaultVariants = "3e6:2:morton,1e7:2:morton,2e7:2:morton,1e7:6:morton,1e7:2:priority";
    private const int HashSlice = 1 << 20;
    private const int FillSlice = 1 << 20;

    /// <summary>The affinity of the hit tests with --pin: logical processor 2, a performance core (.cursor/rules/bench.mdc).</summary>
    private const uint PinnedMask = 1u << 2;

    /// <summary>Which points a hit test searches, and how.</summary>
    private enum HitSearch
    {
        /// <summary>Every point: the octree from the root, a subtree skipped when its cube cannot reach the cursor.</summary>
        Full,

        /// <summary>The points of the nodes the budget draws.</summary>
        Drawn,

        /// <summary>Every point, nearest first, until no cube can hold a nearer point (K-7, Issue #477).</summary>
        Nearest,
    }

    /// <summary>Builds the octree and prints its figures, then runs the hit tests; returns the exit code.</summary>
    public static int OctreeBuild(ReadOnlySpan<string> args)
    {
        var count = (int)double.Parse(args[0], CultureInfo.InvariantCulture);
        var options = OctreeOptions.Parse(args[2..]);
        var tree = BuildTree(count, args[1], options);
        if (options.Hash)
        {
            Console.WriteLine($"hash={HashTree(tree)}");
        }

        return options.Hits > 0 ? HitTests(tree, options) : 0;
    }

    /// <summary>
    /// Builds the octree, then runs the orbit through it once for each variant (a budget, a disc's diameter, and the order
    /// of the points inside a node), in the order given or reversed; returns the exit code.
    /// </summary>
    public static int OctreeOrbit(ReadOnlySpan<string> args)
    {
        var count = (int)double.Parse(args[0], CultureInfo.InvariantCulture);
        var options = OctreeOptions.Parse(args[2..]);
        var variants = ParseVariants(options.Variants);
        if (options.Reverse)
        {
            Array.Reverse(variants);
        }

        var tree = BuildTree(count, args[1], options);
        var uploaded = Stopwatch.StartNew();
        using var gpu = new Gpu(options.Warp, Width, Height, []);
        gpu.SetNodes(tree.Nodes, GpuPoints(tree, tree.Order, options.Workers));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"gpu_upload_ms={uploaded.Elapsed.TotalMilliseconds:F0}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"gpu_pages={gpu.Pages}"));
        var priority = false;
        int[]? priorityRows = null;
        foreach (var variant in variants)
        {
            if (variant.Priority != priority)
            {
                priorityRows ??= PriorityRows(tree, options);
                gpu.SetNodes(tree.Nodes, GpuPoints(tree, variant.Priority ? priorityRows : tree.Order, options.Workers));
                priority = variant.Priority;
            }

            OrbitVariant(gpu, tree, variant, options);
        }

        return 0;
    }

    private static Octree BuildTree(int count, string distribution, OctreeOptions options)
    {
        var cluster = Distribution(distribution);
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"points={count} dist={distribution} grid={1 << options.GridBits} leaf={options.Leaf} workers={options.Workers} seed={options.Seed}"
            )
        );
        var generating = Stopwatch.StartNew();
        var columns = PointData.MakeColumns(count, cluster, options.Seed, options.Workers);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"generated_ms={generating.Elapsed.TotalMilliseconds:F0}"));
        var allocated = GC.GetTotalAllocatedBytes();
        var tree = Octree.Build(columns.X, columns.Y, columns.Z, options.GridBits, options.Leaf, options.Workers, options.Seed);
        allocated = GC.GetTotalAllocatedBytes() - allocated;
        PrintTree(tree, allocated);
        return tree;
    }

    /// <summary>Prints the build's stages, the nodes by level and by size, and the memory against the source's 12 bytes a point.</summary>
    private static void PrintTree(Octree tree, long allocated)
    {
        var times = tree.Times;
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"build_ms: keys={times.Keys:F1} sort={times.Sort:F1} levels={times.Levels:F1} table={times.Table:F1} copy={times.Copy:F1} total={times.Total:F1}"
            )
        );

        var nodes = tree.Nodes;
        var levelNodes = new long[Octree.Bits + 1];
        var levelPoints = new long[Octree.Bits + 1];
        var inner = new List<double>();
        var leaves = new List<double>();
        var maxLevel = 0;
        for (var i = 0; i < nodes.Length; i++)
        {
            var node = nodes[i];
            levelNodes[node.Level]++;
            levelPoints[node.Level] += node.Count;
            maxLevel = Math.Max(maxLevel, node.Level);
            (node.SubtreeEnd == i + 1 ? leaves : inner).Add(node.Count);
        }

        Console.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"nodes={nodes.Length} with_children={inner.Count} without_children={leaves.Count} max_level={maxLevel}")
        );
        for (var level = 0; level <= maxLevel; level++)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"level {level}: nodes={levelNodes[level]} points={levelPoints[level]}"));
        }

        PrintCount("node_points_with_children", CollectionsMarshal.AsSpan(inner));
        PrintCount("node_points_without_children", CollectionsMarshal.AsSpan(leaves));

        long points = tree.Copy.Length;
        var source = points * 12;
        var copy = points * Unsafe.SizeOf<CopyPoint>();
        var table = (long)nodes.Length * Unsafe.SizeOf<OctreeNode>();
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"memory: source_bytes={source} copy_bytes={copy} copy_percent={100.0 * copy / source:F1} table_bytes={table} table_percent={100.0 * table / source:F4} table_bytes_a_point={(double)table / points:F4} node_record_bytes={Unsafe.SizeOf<OctreeNode>()} copy_and_table_percent={100.0 * (copy + table) / source:F2}"
            )
        );
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"spike_arrays: sorted_keys_bytes={points * 8} order_bytes={points * 4} build_scratch_bytes={points * 13} gpu_form_bytes={points * 16}"
            )
        );
        using var process = Process.GetCurrentProcess();
        Console.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"peak_working_set_bytes={process.PeakWorkingSet64} allocated_by_build_bytes={allocated}")
        );
    }

    /// <summary>
    /// Runs the hit tests at cursors over the orbit: at each of the hit frames, half the cursors anywhere over the box on the
    /// screen and half on the projection of a point of the data, where the points are dense. "full" finds the nearest point
    /// among all points, "drawn" among the nodes the budget draws, and "nearest" among all points, nearest first with an
    /// early exit. Each mode runs over every cursor once to warm, then once timed. Then "nearest" is checked against "full"
    /// at every query, and both against every point projected one by one at a few. Returns 1 when one differs.
    /// </summary>
    private static int HitTests(Octree tree, OctreeOptions options)
    {
        var random = new Random(options.Seed);
        var queries = options.HitFrames * options.Hits;
        var frames = new int[queries];
        var cursors = new Vector2[queries];
        var selections = new List<int>[options.HitFrames];
        var queue = new PriorityQueue<int, (float Size, int Node)>();
        for (var f = 0; f < options.HitFrames; f++)
        {
            var frame = f * Turn / options.HitFrames;
            var camera = new OrbitCamera(frame);
            selections[f] = [];
            _ = tree.Select(camera.Eye, camera.Focal, options.HitBudget, selections[f], queue);
            var low = new Vector2(float.MaxValue);
            var high = new Vector2(float.MinValue);
            for (var corner = 0; corner < 8; corner++)
            {
                var at = ToPixel(
                    camera.Transform,
                    new Vector3((corner & 1) != 0 ? 1 : -1, (corner & 2) != 0 ? 1 : -1, (corner & 4) != 0 ? 1 : -1)
                );
                low = Vector2.Min(low, at);
                high = Vector2.Max(high, at);
            }

            for (var k = 0; k < options.Hits; k++)
            {
                var q = (f * options.Hits) + k;
                frames[q] = frame;
                if (k % 2 == 0)
                {
                    cursors[q] = low + (new Vector2(random.NextSingle(), random.NextSingle()) * (high - low));
                }
                else
                {
                    var row = random.Next(tree.X.Length);
                    cursors[q] = ToPixel(camera.Transform, new Vector3(tree.X[row], tree.Y[row], tree.Z[row]));
                }
            }
        }

        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"hit: queries={queries} frames={options.HitFrames} radius_px={options.HitRadius} drawn_budget={options.HitBudget} pinned={options.Pin}"
            )
        );
        var tester = new HitTester(tree, Width, Height);
        nuint previous = 0;
        if (options.Pin)
        {
            previous = SetThreadAffinityMask(GetCurrentThread(), PinnedMask);
        }

        bool agree;
        try
        {
            RunHits("hit_full", tester, frames, cursors, selections, options, HitSearch.Full);
            RunHits("hit_drawn", tester, frames, cursors, selections, options, HitSearch.Drawn);
            RunHits("hit_nearest", tester, frames, cursors, selections, options, HitSearch.Nearest);
            agree = CompareNearest(tester, frames, cursors, options);
            agree &= CheckHits(tester, frames, cursors, options);
        }
        finally
        {
            if (previous != 0)
            {
                _ = SetThreadAffinityMask(GetCurrentThread(), previous);
            }
        }

        return agree ? 0 : 1;
    }

    private static void RunHits(
        string name,
        HitTester tester,
        int[] frames,
        Vector2[] cursors,
        List<int>[] selections,
        OctreeOptions options,
        HitSearch search
    )
    {
        var times = new double[frames.Length];
        var tested = new double[frames.Length];
        var found = 0;
        for (var pass = 0; pass < 2; pass++)
        {
            for (var q = 0; q < frames.Length; q++)
            {
                var camera = new OrbitCamera(frames[q]);
                var start = Stopwatch.GetTimestamp();
                tester.Begin(camera.Transform, camera.View, camera.Focal, cursors[q], options.HitRadius);
                switch (search)
                {
                    case HitSearch.Drawn:
                        tester.Drawn(CollectionsMarshal.AsSpan(selections[q / options.Hits]));
                        break;
                    case HitSearch.Nearest:
                        tester.Nearest();
                        break;
                    default:
                        tester.Full();
                        break;
                }

                times[q] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                tested[q] = tester.Tested;
                found += pass == 1 && tester.Best >= 0 ? 1 : 0;
            }
        }

        Print(name, times);
        PrintCount($"{name}_points_tested", tested);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name}: found={found} of {frames.Length}"));
    }

    /// <summary>
    /// Checks the nearest-first search against the full one at every query: the same point, or none for both. Prints the
    /// first queries that differ and the count; returns whether all agree.
    /// </summary>
    private static bool CompareNearest(HitTester tester, int[] frames, Vector2[] cursors, OctreeOptions options)
    {
        var agree = 0;
        for (var q = 0; q < frames.Length; q++)
        {
            var camera = new OrbitCamera(frames[q]);
            tester.Begin(camera.Transform, camera.View, camera.Focal, cursors[q], options.HitRadius);
            tester.Full();
            var full = tester.Best;
            tester.Begin(camera.Transform, camera.View, camera.Focal, cursors[q], options.HitRadius);
            tester.Nearest();
            var nearest = tester.Best;
            if (full == nearest)
            {
                agree++;
            }
            else if (q - agree < 10)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"hit_nearest_vs_full: query {q} full={full} nearest={nearest}"));
            }
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"hit_nearest_vs_full: agree={agree} of {frames.Length}"));
        return agree == frames.Length;
    }

    /// <summary>
    /// Checks the full and the nearest-first answers against every point projected one by one, at evenly spaced queries.
    /// Prints a line for each search; returns whether both agree at every check.
    /// </summary>
    private static bool CheckHits(HitTester tester, int[] frames, Vector2[] cursors, OctreeOptions options)
    {
        var checks = Math.Min(options.HitCheck, frames.Length);
        var agree = 0;
        var nearestAgree = 0;
        for (var c = 0; c < checks; c++)
        {
            var q = (int)((long)c * frames.Length / checks);
            var camera = new OrbitCamera(frames[q]);
            tester.Begin(camera.Transform, camera.View, camera.Focal, cursors[q], options.HitRadius);
            tester.Full();
            var octree = tester.Best;
            tester.Begin(camera.Transform, camera.View, camera.Focal, cursors[q], options.HitRadius);
            tester.Nearest();
            var nearest = tester.Best;
            tester.Begin(camera.Transform, camera.View, camera.Focal, cursors[q], options.HitRadius);
            tester.All();
            var every = tester.Best;
            agree += octree == every ? 1 : 0;
            nearestAgree += nearest == every ? 1 : 0;
            if (octree != every || nearest != every)
            {
                Console.WriteLine(
                    string.Create(CultureInfo.InvariantCulture, $"hit_check: query {q} octree={octree} nearest={nearest} every_point={every}")
                );
            }
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"hit_check: agree={agree} of {checks}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"hit_check_nearest: agree={nearestAgree} of {checks}"));
        return agree == checks && nearestAgree == checks;
    }

    private static Vector2 ToPixel(Matrix4x4 transform, Vector3 position)
    {
        var clip = Vector4.Transform(new Vector4(position, 1), transform);
        return new Vector2(((clip.X / clip.W * 0.5f) + 0.5f) * Width, (0.5f - (clip.Y / clip.W * 0.5f)) * Height);
    }

    /// <summary>
    /// One variant of the orbit through the octree: each frame chooses the nodes under the budget (CPU), records one draw a
    /// node and sends it, at the frame period. Prints the CPU time of the choice and of the recording, the GPU time, and the
    /// points and nodes drawn, over the frames after the warm-up.
    /// </summary>
    private static void OrbitVariant(Gpu gpu, Octree tree, Variant variant, OctreeOptions options)
    {
        var total = options.Warmup + options.Frames;
        var cpu = new double[total];
        var choosing = new double[total];
        var recording = new double[total];
        var points = new double[total];
        var drawnNodes = new double[total];
        var gpuTimes = new GpuTimes(total);
        TimedHandler take = gpuTimes.Take;
        var selected = new List<int>();
        var queue = new PriorityQueue<int, (float Size, int Node)>();
        var drawOptions = new Options { Diameter = variant.Diameter };
        var period = TimeSpan.FromMilliseconds(1000.0 / 60);

        // A wait of a few milliseconds needs the timer's finer step (.cursor/rules/bench.mdc).
        _ = TimeBeginPeriod(1);
        var framesFrom = DateTime.Now;
        var began = Stopwatch.GetTimestamp();
        try
        {
            for (var f = 0; f < total; f++)
            {
                var start = Stopwatch.GetTimestamp();
                var camera = new OrbitCamera(f);
                points[f] = tree.Select(camera.Eye, camera.Focal, variant.Budget, selected, queue);
                var chosen = Stopwatch.GetTimestamp();
                gpu.DrawNodes(Constants(f, 0, drawOptions, 0), CollectionsMarshal.AsSpan(selected), tree.Nodes, f);
                gpu.Poll(take, wait: false);
                var end = Stopwatch.GetTimestamp();
                choosing[f] = Stopwatch.GetElapsedTime(start, chosen).TotalMilliseconds;
                recording[f] = Stopwatch.GetElapsedTime(chosen, end).TotalMilliseconds;
                cpu[f] = Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;
                drawnNodes[f] = selected.Count;

                // A frame a period at the fastest (.cursor/rules/bench.mdc).
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
                $"variant {variant.Name} viewport={Width}x{Height} device={(gpu.IsWarp ? "WARP" : "hardware")} paced={!options.BackToBack} frames={options.Frames} (after {options.Warmup}) frame_period_ms={seconds * 1000 / total:F2}"
            )
        );
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"frames ran {framesFrom:HH:mm:ss.fff} to {framesTo:HH:mm:ss.fff}"));
        Print("cpu_select", choosing.AsSpan(options.Warmup));
        Print("cpu_record", recording.AsSpan(options.Warmup));
        Print("cpu_frame", cpu.AsSpan(options.Warmup));
        PrintCount("points_drawn", points.AsSpan(options.Warmup));
        PrintCount("nodes_drawn", drawnNodes.AsSpan(options.Warmup));
        Print("gpu_clear", gpuTimes.Stage(0, options.Warmup));
        Print("gpu_draw", gpuTimes.Stage(1, options.Warmup));
        Print("gpu_total", gpuTimes.Stage(1, options.Warmup, cumulative: true));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"gpu frames timed: {gpuTimes.Timed(options.Warmup)} of {options.Frames}"));
    }

    /// <summary>The points as the GPU reads them, in the copy's order with the rows given for each place.</summary>
    private static Point3[] GpuPoints(Octree tree, int[] rows, int workers)
    {
        var points = new Point3[rows.Length];
        var slices = (int)(((long)rows.Length + FillSlice - 1) / FillSlice);
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = workers };
        Parallel.For(0, slices, parallel, slice => FillPoints(slice, tree, rows, points));
        return points;
    }

    private static void FillPoints(int slice, Octree tree, int[] rows, Point3[] points)
    {
        var from = slice * FillSlice;
        var to = Math.Min(rows.Length, from + FillSlice);
        for (var i = from; i < to; i++)
        {
            var row = rows[i];
            points[i] = new Point3 { Position = new Vector3(tree.X[row], tree.Y[row], tree.Z[row]), Color = PointData.ColorOf(row) };
        }
    }

    /// <summary>The rows of the copy with each node's points in the order of their seeded priority (decision 7's shuffle) instead of their keys.</summary>
    private static int[] PriorityRows(Octree tree, OctreeOptions options)
    {
        var rows = (int[])tree.Order.Clone();
        var priorities = new ulong[rows.Length];
        var seedMix = Octree.SeedMix(options.Seed);
        var nodes = tree.Nodes;
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = options.Workers };
        Parallel.For(0, nodes.Length, parallel, n => SortByPriority(nodes[n], rows, priorities, seedMix));
        return rows;
    }

    private static void SortByPriority(OctreeNode node, int[] rows, ulong[] priorities, ulong seedMix)
    {
        var from = (int)node.First;
        var to = from + node.Count;
        for (var i = from; i < to; i++)
        {
            priorities[i] = Octree.Priority(rows[i], seedMix);
        }

        Array.Sort(priorities, rows, from, node.Count);
    }

    /// <summary>A SHA-256 of the table and the copy, to compare builds (the same input on other workers gives the same bytes).</summary>
    private static string HashTree(Octree tree)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> record = stackalloc byte[28];
        foreach (var node in tree.Nodes)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(record, node.Code);
            BinaryPrimitives.WriteInt64LittleEndian(record[8..], node.First);
            BinaryPrimitives.WriteInt32LittleEndian(record[16..], node.Count);
            BinaryPrimitives.WriteInt32LittleEndian(record[20..], node.SubtreeEnd);
            BinaryPrimitives.WriteInt32LittleEndian(record[24..], node.Level);
            hash.AppendData(record);
        }

        for (var from = 0; from < tree.Copy.Length; from += HashSlice)
        {
            var length = Math.Min(HashSlice, tree.Copy.Length - from);
            hash.AppendData(MemoryMarshal.AsBytes(tree.Copy.AsSpan(from, length)));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static Variant[] ParseVariants(string text)
    {
        var parts = text.Split(',');
        var variants = new Variant[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var fields = parts[i].Split(':');
            if (fields.Length != 3 || fields[2] is not ("morton" or "priority"))
            {
                throw new ArgumentException($"A variant is budget:diameter:morton|priority, not {parts[i]}.");
            }

            variants[i] = new Variant
            {
                Budget = (long)double.Parse(fields[0], CultureInfo.InvariantCulture),
                Diameter = float.Parse(fields[1], CultureInfo.InvariantCulture),
                Priority = fields[2] == "priority",
            };
        }

        return variants;
    }

    private static void PrintCount(string name, ReadOnlySpan<double> values)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{name}: n={sorted.Length} min={(sorted.Length == 0 ? double.NaN : sorted[0]):F0} p50={Percentile(sorted, 0.5):F0} p95={Percentile(sorted, 0.95):F0} p99={Percentile(sorted, 0.99):F0} max={(sorted.Length == 0 ? double.NaN : sorted[^1]):F0}"
            )
        );
    }

    [LibraryImport("kernel32.dll", EntryPoint = "SetThreadAffinityMask")]
    private static partial nuint SetThreadAffinityMask(nint thread, nuint mask);

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentThread")]
    private static partial nint GetCurrentThread();

    /// <summary>
    /// The camera of a frame of the orbit: it turns about the box's vertical axis (z), 360° every 600 frames, 30° above the
    /// horizon, at the distance where the box's bounding sphere (radius √3) fits the vertical field of view of 45° (design
    /// §13.8, decision 16). The near and far planes hold the sphere.
    /// </summary>
    private readonly struct OrbitCamera
    {
        private const float FieldOfView = MathF.PI / 4;
        private const float Elevation = MathF.PI / 6;

        public OrbitCamera(int frame)
        {
            var angle = 2 * MathF.PI * (frame % Turn) / Turn;
            var radius = MathF.Sqrt(3);
            var distance = radius / MathF.Sin(FieldOfView / 2);
            Eye = new Vector3(MathF.Cos(Elevation) * MathF.Cos(angle), MathF.Cos(Elevation) * MathF.Sin(angle), MathF.Sin(Elevation)) * distance;
            View = Matrix4x4.CreateLookAt(Eye, Vector3.Zero, Vector3.UnitZ);
            var projection = Matrix4x4.CreatePerspectiveFieldOfView(
                FieldOfView,
                Width / (float)Height,
                distance - radius - 0.1f,
                distance + radius + 0.1f
            );
            Transform = View * projection;
            Focal = Height / 2f / MathF.Tan(FieldOfView / 2);
        }

        public Vector3 Eye { get; }

        public Matrix4x4 View { get; }

        /// <summary>The box's coordinates to clip space, for row vectors.</summary>
        public Matrix4x4 Transform { get; }

        /// <summary>The focal length in pixels: a tangent of 1 off the view's axis is this many pixels from the center.</summary>
        public float Focal { get; }
    }

    /// <summary>A variant of the orbit through the octree.</summary>
    private readonly struct Variant
    {
        public long Budget { get; init; }

        /// <summary>The disc's diameter in pixels (A's DiscPS).</summary>
        public float Diameter { get; init; }

        /// <summary>Whether each node's points are in the order of their seeded priority; otherwise of their Morton keys.</summary>
        public bool Priority { get; init; }

        public string Name =>
            string.Create(CultureInfo.InvariantCulture, $"budget={Budget} diameter={Diameter} order={(Priority ? "priority" : "morton")}");
    }

    /// <summary>The options of the octree scenes, after the positional arguments.</summary>
    private sealed record OctreeOptions
    {
        public int GridBits { get; init; } = 7;

        public int Leaf { get; init; } = 10_000;

        public int Workers { get; init; } = 8;

        public int Seed { get; init; } = 1;

        public int Hits { get; init; } = 50;

        public int HitFrames { get; init; } = 20;

        public float HitRadius { get; init; } = 4;

        public long HitBudget { get; init; } = 10_000_000;

        public int HitCheck { get; init; } = 10;

        public bool Pin { get; init; }

        public bool Hash { get; init; }

        public int Frames { get; init; } = Turn;

        public int Warmup { get; init; } = 120;

        public bool BackToBack { get; init; }

        public bool Warp { get; init; }

        public string Variants { get; init; } = DefaultVariants;

        public bool Reverse { get; init; }

        public static OctreeOptions Parse(ReadOnlySpan<string> args)
        {
            var options = new OctreeOptions();
            for (var i = 0; i < args.Length; i++)
            {
                options = args[i] switch
                {
                    "--grid" => options with { GridBits = GridBitsOf(int.Parse(args[++i], CultureInfo.InvariantCulture)) },
                    "--leaf" => options with { Leaf = Math.Max(1, int.Parse(args[++i], CultureInfo.InvariantCulture)) },
                    "--workers" => options with { Workers = Math.Max(1, int.Parse(args[++i], CultureInfo.InvariantCulture)) },
                    "--seed" => options with { Seed = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--hits" => options with { Hits = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--hit-frames" => options with { HitFrames = Math.Max(1, int.Parse(args[++i], CultureInfo.InvariantCulture)) },
                    "--hit-radius" => options with { HitRadius = float.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--hit-budget" => options with { HitBudget = (long)double.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--hit-check" => options with { HitCheck = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--pin" => options with { Pin = true },
                    "--hash" => options with { Hash = true },
                    "--frames" => options with { Frames = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--warmup" => options with { Warmup = int.Parse(args[++i], CultureInfo.InvariantCulture) },
                    "--back-to-back" => options with { BackToBack = true },
                    "--warp" => options with { Warp = true },
                    "--variants" => options with { Variants = args[++i] },
                    "--reverse" => options with { Reverse = true },
                    _ => throw new ArgumentException($"Unknown option {args[i]}."),
                };
            }

            return options;
        }

        private static int GridBitsOf(int grid) =>
            BitOperations.IsPow2(grid) && grid >= 2 && grid <= (1 << Octree.Bits)
                ? BitOperations.Log2((uint)grid)
                : throw new ArgumentException("--grid takes a power of 2 from 2 to 2^21.");
    }
}
