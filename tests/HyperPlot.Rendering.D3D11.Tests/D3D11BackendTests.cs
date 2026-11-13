using HyperPlot.Testing;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace HyperPlot.Rendering.D3D11.Tests;

/// <summary>The D3D11 backend on WARP (design §5.2, §5.3, §10). Windows only.</summary>
public sealed class D3D11BackendTests
{
    private static readonly Color[] Colors = [new(255, 0, 0, 255), new(10, 200, 30, 128), new(43, 43, 43, 6), new(0, 0, 0, 0), new(255, 255, 255, 1)];

    public static TheoryData<int> LostResults =>
        [DXGI.DXGI_ERROR_DEVICE_REMOVED, DXGI.DXGI_ERROR_DEVICE_RESET, DXGI.DXGI_ERROR_DEVICE_HUNG, DXGI.DXGI_ERROR_DRIVER_INTERNAL_ERROR];

    /// <summary>
    /// A clear reads back as the integer premultiply and un-premultiply of design §5.2 give it, at every size.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend; targets of 1 × 1, 3 × 5 (an odd width, so that RowPitch differs from the width), and 16,384 × 1.
    /// - Opaque, half-transparent, nearly transparent (43, 43, 43, 6, where the division is exactly one half), transparent,
    ///   and alpha 1 colors.
    ///
    /// Steps:
    /// - Clear each target with each color and read it back.
    ///
    /// Expected:
    /// - IsWarp is true; every pixel equals (k · 255 + a / 2) / a for k = (c · a + 127) / 255, computed here, and 0 when
    ///   a is 0; alpha equals a.
    /// </remarks>
    [Fact]
    public void ClearThenRead_Colors_GiveTheIntegerForms()
    {
        using var backend = D3D11Backend.Create(useWarp: true);
        Assert.True(backend.IsWarp);
        foreach (var (width, height) in new[] { (1, 1), (3, 5), (RenderTargetDescription.MaxSide, 1) })
        {
            using var target = backend.CreateRenderTarget(new RenderTargetDescription(width, height));
            var pixels = new byte[width * height * 4];
            foreach (var color in Colors)
            {
                target.Clear(color);
                target.ReadPixels(pixels);
                byte[] expected = [Expect(color.R, color.A), Expect(color.G, color.A), Expect(color.B, color.A), color.A];
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    Assert.Equal(expected, pixels[i..(i + 4)]);
                }
            }
        }

        static byte Expect(byte c, byte a)
        {
            if (a == 0)
            {
                return 0;
            }

            var k = ((c * a) + 127) / 255;
            return (byte)Math.Min(255, ((k * 255) + (a / 2)) / a);
        }
    }

    /// <summary>
    /// Reading back again allocates nothing: the staging texture is made once (design §5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend and a 64 × 64 target, cleared; one read to warm up.
    ///
    /// Steps:
    /// - Read 20 times as a warm-up; collect; read 20 times more, counting the bytes.
    ///
    /// Expected:
    /// - 0 bytes on this thread.
    /// </remarks>
    [Fact]
    public void ReadPixels_SecondCall_AllocatesNothing()
    {
        using var backend = D3D11Backend.Create(useWarp: true);
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(64, 64));
        var pixels = new byte[64 * 64 * 4];
        target.Clear(new Color(1, 2, 3, 255));
        target.ReadPixels(pixels);

        PerformanceAssert.DoesNotAllocate(() =>
        {
            for (var i = 0; i < 20; i++)
            {
                target.ReadPixels(pixels);
            }
        });
    }

    /// <summary>
    /// Sizes outside 1 to 16,384, a depth texture, and a short destination throw as design §5.2 says.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend.
    ///
    /// Steps:
    /// - Make targets of width 0, 16,385, height 0, 16,385, and with a float depth; read a 2 × 2 target into 15 bytes.
    ///
    /// Expected:
    /// - ArgumentOutOfRangeException for each size, NotSupportedException for the depth, ArgumentException for the read.
    /// </remarks>
    [Fact]
    public void Arguments_OutOfRange_Throw()
    {
        using var backend = D3D11Backend.Create(useWarp: true);
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.CreateRenderTarget(new RenderTargetDescription(0, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.CreateRenderTarget(new RenderTargetDescription(16385, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.CreateRenderTarget(new RenderTargetDescription(1, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.CreateRenderTarget(new RenderTargetDescription(1, 16385)));
        Assert.Throws<NotSupportedException>(() => backend.CreateRenderTarget(new RenderTargetDescription(1, 1, DepthFormat.Float32)));
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(2, 2));
        Assert.Throws<ArgumentException>(() => target.ReadPixels(new byte[15]));
    }

    /// <summary>
    /// A lost device seen by a checked call throws GpuDeviceLostException with the reason; the next frame makes the device
    /// and the target again, transparent (design §5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend and a 4 × 4 target cleared red; the seam makes the next Map return the HRESULT under test, and the
    ///   removed reason DEVICE_HUNG.
    ///
    /// Steps:
    /// - Open an outer frame and read; then the same with a removed reason of S_OK; after each loss, remove the seam, close
    ///   the outer frame, and read again;
    ///   then clear blue and read.
    ///
    /// Expected:
    /// - The read throws GpuDeviceLostException with HResult the injected result and RemovedReason DEVICE_HUNG, or the
    ///   injected result when the reason is S_OK; closing an outer frame opened before the loss, while lost, does nothing; the read after the loss is all 0; the
    ///   blue clear reads back blue.
    /// </remarks>
    [Theory]
    [MemberData(nameof(LostResults))]
    public void CheckedCall_DeviceLost_ThrowsAndMakesAgain(int lost)
    {
        foreach (var reason in new[] { DXGI.DXGI_ERROR_DEVICE_HUNG, S.S_OK })
        {
            using var backend = D3D11Backend.Create(useWarp: true);
            using var target = backend.CreateRenderTarget(new RenderTargetDescription(4, 4));
            var pixels = new byte[4 * 4 * 4];
            target.Clear(new Color(255, 0, 0, 255));
            backend.CheckResult = (call, result) => call == "Map" ? lost : result;
            backend.RemovedReason = () => reason;

            var outer = backend.BeginFrame();
            var exception = Assert.Throws<GpuDeviceLostException>(() => target.ReadPixels(pixels));

            Assert.Equal((lost, reason == S.S_OK ? lost : reason), (exception.HResult, exception.RemovedReason));
            backend.CheckResult = null;
            backend.RemovedReason = null;
            outer.Dispose();
            target.ReadPixels(pixels);
            Assert.All(pixels, p => Assert.Equal(0, p));
            target.Clear(new Color(0, 0, 255, 255));
            target.ReadPixels(pixels);
            Assert.Equal([0, 0, 255, 255], pixels[..4]);
        }
    }

    /// <summary>
    /// A frame of clears only sees a loss at its end, where EndFrame asks the device for its removed reason (design §5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend and a target; the seam makes GetDeviceRemovedReason return DEVICE_REMOVED.
    ///
    /// Steps:
    /// - Clear; remove the seam; clear and read.
    ///
    /// Expected:
    /// - The first clear throws GpuDeviceLostException with RemovedReason DEVICE_REMOVED; the next clear makes the device
    ///   again and reads back its color.
    /// </remarks>
    [Fact]
    public void EndFrame_DeviceRemoved_ThrowsAtTheEndOfAClear()
    {
        using var backend = D3D11Backend.Create(useWarp: true);
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(2, 2));
        backend.RemovedReason = () => DXGI.DXGI_ERROR_DEVICE_REMOVED;

        var exception = Assert.Throws<GpuDeviceLostException>(() => target.Clear(new Color(1, 1, 1, 255)));

        Assert.Equal(DXGI.DXGI_ERROR_DEVICE_REMOVED, exception.RemovedReason);
        backend.RemovedReason = null;
        target.Clear(new Color(9, 8, 7, 255));
        var pixels = new byte[16];
        target.ReadPixels(pixels);
        Assert.Equal([9, 8, 7, 255], pixels[..4]);
    }

    /// <summary>
    /// <see cref="IGpuBackend.Copy"/> copies every pixel of a target into another of its size, and refuses what it cannot copy
    /// (design §5.2, row I2b).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend with two targets of 3 × 2 and one of 2 × 2; a second backend with a target of 3 × 2.
    ///
    /// Steps:
    /// - Clear the first to one color and the second to another; copy the first into the second; read the second. Copy
    ///   between the two sizes, a target into itself, a target of the other backend, and null.
    ///
    /// Expected:
    /// - The second reads back the first's color at every pixel. The others throw ArgumentException, and null
    ///   ArgumentNullException.
    /// </remarks>
    [Fact]
    public void Copy_CopiesEveryPixelAndRefusesOtherTargets()
    {
        using var backend = D3D11Backend.Create(useWarp: true);
        using var other = D3D11Backend.Create(useWarp: true);
        using var source = backend.CreateRenderTarget(new RenderTargetDescription(3, 2));
        using var destination = backend.CreateRenderTarget(new RenderTargetDescription(3, 2));
        using var smaller = backend.CreateRenderTarget(new RenderTargetDescription(2, 2));
        using var foreign = other.CreateRenderTarget(new RenderTargetDescription(3, 2));
        source.Clear(new Color(10, 20, 30, 255));
        destination.Clear(new Color(200, 100, 50, 255));

        backend.Copy(source, destination);

        var pixels = new byte[3 * 2 * 4];
        destination.ReadPixels(pixels);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            Assert.Equal([10, 20, 30, 255], pixels[i..(i + 4)]);
        }

        _ = Assert.Throws<ArgumentException>(() => backend.Copy(source, smaller));
        _ = Assert.Throws<ArgumentException>(() => backend.Copy(source, source));
        _ = Assert.Throws<ArgumentException>(() => backend.Copy(foreign, destination));
        _ = Assert.Throws<ArgumentException>(() => backend.Copy(source, foreign));
        _ = Assert.Throws<ArgumentNullException>(() => backend.Copy(null!, destination));
        _ = Assert.Throws<ArgumentNullException>(() => backend.Copy(source, null!));
    }

    /// <summary>
    /// <see cref="IGpuBackend.DeviceGeneration"/> moves when a lost device is made again, and only then (design §5.2, row I2b).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend and a target; a loss injected through Map.
    ///
    /// Steps:
    /// - Read the number; draw two frames; read it; read the target with the loss injected; remove the injection; begin a
    ///   frame; read it.
    ///
    /// Expected:
    /// - The frames leave the number as it was; the frame after the loss makes the device again and moves it by 1.
    /// </remarks>
    [Fact]
    public void DeviceGeneration_MovesWhenTheDeviceIsMadeAgain()
    {
        using var backend = D3D11Backend.Create(useWarp: true);
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(2, 2));
        var first = backend.DeviceGeneration;
        target.Clear(new Color(1, 2, 3, 255));
        target.Clear(new Color(1, 2, 3, 255));
        Assert.Equal(first, backend.DeviceGeneration);

        backend.CheckResult = (call, result) => call == "Map" ? DXGI.DXGI_ERROR_DEVICE_REMOVED : result;
        _ = Assert.Throws<GpuDeviceLostException>(() => target.ReadPixels(new byte[16]));
        backend.CheckResult = null;
        using (backend.BeginFrame())
        {
            Assert.Equal(first + 1, backend.DeviceGeneration);
        }
    }

    /// <summary>
    /// While the device is lost, making a target makes the device and every target again first; a failed remake leaves the
    /// backend lost until a later frame succeeds (design §5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend with two targets, both cleared; a loss injected through Map.
    ///
    /// Steps:
    /// - With device creation failing, begin a frame; remove the failure; make a third target; read all three.
    ///
    /// Expected:
    /// - BeginFrame throws InvalidOperationException with the injected HRESULT; making the third target succeeds; all three
    ///   targets read back transparent.
    /// </remarks>
    [Fact]
    public void Lost_RemakeFailsThenMakingATarget_MakesEveryTargetAgain()
    {
        using var backend = D3D11Backend.Create(useWarp: true);
        using var first = backend.CreateRenderTarget(new RenderTargetDescription(2, 2));
        using var second = backend.CreateRenderTarget(new RenderTargetDescription(3, 1));
        first.Clear(new Color(5, 5, 5, 255));
        second.Clear(new Color(6, 6, 6, 255));
        backend.CheckResult = (call, result) => call == "Map" ? DXGI.DXGI_ERROR_DEVICE_REMOVED : result;
        _ = Assert.Throws<GpuDeviceLostException>(() => first.ReadPixels(new byte[16]));
        backend.CheckResult = null;
        backend.CreateDevice = _ => E.E_OUTOFMEMORY;

        var failure = Assert.Throws<InvalidOperationException>(() => backend.BeginFrame());

        Assert.Equal(E.E_OUTOFMEMORY, failure.HResult);
        backend.CreateDevice = null;
        using var third = backend.CreateRenderTarget(new RenderTargetDescription(1, 1));
        foreach (var target in new[] { first, second, third })
        {
            var pixels = new byte[target.Description.Width * target.Description.Height * 4];
            target.ReadPixels(pixels);
            Assert.All(pixels, p => Assert.Equal(0, p));
        }
    }

    /// <summary>
    /// A render target that fails to come back after a loss leaves the backend lost, with nothing half made; the next frame
    /// makes everything again (design §5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend with two targets; a loss injected through Map; the seam makes CreateTexture2D fail with
    ///   E_OUTOFMEMORY.
    ///
    /// Steps:
    /// - Begin a frame; begin another; remove the seam; begin a frame; read both targets.
    ///
    /// Expected:
    /// - Both frames with the seam throw InvalidOperationException with HResult E_OUTOFMEMORY (the backend stayed lost and
    ///   tried again); after the seam is gone, the frame begins, and both targets read back transparent.
    /// </remarks>
    [Fact]
    public void Lost_TargetRemakeFails_StaysLostUntilALaterFrame()
    {
        using var backend = D3D11Backend.Create(useWarp: true);
        using var first = backend.CreateRenderTarget(new RenderTargetDescription(2, 2));
        using var second = backend.CreateRenderTarget(new RenderTargetDescription(1, 3));
        backend.CheckResult = (call, result) => call == "Map" ? DXGI.DXGI_ERROR_DEVICE_REMOVED : result;
        _ = Assert.Throws<GpuDeviceLostException>(() => first.ReadPixels(new byte[16]));
        backend.CheckResult = (call, result) => call == "CreateTexture2D" ? E.E_OUTOFMEMORY : result;

        var failure = Assert.Throws<InvalidOperationException>(() => backend.BeginFrame());
        var again = Assert.Throws<InvalidOperationException>(() => backend.BeginFrame());

        Assert.Equal((E.E_OUTOFMEMORY, E.E_OUTOFMEMORY), (failure.HResult, again.HResult));
        backend.CheckResult = null;
        backend.BeginFrame();
        foreach (var target in new[] { first, second })
        {
            var pixels = new byte[target.Description.Width * target.Description.Height * 4];
            target.ReadPixels(pixels);
            Assert.All(pixels, p => Assert.Equal(0, p));
        }
    }

    /// <summary>
    /// A backend whose device cannot be made, WARP included, throws InvalidOperationException with the HRESULT (design §5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The seam makes D3D11CreateDevice fail with E_FAIL for every driver type.
    ///
    /// Steps:
    /// - Make a backend, with and without WARP asked for.
    ///
    /// Expected:
    /// - InvalidOperationException with HResult E_FAIL both times.
    /// </remarks>
    [Fact]
    public void Create_NoDevice_Throws()
    {
        foreach (var useWarp in new[] { true, false })
        {
            var failure = Assert.Throws<InvalidOperationException>(() => D3D11Backend.CreateForTest(useWarp, _ => E.E_FAIL));
            Assert.Equal(E.E_FAIL, failure.HResult);
        }
    }

    /// <summary>
    /// Disposing the backend releases its targets; Shared ignores Dispose; a headless renderer releases only its target
    /// (design §5.3, §7.5).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend with a target, and a headless renderer on it.
    ///
    /// Steps:
    /// - Dispose the renderer and use the backend; dispose the backend and use the target, the backend, and the renderer;
    ///   dispose Shared and make a target on it.
    ///
    /// Expected:
    /// - The backend still makes targets after the renderer is gone; afterwards ObjectDisposedException from the target,
    ///   the backend, and the renderer; Shared still makes a target.
    /// </remarks>
    [Fact]
    public void Dispose_BackendRendererAndShared_ReleaseWhatTheyOwn()
    {
        var backend = D3D11Backend.Create(useWarp: true);
        var target = backend.CreateRenderTarget(new RenderTargetDescription(2, 2));
        var renderer = new HeadlessRenderer(backend, 2, 2);
        renderer.Dispose();
        renderer.Dispose();
        using (backend.CreateRenderTarget(new RenderTargetDescription(1, 1))) { }

        backend.Dispose();
        backend.Dispose();

        Assert.Throws<ObjectDisposedException>(() => target.Clear(default));
        Assert.Throws<ObjectDisposedException>(() => backend.BeginFrame());
        Assert.Throws<ObjectDisposedException>(() => backend.CreateRenderTarget(new RenderTargetDescription(1, 1)));
        Assert.Throws<ObjectDisposedException>(() => renderer.Clear(default));
        D3D11Backend.Shared.Dispose();
        using var shared = D3D11Backend.Shared.CreateRenderTarget(new RenderTargetDescription(1, 1));
        Assert.Equal(1, shared.Description.Width);
    }

    /// <summary>
    /// A PNG of a D3D11 image holds the pixels ReadPixels gives (design §7.5).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A 5 × 3 headless renderer on WARP, cleared half-transparent.
    ///
    /// Steps:
    /// - Read the pixels; write a PNG; decode it with the test decoder.
    ///
    /// Expected:
    /// - The decoded size and pixels equal the read.
    /// </remarks>
    [Fact]
    public void WritePng_D3D11Image_HoldsThePixels()
    {
        using var backend = D3D11Backend.Create(useWarp: true);
        using var renderer = new HeadlessRenderer(backend, 5, 3);
        renderer.Clear(new Color(200, 100, 50, 128));
        var pixels = new byte[5 * 3 * 4];
        renderer.ReadPixels(pixels);
        using var stream = new MemoryStream();

        renderer.WritePng(stream);

        var decoded = HyperPlot.Testing.PngDecoder.Decode(stream.ToArray());
        Assert.Equal((5, 3), (decoded.Width, decoded.Height));
        Assert.Equal(pixels, decoded.Pixels);
    }
}
