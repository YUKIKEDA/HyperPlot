using System.Runtime.CompilerServices;

namespace HyperPlot.Wpf.Tests;

/// <summary>Settings the test assembly makes before any test runs.</summary>
internal static class TestAssemblySetup
{
    /// <summary>
    /// Keeps the background work of every worker pool at the normal priority: the tests run beside the workers on the few
    /// processors of a CI runner, where a unit at a lower priority would be starved (design §10, row S4).
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize() => PlotWorkerPool.DefaultPriority = ThreadPriority.Normal;
}
