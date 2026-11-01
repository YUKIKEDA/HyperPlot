using System.Globalization;
using HyperPlot.Rendering;
using static HyperPlot.Tests.GeomTesting;

namespace HyperPlot.Tests;

/// <summary>
/// The stats of row G5c: densities against R's <c>density()</c>, moving averages and 2D bins against brute force, their
/// checks, learning, and the diff (design §10 G5c row).
/// </summary>
public sealed class StatG5cTests
{
    /// <summary>
    /// R 4.6.2's <c>density()</c> (<c>old.coords = FALSE</c>) with the data, bandwidth, adjust, and n of each case, from and
    /// to the data's least and greatest value (R's default range for one distinct value): the bandwidth, the range, and the
    /// x and y at some points. Made by an R script of the integer formulas of <see cref="U"/>.
    /// </summary>
    private const string RDensities = """
        normal bw 0.21533756791596506 from 0.28839812131507947 to 3.7116018786849208
        normal 0 0.28839812131507947 0.12812359690399608
        normal 1 0.29509715019447252 0.13124778284107641
        normal 17 0.40228161226476111 0.17858199677055292
        normal 100 0.95830100925438311 0.29812149215659112
        normal 255 1.9966504855603036 0.33534947302231471
        normal 256 2.0033495144396967 0.33536413519681602
        normal 400 2.9680096730722942 0.3168104346249494
        normal 510 3.7049028498055279 0.1301947416834415
        normal 511 3.7116018786849208 0.1270763596799715
        bimodal bw 0.45958713446265631 from 0.14509843109823123 to 6.8489057659638251
        bimodal 0 0.14509843109823123 0.11528956694479202
        bimodal 1 0.15821742783965118 0.11801785572230052
        bimodal 17 0.36812137570237036 0.16128123884683876
        bimodal 100 1.4569981052402261 0.25581843884463407
        bimodal 255 3.4904426001603182 0.02787969082526584
        bimodal 256 3.5035615969017382 0.027379891136103725
        bimodal 400 5.3926971276662101 0.12830765971575672
        bimodal 510 6.8357867692224046 0.059680053144036564
        bimodal 511 6.8489057659638251 0.058296863018607925
        two bw 0.58469813952724747 from 1 to 3
        two 0 1 0.34213070974287296
        two 1 1.0039138943248531 0.34213134612117047
        two 17 1.0665362035225048 0.34038415172524911
        two 100 1.3913894324853229 0.28042615582769903
        two 255 1.9980430528375734 0.1580753808554573
        two 256 2.0019569471624266 0.15807538085545733
        two 400 2.5655577299412915 0.26832386418339949
        two 510 2.9960861056751469 0.34213134612117052
        two 511 3 0.34213070974287296
        same bw 2.8393080501608696 from -3.5179241504826084 to 13.517924150482608
        same 0 -3.5179241504826084 0.0015620619946191813
        same 1 -3.4845858954905045 0.0016191263295079971
        same 17 -2.9511738156168379 0.0027880153664660632
        same 100 -0.18409865127219449 0.026542615895549116
        same 255 4.9833308725039469 0.140480506129601
        same 256 5.0166691274960513 0.140480506129601
        same 400 9.8173778463590473 0.033323246642355218
        same 510 13.484585895490502 0.0016191263295079847
        same 511 13.517924150482608 0.0015620619946191622
        zeros bw 0.56786161003217395 from -1.703584830096522 to 1.703584830096522
        zeros 0 -1.703584830096522 0.0078103099730959307
        zeros 1 -1.696917179098101 0.008095631647539998
        zeros 17 -1.5902347631233678 0.013940076832330331
        zeros 100 -1.0368197302544391 0.13271307947774555
        zeros 255 -0.0033338254992105743 0.70240253064800506
        zeros 256 0.0033338254992103522 0.70240253064800495
        zeros 400 0.96347556927180955 0.16661623321177629
        zeros 510 1.6969171790981008 0.0080956316475399547
        zeros 511 1.703584830096522 0.0078103099730958483
        flat bw 1.4613168249387125 from 2 to 9
        flat 0 2 0.21845919066183275
        flat 1 2.0136986301369864 0.21845241361119552
        flat 17 2.2328767123287672 0.21576034832378627
        flat 100 3.3698630136986303 0.14200977785956304
        flat 255 5.493150684931507 0.030128384229490855
        flat 256 5.506849315068493 0.030039717468772122
        flat 400 7.4794520547945202 0.041947487452204314
        flat 510 8.9863013698630141 0.03813844933474024
        flat 511 9 0.038001853933100148
        adjust bw 0.55098340592008888 from 0.28839812131507947 to 3.7028080343759369
        adjust 0 0.28839812131507947 0.13770373476481537
        adjust 1 0.29507994110580521 0.13910097633146151
        adjust 17 0.40198905775741722 0.16154959027128099
        adjust 100 0.95658010038765429 0.25949513772542865
        adjust 255 1.9922621679501453 0.30603002683993791
        adjust 256 1.998943987740871 0.30603065136531893
        adjust 400 2.9611260376053785 0.26758130585506679
        adjust 510 3.6961262145852114 0.1383739648121958
        adjust 511 3.7028080343759369 0.1369815652516489
        grid1024 bw 0.27549170296004444 from 0.28839812131507947 to 3.7028080343759369
        grid1024 0 0.28839812131507947 0.13255684443968827
        grid1024 1 0.29409830480933802 0.13467578351108384
        grid1024 17 0.3853012407174744 0.16773841324669231
        grid1024 100 0.85841647074093208 0.27909640939922847
        grid1024 255 1.7419449123510036 0.30375081820005517
        grid1024 256 1.7476450958452621 0.30425624761921538
        grid1024 400 2.5684715190184901 0.30276385572074649
        grid1024 510 3.1954917033869279 0.26682765362460514
        grid1024 511 3.2011918868811859 0.26581219273443912
        grid1024 598 3.6971078508816779 0.13375185891757574
        grid1024 599 3.7028080343759369 0.13165161307063333
        grid4096 bw 0.27549170296004444 from 0.28839812131507947 to 3.7028080343759369
        grid4096 0 0.28839812131507947 0.13255723044466936
        grid4096 1 0.28923192104964868 0.13286730418554904
        grid4096 17 0.30257271680275583 0.13782237522185373
        grid4096 100 0.37177809477199908 0.16299718363498056
        grid4096 255 0.50101705363022442 0.20454495998077413
        grid4096 256 0.50185085336479363 0.20478207899764958
        grid4096 400 0.62191801514275791 0.23477531206275806
        grid4096 510 0.71363598594536937 0.25332735738013246
        grid4096 511 0.71446978567993857 0.25348481861771627
        grid4096 4094 3.7019742346413675 0.13195915326563026
        grid4096 4095 3.7028080343759369 0.13165181559270328
        ties bw 0.24784936627364113 from 0.25 to 3.5
        ties 0 0.25 0.1667676245248543
        ties 1 0.25636007827788648 0.16915999631683051
        ties 17 0.35812133072407043 0.20367194198189853
        ties 100 0.88600782778864962 0.30518128654063076
        ties 255 1.8718199608610566 0.32312411299507526
        ties 256 1.8781800391389432 0.32312769054465201
        ties 400 2.7940313111545985 0.31898148255202075
        ties 510 3.4936399217221132 0.16081274857729191
        ties 511 3.5 0.15846233609376861
        given bw 0.050000000000000003 from 0.29759168581992607 to 3.7028080343759369
        given 0 0.29759168581992607 0.15300076004020879
        given 1 0.30425551429068148 0.16484059709496743
        given 17 0.41087676982276788 0.25000881364598082
        given 100 0.96397453289546631 0.22831838288458178
        given 255 1.9968679458625538 0.49550338320798037
        given 256 2.0035317743333092 0.49625895630497519
        given 400 2.9631230741220871 0.2785060522835503
        given 510 3.6961442059051812 0.14464387678480956
        given 511 3.7028080343759369 0.13071970831991647
        log bw 0.25968401242077155 from 0.28839812131507947 to 3.707204956530429
        log 0 0.28839812131507947 0.1305883141213553
        log 1 0.29508854565013887 0.1331690670595278
        log 17 0.40213533501108911 0.17284755012305431
        log 100 0.95744055482101864 0.29166855423803356
        log 255 1.9944563267552244 0.32371098981018792
        log 256 2.0011467510902836 0.3237536805270767
        log 400 2.9645678553388359 0.31181464820890636
        log 510 3.7005145321953696 0.13310085191250334
        log 511 3.707204956530429 0.13049545630693016
        """;

    /// <summary>The factories refuse values out of range, and the builders references to no column (design §4.1).</summary>
    /// <remarks>
    /// Preconditions:
    /// - A table of two numeric columns, and another table.
    ///
    /// Steps:
    /// - Make densities, moving averages, and 2D bins of each value out of range; read the defaults; add each builder's
    ///   layer of a default reference and of another table's column.
    ///
    /// Expected:
    /// - ArgumentOutOfRangeException for the values, ArgumentException for an axis's density bins and the references, and
    ///   the defaults of design §4.1.
    /// </remarks>
    [Fact]
    public void Builders_CheckTheirArguments()
    {
        Action[] outOfRange =
        [
            () => Stat.Density(bandwidth: 0),
            () => Stat.Density(bandwidth: -1),
            () => Stat.Density(bandwidth: double.NaN),
            () => Stat.Density(bandwidth: double.PositiveInfinity),
            () => Stat.Density(adjust: 0),
            () => Stat.Density(adjust: -1),
            () => Stat.Density(adjust: double.NaN),
            () => Stat.Density(adjust: double.PositiveInfinity),
            () => Stat.Density(points: 1),
            () => Stat.Density(points: 4_097),
            () => Stat.Density(output: (StatOutput)2),
            () => Stat.Smooth(0),
            () => Stat.Smooth(-1),
            () => Stat.Smooth(double.NaN),
            () => Stat.Smooth(double.PositiveInfinity),
            () => Stat.Smooth(1, points: 1),
            () => Stat.Smooth(1, points: 131_073),
            () => Stat.Bin2D(output: (StatOutput)2),
        ];
        foreach (var bad in outOfRange)
        {
            _ = Assert.Throws<ArgumentOutOfRangeException>(bad);
        }

        _ = Assert.Throws<ArgumentException>(() => Stat.Bin2D(Stat.Bin(output: StatOutput.Density)));
        _ = Assert.Throws<ArgumentException>(() => Stat.Bin2D(y: Stat.Bin(output: StatOutput.Density)));
        Assert.Equal(
            (null, 1.0, 512, StatOutput.Density),
            (Stat.Density().Bandwidth, Stat.Density().Adjust, Stat.Density().Points, Stat.Density().Output)
        );
        Assert.Equal(512, Stat.Smooth(1).Points);
        Assert.Equal((10, 10, StatOutput.Count), (Stat.Bin2D().X.MaxBins, Stat.Bin2D().Y.MaxBins, Stat.Bin2D().Output));
        _ = Stat.Density(points: 2);
        _ = Stat.Density(points: 4_096);
        _ = Stat.Smooth(1, points: 131_072);

        using var x = Column.Wrap(new double[] { 1, 2 });
        using var y = Column.Wrap(new double[] { 3, 4 });
        using var otherY = Column.Wrap(new double[] { 1, 2 });
        var t = DataTable.Create().Add("x", x).Add("y", y).Build();
        var other = DataTable.Create().Add("y", otherY).Build();
        Action[] wrongReferences =
        [
            () => Plot.Create(t).Density(other.Col<double>("y")),
            () => Plot.Create(t).Density(default(ColumnRef<double>)),
            () => Plot.Create(t).Smooth(t.Col<double>("x"), other.Col<double>("y"), 1),
            () => Plot.Create(t).Smooth(default(ColumnRef<double>), t.Col<double>("y"), 1),
            () => Plot.Create(t).Bin2D(t.Col<double>("x"), other.Col<double>("y")),
            () => Plot.Create(t).Bin2D(default(ColumnRef<double>), t.Col<double>("y")),
        ];
        foreach (var bad in wrongReferences)
        {
            _ = Assert.Throws<ArgumentException>(bad);
        }

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Plot.Create(t).Smooth(t.Col<double>("x"), t.Col<double>("y"), 0));
        var built = Plot.Create(t)
            .Density(t.Col<double>("x"))
            .Smooth(t.Col<double>("x"), t.Col<double>("y"), 2)
            .Bin2D(t.Col<double>("x"), t.Col<double>("y"))
            .Build();
        Assert.IsType<DensityStatSpec>(Assert.IsType<LineLayerSpec>(built.Layers[0]).Stat);
        Assert.True(((LineLayerSpec)built.Layers[0]).Y.IsDefault);
        Assert.Equal(2, Assert.IsType<SmoothStatSpec>(built.Layers[1].Stat).Window);
        var rect = Assert.IsType<RectLayerSpec>(built.Layers[2]);
        Assert.True(rect.XMax.IsDefault && rect.YMax.IsDefault && rect.Stat is Bin2DStatSpec);
    }

    /// <summary>
    /// The compile refuses each stat of row G5c a geom does not take and the channels and values it does not take (design
    /// §4.1, §4.8), and draws 2D bins up to the rect layer's 131,072 cells.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A table of numbers, longs-free doubles, a category, and a numeric color column; tables of 256 by 512 and 257 by 512
    ///   bins of a step of 1.
    ///
    /// Steps:
    /// - Render each refused spec; render the specs at the limit.
    ///
    /// Expected:
    /// - ArgumentException naming the layer for each refused one (the time scale's own message for a column that is not of
    ///   longs); 256 × 512 cells drawn, 257 × 512 refused.
    /// </remarks>
    [Fact]
    public void Compile_RefusesWhatTheStatsDoNotTake()
    {
        using var pool = new PlotWorkerPool(1);
        using var a = Column.Wrap(new double[] { 1, 2, 3, 4 });
        using var b = Column.Wrap(new double[] { 2, 3, 4, 5 });
        var t = DataTable.Create().Add("a", a).Add("b", b).Add("c", Column.Categorical(["p", "q", "p", "q"])).Build();
        var (n, m, c) = (t.Col<double>("a").ToColumnRef(), t.Col<double>("b").ToColumnRef(), t.Cat("c").ToColumnRef());
        LayerSpec[] refused =
        [
            new BarLayerSpec { X = n, Stat = Stat.Density() },
            new StepLayerSpec { X = n, Stat = Stat.Density() },
            new BarLayerSpec
            {
                X = n,
                Y = m,
                Stat = Stat.Smooth(1),
            },
            new StepLayerSpec
            {
                X = n,
                Y = m,
                Stat = Stat.Smooth(1),
            },
            new AreaLayerSpec
            {
                X = n,
                Y = m,
                Stat = Stat.Smooth(1),
            },
            new BarLayerSpec { X = n, Stat = Stat.Bin2D() },
            new LineLayerSpec { X = n, Stat = Stat.Bin2D() },
            new LineLayerSpec { X = c, Stat = Stat.Density() },
            new LineLayerSpec
            {
                X = n,
                Y = m,
                Stat = Stat.Density(),
            },
            new LineLayerSpec { X = n, Stat = Stat.Smooth(1) },
            new LineLayerSpec { X = n },
            new RectLayerSpec { XMin = n, YMin = m },
            new RectLayerSpec { YMin = m, Stat = Stat.Bin2D() },
            new RectLayerSpec { XMin = n, Stat = Stat.Bin2D() },
            new RectLayerSpec
            {
                XMin = n,
                XMax = m,
                YMin = m,
                Stat = Stat.Bin2D(),
            },
            new RectLayerSpec
            {
                XMin = n,
                YMin = m,
                YMax = n,
                Stat = Stat.Bin2D(),
            },
            new RectLayerSpec
            {
                XMin = n,
                YMin = m,
                Fill = Colors.Black,
                Stat = Stat.Bin2D(),
            },
            new RectLayerSpec
            {
                XMin = n,
                YMin = m,
                ColorBy = c,
                Stat = Stat.Bin2D(),
            },
            new RectLayerSpec
            {
                XMin = n,
                YMin = m,
                FillBy = n,
                Stat = Stat.Bin2D(),
            },
            new RectLayerSpec
            {
                XMin = n,
                YMin = m,
                Position = Position.Stack,
                Stat = Stat.Bin2D(),
            },
            new LineLayerSpec
            {
                X = n,
                Position = Position.Stack,
                Stat = Stat.Density(),
            },
            new LineLayerSpec
            {
                X = n,
                Y = m,
                Position = Position.Stack,
                Stat = Stat.Smooth(1),
            },
            new AreaLayerSpec
            {
                X = n,
                Baseline = 1,
                Stat = Stat.Density(),
            },
            new LineLayerSpec { X = n, Stat = Stat.Density() with { Bandwidth = 0 } },
            new LineLayerSpec { X = n, Stat = Stat.Density() with { Adjust = double.NaN } },
            new LineLayerSpec { X = n, Stat = Stat.Density() with { Points = 4_097 } },
            new LineLayerSpec { X = n, Stat = Stat.Density() with { Output = (StatOutput)2 } },
            new LineLayerSpec
            {
                X = n,
                Y = m,
                Stat = Stat.Smooth(1) with { Window = -1 },
            },
            new LineLayerSpec
            {
                X = n,
                Y = m,
                Stat = Stat.Smooth(1) with { Points = 1 },
            },
            new RectLayerSpec
            {
                XMin = n,
                YMin = m,
                Stat = Stat.Bin2D() with { X = Stat.Bin(output: StatOutput.Density) },
            },
            new RectLayerSpec
            {
                XMin = n,
                YMin = m,
                Stat = Stat.Bin2D() with { Y = Stat.Bin() with { MaxBins = 0 } },
            },
            new RectLayerSpec
            {
                XMin = n,
                YMin = m,
                Stat = Stat.Bin2D() with { Output = (StatOutput)2 },
            },
            new LineLayerSpec
            {
                X = n,
                ColorBy = n,
                Stat = Stat.Density(),
            },
        ];
        foreach (var layer in refused)
        {
            using var plan = TestPlans.Create(pool, Size, Size);
            var thrown = Assert.Throws<ArgumentException>(() => plan.Render(Spec(t, layer)));
            Assert.Contains("layer 0", thrown.Message, StringComparison.OrdinalIgnoreCase);
        }

        // 2D bins fill by numbers: an ordinal color scale is refused (design §4.2).
        using (var plan = TestPlans.Create(pool, Size, Size))
        {
            var cells = new RectLayerSpec
            {
                XMin = n,
                YMin = m,
                Stat = Stat.Bin2D(),
            };
            _ = Assert.Throws<ArgumentException>(() => plan.Render(Spec(t, cells) with { ScaleColor = Scale.Ordinal() }));
        }

        // A time axis reads columns of longs only (the time scale's check, row G2).
        LayerSpec[] timed =
        [
            new LineLayerSpec { X = n, Stat = Stat.Density() },
            new LineLayerSpec
            {
                X = n,
                Y = m,
                Stat = Stat.Smooth(1),
            },
        ];
        foreach (var layer in timed)
        {
            using var plan = TestPlans.Create(pool, Size, Size);
            _ = Assert.Throws<ArgumentException>(() => plan.Render(Spec(t, layer) with { ScaleX = Scale.Time() }));
        }

        using (var plan = TestPlans.Create(pool, Size, Size))
        {
            var cells = new RectLayerSpec
            {
                XMin = n,
                YMin = m,
                Stat = Stat.Bin2D(),
            };
            _ = Assert.Throws<ArgumentException>(() => plan.Render(Spec(t, cells) with { ScaleY = Scale.Time() }));
        }

        // 256 × 512 bins are 131,072 cells; 257 × 512 are too many.
        foreach (var (right, fits) in (ReadOnlySpan<(double, bool)>)[(255.5, true), (256.5, false)])
        {
            using var xs = Column.Wrap(new double[] { 0, right });
            using var ys = Column.Wrap(new double[] { 0, 511.5 });
            var many = DataTable.Create().Add("x", xs).Add("y", ys).Build();
            using var plan = TestPlans.Create(pool, Size, Size);
            var spec = Spec(
                many,
                new RectLayerSpec
                {
                    XMin = many.Col<double>("x"),
                    YMin = many.Col<double>("y"),
                    Stat = Stat.Bin2D(Stat.Bin(step: 1), Stat.Bin(step: 1)),
                }
            );
            if (fits)
            {
                _ = plan.Render(spec);
            }
            else
            {
                _ = Assert.Throws<ArgumentException>(() => plan.Render(spec));
            }
        }
    }

    /// <summary>
    /// A density is R's (design §4.1): the bandwidth within 1e-12 of R's <c>bw.nrd0</c> or the one given times the adjust,
    /// and the density at each point within 1e-12 of the curve's greatest value of R 4.6.2's <c>density()</c>, over data of
    /// one and two peaks, two values, one value repeated (the bandwidth from the first value's magnitude, or 1), an IQR of 0,
    /// grids of 1,024 and 4,096, ties, and a log x with values at and below 0 left out.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The cases of <see cref="RDensities"/>: integer formulas of the same doubles in R and here; the log case's column is
    ///   10^v with 0 and −1 added.
    ///
    /// Steps:
    /// - Render a density line of each case; read the stat's bandwidth and derived table.
    ///
    /// Expected:
    /// - R's bandwidth, range, x, and y at the points of the table.
    /// </remarks>
    [Fact]
    public void Density_IsRs()
    {
        var reference = ParseReference();
        using var pool = new PlotWorkerPool(2);
        foreach (var (name, (bandwidth, from, to, points)) in reference)
        {
            var (values, stat, log) = Case(name);
            using var x = Column.Wrap(values);
            var t = DataTable.Create().Add("x", x).Build();
            using var plan = TestPlans.Create(pool, Size, Size);
            _ = plan.Render(
                Spec(t, new LineLayerSpec { X = t.Col<double>("x"), Stat = stat }) with
                {
                    ScaleX = log ? Scale.Log() : Scale.Linear(),
                    ScaleY = Scale.Linear(),
                }
            );
            var layer = Assert.IsType<StatPlan>(plan.Layers[0]);
            Assert.Equal(bandwidth, layer.Bandwidths[0], bandwidth * 1e-12);
            var xs = Values(layer.Derived, 0);
            var ys = Values(layer.Derived, 1);
            Assert.Equal(stat.Points, xs.Length);
            var top = ys.Max();
            foreach (var (point, rx, ry) in points)
            {
                var at = log ? Math.Log10(xs[point]) : xs[point];
                Assert.True(Math.Abs(at - rx) <= 1e-12 * Math.Max(1, Math.Abs(rx)), $"{name} {point}: x {at} against {rx}");
                Assert.True(Math.Abs(ys[point] - ry) <= 1e-12 * top, $"{name} {point}: y {ys[point]} against {ry}");
            }

            var first = log ? Math.Log10(xs[0]) : xs[0];
            var last = log ? Math.Log10(xs[^1]) : xs[^1];
            Assert.Equal(from, first, 1e-12 * Math.Max(1, Math.Abs(from)));
            Assert.Equal(to, last, 1e-12 * Math.Max(1, Math.Abs(to)));
        }
    }

    /// <summary>
    /// A density is a curve a group over the layer's range (design §4.1): each group's own bandwidth, a group of one value
    /// not drawn with the automatic bandwidth and drawn with a given one, a count the density times the group's values, and
    /// a stacked area reaching the greatest total.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 300 values in group a, 200 spread wider in group b, 1 value in group c.
    ///
    /// Steps:
    /// - Render a density line by group with the automatic bandwidth, with a bandwidth of 0.2, and an area of counts stacked.
    ///
    /// Expected:
    /// - The same x for each group, over the least and greatest of all values; group c absent, then drawn; counts n times the
    ///   densities; the stacked y domain's top the greatest sum of the counts.
    /// </remarks>
    [Fact]
    public void Density_IsACurveAGroupOverTheLayersRange()
    {
        using var pool = new PlotWorkerPool(2);
        var values = Near(300).Concat(Near(200).Select(v => (v * 2) + 1)).Append(9.0).ToArray();
        string?[] groups = [.. Enumerable.Repeat<string?>("a", 300).Concat(Enumerable.Repeat<string?>("b", 200)).Append("c")];
        using var x = Column.Wrap(values);
        var t = DataTable.Create().Add("x", x).Add("g", Column.Categorical(groups, ["a", "b", "c"])).Build();
        var g = t.Cat("g").ToColumnRef();

        using var plan = TestPlans.Create(pool, Size, Size);
        _ = plan.Render(
            Linear(
                t,
                new LineLayerSpec
                {
                    X = t.Col<double>("x"),
                    Stat = Stat.Density(points: 50),
                    ColorBy = g,
                }
            )
        );
        var layer = Assert.IsType<StatPlan>(plan.Layers[0]);
        var (xs, ys, codes) = (Values(layer.Derived, 0), Values(layer.Derived, 1), Codes(layer.Derived));
        Assert.Equal(100, xs.Length);
        Assert.DoesNotContain(2, codes);
        Assert.Equal(values.Min(), xs[0], 12);
        Assert.Equal(values.Max(), xs[^1], 12);
        Assert.True(layer.Bandwidths[0] != layer.Bandwidths[1] && layer.Bandwidths[2] == 0);
        for (var i = 0; i < xs.Length; i += 2)
        {
            Assert.Equal(xs[i], xs[i + 1]);
        }

        _ = plan.Render(
            Linear(
                t,
                new LineLayerSpec
                {
                    X = t.Col<double>("x"),
                    Stat = Stat.Density(0.2, points: 50),
                    ColorBy = g,
                }
            )
        );
        layer = Assert.IsType<StatPlan>(plan.Layers[0]);
        Assert.Equal(150, Values(layer.Derived, 0).Length);
        var densities = Values(layer.Derived, 1);

        _ = plan.Render(
            Linear(
                t,
                new LineLayerSpec
                {
                    X = t.Col<double>("x"),
                    Stat = Stat.Density(0.2, points: 50, output: StatOutput.Count),
                    ColorBy = g,
                }
            )
        );
        layer = Assert.IsType<StatPlan>(plan.Layers[0]);
        var counts = Values(layer.Derived, 1);
        int[] sizes = [300, 200, 1];
        var gs = Codes(layer.Derived);
        for (var i = 0; i < counts.Length; i++)
        {
            Assert.Equal(densities[i] * sizes[gs[i]], counts[i], 12);
        }

        using var stacked = TestPlans.Create(pool, Size, Size);
        _ = stacked.Render(
            Linear(
                t,
                new AreaLayerSpec
                {
                    X = t.Col<double>("x"),
                    Stat = Stat.Density(0.2, points: 50, output: StatOutput.Count),
                    FillBy = g,
                }
            )
        );
        var totals = Enumerable.Range(0, 50).Select(p => counts[(p * 3)..((p * 3) + 3)].Sum()).Max();
        Assert.Equal(totals, Ends(stacked.YDomain).Max, 9);
        Assert.Equal(0, Ends(stacked.YDomain).Min);
    }

    /// <summary>
    /// A moving average is each window's mean (design §4.1): against brute force over the rows whose x lies in [point − w /
    /// 2, point + w / 2), over unsorted x with NaN y, infinite x, values on the windows' edges, a gap of no rows (NaN), two
    /// groups, and a log x.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 3,000 rows: x from 0 to 100 unsorted, many on whole numbers, none in (40, 60); y with every 97th NaN; one +∞ and one
    ///   −∞ x; two groups. Windows of 4 at 101 points (on the whole numbers).
    ///
    /// Steps:
    /// - Render a moving average by group, and one on a log x of 10^(x / 50); sum each window by hand in decimal.
    ///
    /// Expected:
    /// - Each point's mean within 1e-12 of the sum's, NaN where the window has no row, on both scales.
    /// </remarks>
    [Fact]
    public void Smooth_IsEachWindowsMean()
    {
        using var pool = new PlotWorkerPool(2);
        const int Rows = 3_000;
        var xs = new double[Rows];
        var ys = new double[Rows];
        var gs = new string?[Rows];
        for (var i = 0; i < Rows; i++)
        {
            var v = i % 3 == 0 ? (double)((i * 37) % 101) : U(i, 5) * 100;
            xs[i] = v is > 40 and < 60 ? v - 20 : v;
            ys[i] = i % 97 == 0 ? double.NaN : (U(i, 6) * 10) - 5;
            gs[i] = i % 2 == 0 ? "a" : "b";
        }

        (xs[1], xs[2]) = (double.PositiveInfinity, double.NegativeInfinity);
        xs[3] = 0;
        xs[4] = 100;
        foreach (var log in (ReadOnlySpan<bool>)[false, true])
        {
            var column = log ? xs.Select(v => Math.Pow(10, v / 50)).ToArray() : xs;
            using var x = Column.Wrap(column);
            using var y = Column.Wrap(ys);
            var t = DataTable.Create().Add("x", x).Add("y", y).Add("g", Column.Categorical(gs, ["a", "b"])).Build();
            using var plan = TestPlans.Create(pool, Size, Size);
            var window = log ? 4.0 / 50 : 4;
            _ = plan.Render(
                Spec(
                    t,
                    new LineLayerSpec
                    {
                        X = t.Col<double>("x"),
                        Y = t.Col<double>("y"),
                        Stat = Stat.Smooth(window, 101),
                        ColorBy = t.Cat("g").ToColumnRef(),
                    }
                ) with
                {
                    ScaleX = log ? Scale.Log() : Scale.Linear(),
                }
            );
            var layer = Assert.IsType<StatPlan>(plan.Layers[0]);
            var (means, codes) = (Values(layer.Derived, 1), Codes(layer.Derived));
            Assert.Equal(202, means.Length);
            var space = column.Select(v => log ? Math.Log10(v) : v).ToArray();
            var finite = space.Where(double.IsFinite).ToArray();
            var (from, to) = (finite.Min(), finite.Max());
            var empty = 0;
            for (var p = 0; p < 101; p++)
            {
                var middle = from + (p * (to - from) / 100);
                var (low, high) = (middle - (window / 2), middle + (window / 2));
                for (var k = 0; k < 2; k++)
                {
                    var (sum, n) = (0m, 0);
                    for (var i = 0; i < Rows; i++)
                    {
                        if (gs[i] == (k == 0 ? "a" : "b") && double.IsFinite(space[i]) && !double.IsNaN(ys[i]) && space[i] >= low && space[i] < high)
                        {
                            (sum, n) = (sum + (decimal)ys[i], n + 1);
                        }
                    }

                    var at = (p * 2) + k;
                    Assert.Equal(k, codes[at]);
                    if (n == 0)
                    {
                        Assert.True(double.IsNaN(means[at]), $"{log} point {p} group {k}");
                        empty++;
                    }
                    else
                    {
                        var expected = (double)(sum / n);
                        Assert.True(
                            Math.Abs(means[at] - expected) <= 1e-12 * Math.Max(1, Math.Abs(expected)),
                            $"{log} point {p}: {means[at]} against {expected}"
                        );
                    }
                }
            }

            Assert.InRange(empty, 2, 60);
        }
    }

    /// <summary>A moving average over a time x averages in the column's unit and draws its points as longs (design §4.1, §5.5.3).</summary>
    /// <remarks>
    /// Preconditions:
    /// - 48 hourly .NET ticks from 2024-01-01 with y the hour's number; a time scale; windows of 3 hours at 48 points.
    ///
    /// Steps:
    /// - Render a moving average; read the derived table.
    ///
    /// Expected:
    /// - The x column of longs at the hours; each mean the hours in [t − 1.5 h, t + 1.5 h): 0.5 and 46.5 at the ends, the hour
    ///   itself between.
    /// </remarks>
    [Fact]
    public void Smooth_OnATimeX_AveragesInTheColumnsUnit()
    {
        using var pool = new PlotWorkerPool(1);
        var start = new DateTime(2024, 1, 1).Ticks;
        using var x = Column.Wrap(Enumerable.Range(0, 48).Select(h => start + (h * TimeSpan.TicksPerHour)).ToArray());
        using var y = Column.Wrap(Enumerable.Range(0, 48).Select(h => (double)h).ToArray());
        var t = DataTable.Create().Add("x", x).Add("y", y).Build();
        using var plan = TestPlans.Create(pool, Size, Size);
        _ = plan.Render(
            Spec(
                t,
                new LineLayerSpec
                {
                    X = t.Col<long>("x"),
                    Y = t.Col<double>("y"),
                    Stat = Stat.Smooth(3 * TimeSpan.TicksPerHour, 48),
                }
            ) with
            {
                ScaleX = Scale.Time(),
                ScaleY = Scale.Linear(),
            }
        );
        var layer = Assert.IsType<StatPlan>(plan.Layers[0]);
        var times = Assert.IsType<Column<long>>(layer.Derived.GetColumn(0));
        var means = Values(layer.Derived, 1);
        for (var h = 0; h < 48; h++)
        {
            Assert.Equal(start + (h * TimeSpan.TicksPerHour), times.GetChunk(0)[h]);
            var expected =
                h == 0 ? 0.5
                : h == 47 ? 46.5
                : h;
            Assert.Equal(expected, means[h], 12);
        }
    }

    /// <summary>
    /// 2D bins count each cell (design §4.1): against brute force, the axes' edges those of one-axis bins, no cell of 0
    /// drawn, densities over the cells' areas summing to 1, and the color domain the drawn cells' values.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 3,000 rows: x in [0, 10), y in [0, 5), some on the edges of bins of 1 and 0.5, one NaN y, one +∞ x; a corner of no
    ///   rows.
    ///
    /// Steps:
    /// - Render 2D bins of counts and of densities; count the cells by hand.
    ///
    /// Expected:
    /// - The cells' counts by hand, the rects only of cells with rows, Σ density · area = 1, and the color domain the least
    ///   and greatest count.
    /// </remarks>
    [Fact]
    public void Bin2D_CountsEachCell()
    {
        using var pool = new PlotWorkerPool(2);
        const int Rows = 3_000;
        var xs = new double[Rows];
        var ys = new double[Rows];
        for (var i = 0; i < Rows; i++)
        {
            xs[i] = i % 5 == 0 ? i % 10 : U(i, 7) * 10;
            ys[i] = i % 7 == 0 ? (i % 10) / 2.0 : U(i, 8) * 5;
            if (xs[i] > 8 && ys[i] > 4)
            {
                ys[i] -= 2;
            }
        }

        (ys[1], xs[2]) = (double.NaN, double.PositiveInfinity);
        using var x = Column.Wrap(xs);
        using var y = Column.Wrap(ys);
        var t = DataTable.Create().Add("x", x).Add("y", y).Build();
        var (binX, binY) = (Stat.Bin(step: 1), Stat.Bin(step: 0.5));
        var fx = xs.Where(double.IsFinite).ToArray();
        var fy = ys.Where(double.IsFinite).ToArray();
        var ex = StatWork.Edges(fx.Min(), fx.Max(), binX, 0);
        var ey = StatWork.Edges(fy.Min(), fy.Max(), binY, 0);
        var expected = new long[(ex.Length - 1) * (ey.Length - 1)];
        for (var i = 0; i < Rows; i++)
        {
            var (a, b) = (BinOf(ex, xs[i]), BinOf(ey, ys[i]));
            if (a >= 0 && b >= 0)
            {
                expected[(b * (ex.Length - 1)) + a]++;
            }
        }

        foreach (var output in (ReadOnlySpan<StatOutput>)[StatOutput.Count, StatOutput.Density])
        {
            using var plan = TestPlans.Create(pool, Size, Size);
            _ = plan.Render(
                Spec(
                    t,
                    new RectLayerSpec
                    {
                        XMin = t.Col<double>("x"),
                        YMin = t.Col<double>("y"),
                        Stat = Stat.Bin2D(binX, binY, output),
                    }
                ) with
                {
                    ScaleX = Scale.Linear(),
                    ScaleY = Scale.Linear(),
                }
            );
            var layer = Assert.IsType<StatPlan>(plan.Layers[0]);
            var (x0, x1, y0, y1, v) = (
                Values(layer.Derived, 0),
                Values(layer.Derived, 1),
                Values(layer.Derived, 2),
                Values(layer.Derived, 3),
                Values(layer.Derived, 4)
            );
            Assert.Equal(expected.Count(c => c > 0), v.Length);
            Assert.Contains(0, expected);
            var n = expected.Sum();
            var area = 0.0;
            for (var r = 0; r < v.Length; r++)
            {
                var (a, b) = (Array.IndexOf(ex, x0[r]), Array.IndexOf(ey, y0[r]));
                Assert.Equal((ex[a + 1], ey[b + 1]), (x1[r], y1[r]));
                var count = expected[(b * (ex.Length - 1)) + a];
                Assert.Equal(output == StatOutput.Count ? count : count / (n * (x1[r] - x0[r]) * (y1[r] - y0[r])), v[r], 12);
                area += v[r] * (x1[r] - x0[r]) * (y1[r] - y0[r]);
            }

            if (output == StatOutput.Density)
            {
                Assert.Equal(1, area, 12);
            }
            else
            {
                Assert.Equal((v.Min(), v.Max()), Ends(plan.Color!.Domain));
                Assert.Equal((ex[0], ex[^1]), Ends(plan.XDomain));
                Assert.Equal((ey[0], ey[^1]), Ends(plan.YDomain));
            }
        }
    }

    /// <summary>2D bins over a time y take its calendar boundaries (design §4.1): the y edges are those of a time histogram.</summary>
    /// <remarks>
    /// Preconditions:
    /// - .NET ticks from 2020-01-15 to 2021-12-20 on y, numbers on x; a time y scale; at most 10 y bins.
    ///
    /// Steps:
    /// - Render 2D bins; read the cells' y edges.
    ///
    /// Expected:
    /// - Longs on the first day of quarters, the edges <c>TimeTicks.BinEdges</c> gives.
    /// </remarks>
    [Fact]
    public void Bin2D_OnATimeY_TakesCalendarBoundaries()
    {
        using var pool = new PlotWorkerPool(1);
        var (min, max) = (new DateTime(2020, 1, 15).Ticks, new DateTime(2021, 12, 20).Ticks);
        using var y = Column.Wrap(Enumerable.Range(0, 200).Select(i => min + ((max - min) / 199 * i)).ToArray());
        using var x = Column.Wrap(Enumerable.Range(0, 200).Select(i => (double)(i % 4)).ToArray());
        var t = DataTable.Create().Add("x", x).Add("y", y).Build();
        using var plan = TestPlans.Create(pool, Size, Size);
        _ = plan.Render(
            Spec(
                t,
                new RectLayerSpec
                {
                    XMin = t.Col<double>("x"),
                    YMin = t.Col<long>("y"),
                    Stat = Stat.Bin2D(),
                }
            ) with
            {
                ScaleX = Scale.Linear(),
                ScaleY = Scale.Time(),
            }
        );
        var layer = Assert.IsType<StatPlan>(plan.Layers[0]);
        var lows = Assert.IsType<Column<long>>(layer.Derived.GetColumn(2)).GetChunk(0).ToArray().Distinct().Order().ToArray();
        var edges = TimeTicks.BinEdges(min, y.GetChunk(0)[^1], Scale.Time(), 10);
        Assert.Equal(edges[..^1], lows);
    }

    /// <summary>
    /// The passes of row G5c do not depend on the number of workers (design §5.5.3): a density's spread and grid, a moving
    /// average's sums, and 2D bins' counts join in fixed groups of chunks, in their order.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 3 · 2^20 rows (3 chunks) of values of many magnitudes in 3 groups.
    ///
    /// Steps:
    /// - Run each pass with 1 worker and with 4, three times.
    ///
    /// Expected:
    /// - The same results, bit for bit.
    /// </remarks>
    [Fact]
    public void Passes_AreAlikeOnAnyNumberOfWorkers()
    {
        const int Rows = 3 << 20;
        var random = new Random(13);
        var values = new double[Rows];
        var others = new double[Rows];
        for (var i = 0; i < Rows; i++)
        {
            values[i] = random.NextDouble() * Math.Pow(10, random.Next(-3, 3));
            others[i] = random.NextDouble() * 100;
        }

        using var x = Column.Wrap(values);
        using var y = Column.Wrap(others);
        var groups = Column.Categorical(
            Enumerable
                .Range(0, Rows)
                .Select(i =>
                    (string?)(
                        i % 3 == 0 ? "a"
                        : i % 3 == 1 ? "b"
                        : "c"
                    )
                )
                .ToArray(),
            ["a", "b", "c"]
        );
        double[] lo = [-1, -2, -3];
        double[] delta = [0.002, 0.003, 0.004];
        double[] edges = [.. Enumerable.Range(0, 101).Select(i => i * 10.0)];
        var axis = new StatWork.Axis2D(edges, true, 0, null);
        string Run(PlotWorkerPool pool)
        {
            var spread = StatWork.SpreadOf(x, groups, null, 0, pool);
            var grid = StatWork.SpreadOnGrids(x, groups, null, 0, lo, delta, 1024, pool);
            var sums = StatWork.WindowSums(y, x, groups, null, 0, edges, pool);
            var cells = StatWork.Bin2D(y, y, axis, axis, pool);
            return string.Join(
                ",",
                spread
                    .Mean.Concat(spread.Squares)
                    .Concat(grid)
                    .Concat(sums)
                    .Concat(cells.Select(c => (double)c))
                    .Select(v => BitConverter.DoubleToInt64Bits(v))
            );
        }

        using var one = new PlotWorkerPool(1);
        var expected = Run(one);
        foreach (var workers in (int[])[4, 4, 4])
        {
            using var pool = new PlotWorkerPool(workers);
            Assert.Equal(expected, Run(pool));
        }
    }

    /// <summary>
    /// The layers of row G5c learn from their tables (design §4.2): a density's and a moving average's x over the range, a
    /// density's y from 0, 2D bins' x and y over their edges.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The values 1 to 9 on x and 11 to 19 on y.
    ///
    /// Steps:
    /// - Render a density, a moving average, and 2D bins of a step of 2 with learned domains.
    ///
    /// Expected:
    /// - The x domain [1, 9] for the first two and the y domain from 0 for the density; [0, 10] × [10, 20] for the bins.
    /// </remarks>
    [Fact]
    public void Layers_LearnFromTheirTables()
    {
        using var pool = new PlotWorkerPool(1);
        using var x = Column.Wrap(Enumerable.Range(1, 9).Select(i => (double)i).ToArray());
        using var y = Column.Wrap(Enumerable.Range(11, 9).Select(i => (double)i).ToArray());
        var t = DataTable.Create().Add("x", x).Add("y", y).Build();
        using (var plan = TestPlans.Create(pool, Size, Size))
        {
            _ = plan.Render(Linear(t, new LineLayerSpec { X = t.Col<double>("x"), Stat = Stat.Density() }));
            Assert.Equal((1.0, 9.0), Ends(plan.XDomain));
            Assert.Equal(0, Ends(plan.YDomain).Min);
        }

        using (var plan = TestPlans.Create(pool, Size, Size))
        {
            _ = plan.Render(
                Linear(
                    t,
                    new LineLayerSpec
                    {
                        X = t.Col<double>("x"),
                        Y = t.Col<double>("y"),
                        Stat = Stat.Smooth(2),
                    }
                )
            );
            Assert.Equal((1.0, 9.0), Ends(plan.XDomain));
            var (low, high) = Ends(plan.YDomain);
            Assert.InRange(low, 11, 12);
            Assert.InRange(high, 18, 19);
        }

        using (var plan = TestPlans.Create(pool, Size, Size))
        {
            _ = plan.Render(
                Linear(
                    t,
                    new RectLayerSpec
                    {
                        XMin = t.Col<double>("x"),
                        YMin = t.Col<double>("y"),
                        Stat = Stat.Bin2D(Stat.Bin(step: 2), Stat.Bin(step: 2)),
                    }
                )
            );
            Assert.Equal((0.0, 10.0), Ends(plan.XDomain));
            Assert.Equal((10.0, 20.0), Ends(plan.YDomain));
        }
    }

    /// <summary>
    /// A change of a stat of row G5c makes the layer again, a new node of the same values does not (design §4.8); a column's
    /// new version does; another y space makes 2D bins again, another y domain does not.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The values 1 to 9 on x and y.
    ///
    /// Steps:
    /// - Render each stat; render a new node of the same values, then other values; invalidate the x column; for 2D bins,
    ///   render another y domain, then a log y.
    ///
    /// Expected:
    /// - The same layer for the same values and another y domain; a new layer for other values, the invalidated column, and
    ///   the log y.
    /// </remarks>
    [Fact]
    public void Diff_MakesTheLayerAgainForOtherStats()
    {
        using var pool = new PlotWorkerPool(1);
        var values = Enumerable.Range(1, 9).Select(i => (double)i).ToArray();
        using var x = Column.Wrap(values);
        using var y = Column.Wrap(values.ToArray());
        var t = DataTable.Create().Add("x", x).Add("y", y).Build();
        (LayerSpec Layer, LayerSpec Other)[] cases =
        [
            (
                new LineLayerSpec { X = t.Col<double>("x"), Stat = Stat.Density() },
                new LineLayerSpec { X = t.Col<double>("x"), Stat = Stat.Density(adjust: 2) }
            ),
            (
                new LineLayerSpec
                {
                    X = t.Col<double>("x"),
                    Y = t.Col<double>("y"),
                    Stat = Stat.Smooth(2),
                },
                new LineLayerSpec
                {
                    X = t.Col<double>("x"),
                    Y = t.Col<double>("y"),
                    Stat = Stat.Smooth(3),
                }
            ),
            (
                new RectLayerSpec
                {
                    XMin = t.Col<double>("x"),
                    YMin = t.Col<double>("y"),
                    Stat = Stat.Bin2D(),
                },
                new RectLayerSpec
                {
                    XMin = t.Col<double>("x"),
                    YMin = t.Col<double>("y"),
                    Stat = Stat.Bin2D(y: Stat.Bin(maxBins: 4)),
                }
            ),
        ];
        foreach (var (layer, other) in cases)
        {
            using var plan = TestPlans.Create(pool, Size, Size);
            _ = plan.Render(Linear(t, layer));
            var first = plan.Layers[0];
            _ = plan.Render(Linear(t, layer with { Stat = layer.Stat! with { } }));
            Assert.Same(first, plan.Layers[0]);
            _ = plan.Render(Linear(t, other));
            Assert.NotSame(first, plan.Layers[0]);
            var second = plan.Layers[0];
            x.Invalidate();
            _ = plan.Render(Linear(t, other));
            Assert.NotSame(second, plan.Layers[0]);
            if (layer is RectLayerSpec)
            {
                var third = plan.Layers[0];
                _ = plan.Render(Linear(t, other) with { ScaleY = Scale.Linear().WithDomain(0, 7) });
                Assert.Same(third, plan.Layers[0]);
                _ = plan.Render(Linear(t, other) with { ScaleY = Scale.Log() });
                Assert.NotSame(third, plan.Layers[0]);
            }
        }
    }

    /// <summary>Once warm, a density, a moving average, and 2D bins drawn again allocate nothing (design §4.8).</summary>
    /// <remarks>
    /// Preconditions:
    /// - 500 values on x and y in 2 groups; each spec and a new node of it in turn.
    ///
    /// Steps:
    /// - Render each a few times, then five times more in turn.
    ///
    /// Expected:
    /// - 0 bytes on this thread.
    /// </remarks>
    [Fact]
    public void Warm_AllocatesNothing()
    {
        using var pool = new PlotWorkerPool(1);
        using var x = Column.Wrap(Near(500));
        using var y = Column.Wrap(Near(500).Select(v => v * 3).ToArray());
        var t = DataTable
            .Create()
            .Add("x", x)
            .Add("y", y)
            .Add("g", Column.Categorical([.. Enumerable.Range(0, 500).Select(i => (string?)(i % 2 == 0 ? "a" : "b"))], ["a", "b"]))
            .Build();
        var g = t.Cat("g").ToColumnRef();
        PlotSpec[] specs =
        [
            Linear(
                t,
                new LineLayerSpec
                {
                    X = t.Col<double>("x"),
                    Stat = Stat.Density(),
                    ColorBy = g,
                }
            ),
            Linear(
                t,
                new AreaLayerSpec
                {
                    X = t.Col<double>("x"),
                    Stat = Stat.Density(),
                    FillBy = g,
                }
            ),
            Linear(
                t,
                new LineLayerSpec
                {
                    X = t.Col<double>("x"),
                    Y = t.Col<double>("y"),
                    Stat = Stat.Smooth(0.5),
                    ColorBy = g,
                }
            ),
            Linear(
                t,
                new RectLayerSpec
                {
                    XMin = t.Col<double>("x"),
                    YMin = t.Col<double>("y"),
                    Stat = Stat.Bin2D(),
                }
            ),
        ];
        foreach (var spec in specs)
        {
            using var plan = TestPlans.Create(pool, Size, Size);
            var renewed = spec with { Layers = [.. spec.Layers.Select(l => l with { })] };
            for (var i = 0; i < 3; i++)
            {
                _ = plan.Render(spec);
                _ = plan.Render(renewed);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 5; i++)
            {
                _ = plan.Render(spec);
                _ = plan.Render(renewed);
            }

            var spent = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(spent == 0, $"spec {Array.IndexOf(specs, spec)}: {spent}");
        }
    }

    /// <summary>A value of the integer formula the R script shares: exact in both.</summary>
    private static double U(long i, long k) => (((i * 2654435761L) + (k * 40503)) % 10007) / 10007.0;

    /// <summary>The sum of four of <see cref="U"/>, near normal: as the R script adds them, left to right.</summary>
    private static double[] Near(int n) => [.. Enumerable.Range(0, n).Select(i => U(i, 1) + U(i, 2) + U(i, 3) + U(i, 4))];

    private static (double[] Values, DensityStatSpec Stat, bool Log) Case(string name) =>
        name switch
        {
            "normal" => (Near(1000), Stat.Density(), false),
            "bimodal" => (
                [.. Enumerable.Range(0, 1000).Select(i => (U(i, 1) + U(i, 2) + U(i, 3)) is var s && i % 3 == 0 ? s + 4 : s)],
                Stat.Density(),
                false
            ),
            "two" => ([1, 3], Stat.Density(), false),
            "same" => ([.. Enumerable.Repeat(5.0, 10)], Stat.Density(), false),
            "zeros" => (new double[10], Stat.Density(), false),
            "flat" => ([2, 2, 2, 2, 2, 2, 2, 2, 7, 9], Stat.Density(), false),
            "adjust" => (Near(300), Stat.Density(adjust: 2), false),
            "grid1024" => (Near(300), Stat.Density(points: 600), false),
            "grid4096" => (Near(300), Stat.Density(points: 4096), false),
            "ties" => ([.. Near(500).Select(v => Math.Floor(v * 4) / 4)], Stat.Density(), false),
            "given" => (Near(200), Stat.Density(0.05), false),
            _ => ([.. Near(400).Select(v => Math.Pow(10, v)), 0, -1], Stat.Density(), true),
        };

    private static Dictionary<string, (double Bandwidth, double From, double To, List<(int Point, double X, double Y)> Points)> ParseReference()
    {
        var cases = new Dictionary<string, (double, double, double, List<(int, double, double)>)>();
        foreach (var line in RDensities.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ');
            if (parts[1] == "bw")
            {
                cases[parts[0]] = (Number(parts[2]), Number(parts[4]), Number(parts[6]), []);
            }
            else
            {
                cases[parts[0]].Item4.Add((int.Parse(parts[1], CultureInfo.InvariantCulture), Number(parts[2]), Number(parts[3])));
            }
        }

        return cases;

        static double Number(string text) => double.Parse(text, CultureInfo.InvariantCulture);
    }

    private static PlotSpec Linear(DataTable table, LayerSpec layer) => Spec(table, layer) with { ScaleX = Scale.Linear(), ScaleY = Scale.Linear() };

    private static double[] Values(DataTable table, int column)
    {
        var c = table.GetColumn(column);
        var values = new double[c.Length];
        c.ReadChunk(0, 0, values, 0);
        return values;
    }

    private static int[] Codes(DataTable table) => ((CategoricalColumn)table.GetColumn(table.ColumnCount - 1)).GetCodes(0).ToArray();

    private static int BinOf(double[] edges, double v)
    {
        if (!double.IsFinite(v) || v < edges[0] || v > edges[^1])
        {
            return -1;
        }

        for (var b = 0; b < edges.Length - 2; b++)
        {
            if (v < edges[b + 1])
            {
                return b;
            }
        }

        return edges.Length - 2;
    }
}
