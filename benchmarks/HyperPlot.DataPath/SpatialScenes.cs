using System.Diagnostics;
using System.Globalization;

namespace HyperPlot.DataPath;

/// <summary>
/// The spatial tile index scene of design §9 (row C3a): the build of the index over the x and y files of
/// <see cref="PointFiles"/>, timed as a whole and by stage and job, the size of the copy and of the table, the queries at
/// seeded places, the queries during the build, and the wait of <see cref="SpatialIndex.Dispose"/>. Each figure is one line
/// of <c>key=value</c> pairs for <c>docs/benchmarks/c3a-spatial-index.md</c>.
/// </summary>
internal static class SpatialScenes
{
    private const int Width = 1920;
    private const int Height = 1080;
    private const int Places = 1_000;
    private const double ZoomShare = 0.01;
    private const double DeepShare = 0.0001;
    private const double HoverRadius = 8;

    // The largest marker radius of a layer sized by data with the default range of SizeScaleSpec (16 DIPs across).
    private const double LargestMarkerRadius = 8;
    private const int WritesBefore = 200;
    private const int TouchThreads = 8;
    private const int TouchPagesPerThread = 12_288;
    private const ulong PlaceSeed = 0x43_33_61_50_6C_61_63_65UL;
    private static readonly TimeSpan FramePeriod = TimeSpan.FromSeconds(1.0 / 60);

    // The hit tests of the nearest scene: XY without a marker, XY with markers sized by data, and X.
    private static readonly (SpatialHitMode Mode, double Marker)[] NearestHits =
    [
        (SpatialHitMode.XY, 0),
        (SpatialHitMode.XY, LargestMarkerRadius),
        (SpatialHitMode.X, 0),
    ];

    // The empty snapshot has no range yet, so a frame during the build asks over the whole plane.
    private static readonly SpatialRect Everywhere = new(
        double.NegativeInfinity,
        double.PositiveInfinity,
        double.NegativeInfinity,
        double.PositiveInfinity
    );

    /// <summary>Builds the index twice in a row (the first over cold files with <see cref="Options.Cold"/>) and reports each.</summary>
    internal static void Build(Options options)
    {
        using var ownPool = options.Workers > 0 ? new PlotWorkerPool(options.Workers) : null;
        var pool = ownPool ?? PlotWorkerPool.Shared;
        var (xPath, yPath) = PointFiles.Paths(options.File);
        if (options.Cold)
        {
            PointFiles.Write(options.File, options.Points, options.Seed, options.Distribution);
        }

        // Windows wakes a sleeping thread on its timer tick, 15.6 ms by default; 1 ms keeps the frames 16.7 ms apart.
        _ = NativeMethods.TimeBeginPeriod(1);
        try
        {
            for (var run = 1; run <= 2; run++)
            {
                using var x = Column.MapFile<float>(xPath);
                using var y = Column.MapFile<float>(yPath);
                var recorder = new JobRecorder();
                var tuning = Tuning(options) with { JobEnded = recorder.Add };
                var frames = new FrameRecorder();
                recorder.Start = Stopwatch.GetTimestamp();
                var copies = new Copies();
                var index = pool.BuildSpatialIndex(x, y, Input(options), tuning, copies);
                try
                {
                    QueryEachFrame(index, frames);
                    index.Completion.Wait();
                    var seconds = Stopwatch.GetElapsedTime(recorder.Start).TotalSeconds;
                    var common = Common(options, pool, tuning);
                    Report(options, run, "build", [.. common, ("ended_utc", EndedUtc()), ("seconds", seconds), .. recorder.Stages()]);
                    foreach (var line in recorder.Jobs())
                    {
                        Report(options, run, "jobs", [.. common, .. line]);
                    }

                    foreach (var line in frames.Lines())
                    {
                        Report(options, run, "frames", [.. common, .. line]);
                    }

                    var snapshot = index.Acquire();
                    try
                    {
                        Report(options, run, "memory", [.. common, .. Memory(snapshot, x.Length, tuning)]);
                        foreach (var line in Density(snapshot, x.Length, tuning))
                        {
                            Report(options, run, "density", [.. common, .. line]);
                        }

                        foreach (var line in Queries(snapshot, x, y, tuning))
                        {
                            Report(options, run, "queries", [.. common, .. line]);
                        }
                    }
                    finally
                    {
                        snapshot.Release();
                    }
                }
                finally
                {
                    index.Dispose();

                    // The next build starts with the file of this one closed and gone.
                    copies.WaitClosed();
                }

                foreach (var moment in (string[])["started", "counting", "writing"])
                {
                    Report(options, run, "dispose", [.. Common(options, pool, tuning), .. DisposeAt(pool, x, y, options, tuning, moment)]);
                }
            }
        }
        finally
        {
            _ = NativeMethods.TimeEndPeriod(1);
        }
    }

    /// <summary>
    /// The allocation job alone: makes the copy of <see cref="Options.Points"/> float points in a temporary file of
    /// <see cref="Options.Temp"/> as the index does, and disposes of it, three times. On Linux, a file system without
    /// <c>fallocate</c> makes glibc reserve the blocks another way (design §3.4 "一時ファイル"); the record names the file
    /// system.
    /// </summary>
    internal static void Allocate(Options options)
    {
        for (var run = 1; run <= 3; run++)
        {
            var began = Stopwatch.GetTimestamp();
            var copy = SpatialCopyAllocator.Default.Allocate(options.Points, [sizeof(float), sizeof(float)], mapped: true, options.Temp);
            var allocated = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            began = Stopwatch.GetTimestamp();
            copy.Dispose();
            var disposed = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            copy.Closed.Wait();
            var closed = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            Scenes.Report(
                options,
                run,
                ("os", Environment.OSVersion.Platform),
                ("temp", options.Temp ?? Path.GetTempPath()),
                ("bytes", copy.ByteCount),
                ("allocate_ms", allocated),
                ("dispose_ms", disposed),
                ("closed_ms", closed)
            );
        }
    }

    /// <summary>
    /// Times the first writes to the pages of a mapped copy, which the first units of the writing pay for: one write,
    /// then batches in which <see cref="TouchThreads"/> threads each write one element to
    /// <see cref="TouchPagesPerThread"/> pages that nothing wrote before, then the pages of the first batch again.
    /// </summary>
    internal static void Touch(Options options)
    {
        const int ElementsPerPage = 4096 / sizeof(float);
        const int Batches = 8;
        var pages = new int[options.Points / ElementsPerPage];
        if (pages.Length < Batches * TouchThreads * TouchPagesPerThread)
        {
            throw new ArgumentException(
                $"The touch scene needs {(long)Batches * TouchThreads * TouchPagesPerThread * ElementsPerPage} points or more."
            );
        }

        for (var page = 0; page < pages.Length; page++)
        {
            pages[page] = page;
        }

        new Random(1).Shuffle(pages);
        var copy = SpatialCopyAllocator.Default.Allocate(options.Points, [sizeof(float), sizeof(float)], mapped: true, options.Temp);
        try
        {
            var began = Stopwatch.GetTimestamp();
            copy.GetRegion<float>(0).TryWrite((ulong)pages[^1] * ElementsPerPage, 1f);
            Scenes.Report(options, 1, ("figure", "first_write"), ("ms", Stopwatch.GetElapsedTime(began).TotalMilliseconds));

            // The batch after the last writes the pages of the first batch again: the same loop without a first write.
            for (var batch = 0; batch <= Batches; batch++)
            {
                var source = batch % Batches;
                began = Stopwatch.GetTimestamp();
                Parallel.For(
                    0,
                    TouchThreads,
                    new ParallelOptions { MaxDegreeOfParallelism = TouchThreads },
                    thread =>
                    {
                        var region = copy.GetRegion<float>(0);
                        var first = ((source * TouchThreads) + thread) * TouchPagesPerThread;
                        for (var i = 0; i < TouchPagesPerThread; i++)
                        {
                            region.TryWrite((ulong)pages[first + i] * ElementsPerPage, 1f);
                        }
                    }
                );
                var milliseconds = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
                Scenes.Report(
                    options,
                    1,
                    ("figure", "touch"),
                    ("batch", batch),
                    ("pages", batch < Batches ? "new" : "written"),
                    ("threads", TouchThreads),
                    ("pages_per_thread", TouchPagesPerThread),
                    ("ms", milliseconds),
                    ("us_per_page", milliseconds * 1000 / TouchPagesPerThread)
                );
            }
        }
        finally
        {
            copy.Dispose();
            copy.Closed.Wait();
        }
    }

    /// <summary>
    /// Disposes of the index as soon as the writing began and once <see cref="WritesBefore"/> units of it have ended, eight
    /// times each, over the files as they are (design §9: the wait of <see cref="SpatialIndex.Dispose"/>).
    /// </summary>
    internal static void DisposeWhileWriting(Options options)
    {
        using var ownPool = options.Workers > 0 ? new PlotWorkerPool(options.Workers) : null;
        var pool = ownPool ?? PlotWorkerPool.Shared;
        var (xPath, yPath) = PointFiles.Paths(options.File);
        using var x = Column.MapFile<float>(xPath);
        using var y = Column.MapFile<float>(yPath);
        var tuning = Tuning(options);
        for (var run = 1; run <= 8; run++)
        {
            foreach (var moment in (string[])["writing", "written"])
            {
                Report(options, run, "dispose", [.. Common(options, pool, tuning), .. DisposeAt(pool, x, y, options, tuning, moment)]);
            }
        }
    }

    /// <summary>
    /// The nearest-point query of the spike of row R3c: builds the index once (over the files as they are, written first
    /// when missing or with <see cref="Options.Cold"/>), then times <see cref="SpatialSnapshot.Nearest"/> at the seeded
    /// places of the C3a queries, and for the clustered distribution at each cluster's center and at 1, 2, and 3
    /// standard deviations from it in four directions, in the views of 1 % and 0.01 % of the C3a hover with a radius of
    /// <see cref="HoverRadius"/> pixels: in XY without a marker and with markers of <see cref="LargestMarkerRadius"/>
    /// pixels at most (a layer sized by data), and in X. Then checks up to <see cref="Options.CheckPlaces"/> evenly spaced
    /// places of each line (all of them by default) against the exhaustive answer of the C3a hover, GetCells at level 32
    /// over every leaf under the reach (not the first 1,024 of Issue #473) and the first LeafCapacity points of each leaf,
    /// and reports the time of that answer as a line of its own.
    /// </summary>
    internal static void Nearest(Options options)
    {
        using var ownPool = options.Workers > 0 ? new PlotWorkerPool(options.Workers) : null;
        var pool = ownPool ?? PlotWorkerPool.Shared;
        var (xPath, yPath) = PointFiles.Paths(options.File);
        if (options.Cold || !File.Exists(xPath) || !File.Exists(yPath))
        {
            var written = Stopwatch.GetTimestamp();
            PointFiles.Write(options.File, options.Points, options.Seed, options.Distribution);
            Report(options, 1, "generate", [("distribution", options.Distribution), ("seconds", Stopwatch.GetElapsedTime(written).TotalSeconds)]);
        }

        using var x = Column.MapFile<float>(xPath);
        using var y = Column.MapFile<float>(yPath);
        var tuning = Tuning(options);
        var copies = new Copies();
        var began = Stopwatch.GetTimestamp();
        var index = pool.BuildSpatialIndex(x, y, Input(options), tuning, copies);
        try
        {
            index.Completion.Wait();
            var common = Common(options, pool, tuning);
            Report(options, 1, "nearest_build", [.. common, ("points", x.Length), ("seconds", Stopwatch.GetElapsedTime(began).TotalSeconds)]);
            var snapshot = index.Acquire();
            try
            {
                foreach (var line in NearestQueries(snapshot, x, y, tuning, options))
                {
                    Report(options, 1, "nearest", [.. common, .. line]);
                }
            }
            finally
            {
                snapshot.Release();
            }
        }
        finally
        {
            index.Dispose();
            copies.WaitClosed();
        }
    }

    private static IEnumerable<(string Key, object Value)[]> NearestQueries(
        SpatialSnapshot snapshot,
        Column<float> x,
        Column<float> y,
        SpatialIndexTuning tuning,
        Options options
    )
    {
        var xr = snapshot.XRange;
        var yr = snapshot.YRange;
        var (width, height) = (xr.Upper - xr.Lower, yr.Upper - yr.Lower);

        // The places of the C3a queries: the points of seeded rows.
        var sets = new List<(string Name, (double X, double Y)[] Places)>();
        var rowsPlaces = new (double X, double Y)[Places];
        var state = PlaceSeed;
        for (var i = 0; i < Places; i++)
        {
            state ^= state >> 12;
            state ^= state << 25;
            state ^= state >> 27;
            var row = (long)((state * 0x2545F4914F6CDD1DUL) % (ulong)x.Length);
            rowsPlaces[i] = (x.GetValue(row) - (double)xr.Origin, y.GetValue(row) - (double)yr.Origin);
        }

        sets.Add(("rows", rowsPlaces));
        if (options.Distribution == "clustered")
        {
            var clusters = PointFiles.ClusterShapes(options.Seed);
            var centers = new (double X, double Y)[clusters.Length];
            var edges = new List<(double X, double Y)>();
            for (var k = 0; k < clusters.Length; k++)
            {
                var (cx, cy, sigma) = clusters[k];
                centers[k] = (cx - xr.Origin, cy - yr.Origin);
                foreach (var reach in (double[])[1, 2, 3])
                {
                    foreach (var (ux, uy) in ((double, double)[])[(1, 0), (0, 1), (-1, 0), (0, -1)])
                    {
                        edges.Add((cx + (reach * sigma * ux) - xr.Origin, cy + (reach * sigma * uy) - yr.Origin));
                    }
                }
            }

            sets.Add(("centers", centers));
            sets.Add(("edges", [.. edges]));
        }

        var scratch = new SpatialNearestScratch(tuning.LeafCapacity);
        var xs = new double[tuning.LeafCapacity];
        var ys = new double[tuning.LeafCapacity];
        var rows = new long[tuning.LeafCapacity];
        var cells = new SpatialCell[1 << 16];
        foreach (var (name, places) in sets)
        {
            foreach (var share in (double[])[ZoomShare, DeepShare])
            {
                var (spanX, spanY) = (width * share, height * share);
                var (ppx, ppy) = (Width / spanX, Height / spanY);
                foreach (var (mode, marker) in NearestHits)
                {
                    var reach = HoverRadius + marker;
                    var n = places.Length;
                    var times = new double[n];
                    var visited = new double[n];
                    var read = new double[n];
                    var answers = new SpatialNearest[n];

                    // A warm-up call, then only the calls between the two reads of the counter.
                    _ = snapshot.Nearest(places[0].X, places[0].Y, ppx, ppy, HoverRadius, marker, mode, ViewAt(places[0], spanX, spanY), scratch);
                    var allocated = GC.GetAllocatedBytesForCurrentThread();
                    for (var i = 0; i < n; i++)
                    {
                        var (px, py) = places[i];
                        var view = ViewAt(places[i], spanX, spanY);
                        var start = Stopwatch.GetTimestamp();
                        answers[i] = snapshot.Nearest(px, py, ppx, ppy, HoverRadius, marker, mode, view, scratch);
                        times[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
                    }

                    allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                    var (found, peak, overflowed) = (0, 0, 0);
                    for (var i = 0; i < n; i++)
                    {
                        visited[i] = answers[i].CellsVisited;
                        read[i] = answers[i].PointsRead;
                        found += answers[i].Found ? 1 : 0;
                        peak = Math.Max(peak, answers[i].QueuePeak);
                        overflowed += answers[i].Overflowed ? 1 : 0;
                    }

                    // The exhaustive answer at evenly spaced places, each timed alone: GetCells of every leaf under the
                    // reach, grown to hold them all (Issue #473), then the first LeafCapacity points of each leaf.
                    var checks = Math.Min(options.CheckPlaces, n);
                    var (mismatches, excess) = (0, 0.0);
                    var referenceTimes = new double[Math.Max(1, checks)];
                    var referenceLeaves = new double[Math.Max(1, checks)];
                    var referencePoints = new double[Math.Max(1, checks)];
                    for (var c = 0; c < checks; c++)
                    {
                        var i = (int)((long)c * n / checks);
                        var (px, py) = places[i];
                        var view = ViewAt(places[i], spanX, spanY);
                        var (rx, ry) = (reach / ppx, reach / ppy);
                        var rect =
                            mode == SpatialHitMode.XY
                                ? new SpatialRect(
                                    Math.Max(view.XMin, px - rx),
                                    Math.Min(view.XMax, px + rx),
                                    Math.Max(view.YMin, py - ry),
                                    Math.Min(view.YMax, py + ry)
                                )
                                : new SpatialRect(Math.Max(view.XMin, px - rx), Math.Min(view.XMax, px + rx), view.YMin, view.YMax);
                        var start = Stopwatch.GetTimestamp();
                        var count = snapshot.GetCells(rect, 32, cells);
                        if (count > cells.Length)
                        {
                            // The time is that of one call into a buffer that holds every leaf.
                            cells = new SpatialCell[count];
                            start = Stopwatch.GetTimestamp();
                            count = snapshot.GetCells(rect, 32, cells);
                        }

                        var least = double.PositiveInfinity;
                        long points = 0;
                        for (var k = 0; k < count; k++)
                        {
                            var length = (int)Math.Min(cells[k].Count, tuning.LeafCapacity);
                            snapshot.ReadPoints(cells[k].First, xs.AsSpan(0, length), ys.AsSpan(0, length), rows.AsSpan(0, length));
                            points += length;
                            for (var p = 0; p < length; p++)
                            {
                                if (xs[p] < view.XMin || xs[p] > view.XMax || ys[p] < view.YMin || ys[p] > view.YMax)
                                {
                                    continue;
                                }

                                var dx = (xs[p] - px) * ppx;
                                var dy = mode == SpatialHitMode.XY ? (ys[p] - py) * ppy : 0;
                                least = Math.Min(least, Math.Sqrt((dx * dx) + (dy * dy)));
                            }
                        }

                        referenceTimes[c] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
                        referenceLeaves[c] = count;
                        referencePoints[c] = points;
                        var answer = answers[i];
                        var agrees = answer.Found
                            ? answer.Distance < reach && answer.Distance >= least - 1e-9 && answer.Distance <= least + 0.5 + 1e-9
                            : least >= reach - 0.5;
                        mismatches += agrees ? 0 : 1;
                        if (answer.Found)
                        {
                            excess = Math.Max(excess, answer.Distance - least);
                        }
                    }

                    (string Key, object Value)[] line = [("places", name), ("view_share", share), ("mode", mode), ("marker_radius", marker)];
                    yield return
                    [
                        ("query", "nearest"),
                        .. line,
                        ("count", n),
                        ("found", found),
                        .. Percentiles(times),
                        ("cells_p50", Quantile(visited, 0.5)),
                        ("cells_p99", Quantile(visited, 0.99)),
                        ("cells_max", Quantile(visited, 1)),
                        ("points_p50", Quantile(read, 0.5)),
                        ("points_p99", Quantile(read, 0.99)),
                        ("points_max", Quantile(read, 1)),
                        ("queue_peak", peak),
                        ("overflowed", overflowed),
                        ("allocated_bytes", allocated),
                        ("checked", checks),
                        ("mismatches", mismatches),
                        ("max_excess_px", excess),
                    ];
                    if (checks > 0)
                    {
                        yield return
                        [
                            ("query", "exhaustive"),
                            .. line,
                            ("count", checks),
                            .. Percentiles(referenceTimes),
                            ("leaves_p50", Quantile(referenceLeaves, 0.5)),
                            ("leaves_p99", Quantile(referenceLeaves, 0.99)),
                            ("leaves_max", Quantile(referenceLeaves, 1)),
                            ("points_p50", Quantile(referencePoints, 0.5)),
                            ("points_p99", Quantile(referencePoints, 0.99)),
                            ("points_max", Quantile(referencePoints, 1)),
                        ];
                    }
                }
            }
        }
    }

    private static SpatialRect ViewAt((double X, double Y) place, double spanX, double spanY) =>
        new(place.X - (spanX / 2), place.X + (spanX / 2), place.Y - (spanY / 2), place.Y + (spanY / 2));

    // The value at a share of the sorted values; sorts the array.
    private static double Quantile(double[] values, double share)
    {
        Array.Sort(values);
        return values[Math.Max(0, (int)Math.Ceiling(values.Length * share) - 1)];
    }

    private static SpatialIndexTuning Tuning(Options options)
    {
        var tuning = SpatialIndexTuning.Default;
        tuning = options.RowsPerUnit > 0 ? tuning with { RowsPerUnit = options.RowsPerUnit } : tuning;
        return options.DensitySplit > 0 ? tuning with { DensitySplit = options.DensitySplit } : tuning;
    }

    private static SpatialIndexInput Input(Options options) => new() { Rows = RowRange.All, TempFolder = options.Temp };

    private static (string Key, object Value)[] Common(Options options, PlotWorkerPool pool, SpatialIndexTuning tuning) =>
        [
            ("distribution", options.Distribution),
            ("workers", pool.ThreadCount),
            ("rows_per_unit", tuning.RowsPerUnit),
            ("density_split", tuning.DensitySplit),
        ];

    /// <summary>
    /// Calls Acquire, Count over the whole plane at the level of about one cell per plot-area pixel, and Release once a frame
    /// until the build ends, as a plot that draws during the build does.
    /// </summary>
    private static void QueryEachFrame(SpatialIndex index, FrameRecorder frames)
    {
        var clock = Stopwatch.StartNew();
        var frame = 0;
        while (!index.Completion.IsCompleted)
        {
            var began = Stopwatch.GetTimestamp();
            var snapshot = index.Acquire();
            _ = snapshot.Count(Everywhere, LevelFor(Width));
            var stage = snapshot.Stage;
            snapshot.Release();
            frames.Add(stage, Stopwatch.GetElapsedTime(began).TotalMicroseconds);

            frame++;
            var next = FramePeriod * frame;
            while (clock.Elapsed < next && !index.Completion.IsCompleted)
            {
                Thread.Sleep(1);
            }
        }
    }

    /// <summary>The level whose cells are about one plot-area pixel when <paramref name="pixels"/> pixels show the whole range.</summary>
    private static int LevelFor(double pixels) => Math.Min(32, (int)Math.Ceiling(Math.Log2(pixels)));

    private static (string Key, object Value)[] Memory(SpatialSnapshot snapshot, long points, SpatialIndexTuning tuning)
    {
        var sourceBytes = 2.0 * points * sizeof(float);
        var copyBytes = points * ((2 * sizeof(float)) + SpatialCopy.PositionWidthFor(points));
        var rows = snapshot.Rows;
        var maxRows = Math.Max(1, (long)Math.Floor(tuning.TableShare * sourceBytes / 32));
        long leaves = 0;
        long coincident = 0;
        long coincidentPoints = 0;
        long capped = 0;
        long cappedPoints = 0;
        var deepest = 0;
        foreach (var row in rows)
        {
            deepest = Math.Max(deepest, row.Level);
            switch ((SpatialCellKind)row.Kind)
            {
                case SpatialCellKind.Leaf:
                    leaves++;
                    break;
                case SpatialCellKind.CoincidentLeaf:
                    coincident++;
                    coincidentPoints += row.Count;
                    break;
                case SpatialCellKind.CappedLeaf:
                    capped++;
                    cappedPoints += row.Count;
                    break;
            }
        }

        var tableBytes = rows.Length * 32L;
        return
        [
            ("points", snapshot.PointCount),
            ("excluded", snapshot.ExcludedCount),
            ("copy_bytes", copyBytes),
            ("copy_percent", 100 * copyBytes / sourceBytes),
            ("table_rows", rows.Length),
            ("max_table_rows", maxRows),
            ("table_bytes", tableBytes),
            ("table_percent", 100 * tableBytes / sourceBytes),
            ("leaves", leaves),
            ("coincident_leaves", coincident),
            ("coincident_points", coincidentPoints),
            ("capped_leaves", capped),
            ("capped_points", cappedPoints),
            ("deepest_level", deepest),
            ("coarse_bytes", SpatialSnapshot.CoarseLength * sizeof(long)),
        ];
    }

    /// <summary>
    /// At <see cref="Places"/> seeded places, each the point of a seeded row: Count over the whole range, Count over a view
    /// of 1 % of each side around the place, and the hover of that view and of a view of 0.01 %: GetCells of the leaves
    /// under a radius of <see cref="HoverRadius"/> plot-area pixels, then ReadPoints of the first LeafCapacity points of
    /// each leaf. Where outliers stretch the range, the radius of the 1 % view covers whole clusters; the deeper view shows
    /// the hover over a few leaves.
    /// </summary>
    private static IEnumerable<(string Key, object Value)[]> Queries(
        SpatialSnapshot snapshot,
        Column<float> x,
        Column<float> y,
        SpatialIndexTuning tuning
    )
    {
        var xr = snapshot.XRange;
        var yr = snapshot.YRange;
        var (width, height) = (xr.Upper - xr.Lower, yr.Upper - yr.Lower);
        var places = new (double X, double Y)[Places];
        var state = PlaceSeed;
        for (var i = 0; i < Places; i++)
        {
            state ^= state >> 12;
            state ^= state << 25;
            state ^= state >> 27;
            var row = (long)((state * 0x2545F4914F6CDD1DUL) % (ulong)x.Length);
            places[i] = (x.GetValue(row) - (double)xr.Origin, y.GetValue(row) - (double)yr.Origin);
        }

        var whole = new SpatialRect(xr.Lower, xr.Upper, yr.Lower, yr.Upper);
        var (zoomWidth, zoomHeight) = (width * ZoomShare, height * ZoomShare);
        var wholeLevel = LevelFor(Width);
        var zoomLevel = LevelFor(Width / ZoomShare);
        var xs = new double[tuning.LeafCapacity];
        var ys = new double[tuning.LeafCapacity];
        var rows = new long[tuning.LeafCapacity];
        var wholeTimes = new double[Places];
        var zoomTimes = new double[Places];
        double[] shares = [ZoomShare, DeepShare];

        // The hover reads every leaf under the radius (Issue #473): a pass before the timed one sizes the buffer for the
        // most leaves of any place, so that the timed pass neither grows it nor allocates.
        var mostCells = 0;
        for (var i = 0; i < Places; i++)
        {
            foreach (var share in shares)
            {
                mostCells = Math.Max(mostCells, snapshot.GetCells(HoverRect(places[i], share, width, height), 32, []));
            }
        }

        var cells = new SpatialCell[Math.Max(1, mostCells)];
        var hoverTimes = new double[shares.Length][];
        var leafCounts = new int[shares.Length][];
        var pointCounts = new long[shares.Length][];
        for (var h = 0; h < shares.Length; h++)
        {
            (hoverTimes[h], leafCounts[h], pointCounts[h]) = (new double[Places], new int[Places], new long[Places]);
        }

        var check = 0.0;

        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Places; i++)
        {
            var (px, py) = places[i];
            var began = Stopwatch.GetTimestamp();
            var count = snapshot.Count(whole, wholeLevel);
            wholeTimes[i] = Stopwatch.GetElapsedTime(began).TotalMicroseconds;
            check += count.Inside;

            var view = new SpatialRect(px - (zoomWidth / 2), px + (zoomWidth / 2), py - (zoomHeight / 2), py + (zoomHeight / 2));
            began = Stopwatch.GetTimestamp();
            count = snapshot.Count(view, zoomLevel);
            zoomTimes[i] = Stopwatch.GetElapsedTime(began).TotalMicroseconds;
            check += count.Touching;

            for (var h = 0; h < shares.Length; h++)
            {
                began = Stopwatch.GetTimestamp();
                var found = snapshot.GetCells(HoverRect(places[i], shares[h], width, height), 32, cells);
                long read = 0;
                for (var c = 0; c < found; c++)
                {
                    var length = (int)Math.Min(cells[c].Count, tuning.LeafCapacity);
                    snapshot.ReadPoints(cells[c].First, xs.AsSpan(0, length), ys.AsSpan(0, length), rows.AsSpan(0, length));
                    read += length;
                    check += xs[0] + ys[length - 1];
                }

                hoverTimes[h][i] = Stopwatch.GetElapsedTime(began).TotalMicroseconds;
                leafCounts[h][i] = found;
                pointCounts[h][i] = read;
            }
        }

        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        yield return [("query", "count_whole"), ("level", wholeLevel), .. Percentiles(wholeTimes), ("allocated_bytes", allocated)];
        yield return [("query", "count_zoom"), ("level", zoomLevel), .. Percentiles(zoomTimes), ("check", check)];
        for (var h = 0; h < shares.Length; h++)
        {
            Array.Sort(leafCounts[h]);
            Array.Sort(pointCounts[h]);
            yield return
            [
                ("query", "hover"),
                ("view_share", shares[h]),
                .. Percentiles(hoverTimes[h]),
                ("leaves_p50", leafCounts[h][Places / 2]),
                ("leaves_max", leafCounts[h][^1]),
                ("points_p50", pointCounts[h][Places / 2]),
                ("points_max", pointCounts[h][^1]),
            ];
        }
    }

    // The rectangle of the hover at a place: the radius in plot-area pixels of a view of a share of each side.
    private static SpatialRect HoverRect((double X, double Y) place, double share, double width, double height)
    {
        var (rx, ry) = (HoverRadius * width * share / Width, HoverRadius * height * share / Height);
        return new SpatialRect(place.X - rx, place.X + rx, place.Y - ry, place.Y + ry);
    }

    /// <summary>
    /// Starts another build and disposes of the index at a moment of it: as soon as a worker began it, once the coarse
    /// snapshot is published (a counting pass runs), or once the writing began. Reports how long Dispose took, and how long
    /// after the call the thread pool had closed the copy's file.
    /// </summary>
    private static (string Key, object Value)[] DisposeAt(
        PlotWorkerPool pool,
        Column<float> x,
        Column<float> y,
        Options options,
        SpatialIndexTuning tuning,
        string moment
    )
    {
        var recorder = new JobRecorder { Start = Stopwatch.GetTimestamp() };
        var copies = new Copies();
        var index = pool.BuildSpatialIndex(x, y, Input(options), tuning with { JobEnded = recorder.Add }, copies);

        // The state is read once a turn: two reads could see two states.
        Func<SpatialIndexState, bool> reached = moment switch
        {
            "started" => state => state != SpatialIndexState.Waiting,
            "counting" => _ => index.Version >= 1,
            "written" => _ => recorder.Writes >= WritesBefore,
            _ => state => state is not (SpatialIndexState.Waiting or SpatialIndexState.Counting),
        };
        var state = index.State;
        while (!reached(state))
        {
            Thread.Yield();
            state = index.State;
        }

        var jobsBefore = recorder.Count;
        var began = Stopwatch.GetTimestamp();
        index.Dispose();
        var milliseconds = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
        copies.WaitClosed();
        var closed = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
        var (jobsAfter, waited, longest) = recorder.Since(jobsBefore, began);
        return
        [
            ("moment", moment),
            ("state", state),
            ("jobs_before", jobsBefore),
            ("dispose_ms", milliseconds),
            ("closed_ms", closed),
            ("jobs_after", jobsAfter),
            ("last_job_end_ms", waited),
            ("longest_job_ms", longest),
        ];
    }

    /// <summary>
    /// The density tiles of a full snapshot: one line of totals (the tiles, their bytes against the source, the table and
    /// the tiles together against the 10 % of requirement P-8, the estimate, and the level the budget left out), one line
    /// per level with the tiles of each width, and one line for TryGetTile at <see cref="Places"/> seeded cells with a tile.
    /// </summary>
    private static List<(string Key, object Value)[]> Density(SpatialSnapshot snapshot, long points, SpatialIndexTuning tuning)
    {
        var tiles = snapshot.Tiles!;
        var sourceBytes = 2L * points * sizeof(float);
        var rows = snapshot.Rows;
        var byLevel = new int[DensityTiles.DeepestTileLevel + 1, 4];
        var cells = new List<(ulong Code, int Level)>();
        foreach (var row in rows)
        {
            if (tiles.TryGetTile(row.Code, row.Level, out var tile))
            {
                byLevel[row.Level, int.Log2(tile.BytesPerBin)]++;
                cells.Add((row.Code, row.Level));
            }
        }

        using var plan = new DensityBuilder(snapshot, tuning.DensitySplit, tuning.RowsPerUnit, sourceBytes, tuning.TotalShare);
        var tableBytes = rows.Length * 32L;
        var lines = new List<(string Key, object Value)[]>
        {
            new (string Key, object Value)[]
            {
                ("part", "total"),
                ("tiles", tiles.TileCount),
                ("tile_bytes", tiles.ByteCount),
                ("tile_percent", 100.0 * tiles.ByteCount / sourceBytes),
                ("table_and_tiles_percent", 100.0 * (tiles.ByteCount + tableBytes) / sourceBytes),
                ("estimated_bytes", plan.EstimatedBytes),
                ("estimated_percent", 100.0 * (plan.EstimatedBytes + tableBytes) / sourceBytes),
                ("stopped_level", plan.StoppedLevel),
                ("levels", plan.LevelCount),
            },
        };
        for (var level = 0; level < plan.LevelCount; level++)
        {
            lines.Add([
                ("part", "level"),
                ("level", level),
                ("bytes1", byLevel[level, 0]),
                ("bytes2", byLevel[level, 1]),
                ("bytes4", byLevel[level, 2]),
                ("bytes8", byLevel[level, 3]),
            ]);
        }

        if (cells.Count == 0)
        {
            return lines;
        }

        var times = new double[Places];
        var state = PlaceSeed;
        ulong check = 0;
        _ = tiles.TryGetTile(cells[0].Code, cells[0].Level, out _);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Places; i++)
        {
            state ^= state >> 12;
            state ^= state << 25;
            state ^= state >> 27;
            var (code, level) = cells[(int)((state * 0x2545F4914F6CDD1DUL) % (ulong)cells.Count)];
            var began = Stopwatch.GetTimestamp();
            _ = tiles.TryGetTile(code, level, out var tile);
            times[i] = Stopwatch.GetElapsedTime(began).TotalMicroseconds;
            check += tile[i % DensityTiles.Side, (i / DensityTiles.Side) % DensityTiles.Side];
        }

        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        lines.Add([("part", "lookup"), .. Percentiles(times), ("allocated_bytes", allocated), ("check", check)]);
        return lines;
    }

    private static (string Key, object Value)[] Percentiles(double[] microseconds)
    {
        Array.Sort(microseconds);
        return
        [
            ("p50_us", microseconds[microseconds.Length / 2]),
            ("p99_us", microseconds[(int)Math.Ceiling(microseconds.Length * 0.99) - 1]),
            ("max_us", microseconds[^1]),
        ];
    }

    // The time of day in UTC, to find the build in the log of watch-machine.ps1. Formatted from its parts: the formats
    // of a date bring the calendar data into the executable (100 KB).
    private static string EndedUtc()
    {
        var now = TimeProvider.System.GetUtcNow();
        return string.Create(CultureInfo.InvariantCulture, $"{now.Hour:00}:{now.Minute:00}:{now.Second:00}");
    }

    private static void Report(Options options, int run, string figure, (string Key, object Value)[] figures) =>
        Scenes.Report(options, run, [("figure", figure), .. figures]);

    /// <summary>The allocator of the engine, which keeps the copies it made so that a scene can wait until their files are closed.</summary>
    private sealed class Copies : SpatialCopyAllocator
    {
        private readonly List<SpatialCopy> _made = [];

        public override SpatialCopy Allocate(
            long capacity,
            ReadOnlySpan<int> valueWidths,
            bool mapped,
            string? tempFolder,
            Func<bool>? stopped = null
        )
        {
            var copy = base.Allocate(capacity, valueWidths, mapped, tempFolder, stopped);
            lock (_made)
            {
                _made.Add(copy);
            }

            return copy;
        }

        /// <summary>Waits until every copy made so far is closed. The index that made them is disposed.</summary>
        public void WaitClosed()
        {
            SpatialCopy[] made;
            lock (_made)
            {
                made = [.. _made];
            }

            foreach (var copy in made)
            {
                copy.Closed.Wait();
            }
        }
    }

    /// <summary>The jobs of one build: what each was, how long it ran, and when it ended, kept without a lock.</summary>
    private sealed class JobRecorder
    {
        private const int Capacity = 1 << 21;
        private readonly SpatialJob[] _jobs = new SpatialJob[Capacity];
        private readonly long[] _ticks = new long[Capacity];
        private readonly long[] _ends = new long[Capacity];
        private int _count;
        private int _writes;

        public long Start { get; set; }

        public int Count => Math.Min(Volatile.Read(ref _count), Capacity);

        /// <summary>Gets the number of units of the writing that have ended.</summary>
        public int Writes => Volatile.Read(ref _writes);

        public void Add(SpatialJob job, long ticks)
        {
            var end = Stopwatch.GetTimestamp();
            if (job == SpatialJob.Write)
            {
                Interlocked.Increment(ref _writes);
            }

            var slot = Interlocked.Increment(ref _count) - 1;
            if (slot < Capacity)
            {
                _jobs[slot] = job;
                _ticks[slot] = ticks;
                _ends[slot] = end - Start;
            }
        }

        /// <summary>
        /// The jobs that ended after the first <paramref name="before"/>: how many, when the last of them ended after
        /// <paramref name="timestamp"/>, and how long the longest ran, in milliseconds. Dispose waits for these jobs; the
        /// rest of its time frees the build.
        /// </summary>
        public (int Count, double LastEnd, double Longest) Since(int before, long timestamp)
        {
            var (lastEnd, longest) = (0.0, 0.0);
            var count = Count;
            for (var i = before; i < count; i++)
            {
                lastEnd = Math.Max(lastEnd, Seconds(_ends[i] + Start - timestamp) * 1e3);
                longest = Math.Max(longest, Seconds(_ticks[i]) * 1e3);
            }

            return (count - before, lastEnd, longest);
        }

        /// <summary>
        /// The time of each stage, from the end of the single job before it to the end of its own last single job, and the
        /// time to the coarse publish.
        /// </summary>
        public IEnumerable<(string Key, object Value)> Stages()
        {
            var singles = Enumerable
                .Range(0, Count)
                .Where(i => !IsUnit(_jobs[i]))
                .OrderBy(i => _ends[i])
                .Select(i => (Job: _jobs[i], End: Seconds(_ends[i])))
                .ToArray();
            var previous = 0.0;
            var pass = 0;
            var levels = 0;
            foreach (var (job, end) in singles)
            {
                if (job == SpatialJob.EndDensityLevel)
                {
                    // The levels of the tiles are one stage, which ends with PublishDensity.
                    levels++;
                    continue;
                }

                var name = job switch
                {
                    SpatialJob.Allocate => "allocate_s",
                    SpatialJob.EndRange => "range_s",
                    SpatialJob.PublishCoarse => "first_s",
                    SpatialJob.EndPass => $"pass{++pass}_s",
                    SpatialJob.Table => "table_s",
                    SpatialJob.PublishFull => "write_s",
                    SpatialJob.PlanDensity => "plan_density_s",
                    _ => "density_s",
                };
                yield return (name, end - previous);
                if (job == SpatialJob.PublishCoarse)
                {
                    yield return ("coarse_at_s", end);
                }
                else if (job == SpatialJob.PublishFull)
                {
                    // Requirement P-15 is the time to here: the points can be asked for.
                    yield return ("full_at_s", end);
                }

                previous = end;
            }

            yield return ("passes", pass);
            yield return ("density_levels", levels);
        }

        /// <summary>
        /// One line per kind of job: how many ran, and the median, p99, and longest of their times; where the longest one
        /// ended among the jobs of its kind, and the mean time of the first 8 that ended against the mean of the rest.
        /// </summary>
        public IEnumerable<(string Key, object Value)[]> Jobs()
        {
            const int Early = 8;
            foreach (var kind in Enum.GetValues<SpatialJob>())
            {
                var order = Enumerable.Range(0, Count).Where(i => _jobs[i] == kind).OrderBy(i => _ends[i]).ToArray();
                if (order.Length == 0)
                {
                    continue;
                }

                var inOrder = order.Select(i => Seconds(_ticks[i]) * 1e6).ToArray();
                var slowest = Array.IndexOf(inOrder, inOrder.Max());
                var early = inOrder.Take(Early).Average();
                var later = inOrder.Length > Early ? inOrder.Skip(Early).Average() : double.NaN;
                yield return
                [
                    ("job", kind),
                    ("count", inOrder.Length),
                    .. Percentiles([.. inOrder]),
                    ("total_ms", inOrder.Sum() / 1e3),
                    ("slowest_rank", slowest),
                    ("first8_mean_us", early),
                    ("rest_mean_us", later),
                ];
            }
        }

        private static bool IsUnit(SpatialJob job) =>
            job is SpatialJob.Range or SpatialJob.First or SpatialJob.Pass or SpatialJob.Write or SpatialJob.Density;

        private static double Seconds(long ticks) => (double)ticks / Stopwatch.Frequency;
    }

    /// <summary>The time of each frame's Acquire, Count, and Release, by the stage of the snapshot it got.</summary>
    private sealed class FrameRecorder
    {
        private readonly List<(SpatialStage Stage, double Microseconds)> _frames = new(1 << 16);

        public void Add(SpatialStage stage, double microseconds) => _frames.Add((stage, microseconds));

        public IEnumerable<(string Key, object Value)[]> Lines()
        {
            foreach (var stage in Enum.GetValues<SpatialStage>())
            {
                var times = _frames.Where(frame => frame.Stage == stage).Select(frame => frame.Microseconds).ToArray();
                if (times.Length > 0)
                {
                    yield return [("stage", stage), ("frames", times.Length), .. Percentiles(times)];
                }
            }
        }
    }
}
