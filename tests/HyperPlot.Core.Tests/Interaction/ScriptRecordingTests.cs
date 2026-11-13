using System.Numerics;
using HyperPlot.Interaction;
using HyperPlot.Testing;

namespace HyperPlot.Tests;

/// <summary>
/// The input script, the recording and its replay (design §6.6, requirement I-9), and the allocation of the interaction's
/// frames (design §10, row I1a).
/// </summary>
[Collection("Large data")]
public sealed class ScriptRecordingTests
{
    /// <summary>
    /// A script's point of the data goes to its pixel through the current view, and a wait moves the time (design §6.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An engine over a line with x over [0, 999] and y over [−20, 20].
    ///
    /// Steps:
    /// - Zoom by 2 at the middle (animated); wait 200 ms; zoom by 2 more at the data point (600, 5) with the wheel of a
    ///   script; wait 200 ms.
    ///
    /// Expected:
    /// - After the first wait, the animation ended (the time moved 200 ms). The data point (600, 5) is under the same pixel
    ///   before and after the second zoom.
    /// </remarks>
    [Fact]
    public void Script_PlacesDataAndWaits()
    {
        using var f = new EngineFixture();
        new InputScript().Wheel(ScriptPoint.Pixel(f.Middle.X, f.Middle.Y), 1).Wait(TimeSpan.FromMilliseconds(200)).Run(f.Engine, f.Time.Advance);
        _ = f.Changes[^1].TryGet2D(out var target, out _);
        Assert.Equal(target, f.View().X);

        var at = f.Engine.ViewToPixel(EngineFixture.Panel, ViewRange.FromValues(600.0, 601.0), ViewRange.FromValues(5.0, 6.0));
        new InputScript().Wheel(ScriptPoint.Data(EngineFixture.Panel, 600, 5), 1).Wait(TimeSpan.FromMilliseconds(200)).Run(f.Engine, f.Time.Advance);
        var after = f.Engine.ViewToPixel(EngineFixture.Panel, ViewRange.FromValues(600.0, 601.0), ViewRange.FromValues(5.0, 6.0));
        Assert.Equal(at.X, after.X, 1e-3f);
        Assert.Equal(at.Y, after.Y, 1e-3f);
    }

    /// <summary>
    /// A recording plays back to the same views and notices (design §6.6): every kind of event written and read back as it
    /// was; a recording of another size refused.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two engines over the same line with the same time steps; a third of another size.
    ///
    /// Steps:
    /// - Record a drag, a wheel's turn, a key, a pinch, a scroll, and waits on the first; play the recording on the second
    ///   while recording it again; play it on the third.
    ///
    /// Expected:
    /// - The second ends at the first's view with the same reasons of the notices in order; its recording's lines equal the
    ///   first's after the header; the third throws InvalidOperationException.
    /// </remarks>
    [Fact]
    public void Recording_PlaysBackTheSame()
    {
        using var first = new EngineFixture();
        using var text = new StringWriter();
        first.Engine.StartRecording(text);
        var m = first.Middle;
        new InputScript()
            .Drag(ScriptPoint.Pixel(m.X, m.Y), ScriptPoint.Pixel(m.X + 70, m.Y + 20))
            .Wheel(ScriptPoint.Pixel(m.X - 30, m.Y), 2)
            .Wait(TimeSpan.FromMilliseconds(400))
            .Key(PlotKey.Right)
            .Pinch(ScriptPoint.Pixel(m.X, m.Y), 1.25)
            .Scroll(ScriptPoint.Pixel(m.X, m.Y), new Vector2(-15, 5))
            .Wait(TimeSpan.FromMilliseconds(400))
            .Run(first.Engine, first.Time.Advance);
        first.Engine.StopRecording();
        var recording = text.ToString();

        using var second = new EngineFixture();
        using var again = new StringWriter();
        second.Engine.StartRecording(again);
        InputReplay.Run(second.Engine, new StringReader(recording), second.Time.Advance);
        second.Engine.StopRecording();
        second.Settle();
        first.Settle();

        Assert.Equal(first.View(), second.View());
        Assert.Equal(first.Changes.Select(c => c.Reason), second.Changes.Select(c => c.Reason));
        Assert.Equal(Lines(recording)[1..], Lines(again.ToString())[1..]);
        Assert.Contains(Lines(recording), l => l.Contains(" pointer ", StringComparison.Ordinal));
        Assert.Contains(Lines(recording), l => l.Contains(" wheel ", StringComparison.Ordinal));
        Assert.Contains(Lines(recording), l => l.Contains(" key ", StringComparison.Ordinal));
        Assert.Contains(Lines(recording), l => l.Contains(" gesture ", StringComparison.Ordinal));

        using var other = new PlotEngine(new NullBackend(), 320, 200, 1, first.Pool, first.Time) { WaitsForIndexes = true };
        other.SetPlot(first.Spec);
        other.RenderFrame();
        _ = Assert.Throws<InvalidOperationException>(() => InputReplay.Run(other, new StringReader(recording), first.Time.Advance));
    }

    /// <summary>
    /// Once warm, the frames of a wheel's zooming and a drag's panning allocate nothing (design §10, row I1a, with row I1c's
    /// labels), nor the input that drives them.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An engine over a line with the fake text backend recording no calls, its shaping cache filled with 8,192 other texts.
    ///
    /// Steps:
    /// - Warm: zoom in and out by the wheel and pan by drags, each event and frame 16 ms apart, until a round allocates
    ///   nothing (at most 6 rounds); then one more round, counting the bytes of the input and the frames.
    ///
    /// Expected:
    /// - 0 bytes.
    /// </remarks>
    [Fact]
    public void Frames_OfZoomAndPan_AllocateNothing()
    {
        using var f = new EngineFixture();

        // The shaping cache full first, as after a long session (design §5.6): until then a new label takes a new entry.
        for (var i = 0; i < Rendering.TextEngine.CacheCapacity; i++)
        {
            _ = f.Backend.Text.Measure(
                "filler " + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new Rendering.TextStyle(new Color(0, 0, 0, 255), 9)
            );
        }

        // The test's own list of notices grows by doubling: room for every notice of the rounds, so that it allocates nothing.
        f.Changes.Capacity = 10_000;
        var bytes = -1L;
        for (var round = 0; round < 6 && bytes != 0; round++)
        {
            bytes = Round(f);
        }

        Assert.Equal(0, Round(f));
    }

    private static long Round(EngineFixture f)
    {
        var total = 0L;
        var m = f.Middle;
        var step = TimeSpan.FromMilliseconds(16);
        for (var i = 0; i < 40; i++)
        {
            f.Time.Advance(step);
            var notches = i < 20 ? 1 : -1;
            var e = new WheelEvent(m + new Vector2(i, 0), 0, notches, ModifierKeys.None, f.Time.GetTimestamp());
            var before = GC.GetAllocatedBytesForCurrentThread();
            f.Engine.OnWheel(e);
            f.Engine.RenderFrame();
            total += GC.GetAllocatedBytesForCurrentThread() - before;
        }

        for (var drag = 0; drag < 4; drag++)
        {
            var events = new PointerEvent[12];
            events[0] = f.Pointer(PointerEventKind.Down, m, PointerButtons.Left);
            for (var i = 1; i < 11; i++)
            {
                events[i] = f.Pointer(PointerEventKind.Move, m + new Vector2(i * (drag % 2 == 0 ? 6 : -6), i * 2)) with
                {
                    Buttons = PointerButtons.Left,
                };
            }

            events[11] = f.Pointer(PointerEventKind.Up, m, PointerButtons.Left);
            foreach (var e in events)
            {
                f.Time.Advance(step);
                var stamped = e with { Timestamp = f.Time.GetTimestamp() };
                var before = GC.GetAllocatedBytesForCurrentThread();
                f.Engine.OnPointer(stamped);
                f.Engine.RenderFrame();
                total += GC.GetAllocatedBytesForCurrentThread() - before;
            }
        }

        return total;
    }

    /// <summary>
    /// Once warm, the frames of a zoom over three decades allocate nothing (invariant 3, Issue #474): the y labels' width
    /// changes the panel's width W at many views, and the boundaries of the pixel columns, primary and secondary, and a
    /// flipped rule's boundaries of the pixel rows, are written into buffers kept at the widest W.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An engine of 640 × 360 on the null backend over 4,000 seeded points, y in increasing order, with x and y domains
    ///   [0, 100]: a point layer
    ///   (points); a point layer on a secondary x scale of domain [0, 100] (secondary); a horizontal rule of the y values
    ///   on a flipped coordinate system (flipped rule). A first frame drawn.
    /// - The script: 60 square views centered at (50, 50), of side 100 · 0.001^(i / 59), each set and drawn.
    ///
    /// Steps:
    /// - Run the script twice; collect the garbage; run it once more, counting each frame's bytes.
    ///
    /// Expected:
    /// - 0 bytes in every frame of the third run; no work failed.
    /// </remarks>
    /// <param name="kind">The layer: points, secondary, or flipped rule.</param>
    [Theory]
    [InlineData("points")]
    [InlineData("secondary")]
    [InlineData("flipped rule")]
    public void Frames_OfADeepZoom_AllocateNothing(string kind)
    {
        const int Count = 4000;
        var random = new Random(474);
        using var xs = Column.Wrap(Enumerable.Range(0, Count).Select(_ => random.NextDouble() * 100).ToArray());

        // In increasing order, which a rule's column needs (design §5.5.3).
        using var ys = Column.Wrap(Enumerable.Range(0, Count).Select(_ => random.NextDouble() * 100).Order().ToArray());
        var table = DataTable.Create().Add("x", xs).Add("y", ys).Build();
        var (x, y) = (table.Col<double>("x"), table.Col<double>("y"));
        var builder = Plot.Create(table).ScaleX(Scale.Linear().WithDomain(0, 100)).ScaleY(Scale.Linear().WithDomain(0, 100));
        builder = kind switch
        {
            "points" => builder.Point(x, y),
            "secondary" => builder.ScaleX2(Scale.Linear().WithDomain(0, 100)).Point(x, y, p => p.SecondaryX()),
            _ => builder.HRule(y).Coord(Coord.Cartesian(flip: true)),
        };
        using var engine = new PlotEngine(new NullBackend(), 640, 360);
        var failures = 0;
        engine.WorkFailed += (in WorkFailure _) => failures++;
        engine.SetPlot(builder.Build());
        engine.RenderFrame();
        var views = new PanelView2D[60];
        for (var i = 0; i < views.Length; i++)
        {
            var half = 50 * Math.Pow(0.001, i / 59.0);
            views[i] = new PanelView2D(ViewRange.FromValues(50 - half, 50 + half), ViewRange.FromValues(50 - half, 50 + half));
        }

        var bytes = new long[views.Length];
        for (var round = 0; round < 3; round++)
        {
            if (round == 2)
            {
                GC.Collect();
            }

            for (var i = 0; i < views.Length; i++)
            {
                engine.Views.SetView(EngineFixture.Panel, views[i]);
                var before = GC.GetAllocatedBytesForCurrentThread();
                engine.RenderFrame();
                bytes[i] = GC.GetAllocatedBytesForCurrentThread() - before;
            }
        }

        Assert.Equal(new long[views.Length], bytes);
        Assert.Equal(0, failures);
    }

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
