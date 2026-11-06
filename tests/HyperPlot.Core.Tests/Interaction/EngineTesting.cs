using System.Numerics;
using HyperPlot.Interaction;
using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>A time the tests move by hand (design §8.3): it starts at 0 and moves only when told.</summary>
internal sealed class ManualTime : TimeProvider
{
    private long _ticks;

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>Moves the time forward.</summary>
    public void Advance(TimeSpan by) => _ticks += by.Ticks;

    /// <inheritdoc />
    public override long GetTimestamp() => _ticks;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(_ticks);
}

/// <summary>A backend that draws nothing and keeps the lists of the last frame (design §6.6): no GPU, so Linux runs it.</summary>
internal sealed class NullBackend : IGpuBackend
{
    private long _frame;

    public bool IsWarp => true;

    public GpuFrameTime? LastFrameTime => null;

    public TextEngine Text { get; } = new(new FakeTextBackend { RecordShapes = false });

    public ResidencyCache Images { get; } = new();

    /// <summary>Gets the frames begun.</summary>
    public int Frames { get; private set; }

    /// <summary>Gets the number of lists drawn in the last frame.</summary>
    public int DrawsInFrame { get; private set; }

    /// <summary>Gets the last list drawn: the overlay when one is drawn.</summary>
    public RenderList? LastList { get; private set; }

    public IRenderTarget CreateRenderTarget(in RenderTargetDescription description) => new Target(description);

    public FrameScope BeginFrame()
    {
        Frames++;
        DrawsInFrame = 0;
        return new(this, ++_frame);
    }

    public void Draw(IRenderTarget target, RenderList list)
    {
        DrawsInFrame++;
        LastList = list;
    }

    public void EndFrame(long token) { }

    public void Dispose() { }

    private sealed class Target(RenderTargetDescription description) : IRenderTarget
    {
        public RenderTargetDescription Description => description;

        public void Clear(Color color) { }

        public void ReadPixels(Span<byte> destination) { }

        public void Dispose() { }
    }
}

/// <summary>An engine over a line of 1,000 points and the tools to drive it in the tests of row I1a.</summary>
internal sealed class EngineFixture : IDisposable
{
    public const int Width = 640;
    public const int Height = 360;

    private readonly Column<float> _y;

    public EngineFixture(Func<PlotBuilder, PlotBuilder>? configure = null)
    {
        var values = new float[1000];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = MathF.Sin(i * 0.02f) * 10;
        }

        _y = Column.Wrap(values);
        Table = DataTable.Create().Add("x", Column.Uniform(0, 1, values.Length)).Add("y", _y).Build();
        var builder = Plot.Create(Table).Line(Table.Col<double>("x"), Table.Col<float>("y")).ScaleY(Scale.Linear().WithDomain(-20, 20));
        Spec = (configure?.Invoke(builder) ?? builder).Build();
        Engine = new PlotEngine(Backend, Width, Height, 1, Pool, Time);
        Engine.FrameRequested += () => Requests++;
        Engine.ViewChanged += (in ViewChange change) => Changes.Add(change);
        Engine.SetPlot(Spec);
        Engine.RenderFrame();
    }

    public PlotWorkerPool Pool { get; } = new(2);

    public ManualTime Time { get; } = new();

    public NullBackend Backend { get; } = new();

    public DataTable Table { get; }

    public PlotSpec Spec { get; }

    public PlotEngine Engine { get; }

    public int Requests { get; set; }

    public List<ViewChange> Changes { get; } = [];

    public static PanelId Panel => new(0, 0);

    /// <summary>Gets the panel's rectangle.</summary>
    public (int Left, int Top, int Width, int Height) Rect => Engine.Plan!.Guides is var g ? (g.Left, g.Top, g.Width, g.Height) : default;

    /// <summary>Gets the middle of the panel.</summary>
    public Vector2 Middle => new(Rect.Left + (Rect.Width / 2f), Rect.Top + (Rect.Height / 2f));

    /// <summary>Returns the panel's current x and y ranges.</summary>
    public (ViewRange X, ViewRange Y) View()
    {
        var view = (PanelView2D)Engine.CurrentView(Panel);
        return (view.X, view.Y);
    }

    /// <summary>Moves the time and draws a frame.</summary>
    public void Frame(TimeSpan after)
    {
        Time.Advance(after);
        Engine.RenderFrame();
    }

    /// <summary>Moves the time past any animation and draws.</summary>
    public void Settle() => Frame(TimeSpan.FromSeconds(1));

    public PointerEvent Pointer(
        PointerEventKind kind,
        Vector2 at,
        PointerButtons button = PointerButtons.None,
        ModifierKeys modifiers = ModifierKeys.None
    ) => new(kind, at, button, kind == PointerEventKind.Down ? button : PointerButtons.None, modifiers, PointerKind.Mouse, Time.GetTimestamp());

    /// <summary>Drags a button from one point to another in steps of 10 ms.</summary>
    public void Drag(Vector2 from, Vector2 to, PointerButtons button = PointerButtons.Left, ModifierKeys modifiers = ModifierKeys.None, int steps = 4)
    {
        Engine.OnPointer(Pointer(PointerEventKind.Down, from, button, modifiers));
        for (var i = 1; i <= steps; i++)
        {
            Time.Advance(TimeSpan.FromMilliseconds(10));
            Engine.OnPointer(
                Pointer(PointerEventKind.Move, Vector2.Lerp(from, to, i / (float)steps), PointerButtons.None, modifiers) with
                {
                    Buttons = button,
                }
            );
        }

        Engine.OnPointer(Pointer(PointerEventKind.Up, to, button, modifiers));
    }

    /// <summary>Turns the wheel at a point.</summary>
    public bool Wheel(Vector2 at, double notches, ModifierKeys modifiers = ModifierKeys.None) =>
        Engine.OnWheel(new WheelEvent(at, 0, notches, modifiers, Time.GetTimestamp()));

    /// <summary>Presses a key.</summary>
    public bool Key(PlotKey key, ModifierKeys modifiers = ModifierKeys.None) =>
        Engine.OnKey(new KeyEvent(KeyEventKind.Down, key, modifiers, false, Time.GetTimestamp()));

    public void Dispose()
    {
        Engine.Dispose();
        _y.Dispose();
        Pool.Dispose();
    }
}
