using System.Numerics;
using HyperPlot.Interaction;
using HyperPlot.Rendering;
using HyperPlot.Testing;

namespace HyperPlot.Tests;

/// <summary>
/// The measurement (design §3.3 "バケットの和", §6.5 "計測", §10, row I4b, Issue #390): the statistics against a brute force, the
/// differences, the tool, the notices, what it draws, and its allocations.
/// </summary>
public sealed class MeasureTests
{
    private const int Chunk = 1 << 20;
    private const int Length = (2 * Chunk) + (Chunk / 2);
    private static readonly PanelId Panel = new(0, 0);

    /// <summary>
    /// The statistics equal a brute force over the rows of the x range (Issue #390 Decisions 3 and 4): across chunks and
    /// buckets of sums, inside one bucket, on a bucket exactly, on one row and on none; over a stored x; for a float y with
    /// NaN and a double y.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 2.5 chunks of rows, x the row (uniform); a float y of a seeded walk with every 97th value NaN, a double y of the row's
    ///   square root; a second plot over a stored x of twice the row.
    ///
    /// Steps:
    /// - Ask the statistics of [1,000.5, 2,500,000.25], [100, 200], [4,096, 8,191], [5, 5], [5.2, 5.3] of x; and of [10, 50]
    ///   on the stored x; with a span of 0 and of 1; a reversed range.
    ///
    /// Expected:
    /// - Two series each, layers 0 and 1: count exact, mean within 1e-12 relative, least and greatest equal; [5.2, 5.3] count 0
    ///   and NaN. The stored x: the rows of x in [10, 50]. A span of 0 returns 2 and writes none; of 1 writes the first. A
    ///   reversed range throws ArgumentException.
    /// </remarks>
    [Fact]
    public void Statistics_EqualTheBruteForce()
    {
        var floats = Walk();
        var doubles = Enumerable.Range(0, Length).Select(i => Math.Sqrt(i)).ToArray();
        using var y = Column.Wrap(floats);
        using var y2 = Column.Wrap(doubles);
        var table = DataTable.Create().Add("x", Column.Uniform(0, 1, Length)).Add("y", y).Add("y2", y2).Build();
        using var pool = new PlotWorkerPool(2);
        using var engine = Engine(
            pool,
            Plot.Create(table).Line(table.Col<double>("x"), table.Col<float>("y")).Line(table.Col<double>("x"), table.Col<double>("y2")).Build()
        );

        foreach (var (low, high) in ((double, double)[])[(1000.5, 2_500_000.25), (100, 200), (4096, 8191), (5, 5), (5.2, 5.3)])
        {
            var results = new SeriesStatistics[4];
            Assert.Equal(2, engine.Statistics(Panel, ViewRange.FromValues(low, high), results));
            var (first, last) = ((int)Math.Ceiling(low), (int)Math.Floor(high));
            Check(results[0], 0, floats.Skip(first).Take(Math.Max(0, last - first + 1)).Where(v => !float.IsNaN(v)).Select(v => (double)v).ToArray());
            Check(results[1], 1, doubles.Skip(first).Take(Math.Max(0, last - first + 1)).ToArray());
        }

        Assert.Equal(2, engine.Statistics(Panel, ViewRange.FromValues(100.0, 200.0), []));
        var one = new SeriesStatistics[1];
        Assert.Equal(2, engine.Statistics(Panel, ViewRange.FromValues(100.0, 200.0), one));
        Assert.Equal(0, one[0].Layer);
        _ = Assert.Throws<ArgumentException>(() => engine.Statistics(Panel, new ViewRange(0, 10, 5), one));

        using var stored = Column.Wrap(Enumerable.Range(0, 1000).Select(i => 2.0 * i).ToArray());
        using var walk = Column.Wrap(floats[..1000]);
        var storedTable = DataTable.Create().Add("x", stored).Add("y", walk).Build();
        engine.SetPlot(Plot.Create(storedTable).Line(storedTable.Col<double>("x"), storedTable.Col<float>("y")).Build());
        engine.RenderFrame();
        var storedResults = new SeriesStatistics[1];
        Assert.Equal(1, engine.Statistics(Panel, ViewRange.FromValues(10.0, 50.0), storedResults));
        Check(storedResults[0], 0, floats[5..26].Where(v => !float.IsNaN(v)).Select(v => (double)v).ToArray());

        static void Check(in SeriesStatistics actual, int layer, double[] values)
        {
            Assert.Equal((layer, 0, (long)values.Length, false), (actual.Layer, actual.Series, actual.Count, actual.Provisional));
            if (values.Length == 0)
            {
                Assert.True(double.IsNaN(actual.Mean) && double.IsNaN(actual.Min) && double.IsNaN(actual.Max));
                return;
            }

            var mean = values.Sum() / values.Length;
            Assert.True(Math.Abs(actual.Mean - mean) <= 1e-12 * Math.Max(1, Math.Abs(mean)), $"{actual.Mean} against {mean}");
            Assert.Equal((values.Min(), values.Max()), (actual.Min, actual.Max));
        }
    }

    /// <summary>
    /// Each kind of layer answers as Decisions 2, 3, and 15 say: a split layer by category, a facet's panel its own rows, a
    /// band its two edges, an area its value line, a stat and a stack from their derived tables; bars and rules none.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 1,000 rows, x the row, y = row % 10, a category g alternating a and b, a facet column of the first and second halves.
    ///
    /// Steps:
    /// - Ask [100, 199] of: a line split by g; a band from 0 to y; an area of y; a smooth of y; areas of y stacked by g; bars
    ///   and horizontal rules. Ask [100, 199] and [600, 699] of a facet's panel 1 (the second half).
    ///
    /// Expected:
    /// - Split: 2 series, 50 rows each, a's mean of the even rows' y. Band: 2 series, low 0 and high y's. Area: y's. Smooth: one
    ///   series, its count the derived rows in the range, and a mean within y's range. Stacked: 2 series, the second's least
    ///   and greatest are sums. Bars and rule: 0. The facet: [100, 199] none, [600, 699] 100 rows.
    /// </remarks>
    [Fact]
    public void Layers_AnswerByTheirKind()
    {
        var values = Enumerable.Range(0, 1000).Select(i => (double)(i % 10)).ToArray();
        using var y = Column.Wrap(values);
        using var zero = Column.Wrap(new double[1000]);
        var table = DataTable
            .Create()
            .Add("x", Column.Uniform(0, 1, 1000))
            .Add("y", y)
            .Add("zero", zero)
            .Add("g", Column.Categorical([.. Enumerable.Range(0, 1000).Select(i => i % 2 == 0 ? "a" : "b")], ["a", "b"]))
            .Add("half", Column.Categorical([.. Enumerable.Range(0, 1000).Select(i => i < 500 ? "first" : "second")], ["first", "second"]))
            .Build();
        var (x, ty, z) = (table.Col<double>("x"), table.Col<double>("y"), table.Col<double>("zero"));
        using var pool = new PlotWorkerPool(2);
        var range = ViewRange.FromValues(100.0, 199.0);
        SeriesStatistics[] Ask(PlotSpec spec, ViewRange of, int panel = 0)
        {
            using var engine = Engine(pool, spec);
            var results = new SeriesStatistics[8];
            return results[..engine.Statistics(new PanelId(0, panel), of, results)];
        }

        var split = Ask(Plot.Create(table).Line(x, ty, l => l.ColorBy(table.Cat("g"))).Build(), range);
        Assert.Equal(2, split.Length);
        Assert.All(split, s => Assert.Equal(50, s.Count));
        Assert.Equal(values.Where((_, i) => i is >= 100 and <= 199 && i % 2 == 0).Average(), split[0].Mean, 12);

        var band = Ask(Plot.Create(table).Band(x, z, ty).Build(), range);
        Assert.Equal([(0, 0.0, 0.0), (1, 0.0, 9.0)], band.Select(s => (s.Series, s.Min, s.Max)));

        var area = Assert.Single(Ask(Plot.Create(table).Area(x, ty).Build(), range));
        Assert.Equal((100L, 4.5, 0.0, 9.0), (area.Count, area.Mean, area.Min, area.Max));

        var smooth = Assert.Single(Ask(Plot.Create(table).Smooth(x, ty, 9).Build(), range));
        Assert.True(smooth.Count > 0 && smooth.Mean is >= 0 and <= 9);

        var stacked = Ask(Plot.Create(table).Area(x, ty, configure: a => a.ColorBy(table.Cat("g")).Position(Position.Stack)).Build(), range);
        Assert.True(stacked.Length >= 2);
        Assert.True(stacked.Max(s => s.Max) > 9);

        Assert.Empty(Ask(Plot.Create(table).Bar(x, ty).Build(), range));
        Assert.Empty(Ask(Plot.Create(table).HRule(x).Build(), range));

        var facet = Plot.Create(table).Line(x, ty).FacetWrap(table.Cat("half")).Build();
        Assert.Equal(0, Assert.Single(Ask(facet, range, panel: 1)).Count);
        Assert.Equal(100, Assert.Single(Ask(facet, ViewRange.FromValues(600.0, 699.0), panel: 1)).Count);
    }

    /// <summary>
    /// A range over a chunk whose pyramid is not built is answered without it and provisional, and the frame after it is
    /// built finds the statistics again and tells them (Issue #390 Decisions 9, 10). The box writes a count of a million rows
    /// and more as an integer (Issue #397).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread; 2 chunks of rows, x the row, y 1 everywhere, chunk 1 held on the worker; a frame not waiting.
    ///
    /// Steps:
    /// - Draw until the worker reads chunk 1; measure the range [0, 2 chunks) by code. Release chunk 1; draw until a frame is
    ///   not provisional.
    ///
    /// Expected:
    /// - Held: provisional, the count at most the first chunk's. Released: the measurement's statistics not provisional, the
    ///   count 2 chunks, and a notice for the change; the box's text holds "n=2097152,", not the exponent form of a
    ///   general number format ("n=2.09715E+06").
    /// </remarks>
    [Fact]
    public void Provisional_IsFoundAgainOnceBuilt()
    {
        using var pool = new PlotWorkerPool(1);
        using var y = TestStorage<float>.MakeColumn([.. Enumerable.Repeat(1f, 2 * Chunk)], out var storage);
        storage.Hold(1);
        var table = DataTable.Create().Add("x", Column.Uniform(0, 1, 2 * Chunk)).Add("y", y).Build();
        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, 640, 360, 1, pool);
        engine.SetPlot(Plot.Create(table).Line(table.Col<double>("x"), table.Col<float>("y")).Build());
        engine.RenderFrame();
        storage.WaitForWorker(1);
        engine.RenderFrame();

        var told = 0;
        engine.MeasurementChanged += () => told++;
        engine.Measure(Measurement2D.OfRange(Panel, ViewRange.FromValues(0.0, (2.0 * Chunk) - 1)));
        var held = Assert.IsType<Measurement2D>(engine.Measurement).Statistics.Span[0];
        Assert.True(held.Provisional && held.Count <= Chunk);

        storage.Release(1);
        Eventually.True(
            () =>
            {
                engine.RenderFrame();
                return !engine.IsProvisional;
            },
            "a frame that is not provisional"
        );
        engine.RenderFrame();
        var done = Assert.IsType<Measurement2D>(engine.Measurement).Statistics.Span[0];
        Assert.Equal((false, 2L * Chunk), (done.Provisional, done.Count));
        Assert.True(told >= 2);
        Assert.Contains("n=2097152,", Assert.Single(backend.LastList!.Texts.ToArray()).Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The differences are in data units (Issue #390 Decision 5): a log axis's of the data values, a time axis's Δx in its
    /// unit and its slope a y unit a second; Δx of 0 makes the slope NaN.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The fixture's line; the line over a log x; the line over a time x in milliseconds.
    ///
    /// Steps:
    /// - Measure points from (100, −5) to (300, 5) of the scale space; on the log x from log10 10 to log10 100; on the time x
    ///   from 0 to 2,000 ms with y from 0 to 4; from (100, 0) to (100, 1).
    ///
    /// Expected:
    /// - Δx 200, Δy 10, slope 0.05. Log: Δx 90. Time: Δx 2,000, slope 2 a second. Vertical: slope NaN.
    /// </remarks>
    [Fact]
    public void Deltas_AreInDataUnits()
    {
        using var f = new EngineFixture();
        (ViewRange, ViewRange) P(double x, double y) => (ViewRange.FromValues(x, x), ViewRange.FromValues(y, y));
        f.Engine.Measure(Measurement2D.OfPoints(Panel, P(100, -5), P(300, 5)));
        var points = Assert.IsType<Measurement2D>(f.Engine.Measurement);
        Assert.Equal((200.0, 10.0, 0.05), (points.DeltaX, points.DeltaY, Math.Round(points.Slope, 12)));

        var (x, y) = (f.Table.Col<double>("x"), f.Table.Col<float>("y"));
        f.Engine.SetPlot(Plot.Create(f.Table).Line(x, y).ScaleX(Scale.Log().WithDomain(1, 1000)).Build());
        f.Engine.RenderFrame();
        f.Engine.Measure(Measurement2D.OfPoints(Panel, P(1, 0), P(2, 0)));
        Assert.Equal(90.0, ((Measurement2D)f.Engine.Measurement!).DeltaX, 9);

        using var times = Column.Wrap(Enumerable.Range(0, 1000).Select(i => i * 100L).ToArray());
        var timed = DataTable.Create().Add("t", times).Add("y", y.Column).Build();
        f.Engine.SetPlot(Plot.Create(timed).Line(timed.Col<long>("t"), timed.Col<float>("y")).ScaleX(Scale.Time(TimeUnit.Milliseconds)).Build());
        f.Engine.RenderFrame();
        f.Engine.Measure(
            Measurement2D.OfPoints(
                Panel,
                (ViewRange.FromValues(0L, 0L), ViewRange.FromValues(0.0, 0.0)),
                (ViewRange.FromValues(2000L, 2000L), ViewRange.FromValues(4.0, 4.0))
            )
        );
        var time = (Measurement2D)f.Engine.Measurement!;
        Assert.Equal((2000.0, 2.0), (time.DeltaX, time.Slope));

        f.Engine.SetPlot(f.Spec);
        f.Engine.RenderFrame();
        f.Engine.Measure(Measurement2D.OfPoints(Panel, P(100, 0), P(100, 1)));
        Assert.True(double.IsNaN(((Measurement2D)f.Engine.Measurement!).Slope));
    }

    /// <summary>
    /// The tool measures as it drags and keeps the measurement after the release; it is not in the default bindings, and a
    /// polar panel is not measured (Issue #390 Decisions 1, 6, 9).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The fixture's line; a MeasureTool of points bound to a left drag with Alt, one of a range to a right drag with Alt.
    ///
    /// Steps:
    /// - Read the default bindings. Drag with Alt from a quarter to three quarters of the panel, counting notices; drag the
    ///   range likewise. Set the line in polar coordinates and drag with Alt.
    ///
    /// Expected:
    /// - No MeasureTool by default. Points: notices at each move, the measurement's From and To at the drag's ends after the
    ///   release. Range: X from the first x to the second. Polar: the measurement unchanged.
    /// </remarks>
    [Fact]
    public void Tool_MeasuresAsItDrags()
    {
        using var f = new EngineFixture();
        Assert.DoesNotContain(f.Engine.Bindings, b => b.Tool is MeasureTool);
        f.Engine.Bindings.Insert(
            0,
            new InputBinding(InputGesture.Drag(PointerButtons.Left), ModifierKeys.Alt, HitAreaKind.Panel, new MeasureTool(MeasureKind.Points))
        );
        f.Engine.Bindings.Insert(
            0,
            new InputBinding(InputGesture.Drag(PointerButtons.Right), ModifierKeys.Alt, HitAreaKind.Panel, new MeasureTool(MeasureKind.Range))
        );
        var told = 0;
        f.Engine.MeasurementChanged += () => told++;
        var (left, top, width, height) = f.Rect;
        var (from, to) = (new Vector2(left + (width / 4f), top + (height / 2f)), new Vector2(left + (width * 3 / 4f), top + (height / 4f)));
        f.Drag(from, to, modifiers: ModifierKeys.Alt);
        var points = Assert.IsType<Measurement2D>(f.Engine.Measurement);
        Assert.True(told >= 4);
        var context = new ToolContext(f.Engine);
        Assert.Equal((MeasureKind.Points, context.PixelToView(Panel, from), context.PixelToView(Panel, to)), (points.Kind, points.From, points.To));

        f.Drag(from, to, PointerButtons.Right, ModifierKeys.Alt);
        var range = Assert.IsType<Measurement2D>(f.Engine.Measurement);
        Assert.Equal(
            (MeasureKind.Range, context.PixelToView(Panel, from).X.LowValue, context.PixelToView(Panel, to).X.LowValue),
            (range.Kind, range.X.LowValue, range.X.HighValue)
        );

        f.Engine.SetPlot(f.Spec with { Coord = Coord.Polar() });
        f.Engine.RenderFrame();
        var before = f.Engine.Measurement;
        f.Drag(f.Middle, f.Middle + new Vector2(40, 20), modifiers: ModifierKeys.Alt);
        Assert.Same(before, f.Engine.Measurement);
    }

    /// <summary>
    /// The measurement stays through a zoom, goes with Escape's third entry after the selection, and with a new mapping; a new
    /// one replaces it; <see cref="PlotEngine.Measure"/> checks its measurement and <see cref="PlotEngine.ClearMeasurement"/>
    /// tells once (Issue #390 Decisions 8, 9).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The fixture's line; animations off.
    ///
    /// Steps:
    /// - Measure a range; zoom by a wheel; select a rectangle; press Escape twice. Measure again; set the spec with y over
    ///   [−50, 50] and draw. Measure again, then points. Call Measure with null, a panel not drawn, a NaN point, a reversed
    ///   range, a polar panel. Measure again on the line, and call ClearMeasurement twice.
    ///
    /// Expected:
    /// - The zoom keeps the same object. The first Escape clears the selection only, the second the measurement. The new
    ///   mapping clears it. The points replace the range. ArgumentNullException, ArgumentOutOfRangeException, and
    ///   ArgumentException for the three others; the first ClearMeasurement tells once, the second not.
    /// </remarks>
    [Fact]
    public void Measurement_StaysUntilItGoes()
    {
        using var f = new EngineFixture();
        f.Engine.Options.ZoomAnimation = TimeSpan.Zero;
        var range = Measurement2D.OfRange(Panel, ViewRange.FromValues(100.0, 300.0));
        f.Engine.Measure(range);
        var kept = f.Engine.Measurement;
        Assert.True(f.Wheel(f.Middle, 1));
        f.Engine.RenderFrame();
        Assert.Same(kept, f.Engine.Measurement);

        f.Engine.Select(Selection2D.Rectangle(Panel, ViewRange.FromValues(200.0, 400.0), ViewRange.FromValues(-5.0, 5.0)));
        Assert.True(f.Key(PlotKey.Escape));
        Assert.Null(f.Engine.Selection(0));
        Assert.Same(kept, f.Engine.Measurement);
        Assert.True(f.Key(PlotKey.Escape));
        Assert.Null(f.Engine.Measurement);

        f.Engine.Measure(range);
        f.Engine.SetPlot(f.Spec with { ScaleY = Scale.Linear().WithDomain(-50, 50) });
        f.Engine.RenderFrame();
        Assert.Null(f.Engine.Measurement);

        f.Engine.Measure(range);
        f.Engine.Measure(
            Measurement2D.OfPoints(
                Panel,
                (ViewRange.FromValues(1.0, 1.0), ViewRange.FromValues(0.0, 0.0)),
                (ViewRange.FromValues(2.0, 2.0), ViewRange.FromValues(1.0, 1.0))
            )
        );
        Assert.Equal(MeasureKind.Points, f.Engine.Measurement!.Kind);

        _ = Assert.Throws<ArgumentNullException>(() => f.Engine.Measure(null!));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
            f.Engine.Measure(Measurement2D.OfRange(new PanelId(0, 3), ViewRange.FromValues(1.0, 2.0)))
        );
        var nan = ViewRange.FromValues(double.NaN, double.NaN);
        _ = Assert.Throws<ArgumentException>(() => f.Engine.Measure(Measurement2D.OfPoints(Panel, (nan, nan), (nan, nan))));
        _ = Assert.Throws<ArgumentException>(() => f.Engine.Measure(Measurement2D.OfRange(Panel, new ViewRange(0, 5, 1))));
        f.Engine.SetPlot(f.Spec with { Coord = Coord.Polar() });
        f.Engine.RenderFrame();
        _ = Assert.Throws<ArgumentException>(() => f.Engine.Measure(range));

        f.Engine.SetPlot(f.Spec);
        f.Engine.RenderFrame();
        f.Engine.Measure(range);
        var told = 0;
        f.Engine.MeasurementChanged += () => told++;
        f.Engine.ClearMeasurement();
        f.Engine.ClearMeasurement();
        Assert.Equal(1, told);
        Assert.Null(f.Engine.Measurement);
    }

    /// <summary>
    /// What a measurement draws (Issue #390 Decisions 6 and 11): points as a line and two markers, a range as its fill across
    /// the panel, the box's default lines, "… (n more)" past the room, a format's text, a format's false, a format's length
    /// out of its span.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The fixture's line; then 60 lines split by a category of 60 values over the same x.
    ///
    /// Steps:
    /// - Measure points (100, −5) to (300, 5) by code; draw; zoom by a wheel and draw. Measure points from category 1 to 3 over
    ///   bars of a band x; draw. Measure the range [100, 300]; draw. Measure the range over the 60
    ///   series; draw. With a MeasureTool whose Format writes "custom", drag; whose Format returns false; whose Format writes
    ///   2,000 characters.
    ///
    /// Expected:
    /// - Points: a polyline of one segment between the points' pixels, 4 sector calls, the box's text "Δx: 200\nΔy: 10\nslope:
    ///   0.05"; after the zoom the box's rect starts 12 pixels from the end's new pixel. The band: the text starts with
    ///   "Δx: 2". Range: a rect of 15 % of the foreground from x 100's pixel to x 300's, the panel's height; one text that starts
    ///   with "line: n=201". 60 series: the text ends with " more)". The format: "custom"; false: no text; 2,000:
    ///   InvalidOperationException from RenderFrame.
    /// </remarks>
    [Fact]
    public void Draw_TheLineTheRangeAndTheBox()
    {
        using var f = new EngineFixture();
        (ViewRange, ViewRange) P(double x, double y) => (ViewRange.FromValues(x, x), ViewRange.FromValues(y, y));
        f.Engine.Measure(Measurement2D.OfPoints(Panel, P(100, -5), P(300, 5)));
        f.Engine.RenderFrame();
        var list = f.Backend.LastList!;
        var (a, b) = (f.Engine.ViewToPixel(Panel, P(100, -5).Item1, P(100, -5).Item2), f.Engine.ViewToPixel(Panel, P(300, 5).Item1, P(300, 5).Item2));
        var segment = list.Segments[list.Polylines[0].SegmentStart];
        Assert.True(Vector2.Distance(segment.Start, a) < 0.75f && Vector2.Distance(segment.End, b) < 0.75f);
        Assert.Equal(4, list.SectorCalls.Length);
        Assert.Equal("Δx: 200\nΔy: 10\nslope: 0.05", Assert.Single(list.Texts.ToArray()).Text);

        // The box follows the end through a zoom (PR #395's review): its rect starts 12 pixels from the end's new pixel.
        f.Engine.Options.ZoomAnimation = TimeSpan.Zero;
        Assert.True(f.Wheel(f.Middle, 1));
        f.Engine.RenderFrame();
        var end = f.Engine.ViewToPixel(Panel, P(300, 5).Item1, P(300, 5).Item2);
        Assert.False(Vector2.Distance(end, b) < 1);
        Assert.Contains(
            f.Backend.LastList!.Rects.ToArray(),
            r => r.Width > 20 && (Math.Abs(r.X - (MathF.Round(end.X) + 12)) <= 1.5f || Math.Abs(r.X + r.Width - (MathF.Round(end.X) - 12)) <= 1.5f)
        );
        f.Engine.Views.ResetAll();
        f.Engine.RenderFrame();

        // A band x writes Δx as a number of categories, not a category's name (PR #395's review).
        string[] names = ["c0", "c1", "c2", "c3", "c4"];
        using var heights = Column.Wrap(new double[] { 1, 2, 3, 4, 5 });
        var bars = DataTable.Create().Add("c", Column.Categorical(names, names)).Add("v", heights).Build();
        f.Engine.SetPlot(Plot.Create(bars).Bar(bars.Cat("c"), bars.Col<double>("v")).ScaleX(Scale.Band()).Build());
        f.Engine.RenderFrame();
        f.Engine.Measure(Measurement2D.OfPoints(Panel, P(1, 1), P(3, 2)));
        f.Engine.RenderFrame();
        Assert.StartsWith("Δx: 2\n", Assert.Single(f.Backend.LastList!.Texts.ToArray()).Text);
        f.Engine.SetPlot(f.Spec);
        f.Engine.RenderFrame();

        f.Engine.Measure(Measurement2D.OfRange(Panel, ViewRange.FromValues(100.0, 300.0)));
        f.Engine.RenderFrame();
        list = f.Backend.LastList!;
        var (left, top, _, height) = f.Rect;
        var x100 = f.Engine.ViewToPixel(Panel, ViewRange.FromValues(100.0, 100.0), ViewRange.FromValues(0.0, 0.0)).X;
        var x300 = f.Engine.ViewToPixel(Panel, ViewRange.FromValues(300.0, 300.0), ViewRange.FromValues(0.0, 0.0)).X;
        Assert.Contains(
            list.Rects.ToArray(),
            r => Math.Abs(r.X - x100) < 0.5f && Math.Abs(r.Width - (x300 - x100)) < 0.5f && r.Y == top && r.Height == height
        );
        Assert.StartsWith("line: n=201", Assert.Single(list.Texts.ToArray()).Text);

        var categories = Enumerable.Range(0, 60).Select(i => $"c{i}").ToArray();
        var split = DataTable
            .Create()
            .Add("x", Column.Uniform(0, 1, 1000))
            .Add("y", f.Table.Col<float>("y").Column)
            .Add("k", Column.Categorical([.. Enumerable.Range(0, 1000).Select(i => categories[i % 60])], categories))
            .Build();
        f.Engine.SetPlot(Plot.Create(split).Line(split.Col<double>("x"), split.Col<float>("y"), l => l.ColorBy(split.Cat("k"))).Build());
        f.Engine.RenderFrame();
        f.Engine.Measure(Measurement2D.OfRange(Panel, ViewRange.FromValues(0.0, 999.0)));
        f.Engine.RenderFrame();
        Assert.EndsWith(" more)", Assert.Single(f.Backend.LastList!.Texts.ToArray()).Text);

        f.Engine.SetPlot(f.Spec);
        f.Engine.RenderFrame();
        var tool = new MeasureTool(MeasureKind.Points)
        {
            Format = (Measurement _, Span<char> to, out int written) =>
            {
                "custom".CopyTo(to);
                written = 6;
                return true;
            },
        };
        f.Engine.Bindings.Insert(0, new InputBinding(InputGesture.Drag(PointerButtons.Left), ModifierKeys.Alt, HitAreaKind.Panel, tool));
        f.Drag(f.Middle, f.Middle + new Vector2(50, 0), modifiers: ModifierKeys.Alt);
        f.Engine.RenderFrame();
        Assert.Equal("custom", Assert.Single(f.Backend.LastList!.Texts.ToArray()).Text);
        tool.Format = (Measurement _, Span<char> _, out int written) =>
        {
            written = 0;
            return false;
        };
        f.Engine.RenderFrame();
        Assert.Empty(f.Backend.LastList!.Texts.ToArray());
        tool.Format = (Measurement _, Span<char> _, out int written) =>
        {
            written = 2000;
            return true;
        };
        _ = Assert.Throws<InvalidOperationException>(f.Engine.RenderFrame);
    }

    /// <summary>The statistics and warm frames with a range measured, of the mouse alone and of a zoom, allocate nothing (§10, invariant 3).</summary>
    /// <remarks>
    /// Preconditions:
    /// - The fixture's line with the range [100, 300] measured; the shaping cache full.
    ///
    /// Steps:
    /// - Rounds of 50 statistics queries; rounds of 8 moves drawn after each; the pointer leaves; rounds of 20 wheel turns
    ///   drawn after each. 4 of each to warm, a garbage collection, then 2 of each counted, each kind apart.
    ///
    /// Expected:
    /// - The counted rounds: 0 bytes.
    /// </remarks>
    [Fact]
    public void Frames_AllocateNothing()
    {
        using var f = new EngineFixture();
        f.Engine.Measure(Measurement2D.OfRange(Panel, ViewRange.FromValues(100.0, 300.0)));
        for (var i = 0; i < TextEngine.CacheCapacity; i++)
        {
            _ = f.Backend.Text.Measure(
                "filler " + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new TextStyle(new Color(0, 0, 0, 255), 9)
            );
        }

        f.Changes.Capacity = 20_000;
        var (left, top, _, _) = f.Rect;
        var results = new SeriesStatistics[4];
        void Queries()
        {
            for (var i = 0; i < 50; i++)
            {
                _ = f.Engine.Statistics(Panel, ViewRange.FromValues(i, 500.0 + i), results);
            }
        }

        void Moves()
        {
            for (var i = 0; i < 8; i++)
            {
                _ = f.Engine.OnPointer(f.Pointer(PointerEventKind.Move, new Vector2(left + 20 + (i * 23), top + 30 + (i * 7))));
                f.Engine.RenderFrame();
            }
        }

        void Wheels()
        {
            for (var i = 0; i < 20; i++)
            {
                f.Time.Advance(TimeSpan.FromMilliseconds(16));
                _ = f.Engine.OnWheel(new WheelEvent(f.Middle, 0, i < 10 ? 1 : -1, ModifierKeys.None, f.Time.GetTimestamp()));
                f.Engine.RenderFrame();
            }
        }

        // Two rounds a run: 4 rounds warm, then 2 counted.
        PerformanceAssert.DoesNotAllocate(
            () =>
            {
                Queries();
                Queries();
            },
            warmUps: 2,
            scenario: "queries"
        );
        PerformanceAssert.DoesNotAllocate(
            () =>
            {
                Moves();
                Moves();
            },
            warmUps: 2,
            scenario: "moves"
        );
        _ = f.Engine.OnPointer(f.Pointer(PointerEventKind.Leave, new Vector2(left, top)));
        PerformanceAssert.DoesNotAllocate(
            () =>
            {
                Wheels();
                Wheels();
            },
            warmUps: 2,
            scenario: "wheels"
        );
    }

    private static PlotEngine Engine(PlotWorkerPool pool, PlotSpec spec)
    {
        var engine = new PlotEngine(new NullBackend(), 640, 360, 1, pool) { WaitsForIndexes = true };
        engine.SetPlot(spec);
        engine.RenderFrame();
        return engine;
    }

    private static float[] Walk()
    {
        var random = new Random(7);
        var values = new float[Length];
        var level = 0f;
        for (var i = 0; i < values.Length; i++)
        {
            level += (float)(random.NextDouble() - 0.5);
            values[i] = i % 97 == 0 ? float.NaN : level;
        }

        return values;
    }
}
