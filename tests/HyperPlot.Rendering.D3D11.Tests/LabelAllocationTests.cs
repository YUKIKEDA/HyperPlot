using HyperPlot.Tests;

namespace HyperPlot.Rendering.D3D11.Tests;

/// <summary>
/// New tick labels while zooming allocate nothing with DirectWrite either (design §4.6, §5.6, row I1c): the labels'
/// characters, the list's characters, and the shaping cache are reused, and the backend shapes into the cache's entry.
/// Windows only.
/// </summary>
public sealed class LabelAllocationTests
{
    /// <summary>
    /// With DirectWrite, a plot whose x domain moves each frame, so that every x label is new, allocates nothing per frame
    /// once the shaping cache is full.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A line of 10,000 points; a WARP backend with DirectWrite; 1,280 × 720; x domains 1,370 apart, each made beforehand.
    ///
    /// Steps:
    /// - Render the domains in turn until a run of 300 frames allocates nothing (at most 4 runs, which fill the shaping cache
    ///   of 8,192 texts); collect; then render 300 more domains, none drawn before.
    ///
    /// Expected:
    /// - 0 bytes over the 300 frames; each frame's x labels differ from the frame before's.
    /// </remarks>
    [Fact]
    public void NewLabels_DirectWrite_AllocateNothing()
    {
        var walk = new float[10_000];
        for (var i = 1; i < walk.Length; i++)
        {
            walk[i] = walk[i - 1] + (float)Math.Sin(i * 0.01);
        }

        using var pool = new PlotWorkerPool(2);
        using var y = Column.Wrap(walk);
        var table = DataTable.Create().Add("x", Column.Uniform(0, 1, walk.Length)).Add("y", y).Build();
        const int Frames = 300;
        const int Runs = 5;
        var specs = new PlotSpec[Frames * Runs];
        for (var i = 0; i < specs.Length; i++)
        {
            specs[i] = Plot.Create(table)
                .Line(table.Col<double>("x"), table.Col<float>("y"))
                .ScaleY(Scale.Linear().WithDomain(-5000, 5000))
                .ScaleX(Scale.Linear().WithDomain(i * 1370.0, (i * 1370.0) + 1000))
                .Build();
        }

        using var text = new DWriteTextBackend();
        using var backend = D3D11Backend.CreateForTest(useWarp: true, createDevice: null, new TextEngine(text));
        using var renderer = new HeadlessRenderer(backend, 1280, 720, pool);
        var bytes = -1L;
        var run = 0;
        for (; run < Runs - 1 && bytes != 0; run++)
        {
            bytes = Measure(renderer, specs.AsSpan(run * Frames, Frames));
        }

        Assert.Equal(0, bytes);
        var first = renderer.Plan!.Guides.X.TickLabels.ToArray();

        // An allocation that follows a collection is counted in every run (.cursor/rules/bench.mdc).
        GC.Collect();
        Assert.Equal(0, Measure(renderer, specs.AsSpan(run * Frames, Frames)));
        Assert.DoesNotContain(renderer.Plan!.Guides.X.TickLabels.ToArray()[0], first);
    }

    private static long Measure(HeadlessRenderer renderer, ReadOnlySpan<PlotSpec> specs)
    {
        var total = 0L;
        foreach (var spec in specs)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            renderer.Render(spec);
            total += GC.GetAllocatedBytesForCurrentThread() - before;
        }

        return total;
    }
}
