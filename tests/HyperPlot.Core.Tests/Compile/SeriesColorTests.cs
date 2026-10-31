using System.Numerics;
using HyperPlot.Rendering;
using static HyperPlot.Tests.GeomTesting;

namespace HyperPlot.Tests;

/// <summary>The series colors a plot takes from its theme's palette, row G7a (design §4.1, §4.7, §4.8, §10 G7a row).</summary>
public sealed class SeriesColorTests
{
    private static readonly Color First = new(10, 120, 200, 255);
    private static readonly Color Second = new(220, 90, 10, 255);
    private static readonly Color Red = new(200, 0, 0, 255);

    /// <summary>
    /// A layer whose spec gives no color takes the palette's color at its position, around the palette: a line and an
    /// outline opaque, an area's and a band's fill at alpha 128, bars and rects opaque. A layer given a color keeps it and
    /// does not move the others' (design §4.1).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A 100 × 100 target without guides, domains [0, 100], the palette (First, Second). Layers: a line (0), an area
    ///   with an outline of no color (1), bars (2), a line in red (3), a band (4), none given a color but layer 3. Another
    ///   spec of two rect layers, two rects each.
    ///
    /// Steps:
    /// - Render; render the rects; render the first again with layer 0 given red.
    ///
    /// Expected:
    /// - Layer 0 First; layer 1's fill Second at alpha 128 and its outline Second; layer 2's bars First, opaque (2 mod 2);
    ///   layer 3 red; layer 4's fill First at alpha 128. The rects First, then Second, opaque. With layer 0 red, layer 1
    ///   still Second.
    /// </remarks>
    [Fact]
    public void Defaults_TakeThePaletteByPosition()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = Data.Create();
        var t = data.Table;
        var (x, y, lo, hi) = (t.Col<double>("x"), t.Col<double>("y"), t.Col<double>("lo"), t.Col<double>("hi"));
        var spec = Spec(
            t,
            new LineLayerSpec { X = x, Y = y },
            new AreaLayerSpec
            {
                X = x,
                Y = y,
                Outline = new StrokeSpec(null, 2),
            },
            new BarLayerSpec { X = x, Y = y },
            new LineLayerSpec
            {
                X = x,
                Y = y,
                Style = new StrokeSpec(Red),
            },
            new BandLayerSpec
            {
                X = x,
                YMin = lo,
                YMax = hi,
            }
        ) with
        {
            Theme = TestPlans.Bare with { Palette = new Palette(First, Second) },
        };
        using var plan = TestPlans.Create(pool, Size, Size);

        var list = plan.Render(spec);
        var lines = list.Polylines.ToArray().Select(p => p.Style.Color).ToArray();
        Assert.Equal([First, Second, Red], lines);
        var columns = list.Columns.ToArray().Select(c => c.Color).Distinct().ToArray();
        Assert.Equal([Premultiplied(Second with { A = 128 }), Premultiplied(First with { A = 128 })], columns);
        Assert.All(list.Rects[1..].ToArray(), r => Assert.Equal(Premultiplied(First), r.Color));

        // Rects: the second layer's are Second, opaque.
        var few = data.Few;
        var rect = new RectLayerSpec
        {
            XMin = few.Col<double>("x"),
            XMax = few.Col<double>("x2"),
            YMin = few.Col<double>("y"),
            YMax = few.Col<double>("y2"),
        };
        var rectList = plan.Render(Spec(few, rect, rect) with { Theme = spec.Theme });
        Assert.Equal(
            [Premultiplied(First), Premultiplied(First), Premultiplied(Second), Premultiplied(Second)],
            rectList.Rects[1..].ToArray().Select(r => r.Color)
        );

        var given = spec with { Layers = [((LineLayerSpec)spec.Layers[0]) with { Style = new StrokeSpec(Red) }, .. spec.Layers.Skip(1)] };
        Assert.Equal([Red, Second, Red], plan.Render(given).Polylines.ToArray().Select(p => p.Style.Color).ToArray());
    }

    /// <summary>
    /// The legend draws each sample in the color its layer is drawn in, and a user's geom reads its layer's series color
    /// from its frame (design §4.1, §4.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A 300 × 200 target with the light theme without axes or grid, the palette (First, Second); a line labeled "L" (0),
    ///   an area labeled "A" (1), and a user's geom labeled "C" (2), none given a color.
    ///
    /// Steps:
    /// - Render; read the legend's samples and what the geom read.
    ///
    /// Expected:
    /// - The geom read First (2 mod 2). The area's square in Second at alpha 128; the samples' lines First (the line) and
    ///   First 1 wide (the user's geom).
    /// </remarks>
    [Fact]
    public void Legend_AndUserGeom_TakeTheSeriesColor()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = Data.Create();
        var (x, y) = (data.Table.Col<double>("x"), data.Table.Col<double>("y"));
        var geom = new ColorGeom();
        var spec = Plot.Create(data.Table)
            .Line(x, y, l => l.Label("L"))
            .Area(x, y, configure: a => a.Label("A"))
            .Custom(geom, "C")
            .Theme(Theme.Light with { ShowAxes = false, ShowGrid = false, Palette = new Palette(First, Second) })
            .Build();
        using var plan = TestPlans.Create(pool, 300, 200);

        var list = plan.Render(spec);

        Assert.Equal(First, geom.Read);
        Assert.Equal(Premultiplied(Second with { A = 128 }), list.Rects[^1].Color);
        var samples = list.Polylines[^2..].ToArray().Select(p => p.Style).ToArray();
        Assert.Equal([new LineStyle(First, 1), new LineStyle(First, 1)], samples);
    }

    /// <summary>
    /// A change of the palette alone lays out and records again without extracting; the same palette in a new theme does
    /// nothing. A null palette set with <c>with</c> throws when drawn (design §4.7, §4.8).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A line over 800 points with learned domains, drawn once with the default palette.
    ///
    /// Steps:
    /// - Render with Set1; with a new theme holding a new Set1 of the same colors; with a null palette.
    ///
    /// Expected:
    /// - (layouts, extractions, recordings) changes (1, 0, 1), then (0, 0, 0); the line's color Set1's first. The null palette
    ///   throws ArgumentNullException naming the Palette.
    /// </remarks>
    [Fact]
    public void Palette_DiffAndCheck()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = Data.Create();
        var spec = Plot.Create(data.Table).Line(data.Table.Col<double>("x"), data.Table.Col<double>("y")).Theme(TestPlans.Bare).Build();
        using var plan = TestPlans.Create(pool, Size, Size);
        _ = plan.Render(spec);

        var set1 = spec with { Theme = TestPlans.Bare with { Palette = Palettes.Set1 } };
        Assert.Equal((1, 0, 1), Counts(set1));
        Assert.Equal(Palettes.Set1[0], plan.Render(set1).Polylines[0].Style.Color);
        var copy = new Palette(Enumerable.Range(0, Palettes.Set1.Count).Select(i => Palettes.Set1[i]).ToArray());
        Assert.Equal((0, 0, 0), Counts(spec with { Theme = TestPlans.Bare with { Palette = copy } }));

        var error = Assert.Throws<ArgumentNullException>(() => plan.Render(spec with { Theme = TestPlans.Bare with { Palette = null! } }));
        Assert.Contains("Palette", error.Message, StringComparison.Ordinal);

        (int, int, int) Counts(PlotSpec next)
        {
            var before = (plan.Layouts, plan.Extractions, plan.Recordings);
            _ = plan.Render(next);
            return (plan.Layouts - before.Layouts, plan.Extractions - before.Extractions, plan.Recordings - before.Recordings);
        }
    }

    /// <summary>Once warm, a spec whose layers give no color draws again without allocating (design §4.8).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A 640 × 360 target with the light theme; a line, an area with an outline of no color, bars, and a band, each
    ///   labeled, none given a color; drawn once.
    ///
    /// Steps:
    /// - Render it five times.
    ///
    /// Expected:
    /// - 0 bytes on this thread.
    /// </remarks>
    [Fact]
    public void Defaults_Warm_AllocateNothing()
    {
        using var pool = new PlotWorkerPool(1);
        using var data = Data.Create();
        var t = data.Table;
        var (x, y, lo, hi) = (t.Col<double>("x"), t.Col<double>("y"), t.Col<double>("lo"), t.Col<double>("hi"));
        var spec = Plot.Create(t)
            .Line(x, y, l => l.Label("line"))
            .Area(x, y, configure: a => a.Outline(new StrokeSpec(null)).Label("area"))
            .Bar(x, y, configure: b => b.Label("bars"))
            .Band(x, lo, hi, b => b.Label("band"))
            .Build();
        using var plan = new PlotPlan(pool, 640, 360, new TextEngine(new FakeTextBackend()), 1);
        _ = plan.Render(spec);
        _ = plan.Render(spec);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 5; i++)
        {
            _ = plan.Render(spec);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static Vector4 Premultiplied(Color color)
    {
        var probe = new RenderList();
        probe.DrawRect(0, 0, 1, 1, color);
        return probe.Rects[0].Color;
    }

    /// <summary>The tables: 800 points, and two rects.</summary>
    private sealed class Data : IDisposable
    {
        private readonly IDisposable[] _columns;

        private Data(IDisposable[] columns, DataTable table, DataTable few) => (_columns, Table, Few) = (columns, table, few);

        public DataTable Table { get; }

        public DataTable Few { get; }

        public static Data Create()
        {
            var y = Column.Wrap(Enumerable.Range(0, 800).Select(i => 50 + (40 * Math.Sin(i * 0.05))).ToArray());
            var lo = Column.Wrap(Enumerable.Range(0, 800).Select(i => 30 + (10 * Math.Sin(i * 0.07))).ToArray());
            var hi = Column.Wrap(Enumerable.Range(0, 800).Select(i => 70 + (10 * Math.Cos(i * 0.05))).ToArray());
            var fx = Column.Wrap(new double[] { 10, 60 });
            var fx2 = Column.Wrap(new double[] { 30, 80 });
            var fy = Column.Wrap(new double[] { 10, 40 });
            var fy2 = Column.Wrap(new double[] { 20, 70 });
            var table = DataTable.Create().Add("x", Column.Uniform(0, 0.125, 800)).Add("y", y).Add("lo", lo).Add("hi", hi).Build();
            var few = DataTable.Create().Add("x", fx).Add("x2", fx2).Add("y", fy).Add("y2", fy2).Build();
            return new Data([y, lo, hi, fx, fx2, fy, fy2], table, few);
        }

        public void Dispose()
        {
            foreach (var column in _columns)
            {
                column.Dispose();
            }
        }
    }

    /// <summary>A geom that reads its series color and draws nothing.</summary>
    private sealed class ColorGeom : IGeom
    {
        public Color Read { get; private set; }

        public void Learn(GeomRanges ranges) { }

        public void Record(GeomFrame frame, RenderList list) => Read = frame.SeriesColor;
    }
}
