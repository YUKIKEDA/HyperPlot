using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using HyperPlot.Interaction;

namespace HyperPlot.Wpf.Tests;

/// <summary>
/// <see cref="HyperPlotView"/>'s summary for the accessibility API (design §7.2 "アクセシビリティ", §10 "アクセシビリティ
/// （W1c）", requirement N-10, row W1c, Issue #323): its automation peer, and when it tells that the summary changed. Each test
/// runs on the assembly's WPF thread, with a clock the test moves.
/// </summary>
[Collection(WpfThread.Collection)]
public sealed class AccessibilityTests(WpfThread wpf)
{
    /// <summary>
    /// The view's automation peer is an image whose name is the engine's summary, or the application's name when it sets one
    /// (design §7.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A view of a line plot in a window, drawn once.
    ///
    /// Steps:
    /// - Make the view's peer and read its type, class, and name; set AutomationProperties.Name to "Sales" and read the name.
    ///
    /// Expected:
    /// - Image, "HyperPlotView", and the engine's Describe(); then "Sales".
    /// </remarks>
    [Fact]
    public void Peer_IsAnImageNamedByTheSummary() =>
        wpf.Run(() =>
        {
            using var scene = new Scene();
            scene.Draw();
            var peer = UIElementAutomationPeer.CreatePeerForElement(scene.View);

            Assert.Equal(AutomationControlType.Image, peer.GetAutomationControlType());
            Assert.Equal(nameof(HyperPlotView), peer.GetClassName());
            Assert.Equal(scene.View.Engine.Describe(), peer.GetName());
            Assert.StartsWith("Plot: line layer.", peer.GetName(), StringComparison.Ordinal);

            AutomationProperties.SetName(scene.View, "Sales");
            Assert.Equal("Sales", peer.GetName());
        });

    /// <summary>
    /// A new spec's summary is told after the frame that draws it; a view's change is told once, 500 ms after the last
    /// change on the engine's clock, and not by the frames between; nothing is told without a listener (Issue #323
    /// Decisions 1, 6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A view of a line plot in a window, with a clock the test moves and a listener.
    ///
    /// Steps:
    /// - Draw. Set the view twice, 300 ms apart, drawing after each; move the clock 499 ms and pump; move it 1 ms more and
    ///   pump. Set a null spec; set the plot again and draw. Take the listener away; set the view; move the clock 500 ms and
    ///   pump.
    ///
    /// Expected:
    /// - 1 change told after the first frame. None more after the views, the frames, and the first 499 ms; a second once
    ///   500 ms passed since the last view. The null spec tells a third at once, with an empty name; the plot again a
    ///   fourth after its frame. None more without a listener.
    /// </remarks>
    [Fact]
    public void Changes_AreToldOnceSettled() =>
        wpf.Run(() =>
        {
            var clock = new ManualClock();
            using var scene = new Scene(clock, listener: true);
            scene.Draw();
            Assert.Equal(1, scene.View.NameChangesTold);

            scene.SetView(0, 400);
            scene.Draw();
            clock.Advance(TimeSpan.FromMilliseconds(300));
            WpfThread.Pump();
            scene.SetView(100, 300);
            scene.Draw();
            clock.Advance(TimeSpan.FromMilliseconds(499));
            WpfThread.Pump();
            Assert.Equal(1, scene.View.NameChangesTold);

            clock.Advance(TimeSpan.FromMilliseconds(1));
            WpfThread.Pump();
            Assert.Equal(2, scene.View.NameChangesTold);

            scene.View.Spec = null;
            Assert.Equal(3, scene.View.NameChangesTold);
            Assert.Equal(string.Empty, scene.View.AccessibleName());

            scene.View.Spec = scene.Plot;
            scene.Draw();
            Assert.Equal(4, scene.View.NameChangesTold);

            scene.View.ListenerExists = () => false;
            scene.SetView(200, 300);
            scene.Draw();
            clock.Advance(HyperPlotView.SettleTime);
            WpfThread.Pump();
            Assert.Equal(4, scene.View.NameChangesTold);
        });

    /// <summary>
    /// The end of a provisional frame tells the summary again, by the same 500 ms rule (Issue #323 Decisions 2, 6): the
    /// summary loses its "(provisional)".
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A view in a shown window, with a clock the test moves and a listener; then a line set over a Y whose chunk is held
    ///   on the workers (chunk 1 of two), so that the frames stay provisional until it is released.
    ///
    /// Steps:
    /// - Draw; release the chunk and draw until a frame is not provisional; move the clock 500 ms and pump.
    ///
    /// Expected:
    /// - The frames went provisional. After them, 1 change told; after the clock, 2, and the name has no "(provisional)".
    /// </remarks>
    [Fact]
    public void EndOfProvisional_IsToldOnceSettled() =>
        wpf.Run(() =>
        {
            var clock = new ManualClock();
            using var scene = new Scene(clock, listener: true, held: true);
            var changes = 0;
            scene.View.Engine.ProvisionalChanged += () => changes++;
            scene.Draw();
            Assert.True(scene.View.Engine.IsProvisional);
            scene.Release();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (scene.View.Engine.IsProvisional && DateTime.UtcNow < deadline)
            {
                scene.Draw();
            }

            Assert.False(scene.View.Engine.IsProvisional);
            Assert.True(changes >= 1, "the frames went provisional");
            Assert.Equal(1, scene.View.NameChangesTold);

            clock.Advance(HyperPlotView.SettleTime);
            WpfThread.Pump();
            Assert.Equal(2, scene.View.NameChangesTold);
            Assert.DoesNotContain("(provisional)", scene.View.AccessibleName(), StringComparison.Ordinal);
        });

    /// <summary>A window with a view of a line plot, shown, with the dispatcher's backend, the given clock, and a listener or none.</summary>
    private sealed class Scene : IDisposable
    {
        private readonly Column<float> _y;
        private readonly HeldStorage? _held;

        // A held Y's view runs on a pool of 1 thread, as the engine's tests of row W1b1 do: the held storage holds every read
        // on a worker, and with many workers a free one takes the frame's M4 job, whose read of the chunk's edge is held too
        // (Issue #328; real storage holds nothing).
        private readonly PlotWorkerPool? _pool;

        public Scene(TimeProvider? clock = null, bool listener = false, bool held = false)
        {
            // A held Y has two chunks, and only chunk 1 waits: chunk 0's pyramid is built, as in the engine's tests of row W1b1.
            var points = held ? 2 * Column.ChunkLength : 1000;
            var values = Enumerable.Range(0, points).Select(i => MathF.Sin(i * 0.02f) * 10).ToArray();
            _held = held ? new HeldStorage(values) : null;
            _pool = held ? new PlotWorkerPool(1) : null;
            _y = _held is { } storage ? new Column<float>(storage, points) : Column.Wrap(values);
            var table = DataTable.Create().Add("x", Column.Uniform(0, 1, points)).Add("y", _y).Build();

            // The listener is there before the window is shown: WPF's own tick may draw the first frame as it shows.
            // The dispatcher's backend, as the views of HyperPlotViewTests take: a backend of its own each made the device-loss
            // and shared-texture tests that follow fail in most runs (Issue #299).
            View = new HyperPlotView(HostResources.ForCurrentThread().Backend, clock, _pool) { ListenerExists = () => listener };
            Plot = HyperPlot.Plot.Create(table).Line(table.Col<double>("x"), table.Col<float>("y")).Build();
            View.Spec = Plot;
            Window = new Window
            {
                Width = 320,
                Height = 200,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Content = View,
            };
            Window.Show();
            WpfThread.Pump();
        }

        public HyperPlotView View { get; }

        public PlotSpec Plot { get; }

        public Window Window { get; }

        /// <summary>Draws the frame the engine asked for now, as WPF's tick does, pumping while a copy is pending.</summary>
        public void Draw()
        {
            var drawn = View.FramesDrawn;
            for (var i = 0; i < 100 && View.FramesDrawn == drawn; i++)
            {
                View.Render();
                WpfThread.Pump();
            }
        }

        public void SetView(double low, double high) =>
            View.Engine.Views.SetView(
                new PanelId(0, 0),
                new PanelView2D(ViewRange.FromValues(low, high), ViewRange.FromValues(-10.0, 10.0)),
                animate: false
            );

        /// <summary>Lets the workers read the held chunk.</summary>
        public void Release() => _held?.Release();

        public void Dispose()
        {
            // Released before the view: a pyramid's disposal waits for the chunk being built.
            Release();
            Window.Close();
            View.Dispose();
            _pool?.Dispose();
            _y.Dispose();
        }
    }

    /// <summary>A storage whose chunk 1 the pool's workers wait to read until it is released; other chunks and threads read at once.</summary>
    private sealed class HeldStorage(float[] values) : ColumnStorage<float>
    {
        private readonly ManualResetEventSlim _gate = new(initialState: false);

        public override ColumnStorageKind Kind => ColumnStorageKind.Managed;

        public void Release() => _gate.Set();

        public override ReadOnlySpan<float> GetChunk(int chunkIndex, int length)
        {
            if (chunkIndex == 1 && Thread.CurrentThread.Name?.StartsWith("HyperPlot worker", StringComparison.Ordinal) == true)
            {
                Assert.True(_gate.Wait(30_000), "The held chunk was not released within the watchdog.");
            }

            return values.AsSpan(chunkIndex * Column.ChunkLength, length);
        }

        public override void Dispose() => _gate.Set();
    }

    /// <summary>A clock the test moves (design §8.3): its timers fire on the test's thread when it passes their time.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private readonly List<Timer> _timers = [];
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(_ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            _timers.Add(timer);
            _ = timer.Change(dueTime, period);
            return timer;
        }

        /// <summary>Moves the clock forward and fires the timers whose time came.</summary>
        public void Advance(TimeSpan by)
        {
            _ticks += by.Ticks;
            foreach (var timer in _timers.ToArray())
            {
                timer.FireIfDue(_ticks);
            }
        }

        private sealed class Timer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private long? _due;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._ticks + dueTime.Ticks;
                return true;
            }

            public void FireIfDue(long now)
            {
                if (_due is { } due && due <= now)
                {
                    _due = null;
                    callback(state);
                }
            }

            public void Dispose() => clock._timers.Remove(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
