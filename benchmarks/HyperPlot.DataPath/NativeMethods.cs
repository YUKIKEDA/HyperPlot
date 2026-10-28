using System.Runtime.InteropServices;

namespace HyperPlot.DataPath;

/// <summary>The Windows timer resolution, which sets how soon a sleeping thread wakes.</summary>
internal static partial class NativeMethods
{
    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    internal static partial uint TimeBeginPeriod(uint milliseconds);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    internal static partial uint TimeEndPeriod(uint milliseconds);
}
