using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>
/// What the plan tests of rows G1 and G2 share since row G3 (design §4.8): the theme without guides, whose panel is the
/// whole target with the bounds and the mapping those rows wrote, and a plan that measures text with the fake backend.
/// </summary>
internal static class TestPlans
{
    /// <summary>Gets the theme without guides: no axes, no grid, no padding. Its panel is the whole target.</summary>
    internal static Theme Bare { get; } = Theme.Light with { ShowAxes = false, ShowGrid = false, Padding = 0 };

    /// <summary>Returns a plan for a target, measuring text with a fake backend, at the scale 1.</summary>
    internal static PlotPlan Create(PlotWorkerPool pool, int width, int height) => new(pool, width, height, new TextEngine(new FakeTextBackend()), 1);
}
