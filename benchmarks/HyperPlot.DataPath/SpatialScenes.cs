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
    private const int WritesBefore = 200;
    private const int TouchThreads = 8;
    private const int TouchPagesPerThread = 12_288;
    private const ulong PlaceSeed = 0x43_33_61_50_6C_61_63_65UL;
    private static readonly TimeSpan FramePeriod = TimeSpan.FromSeconds(1.0 / 60);

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
    /// Builds the index over the files as they are, at the normal priority of the process, with a pool that yields the
    /// CPU or not (<see cref="Options.Yields"/>, and <see cref="Options.MostRest"/> for its greatest level), and reports
    /// what the build leaves to others (design §9 "CPU を譲ること", requirement N-12): the speed of a competitor on every logical processor against its speed before the
    /// build (<see cref="Options.Competitor"/>), the frames' Acquire, Count, and Release each 16.7 ms, the processors the
    /// build took, and the time of GC.Collect from another thread (<see cref="Options.Collects"/>). With
    /// <see cref="Options.Seconds"/> the build is disposed of after that many seconds, and the units that ended and the
    /// time of Dispose are reported.
    /// </summary>
    internal static void Yield(Options options)
    {
        var workers = options.Workers > 0 ? options.Workers : Math.Max(1, Environment.ProcessorCount - 1);
        var resting = WorkerYieldTuning.Default;
        var meter = options.Yields ? CpuMeter.TryCreate(workers) : null;
        using var pool = meter is null
            ? new PlotWorkerPool(workers, yields: false)
            : new PlotWorkerPool(workers, meter, options.MostRest > 0 ? resting with { MostRest = options.MostRest } : resting);
        var (xPath, yPath) = PointFiles.Paths(options.File);
        using var x = Column.MapFile<float>(xPath);
        using var y = Column.MapFile<float>(yPath);
        using var process = Process.GetCurrentProcess();
        using var competitor = options.Competitor ? new YieldScenes.Competitor() : null;

        // The competitor's speed with nothing beside it.
        if (competitor is not null)
        {
            Thread.Sleep(TimeSpan.FromSeconds(3));
        }

        var baseline = competitor?.Measure(TimeSpan.FromSeconds(8)) ?? 0;

        _ = NativeMethods.TimeBeginPeriod(1);
        try
        {
            var recorder = new JobRecorder();
            var tuning = Tuning(options) with { JobEnded = recorder.Add };
            var copies = new Copies();
            var times = new List<double>(1 << 16);
            var collects = new List<double>();
            var cpu = process.TotalProcessorTime;
            competitor?.Mark();
            recorder.Start = Stopwatch.GetTimestamp();
            var index = pool.BuildSpatialIndex(x, y, Input(options), tuning, copies);
            var clock = Stopwatch.StartNew();
            var done = 0;
            var collector = options.Collects
                ? new Thread(() =>
                {
                    while (Volatile.Read(ref done) == 0)
                    {
                        Thread.Sleep(2_000);
                        var began = Stopwatch.GetTimestamp();
                        GC.Collect();
                        collects.Add(Stopwatch.GetElapsedTime(began).TotalMilliseconds);
                    }
                })
                : null;
            collector?.Start();
            var frame = 0;
            var limit = options.Seconds > 0 ? TimeSpan.FromSeconds(options.Seconds) : TimeSpan.MaxValue;
            while (!index.Completion.IsCompleted && clock.Elapsed < limit)
            {
                var began = Stopwatch.GetTimestamp();
                var snapshot = index.Acquire();
                _ = snapshot.Count(Everywhere, LevelFor(Width));
                snapshot.Release();
                times.Add(Stopwatch.GetElapsedTime(began).TotalMicroseconds);
                frame++;
                var next = FramePeriod * frame;
                while (clock.Elapsed < next && !index.Completion.IsCompleted)
                {
                    Thread.Sleep(1);
                }
            }

            var seconds = clock.Elapsed.TotalSeconds;
            var during = competitor?.SinceMark() ?? 0;
            var completed = index.Completion.IsCompletedSuccessfully;
            process.Refresh();
            var cores = (process.TotalProcessorTime - cpu).TotalSeconds / seconds;
            var disposing = Stopwatch.GetTimestamp();
            index.Dispose();
            var disposed = Stopwatch.GetElapsedTime(disposing).TotalMilliseconds;
            Volatile.Write(ref done, 1);
            collector?.Join();
            copies.WaitClosed();

            var frames = times.ToArray();
            var means = pool.Yield?.Means ?? default;
            Array.Sort(frames);
            (string Key, object Value)[] figures =
            [
                ("yields", pool.Yield is not null),
                ("most_rest", options.MostRest),
                ("workers", pool.ThreadCount),
                ("competitor", options.Competitor),
                ("completed", completed),
                ("ended_utc", EndedUtc()),
                ("seconds", seconds),
                ("cores", cores),
                ("units_range", recorder.CountOf(SpatialJob.Range)),
                ("units_first", recorder.CountOf(SpatialJob.First)),
                ("units_pass", recorder.CountOf(SpatialJob.Pass)),
                ("units_write", recorder.CountOf(SpatialJob.Write)),
                ("units_density", recorder.CountOf(SpatialJob.Density)),
                ("dispose_ms", disposed),
                ("frames", frames.Length),
                ("frame_p50_us", frames[frames.Length / 2]),
                ("frame_p95_us", frames[(int)Math.Ceiling(frames.Length * 0.95) - 1]),
                ("frame_p99_us", frames[(int)Math.Ceiling(frames.Length * 0.99) - 1]),
                ("frame_max_us", frames[^1]),
                ("frames_over_16ms", frames.Count(time => time > 16_700)),
                ("collects", collects.Count),
                ("collect_min_ms", collects.Count > 0 ? collects.Min() : 0),
                ("collect_max_ms", collects.Count > 0 ? collects.Max() : 0),
                ("samples", means.Samples),
                ("idle_share_mean", means.IdleShare),
                ("other_cores_mean", means.OtherCores),
                ("worker_cores_mean", means.WorkerCores),
                ("level_mean", means.Level),
                ("rests", pool.Yield?.RestCount ?? 0),
                ("rest_s", pool.Yield?.RestTime.TotalSeconds ?? 0),
                ("competitor_alone_per_s", baseline),
                ("competitor_beside_per_s", during),
                ("competitor_percent", baseline > 0 ? 100 * during / baseline : 0),
            ];
            Scenes.Report(options, 1, completed ? [.. figures, .. recorder.Stages()] : figures);
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
        var cells = new SpatialCell[1024];
        var xs = new double[tuning.LeafCapacity];
        var ys = new double[tuning.LeafCapacity];
        var rows = new long[tuning.LeafCapacity];
        var wholeTimes = new double[Places];
        var zoomTimes = new double[Places];
        double[] shares = [ZoomShare, DeepShare];
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
                var (rx, ry) = (HoverRadius * width * shares[h] / Width, HoverRadius * height * shares[h] / Height);
                began = Stopwatch.GetTimestamp();
                var found = snapshot.GetCells(new SpatialRect(px - rx, px + rx, py - ry, py + ry), 32, cells);
                long read = 0;
                for (var c = 0; c < Math.Min(found, cells.Length); c++)
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

        /// <summary>Returns the number of jobs of a kind that have ended.</summary>
        public int CountOf(SpatialJob job)
        {
            var count = 0;
            for (var i = 0; i < Count; i++)
            {
                count += _jobs[i] == job ? 1 : 0;
            }

            return count;
        }

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
