using System.Numerics;
using HyperPlot.Rendering;
using static HyperPlot.Tests.GeomTesting;

namespace HyperPlot.Tests;

/// <summary>A user's geom, the legend of the geoms, the diff, and the allocations of row G4 (design §4.6, §4.8, §5.5.3).</summary>
public sealed class GeomCustomTests
{
    private static readonly double[] Values = [10, 30, 20];
    private static readonly double[] Ats = [10, 50, 90];

    /// <summary>
    /// A user's geom learns once each time the plot learns and records once each time it records; drawing the same spec
    /// again calls neither (design §5.5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A custom layer that counts its calls and a line, with learned domains.
    ///
    /// Steps:
    /// - Render the spec; again; with a new title (looks); with a given x domain (scale).
    ///
    /// Expected:
    /// - Learn and Record calls: (1, 1), (0, 0), (0, 1), (1, 1).
    /// </remarks>
    [Fact]
    public void Custom_CalledOnceALearningAndARecording()
    {
        using var pool = new PlotWorkerPool(1);
        var (table, _) = Table();
        var geom = new CountingGeom();
        var spec = Plot.Create(table).Custom(geom).Line(table.Col<double>("x"), table.Col<double>("y")).Theme(TestPlans.Bare).Build();
        using var plan = TestPlans.Create(pool, Size, Size);

        Assert.Equal((1, 1), Calls(spec));
        Assert.Equal((0, 0), Calls(spec));
        Assert.Equal((0, 1), Calls(spec with { Title = "New" }));
        Assert.Equal((1, 1), Calls(spec with { ScaleX = Scale.Linear().WithDomain(0, 100) }));

        (int Learns, int Records) Calls(PlotSpec next)
        {
            var before = (geom.Learns, geom.Records);
            _ = plan.Render(next);
            return (geom.Learns - before.Learns, geom.Records - before.Records);
        }
    }

    /// <summary>
    /// What a user's geom adds joins the domains (design §4.2): doubles, longs exactly, nothing; a range that is not finite
    /// or whose min is above its max throws out of Render.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Geoms that add x [−5, 200] and y [−1, 3]; x [2^60 + 1, 2^60 + 3] as longs; nothing; x [NaN, 1]; y [2, 1].
    ///
    /// Steps:
    /// - Render each with a line over x 10 to 90 and y 10 to 30, with learned domains.
    ///
    /// Expected:
    /// - [−5, 200] × [−1, 30]; alone, the long range [2^60 + 1, 2^60 + 3] with its origin exact; the line's own [10, 90] ×
    ///   [10, 30]; ArgumentException for the last two.
    /// </remarks>
    [Fact]
    public void Custom_RangesJoinTheDomains()
    {
        using var pool = new PlotWorkerPool(1);
        var (table, line) = Table();
        const long Far = (1L << 60) + 1;

        Assert.Equal(
            ((-5d, 200d), (-1d, 30d)),
            Learned(
                r =>
                {
                    r.AddX(-5, 200);
                    r.AddY(-1, 3);
                },
                line
            )
        );
        using (var plan = TestPlans.Create(pool, Size, Size))
        {
            _ = plan.Render(Spec(new DelegateGeom(r => r.AddX(Far, Far + 2))) with { ScaleX = Scale.Linear(), ScaleY = Scale.Linear() });
            Assert.Equal((Far, 0d, 2d), (plan.XDomain.Origin, plan.XDomain.Lower, plan.XDomain.Upper));
        }

        Assert.Equal(((10d, 90d), (10d, 30d)), Learned(_ => { }, line));
        _ = Assert.Throws<ArgumentException>(() => Learned(r => r.AddX(double.NaN, 1), line));
        _ = Assert.Throws<ArgumentException>(() => Learned(r => r.AddY(2, 1), line));

        ((double, double), (double, double)) Learned(Action<GeomRanges> learn, LayerSpec other)
        {
            using var plan = TestPlans.Create(pool, Size, Size);
            _ = plan.Render(Spec(new DelegateGeom(learn), other) with { ScaleX = Scale.Linear(), ScaleY = Scale.Linear() });
            return (Ends(plan.XDomain), Ends(plan.YDomain));
        }

        PlotSpec Spec(IGeom geom, params LayerSpec[] layers) => GeomTesting.Spec(table, [new CustomLayerSpec(geom), .. layers]);
    }

    /// <summary>
    /// The frame works only inside Record; a clip the geom sets ends with its layer; an exception it throws comes out of
    /// Render as it is (design §5.5.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A geom that keeps its frame, sets a clip of (0, 0, 10, 10), and draws a rect; a line after it; a geom that throws.
    ///
    /// Steps:
    /// - Render; read the kept frame's members; render the throwing geom.
    ///
    /// Expected:
    /// - The rect has the clip set; the line after it the panel's; each member of the kept frame throws
    ///   InvalidOperationException; the thrown exception is the same instance.
    /// </remarks>
    [Fact]
    public void Custom_FrameClipAndException()
    {
        using var pool = new PlotWorkerPool(1);
        var (table, line) = Table();
        GeomFrame? kept = null;
        var geom = new DelegateGeom(
            _ => { },
            (frame, list) =>
            {
                kept = frame;
                list.SetClip(0, 0, 10, 10);
                list.DrawRect(1, 1, 5, 5, new Color(1, 2, 3, 255));
            }
        );
        using var plan = TestPlans.Create(pool, Size, Size);

        var drawn = plan.Render(Spec(table, new CustomLayerSpec(geom), line));

        Assert.Equal(new ClipRect(0, 0, 10, 10), drawn.RectClips[1]);
        Assert.Equal(new ClipRect(0, 0, Size, Size), drawn.Polylines[0].Clip);
        var frame = kept!;
        foreach (
            var member in new Action[]
            {
                () => _ = frame.Left,
                () => _ = frame.Top,
                () => _ = frame.Width,
                () => _ = frame.Height,
                () => _ = frame.Scale,
                () => frame.PixelX(1.0),
                () => frame.PixelY(1.0),
                () => frame.PixelX(1L),
                () => frame.PixelY(1L),
            }
        )
        {
            _ = Assert.Throws<InvalidOperationException>(member);
        }

        var thrown = new FormatException("geom");
        var throwing = new DelegateGeom(_ => { }, (_, _) => throw thrown);
        Assert.Same(thrown, Assert.Throws<FormatException>(() => plan.Render(Spec(table, new CustomLayerSpec(throwing)))));
    }

    /// <summary>
    /// The frame maps values to the pixels a line of the same scales draws them at (design §4.4): linear, log, reversed,
    /// clamped, long values of 2026 exactly, and NaN where a value does not map.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A line through (10, 10), (50, 30), (90, 20) and a geom that maps the same values; a long line of 2026 in ns.
    ///
    /// Steps:
    /// - Render on linear scales; on a log x reversed with a clamped y of domain [15, 25]; read the geom's pixels against the
    ///   line's points; render the long line on a time x and map its values as longs; map −1 on the log x.
    ///
    /// Expected:
    /// - The pixels equal the line's, as floats; −1 on log maps to NaN.
    /// </remarks>
    [Fact]
    public void Custom_FramePixelsMatchTheLine()
    {
        using var pool = new PlotWorkerPool(1);
        var (table, line) = Table();
        var scales = new (ScaleSpec X, ScaleSpec Y)[]
        {
            (Scale.Linear().WithDomain(0, 100), Scale.Linear().WithDomain(0, 40)),
            (Scale.Log().WithDomain(1, 100) with { Reverse = true }, Scale.Linear().WithDomain(15, 25) with { Clamp = true }),
        };
        foreach (var (x, y) in scales)
        {
            var mapped = new List<Vector2>();
            var nan = double.NaN;
            var geom = new DelegateGeom(
                _ => { },
                (frame, _) =>
                {
                    for (var i = 0; i < Ats.Length; i++)
                    {
                        mapped.Add(new Vector2((float)frame.PixelX(Ats[i]), (float)frame.PixelY(Values[i])));
                    }

                    nan = frame.PixelX(-1.0);
                }
            );
            using var plan = TestPlans.Create(pool, Size, Size);
            var list = plan.Render(Spec(table, new CustomLayerSpec(geom), line) with { ScaleX = x, ScaleY = y });

            Assert.Equal(Vertices(list), mapped);
            Assert.Equal(x is LogScaleSpec, double.IsNaN(nan));
        }

        const long Start = 1_767_225_600_000_000_000;
        using var times = Column.Wrap(new[] { Start, Start + 1, Start + 3 });
        using var ys = Column.Wrap(Values);
        var timeTable = DataTable.Create().Add("t", times).Add("y", ys).Build();
        var longs = new List<Vector2>();
        var timeGeom = new DelegateGeom(
            _ => { },
            (frame, _) =>
            {
                foreach (var (t, v) in new[] { (Start, 10.0), (Start + 1, 30.0), (Start + 3, 20.0) })
                {
                    longs.Add(new Vector2((float)frame.PixelX(t), (float)frame.PixelY(v)));
                }
            }
        );
        using var timePlan = TestPlans.Create(pool, Size, Size);
        var timeList = timePlan.Render(
            Spec(timeTable, new CustomLayerSpec(timeGeom), new LineLayerSpec { X = timeTable.Col<long>("t"), Y = timeTable.Col<double>("y") }) with
            {
                ScaleX = Scale.Time(TimeUnit.Nanoseconds),
            }
        );
        Assert.Equal(Vertices(timeList), longs);

        static Vector2[] Vertices(RenderList list)
        {
            var segments = SegmentsOf(list, 0);
            return [segments[0].Start, .. segments.Select(s => s.End)];
        }
    }

    /// <summary>
    /// The legend shows a fill geom as a square of the row's height in its fill, a line geom as its stroke, and a user's
    /// geom as a black line 1 wide (design §4.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The light theme without axes or grid; an area filled (10, 200, 30, 255) labeled "A", a step of a red stroke 2 wide
    ///   labeled "S", a custom layer labeled "C" that records nothing.
    ///
    /// Steps:
    /// - Render.
    ///
    /// Expected:
    /// - The last rect is square, in the area's fill; after the data, polylines in the step's stroke and in black 1 wide.
    /// </remarks>
    [Fact]
    public void Legend_SamplesByKind()
    {
        using var pool = new PlotWorkerPool(1);
        var (table, _) = Table();
        var fill = new Color(10, 200, 30, 255);
        var stroke = new LineStyle(new Color(200, 0, 0, 255), 2);
        var spec = Plot.Create(table)
            .Area(table.Col<double>("x"), table.Col<double>("y"), configure: a => a.Fill(fill).Label("A"))
            .Step(table.Col<double>("x"), table.Col<double>("y"), configure: l => l.Color(stroke.Color).Width(stroke.Width).Label("S"))
            .Custom(new DelegateGeom(_ => { }), "C")
            .Theme(Theme.Light with { ShowAxes = false, ShowGrid = false })
            .Build();
        using var plan = TestPlans.Create(pool, 300, 200);
        var probe = new RenderList();
        probe.DrawRect(0, 0, 1, 1, fill);

        var list = plan.Render(spec);

        var square = list.Rects[^1];
        Assert.Equal(probe.Rects[0].Color, square.Color);
        Assert.True(square.Width > 0 && square.Width == square.Height);
        Assert.Equal([stroke, stroke, new LineStyle(new Color(0, 0, 0, 255), 1)], list.Polylines.ToArray().Select(p => p.Style));
    }

    /// <summary>
    /// The diff of row G4 (design §4.8): a channel builds and extracts; a Baseline or a bar's Width extracts; a fill, an
    /// outline, a stroke, a mode, a cap width, or a label only records.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An area, a bar labeled "b1", a step, and an error bar over "x", "y", and "z"; the legend keeps its width when the
    ///   label becomes "b2", so the panel's width does not change.
    ///
    /// Steps:
    /// - Render the spec, then specs that change one member at a time.
    ///
    /// Expected:
    /// - (pyramids, extractions, recordings): first (2, 1, 1); same (0, 0, 0); Baseline and Width (0, 1, 1); Fill, Outline,
    ///   Style, Mode, CapWidth, and Label (0, 0, 1); the area's Y to "z" and the error bar's YMax to "y" (0, 1, 1): the
    ///   pyramids of "y" and "z" are kept.
    /// </remarks>
    [Fact]
    public void Diff_ChannelsRangesAndLooks()
    {
        using var pool = new PlotWorkerPool(1);
        using var y = Column.Wrap(Values);
        using var z = Column.Wrap(new double[] { 5, 6, 7 });
        var table = DataTable.Create().Add("x", Column.Wrap(Ats)).Add("y", y).Add("z", z).Build();
        var (rx, ry, rz) = (table.Col<double>("x"), table.Col<double>("y"), table.Col<double>("z"));
        var area = new AreaLayerSpec { X = rx, Y = ry };
        var bar = new BarLayerSpec
        {
            X = rx,
            Y = rz,
            Label = "b1",
        };
        var step = new StepLayerSpec { X = rx, Y = ry };
        var errorBar = new ErrorBarLayerSpec
        {
            X = rx,
            YMin = ry,
            YMax = rz,
        };
        var spec = Plot.Create(table).Theme(TestPlans.Bare).Build() with { Layers = [area, bar, step, errorBar] };
        using var plan = TestPlans.Create(pool, Size, Size);

        Step(spec, (2, 1, 1));
        Step(spec, (0, 0, 0));
        Step(With(0, area = area with { Baseline = 5 }), (0, 1, 1));
        Step(With(1, bar = bar with { Width = 3 }), (0, 1, 1));
        Step(With(1, bar = bar with { Baseline = -2 }), (0, 1, 1));
        Step(With(0, area = area with { Fill = new Color(1, 2, 3, 4) }), (0, 0, 1));
        Step(With(0, area = area with { Outline = new LineStyle(new Color(1, 2, 3, 4), 2) }), (0, 0, 1));
        Step(With(2, step = step with { Mode = StepMode.Before }), (0, 0, 1));
        Step(With(2, step with { Style = new LineStyle(new Color(1, 2, 3, 4), 2) }), (0, 0, 1));
        Step(With(3, errorBar = errorBar with { CapWidth = 2 }), (0, 0, 1));
        Step(With(1, bar = bar with { Label = "b2" }), (0, 0, 1));
        Step(With(0, area with { Y = rz }), (0, 1, 1));
        Step(With(3, errorBar with { YMax = ry }), (0, 1, 1));

        PlotSpec With(int index, LayerSpec layer)
        {
            LayerSpec[] layers = [.. spec.Layers];
            layers[index] = layer;
            return spec with { Layers = layers };
        }

        void Step(PlotSpec next, (int Pyramids, int Extractions, int Recordings) expected)
        {
            // Each change is made on the spec drawn last, which the next one starts from.
            var before = (plan.PyramidsBuilt, plan.Extractions, plan.Recordings);
            spec = next;
            _ = plan.Render(next);
            Assert.Equal(
                expected,
                (plan.PyramidsBuilt - before.PyramidsBuilt, plan.Extractions - before.Extractions, plan.Recordings - before.Recordings)
            );
        }
    }

    /// <summary>Once warm, drawing the same spec of each geom again allocates nothing (design §5.5.3, invariant 2).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A spec of each geom, both the few and the many paths of bars, error bars, and rules (800 elements on 100 pixels).
    ///
    /// Steps:
    /// - For each, render the spec and a copy of its layer, a new node that records again; then render them in turn five
    ///   times, counting this thread's bytes.
    ///
    /// Expected:
    /// - 0 bytes for each.
    /// </remarks>
    [Fact]
    public void Record_Warm_AllocatesNothing()
    {
        using var pool = new PlotWorkerPool(1);
        var values = Enumerable.Range(0, 800).Select(i => 50 + (30 * Math.Sin(i * 0.05))).ToArray();
        using var y = Column.Wrap(values);
        using var z = Column.Wrap(values.Select(v => v + 5).ToArray());
        using var sorted = Column.Wrap(Enumerable.Range(0, 800).Select(i => i * 0.125).ToArray());
        var table = DataTable.Create().Add("x", Column.Uniform(0, 0.125, 800)).Add("y", y).Add("z", z).Add("s", sorted).Build();
        var (rx, ry, rz, rs) = (table.Col<double>("x"), table.Col<double>("y"), table.Col<double>("z"), table.Col<double>("s"));
        var layers = new LayerSpec[]
        {
            new StepLayerSpec
            {
                X = rx,
                Y = ry,
                Mode = StepMode.Middle,
            },
            new AreaLayerSpec
            {
                X = rx,
                Y = ry,
                Outline = new LineStyle(new Color(0, 0, 0, 255), 1),
            },
            new BandLayerSpec
            {
                X = rx,
                YMin = ry,
                YMax = rz,
                Outline = new LineStyle(new Color(0, 0, 0, 255), 1),
            },
            new BarLayerSpec
            {
                X = rx,
                Y = ry,
                Baseline = 50,
            },
            new ErrorBarLayerSpec
            {
                X = rx,
                YMin = ry,
                YMax = rz,
            },
            new RuleLayerSpec { X = rs },
            new RuleLayerSpec { Y = rs },
            new RectLayerSpec
            {
                XMin = rx,
                XMax = rs,
                YMin = ry,
                YMax = rz,
            },
            new SegmentLayerSpec
            {
                X = rx,
                Y = ry,
                XEnd = rs,
                YEnd = rz,
            },
            new CustomLayerSpec(new DelegateGeom(r => r.AddX(0, 1), (f, l) => l.DrawRect((float)f.PixelX(1.0), 0, 1, 1, new Color(0, 0, 0, 255)))),
        };
        foreach (var scale in new[] { (Min: 0d, Max: 100d), (Min: 40d, Max: 60d) })
        {
            foreach (var layer in layers)
            {
                var spec = Spec(table, layer) with { ScaleX = Scale.Linear().WithDomain(scale.Min, scale.Max) };
                using var plan = TestPlans.Create(pool, Size, Size);
                var other = spec with { Layers = [layer with { }] };
                _ = plan.Render(spec);
                _ = plan.Render(other);
                var recordings = plan.Recordings;

                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 5; i++)
                {
                    _ = plan.Render(spec);
                    _ = plan.Render(other);
                }

                var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.True(bytes == 0, $"{layer.GetType().Name} on [{scale.Min}, {scale.Max}] allocated {bytes} bytes.");
                Assert.Equal(recordings + 10, plan.Recordings);
            }
        }
    }

    private static (DataTable Table, LineLayerSpec Line) Table()
    {
        var table = DataTable.Create().Add("x", Column.Wrap(Ats)).Add("y", Column.Wrap(Values)).Build();
        return (table, new LineLayerSpec { X = table.Col<double>("x"), Y = table.Col<double>("y") });
    }

    private sealed class CountingGeom : IGeom
    {
        public int Learns { get; private set; }

        public int Records { get; private set; }

        public void Learn(GeomRanges ranges) => Learns++;

        public void Record(GeomFrame frame, RenderList list) => Records++;
    }

    private sealed class DelegateGeom(Action<GeomRanges> learn, Action<GeomFrame, RenderList>? record = null) : IGeom
    {
        public void Learn(GeomRanges ranges) => learn(ranges);

        public void Record(GeomFrame frame, RenderList list) => record?.Invoke(frame, list);
    }
}
