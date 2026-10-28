using System.Numerics;
using TerraFX.Interop.DirectX;

namespace HyperPlot.Rendering.D3D11.Tests;

/// <summary>Frames, their scopes, and their GPU time on WARP (design §5.2, §5.3, §10 "フレームと GPU 時間（S2）").</summary>
public sealed class FrameTests
{
    private const int MaxFrames = 400;

    /// <summary>
    /// Drawing with no read back still brings GPU times, numbered by outermost frames only, in order, before the frame that
    /// asks, and above 0 (design §5.2, §5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend, a 64 × 64 target, a list with one line; every time read is recorded.
    ///
    /// Steps:
    /// - 100 outer frames, each with a clear and two draws inside; then bare draws, each its own frame, until a time of a
    ///   bare frame arrives (at most 400).
    ///
    /// Expected:
    /// - Times arrive with no ReadPixels; their numbers increase and stay below the frames opened; their durations are
    ///   above 0 and below 1 s (seconds, not GPU ticks); a time numbered 100 or above arrives, so inner calls did not take numbers (else the 100 outer frames
    ///   would have used 300).
    /// </remarks>
    [Fact]
    public void LastFrameTime_DrawsOnly_ArrivesInOrder()
    {
        using var backend = D3D11Backend.Create(useWarp: true);
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(64, 64));
        var list = Line();
        var times = new List<GpuFrameTime>();
        backend.Timer.TestTimed = times.Add;
        long opened = 0;

        for (var i = 0; i < 100; i++)
        {
            using var frame = backend.BeginFrame();
            target.Clear(new Color(255, 255, 255, 255));
            backend.Draw(target, list);
            backend.Draw(target, list);
            opened++;
        }

        // CreateRenderTarget opened frame 0 before the loop, so the loop's frames are 1 to 100.
        for (var i = 0; i < MaxFrames && !times.Exists(t => t.Frame > 100); i++)
        {
            backend.Draw(target, list);
            opened++;
        }

        Assert.NotEmpty(times);
        Assert.Contains(times, t => t.Frame > 100);
        Assert.All(times, t => Assert.InRange(t.Duration, TimeSpan.FromTicks(1), TimeSpan.FromSeconds(1)));
        Assert.Equal(times.Select(t => t.Frame).Order(), times.Select(t => t.Frame));
        Assert.True(times[^1].Frame <= opened, $"A time of frame {times[^1].Frame} came after {opened} frames opened.");
        Assert.Equal(times[^1], backend.LastFrameTime);
    }

    /// <summary>
    /// Scopes close what they opened and never throw for the order: closing an outer frame closes the inner ones; closed,
    /// default, and twice-disposed scopes do nothing (design §5.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend.
    ///
    /// Steps:
    /// - Open an outer and an inner frame; close the outer; close the inner; dispose the outer again and a default scope;
    ///   open a frame.
    ///
    /// Expected:
    /// - 2 frames open, then 0 after the outer closes; no exception; the next frame opens at depth 1.
    /// </remarks>
    [Fact]
    public void Scopes_AnyOrder_CloseWhatTheyOpenedAndNeverThrow()
    {
        using var backend = D3D11Backend.Create(useWarp: true);
        var outer = backend.BeginFrame();
        var inner = backend.BeginFrame();
        Assert.Equal(2, backend.OpenFrames);

        outer.Dispose();
        Assert.Equal(0, backend.OpenFrames);
        inner.Dispose();
        outer.Dispose();
        default(FrameScope).Dispose();
        backend.EndFrame(12345);

        using var next = backend.BeginFrame();
        Assert.Equal(1, backend.OpenFrames);
        Assert.NotEqual(outer, next);
    }

    /// <summary>
    /// A loss inside a call's own frame leaves nothing open; a frame that saw a loss gets no time even when an inner call
    /// made the device again, and closing it does not throw (design §5.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend and a target; every time read is recorded.
    ///
    /// Steps:
    /// - Lose the device through Map in a bare ReadPixels; then open an outer frame, lose the device in a ReadPixels
    ///   inside, clear inside (which makes the device again), and close the outer frame; then draw until times arrive.
    ///
    /// Expected:
    /// - No frame is open after the bare loss; the outer frame closes without an exception; its number never appears
    ///   among the times; times arrive afterwards, from the queries made again.
    /// </remarks>
    [Fact]
    public void Loss_InsideFrames_LeavesNothingOpenAndNoTime()
    {
        using var backend = D3D11Backend.Create(useWarp: true);
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(8, 8));
        var times = new List<GpuFrameTime>();
        backend.Timer.TestTimed = times.Add;
        var pixels = new byte[8 * 8 * 4];
        backend.CheckResult = (call, result) => call == "Map" ? DXGI.DXGI_ERROR_DEVICE_REMOVED : result;
        _ = Assert.Throws<GpuDeviceLostException>(() => target.ReadPixels(pixels));
        Assert.Equal(0, backend.OpenFrames);

        // The frames so far: 0 (CreateRenderTarget), 1 (the ReadPixels that lost the device). The outer frame is 2.
        var outer = backend.BeginFrame();
        _ = Assert.Throws<GpuDeviceLostException>(() => target.ReadPixels(pixels));
        backend.CheckResult = null;
        target.Clear(new Color(1, 2, 3, 255));
        outer.Dispose();
        Assert.Equal(0, backend.OpenFrames);

        var list = Line();
        for (var i = 0; i < MaxFrames && times.Count == 0; i++)
        {
            backend.Draw(target, list);
        }

        Assert.NotEmpty(times);
        Assert.DoesNotContain(times, t => t.Frame == 2);
    }

    /// <summary>
    /// Closing a frame never waits for the GPU: with every query read as not ready, the ring fills, frames go on untimed,
    /// and times come again when the queries are ready (design §5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend and a target; the timer reads every query as not ready.
    ///
    /// Steps:
    /// - Draw 20 frames; let the queries be read; draw until a time arrives.
    ///
    /// Expected:
    /// - The 20 frames end with no time; afterwards the first 4 times are those of frames 0 to 3, the 4 the ring held.
    /// </remarks>
    [Fact]
    public void Close_RingFull_GoesOnWithoutWaiting()
    {
        using var backend = D3D11Backend.Create(useWarp: true);
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(8, 8));
        var times = new List<GpuFrameTime>();
        backend.Timer.TestTimed = times.Add;
        backend.Timer.TestHold = _ => true;
        var list = Line();

        for (var i = 0; i < 20; i++)
        {
            backend.Draw(target, list);
        }

        Assert.Empty(times);
        Assert.Null(backend.LastFrameTime);
        backend.Timer.TestHold = null;
        for (var i = 0; i < MaxFrames && times.Count < 4; i++)
        {
            backend.Draw(target, list);
        }

        // Frame 0 was CreateRenderTarget's; the ring held the first 4 frames, 0 to 3, while every read was held.
        Assert.Equal([0L, 1, 2, 3], times.Take(4).Select(t => t.Frame));
    }

    /// <summary>
    /// Times arrive in the order of the frames: while an older frame's queries are not ready, the newer frames' times wait
    /// for it, even if theirs are (design §5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend and a target; the timer reads the queries of frame 2 as not ready.
    ///
    /// Steps:
    /// - Draw 20 frames; let frame 2 be read; draw until a time of a frame above 2 arrives.
    ///
    /// Expected:
    /// - While frame 2 is held, no time of a frame above 2 arrives; afterwards the times go on from frame 2, in order.
    /// </remarks>
    [Fact]
    public void Poll_OlderFrameNotReady_NewerTimesWait()
    {
        using var backend = D3D11Backend.Create(useWarp: true);
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(8, 8));
        var times = new List<GpuFrameTime>();
        backend.Timer.TestTimed = times.Add;
        backend.Timer.TestHold = frame => frame == 2;
        var list = Line();

        for (var i = 0; i < 20; i++)
        {
            backend.Draw(target, list);
        }

        Assert.All(times, t => Assert.True(t.Frame < 2, $"Frame {t.Frame} came while frame 2 was held."));
        var before = times.Count;
        backend.Timer.TestHold = null;
        for (var i = 0; i < MaxFrames && !times.Exists(t => t.Frame > 2); i++)
        {
            backend.Draw(target, list);
        }

        Assert.Equal(2, times[before].Frame);
        Assert.Equal(times.Select(t => t.Frame).Order(), times.Select(t => t.Frame));
    }

    /// <summary>
    /// A frame whose GPU clock was disjoint gets no time, and its query set is free again: times come when the clock is
    /// steady (design §5.2, §5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend and a target; the timer reads every set as disjoint (WARP never is).
    ///
    /// Steps:
    /// - Draw 100 frames; let the sets read as steady; draw until a time arrives.
    ///
    /// Expected:
    /// - The 100 frames bring no time and LastFrameTime stays null; afterwards a time arrives.
    /// </remarks>
    [Fact]
    public void Disjoint_Frames_GetNoTime()
    {
        using var backend = D3D11Backend.Create(useWarp: true);
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(8, 8));
        var times = new List<GpuFrameTime>();
        backend.Timer.TestTimed = times.Add;
        backend.Timer.TestDisjoint = true;
        var list = Line();

        for (var i = 0; i < 100; i++)
        {
            backend.Draw(target, list);
        }

        Assert.Empty(times);
        Assert.Null(backend.LastFrameTime);
        backend.Timer.TestDisjoint = false;
        for (var i = 0; i < MaxFrames && times.Count == 0; i++)
        {
            backend.Draw(target, list);
        }

        Assert.NotEmpty(times);
    }

    /// <summary>
    /// Drawing frames again allocates nothing, timing included (design §5.2, invariant 3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend, a 64 × 64 target, a list with one line; 50 frames to warm up.
    ///
    /// Steps:
    /// - 50 outer frames, each with a clear and a draw, reading LastFrameTime.
    ///
    /// Expected:
    /// - 0 bytes on this thread.
    /// </remarks>
    [Fact]
    public void Frames_SteadyState_AllocateNothing()
    {
        using var backend = D3D11Backend.Create(useWarp: true);
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(64, 64));
        var list = Line();
        long sink = 0;
        for (var i = 0; i < 50; i++)
        {
            Frame();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 50; i++)
        {
            Frame();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(sink >= 0);

        void Frame()
        {
            using var frame = backend.BeginFrame();
            target.Clear(new Color(255, 255, 255, 255));
            backend.Draw(target, list);
            sink += backend.LastFrameTime?.Frame ?? 0;
        }
    }

    private static RenderList Line()
    {
        var list = new RenderList();
        list.DrawPolyline([new Vector2(2, 2), new Vector2(60, 50)], new LineStyle(new Color(0, 0, 0, 255), 2));
        return list;
    }
}
