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
            Assert.Equal(Target(view), Rgba(d3d9.Read(view.ShownHandle, view.Engine.Width, view.Engine.Height)));
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
    /// - Say the front buffer is lost; draw until shown. Say it is back; draw until shown.
    ///
    /// Expected:
    /// - Lost: no shared texture is shown, the image shows a Pbgra32 bitmap of the engine's size, whose pixels, made
    ///   straight, are the target's. Back: the image shows the D3DImage with a shared texture again.
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
            Assert.Equal(Target(view), Rgba(d3d9.Read(view.ShownHandle, view.Engine.Width, view.Engine.Height)));

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
            Assert.Equal(Target(view), Rgba(d3d9.Read(view.ShownHandle, view.Engine.Width, view.Engine.Height)));
        });

    /// <summary>
    /// <c>Spec</c> takes a plot or a figure, and null shows nothing; another node throws (design §7.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A view in a window.
    ///
    /// Steps:
    /// - Set a figure of two plots and draw; set a plot and draw; set a scale; set null.
    ///
    /// Expected:
    /// - The figure: the view of plot 1 reads. The plot: the view of plot 0 reads and plot 1's throws. The scale:
    ///   ArgumentException, the spec unchanged. Null: the image shows nothing.
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
            view.Spec = null;
            Assert.Null(view.Shown);
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
    /// - Read the engine's size; make the window 400 × 260; collapse the view and draw.
    ///
    /// Expected:
    /// - Each time, the engine's size and scale are the view's DIPs times the DPI, rounded, and the DPI's scale. Collapsed,
    ///   a draw keeps the size and the image as it was.
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
    ///   wanted; back in, it waits.
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
            WpfThread.Pump();
            Assert.True(view.Hooked);
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

            Assert.True(view.Pointer(PointerEventKind.Down, middle, PointerButtons.Left, PointerButtons.Left, PointerKind.Mouse));
            Assert.True(view.IsFocused);
            Assert.True(view.IsMouseCaptured);
            _ = view.Pointer(PointerEventKind.Move, middle + new Vector(30, 0), PointerButtons.None, PointerButtons.Left, PointerKind.Mouse);
            Assert.NotEqual(x, X(view));
            var inside = X(view);
            var outside = new Point(view.ActualWidth + 40, middle.Y);
            _ = view.Pointer(PointerEventKind.Move, outside, PointerButtons.None, PointerButtons.Left, PointerKind.Mouse);
            Assert.NotEqual(inside, X(view));
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
            Assert.True(view.Wheel(middle, 0, 2));
            Assert.True(Width(X(view)) < Width(x));
            x = X(view);
            Assert.True(view.Key(KeyEventKind.Down, Key.Add, repeat: false));
            Assert.True(Width(X(view)) < Width(x));
            Assert.False(view.Key(KeyEventKind.Down, Key.F1, repeat: false));

            var screen = view.PointToScreen(middle);
            Assert.True(view.HorizontalWheel((nint)120 << 16, Words((int)screen.X, (int)screen.Y)));
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
            Assert.True(view.Touch(view.Touches.Down(1), PointerEventKind.Down, middle));
            Assert.True(view.IsFocused);
            _ = view.Touch(view.Touches.Move(1), PointerEventKind.Move, middle + new Vector(30, 0));
            Assert.NotEqual(x, X(view));
            Assert.True(view.Touch(view.Touches.Down(2), PointerEventKind.Down, middle + new Vector(0, 20)));

            x = X(view);
            view.Manipulate(middle, new Vector(1.5, 1.5), default);
            Assert.True(Width(X(view)) < Width(x) * 0.9);
            x = X(view);
            view.Manipulate(middle, new Vector(1, 1), new Vector(20, 0));
            Assert.NotEqual(x, X(view));
            Assert.Equal(Width(x), Width(X(view)), 1e-9 * Width(x));
            _ = view.Touch(view.Touches.Up(1), PointerEventKind.Up, middle);
            _ = view.Touch(view.Touches.Up(2), PointerEventKind.Up, middle);
            Assert.False(view.Touches.Gesturing);
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
