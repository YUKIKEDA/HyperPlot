using System.Runtime.CompilerServices;

namespace HyperPlot.Testing.Tests;

/// <summary>Settings the test assembly makes before any test runs.</summary>
internal static class TestAssemblySetup
{
    /// <summary>
    /// Keeps every worker pool from resting: the tests run side by side and fill the few processors of a CI runner,
    /// where workers that rested would slow the tests that wait for a worker (design §10, row S4).
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize() => PlotWorkerPool.DefaultYields = false;
}
