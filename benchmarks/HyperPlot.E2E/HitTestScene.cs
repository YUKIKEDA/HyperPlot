using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using HyperPlot.DataPath;
using HyperPlot.Interaction;
using HyperPlot.Rendering.D3D11;

namespace HyperPlot.E2E;

/// <summary>
/// The scene of the hit test of scatter on the index path (design §9 "散布のヒットテストと選択（行 R3c）", requirement P-5): a
/// <see cref="PlotEngine"/> over one point layer of seeded <see cref="float"/> x and y columns mapped from files, at
/// 1,920 × 1,080 on the hardware. After the first display it times one <see cref="PlotEngine.HitTest"/> a place in
/// <see cref="HitMode.XY"/> and in <see cref="HitMode.X"/> with the hover's radius of 8 DIPs, at the points of 1,000 seeded
/// rows and, for the clustered distribution, at each cluster's center and at 1, 2, and 3 standard deviations from it in four
/// directions: in the whole view, and in views of 1 %, of 0.01 %, and (as Auto draws) of a millionth of the domain's side
/// centered on the place, each drawn before its hit tests. The layer is drawn with <see cref="PointDraw.Auto"/> (a density where the view holds more points
/// than the threshold) and, for the clustered distribution, with <see cref="PointDraw.Points"/> too, so that the search
/// meets the clusters as raw points; with markers of 2 DIPs and of 16 DIPs (a radius of 8 physical pixels, the largest a
/// layer sized by data draws by default). Where raw points answer, places are checked against the nearest point drawn
/// inside the panel among the first points of every leaf under the reach (Issue #471 Decision 21: within half a pixel of
/// the nearest distance; Issue #569: only a point drawn inside the panel answers). <see cref="RunLines"/> is the scene of
/// vertical lines of the same issue.
/// </summary>
internal static class HitTestScene
{
    private const int Places = 1000;

    // The seed of the scene of vertical lines (design §9 "要件 P-5 の `HitMode.X` の測り直し").
    private const ulong LineSeed = 1;

    // The seed of the places of row C3a's queries and of the spike of Issue #496 (SpatialScenes.PlaceSeed).
    private const ulong PlaceSeed = 0x43_33_61_50_6C_61_63_65UL;

    // The hover's default radius, in DIPs (HoverTool).
    private const float HoverRadius = 8;

    // The answer may be this far from the nearest distance, in physical pixels (Issue #471 Decision 21).
    private const double Tolerance = 0.5;

    // The check maps the values to pixels in doubles, and the answer is measured where the vertex shader draws the point,
    // on a unit of 2^-32 of its resident cell and in floats: in these views that is under 0.005 pixels.
    private const double Slack = 0.01;

    // A step coprime to every set's size, so that the places checked before the time runs out are spread over the set.
    private const int CheckStep = 7919;

    // A view of a millionth of the domain's side, measured where Auto draws: a search along x alone finds no point within
    // half a pixel in a leaf there and goes on down the column of leaves (.cursor/rules/bench.mdc: the hardest input).
    private const double DeepShare = 0.000001;

    // The reference machine's Core i5-13400F numbers the 12 logical processors of its 6 performance cores first, then its 4
    // efficiency cores: a call that ends on processor 12 or later ran on an efficiency core (.cursor/rules/bench.mdc).
    private const int FirstEfficiencyProcessor = 12;

    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(10);
    private static readonly double[] Shares = [0.01, 0.0001];
    private static readonly PanelId Panel = new(0, 0);

    /// <summary>Runs the scene and prints its figures; returns the exit code.</summary>
    /// <param name="args">The folder of the data files, the count, "uniform" or "cluster", the seconds of the reference a line and mode (8 by default), and the places of each set in the phase of 16-DIP markers drawn as raw points whatever the view holds (250 by default).</param>
    public static int Run(ReadOnlySpan<string> args)
    {
        const string Usage = "hittest <dir> <count> <uniform|cluster> [reference seconds a line] [places with 16-DIP markers forced to points]";
        if (args.Length < 3)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var directory = args[0];
        var count = (long)double.Parse(args[1], CultureInfo.InvariantCulture);
        string? distribution = args[2] switch
        {
            "uniform" => "uniform",
            "cluster" or "clustered" => "clustered",
            _ => null,
        };
        var referenceSeconds = args.Length > 3 ? double.Parse(args[3], CultureInfo.InvariantCulture) : 8;
        var forcedPlaces = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 250;
        if (distribution is null || count < 1 || forcedPlaces < 1)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        _ = Directory.CreateDirectory(directory);
        var made = Stopwatch.StartNew();
        var (xPath, yPath, written) = DensityScene.Ensure(directory, count, distribution);
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"files: {xPath} and {yPath}, {count * sizeof(float)} bytes each, seed {DensityScene.Seed}, {(written ? $"written now in {made.Elapsed.TotalSeconds:F1} s, unbuffered" : "kept from an earlier run")}"
            )
        );

        using var x = Column.MapFile<float>(xPath);
        using var y = Column.MapFile<float>(yPath);
        var table = DataTable.Create().Add("x", x).Add("y", y).Build();
        var clustered = distribution == "clustered";
        var (lo, hi) = clustered ? (-PointFiles.OutlierReach, PointFiles.OutlierReach) : (0.0, PointFiles.Side);

        PlotSpec Make(PointDraw draw, float size) =>
            Plot.Create(table)
                .Point(
                    table.Col<float>("x"),
                    table.Col<float>("y"),
                    p =>
                    {
                        _ = p.Draw(draw).Size(size);
                    }
                )
                .ScaleX(Scale.Linear().WithDomain(lo, hi))
                .ScaleY(Scale.Linear().WithDomain(lo, hi))
                .Build();

        // The phases: how the layer draws, the markers' diameter in DIPs, and the places of each set.
        (PointDraw Draw, float Size, int Cap)[] phases = clustered
            ? [(PointDraw.Auto, 2, Places), (PointDraw.Points, 2, Places), (PointDraw.Points, 16, forcedPlaces)]
            : [(PointDraw.Auto, 2, Places), (PointDraw.Auto, 16, Places)];

        using var backend = D3D11Backend.Create();
        _ = PointsScene.TimeBeginPeriod(1);
        try
        {
            _ = DensityScene.Warm(backend);
            using var engine = new PlotEngine(backend, 1920, 1080);
            var build = MachineLoad.Now();
            if (DensityScene.Show(engine, backend, Make(phases[0].Draw, phases[0].Size)) < 0)
            {
                return 1;
            }

            Cores("first display", build);
            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"hittest points={count} dist={distribution} viewport=1920x1080 radius={HoverRadius * engine.Scale} px reference_seconds={referenceSeconds} forced_places={forcedPlaces}"
                )
            );

            var index = Layer(engine).SpatialIndex!;
            var snapshot = index.Acquire();
            try
            {
                var sets = PlaceSets(x, y, clustered);
                var work = new Work(index.LeafCapacity);
                for (var p = 0; p < phases.Length; p++)
                {
                    var (draw, size, cap) = phases[p];
                    if (p > 0)
                    {
                        // A new node of the same columns: the plan takes its looks and keeps its index (design §4.8).
                        var taken = Stopwatch.StartNew();
                        engine.SetPlot(Make(draw, size));
                        engine.RenderFrame();
                        var kept = ReferenceEquals(Layer(engine).SpatialIndex, index);
                        while (Layer(engine).SpatialIndex is not { IsComplete: true })
                        {
                            if (taken.Elapsed > Deadline)
                            {
                                Console.Error.WriteLine("The index of the new node was not built before the deadline.");
                                return 1;
                            }

                            Thread.Sleep(5);
                            engine.RenderFrame();
                        }

                        if (!kept)
                        {
                            snapshot.Release();
                            index = Layer(engine).SpatialIndex!;
                            snapshot = index.Acquire();
                        }

                        Console.WriteLine(
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"phase draw={draw} size={size}: index kept={kept}, ready after {taken.Elapsed.TotalSeconds:F2} s"
                            )
                        );
                    }

                    var mark = MachineLoad.Now();
                    var marker = size * engine.Scale / 2;
                    foreach (var (name, all) in sets)
                    {
                        var places = Subset(all, cap);

                        // The whole view shows the layer as Auto draws it; the edges of the clusters are the centers' pixel there.
                        if (draw == PointDraw.Auto && name != "edges")
                        {
                            Line(engine, snapshot, work, draw, size, marker, name, "1", places, Centered(places, lo, hi, 1), referenceSeconds);
                        }

                        foreach (var share in Shares)
                        {
                            var label = share.ToString(CultureInfo.InvariantCulture);
                            Line(engine, snapshot, work, draw, size, marker, name, label, places, Centered(places, lo, hi, share), referenceSeconds);
                        }

                        if (draw == PointDraw.Auto)
                        {
                            var label = DeepShare.ToString(CultureInfo.InvariantCulture);
                            Line(
                                engine,
                                snapshot,
                                work,
                                draw,
                                size,
                                marker,
                                name,
                                label,
                                places,
                                Centered(places, lo, hi, DeepShare),
                                referenceSeconds
                            );
                        }
                    }

                    Cores(string.Create(CultureInfo.InvariantCulture, $"phase draw={draw} size={size}"), mark);
                }
            }
            finally
            {
                snapshot.Release();
            }

            using var process = Process.GetCurrentProcess();
            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"memory: working set {process.WorkingSet64 / 1048576.0:F0} MB, private {process.PrivateMemorySize64 / 1048576.0:F0} MB"
                )
            );
            return 0;
        }
        finally
        {
            _ = PointsScene.TimeEndPeriod(1);
        }
    }

    /// <summary>
    /// Runs the scene of vertical lines (design §9 "要件 P-5 の `HitMode.X` の測り直し", the limit of design §6.3 "散布の点が答える範囲",
    /// Issue #569) and prints its figures; returns the exit code. The data are points whose x is one of a number of whole
    /// values, as many points on each, and whose y is uniform over [0, 1), seed 1. After a first display drawn as Auto
    /// draws it, the layer draws <see cref="PointDraw.Points"/> in a view a half unit wide on x around the middle line, so
    /// that one line shows, over the whole y and over 1 % of it; the cursor lies on the line and 1, 4, and 8 pixels to its
    /// right, at 1,000 heights spread over the view.
    /// </summary>
    /// <param name="args">The folder of the data files, the count, the number of lines, and the seconds of the reference a line and mode (8 by default).</param>
    public static int RunLines(ReadOnlySpan<string> args)
    {
        const string Usage = "hittest-lines <dir> <count> <lines> [reference seconds a line]";
        if (args.Length < 3)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var directory = args[0];
        var count = (long)double.Parse(args[1], CultureInfo.InvariantCulture);
        var lines = (long)double.Parse(args[2], CultureInfo.InvariantCulture);
        var referenceSeconds = args.Length > 3 ? double.Parse(args[3], CultureInfo.InvariantCulture) : 8;
        if (count < 1 || lines < 1 || count % lines != 0)
        {
            Console.Error.WriteLine(Usage + ": the count is a whole number of lines.");
            return 2;
        }

        _ = Directory.CreateDirectory(directory);
        var made = Stopwatch.StartNew();
        var prefix = Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"lines-{lines}-{count}"));
        var (xPath, yPath) = PointFiles.Paths(prefix);
        var written = !(
            File.Exists(xPath)
            && File.Exists(yPath)
            && new FileInfo(xPath).Length == count * sizeof(float)
            && new FileInfo(yPath).Length == count * sizeof(float)
        );
        if (written)
        {
            PointFiles.Write(prefix, count, LineSeed, "lines", lines);
        }

        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"files: {xPath} and {yPath}, {count * sizeof(float)} bytes each, {lines} lines of {count / lines} points, seed {LineSeed}, {(written ? $"written now in {made.Elapsed.TotalSeconds:F1} s, unbuffered" : "kept from an earlier run")}"
            )
        );

        using var x = Column.MapFile<float>(xPath);
        using var y = Column.MapFile<float>(yPath);
        var table = DataTable.Create().Add("x", x).Add("y", y).Build();

        PlotSpec Make(PointDraw draw) =>
            Plot.Create(table)
                .Point(
                    table.Col<float>("x"),
                    table.Col<float>("y"),
                    p =>
                    {
                        _ = p.Draw(draw).Size(2);
                    }
                )
                .ScaleX(Scale.Linear().WithDomain(0, lines))
                .ScaleY(Scale.Linear().WithDomain(0, 1))
                .Build();

        using var backend = D3D11Backend.Create();
        _ = PointsScene.TimeBeginPeriod(1);
        try
        {
            _ = DensityScene.Warm(backend);
            using var engine = new PlotEngine(backend, 1920, 1080);
            var build = MachineLoad.Now();
            if (DensityScene.Show(engine, backend, Make(PointDraw.Auto)) < 0)
            {
                return 1;
            }

            Cores("first display", build);
            var radius = HoverRadius * engine.Scale;
            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"hittest-lines points={count} lines={lines} viewport=1920x1080 radius={radius} px reference_seconds={referenceSeconds}"
                )
            );

            // The middle line, in a view a half unit wide: the lines are a unit apart, so it is the only one that shows.
            var line = (double)(lines / 2);
            var views = new[] { new View(line - 0.25, 0, 0.5, 1), new View(line - 0.25, 0.495, 0.5, 0.01) };
            if (!Settle(engine, views[0], Make(PointDraw.Points)))
            {
                return 1;
            }

            var snapshot = Layer(engine).SpatialIndex!.Acquire();
            try
            {
                var work = new Work(Layer(engine).SpatialIndex!.LeafCapacity);
                var mark = MachineLoad.Now();
                foreach (var view in views)
                {
                    if (!Settle(engine, view, null))
                    {
                        return 1;
                    }

                    var origin = engine.ViewToPixel(Panel, ViewRange.FromValues(view.X0, view.X0), ViewRange.FromValues(view.Y0, view.Y0));
                    var far = engine.ViewToPixel(Panel, ViewRange.FromValues(view.X0 + view.SpanX, view.X0 + view.SpanX), ViewRange.FromValues(0, 0));
                    var perX = (far.X - (double)origin.X) / view.SpanX;
                    var label = view.SpanY.ToString(CultureInfo.InvariantCulture);
                    var same = new View[Places];
                    Array.Fill(same, view);
                    foreach (var pixels in (double[])[0, 1, 4, 8])
                    {
                        var places = new (double X, double Y)[Places];
                        for (var i = 0; i < Places; i++)
                        {
                            places[i] = (line + (pixels / perX), view.Y0 + (view.SpanY * (i + 0.5) / Places));
                        }

                        var name = string.Create(CultureInfo.InvariantCulture, $"line+{pixels}px");
                        Line(engine, snapshot, work, PointDraw.Points, 2, engine.Scale, name, label, places, same, referenceSeconds);
                    }
                }

                Cores("lines", mark);
            }
            finally
            {
                snapshot.Release();
            }

            return 0;
        }
        finally
        {
            _ = PointsScene.TimeEndPeriod(1);
        }

        // Sets the view (and a new node of the same columns, which keeps the index) and draws until the frame is final.
        static bool Settle(PlotEngine engine, View view, PlotSpec? plot)
        {
            engine.Views.SetView(
                Panel,
                new PanelView2D(ViewRange.FromValues(view.X0, view.X0 + view.SpanX), ViewRange.FromValues(view.Y0, view.Y0 + view.SpanY))
            );
            if (plot is not null)
            {
                engine.SetPlot(plot);
            }

            var taken = Stopwatch.StartNew();
            engine.RenderFrame();
            while (engine.IsProvisional || Layer(engine).SpatialIndex is not { IsComplete: true })
            {
                if (taken.Elapsed > Deadline)
                {
                    Console.Error.WriteLine("The view stayed provisional past the deadline.");
                    return false;
                }

                Thread.Sleep(5);
                engine.RenderFrame();
            }

            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"view x=[{view.X0}, {view.X0 + view.SpanX}] y=[{view.Y0}, {view.Y0 + view.SpanY}]: final after {taken.Elapsed.TotalSeconds:F2} s, density={Layer(engine).DrewDensity}"
                )
            );
            return !Layer(engine).DrewDensity;
        }
    }

    private static IndexedPointPlan Layer(PlotEngine engine) => (IndexedPointPlan)engine.Plan!.Layers[0];

    private static void Cores(string what, MachineLoad.Mark from)
    {
        var (others, own) = MachineLoad.CoresSince(from);
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"machine during {what}: other processes {others:F1} cores, this process {own:F1} cores, {Stopwatch.GetElapsedTime(from.Stamp).TotalSeconds:F1} s"
            )
        );
    }

    /// <summary>
    /// Returns the sets of places in data units: the points of seeded rows, and for the clustered distribution each
    /// cluster's center and the places 1, 2, and 3 standard deviations from it in four directions.
    /// </summary>
    private static List<(string Name, (double X, double Y)[] Places)> PlaceSets(Column<float> x, Column<float> y, bool clustered)
    {
        var sets = new List<(string, (double, double)[])>();
        var rows = new (double, double)[Places];
        var state = PlaceSeed;
        for (var i = 0; i < Places; i++)
        {
            state ^= state >> 12;
            state ^= state << 25;
            state ^= state >> 27;
            var row = (long)((state * 0x2545F4914F6CDD1DUL) % (ulong)x.Length);
            rows[i] = (x.GetValue(row), y.GetValue(row));
        }

        sets.Add(("rows", rows));
        if (clustered)
        {
            var clusters = PointFiles.ClusterShapes(DensityScene.Seed);
            var centers = new (double, double)[clusters.Length];
            var edges = new List<(double, double)>();
            for (var k = 0; k < clusters.Length; k++)
            {
                var (cx, cy, sigma) = clusters[k];
                centers[k] = (cx, cy);
                foreach (var reach in (double[])[1, 2, 3])
                {
                    foreach (var (ux, uy) in ((double, double)[])[(1, 0), (0, 1), (-1, 0), (0, -1)])
                    {
                        edges.Add((cx + (reach * sigma * ux), cy + (reach * sigma * uy)));
                    }
                }
            }

            sets.Add(("centers", centers));
            sets.Add(("edges", [.. edges]));
        }

        return sets;
    }

    /// <summary>Returns the places, or evenly spaced ones of them when there are more than the cap.</summary>
    private static (double X, double Y)[] Subset((double X, double Y)[] places, int cap)
    {
        if (places.Length <= cap)
        {
            return places;
        }

        var kept = new (double, double)[cap];
        for (var k = 0; k < cap; k++)
        {
            kept[k] = places[(int)((long)k * places.Length / cap)];
        }

        return kept;
    }

    /// <summary>Returns the views of a share of the domain's side: the whole view for a share of 1, else one centered on each place.</summary>
    private static View[] Centered((double X, double Y)[] places, double lo, double hi, double share)
    {
        var span = (hi - lo) * share;
        var views = new View[places.Length];
        for (var i = 0; i < places.Length; i++)
        {
            var (px, py) = places[i];
            views[i] = share == 1 ? new View(lo, lo, span, span) : new View(px - (span / 2), py - (span / 2), span, span);
        }

        return views;
    }

    /// <summary>
    /// Times the hit tests of one set of places in one set of views, and prints a line a mode. Each place's view is set and
    /// drawn before the place's hit tests when it differs from the view before: the whole view is drawn once, a view
    /// centered on each place once a place. Each place's first call of a mode is the sample; the modes take turns to go
    /// first, and a second call of each is timed apart. The allocations are counted over the four calls of every place
    /// after the first place's warm-up calls.
    /// </summary>
    private static void Line(
        PlotEngine engine,
        SpatialSnapshot snapshot,
        Work work,
        PointDraw draw,
        float size,
        double marker,
        string name,
        string view,
        (double X, double Y)[] places,
        View[] views,
        double referenceSeconds
    )
    {
        var n = places.Length;
        var samples = work.Samples(n);
        var radius = HoverRadius * engine.Scale;
        var (density, provisionalFrames) = (0, 0);
        long allocated = 0;
        var hits = work.Hits;
        var drawn = Stopwatch.StartNew();
        for (var i = 0; i < n; i++)
        {
            var (px, py) = places[i];
            var (x0, y0, spanX, spanY) = views[i];
            if (i == 0 || views[i] != views[i - 1])
            {
                engine.Views.SetView(Panel, new PanelView2D(ViewRange.FromValues(x0, x0 + spanX), ViewRange.FromValues(y0, y0 + spanY)));
                engine.RenderFrame();
            }

            // The panel's linear map of this view, from two of its corners.
            var origin = engine.ViewToPixel(Panel, ViewRange.FromValues(x0, x0), ViewRange.FromValues(y0, y0));
            var far = engine.ViewToPixel(Panel, ViewRange.FromValues(x0 + spanX, x0 + spanX), ViewRange.FromValues(y0 + spanY, y0 + spanY));
            var (perX, perY) = ((far.X - (double)origin.X) / spanX, (far.Y - (double)origin.Y) / spanY);
            var cursor = new Vector2((float)(origin.X + ((px - x0) * perX)), (float)(origin.Y + ((py - y0) * perY)));
            ref var s = ref samples[i];
            s = default;
            (s.OriginX, s.OriginY, s.PerX, s.PerY, s.X0, s.Y0) = (origin.X, origin.Y, perX, perY, x0, y0);
            s.Panel = new PixelBox(Math.Min(origin.X, far.X), Math.Max(origin.X, far.X), Math.Min(origin.Y, far.Y), Math.Max(origin.Y, far.Y));
            (s.Along, s.Across) = (cursor.X, cursor.Y);
            s.Density = Layer(engine).DrewDensity;
            density += s.Density ? 1 : 0;
            provisionalFrames += engine.IsProvisional ? 1 : 0;
            if (i == 0)
            {
                // The first raw-point search of a plot makes the search's buffers (design §6.3 "探し方").
                _ = engine.HitTest(cursor, radius, HitMode.XY, hits);
                _ = engine.HitTest(cursor, radius, HitMode.X, hits);
            }

            var (first, second) = i % 2 == 0 ? (HitMode.XY, HitMode.X) : (HitMode.X, HitMode.XY);
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            var t0 = Stopwatch.GetTimestamp();
            var found0 = engine.HitTest(cursor, radius, first, hits);
            var t1 = Stopwatch.GetTimestamp();
            var hit0 = hits[0];
            var t2 = Stopwatch.GetTimestamp();
            var found1 = engine.HitTest(cursor, radius, second, hits);
            var t3 = Stopwatch.GetTimestamp();
            var hit1 = hits[0];
            var t4 = Stopwatch.GetTimestamp();
            _ = engine.HitTest(cursor, radius, first, hits);
            var t5 = Stopwatch.GetTimestamp();
            _ = engine.HitTest(cursor, radius, second, hits);
            var t6 = Stopwatch.GetTimestamp();
            allocated += GC.GetAllocatedBytesForCurrentThread() - bytes;
            s.Processor = Thread.GetCurrentProcessorId();

            var xyFirst = first == HitMode.XY;
            s.Xy = new Answer(
                xyFirst ? found0 : found1,
                xyFirst ? hit0 : hit1,
                Micro(xyFirst ? (t0, t1) : (t2, t3)),
                Micro(xyFirst ? (t4, t5) : (t5, t6))
            );
            s.X = new Answer(
                xyFirst ? found1 : found0,
                xyFirst ? hit1 : hit0,
                Micro(xyFirst ? (t2, t3) : (t0, t1)),
                Micro(xyFirst ? (t5, t6) : (t4, t5))
            );
        }

        var seconds = drawn.Elapsed.TotalSeconds;
        foreach (var mode in (HitMode[])[HitMode.XY, HitMode.X])
        {
            Report(
                snapshot,
                work,
                draw,
                size,
                marker,
                name,
                view,
                mode,
                samples.AsSpan(0, n),
                radius,
                referenceSeconds,
                density,
                provisionalFrames,
                allocated,
                seconds
            );
        }

        static double Micro((long From, long To) stamps) => Stopwatch.GetElapsedTime(stamps.From, stamps.To).TotalMicroseconds;
    }

    /// <summary>
    /// Prints one line: the times of the mode's calls, what the answers were, what the same search reads when it is called on
    /// the snapshot with the panel's linear map (cells taken from the queue, points read, queues that overflowed, and the
    /// time of that call alone, which leaves out the engine and the placing of a point as the vertex shader does), and the
    /// check of the places whose layer drew raw points against the nearest point drawn inside the panel of every leaf under
    /// the reach (in X, the leaves of the column over the panel's height).
    /// </summary>
    private static void Report(
        SpatialSnapshot snapshot,
        Work work,
        PointDraw draw,
        float size,
        double marker,
        string name,
        string view,
        HitMode mode,
        Span<Sample> samples,
        float radius,
        double referenceSeconds,
        int density,
        int provisionalFrames,
        long allocated,
        double seconds
    )
    {
        var n = samples.Length;
        var (first, again, visited, read, alone) = (new double[n], new double[n], new List<double>(n), new List<double>(n), new List<double>(n));
        var (found, bins, provisional, overflowed) = (0, 0, 0, 0);
        var (onPerformance, onEfficiency) = (new List<double>(n), new List<double>(n));
        var xr = snapshot.XRange;
        var yr = snapshot.YRange;
        var reach = mode == HitMode.XY ? radius + marker : radius;
        for (var i = 0; i < n; i++)
        {
            ref var s = ref samples[i];
            var answer = mode == HitMode.XY ? s.Xy : s.X;
            (first[i], again[i]) = (answer.First, answer.Again);
            (s.Processor >= FirstEfficiencyProcessor ? onEfficiency : onPerformance).Add(answer.First);
            found += answer.Found > 0 ? 1 : 0;
            bins += answer.Found > 0 && answer.Hit.Row < 0 ? 1 : 0;
            provisional += answer.Found > 0 && answer.Hit.Provisional ? 1 : 0;
            if (!s.Density)
            {
                var screen = new LinearScreen(s.OriginX + ((xr.Origin - s.X0) * s.PerX), s.PerX, s.OriginY + ((yr.Origin - s.Y0) * s.PerY), s.PerY);
                var began = Stopwatch.GetTimestamp();
                var nearest = snapshot.Nearest(ref screen, s.Panel, s.Along, s.Across, radius, marker, mode, work.Scratch);
                alone.Add(Stopwatch.GetElapsedTime(began).TotalMicroseconds);
                visited.Add(nearest.CellsVisited);
                read.Add(nearest.PointsRead);
                overflowed += nearest.Overflowed ? 1 : 0;
            }
        }

        // The reference: the places in a spread order until the line's time for it is used up.
        var (checkedPlaces, mismatches, excess, mostLeaves) = (0, 0, 0.0, 0);
        long mostPoints = 0;
        var referenceTimes = new List<double>();
        var spent = Stopwatch.StartNew();
        for (var c = 0; c < n && spent.Elapsed.TotalSeconds < referenceSeconds; c++)
        {
            ref var s = ref samples[(int)(((long)c * CheckStep) % n)];
            if (s.Density)
            {
                continue;
            }

            var (alongOfZero, acrossOfZero) = (s.OriginX + ((xr.Origin - s.X0) * s.PerX), s.OriginY + ((yr.Origin - s.Y0) * s.PerY));
            var (cx, cy) = ((s.Along - alongOfZero) / s.PerX, (s.Across - acrossOfZero) / s.PerY);
            var (rx, ry) = ((reach + 1) / Math.Abs(s.PerX), (reach + 1) / Math.Abs(s.PerY));

            // X reads the column of the panel's height (Issue #569), a pixel wider: only a point drawn inside the panel answers.
            var (yLow, yHigh) = ((s.Panel.AcrossLow - acrossOfZero) / s.PerY, (s.Panel.AcrossHigh - acrossOfZero) / s.PerY);
            var pixelY = 1 / Math.Abs(s.PerY);
            var rect =
                mode == HitMode.XY
                    ? new SpatialRect(cx - rx, cx + rx, cy - ry, cy + ry)
                    : new SpatialRect(
                        cx - rx,
                        cx + rx,
                        Math.Max(yr.Lower, Math.Min(yLow, yHigh) - pixelY),
                        Math.Min(yr.Upper, Math.Max(yLow, yHigh) + pixelY)
                    );
            var start = Stopwatch.GetTimestamp();
            var cells = snapshot.GetCells(rect, 32, work.Cells);
            if (cells > work.Cells.Length)
            {
                work.Cells = new SpatialCell[cells];
                cells = snapshot.GetCells(rect, 32, work.Cells);
            }

            var least = double.PositiveInfinity;
            long points = 0;
            for (var k = 0; k < cells; k++)
            {
                var length = (int)Math.Min(work.Cells[k].Count, work.Xs.Length);
                snapshot.ReadPoints(work.Cells[k].First, work.Xs.AsSpan(0, length), work.Ys.AsSpan(0, length), work.Rows.AsSpan(0, length));
                points += length;
                for (var p = 0; p < length; p++)
                {
                    var (along, across) = (alongOfZero + (work.Xs[p] * s.PerX), acrossOfZero + (work.Ys[p] * s.PerY));
                    if (!s.Panel.Holds(along, across))
                    {
                        continue;
                    }

                    var dx = along - s.Along;
                    var dy = mode == HitMode.XY ? across - s.Across : 0;
                    least = Math.Min(least, Math.Sqrt((dx * dx) + (dy * dy)));
                }
            }

            referenceTimes.Add(Stopwatch.GetElapsedTime(start).TotalSeconds);
            (mostLeaves, mostPoints) = (Math.Max(mostLeaves, cells), Math.Max(mostPoints, points));
            checkedPlaces++;
            var answer = mode == HitMode.XY ? s.Xy : s.X;
            if (answer.Found > 0)
            {
                // The answer's distance from its values, by the same map as the reference's points.
                var dx = s.OriginX + ((answer.Hit.X - s.X0) * s.PerX) - s.Along;
                var dy = mode == HitMode.XY ? s.OriginY + ((answer.Hit.Y - s.Y0) * s.PerY) - s.Across : 0;
                var distance = Math.Sqrt((dx * dx) + (dy * dy));
                excess = Math.Max(excess, distance - least);
                mismatches += answer.Hit.Row >= 0 && distance <= least + Tolerance + Slack && distance < reach + Slack ? 0 : 1;
            }
            else
            {
                mismatches += least >= reach - Tolerance - Slack ? 0 : 1;
            }
        }

        var (visitedOf, readOf, aloneOf, referenceOf) = (visited.ToArray(), read.ToArray(), alone.ToArray(), referenceTimes.ToArray());
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"hit draw={draw} size={size} places={name} view_share={view} mode={mode} count={n} density_frames={density} provisional_frames={provisionalFrames} found={found} bins={bins} provisional={provisional} first_us p50={P(first, 0.5):F1} p99={P(first, 0.99):F1} max={first.Max():F1}; again_us p50={P(again, 0.5):F1} p99={P(again, 0.99):F1} max={again.Max():F1}; searched={visitedOf.Length} cells p50={P(visitedOf, 0.5):F0} p99={P(visitedOf, 0.99):F0} max={Most(visitedOf):F0} points p50={P(readOf, 0.5):F0} p99={P(readOf, 0.99):F0} max={Most(readOf):F0} overflowed={overflowed} search_alone_us p50={P(aloneOf, 0.5):F1} p99={P(aloneOf, 0.99):F1}; checked={checkedPlaces} mismatches={mismatches} max_excess_px={excess:F3} reference_s p50={P(referenceOf, 0.5):F4} max={Most(referenceOf):F4} leaves_max={mostLeaves} points_max={mostPoints}; allocated_both_modes={allocated} B; on_performance_cores={onPerformance.Count} p50={P([.. onPerformance], 0.5):F1} on_efficiency_cores={onEfficiency.Count} p50={P([.. onEfficiency], 0.5):F1}; frames_and_calls={seconds:F1} s"
            )
        );

        static double P(double[] values, double share) => PointsScene.Percentile(values, share);
        static double Most(double[] values) => values.Length == 0 ? double.NaN : values.Max();
    }

    /// <summary>A view of the panel: its lower corner and its spans, in data units.</summary>
    private readonly record struct View(double X0, double Y0, double SpanX, double SpanY);

    /// <summary>What one mode's calls at a place answered: the hits found, the first of them, and the microseconds of the first and of the second call.</summary>
    private readonly record struct Answer(int Found, SeriesHit Hit, double First, double Again);

    /// <summary>A place's cursor in physical pixels, its view's linear map (a corner's pixel, the pixels a data unit, and the corner's values), what the layer drew, the processor the thread was on after the calls, and the answers.</summary>
    private struct Sample
    {
        public double Along;
        public double Across;
        public double OriginX;
        public double OriginY;
        public double PerX;
        public double PerY;
        public double X0;
        public double Y0;
        public PixelBox Panel;
        public bool Density;
        public int Processor;
        public Answer Xy;
        public Answer X;
    }

    /// <summary>
    /// The panel's linear map as a screen of <see cref="SpatialSnapshot.Nearest{TScreen}"/>: a difference from the snapshot's
    /// origin to its pixel, in doubles. The plan's own screen places a point as the vertex shader does; this one serves to
    /// count what the search reads.
    /// </summary>
    private struct LinearScreen(double alongOfZero, double perX, double acrossOfZero, double perY) : ISpatialScreen
    {
        private ResidentCell _cell;

        public readonly double Along(double x) => alongOfZero + (x * perX);

        public readonly double Across(double y) => acrossOfZero + (y * perY);

        public void Enter(in ResidentCell cell) => _cell = cell;

        public readonly (double Along, double Across) Place(uint unitX, uint unitY) =>
            (Along(_cell.CornerX + ((unitX / 4294967296.0) * _cell.ExtentX)), Across(_cell.CornerY + ((unitY / 4294967296.0) * _cell.ExtentY)));
    }

    /// <summary>The buffers the lines share.</summary>
    private sealed class Work(int leafCapacity)
    {
        private Sample[] _samples = new Sample[Places];

        public SeriesHit[] Hits { get; } = new SeriesHit[4];

        public SpatialNearestScratch Scratch { get; } = new(leafCapacity);

        public SpatialCell[] Cells { get; set; } = new SpatialCell[1 << 16];

        public double[] Xs { get; } = new double[leafCapacity];

        public double[] Ys { get; } = new double[leafCapacity];

        public long[] Rows { get; } = new long[leafCapacity];

        public Sample[] Samples(int count) => _samples.Length >= count ? _samples : _samples = new Sample[count];
    }
}
