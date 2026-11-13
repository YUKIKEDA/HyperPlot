using System.Numerics;
using TerraFX.Interop.DirectX;

namespace HyperPlot.Rendering.D3D11.Tests;

/// <summary>
/// The host's surfaces (design §7.2, §10, row W1a): <see cref="D3D11SharedSurface"/> hands copies to a Direct3D 9Ex texture
/// without waiting, and <see cref="D3D11ReadbackSurface"/> reads them back without waiting; and the backend on an adapter.
/// </summary>
public sealed class SurfaceTests
{
    private const int Width = 64;
    private const int Height = 48;
    private const int Polls = 1_000_000;

    private static readonly Color Red = new(255, 0, 0, 255);
    private static readonly Color Green = new(0, 255, 0, 255);
    private static readonly Color Blue = new(0, 0, 255, 255);

    /// <summary>
    /// A backend made on an adapter's identifier uses that adapter (design §7.2); an identifier no adapter has falls back to
    /// WARP, and WARP has an identifier of its own.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A Direct3D 9Ex device on adapter 0.
    ///
    /// Steps:
    /// - Make a backend on adapter 0's identifier; one on an identifier no adapter has; a WARP backend.
    ///
    /// Expected:
    /// - The first's AdapterLuid is adapter 0's. The second is WARP, with WARP's identifier. WARP's is not 0.
    /// </remarks>
    [Fact]
    public void Backend_UsesTheAdapterAskedFor()
    {
        using var d3d9 = new D3D9Reader();
        using var onAdapter = D3D11Backend.Create(adapterLuid: d3d9.AdapterLuid);
        Assert.Equal(d3d9.AdapterLuid, onAdapter.AdapterLuid);

        using var warp = D3D11Backend.Create(useWarp: true);
        Assert.NotEqual(0, warp.AdapterLuid);
        using var missing = D3D11Backend.Create(adapterLuid: 0x7FFF_FFFF_7FFF_FFFF);
        Assert.True(missing.IsWarp);
        Assert.Equal(warp.AdapterLuid, missing.AdapterLuid);
    }

    /// <summary>
    /// A copy is handed over once the GPU has made it, and its texture opened in Direct3D 9Ex holds the render target's
    /// pixels (design §7.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A backend on adapter 0 (a shared texture opens on its own adapter only); a 64 × 48 target cleared to blue with a red
    ///   line across.
    ///
    /// Steps:
    /// - Before any copy, take; present the target; take until a copy is done; open its handle and read it in Direct3D 9Ex.
    ///
    /// Expected:
    /// - The first take is false. A take is true within the polls, with a handle that is not 0; the pixels read, made
    ///   straight, are the target's read back.
    /// </remarks>
    [Fact]
    public void Shared_HandsOverTheCopy()
    {
        using var d3d9 = new D3D9Reader();
        using var backend = D3D11Backend.Create(adapterLuid: d3d9.AdapterLuid);
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(Width, Height));
        target.Clear(Blue);
        var list = new RenderList();
        list.DrawPolyline([new Vector2(2, 2), new Vector2(Width - 2, Height - 2)], new LineStyle(Red, 3));
        backend.Draw(target, list);
        using var surface = new D3D11SharedSurface(backend, Width, Height);
        Assert.False(surface.TryTakeCompleted(out _));

        surface.Present(target);
        var handle = Take(surface);
        Assert.NotEqual(0, handle);
        Assert.Equal(Straight(target), Rgba(d3d9.Read(handle, Width, Height)));
    }

    /// <summary>
    /// A copy goes to the texture the host does not hold, and the take gives the newest finished copy (design §7.2): the held
    /// texture keeps what it shows while the next copy is made.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A backend on adapter 0 and a 64 × 48 target; a surface.
    ///
    /// Steps:
    /// - Present red and present green, wait for the GPU (a read back), then take. Present blue; read the held texture; take; read the new one. Present red
    ///   and green in a row and wait; read the held texture; take; read it.
    ///
    /// Expected:
    /// - The first take gives green (the newer of two done). After presenting blue, the held texture is still green; the next take
    ///   gives the other handle, blue. After the two presents, the held texture is still blue (both went to the other
    ///   texture, though blue's was then the older copy); the take gives green's handle, green.
    /// </remarks>
    [Fact]
    public void Shared_CopiesToTheTextureNotHeld()
    {
        using var d3d9 = new D3D9Reader();
        using var backend = D3D11Backend.Create(adapterLuid: d3d9.AdapterLuid);
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(Width, Height));
        using var surface = new D3D11SharedSurface(backend, Width, Height);
        target.Clear(Red);
        surface.Present(target);
        target.Clear(Green);
        surface.Present(target);

        // A read back waits for the GPU: both copies are done, and the take gives the newer.
        _ = Straight(target);
        var green = Take(surface);
        Assert.Equal(Fill(Green), Rgba(d3d9.Read(green, Width, Height)));

        target.Clear(Blue);
        surface.Present(target);
        Assert.Equal(Fill(Green), Rgba(d3d9.Read(green, Width, Height)));
        var blue = Take(surface);
        Assert.NotEqual(green, blue);
        Assert.Equal(Fill(Blue), Rgba(d3d9.Read(blue, Width, Height)));

        // The host holds blue, now the older copy: two copies in a row both go to the other texture, never to blue's.
        target.Clear(Red);
        surface.Present(target);
        target.Clear(Green);
        surface.Present(target);
        _ = Straight(target);
        Assert.Equal(Fill(Blue), Rgba(d3d9.Read(blue, Width, Height)));
        var last = Take(surface);
        Assert.Equal(green, last);
        Assert.Equal(Fill(Green), Rgba(d3d9.Read(last, Width, Height)));
    }

    /// <summary>
    /// The newest copy stays unread until it is handed over, even when the older one is handed over first (design §7.2, row
    /// W1b1): a host takes until the last frame it presented is shown.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A backend on adapter 0, a 64 × 48 target, and a surface.
    ///
    /// Steps:
    /// - Present the target cleared to red, then cleared to green; take until nothing is unread.
    ///
    /// Expected:
    /// - Nothing is unread before the copies and something is after them. The takes end within the polls, and the last
    ///   handle taken holds green.
    /// </remarks>
    [Fact]
    public void Shared_KeepsTheNewestUnreadUntilTaken()
    {
        using var d3d9 = new D3D9Reader();
        using var backend = D3D11Backend.Create(adapterLuid: d3d9.AdapterLuid);
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(Width, Height));
        using var surface = new D3D11SharedSurface(backend, Width, Height);
        Assert.False(surface.HasUnread);

        target.Clear(Red);
        surface.Present(target);
        target.Clear(Green);
        surface.Present(target);
        Assert.True(surface.HasUnread);
        nint last = 0;
        for (var i = 0; i < Polls && surface.HasUnread; i++)
        {
            if (surface.TryTakeCompleted(out var handle))
            {
                last = handle;
            }
        }

        Assert.False(surface.HasUnread);
        Assert.Equal(Fill(Green), Rgba(d3d9.Read(last, Width, Height)));
    }

    /// <summary>A surface copies a target of its own backend and size only (design §7.2).</summary>
    /// <remarks>
    /// Preconditions:
    /// - Two WARP backends; a 64 × 48 shared surface and readback surface on the first.
    ///
    /// Steps:
    /// - Present a target of the second backend; a 32 × 48 target of the first; a null target.
    ///
    /// Expected:
    /// - ArgumentException, ArgumentException, ArgumentNullException, from both surfaces.
    /// </remarks>
    [Fact]
    public void Present_OwnTargetsOnly()
    {
        using var backend = D3D11Backend.Create(useWarp: true);
        using var other = D3D11Backend.Create(useWarp: true);
        using var foreign = other.CreateRenderTarget(new RenderTargetDescription(Width, Height));
        using var narrow = backend.CreateRenderTarget(new RenderTargetDescription(32, Height));
        using var shared = new D3D11SharedSurface(backend, Width, Height);
        using var readback = new D3D11ReadbackSurface(backend, Width, Height);
        foreach (var present in new Action<IRenderTarget>[] { shared.Present, readback.Present })
        {
            _ = Assert.Throws<ArgumentException>(() => present(foreign));
            _ = Assert.Throws<ArgumentException>(() => present(narrow));
            _ = Assert.Throws<ArgumentNullException>(() => present(null!));
        }
    }

    /// <summary>
    /// The readback gives the newest finished copy once, without waiting (design §7.2): the pixels are the target's,
    /// premultiplied B8G8R8A8, row after row the stride apart.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend; a 64 × 48 target cleared to green with a red line; a readback surface.
    ///
    /// Steps:
    /// - Read before any copy; present; read until done into a buffer with a stride of 64 · 4 + 16 filled with 7; read again.
    ///
    /// Expected:
    /// - The first read is false. A read is true within the polls; each row's pixels, made straight, are the target's, and the
    ///   16 bytes after each row stay 7. The read after is false: the copy was read.
    /// </remarks>
    [Fact]
    public void Readback_ReadsTheNewestCopyOnce()
    {
        using var backend = D3D11Backend.Create(useWarp: true);
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(Width, Height));
        target.Clear(Green);
        var list = new RenderList();
        list.DrawPolyline([new Vector2(2, Height - 2), new Vector2(Width - 2, 2)], new LineStyle(Red, 3));
        backend.Draw(target, list);
        using var surface = new D3D11ReadbackSurface(backend, Width, Height);
        const int Stride = (Width * 4) + 16;
        var buffer = new byte[Stride * Height];
        buffer.AsSpan().Fill(7);
        Assert.False(surface.TryRead(buffer, Stride));

        surface.Present(target);
        var read = false;
        for (var i = 0; i < Polls && !read; i++)
        {
            read = surface.TryRead(buffer, Stride);
        }

        Assert.True(read);
        var rows = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        {
            buffer.AsSpan(y * Stride, Width * 4).CopyTo(rows.AsSpan(y * Width * 4));
            Assert.All(buffer.AsSpan((y * Stride) + (Width * 4), 16).ToArray(), b => Assert.Equal(7, b));
        }

        Assert.Equal(Straight(target), Rgba(rows));
        Assert.False(surface.TryRead(buffer, Stride));
    }

    /// <summary>
    /// The newest copy stays unread until it is read, even when the older one is read first (design §7.2): a host polls
    /// until the last frame it presented is shown.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend; a 64 × 48 target cleared to green and one cleared to red; a readback surface.
    ///
    /// Steps:
    /// - Present the green one, then the red one; read until nothing is unread.
    ///
    /// Expected:
    /// - Nothing is unread before the copies and something is after them. The reads end within the polls, and the pixels
    ///   are the red target's.
    /// </remarks>
    [Fact]
    public void Readback_KeepsTheNewestUnreadUntilRead()
    {
        using var backend = D3D11Backend.Create(useWarp: true);
        using var green = backend.CreateRenderTarget(new RenderTargetDescription(Width, Height));
        using var red = backend.CreateRenderTarget(new RenderTargetDescription(Width, Height));
        green.Clear(Green);
        red.Clear(Red);
        using var surface = new D3D11ReadbackSurface(backend, Width, Height);
        var buffer = new byte[Width * Height * 4];
        Assert.False(surface.HasUnread);

        surface.Present(green);
        surface.Present(red);
        Assert.True(surface.HasUnread);
        for (var i = 0; i < Polls && surface.HasUnread; i++)
        {
            _ = surface.TryRead(buffer, Width * 4);
        }

        Assert.False(surface.HasUnread);
        Assert.Equal(Straight(red), Rgba(buffer));
    }

    /// <summary>A readback checks its destination before it reads (design §7.2).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend and a 64 × 48 readback surface.
    ///
    /// Steps:
    /// - Read with a stride of 64 · 4 − 1; with a destination one byte short of 47 strides and a row.
    ///
    /// Expected:
    /// - ArgumentOutOfRangeException; ArgumentException.
    /// </remarks>
    [Fact]
    public void Readback_ChecksItsDestination()
    {
        using var backend = D3D11Backend.Create(useWarp: true);
        using var surface = new D3D11ReadbackSurface(backend, Width, Height);
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => surface.TryRead(new byte[Width * Height * 4], (Width * 4) - 1));
        _ = Assert.Throws<ArgumentException>(() => surface.TryRead(new byte[(Width * 4 * Height) - 1], Width * 4));
    }

    /// <summary>
    /// A device loss makes the surfaces again with the device (design §5.3, §7.2): a new handle, which holds the next copy.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A backend on adapter 0, a target, a shared surface whose first copy (red) was taken; a readback surface.
    ///
    /// Steps:
    /// - Make GetDeviceRemovedReason say DEVICE_REMOVED; present (throws); take and read; remove the seam; clear the target
    ///   to blue; present to both; take; read back.
    ///
    /// Expected:
    /// - The present throws GpuDeviceLostException; the take and the read during the loss are false. After, a take gives a
    ///   handle whose pixels are blue, and the readback gives blue.
    /// </remarks>
    [Fact]
    public void DeviceLoss_MakesTheSurfacesAgain()
    {
        using var d3d9 = new D3D9Reader();
        using var backend = D3D11Backend.Create(adapterLuid: d3d9.AdapterLuid);
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(Width, Height));
        using var shared = new D3D11SharedSurface(backend, Width, Height);
        using var readback = new D3D11ReadbackSurface(backend, Width, Height);
        var buffer = new byte[Width * Height * 4];
        target.Clear(Red);
        shared.Present(target);
        _ = Take(shared);

        backend.RemovedReason = () => DXGI.DXGI_ERROR_DEVICE_REMOVED;
        _ = Assert.Throws<GpuDeviceLostException>(() => shared.Present(target));
        Assert.False(shared.TryTakeCompleted(out _));
        Assert.False(readback.TryRead(buffer, Width * 4));
        backend.RemovedReason = null;

        target.Clear(Blue);
        shared.Present(target);
        readback.Present(target);
        Assert.Equal(Fill(Blue), Rgba(d3d9.Read(Take(shared), Width, Height)));
        var read = false;
        for (var i = 0; i < Polls && !read; i++)
        {
            read = readback.TryRead(buffer, Width * 4);
        }

        Assert.True(read);
        Assert.Equal(Fill(Blue), Rgba(buffer));
    }

    /// <summary>
    /// A host's frame allocates nothing once warm (design §7.2, rule perf): the engine's draw and the copy to a shared texture
    /// with its take, and the same with a read back. WPF's own calls (<c>D3DImage.Lock</c>, <c>SetBackBuffer</c>,
    /// <c>WriteableBitmap.Lock</c>) allocate, the design's exception; this test runs HyperPlot's part of the frame alone, on
    /// a thread without WPF.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend; an engine of 320 × 200 over a line of 1,000 points, drawn; a shared and a readback surface of its
    ///   size; animations off.
    ///
    /// Steps:
    /// - Rounds of 20 frames, each a pan of 2 pixels, the draw and the copy in one frame of the backend, and the takes or
    ///   reads until the copy is done, until a round allocates nothing (at most 6), then a garbage collection and one more;
    ///   for each surface.
    ///
    /// Expected:
    /// - The last round of each: 0 bytes.
    /// </remarks>
    [Fact]
    public void HostFrame_AllocatesNothingOnceWarm()
    {
        using var backend = D3D11Backend.Create(useWarp: true);
        using var y = Column.Wrap(Enumerable.Range(0, 1000).Select(i => MathF.Sin(i * 0.02f) * 10).ToArray());
        var table = DataTable.Create().Add("x", Column.Uniform(0, 1, 1000)).Add("y", y).Build();
        using var engine = new Interaction.PlotEngine(backend, 320, 200);
        engine.Options.ZoomAnimation = TimeSpan.Zero;
        engine.SetPlot(Plot.Create(table).Line(table.Col<double>("x"), table.Col<float>("y")).Build());
        engine.RenderFrame();
        using var shared = new D3D11SharedSurface(backend, 320, 200);
        using var readback = new D3D11ReadbackSurface(backend, 320, 200);
        var buffer = new byte[320 * 200 * 4];
        Assert.Equal(0, Warm(() => _ = Take(shared), present: shared.Present));
        Assert.Equal(0, Warm(() => Read(readback, buffer), present: readback.Present));

        long Warm(Action show, Action<IRenderTarget> present)
        {
            var bytes = -1L;
            for (var i = 0; i < 6 && bytes != 0; i++)
            {
                bytes = Round(show, present);
            }

            // An allocation that follows a collection is counted in every run (.cursor/rules/bench.mdc).
            GC.Collect();
            return Round(show, present);
        }

        long Round(Action show, Action<IRenderTarget> present)
        {
            var total = 0L;
            for (var i = 0; i < 20; i++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                engine.Views.Pan(new Interaction.PanelId(0, 0), new Vector2(i % 2 == 0 ? 2 : -2, 0));
                using (backend.BeginFrame())
                {
                    engine.RenderFrame();
                    present(engine.Target);
                }

                show();
                total += GC.GetAllocatedBytesForCurrentThread() - before;
            }

            return total;
        }

        static void Read(D3D11ReadbackSurface surface, byte[] buffer)
        {
            for (var i = 0; i < Polls; i++)
            {
                if (surface.TryRead(buffer, 320 * 4))
                {
                    return;
                }
            }

            Assert.Fail($"No copy was read after {Polls} polls.");
        }
    }

    private static nint Take(D3D11SharedSurface surface)
    {
        for (var i = 0; i < Polls; i++)
        {
            if (surface.TryTakeCompleted(out var handle))
            {
                return handle;
            }
        }

        Assert.Fail($"No copy was done after {Polls} polls.");
        return 0;
    }

    /// <summary>Returns a target's pixels as ReadPixels gives them: straight RGBA.</summary>
    private static byte[] Straight(IRenderTarget target)
    {
        var pixels = new byte[Width * Height * 4];
        target.ReadPixels(pixels);
        return pixels;
    }

    /// <summary>Returns premultiplied B8G8R8A8 pixels as straight RGBA, as ReadPixels gives them.</summary>
    private static byte[] Rgba(byte[] bgra)
    {
        var rgba = new byte[bgra.Length];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var alpha = bgra[i + 3];
            rgba[i] = Premultiplied.Divide(bgra[i + 2], alpha);
            rgba[i + 1] = Premultiplied.Divide(bgra[i + 1], alpha);
            rgba[i + 2] = Premultiplied.Divide(bgra[i], alpha);
            rgba[i + 3] = alpha;
        }

        return rgba;
    }

    private static byte[] Fill(Color color)
    {
        var pixels = new byte[Width * Height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            // Four statements, not one tuple assignment: in the Release build of this project the tuple form left R at 0 in
            // every pixel of a reference (RectTests.Reference; Issue #117).
            pixels[i] = color.R;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.B;
            pixels[i + 3] = color.A;
        }

        return pixels;
    }
}
