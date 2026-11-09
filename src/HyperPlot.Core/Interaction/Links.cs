using System.Collections;
using System.Collections.ObjectModel;

namespace HyperPlot.Interaction;

/// <summary>What a <see cref="LinkGroup"/> shares among its panels (design §6.4, row I3b): each flag one kind.</summary>
[Flags]
public enum LinkKinds
{
    /// <summary>Nothing; a group does not take it.</summary>
    None = 0,

    /// <summary>The view's x range.</summary>
    ViewX = 1,

    /// <summary>The view's primary y range; a secondary y scale moves with it by the same shares (row I1b).</summary>
    ViewY = 2,

    /// <summary>The view's x and y ranges.</summary>
    View = ViewX | ViewY,

    /// <summary>The cursor: the hover's crosshair and markers at the same place of the scales (row I2b).</summary>
    Cursor = 4,

    /// <summary>The selection's predicate (row I3a): a rectangle, an x range, or a lasso; not the points of clicks.</summary>
    Selection = 8,

    /// <summary>Every kind.</summary>
    All = View | Cursor | Selection,
}

/// <summary>A member of a <see cref="LinkGroup"/> (design §6.4, row I3b): a panel of a plot, or all the panels of a plot.</summary>
/// <param name="Plot">The plot: 0 for a plot alone, its number in a figure.</param>
/// <param name="Panel">The panel; null for every panel the plot draws, whatever their number now.</param>
public readonly record struct LinkMember(int Plot, int? Panel);

/// <summary>
/// A group of panels that share views, the cursor, or the selection (design §6.4, requirement I-4, row I3b): the panels of a
/// figure's plots and of a facet's, in one <see cref="PlotEngine"/>. Add it to <see cref="PlotEngine.Links"/>.
/// </summary>
/// <remarks>
/// <para>
/// A change of a member's view, cursor, or selection is copied to the other members, as values of the scales: a member
/// whose axis is of another kind (another scale's space, a band of other categories), and a polar panel, is passed over. A
/// panel may be in several groups; what one kind shares goes to every panel the groups of that kind join.
/// </para>
/// <para>
/// A member the spec does not draw now is passed over, and takes effect again from the next change after it is drawn. Once in
/// an engine, a group is called from the engine's thread only, and a change takes effect from the next input.
/// </para>
/// </remarks>
public sealed class LinkGroup
{
    private readonly List<LinkMember> _members = [];

    /// <summary>Initializes a new instance of the <see cref="LinkGroup"/> class.</summary>
    /// <param name="kinds">What the group shares: one or more named flags.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kinds"/> is <see cref="LinkKinds.None"/> or has a flag without a name.</exception>
    public LinkGroup(LinkKinds kinds)
    {
        if (kinds == LinkKinds.None || (kinds & ~LinkKinds.All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(kinds), kinds, "A group shares one or more named LinkKinds.");
        }

        Kinds = kinds;
        Members = new ReadOnlyCollection<LinkMember>(_members);
    }

    /// <summary>Gets what the group shares.</summary>
    public LinkKinds Kinds { get; }

    /// <summary>Gets the members, in the order they were added.</summary>
    public IReadOnlyList<LinkMember> Members { get; }

    /// <summary>Gets or sets the engine the group is in, null when in none.</summary>
    internal PlotEngine? Engine { get; set; }

    /// <summary>Adds a panel.</summary>
    /// <param name="panel">The panel.</param>
    /// <returns>Whether it was added: false when it is a member already.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The plot or the panel is negative.</exception>
    /// <exception cref="InvalidOperationException">The group is in an engine, and the call comes from another thread than the engine's.</exception>
    public bool Add(PanelId panel)
    {
        Check(panel.Plot, nameof(panel));
        Check(panel.Panel, nameof(panel));
        return Insert(new LinkMember(panel.Plot, panel.Panel));
    }

    /// <summary>Adds every panel of a plot: those it draws at each change, so that a facet's panels are followed.</summary>
    /// <param name="plot">The plot: 0 for a plot alone, its number in a figure.</param>
    /// <returns>Whether it was added: false when it is a member already.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="plot"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">The group is in an engine, and the call comes from another thread than the engine's.</exception>
    public bool AddPlot(int plot)
    {
        Check(plot, nameof(plot));
        return Insert(new LinkMember(plot, null));
    }

    /// <summary>Removes a panel added with <see cref="Add"/>.</summary>
    /// <param name="panel">The panel.</param>
    /// <returns>Whether it was removed: false when it was not a member.</returns>
    /// <exception cref="InvalidOperationException">The group is in an engine, and the call comes from another thread than the engine's.</exception>
    public bool Remove(PanelId panel) => Delete(new LinkMember(panel.Plot, panel.Panel));

    /// <summary>Removes a plot added with <see cref="AddPlot"/>.</summary>
    /// <param name="plot">The plot.</param>
    /// <returns>Whether it was removed: false when it was not a member.</returns>
    /// <exception cref="InvalidOperationException">The group is in an engine, and the call comes from another thread than the engine's.</exception>
    public bool RemovePlot(int plot) => Delete(new LinkMember(plot, null));

    private static void Check(int number, string name)
    {
        if (number < 0)
        {
            throw new ArgumentOutOfRangeException(name, number, "A plot's and a panel's number is 0 or more.");
        }
    }

    private bool Insert(LinkMember member)
    {
        Engine?.CheckLinkThread();
        if (_members.Contains(member))
        {
            return false;
        }

        _members.Add(member);
        Engine?.LinksChanged();
        return true;
    }

    private bool Delete(LinkMember member)
    {
        Engine?.CheckLinkThread();
        if (!_members.Remove(member))
        {
            return false;
        }

        Engine?.LinksChanged();
        return true;
    }
}

/// <summary>The link groups of a <see cref="PlotEngine"/> (design §6.4, row I3b), in the order they were added.</summary>
public sealed class LinkGroups : IReadOnlyList<LinkGroup>
{
    private readonly PlotEngine _engine;
    private readonly List<LinkGroup> _groups = [];

    internal LinkGroups(PlotEngine engine) => _engine = engine;

    /// <summary>Gets the number of groups.</summary>
    public int Count => _groups.Count;

    /// <summary>Gets a group.</summary>
    /// <param name="index">The group's place.</param>
    /// <returns>The group.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the groups.</exception>
    public LinkGroup this[int index] => _groups[index];

    /// <summary>Adds a group; one in this engine already is not added again.</summary>
    /// <param name="group">The group.</param>
    /// <exception cref="ArgumentNullException"><paramref name="group"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The group is in another engine, or the call comes from another thread than the engine's.</exception>
    public void Add(LinkGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        _engine.CheckLinkThread();
        if (ReferenceEquals(group.Engine, _engine))
        {
            return;
        }

        if (group.Engine is not null)
        {
            throw new InvalidOperationException("The group is in another engine: a group links the panels of one engine (design §6.4).");
        }

        group.Engine = _engine;
        _groups.Add(group);
        _engine.LinksChanged();
    }

    /// <summary>Removes a group.</summary>
    /// <param name="group">The group.</param>
    /// <returns>Whether it was removed: false when it was not in this engine.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="group"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The call comes from another thread than the engine's.</exception>
    public bool Remove(LinkGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        _engine.CheckLinkThread();
        if (!_groups.Remove(group))
        {
            return false;
        }

        group.Engine = null;
        _engine.LinksChanged();
        return true;
    }

    /// <summary>Removes every group.</summary>
    /// <exception cref="InvalidOperationException">The call comes from another thread than the engine's.</exception>
    public void Clear()
    {
        _engine.CheckLinkThread();
        foreach (var group in _groups)
        {
            group.Engine = null;
        }

        _groups.Clear();
        _engine.LinksChanged();
    }

    /// <inheritdoc/>
    public IEnumerator<LinkGroup> GetEnumerator() => _groups.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
