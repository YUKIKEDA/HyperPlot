using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HyperPlot.Interaction;
using HyperPlot.Rendering;
using HyperPlot.Rendering.D3D11;

namespace HyperPlot.Wpf;

/// <summary>
/// Shows a plot or a figure in WPF and passes the user's input to it (design §7.2, requirement H-2): set <see cref="Spec"/>,
/// and use <see cref="Engine"/> for the views, the bindings, the constraints, and the notices. The plot is drawn with
/// Direct3D 11 and shown through <c>D3DImage</c>, so WPF elements can be placed over it; when WPF renders in software, its
/// front buffer is lost, or the backend is on another adapter, the frames are read back into a <c>WriteableBitmap</c>
/// instead (requirement H-3).
/// </summary>
/// <remarks>
/// A view is used from its dispatcher's thread. The views of a dispatcher share a backend, released when the dispatcher shuts
/// down; <see cref="Dispose"/> releases a view's own resources earlier.
/// </remarks>
public sealed class HyperPlotView : FrameworkElement, IDisposable
{
    /// <summary>Identifies the <see cref="Spec"/> dependency property.</summary>
    public static readonly DependencyProperty SpecProperty = DependencyProperty.Register(
        nameof(Spec),
        typeof(SpecNode),
        typeof(HyperPlotView),
        new PropertyMetadata(null, (view, e) => ((HyperPlotView)view).OnSpecChanged((SpecNode?)e.NewValue)),
        value => value is null or PlotSpec or FigureSpec
    );

    private readonly HostResources _host;
    private readonly D3D11Backend _backend;
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly D3DImage _d3dImage = new();
    private readonly TouchTracker _touch = new();
    private readonly EventHandler _onRendering;

    // The shared textures opened in Direct3D 9Ex, by handle: the two of the shared surface, at most.
    private readonly (nint Handle, nint Surface)[] _opened = new (nint, nint)[2];
    private D3D11SharedSurface? _shared;
    private D3D11ReadbackSurface? _readback;
    private WriteableBitmap? _bitmap;
    private HwndSource? _source;
    private nint _shown;
    private (double X, double Y) _dpi = (1, 1);
    private (int Width, int Height, float Scale) _size = (1, 1, 1);
    private bool _sized;
    private bool _resizing;
    private bool _wanted;
    private bool _polling;
    private bool _pollingShared;
    private bool _hooked;
    private bool _pressed;
    private bool _releasing;
    private bool _disposed;

    static HyperPlotView()
    {
        FocusableProperty.OverrideMetadata(typeof(HyperPlotView), new FrameworkPropertyMetadata(true));
    }

    /// <summary>Initializes a new instance of the <see cref="HyperPlotView"/> class with its dispatcher's backend.</summary>
    /// <exception cref="InvalidOperationException">No Direct3D 11 device could be made, WARP included.</exception>
    public HyperPlotView()
    {
        _host = HostResources.ForCurrentThread();
        _backend = _host.Backend;
        _onRendering = OnRendering;
        Engine = Initialize();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HyperPlotView"/> class with a backend of the application's, which it
    /// disposes after the view. A backend on another adapter than WPF's is read back (design §7.2).
    /// </summary>
    /// <param name="backend">The backend; used from this thread only.</param>
    /// <exception cref="ArgumentNullException"><paramref name="backend"/> is null.</exception>
    public HyperPlotView(D3D11Backend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _host = HostResources.ForCurrentThread();
        _backend = backend;
        _onRendering = OnRendering;
        Engine = Initialize();
    }

    /// <summary>
    /// Gets or sets what the view shows: a <see cref="PlotSpec"/> or a <see cref="FigureSpec"/>; null shows nothing. Another
    /// <see cref="SpecNode"/> throws <see cref="ArgumentException"/>, and a spec that does not check throws as
    /// <see cref="PlotEngine.SetPlot"/> and <see cref="PlotEngine.SetFigure"/> do.
    /// </summary>
    public SpecNode? Spec
    {
        get => (SpecNode?)GetValue(SpecProperty);
        set => SetValue(SpecProperty, value);
    }

    /// <summary>Gets the engine that draws and takes the input: its views, bindings, constraints, options, and notices.</summary>
    public PlotEngine Engine { get; }

    /// <summary>Gets the backend the view draws with.</summary>
    internal D3D11Backend Backend => _backend;

    /// <summary>Gets the resources the view's dispatcher shares.</summary>
    internal HostResources Host => _host;

    /// <summary>Gets a value indicating whether <c>D3DImage</c> has its front buffer, whatever <see cref="FrontBuffer"/> says. Tests only (Issue #299).</summary>
    internal bool FrontBufferAvailable => _d3dImage.IsFrontBufferAvailable;

    /// <summary>Gets the shared surface the frames are copied to, or null. Tests only (Issue #299).</summary>
    internal D3D11SharedSurface? SharedSurface => _shared;

    /// <summary>Gets the shared texture <c>D3DImage</c> shows, or 0. Tests only.</summary>
    internal nint ShownHandle => _shown;

    /// <summary>Gets the bitmap the read-back frames go to, or null. Tests only.</summary>
    internal WriteableBitmap? Bitmap => _bitmap;

    /// <summary>Gets the image's source: the <c>D3DImage</c>, the bitmap, or null. Tests only.</summary>
    internal ImageSource? Shown => _image.Source;

    /// <summary>Gets a value indicating whether the view waits for <c>CompositionTarget.Rendering</c>. Tests only.</summary>
    internal bool Hooked => _hooked;

    /// <summary>Gets or sets whether the front buffer is there, in place of <c>D3DImage</c>'s; null reads it. Tests only.</summary>
    internal bool? FrontBuffer { get; set; }

    /// <summary>Gets a value indicating whether the frames go through the shared textures (else they are read back).</summary>
    internal bool UsesSharedTextures =>
        _host.D3D9 is { HasDevice: true } d3d9 && _backend.AdapterLuid == d3d9.AdapterLuid && (FrontBuffer ?? _d3dImage.IsFrontBufferAvailable);

    /// <inheritdoc />
    protected override int VisualChildrenCount => 1;

    /// <summary>Releases the view's engine and surfaces. The backend stays with its owner.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unhook();
        ReleaseOpened();
        _shared?.Dispose();
        _readback?.Dispose();
        Engine.Dispose();
    }

    /// <summary>
    /// Draws the frame the engine asked for and shows it, or shows a copy that was not done at the last tick (design §7.2).
    /// What <c>CompositionTarget.Rendering</c> runs; the tests call it to draw without waiting for WPF's ticks.
    /// </summary>
    internal void Render()
    {
        if (_disposed)
        {
            return;
        }

        if (Spec is null || !_sized || !IsVisible)
        {
            // Nothing to draw: the tick is left until a spec, a size, or the view shown again asks again (design §7.2).
            Unhook();
            return;
        }

        if (_wanted && _host.D3D9 is { HasDevice: false } d3d9)
        {
            // The Direct3D 9Ex device could not be made: tried again once a frame, which is read back until it is (design §7.2).
            _ = d3d9.EnsureDevice();
        }

        var shared = UsesSharedTextures;
        if (_polling && shared != _pollingShared)
        {
            // The way changed while a copy waited (the front buffer, the Direct3D 9Ex device): the frame is drawn again.
            (_polling, _wanted) = (false, true);
        }

        try
        {
            if (_wanted)
            {
                _wanted = false;
                using (_backend.BeginFrame())
                {
                    if (_resizing)
                    {
                        // A size the device loss kept from the engine: the frame has made the device again.
                        Engine.Resize(_size.Width, _size.Height, _size.Scale);
                        _resizing = false;
                    }

                    Engine.RenderFrame();

                    if (shared)
                    {
                        Shared().Present(Engine.Target);
                    }
                    else
                    {
                        Readback().Present(Engine.Target);
                    }
                }

                (_polling, _pollingShared) = (true, shared);
            }

            if (_polling)
            {
                // A copy taken while a newer one is still made goes on polling for the newest, the last frame shown; a frame
                // wanted meanwhile is drawn at the next tick instead (row W1b1: provisional frames keep coming).
                _polling = !(shared ? ShowShared() && (_wanted || !_shared!.HasUnread) : ShowReadback() && (_wanted || !_readback!.HasUnread));
            }
        }
        catch (GpuDeviceLostException)
        {
            Lost();
        }

        if (_wanted || _polling)
        {
            Hook();
        }
    }

    /// <summary>The front buffer came or went (design §7.2): a lost one reads back; a new one opens the textures again.</summary>
    internal void FrontBufferChanged()
    {
        ReleaseOpened();
        _wanted = true;
        Hook();
    }

    /// <summary>Reads the size in DIPs and the DPI, and resizes the engine's target to the pixels they make (design §7.2).</summary>
    internal void UpdateSize()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        _dpi = (dpi.DpiScaleX, dpi.DpiScaleY);
        var (width, height) = InputTranslator.PixelSize(ActualWidth, ActualHeight, dpi.DpiScaleX, dpi.DpiScaleY);
        _sized = width >= 1 && height >= 1;
        if (!_sized)
        {
            return;
        }

        _size = (Math.Min(width, RenderTargetDescription.MaxSide), Math.Min(height, RenderTargetDescription.MaxSide), (float)dpi.DpiScaleX);
        try
        {
            Engine.Resize(_size.Width, _size.Height, _size.Scale);
        }
        catch (GpuDeviceLostException)
        {
            // Called from WPF's layout, where the application cannot catch it: the next frame resizes (design §7.2).
            _resizing = true;
            Lost();
        }

        // A view shown again at the size it had asks for nothing new; a frame wanted while it was empty is drawn now.
        if (_wanted || _polling)
        {
            Hook();
        }
    }

    /// <inheritdoc />
    protected override Visual GetVisualChild(int index) =>
        index == 0 ? _image : throw new ArgumentOutOfRangeException(nameof(index), index, "A view has one child.");

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        _image.Measure(availableSize);
        return default;
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        _image.Arrange(new Rect(finalSize));
        return finalSize;
    }

    /// <inheritdoc />
    /// <remarks>The whole rectangle takes the pointer, drawn or not.</remarks>
    protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters) =>
        new PointHitTestResult(this, hitTestParameters.HitPoint);

    /// <inheritdoc />
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateSize();
    }

    /// <inheritdoc />
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateSize();
    }

    /// <inheritdoc />
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (!IsTouch(e))
        {
            e.Handled = Pointer(PointerEventKind.Down, e.GetPosition(this), InputTranslator.Button(e.ChangedButton), Held(e), Device(e));
        }
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsTouch(e))
        {
            e.Handled = Pointer(PointerEventKind.Move, e.GetPosition(this), PointerButtons.None, Held(e), Device(e));
        }
    }

    /// <inheritdoc />
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (!IsTouch(e))
        {
            e.Handled = Pointer(PointerEventKind.Up, e.GetPosition(this), InputTranslator.Button(e.ChangedButton), Held(e), Device(e));
        }
    }

    /// <inheritdoc />
    /// <remarks>A capture lost while a button is down (another window took it) cancels the drag (design §6.1).</remarks>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_pressed && !_releasing)
        {
            _pressed = false;
            _ = Engine.OnPointer(
                new PointerEvent(
                    PointerEventKind.Cancel,
                    Pixels(e.GetPosition(this)),
                    PointerButtons.None,
                    PointerButtons.None,
                    Modifiers(),
                    Device(e),
                    Engine.Time.GetTimestamp()
                )
            );
        }
    }

    /// <inheritdoc />
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        e.Handled = Wheel(e.GetPosition(this), 0, InputTranslator.Notches(e.Delta));
    }

    /// <inheritdoc />
    protected override void OnTouchDown(TouchEventArgs e)
    {
        base.OnTouchDown(e);
        _ = CaptureTouch(e.TouchDevice);
        _ = Touch(_touch.Down(e.TouchDevice.Id), PointerEventKind.Down, e.GetTouchPoint(this).Position);

        // Handled, so that WPF makes no mouse event of the touch; the manipulation still comes.
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnTouchMove(TouchEventArgs e)
    {
        base.OnTouchMove(e);
        _ = Touch(_touch.Move(e.TouchDevice.Id), PointerEventKind.Move, e.GetTouchPoint(this).Position);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnTouchUp(TouchEventArgs e)
    {
        base.OnTouchUp(e);
        _ = ReleaseTouchCapture(e.TouchDevice);
        _ = Touch(_touch.Up(e.TouchDevice.Id), PointerEventKind.Up, e.GetTouchPoint(this).Position);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnManipulationStarting(ManipulationStartingEventArgs e)
    {
        base.OnManipulationStarting(e);
        e.ManipulationContainer = this;
        e.Mode = ManipulationModes.Scale | ManipulationModes.Translate;
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnManipulationDelta(ManipulationDeltaEventArgs e)
    {
        base.OnManipulationDelta(e);
        Manipulate(e.ManipulationOrigin, e.DeltaManipulation.Scale, e.DeltaManipulation.Translation);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnManipulationInertiaStarting(ManipulationInertiaStartingEventArgs e)
    {
        base.OnManipulationInertiaStarting(e);

        // WPF's inertia, in DIPs per ms²: 10 inches/s² for a move and 0.1 inches/s² for a scale, the values of Microsoft Learn's
        // "Walkthrough: Creating Your First Touch Application".
        e.TranslationBehavior.DesiredDeceleration = 10.0 * 96 / (1000.0 * 1000.0);
        e.ExpansionBehavior.DesiredDeceleration = 0.1 * 96 / (1000.0 * 1000.0);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        e.Handled = Key(KeyEventKind.Down, e);
    }

    /// <inheritdoc />
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        e.Handled = Key(KeyEventKind.Up, e);
    }

    /// <summary>
    /// Passes a pointer's event at a position in DIPs (design §6.1 "WPF の変換"): a press the engine uses takes the focus and
    /// captures the mouse, so that a drag goes on outside the view; the last button up releases it. Returns whether the
    /// engine used it.
    /// </summary>
    internal bool Pointer(PointerEventKind kind, Point at, PointerButtons button, PointerButtons held, PointerKind device)
    {
        if (kind == PointerEventKind.Down)
        {
            _ = Focus();
        }

        var used = Engine.OnPointer(new PointerEvent(kind, Pixels(at), button, held, Modifiers(), device, Engine.Time.GetTimestamp()));
        if (kind == PointerEventKind.Down && used && !_pressed)
        {
            _pressed = true;
            _ = CaptureMouse();
        }
        else if (kind == PointerEventKind.Up && held == PointerButtons.None && _pressed)
        {
            _pressed = false;
            _releasing = true;
            ReleaseMouseCapture();
            _releasing = false;
        }

        return used;
    }

    /// <summary>Passes a turn of the wheel at a position in DIPs, in notches. Returns whether the engine used it.</summary>
    internal bool Wheel(Point at, double notchesX, double notchesY) =>
        Engine.OnWheel(new WheelEvent(Pixels(at), notchesX, notchesY, Modifiers(), Engine.Time.GetTimestamp()));

    /// <summary>
    /// Passes a finger's action at a position in DIPs: the pointer's event of the kind (the contact is the left button), a
    /// cancel, or nothing (design §6.1). A finger down takes the focus. Returns whether the engine used it.
    /// </summary>
    internal bool Touch(TouchTracker.Action action, PointerEventKind kind, Point at)
    {
        if (kind == PointerEventKind.Down)
        {
            _ = Focus();
        }

        if (action == TouchTracker.Action.None)
        {
            return false;
        }

        var (eventKind, button, held) =
            action == TouchTracker.Action.Cancel
                ? (PointerEventKind.Cancel, PointerButtons.None, PointerButtons.None)
                : kind switch
                {
                    PointerEventKind.Down => (kind, PointerButtons.Left, PointerButtons.Left),
                    PointerEventKind.Up => (kind, PointerButtons.Left, PointerButtons.None),
                    _ => (kind, PointerButtons.None, PointerButtons.Left),
                };
        return Engine.OnPointer(new PointerEvent(eventKind, Pixels(at), button, held, Modifiers(), PointerKind.Touch, Engine.Time.GetTimestamp()));
    }

    /// <summary>
    /// Passes a manipulation's step while two or more fingers are down (design §6.1): a pinch by the mean of the scale's
    /// two parts, and a scroll by the move in device pixels; WPF's inertia comes as more steps. Nothing for one finger.
    /// </summary>
    internal void Manipulate(Point origin, System.Windows.Vector scale, System.Windows.Vector translation)
    {
        if (!_touch.Gesturing)
        {
            return;
        }

        var center = Pixels(origin);
        var pinch = (scale.X + scale.Y) / 2;
        if (pinch != 1 && double.IsFinite(pinch) && pinch > 0)
        {
            _ = Engine.OnGesture(new GestureEvent(GestureKind.Pinch, center, pinch, Vector2.Zero, Engine.Time.GetTimestamp()));
        }

        if (translation.X != 0 || translation.Y != 0)
        {
            var delta = new Vector2((float)(translation.X * _dpi.X), (float)(translation.Y * _dpi.Y));
            _ = Engine.OnGesture(new GestureEvent(GestureKind.Scroll, center, 1, delta, Engine.Time.GetTimestamp()));
        }
    }

    /// <summary>Gets the fingers on the view. Tests only.</summary>
    internal TouchTracker Touches => _touch;

    private static bool IsTouch(MouseEventArgs e) => e.StylusDevice?.TabletDevice?.Type == TabletDeviceType.Touch;

    private static Interaction.ModifierKeys Modifiers() => InputTranslator.Modifiers(Keyboard.Modifiers);

    private PlotEngine Initialize()
    {
        var engine = new PlotEngine(_backend, 1, 1);
        engine.FrameRequested += () =>
        {
            _wanted = true;
            Hook();
        };
        IsManipulationEnabled = true;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
        AddVisualChild(_image);
        _d3dImage.IsFrontBufferAvailableChanged += (_, _) => FrontBufferChanged();
        Loaded += (_, _) => OnLoaded();
        IsVisibleChanged += (_, _) => OnVisibleChanged();
        Unloaded += (_, _) => OnUnloaded();
        return engine;
    }

    private void OnSpecChanged(SpecNode? spec)
    {
        switch (spec)
        {
            case PlotSpec plot:
                Engine.SetPlot(plot);
                break;
            case FigureSpec figure:
                Engine.SetFigure(figure);
                break;
            default:
                _image.Source = null;
                break;
        }

        // A frame wanted before a null spec left the tick: the engine asks once until it draws, so a new spec asks nothing.
        if (_wanted || _polling)
        {
            Hook();
        }
    }

    private void OnLoaded()
    {
        _source = PresentationSource.FromVisual(this) as HwndSource;
        _source?.AddHook(WndProc);
        UpdateSize();
        if (_wanted || _polling)
        {
            Hook();
        }
    }

    /// <summary>
    /// A view collapsed or hidden keeps its size (WPF gives no size change), so the tick is left by <see cref="Render"/>;
    /// shown again, a frame wanted meanwhile is drawn.
    /// </summary>
    private void OnVisibleChanged()
    {
        if (IsVisible && (_wanted || _polling))
        {
            Hook();
        }
    }

    private void OnUnloaded()
    {
        Unhook();
        _source?.RemoveHook(WndProc);
        _source = null;
    }

    private void Hook()
    {
        if (!_hooked && IsLoaded && !_disposed)
        {
            CompositionTarget.Rendering += _onRendering;
            _hooked = true;
        }
    }

    private void Unhook()
    {
        if (_hooked)
        {
            CompositionTarget.Rendering -= _onRendering;
            _hooked = false;
        }
    }

    private void OnRendering(object? sender, EventArgs e) => Tick();

    /// <summary>
    /// What WPF's rendering tick does: draws the frame wanted or shows the copy that waits; a tick that finds neither stops
    /// waiting for the tick. While frames keep coming the view stays subscribed, since each subscription allocates WPF's
    /// handler list; the cost is one tick with nothing to do after the last frame.
    /// </summary>
    internal void Tick()
    {
        if (!_wanted && !_polling)
        {
            Unhook();
            return;
        }

        Render();
    }

    private D3D11SharedSurface Shared()
    {
        if (_shared is { } shared && shared.Width == Engine.Width && shared.Height == Engine.Height)
        {
            return shared;
        }

        // New handles: the old textures are closed and the image takes the new surface when it comes.
        ReleaseOpened();
        _shared?.Dispose();
        _shared = null;
        _shared = new D3D11SharedSurface(_backend, Engine.Width, Engine.Height);
        return _shared;
    }

    private D3D11ReadbackSurface Readback()
    {
        if (_readback is { } readback && readback.Width == Engine.Width && readback.Height == Engine.Height)
        {
            return readback;
        }

        _readback?.Dispose();
        _readback = null;
        _readback = new D3D11ReadbackSurface(_backend, Engine.Width, Engine.Height);
        return _readback;
    }

    /// <summary>Hands the newest finished copy to <c>D3DImage</c>; false while none is done.</summary>
    private bool ShowShared()
    {
        var d3d9 = _host.D3D9!;
        if (!d3d9.EnsureDevice())
        {
            // The Direct3D 9Ex device was made again: what it had opened is gone (design §7.2).
            ReleaseOpened();
            if (!d3d9.HasDevice)
            {
                // It could not be made: the frame is drawn again, read back (UsesSharedTextures).
                _wanted = true;
                return true;
            }
        }

        if (!_shared!.TryTakeCompleted(out var handle))
        {
            return false;
        }

        var surface = Open(d3d9, handle);

        if (surface == 0)
        {
            return true;
        }

        _d3dImage.Lock();
        try
        {
            if (handle != _shown)
            {
                _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surface);
                _shown = handle;
            }

            _d3dImage.AddDirtyRect(new Int32Rect(0, 0, _d3dImage.PixelWidth, _d3dImage.PixelHeight));
        }
        finally
        {
            _d3dImage.Unlock();
        }

        if (_image.Source != _d3dImage)
        {
            _image.Source = _d3dImage;
        }

        return true;
    }

    /// <summary>Reads the newest finished copy into the bitmap; false while none is done.</summary>
    private unsafe bool ShowReadback()
    {
        var (width, height) = (_readback!.Width, _readback.Height);
        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _bitmap = new WriteableBitmap(width, height, 96 * _dpi.X, 96 * _dpi.Y, PixelFormats.Pbgra32, null);
        }

        bool read;
        _bitmap.Lock();
        try
        {
            // The back buffer holds BackBufferStride · PixelHeight bytes, valid between Lock and Unlock.
            var stride = _bitmap.BackBufferStride;
            read = _readback.TryRead(new Span<byte>((void*)_bitmap.BackBuffer, stride * height), stride);
            if (read)
            {
                _bitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
            }
        }
        finally
        {
            _bitmap.Unlock();
        }

        if (_image.Source != _bitmap)
        {
            _image.Source = _bitmap;
        }

        return read;
    }

    /// <summary>Returns the surface of a shared texture, opened the first time; the other one stays open, the older goes.</summary>
    private nint Open(D3D9Host d3d9, nint handle)
    {
        foreach (var (opened, surface) in _opened)
        {
            if (opened == handle)
            {
                return surface;
            }
        }

        var made = d3d9.Open(handle, Engine.Width, Engine.Height);
        if (made == 0)
        {
            return 0;
        }

        // The slot that is not shown takes it.
        var slot = _opened[0].Handle == _shown && _shown != 0 ? 1 : 0;
        D3D9Host.Release(_opened[slot].Surface);
        _opened[slot] = (handle, made);
        return made;
    }

    private void ReleaseOpened()
    {
        if (_shown != 0)
        {
            _d3dImage.Lock();
            _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, 0);
            _d3dImage.Unlock();
            _shown = 0;
        }

        for (var i = 0; i < _opened.Length; i++)
        {
            D3D9Host.Release(_opened[i].Surface);
            _opened[i] = default;
        }
    }

    /// <summary>
    /// A lost device (design §7.2): the backend makes it again at the next frame; the surfaces and the opened textures are
    /// made again then.
    /// </summary>
    private void Lost()
    {
        ReleaseSurfaces();
        _wanted = true;
        Hook();
    }

    private void ReleaseSurfaces()
    {
        ReleaseOpened();
        _shared?.Dispose();
        _shared = null;
        _readback?.Dispose();
        _readback = null;
        _polling = false;
    }

    private Vector2 Pixels(Point dips) => InputTranslator.ToPixels(dips, _dpi.X, _dpi.Y);

    private static PointerButtons Held(MouseEventArgs e) =>
        InputTranslator.Buttons(e.LeftButton, e.RightButton, e.MiddleButton, e.XButton1, e.XButton2);

    private static PointerKind Device(MouseEventArgs e) => InputTranslator.Kind(e.StylusDevice is not null, IsTouch(e));

    private bool Key(KeyEventKind kind, KeyEventArgs e) => Key(kind, e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key, e.IsRepeat);

    /// <summary>Passes a key the engine knows; a key it does not is not used. Returns whether the engine used it.</summary>
    internal bool Key(KeyEventKind kind, Key key, bool repeat)
    {
        var plotKey = InputTranslator.Key(key);
        return plotKey != PlotKey.None && Engine.OnKey(new KeyEvent(kind, plotKey, Modifiers(), repeat, Engine.Time.GetTimestamp()));
    }

    /// <summary>The horizontal wheel (design §6.1): WPF raises no event for <c>WM_MOUSEHWHEEL</c>, so the window's hook reads it.</summary>
    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != InputTranslator.WmMouseHWheel || !IsVisible)
        {
            return 0;
        }

        handled = HorizontalWheel(wParam, lParam);
        return 0;
    }

    /// <summary>Reads a <c>WM_MOUSEHWHEEL</c> message's values: a turn over the view goes to the engine. Returns whether it used it.</summary>
    internal bool HorizontalWheel(nint wParam, nint lParam)
    {
        var (notches, x, y) = InputTranslator.HorizontalWheel(wParam, lParam);
        var at = PointFromScreen(new Point(x, y));
        return at.X >= 0 && at.Y >= 0 && at.X < ActualWidth && at.Y < ActualHeight && Wheel(at, notches, 0);
    }
}
