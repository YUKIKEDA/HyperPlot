using System.Numerics;
using HyperPlot.Interaction;

namespace HyperPlot.Tests;

/// <summary>
/// The views past one panel of one plot (design §6.2, §10, row I1b): secondary scales, facets, figures, band axes, and polar
/// radii.
/// </summary>
public sealed class MultiPanelTests
{
    private static readonly Color Magenta = new(255, 0, 255, 255);

    /// <summary>
    /// An operation in a panel moves a primary and its secondary scale by the same shares of their ranges (design §6.2): the
    /// overlap of the lines over each stays.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A line over y fixed to [−20, 20] and one over a secondary y fixed to [0, 100]; animations off.
    ///
    /// Steps:
    /// - Turn the wheel 2 notches at a quarter of the panel's width and a third of its height up; pan by (30, −20) pixels.
    ///
    /// Expected:
    /// - After each, the new y's ends, as shares of the old y, equal the new secondary y's ends as shares of the old one,
    ///   within 1e-9; y has narrowed after the wheel.
    /// </remarks>
    [Fact]
    public void Secondary_PanelOperationMovesBothByTheSameShares()
    {
        using var f = SecondaryFixture();
        var (left, top, width, height) = f.Rect;
        var at = new Vector2(left + (width / 4f), top + (height * 2 / 3f));
        var (y, y2) = (f.View().Y, SecondaryY(f));
        f.Wheel(at, 2);
        f.Settle();
        var (wheeledY, wheeledY2) = (f.View().Y, SecondaryY(f));
        AssertSameShares(y, wheeledY, y2, wheeledY2);
        Assert.True(wheeledY.HighValue - wheeledY.LowValue < y.HighValue - y.LowValue);

        f.Engine.Views.Pan(EngineFixture.Panel, new Vector2(30, -20));
        f.Settle();
        AssertSameShares(wheeledY, f.View().Y, wheeledY2, SecondaryY(f));
    }

    /// <summary>
    /// An operation on an axis moves that axis's scale alone (design §6.2): the secondary y's band moves the secondary y, and
    /// the y's band the y.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The plot of <see cref="Secondary_PanelOperationMovesBothByTheSameShares"/>.
    ///
    /// Steps:
    /// - Turn the wheel 2 notches 5 pixels right of the panel; then 5 pixels left of it.
    ///
    /// Expected:
    /// - The first: the secondary y narrower, x and y as they were. The second: y narrower, x and the secondary y as they
    ///   were.
    /// </remarks>
    [Fact]
    public void Secondary_AxisOperationMovesThatAxisAlone()
    {
        using var f = SecondaryFixture();
        var (left, top, width, height) = f.Rect;
        var (view, y2) = (f.View(), SecondaryY(f));
        Assert.Equal(HitAreaKind.AxisY2, f.Engine.HitArea(new Vector2(left + width + 5, top + (height / 2f))).Kind);
        f.Wheel(new Vector2(left + width + 5, top + (height / 2f)), 2);
        f.Settle();
        Assert.Equal(view, f.View());
        Assert.True(Width(SecondaryY(f)) < Width(y2) * 0.8);

        y2 = SecondaryY(f);
        f.Wheel(new Vector2(left - 5, top + (height / 2f)), 2);
        f.Settle();
        Assert.Equal(view.X, f.View().X);
        Assert.True(f.View().Y.HighValue - f.View().Y.LowValue < (view.Y.HighValue - view.Y.LowValue) * 0.8);
        Assert.Equal(y2, SecondaryY(f));
    }

    /// <summary>
    /// A transform's second axis follows its primary and has no view of its own (design §6.2): an operation on it moves the
    /// primary, and the second axis shows the primary's range through the transform.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A line over y fixed to [−20, 20] with a second axis that copies it; animations off.
    ///
    /// Steps:
    /// - Turn the wheel 2 notches 5 pixels right of the panel, over the second axis.
    ///
    /// Expected:
    /// - The place is AxisY2; y narrower; the second axis's final domain is y's view.
    /// </remarks>
    [Fact]
    public void Secondary_TransformAxisFollowsThePrimary()
    {
        using var f = new EngineFixture(b => b.ScaleY(Scale.Linear().WithDomain(-20, 20).WithSecondaryAxis()));
        f.Engine.Options.ZoomAnimation = TimeSpan.Zero;
        var (left, top, width, height) = f.Rect;
        var at = new Vector2(left + width + 5, top + (height / 2f));
        var y = f.View().Y;
        Assert.Equal(HitAreaKind.AxisY2, f.Engine.HitArea(at).Kind);
        f.Wheel(at, 2);
        f.Settle();
        var view = f.View().Y;
        Assert.True(view.HighValue - view.LowValue < (y.HighValue - y.LowValue) * 0.8);
        var second = f.Engine.Plan!.Guides.PanelAt(0).Y2.FinalDomain;
        Assert.Equal(view.LowValue, second.Origin + second.Lower, 9);
        Assert.Equal(view.HighValue, second.Origin + second.Upper, 9);
    }

    /// <summary>
    /// A constraint reads and changes the primary scales, and a secondary scale takes the change it made by the same shares
    /// (design §6.2, row I1b): the overlap of the lines stays.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The plot of <see cref="Secondary_PanelOperationMovesBothByTheSameShares"/>, with a constraint that sets y to [−5, 15].
    ///
    /// Steps:
    /// - Turn the wheel 2 notches at the panel's middle.
    ///
    /// Expected:
    /// - y is [−5, 15]; the secondary y's ends, as shares of its range before, equal y's as shares of y before, within 1e-9.
    /// </remarks>
    [Fact]
    public void Secondary_FollowsWhatAConstraintDoes()
    {
        using var f = SecondaryFixture();
        f.Engine.Constraints.Add(new SetY(-5, 15));
        var (y, y2) = (f.View().Y, SecondaryY(f));
        f.Wheel(f.Middle, 2);
        f.Settle();
        Assert.Equal((-5.0, 15.0), (f.View().Y.LowValue, f.View().Y.HighValue));
        AssertSameShares(y, f.View().Y, y2, SecondaryY(f));
    }

    /// <summary>
    /// A facet's shared axis moves on every panel that shares it, and a free one on the panel operated on alone (design §6.2);
    /// a notice goes out for each panel whose view changed, a pixel names its panel, and keys go to the panel last operated on.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A line wrapped by 2 categories with the facet's scales given; animations off.
    ///
    /// Steps:
    /// - Read the place at the second panel's middle, and 5 pixels left of it; turn the wheel 2 notches at its middle; press
    ///   Plus.
    ///
    /// Expected:
    /// - The place is the panel (0, 1); left of it, a free y's axis of the panel (0, 1). The second panel's x and y narrower.
    ///   A shared axis (Fixed's x and y, FreeX's y, FreeY's x): the first panel's the same range as the second's. A free
    ///   one: the first panel's as it was. The notices: one for the second panel, and one for the first when an axis is
    ///   shared (none of Free's). Plus: the second panel's x narrower than after the wheel.
    /// </remarks>
    [Theory]
    [InlineData(FacetScales.Fixed)]
    [InlineData(FacetScales.FreeX)]
    [InlineData(FacetScales.FreeY)]
    [InlineData(FacetScales.Free)]
    public void Facet_SharedAxesMoveTogether(FacetScales scales)
    {
        using var data = new FacetData(scales);
        var f = data.Fixture;
        var (first, second) = (new PanelId(0, 0), new PanelId(0, 1));
        var before = View(f, first);
        var middle = Middle(f, second);
        Assert.Equal(new HitArea(HitAreaKind.Panel, second), f.Engine.HitArea(middle));
        if (scales is FacetScales.FreeY or FacetScales.Free)
        {
            var left = PanelRectangle(f, second).Left;
            Assert.Equal(new HitArea(HitAreaKind.AxisY, second), f.Engine.HitArea(new Vector2(left - 5, middle.Y)));
        }

        var full = View(f, second);
        f.Changes.Clear();
        f.Wheel(middle, 2);
        f.Settle();
        var (one, two) = (View(f, first), View(f, second));
        Assert.True(Width(two.X) < Width(full.X) && Width(two.Y) < Width(full.Y));
        var (sharedX, sharedY) = (scales is FacetScales.Fixed or FacetScales.FreeY, scales is FacetScales.Fixed or FacetScales.FreeX);
        Assert.Equal(sharedX ? two.X : before.X, one.X);
        Assert.Equal(sharedY ? two.Y : before.Y, one.Y);
        PanelId[] told = sharedX || sharedY ? [first, second] : [second];
        Assert.Equal(told, f.Changes.Select(c => c.Panel).OrderBy(p => p.Panel));

        Assert.True(f.Key(PlotKey.Plus));
        f.Settle();
        Assert.True(Width(View(f, second).X) < Width(two.X));
    }

    /// <summary>
    /// A pixel outside a facet's panels is the band of an axis the nearest panel in its row or column draws on that side
    /// (design §6.3): a shared axis is drawn on the outer panels alone (row G6b), and a strip between a side and its panel is
    /// no axis.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A line wrapped by 4 categories in 2 columns with fixed scales, x with a secondary axis on top.
    ///
    /// Steps:
    /// - Read the places 1 pixel below and left of each panel, in the strip above the first, and above that strip.
    ///
    /// Expected:
    /// - Below the bottom panels: their AxisX. Below the top ones, in the gap between the rows: Figure. Left of the left
    ///   panels: their AxisY; left of the right ones: Figure. The strip: Figure; above it: the first panel's AxisX2.
    /// </remarks>
    [Fact]
    public void Facet_AxisBandsAreTheOnesItDraws()
    {
        var names = Enumerable.Range(0, 1000).Select(i => "abcd"[i / 250].ToString()).ToArray();
        using var y = Column.Wrap(Enumerable.Range(0, 1000).Select(i => MathF.Sin(i * 0.02f)).ToArray());
        var table = DataTable
            .Create()
            .Add("x", Column.Uniform(0, 1, 1000))
            .Add("y", y)
            .Add("k", Column.Categorical(names, ["a", "b", "c", "d"]))
            .Build();
        using var f = new EngineFixture();
        f.Engine.SetPlot(
            Plot.Create(table)
                .Line(table.Col<double>("x"), table.Col<float>("y"))
                .ScaleX(Scale.Linear().WithSecondaryAxis())
                .FacetWrap(table.Cat("k"), columns: 2)
                .Build()
        );
        f.Engine.RenderFrame();
        HitArea At(float x, float y) => f.Engine.HitArea(new Vector2(x, y));
        for (var p = 0; p < 4; p++)
        {
            var (left, top, width, height) = PanelRectangle(f, new PanelId(0, p));
            var (bottom, isLeft) = (p >= 2, p % 2 == 0);
            Assert.Equal(bottom ? new HitArea(HitAreaKind.AxisX, new PanelId(0, p)) : Kind(HitAreaKind.Figure), At(left + 5, top + height + 1));
            Assert.Equal(isLeft ? new HitArea(HitAreaKind.AxisY, new PanelId(0, p)) : Kind(HitAreaKind.Figure), At(left - 1, top + 5));
        }

        var first = f.Engine.Plan!.Guides.PanelAt(0);
        var strip = first.Offsets[PlotGuides.SideTop];
        Assert.True(strip > 2);
        Assert.Equal(Kind(HitAreaKind.Figure), At(first.Left + 5, first.Top - (strip / 2f)));
        Assert.Equal(new HitArea(HitAreaKind.AxisX2, new PanelId(0, 0)), At(first.Left + 5, first.Top - strip - 2));

        static HitArea Kind(HitAreaKind kind) => new(kind, new PanelId(0, 0));
    }

    /// <summary>
    /// The history is one a plot, and an entry holds every panel's views (design §6.2): going back undoes the last operation
    /// on whichever panel it was.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A line wrapped by 2 categories with free scales; animations off.
    ///
    /// Steps:
    /// - Zoom the first panel by 2; zoom the second; go back; go back.
    ///
    /// Expected:
    /// - Three entries. After the first back: the second panel at its full view, the first still zoomed. After the second:
    ///   both at their full views.
    /// </remarks>
    [Fact]
    public void Facet_OneHistoryEntryHoldsEveryPanel()
    {
        using var data = new FacetData(FacetScales.Free);
        var f = data.Fixture;
        var (first, second) = (new PanelId(0, 0), new PanelId(0, 1));
        var (full1, full2) = (View(f, first), View(f, second));
        f.Engine.Views.ZoomAt(first, Middle(f, first), 2, animate: false);
        var zoomed = View(f, first);
        f.Engine.Views.ZoomAt(second, Middle(f, second), 2, animate: false);
        Assert.Equal((3, 2), f.Engine.History);

        Assert.True(f.Engine.Views.Back());
        f.Settle();
        Assert.Equal((zoomed, full2), (View(f, first), View(f, second)));
        Assert.True(f.Engine.Views.Back());
        f.Settle();
        Assert.Equal((full1, full2), (View(f, first), View(f, second)));
    }

    /// <summary>
    /// A figure sends a drag to the plot it was pressed on, the wheel to the plot under the cursor, and keys to the plot last
    /// operated on; each plot has its views and history (design §6.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A figure of two plots side by side of the fixture's line; animations off.
    ///
    /// Steps:
    /// - Read the place at the second plot's panel; drag from the first plot's middle into the second plot; turn the wheel
    ///   over the second; press Plus; click the first plot and press Plus; go back.
    ///
    /// Expected:
    /// - The place is the panel (1, 0). The drag moves the first plot's x alone. The wheel the second's alone. The first Plus
    ///   zooms the second, the one after the click the first. Back undoes the first plot's zoom and leaves the second's.
    /// </remarks>
    [Fact]
    public void Figure_RoutesInputToItsPlots()
    {
        using var f = new EngineFixture();
        f.Engine.Options.ZoomAnimation = TimeSpan.Zero;
        f.Engine.SetFigure(Figure.HConcat(f.Spec, f.Spec));
        f.Engine.RenderFrame();
        var (one, two) = (new PanelId(0, 0), new PanelId(1, 0));
        var (middle1, middle2) = (Middle(f, one), Middle(f, two));
        Assert.Equal(new HitArea(HitAreaKind.Panel, two), f.Engine.HitArea(middle2));
        var (full1, full2) = (View(f, one), View(f, two));

        f.Drag(middle1, middle2);
        f.Settle();
        Assert.NotEqual(full1.X, View(f, one).X);
        Assert.Equal(full2, View(f, two));

        var dragged = View(f, one);
        f.Wheel(middle2, 2);
        f.Settle();
        Assert.Equal(dragged, View(f, one));
        var wheeled = View(f, two);
        Assert.True(Width(wheeled.X) < Width(full2.X));

        Assert.True(f.Key(PlotKey.Plus));
        f.Settle();
        Assert.Equal(dragged, View(f, one));
        Assert.True(Width(View(f, two).X) < Width(wheeled.X));

        var keyed = View(f, two);
        f.Engine.OnPointer(f.Pointer(PointerEventKind.Down, middle1, PointerButtons.Left));
        f.Engine.OnPointer(f.Pointer(PointerEventKind.Up, middle1, PointerButtons.Left));
        Assert.True(f.Key(PlotKey.Plus));
        f.Settle();
        Assert.True(Width(View(f, one).X) < Width(dragged.X));
        Assert.Equal(keyed, View(f, two));

        Assert.True(f.Engine.Views.Back());
        f.Settle();
        Assert.Equal(dragged, View(f, one));
        Assert.Equal(keyed, View(f, two));
    }

    /// <summary>
    /// <see cref="ViewOperations.ResetAll"/> brings every plot of a figure to its full view, and a new figure keeps each
    /// plot's view where the plots and their mappings stay (design §6.2, row I1b).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A figure of two plots side by side of the fixture's line; animations off.
    ///
    /// Steps:
    /// - Zoom both plots by 2 and reset all. Zoom both again; set a new figure of the same two plots; one whose second plot
    ///   has y fixed to [−50, 50]; one of three plots of the fixture's line.
    ///
    /// Expected:
    /// - The reset: both at their full views. The same plots: both views as zoomed. The second plot's mapping changed: the
    ///   first as zoomed, the second's y [−50, 50]. Three plots: each at the full view.
    /// </remarks>
    [Fact]
    public void Figure_ResetAllAndANewFigure()
    {
        using var f = new EngineFixture();
        f.Engine.Options.ZoomAnimation = TimeSpan.Zero;
        f.Engine.SetFigure(Figure.HConcat(f.Spec, f.Spec));
        f.Engine.RenderFrame();
        var (one, two) = (new PanelId(0, 0), new PanelId(1, 0));
        var full = View(f, one);
        Assert.Equal(full, View(f, two));

        Zoom();
        Assert.NotEqual(full, View(f, one));
        Assert.NotEqual(full, View(f, two));
        f.Engine.Views.ResetAll(animate: false);
        f.Settle();
        Assert.Equal((full, full), (View(f, one), View(f, two)));

        Zoom();
        var zoomed = View(f, one);
        f.Engine.SetFigure(Figure.HConcat(f.Spec, f.Spec));
        f.Engine.RenderFrame();
        Assert.Equal((zoomed, zoomed), (View(f, one), View(f, two)));

        var wide = Plot.Create(f.Table).Line(f.Table.Col<double>("x"), f.Table.Col<float>("y")).ScaleY(Scale.Linear().WithDomain(-50, 50)).Build();
        f.Engine.SetFigure(Figure.HConcat(f.Spec, wide));
        f.Engine.RenderFrame();
        Assert.Equal(zoomed, View(f, one));
        Assert.Equal((-50.0, 50.0), (View(f, two).Y.LowValue, View(f, two).Y.HighValue));

        f.Engine.SetFigure(Figure.HConcat(f.Spec, f.Spec, f.Spec));
        f.Engine.RenderFrame();
        Assert.All(Enumerable.Range(0, 3), k => Assert.Equal(full.Y, View(f, new PanelId(k, 0)).Y));

        void Zoom()
        {
            f.Engine.Views.ZoomAt(one, Middle(f, one), 2, animate: false);
            f.Engine.Views.ZoomAt(two, Middle(f, two), 2, animate: false);
            f.Settle();
        }
    }

    /// <summary>
    /// A figure's plot names its own title and legend (design §6.3): the place carries the plot's number.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A figure of two plots side by side, each a labeled line (a legend) with a title.
    ///
    /// Steps:
    /// - Read the places at the middle of each plot's title band and legend.
    ///
    /// Expected:
    /// - Title and Legend, each with the plot's number and panel 0.
    /// </remarks>
    [Fact]
    public void Figure_TitleAndLegendNameTheirPlot()
    {
        using var f = new EngineFixture();
        var spec = Plot.Create(f.Table).Line(f.Table.Col<double>("x"), f.Table.Col<float>("y"), l => l.Label("sine")).Title("Cell").Build();
        f.Engine.SetFigure(Figure.HConcat(spec, spec));
        f.Engine.RenderFrame();
        var padding = MathF.Round(spec.Theme.Padding);
        for (var k = 0; k < 2; k++)
        {
            var guides = f.Engine.Figure!.Plans[k].Guides;
            var (left, top, width, _) = guides.Area;
            Assert.True(guides.TitleBand > 0 && guides.LegendWidth > 0);
            var panel = guides.PanelAt(0);
            var title = new Vector2(left + (width / 2f), top + padding + (guides.TitleBand / 2f));
            var legend = new Vector2(left + width - padding - (guides.LegendWidth / 2f), panel.Top + (panel.Height / 2f));
            Assert.Equal(new HitArea(HitAreaKind.Title, new PanelId(k, 0)), f.Engine.HitArea(title));
            Assert.Equal(new HitArea(HitAreaKind.Legend, new PanelId(k, 0)), f.Engine.HitArea(legend));
        }
    }

    /// <summary>
    /// A band axis's view is a range of the categories' positions (design §6.2): the bands outside it are not drawn, and the
    /// labels are thinned over the categories it shows.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Bars over 1,000 categories c0 to c999, magenta; animations off.
    ///
    /// Steps:
    /// - Set the view of x to the positions [100, 120] with y as it is; draw.
    ///
    /// Expected:
    /// - The view's x is [100, 120]; 20 or 21 magenta bars, each over the panel; at least 2 labels, each of a category from
    ///   c100 to c119, at a pixel inside the axis.
    /// </remarks>
    [Fact]
    public void Band_ViewDrawsTheCategoriesItShows()
    {
        const int Count = 1000;
        var names = Enumerable.Range(0, Count).Select(i => $"c{i}").ToArray();
        using var y = Column.Wrap(Enumerable.Range(0, Count).Select(i => 1.0 + (i % 7)).ToArray());
        var table = DataTable
            .Create()
            .Add("c", Column.Categorical(Enumerable.Range(0, Count).Select(i => names[i]).ToArray(), names))
            .Add("y", y)
            .Build();
        using var f = new EngineFixture();
        f.Engine.Options.ZoomAnimation = TimeSpan.Zero;
        f.Engine.SetPlot(
            Plot.Create(table).Bar(table.Cat("c"), table.Col<double>("y"), configure: b => b.Fill(Magenta)).ScaleX(Scale.Band()).Build()
        );
        f.Engine.RenderFrame();
        var full = f.View();
        f.Engine.Views.SetView(EngineFixture.Panel, new PanelView2D(ViewRange.FromValues(100.0, 120.0), full.Y));
        f.Engine.RenderFrame();

        Assert.Equal((100.0, 120.0), (f.View().X.LowValue, f.View().X.HighValue));
        var (left, _, width, _) = f.Rect;
        var bars = f.Engine.Plan!.List.Rects.ToArray().Where(r => r.Color.X > 0.99f && r.Color.Y < 0.01f && r.Color.Z > 0.99f).ToArray();
        Assert.InRange(bars.Length, 20, 21);
        Assert.All(bars, r => Assert.True(r.X + r.Width >= left - 1e-3 && r.X <= left + width + 1e-3, $"{r}"));
        var axis = f.Engine.Plan.Guides.X;
        var labels = axis.TickLabels.ToArray();
        Assert.True(labels.Length >= 2);
        Assert.All(labels, l => Assert.InRange(int.Parse(l[1..], System.Globalization.CultureInfo.InvariantCulture), 100, 119));
        Assert.All(axis.Pixels.ToArray(), p => Assert.InRange(p, 0, axis.Length));
    }

    /// <summary>
    /// A band axis's labels stay on their categories when the view pans (design §6.2): they go on the categories whose
    /// numbers are multiples of the thinning step k, wherever the view starts.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Bars over 1,000 categories c0 to c999, x's view [100, 140]; animations off.
    ///
    /// Steps:
    /// - Read the labels; pan x by 3.4 categories to higher numbers; read them again.
    ///
    /// Expected:
    /// - Before, at least 2 labels, k the difference of the first two numbers, each number a multiple of k. After the pan,
    ///   each number a multiple of k; a label on both sides of the pan moved by the pan's pixels, within 1e-3.
    /// </remarks>
    [Fact]
    public void Band_PanKeepsTheLabelsOnTheirCategories()
    {
        const int Count = 1000;
        var names = Enumerable.Range(0, Count).Select(i => $"c{i}").ToArray();
        using var y = Column.Wrap(Enumerable.Range(0, Count).Select(i => 1.0 + (i % 7)).ToArray());
        var table = DataTable.Create().Add("c", Column.Categorical(names, names)).Add("y", y).Build();
        using var f = new EngineFixture();
        f.Engine.Options.ZoomAnimation = TimeSpan.Zero;
        f.Engine.SetPlot(Plot.Create(table).Bar(table.Cat("c"), table.Col<double>("y")).ScaleX(Scale.Band()).Build());
        f.Engine.RenderFrame();
        f.Engine.Views.SetView(EngineFixture.Panel, new PanelView2D(ViewRange.FromValues(100.0, 140.0), f.View().Y));
        f.Engine.RenderFrame();
        var before = Labels(f);
        Assert.True(before.Length >= 2);
        var k = before[1].Number - before[0].Number;
        Assert.True(k >= 1);
        Assert.All(before, l => Assert.Equal(0, l.Number % k));

        var pixels = 3.4f * f.Rect.Width / 40f;
        f.Engine.Views.Pan(EngineFixture.Panel, new Vector2(-pixels, 0));
        f.Engine.RenderFrame();
        var after = Labels(f);
        Assert.All(after, l => Assert.Equal(0, l.Number % k));
        var common = after.Where(a => before.Any(b => b.Number == a.Number)).ToArray();
        Assert.NotEmpty(common);
        Assert.All(common, a => Assert.Equal(before.Single(b => b.Number == a.Number).Pixel - pixels, a.Pixel, 1e-3));

        static (int Number, double Pixel)[] Labels(EngineFixture f)
        {
            var axis = f.Engine.Plan!.Guides.X;
            var pixels = axis.Pixels.ToArray();
            return axis
                .TickLabels.ToArray()
                .Select((l, i) => (int.Parse(l[1..], System.Globalization.CultureInfo.InvariantCulture), (double)pixels[i]))
                .ToArray();
        }
    }

    /// <summary>
    /// A polar plot moves its radius alone and keeps the full turn of its angle (design §6.2): a move up moves the data
    /// outward, and a rectangle zooms the radius to its span of distances from the center. The data past the outer edge of a
    /// view of the radius are cut there (§4.4).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The fixture's line in polar coordinates, theta x, without axes and grid; animations off.
    ///
    /// Steps:
    /// - Turn the wheel 2 notches at the middle; pan by (0, −30) pixels; zoom to the rectangle from the center plus
    ///   (20, −10) to the center plus (60, 30); set a view with a narrower x.
    ///
    /// Expected:
    /// - After each: x as it was. The wheel: y narrower. The pan: y lower by 30 / (R − r0) of its width, so the values under
    ///   the radius grow and the data move out; every point of the line within R and the outer tolerance of the center. The
    ///   rectangle: y from the share 20 / (R − r0) of the y before to the share √(60² + 30²) / (R − r0), its nearest and
    ///   farthest points (r0 = 0). The view: ArgumentException.
    /// </remarks>
    [Fact]
    public void Polar_RadiusAloneMoves()
    {
        using var f = new EngineFixture(b => b.Coord(Coord.Polar()).Theme(Theme.Light with { ShowAxes = false, ShowGrid = false }));
        f.Engine.Options.ZoomAnimation = TimeSpan.Zero;
        var full = f.View();
        f.Wheel(f.Middle, 2);
        f.Settle();
        var wheeled = f.View();
        Assert.Equal(full.X, wheeled.X);
        Assert.True(Width(wheeled.Y) < Width(full.Y) * 0.8);
        var polar = f.Engine.Plan!.Guides.PanelAt(0).Polar!.Value;
        Assert.Equal(0, polar.Inner);
        f.Engine.Views.Pan(EngineFixture.Panel, new Vector2(0, -30));
        f.Settle();
        var panned = f.View();
        Assert.Equal(full.X, panned.X);
        var shift = 30.0 / polar.Radius * Width(wheeled.Y);
        Assert.Equal(wheeled.Y.LowValue - shift, panned.Y.LowValue, 1e-9);
        Assert.Equal(wheeled.Y.HighValue - shift, panned.Y.HighValue, 1e-9);

        var center = new Vector2((float)polar.CenterX, (float)polar.CenterY);
        var segments = f.Engine.Plan.List.Segments.ToArray();
        Assert.NotEmpty(segments);
        Assert.All(
            segments,
            s =>
                Assert.True(
                    Math.Max((s.Start - center).Length(), (s.End - center).Length()) <= polar.Radius + PolarTransform.OuterTolerance + 1e-3,
                    $"{s}"
                )
        );

        f.Engine.Views.ZoomToRect(EngineFixture.Panel, center + new Vector2(20, -10), center + new Vector2(60, 30), animate: false);
        f.Settle();
        Assert.Equal(full.X, f.View().X);
        var (near, far) = (20.0 / polar.Radius, Math.Sqrt((60.0 * 60) + (30.0 * 30)) / polar.Radius);
        Assert.Equal(panned.Y.LowValue + (near * Width(panned.Y)), f.View().Y.LowValue, 1e-6);
        Assert.Equal(panned.Y.LowValue + (far * Width(panned.Y)), f.View().Y.HighValue, 1e-6);

        _ = Assert.Throws<ArgumentException>(() =>
            f.Engine.Views.SetView(EngineFixture.Panel, new PanelView2D(ViewRange.FromValues(100.0, 200.0), f.View().Y))
        );
    }

    /// <summary>
    /// The wheel on a facet's panel and a drag on a figure's plot allocate nothing once warm (design §6.2, rule perf), as on a
    /// plot alone (row I1a).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A line wrapped by 2 categories with fixed scales; a figure of two plots of the fixture's line. The shaping cache full.
    ///
    /// Steps:
    /// - Rounds of 40 wheel turns on the second panel, drawing after each, until a round allocates nothing (at most 6), then
    ///   a garbage collection and one more. The same with 4 drags of 12 events on the second plot.
    ///
    /// Expected:
    /// - The last round of each: 0 bytes.
    /// </remarks>
    [Fact]
    public void FacetWheelAndFigureDrag_AllocateNothing()
    {
        using var data = new FacetData(FacetScales.Fixed);
        var facet = data.Fixture;
        Fill(facet);
        var panel = Middle(facet, new PanelId(0, 1));
        Assert.Equal(0, Warm(() => Wheels(facet, panel)));

        using var f = new EngineFixture();
        f.Engine.SetFigure(Figure.HConcat(f.Spec, f.Spec));
        f.Engine.RenderFrame();
        Fill(f);
        var plot = Middle(f, new PanelId(1, 0));
        Assert.Equal(0, Warm(() => Drags(f, plot)));

        static long Warm(Func<long> round)
        {
            var bytes = -1L;
            for (var i = 0; i < 6 && bytes != 0; i++)
            {
                bytes = round();
            }

            // An allocation that follows a collection is counted in every run (.cursor/rules/bench.mdc).
            GC.Collect();
            return round();
        }

        static void Fill(EngineFixture f)
        {
            // The shaping cache full, as after a long session (design §5.6), and the notices' list with room for every round.
            for (var i = 0; i < Rendering.TextEngine.CacheCapacity; i++)
            {
                _ = f.Backend.Text.Measure(
                    "filler " + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    new Rendering.TextStyle(new Color(0, 0, 0, 255), 9)
                );
            }

            f.Changes.Capacity = 20_000;
        }
    }

    private static long Wheels(EngineFixture f, Vector2 at)
    {
        var total = 0L;
        for (var i = 0; i < 40; i++)
        {
            f.Time.Advance(TimeSpan.FromMilliseconds(16));
            var e = new WheelEvent(at + new Vector2(i, 0), 0, i < 20 ? 1 : -1, ModifierKeys.None, f.Time.GetTimestamp());
            var before = GC.GetAllocatedBytesForCurrentThread();
            f.Engine.OnWheel(e);
            f.Engine.RenderFrame();
            total += GC.GetAllocatedBytesForCurrentThread() - before;
        }

        return total;
    }

    private static long Drags(EngineFixture f, Vector2 at)
    {
        var total = 0L;
        var events = new PointerEvent[12];
        for (var drag = 0; drag < 4; drag++)
        {
            events[0] = f.Pointer(PointerEventKind.Down, at, PointerButtons.Left);
            for (var i = 1; i < 11; i++)
            {
                events[i] = f.Pointer(PointerEventKind.Move, at + new Vector2(i * (drag % 2 == 0 ? 6 : -6), i * 2)) with
                {
                    Buttons = PointerButtons.Left,
                };
            }

            events[11] = f.Pointer(PointerEventKind.Up, at, PointerButtons.Left);
            foreach (var e in events)
            {
                f.Time.Advance(TimeSpan.FromMilliseconds(16));
                var stamped = e with { Timestamp = f.Time.GetTimestamp() };
                var before = GC.GetAllocatedBytesForCurrentThread();
                f.Engine.OnPointer(stamped);
                f.Engine.RenderFrame();
                total += GC.GetAllocatedBytesForCurrentThread() - before;
            }
        }

        return total;
    }

    private static EngineFixture SecondaryFixture()
    {
        var f = new EngineFixture();
        f.Engine.Options.ZoomAnimation = TimeSpan.Zero;
        var (x, y) = (f.Table.Col<double>("x"), f.Table.Col<float>("y"));
        f.Engine.SetPlot(
            Plot.Create(f.Table)
                .Line(x, y)
                .Line(x, y, l => l.SecondaryY())
                .ScaleY(Scale.Linear().WithDomain(-20, 20))
                .ScaleY2(Scale.Linear().WithDomain(0, 100))
                .Build()
        );
        f.Engine.RenderFrame();
        return f;
    }

    private static ScaleRange SecondaryY(EngineFixture f) => f.Engine.Plan!.Guides.PanelAt(0).Y2.FinalDomain;

    private static void AssertSameShares(ViewRange from, ViewRange to, ScaleRange secondFrom, ScaleRange secondTo)
    {
        var width = from.HighValue - from.LowValue;
        var secondWidth = secondFrom.Upper - secondFrom.Lower;
        var secondLow = secondFrom.Origin + secondFrom.Lower;
        Assert.Equal((to.LowValue - from.LowValue) / width, (secondTo.Origin + secondTo.Lower - secondLow) / secondWidth, 9);
        Assert.Equal((to.HighValue - from.LowValue) / width, (secondTo.Origin + secondTo.Upper - secondLow) / secondWidth, 9);
    }

    private static double Width(in ViewRange range) => range.HighValue - range.LowValue;

    private static double Width(in ScaleRange range) => range.Upper - range.Lower;

    private static (ViewRange X, ViewRange Y) View(EngineFixture f, PanelId panel)
    {
        var view = (PanelView2D)f.Engine.CurrentView(panel);
        return (view.X, view.Y);
    }

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

    /// <summary>A constraint that sets every proposal's y.</summary>
    private sealed class SetY(double low, double high) : IViewConstraint
    {
        public bool Constrain(ViewProposal proposal)
        {
            _ = proposal.TryGet2D(out var x, out _);
            proposal.Set2D(x, ViewRange.FromValues(low, high));
            return true;
        }
    }

    /// <summary>A line of 1,000 points wrapped by 2 categories of 500 in an engine, the facet's scales given; animations off.</summary>
    private sealed class FacetData : IDisposable
    {
        private readonly Column<float> _y;

        public FacetData(FacetScales scales)
        {
            var names = Enumerable.Range(0, 1000).Select(i => i < 500 ? "a" : "b").ToArray();
            _y = Column.Wrap(Enumerable.Range(0, 1000).Select(i => MathF.Sin(i * 0.02f) * (i < 500 ? 10 : 30)).ToArray());
            var table = DataTable.Create().Add("x", Column.Uniform(0, 1, 1000)).Add("y", _y).Add("k", Column.Categorical(names, ["a", "b"])).Build();
            Fixture = new EngineFixture();
            Fixture.Engine.Options.ZoomAnimation = TimeSpan.Zero;
            Fixture.Engine.SetPlot(
                Plot.Create(table).Line(table.Col<double>("x"), table.Col<float>("y")).FacetWrap(table.Cat("k"), scales: scales).Build()
            );
            Fixture.Engine.RenderFrame();
        }

        public EngineFixture Fixture { get; }

        public void Dispose()
        {
            Fixture.Dispose();
            _y.Dispose();
        }
    }
}
