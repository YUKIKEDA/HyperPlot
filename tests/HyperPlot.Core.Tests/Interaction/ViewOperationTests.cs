using System.Numerics;
using HyperPlot.Interaction;

namespace HyperPlot.Tests;

/// <summary>
/// The operations on the views, the ranges, the constraints, and the view stage of the plan (design §6.2, §4.8, row I1a).
/// </summary>
public sealed class ViewOperationTests
{
    /// <summary>
    /// A zoom at a pixel keeps the value under it (design §6.2), in the origin and differences even past 1e12; a pan moves
    /// the data with the pointer; a rectangle becomes the view; a view set directly is taken; the full view comes back; the
    /// axes given are the ones that move; every view's low difference lies in [0, 1) (§3.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A line over x from 1e13 to 1e13 + 999 and y fixed to [−20, 20]; animations off.
    ///
    /// Steps:
    /// - Read the value under a pixel a third of the way across; zoom by 2 there; read it again. Pan by 59 pixels right;
    ///   zoom x alone; zoom to a rectangle; set a view; show the full view.
    ///
    /// Expected:
    /// - The value under the pixel unchanged within 1e-3; the pan moves x by −59 / width of its width and y not; x alone moves
    ///   x alone; the rectangle's corners become the ends; the view set is the view; the full view is the first one.
    /// </remarks>
    [Fact]
    public void Operations_MoveTheViewAsTheySay()
    {
        var values = new float[1000];
        using var y = Column.Wrap(values);
        var table = DataTable.Create().Add("x", Column.Uniform(1e13, 1, values.Length)).Add("y", y).Build();
        using var f = new EngineFixture();
        f.Engine.Options.ZoomAnimation = TimeSpan.Zero;
        f.Engine.SetPlot(Plot.Create(table).Line(table.Col<double>("x"), table.Col<float>("y")).ScaleY(Scale.Linear().WithDomain(-20, 20)).Build());
        f.Engine.RenderFrame();
        var views = f.Engine.Views;
        var full = f.View();
        var at = new Vector2(f.Rect.Left + (f.Rect.Width / 3f), f.Middle.Y);

        var under = Value(f, at);
        views.ZoomAt(EngineFixture.Panel, at, 2);
        f.Engine.RenderFrame();
        var zoomed = f.View();
        Assert.Equal(0, (double)(Value(f, at).X.Origin - under.X.Origin) + Value(f, at).X.Lower - under.X.Lower, 1e-3);
        Assert.Equal(Width(full.X) / 2, Width(zoomed.X), 1e-6);
        Assert.InRange(zoomed.X.Lower, 0, 1);

        views.Pan(EngineFixture.Panel, new Vector2(59, 0));
        f.Engine.RenderFrame();
        var panned = f.View();
        Assert.Equal(-59.0 / f.Rect.Width * Width(zoomed.X), LowDiff(panned.X, zoomed.X), 1e-6);
        Assert.Equal(zoomed.Y, panned.Y);

        views.ZoomAt(EngineFixture.Panel, f.Middle, 4, ViewAxes.X);
        f.Engine.RenderFrame();
        Assert.Equal(panned.Y, f.View().Y);

        var (a, b) = (new Vector2(f.Rect.Left + 10, f.Rect.Top + 20), new Vector2(f.Rect.Left + 110, f.Rect.Top + 120));
        var (va, vb) = (Value(f, a), Value(f, b));
        views.ZoomToRect(EngineFixture.Panel, a, b);
        f.Engine.RenderFrame();
        var rect = f.View();
        Assert.Equal(0, LowDiff(rect.X, va.X), 1e-6);
        Assert.Equal(0, LowDiff(new ViewRange(rect.X.Origin, rect.X.Upper, rect.X.Upper), vb.X), 1e-6);
        Assert.Equal(Low(vb.Y), Low(rect.Y), 1e-9);
        Assert.Equal(Low(va.Y), High(rect.Y), 1e-9);

        var set = new PanelView2D(ViewRange.FromValues(1e13 + 100, 1e13 + 200), ViewRange.FromValues(-3.0, 4.0));
        views.SetView(EngineFixture.Panel, set);
        f.Engine.RenderFrame();
        Assert.Equal((0.0, Width(set.X)), (LowDiff(f.View().X, set.X), Width(f.View().X)));
        Assert.Equal((Low(set.Y), High(set.Y)), (Low(f.View().Y), High(f.View().Y)));

        views.Reset(EngineFixture.Panel);
        f.Engine.RenderFrame();
        Assert.Equal(full, f.View());
    }

    /// <summary>
    /// A range's four ways in and out (design §6.2, PR #267's review): from doubles, from longs exactly, from an origin and
    /// differences, and back; a time axis's view of 2026's nanoseconds round-trips through its origin and differences, and
    /// would lose a nanosecond through doubles.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A time scale in nanoseconds over 2026-01-01 and a line on it.
    ///
    /// Steps:
    /// - Make a range from two longs a nanosecond past a second apart; set it as the x view; read the view's origin and
    ///   differences; make a range from the same ends as doubles.
    ///
    /// Expected:
    /// - The long range's origin is the low end and its differences 0 and the span; the view read back has the same ends;
    ///   the double range's high end differs by the nanosecond.
    /// </remarks>
    [Fact]
    public void ViewRange_KeepsNanosecondsThroughTheOrigin()
    {
        var start = (new DateTime(2026, 1, 1) - DateTime.UnixEpoch).Ticks * 100;
        var low = start + 500_000_000_001;
        var high = low + 1_000_000_001;
        var range = ViewRange.FromValues(low, high);
        Assert.Equal((low, 0.0, 1_000_000_001.0), (range.Origin, range.Lower, range.Upper));
        Assert.Equal(new ViewRange(5, 0.25, 7.5), new ViewRange(5, 0.25, 7.5) with { });

        using var column = Column.Wrap(Enumerable.Range(0, 1000).Select(i => start + (i * 1_000_000_000L)).ToArray());
        using var values = Column.Wrap(new float[1000]);
        var table = DataTable.Create().Add("t", column).Add("y", values).Build();
        using var f = new EngineFixture();
        f.Engine.SetPlot(Plot.Create(table).Line(table.Col<long>("t"), table.Col<float>("y")).ScaleX(Scale.Time(TimeUnit.Nanoseconds)).Build());
        f.Engine.RenderFrame();
        var y = f.View().Y;
        f.Engine.Views.SetView(EngineFixture.Panel, new PanelView2D(range, y));
        f.Engine.RenderFrame();
        var read = f.View().X;
        Assert.Equal(low, read.Origin + (long)Math.Floor(read.Lower));
        Assert.Equal(high, read.Origin + (long)Math.Round(read.Upper));
        Assert.NotEqual(high, (long)ViewRange.FromValues((double)low, (double)high).HighValue);
    }

    /// <summary>
    /// The constraints (design §6.2): one that changes a proposal changes the view; one that refuses it leaves the view, with
    /// no notice and no frame; the zoom limits keep the panel's boundaries increasing however far a zoom goes.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An engine over a line, animations off; a constraint that clamps x to [0, 500]; then one that refuses everything.
    ///
    /// Steps:
    /// - Zoom out by 10; then, with the refusing one, zoom in; then with none, zoom in by 1e10 fifty times.
    ///
    /// Expected:
    /// - x within [0, 500]; the view, the notices, and the frame requests unchanged by the refused zoom; the last x range's
    ///   width above 0 and the frames drawn with no exception.
    /// </remarks>
    [Fact]
    public void Constraints_ChangeRefuseAndLimit()
    {
        using var f = new EngineFixture();
        f.Engine.Options.ZoomAnimation = TimeSpan.Zero;
        f.Engine.Constraints.Add(new Clamp());
        f.Engine.Views.ZoomAt(EngineFixture.Panel, f.Middle, 0.1);
        f.Engine.RenderFrame();
        Assert.InRange(Low(f.View().X), 0, 500);
        Assert.InRange(High(f.View().X), 0, 500);

        f.Engine.Constraints.Add(new Refuse());
        var (view, notices, requests) = (f.View(), f.Changes.Count, f.Requests);
        f.Engine.Views.ZoomAt(EngineFixture.Panel, f.Middle, 2);
        Assert.Equal((view, notices, requests), (f.View(), f.Changes.Count, f.Requests));

        f.Engine.Constraints.Clear();
        for (var i = 0; i < 50; i++)
        {
            f.Engine.Views.ZoomAt(EngineFixture.Panel, f.Middle, 1e10);
            f.Engine.RenderFrame();
        }

        Assert.True(Width(f.View().X) > 0);
    }

    /// <summary>
    /// The y fitted to the visible x (design §6.2): the least and greatest y of the rows between the x ends, from the
    /// pyramid, equal to a loop over the rows.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A sine line of 1,000 points; <see cref="AutoRangeY"/> added; animations off.
    ///
    /// Steps:
    /// - Set x views over [100, 140], [0, 999], and [500.5, 520.25].
    ///
    /// Expected:
    /// - Each y is the least and greatest of the rows whose x lies in the view, by a loop.
    /// </remarks>
    [Fact]
    public void AutoRangeY_FitsTheVisibleRows()
    {
        using var f = new EngineFixture();
        f.Engine.Options.ZoomAnimation = TimeSpan.Zero;
        f.Engine.Constraints.Add(new AutoRangeY(f.Engine));
        foreach (var (low, high) in new[] { (100.0, 140.0), (0.0, 999.0), (500.5, 520.25) })
        {
            f.Engine.Views.SetView(EngineFixture.Panel, new PanelView2D(ViewRange.FromValues(low, high), f.View().Y));
            f.Engine.RenderFrame();
            var rows = Enumerable.Range(0, 1000).Where(i => i >= low && i <= high).Select(i => (double)(MathF.Sin(i * 0.02f) * 10)).ToArray();
            Assert.Equal(rows.Min(), Low(f.View().Y), 1e-6);
            Assert.Equal(rows.Max(), High(f.View().Y), 1e-6);
        }
    }

    /// <summary>
    /// The y fitted to the visible x keeps the y where the design says so (design §6.2, PR #275's review): an x of a log
    /// scale, a stat's line alone, and a line split by categories that lie in runs, whose x starts again in each run.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - <see cref="AutoRangeY"/> added; animations off. Three plots: a line over x from 1 to 1,000 on a log x; a smooth of
    ///   the sine alone; a line colored by 2 categories of 500 rows each, x from 0 to 499 in each.
    ///
    /// Steps:
    /// - For each, set a view of a narrower x with the y as it is.
    ///
    /// Expected:
    /// - The y unchanged each time.
    /// </remarks>
    [Fact]
    public void AutoRangeY_KeepsTheYWhereItReadsNoPyramid()
    {
        var values = Enumerable.Range(0, 1000).Select(i => MathF.Sin(i * 0.02f) * 10).ToArray();
        using var y = Column.Wrap(values);
        using var positive = Column.Wrap(Enumerable.Range(1, 1000).Select(i => (double)i).ToArray());
        using var restarting = Column.Wrap(Enumerable.Range(0, 1000).Select(i => (double)(i % 500)).ToArray());
        var names = Enumerable.Range(0, 1000).Select(i => i < 500 ? "a" : "b").ToArray();
        var table = DataTable
            .Create()
            .Add("x", Column.Uniform(0, 1, 1000))
            .Add("p", positive)
            .Add("r", restarting)
            .Add("y", y)
            .Add("k", Column.Categorical(names, ["a", "b"]))
            .Build();
        var plots = new (PlotSpec Spec, double Low, double High)[]
        {
            (Plot.Create(table).Line(table.Col<double>("p"), table.Col<float>("y")).ScaleX(Scale.Log()).Build(), 1, 2),
            (Plot.Create(table).Smooth(table.Col<double>("x"), table.Col<float>("y"), 8).Build(), 100, 140),
            (Plot.Create(table).Line(table.Col<double>("r"), table.Col<float>("y"), l => l.ColorBy(table.Cat("k"))).Build(), 100, 140),
        };
        using var f = new EngineFixture();
        f.Engine.Options.ZoomAnimation = TimeSpan.Zero;
        f.Engine.Constraints.Add(new AutoRangeY(f.Engine));
        foreach (var (spec, low, high) in plots)
        {
            f.Engine.SetPlot(spec);
            f.Engine.RenderFrame();
            var before = f.View().Y;
            f.Engine.Views.SetView(EngineFixture.Panel, new PanelView2D(ViewRange.FromValues(low, high), before));
            f.Engine.RenderFrame();
            Assert.Equal(before, f.View().Y);
        }
    }

    /// <summary>
    /// A reversed scale zooms and pans by its own direction (design §6.2, row I1b): the value under the pixel of a zoom stays
    /// under it, and a pan moves the data with the pointer.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The fixture's line with x and y reversed, y fixed to [−20, 20]; animations off.
    ///
    /// Steps:
    /// - Read the values under a pixel a third of the way across and a quarter of the way up; zoom by 2 there; read them
    ///   again; pan by (30, −20) pixels; read the values under the pixel moved by as much.
    ///
    /// Expected:
    /// - Each time the values first read: within 1e-6 after the zoom, and within 1e-3 after the pan, whose pixels are floats.
    /// </remarks>
    [Fact]
    public void ReversedScales_ZoomAndPanByTheirDirection()
    {
        using var f = new EngineFixture(b =>
            b.ScaleX(Scale.Linear() with { Reverse = true }).ScaleY(Scale.Linear().WithDomain(-20, 20) with { Reverse = true })
        );
        f.Engine.Options.ZoomAnimation = TimeSpan.Zero;
        var (left, top, width, height) = f.Rect;
        var at = new Vector2(left + (width / 3f), top + (height * 3 / 4f));
        var (x, y) = f.Engine.PixelToView(EngineFixture.Panel, at);
        f.Engine.Views.ZoomAt(EngineFixture.Panel, at, 2);
        var (zoomedX, zoomedY) = f.Engine.PixelToView(EngineFixture.Panel, at);
        Assert.Equal(x.LowValue, zoomedX.LowValue, 1e-6);
        Assert.Equal(y.LowValue, zoomedY.LowValue, 1e-6);

        var by = new Vector2(30, -20);
        f.Engine.Views.Pan(EngineFixture.Panel, by);
        var (pannedX, pannedY) = f.Engine.PixelToView(EngineFixture.Panel, at + by);
        Assert.Equal(x.LowValue, pannedX.LowValue, 1e-3);
        Assert.Equal(y.LowValue, pannedY.LowValue, 1e-3);
    }

    /// <summary>
    /// The view stage of the plan (design §4.8): a view that changes lays out, extracts, and records again, but does not
    /// learn; an axis over a view takes its range with no nice.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An engine over a line with a learned, nice x; animations off.
    ///
    /// Steps:
    /// - Read the plan's learnings; zoom x by 3 at a third of the way; draw.
    ///
    /// Expected:
    /// - The learnings unchanged, the layouts and extractions one more; the x axis's final domain is the view, not a nice one.
    /// </remarks>
    [Fact]
    public void ViewStage_DoesNotLearnAndTakesNoNice()
    {
        using var f = new EngineFixture();
        f.Engine.Options.ZoomAnimation = TimeSpan.Zero;
        var plan = f.Engine.Plan!;
        var (learnings, layouts, extractions) = (plan.Learnings, plan.Layouts, plan.Extractions);
        f.Engine.Views.ZoomAt(EngineFixture.Panel, new Vector2(f.Rect.Left + (f.Rect.Width / 3f), f.Middle.Y), 3, ViewAxes.X);
        f.Engine.RenderFrame();

        Assert.Equal((learnings, layouts + 1, extractions + 1), (plan.Learnings, plan.Layouts, plan.Extractions));
        var view = f.View().X;
        var final = plan.Guides.X.FinalDomain;
        Assert.Equal((Low(view), High(view)), (final.Origin + final.Lower, final.Origin + final.Upper));
        Assert.NotEqual(Math.Round(Low(view)), Low(view));
    }

    private static (ViewRange X, ViewRange Y) Value(EngineFixture f, Vector2 at)
    {
        var view = (PanelView2D)f.Engine.CurrentView(EngineFixture.Panel);
        var (left, top, width, height) = f.Rect;
        var fx = (at.X - left) / (double)width;
        var fy = (top + height - at.Y) / (double)height;
        var x = view.X.Lower + (fx * (view.X.Upper - view.X.Lower));
        var y = view.Y.Lower + (fy * (view.Y.Upper - view.Y.Lower));
        return (new ViewRange(view.X.Origin, x, x), new ViewRange(view.Y.Origin, y, y));
    }

    private static double Low(ViewRange r) => r.Origin + r.Lower;

    // The low ends' difference, from the origins' exact difference: values near 1e13 lose 2e-3 as one double.
    private static double LowDiff(ViewRange a, ViewRange b) => (double)(a.Origin - b.Origin) + a.Lower - b.Lower;

    private static double High(ViewRange r) => r.Origin + r.Upper;

    private static double Width(ViewRange r) => r.Upper - r.Lower;

    private sealed class Clamp : IViewConstraint
    {
        public bool Constrain(ViewProposal proposal)
        {
            _ = proposal.TryGet2D(out var x, out var y);
            proposal.Set2D(ViewRange.FromValues(Math.Max(0, x.LowValue), Math.Min(500, x.HighValue)), y);
            return true;
        }
    }

    private sealed class Refuse : IViewConstraint
    {
        public bool Constrain(ViewProposal proposal) => false;
    }
}
