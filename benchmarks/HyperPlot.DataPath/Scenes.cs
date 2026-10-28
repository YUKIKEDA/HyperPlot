using System.Diagnostics;
using System.Globalization;

namespace HyperPlot.DataPath;

/// <summary>
/// The data-path scenes of design §9. Each prints one line per figure, <c>key=value</c> pairs, for the record in
/// <c>docs/benchmarks/</c>. A cold scene rewrites the file unbuffered first and runs twice in a row; the second run is warm,
/// and the ratio of the two shows that the first was cold.
/// </summary>
internal static class Scenes
{
    private const int Columns = 1920;
    private static readonly TimeSpan FramePeriod = TimeSpan.FromSeconds(1.0 / 60);

    /// <summary>The pyramid build, from <c>BuildPyramid</c> to <c>Completion</c> (requirement P-2).</summary>
    internal static void Build(Options options)
    {
        using var ownPool = options.Workers > 0 ? new PlotWorkerPool(options.Workers) : null;
        var pool = ownPool ?? PlotWorkerPool.Shared;
        Prepare(options);
        for (var run = 1; run <= 2; run++)
        {
            using var column = Column.MapFile<float>(options.File);
            var clock = Stopwatch.StartNew();
            using var pyramid = pool.BuildPyramid(column);
            pyramid.Completion.Wait();
            var seconds = clock.Elapsed.TotalSeconds;
            var bytes = column.Length * sizeof(float);
            Report(
                options,
                run,
                ("workers", pool.ThreadCount),
                ("seconds", seconds),
                ("gb_per_s", bytes / seconds / 1e9),
                ("pyramid_bytes", pyramid.BlockBytes),
                ("pyramid_percent", 100.0 * pyramid.BlockBytes / bytes)
            );
        }
    }

    /// <summary>
    /// The time from the start of a build until M4, run once a frame, is no longer provisional (design §3.3). The view is
    /// the whole column, or the middle 1 %.
    /// </summary>
    internal static void Provisional(Options options)
    {
        var pool = PlotWorkerPool.Shared;
        Prepare(options);

        // Windows wakes a sleeping thread on its timer tick, 15.6 ms by default; 1 ms keeps the frames 16.7 ms apart.
        _ = NativeMethods.TimeBeginPeriod(1);
        for (var run = 1; run <= 2; run++)
        {
            using var column = Column.MapFile<float>(options.File);
            var x = Column.Uniform(0, 1, column.Length);
            var (from, to) = options.Zoom ? (column.Length * 0.495, column.Length * 0.505) : (0.0, column.Length);
            var boundaries = new double[Columns + 1];
            for (var k = 0; k <= Columns; k++)
            {
                boundaries[k] = from + ((to - from) * k / Columns);
            }

            var points = new M4Point[M4.MaxPointCount(Columns)];
            var frames = 0;
            var longest = 0.0;
            var total = 0.0;
            var clock = Stopwatch.StartNew();
            using var pyramid = pool.BuildPyramid(column);
            while (true)
            {
                var frameStart = clock.Elapsed;
                var result = M4.Extract(x, pyramid, boundaries, points);
                var extract = (clock.Elapsed - frameStart).TotalMilliseconds;
                frames++;
                longest = Math.Max(longest, extract);
                total += extract;
                if (!result.IsProvisional)
                {
                    Report(
                        options,
                        run,
                        ("view", options.Zoom ? "zoom" : "full"),
                        ("seconds", frameStart.TotalSeconds),
                        ("frames", frames),
                        ("m4_mean_ms", total / frames),
                        ("m4_max_ms", longest)
                    );
                    break;
                }

                // Wait for the next frame; a sleep of 1 ms keeps the wait off the workers' cores.
                var next = FramePeriod * frames;
                while (clock.Elapsed < next)
                {
                    Thread.Sleep(1);
                }
            }

            pyramid.Completion.Wait();
        }

        _ = NativeMethods.TimeEndPeriod(1);
    }

    /// <summary>
    /// The first M4 over a file not in the page cache, right after <c>BuildPyramid</c> (design §9): no pyramid and no chunk
    /// statistics yet, so M4 reads only the boundary elements. With <see cref="Options.ReadsOnly"/>, the same elements are
    /// read with <see cref="Column{T}.GetValue(long)"/> and no workers.
    /// </summary>
    internal static void ColdFrame(Options options)
    {
        Prepare(options);
        using var column = Column.MapFile<float>(options.File);
        var x = Column.Uniform(0, 1, column.Length);
        var boundaries = new double[Columns + 1];
        for (var k = 0; k <= Columns; k++)
        {
            boundaries[k] = (double)column.Length * k / Columns;
        }

        var points = new M4Point[M4.MaxPointCount(Columns)];
        var pyramid = options.ReadsOnly ? null : PlotWorkerPool.Shared.BuildPyramid(column);
        try
        {
            for (var run = 1; run <= 2; run++)
            {
                var clock = Stopwatch.StartNew();
                var count = pyramid is null ? ReadBoundaries(column, boundaries) : M4.Extract(x, pyramid, boundaries, points).Count;
                Report(options, run, ("mode", pyramid is null ? "reads" : "m4"), ("ms", clock.Elapsed.TotalMilliseconds), ("values", count));
            }

            pyramid?.Completion.Wait();
        }
        finally
        {
            pyramid?.Dispose();
        }
    }

    /// <summary>Reads the first and the last element of each pixel column, as M4 does without statistics.</summary>
    private static int ReadBoundaries(Column<float> column, double[] boundaries)
    {
        var count = 0;
        var sum = 0f;
        for (var k = 0; k < Columns; k++)
        {
            var start = (long)Math.Ceiling(boundaries[k]);
            var end = (long)Math.Ceiling(boundaries[k + 1]);
            if (start < end)
            {
                sum += column.GetValue(start) + column.GetValue(end - 1);
                count += 2;
            }
        }

        // The sum keeps the reads from being removed.
        return float.IsNaN(sum) ? -count : count;
    }

    private static void Prepare(Options options)
    {
        if (options.Cold)
        {
            SeriesFile.Write(options.File, options.Points, options.Seed);
        }
    }

    private static void Report(Options options, int run, params (string Key, object Value)[] figures)
    {
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"scene={options.Scene} state={(options.Cold && run == 1 ? "cold" : "warm")} run={run}"
        );
        foreach (var (key, value) in figures)
        {
            line += string.Create(
                CultureInfo.InvariantCulture,
                $" {key}={(value is double d ? d.ToString("G6", CultureInfo.InvariantCulture) : value)}"
            );
        }

        Console.WriteLine(line);
    }
}
