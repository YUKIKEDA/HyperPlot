using System.Diagnostics;
using System.Globalization;

namespace HyperPlot.DataPath;

/// <summary>
/// The competitor of the scene of row S4 (design §9 "CPU を譲ること"): a process that spins on every logical processor
/// at the normal priority and says each second how far it came.
/// </summary>
internal static class YieldScenes
{
    /// <summary>
    /// Spins one thread on every logical processor and writes, once a second, the time in milliseconds and the loops of all
    /// threads since the last line. Runs for <see cref="Options.Seconds"/> seconds, or until it is killed.
    /// </summary>
    internal static void Burn(Options options)
    {
        var threads = Environment.ProcessorCount;

        // One counter a cache line apart for each thread.
        var counts = new long[threads * 8];
        for (var thread = 0; thread < threads; thread++)
        {
            var slot = thread * 8;
            new Thread(() =>
            {
                var state = 0x9E3779B97F4A7C15UL + (ulong)slot;
                while (true)
                {
                    for (var i = 0; i < 1 << 16; i++)
                    {
                        state ^= state >> 12;
                        state ^= state << 25;
                        state ^= state >> 27;
                    }

                    Volatile.Write(ref counts[slot], counts[slot] + 1);
                }
            })
            {
                IsBackground = true,
            }.Start();
        }

        var clock = Stopwatch.StartNew();
        long before = 0;
        for (var second = 1; options.Seconds <= 0 || second <= options.Seconds; second++)
        {
            Thread.Sleep(TimeSpan.FromSeconds(second) - clock.Elapsed is { Ticks: > 0 } wait ? wait : TimeSpan.Zero);
            long total = 0;
            for (var thread = 0; thread < threads; thread++)
            {
                total += Volatile.Read(ref counts[thread * 8]);
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"burn ms={clock.ElapsedMilliseconds} loops={total - before}"));
            before = total;
        }
    }

    /// <summary>The competitor as a child process whose lines the scene reads.</summary>
    internal sealed class Competitor : IDisposable
    {
        private readonly Process _process;
        private readonly Lock _gate = new();
        private readonly List<(long Milliseconds, long Loops)> _lines = [];
        private int _mark;

        /// <summary>Initializes a new instance of the <see cref="Competitor"/> class and starts the process.</summary>
        public Competitor()
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!, "burn") { RedirectStandardOutput = true, UseShellExecute = false };
            _process = Process.Start(start)!;
            _process.OutputDataReceived += (_, line) =>
            {
                if (line.Data is { } text && text.StartsWith("burn ms=", StringComparison.Ordinal))
                {
                    var parts = text.Split(' ', '=');
                    lock (_gate)
                    {
                        _lines.Add((long.Parse(parts[2], CultureInfo.InvariantCulture), long.Parse(parts[4], CultureInfo.InvariantCulture)));
                    }
                }
            };
            _process.BeginOutputReadLine();
        }

        /// <summary>Returns the competitor's loops a second over the time that follows.</summary>
        public double Measure(TimeSpan time)
        {
            Mark();
            Thread.Sleep(time);
            return SinceMark();
        }

        /// <summary>Notes the lines so far: <see cref="SinceMark"/> counts from the next one.</summary>
        public void Mark()
        {
            lock (_gate)
            {
                _mark = _lines.Count;
            }
        }

        /// <summary>Returns the competitor's loops a second over its lines since <see cref="Mark"/>, or 0 with fewer than 2 lines.</summary>
        public double SinceMark()
        {
            lock (_gate)
            {
                if (_lines.Count - _mark < 2)
                {
                    return 0;
                }

                // The first line since the mark ends a second that began before it: it only gives the time to count from.
                long loops = 0;
                for (var i = _mark + 1; i < _lines.Count; i++)
                {
                    loops += _lines[i].Loops;
                }

                return loops * 1000.0 / (_lines[^1].Milliseconds - _lines[_mark].Milliseconds);
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (!_process.HasExited)
            {
                _process.Kill();
                _process.WaitForExit();
            }

            _process.Dispose();
        }
    }
}
