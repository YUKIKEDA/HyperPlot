using System.Diagnostics;

namespace HyperPlot.Tests;

/// <summary>
/// The role of the test executable as a child process: it builds the spatial tile index of two memory-mapped columns
/// and stops in the writing of the copy, so that the test can kill it while the temporary file is open and written
/// (design §3.4 "一時ファイル", row C3a).
/// </summary>
internal static class SpatialCopyChild
{
    /// <summary>The first argument of the command line that selects this role.</summary>
    public const string Switch = "--spatial-copy-child";

    /// <summary>The name of the file that the child makes in the folder when a worker has stopped in the writing.</summary>
    public const string ReadyName = "ready.txt";

    // A child that nobody kills ends by itself: the test that started it has failed or was stopped.
    private const int Watchdog = 120_000;

    private static int _stopped;

    /// <summary>Starts this executable again in the role of the child.</summary>
    /// <param name="data">The file of the x values followed by the y values, both <see cref="float"/>.</param>
    /// <param name="rows">The number of rows.</param>
    /// <param name="folder">The folder of the temporary file, where the child also makes <see cref="ReadyName"/>.</param>
    /// <returns>The started process.</returns>
    public static Process Start(string data, int rows, string folder)
    {
        var host = Environment.ProcessPath!;
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };

        // Under "dotnet exec" the process is the host, and the test assembly is its first argument.
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(typeof(SpatialCopyChild).Assembly.Location);
        }

        start.ArgumentList.Add(Switch);
        start.ArgumentList.Add(data);
        start.ArgumentList.Add(rows.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(folder);
        return Process.Start(start)!;
    }

    /// <summary>Builds the index and waits to be killed.</summary>
    /// <param name="args">The file of the values, the number of rows, and the folder.</param>
    /// <returns>An exit code that is not 0: the child was not killed within its watchdog, or the build ended.</returns>
    public static int Run(ReadOnlySpan<string> args)
    {
        var data = args[0];
        var rows = int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
        var folder = args[2];

        using var x = Column.MapFile<float>(data, 0, rows);
        using var y = Column.MapFile<float>(data, (long)rows * sizeof(float), rows);
        using var pool = new PlotWorkerPool(2);
        var tuning = SpatialReference.Small with
        {
            JobEnded = (job, _) =>
            {
                if (job != SpatialJob.Write)
                {
                    return;
                }

                // The unit has written its points into the mapped file. Its end is not told to the build, so the
                // build cannot end and close the file before the kill.
                if (Interlocked.Exchange(ref _stopped, 1) == 0)
                {
                    File.WriteAllText(Path.Combine(folder, ReadyName), "ready");
                }

                Thread.Sleep(Watchdog);
            },
        };
        var index = pool.BuildSpatialIndex(
            x,
            y,
            new SpatialIndexInput { Rows = RowRange.All, TempFolder = folder },
            tuning,
            SpatialCopyAllocator.Default
        );
        var ended = index.Completion.ContinueWith(_ => { }, TaskScheduler.Default).Wait(Watchdog);
        Console.Error.WriteLine(ended ? $"The build ended: {index.Fault}" : "Nobody killed the child within its watchdog.");
        return ended ? 3 : 2;
    }
}
