using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HyperPlot.Interaction;
using HyperPlot.Rendering;
using HyperPlot.Rendering.D3D11;
using HyperPlot.Rendering.D3D11.Tests;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace HyperPlot.Wpf.Tests;

/// <summary>
/// <see cref="HyperPlotView"/> (design §7.2, §10 "WPF のホスト（W1a）"): what it shows through <c>D3DImage</c> or a read-back
/// bitmap, its size and DPI, its frames, its backend, and the input it passes. Each test runs on the assembly's WPF thread.
/// </summary>
public sealed class HyperPlotViewTests(WpfThread wpf)
{
    private const int Polls = 100_000;

    /// <summary>
    /// A plot is drawn with Direct3D 11 and shown through <c>D3DImage</c> (design §7.2): the shared texture it holds has the
    /// engine's pixels.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A view of the dispatcher's backend in a 320 × 200 window; a line plot set.
    ///
    /// Steps:
    /// - Draw until the copy is shown; open the shown texture in Direct3D 9Ex and read it; read the engine's target.
    ///
    /// Expected:
    /// - The view uses the shared textures, and the backend is on adapter 0. The image shows the D3DImage, of the engine's
    ///   size; the texture's pixels, made straight, are the target's.
    /// </remarks>
    [Fact]
    public void SharedTextures_ShowTheEnginesPixels() =>
        wpf.Run(() =>
        {
            using var scene = new Scene();
            var view = scene.View;
            Assert.True(view.UsesSharedTextures);
            Assert.Equal(view.Host.D3D9!.AdapterLuid, view.Backend.AdapterLuid);
            DrawUntilShown(view);

            var image = Assert.IsType<D3DImage>(view.Shown);
            Assert.Equal((view.Engine.Width, view.Engine.Height), (image.PixelWidth, image.PixelHeight));
            using var d3d9 = new D3D9Reader();
            AssertShown(view, d3d9);
        });

    /// <summary>
    /// A lost front buffer reads the frames back into a bitmap, and a new one shows through <c>D3DImage</c> again (design
    /// §7.2, requirement H-3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The scene of <see cref="SharedTextures_ShowTheEnginesPixels"/>, shown.
    ///
    /// Steps:
    /// - Say the front buffer is lost; draw until shown. Say it is back; draw until shown. Zoom and tick, then say the
    ///   front buffer is lost without its event (the copy may still wait); tick; zoom; draw until shown.
    ///
    /// Expected:
    /// - Lost: no shared texture is shown, the image shows a Pbgra32 bitmap of the engine's size, whose pixels, made
    ///   straight, are the target's. Back: the image shows the D3DImage with a shared texture again. A way that changes
    ///   while a copy waits throws nothing, and the next frame is read back with the target's pixels.
    /// </remarks>
    [Fact]
    public void LostFrontBuffer_ReadsBackAndComesBack() =>
        wpf.Run(() =>
        {
            using var scene = new Scene();
            var view = scene.View;
            DrawUntilShown(view);

            view.FrontBuffer = false;
            view.FrontBufferChanged();
            Assert.False(view.UsesSharedTextures);
            Assert.Equal(0, view.ShownHandle);
            DrawUntilShown(view);
            var bitmap = Assert.IsType<WriteableBitmap>(view.Shown);
            Assert.Equal(PixelFormats.Pbgra32, bitmap.Format);
            Assert.Equal(Target(view), Rgba(Pixels(bitmap)));

            view.FrontBuffer = true;
            view.FrontBufferChanged();
            DrawUntilShown(view);
            _ = Assert.IsType<D3DImage>(view.Shown);
            Assert.NotEqual(0, view.ShownHandle);

            Zoom(view);
            view.Tick();
            view.FrontBuffer = false;
            view.Tick();
            Zoom(view);
            DrawUntilShown(view);
            Assert.Equal(Target(view), Rgba(Pixels(Assert.IsType<WriteableBitmap>(view.Shown))));
        });

    /// <summary>
    /// A front buffer that WPF itself takes away and gives back, by its own notification, reads the frames back and then
    /// shows through <c>D3DImage</c> again (design §7.2, Issue #556): the view does not detach <c>D3DImage</c> from its
    /// bitmap while the front buffer is lost, or the notification that it is back never comes. The one test of the real
    /// notification, which waits in real time with a bound (rule testing).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The scene of <see cref="SharedTextures_ShowTheEnginesPixels"/>, shown through <c>D3DImage</c>, whose front buffer
    ///   is there. No test seam replaces the front buffer's value.
    ///
    /// Steps:
    /// - Set the process to render in software only (<c>RenderOptions.ProcessRenderMode</c>; with the window's own
    ///   target alone, the fixture's window keeps WPF's device and nothing is lost); let the dispatcher run until
    ///   <c>D3DImage</c> says the front buffer is lost, at most 10 s (measured: within 31 ms); draw until shown. Set the mode
    ///   back; let the dispatcher run until the front buffer is back, at most 10 s (measured: 23 to 75 ms); draw until shown.
    ///
    /// Expected:
    /// - Lost: the view does not use the shared textures, shows no shared texture, and the image shows a bitmap with the
    ///   target's pixels. Back: the front buffer is there within the bound, which is the assertion that guards the fix (with
    ///   a null back buffer set at the loss it never comes back); the image shows the D3DImage, and the shown texture has
    ///   the target's pixels. A machine on which rendering in software takes no front buffer away skips the test.
    /// </remarks>
    [Fact]
    [Trait("Category", "OsTiming")]
    public void FrontBufferLostByWpf_ReadsBackAndComesBack() =>
        wpf.Run(() =>
        {
            using var scene = new Scene();
            var view = scene.View;
            DrawUntilShown(view);
            _ = Assert.IsType<D3DImage>(view.Shown);
            Assert.True(view.FrontBufferAvailable);
            var mode = RenderOptions.ProcessRenderMode;
            var bound = TimeSpan.FromSeconds(10);

            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            try
            {
                if (!WaitUntil(() => !view.FrontBufferAvailable, bound))
                {
                    Assert.Skip("A process set to render in software took no front buffer from D3DImage within 10 s on this machine.");
                }

                DrawUntilShown(view);
                Assert.False(view.UsesSharedTextures);
                Assert.Equal(0, view.ShownHandle);
                Assert.Equal(Target(view), Rgba(Pixels(Assert.IsType<WriteableBitmap>(view.Shown))));
            }
            finally
            {
                RenderOptions.ProcessRenderMode = mode;
            }

            Assert.True(
                WaitUntil(() => view.FrontBufferAvailable, bound),
                "D3DImage did not say that its front buffer was back within 10 s of the process rendering in hardware again (Issue #556)."
            );
            DrawUntilShown(view);
            _ = Assert.IsType<D3DImage>(view.Shown);
            Assert.NotEqual(0, view.ShownHandle);
            using var d3d9 = new D3D9Reader();
            AssertShown(view, d3d9);
        });

    /// <summary>A backend on another adapter than Direct3D 9Ex's is read back (design §7.2), and stays its owner's.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A WARP backend of the test's, on another adapter than Direct3D 9Ex's adapter 0.
    ///
    /// Steps:
    /// - Show a view of that backend with a plot; draw until shown; dispose the view.
    ///
    /// Expected:
    /// - The view does not use the shared textures; the bitmap's pixels are the target's. After the view, the backend still
    ///   makes a target.
    /// </remarks>
    [Fact]
    public void OtherAdapter_ReadsBack() =>
        wpf.Run(() =>
        {
            using var backend = D3D11Backend.Create(useWarp: true);
            using (var scene = new Scene(backend))
            {
                var view = scene.View;
                Assert.NotEqual(view.Host.D3D9!.AdapterLuid, backend.AdapterLuid);
                Assert.False(view.UsesSharedTextures);
                DrawUntilShown(view);
                Assert.Equal(Target(view), Rgba(Pixels(Assert.IsType<WriteableBitmap>(view.Shown))));
            }

            using var target = backend.CreateRenderTarget(new RenderTargetDescription(4, 4));
        });

    /// <summary>
    /// A lost Direct3D 11 device and a lost Direct3D 9Ex device are made again, and the view shows the next frame (design
    /// §7.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The scene of <see cref="SharedTextures_ShowTheEnginesPixels"/>, shown.
    ///
    /// Steps:
    /// - Make the backend's GetDeviceRemovedReason say DEVICE_REMOVED; ask for a frame and draw; remove the seam; draw until
    ///   shown. Make CheckDeviceState say D3DERR_DEVICELOST once; ask for a frame; draw until shown.
    ///
    /// Expected:
    /// - The draw during the loss throws nothing and asks for another frame. After each, a shared texture is shown with the
    ///   target's pixels; the Direct3D 9Ex state was read.
    /// </remarks>
    [Fact]
    public void DeviceLosses_MakeTheDevicesAgain() =>
        wpf.Run(() =>
        {
            using var scene = new Scene();
            var view = scene.View;
            DrawUntilShown(view);

            view.Backend.RemovedReason = () => DXGI.DXGI_ERROR_DEVICE_REMOVED;
            view.Engine.Views.Reset(new PanelId(0, 0), animate: false);
            view.Engine.Views.ZoomAt(new PanelId(0, 0), new System.Numerics.Vector2(50, 50), 2, animate: false);
            view.Tick();
            Assert.True(view.Hooked);
            view.Backend.RemovedReason = null;
            DrawUntilShown(view);
            using var d3d9 = new D3D9Reader();
            AssertShown(view, d3d9);

            var asked = 0;
            view.Host.D3D9!.DeviceState = () => asked++ == 0 ? unchecked((int)0x88760868) : 0;
            try
            {
                view.Engine.Views.ZoomAt(new PanelId(0, 0), new System.Numerics.Vector2(50, 50), 2, animate: false);
                DrawUntilShown(view);
            }
            finally
            {
                view.Host.D3D9.DeviceState = null;
            }

            Assert.True(asked >= 1);
            Assert.NotEqual(0, view.ShownHandle);
            AssertShown(view, d3d9);
        });

    /// <summary>
    /// A Direct3D 9Ex device that cannot be made again reads the frames back, and is tried again at each frame until it is
    /// made (design §7.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The scene of <see cref="SharedTextures_ShowTheEnginesPixels"/>, shown.
    ///
    /// Steps:
    /// - Make CheckDeviceState say D3DERR_DEVICELOST and making the device fail; zoom; draw until shown. Let the device be
    ///   made; zoom; draw until shown.
    ///
    /// Expected:
    /// - While it fails: there is no device, the view does not use the shared textures, and the image shows a bitmap with
    ///   the target's pixels. Then: the device is there, and a shared texture is shown with the target's pixels.
    /// </remarks>
    [Fact]
    public void D3D9DeviceNotMadeAgain_ReadsBackUntilItIs() =>
        wpf.Run(() =>
        {
            using var scene = new Scene();
            var view = scene.View;
            var d3d9 = view.Host.D3D9!;
            DrawUntilShown(view);

            d3d9.DeviceState = () => unchecked((int)0x88760868);
            d3d9.FailMakeDevice = true;
            try
            {
                Zoom(view);
                DrawUntilShown(view);
                Assert.False(d3d9.HasDevice);
                Assert.False(view.UsesSharedTextures);
                Assert.Equal(Target(view), Rgba(Pixels(Assert.IsType<WriteableBitmap>(view.Shown))));
            }
            finally
            {
                d3d9.DeviceState = null;
                d3d9.FailMakeDevice = false;
            }

            Zoom(view);
            DrawUntilShown(view);
            Assert.True(d3d9.HasDevice);
            _ = Assert.IsType<D3DImage>(view.Shown);
            using var reader = new D3D9Reader();
            AssertShown(view, reader);
        });

    /// <summary>
    /// A device lost while the view resizes is not thrown into WPF's layout: the next frame makes the device again and
    /// resizes (design §7.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The scene's view, shown.
    ///
    /// Steps:
    /// - Make the backend's CreateTexture2D and GetDeviceRemovedReason say DEVICE_REMOVED; make the window 400 × 260 and
    ///   lay it out; remove the seams; draw until shown.
    ///
    /// Expected:
    /// - The layout throws nothing and the view waits for the tick (or WPF's own tick in the pump drew the frame, Issue #305). After the draw, the engine's size is the view's new size
    ///   in pixels, and the shown texture has the target's pixels.
    /// </remarks>
    [Fact]
    public void DeviceLostInResize_ResizesAtTheNextFrame() =>
        wpf.Run(() =>
        {
            using var scene = new Scene();
            var view = scene.View;
            DrawUntilShown(view);

            view.Backend.CheckResult = (call, result) => call == "CreateTexture2D" ? DXGI.DXGI_ERROR_DEVICE_REMOVED : result;
            view.Backend.RemovedReason = () => DXGI.DXGI_ERROR_DEVICE_REMOVED;
            try
            {
                scene.Window.Width = 400;
                scene.Window.Height = 260;
                var drawn = view.FramesDrawn;
                WpfThread.Pump();
                AssertWaitsOrDrew(view, drawn);
            }
            finally
            {
                view.Backend.CheckResult = null;
                view.Backend.RemovedReason = null;
            }

            DrawUntilShown(view);
            var dpi = VisualTreeHelper.GetDpi(view);
            Assert.Equal(
                InputTranslator.PixelSize(view.ActualWidth, view.ActualHeight, dpi.DpiScaleX, dpi.DpiScaleY),
                (view.Engine.Width, view.Engine.Height)
            );
            using var d3d9 = new D3D9Reader();
            AssertShown(view, d3d9);
        });

    /// <summary>
    /// <c>Spec</c> takes a plot or a figure, and null shows nothing; another node throws (design §7.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A view in a window.
    ///
    /// Steps:
    /// - Set a figure of two plots and draw; set a plot and draw; set a scale; set the figure and, before the tick, null;
    ///   tick; set the plot; draw until shown.
    ///
    /// Expected:
    /// - The figure: the view of plot 1 reads. The plot: the view of plot 0 reads and plot 1's throws. The scale:
    ///   ArgumentException, the spec unchanged. Null: the image shows nothing, and the tick leaves WPF's tick though the
    ///   figure's frame was wanted. The plot again: the view waits for the tick, and the shown texture has the target's
    ///   pixels.
    /// </remarks>
    [Fact]
    public void Spec_TakesAPlotOrAFigure() =>
        wpf.Run(() =>
        {
            using var scene = new Scene();
            var view = scene.View;
            var plot = scene.Plot;
            view.Spec = Figure.HConcat(plot, plot);
            DrawUntilShown(view);
            _ = view.Engine.CurrentView(new PanelId(1, 0));

            view.Spec = plot;
            DrawUntilShown(view);
            _ = view.Engine.CurrentView(new PanelId(0, 0));
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => view.Engine.CurrentView(new PanelId(1, 0)));

            _ = Assert.Throws<ArgumentException>(() => view.Spec = Scale.Linear());
            Assert.Same(plot, view.Spec);
            view.Spec = Figure.HConcat(plot, plot);
            Assert.True(view.Hooked);
            view.Spec = null;
            Assert.Null(view.Shown);
            view.Tick();
            Assert.False(view.Hooked);

            view.Spec = plot;
            Assert.True(view.Hooked);
            DrawUntilShown(view);
            using var d3d9 = new D3D9Reader();
            AssertShown(view, d3d9);
        });

    /// <summary>
    /// The engine's target is the view's size in device pixels at its DPI, and follows the view's size; an empty view draws
    /// nothing (design §7.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The scene's view in a 320 × 200 window.
    ///
    /// Steps:
    /// - Read the engine's size; make the window 400 × 260; collapse the view and draw; zoom and tick; show the view again
    ///   at the same size; draw until shown.
    ///
    /// Expected:
    /// - Each time, the engine's size and scale are the view's DIPs times the DPI, rounded, and the DPI's scale. Collapsed,
    ///   a draw keeps the size and the image as it was, and a wanted frame leaves WPF's tick after one tick. Shown again,
    ///   it waits for the tick (or WPF's own tick in the pump drew the frame, Issue #305), and the shown texture has the
    ///   zoomed target's pixels.
    /// </remarks>
    [Fact]
    public void Size_FollowsTheViewAndItsDpi() =>
        wpf.Run(() =>
        {
            using var scene = new Scene();
            var view = scene.View;
            AssertSize(view);
            scene.Window.Width = 400;
            scene.Window.Height = 260;
            WpfThread.Pump();
            AssertSize(view);
            DrawUntilShown(view);

            var (size, shown) = ((view.Engine.Width, view.Engine.Height), view.Shown);
            view.Visibility = Visibility.Collapsed;
            WpfThread.Pump();
            view.Tick();
            Assert.Equal(size, (view.Engine.Width, view.Engine.Height));
            Assert.Same(shown, view.Shown);
            Zoom(view);
            Assert.True(view.Hooked);
            view.Tick();
            Assert.False(view.Hooked);

            view.Visibility = Visibility.Visible;
            var drawn = view.FramesDrawn;
            WpfThread.Pump();
            Assert.Equal(size, (view.Engine.Width, view.Engine.Height));
            AssertWaitsOrDrew(view, drawn);
            DrawUntilShown(view);
            using var d3d9 = new D3D9Reader();
            AssertShown(view, d3d9);

            static void AssertSize(HyperPlotView view)
            {
                var dpi = VisualTreeHelper.GetDpi(view);
                Assert.Equal(
                    InputTranslator.PixelSize(view.ActualWidth, view.ActualHeight, dpi.DpiScaleX, dpi.DpiScaleY),
                    (view.Engine.Width, view.Engine.Height)
                );
                Assert.Equal((float)dpi.DpiScaleX, view.Engine.Scale);
            }
        });

    /// <summary>
    /// The view waits for WPF's rendering tick only while a frame is wanted and it is loaded (design §7.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The scene's view, drawn.
    ///
    /// Steps:
    /// - Zoom; tick; zoom; tick; draw until shown; take the view out of the window and zoom; put it back.
    ///
    /// Expected:
    /// - After the zoom it waits, and still after the tick that drew it and another zoom and tick (frames that keep coming
    ///   keep the subscription, design §7.2); after the copy is shown it does not; out of the window it does not wait though a frame is
    ///   wanted; back in, it waits (or WPF's own tick in the pump drew the frame, Issue #305).
    /// </remarks>
    [Fact]
    public void Frames_WaitForTheTickWhileWantedAndLoaded() =>
        wpf.Run(() =>
        {
            using var scene = new Scene();
            var view = scene.View;
            DrawUntilShown(view);
            Assert.False(view.Hooked);

            Zoom(view);
            Assert.True(view.Hooked);
            view.Tick();
            Zoom(view);
            view.Tick();
            Assert.True(view.Hooked);
            DrawUntilShown(view);
            Assert.False(view.Hooked);

            scene.Window.Content = null;
            WpfThread.Pump();
            Zoom(view);
            Assert.False(view.Hooked);
            scene.Window.Content = view;
            var drawn = view.FramesDrawn;
            WpfThread.Pump();
            AssertWaitsOrDrew(view, drawn);
        });

    /// <summary>
    /// WPF's own rendering tick draws a frame the engine asked for (design §7.2): the one test of the real tick, which waits
    /// in real time with a watchdog (rule testing).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The scene's view in a window.
    ///
    /// Steps:
    /// - Let the dispatcher run until something is shown, at most 10 s (measured: a few ticks of 16 ms).
    ///
    /// Expected:
    /// - The image shows a source within the time.
    /// </remarks>
    [Fact]
    [Trait("Category", "OsTiming")]
    public void Tick_DrawsTheFrame() =>
        wpf.Run(() =>
        {
            using var scene = new Scene(pump: false);
            var view = scene.View;
            var frame = new DispatcherFrame();
            var watchdog = new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Send, (_, _) => frame.Continue = false, view.Dispatcher);
            void Check(object? sender, EventArgs e)
            {
                if (view.Shown is not null)
                {
                    frame.Continue = false;
                }
            }

            CompositionTarget.Rendering += Check;
            try
            {
                Dispatcher.PushFrame(frame);
            }
            finally
            {
                CompositionTarget.Rendering -= Check;
                watchdog.Stop();
            }

            Assert.NotNull(view.Shown);
        });

    /// <summary>
    /// The views of a dispatcher share a backend, released when the dispatcher shuts down; a backend the application gives
    /// stays its own (design §7.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A thread of its own with a dispatcher.
    ///
    /// Steps:
    /// - On it, make two views and a view of a WARP backend of the test's; shut the dispatcher down.
    ///
    /// Expected:
    /// - The two views have the same backend and resources; the given backend's view has the given one. After the shutdown,
    ///   the resources are released and the shared backend throws ObjectDisposedException; the given one makes a target.
    /// </remarks>
    [Fact]
    public void Backend_IsTheDispatchers()
    {
        HostResources? host = null;
        D3D11Backend? shared = null;
        D3D11Backend? given = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var one = new HyperPlotView();
                var two = new HyperPlotView();
                given = D3D11Backend.Create(useWarp: true);
                var three = new HyperPlotView(given);
                Assert.Same(one.Backend, two.Backend);
                Assert.Same(one.Host, two.Host);
                Assert.Same(given, three.Backend);
                (host, shared) = (one.Host, one.Backend);
                one.Dispose();
                two.Dispose();
                three.Dispose();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        Assert.Null(failure);
        Assert.True(host!.Released);
        _ = Assert.Throws<ObjectDisposedException>(() => shared!.CreateRenderTarget(new RenderTargetDescription(4, 4)));
        using (given)
        {
            using var target = given!.CreateRenderTarget(new RenderTargetDescription(4, 4));
        }
    }

    /// <summary>
    /// A press the engine uses takes the focus and captures the mouse, so a drag pans; a lost capture cancels the drag, and
    /// the moves after it pan nothing (design §6.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The scene's view, drawn, with the default bindings.
    ///
    /// Steps:
    /// - Press the left button at the middle; move 30 DIPs right with it held; move to 40 DIPs right of the view; release.
    ///   Press again; move 30 right; take the capture away; move 30 more with it held.
    ///
    /// Expected:
    /// - After the press, the view is focused and captures the mouse; the move pans x, and so does the move outside the view
    ///   (the drag's tool has the pointer); after the release, no capture. In the
    ///   second drag, the moves after the lost capture leave the view as it was at the loss, and the view has no capture.
    /// </remarks>
    [Fact]
    public void Mouse_DragPansAndALostCaptureCancels() =>
        wpf.Run(() =>
        {
            using var scene = new Scene();
            var view = scene.View;
            DrawUntilShown(view);
            var middle = new Point(view.ActualWidth / 2, view.ActualHeight / 2);
            var x = X(view);

            AssertInput(
                view.Pointer(PointerEventKind.Down, middle, PointerButtons.Left, PointerButtons.Left, PointerKind.Mouse),
                "The press was not used."
            );
            Assert.True(view.IsFocused);
            Assert.True(view.IsMouseCaptured);
            _ = view.Pointer(PointerEventKind.Move, middle + new Vector(30, 0), PointerButtons.None, PointerButtons.Left, PointerKind.Mouse);
            AssertInput(x != X(view), "The drag inside the view did not pan x.");
            var inside = X(view);
            var outside = new Point(view.ActualWidth + 40, middle.Y);
            _ = view.Pointer(PointerEventKind.Move, outside, PointerButtons.None, PointerButtons.Left, PointerKind.Mouse);
            AssertInput(inside != X(view), "The drag outside the view did not pan x.");
            _ = view.Pointer(PointerEventKind.Up, outside, PointerButtons.Left, PointerButtons.None, PointerKind.Mouse);
            Assert.False(view.IsMouseCaptured);

            _ = view.Pointer(PointerEventKind.Down, middle, PointerButtons.Left, PointerButtons.Left, PointerKind.Mouse);
            _ = view.Pointer(PointerEventKind.Move, middle + new Vector(30, 0), PointerButtons.None, PointerButtons.Left, PointerKind.Mouse);
            _ = Mouse.Capture(null);
            Assert.False(view.IsMouseCaptured);
            var lost = X(view);
            _ = view.Pointer(PointerEventKind.Move, middle + new Vector(60, 0), PointerButtons.None, PointerButtons.Left, PointerKind.Mouse);
            Assert.Equal(lost, X(view));
        });

    /// <summary>The mouse leaving the view passes <see cref="PointerEventKind.Leave"/>, and the engine empties its hover (design §7.2, row I2b).</summary>
    /// <remarks>
    /// Preconditions:
    /// - The scene's view, drawn, with the default bindings.
    ///
    /// Steps:
    /// - Move the mouse to the middle with no button; raise MouseLeave on the view.
    ///
    /// Expected:
    /// - After the move the engine hovers a hit; after the leave its hover is empty.
    /// </remarks>
    [Fact]
    public void MouseLeave_EndsTheHover() =>
        wpf.Run(() =>
        {
            using var scene = new Scene();
            var view = scene.View;
            DrawUntilShown(view);
            var middle = new Point(view.ActualWidth / 2, view.ActualHeight / 2);

            _ = view.Pointer(PointerEventKind.Move, middle, PointerButtons.None, PointerButtons.None, PointerKind.Mouse);
            AssertInput(view.Engine.Hover.Length != 0, "The move hovers nothing.");
            view.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
            Assert.Equal(0, view.Engine.Hover.Length);
        });

    /// <summary>
    /// The wheel zooms, keys zoom when the engine knows them, and the horizontal wheel's message over the view reaches the
    /// engine with its notches (design §6.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The scene's view, drawn; animations off; a tool of the test's first in the bindings for the wheel on the panel,
    ///   which records the horizontal amounts and takes only a turn with no vertical amount.
    ///
    /// Steps:
    /// - Turn the wheel 2 notches at the middle; press Add; press F1; send WM_MOUSEHWHEEL of 120 at the middle's screen point,
    ///   and of 120 at a point left of the view.
    ///
    /// Expected:
    /// - The wheel and Add narrow x and are used; F1 is not used. The message over the view is used with 1 notch; the one
    ///   outside is not used and records nothing.
    /// </remarks>
    [Fact]
    public void WheelKeysAndHorizontalWheel_Reach() =>
        wpf.Run(() =>
        {
            using var scene = new Scene();
            var view = scene.View;
            view.Engine.Options.ZoomAnimation = TimeSpan.Zero;
            var tool = new HorizontalTool();
            view.Engine.Bindings.Insert(
                0,
                new Interaction.InputBinding(Interaction.InputGesture.Wheel, Interaction.ModifierKeys.None, HitAreaKind.Panel, tool)
            );
            DrawUntilShown(view);
            var middle = new Point(view.ActualWidth / 2, view.ActualHeight / 2);

            var x = X(view);
            AssertInput(view.Wheel(middle, 0, 2), "The wheel was not used.");
            AssertInput(Width(X(view)) < Width(x), "The wheel did not narrow x.");
            x = X(view);
            AssertInput(view.Key(KeyEventKind.Down, Key.Add, repeat: false), "Add was not used.");
            AssertInput(Width(X(view)) < Width(x), "Add did not narrow x.");
            Assert.False(view.Key(KeyEventKind.Down, Key.F1, repeat: false));

            var screen = view.PointToScreen(middle);
            AssertInput(
                view.HorizontalWheel((nint)120 << 16, Words((int)screen.X, (int)screen.Y)),
                "The horizontal wheel over the view was not used."
            );
            Assert.Equal([1.0], tool.Turns);
            var outside = view.PointToScreen(new Point(-20, middle.Y));
            Assert.False(view.HorizontalWheel((nint)120 << 16, Words((int)outside.X, (int)outside.Y)));
            Assert.Equal([1.0], tool.Turns);

            static nint Words(int x, int y) => (nint)(((uint)(ushort)(short)y << 16) | (ushort)(short)x);
        });

    /// <summary>
    /// One finger drags as a pointer; a second cancels it, and the manipulation pinches and scrolls; one finger's
    /// manipulation does nothing (design §6.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The scene's view, drawn; animations off.
    ///
    /// Steps:
    /// - A manipulation of scale 2 with one finger; finger 1 down at the middle and moving 30 DIPs right; finger 2 down;
    ///   a manipulation of scale 1.5 at the middle; one of a 20 DIP move right; both fingers up.
    ///
    /// Expected:
    /// - The one finger's manipulation leaves x; the finger's move pans x and focuses the view; the pinch narrows x; the
    ///   scroll moves it without changing its width; after the fingers, none is down.
    /// </remarks>
    [Fact]
    public void Touch_OneFingerDragsTwoPinchAndScroll() =>
        wpf.Run(() =>
        {
            using var scene = new Scene();
            var view = scene.View;
            view.Engine.Options.ZoomAnimation = TimeSpan.Zero;
            DrawUntilShown(view);
            var middle = new Point(view.ActualWidth / 2, view.ActualHeight / 2);
            var x = X(view);

            view.Manipulate(middle, new Vector(2, 2), default);
            Assert.Equal(x, X(view));
            AssertInput(view.Touch(view.Touches.Down(1), PointerEventKind.Down, middle), "The first finger was not used.");
            Assert.True(view.IsFocused);
            _ = view.Touch(view.Touches.Move(1), PointerEventKind.Move, middle + new Vector(30, 0));
            AssertInput(x != X(view), "The finger's move did not pan x.");
            AssertInput(view.Touch(view.Touches.Down(2), PointerEventKind.Down, middle + new Vector(0, 20)), "The second finger was not used.");

            x = X(view);
            view.Manipulate(middle, new Vector(1.5, 1.5), default);
            AssertInput(Width(X(view)) < Width(x) * 0.9, "The pinch did not narrow x.");
            x = X(view);
            view.Manipulate(middle, new Vector(1, 1), new Vector(20, 0));
            AssertInput(x != X(view), "The scroll did not move x.");
            Assert.Equal(Width(x), Width(X(view)), 1e-9 * Width(x));
            _ = view.Touch(view.Touches.Up(1), PointerEventKind.Up, middle);
            _ = view.Touch(view.Touches.Up(2), PointerEventKind.Up, middle);
            Assert.False(view.Touches.Gesturing);
        });

    /// <summary>
    /// A WPF element over the plot takes the pointer where it is, and the plot takes it elsewhere (requirement H-2, design
    /// §7.2): the view is a WPF element, not a child window.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A grid with the scene's view and a 100 × 30 button over its top-right corner, in the window; drawn.
    ///
    /// Steps:
    /// - Hit-test the button's middle, and the view's middle.
    ///
    /// Expected:
    /// - The button's middle hits the button (or an element in it); the view's middle hits the view (or its image).
    /// </remarks>
    [Fact]
    public void Overlay_ElementOverThePlotTakesThePointer() =>
        wpf.Run(() =>
        {
            using var scene = new Scene();
            var view = scene.View;
            var button = new System.Windows.Controls.Button
            {
                Content = "Reset",
                Width = 100,
                Height = 30,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
            };
            var grid = new System.Windows.Controls.Grid();
            scene.Window.Content = grid;
            _ = grid.Children.Add(view);
            _ = grid.Children.Add(button);
            DrawUntilShown(view);

            var onButton = button.TranslatePoint(new Point(50, 15), grid);
            var hit = VisualTreeHelper.HitTest(grid, onButton)?.VisualHit as DependencyObject;
            Assert.True(hit is not null && (hit == button || button.IsAncestorOf(hit)), $"{hit}");
            var onView = new Point(view.ActualWidth / 2, view.ActualHeight / 2);
            var inView = VisualTreeHelper.HitTest(grid, onView)?.VisualHit as DependencyObject;
            Assert.True(inView is not null && (inView == view || view.IsAncestorOf(inView)), $"{inView}");
        });

    /// <summary>The view takes the keyboard focus and Tab stops on it (design §6.1).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A view.
    ///
    /// Steps:
    /// - Read its Focusable and IsTabStop.
    ///
    /// Expected:
    /// - Both true.
    /// </remarks>
    [Fact]
    public void Focus_TakenAndTabbedTo() =>
        wpf.Run(() =>
        {
            using var scene = new Scene();
            Assert.True(scene.View.Focusable);
            Assert.True(KeyboardNavigation.GetIsTabStop(scene.View));
        });

    /// <summary>The input, the size, and the spec a disposed view is given (Issue #601).</summary>
    public enum Given
    {
        /// <summary>A mouse button pressed.</summary>
        MouseDown,

        /// <summary>The mouse moved.</summary>
        MouseMove,

        /// <summary>The mouse left the view.</summary>
        MouseLeave,

        /// <summary>A mouse button released.</summary>
        MouseUp,

        /// <summary>The wheel turned.</summary>
        Wheel,

        /// <summary>The window's <c>WM_MOUSEHWHEEL</c> message over the view.</summary>
        HorizontalWheel,

        /// <summary>A finger down.</summary>
        TouchDown,

        /// <summary>A finger that is down moved.</summary>
        TouchMove,

        /// <summary>A finger that is down lifted.</summary>
        TouchUp,

        /// <summary>A manipulation starting.</summary>
        ManipulationStarting,

        /// <summary>A manipulation's step while two fingers are down.</summary>
        ManipulationDelta,

        /// <summary>A manipulation's inertia starting.</summary>
        ManipulationInertiaStarting,

        /// <summary>A key pressed that the engine knows.</summary>
        KeyDown,

        /// <summary>A key released that the engine knows.</summary>
        KeyUp,

        /// <summary>The view resized.</summary>
        Size,

        /// <summary>A new spec.</summary>
        Spec,
    }

    /// <summary>
    /// A disposed view that is still in its window takes no input, size, or spec: each does nothing and throws nothing
    /// (Issue #601, design §7.2). Before the fix, each one that reached the engine threw <see cref="ObjectDisposedException"/>
    /// out of WPF's input, and the others marked the event as the view's.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The scene's view, drawn, with the default bindings, left in its window. Before the view is disposed, the state with
    ///   which a view that is not disposed passes the input on to the engine: finger 1 down for the finger's move and lift,
    ///   fingers 1 and 2 down for the manipulation's step. The lost capture is
    ///   <see cref="Dispose_InADrag_ReleasesTheMouseAndTheFingers"/>'s: a disposed view holds no capture to lose.
    ///
    /// Steps:
    /// - Dispose the view. Then, one case each: raise MouseDown, MouseMove, MouseLeave, MouseUp (left button), and
    ///   MouseWheel (120) on it; post <c>WM_MOUSEHWHEEL</c> of 120
    ///   at the view's middle to the window and pump; raise TouchDown, TouchMove, and TouchUp of finger 1 of a test touch
    ///   device; start a manipulation on the view with a test manipulator, move it 30 device-independent pixels right and
    ///   lay the view out (the manipulation reads its contacts then), and remove it, pumping after each; raise KeyDown and KeyUp of Add; make the view half as wide and pump; set a figure of
    ///   two plots as the spec.
    ///
    /// Expected:
    /// - Nothing throws. Each routed event the test raises or WPF raises is not marked handled, and each that WPF raises
    ///   (the manipulation's) did reach the view; the view has no mouse capture.
    /// </remarks>
    [Theory]
    [InlineData(Given.MouseDown)]
    [InlineData(Given.MouseMove)]
    [InlineData(Given.MouseLeave)]
    [InlineData(Given.MouseUp)]
    [InlineData(Given.Wheel)]
    [InlineData(Given.HorizontalWheel)]
    [InlineData(Given.TouchDown)]
    [InlineData(Given.TouchMove)]
    [InlineData(Given.TouchUp)]
    [InlineData(Given.ManipulationStarting)]
    [InlineData(Given.ManipulationDelta)]
    [InlineData(Given.ManipulationInertiaStarting)]
    [InlineData(Given.KeyDown)]
    [InlineData(Given.KeyUp)]
    [InlineData(Given.Size)]
    [InlineData(Given.Spec)]
    public void Disposed_TakesNoInput(Given given) =>
        wpf.Run(() =>
        {
            using var scene = new Scene();
            var view = scene.View;
            DrawUntilShown(view);
            var middle = new Point(view.ActualWidth / 2, view.ActualHeight / 2);
            using var finger = new TestTouchDevice(1, middle, PresentationSource.FromVisual(view));
            switch (given)
            {
                case Given.TouchMove or Given.TouchUp:
                    _ = view.Touches.Down(finger.Id);
                    break;
                case Given.ManipulationDelta:
                    _ = view.Touches.Down(1);
                    _ = view.Touches.Down(2);
                    break;
            }

            var screen = view.PointToScreen(middle);
            view.Dispose();
            var handled = given switch
            {
                Given.MouseDown => Raise(
                    view,
                    new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent }
                ),
                Given.MouseMove => Raise(view, new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseMoveEvent }),
                Given.MouseLeave => Raise(view, new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent }),
                Given.MouseUp => Raise(view, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent }),
                Given.Wheel => Raise(view, new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = Mouse.MouseWheelEvent }),
                Given.HorizontalWheel => PostHorizontalWheel(scene.Window, screen),
                Given.TouchDown => Raise(view, new TouchEventArgs(finger, 0) { RoutedEvent = UIElement.TouchDownEvent }),
                Given.TouchMove => Raise(view, new TouchEventArgs(finger, 0) { RoutedEvent = UIElement.TouchMoveEvent }),
                Given.TouchUp => Raise(view, new TouchEventArgs(finger, 0) { RoutedEvent = UIElement.TouchUpEvent }),
                Given.ManipulationStarting => Manipulate(view, middle, UIElement.ManipulationStartingEvent),
                Given.ManipulationDelta => Manipulate(view, middle, UIElement.ManipulationDeltaEvent),
                Given.ManipulationInertiaStarting => Manipulate(view, middle, UIElement.ManipulationInertiaStartingEvent),
                Given.KeyDown => Raise(
                    view,
                    new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(view), 0, Key.Add) { RoutedEvent = Keyboard.KeyDownEvent }
                ),
                Given.KeyUp => Raise(
                    view,
                    new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(view), 0, Key.Add) { RoutedEvent = Keyboard.KeyUpEvent }
                ),
                Given.Size => Resize(view),
                Given.Spec => SetSpec(view, Figure.HConcat(scene.Plot, scene.Plot)),
                _ => throw new ArgumentOutOfRangeException(nameof(given), given, "No such input."),
            };

            Assert.False(handled, $"The disposed view marked {given} as its own.");
            Assert.False(view.IsMouseCaptured);

            static bool Raise(UIElement view, RoutedEventArgs e)
            {
                view.RaiseEvent(e);
                return e.Handled;
            }

            static bool PostHorizontalWheel(Window window, Point screen)
            {
                var hwnd = (HWND)new WindowInteropHelper(window).Handle;
                var at = ((uint)(ushort)(short)screen.Y << 16) | (ushort)(short)screen.X;
                Assert.True(PostMessageW(hwnd, InputTranslator.WmMouseHWheel, (nuint)(120 << 16), (nint)at));
                WpfThread.Pump();
                return false;
            }

            static bool Resize(HyperPlotView view)
            {
                // The view's own size, laid out in the pump. A window resized here lays out inside this call, and on the code
                // before Issue #601's fix the exception thrown there ended the test process, and with it every test after it.
                view.Width = view.ActualWidth / 2;
                WpfThread.Pump();
                return false;
            }

            static bool SetSpec(HyperPlotView view, SpecNode spec)
            {
                view.Spec = spec;
                return false;
            }
        });

    /// <summary>
    /// A view disposed in a drag lets go of the mouse capture and of the fingers it captured (Issue #601): it no longer takes
    /// the release that would let them go. The lost capture then reaches the disposed view, which throws nothing. On the code
    /// before the fix, the capture stayed, and the window's close lost it into the disposed engine, which ended the test
    /// process.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The scene's view, drawn, with the default bindings.
    ///
    /// Steps:
    /// - Press the left button at the middle; dispose the view. Make a second scene; raise TouchDown of finger 1 of a test
    ///   touch device at its view's middle; dispose that view.
    ///
    /// Expected:
    /// - After the press the view captures the mouse, and after the disposal neither it nor any element does. After the
    ///   finger, the second view captures it, and after the disposal it captures no finger.
    /// </remarks>
    [Fact]
    public void Dispose_InADrag_ReleasesTheMouseAndTheFingers() =>
        wpf.Run(() =>
        {
            using (var scene = new Scene())
            {
                var view = scene.View;
                DrawUntilShown(view);
                var middle = new Point(view.ActualWidth / 2, view.ActualHeight / 2);
                AssertInput(
                    view.Pointer(PointerEventKind.Down, middle, PointerButtons.Left, PointerButtons.Left, PointerKind.Mouse),
                    "The press was not used."
                );
                Assert.True(view.IsMouseCaptured);
                view.Dispose();
                Assert.False(view.IsMouseCaptured);
                Assert.Null(Mouse.Captured);
            }

            using (var scene = new Scene())
            {
                var view = scene.View;
                DrawUntilShown(view);
                var middle = new Point(view.ActualWidth / 2, view.ActualHeight / 2);
                using var finger = new TestTouchDevice(1, middle, PresentationSource.FromVisual(view));
                view.RaiseEvent(new TouchEventArgs(finger, 0) { RoutedEvent = UIElement.TouchDownEvent });
                Assert.Equal([finger], view.TouchesCaptured);
                view.Dispose();
                Assert.Empty(view.TouchesCaptured);
            }
        });

    /// <summary>
    /// A view under the mouse that is taken out of its panel and disposed takes the mouse's leave that WPF then sends it,
    /// and throws nothing (Issue #601, the investigation's P7): what closing a tab or replacing a plot does. The one test of
    /// the element under the mouse as WPF itself keeps it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A grid with the scene's view, in the window; drawn. The real cursor is not moved and no window is put under it: the
    ///   test sends its window a <c>WM_MOUSEMOVE</c> at the view's middle while the window holds the thread's mouse capture
    ///   (<c>SetCapture</c>). WPF starts a window's mouse input only when the cursor is over the window or the window has the
    ///   capture (read in dotnet/wpf's main, 2026-11-22: <c>HwndMouseInputProvider.ReportInput</c>), and a capture taken
    ///   with no button down does not take the machine's mouse from other windows (the comment on <c>WM_CAPTURECHANGED</c> in
    ///   the same file: Windows picks the queue without this capture).
    ///
    /// Steps:
    /// - Take the capture; send <c>WM_MOUSEMOVE</c>; release the capture. Take the view out of the grid; dispose it; pump
    ///   the dispatcher.
    ///
    /// Expected:
    /// - After the message the mouse is over the view. The pump throws nothing; the view got the mouse's leave after it
    ///   was disposed (so the disposed view's handler ran); after the pump the mouse is not over it.
    /// </remarks>
    [Fact]
    public void Disposed_UnderTheMouse_TakesTheLeave() =>
        wpf.Run(() =>
        {
            using var scene = new Scene();
            var view = scene.View;
            var grid = new System.Windows.Controls.Grid();
            scene.Window.Content = grid;
            _ = grid.Children.Add(view);
            DrawUntilShown(view);
            var middle = new Point(view.ActualWidth / 2, view.ActualHeight / 2);
            var disposed = false;
            var leavesAfterDispose = 0;
            view.AddHandler(Mouse.MouseLeaveEvent, new MouseEventHandler((_, _) => leavesAfterDispose += disposed ? 1 : 0), handledEventsToo: true);

            var client = PresentationSource
                .FromVisual(scene.Window)
                .CompositionTarget.TransformToDevice.Transform(view.TranslatePoint(middle, scene.Window));
            var at = ((uint)(ushort)(short)Math.Round(client.Y) << 16) | (ushort)(short)Math.Round(client.X);
            var hwnd = (HWND)new WindowInteropHelper(scene.Window).Handle;
            Assert.Equal(HWND.NULL, SetCapture(hwnd));
            try
            {
                _ = SendMessageW(hwnd, WM.WM_MOUSEMOVE, 0, (nint)at);
            }
            finally
            {
                Assert.True(ReleaseCapture());
            }

            Assert.True(view.IsMouseOver, $"The mouse is not over the view after WM_MOUSEMOVE at {client} (over {Mouse.DirectlyOver}).");

            grid.Children.Remove(view);
            view.Dispose();
            disposed = true;
            WpfThread.Pump();
            Assert.Equal(1, leavesAfterDispose);
            Assert.False(view.IsMouseOver);
        });

    /// <summary>
    /// Starts a manipulation on the view with a test manipulator, moves it, and ends it, pumping after each, and returns
    /// whether the event of <paramref name="routedEvent"/> was marked handled; asserts that it came.
    /// </summary>
    private static bool Manipulate(HyperPlotView view, Point at, RoutedEvent routedEvent)
    {
        bool? handled = null;
        view.AddHandler(routedEvent, new RoutedEventHandler((_, e) => handled ??= e.Handled), handledEventsToo: true);
        var came = new List<string>();
        foreach (
            var each in new[]
            {
                UIElement.ManipulationStartingEvent,
                UIElement.ManipulationStartedEvent,
                UIElement.ManipulationDeltaEvent,
                UIElement.ManipulationInertiaStartingEvent,
                UIElement.ManipulationCompletedEvent,
            }
        )
        {
            view.AddHandler(each, new RoutedEventHandler((_, e) => came.Add(e.RoutedEvent.Name)), handledEventsToo: true);
        }

        var manipulator = new TestManipulator(1, at);
        try
        {
            Manipulation.AddManipulator(view, manipulator);
            WpfThread.Pump();
            manipulator.Move(at + new Vector(30, 0));

            // The manipulation reads its contacts at WPF's rendering tick or after a layout of its container; a layout is made
            // here, so the test does not wait for the tick.
            view.InvalidateArrange();
            view.UpdateLayout();
            WpfThread.Pump();
            Manipulation.RemoveManipulator(view, manipulator);
            WpfThread.Pump();
        }
        finally
        {
            if (Manipulation.IsManipulationActive(view))
            {
                Manipulation.CompleteManipulation(view);
            }
        }

        Assert.True(handled.HasValue, $"{routedEvent.Name} did not reach the view; it got {string.Join(", ", came)}.");
        return handled.Value;
    }

    /// <summary>
    /// Asserts that the view waits for WPF's rendering tick, or that the tick already drew a frame since <paramref name="drawn"/>
    /// (Issue #305): a pump of the dispatcher may run WPF's own tick, which draws the frame the view waited for.
    /// </summary>
    private static void AssertWaitsOrDrew(HyperPlotView view, int drawn) =>
        Assert.True(
            view.Hooked || view.FramesDrawn > drawn,
            $"The view neither waits for the tick nor drew a frame ({view.FramesDrawn} drawn, {drawn} before)."
        );

    /// <summary>
    /// Asserts that the shared texture the view shows holds the engine's target, made straight (design §7.2). On a mismatch the
    /// message says what Issue #299 needs to tell an unfinished copy from another texture: the surface's textures and copies,
    /// whether a read after 200 ms, and one after those 200 ms and a pump of the dispatcher, match, whether the frame was provisional, and
    /// whether the view uses the shared textures and what decides it (the backend's adapter, Direct3D 9Ex's adapter and device,
    /// the front buffer), and whether it waits for the tick. The pause is on the failure's path only.
    /// </summary>
    private static void AssertShown(HyperPlotView view, D3D9Reader reader)
    {
        var (width, height) = (view.Engine.Width, view.Engine.Height);
        var expected = Target(view);
        var shown = view.ShownHandle;
        var first = Rgba(reader.Read(shown, width, height));
        if (expected.AsSpan().SequenceEqual(first))
        {
            return;
        }

        var state = view.SharedSurface?.Describe() ?? "no surface";
        var zeros = first.All(b => b == 0);
        Thread.Sleep(200);
        var again = Rgba(reader.Read(shown, width, height)).AsSpan().SequenceEqual(expected);
        WpfThread.Pump();
        var afterPump = Rgba(reader.Read(shown, width, height)).AsSpan().SequenceEqual(expected);
        Assert.Fail(
            $"The shown texture {shown} does not hold the target's pixels (Issue #299): {state}; all 0 {zeros}; equal after 200 ms {again}; equal after 200 ms and a pump {afterPump}; provisional {view.Engine.IsProvisional}; shared textures {view.UsesSharedTextures} (backend WARP {view.Backend.IsWarp}, adapter {view.Backend.AdapterLuid}, Direct3D 9Ex's {view.Host.D3D9?.AdapterLuid}, its device {view.Host.D3D9?.HasDevice}, front buffer {view.FrontBufferAvailable}, the test's {view.FrontBuffer?.ToString() ?? "none"}); waits for the tick {view.Hooked}."
        );
    }

    /// <summary>
    /// Runs the dispatcher in real time until a condition holds or a time has passed, and returns whether it holds: for what
    /// WPF tells the view by a notification of its own. The timer that checks is the watchdog too, so the wait always ends.
    /// </summary>
    private static bool WaitUntil(Func<bool> done, TimeSpan bound)
    {
        var frame = new DispatcherFrame();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(5),
            DispatcherPriority.Background,
            (_, _) => frame.Continue = !done() && System.Diagnostics.Stopwatch.GetElapsedTime(started) < bound,
            Dispatcher.CurrentDispatcher
        );
        try
        {
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            timer.Stop();
        }

        return done();
    }

    /// <summary>
    /// Asserts what an input the test sent did, and says on a failure which modifier keys WPF read at that moment (Issue #564):
    /// the view passes them with the input, and the default bindings of a move, a drag, and the wheel take none.
    /// </summary>
    private static void AssertInput(bool done, string otherwise)
    {
        if (!done)
        {
            Assert.Fail($"{otherwise} Keyboard.Modifiers is {Keyboard.Modifiers}.");
        }
    }

    private static void DrawUntilShown(HyperPlotView view)
    {
        // The ticks WPF would run, without waiting for them: each stops waiting, then draws or shows the copy.
        WpfThread.Pump();
        view.Tick();
        for (var i = 0; i < Polls && view.Hooked; i++)
        {
            view.Tick();
        }

        Assert.False(view.Hooked, $"No copy was shown after {Polls} draws.");
        Assert.NotNull(view.Shown);
    }

    private static void Zoom(HyperPlotView view) =>
        view.Engine.Views.ZoomAt(new PanelId(0, 0), new System.Numerics.Vector2(view.Engine.Width / 2f, view.Engine.Height / 2f), 2, animate: false);

    private static ViewRange X(HyperPlotView view) => ((PanelView2D)view.Engine.CurrentView(new PanelId(0, 0))).X;

    private static double Width(ViewRange range) => range.HighValue - range.LowValue;

    private static byte[] Target(HyperPlotView view)
    {
        var pixels = new byte[view.Engine.Width * view.Engine.Height * 4];
        view.Engine.ReadPixels(pixels);
        return pixels;
    }

    private static byte[] Pixels(WriteableBitmap bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    /// <summary>Returns premultiplied B8G8R8A8 pixels as straight RGBA, as ReadPixels gives them.</summary>
    private static byte[] Rgba(byte[] bgra)
    {
        var rgba = new byte[bgra.Length];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var alpha = bgra[i + 3];
            rgba[i] = Divide(bgra[i + 2], alpha);
            rgba[i + 1] = Divide(bgra[i + 1], alpha);
            rgba[i + 2] = Divide(bgra[i], alpha);
            rgba[i + 3] = alpha;
        }

        return rgba;

        // The rounding of Premultiplied.Divide (design §5.3): round(c · 255 / a).
        static byte Divide(byte c, byte a) => a == 0 ? (byte)0 : (byte)Math.Min(255, ((c * 255) + (a / 2)) / a);
    }

    /// <summary>A window with a view of a line plot, shown and laid out.</summary>
    private sealed class Scene : IDisposable
    {
        private readonly Column<float> _y;

        public Scene(D3D11Backend? backend = null, bool pump = true)
        {
            _y = Column.Wrap(Enumerable.Range(0, 1000).Select(i => MathF.Sin(i * 0.02f) * 10).ToArray());
            var table = DataTable.Create().Add("x", Column.Uniform(0, 1, 1000)).Add("y", _y).Build();
            Plot = HyperPlot.Plot.Create(table).Line(table.Col<double>("x"), table.Col<float>("y")).Build();
            View = backend is null ? new HyperPlotView() : new HyperPlotView(backend);
            View.Spec = Plot;
            Window = new Window
            {
                Width = 320,
                Height = 200,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Left = 0,
                Top = 0,
                Content = View,
            };
            Window.Show();
            if (pump)
            {
                WpfThread.Pump();
            }
        }

        public HyperPlotView View { get; }

        public PlotSpec Plot { get; }

        public Window Window { get; }

        public void Dispose()
        {
            Window.Close();
            View.Dispose();
            _y.Dispose();
        }
    }

    /// <summary>
    /// A finger of the test's, at a fixed position in device-independent pixels of the element that asks; active on a
    /// window's source from its making to its disposal, as a finger is while it touches (WPF lists only active fingers among
    /// an element's captured ones).
    /// </summary>
    private sealed class TestTouchDevice : TouchDevice, IDisposable
    {
        private readonly Point _position;

        public TestTouchDevice(int id, Point position, PresentationSource source)
            : base(id)
        {
            _position = position;
            SetActiveSource(source);
            Activate();
        }

        public void Dispose() => Deactivate();

        public override TouchPoint GetTouchPoint(IInputElement relativeTo) =>
            new(this, _position, new Rect(_position, new Size(1, 1)), TouchAction.Move);

        public override TouchPointCollection GetIntermediateTouchPoints(IInputElement relativeTo) => [];
    }

    /// <summary>A manipulator of the test's: a contact at a position in device-independent pixels, moved by the test.</summary>
    private sealed class TestManipulator(int id, Point position) : IManipulator
    {
        private Point _position = position;

        public event EventHandler? Updated;

        public int Id => id;

        public Point GetPosition(IInputElement relativeTo) => _position;

        public void ManipulationEnded(bool cancel) { }

        public void Move(Point to)
        {
            _position = to;
            Updated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Records the horizontal amounts of the wheel's turns, and takes a turn with no vertical amount only.</summary>
    private sealed class HorizontalTool : IPlotTool
    {
        public List<double> Turns { get; } = [];

        public ToolResult OnWheel(in WheelEvent e, ToolContext context)
        {
            if (e.DeltaY != 0)
            {
                return ToolResult.Ignored;
            }

            Turns.Add(e.DeltaX);
            return ToolResult.Handled;
        }
    }
}
