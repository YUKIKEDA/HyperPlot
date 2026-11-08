using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Threading;
using HyperPlot.Interaction;

namespace HyperPlot.Wpf;

/// <content>
/// The plot's summary for the accessibility API (design §7.2 "アクセシビリティ", requirement N-10, row W1c, Issue #323): the
/// automation peer's name, and when its change is told.
/// </content>
public sealed partial class HyperPlotView
{
    /// <summary>How long the view stays unchanged before its summary's change is told (Issue #323 Decision 1).</summary>
    internal static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(500);

    private ITimer? _settle;
    private string? _toldName;
    private bool _specToTell;

    /// <summary>
    /// Gets or sets whether a client of the accessibility API listens for property changes, in place of
    /// <see cref="AutomationPeer.ListenerExists"/>. Tests only.
    /// </summary>
    internal Func<bool> ListenerExists { get; set; } = static () => AutomationPeer.ListenerExists(AutomationEvents.PropertyChanged);

    /// <summary>Gets the number of times the view told that its summary changed. Tests only.</summary>
    internal int NameChangesTold { get; private set; }

    /// <summary>
    /// Returns the view's name for the accessibility API: the application's <see cref="AutomationProperties.NameProperty"/>,
    /// or the engine's summary of the last frame; empty before the first frame and while <see cref="Spec"/> is null, which
    /// shows nothing.
    /// </summary>
    internal string AccessibleName()
    {
        if (AutomationProperties.GetName(this) is { Length: > 0 } name)
        {
            return name;
        }

        if (Spec is null)
        {
            return string.Empty;
        }

        try
        {
            return _disposed ? string.Empty : Engine.Describe();
        }
        catch (InvalidOperationException)
        {
            // No frame drawn yet: there is nothing to tell.
            return string.Empty;
        }
    }

    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new PlotAutomationPeer(this);

    /// <summary>Starts telling the summary: a new spec once its frame is drawn, a view or the end of a provisional frame once settled.</summary>
    private void WatchSummary()
    {
        Engine.ViewChanged += (in ViewChange _) => Settle();
        Engine.ProvisionalChanged += Settle;
    }

    /// <summary>
    /// Marks the spec's change, told once the frame that draws it is drawn (Issue #327 Decision 4); a null spec, which draws
    /// no frame, is told now, as an empty name (PR #329 review).
    /// </summary>
    private void SpecChangedForSummary()
    {
        _specToTell = Spec is not null;
        if (Spec is null)
        {
            TellName();
        }
    }

    /// <summary>Tells a new spec's summary after the frame that drew it.</summary>
    private void FrameDrawnForSummary()
    {
        if (_specToTell)
        {
            _specToTell = false;
            TellName();
        }
    }

    /// <summary>
    /// Starts again the one timer of the engine's clock that tells the summary once nothing changed for
    /// <see cref="SettleTime"/> (Issue #323 Decisions 1, 6): the view's frames stop when it stops, so no tick would tell it.
    /// </summary>
    private void Settle()
    {
        if (_disposed)
        {
            return;
        }

        _settle ??= Engine.Time.CreateTimer(
            static state => ((HyperPlotView)state!).Settled(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan
        );
        _ = _settle.Change(SettleTime, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The timer fired, on the thread pool: the summary is told on the view's dispatcher.</summary>
    private void Settled() => _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, TellName);

    /// <summary>Tells the accessibility clients that the summary changed, when one listens and it did.</summary>
    private void TellName()
    {
        if (_disposed || !ListenerExists())
        {
            return;
        }

        var name = AccessibleName();
        if (name == _toldName)
        {
            return;
        }

        var old = _toldName ?? string.Empty;
        _toldName = name;
        NameChangesTold++;
        UIElementAutomationPeer.CreatePeerForElement(this)?.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, old, name);
    }

    /// <summary>Lets the timer go: the view is unloaded or disposed.</summary>
    private void StopSummary()
    {
        _settle?.Dispose();
        _settle = null;
    }

    /// <summary>The view's automation peer (design §7.2): an image whose name is the plot's summary.</summary>
    private sealed class PlotAutomationPeer(HyperPlotView view) : FrameworkElementAutomationPeer(view)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

        protected override string GetClassNameCore() => nameof(HyperPlotView);

        protected override string GetNameCore() => view.AccessibleName();
    }
}
