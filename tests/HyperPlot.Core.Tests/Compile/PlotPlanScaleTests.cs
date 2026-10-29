using System.Numerics;
using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>The compile of row G2's position scales (design §4.2, §4.8): checks, learning, drawing, boundaries, and the diff.</summary>
[Collection("Large data")]
public sealed class PlotPlanScaleTests
{
    private const int Width = 300;
    private const int Height = 200;
    private const int Length = 50_000;

    /// <summary>
    /// For each scale, the plan records the lines that M4 and RenderList give when the transform, the boundaries, and the
    /// mapping of design §4.2 are put together by hand (design §4.2, §4.8).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An evenly spaced X from 1 and float Ys: positive ("pos"), crossing 0 ("cross"), and with -5, 0, and NaN among
    ///   positive values ("mixed", with -5 and a positive value in one pixel column); a user transform log2.
    ///
    /// Steps:
    /// - Render, each against a plan of its own and its hand-built list: log y; symlog y over "cross"; sqrt y and pow 2 y
    ///   over "cross"; log2 x; log y over "mixed" with a domain given; reverse x and y; clamp x and y with domains narrower
    ///   than the data; log x of (100, 40,000), whose origin in the scale space is 2; clamped log y over "mixed", whose 0 maps
    ///   to −∞ and is still dropped, not clamped.
    ///
    /// Expected:
    /// - Each recorded list equals the hand-built one, segment for segment; in the "mixed" one, -5 and 0 are dropped after M4
    ///   and break the line, as NaN does before it (the next test pins which point goes).
    /// </remarks>
    [Fact]
    public void Render_EachScale_EqualsTheTransformByHand()
    {
        var random = new Random(89);
        var pos = Enumerable.Range(0, Length).Select(i => (float)(Math.Exp(Math.Sin(i * 1e-3) * 3) + random.NextDouble())).ToArray();
        var cross = Enumerable.Range(0, Length).Select(i => (float)((Math.Sin(i * 1e-3) * 100) + random.NextDouble())).ToArray();
        var mixed = (float[])pos.Clone();
        (mixed[10_000], mixed[20_000], mixed[30_000]) = (-5, 0, float.NaN);
        using var pool = new PlotWorkerPool(2);
        var x = Column.Uniform(1, 1, Length);
        using var posColumn = Column.Wrap(pos);
        using var crossColumn = Column.Wrap(cross);
        using var mixedColumn = Column.Wrap(mixed);
        var log = Scale.Log();
        var crossRange = (Min: (double)cross.Min(), Max: (double)cross.Max());
        var posRange = (Min: (double)pos.Min(), Max: (double)pos.Max());

        Check(posColumn, Scale.Linear(), log, Axis.Linear(1, Length), Axis.Mapped(LogTransform.Instance, posRange));
        Check(crossColumn, Scale.Linear(), Scale.Symlog(), Axis.Linear(1, Length), Axis.Mapped(new SymlogTransform(1), crossRange));
        Check(crossColumn, Scale.Linear(), Scale.Sqrt(), Axis.Linear(1, Length), Axis.Mapped(new PowTransform(0.5), crossRange));
        Check(crossColumn, Scale.Linear(), Scale.Pow(2), Axis.Linear(1, Length), Axis.Mapped(new PowTransform(2), crossRange));
        var log2 = new ScaleSpecTests.Log2();
        Check(posColumn, Scale.Transform(log2), Scale.Linear(), Axis.Mapped(log2, (1, Length)), Axis.Linear(posRange.Min, posRange.Max));
        Check(mixedColumn, Scale.Linear(), log.WithDomain(1, 30), Axis.Linear(1, Length), Axis.Mapped(LogTransform.Instance, (1, 30)));
        Check(
            posColumn,
            Scale.Linear() with
            {
                Reverse = true,
            },
            Scale.Linear() with
            {
                Reverse = true,
            },
            Axis.Linear(1, Length) with
            {
                Reverse = true,
            },
            Axis.Linear(posRange.Min, posRange.Max) with
            {
                Reverse = true,
            }
        );
        var narrowX = Scale.Linear().WithDomain(10_000, 20_000) with { Clamp = true };
        var narrowY = Scale.Log().WithDomain(2, 5) with { Clamp = true };
        Check(
            posColumn,
            narrowX,
            narrowY,
            Axis.Linear(10_000, 20_000) with
            {
                Clamp = true,
            },
            Axis.Mapped(LogTransform.Instance, (2, 5)) with
            {
                Clamp = true,
            }
        );

        Check(
            posColumn,
            Scale.Log().WithDomain(100, 40_000),
            Scale.Linear(),
            Axis.Mapped(LogTransform.Instance, (100, 40_000)),
            Axis.Linear(posRange.Min, posRange.Max)
        );
        var clampedLog = Scale.Log().WithDomain(1, 30) with { Clamp = true };
        Check(mixedColumn, Scale.Linear(), clampedLog, Axis.Linear(1, Length), Axis.Mapped(LogTransform.Instance, (1, 30)) with { Clamp = true });

        void Check(Column<float> y, ScaleSpec xScale, ScaleSpec yScale, Axis xAxis, Axis yAxis)
        {
            using var plan = new PlotPlan(pool, Width, Height);
            var list = plan.Render(Spec(x, y) with { ScaleX = xScale, ScaleY = yScale });
            PlotPlanTests.AssertSameLines(ByHand(pool, x, y, xAxis, yAxis), list);
        }
    }

    /// <summary>
    /// A log scale drops a value at or below 0 after M4 picked it in data units, so a pixel column that holds -5 and positive
    /// values loses its minimum and does not draw the positive minimum (design §4.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 20 elements over 2 pixel columns: column 0 holds 3, -5, 2, 4, 6, 8, 7, 9, 5, 4; column 1 holds 10 positive values.
    ///
    /// Steps:
    /// - Render with a log y over a domain of (1, 20).
    ///
    /// Expected:
    /// - Column 0's points are first (3), max (9), and last (4), with a break where -5 was; the positive minimum 2 is not
    ///   drawn.
    /// </remarks>
    [Fact]
    public void Render_LogOverNegative_DropsThePickedMinimum()
    {
        float[] values = [3, -5, 2, 4, 6, 8, 7, 9, 5, 4, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
        using var pool = new PlotWorkerPool(1);
        using var y = Column.Wrap(values);
        using var plan = new PlotPlan(pool, 2, Height);

        var list = plan.Render(
            Spec(Column.Uniform(0, 1, values.Length), y) with
            {
                ScaleX = Scale.Linear().WithDomain(0, 20),
                ScaleY = Scale.Log().WithDomain(1, 20),
            }
        );

        var ys = list.Segments.ToArray().SelectMany(s => new[] { s.Start.Y, s.End.Y }).Distinct().ToArray();
        var pixelOf2 = (float)(Height - (Math.Log10(2) / Math.Log10(20) * Height));
        Assert.DoesNotContain(pixelOf2, ys);
        Assert.Contains((float)(Height - (Math.Log10(9) / Math.Log10(20) * Height)), ys);
        Assert.Contains((float)(Height - (Math.Log10(3) / Math.Log10(20) * Height)), ys);
    }

    /// <summary>A time scale draws a long column the same pixels as a linear scale over it, in nanoseconds and in ticks (design §4.2).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A Column&lt;long&gt; X of 1970-based nanoseconds of 2026, one apart, and float Y.
    ///
    /// Steps:
    /// - Render with Scale.Time(Nanoseconds), Scale.Time(Ticks), and Scale.Linear() for x.
    ///
    /// Expected:
    /// - The three lists are equal, and so are the x domains.
    /// </remarks>
    [Fact]
    public void Render_Time_EqualsLinearOverTheSameColumn()
    {
        var random = new Random(97);
        using var pool = new PlotWorkerPool(1);
        using var x = Column.Wrap(Enumerable.Range(0, Length).Select(i => 1_793_232_000_000_000_000L + i).ToArray());
        using var y = Column.Wrap(Enumerable.Range(0, Length).Select(_ => (float)random.NextDouble()).ToArray());
        using var linear = new PlotPlan(pool, Width, Height);
        var expected = linear.Render(Spec(x, y));
        foreach (var unit in new[] { TimeUnit.Nanoseconds, TimeUnit.Ticks })
        {
            using var plan = new PlotPlan(pool, Width, Height);
            PlotPlanTests.AssertSameLines(expected, plan.Render(Spec(x, y) with { ScaleX = Scale.Time(unit) }));
            Assert.Equal(linear.XDomain, plan.XDomain);
        }
    }

    /// <summary>Learning follows design §4.2 for transformed scales and for zero.</summary>
    /// <remarks>
    /// Preconditions:
    /// - Columns: floats with -1 ("neg"), one value 0.3 ("one"), no value ("empty"), 2 to 5 ("mid"), longs 10 to 20
    ///   ("longs"), one value 5 ("five"), one value 0 ("nil").
    ///
    /// Steps:
    /// - Render y with: log over "neg"; pow 2 over "small" and "huge" (1e200, which maps to ∞) as two layers; log over "one" and "empty"; linear, symlog, and pow with zero over "mid"; linear with
    ///   zero and a domain given; linear with zero over "longs", "five", and "nil".
    ///
    /// Expected:
    /// - InvalidOperationException naming "neg" and -1, and one naming "huge" (the column of the max end); log10(0.3) ± 0.5; [0, 1] of the log space; [0, 5] mapped by each;
    ///   the given domain kept; [0, 20] exactly as longs; [0, 5]; [-0.5, 0.5].
    /// </remarks>
    [Fact]
    public void Render_Learning_FollowsTheDesign()
    {
        using var pool = new PlotWorkerPool(1);
        var exception = Assert.Throws<InvalidOperationException>(() => YDomain(pool, "neg", Scale.Log(), 2, -1, 3));
        Assert.Contains("\"neg\"", exception.Message, StringComparison.Ordinal);
        Assert.Contains("-1", exception.Message, StringComparison.Ordinal);
        using (var small = Column.Wrap(new float[] { 1, 2 }))
        using (var huge = Column.Wrap(new double[] { 1, 1e200 }))
        {
            var table = DataTable.Create().Add("x", Column.Uniform(1, 1, 2)).Add("small", small).Add("huge", huge).Build();
            var two = Plot.Create(table)
                .Line(table.Col<double>("x"), table.Col<float>("small"))
                .Line(table.Col<double>("x"), table.Col<double>("huge"))
                .ScaleY(Scale.Pow(2))
                .Build();
            using var plan = new PlotPlan(pool, Width, Height);
            Assert.Contains("\"huge\"", Assert.Throws<InvalidOperationException>(() => plan.Render(two)).Message, StringComparison.Ordinal);
        }

        Assert.Equal(ScaleRange.FromDouble(Math.Log10(0.3f) - 0.5, Math.Log10(0.3f) + 0.5), YDomain(pool, "one", Scale.Log(), 0.3f));
        Assert.Equal(ScaleRange.FromDouble(0, 1), YDomain(pool, "empty", Scale.Log()));
        Assert.Equal(ScaleRange.FromDouble(0, 5), YDomain(pool, "mid", Scale.Linear() with { Zero = true }, 2, 5));
        Assert.Equal(ScaleRange.FromDouble(0, Math.Log10(1 + 5)), YDomain(pool, "mid", Scale.Symlog() with { Zero = true }, 2, 5));
        Assert.Equal(ScaleRange.FromDouble(0, 25), YDomain(pool, "mid", Scale.Pow(2) with { Zero = true }, 2, 5));
        Assert.Equal(ScaleRange.FromDouble(2, 3), YDomain(pool, "mid", Scale.Linear().WithDomain(2, 3) with { Zero = true }, 2, 5));
        Assert.Equal(ScaleRange.FromInteger(0, 20), LongDomain(pool, "longs", Scale.Linear() with { Zero = true }, 10, 20));
        Assert.Equal(ScaleRange.FromInteger(0, 5), LongDomain(pool, "five", Scale.Linear() with { Zero = true }, 5));
        Assert.Equal(new ScaleRange(-1, 0.5, 1.5), LongDomain(pool, "nil", Scale.Linear() with { Zero = true }, 0));
    }

    /// <summary>The compile refuses the scales design §4.8 refuses.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A scale derived from ScaleSpec through its copy constructor; a transform that maps negative values to NaN.
    ///
    /// Steps:
    /// - Render with the derived scale; with domains put in by `with` that WithDomain would refuse (log (0, 1), time (5, 5),
    ///   the transform's (-1, 1)); with a time scale over an evenly spaced column of doubles and over a stored double column.
    ///
    /// Expected:
    /// - ArgumentException naming the derived type; ArgumentException for each domain; ArgumentException naming the column
    ///   and its kind for the time scale.
    /// </remarks>
    [Fact]
    public void Render_ScalesOutOfContract_Throw()
    {
        using var pool = new PlotWorkerPool(1);
        using var y = Column.Wrap(new float[] { 1, 2, 3 });
        using var doubles = Column.Wrap(new double[] { 1, 2, 3 });
        using var plan = new PlotPlan(pool, Width, Height);
        var spec = Spec(Column.Uniform(1, 1, 3), y);

        Assert.Contains(
            nameof(OtherScale),
            Assert.Throws<ArgumentException>(() => plan.Render(spec with { ScaleX = new OtherScale(Scale.Linear()) })).Message,
            StringComparison.Ordinal
        );
        Assert.Throws<ArgumentException>(() => plan.Render(spec with { ScaleY = Scale.Log() with { Domain = (0, 1) } }));
        Assert.Throws<ArgumentException>(() => plan.Render(spec with { ScaleX = Scale.Time() with { Domain = (5, 5) } }));
        Assert.Throws<ArgumentException>(() =>
            plan.Render(spec with { ScaleY = Scale.Transform(new ScaleSpecTests.Log2()) with { Domain = (-1, 1) } })
        );
        Assert.Throws<ArgumentException>(() => plan.Render(spec with { ScaleX = Scale.Time() }));
        var time = Spec(doubles, y) with { ScaleX = Scale.Time() };
        var message = Assert.Throws<ArgumentException>(() => plan.Render(time)).Message;
        Assert.Contains("\"x\"", message, StringComparison.Ordinal);
        Assert.Contains("Column<Double>", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A log x domain whose boundaries increase in the log space but repeat once mapped back to data units is refused, before
    /// M4 (design §4.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A plan 1,000 pixels wide.
    ///
    /// Steps:
    /// - Render with a log x of the domain (1, 1 + 1e-13).
    ///
    /// Expected:
    /// - ArgumentException saying the boundaries mapped back to data units do not increase.
    /// </remarks>
    [Fact]
    public void Render_LogDomainTooNarrowInData_Throws()
    {
        using var pool = new PlotWorkerPool(1);
        using var y = Column.Wrap(new float[] { 1, 2, 3 });
        using var plan = new PlotPlan(pool, 1000, Height);

        var exception = Assert.Throws<ArgumentException>(() =>
            plan.Render(Spec(Column.Uniform(1, 1, 3), y) with { ScaleX = Scale.Log().WithDomain(1, 1 + 1e-13) })
        );

        Assert.Contains("mapped back to data units", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Changing only reverse or clamp records again without M4, and changing the kind, zero, or the transform extracts again
    /// (design §4.8).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A plan that rendered a linear x and y; two log2 transforms.
    ///
    /// Steps:
    /// - Render, in order: y with reverse; y plain; y with clamp; y as log; y as linear; y as linear with zero; x as the first
    ///   log2; x as the second log2; x as the second log2 with a domain; then, one member at a time from the step before of
    ///   the same kind, a log domain, a symlog C, zero, and domain, and a pow k, zero, and domain.
    ///
    /// Expected:
    /// - Pyramids, extractions, recordings added: 0/0/1 for each of the first three, then 0/1/1 for every other step.
    /// </remarks>
    [Fact]
    public void Render_ChangedScales_RedoOnlyTheChangedStages()
    {
        using var pool = new PlotWorkerPool(1);
        using var y = Column.Wrap(Enumerable.Range(1, 100).Select(i => (float)i).ToArray());
        using var plan = new PlotPlan(pool, Width, Height);
        var spec = Spec(Column.Uniform(1, 1, 100), y);
        _ = plan.Render(spec);

        Step(spec with { ScaleY = Scale.Linear() with { Reverse = true } }, (0, 0, 1));
        Step(spec with { ScaleY = Scale.Linear() }, (0, 0, 1));
        Step(spec with { ScaleY = Scale.Linear() with { Clamp = true } }, (0, 0, 1));
        Step(spec with { ScaleY = Scale.Log() }, (0, 1, 1));
        Step(spec with { ScaleY = Scale.Linear() }, (0, 1, 1));
        Step(spec with { ScaleY = Scale.Linear() with { Zero = true } }, (0, 1, 1));
        Step(spec with { ScaleX = Scale.Transform(new ScaleSpecTests.Log2()) }, (0, 1, 1));
        var log2 = new ScaleSpecTests.Log2();
        Step(spec with { ScaleX = Scale.Transform(log2) }, (0, 1, 1));
        Step(spec with { ScaleX = Scale.Transform(log2).WithDomain(1, 50) }, (0, 1, 1));
        Step(spec with { ScaleY = Scale.Log().WithDomain(1, 50) }, (0, 1, 1));
        Step(spec with { ScaleY = Scale.Log().WithDomain(1, 60) }, (0, 1, 1));
        Step(spec with { ScaleY = Scale.Symlog(1) }, (0, 1, 1));
        Step(spec with { ScaleY = Scale.Symlog(2) }, (0, 1, 1));
        Step(spec with { ScaleY = Scale.Symlog(2) with { Zero = true } }, (0, 1, 1));
        Step(spec with { ScaleY = (Scale.Symlog(2) with { Zero = true }).WithDomain(0, 50) }, (0, 1, 1));
        Step(spec with { ScaleY = Scale.Pow(2) }, (0, 1, 1));
        Step(spec with { ScaleY = Scale.Pow(3) }, (0, 1, 1));
        Step(spec with { ScaleY = Scale.Pow(3) with { Zero = true } }, (0, 1, 1));
        Step(spec with { ScaleY = (Scale.Pow(3) with { Zero = true }).WithDomain(0, 50) }, (0, 1, 1));

        void Step(PlotSpec next, (int Pyramids, int Extractions, int Recordings) expected)
        {
            var before = (plan.PyramidsBuilt, plan.Extractions, plan.Recordings);
            _ = plan.Render(next);
            Assert.Equal(
                expected,
                (plan.PyramidsBuilt - before.PyramidsBuilt, plan.Extractions - before.Extractions, plan.Recordings - before.Recordings)
            );
        }
    }

    /// <summary>
    /// A time scale extracts again when its unit, epoch, or domain changes, and a given time domain is taken exactly in the
    /// column's unit (design §4.2, §4.8).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A plan that rendered an integer uniform X of 2026's ticks + 1 with a time scale in ticks.
    ///
    /// Steps:
    /// - Render, in order (from ticks with the Unix epoch): the unit nanoseconds; another epoch; a domain of the ticks + 11 to + 501.
    ///
    /// Expected:
    /// - 0/1/1 each; the last x domain has the origin of the ticks + 11, and differences 0 and 490.
    /// </remarks>
    [Fact]
    public void Render_ChangedTimeMembers_ExtractAgain()
    {
        const long ticks = 639_288_288_000_000_001;
        using var pool = new PlotWorkerPool(1);
        using var y = Column.Wrap(Enumerable.Range(1, 100).Select(i => (float)i).ToArray());
        using var plan = new PlotPlan(pool, Width, Height);
        var spec = Spec(Column.UniformInteger(ticks, 10, 100), y) with { ScaleX = Scale.Time(TimeUnit.Ticks, DateTime.UnixEpoch) };
        _ = plan.Render(spec);

        Step(spec with { ScaleX = Scale.Time(TimeUnit.Nanoseconds, DateTime.UnixEpoch) });
        Step(spec with { ScaleX = Scale.Time(TimeUnit.Nanoseconds, new DateTime(2000, 1, 1)) });
        Step(spec with { ScaleX = Scale.Time(TimeUnit.Nanoseconds, new DateTime(2000, 1, 1)).WithDomain(ticks + 10, ticks + 500) });

        Assert.Equal(new ScaleRange(ticks + 10, 0, 490), plan.XDomain);

        void Step(PlotSpec next)
        {
            var before = (plan.PyramidsBuilt, plan.Extractions, plan.Recordings);
            _ = plan.Render(next);
            Assert.Equal(
                (0, 1, 1),
                (plan.PyramidsBuilt - before.PyramidsBuilt, plan.Extractions - before.Extractions, plan.Recordings - before.Recordings)
            );
        }
    }

    /// <summary>
    /// A new scale makes new boundaries even when its range of the scale space equals the last one's: a linear x of (0, 2)
    /// and a log x of (1, 100) both span 0 to 2 (design §4.2).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A plan that rendered a linear x with the domain (0, 2) over an X of 1 to 100.
    ///
    /// Steps:
    /// - Render the log x of (1, 100).
    ///
    /// Expected:
    /// - The lines equal those a new plan records for the log spec.
    /// </remarks>
    [Fact]
    public void Render_SameRangeNewScale_MakesNewBoundaries()
    {
        using var pool = new PlotWorkerPool(1);
        using var y = Column.Wrap(Enumerable.Range(1, 100).Select(i => (float)Math.Sin(i * 0.1)).ToArray());
        using var plan = new PlotPlan(pool, Width, Height);
        using var fresh = new PlotPlan(pool, Width, Height);
        var spec = Spec(Column.Uniform(1, 1, 100), y);
        _ = plan.Render(spec with { ScaleX = Scale.Linear().WithDomain(0, 2) });

        var log = spec with { ScaleX = Scale.Log().WithDomain(1, 100) };

        PlotPlanTests.AssertSameLines(fresh.Render(log), plan.Render(log));
    }

    /// <summary>An exception that a user's transform throws comes out of Render as it is (design §4.2).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A transform whose Forward throws a given exception.
    ///
    /// Steps:
    /// - Render with it as the y scale.
    ///
    /// Expected:
    /// - Render throws that exception object.
    /// </remarks>
    [Fact]
    public void Render_TransformThrows_ThrowsItAsItIs()
    {
        using var pool = new PlotWorkerPool(1);
        using var y = Column.Wrap(new float[] { 1, 2, 3 });
        using var plan = new PlotPlan(pool, Width, Height);
        var failure = new InvalidDataException("transform");

        var thrown = Assert.Throws<InvalidDataException>(() =>
            plan.Render(Spec(Column.Uniform(1, 1, 3), y) with { ScaleY = Scale.Transform(new Throwing(failure)) })
        );

        Assert.Same(failure, thrown);
    }

    /// <summary>Once warm, switching a log x between two domains allocates nothing (design §4.2, invariant 3).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A plan that rendered a log x over two domains, twice each.
    ///
    /// Steps:
    /// - Count the bytes this thread allocates over 20 Render calls that alternate the domains.
    ///
    /// Expected:
    /// - 0 bytes; 20 extractions.
    /// </remarks>
    [Fact]
    public void Render_LogDomainsWarm_AllocateNothing()
    {
        using var pool = new PlotWorkerPool(1);
        using var y = Column.Wrap(Enumerable.Range(1, Length).Select(i => (float)Math.Sin(i * 1e-3)).ToArray());
        using var plan = new PlotPlan(pool, Width, Height);
        var spec = Spec(Column.Uniform(1, 1, Length), y);
        PlotSpec[] zooms = [spec with { ScaleX = Scale.Log().WithDomain(10, 1000) }, spec with { ScaleX = Scale.Log().WithDomain(100, 40_000) }];
        foreach (var warm in new[] { zooms[0], zooms[1], zooms[0], zooms[1] })
        {
            _ = plan.Render(warm);
        }

        var extractions = plan.Extractions;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 20; i++)
        {
            _ = plan.Render(zooms[i & 1]);
        }

        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(extractions + 20, plan.Extractions);
    }

    private static ScaleRange YDomain(PlotWorkerPool pool, string name, ScaleSpec scale, params float[] values)
    {
        using (var y = Column.Wrap(values))
        {
            var table = DataTable.Create().Add("x", Column.Uniform(1, 1, y.Length)).Add(name, y).Build();
            using var plan = new PlotPlan(pool, Width, Height);
            _ = plan.Render(
                new PlotSpec
                {
                    Data = table,
                    Layers = [new LineLayerSpec { X = table.Col<double>("x"), Y = table.Col<float>(name) }],
                    ScaleY = scale,
                }
            );
            return plan.YDomain;
        }
    }

    private static ScaleRange LongDomain(PlotWorkerPool pool, string name, ScaleSpec scale, params long[] values)
    {
        using (var y = Column.Wrap(values))
        {
            var table = DataTable.Create().Add("x", Column.Uniform(1, 1, y.Length)).Add(name, y).Build();
            using var plan = new PlotPlan(pool, Width, Height);
            _ = plan.Render(
                new PlotSpec
                {
                    Data = table,
                    Layers = [new LineLayerSpec { X = table.Col<double>("x"), Y = table.Col<long>(name) }],
                    ScaleY = scale,
                }
            );
            return plan.YDomain;
        }
    }

    private static PlotSpec Spec(Column x, Column<float> y)
    {
        var table = DataTable.Create().Add("x", x).Add("y", y).Build();
        ColumnRef xRef = x switch
        {
            UniformColumn { IsInteger: true } or Column<long> => table.Col<long>("x"),
            _ => table.Col<double>("x"),
        };
        return new PlotSpec { Data = table, Layers = [new LineLayerSpec { X = xRef, Y = table.Col<float>("y") }] };
    }

    /// <summary>
    /// Puts M4 and the list together as design §4.2 says the plan does: the boundaries in the scale space mapped back with
    /// the inverse, the origin 0 for a transformed axis, and each point mapped, dropped when not finite, clamped, and
    /// reversed.
    /// </summary>
    private static RenderList ByHand(PlotWorkerPool pool, UniformColumn x, Column<float> y, Axis xAxis, Axis yAxis)
    {
        using var pyramid = MinMaxPyramidTests.Build(pool, y);
        var scaleBoundaries = Enumerable
            .Range(0, Width + 1)
            .Select(k => xAxis.Domain.Lower + ((xAxis.Domain.Upper - xAxis.Domain.Lower) * k / Width))
            .ToArray();
        var boundaries = new double[Width + 1];
        if (xAxis.Transform is null)
        {
            scaleBoundaries.CopyTo(boundaries, 0);
        }
        else
        {
            xAxis.Transform.Inverse(scaleBoundaries.Select(s => s + xAxis.Domain.Origin).ToArray(), boundaries);
        }

        var points = new M4Point[M4.MaxPointCount(Width)];
        var origin = new ScaleOrigin(xAxis.Transform is null ? xAxis.Domain.Origin : 0, yAxis.Transform is null ? yAxis.Domain.Origin : 0);
        var result = M4.Extract(x, pyramid, origin, boundaries, points);
        var polyline = new List<Vector2>();
        foreach (var point in points.AsSpan(0, result.Count))
        {
            if (point.BreakBefore)
            {
                polyline.Add(new Vector2(float.NaN));
            }

            polyline.Add(new Vector2(xAxis.Pixel(point.X, Width, ofX: true), yAxis.Pixel(point.Y, Height, ofX: false)));
        }

        var list = new RenderList();
        list.DrawPolyline(polyline.ToArray(), new LineStyle(new Color(0, 0, 0, 255), 1));
        return list;
    }

    /// <summary>One axis as the test builds it by hand: its transform, its domain as origin and differences, and its options.</summary>
    private sealed record Axis(IScale? Transform, ScaleRange Domain, bool Reverse = false, bool Clamp = false)
    {
        public static Axis Linear(double min, double max) => new(null, ScaleRange.FromDouble(min, max));

        public static Axis Mapped(IScale transform, (double Min, double Max) range)
        {
            var mapped = new double[2];
            transform.Forward([range.Min, range.Max], mapped);
            return new(transform, ScaleRange.FromDouble(mapped[0], mapped[1]));
        }

        public float Pixel(double value, int size, bool ofX)
        {
            var d = value;
            if (Transform is not null)
            {
                var mapped = new double[1];
                Transform.Forward([value], mapped);
                d = mapped[0] - Domain.Origin;
            }

            if (!double.IsFinite(d))
            {
                return float.NaN;
            }

            d = Clamp ? Math.Clamp(d, Domain.Lower, Domain.Upper) : d;
            var t = (d - Domain.Lower) / (Domain.Upper - Domain.Lower) * size;
            return (float)(ofX ? (Reverse ? size - t : t) : (Reverse ? t : size - t));
        }
    }

    private sealed record OtherScale : ScaleSpec
    {
        public OtherScale(ScaleSpec original)
            : base(original) { }
    }

    private sealed class Throwing(Exception failure) : IScale
    {
        public void Forward(ReadOnlySpan<double> values, Span<double> destination) => throw failure;

        public void Inverse(ReadOnlySpan<double> values, Span<double> destination) => throw failure;
    }
}
