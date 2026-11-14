using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;

[assembly: AssemblyFixture(typeof(HyperPlot.Wpf.Tests.WpfThread))]

namespace HyperPlot.Wpf.Tests;

/// <summary>
/// The one thread of the test assembly that touches WPF (rule testing: process-wide state from one dedicated thread): an STA
/// thread running a dispatcher, which every test sends its work to. It keeps one window open from the first test to the last
/// (Issue #299). <c>D3DImage</c> says it has no front buffer when it first presents while WPF has no Direct3D device of its
/// own for the adapter, which is so while composition has no render target (read in dotnet/wpf,
/// <c>CInteropDeviceBitmap::CreateFrontBuffer</c>). With each test opening and closing the only window, a view lost its front
/// buffer right after its first shared frame in 13 runs of 20, and a test that reads the shared texture then failed; with
/// this window open, in none of 20 (the same build, the runs alternated).
/// </summary>
public sealed class WpfThread : IDisposable
{
    /// <summary>
    /// The collection of a test class that runs alone, after the others (row W1c): a test that pumps the dispatcher would
    /// otherwise run another class's work posted meanwhile. <c>HyperPlotViewTests</c> stays in its own collection: put into
    /// this one, its device-loss test failed in most runs (Issue #299).
    /// </summary>
    public const string Collection = "WPF thread, alone";

    private readonly Thread _thread;
    private readonly Dispatcher _dispatcher;
    private readonly Window _window;

    public WpfThread()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "WPF tests",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        _dispatcher = dispatcher!;
        _window = _dispatcher.Invoke(() =>
        {
            var window = new Window
            {
                Width = 16,
                Height = 16,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Left = 0,
                Top = 0,
            };
            window.Show();
            return window;
        });
    }

    /// <summary>Runs work on the dispatcher's thread and returns when it is done, its exception thrown here with its stack.</summary>
    public void Run(Action work) =>
        _ = Run(() =>
        {
            work();
            return 0;
        });

    /// <summary>Runs work on the dispatcher's thread and returns its result.</summary>
    public T Run<T>(Func<T> work)
    {
        ExceptionDispatchInfo? failure = null;
        var result = _dispatcher.Invoke(() =>
        {
            try
            {
                return work();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
                return default!;
            }
        });
        failure?.Throw();
        return result;
    }

    /// <summary>
    /// Runs the dispatcher's work queued at Background priority or above (layout, Loaded, input), without waiting in real
    /// time. Called on the dispatcher's thread.
    /// </summary>
    public static void Pump()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    public void Dispose()
    {
        _dispatcher.Invoke(_window.Close);
        _dispatcher.InvokeShutdown();
        _ = _thread.Join(TimeSpan.FromSeconds(10));
    }
}

/// <summary>The collection of <see cref="WpfThread.Collection"/>: its classes run with no other test running.</summary>
[CollectionDefinition(WpfThread.Collection, DisableParallelization = true)]
public sealed class AloneOnTheWpfThread;
