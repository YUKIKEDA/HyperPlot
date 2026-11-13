using System.Numerics;
using HyperPlot.Interaction;

namespace HyperPlot.Tests;

/// <summary>
/// The links (design §6.4 "リンク", §10, row I3b, Issue #373): the groups, and the views, the cursor, and the selection their
/// panels share.
/// </summary>
public sealed class LinkTests
{
    private static readonly PanelId First = new(0, 0);
    private static readonly PanelId Second = new(1, 0);
    private static readonly PanelId Third = new(2, 0);

    /// <summary>A group takes named kinds and members of numbers 0 or more, and belongs to one engine, called from its thread.</summary>
    /// <remarks>
    /// Preconditions:
    /// - An engine over the fixture's line; a second engine.
    ///
    /// Steps:
    /// - Make groups of None and of an unnamed flag; add a negative panel and plot. Add a panel twice and remove it twice; add
    ///   and remove a plot. Add the group to the engine twice, then to the second engine; call it from another thread; remove
    ///   it, then clear the groups.
    ///
    /// Expected:
    /// - ArgumentOutOfRangeException for each bad kind and number. Add true then false, Remove true then false, the members in
    ///   order. The second add to the same engine does nothing; to another engine InvalidOperationException; from another
    ///   thread InvalidOperationException. Remove true, then false; Clear leaves none, and the group may join the other.
    /// </remarks>
    [Fact]
    public void Groups_CheckTheirArgumentsAndTheirEngine()
    {
        using var f = new EngineFixture();
        using var other = new EngineFixture();
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new LinkGroup(LinkKinds.None));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new LinkGroup((LinkKinds)16));
        var group = new LinkGroup(LinkKinds.View);
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => group.Add(new PanelId(-1, 0)));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => group.Add(new PanelId(0, -1)));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => group.AddPlot(-1));

        Assert.True(group.Add(First));
        Assert.False(group.Add(First));
        Assert.True(group.AddPlot(1));
        Assert.Equal([new LinkMember(0, 0), new LinkMember(1, null)], group.Members);
        Assert.True(group.Remove(First));
        Assert.False(group.Remove(First));
        Assert.True(group.RemovePlot(1));
        Assert.False(group.RemovePlot(1));

        f.Engine.Links.Add(group);
        f.Engine.Links.Add(group);
        Assert.Single(f.Engine.Links);
        _ = Assert.Throws<InvalidOperationException>(() => other.Engine.Links.Add(group));
        var thrown = OnAnotherThread(() => group.Add(First));
        _ = Assert.IsType<InvalidOperationException>(thrown);

        Assert.True(f.Engine.Links.Remove(group));
        Assert.False(f.Engine.Links.Remove(group));
        f.Engine.Links.Add(group);
        f.Engine.Links.Clear();
        Assert.Empty(f.Engine.Links);
        other.Engine.Links.Add(group);
        Assert.Same(group, other.Engine.Links[0]);
    }

    /// <summary>
    /// A view group copies the axes its kind names, as the same origin and differences, and a secondary scale moves with its
    /// primary by the same shares (Issue #373 Decisions 2 and 8).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A figure of three plots of the fixture's line, the third with a secondary y over [0, 100]; animations off.
    ///
    /// Steps:
    /// - Link the three with ViewX; zoom plot 0 by a wheel. Then with ViewY; then with View; each time from the full views.
    ///
    /// Expected:
    /// - ViewX: the others' x the same ViewRange as plot 0's (origin and differences), their y as it was. ViewY: the y the
    ///   same, the x as it was. View: both. The third plot's secondary y keeps its shares of the primary y.
    /// </remarks>
    [Fact]
    public void Views_CopyTheAxesTheirKindNames()
    {
        foreach (var kinds in (LinkKinds[])[LinkKinds.ViewX, LinkKinds.ViewY, LinkKinds.View])
        {
            using var f = new EngineFixture();
            var (x, y) = (f.Table.Col<double>("x"), f.Table.Col<float>("y"));
            var secondary = Plot.Create(f.Table)
                .Line(x, y)
                .Line(x, y, l => l.SecondaryY())
                .ScaleY(Scale.Linear().WithDomain(-20, 20))
                .ScaleY2(Scale.Linear().WithDomain(0, 100))
                .Build();
            Figure(f, f.Spec, f.Spec, secondary);
            var full = View(f, Second);
            var full2 = SecondaryY(f, Third);
            Link(f, kinds, First, Second, Third);

            f.Wheel(Middle(f, First), 2);
            f.Engine.RenderFrame();
            var origin = View(f, First);
            foreach (var member in (PanelId[])[Second, Third])
            {
                var view = View(f, member);
                Assert.Equal((kinds & LinkKinds.ViewX) != 0 ? origin.X : full.X, view.X);
                Assert.Equal((kinds & LinkKinds.ViewY) != 0 ? origin.Y : full.Y, view.Y);
            }

            var y2 = SecondaryY(f, Third);
            var shares = ((y2.LowValue - full2.LowValue) / Width(full2), (y2.HighValue - full2.LowValue) / Width(full2));
            var primary = (
                (View(f, Third).Y.LowValue - full.Y.LowValue) / Width(full.Y),
                (View(f, Third).Y.HighValue - full.Y.LowValue) / Width(full.Y)
            );
            Assert.Equal(primary.Item1, shares.Item1, 9);
            Assert.Equal(primary.Item2, shares.Item2, 9);
        }
    }

    /// <summary>
    /// A member of another kind of axis, a polar one, and a band of other categories take no view; a band of the same
    /// categories in the same order does (Issue #373 Decisions 2 and 11).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Figures of the fixture's line beside: a line over a log x; a polar line. Figures of a band bar over categories a to e
    ///   beside the same categories, and beside them in another order; animations off.
    ///
    /// Steps:
    /// - Link each pair with View; zoom plot 0 by a wheel.
    ///
    /// Expected:
    /// - The log x: its x as it was, its y plot 0's. The polar plot: as it was. The band of the same categories: plot 0's x;
    ///   the band of another order: its x as it was.
    /// </remarks>
    [Fact]
    public void Views_PassOverOtherKindsPolarAndOtherBands()
    {
        using var f = new EngineFixture();
        var (x, y) = (f.Table.Col<double>("x"), f.Table.Col<float>("y"));
        var log = Plot.Create(f.Table).Line(x, y).ScaleX(Scale.Log().WithDomain(1, 1000)).ScaleY(Scale.Linear().WithDomain(-20, 20)).Build();
        Figure(f, f.Spec, log);
        Link(f, LinkKinds.View, First, Second);
        var full = View(f, Second);
        f.Wheel(Middle(f, First), 2);
        f.Engine.RenderFrame();
        Assert.Equal(full.X, View(f, Second).X);
        Assert.Equal(View(f, First).Y, View(f, Second).Y);

        var polar = f.Spec with { Coord = Coord.Polar() };
        Figure(f, f.Spec, polar);
        f.Engine.Links.Clear();
        Link(f, LinkKinds.View, First, Second);
        full = View(f, Second);
        f.Wheel(Middle(f, First), 2);
        f.Engine.RenderFrame();
        Assert.Equal(full, View(f, Second));

        string[] names = ["a", "b", "c", "d", "e"];
        using var values = Column.Wrap(new double[] { 1, 2, 3, 4, 5 });
        var table = DataTable.Create().Add("c", Column.Categorical(names, names)).Add("v", values).Build();
        var other = DataTable.Create().Add("c", Column.Categorical(names, [.. names.Reverse()])).Add("v", values).Build();
        PlotSpec Bars(DataTable t) => Plot.Create(t).Bar(t.Cat("c"), t.Col<double>("v")).ScaleX(Scale.Band()).Build();
        foreach (var (beside, copied) in ((PlotSpec, bool)[])[(Bars(table), true), (Bars(other), false)])
        {
            Figure(f, Bars(table), beside);
            f.Engine.Links.Clear();
            Link(f, LinkKinds.ViewX, First, Second);
            full = View(f, Second);
            f.Engine.Views.SetView(First, new PanelView2D(ViewRange.FromValues(1.0, 3.0), View(f, First).Y));
            f.Engine.RenderFrame();
            Assert.Equal(copied ? View(f, First).X : full.X, View(f, Second).X);
        }
    }

    /// <summary>
    /// A member's constraint acts on its copy and is not given back, a member writes no history, going back is copied, and an
    /// animated change goes to the same end on each member (Issue #373 Decisions 2, 3, 14).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A figure of two plots of the fixture's line, linked with View; a constraint that sets plot 1's y to [−5, 5].
    ///
    /// Steps:
    /// - Zoom plot 0 by a wheel (animations off); read the histories. Go back on plot 0. Turn animations on, zoom plot 0 by a
    ///   key, draw a frame mid-way and one past the end.
    ///
    /// Expected:
    /// - Plot 1: plot 0's x, y [−5, 5]; plot 0's y as its own wheel made it. Plot 1's history one entry, plot 0's two. Back:
    ///   both x at the full view again. The animation: mid-way plot 1 is not at its end; after it, its x is plot 0's.
    /// </remarks>
    [Fact]
    public void Views_KeepTheirConstraintsAndHistoryAndAnimateTogether()
    {
        using var f = new EngineFixture();
        Figure(f, f.Spec, f.Spec);
        Link(f, LinkKinds.View, First, Second);
        f.Engine.Constraints.Add(new SetYOf(1, -5, 5));
        var full = View(f, First);

        f.Wheel(Middle(f, First), 2);
        f.Engine.RenderFrame();
        var (origin, member) = (View(f, First), View(f, Second));
        Assert.Equal(origin.X, member.X);
        Assert.Equal((-5.0, 5.0), (member.Y.LowValue, member.Y.HighValue));
        Assert.True(Width(origin.Y) < Width(full.Y) && origin.Y.LowValue != -5);
        Assert.Equal((2, 1), (f.Engine.History.Count, HistoryOf(f, Second)));

        Assert.True(f.Engine.Views.Back());
        f.Settle();
        Assert.Equal(full.X, View(f, First).X);
        Assert.Equal(full.X, View(f, Second).X);

        f.Engine.Options.ZoomAnimation = TimeSpan.FromMilliseconds(150);
        Assert.True(f.Key(PlotKey.Plus));
        f.Frame(TimeSpan.FromMilliseconds(50));
        Assert.NotEqual(Target(f, Second).X, View(f, Second).X);
        f.Settle();
        Assert.Equal(View(f, First).X, View(f, Second).X);
    }

    /// <summary>
    /// The origin is told first and the members after it, in the order of the panels, once each with the reason Link; a facet's
    /// shared axis and the groups make one closure; groups that share a panel join; a handler's change is copied after
    /// (Issue #373 Decision 10).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A figure of a facet of 2 panels sharing x (plot 0) and two plots of the fixture's line; animations off. Group A links
    ///   panel (0, 1) and plot 1 by ViewX, group B plot 1 and plot 2.
    ///
    /// Steps:
    /// - Set plot 2's x by code. Then add a handler that, on plot 1's Link notice, sets plot 0's panel 0 x once; set plot 2's x
    ///   again.
    ///
    /// Expected:
    /// - The notices: (2, 0) Code, then (0, 0), (0, 1), (1, 0) Link, once each; all four x plot 2's. With the handler: after
    ///   the first copy's notices, the handler's (0, 0) and (0, 1) Code, then (1, 0) and (2, 0) Link with the handler's x.
    /// </remarks>
    [Fact]
    public void Notices_GoInOrderOnceAndAfterAHandlersChange()
    {
        using var facet = new FacetPlot(FacetScales.Fixed);
        var f = facet.Fixture;
        Figure(f, facet.Spec, f.Spec, f.Spec);
        var a = new LinkGroup(LinkKinds.ViewX);
        _ = a.Add(new PanelId(0, 1));
        _ = a.AddPlot(1);
        var b = new LinkGroup(LinkKinds.ViewX);
        _ = b.AddPlot(1);
        _ = b.AddPlot(2);
        f.Engine.Links.Add(a);
        f.Engine.Links.Add(b);
        var y = View(f, Third).Y;
        f.Changes.Clear();

        f.Engine.Views.SetView(Third, new PanelView2D(ViewRange.FromValues(100.0, 200.0), y));
        Assert.Equal(
            [
                (Third, ViewChangeReason.Code),
                (First, ViewChangeReason.Link),
                (new PanelId(0, 1), ViewChangeReason.Link),
                (Second, ViewChangeReason.Link),
            ],
            f.Changes.Select(c => (c.Panel, c.Reason))
        );
        foreach (var panel in (PanelId[])[First, new PanelId(0, 1), Second, Third])
        {
            Assert.Equal((100.0, 200.0), (View(f, panel).X.LowValue, View(f, panel).X.HighValue));
        }

        var once = false;
        f.Engine.ViewChanged += (in ViewChange change) =>
        {
            if (!once && change.Panel == Second && change.Reason == ViewChangeReason.Link)
            {
                once = true;
                f.Engine.Views.SetView(First, new PanelView2D(ViewRange.FromValues(300.0, 400.0), View(f, First).Y));
            }
        };
        f.Changes.Clear();
        f.Engine.Views.SetView(Third, new PanelView2D(ViewRange.FromValues(500.0, 600.0), y));
        Assert.Equal(
            [
                (Third, ViewChangeReason.Code),
                (First, ViewChangeReason.Link),
                (new PanelId(0, 1), ViewChangeReason.Link),
                (Second, ViewChangeReason.Link),
                (First, ViewChangeReason.Code),
                (new PanelId(0, 1), ViewChangeReason.Code),
                (Second, ViewChangeReason.Link),
                (Third, ViewChangeReason.Link),
            ],
            f.Changes.Select(c => (c.Panel, c.Reason))
        );
        Assert.Equal(300.0, View(f, Third).X.LowValue);
    }

    /// <summary>
    /// A member the spec no longer draws is passed over, and takes effect again from the next change after it is drawn, not
    /// at once (Issue #373 Decisions 6 and 15).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A figure of the fixture's line and a facet of 2 panels with free scales; panel (1, 1) linked to plot 0 by ViewX;
    ///   animations off.
    ///
    /// Steps:
    /// - Set a figure whose second plot is the fixture's line (one panel); zoom plot 0. Set the facet back; draw. Zoom plot 0
    ///   again.
    ///
    /// Expected:
    /// - No throw, plot 1 at its full view. Drawn again: panel (1, 1) at its full view. The next zoom: its x plot 0's.
    /// </remarks>
    [Fact]
    public void Members_GoneAndBackTakeEffectFromTheNextChange()
    {
        using var facet = new FacetPlot(FacetScales.Free);
        var f = facet.Fixture;
        Figure(f, f.Spec, facet.Spec);
        var member = new PanelId(1, 1);
        Link(f, LinkKinds.ViewX, First, member);

        Figure(f, f.Spec, f.Spec);
        var full = View(f, Second);
        f.Wheel(Middle(f, First), 2);
        f.Engine.RenderFrame();
        Assert.Equal(full, View(f, Second));

        Figure(f, f.Spec, facet.Spec);
        var back = View(f, member);
        Assert.NotEqual(View(f, First).X, back.X);
        f.Wheel(Middle(f, First), 1);
        f.Engine.RenderFrame();
        Assert.Equal(View(f, First).X, View(f, member).X);
    }

    /// <summary>
    /// A cursor group draws the crosshair and the markers of <see cref="HitMode.X"/> on its members, the tooltip on the origin
    /// alone; <see cref="PlotEngine.Hover"/> holds the origin's hits; a member of another x draws neither (Issue #373 Decisions
    /// 4 and 16).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A figure of the fixture's line, the same line, and a line over a log x; linked with Cursor; the hover's mode XY.
    ///
    /// Steps:
    /// - Move to the origin's point of row 300; draw.
    ///
    /// Expected:
    /// - A crosshair rect at the origin's column and one at plot 1's column of x 300, of plot 1's top and height; none on plot
    ///   2. Two discs on the origin and two on plot 1, plot 1's at its point of row 300. One text. Hover: one hit, in plot 0.
    /// </remarks>
    [Fact]
    public void Cursor_DrawsOnTheMembersAndTellsTheOrigin()
    {
        using var f = new EngineFixture();
        var (x, y) = (f.Table.Col<double>("x"), f.Table.Col<float>("y"));
        var log = Plot.Create(f.Table).Line(x, y).ScaleX(Scale.Log().WithDomain(1, 1000)).ScaleY(Scale.Linear().WithDomain(-20, 20)).Build();
        Figure(f, f.Spec, f.Spec, log);
        Link(f, LinkKinds.Cursor, First, Second, Third);
        var hover = (HoverTool)f.Engine.Bindings.First(b => b.Tool is HoverTool).Tool!;
        hover.Mode = HitMode.XY;

        var value = MathF.Sin(300 * 0.02f) * 10;
        var at = f.Engine.ViewToPixel(First, ViewRange.FromValues(300.0, 300.0), ViewRange.FromValues(value, value));
        _ = f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, at));
        f.Engine.RenderFrame();

        var list = f.Backend.LastList!;
        var member = f.Engine.ViewToPixel(Second, ViewRange.FromValues(300.0, 300.0), ViewRange.FromValues(value, value));
        var (_, top, _, height) = PanelRectangle(f, Second);
        var rects = list.Rects.ToArray();
        Assert.Contains(rects, r => r.X == MathF.Floor(at.X) && r.Width == 1);
        Assert.Contains(rects, r => r.X == MathF.Floor(member.X) && r.Y == top && r.Height == height && r.Width == 1);
        var (left3, _, width3, _) = PanelRectangle(f, Third);
        Assert.DoesNotContain(rects, r => r.Width == 1 && r.X >= left3 && r.X < left3 + width3);

        var discs = list.SectorCalls.ToArray();
        Assert.Equal(4, discs.Length);
        Assert.Equal(member.X, discs[3].Center.X, 2);
        Assert.Equal(member.Y, discs[3].Center.Y, 2);
        _ = Assert.Single(list.Texts.ToArray());
        var hit = Assert.Single(f.Engine.Hover.ToArray());
        Assert.Equal(0, hit.Plot);
    }

    /// <summary>
    /// A member draws the line along the axis whose kind it shares: the vertical line where its x is of the origin's kind,
    /// the horizontal one where its y is (Issue #373 Decisions 4 and 16).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A figure of the fixture's line, the line over a log x, and the line over a symlog y; linked with Cursor; the hover's
    ///   crosshair Both.
    ///
    /// Steps:
    /// - Move to the origin's point of row 300; draw.
    ///
    /// Expected:
    /// - Plot 1 (log x): a horizontal line across its panel, no vertical one. Plot 2 (symlog y): a vertical line down its
    ///   panel, no horizontal one.
    /// </remarks>
    [Fact]
    public void Cursor_DrawsTheLineOfTheAxisOfTheSameKind()
    {
        using var f = new EngineFixture();
        var (x, y) = (f.Table.Col<double>("x"), f.Table.Col<float>("y"));
        var logX = Plot.Create(f.Table).Line(x, y).ScaleX(Scale.Log().WithDomain(1, 1000)).ScaleY(Scale.Linear().WithDomain(-20, 20)).Build();
        var symlogY = Plot.Create(f.Table).Line(x, y).ScaleY(Scale.Symlog().WithDomain(-20, 20)).Build();
        Figure(f, f.Spec, logX, symlogY);
        Link(f, LinkKinds.Cursor, First, Second, Third);
        ((HoverTool)f.Engine.Bindings.First(b => b.Tool is HoverTool).Tool!).Crosshair = HoverCrosshair.Both;

        var value = MathF.Sin(300 * 0.02f) * 10;
        var at = f.Engine.ViewToPixel(First, ViewRange.FromValues(300.0, 300.0), ViewRange.FromValues(value, value));
        _ = f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, at));
        f.Engine.RenderFrame();
        var rects = f.Backend.LastList!.Rects.ToArray();
        foreach (var (panel, across, down) in ((PanelId, bool, bool)[])[(Second, true, false), (Third, false, true)])
        {
            var (left, top, width, height) = PanelRectangle(f, panel);
            Assert.Equal(across, rects.Any(r => r.X == left && r.Width == width && r.Height == 1 && r.Y >= top && r.Y < top + height));
            Assert.Equal(down, rects.Any(r => r.Y == top && r.Height == height && r.Width == 1 && r.X >= left && r.X < left + width));
        }
    }

    /// <summary>
    /// A change of a secondary scale alone copies nothing: a link copies the primary x and y (Issue #373 Decision 2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A figure of two plots of the fixture's line with a secondary x over [0, 2000], linked with View; a constraint that
    ///   sets plot 1's y to [−5, 5]; animations off.
    ///
    /// Steps:
    /// - Zoom plot 0's secondary x alone.
    ///
    /// Expected:
    /// - No notice for plot 1, and its y at its full view, untouched by its constraint.
    /// </remarks>
    [Fact]
    public void Views_ASecondaryChangeAloneIsNotCopied()
    {
        using var f = new EngineFixture();
        var (x, y) = (f.Table.Col<double>("x"), f.Table.Col<float>("y"));
        var spec = Plot.Create(f.Table)
            .Line(x, y)
            .Line(x, y, l => l.SecondaryX())
            .ScaleY(Scale.Linear().WithDomain(-20, 20))
            .ScaleX2(Scale.Linear().WithDomain(0, 2000))
            .Build();
        Figure(f, spec, spec);
        Link(f, LinkKinds.View, First, Second);
        var full = View(f, Second);
        f.Engine.Constraints.Add(new SetYOf(1, -5, 5));
        f.Changes.Clear();

        f.Engine.Views.ZoomAt(First, Middle(f, First), 2, ViewAxes.X2);
        f.Engine.RenderFrame();
        Assert.NotEmpty(f.Changes);
        Assert.DoesNotContain(f.Changes, c => c.Panel == Second);
        Assert.Equal(full, View(f, Second));
    }

    /// <summary>
    /// A selection group copies a rectangle and an x range to another plot, each told in the order of the plots, an x range
    /// taking the member's y; points are not copied; a member of another x is passed over; clearing clears them all (Issue
    /// #373 Decisions 5 and 12).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A figure of the fixture's line, the line with y over [−50, 50], and a line over a log x; linked with Selection.
    ///
    /// Steps:
    /// - Select by code a rectangle on plot 0; an x range; points; then clear. Select the rectangle again, and set a figure whose
    ///   plot 0 has y over [−30, 30].
    ///
    /// Expected:
    /// - The rectangle: plot 1's selection the same kind, X, and Y on panel (1, 0); plot 2 none; told 0 then 1. The x range:
    ///   plot 1's Y [−50, 50]. Points: plot 1 keeps the x range. Clear: no selection anywhere. The new mapping: plot 0's
    ///   selection dropped, plot 1's the same object.
    /// </remarks>
    [Fact]
    public void Selection_IsCopiedToAnotherPlot()
    {
        using var f = new EngineFixture();
        var (x, y) = (f.Table.Col<double>("x"), f.Table.Col<float>("y"));
        var wide = f.Spec with { ScaleY = Scale.Linear().WithDomain(-50, 50) };
        var log = Plot.Create(f.Table).Line(x, y).ScaleX(Scale.Log().WithDomain(1, 1000)).ScaleY(Scale.Linear().WithDomain(-20, 20)).Build();
        Figure(f, f.Spec, wide, log);
        Link(f, LinkKinds.Selection, First, Second, Third);
        var told = new List<int>();
        f.Engine.SelectionChanged += told.Add;

        var (rx, ry) = (ViewRange.FromValues(100.0, 200.0), ViewRange.FromValues(-5.0, 5.0));
        f.Engine.Select(Selection2D.Rectangle(First, rx, ry));
        var copy = Assert.IsType<Selection2D>(f.Engine.Selection(1));
        Assert.Equal((SelectionKind.Rectangle, Second, rx, ry), (copy.Kind, copy.Panel, copy.X, copy.Y));
        Assert.Null(f.Engine.Selection(2));
        Assert.Equal([0, 1], told);

        f.Engine.Select(Selection2D.XRange(First, rx));
        var range = Assert.IsType<Selection2D>(f.Engine.Selection(1));
        Assert.Equal((SelectionKind.XRange, -50.0, 50.0), (range.Kind, range.Y.LowValue, range.Y.HighValue));

        var hits = new SeriesHit[4];
        var at = f.Engine.ViewToPixel(First, ViewRange.FromValues(300.0, 300.0), ViewRange.FromValues(0.0, 0.0));
        var count = f.Engine.HitTest(at, 0, HitMode.X, hits);
        f.Engine.Select(Selection2D.OfPoints(First, hits.AsSpan(0, count)));
        Assert.Same(range, f.Engine.Selection(1));

        f.Engine.ClearSelection();
        Assert.All([0, 1, 2], k => Assert.Null(f.Engine.Selection(k)));

        // A selection dropped for a new mapping is not copied: the members keep theirs (Issue #377 Decision 2).
        f.Engine.Select(Selection2D.Rectangle(First, rx, ry));
        var kept = f.Engine.Selection(1);
        f.Engine.SetFigure(HyperPlot.Figure.HConcat(f.Spec with { ScaleY = Scale.Linear().WithDomain(-30, 30) }, wide, log));
        f.Engine.RenderFrame();
        Assert.Null(f.Engine.Selection(0));
        Assert.Same(kept, f.Engine.Selection(1));
    }

    /// <summary>
    /// A selection group over a facet's panels keeps one selection and stands it on the other members: each draws the range,
    /// and the rows are each panel's (Issue #373 Decision 5).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A facet of 2 panels with fixed scales (rows 0 to 499 and 500 to 999, x the row); every panel linked with Selection.
    ///
    /// Steps:
    /// - Select an x range of [450, 550] on panel 0; draw; read the rows of layer 0. Clear the groups; draw; read them again.
    ///   Select the same range again; read them.
    ///
    /// Expected:
    /// - The selection's panel (0, 0). The last list holds the range's rect of 15 % of the foreground on both panels. The rows
    ///   are those of x in [450, 550]: 450 to 499 of panel 0 and 500 to 550 of panel 1. After the groups go: the same 101.
    ///   The new selection: panel 0's 50 rows alone.
    /// </remarks>
    [Fact]
    public void Selection_StandsOnAFacetsLinkedPanels()
    {
        using var facet = new FacetPlot(FacetScales.Fixed);
        var f = facet.Fixture;
        var group = new LinkGroup(LinkKinds.Selection);
        _ = group.AddPlot(0);
        f.Engine.Links.Add(group);

        f.Engine.Select(Selection2D.XRange(First, ViewRange.FromValues(450.0, 550.0)));
        f.Engine.RenderFrame();
        Assert.Equal(First, f.Engine.Selection(0)!.Panel);
        var faint = Theme.Light.Foreground with { A = (byte)Math.Round(Theme.Light.Foreground.A * 0.15) };
        var rects = f.Backend.LastList!.Rects.ToArray().Where(r => r.Color.W == faint.A / 255f).ToArray();
        foreach (var panel in (PanelId[])[First, new PanelId(0, 1)])
        {
            var (left, top, width, height) = PanelRectangle(f, panel);
            Assert.Contains(rects, r => r.X >= left && r.X + r.Width <= left + width + 1 && r.Y == top && r.Height == height);
        }

        var rows = SelectedRows(f.Engine);
        Assert.Equal(101, rows.Count);
        Assert.True(rows.Contains(450) && rows.Contains(499) && rows.Contains(500) && rows.Contains(550));
        Assert.False(rows.Contains(449) || rows.Contains(551));

        // The group gone, the selection stands on panel 1 until it changes (Issue #377 Decision 3); a new one stands on its own.
        f.Engine.Links.Clear();
        f.Engine.RenderFrame();
        Assert.Equal(101, SelectedRows(f.Engine).Count);
        f.Engine.Select(Selection2D.XRange(First, ViewRange.FromValues(450.0, 550.0)));
        Assert.Equal(50, SelectedRows(f.Engine).Count);
    }

    /// <summary>
    /// A plot added whole stands for the panels it draws at each change: a facet's panels each, and the one panel of the plot
    /// that takes its place (Issue #373 Decision 9).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A figure of the fixture's line and a facet of 2 panels with free scales; plot 1 added whole to a ViewX group with
    ///   panel (0, 0); animations off.
    ///
    /// Steps:
    /// - Zoom plot 0. Set a figure whose plot 1 is the fixture's line; zoom plot 0 again.
    ///
    /// Expected:
    /// - Both facet panels' x plot 0's. After the new figure, panel (1, 0)'s x plot 0's.
    /// </remarks>
    [Fact]
    public void Members_APlotStandsForItsPanels()
    {
        using var facet = new FacetPlot(FacetScales.Free);
        var f = facet.Fixture;
        Figure(f, f.Spec, facet.Spec);
        var group = new LinkGroup(LinkKinds.ViewX);
        _ = group.Add(First);
        _ = group.AddPlot(1);
        f.Engine.Links.Add(group);

        f.Wheel(Middle(f, First), 2);
        f.Engine.RenderFrame();
        Assert.Equal(View(f, First).X, View(f, Second).X);
        Assert.Equal(View(f, First).X, View(f, new PanelId(1, 1)).X);

        Figure(f, f.Spec, f.Spec);
        f.Wheel(Middle(f, First), 1);
        f.Engine.RenderFrame();
        Assert.Equal(View(f, First).X, View(f, Second).X);
    }

    /// <summary>
    /// <see cref="ViewOperations.ResetAll"/> takes each plot to its own full view: a link copies none of them (Issue #377
    /// Decision 1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A figure of the fixture's line (y over [−20, 20]) and the line with y over [−50, 50], linked with View; animations off.
    ///
    /// Steps:
    /// - Zoom plot 0; reset all.
    ///
    /// Expected:
    /// - After the zoom plot 1's y is plot 0's. After the reset plot 0's y [−20, 20] and plot 1's [−50, 50].
    /// </remarks>
    [Fact]
    public void Views_ResetAllIsNotCopied()
    {
        using var f = new EngineFixture();
        Figure(f, f.Spec, f.Spec with { ScaleY = Scale.Linear().WithDomain(-50, 50) });
        Link(f, LinkKinds.View, First, Second);
        f.Wheel(Middle(f, First), 2);
        f.Engine.RenderFrame();
        Assert.Equal(View(f, First).Y, View(f, Second).Y);

        f.Engine.Views.ResetAll();
        f.Engine.RenderFrame();
        Assert.Equal((-20.0, 20.0), (View(f, First).Y.LowValue, View(f, First).Y.HighValue));
        Assert.Equal((-50.0, 50.0), (View(f, Second).Y.LowValue, View(f, Second).Y.HighValue));
    }

    /// <summary>
    /// A band of other categories, or of the same ones in another order, takes neither the cursor nor the selection; one of
    /// the same categories in the same order takes both (Issue #373 Decisions 11 and 16).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Figures of a band bar over categories a to e beside the same categories, and beside them in the reverse order; linked
    ///   with Cursor and Selection; animations off.
    ///
    /// Steps:
    /// - Move to the middle of plot 0's category c; draw. Select an x range over [1.5, 3.5] on plot 0.
    ///
    /// Expected:
    /// - The same order: a 1-pixel crosshair rect within plot 1, and plot 1's selection an x range of the same X. The reverse
    ///   order: no such rect, and no selection on plot 1.
    /// </remarks>
    [Fact]
    public void CursorAndSelection_PassOverABandOfOtherCategories()
    {
        using var f = new EngineFixture();
        string[] names = ["a", "b", "c", "d", "e"];
        using var values = Column.Wrap(new double[] { 1, 2, 3, 4, 5 });
        var table = DataTable.Create().Add("c", Column.Categorical(names, names)).Add("v", values).Build();
        var other = DataTable.Create().Add("c", Column.Categorical(names, [.. names.Reverse()])).Add("v", values).Build();
        PlotSpec Bars(DataTable t) => Plot.Create(t).Bar(t.Cat("c"), t.Col<double>("v")).ScaleX(Scale.Band()).Build();
        foreach (var (beside, same) in ((PlotSpec, bool)[])[(Bars(table), true), (Bars(other), false)])
        {
            Figure(f, Bars(table), beside);
            f.Engine.ClearSelection();
            f.Engine.Links.Clear();
            Link(f, LinkKinds.Cursor | LinkKinds.Selection, First, Second);
            var at = f.Engine.ViewToPixel(First, ViewRange.FromValues(2.5, 2.5), ViewRange.FromValues(1.0, 1.0));
            _ = f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, at));
            f.Engine.RenderFrame();
            var (left, _, width, _) = PanelRectangle(f, Second);
            var crossed = f.Backend.LastList!.Rects.ToArray().Any(r => r.Width == 1 && r.X >= left && r.X < left + width);
            Assert.Equal(same, crossed);

            var range = ViewRange.FromValues(1.5, 3.5);
            f.Engine.Select(Selection2D.XRange(First, range));
            Assert.Equal(same, f.Engine.Selection(1) is Selection2D { Kind: SelectionKind.XRange } copy && copy.X == range);
        }
    }

    /// <summary>Warm frames of the mouse over a cursor group and of zooms over a view group allocate nothing (§10, invariant 3).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A figure of two plots of the fixture's line, linked with All; the shaping cache full.
    ///
    /// Steps:
    /// - Rounds of 8 moves over plot 0 drawn after each; the pointer leaves; rounds of 20 wheel turns on plot 0 drawn after each
    ///   (a zoom under a hover allocates on main too, Issue #378): 4 of each to
    ///   warm the lists, the caches, and the plans of both plots (a wheel's round allocates in its first 3 as the member's ticks
    ///   and extractions first meet each view), a garbage collection, then 2 more of each counted.
    ///
    /// Expected:
    /// - The counted rounds: 0 bytes.
    /// </remarks>
    [Fact]
    public void LinkedFrames_AllocateNothing()
    {
        using var f = new EngineFixture();
        Figure(f, f.Spec, f.Spec);
        Link(f, LinkKinds.All, First, Second);
        for (var i = 0; i < Rendering.TextEngine.CacheCapacity; i++)
        {
            _ = f.Backend.Text.Measure(
                "filler " + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new Rendering.TextStyle(new Color(0, 0, 0, 255), 9)
            );
        }

        f.Changes.Capacity = 20_000;
        var (left, top, _, _) = PanelRectangle(f, First);
        long Moves()
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 8; i++)
            {
                _ = f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, new Vector2(left + 20 + (i * 23), top + 30 + (i * 7))));
                f.Engine.RenderFrame();
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        long Wheels()
        {
            var middle = Middle(f, First);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 20; i++)
            {
                f.Time.Advance(TimeSpan.FromMilliseconds(16));
                _ = f.Engine.OnWheel(new WheelEvent(middle, 0, i < 10 ? 1 : -1, ModifierKeys.None, f.Time.GetTimestamp()));
                f.Engine.RenderFrame();
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        // The wheel's rounds after the pointer leaves: a zoom under a hover allocates on main too (Issue #378).
        var moves = Warm(Moves);
        _ = f.Engine.OnPointer(f.Pointer(PointerEventKind.Leave, new Vector2(left, top)));
        var wheels = Warm(Wheels);
        Assert.Equal((0L, 0L), (moves, wheels));

        static long Warm(Func<long> round)
        {
            for (var i = 0; i < 4; i++)
            {
                _ = round();
            }

            // An allocation that follows a collection is counted in every run (.cursor/rules/bench.mdc).
            GC.Collect();
            return round() + round();
        }
    }

    private static void Figure(EngineFixture f, params PlotSpec[] plots)
    {
        f.Engine.Options.ZoomAnimation = TimeSpan.Zero;
        f.Engine.SetFigure(HyperPlot.Figure.HConcat(plots));
        f.Engine.RenderFrame();
    }

    private static void Link(EngineFixture f, LinkKinds kinds, params PanelId[] panels)
    {
        var group = new LinkGroup(kinds);
        foreach (var panel in panels)
        {
            _ = group.Add(panel);
        }

        f.Engine.Links.Add(group);
    }

    private static RowSelection SelectedRows(PlotEngine engine) => engine.SelectedRowsAsync(0, 0).AsTask().GetAwaiter().GetResult();

    private static Exception? OnAnotherThread(Action action)
    {
        Exception? thrown = null;
        var thread = new Thread(() => thrown = Record.Exception(action));
        thread.Start();
        thread.Join();
        return thrown;
    }

    private static double Width(in ViewRange range) => range.HighValue - range.LowValue;

    private static (ViewRange X, ViewRange Y) View(EngineFixture f, PanelId panel)
    {
        var view = (PanelView2D)f.Engine.CurrentView(panel);
        return (view.X, view.Y);
    }

    private static (ViewRange X, ViewRange Y) Target(EngineFixture f, PanelId panel)
    {
        var view = f.Engine.TargetView(panel);
        return (ViewRange.FromScale(view.X), ViewRange.FromScale(view.Y));
    }

    private static ViewRange SecondaryY(EngineFixture f, PanelId panel) => ViewRange.FromScale(f.Engine.TargetView(panel).Y2);

    private static int HistoryOf(EngineFixture f, PanelId panel) => f.Engine.HistoryOf(panel.Plot).Count;

    private static Vector2 Middle(EngineFixture f, PanelId panel)
    {
        var (left, top, width, height) = PanelRectangle(f, panel);
        return new Vector2(left + (width / 2f), top + (height / 2f));
    }

    private static (int Left, int Top, int Width, int Height) PanelRectangle(EngineFixture f, PanelId panel)
    {
        var plan = panel.Plot == 0 && f.Engine.Plan is { } single ? single : f.Engine.Figure!.Plans[panel.Plot];
        var guide = plan.Guides.PanelAt(panel.Panel);
        return (guide.Left, guide.Top, guide.Width, guide.Height);
    }

    /// <summary>A constraint that sets one plot's y.</summary>
    private sealed class SetYOf(int plot, double low, double high) : IViewConstraint
    {
        public bool Constrain(ViewProposal proposal)
        {
            if (proposal.Panel.Plot == plot)
            {
                _ = proposal.TryGet2D(out var x, out _);
                proposal.Set2D(x, ViewRange.FromValues(low, high));
            }

            return true;
        }
    }

    /// <summary>A line wrapped by 2 categories (rows 0 to 499 a, 500 to 999 b; x the row) with the facet's scales given.</summary>
    private sealed class FacetPlot : IDisposable
    {
        private readonly Column<float> _y;

        public FacetPlot(FacetScales scales)
        {
            var names = Enumerable.Range(0, 1000).Select(i => i < 500 ? "a" : "b").ToArray();
            _y = Column.Wrap(Enumerable.Range(0, 1000).Select(i => MathF.Sin(i * 0.02f) * 10).ToArray());
            var table = DataTable.Create().Add("x", Column.Uniform(0, 1, 1000)).Add("y", _y).Add("k", Column.Categorical(names, ["a", "b"])).Build();
            Fixture = new EngineFixture();
            Fixture.Engine.Options.ZoomAnimation = TimeSpan.Zero;
            Spec = Plot.Create(table)
                .Line(table.Col<double>("x"), table.Col<float>("y"))
                .ScaleY(Scale.Linear().WithDomain(-20, 20))
                .FacetWrap(table.Cat("k"), scales: scales)
                .Build();
            Fixture.Engine.SetPlot(Spec);
            Fixture.Engine.RenderFrame();
        }

        public EngineFixture Fixture { get; }

        public PlotSpec Spec { get; }

        public void Dispose()
        {
            Fixture.Dispose();
            _y.Dispose();
        }
    }
}
