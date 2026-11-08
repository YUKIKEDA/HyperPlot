using System.Runtime.CompilerServices;

namespace HyperPlot.Tests;

/// <summary>Settings the test assembly makes before any test runs.</summary>
internal static class TestAssemblySetup
{
    /// <summary>
    /// Has each new worker pool make its lock's wait event when it is made (Issue #304), so that the Zero-Alloc tests do not
    /// count it in the frame where their thread first waits on the pool's lock.
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize() => PlotWorkerPool.WarmsGate = true;
}
