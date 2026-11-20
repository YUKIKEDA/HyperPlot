using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using HyperPlot.Interaction;
using HyperPlot.Rendering.D3D11;

namespace HyperPlot.E2E;

/// <summary>
/// The scene of the selection of scatter on the index path (design §9 "散布のヒットテストと選択（行 R3c）", requirement P-6): a
/// <see cref="PlotEngine"/> over one point layer of seeded points uniform over [0, 100]², the whole view, at
/// 1,920 × 1,080 on the hardware, a frame every 16.7 ms or more. Each round runs, in turn: the mouse alone moved by the
/// hover's script with no selection, and with a rectangle that holds every point; frames that each set another rectangle
/// that holds every point, and frames that each set another lasso of 256 vertices that holds every point (the retained
/// layer is drawn again); and frames that each change the view by half a data unit, under the lasso, under the rectangle,
/// and with no selection (the layer drawn again, cut by the region that stands). Each frame's CPU time (the input and <see cref="PlotEngine.RenderFrame"/>), its GPU time, and
/// its allocations. "points" draws the raw points; "density" draws the density, whose cells without a tile are fills that
/// the CPU cuts by the selection's region (a lasso's mask read a word at a time, Issue #576).
/// </summary>
internal static class SelectionScene
{
    private const int LassoVertices = 256;
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Period = TimeSpan.FromMilliseconds(1000.0 / 60);
    private static readonly PanelId Panel = new(0, 0);

    /// <summary>Runs the scene and prints its figures; returns the exit code.</summary>
    /// <param name="args">The count, "points" or "density", the frames measured a sequence (300 by default), and the rounds after the warm-up round (3 by default).</param>
    public static int Run(ReadOnlySpan<string> args)
    {
        const string Usage = "selection <count> <points|density> [frames] [rounds]";
        if (args.Length < 2 || args[1] is not ("points" or "density"))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var count = (int)double.Parse(args[0], CultureInfo.InvariantCulture);
        var draw = args[1] == "points" ? PointDraw.Points : PointDraw.Density;
        var frames = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 300;
        var rounds = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 3;

        var random = new Random(1);
        var (xs, ys) = (new double[count], new double[count]);
        for (var i = 0; i < count; i++)
        {
            (xs[i], ys[i]) = (random.NextDouble() * 100, random.NextDouble() * 100);
        }

        using var x = Column.Wrap(xs);
        using var y = Column.Wrap(ys);
        var table = DataTable.Create().Add("x", x).Add("y", y).Build();
        var plot = Plot.Create(table)
            .Point(
                table.Col<double>("x"),
                table.Col<double>("y"),
                p =>
                {
                    _ = p.Draw(draw).Size(2);
                }
            )
            .ScaleX(Scale.Linear().WithDomain(0, 100))
            .ScaleY(Scale.Linear().WithDomain(0, 100))
            .Build();

        using var backend = D3D11Backend.Create();
        using var engine = new PlotEngine(backend, 1920, 1080);
        _ = PointsScene.TimeBeginPeriod(1);
        try
        {
            var run = new Runner(engine, backend);
            var shown = Stopwatch.StartNew();
            engine.SetPlot(plot);
            run.Render();
            while (engine.IsProvisional)
            {
                if (shown.Elapsed > Deadline)
                {
                    Console.Error.WriteLine("The first display was still provisional at the deadline.");
                    return 1;
                }

                Thread.Sleep(1);
                run.Render();
            }

            var guide = HoverScene.Panel(engine);
            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"selection points={count} draw={draw} frames={frames} rounds={rounds} viewport=1920x1080 panel={guide.Width}x{guide.Height} ({(long)guide.Width * guide.Height} pixels) first display {shown.Elapsed.TotalSeconds:F2} s"
                )
            );
            if (!run.Calibrate())
            {
                Console.Error.WriteLine("The GPU's frame numbers did not match the frames drawn three times in a row.");
                return 1;
            }

            // Made before the frames, so that a frame's allocations are the engine's own. Each holds every point.
            Selection2D[] rectangles =
            [
                Selection2D.Rectangle(Panel, ViewRange.FromValues(-1.0, 101.0), ViewRange.FromValues(-1.0, 101.0)),
                Selection2D.Rectangle(Panel, ViewRange.FromValues(-1.0, 102.0), ViewRange.FromValues(-1.0, 101.0)),
            ];
            Selection2D[] lassos = [Lasso(80), Lasso(81)];
            PanelView2D[] views =
            [
                new(ViewRange.FromValues(-1.0, 101.0), ViewRange.FromValues(-1.0, 101.0)),
                new(ViewRange.FromValues(-1.5, 100.5), ViewRange.FromValues(-1.0, 101.0)),
            ];

            void Move(int f)
            {
                // The hover's script of row I2b: across the panel from left to right, up and down along a sine.
                var t = ((f % frames) + 0.5) / frames;
                var at = new Vector2(
                    (float)(guide.Left + (guide.Width * (0.05 + (0.9 * t)))),
                    (float)(guide.Top + (guide.Height * (0.5 + (0.35 * Math.Sin(t * 6 * Math.PI)))))
                );
                _ = engine.OnPointer(
                    new PointerEvent(
                        PointerEventKind.Move,
                        at,
                        PointerButtons.None,
                        PointerButtons.None,
                        ModifierKeys.None,
                        PointerKind.Mouse,
                        engine.Time.GetTimestamp()
                    )
                );
            }

            // Round 0 warms: the pipelines, the tooltips' texts, the regions' buffers.
            for (var round = 0; round <= rounds; round++)
            {
                engine.ClearSelection();
                engine.Views.SetView(Panel, views[0]);
                run.Sequence("hover-none", round, frames, frames, Move);
                engine.Select(rectangles[0]);
                run.Sequence("hover-selected", round, frames, frames, Move);
                run.Sequence("change-rectangle", round, 30, frames, f => engine.Select(rectangles[f % 2]));
                run.Sequence("change-lasso", round, 30, frames, f => engine.Select(lassos[f % 2]));
                engine.Select(lassos[0]);
                run.Sequence("view-under-lasso", round, 30, frames, f => engine.Views.SetView(Panel, views[f % 2]));
                engine.Select(rectangles[0]);
                run.Sequence("view-under-rectangle", round, 30, frames, f => engine.Views.SetView(Panel, views[f % 2]));
                engine.ClearSelection();
                run.Sequence("change-view", round, 30, frames, f => engine.Views.SetView(Panel, views[f % 2]));
            }

            using var process = Process.GetCurrentProcess();
            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"memory: working set {process.WorkingSet64 / 1048576.0:F0} MB, private {process.PrivateMemorySize64 / 1048576.0:F0} MB"
                )
            );
            return 0;
        }
        finally
        {
            _ = PointsScene.TimeEndPeriod(1);
        }
    }

    /// <summary>Returns a lasso of <see cref="LassoVertices"/> vertices on a circle around the middle of the data: a radius of 71 or more holds [0, 100]².</summary>
    private static Selection2D Lasso(double radius)
    {
        var polygon = new Vector2[LassoVertices];
        for (var k = 0; k < polygon.Length; k++)
        {
            var angle = 2 * Math.PI * k / polygon.Length;
            polygon[k] = new Vector2((float)(0.5 + (0.5 * Math.Cos(angle))), (float)(0.5 + (0.5 * Math.Sin(angle))));
        }

        return Selection2D.Lasso(Panel, ViewRange.FromValues(50 - radius, 50 + radius), ViewRange.FromValues(50 - radius, 50 + radius), polygon);
    }

    /// <summary>Draws the frames, counts them, and gives each GPU time to the frame it timed.</summary>
    private sealed class Runner(PlotEngine engine, D3D11Backend backend)
    {
        // The frames drawn through Render, and the GPU's number of a frame less its number here.
        private long _rendered;
        private long _offset;

        public void Render()
        {
            engine.RenderFrame();
            _rendered++;
        }

        /// <summary>
        /// Finds the GPU's number of the frames drawn here: a frame, a wait long enough for the GPU to end it, and a second
        /// frame, whose end reads the first one's time. Returns whether three such pairs agree.
        /// </summary>
        public bool Calibrate()
        {
            var offsets = new long[3];
            for (var k = 0; k < offsets.Length; k++)
            {
                Render();
                var number = _rendered - 1;
                Thread.Sleep(100);
                Render();
                if (backend.LastFrameTime is not { } time)
                {
                    return false;
                }

                offsets[k] = time.Frame - number;
            }

            _offset = offsets[0];
            return offsets[1] == offsets[0] && offsets[2] == offsets[0];
        }

        /// <summary>
        /// Runs a sequence: the input of each frame and the frame, a period apart; prints the times of the frames after the
        /// warm-up ones, the frames whose GPU time came, the allocations, what the last frame recorded, and the cores the other
        /// processes used.
        /// </summary>
        public void Sequence(string name, int round, int warm, int frames, Action<int> input)
        {
            var total = warm + frames;
            var cpu = new double[total];
            var gpu = new double[total];
            Array.Fill(gpu, double.NaN);
            var allocated = new long[total];
            var first = _rendered;
            var from = DateTime.Now;
            var mark = MachineLoad.Now();
            for (var f = 0; f < total; f++)
            {
                var bytes = GC.GetAllocatedBytesForCurrentThread();
                var start = Stopwatch.GetTimestamp();
                input(f);
                Render();
                cpu[f] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                allocated[f] = GC.GetAllocatedBytesForCurrentThread() - bytes;
                Take(gpu, first);
                Pace(start);
            }

            var (others, own) = MachineLoad.CoresSince(mark);
            var to = DateTime.Now;
            var list = engine.Plan!.List;
            var (rects, images, runs, regions) = (list.Rects.Length, list.Images.Length, list.ResidentRuns.Length, list.Highlights.Length);

            // The last frames' times come a few frames later: frames with no input, not measured.
            for (var k = 0; k < 4; k++)
            {
                var start = Stopwatch.GetTimestamp();
                Render();
                Take(gpu, first);
                Pace(start);
            }

            var kept = cpu[warm..];
            var timed = Enumerable.Range(warm, frames).Where(f => !double.IsNaN(gpu[f])).ToArray();
            var gpuKept = timed.Select(f => gpu[f]).ToArray();
            var sums = timed.Select(f => cpu[f] + gpu[f]).ToArray();
            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"sequence={name} round={round}{(round == 0 ? " (warm-up)" : string.Empty)} frames={frames} (after {warm}) ran {from:HH:mm:ss.fff} to {to:HH:mm:ss.fff}: cpu_ms p50={P(kept, 0.5):F3} p95={P(kept, 0.95):F3} p99={P(kept, 0.99):F3} max={kept.Max():F3}; gpu_ms timed={gpuKept.Length} of {frames} p50={P(gpuKept, 0.5):F3} p95={P(gpuKept, 0.95):F3} p99={P(gpuKept, 0.99):F3} max={(gpuKept.Length == 0 ? double.NaN : gpuKept.Max()):F3}; cpu+gpu of a frame p95={P(sums, 0.95):F3}; allocated={allocated[warm..].Sum()} B in {allocated[warm..].Count(b => b > 0)} frames; last frame recorded rects={rects} images={images} resident_runs={runs} regions={regions}; other processes {others:F1} cores, this process {own:F1} cores"
                )
            );

            static double P(double[] values, double share) => PointsScene.Percentile(values, share);
        }

        // One time at most is read a frame; it goes to the frame it timed when that frame is of this sequence.
        private void Take(double[] gpu, long first)
        {
            if (backend.LastFrameTime is { } time)
            {
                var frame = time.Frame - _offset - first;
                if (frame >= 0 && frame < gpu.Length)
                {
                    gpu[frame] = time.Duration.TotalMilliseconds;
                }
            }
        }

        private static void Pace(long start)
        {
            // A frame a period at the fastest: back to back, the GPU's clock rises and its time reads low (.cursor/rules/bench.mdc).
            var left = Period - Stopwatch.GetElapsedTime(start);
            if (left > TimeSpan.Zero)
            {
                Thread.Sleep(left);
            }
        }
    }
}
