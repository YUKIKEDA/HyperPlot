using System.Windows.Threading;
using HyperPlot.Rendering.D3D11;

namespace HyperPlot.Wpf;

/// <summary>
/// What a dispatcher's views share (design §7.2, row W1a): the Direct3D 9Ex device on adapter 0, and one backend on that
/// adapter, so that its shared textures open there. Made for the dispatcher's first view (the backend when a view needs the
/// default one), and released when the dispatcher shuts down. A backend is for one thread, and a dispatcher is one thread.
/// </summary>
internal sealed class HostResources
{
    [ThreadStatic]
    private static HostResources? _current;

    private D3D11Backend? _backend;

    private HostResources(D3D9Host? d3d9) => D3D9 = d3d9;

    /// <summary>Gets the Direct3D 9Ex device, or null when this machine has none: the views then read back.</summary>
    public D3D9Host? D3D9 { get; }

    /// <summary>Gets the backend the views share, made the first time on the Direct3D 9Ex device's adapter.</summary>
    public D3D11Backend Backend => _backend ??= D3D11Backend.Create(adapterLuid: D3D9?.AdapterLuid);

    /// <summary>Gets a value indicating whether the dispatcher shut down and the resources were released.</summary>
    public bool Released { get; private set; }

    /// <summary>Gets a value indicating whether the shared backend was made.</summary>
    internal bool HasBackend => _backend is not null;

    /// <summary>Returns the current thread's resources, made the first time for its dispatcher.</summary>
    public static HostResources ForCurrentThread()
    {
        if (_current is { Released: false } current)
        {
            return current;
        }

        var made = new HostResources(D3D9Host.TryCreate());
        Dispatcher.CurrentDispatcher.ShutdownStarted += (_, _) => made.Release();
        _current = made;
        return made;
    }

    private void Release()
    {
        Released = true;
        _backend?.Dispose();
        D3D9?.Dispose();
    }
}
