using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HyperPlot.E2E;

/// <summary>
/// The cores the other processes of the machine used over a stretch of a scene (.cursor/rules/bench.mdc): the machine's
/// busy time from <c>GetSystemTimes</c>, less this process's own CPU time, over the wall time. The machine is shared, so
/// each run of a scene that sets a requirement's value prints it.
/// </summary>
internal static partial class MachineLoad
{
    /// <summary>Returns the machine's busy time, this process's CPU time, and the clock, to measure from.</summary>
    internal static Mark Now()
    {
        _ = GetSystemTimes(out var idle, out var kernel, out var user);
        using var own = Process.GetCurrentProcess();

        // The kernel time holds the idle time; all three count 100 ns.
        return new Mark(kernel + user - idle, own.TotalProcessorTime.Ticks, Stopwatch.GetTimestamp());
    }

    /// <summary>Returns the cores the other processes used since a mark, and the cores this process used.</summary>
    internal static (double Others, double Own) CoresSince(Mark from)
    {
        var to = Now();
        var wall = Stopwatch.GetElapsedTime(from.Stamp, to.Stamp).Ticks;
        if (wall <= 0)
        {
            return (0, 0);
        }

        var own = to.Own - from.Own;
        return (Math.Max(0, to.Busy - from.Busy - own) / (double)wall, own / (double)wall);
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out long idle, out long kernel, out long user);

    /// <summary>A moment to measure from: the machine's busy time and this process's CPU time in units of 100 ns, and the clock's stamp.</summary>
    internal readonly record struct Mark(long Busy, long Own, long Stamp);
}
