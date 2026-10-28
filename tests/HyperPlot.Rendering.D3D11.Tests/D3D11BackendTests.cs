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
    /// - Read 20 times.
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

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 20; i++)
        {
            target.ReadPixels(pixels);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
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
    /// - Read; then the same with a removed reason of S_OK; after each loss, remove the seam, end a frame, and read again;
    ///   then clear blue and read.
    ///
    /// Expected:
    /// - The read throws GpuDeviceLostException with HResult the injected result and RemovedReason DEVICE_HUNG, or the
    ///   injected result when the reason is S_OK; EndFrame while lost does nothing; the read after the loss is all 0; the
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

            var exception = Assert.Throws<GpuDeviceLostException>(() => target.ReadPixels(pixels));

            Assert.Equal((lost, reason == S.S_OK ? lost : reason), (exception.HResult, exception.RemovedReason));
            backend.CheckResult = null;
            backend.RemovedReason = null;
            backend.EndFrame();
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

        var failure = Assert.Throws<InvalidOperationException>(backend.BeginFrame);

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
        Assert.Throws<ObjectDisposedException>(backend.BeginFrame);
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

        var decoded = HyperPlot.Tests.PngDecoder.Decode(stream.ToArray());
        Assert.Equal((5, 3), (decoded.Width, decoded.Height));
        Assert.Equal(pixels, decoded.Pixels);
    }
}
