using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>The end labels held inside the figure, of row G3b (design §4.6, §10 G3b row).</summary>
public sealed class EdgeLabelTests : IDisposable
{
    private const int Width = 640;
    private const int Height = 360;
    private const int Length = 101;
    private const float Padding = 10;
    private readonly PlotWorkerPool _pool = new(1);
    private readonly Column<float> _y;
    private readonly DataTable _table;

    public EdgeLabelTests()
    {
        _y = Column.Wrap(Enumerable.Range(0, Length).Select(i => (float)(i / 10.0)).ToArray());
        _table = DataTable.Create().Add("x", Column.Uniform(0, 1, Length)).Add("y", _y).Build();
    }

    public void Dispose()
    {
        _y.Dispose();
        _pool.Dispose();
    }

    /// <summary>
    /// The bottom axis's first and last labels, too wide for the room beyond the panel, end at F's left and right edges; the
    /// others stay centered on their ticks, and the ticks and the panel stay where they were (design §4.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The light theme without a title or a legend; x over [0, 100] with labels "value 000" (9 characters, 60.75 pixels
    ///   at 12 px); y over [0, 10].
    ///
    /// Steps:
    /// - Render; read the bottom labels' texts and the ticks.
    ///
    /// Expected:
    /// - The first label starts at F's left edge (10) and the last ends at its right edge (630); every other label's center
    ///   is its tick's place. Each tick is at the panel's left plus its pixel, as without the hold.
    /// </remarks>
    [Fact]
    public void BottomAxis_EndLabelsHeldInsideF()
    {
        using var plan = Plan();
        var list = plan.Render(Spec(Wide()));
        var g = plan.Guides;
        var width = 9 * FakeTextBackend.Advance(12);
        var labels = BottomTexts(list, g);
        Assert.True(labels.Length > 2);
        Assert.Equal(Padding, labels[0].Position.X, 3);
        Assert.Equal(Width - Padding, labels[^1].Position.X + width, 3);
        for (var i = 1; i < labels.Length - 1; i++)
        {
            var tick = (float)(Math.Floor(g.Left + g.X.Pixels[i]) + 0.5);
            Assert.Equal(tick, labels[i].Position.X + (width / 2), 3);
        }

        // The hold moves the labels only: the axis and its ticks lie on the panel as before.
        Assert.Equal(0, g.X.Pixels[0], 6);
        Assert.Equal(g.Width, g.X.Pixels[^1], 6);
    }

    /// <summary>
    /// Without a title, the left axis's top label at the panel's top crosses F by half a line and moves down until its top
    /// is F's top; the bottom label, over the bottom band, stays centered (design §4.6: F's consequence).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The light theme without a title; y over [0, 10], whose ticks reach the panel's top and bottom.
    ///
    /// Steps:
    /// - Render; read the left labels. Render with y reversed.
    ///
    /// Expected:
    /// - The top label's center is F's top (10) plus half a line height (7.5); the bottom label's center is its tick's
    ///   place. Reversed, the top label (now 0) is held the same way.
    /// </remarks>
    [Fact]
    public void LeftAxis_TopLabelHeldBelowFsTop()
    {
        using var plan = Plan();
        var list = plan.Render(Spec(Scale.Linear().WithDomain(0, 100)));
        var g = plan.Guides;
        var line = FakeTextBackend.LineHeight(12);
        var left = list.Texts.ToArray().Where(t => t.Position.X < g.Left && t.Style.Vertical == TextBaseline.Middle).ToArray();
        var top = left.MinBy(t => t.Position.Y);
        var bottom = left.MaxBy(t => t.Position.Y);
        Assert.Equal(Padding, g.Top);
        Assert.Equal(Padding + (line / 2), top.Position.Y, 3);
        Assert.Equal((float)(Math.Floor(g.Top + g.Height - g.Y.Pixels[0]) + 0.5), bottom.Position.Y, 3);

        // Reversed, the axis runs down from the panel's top: its low end (pixel 0) is at the top, and the room before it is
        // the title's band.
        list = plan.Render(Spec(Scale.Linear().WithDomain(0, 100)) with { ScaleY = Scale.Linear().WithDomain(0, 10) with { Reverse = true } });
        left = list.Texts.ToArray().Where(t => t.Position.X < plan.Guides.Left && t.Style.Vertical == TextBaseline.Middle).ToArray();
        Assert.Equal(Padding + (line / 2), left.Min(t => t.Position.Y), 3);
    }

    /// <summary>
    /// The same holds reversed and flipped (design §4.6): a reversed x has its first label at the right end, and a flipped
    /// plot's left axis is x; every label stays inside F.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The wide labels of x; x reversed; then the plot flipped with the same x on the left.
    ///
    /// Steps:
    /// - Render each; read the axes' labels.
    ///
    /// Expected:
    /// - Every label's box lies inside [10, 630] across and [10, 350] down; reversed, one ends at 630 and one starts at 10.
    /// </remarks>
    [Fact]
    public void ReversedAndFlipped_LabelsStayInsideF()
    {
        using var plan = Plan();
        var reversed = plan.Render(Spec(Wide() with { Reverse = true }));
        var width = 9 * FakeTextBackend.Advance(12);
        var labels = BottomTexts(reversed, plan.Guides);
        Assert.All(labels, t => Assert.InRange(t.Position.X, Padding - 1e-3, Width - Padding - width + 1e-3));
        Assert.Contains(labels, t => Math.Abs(t.Position.X - Padding) < 1e-3);
        Assert.Contains(labels, t => Math.Abs(t.Position.X + width - (Width - Padding)) < 1e-3);

        var flipped = plan.Render(Spec(Wide()) with { Coord = Coord.Cartesian(flip: true) });
        var line = FakeTextBackend.LineHeight(12);
        var g = plan.Guides;
        var side = flipped.Texts.ToArray().Where(t => t.Position.X < g.Left && t.Style.Vertical == TextBaseline.Middle).ToArray();
        Assert.NotEmpty(side);
        Assert.All(side, t => Assert.InRange(t.Position.Y, Padding + (line / 2) - 1e-3, Height - Padding - (line / 2) + 1e-3));
    }

    /// <summary>A polar plot's axes hold nothing: their room is unbounded, so each label stays on its tick (design §4.6).</summary>
    /// <remarks>
    /// Preconditions:
    /// - The wide labels of x, in a polar coordinate system.
    ///
    /// Steps:
    /// - Render; read both axes' room and label centers.
    ///
    /// Expected:
    /// - Infinite room before and after on both axes, and every label's center equal to its tick's pixel.
    /// </remarks>
    [Fact]
    public void Polar_HoldsNothing()
    {
        using var plan = Plan();
        _ = plan.Render(Spec(Wide()) with { Coord = Coord.Polar() });
        foreach (var axis in new[] { plan.Guides.X, plan.Guides.Y })
        {
            Assert.Equal((double.PositiveInfinity, double.PositiveInfinity), (axis.Before, axis.After));
            for (var i = 0; i < axis.Count; i++)
            {
                Assert.Equal(axis.Pixels[i], axis.LabelCenter(i));
            }
        }
    }

    /// <summary>
    /// A box is held inside [−before, length + after]: moved by its excess at either end, kept where it fits, and started at
    /// the low end when it is wider than the range (design §4.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An axis 100 long with 5 pixels of room at each end.
    ///
    /// Steps:
    /// - Hold boxes 20 wide at 0, 50, and 100, and one 200 wide at 50 and at 100 (whose low end fits, its high end not).
    ///
    /// Expected:
    /// - 5, 50, 95; and −5 + 100 = 95 both times: the wide box starts at the low end whichever end it crosses.
    /// </remarks>
    [Fact]
    public void Hold_MovesByTheExcess()
    {
        var axis = new TickAxis
        {
            Length = 100,
            Before = 5,
            After = 5,
        };
        Assert.Equal((5.0, 50.0, 95.0), (axis.Hold(0, 20), axis.Hold(50, 20), axis.Hold(100, 20)));
        Assert.Equal((95.0, 95.0), (axis.Hold(50, 200), axis.Hold(100, 200)));
    }

    /// <summary>
    /// The overlap value is measured between the boxes as held (design §4.6): two labels apart at their ticks overlap once the
    /// first moves inside F toward the second.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A horizontal axis 100 long, no room before, and two labels of 4 characters (27 pixels) at the pixels 0 and 30.
    ///
    /// Steps:
    /// - Measure the overlap value; then with infinite room.
    ///
    /// Expected:
    /// - −∞ held (the first moves to 13.5, 16.5 from the second); 2 − 1.5 em / 3 unheld (3 pixels apart).
    /// </remarks>
    [Fact]
    public void Overlap_MeasuredBetweenHeldBoxes()
    {
        var axis = new TickAxis
        {
            Length = 100,
            Before = 0,
            After = 1000,
            Horizontal = true,
            Em = 12,
            Style = new TextStyle(Colors.Black, 12),
            Text = new TextEngine(new FakeTextBackend()),
        };
        axis.AddLabel("aaaa", 0, 0);
        axis.AddLabel("bbbb", 30, 0);
        Assert.Equal(double.NegativeInfinity, axis.Measure());
        axis.Before = double.PositiveInfinity;
        Assert.Equal(2 - (1.5 * 12 / 3), axis.Measure(), 9);
    }

    /// <summary>Once warm, a plot whose end labels are held drawn again allocates nothing (design §4.6).</summary>
    /// <remarks>
    /// Preconditions:
    /// - The wide labels of x, drawn twice.
    ///
    /// Steps:
    /// - Render five times.
    ///
    /// Expected:
    /// - 0 bytes on this thread.
    /// </remarks>
    [Fact]
    public void Held_Warm_AllocatesNothing()
    {
        using var plan = Plan();
        var spec = Spec(Wide());
        _ = plan.Render(spec);
        _ = plan.Render(spec);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 5; i++)
        {
            _ = plan.Render(spec);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static LinearScaleSpec Wide() =>
        Scale.Linear().WithDomain(0, 100) with
        {
            TickFormat = static (value, destination, out written) =>
                $"value {value:000}".AsSpan().TryCopyTo(destination) ? (written = 9) > 0 : (written = 0) > 0,
        };

    private static TextRecord[] BottomTexts(RenderList list, PlotGuides g) =>
        list
            .Texts.ToArray()
            .Where(t => t.Position.Y >= g.Top + g.Height && t.Text.StartsWith("value", StringComparison.Ordinal))
            .OrderBy(t => t.Position.X)
            .ToArray();

    private PlotPlan Plan() => new(_pool, Width, Height, new TextEngine(new FakeTextBackend()), 1);

    private PlotSpec Spec(LinearScaleSpec x) =>
        Plot.Create(_table)
            .Line(_table.Col<double>("x"), _table.Col<float>("y"))
            .ScaleX(x)
            .ScaleY(Scale.Linear().WithDomain(0, 10))
            .Theme(Theme.Light)
            .Build();
}
