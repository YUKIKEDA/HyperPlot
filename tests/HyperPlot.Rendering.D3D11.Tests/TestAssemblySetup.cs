using System.Runtime.CompilerServices;

namespace HyperPlot.Rendering.D3D11.Tests;

/// <summary>Settings the test assembly makes before any test runs.</summary>
internal static class TestAssemblySetup
{
    /// <summary>Gets the priority of background work that the engine ships with, read before the tests' own was set.</summary>
    internal static ThreadPriority ShippedPriority { get; private set; }

    /// <summary>
    /// Has each new worker pool make its lock's wait event when it is made (Issue #304), so that the Zero-Alloc tests do not
    /// count it in the frame where their thread first waits on the pool's lock.
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        PlotWorkerPool.WarmsGate = true;

        // The tests run beside the workers on the few processors of a CI runner: a unit at a lower priority would be
        // starved there, and a test that waits for a worker would time out (design §10, row S4).
        ShippedPriority = PlotWorkerPool.DefaultPriority;
        PlotWorkerPool.DefaultPriority = ThreadPriority.Normal;
    }
}
