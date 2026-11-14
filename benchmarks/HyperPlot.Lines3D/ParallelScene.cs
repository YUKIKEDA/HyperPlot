using System.Diagnostics;
using System.Globalization;
using System.Numerics;

namespace HyperPlot.Lines3D;

/// <summary>
/// The scenes of Issue #488's decision 2 and 3 (SPIKE, row K0b): the packed layout against the split one, the extraction
/// split over threads, the level-order extraction stopped by time or by nodes visited, and the extraction outside the frame.
/// </summary>
internal static class ParallelScene
{
    /// <summary>
    /// layout: per frame, the 1-pixel extraction from the split arrays (10 B a vertex) and from the packed nodes (8 B), in
    /// alternating order, on one pinned thread; their outputs must be the same entries. Also the nodes visited per vertex
    /// and the time per node, which compare 1e6 with 1e8 vertices (the unexplained 4 times of the Result).
    /// </summary>
    public static int Layout(Options options)
    {
        var (data, index) = Scene.Prepare(options);
        Extractor[] extractors = [new Extractor(index, NodeLayout.Split), new Extractor(index, NodeLayout.Packed)];
        string[] names = ["split", "packed"];

        // Two buffers of --thread-cap entries, not --full-cap: the index holds both layouts already (3 GB at 1e8 vertices).
        var capacity = Math.Min(data.Count, options.ThreadCap);
        int[][] buffers = [new int[capacity + index.SegmentCount + 4], new int[capacity + index.SegmentCount + 4]];
        var results = new Extraction[2];
        var middle = data[data.Count / 2];
        Scene.Pin(options);
        for (var f = 0; f < options.Warmup; f++)
        {
            var view = Scene.SceneView("orbit", f, options.Frames, middle);
            foreach (var extractor in extractors)
            {
                _ = extractor.Extract(view, Scene.Tolerance, capacity, buffers[0]);
            }
        }

        GC.Collect();
        var mismatches = 0;
        var frames = options.Frames;
        foreach (var scene in options.Scenes)
        {
            var times = Scene.NewTable(2, frames);
            var vertices = Scene.NewTable(2, frames);
            var nodes = Scene.NewTable(2, frames);
            var perVertex = Scene.NewTable(2, frames);
            var perNode = Scene.NewTable(2, frames);
            var ratio = new double[frames];
            var capped = new int[2];
            long maxBytes = 0;
            var began = DateTime.Now;
            for (var f = 0; f < frames; f++)
            {
                var view = Scene.SceneView(scene, f, frames, middle);
                for (var k = 0; k < 2; k++)
                {
                    var which = (f + k) % 2;
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    var start = Stopwatch.GetTimestamp();
                    var result = extractors[which].Extract(view, Scene.Tolerance, capacity, buffers[which]);
                    var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    maxBytes = Math.Max(maxBytes, GC.GetAllocatedBytesForCurrentThread() - before);
                    results[which] = result;
                    times[which][f] = ms;
                    vertices[which][f] = result.Vertices;
                    nodes[which][f] = result.Nodes;
                    perVertex[which][f] = result.Vertices > 0 ? (double)result.Nodes / result.Vertices : 0;
                    perNode[which][f] = result.Nodes > 0 ? ms * 1e6 / result.Nodes : 0;
                    capped[which] += result.Aborted ? 1 : 0;
                }

                var length = results[0].Length;
                if (results[1].Length != length || !buffers[0].AsSpan(0, length).SequenceEqual(buffers[1].AsSpan(0, length)))
                {
                    mismatches++;
                }

                ratio[f] = times[1][f] / times[0][f];
            }

            var label = Scene.Label(options, data.Count, scene);
            Scene.Line($"{label} layout frames={frames} ran {began:HH:mm:ss} to {DateTime.Now:HH:mm:ss}");
            for (var which = 0; which < 2; which++)
            {
                var name = string.Create(CultureInfo.InvariantCulture, $"{label} layout={names[which]}");
                Scene.Print($"{name} extract_ms", times[which]);
                Scene.Print($"{name} vertices", vertices[which]);
                Scene.Print($"{name} nodes", nodes[which]);
                Scene.Print($"{name} nodes_per_vertex", perVertex[which]);
                Scene.Print($"{name} ns_per_node", perNode[which]);
                Scene.Line($"{name} capped={capped[which]}");
            }

            Scene.Print($"{label} layout ratio_packed_over_split", ratio);
            Scene.Line($"{label} layout alloc_max_bytes_per_call={maxBytes}");
        }

        Scene.Line($"layout result={(mismatches == 0 ? "ok" : "MISMATCH")} mismatched_frames={mismatches}");
        return mismatches == 0 ? 0 : 1;
    }

    /// <summary>
    /// par: per frame, each variant once in an order rotated by the frame: its wall time (the team's wake-up and wait
    /// included), the vertices, the nodes visited, and, for a stopped level-order run, the screen error it can have.
    /// </summary>
    public static int Par(Options options)
    {
        var (data, index) = Scene.Prepare(options);
        var variants = options.Variants;
        var size = variants.Max(v => v.Threads);
        using var crew = new Crew(index, options.Layout, size, options.ThreadCap, options.Block);
        var middle = data[data.Count / 2];
        Scene.Raise();
        for (var f = 0; f < options.Warmup; f++)
        {
            var view = Scene.SceneView("orbit", f, options.Frames, middle);
            foreach (var variant in variants)
            {
                _ = crew.Run(variant, view, Scene.Tolerance);
            }
        }

        GC.Collect();
        using var csv = options.Csv is { } path ? new StreamWriter(path) : null;
        csv?.WriteLine("scene,frame,variant,ms,vertices,nodes,stopped_members,capped_members,remaining,achieved_px");
        var frames = options.Frames;
        var count = variants.Length;
        foreach (var scene in options.Scenes)
        {
            var times = Scene.NewTable(count, frames);
            var vertices = Scene.NewTable(count, frames);
            var nodes = Scene.NewTable(count, frames);
            var perNode = Scene.NewTable(count, frames);
            var achieved = Scene.NewTable(count, frames);
            var stopped = new int[count];
            var capped = new int[count];
            var maxBytes = new long[count];
            var began = DateTime.Now;
            for (var f = 0; f < frames; f++)
            {
                var view = Scene.SceneView(scene, f, frames, middle);
                for (var k = 0; k < count; k++)
                {
                    var i = (f + k) % count;
                    var variant = variants[i];
                    var result = crew.Run(variant, view, Scene.Tolerance);
                    var px =
                        variant.Levels && result.Stopped + result.Capped > 0 ? crew.AchievedPx(variant.Threads) : Scene.Tolerance;
                    times[i][f] = result.Ms;
                    vertices[i][f] = result.Vertices;
                    nodes[i][f] = result.Nodes;
                    perNode[i][f] = result.Nodes > 0 ? result.Ms * 1e6 / result.Nodes : 0;
                    achieved[i][f] = px;
                    stopped[i] += result.Stopped > 0 ? 1 : 0;
                    capped[i] += result.Capped > 0 ? 1 : 0;
                    maxBytes[i] = Math.Max(maxBytes[i], result.Bytes);
                    csv?.WriteLine(
                        FormattableString.Invariant(
                            $"{scene},{f},{variant.Name},{result.Ms:F4},{result.Vertices},{result.Nodes},{result.Stopped},{result.Capped},{result.Remaining},{px:G5}"
                        )
                    );
                }
            }

            var label = Scene.Label(options, data.Count, scene);
            Scene.Line(
                $"{label} par layout={options.LayoutName} frames={frames} variants={count} ran {began:HH:mm:ss} to {DateTime.Now:HH:mm:ss}"
            );
            for (var i = 0; i < count; i++)
            {
                var name = string.Create(CultureInfo.InvariantCulture, $"{label} variant={variants[i].Name}");
                Scene.Print($"{name} extract_ms", times[i]);
                Scene.Print($"{name} vertices", vertices[i]);
                Scene.Print($"{name} nodes", nodes[i]);
                Scene.Print($"{name} ns_per_node", perNode[i]);
                Scene.Print($"{name} achieved_px", achieved[i]);
                Scene.Line($"{name} frames_stopped={stopped[i]} frames_capped={capped[i]} alloc_max_bytes_per_member={maxBytes[i]}");
            }
        }

        return 0;
    }

    /// <summary>
    /// async: the extraction outside the frame. A frame thread ticks at the period (timeBeginPeriod(1)), posts the frame's
    /// view, and takes the newest finished result; a producer thread extracts the newest posted view with the variant's
    /// team, skipping the views posted while it worked. Measures, per displayed frame, the age of the drawn line in frames
    /// (1 when the previous frame's view was done in time), and per result its extraction time and its latency from the
    /// post of its view.
    /// </summary>
    public static int Async(Options options)
    {
        var (data, index) = Scene.Prepare(options);
        var variants = options.Variants;
        var size = variants.Max(v => v.Threads);
        using var crew = new Crew(index, options.Layout, size, options.ThreadCap, options.Block);
        var middle = data[data.Count / 2];
        Scene.Raise();
        for (var f = 0; f < options.Warmup; f++)
        {
            var view = Scene.SceneView("orbit", f, options.Frames, middle);
            foreach (var variant in variants)
            {
                _ = crew.Run(variant, view, Scene.Tolerance);
            }
        }

        GC.Collect();

        // Windows wakes a sleeping thread on its timer tick, 15.6 ms by default; 1 ms keeps the frames 16.7 ms apart.
        _ = NativeMethods.TimeBeginPeriod(1);
        try
        {
            for (var round = 1; round <= options.Rounds; round++)
            {
                for (var k = 0; k < variants.Length; k++)
                {
                    var variant = variants[(round - 1 + k) % variants.Length];
                    foreach (var scene in options.Scenes)
                    {
                        Feed(options, data.Count, crew, variant, scene, middle, round);
                    }
                }
            }
        }
        finally
        {
            _ = NativeMethods.TimeEndPeriod(1);
        }

        return 0;
    }

    /// <summary>
    /// check: on 1e6 vertices, every --every-th frame of each scene, compares with the split in-order walk on one thread:
    /// the packed walk (the same entries), dfs/1, dfs/4, bfs/1, bfs/4 (the same chords), and the level-order walk stopped
    /// at a quarter of the nodes on 1 and 4 threads (vertices in the reference, in order; on 1 thread, no vertex past the
    /// achieved tolerance by brute force and none uncovered). Exits with 1 on any difference.
    /// </summary>
    public static int Check(Options options)
    {
        var (data, index) = Scene.Prepare(options);
        var n = data.Count;
        var reference = new Extractor(index, NodeLayout.Split);
        var packed = new Extractor(index, NodeLayout.Packed);
        var referenceBuffer = new int[n + index.SegmentCount + 4];
        var packedBuffer = new int[n + index.SegmentCount + 4];
        using var crew = new Crew(index, NodeLayout.Packed, 4, n + 16, options.Block);
        var camera = new Vector3[n];
        var inside = new bool[n];
        var marked = new bool[n];
        var middle = data[n / 2];
        Variant[] exact = [Variant.Parse("dfs/1"), Variant.Parse("dfs/4"), Variant.Parse("bfs/1"), Variant.Parse("bfs/4")];
        var failures = 0;
        var checks = 0;
        foreach (var scene in options.Scenes)
        {
            for (var f = 0; f < options.Frames; f += options.Every)
            {
                var view = Scene.SceneView(scene, f, options.Frames, middle);
                for (var i = 0; i < n; i++)
                {
                    var box = view.ToBox(data[i]);
                    inside[i] = View.Inside(box);
                    camera[i] = view.ToCamera(box);
                }

                var r = reference.Extract(view, Scene.Tolerance, n + 1, referenceBuffer);
                var referenceEntries = referenceBuffer.AsSpan(0, r.Length);
                var referenceChords = Sorted(referenceEntries);
                var p = packed.Extract(view, Scene.Tolerance, n + 1, packedBuffer);
                Expect(
                    !r.Aborted && p.Length == r.Length && packedBuffer.AsSpan(0, p.Length).SequenceEqual(referenceEntries),
                    "packed dfs entries",
                    scene,
                    f
                );

                foreach (var variant in exact)
                {
                    var result = crew.Run(variant, view, Scene.Tolerance);
                    var chords = Chords(crew, variant);
                    Expect(result.Capped == 0 && chords.AsSpan().SequenceEqual(referenceChords), variant.Name, scene, f);
                }

                Array.Clear(marked);
                foreach (var v in referenceEntries)
                {
                    if (v >= 0)
                    {
                        marked[v] = true;
                    }
                }

                foreach (var threads in (ReadOnlySpan<int>)[1, 4])
                {
                    var limit = Math.Max(4, r.Nodes / 4);
                    var variant = Variant.Parse(string.Create(CultureInfo.InvariantCulture, $"nodes/{threads}/{limit}"));
                    var result = crew.Run(variant, view, Scene.Tolerance);
                    var ordered = true;
                    for (var m = 0; m < threads; m++)
                    {
                        var previous = -1;
                        foreach (var v in crew.Output(m, levels: true))
                        {
                            if (v < 0)
                            {
                                continue;
                            }

                            ordered &= v > previous && marked[v];
                            previous = v;
                        }
                    }

                    Expect(ordered && result.Capped == 0, variant.Name + " order and subset", scene, f);
                    if (threads == 1)
                    {
                        var px = crew.AchievedPx(1);
                        var check = Scene.Check(view, crew.Output(0, levels: true), camera, inside, px);
                        Expect(check.Violations == 0 && check.Uncovered == 0, variant.Name + " bound", scene, f);
                        Scene.Line(
                            $"check scene={scene} frame={f} reference_vertices={r.Vertices} reference_nodes={r.Nodes} stopped_vertices={result.Vertices} stopped_nodes={result.Nodes} achieved_px={px:F4} max_error_px={check.MaxError:F4}"
                        );
                    }
                }

                checks++;
            }
        }

        Scene.Line($"check frames={checks} failures={failures} result={(failures == 0 ? "ok" : "FAILED")}");
        return failures == 0 ? 0 : 1;

        void Expect(bool ok, string what, string scene, int frame)
        {
            if (!ok)
            {
                failures++;
                Scene.Line($"check FAILED scene={scene} frame={frame} {what}");
            }
        }
    }

    private static void Feed(Options options, int count, Crew crew, Variant variant, string scene, Vector3 middle, int round)
    {
        var frames = options.Frames;
        var warmup = options.Warmup;
        var total = warmup + frames;
        using var feed = new FeedState(total);
        var producer = new Thread(() => Produce(feed, crew, variant, scene, frames, middle))
        {
            IsBackground = true,
            Name = "Lines3D producer",
        };
        var ages = new double[frames];
        var late = new double[frames];
        var work = new double[frames];
        var none = 0;
        var frequency = (double)Stopwatch.Frequency;
        var period = options.PeriodMs * frequency / 1000;
        var began = DateTime.Now;
        producer.Start();
        var origin = Stopwatch.GetTimestamp() + (long)period;
        try
        {
            for (var f = 0; f < total; f++)
            {
                var due = origin + (long)(f * period);
                WaitUntil(due, frequency);
                var now = Stopwatch.GetTimestamp();
                feed.PostedAt[f] = now;
                Volatile.Write(ref feed.Posted, f);
                feed.Wake.Release();
                var shown = Volatile.Read(ref feed.Shown);
                var after = Stopwatch.GetTimestamp();
                if (f < warmup)
                {
                    continue;
                }

                var at = f - warmup;
                late[at] = (now - due) * 1000 / frequency;
                work[at] = (after - now) * 1e6 / frequency;
                if (shown < 0)
                {
                    ages[at] = double.NaN;
                    none++;
                }
                else
                {
                    ages[at] = f - shown;
                }
            }
        }
        finally
        {
            Volatile.Write(ref feed.Closing, true);
            feed.Wake.Release();
            producer.Join();
        }

        if (feed.Failure is { } failure)
        {
            throw new InvalidOperationException("The producer failed.", failure);
        }

        // The results of views posted after the warm-up.
        var measured = new List<int>();
        for (var k = 0; k < feed.Results; k++)
        {
            if (feed.ResultFrame[k] >= warmup)
            {
                measured.Add(k);
            }
        }

        var label = string.Create(
            CultureInfo.InvariantCulture,
            $"{Scene.Label(options, count, scene)} async variant={variant.Name} round={round}"
        );
        Scene.Line(
            $"{label} frames={frames} period_ms={options.PeriodMs:F3} ran {began:HH:mm:ss} to {DateTime.Now:HH:mm:ss}"
        );
        Scene.Print($"{label} age_frames", [.. ages.Where(a => !double.IsNaN(a))]);
        Scene.Print($"{label} frame_late_ms", late);
        Scene.Print($"{label} frame_thread_us", work);
        Scene.Print($"{label} result extract_ms", [.. measured.Select(k => feed.ResultMs[k])]);
        Scene.Print($"{label} result latency_ms", [.. measured.Select(k => feed.ResultLatencyMs[k])]);
        Scene.Print($"{label} result vertices", [.. measured.Select(k => feed.ResultVertices[k])]);
        var stopped = measured.Count(k => feed.ResultStopped[k]);
        var seconds = frames * options.PeriodMs / 1000;
        Scene.Line(
            $"{label} results={measured.Count} results_per_s={measured.Count / seconds:F1} stopped_results={stopped} frames_without_result={none}"
        );
    }

    private static void Produce(FeedState feed, Crew crew, Variant variant, string scene, int frames, Vector3 middle)
    {
        try
        {
            var last = -1;
            while (true)
            {
                feed.Wake.Wait();
                if (Volatile.Read(ref feed.Closing))
                {
                    return;
                }

                var f = Volatile.Read(ref feed.Posted);
                if (f == last || f < 0)
                {
                    continue;
                }

                var view = Scene.SceneView(scene, f % frames, frames, middle);
                var result = crew.Run(variant, view, Scene.Tolerance);
                var end = Stopwatch.GetTimestamp();
                Volatile.Write(ref feed.Shown, f);

                // Only the cadence is recorded here. The achieved tolerance (Crew.AchievedPx) walks the frontier left and
                // delayed the next extraction in the run of 2026-11-14; par measures it, outside its timing.
                var stopped = result.Stopped + result.Capped > 0;
                var k = feed.Results++;
                feed.ResultFrame[k] = f;
                feed.ResultMs[k] = result.Ms;
                feed.ResultLatencyMs[k] = (end - feed.PostedAt[f]) * 1000.0 / Stopwatch.Frequency;
                feed.ResultVertices[k] = result.Vertices;
                feed.ResultStopped[k] = stopped;
                last = f;
            }
        }
        catch (Exception exception)
        {
            feed.Failure = exception;
        }
    }

    /// <summary>Sleeps in 1 ms steps while more than 2 ms remain, then yields until the time.</summary>
    private static void WaitUntil(long due, double frequency)
    {
        while (true)
        {
            var left = (due - Stopwatch.GetTimestamp()) * 1000 / frequency;
            if (left <= 0)
            {
                return;
            }

            if (left > 2)
            {
                Thread.Sleep(1);
            }
            else
            {
                _ = Thread.Yield();
            }
        }
    }

    private static long[] Sorted(ReadOnlySpan<int> output)
    {
        var list = new List<long>();
        AddChords(list, output);
        var chords = list.ToArray();
        Array.Sort(chords);
        return chords;
    }

    private static long[] Chords(Crew crew, Variant variant)
    {
        var list = new List<long>();
        for (var m = 0; m < variant.Threads; m++)
        {
            AddChords(list, crew.Output(m, variant.Levels));
        }

        var chords = list.ToArray();
        Array.Sort(chords);
        return chords;
    }

    /// <summary>Adds each drawn chord (two consecutive vertices with no break between) as first × 2^32 + second.</summary>
    private static void AddChords(List<long> list, ReadOnlySpan<int> output)
    {
        var previous = -1;
        foreach (var v in output)
        {
            if (v >= 0 && previous >= 0)
            {
                list.Add(((long)previous << 32) | (uint)v);
            }

            previous = v;
        }
    }

    /// <summary>What the frame thread and the producer of the async scene share.</summary>
    private sealed class FeedState : IDisposable
    {
        public readonly SemaphoreSlim Wake = new(0);
        public readonly long[] PostedAt;
        public readonly int[] ResultFrame;
        public readonly double[] ResultMs;
        public readonly double[] ResultLatencyMs;
        public readonly double[] ResultVertices;
        public readonly bool[] ResultStopped;
        public int Posted = -1;
        public int Shown = -1;
        public int Results;
        public bool Closing;
        public Exception? Failure;

        public FeedState(int total)
        {
            PostedAt = new long[total];
            ResultFrame = new int[total];
            ResultMs = new double[total];
            ResultLatencyMs = new double[total];
            ResultVertices = new double[total];
            ResultStopped = new bool[total];
        }

        public void Dispose() => Wake.Dispose();
    }
}
