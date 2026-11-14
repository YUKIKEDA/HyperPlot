using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using HyperPlot.Interaction;
using HyperPlot.Rendering;
using HyperPlot.Rendering.D3D11;

namespace HyperPlot.E2E;

/// <summary>
/// The scene of the wheel after a hover (PR #511, the review of Issue #378's fix): a <see cref="PlotEngine"/> over a line,
/// a title, two axis titles, and optionally a text layer of long data labels, at 1,920 × 1,080 on the hardware, its shaping
/// cache full of short texts first. Each cycle moves the pointer over the panel for a number of frames (each a new tooltip,
/// the plot kept) and then turns the wheel once (in and out in turn), a frame every 16.7 ms or more. The CPU time and the
/// allocations of the wheel's frame (the wheel event and <see cref="PlotEngine.RenderFrame"/>) and of the hover's frames.
/// </summary>
internal static partial class WheelAfterHoverScene
{
    private const int Warmup = 10;

    /// <summary>Runs the scene and prints its figures; returns the exit code.</summary>
    /// <param name="args">
    /// The data labels (0 for none; each over 16 characters), the hover frames before each wheel frame (0 for wheel frames
    /// back to back under the hover of one move), and the cycles measured after 10 to warm (100 by default).
    /// </param>
    public static int Run(ReadOnlySpan<string> args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("wheel-after-hover <labels> <hover frames> [cycles]");
            return 2;
        }

        var labels = int.Parse(args[0], CultureInfo.InvariantCulture);
        var hovers = int.Parse(args[1], CultureInfo.InvariantCulture);
        var cycles = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 100;
        var rows = labels > 0 ? labels : 1000;
        using var x = Column.Wrap(Enumerable.Range(0, rows).Select(i => i * 100.0 / rows).ToArray());
        using var y = Column.Wrap(Enumerable.Range(0, rows).Select(i => 50 + (Math.Sin(i * 6 * Math.PI / rows) * 30)).ToArray());
        var builder = DataTable.Create().Add("x", x).Add("y", y);
        if (labels > 0)
        {
            var names = Enumerable
                .Range(0, rows)
                .Select(i => (string?)string.Create(CultureInfo.InvariantCulture, $"a data label of row {i}"))
                .ToArray();
            builder = builder.Add("label", Column.Categorical(names));
        }

        var table = builder.Build();
        var plot = Plot.Create(table)
            .Title("Wheel frames after a hover")
            .Line(table.Col<double>("x"), table.Col<double>("y"))
            .ScaleX(Scale.Linear().WithDomain(0, 100) with { Title = "the x axis, in seconds" })
            .ScaleY(Scale.Linear().WithDomain(0, 100) with { Title = "the y axis, in metres" });
        if (labels > 0)
        {
            plot = plot.Text(table.Col<double>("x"), table.Col<double>("y"), table.Cat("label"));
        }

        using var backend = D3D11Backend.Create();

        // The cache full of short texts before the plot's first frame, as the tests of Issue #378 fill it: under PR #511's
        // first fix the plot's long texts then held the only grown entries, the case where a hover made a whole frame shape
        // them all again. PR #511's body has the command that ran that fix's tree beside main and the head.
        var filler = new TextStyle(new Color(0, 0, 0, 255), 9);
        for (var i = 0; i < TextEngine.CacheCapacity; i++)
        {
            _ = backend.Text.Measure(i.ToString(CultureInfo.InvariantCulture), filler);
        }

        using var engine = new PlotEngine(backend, 1920, 1080);
        engine.Options.ZoomAnimation = TimeSpan.Zero;
        engine.SetPlot(plot.Build());
        engine.RenderFrame();
        var guide = HoverScene.Panel(engine);
        var middle = new Vector2(guide.Left + (guide.Width / 2f), guide.Top + (guide.Height / 2f));

        // A hover from the start, so that the wheel frames of 0 hover frames also draw a tooltip.
        _ = engine.OnPointer(Move(engine, middle));
        engine.RenderFrame();

        var total = cycles + Warmup;
        var wheelCpu = new double[total];
        var wheelBytes = new long[total];
        var hoverCpu = new List<double>(cycles * hovers);
        long hoverBytes = 0;
        var step = 0;
        var period = TimeSpan.FromMilliseconds(1000.0 / 60);

        // A wait of a few milliseconds needs the timer's finer step (.cursor/rules/bench.mdc).
        _ = TimeBeginPeriod(1);
        for (var c = 0; c < total; c++)
        {
            for (var h = 0; h < hovers; h++)
            {
                // 3 plot-area pixels a frame, from left to right and again: at 1,000 rows a pixel next to the last may name
                // the same row, and its tooltip would be no new text.
                var across = (step++ * 3) % (int)(guide.Width * 0.9);
                var at = new Vector2(guide.Left + (guide.Width * 0.05f) + across, middle.Y);
                var bytes = GC.GetAllocatedBytesForCurrentThread();
                var start = Stopwatch.GetTimestamp();
                _ = engine.OnPointer(Move(engine, at));
                engine.RenderFrame();
                var elapsed = Stopwatch.GetElapsedTime(start);
                if (c >= Warmup)
                {
                    hoverCpu.Add(elapsed.TotalMilliseconds);
                    hoverBytes += GC.GetAllocatedBytesForCurrentThread() - bytes;
                }

                Wait(period - elapsed);
            }

            var wheelStartBytes = GC.GetAllocatedBytesForCurrentThread();
            var wheelStart = Stopwatch.GetTimestamp();
            _ = engine.OnWheel(new WheelEvent(middle, 0, c % 2 == 0 ? 1 : -1, ModifierKeys.None, engine.Time.GetTimestamp()));
            engine.RenderFrame();
            var wheelElapsed = Stopwatch.GetElapsedTime(wheelStart);
            wheelCpu[c] = wheelElapsed.TotalMilliseconds;
            wheelBytes[c] = GC.GetAllocatedBytesForCurrentThread() - wheelStartBytes;
            Wait(period - wheelElapsed);
        }

        _ = TimeEndPeriod(1);
        var wheel = wheelCpu.Skip(Warmup).ToArray();
        var hover = hoverCpu.ToArray();
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"wheel-after-hover labels={labels} hovers={hovers} cycles={cycles} (after {Warmup}) cache={backend.Text.CacheCount}"
            )
        );
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"wheel_cpu: p50={Percentile(wheel, 0.5):F3} p95={Percentile(wheel, 0.95):F3} max={wheel.Max():F3} ms"
            )
        );
        if (hover.Length > 0)
        {
            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"hover_cpu: p50={Percentile(hover, 0.5):F3} p95={Percentile(hover, 0.95):F3} max={hover.Max():F3} ms"
                )
            );
        }

        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"allocated after warm-up: wheel {wheelBytes.Skip(Warmup).Sum()} bytes over {cycles} frames; hover {hoverBytes} bytes over {hover.Length} frames"
            )
        );
        return 0;
    }

    private static PointerEvent Move(PlotEngine engine, Vector2 at) =>
        new(PointerEventKind.Move, at, PointerButtons.None, PointerButtons.None, ModifierKeys.None, PointerKind.Mouse, engine.Time.GetTimestamp());

    // A frame a period at the fastest: back to back, the GPU's clock rises, and a hover comes at the pointer's rate.
    private static void Wait(TimeSpan left)
    {
        if (left > TimeSpan.Zero)
        {
            Thread.Sleep(left);
        }
    }

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint period);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static partial uint TimeEndPeriod(uint period);

    private static double Percentile(double[] values, double share)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? double.NaN : sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * share))];
    }
}
