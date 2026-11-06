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
    /// A facet's shared axis moves on every panel that shares it, and a free one on the panel operated on alone (design §6.2);
    /// a pixel names its panel.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A line wrapped by 2 categories with the facet's scales given; animations off.
    ///
    /// Steps:
    /// - Read the place at the second panel's middle, and 5 pixels left of it; turn the wheel 2 notches at its middle.
    ///
    /// Expected:
    /// - The place is the panel (0, 1); left of it, a free y's axis of the panel (0, 1). The second panel's x and y narrower.
    ///   A shared axis (Fixed's x and y, FreeX's y, FreeY's x): the first panel's the same range as the second's. A free
    ///   one: the first panel's as it was.
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
        f.Wheel(middle, 2);
        f.Settle();
        var (one, two) = (View(f, first), View(f, second));
        Assert.True(Width(two.X) < Width(full.X) && Width(two.Y) < Width(full.Y));
        var (sharedX, sharedY) = (scales is FacetScales.Fixed or FacetScales.FreeY, scales is FacetScales.Fixed or FacetScales.FreeX);
        Assert.Equal(sharedX ? two.X : before.X, one.X);
        Assert.Equal(sharedY ? two.Y : before.Y, one.Y);
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
    /// A polar plot moves its radius alone and keeps the full turn of its angle (design §6.2); the data past the outer edge
    /// of a view of the radius are cut there (§4.4).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The fixture's line in polar coordinates, theta x, without axes and grid; animations off.
    ///
    /// Steps:
    /// - Turn the wheel 2 notches at the middle; pan by (0, −30) pixels; set a view with a narrower x.
    ///
    /// Expected:
    /// - After the wheel and the pan: x as it was, y narrower and then moved; every point of the line within R and the
    ///   tolerance of the center. The view: ArgumentException.
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
        f.Engine.Views.Pan(EngineFixture.Panel, new Vector2(0, -30));
        f.Settle();
        Assert.Equal(full.X, f.View().X);
        Assert.NotEqual(wheeled.Y, f.View().Y);

        var polar = f.Engine.Plan!.Guides.PanelAt(0).Polar!.Value;
        var center = new Vector2((float)polar.CenterX, (float)polar.CenterY);
        var segments = f.Engine.Plan.List.Segments.ToArray();
        Assert.NotEmpty(segments);
        Assert.All(
            segments,
            s =>
                Assert.True(
                    Math.Max((s.Start - center).Length(), (s.End - center).Length()) <= polar.Radius + PolarTransform.Tolerance + 1e-3,
                    $"{s}"
                )
        );
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
    ///   one more. The same with 4 drags of 12 events on the second plot.
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
