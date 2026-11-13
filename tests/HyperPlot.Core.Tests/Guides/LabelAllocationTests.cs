using System.Globalization;
using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>
/// The tick labels' characters (design §4.6, row I1c): written into the axis's buffer with no string, so that new labels
/// while zooming allocate nothing once the buffers and the shaping cache have grown.
/// </summary>
[Collection("Large data")]
public sealed class LabelAllocationTests
{
    private const int Frames = 300;
    private const int Runs = 5;
    private const int Length = 10_000;

    /// <summary>
    /// An axis whose labels are longer together than its buffer's first size grows the buffer and keeps every label (design
    /// §4.6, row I1c).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A linear axis over [0, 10] of 800 pixels, labels written by a format of 40 characters each ("v" and the value padded
    ///   with dots), more than the buffer's first 256 characters over the ticks.
    ///
    /// Steps:
    /// - Choose the ticks; choose them again over [100, 110].
    ///
    /// Expected:
    /// - Each label is its value's 40 characters, the second time too.
    /// </remarks>
    [Fact]
    public void Labels_LongerThanTheBuffer_KeepEachLabel()
    {
        static bool Format(double value, Span<char> destination, out int written)
        {
            var text = ("v" + value.ToString(CultureInfo.InvariantCulture)).PadRight(40, '.');
            text.CopyTo(destination);
            written = text.Length;
            return true;
        }

        foreach (var (low, high) in new[] { (0.0, 10.0), (100.0, 110.0) })
        {
            var axis = new TickAxis
            {
                Scale = Scale.Linear() with { TickFormat = Format },
                Domain = ScaleRange.FromDouble(low, high),
                Length = 4000,
                Spacing = 200,
                Horizontal = true,
                LineHeight = FakeTextBackend.LineHeight(12),
                Em = 12,
                Style = new TextStyle(new Color(0, 0, 0, 255), 12),
                Text = new TextEngine(new FakeTextBackend()),
            };
            axis.Choose(nice: false);
            Assert.True(axis.Count * 40 > 256, $"{axis.Count} ticks");
            for (var i = 0; i < axis.Count; i++)
            {
                Assert.Equal(("v" + axis.NumberAt(i).Value.ToString(CultureInfo.InvariantCulture)).PadRight(40, '.'), axis.TickLabels[i].ToString());
            }
        }
    }

    /// <summary>
    /// A plot whose x domain moves each frame, so that every x label is new, allocates nothing per frame once the buffers and
    /// the shaping cache have grown (design §4.6, §5.6, row I1c): numeric and time axes, the default formats and a user's.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A line of 10,000 points; the fake text backend, recording no calls, its shaping cache filled with 8,192 other texts;
    ///   1,280 × 720. The x scale is linear, linear with a user's format, or time in ticks; its domains lie 1,370 units
    ///   (4,570 s for time) apart, each made beforehand.
    ///
    /// Steps:
    /// - Render the domains in turn until a run of 300 frames allocates nothing (at most 4 runs: the buffers grow and the
    ///   entries take their largest texts); collect; then render 300 more domains, none drawn before.
    ///
    /// Expected:
    /// - 0 bytes over the 300 frames; the last frame's first x label is not among the labels of the warm-up's last frame.
    /// </remarks>
    /// <param name="kind">The x scale: "linear", "format", or "time".</param>
    [Theory]
    [InlineData("linear")]
    [InlineData("format")]
    [InlineData("time")]
    public void NewLabels_AllocateNothing(string kind)
    {
        var values = new float[Length];
        for (var i = 1; i < values.Length; i++)
        {
            values[i] = values[i - 1] + MathF.Sin(i * 0.01f);
        }

        using var pool = new PlotWorkerPool(2);
        using var y = Column.Wrap(values);
        var time = kind == "time";
        var start = new DateTime(2026, 1, 1).Ticks;
        var table = time
            ? DataTable.Create().Add("x", Column.UniformInteger(start, TimeSpan.TicksPerSecond, Length)).Add("y", y).Build()
            : DataTable.Create().Add("x", Column.Uniform(0, 1, Length)).Add("y", y).Build();
        var specs = new PlotSpec[Frames * Runs];
        for (var i = 0; i < specs.Length; i++)
        {
            var builder = Plot.Create(table).ScaleY(Scale.Linear().WithDomain(-5000, 5000));
            builder = time
                ? builder
                    .Line(table.Col<long>("x"), table.Col<float>("y"))
                    .ScaleX(
                        Scale
                            .Time(TimeUnit.Ticks)
                            .WithDomain(start + (i * 4570 * TimeSpan.TicksPerSecond), start + (((i * 4570) + 1000) * TimeSpan.TicksPerSecond))
                    )
                : builder
                    .Line(table.Col<double>("x"), table.Col<float>("y"))
                    .ScaleX(
                        kind == "format"
                            ? Scale.Linear().WithDomain(i * 1370.0, (i * 1370.0) + 1000) with
                            {
                                TickFormat = Hash,
                            }
                            : Scale.Linear().WithDomain(i * 1370.0, (i * 1370.0) + 1000)
                    );
            specs[i] = builder.Build();
        }

        // The shaping cache full first, as after a long session: a time axis's labels ("HH:mm") repeat, so its runs alone
        // would take a new entry for each new date until the cache is full.
        var engine = new TextEngine(new FakeTextBackend { RecordShapes = false });
        for (var i = 0; i < TextEngine.CacheCapacity; i++)
        {
            _ = engine.Measure("filler " + i.ToString(CultureInfo.InvariantCulture), new TextStyle(new Color(0, 0, 0, 255), 9));
        }

        using var plan = new PlotPlan(pool, 1280, 720, engine, 1);
        var bytes = -1L;
        var run = 0;
        for (; run < Runs - 1 && bytes != 0; run++)
        {
            bytes = Measure(plan, specs.AsSpan(run * Frames, Frames));
        }

        Assert.Equal(0, bytes);
        var warm = plan.Guides.X.TickLabels.ToArray();

        // An allocation that follows a collection is counted in every run (.cursor/rules/bench.mdc).
        GC.Collect();
        Assert.Equal(0, Measure(plan, specs.AsSpan(run * Frames, Frames)));
        Assert.DoesNotContain(plan.Guides.X.TickLabels.ToArray()[0], warm);
    }

    /// <summary>A user's format: "#" and the value.</summary>
    private static bool Hash(double value, Span<char> destination, out int written)
    {
        destination[0] = '#';
        var done = value.TryFormat(destination[1..], out written, default, CultureInfo.InvariantCulture);
        written++;
        return done;
    }

    private static long Measure(PlotPlan plan, ReadOnlySpan<PlotSpec> specs)
    {
        var total = 0L;
        foreach (var spec in specs)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            _ = plan.Render(spec);
            total += GC.GetAllocatedBytesForCurrentThread() - before;
        }

        return total;
    }
}
