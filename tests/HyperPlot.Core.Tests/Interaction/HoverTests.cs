using System.Numerics;
using HyperPlot.Interaction;
using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>
/// The hover (design §6.5 "ホバー", §10 "ホバー（I2b）", requirement I-2, row I2b, Issue #347): the built-in
/// <see cref="HoverTool"/> asks the hit test at the pointer, the engine keeps the hits and draws a crosshair, markers, and a
/// tooltip over the plot, and a frame of the mouse alone copies the kept plot instead of recording it again.
/// </summary>
public sealed class HoverTests
{
    private static readonly PanelId Panel = new(0, 0);

    /// <summary>
    /// The default bindings hover on a move over a panel: <see cref="PlotEngine.Hover"/> holds what the hit test answers at the
    /// pointer, <see cref="PlotEngine.HoverChanged"/> comes once for other hits and not for the same ones at another distance
    /// (Issue #347 Decisions 2 and 3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The engine fixture: a line of 1,000 points, 640 × 360, its first frame drawn.
    ///
    /// Steps:
    /// - Move to the panel's middle; move 3 pixels down; move 20 pixels right.
    ///
    /// Expected:
    /// - One binding of a move, with no modifier, over a panel, runs a <see cref="HoverTool"/> of the default settings. The
    ///   first move returns false (the hover lets it go on) and the hover is the hit test's answer in
    ///   <see cref="HitMode.X"/>, told once. The move down finds the same row and tells nothing. The move right finds
    ///   another row, as the hit test does there, and tells once more.
    /// </remarks>
    [Fact]
    public void Default_MoveHoversAtThePointer()
    {
        using var f = new EngineFixture();
        var binding = Assert.Single(f.Engine.Bindings, b => b.Gesture.Kind == InputGestureKind.Move);
        var tool = Assert.IsType<HoverTool>(binding.Tool);
        Assert.Equal((ModifierKeys.None, HitAreaKind.Panel), (binding.Modifiers, binding.Areas));
        Assert.Equal((HitMode.X, 8f, HoverCrosshair.Vertical, true, true), (tool.Mode, tool.Radius, tool.Crosshair, tool.Markers, tool.Tooltip));
        Assert.Null(tool.TooltipFormat);
        var changes = 0;
        f.Engine.HoverChanged += () => changes++;

        Assert.False(f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, f.Middle)));
        Assert.Equal(Hits(f.Engine, f.Middle, 0, HitMode.X), f.Engine.Hover.ToArray());
        Assert.Equal(1, changes);
        var row = f.Engine.Hover[0].Row;

        f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, f.Middle + new Vector2(0, 3)));
        Assert.Equal(row, Assert.Single(f.Engine.Hover.ToArray()).Row);
        Assert.Equal(1, changes);

        var right = f.Middle + new Vector2(20, 0);
        f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, right));
        Assert.Equal(Hits(f.Engine, right, 0, HitMode.X), f.Engine.Hover.ToArray());
        Assert.NotEqual(row, f.Engine.Hover[0].Row);
        Assert.Equal(2, changes);
    }

    /// <summary>The hover is empty outside a panel and once the host tells the pointer left the target (Issue #347 Decisions 2 and 6).</summary>
    /// <remarks>
    /// Preconditions:
    /// - The engine fixture.
    ///
    /// Steps:
    /// - Move to the middle; move left of the panel, over the y axis; move back; pass a <see cref="PointerEventKind.Leave"/>.
    ///
    /// Expected:
    /// - Over the axis and after the leave the hover is empty, each told once and with a frame asked for; the leave returns
    ///   false. Back over the panel the hover has its hit again.
    /// </remarks>
    [Fact]
    public void OutsideAndLeave_EmptyTheHover()
    {
        using var f = new EngineFixture();
        var changes = 0;
        f.Engine.HoverChanged += () => changes++;
        f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, f.Middle));
        Assert.Single(f.Engine.Hover.ToArray());
        f.Engine.RenderFrame();

        var requests = f.Requests;
        f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, new Vector2(f.Rect.Left - 5, f.Middle.Y)));
        Assert.Equal(0, f.Engine.Hover.Length);
        Assert.Equal(2, changes);
        Assert.Equal(requests + 1, f.Requests);

        f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, f.Middle));
        Assert.Single(f.Engine.Hover.ToArray());
        Assert.Equal(3, changes);
        f.Engine.RenderFrame();

        requests = f.Requests;
        Assert.False(f.Engine.OnPointer(f.Pointer(PointerEventKind.Leave, f.Middle)));
        Assert.Equal(0, f.Engine.Hover.Length);
        Assert.Equal(4, changes);
        Assert.Equal(requests + 1, f.Requests);
    }

    /// <summary>
    /// A move that no hover tool takes empties the hover (row I2b): one with a modifier the binding does not name, and one with
    /// a button held that no press of the engine took.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The engine fixture, hovering at the middle.
    ///
    /// Steps:
    /// - Move with Shift held; move to the middle again; move with the middle button held (its press not passed).
    ///
    /// Expected:
    /// - Each of the two moves empties the hover and tells once; the plain move between hovers again.
    /// </remarks>
    [Fact]
    public void UntakenMove_EmptiesTheHover()
    {
        using var f = new EngineFixture();
        var changes = 0;
        f.Engine.HoverChanged += () => changes++;
        f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, f.Middle));

        f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, f.Middle, modifiers: ModifierKeys.Shift));
        Assert.Equal(0, f.Engine.Hover.Length);
        Assert.Equal(2, changes);

        f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, f.Middle));
        Assert.Single(f.Engine.Hover.ToArray());
        f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, f.Middle) with { Buttons = PointerButtons.Middle });
        Assert.Equal(0, f.Engine.Hover.Length);
        Assert.Equal(4, changes);
    }

    /// <summary>
    /// The tool's settings: <see cref="HitMode.XY"/> with its radius in DIPs, and the values each setting refuses (Issue #347
    /// Decision 3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The engine fixture; the default tool set to <see cref="HitMode.XY"/> and a radius of 4 DIPs.
    ///
    /// Steps:
    /// - Move 3 pixels below the line's peak (row 79); move 30 pixels above it. Resize to a scale of 2 and draw; move 6 pixels
    ///   below the peak. Set each setting to a value out of range.
    ///
    /// Expected:
    /// - Below: the hover is the hit test's answer in XY with a radius of 4 pixels, a row near the peak. Above: no line point lies
    ///   within the radius, and the hover is empty. At the scale of 2: the hover is the hit test's answer with a radius of 8
    ///   pixels, which one of 4 pixels does not find. A mode or a crosshair that is not named, and a radius that is negative,
    ///   NaN, or infinite, throw <see cref="ArgumentOutOfRangeException"/>.
    /// </remarks>
    [Fact]
    public void Settings_TakeTheModeAndTheRadius()
    {
        using var f = new EngineFixture();
        var tool = (HoverTool)f.Engine.Bindings.Single(b => b.Tool is HoverTool).Tool!;
        (tool.Mode, tool.Radius) = (HitMode.XY, 4);
        var peak = f.Engine.ViewToPixel(
            Panel,
            ViewRange.FromValues(79.0, 79.0),
            ViewRange.FromValues(10 * Math.Sin(79 * 0.02), 10 * Math.Sin(79 * 0.02))
        );

        var below = peak + new Vector2(0, 3);
        f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, below));
        var hit = Assert.Single(f.Engine.Hover.ToArray());
        Assert.Equal(Hits(f.Engine, below, 4, HitMode.XY), f.Engine.Hover.ToArray());
        Assert.InRange(hit.Row, 70, 88);

        f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, peak - new Vector2(0, 30)));
        Assert.Equal(0, f.Engine.Hover.Length);

        // At a scale of 2 the radius of 4 DIPs is 8 pixels: a point 6 pixels away is in it.
        f.Engine.Resize(EngineFixture.Width, EngineFixture.Height, 2);
        f.Engine.RenderFrame();
        peak = f.Engine.ViewToPixel(
            Panel,
            ViewRange.FromValues(79.0, 79.0),
            ViewRange.FromValues(10 * Math.Sin(79 * 0.02), 10 * Math.Sin(79 * 0.02))
        );
        below = peak + new Vector2(0, 6);
        f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, below));
        Assert.Single(f.Engine.Hover.ToArray());
        Assert.Equal(Hits(f.Engine, below, 8, HitMode.XY), f.Engine.Hover.ToArray());
        Assert.Empty(Hits(f.Engine, below, 4, HitMode.XY));

        Assert.Throws<ArgumentOutOfRangeException>(() => tool.Mode = (HitMode)7);
        Assert.Throws<ArgumentOutOfRangeException>(() => tool.Crosshair = (HoverCrosshair)7);
        Assert.Throws<ArgumentOutOfRangeException>(() => tool.Radius = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => tool.Radius = float.NaN);
        Assert.Throws<ArgumentOutOfRangeException>(() => tool.Radius = float.PositiveInfinity);
    }

    /// <summary>A hover tool taken out of the bindings hovers no more: the next frame drawn again empties the hover, and a move finds none.</summary>
    /// <remarks>
    /// Preconditions:
    /// - The engine fixture, hovering at the middle.
    ///
    /// Steps:
    /// - Remove the binding of the move; pan right with the key and draw; move to the middle again.
    ///
    /// Expected:
    /// - The frame drawn again empties the hover and tells once; the move leaves it empty and tells nothing.
    /// </remarks>
    [Fact]
    public void Unbound_HoversNoMore()
    {
        using var f = new EngineFixture();
        var changes = 0;
        f.Engine.HoverChanged += () => changes++;
        f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, f.Middle));
        Assert.Equal(1, changes);

        Assert.Equal(1, f.Engine.Bindings.RemoveAll(b => b.Tool is HoverTool));
        f.Key(PlotKey.Right);
        f.Settle();
        Assert.Equal(0, f.Engine.Hover.Length);
        Assert.Equal(2, changes);

        f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, f.Middle));
        Assert.Equal(0, f.Engine.Hover.Length);
        Assert.Equal(2, changes);
    }

    /// <summary>
    /// The hover returns <see cref="ToolResult.Ignored"/> to a move, so a tool bound to a move after it gets the move too
    /// (Issue #347 Decision 7).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The engine fixture, with a tool bound to a move at the end of the bindings that counts moves and handles them.
    ///
    /// Steps:
    /// - Move to the middle.
    ///
    /// Expected:
    /// - The hover's tool returns Ignored; the counting tool gets the move; the engine returns true (the counting tool used
    ///   it); the hover has its hit.
    /// </remarks>
    [Fact]
    public void Move_GoesOnToTheToolsAfter()
    {
        using var f = new EngineFixture();
        var counter = new MoveCounter();
        f.Engine.Bindings.Add(new InputBinding(InputGesture.Move, ModifierKeys.None, HitAreaKind.All, counter));
        var tool = (HoverTool)f.Engine.Bindings.First(b => b.Tool is HoverTool).Tool!;

        Assert.True(f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, f.Middle)));
        Assert.Equal(1, counter.Moves);
        Assert.Single(f.Engine.Hover.ToArray());
        var context = new ToolContext(f.Engine);
        Assert.Equal(ToolResult.Ignored, tool.OnPointer(f.Pointer(PointerEventKind.Move, f.Middle), context));
    }

    /// <summary>A tool that takes the pointer empties the hover (Issue #347 Decision 8).</summary>
    /// <remarks>
    /// Preconditions:
    /// - The engine fixture, hovering at the middle.
    ///
    /// Steps:
    /// - Drag the left button 50 pixels right from the middle (the pan tool takes the pointer).
    ///
    /// Expected:
    /// - Once the drag starts the hover is empty, told once, and stays empty through the drag's end.
    /// </remarks>
    [Fact]
    public void Capture_EmptiesTheHover()
    {
        using var f = new EngineFixture();
        var changes = 0;
        f.Engine.HoverChanged += () => changes++;
        f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, f.Middle));

        f.Drag(f.Middle, f.Middle + new Vector2(50, 0));
        Assert.Equal(0, f.Engine.Hover.Length);
        Assert.Equal(2, changes);
    }

    /// <summary>
    /// A frame drawn again hits again where the pointer is (Issue #347 Decision 9): after a pan by a key and a wheel's zoom,
    /// with no move of the mouse, the hover is the hit test's answer at the same pixel in the new view.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The engine fixture, hovering at the middle.
    ///
    /// Steps:
    /// - Pan right with the key and let the animation end; then zoom in by 2 notches 100 pixels right of the pointer and let
    ///   it end.
    ///
    /// Expected:
    /// - After each, the hover is the hit test's answer at the middle, another row than before.
    /// </remarks>
    [Fact]
    public void FrameDrawnAgain_HitsAgainAtThePointer()
    {
        using var f = new EngineFixture();
        f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, f.Middle));
        var row = f.Engine.Hover[0].Row;

        f.Key(PlotKey.Right);
        f.Settle();
        Assert.Equal(Hits(f.Engine, f.Middle, 0, HitMode.X), f.Engine.Hover.ToArray());
        Assert.NotEqual(row, f.Engine.Hover[0].Row);
        row = f.Engine.Hover[0].Row;

        f.Wheel(f.Middle + new Vector2(100, 0), 2);
        f.Settle();
        Assert.Equal(Hits(f.Engine, f.Middle, 0, HitMode.X), f.Engine.Hover.ToArray());
        Assert.NotEqual(row, f.Engine.Hover[0].Row);
    }

    /// <summary>
    /// A provisional hover is hit again once the frame stops being provisional, with no move of the mouse (Issue #347
    /// Decision 9).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread (<c>TestStorage</c> holds every read on a worker); a line over a uniform x and a Y of two chunks,
    ///   chunk 1 held on the worker; an engine that does not wait; the hover in XY with a radius of 400 DIPs.
    ///
    /// Steps:
    /// - Draw until the worker reads chunk 1; draw; move to three quarters of the panel's width (over chunk 1); draw; move a
    ///   pixel and draw; release the chunk and draw until a frame is not provisional; move to the same pixel and draw.
    ///
    /// Expected:
    /// - Held: the hover's hit is provisional, and each frame, a move's too, draws the plot again and keeps nothing (no copy,
    ///   two lists). Released: the hit is not provisional, and it is the hit test's answer at the same pixel; the next move's
    ///   frame copies the kept plot (one copy, one list).
    /// </remarks>
    [Fact]
    public void Provisional_HitsAgainWhenItEnds()
    {
        const int Chunk = Column.ChunkLength;
        var values = new float[2 * Chunk];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = i < Chunk ? MathF.Sin(i * 0.001f) : 10 * MathF.Sin(i * 0.001f);
        }

        using var pool = new PlotWorkerPool(1);
        using var y = TestStorage<float>.MakeColumn(values, out var storage);
        storage.Hold(1);
        try
        {
            var table = DataTable.Create().Add("x", Column.Uniform(0, 1, 2 * Chunk)).Add("y", y).Build();
            var backend = new NullBackend();
            using var engine = new PlotEngine(backend, 640, 360, 1, pool);
            engine.SetPlot(Plot.Create(table).Line(table.Col<double>("x"), table.Col<float>("y")).Build());
            engine.RenderFrame();
            storage.WaitForWorker(1);
            engine.RenderFrame();
            Assert.True(engine.IsProvisional);

            // XY over a wide radius answers from the extremes the pyramid has while chunk 1 is built, as row I2a's test does.
            var tool = (HoverTool)engine.Bindings.Single(b => b.Tool is HoverTool).Tool!;
            (tool.Mode, tool.Radius) = (HitMode.XY, 400);
            var guide = engine.Plan!.Guides.PanelAt(0);
            var at = new Vector2(guide.Left + (guide.Width * 0.75f), guide.Top + (guide.Height / 2f));
            engine.OnPointer(Move(at));
            Assert.True(Assert.Single(engine.Hover.ToArray()).Provisional);
            engine.RenderFrame();
            Assert.Equal((0, 2), (backend.CopiesInFrame, backend.DrawsInFrame));
            at += new Vector2(1, 0);
            engine.OnPointer(Move(at));
            engine.RenderFrame();
            Assert.Equal((0, 2), (backend.CopiesInFrame, backend.DrawsInFrame));

            storage.Release(1);
            Eventually.True(
                () =>
                {
                    engine.RenderFrame();
                    return !engine.IsProvisional;
                },
                "a frame that is not provisional"
            );
            Assert.False(Assert.Single(engine.Hover.ToArray()).Provisional);
            Assert.Equal(Hits(engine, at, 400, HitMode.XY), engine.Hover.ToArray());
            engine.OnPointer(Move(at));
            engine.RenderFrame();
            Assert.Equal((1, 1), (backend.CopiesInFrame, backend.DrawsInFrame));
        }
        finally
        {
            storage.Release(1);
        }
    }

    /// <summary>
    /// What the hover records over the plot (Issue #347 Decisions 3 and 4): a vertical crosshair the panel's height through the
    /// pointer, a disc in the series' color at each hit, and the tooltip at the pointer's lower right, "label: x, y" in the
    /// axes' formats, in a box of the theme's background with its foreground's text.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A line labeled "speed" of 100 rows, y = 0.25 · row, the y domain [0, 30], and a line without a label of y = 1;
    ///   640 × 360, Theme.Light.
    ///
    /// Steps:
    /// - Move to the pixel of row 41 of the first line; draw.
    ///
    /// Expected:
    /// - The last list drawn holds a rect at the pointer's pixel column, 1 pixel wide, from the panel's top for its height, in
    ///   half the foreground's alpha; a disc centered on each hit's point, the first in palette color 0 and the second in
    ///   color 1; a rect of the background below and right of the pointer; and one text at its top left, of the foreground,
    ///   "speed: 41, 10.25\nline: 41, 1".
    /// </remarks>
    [Fact]
    public void Record_DrawsTheCrosshairMarkersAndTooltip()
    {
        using var hover = HoverPlot.Make();
        var (engine, backend) = (hover.Engine, hover.Backend);
        var at = engine.ViewToPixel(Panel, ViewRange.FromValues(41.0, 41.0), ViewRange.FromValues(10.25, 10.25));
        engine.OnPointer(Move(at));
        engine.RenderFrame();

        var list = backend.LastList!;
        var guide = engine.Plan!.Guides.PanelAt(0);
        var foreground = Theme.Light.Foreground;
        var crosshair = Premultiplied(foreground with { A = (byte)Math.Round(foreground.A * 0.5) });
        Assert.Contains(list.Rects.ToArray(), r => r == new RectInstance(MathF.Floor(at.X), guide.Top, 1, guide.Height, crosshair));

        var hits = engine.Hover.ToArray();
        Assert.Equal(2, hits.Length);
        var discs = list.SectorCalls.ToArray();
        Assert.Equal(4, discs.Length);
        for (var i = 0; i < 2; i++)
        {
            var point = engine.ViewToPixel(Panel, ViewRange.FromValues(hits[i].X, hits[i].X), ViewRange.FromValues(hits[i].Y, hits[i].Y));
            Assert.Equal(point.X, discs[(2 * i) + 1].Center.X, 3);
            Assert.Equal(point.Y, discs[(2 * i) + 1].Center.Y, 3);
            Assert.Equal(Premultiplied(Theme.Light.Palette.Series(i)), discs[(2 * i) + 1].Color);
            Assert.Equal(Premultiplied(Theme.Light.PanelBackground), discs[2 * i].Color);
        }

        var text = Assert.Single(list.Texts.ToArray());
        Assert.Equal("speed: 41, 10.25\nline: 41, 1", text.Text);
        Assert.Equal(foreground, text.Style.Color);
        Assert.True(text.Position.X > at.X && text.Position.Y > at.Y);
        Assert.Contains(
            list.Rects.ToArray(),
            r => r.Color == Premultiplied(Theme.Light.Background) && r.X < text.Position.X && r.Y < text.Position.Y && r.X > at.X
        );
    }

    /// <summary>
    /// The tooltip goes to the other side of the pointer where it would leave the panel (Issue #347 Decision 4), and the
    /// tool's <see cref="TooltipFormatter"/> writes its lines, or hides it by returning false.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The plot of <see cref="Record_DrawsTheCrosshairMarkersAndTooltip"/>; the tool's format writes "row " and the first
    ///   hit's row, and returns false when the first hit's row is above 90.
    ///
    /// Steps:
    /// - Move near the panel's lower right corner (row 89); draw. Move to row 95; draw. Set a format that writes one more
    ///   character than its span; move back and draw.
    ///
    /// Expected:
    /// - Row 89: one text, "row 89", left of and above the pointer. Row 95: no text. A format that says it wrote more than its
    ///   span holds makes the frame throw InvalidOperationException.
    /// </remarks>
    [Fact]
    public void Tooltip_FlipsAndTakesTheFormatter()
    {
        using var hover = HoverPlot.Make();
        var (engine, backend) = (hover.Engine, hover.Backend);
        hover.Tool.TooltipFormat = static (ReadOnlySpan<SeriesHit> hits, Span<char> destination, out int written) =>
        {
            written = 0;
            if (hits[0].Row > 90)
            {
                return false;
            }

            "row ".AsSpan().CopyTo(destination);
            _ = hits[0].Row.TryFormat(destination[4..], out var digits, provider: System.Globalization.CultureInfo.InvariantCulture);
            written = 4 + digits;
            return true;
        };
        var guide = engine.Plan!.Guides.PanelAt(0);
        var at = engine.ViewToPixel(Panel, ViewRange.FromValues(89.0, 89.0), ViewRange.FromValues(1.0, 1.0));
        at = at with { Y = guide.Top + guide.Height - 4 };
        engine.OnPointer(Move(at));
        engine.RenderFrame();
        var text = Assert.Single(backend.LastList!.Texts.ToArray());
        Assert.Equal("row 89", text.Text);
        Assert.True(text.Position.X < at.X && text.Position.Y < at.Y, $"{text.Position} {at}");

        engine.OnPointer(Move(engine.ViewToPixel(Panel, ViewRange.FromValues(95.0, 95.0), ViewRange.FromValues(1.0, 1.0))));
        engine.RenderFrame();
        Assert.Empty(backend.LastList!.Texts.ToArray());

        hover.Tool.TooltipFormat = static (ReadOnlySpan<SeriesHit> hits, Span<char> destination, out int written) =>
        {
            written = destination.Length + 1;
            return true;
        };
        engine.OnPointer(Move(at));
        _ = Assert.Throws<InvalidOperationException>(engine.RenderFrame);
    }

    /// <summary>
    /// The tooltip writes each value in the format of its axis's tick labels (row I2b): a time axis at the finest boundary of
    /// its ticks, a log axis at three significant digits, a user's format, and G6 where a user's format writes nothing.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 100 rows an hour apart from 2026-01-01 (a long column of seconds on a time scale) and a y of 1.5 + row on a log scale;
    ///   a linear y whose format writes "y=" and the value at one decimal, and one whose format writes whole numbers and returns false for others.
    ///
    /// Steps:
    /// - Hover over row 41 of the time plot; over row 41 with each of the two formats.
    ///
    /// Expected:
    /// - The time plot: "line: " and the instant of row 41 written at the finest boundary of the x ticks, then ", 42.5". The
    ///   formats: "line: 41, y=" and 10.25 at one decimal, and "line: 41, 10.25".
    /// </remarks>
    [Fact]
    public void Tooltip_WritesValuesInTheAxesFormats()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using var seconds = Column.Wrap(Enumerable.Range(0, 100).Select(i => i * 3600L).ToArray());
        using var logY = Column.Wrap(Enumerable.Range(0, 100).Select(i => 1.5 + i).ToArray());
        var times = DataTable.Create().Add("t", seconds).Add("y", logY).Build();
        var timeSpec = new PlotSpec
        {
            Data = times,
            Layers = [new LineLayerSpec { X = times.Col<long>("t"), Y = times.Col<double>("y") }],
            ScaleX = Scale.Time(TimeUnit.Seconds, start),
            ScaleY = Scale.Log(),
        };
        var guide = default(PanelGuide);
        var timeTooltip = Tooltip(timeSpec, plan => Pixel(plan, 41 * 3600.0, 42.5), plan => guide = plan.Guides.PanelAt(0));
        Assert.Equal($"line: {TimeText(start.AddHours(41))}, 42.5", timeTooltip);

        string TimeText(DateTime at)
        {
            var finest = TimeInterval.Day;
            for (var i = 0; i < guide!.X.Count; i++)
            {
                finest = i == 0 || guide.X.BoundaryAt(i) > finest ? guide.X.BoundaryAt(i) : finest;
            }

            var buffer = new char[64];
            Assert.True(TickFormat.TryWriteTime(at, 0, finest, buffer, out var written));
            return new string(buffer, 0, written);
        }

        using var speed = Column.Wrap(Enumerable.Range(0, 100).Select(i => i * 0.25).ToArray());
        var linear = DataTable.Create().Add("x", Column.Uniform(0, 1, 100)).Add("y", speed).Build();
        var written = new PlotSpec
        {
            Data = linear,
            Layers = [new LineLayerSpec { X = linear.Col<double>("x"), Y = linear.Col<double>("y") }],
            ScaleY = Scale.Linear() with
            {
                TickFormat = static (double value, Span<char> destination, out int count) =>
                {
                    "y=".AsSpan().CopyTo(destination);
                    _ = value.TryFormat(destination[2..], out count, "F1", System.Globalization.CultureInfo.InvariantCulture);
                    count += 2;
                    return true;
                },
            },
        };
        Assert.Equal(
            $"line: 41, y={10.25.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}",
            Tooltip(written, plan => Pixel(plan, 41, 10.25), null)
        );
        var refused = written with
        {
            ScaleY = Scale.Linear() with
            {
                TickFormat = static (double value, Span<char> destination, out int count) =>
                {
                    // The ticks are whole numbers and are written; the hit's 10.25 is not.
                    count = 0;
                    return value == Math.Round(value)
                        && value.TryFormat(destination, out count, "F0", System.Globalization.CultureInfo.InvariantCulture);
                },
            },
        };
        Assert.Equal("line: 41, 10.25", Tooltip(refused, plan => Pixel(plan, 41, 10.25), null));

        static Vector2 Pixel(PlotPlan plan, double x, double y)
        {
            var frames = plan.Panels[0].Frames;
            return new Vector2((float)HitScan.PixelOfValue(frames.X, x), (float)HitScan.PixelOfValue(frames.Y, y));
        }

        static string Tooltip(PlotSpec spec, Func<PlotPlan, Vector2> at, Action<PlotPlan>? look)
        {
            using var pool = new PlotWorkerPool(2);
            var backend = new NullBackend();
            using var engine = new PlotEngine(backend, EngineFixture.Width, EngineFixture.Height, 1, pool) { WaitsForIndexes = true };
            engine.SetPlot(spec);
            engine.RenderFrame();
            look?.Invoke(engine.Plan!);
            engine.OnPointer(Move(at(engine.Plan!)));
            engine.RenderFrame();
            return Assert.Single(backend.LastList!.Texts.ToArray()).Text;
        }
    }

    /// <summary>The crosshair's directions, the markers, and the tooltip each follow the tool's settings (Issue #347 Decision 3).</summary>
    /// <remarks>
    /// Preconditions:
    /// - The plot of <see cref="Record_DrawsTheCrosshairMarkersAndTooltip"/>, hovering at row 41.
    ///
    /// Steps:
    /// - Draw with the crosshair set to each value, then with the markers and the tooltip off.
    ///
    /// Expected:
    /// - None draws no line; Vertical one the panel's height; Horizontal one the panel's width through the pointer's pixel
    ///   row; Both the two. With the markers and the tooltip off and no crosshair, the frame copies the kept plot and draws
    ///   no list over it.
    /// </remarks>
    [Fact]
    public void Settings_ChooseWhatIsDrawn()
    {
        using var hover = HoverPlot.Make();
        var (engine, backend, tool) = (hover.Engine, hover.Backend, hover.Tool);
        var at = engine.ViewToPixel(Panel, ViewRange.FromValues(41.0, 41.0), ViewRange.FromValues(10.25, 10.25));
        var guide = engine.Plan!.Guides.PanelAt(0);
        var vertical = new RectInstance(MathF.Floor(at.X), guide.Top, 1, guide.Height, default);
        var horizontal = new RectInstance(guide.Left, MathF.Floor(at.Y), guide.Width, 1, default);
        foreach (
            var (crosshair, lines) in new[]
            {
                (HoverCrosshair.None, 0),
                (HoverCrosshair.Vertical, 1),
                (HoverCrosshair.Horizontal, 1),
                (HoverCrosshair.Both, 2),
            }
        )
        {
            tool.Crosshair = crosshair;
            engine.OnPointer(Move(at));
            engine.RenderFrame();
            var rects = backend.LastList!.Rects.ToArray().Select(r => r with { Color = default }).ToArray();
            Assert.Equal(lines, rects.Count(r => r == vertical || r == horizontal));
            Assert.Equal(crosshair is HoverCrosshair.Vertical or HoverCrosshair.Both, rects.Contains(vertical));
            Assert.Equal(crosshair is HoverCrosshair.Horizontal or HoverCrosshair.Both, rects.Contains(horizontal));
        }

        (tool.Crosshair, tool.Markers, tool.Tooltip) = (HoverCrosshair.None, false, false);
        engine.OnPointer(Move(at + new Vector2(1, 0)));
        var drawn = engine.Plan.Recordings;
        engine.RenderFrame();
        Assert.Equal(drawn, engine.Plan.Recordings);
        Assert.Equal((1, 0), (backend.CopiesInFrame, backend.DrawsInFrame));
    }

    /// <summary>
    /// Each marker takes its series' color (row I2b): a line's own color, an area's fill made opaque, and each category's
    /// color of a line split by a category.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 100 rows: a red line (y = 5), an area of y = 10 filled half-transparent blue, and a line of y = 15 + row % 2 split by
    ///   a category of 2 values that alternate row by row; Theme.Light; the y domain [0, 30].
    ///
    /// Steps:
    /// - Hover at the middle of the panel in X mode; draw.
    ///
    /// Expected:
    /// - Four hits; each disc (the second of each pair of sector calls) is, in the hits' order, red, the blue made opaque, and
    ///   the palette's colors of the hits' categories.
    /// </remarks>
    [Fact]
    public void Markers_TakeTheSeriesColors()
    {
        using var flat = Column.Wrap(Enumerable.Repeat(5.0, 100).ToArray());
        using var area = Column.Wrap(Enumerable.Repeat(10.0, 100).ToArray());
        using var split = Column.Wrap(Enumerable.Range(0, 100).Select(i => 15.0 + (i % 2)).ToArray());
        var table = DataTable
            .Create()
            .Add("x", Column.Uniform(0, 1, 100))
            .Add("flat", flat)
            .Add("area", area)
            .Add("split", split)
            .Add("g", Column.Categorical([.. Enumerable.Range(0, 100).Select(i => i % 2 == 0 ? "even" : "odd")]))
            .Build();
        var red = new Color(220, 20, 20, 255);
        var blue = new Color(20, 40, 220, 128);
        var spec = Plot.Create(table)
            .Line(table.Col<double>("x"), table.Col<double>("flat"), l => l.Color(red))
            .Area(table.Col<double>("x"), table.Col<double>("area"), configure: a => a.Fill(blue))
            .Line(table.Col<double>("x"), table.Col<double>("split"), l => l.ColorBy(table.Cat("g")))
            .ScaleY(Scale.Linear().WithDomain(0, 30))
            .Build();
        using var pool = new PlotWorkerPool(2);
        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, EngineFixture.Width, EngineFixture.Height, 1, pool) { WaitsForIndexes = true };
        engine.SetPlot(spec);
        engine.RenderFrame();
        var guide = engine.Plan!.Guides.PanelAt(0);
        engine.OnPointer(Move(new Vector2(guide.Left + (guide.Width / 2f), guide.Top + (guide.Height / 2f))));
        engine.RenderFrame();

        var hits = engine.Hover.ToArray();
        Assert.Equal(4, hits.Length);
        var discs = backend.LastList!.SectorCalls.ToArray();
        Assert.Equal(8, discs.Length);
        Assert.Equal(Premultiplied(red), discs[1].Color);
        Assert.Equal(Premultiplied(blue with { A = 255 }), discs[3].Color);
        Assert.Equal(Premultiplied(Theme.Light.Palette.Series(hits[2].Series)), discs[5].Color);
        Assert.Equal(Premultiplied(Theme.Light.Palette.Series(hits[3].Series)), discs[7].Color);
        Assert.NotEqual(hits[2].Series, hits[3].Series);
    }

    /// <summary>
    /// A stacked layer's markers take each group's color, as its parts are drawn, and its groups' series are numbered 2 a
    /// group (design §6.3, row I2b, the review of PR #352).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 200 rows: x = row / 2, a y of 1, and a category of 2 values that alternate row by row; an area stacked over x and
    ///   colored by the category; Theme.Light.
    ///
    /// Steps:
    /// - Hover at the middle of the panel in X mode; draw.
    ///
    /// Expected:
    /// - Three hits of three series: the lowest group's area and the upper group's band's two edges, numbered 2 a group so
    ///   that no two meet. Each disc is the palette's color of its hit's group (series / 2), made opaque.
    /// </remarks>
    [Fact]
    public void Markers_TakeEachStackedGroupsColor()
    {
        using var x = Column.Wrap(Enumerable.Range(0, 200).Select(i => i / 2.0).ToArray());
        using var y = Column.Wrap(Enumerable.Repeat(1.0, 200).ToArray());
        var table = DataTable
            .Create()
            .Add("x", x)
            .Add("y", y)
            .Add("g", Column.Categorical([.. Enumerable.Range(0, 200).Select(i => i % 2 == 0 ? "a" : "b")]))
            .Build();
        var spec = Plot.Create(table)
            .Area(table.Col<double>("x"), table.Col<double>("y"), configure: a => a.ColorBy(table.Cat("g")).Position(Position.Stack))
            .Build();
        using var pool = new PlotWorkerPool(2);
        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, EngineFixture.Width, EngineFixture.Height, 1, pool) { WaitsForIndexes = true };
        engine.SetPlot(spec);
        engine.RenderFrame();
        var guide = engine.Plan!.Guides.PanelAt(0);
        engine.OnPointer(Move(new Vector2(guide.Left + (guide.Width / 2f), guide.Top + (guide.Height / 2f))));
        engine.RenderFrame();

        // The lowest group answers its area (2 · group), the one above its band's two edges (2 · group and 2 · group + 1).
        var hits = engine.Hover.ToArray();
        Assert.Equal(3, hits.Length);
        Assert.Equal(3, hits.Select(h => h.Series).Distinct().Count());
        Assert.Equal(2, hits.Select(h => h.Series / 2).Distinct().Count());
        var discs = backend.LastList!.SectorCalls.ToArray();
        Assert.Equal(6, discs.Length);
        for (var i = 0; i < hits.Length; i++)
        {
            Assert.Equal(Premultiplied(Theme.Light.Palette.Series(hits[i].Series / 2) with { A = 255 }), discs[(2 * i) + 1].Color);
        }
    }

    /// <summary>A stat's marker takes the color of the layer the stat draws (row I2b, the review of PR #352): a red line of a density is marked red.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 1,000 rows of a value that rises with the row; a line of its density with a red stroke; Theme.Light.
    ///
    /// Steps:
    /// - Hover at the middle of the panel in X mode; draw.
    ///
    /// Expected:
    /// - One hit, marked derived; its disc is red, not the palette's first color.
    /// </remarks>
    [Fact]
    public void Markers_TakeTheStatsLayerColor()
    {
        using var values = Column.Wrap(Enumerable.Range(0, 1000).Select(i => Math.Sqrt(i)).ToArray());
        var table = DataTable.Create().Add("v", values).Build();
        var red = new Color(220, 20, 20, 255);
        var spec = new PlotSpec
        {
            Data = table,
            Layers =
            [
                new LineLayerSpec
                {
                    X = table.Col<double>("v"),
                    Stat = Stat.Density(),
                    Style = new StrokeSpec(red),
                },
            ],
        };
        using var pool = new PlotWorkerPool(2);
        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, EngineFixture.Width, EngineFixture.Height, 1, pool) { WaitsForIndexes = true };
        engine.SetPlot(spec);
        engine.RenderFrame();
        var guide = engine.Plan!.Guides.PanelAt(0);
        engine.OnPointer(Move(new Vector2(guide.Left + (guide.Width / 2f), guide.Top + (guide.Height / 2f))));
        engine.RenderFrame();

        Assert.True(Assert.Single(engine.Hover.ToArray()).Derived);
        Assert.Equal(Premultiplied(red), backend.LastList!.SectorCalls[1].Color);
    }

    /// <summary>
    /// The crosshair runs along the screen's sides under a flip too (row I2b): a vertical line the panel's height through the
    /// pointer's pixel column.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The engine fixture with a flipped Cartesian coordinate system; the default hover.
    ///
    /// Steps:
    /// - Move to the middle; draw.
    ///
    /// Expected:
    /// - The last list holds a rect at the pointer's pixel column, 1 pixel wide, from the panel's top for its height.
    /// </remarks>
    [Fact]
    public void Crosshair_KeepsTheScreensSidesUnderAFlip()
    {
        using var f = new EngineFixture(b => b.Coord(Coord.Cartesian(flip: true)));
        f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, f.Middle));
        f.Engine.RenderFrame();

        var guide = f.Engine.Plan!.Guides.PanelAt(0);
        var rects = f.Backend.LastList!.Rects.ToArray().Select(r => r with { Color = default });
        Assert.Contains(new RectInstance(MathF.Floor(f.Middle.X), guide.Top, 1, guide.Height, default), rects);
    }

    /// <summary>
    /// A frame of the mouse alone records no layer again (design §5.4, Issue #347 Decision 1): it copies the plot kept from the
    /// frame before and draws the hover over it; a change of the view, the data's version, the size, the spec, a device made
    /// again, or a provisional frame draws the whole frame again.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The engine fixture; the backend counts the copies and the lists drawn in a frame.
    ///
    /// Steps:
    /// - Draw with nothing over the plot. Move to the middle and draw (the first frame with a hover). Move and draw three
    ///   times. Then, each followed by a move and a frame: pan with the key and let it end; invalidate the y column; resize;
    ///   change the scale alone; set a spec of another theme; move the backend's device number. Then a move and a frame.
    ///
    /// Expected:
    /// - The frame with nothing over the plot keeps nothing (no copy, one list). The first frame with a hover draws the
    ///   plot's list and keeps it (one copy, two lists). Each frame of a move alone records nothing,
    ///   copies once, and draws one list. Each change draws the plot's list again in its frame and keeps it (one copy, two
    ///   lists), and the move after records nothing.
    /// </remarks>
    [Fact]
    public void Compose_FramesOfTheMouseAloneRecordNoLayer()
    {
        using var f = new EngineFixture();
        var x = f.Middle.X;
        void MoveAndDraw()
        {
            x += 3;
            f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, new Vector2(x, f.Middle.Y)));
            f.Engine.RenderFrame();
        }

        // A frame with nothing over the plot keeps nothing.
        f.Engine.RenderFrame();
        Assert.Equal((0, 1), (f.Backend.CopiesInFrame, f.Backend.DrawsInFrame));

        MoveAndDraw();
        Assert.Equal((1, 2), (f.Backend.CopiesInFrame, f.Backend.DrawsInFrame));

        void Alone()
        {
            var before = f.Engine.Plan!.Recordings;
            MoveAndDraw();
            Assert.Equal(before, f.Engine.Plan.Recordings);
            Assert.Equal((1, 1), (f.Backend.CopiesInFrame, f.Backend.DrawsInFrame));
        }

        void Again(Action change)
        {
            change();
            MoveAndDraw();
            Assert.Equal((1, 2), (f.Backend.CopiesInFrame, f.Backend.DrawsInFrame));
            Alone();
        }

        Alone();
        Alone();
        Alone();
        Again(() =>
        {
            f.Key(PlotKey.Right);
            f.Time.Advance(TimeSpan.FromSeconds(1));
        });
        Again(() => ((Column<float>)f.Table.Col<float>("y").Column).Invalidate(0, 10));
        Again(() => f.Engine.Resize(EngineFixture.Width, EngineFixture.Height + 20, 1));
        Again(() => f.Engine.Resize(EngineFixture.Width, EngineFixture.Height + 20, 2));
        Again(() => f.Engine.SetPlot(f.Spec with { Theme = Theme.Dark }));
        Again(() => f.Backend.DeviceGeneration++);
    }

    /// <summary>
    /// A frame of the mouse alone keeps what the frame drawn whole found (design §5.4, row I2b): a layer's failure is told
    /// once, and the frame stays not provisional.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread; a line over a uniform x and a line over a decreasing x, which fails its work; an engine that does
    ///   not wait.
    ///
    /// Steps:
    /// - Draw until the failure is told and a frame is not provisional; move over the panel and draw 5 times.
    ///
    /// Expected:
    /// - Each of the 5 frames copies the kept plot (one copy, one list); WorkFailed came once and ProvisionalChanged as many
    ///   times as before the moves; the engine is not provisional.
    /// </remarks>
    [Fact]
    public void Compose_KeepsTheFailuresAndTheProvisionalState()
    {
        const int Length = 1000;
        using var pool = new PlotWorkerPool(1);
        using var bad = Column.Wrap(Enumerable.Range(0, Length).Select(i => (double)(Length - i)).ToArray());
        using var y = Column.Wrap(Enumerable.Range(0, Length).Select(i => 1.0 + (i % 7)).ToArray());
        var table = DataTable.Create().Add("u", Column.Uniform(0, 1, Length)).Add("y", y).Add("bad", bad).Build();
        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, 640, 360, 1, pool);
        var (failures, provisional) = (0, 0);
        engine.WorkFailed += (in WorkFailure failure) => failures++;
        engine.ProvisionalChanged += () => provisional++;
        engine.SetPlot(
            Plot.Create(table).Line(table.Col<double>("u"), table.Col<double>("y")).Line(table.Col<double>("bad"), table.Col<double>("y")).Build()
        );
        Eventually.True(
            () =>
            {
                engine.RenderFrame();
                return failures > 0 && !engine.IsProvisional;
            },
            "the failure, and a frame that is not provisional"
        );
        var changes = provisional;

        var guide = engine.Plan!.Guides.PanelAt(0);
        for (var i = 0; i < 6; i++)
        {
            engine.OnPointer(Move(new Vector2(guide.Left + 20 + (i * 10), guide.Top + (guide.Height / 2f))));
            engine.RenderFrame();
            if (i > 0)
            {
                Assert.Equal((1, 1), (backend.CopiesInFrame, backend.DrawsInFrame));
            }
        }

        Assert.Equal(1, failures);
        Assert.Equal(changes, provisional);
        Assert.False(engine.IsProvisional);
    }

    /// <summary>Warm frames of the mouse alone allocate nothing (Issue #347, §10): the moves, the hit test, the copy, and the hover's recording.</summary>
    /// <remarks>
    /// Preconditions:
    /// - The plot of <see cref="Record_DrawsTheCrosshairMarkersAndTooltip"/>, the default hover.
    ///
    /// Steps:
    /// - Move over 8 pixels of the panel in turn and draw after each, 3 times round to warm the shaping cache and the lists;
    ///   collect; then count the thread's allocations over 5 more rounds.
    ///
    /// Expected:
    /// - 0 bytes.
    /// </remarks>
    [Fact]
    public void Compose_WarmFramesOfTheMouseAllocateNothing()
    {
        using var hover = HoverPlot.Make();
        var engine = hover.Engine;
        var guide = engine.Plan!.Guides.PanelAt(0);
        void Round()
        {
            for (var i = 0; i < 8; i++)
            {
                engine.OnPointer(Move(new Vector2(guide.Left + 40 + (i * 37), guide.Top + 30 + (i * 11))));
                engine.RenderFrame();
            }
        }

        for (var k = 0; k < 3; k++)
        {
            Round();
        }

        // An allocation that follows a collection is counted in every run (.cursor/rules/bench.mdc).
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var k = 0; k < 5; k++)
        {
            Round();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static PointerEvent Move(Vector2 at) =>
        new(PointerEventKind.Move, at, PointerButtons.None, PointerButtons.None, ModifierKeys.None, PointerKind.Mouse, 0);

    private static SeriesHit[] Hits(PlotEngine engine, Vector2 at, float radius, HitMode mode)
    {
        var hits = new SeriesHit[8];
        return hits[..engine.HitTest(at, radius, mode, hits)];
    }

    /// <summary>Returns a color as the list records it: each channel premultiplied in whole steps of 1 / 255.</summary>
    private static Vector4 Premultiplied(Color color) =>
        new(
            HyperPlot.Rendering.Premultiplied.Multiply(color.R, color.A) / 255f,
            HyperPlot.Rendering.Premultiplied.Multiply(color.G, color.A) / 255f,
            HyperPlot.Rendering.Premultiplied.Multiply(color.B, color.A) / 255f,
            color.A / 255f
        );

    /// <summary>A tool that counts the moves it gets and handles them.</summary>
    private sealed class MoveCounter : IPlotTool
    {
        public int Moves { get; private set; }

        public ToolResult OnPointer(in PointerEvent e, ToolContext context)
        {
            Moves++;
            return ToolResult.Handled;
        }
    }

    /// <summary>
    /// An engine over a line labeled "speed" of 100 rows, y = 0.25 · row, and an unlabeled line of y = 1, with the y domain
    /// [0, 30], 640 × 360, its first frame drawn, and its default hover tool.
    /// </summary>
    private sealed class HoverPlot : IDisposable
    {
        private readonly PlotWorkerPool _pool = new(2);
        private readonly Column<double> _speed;
        private readonly Column<double> _flat;

        private HoverPlot()
        {
            _speed = Column.Wrap(Enumerable.Range(0, 100).Select(i => i * 0.25).ToArray());
            _flat = Column.Wrap(Enumerable.Repeat(1.0, 100).ToArray());
            var table = DataTable.Create().Add("x", Column.Uniform(0, 1, 100)).Add("speed", _speed).Add("flat", _flat).Build();
            var spec = Plot.Create(table)
                .Line(table.Col<double>("x"), table.Col<double>("speed"), l => l.Label("speed"))
                .Line(table.Col<double>("x"), table.Col<double>("flat"))
                .ScaleY(Scale.Linear().WithDomain(0, 30))
                .Build();
            Engine = new PlotEngine(Backend, EngineFixture.Width, EngineFixture.Height, 1, _pool) { WaitsForIndexes = true };
            Engine.SetPlot(spec);
            Engine.RenderFrame();
            Tool = (HoverTool)Engine.Bindings.Single(b => b.Tool is HoverTool).Tool!;
        }

        public NullBackend Backend { get; } = new();

        public PlotEngine Engine { get; }

        public HoverTool Tool { get; }

        public static HoverPlot Make() => new();

        public void Dispose()
        {
            Engine.Dispose();
            _speed.Dispose();
            _flat.Dispose();
            _pool.Dispose();
        }
    }
}
