using System.Numerics;
using HyperPlot.Rendering;

namespace HyperPlot.Interaction;

/// <content>
/// The links of the engine (design §6.4 "リンク", row I3b, Issue #373): the groups, the panels each kind joins, and the copy of a
/// change to them.
/// </content>
public sealed partial class PlotEngine
{
    // The kinds a closure is kept for, in the order of _roots.
    private const int ViewXKind = 0;
    private const int ViewYKind = 1;
    private const int CursorKind = 2;
    private const int SelectionLinkKind = 3;

    // A member's changes, told after its commit in the order of the panels.
    private readonly List<PanelId> _linkNotices = [];

    // The panels as slots, plot by plot (_slotStart[k] is plot k's first), and what made them.
    private int[] _slotStart = [0];
    private object?[] _linkSpecs = [];
    private int[] _linkPanels = [];
    private bool _linksDirty = true;

    // A union-find of the slots for each kind: the panels the groups of that kind join (the view's with a facet's shared axes).
    private int[][] _roots =
    [
        [],
        [],
        [],
        [],
    ];

    // Whether two plots' x (or y) take each other's values: the same space, and a band's same categories (decided when built).
    private bool[] _sameX = [];
    private bool[] _sameY = [];

    // The changes waiting to be copied (Issue #373 Decision 10): a ring of slots (a view) and of plots (a selection, −1 − plot),
    // each at most once, the later change of a panel taking the place of the earlier.
    private int[] _queue = [];
    private int _queueHead;
    private int _queueCount;
    private bool[] _viewQueued = [];
    private ViewAxes[] _queueAxes = [];
    private bool[] _queueAnimate = [];
    private bool[] _selectionQueued = [];
    private bool _draining;

    // A member's change told after its commit, in the order of the panels; the panels a copy moved.
    private bool _linkCommit;
    private bool[] _movedX = [];
    private bool[] _movedY = [];

    // A plot's selection on more of its panels (a facet's linked panels), and the buffer of a linked cursor's hits.
    private bool[][] _applied = [];
    private SeriesHit[] _linkHits = new SeriesHit[16];

    /// <summary>
    /// Gets the link groups (design §6.4, requirement I-4, row I3b): the panels that share views, the cursor, or the selection.
    /// </summary>
    public LinkGroups Links { get; }

    internal void CheckLinkThread() => CheckThread();

    /// <summary>Returns a panel's target view: where an animation ends. Tests only.</summary>
    internal PanelDomains TargetView(PanelId panel) => Resolve(panel.Plot, panel.Panel, _states[panel.Plot].Target[panel.Panel]);

    /// <summary>Returns the number of entries of a plot's history and the place in it. Tests only.</summary>
    internal (int Count, int Position) HistoryOf(int plot) => (_states[plot].Count, _states[plot].Position);

    /// <summary>Marks the closures to be built again: a group or a member changed.</summary>
    internal void LinksChanged() => _linksDirty = true;

    /// <summary>Marks the closures to be built again when a plot's spec or number of panels changed (a frame drawn again).</summary>
    private void CheckLinkLayout()
    {
        var count = PlotCount;
        if (_linkSpecs.Length != count)
        {
            _linksDirty = true;
            return;
        }

        for (var k = 0; k < count && !_linksDirty; k++)
        {
            _linksDirty = !ReferenceEquals(_linkSpecs[k], SpecOf(k)) || _linkPanels[k] != _states[k].Panels;
        }
    }

    /// <summary>
    /// Builds the slots, the closures, and the plots' sameness again when they are stale. Allocates then only: after a change of
    /// the groups, the spec, or the panels, never in the steady state (invariant 3).
    /// </summary>
    private void EnsureLinks()
    {
        if (!_linksDirty)
        {
            return;
        }

        _linksDirty = false;
        var count = PlotCount;
        _slotStart = new int[count + 1];
        _linkSpecs = new object?[count];
        _linkPanels = new int[count];
        for (var k = 0; k < count; k++)
        {
            (_linkSpecs[k], _linkPanels[k]) = (SpecOf(k), _states[k].Panels);
            _slotStart[k + 1] = _slotStart[k] + _states[k].Panels;
        }

        var slots = _slotStart[count];
        for (var kind = 0; kind < _roots.Length; kind++)
        {
            var roots = _roots[kind] = new int[slots];
            for (var s = 0; s < slots; s++)
            {
                roots[s] = s;
            }
        }

        foreach (var group in Links)
        {
            for (var kind = 0; kind < _roots.Length; kind++)
            {
                if ((group.Kinds & KindFlag(kind)) == 0)
                {
                    continue;
                }

                var first = -1;
                foreach (var member in group.Members)
                {
                    if (member.Plot >= count)
                    {
                        continue;
                    }

                    var (from, to) = member.Panel is { } panel
                        ? (panel < _states[member.Plot].Panels ? (panel, panel + 1) : (0, 0))
                        : (0, _states[member.Plot].Panels);
                    for (var p = from; p < to; p++)
                    {
                        var slot = _slotStart[member.Plot] + p;
                        if (first < 0)
                        {
                            first = slot;
                        }
                        else
                        {
                            Union(_roots[kind], first, slot);
                        }
                    }
                }
            }
        }

        // A facet's shared axis joins its panels for the view (row I1b): the closure of Decision 10.
        for (var k = 0; k < count; k++)
        {
            var (shareX, shareY) = SharedAxes(k);
            for (var p = 1; p < _states[k].Panels; p++)
            {
                if (shareX)
                {
                    Union(_roots[ViewXKind], _slotStart[k], _slotStart[k] + p);
                }

                if (shareY)
                {
                    Union(_roots[ViewYKind], _slotStart[k], _slotStart[k] + p);
                }
            }
        }

        _sameX = new bool[count * count];
        _sameY = new bool[count * count];
        for (var a = 0; a < count; a++)
        {
            for (var b = 0; b < count; b++)
            {
                var (one, other) = (SpecOf(a), SpecOf(b));
                var flat = one.Coord is not PolarCoordSpec && other.Coord is not PolarCoordSpec;
                _sameX[(a * count) + b] = flat && PlotPlan.SameSpace(one.ScaleX, other.ScaleX) && SameBands(PlanOf(a), PlanOf(b));
                _sameY[(a * count) + b] = flat && PlotPlan.SameSpace(one.ScaleY, other.ScaleY);
            }
        }

        _queue = new int[slots + count];
        (_queueHead, _queueCount) = (0, 0);
        _viewQueued = new bool[slots];
        _queueAxes = new ViewAxes[slots];
        _queueAnimate = new bool[slots];
        _selectionQueued = new bool[count];
        _movedX = new bool[slots];
        _movedY = new bool[slots];
        _linkNotices.Capacity = Math.Max(_linkNotices.Capacity, slots);
        var applied = new bool[count][];
        for (var k = 0; k < count; k++)
        {
            applied[k] = new bool[_states[k].Panels];
            if (k < _applied.Length && _applied[k].Length == applied[k].Length)
            {
                _applied[k].CopyTo(applied[k], 0);
            }
        }

        _applied = applied;

        static bool SameBands(PlotPlan a, PlotPlan b) =>
            (a.BandCategories(), b.BandCategories()) switch
            {
                (null, null) => true,
                ({ } one, { } other) => one.SameCategories(other),
                _ => false,
            };
    }

    private static LinkKinds KindFlag(int kind) =>
        kind switch
        {
            ViewXKind => LinkKinds.ViewX,
            ViewYKind => LinkKinds.ViewY,
            CursorKind => LinkKinds.Cursor,
            _ => LinkKinds.Selection,
        };

    private static int Find(int[] roots, int slot)
    {
        while (roots[slot] != slot)
        {
            roots[slot] = roots[roots[slot]];
            slot = roots[slot];
        }

        return slot;
    }

    private static void Union(int[] roots, int a, int b)
    {
        var (ra, rb) = (Find(roots, a), Find(roots, b));
        if (ra != rb)
        {
            roots[Math.Max(ra, rb)] = Math.Min(ra, rb);
        }
    }

    /// <summary>Returns whether a plot's facet shares its x and its y among its panels (row I1b).</summary>
    private (bool X, bool Y) SharedAxes(int plot)
    {
        if (PlanOf(plot).Facet is not { } facet)
        {
            return (false, false);
        }

        return (!facet.Arrangement.FreeXOf(SpecOf(plot).ScaleX), !facet.Arrangement.FreeY);
    }

    private int Slot(PanelId panel) => _slotStart[panel.Plot] + panel.Panel;

    private PanelId PanelAt(int slot)
    {
        var plot = 0;
        while (_slotStart[plot + 1] <= slot)
        {
            plot++;
        }

        return new PanelId(plot, slot - _slotStart[plot]);
    }

    private bool SameX(int a, int b) => _sameX[(a * (_slotStart.Length - 1)) + b];

    private bool SameY(int a, int b) => _sameY[(a * (_slotStart.Length - 1)) + b];

    /// <summary>Returns whether a slot is joined to another by a kind's groups (with the facet's axes for the view).</summary>
    private bool Joined(int kind, int a, int b) => Find(_roots[kind], a) == Find(_roots[kind], b);

    /// <summary>
    /// Takes a panel's view change to be copied (Issue #373 Decision 10): the axes it moved are added to the panel's place in
    /// the queue, or a place is taken. The copy reads the panel's view when its turn comes.
    /// </summary>
    private void QueueView(int plot, int panel, ViewAxes axes, bool animate)
    {
        if (Links.Count == 0 || axes == ViewAxes.None)
        {
            return;
        }

        EnsureLinks();
        if (plot >= _slotStart.Length - 1 || panel >= _linkPanels[plot])
        {
            return;
        }

        var slot = _slotStart[plot] + panel;
        _queueAxes[slot] |= axes;
        _queueAnimate[slot] = animate;
        if (!_viewQueued[slot])
        {
            _viewQueued[slot] = true;
            _queue[(_queueHead + _queueCount++) % _queue.Length] = slot;
        }
    }

    /// <summary>Takes a plot's new selection to be copied (Issue #373 Decision 10).</summary>
    private void QueueSelection(int plot)
    {
        if (Links.Count == 0)
        {
            return;
        }

        EnsureLinks();
        if (plot >= _selectionQueued.Length || _selectionQueued[plot])
        {
            return;
        }

        _selectionQueued[plot] = true;
        _queue[(_queueHead + _queueCount++) % _queue.Length] = -1 - plot;
    }

    /// <summary>
    /// Copies the changes in the queue, oldest first, until none is left (Issue #373 Decision 10): a handler that changes a
    /// view or a selection while one is copied adds its change to the queue, and it is copied after.
    /// </summary>
    private void Drain()
    {
        while (_queueCount > 0)
        {
            var item = _queue[_queueHead];
            _queueHead = (_queueHead + 1) % _queue.Length;
            _queueCount--;
            if (item >= 0)
            {
                var (axes, animate) = (_queueAxes[item], _queueAnimate[item]);
                (_viewQueued[item], _queueAxes[item]) = (false, ViewAxes.None);
                CopyView(item, axes, animate);
            }
            else
            {
                _selectionQueued[-1 - item] = false;
                CopySelection(-1 - item);
            }
        }

        _queueHead = 0;
    }

    /// <summary>
    /// Copies a panel's view to the panels its view groups join (design §6.4 "ビュー", Issue #373 Decisions 2, 3, 10, 11): the
    /// values of the axes it moved, as they are now, to each member in the order of the panels, through the member's
    /// constraints, into no history, told with <see cref="ViewChangeReason.Link"/>. A member of another kind of axis, a polar
    /// one, and a panel a facet moved with one already are passed over.
    /// </summary>
    private void CopyView(int origin, ViewAxes axes, bool animate)
    {
        var from = PanelAt(origin);
        if (from.Plot >= PlotCount || from.Panel >= _states[from.Plot].Panels || SpecOf(from.Plot).Coord is PolarCoordSpec)
        {
            return;
        }

        var view = Resolve(from.Plot, from.Panel, _states[from.Plot].Target[from.Panel]);
        var (moveX, moveY) = ((axes & ViewAxes.X) != 0, (axes & ViewAxes.Y) != 0);
        Array.Clear(_movedX);
        Array.Clear(_movedY);
        Moved(from, moveX, moveY);
        for (var slot = 0; slot < _movedX.Length; slot++)
        {
            var x = moveX && !_movedX[slot] && Joined(ViewXKind, origin, slot);
            var y = moveY && !_movedY[slot] && Joined(ViewYKind, origin, slot);
            if (!x && !y)
            {
                continue;
            }

            var to = PanelAt(slot);
            (x, y) = (x && SameX(from.Plot, to.Plot), y && SameY(from.Plot, to.Plot));
            if (!x && !y)
            {
                continue;
            }

            var spec = SpecOf(to.Plot);
            var now = Resolve(to.Plot, to.Panel, _states[to.Plot].Target[to.Panel]);
            var (nx, ny) = (x ? view.X : now.X, y ? view.Y : now.Y);
            var next = new PanelDomains(
                nx,
                ny,
                x && spec.ScaleX2 is not null ? Follow(now.X, nx, now.X2, Reversed(spec.ScaleX) == Reversed(spec.ScaleX2)) : now.X2,
                y && spec.ScaleY2 is not null ? Follow(now.Y, ny, now.Y2, Reversed(spec.ScaleY) == Reversed(spec.ScaleY2)) : now.Y2
            );
            var moves = (x ? ViewAxes.X | ViewAxes.X2 : ViewAxes.None) | (y ? ViewAxes.Y | ViewAxes.Y2 : ViewAxes.None);

            // The reason is a link's for the commit alone: a handler told after it changes views as code does.
            var reason = _reason;
            (_linkCommit, _reason) = (true, ViewChangeReason.Link);
            try
            {
                Commit(to, next, moves, animate, HistoryMode.None);
            }
            finally
            {
                (_linkCommit, _reason) = (false, reason);
            }

            Moved(to, x, y);
            TellLinked();
        }
    }

    /// <summary>Marks a panel moved on its axes, and the panels its facet moves with it (row I1b).</summary>
    private void Moved(PanelId panel, bool x, bool y)
    {
        var (shareX, shareY) = SharedAxes(panel.Plot);
        for (var p = 0; p < _states[panel.Plot].Panels; p++)
        {
            var slot = _slotStart[panel.Plot] + p;
            _movedX[slot] |= x && (p == panel.Panel || shareX);
            _movedY[slot] |= y && (p == panel.Panel || shareY);
        }
    }

    /// <summary>Tells the views a member's commit changed (Issue #373 Decision 10), in the order of its panels.</summary>
    private void TellLinked()
    {
        for (var i = 0; i < _linkNotices.Count; i++)
        {
            Notify(_linkNotices[i].Plot, _linkNotices[i].Panel, ViewChangeReason.Link);
        }

        _linkNotices.Clear();
    }

    /// <summary>
    /// Copies a plot's selection to the panels its selection groups join (design §6.4 "選択", Issue #373 Decisions 5 and 12):
    /// to another plot as its selection on its first member panel, to more of a plot's panels as panels it also holds. The
    /// points of clicks are not copied; a member of another kind of axis, or a polar one, is passed over.
    /// </summary>
    private void CopySelection(int plot)
    {
        if (plot >= _selections.Length || _selections[plot] is not { } selection || selection.Kind == SelectionKind.Points)
        {
            return;
        }

        var origin = selection.Panel;
        if (origin.Panel >= _linkPanels[plot] || SpecOf(plot).Coord is PolarCoordSpec)
        {
            return;
        }

        var start = Slot(origin);
        var last = -1;
        for (var slot = 0; slot < _slotStart[^1]; slot++)
        {
            if (slot == start || !Joined(SelectionLinkKind, start, slot))
            {
                continue;
            }

            var to = PanelAt(slot);
            var fits = SameX(plot, to.Plot) && (selection.Kind == SelectionKind.XRange || SameY(plot, to.Plot));
            if (!fits)
            {
                continue;
            }

            if (to.Plot == plot || to.Plot == last)
            {
                _applied[to.Plot][to.Panel] = true;
                _selectionVersions[to.Plot]++;
                RequestFrame();
                continue;
            }

            last = to.Plot;
            var copy = selection.WithPanel(to);
            if (selection.Kind == SelectionKind.XRange)
            {
                copy = copy.WithY(CurrentView(to) is PanelView2D view ? view.Y : default);
            }

            SetSelection(to.Plot, copy, linked: true);
        }
    }

    /// <summary>
    /// Records the cursor on the panels the hover's cursor groups join (design §6.4 "カーソル", Issue #373 Decisions 4 and 16):
    /// the crosshair at the pointer's place of the scales, and a marker at each hit of <see cref="HitMode.X"/> there. An axis
    /// of another kind draws no line across it, and an x of another kind no markers; a polar panel draws nothing.
    /// </summary>
    private void DrawLinkedCursor(RenderList list, HoverTool tool, PanelId origin)
    {
        if (Links.Count == 0 || IsPolar(origin))
        {
            return;
        }

        EnsureLinks();
        if (origin.Plot >= _linkPanels.Length || origin.Panel >= _linkPanels[origin.Plot])
        {
            return;
        }

        var (vx, vy) = PixelToView(origin, _hoverPixel);
        var start = Slot(origin);
        var line = MathF.Max(1, MathF.Round(Scale));
        for (var slot = 0; slot < _slotStart[^1]; slot++)
        {
            if (slot == start || !Joined(CursorKind, start, slot))
            {
                continue;
            }

            var to = PanelAt(slot);
            var (x, y) = (SameX(origin.Plot, to.Plot), SameY(origin.Plot, to.Plot));
            if (!x && !y)
            {
                continue;
            }

            // An axis that does not take the value stands at its middle: only the other's coordinate is read.
            var shown = Resolve(to.Plot, to.Panel, _states[to.Plot].Shown[to.Panel]);
            var mx = (shown.X.Lower + shown.X.Upper) / 2;
            var my = (shown.Y.Lower + shown.Y.Upper) / 2;
            var at = ViewToPixel(to, x ? vx : new ViewRange(shown.X.Origin, mx, mx), y ? vy : new ViewRange(shown.Y.Origin, my, my));
            var (guide, theme, plan) = (PanelOf(to), SpecOf(to.Plot).Theme, PlanOf(to.Plot));
            var (left, top, width, height) = (guide.Left, guide.Top, guide.Width, guide.Height);
            var flipped = plan.Guides.Flipped;
            var (across, up) = flipped ? (y, x) : (x, y);
            var faint = theme.Foreground with { A = (byte)Math.Round(theme.Foreground.A * 0.5) };
            var (px, py) = (MathF.Floor(at.X), MathF.Floor(at.Y));
            if (across && px >= left && px < left + width && tool.Crosshair is HoverCrosshair.Vertical or HoverCrosshair.Both)
            {
                list.DrawRect(px, top, line, height, faint);
            }

            if (up && py >= top && py < top + height && tool.Crosshair is HoverCrosshair.Horizontal or HoverCrosshair.Both)
            {
                list.DrawRect(left, py, width, line, faint);
            }

            var along = flipped ? py >= top && py < top + height : px >= left && px < left + width;
            if (!tool.Markers || !x || !along)
            {
                continue;
            }

            var count = plan.HitTest(to.Plot, to.Panel, at, 0, HitMode.X, _linkHits);
            if (count > _linkHits.Length)
            {
                // More series than ever before: the buffer grows once, and the hits are asked again.
                _linkHits = new SeriesHit[count];
                count = plan.HitTest(to.Plot, to.Panel, at, 0, HitMode.X, _linkHits);
            }

            for (var i = 0; i < count; i++)
            {
                if (plan.HitPixel(_linkHits[i], out var hit) && hit.X >= left && hit.X <= left + width && hit.Y >= top && hit.Y <= top + height)
                {
                    Marker(list, hit, plan.HitColor(_linkHits[i]), theme);
                }
            }
        }
    }

    /// <summary>Returns whether a plot's selection also stands on one of its panels by a link (row I3b).</summary>
    private bool Applied(int plot, int panel) => plot < _applied.Length && panel < _applied[plot].Length && _applied[plot][panel];

    /// <summary>Forgets the panels a plot's selection stood on by a link: its selection changed.</summary>
    private void ClearApplied(int plot)
    {
        if (plot < _applied.Length)
        {
            Array.Clear(_applied[plot]);
        }
    }
}
