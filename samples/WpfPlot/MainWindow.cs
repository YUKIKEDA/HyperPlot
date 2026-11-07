using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HyperPlot.Interaction;
using HyperPlot.Rendering;
using HyperPlot.Rendering.D3D11;
using HyperPlot.Wpf;

namespace HyperPlot.Samples;

/// <summary>The sample's window: the time series, facet, and figure tabs, or the measurements of design §9.</summary>
internal sealed class MainWindow : Window, IDisposable
{
    private const int Frames = 330;
    private const int Warmup = 30;

    private readonly HyperPlotView _series = new();
    private readonly List<IDisposable> _columns = [];

    public MainWindow(int points, bool measure)
    {
        var title = $"WpfPlot: {points:N0} points a series";
        Title = title;
        Width = 1200;
        Height = 760;

        // The host's notice (design §4.8, row W1b1): while the pyramids build, the frames are provisional, and the title says so.
        _series.Engine.ProvisionalChanged += () => Title = _series.Engine.IsProvisional ? $"{title} (provisional)" : title;
        _series.Spec = TimeSeries(points);

        // A WPF button over the plot (requirement H-2): it resets every view through the engine's operations.
        var reset = new Button
        {
            Content = "Reset views",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 8, 0),
            Padding = new Thickness(8, 2, 8, 2),
        };
        reset.Click += (_, _) => _series.Engine.Views.ResetAll();
        var seriesTab = new Grid();
        _ = seriesTab.Children.Add(_series);
        _ = seriesTab.Children.Add(reset);

        var tabs = new TabControl();
        _ = tabs.Items.Add(new TabItem { Header = "Time series", Content = seriesTab });
        _ = tabs.Items.Add(
            new TabItem
            {
                Header = "Facet",
                Content = new HyperPlotView { Spec = Facet() },
            }
        );
        _ = tabs.Items.Add(
            new TabItem
            {
                Header = "Figure",
                Content = new HyperPlotView { Spec = FigureOfThree() },
            }
        );
        Content = tabs;
        if (measure)
        {
            Loaded += (_, _) => _ = Dispatcher.InvokeAsync(Measure, DispatcherPriority.ApplicationIdle);
        }

        Closed += (_, _) => Dispose();
    }

    /// <summary>Releases the time series' view and the columns.</summary>
    public void Dispose()
    {
        _series.Dispose();
        foreach (var column in _columns)
        {
            column.Dispose();
        }

        _columns.Clear();
    }

    private PlotSpec TimeSeries(int points)
    {
        // Two seeded sines with noise from a hash of the index, made in parallel; one point a millisecond from 2026-01-01.
        var first = new float[points];
        var second = new float[points];
        Parallel.For(
            0,
            points,
            i =>
            {
                var t = i / (double)points;
                first[i] = (float)((Math.Sin(t * 40 * Math.PI) * 2.5) + Noise(i));
                second[i] = (float)((Math.Cos(t * 9 * Math.PI) * 1.5) + 1 + (Noise(i + points) * 0.5));
            }
        );
        var (ch1, ch2) = (Column.Wrap(first), Column.Wrap(second));
        _columns.Add(ch1);
        _columns.Add(ch2);
        var start = (new DateTime(2026, 1, 1) - DateTime.UnixEpoch).Ticks * 100;
        var table = DataTable
            .Create()
            .Add("t", Column.UniformInteger(start: start, step: 1_000_000, length: points))
            .Add("ch1", ch1)
            .Add("ch2", ch2)
            .Build();
        var time = table.Col<long>("t");
        return Plot.Create(table)
            .Line(time, table.Col<float>("ch1"), l => l.Color(new Color(31, 119, 180, 255)).Label("ch1"))
            .Line(time, table.Col<float>("ch2"), l => l.Color(new Color(255, 127, 14, 255)).Label("ch2"))
            .ScaleX(Scale.Time(TimeUnit.Nanoseconds).WithTitle("Time"))
            .ScaleY(Scale.Linear().WithTitle("Value"))
            .Title($"Two series of {points:N0} points")
            .Build();
    }

    private PlotSpec Facet()
    {
        // Four groups of 100,000 points, wrapped in two columns; each group's own y.
        const int Each = 100_000;
        var names = new[] { "north", "east", "south", "west" };
        var values = new float[Each * 4];
        var positions = new double[Each * 4];
        var groups = new string[Each * 4];
        for (var i = 0; i < values.Length; i++)
        {
            var g = i / Each;
            positions[i] = i % Each;
            values[i] = (float)((Math.Sin((i % Each) * 0.0005 * (g + 1)) * (g + 1)) + (Noise(i) * 0.3));
            groups[i] = names[g];
        }

        // Each group's rows run over the same x, from 0, so that the shared x lines them up.
        var (x, y) = (Column.Wrap(positions), Column.Wrap(values));
        _columns.Add(x);
        _columns.Add(y);
        var table = DataTable.Create().Add("x", x).Add("y", y).Add("group", Column.Categorical(groups, names)).Build();
        return Plot.Create(table)
            .Line(table.Col<double>("x"), table.Col<float>("y"))
            .FacetWrap(table.Cat("group"), columns: 2, scales: FacetScales.FreeY)
            .Title("A facet: four groups, a shared x")
            .Build();
    }

    private PlotSpec[] Cells()
    {
        var a = new float[50_000];
        var b = new float[50_000];
        for (var i = 0; i < a.Length; i++)
        {
            a[i] = (float)Math.Sin(i * 0.001);
            b[i] = (float)(Math.Cos(i * 0.0007) + (Noise(i) * 0.2));
        }

        var (ya, yb) = (Column.Wrap(a), Column.Wrap(b));
        _columns.Add(ya);
        _columns.Add(yb);
        var table = DataTable.Create().Add("x", Column.Uniform(0, 1, a.Length)).Add("a", ya).Add("b", yb).Build();
        var x = table.Col<double>("x");
        return
        [
            Plot.Create(table).Line(x, table.Col<float>("a")).Title("a").Build(),
            Plot.Create(table).Line(x, table.Col<float>("b")).Title("b").Build(),
            Plot.Create(table).Area(x, table.Col<float>("a")).Title("a as an area").Build(),
        ];
    }

    private FigureSpec FigureOfThree() => Figure.Concat(2, Cells()) with { Title = "A figure of three plots" };

    /// <summary>A value in [−0.5, 0.5) from a hash of an index: the same noise on every run and every thread.</summary>
    private static double Noise(long i)
    {
        var h = (ulong)i * 0x9E3779B97F4A7C15UL;
        h ^= h >> 31;
        h *= 0xBF58476D1CE4E5B9UL;
        h ^= h >> 29;
        return ((h >> 11) / (double)(1UL << 53)) - 0.5;
    }

    /// <summary>
    /// The measurements of design §9 "WPF のホスト", printed, each over the time series drawn at 1,920 × 1,080 (a real frame,
    /// not a cleared one, which a GPU may compress): the GPU time of the copy into a shared texture (the default adapter and
    /// WARP); the CPU time of a read back; the engine's frame alone and the copy with its take alone, panned every frame; and
    /// the CPU time of the view's tick while the time series pans every frame. Each sample says whether a collection ran
    /// during it. Then the window closes.
    /// </summary>
    private void Measure()
    {
        Console.WriteLine($"Process: {(Environment.Is64BitProcess ? "x64" : "x86")}, .NET {Environment.Version}, JIT");
        var spec = (PlotSpec)_series.Spec!;
        foreach (var warp in new[] { false, true })
        {
            using var backend = D3D11Backend.Create(useWarp: warp);
            using var engine = new PlotEngine(backend, 1920, 1080);
            engine.Options.ZoomAnimation = TimeSpan.Zero;
            engine.SetPlot(spec);
            engine.RenderFrame();
            using var shared = new D3D11SharedSurface(backend, 1920, 1080);
            var gpu = new List<Sample>();
            var seen = -1L;
            for (var i = 0; i < Frames + 60 && gpu.Count < Frames; i++)
            {
                var collections = GC.CollectionCount(0);
                using (backend.BeginFrame())
                {
                    shared.Present(engine.Target);
                }

                while (!shared.TryTakeCompleted(out _))
                {
                    _ = Thread.Yield();
                }

                if (backend.LastFrameTime is { } time && time.Frame != seen)
                {
                    seen = time.Frame;
                    gpu.Add(new(time.Duration.TotalMilliseconds, GC.CollectionCount(0) != collections));
                }
            }

            using var readback = new D3D11ReadbackSurface(backend, 1920, 1080);
            var buffer = new byte[1920 * 1080 * 4];
            var cpu = new List<Sample>();
            for (var i = 0; i < Frames; i++)
            {
                var collections = GC.CollectionCount(0);
                var clock = Stopwatch.StartNew();
                readback.Present(engine.Target);
                var presented = clock.Elapsed.TotalMilliseconds;
                while (true)
                {
                    clock.Restart();
                    if (readback.TryRead(buffer, 1920 * 4))
                    {
                        cpu.Add(new(presented + clock.Elapsed.TotalMilliseconds, GC.CollectionCount(0) != collections));
                        break;
                    }
                }
            }

            var device = warp ? "WARP" : "default adapter";
            Console.WriteLine($"Copy to a shared texture, GPU ({device}): {Stats(gpu)}");
            Console.WriteLine($"Read back, CPU of Present and the TryRead that reads ({device}): {Stats(cpu)}");
            if (warp)
            {
                continue;
            }

            // The parts of a host's frame without WPF: the engine's frame, and the copy with the take that finds it done.
            var frames = new List<Sample>();
            var copies = new List<Sample>();
            for (var i = 0; i < Frames; i++)
            {
                engine.Views.Pan(new PanelId(0, 0), new System.Numerics.Vector2(i % 2 == 0 ? 1 : -1, 0));
                var collections = GC.CollectionCount(0);
                var clock = Stopwatch.StartNew();
                using (backend.BeginFrame())
                {
                    engine.RenderFrame();
                    var drawn = clock.Elapsed.TotalMilliseconds;
                    frames.Add(new(drawn, GC.CollectionCount(0) != collections));
                    clock.Restart();
                    shared.Present(engine.Target);
                }

                var presented = clock.Elapsed.TotalMilliseconds;
                while (!shared.TryTakeCompleted(out _))
                {
                    _ = Thread.Yield();
                }

                clock.Restart();
                _ = shared.TryTakeCompleted(out _);
                copies.Add(new(presented + clock.Elapsed.TotalMilliseconds, GC.CollectionCount(0) != collections));
            }

            Console.WriteLine($"Engine frame alone, CPU ({device}, panned every frame): {Stats(frames)}");
            Console.WriteLine($"Present and a take, CPU ({device}): {Stats(copies)}");
        }

        // The view's tick: our handler runs first each tick and pans; the view's handler, added after it, draws; an operation
        // at Send priority runs when the tick's handlers are done.
        var dpi = VisualTreeHelper.GetDpi(_series);
        _series.Width = 1920 / dpi.DpiScaleX;
        _series.Height = 1080 / dpi.DpiScaleY;
        var ticks = new List<Sample>();
        var start = 0L;
        var gen0 = 0;
        var step = 1;
        void OnTick(object? sender, EventArgs e)
        {
            start = Stopwatch.GetTimestamp();
            gen0 = GC.CollectionCount(0);
            _series.Engine.Views.Pan(new PanelId(0, 0), new System.Numerics.Vector2(step, 0));
            step = -step;
            _ = Dispatcher.InvokeAsync(
                () =>
                {
                    ticks.Add(new(Stopwatch.GetElapsedTime(start).TotalMilliseconds, GC.CollectionCount(0) != gen0));
                    if (ticks.Count == Frames)
                    {
                        CompositionTarget.Rendering -= OnTick;
                        Console.WriteLine(
                            $"View tick, CPU ({_series.Engine.Width} x {_series.Engine.Height}, the time series panned every frame): {Stats(ticks)}"
                        );
                        Close();
                    }
                },
                DispatcherPriority.Send
            );
        }

        CompositionTarget.Rendering += OnTick;
    }

    private static string Stats(List<Sample> values)
    {
        var kept = values.Skip(Warmup).ToArray();
        if (kept.Length == 0)
        {
            return "no samples";
        }

        var sorted = kept.Select(v => v.Milliseconds).Order().ToArray();
        double At(double q) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(q * sorted.Length) - 1)];
        var collected = kept.Where(v => v.Collected).Select(v => v.Milliseconds).ToArray();
        var quiet = kept.Where(v => !v.Collected).Select(v => v.Milliseconds).DefaultIfEmpty().Max();
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"n={sorted.Length}, p50={At(0.5):F3} ms, p95={At(0.95):F3} ms, max={sorted[^1]:F3} ms; with a GC: {collected.Length} (max {collected.DefaultIfEmpty().Max():F3} ms), max without: {quiet:F3} ms"
        );
    }

    /// <summary>A time, and whether a gen-0 collection ran during it.</summary>
    private readonly record struct Sample(double Milliseconds, bool Collected);
}
