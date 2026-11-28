using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using HyperPlot.Interaction;
using HyperPlot.Rendering;
using HyperPlot.Rendering.D3D11;

namespace HyperPlot.E2E;

/// <summary>
/// The scene of the raw points (design §9 "散布の生の点（行 R3a）", requirement P-4): a <see cref="PlotEngine"/> over one
/// point layer at 1,920 × 1,080 on the hardware, zoomed and panned by a script, a frame every 16.7 ms or more. Each frame's
/// CPU time (the view's change and <see cref="PlotEngine.RenderFrame"/>), its GPU time, and its allocations; then the time
/// of a hit test at seeded pixels in both modes. A layer of at most 131,072 rows takes the CPU path, and a larger one the index path.
/// Row R3b2 (design §9 "重なりと予算"): the runs and the points of the index path a frame, the blocks in view against whole
/// pages, the frames without their drawing, and the cores the other processes used. Row R3c2 (design §6.4 "強調のない run の
/// 描き方"): the same frames under a rectangle or a lasso selected before them.
/// </summary>
internal static partial class PointsScene
{
    // Two seconds of frames before the measured ones: a GPU scene is drawn that long before it is timed (.cursor/rules/bench.mdc).
    private const int Warmup = 120;

    /// <summary>Runs the scene and prints its figures; returns the exit code.</summary>
    /// <param name="args">The count, the diameter in DIPs, the opacity, "uniform" or "cluster", the frames measured (600 by default), the categories (0 by default), "whole" or "zoom", what the categories set ("color", "shape", "both"; "size" and "shapesize" size the points by a number too, row R3f), the layer's shape, "blocks" (the blocks in view, the product's form) or "pages" (every block of each page drawn, row R3b2), and "draw" or "record" (the measured frames record and do not draw, so that their CPU time is the recording's), a file to write one row a frame to (its times and allocations), the selection the frames are drawn under: "none" (by default), "rectangle" ([25, 75]²), or "lasso" (a circle of radius 30 around (50, 50)); then (Issue #624) the clump's σ (10 by default), the panels of a facet that wraps the rows (0 by default: no facet), the layer's drawing ("auto" by default, or "points"), and how the rows fall into the categories ("even" by default, or "uneven": nine rows in ten in the first); then (Issue #563) a second layer of the same count over the first in the same panel: "none" (by default), "uniform", or "cluster", with the first's diameter, opacity, and drawing; then (Issue #650) the layers' DensityThreshold (10,000,000 by default) and, with a second layer, the number of layers (2 by default), the two layers' columns in turn.</param>
    public static int Run(ReadOnlySpan<string> args)
    {
        // Row R3b2: a file of one row a frame, when named after the other arguments.
        var csv = args.Length > 11 ? args[11] : null;
        if (args.Length < 4)
        {
            Console.Error.WriteLine(
                "points <count> <diameter> <opacity> <uniform|cluster> [frames] [categories] [whole|zoom] [color|shape|both|size|shapesize] [shape] [blocks|pages] [draw|record] [file of one row a frame] [none|rectangle|lasso] [sigma] [panels] [auto|points] [even|uneven] [none|uniform|cluster] [density threshold] [layers]"
            );
            return 2;
        }

        var count = int.Parse(args[0], CultureInfo.InvariantCulture);
        var diameter = float.Parse(args[1], CultureInfo.InvariantCulture);
        var opacity = double.Parse(args[2], CultureInfo.InvariantCulture);
        var cluster = args[3] == "cluster";
        var frames = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 600;
        var categories = args.Length > 5 ? int.Parse(args[5], CultureInfo.InvariantCulture) : 0;
        var whole = args.Length > 6 && args[6] == "whole";

        // Row R3f: what the categories set (the color, the shape, or both), whether a number sizes the points, and the shape.
        var channel = args.Length > 7 ? args[7] : "color";
        var shape = args.Length > 8 ? Enum.Parse<MarkerShape>(args[8]) : MarkerShape.Circle;

        // Row R3b2: whole pages instead of the blocks in view, and frames that record without drawing.
        var pages = args.Length > 9 && args[9] == "pages";
        var recordOnly = args.Length > 10 && args[10] == "record";

        // Row R3c2: a selection that the measured frames draw the highlight of.
        var selected = args.Length > 12 ? args[12] : "none";
        if (selected is not ("none" or "rectangle" or "lasso"))
        {
            Console.Error.WriteLine($"unknown selection {selected}: none, rectangle, or lasso");
            return 2;
        }

        // Issue #624: the scenes of the prototype's switch formula (Issue #552's sweep 8) and the ones it did not measure.
        var sigma = args.Length > 13 ? double.Parse(args[13], CultureInfo.InvariantCulture) : 10;
        var panels = args.Length > 14 ? int.Parse(args[14], CultureInfo.InvariantCulture) : 0;
        var draw = args.Length > 15 && args[15] == "points" ? PointDraw.Points : PointDraw.Auto;
        var uneven = args.Length > 16 && args[16] == "uneven";

        // Issue #563: a second layer over the first in the same panel, of its own seeded points.
        var second = args.Length > 17 ? args[17] : "none";
        if (second is not ("none" or "uniform" or "cluster"))
        {
            Console.Error.WriteLine($"unknown second layer {second}: none, uniform, or cluster");
            return 2;
        }

        // Issue #650: the layers' DensityThreshold, which a pair of 8e6 points passes summed at the default.
        var threshold = args.Length > 18 ? long.Parse(args[18], CultureInfo.InvariantCulture) : 10_000_000;

        // Issue #650: the layers drawn over one another with a second layer, its columns and the first's in turn (2 by default).
        var layerCount = args.Length > 19 ? int.Parse(args[19], CultureInfo.InvariantCulture) : 2;

        // Seeded: uniform over [0, 100]², or a normal blob of σ (a tenth of the side by default) around the middle.
        var random = new Random(1);
        (double[] Xs, double[] Ys) Points(Random seeded, bool clumped)
        {
            var (xs, ys) = (new double[count], new double[count]);
            for (var i = 0; i < count; i++)
            {
                if (clumped)
                {
                    var r = sigma * Math.Sqrt(-2 * Math.Log(1 - seeded.NextDouble()));
                    var a = 2 * Math.PI * seeded.NextDouble();
                    (xs[i], ys[i]) = (50 + (r * Math.Cos(a)), 50 + (r * Math.Sin(a)));
                }
                else
                {
                    (xs[i], ys[i]) = (seeded.NextDouble() * 100, seeded.NextDouble() * 100);
                }
            }

            return (xs, ys);
        }

        var (firstXs, firstYs) = Points(random, cluster);
        using var x = Column.Wrap(firstXs);
        using var y = Column.Wrap(firstYs);
        using var size = Column.Wrap(Enumerable.Range(0, count).Select(i => (double)(i % 101)).ToArray());
        var builder = DataTable.Create().Add("x", x).Add("y", y).Add("s", size);

        // The second layer's points come from another seed, so that two uniform layers do not draw the same points.
        var (secondXs, secondYs) = second == "none" ? (Array.Empty<double>(), Array.Empty<double>()) : Points(new Random(2), second == "cluster");
        using var x2 = Column.Wrap(secondXs);
        using var y2 = Column.Wrap(secondYs);
        if (second != "none")
        {
            builder = builder.Add("x2", x2).Add("y2", y2);
        }

        if (categories > 0)
        {
            var names = Enumerable.Range(0, categories).Select(i => $"k{i:D3}").ToArray();

            // Row i takes category i mod n; "uneven" puts rows 0 to 44 of every 50 in the first category and spreads the
            // other 5 over the rest (Issue #624: 90 % of the points in one of 6 parts, 2 % in each other).
            builder = builder.Add(
                "c",
                Column.Categorical(
                    Enumerable
                        .Range(0, count)
                        .Select(i => (string?)names[uneven ? ((i % 50) < 45 ? 0 : 1 + ((i % 50) % (categories - 1))) : i % categories])
                        .ToArray()
                )
            );
        }

        // Issue #624: a facet wraps row i into panel i mod n; the panels share their scales.
        if (panels > 0)
        {
            var panelNames = Enumerable.Range(0, panels).Select(i => $"f{i:D2}").ToArray();
            builder = builder.Add("f", Column.Categorical(Enumerable.Range(0, count).Select(i => (string?)panelNames[i % panels]).ToArray()));
        }

        var table = builder.Build();
        PlotSpec Make(PointDraw drawing)
        {
            void Style(PointBuilder p)
            {
                _ = p.Size(diameter).Opacity(opacity).Shape(shape).Draw(drawing);
                _ = threshold != 10_000_000 ? p.Density(threshold: threshold) : p;
                _ = categories > 0 && channel is "color" or "both" ? p.ColorBy(table.Cat("c")) : p;
                _ = categories > 0 && channel is "shape" or "both" or "shapesize" ? p.ShapeBy(table.Cat("c")) : p;
                _ = channel is "size" or "shapesize" ? p.SizeBy(table.Col<double>("s")) : p;
            }

            var made = Plot.Create(table).Point(table.Col<double>("x"), table.Col<double>("y"), Style);
            made = second == "none" ? made : made.Point(table.Col<double>("x2"), table.Col<double>("y2"), Style);
            for (var extra = 2; second != "none" && extra < layerCount; extra++)
            {
                var suffix = extra % 2 == 0 ? string.Empty : "2";
                made = made.Point(table.Col<double>("x" + suffix), table.Col<double>("y" + suffix), Style);
            }

            made = made.ScaleX(Scale.Linear().WithDomain(0, 100)).ScaleY(Scale.Linear().WithDomain(0, 100));
            return (panels > 0 ? made.FacetWrap(table.Cat("f")) : made).Build();
        }

        var plot = Make(draw);
        using var device = D3D11Backend.Create();
        var backend = new CountingBackend(device);
        backend.Images.Points.DrawsWholePages = pages;
        using var engine = new PlotEngine(backend, 1920, 1080);

        // The first display: frames until the index is built and every page the whole view draws is on the GPU.
        var shown = Stopwatch.StartNew();
        engine.SetPlot(plot);
        engine.RenderFrame();
        var (waited, firstRuns, pageFrames) = (0, -1.0, 0);
        while (engine.IsProvisional)
        {
            Thread.Sleep(1);
            engine.RenderFrame();
            waited++;
            if (backend.Images.Points.PagesInUse > 0)
            {
                firstRuns = firstRuns < 0 ? shown.Elapsed.TotalSeconds : firstRuns;
                pageFrames++;
            }
        }

        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"first display: {shown.Elapsed.TotalSeconds:F3} s over {waited} frames; first pages at {firstRuns:F3} s; frames that sent pages: {pageFrames}; pages on the GPU: {backend.Images.Points.PagesInUse} ({backend.Images.Points.BytesInUse} bytes)"
            )
        );
        for (var layer = 0; layer < engine.Plan!.Layers.Count; layer++)
        {
            PrintCells(engine.Plan!, layer);
        }

        // The script: zoom in to a tenth of the side around a point that drifts, and out again, 300 frames a round. The
        // views are made before the frames: a view is an object of the scene's own.
        var panel = new PanelId(0, 0);
        if (selected == "rectangle")
        {
            engine.Select(Selection2D.Rectangle(panel, ViewRange.FromValues(25.0, 75.0), ViewRange.FromValues(25.0, 75.0)));
        }
        else if (selected == "lasso")
        {
            engine.Select(SelectionScene.Lasso(30));
        }

        var total = frames + Warmup;
        var views = new PanelView2D[total];
        for (var f = 0; f < total; f++)
        {
            var t = (f % 300) / 300.0;

            // "whole": the whole data stays in view, moved by a hundredth of the side, so that every frame draws every point.
            var span = whole ? 102 : 100 * Math.Pow(10, -Math.Sin(t * Math.PI));
            var (cx, cy) = whole
                ? (50 + Math.Sin(t * 2 * Math.PI), 50 + Math.Cos(t * 2 * Math.PI))
                : (50 + (20 * Math.Sin(t * 2 * Math.PI)), 50 + (10 * Math.Cos(t * 2 * Math.PI)));
            views[f] = new PanelView2D(
                ViewRange.FromValues(cx - (span / 2), cx + (span / 2)),
                ViewRange.FromValues(cy - (span / 2), cy + (span / 2))
            );
        }

        var cpu = new double[total];
        var allocated = new long[total];
        var gpu = new List<double>(frames);
        long lastTimed = -1;
        var period = TimeSpan.FromMilliseconds(1000.0 / 60);

        // A wait of a few milliseconds needs the timer's finer step: by default a sleep ends on a 15.6 ms tick (.cursor/rules/bench.mdc).
        _ = TimeBeginPeriod(1);
        var (runs, drawn, highlighted) = (new int[total], new long[total], new int[total]);

        // Each frame's allocations by stage (the view's change, the drawing, the rest of the frame), its GPU time put on the
        // frame it timed, and when it began, for a file of one row a frame (.cursor/rules/bench.mdc).
        var (inView, inDraw) = (new long[total], new long[total]);
        var (gpuOf, beganAt) = (new double[total], new double[total]);

        // Row R3b2: the time of the layers' scan before the recording, the stage that chooses the drawing (design §5.5.2).
        var (scan, dense, first) = (new double[total], new bool[total], FirstPart(engine.Plan!));

        // Issue #650: the time of the choice over the layers (the panels' sums, the budget, the way back), each frame.
        var decide = new double[total];
        Array.Fill(gpuOf, double.NaN);
        backend.Skips = recordOnly;
        var (busyFrom, ownFrom, clockFrom) = (MachineBusy(), Environment.CpuUsage.TotalTime, DateTime.Now);
        var began = Stopwatch.GetTimestamp();
        var firstFrame = backend.Frames;
        for (var f = 0; f < total; f++)
        {
            beganAt[f] = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            engine.Views.SetView(panel, views[f]);
            inView[f] = GC.GetAllocatedBytesForCurrentThread() - bytes;
            engine.RenderFrame();
            cpu[f] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            allocated[f] = GC.GetAllocatedBytesForCurrentThread() - bytes;
            (runs[f], drawn[f], highlighted[f], inDraw[f]) = (backend.Runs, backend.Points, backend.Highlighted, backend.DrawBytes);
            (scan[f], dense[f]) = (engine.Plan!.ScanTicks * 1000.0 / Stopwatch.Frequency, first is { DrewDensity: true });
            decide[f] = engine.Plan!.Budget.DecideTicks * 1000.0 / Stopwatch.Frequency;
            if (backend.LastFrameTime is { } time && time.Frame > lastTimed)
            {
                lastTimed = time.Frame;
                var timed = time.Frame - firstFrame;
                if (timed >= 0 && timed < total)
                {
                    gpuOf[timed] = time.Duration.TotalMilliseconds;
                }

                if (f >= Warmup)
                {
                    gpu.Add(time.Duration.TotalMilliseconds);
                }
            }

            // A frame a period at the fastest: back to back, the GPU's clock rises and its time reads low (.cursor/rules/bench.mdc).
            var left = period - Stopwatch.GetElapsedTime(start);
            if (left > TimeSpan.Zero)
            {
                Thread.Sleep(left);
            }
        }

        var seconds = Stopwatch.GetElapsedTime(began).TotalSeconds;
        _ = TimeEndPeriod(1);

        // The cores the other processes used over the frames: the machine's busy time less this process's (.cursor/rules/bench.mdc).
        var others = ((MachineBusy() - busyFrom) - (Environment.CpuUsage.TotalTime - ownFrom)).TotalSeconds / seconds;
        var clockTo = DateTime.Now;
        backend.Skips = false;

        // Hit tests at seeded pixels of the whole view, each timed alone.
        engine.Views.SetView(panel, new PanelView2D(ViewRange.FromValues(0, 100), ViewRange.FromValues(0, 100)));
        engine.RenderFrame();
        var hits = new SeriesHit[4];
        var cursors = new Vector2[1000];
        for (var i = 0; i < cursors.Length; i++)
        {
            var (px, py) = (5 + (random.NextDouble() * 90), 5 + (random.NextDouble() * 90));
            cursors[i] = engine.ViewToPixel(panel, ViewRange.FromValues(px, px), ViewRange.FromValues(py, py));
        }

        var radius = 8 * engine.Scale;
        var (inX, inXY) = (new double[cursors.Length], new double[cursors.Length]);
        var found = 0;
        for (var round = 0; round < 2; round++)
        {
            for (var i = 0; i < cursors.Length; i++)
            {
                var start = Stopwatch.GetTimestamp();
                found += engine.HitTest(cursors[i], radius, HitMode.X, hits);
                var middle = Stopwatch.GetTimestamp();
                found += engine.HitTest(cursors[i], radius, HitMode.XY, hits);
                inX[i] = Stopwatch.GetElapsedTime(start, middle).TotalMilliseconds;
                inXY[i] = Stopwatch.GetElapsedTime(middle).TotalMilliseconds;
            }
        }

        var kept = cpu.Skip(Warmup).ToArray();
        var gpuKept = gpu.Count > 0 ? gpu.ToArray() : [double.NaN];
        var (keptRuns, keptDrawn) = (runs.Skip(Warmup).ToArray(), drawn.Skip(Warmup).ToArray());
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"form={(pages ? "pages" : "blocks")} frames={(recordOnly ? "record" : "draw")} selection={selected} ran {Clock(clockFrom)} to {Clock(clockTo)} other_cores={others:F2}"
            )
        );
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"resident_runs: mean={keptRuns.Average():F1} max={keptRuns.Max()} a frame, highlighted mean={highlighted.Skip(Warmup).Average():F1}; resident_points: mean={keptDrawn.Average():F0} max={keptDrawn.Max()} a frame"
            )
        );
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"points={count} diameter={diameter} opacity={opacity} dist={args[3]} second={second} threshold={threshold} layers={(second == "none" ? 1 : layerCount)} categories={categories} channel={channel} shape={shape} script={(whole ? "whole" : "zoom")} frames={frames} (after {Warmup}) frame_period_ms={seconds * 1000 / total:F2}"
            )
        );
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"cpu_frame: p50={Percentile(kept, 0.5):F3} p95={Percentile(kept, 0.95):F3} p99={Percentile(kept, 0.99):F3} max={kept.Max():F3} ms"
            )
        );
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"gpu_frame: frames={gpu.Count} p50={Percentile(gpuKept, 0.5):F3} p95={Percentile(gpuKept, 0.95):F3} p99={Percentile(gpuKept, 0.99):F3} max={gpuKept.Max():F3} ms"
            )
        );
        var scanKept = scan.Skip(Warmup).ToArray();
        var decideKept = decide.Skip(Warmup).ToArray();
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"decide: p50={Percentile(decideKept, 0.5):F4} p95={Percentile(decideKept, 0.95):F4} max={decideKept.Max():F4} ms over {engine.Plan!.Budget.LastPanels} panels"
            )
        );
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"scan: p50={Percentile(scanKept, 0.5):F4} p95={Percentile(scanKept, 0.95):F4} max={scanKept.Max():F4} ms; density frames {dense.Skip(Warmup).Count(d => d)} of {frames}"
            )
        );
        Console.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"allocated after warm-up: {allocated.Skip(Warmup).Sum()} bytes over {frames} frames")
        );

        // The script repeats every 300 frames: the last 300 show views that were drawn before, with no new tick label.
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"allocated in the last 300 frames: {allocated.TakeLast(300).Sum()} bytes; frames that allocated: {allocated.Skip(Warmup).Count(b => b > 0)}, the last at frame {Array.FindLastIndex(allocated, b => b > 0)}"
            )
        );
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"hit_x: p50={Percentile(inX, 0.5):F4} p99={Percentile(inX, 0.99):F4} max={inX.Max():F4} ms; hit_xy: p50={Percentile(inXY, 0.5):F4} p99={Percentile(inXY, 0.99):F4} max={inXY.Max():F4} ms; found={found}"
            )
        );

        // The frames after the warm-up that allocated, by stage: what the view's change, the drawing, and the rest of the
        // frame (the plan's layout, its guides, and the layers' recording) allocated.
        for (var f = Warmup; f < total; f++)
        {
            if (allocated[f] != 0)
            {
                Console.WriteLine(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"  allocated at frame {f}: {allocated[f]} B = view {inView[f]} + draw {inDraw[f]} + rest {allocated[f] - inView[f] - inDraw[f]}; runs {runs[f]}, the most before {(f == 0 ? 0 : runs[..f].Max())}"
                    )
                );
            }
        }

        if (csv is not null)
        {
            using var writer = new StreamWriter(csv);
            writer.WriteLine("frame,began_ms,cpu_ms,gpu_ms,runs,points,allocated,view_bytes,draw_bytes,scan_ms");
            for (var f = 0; f < total; f++)
            {
                writer.WriteLine(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{f},{beganAt[f]:F3},{cpu[f]:F4},{gpuOf[f]:F4},{runs[f]},{drawn[f]},{allocated[f]},{inView[f]},{inDraw[f]},{scan[f]:F4}"
                    )
                );
            }
        }

        if (draw == PointDraw.Points)
        {
            PrintLargestBin(engine, Make(PointDraw.Auto), panel, views.AsSpan(Warmup), diameter);
        }

        return 0;
    }

    /// <summary>
    /// Issue #624: what <see cref="PointDraw.Auto"/> reads over the measured views of a layer drawn as raw points (design
    /// §5.5.2 "予測時間"), at every 30th view of one round of the script: the upper count summed over the parts, the largest
    /// bin from the density tiles, the estimate from the resident cells before them, and the predicted times of both. The
    /// same layer is set again as <see cref="PointDraw.Auto"/> and drawn until every part's tiles have come (60 s at most).
    /// </summary>
    private static void PrintLargestBin(PlotEngine engine, PlotSpec auto, PanelId panel, ReadOnlySpan<PanelView2D> views, float diameter)
    {
        var waited = Stopwatch.StartNew();
        engine.SetPlot(auto);
        engine.RenderFrame();
        while (!TilesCame(engine.Plan!) && waited.Elapsed < TimeSpan.FromSeconds(60))
        {
            Thread.Sleep(10);
            engine.RenderFrame();
        }

        var came = TilesCame(engine.Plan!);
        var layers = engine.Plan!.Layers.Count;
        var (count, bin, estimate, time, timeOfEstimate, timeAtFour, densities) = (
            new MinMax[layers],
            new MinMax[layers],
            new MinMax[layers],
            new MinMax[layers],
            new MinMax[layers],
            new MinMax[layers],
            new int[layers]
        );
        for (var layer = 0; layer < layers; layer++)
        {
            (count[layer], bin[layer], estimate[layer], time[layer], timeOfEstimate[layer], timeAtFour[layer]) = (
                new MinMax(),
                new MinMax(),
                new MinMax(),
                new MinMax(),
                new MinMax(),
                new MinMax()
            );
        }

        // Issue #563: the panel's sum of the layers' predicted times, the fixed term counted once.
        var (sum, sampled) = (new MinMax(), 0);
        var physical = diameter * engine.Scale;
        for (var f = 0; f < Math.Min(views.Length, 300); f += 30)
        {
            engine.Views.SetView(panel, views[f]);
            engine.RenderFrame();
            var summed = PredictedTime.Fixed;
            for (var layer = 0; layer < layers; layer++)
            {
                var parts = Parts(engine.Plan!, layer).ToArray();
                var choice = parts[0].Switch;
                var cells = parts.Max(p => p.Estimate);
                count[layer].Add(choice.LastCount);
                bin[layer].Add(choice.LastLargestBin);
                estimate[layer].Add(cells);
                time[layer].Add(choice.LastTime);
                timeOfEstimate[layer].Add(PredictedTime.Of(choice.LastCount, physical, cells));
                timeAtFour[layer].Add(choice.LastTimeAtFourBins);
                densities[layer] += choice.Density ? 1 : 0;
                summed += choice.LastTime - PredictedTime.Fixed;
            }

            sum.Add(summed);
            sampled++;
        }

        for (var layer = 0; layer < layers; layer++)
        {
            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"largest_bin{(layers > 1 ? $" layer {layer}" : string.Empty)}: tiles_came={came} after {waited.Elapsed.TotalSeconds:F2} s; views={sampled} diameter_px={physical:F2}; count={count[layer]}; tiles={bin[layer]}; estimate={estimate[layer]}; time_tiles={time[layer]}; time_estimate={timeOfEstimate[layer]}; time_four_bins={timeAtFour[layer]}; density_views={densities[layer]}"
                )
            );
        }

        if (layers > 1)
        {
            Console.WriteLine(
                string.Create(CultureInfo.InvariantCulture, $"panel_time_tiles (the layers' sum, the fixed term once): {sum} ms over {sampled} views")
            );
        }
    }

    // Whether every part of the plot's layers on the index path, in every panel, has a full snapshot with its density tiles.
    private static bool TilesCame(PlotPlan plan)
    {
        foreach (var part in Enumerable.Range(0, plan.Layers.Count).SelectMany(layer => Parts(plan, layer)))
        {
            if (part.SpatialIndex is not { } index)
            {
                return false;
            }

            var snapshot = index.Acquire();
            var full = snapshot.Stage == SpatialStage.Full && snapshot.Tiles is not null;
            snapshot.Release();
            if (!full)
            {
                return false;
            }
        }

        return true;
    }

    // The parts of one of the plot's layers on the index path, in every panel.
    private static IEnumerable<IndexedPointPlan> Parts(PlotPlan plan, int layer) =>
        plan.Panels.SelectMany(p => p.Layers[layer] is SplitPlan split ? split.Parts : [p.Layers[layer]]).OfType<IndexedPointPlan>();

    // The least and the most of a value over the views, written "min..max" to four places.
    private sealed class MinMax
    {
        private double _min = double.PositiveInfinity;
        private double _max = double.NegativeInfinity;

        public void Add(double value) => (_min, _max) = (Math.Min(_min, value), Math.Max(_max, value));

        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{_min:F4}..{_max:F4}");
    }

    /// <summary>
    /// Prints the resident cells of one of the plot's layers (the first by default): their number over its parts on the
    /// index path, and the fewest and the most of a part, with the mean points of a cell (design §5.5.2 "セルの表").
    /// </summary>
    internal static void PrintCells(PlotPlan plan, int which = 0)
    {
        var layer = plan.Layers[which];
        IEnumerable<LayerPlan> parts = layer is SplitPlan split ? split.Parts : [layer];
        var (cells, least, most, points, indexed) = (0L, int.MaxValue, 0, 0L, 0);
        foreach (var part in parts)
        {
            if (part is not IndexedPointPlan { SpatialIndex: { } index })
            {
                continue;
            }

            var snapshot = index.Acquire();
            var count = snapshot.ResidentCells.Length;
            (cells, least, most, points, indexed) = (
                cells + count,
                Math.Min(least, count),
                Math.Max(most, count),
                points + snapshot.PointCount,
                indexed + 1
            );
            snapshot.Release();
        }

        Console.WriteLine(
            indexed == 0
                ? "resident_cells: none (the CPU path)"
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"resident_cells{(which > 0 ? $" layer {which}" : string.Empty)}: {cells} over {indexed} parts, {least} to {most} a part, {(double)points / cells:F0} points a cell"
                )
        );
    }

    /// <summary>Returns the first part of the plot's first layer on the index path, whose drawing is the layer's (row R3b2); null on the CPU path.</summary>
    private static IndexedPointPlan? FirstPart(PlotPlan plan)
    {
        var layer = plan.Layers[0];
        return (layer is SplitPlan split ? split.Parts.FirstOrDefault() : layer) as IndexedPointPlan;
    }

    [System.Runtime.InteropServices.LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    internal static partial uint TimeBeginPeriod(uint period);

    [System.Runtime.InteropServices.LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    internal static partial uint TimeEndPeriod(uint period);

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", EntryPoint = "GetSystemTimes")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out long idle, out long kernel, out long user);

    // The time every processor of the machine was busy since it started: the kernel time holds the idle time.
    private static TimeSpan MachineBusy() =>
        GetSystemTimes(out var idle, out var kernel, out var user) ? new TimeSpan(kernel + user - idle) : default;

    // A wall-clock time as nvidia-smi writes its samples' times of day, without a date format (.cursor/rules/aot.mdc).
    private static string Clock(DateTime time) =>
        string.Create(CultureInfo.InvariantCulture, $"{time.Hour:D2}:{time.Minute:D2}:{time.Second:D2}.{time.Millisecond:D3}");

    /// <summary>
    /// The backend of the scene: the hardware's, with the resident runs and their points of the lists a frame draws counted
    /// (the retained data layer's and the frame's own under a selection, row R3c2), and the drawing left out when asked, so
    /// that a frame's CPU time is what the engine does before it draws.
    /// </summary>
    internal sealed class CountingBackend(D3D11Backend inner) : IGpuBackend
    {
        public bool Skips { get; set; }

        public int Runs { get; private set; }

        public long Points { get; private set; }

        // The runs with a selection's highlight (row R3c2).
        public int Highlighted { get; private set; }

        public bool IsWarp => inner.IsWarp;

        public long DeviceGeneration => inner.DeviceGeneration;

        public GpuFrameTime? LastFrameTime => inner.LastFrameTime;

        public TextEngine Text => inner.Text;

        public ResidencyCache Images => inner.Images;

        public IRenderTarget CreateRenderTarget(in RenderTargetDescription description) => inner.CreateRenderTarget(description);

        // The hardware's scopes of the open frames, innermost last: a frame opened inside another is not numbered.
        private readonly Stack<FrameScope> _open = new();

        // The outermost frames begun: the number the hardware's timer gives the next one, as it numbers only those.
        public long Frames { get; private set; }

        // What the last drawing allocated on this thread, the counting included.
        public long DrawBytes { get; private set; }

        public FrameScope BeginFrame()
        {
            if (_open.Count == 0)
            {
                (Frames, Runs, Points, Highlighted) = (Frames + 1, 0, 0, 0);
            }

            _open.Push(inner.BeginFrame());
            return new FrameScope(this, _open.Count);
        }

        public void Draw(IRenderTarget target, RenderList list)
        {
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            Count(list);
            if (!Skips)
            {
                inner.Draw(target, list);
            }

            DrawBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        }

        private void Count(RenderList list)
        {
            var runs = list.ResidentRuns;
            long points = 0;
            var highlighted = 0;
            foreach (ref readonly var run in runs)
            {
                points += run.Count;
                highlighted += run.Highlight != 0 ? 1 : 0;
            }

            (Runs, Points, Highlighted) = (Runs + runs.Length, Points + points, Highlighted + highlighted);
        }

        public void Copy(IRenderTarget source, IRenderTarget destination) => inner.Copy(source, destination);

        // Frames close innermost first: the token is the depth the frame was opened at.
        public void EndFrame(long token)
        {
            if (token > 0 && token == _open.Count)
            {
                _open.Pop().Dispose();
            }
        }

        public void Dispose() => inner.Dispose();
    }

    internal static double Percentile(double[] values, double share)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? double.NaN : sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * share))];
    }
}
