using System.Diagnostics;
using HyperPlot.Interaction;
using HyperPlot.Rendering;
using HyperPlot.Testing;

namespace HyperPlot.Tests;

/// <summary>
/// The switch of an Auto point layer by the predicted GPU time of its raw points (design §5.5.2 "`Auto` の切り替え",
/// "予測時間", "点の数の上限", "最大のビン", "密度タイルが付く前", "入れ替わりを抑える", "水準が替わるところ", §10 R3b2 row).
/// </summary>
[Collection("Large data")]
public sealed class AutoSwitchTests
{
    private static readonly PanelId Panel = new(0, 0);
    private static readonly string[] Three = ["a", "b", "c"];
    private static readonly string[] Two = ["p", "q"];

    /// <summary>The 41 scenes of design §5.5.2 "製品の計測", the prototype's sweep 8 measured again on the product's NativeAOT build (Issue #624), in its order.</summary>
    private static readonly DesignScene[] Scenes =
    [
        new("d1-uniform", 10_000_000, 1, 9.58, 7.80, Fit.In, false),
        new("d1-s10-p6", 10_000_000, 1, 28.65, 7.86, Fit.In, false),
        new("d1-s10", 10_000_000, 1, 109.88, 7.98, Fit.In, false),
        new("d6-n5e6-s8.49", 5_000_000, 6, 68.72, 9.28, Fit.In, false),
        new("d10-n5e6", 5_000_000, 10, 5.92, 9.85, Fit.In, false),
        new("d6-n5e6-s7", 5_000_000, 6, 101.09, 10.00, Fit.In, false),
        new("d6-uniform-p8", 10_000_000, 6, 2.76, 10.51, Fit.In, false),
        new("d6-n5e6-s6", 5_000_000, 6, 137.60, 10.65, Fit.In, false),
        new("d6-n5e6-s5", 5_000_000, 6, 198.14, 11.52, Fit.In, false),
        new("d12-n5e6", 5_000_000, 12, 5.92, 12.15, Fit.In, false),
        new("d6-uniform", 10_000_000, 6, 9.58, 12.17, Fit.In, false),
        new("d6-uniform-facet6", 10_000_000, 6, 12.03, 12.57, Fit.In, false),
        new("d4-s9", 10_000_000, 4, 118.10, 12.74, Fit.In, false),
        new("d4-s8", 10_000_000, 4, 149.48, 13.20, Fit.In, false),
        new("d4-s7.5", 10_000_000, 4, 170.07, 13.47, Fit.In, false),
        new("d6-n7.5e6-s10", 7_500_000, 6, 83.60, 13.67, Fit.In, false),
        new("d6-s10-p8", 10_000_000, 6, 21.61, 13.75, Fit.In, false),
        new("d4-s7", 10_000_000, 4, 195.23, 13.77, Fit.In, false),
        new("d4-s6.5", 10_000_000, 4, 226.43, 14.12, Fit.Out, true),
        new("d6-s10-p6", 10_000_000, 6, 28.65, 14.42, Fit.In, true),
        new("d6-n7.5e6-s8", 7_500_000, 6, 112.53, 14.54, Fit.In, true),
        new("d8-s30", 8_516_667, 8, 14.86, 14.78, Fit.In, true),
        new("d6-s18", 9_953_765, 6, 33.91, 14.79, Fit.In, true),
        new("d10-n7.5e6", 7_500_000, 10, 8.03, 14.93, Fit.In, true),
        new("d6-s16", 9_996_464, 6, 42.92, 15.51, Fit.In, true),
        new("d8-uniform", 10_000_000, 8, 9.58, 15.67, Fit.In, true),
        new("d6-s14", 9_997_646, 6, 56.06, 16.32, Fit.In, true),
        new("d6-s10-p2", 10_000_000, 6, 61.63, 16.63, Fit.Out, true),
        new("d8-s26", 9_156_178, 8, 19.79, 16.74, Fit.In, true),
        new("d6-s13", 9_999_893, 6, 65.02, 16.81, Fit.In, true),
        new("d6-s12.5", 9_999_893, 6, 70.32, 17.08, Fit.Split, true),
        new("d6-s12", 9_999_957, 6, 76.30, 17.37, Fit.Out, true),
        new("d6-s11", 10_000_000, 6, 90.81, 18.02, Fit.Out, true),
        new("d6-s10", 10_000_000, 6, 109.88, 18.77, Fit.Out, true),
        new("d12-n7.5e6", 7_500_000, 12, 8.03, 18.79, Fit.Out, true),
        new("d8-s22", 9_703_151, 8, 27.64, 18.98, Fit.Out, true),
        new("d8-s20", 9_865_447, 8, 33.44, 20.13, Fit.Out, true),
        new("d10-n1e7", 10_000_000, 10, 9.58, 20.17, Fit.Out, true),
        new("d8-s18", 9_953_765, 8, 33.91, 20.36, Fit.Out, true),
        new("d8-s16", 9_996_464, 8, 42.92, 21.60, Fit.Out, true),
        new("d12-n1e7", 10_000_000, 12, 9.58, 25.67, Fit.Out, true),
    ];

    /// <summary>The 3 scenes of the same table that the fit did not use (Issue #624): uneven parts, 16 DIP, and 2.5e6 points.</summary>
    private static readonly DesignScene[] Added =
    [
        new("d12-n2.5e6-s5", 2_500_000, 12, 104.83, 14.00, Fit.In, false),
        new("d16-n5e6", 5_000_000, 16, 5.92, 18.00, Fit.Out, true),
        new("d6-s10-p6-uneven", 10_000_000, 6, 100.08, 18.39, Fit.Out, true),
    ];

    private static double _sink;

    /// <summary>
    /// The predicted time of each of the 41 scenes that set the formula equals the design's table to within 0.01 ms, and
    /// the threshold puts every scene that did not fit 16.7 ms on the density side, the 3 scenes the fit did not use
    /// included (design §5.5.2 "予測時間", "境目", "製品の計測").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The design's table of the product's measurement, as it writes it: the upper count of the points in view, the
    ///   markers' diameter in pixels (DIPs at the scale 1), the largest bin of the tiles, the predicted time from the
    ///   written coefficients to 0.01 ms, and whether the scene fit 16.7 ms (in, out, or split between rounds). The facet's
    ///   scene draws 1e7 points over its six panels.
    ///
    /// Steps:
    /// - Compute the product's predicted time of each scene from its count, diameter, and largest bin, and the drawing an
    ///   Auto layer of the default threshold takes from raw points.
    ///
    /// Expected:
    /// - Each time is within 0.01 ms of the table's (the table rounds to 0.01 ms); d6-uniform is 12.17 and d12-n1e7 25.67.
    /// - d4-s6.5 is 14.117 ms and d4-s7 13.773 ms, to within 0.001 ms, on either side of 14.1.
    /// - Each scene's drawing is the table's (a density from 14.12 ms up, raw points to 13.77 ms): the 12 scenes that did
    ///   not fit and the one split between rounds draw a density; 18 stay raw points; 10 scenes that fit draw a density
    ///   too, 23 densities in all.
    /// - Of the 3 scenes the fit did not use, the 2 that did not fit (16 DIP, and 9 points in 10 in one of 6 parts) draw a
    ///   density and the one that fit (2.5e6 points of 12 DIP) stays raw points.
    /// </remarks>
    [Fact]
    public void PredictedTime_OfTheDesignsScenes_MatchesItsTable()
    {
        var (raw, outside, split, fitting) = (0, 0, 0, 0);
        foreach (var scene in Added)
        {
            var time = PredictedTime.Of(scene.Points, scene.Diameter, scene.LargestBin);
            Assert.True(Math.Abs(time - scene.Time) <= 0.01, $"{scene.Name}: {time:F4} ms against the table's {scene.Time} ms.");
            Assert.Equal(scene.Fit != Fit.In, scene.Density);
            Assert.Equal(scene.Density, DensitySwitch.Next(false, scene.Points, 10_000_000, time, time));
        }

        foreach (var scene in Scenes)
        {
            var time = PredictedTime.Of(scene.Points, scene.Diameter, scene.LargestBin);
            Assert.True(Math.Abs(time - scene.Time) <= 0.01, $"{scene.Name}: {time:F4} ms against the table's {scene.Time} ms.");
            var density = DensitySwitch.Next(
                density: false,
                scene.Points,
                10_000_000,
                time,
                PredictedTime.Of(scene.Points, scene.Diameter, 4 * scene.LargestBin)
            );
            Assert.Equal(scene.Density, density);
            switch (scene.Fit)
            {
                case Fit.Out:
                    Assert.True(density, scene.Name);
                    outside++;
                    break;
                case Fit.Split:
                    Assert.True(density, scene.Name);
                    split++;
                    break;
                case Fit.In when density:
                    fitting++;
                    break;
                default:
                    raw++;
                    break;
            }
        }

        Assert.Equal((18, 12, 1, 10), (raw, outside, split, fitting));
        Assert.Equal(14.117, Time("d4-s6.5"), 0.001);
        Assert.Equal(13.773, Time("d4-s7"), 0.001);
        Assert.Equal(12.17, Time("d6-uniform"), 0.01);
        Assert.Equal(25.67, Time("d12-n1e7"), 0.01);

        static double Time(string name)
        {
            var scene = Scenes.Single(s => s.Name == name);
            return PredictedTime.Of(scene.Points, scene.Diameter, scene.LargestBin);
        }
    }

    /// <summary>
    /// The predicted time's third term is 0 for a layer of no point or of a largest bin of 0, and it grows with the
    /// largest bin to the power 0.37 (design §5.5.2 "予測時間").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - None.
    ///
    /// Steps:
    /// - Compute the time of 0 points (6 px, a largest bin of 100), of 5e6 points with a largest bin of 0, and of 1e7 points
    ///   of 6 px with largest bins of 1 and 100.
    ///
    /// Expected:
    /// - 1.56 and 1.56 + 3.055 exactly. 1.56 + 6.11 + 0.0690 × 9π and the same with 100^0.37 = 5.4954 times the term (to
    ///   within 1e-4 of the factor), to within 1e-12 for the first and 1e-3 for the second.
    /// </remarks>
    [Fact]
    public void PredictedTime_NoPointOrNoBin_HasNoOverlapTerm()
    {
        Assert.Equal(1.56, PredictedTime.Of(0, 6, 100));
        Assert.Equal(1.56 + 3.055, PredictedTime.Of(5_000_000, 6, 0), 12);
        var term = 0.0690 * 9 * Math.PI;
        Assert.Equal(1.56 + 6.11 + term, PredictedTime.Of(10_000_000, 6, 1), 12);
        Assert.Equal(1.56 + 6.11 + (5.4954 * term), PredictedTime.Of(10_000_000, 6, 100), 1e-3);
    }

    /// <summary>
    /// From raw points a layer switches at a predicted time of 14.1 ms or a count above the threshold; from a density it
    /// goes back only when the count is under 0.8 times the threshold, the time under 11.28 ms, and the time of four times
    /// the largest bin under 14.1 ms, each at its exact end (design §5.5.2 "入れ替わりを抑える").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A threshold of 10,000 points; the time's ends as the design writes them, 14.1 ms and 11.28 ms, not from the
    ///   product's constants. 14.1 is a double the code holds as it is; 14.1 × 0.8 in doubles is one unit in the last place
    ///   above the double 11.28, so the back end is checked 1e-9 ms on each side of 11.28.
    ///
    /// Steps:
    /// - From raw points: a time of exactly 14.1 and one unit in the last place under it; a count of 10,000 and 10,001.
    /// - From a density, with the other two conditions met: a time of 11.28 + 1e-9 and 11.28 − 1e-9; a time of four bins of
    ///   exactly 14.1 and just under it; a count of 8,000 and 7,999. Then each condition alone left in its band.
    ///
    /// Expected:
    /// - From raw points: density at 14.1 and above 10,000 points; raw points just under 14.1 and at 10,000.
    /// - From a density: raw points only when all three are under their ends; a density when any one is at its end.
    /// </remarks>
    [Fact]
    public void Next_AtTheEndsOfEachBand_SwitchesOrHolds()
    {
        const long Threshold = 10_000;
        const double Top = 14.1;
        var under = Math.BitDecrement(Top);
        var (overBack, underBack) = (11.28 + 1e-9, 11.28 - 1e-9);

        Assert.True(DensitySwitch.Next(false, 0, Threshold, Top, Top));
        Assert.False(DensitySwitch.Next(false, 0, Threshold, under, 100));
        Assert.False(DensitySwitch.Next(false, Threshold, Threshold, 0, 0));
        Assert.True(DensitySwitch.Next(false, Threshold + 1, Threshold, 0, 0));

        // From a density: the three conditions met, then each alone at its end.
        Assert.False(DensitySwitch.Next(true, 7_999, Threshold, underBack, under));
        Assert.True(DensitySwitch.Next(true, 7_999, Threshold, overBack, under));
        Assert.True(DensitySwitch.Next(true, 7_999, Threshold, underBack, Top));
        Assert.True(DensitySwitch.Next(true, 8_000, Threshold, underBack, under));

        // Each one alone left in its band, the others well under theirs.
        Assert.True(DensitySwitch.Next(true, 9_000, Threshold, 2, 2));
        Assert.True(DensitySwitch.Next(true, 0, Threshold, 12, 12));
        Assert.True(DensitySwitch.Next(true, 0, Threshold, 2, 15));
        Assert.False(DensitySwitch.Next(true, 0, Threshold, 2, 2));
    }

    /// <summary>
    /// A layer drawn as a density whose time falls under 11.28 ms stays a density while the time of four times its
    /// largest bin is 14.1 ms or more (design §5.5.2 "水準が替わるところ"): the scene d6-n5e6-s6 stays, and d6-n5e6-s7 and
    /// d6-n5e6-s8.49 go back.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The three scenes of 5e6 points and 6 px from the design's table, with their largest bins 101.09, 137.60, and 68.72;
    ///   the default threshold, whose 0.8 times is above 5e6 points.
    ///
    /// Steps:
    /// - From a density, compute the drawing of each.
    /// - The design's example: 5e6 points whose third term is 6.665 ms, 11.28 ms and 15.75 ms with four times the bin.
    ///
    /// Expected:
    /// - The times 10.00, 10.65, and 9.28 ms are all under 11.28; four times the bin gives 13.60 (under 14.1), 14.69, and
    ///   under 14.1 ms.
    /// - d6-n5e6-s6 stays a density; d6-n5e6-s7 and d6-n5e6-s8.49 go back to raw points.
    /// - The example's 1.56 + 3.055 + 6.665 × 4^0.37 is 15.75 ms to within 0.01 ms, and it stays a density.
    /// </remarks>
    [Fact]
    public void Next_FromADensity_HoldsWhileFourTimesTheBinReachesTheThreshold()
    {
        bool Back(string name, double expectedTime, double expectedAtFour)
        {
            var scene = Scenes.Single(s => s.Name == name);
            var (time, atFour) = (
                PredictedTime.Of(scene.Points, scene.Diameter, scene.LargestBin),
                PredictedTime.Of(scene.Points, scene.Diameter, 4 * scene.LargestBin)
            );
            Assert.Equal(expectedTime, time, 0.02);
            Assert.True(time < 11.28);
            if (!double.IsNaN(expectedAtFour))
            {
                Assert.Equal(expectedAtFour, atFour, 0.02);
            }

            return !DensitySwitch.Next(true, scene.Points, 10_000_000, time, atFour);
        }

        Assert.True(Back("d6-n5e6-s7", 10.00, 13.60));
        Assert.False(Back("d6-n5e6-s6", 10.65, 14.69));
        Assert.True(Back("d6-n5e6-s8.49", 9.28, double.NaN));
        var example = 1.56 + 3.055 + (6.665 * Math.Pow(4, 0.37));
        Assert.Equal(15.75, example, 0.01);
        Assert.True(DensitySwitch.Next(true, 5_000_000, 10_000_000, 11.28, example));
    }

    /// <summary>
    /// A layer whose largest bin goes back and forth between ρ and up to 4ρ from frame to frame, its count the same,
    /// changes its drawing once at most, whatever ρ and wherever it starts (design §5.5.2 "水準が替わるところ").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Counts of 1e6, 2.5e6, 5e6, and 7.5e6 points (under 0.8 times the default threshold); diameters of 2, 4, 6, 8, 10,
    ///   and 12 px; largest bins ρ from 0.5 to 400 in steps of 2^(1/4); jumps k of 1.01, 1.5, 2, 3, and 4.
    ///
    /// Steps:
    /// - For each case and each start (raw points, a density), draw 12 frames whose largest bin is ρ, kρ, ρ, kρ, ...
    ///
    /// Expected:
    /// - The drawing changes at most once over the 12 frames; the cases include ones that switch once.
    /// </remarks>
    [Fact]
    public void Next_LargestBinWaveringByALevel_SwitchesOnceAtMost()
    {
        var switched = 0;
        foreach (var count in new long[] { 1_000_000, 2_500_000, 5_000_000, 7_500_000 })
        {
            foreach (var diameter in new double[] { 2, 4, 6, 8, 10, 12 })
            {
                for (var rho = 0.5; rho < 400; rho *= Math.Pow(2, 0.25))
                {
                    foreach (var jump in new[] { 1.01, 1.5, 2, 3, 4 })
                    {
                        foreach (var start in new[] { false, true })
                        {
                            var (density, changes) = (start, 0);
                            for (var frame = 0; frame < 12; frame++)
                            {
                                var bin = frame % 2 == 0 ? rho : jump * rho;
                                var next = DensitySwitch.Next(
                                    density,
                                    count,
                                    10_000_000,
                                    PredictedTime.Of(count, diameter, bin),
                                    PredictedTime.Of(count, diameter, PredictedTime.LevelJump * bin)
                                );
                                changes += next != density ? 1 : 0;
                                density = next;
                            }

                            Assert.True(changes <= 1, $"{changes} changes at {count} points, {diameter} px, ρ {rho}, k {jump}.");
                            switched += changes;
                        }
                    }
                }
            }
        }

        Assert.True(switched > 0);
    }

    /// <summary>
    /// The predicted time and the next drawing allocate nothing (design §5.5.2 "予測時間", invariant 3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - None.
    ///
    /// Steps:
    /// - Compute 1,000 times and drawings, counting the thread's allocations.
    ///
    /// Expected:
    /// - 0 bytes; the sink of the results is not 0.
    /// </remarks>
    [Fact]
    public void PredictedTime_AndNext_AllocateNothing()
    {
        PerformanceAssert.DoesNotAllocate(() =>
        {
            for (var i = 0; i < 1_000; i++)
            {
                var time = PredictedTime.Of(1_000_000 + i, 6, 10 + i);
                _sink += time + (DensitySwitch.Next(i % 2 == 0, i, 10_000_000, time, time) ? 1 : 0);
            }
        });
        Assert.NotEqual(0, _sink);
    }

    /// <summary>
    /// The count of the points in view is the sum over every part of the layer, its facet panels' included, and the largest
    /// bin the greatest part's, not their sum; one drawing goes to every panel, so a facet whose summed count predicts
    /// 14.1 ms or more draws a density in every panel though each panel alone predicts less (design §5.5.2 "予測時間").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 240,000 seeded uniform points over [0, 100)², colored by 3 names and faceted by 2, row by row: 6 parts of 40,000
    ///   points. Auto, 6 DIPs; the tiles of the parts' indexes made from 2,048 points. An engine that waits for its indexes,
    ///   the view [0, 100]², which holds every point.
    ///
    /// Steps:
    /// - Draw the view. Then set the markers' size so that the summed count predicts 15 ms, and draw again.
    ///
    /// Expected:
    /// - Two panels of three parts, one switch for all six, a panel's own for each three. The count is 240,000, both panels'
    ///   points; the largest bin is the greatest of the parts' tops, less than their sum; the time is the formula's of them.
    /// - At the new size the time is 15 ms to within 1e-9, the time of one panel's 120,000 points under 14.1 ms, and every
    ///   part of both panels draws a density; at the first size, raw points.
    /// </remarks>
    [Fact]
    public void Count_SumsEveryPartOfEveryPanel_AndTheLargestBinIsTheGreatest()
    {
        const int Count = 240_000;
        var random = new Random(601);
        using var xs = Column.Wrap(Enumerable.Range(0, Count).Select(_ => random.NextDouble() * 100).ToArray());
        using var ys = Column.Wrap(Enumerable.Range(0, Count).Select(_ => random.NextDouble() * 100).ToArray());
        var table = DataTable
            .Create()
            .Add("x", xs)
            .Add("y", ys)
            .Add("c", Column.Categorical(Enumerable.Range(0, Count).Select(i => (string?)Three[i % 3]).ToArray()))
            .Add("f", Column.Categorical(Enumerable.Range(0, Count).Select(i => (string?)Two[(i / 3) % 2]).ToArray()))
            .Build();
        PlotSpec Spec(float size) =>
            GeomTesting.Spec(
                table,
                new PointLayerSpec
                {
                    X = table.Col<double>("x"),
                    Y = table.Col<double>("y"),
                    ColorBy = table.Cat("c").ToColumnRef(),
                    Size = size,
                }
            ) with
            {
                Facet = new FacetWrapSpec { By = table.Cat("f").ToColumnRef() },
            };

        using var pool = new PlotWorkerPool(4) { IndexTuning = SmallTiles };
        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, 960, 480, pool: pool) { WaitsForIndexes = true };
        engine.SetPlot(Spec(6));
        engine.RenderFrame();
        var panels = engine.Plan!.Panels;
        Assert.Equal(2, panels.Count);
        var parts = panels.SelectMany(p => ((SplitPlan)p.Layers[0]).Parts.Cast<IndexedPointPlan>()).ToArray();
        Assert.Equal(6, parts.Length);
        Assert.All(parts, p => Assert.Same(parts[0].Switch, p.Switch));
        Assert.NotSame(parts[0].Panel, parts[3].Panel);
        Assert.All(parts[..3], p => Assert.Same(parts[0].Panel, p.Panel));
        var choice = parts[0].Switch;
        Assert.Equal((long)Count, choice.LastCount);
        var tops = parts.Select(p => p.PieceTop).ToArray();
        Assert.All(tops, t => Assert.True(t > 0));
        Assert.Equal(tops.Max(), choice.LastLargestBin);
        Assert.True(choice.LastLargestBin < tops.Sum());
        Assert.Equal(PredictedTime.Of(Count, 6, tops.Max()), choice.LastTime);
        Assert.All(parts, p => Assert.False(p.DrewDensity));

        // The diameter whose summed count predicts 15 ms: the third term is 15 − 1.56 − 6.11 n.
        var n = Count / PredictedTime.PointsUnit;
        var term = 15 - PredictedTime.Fixed - (PredictedTime.PerPoints * n);
        var area = term / (PredictedTime.Overlap * n * Math.Pow(choice.LastLargestBin, PredictedTime.Exponent));
        var diameter = (float)Math.Sqrt(area * 4 / Math.PI);
        engine.SetPlot(Spec(diameter));
        engine.RenderFrame();
        Assert.Equal((long)Count, choice.LastCount);
        Assert.Equal(15, choice.LastTime, 1e-4);
        Assert.True(PredictedTime.Of(Count / 2, diameter, choice.LastLargestBin) < PredictedTime.Threshold);
        Assert.All(parts, p => Assert.True(p.DrewDensity));
    }

    /// <summary>
    /// Over a zoom in and out, an Auto layer whose count stays under its threshold draws a density exactly when the
    /// predicted time and its band say so, from the last frame's drawing; its largest bin is the greatest of its parts'
    /// tops, and the density's top where it draws one (design §5.5.2 "予測時間", "最大のビン", "入れ替わりを抑える").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 300,000 seeded points of a normal blob of σ 12 around (50, 50), colored by 3 names row by row; Auto with the
    ///   default threshold; the tiles of the indexes made from 2,048 points; an engine of 640 × 360 that waits for its
    ///   indexes. The markers' size is set from the first frame so that the whole view predicts 20 ms.
    ///
    /// Steps:
    /// - Zoom from the whole view [0, 100]² to a side of 2 around (50, 50) in 120 steps, and out again, drawing each view.
    ///
    /// Expected:
    /// - At each view: every part draws alike; the drawing is the next one from the last frame's by the summed count, the
    ///   time, and the time of four times the largest bin; the time is the formula's of the count, the diameter, and the
    ///   largest bin; the largest bin is the greatest of the parts' tops, and equals the panel's top where it draws a
    ///   density; the count never passes the threshold.
    /// - The walk draws a density at the whole view and raw points at a side of 2, and holds each drawing over a view whose
    ///   time lies between 11.28 and 14.1 ms: a density zooming in and raw points zooming out.
    /// </remarks>
    [Fact]
    public void Zoom_SwitchesByThePredictedTimeWithItsBand()
    {
        const int Count = 300_000;
        using var blob = new Blob(Count, sigma: 12, seed: 602);
        var (table, categories) = (blob.Table, blob.Categories);
        PlotSpec Spec(float size, long threshold) =>
            GeomTesting.Spec(
                table,
                new PointLayerSpec
                {
                    X = table.Col<double>("x"),
                    Y = table.Col<double>("y"),
                    ColorBy = categories,
                    Size = size,
                    DensityThreshold = threshold,
                }
            );

        using var pool = new PlotWorkerPool(4) { IndexTuning = SmallTiles };
        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, 640, 360, pool: pool) { WaitsForIndexes = true };
        engine.SetPlot(Spec(1, 10_000_000));
        engine.RenderFrame();
        IndexedPointPlan[] Parts() => [.. ((SplitPlan)engine.Plan!.Layers[0]).Parts.Cast<IndexedPointPlan>()];
        var choice = Parts()[0].Switch;
        var diameter = DiameterFor(20, choice.LastCount, choice.LastLargestBin);

        // A new threshold forgets the drawing: the walk starts from raw points.
        engine.SetPlot(Spec(diameter, 9_999_999));
        var sides = Enumerable.Range(0, 121).Select(i => 100 * Math.Pow(0.02, i / 120.0)).ToArray();
        var (last, heldDensity, heldPoints) = (false, false, false);
        var drawn = new List<bool>();
        foreach (var side in sides.Concat(sides.Reverse()))
        {
            var half = side / 2;
            engine.Views.SetView(Panel, new PanelView2D(ViewRange.FromValues(50 - half, 50 + half), ViewRange.FromValues(50 - half, 50 + half)));
            backend.Images.Sent();
            engine.RenderFrame();
            var parts = Parts();
            var density = Assert.Single(parts.Select(p => p.DrewDensity).Distinct());
            choice = parts[0].Switch;
            Assert.True(choice.LastCount <= 9_999_999);
            Assert.Equal(DensitySwitch.Next(last, choice.LastCount, 9_999_999, choice.LastTime, choice.LastTimeAtFourBins), density);
            Assert.Equal(PredictedTime.Of(choice.LastCount, diameter, choice.LastLargestBin), choice.LastTime);
            Assert.Equal(PredictedTime.Of(choice.LastCount, diameter, 4 * choice.LastLargestBin), choice.LastTimeAtFourBins);
            Assert.Equal(parts.Max(p => p.PieceTop), choice.LastLargestBin);
            if (density)
            {
                Assert.Equal(parts[0].Panel.Top, choice.LastLargestBin);
            }

            var band = choice.LastTime >= 11.28 && choice.LastTime < 14.1;
            (heldDensity, heldPoints) = (heldDensity || (band && density), heldPoints || (band && !density));
            last = density;
            drawn.Add(density);
        }

        Assert.True(drawn[0]);
        Assert.False(drawn[120]);
        Assert.True(heldDensity && heldPoints);
    }

    /// <summary>
    /// Before a full snapshot's density tiles come, the largest bin is the estimate from its resident cells in view, the
    /// greatest "points over the area on the screen", a cell smaller than a bin of the chosen level over the bin's area;
    /// a time that reaches 14.1 ms with it draws a density; once the tiles come, the largest bin is the tiles' (design
    /// §5.5.2 "密度タイルが付く前").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 300,000 seeded points of a normal blob of σ 6 around (50, 50); Auto; an engine of 640 × 360 that does not wait, on
    ///   a pool of 4 whose index builds stop after their full snapshot until the test lets them go.
    ///
    /// Steps:
    /// - Draw frames until the part scans a full snapshot; check that it has no tiles. Compute the estimate in the test from
    ///   the snapshot's resident cells, the view [0, 100]², and the panel of 640 × 360 pixels.
    /// - Set the markers' size so that the time with the estimate is 16 ms, and draw a frame.
    /// - Let the build go, and draw until the snapshot has its tiles.
    ///
    /// Expected:
    /// - Before the tiles: the largest bin is the test's estimate to within 1e-9 of it, and above 0; at the new size the
    ///   time is 16 ms, the time of four times the bin is the formula's of four times the estimate, the count is under the
    ///   threshold, and the layer draws a density.
    /// - After: the largest bin is the part's top of its tiles, and the estimate is no greater than it.
    /// </remarks>
    [Fact]
    public void BeforeTheTiles_TheLargestBinIsTheResidentCellsEstimate()
    {
        const int Count = 300_000;
        using var blob = new Blob(Count, sigma: 6, seed: 603);
        var table = blob.Table;
        using var hold = new BuildHold(SpatialJob.PublishFull);
        using var pool = new PlotWorkerPool(4) { IndexTuning = SpatialIndexTuning.Default with { JobEnded = hold.OnJob } };
        PlotSpec Spec(float size) =>
            GeomTesting.Spec(
                table,
                new PointLayerSpec
                {
                    X = table.Col<double>("x"),
                    Y = table.Col<double>("y"),
                    Size = size,
                }
            );

        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, 640, 360, pool: pool);
        var clock = Stopwatch.StartNew();
        try
        {
            engine.SetPlot(Spec(1));
            IndexedPointPlan? plan = null;
            while (plan is null || plan.Switch.LastTime == 0)
            {
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), "No full snapshot was scanned in 60 s.");
                backend.Images.Sent();
                engine.RenderFrame();
                plan = engine.Plan?.Layers is [IndexedPointPlan p, ..] ? p : null;
            }

            var snapshot = plan.SpatialIndex!.Acquire();
            try
            {
                Assert.Equal(SpatialStage.Full, snapshot.Stage);
                Assert.Null(snapshot.Tiles);
                var expected = Estimate(snapshot, plan.TileLevel, 640, 360);
                Assert.True(expected > 0);
                Assert.Equal(expected, plan.Switch.LastLargestBin, expected * 1e-9);
                Assert.Equal(plan.Estimate, plan.Switch.LastLargestBin);
            }
            finally
            {
                snapshot.Release();
            }

            var choice = plan.Switch;
            var diameter = DiameterFor(16, choice.LastCount, choice.LastLargestBin);
            engine.SetPlot(Spec(diameter));
            backend.Images.Sent();
            engine.RenderFrame();
            Assert.Same(plan, engine.Plan!.Layers[0]);
            Assert.Equal(16, choice.LastTime, 1e-4);
            Assert.Equal(PredictedTime.Of(choice.LastCount, diameter, 4 * plan.Estimate), choice.LastTimeAtFourBins);
            Assert.True(choice.LastCount <= 10_000_000);
            Assert.True(plan.DrewDensity);

            hold.Release();
            Eventually.Completes(plan.SpatialIndex!.WhenComplete());
            backend.Images.Sent();
            engine.RenderFrame();
            Assert.Equal(plan.PieceTop, choice.LastLargestBin);
            Assert.True(plan.Estimate <= plan.PieceTop);
        }
        finally
        {
            hold.Release();
        }
    }

    /// <summary>
    /// On a view whose every piece reads a tile of the chosen level, the estimate from the resident cells is no greater than
    /// the largest bin of the tiles, for uniform, clustered, and coincident points (design §5.5.2 "密度タイルが付く前").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 200,000 seeded points of each kind: uniform over [0, 100)²; a normal blob of σ 4 around (50, 50); 120,000 points at
    ///   (50, 50) itself and 80,000 uniform. Auto at 1 DIP; the tiles made from 2,048 points; a plan of 640 × 360 that waits.
    ///
    /// Steps:
    /// - Render the whole view, and squares of sides 100 / 2^k for k from 1 to 6 around (50, 50) and around (35, 60).
    ///
    /// Expected:
    /// - At each view whose pieces all read a tile of the chosen level, the estimate is above 0 and no greater than the
    ///   pieces' top (to within 1e-12 of it); each kind has two such views or more.
    /// </remarks>
    [Theory]
    [InlineData("uniform")]
    [InlineData("cluster")]
    [InlineData("coincident")]
    public void Estimate_WhereTheChosenLevelHasTiles_IsNoGreaterThanTheTiles(string kind)
    {
        const int Count = 200_000;
        var random = new Random(604);
        var (x, y) = (new double[Count], new double[Count]);
        for (var i = 0; i < Count; i++)
        {
            (x[i], y[i]) = kind switch
            {
                "uniform" => (random.NextDouble() * 100, random.NextDouble() * 100),
                "cluster" => Normal(random, 4),
                _ => i < 120_000 ? (50.0, 50.0) : (random.NextDouble() * 100, random.NextDouble() * 100),
            };
        }

        using var xs = Column.Wrap(x);
        using var ys = Column.Wrap(y);
        var table = DataTable.Create().Add("x", xs).Add("y", ys).Build();
        using var pool = new PlotWorkerPool(4) { IndexTuning = SmallTiles };
        using var plan = TestPlans.Create(pool, 640, 360);
        var point = new PointLayerSpec
        {
            X = table.Col<double>("x"),
            Y = table.Col<double>("y"),
            Size = 1,
        };
        var checkedViews = 0;
        foreach (var (cx, cy, side) in Views())
        {
            _ = plan.Render(
                GeomTesting.Spec(table, point) with
                {
                    ScaleX = Scale.Linear().WithDomain(cx - (side / 2), cx + (side / 2)),
                    ScaleY = Scale.Linear().WithDomain(cy - (side / 2), cy + (side / 2)),
                }
            );
            var layer = (IndexedPointPlan)plan.Layers[0];
            var pieces = layer.Pieces.ToArray();
            if (pieces.Length == 0 || !pieces.All(p => p.TileLevel == layer.TileLevel))
            {
                continue;
            }

            Assert.True(layer.Estimate > 0);
            Assert.True(
                layer.Estimate <= layer.PieceTop * (1 + 1e-12),
                $"{kind} at ({cx}, {cy}), side {side}: {layer.Estimate} over {layer.PieceTop}."
            );
            checkedViews++;
        }

        Assert.True(checkedViews >= 2, $"{checkedViews} views of {kind} read tiles of the chosen level alone.");

        static IEnumerable<(double X, double Y, double Side)> Views()
        {
            yield return (50, 50, 100);
            for (var k = 1; k <= 6; k++)
            {
                yield return (50, 50, 100 / Math.Pow(2, k));
                yield return (35, 60, 100 / Math.Pow(2, k));
            }
        }
    }

    /// <summary>
    /// The estimate reads the resident cells that touch the view itself, also on axes that clamp, where the cells outside
    /// the view are drawn at its ends (design §5.5.2 "密度タイルが付く前", "クランプする軸").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 200,000 seeded points: 100,000 of a normal blob of σ 1 around (80, 80) and 100,000 uniform over [0, 100)². Auto at
    ///   1 DIP; both scales clamp; a plan of 640 × 360 that waits.
    ///
    /// Steps:
    /// - Render the view [0, 50]², which the blob lies outside.
    ///
    /// Expected:
    /// - The estimate is the test's, over the cells that touch [0, 50]², to within 1e-9 of it; the same estimate over every
    ///   resident cell is more than 10 times greater (the blob's cells).
    /// </remarks>
    [Fact]
    public void Estimate_OnAxesThatClamp_ReadsTheCellsOfTheViewOnly()
    {
        const int Count = 200_000;
        var random = new Random(609);
        var (x, y) = (new double[Count], new double[Count]);
        for (var i = 0; i < Count; i++)
        {
            (x[i], y[i]) =
                i < Count / 2
                    ? (80 + (Normal(random, 1).X - 50), 80 + (Normal(random, 1).Y - 50))
                    : (random.NextDouble() * 100, random.NextDouble() * 100);
        }

        using var xs = Column.Wrap(x);
        using var ys = Column.Wrap(y);
        var table = DataTable.Create().Add("x", xs).Add("y", ys).Build();
        using var pool = new PlotWorkerPool(4);
        using var plan = TestPlans.Create(pool, 640, 360);
        _ = plan.Render(
            GeomTesting.Spec(
                table,
                new PointLayerSpec
                {
                    X = table.Col<double>("x"),
                    Y = table.Col<double>("y"),
                    Size = 1,
                }
            ) with
            {
                ScaleX = Scale.Linear().WithDomain(0, 50) with { Clamp = true },
                ScaleY = Scale.Linear().WithDomain(0, 50) with { Clamp = true },
            }
        );
        var layer = (IndexedPointPlan)plan.Layers[0];
        var snapshot = layer.SpatialIndex!.Acquire();
        try
        {
            var inView = Estimate(snapshot, layer.TileLevel, 640, 360, side: 50);
            Assert.Equal(inView, layer.Estimate, inView * 1e-9);
            Assert.True(Estimate(snapshot, layer.TileLevel, 640, 360, side: 50, everyCell: true) > 10 * inView);
        }
        finally
        {
            snapshot.Release();
        }
    }

    /// <summary>
    /// A resident cell's estimate is its count over its area on the screen, or over a bin's area when the cell is smaller;
    /// a collapsed axis, a count of 0, NaN, and areas past a double give 0, never NaN or infinity (design §5.5.2
    /// "密度タイルが付く前").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A count of 1,000 points; pixels a unit of 10 along x and −20 along y (a y axis that points down).
    ///
    /// Steps:
    /// - A cell of 2 × 3 units over a bin of 100 px²; one of 0.1 × 0.1 over a bin of 4 px².
    /// - A width of 0 (0 pixels a unit along x, so a bin of 0 too); a count of 0 on that width and on a cell of positive area;
    ///   NaN pixels a unit, a NaN extent, and a NaN bin; extents of 1e300 at 10 pixels a unit (an area past a double), and
    ///   extents of 1e300 at 1e-300 pixels a unit (an area of 1 px², whose extents' product alone is past a double).
    ///
    /// Expected:
    /// - 1,000 / 1,200 and 1,000 / 4 = 250.
    /// - 0 for each of the others but the last, whose estimate is 1,000 / 4 over a bin of 4 px²; none is NaN or infinite.
    /// </remarks>
    [Fact]
    public void CellEstimate_CollapsedNaNOrHugeArea_IsFinite()
    {
        Assert.Equal(1_000.0 / 1_200, DensityMath.CellEstimate(1_000, 2, 3, 10, -20, 100), 12);
        Assert.Equal(250, DensityMath.CellEstimate(1_000, 0.1, 0.1, 10, 10, 4), 12);
        double[] zeros =
        [
            DensityMath.CellEstimate(1_000, 2, 3, 0, -20, DensityMath.BinArea(2, 3, 0, -20)),
            DensityMath.CellEstimate(0, 2, 3, 0, -20, 0),
            DensityMath.CellEstimate(0, 2, 3, 10, -20, 100),
            DensityMath.CellEstimate(1_000, 2, 3, double.NaN, -20, 100),
            DensityMath.CellEstimate(1_000, double.NaN, 3, 10, -20, 100),
            DensityMath.CellEstimate(1_000, 2, 3, 10, -20, double.NaN),
            DensityMath.CellEstimate(1_000, 1e300, 1e300, 10, 10, 100),
        ];
        Assert.All(zeros, z => Assert.Equal(0.0, z));
        Assert.Equal(0.0, DensityMath.BinArea(2, 3, 0, -20));
        Assert.Equal(250, DensityMath.CellEstimate(1_000, 1e300, 1e300, 1e-300, 1e-300, 4), 9);
    }

    /// <summary>
    /// Points and Density layers read no condition, and a layer on its coarse snapshot predicts no time (design §5.5.2
    /// "`Auto` の切り替え", "スナップショットが確定する前").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 300,000 seeded points of a normal blob of σ 6 around (50, 50), markers of 64 DIPs, which predict far more than
    ///   14.1 ms at the whole view; an engine of 640 × 360.
    ///
    /// Steps:
    /// - Points, waiting for the index: draw the whole view.
    /// - Density, waiting: draw a square of side 1 around (5, 5), where hardly a point lies.
    /// - Auto, on a pool whose build stops after its coarse snapshot: draw until the coarse density is drawn. Let the build
    ///   go, and draw until a full snapshot is scanned.
    ///
    /// Expected:
    /// - Points draws raw points and Density a density; the switch read no count and no time for either (0 both).
    /// - The coarse frame draws the coarse density, scanned no full snapshot, and predicted no time; the layer, with no
    ///   decision yet, is on raw points. Its first full frame predicts 14.1 ms or more and draws a density.
    /// </remarks>
    [Fact]
    public void PointsDensityAndCoarse_ReadNoCondition()
    {
        const int Count = 300_000;
        using var blob = new Blob(Count, sigma: 6, seed: 605);
        var table = blob.Table;
        PlotSpec Spec(PointDraw draw) =>
            GeomTesting.Spec(
                table,
                new PointLayerSpec
                {
                    X = table.Col<double>("x"),
                    Y = table.Col<double>("y"),
                    Size = 64,
                    Draw = draw,
                }
            );

        var backend = new NullBackend();
        using (var engine = new PlotEngine(backend, 640, 360) { WaitsForIndexes = true })
        {
            engine.SetPlot(Spec(PointDraw.Points));
            engine.RenderFrame();
            var plan = (IndexedPointPlan)engine.Plan!.Layers[0];
            Assert.False(plan.DrewDensity);
            Assert.Equal((0L, 0.0), (plan.Switch.LastCount, plan.Switch.LastTime));

            engine.SetPlot(Spec(PointDraw.Density));
            engine.Views.SetView(Panel, new PanelView2D(ViewRange.FromValues(4.5, 5.5), ViewRange.FromValues(4.5, 5.5)));
            backend.Images.Sent();
            engine.RenderFrame();
            plan = (IndexedPointPlan)engine.Plan!.Layers[0];
            Assert.True(plan.DrewDensity);
            Assert.Equal((0L, 0.0), (plan.Switch.LastCount, plan.Switch.LastTime));
        }

        using var hold = new BuildHold(SpatialJob.PublishCoarse);
        using var pool = new PlotWorkerPool(4) { IndexTuning = SpatialIndexTuning.Default with { JobEnded = hold.OnJob } };
        var coarseBackend = new NullBackend();
        using var coarseEngine = new PlotEngine(coarseBackend, 640, 360, pool: pool);
        var clock = Stopwatch.StartNew();
        try
        {
            coarseEngine.SetPlot(Spec(PointDraw.Auto));
            while (coarseBackend.LastList is not { Images.Length: > 0 })
            {
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), "The coarse density was not drawn in 60 s.");
                coarseBackend.Images.Sent();
                coarseEngine.RenderFrame();
            }

            var coarse = (IndexedPointPlan)coarseEngine.Plan!.Layers[0];
            Assert.True(coarse.DrewDensity);
            Assert.Equal((0L, 0.0, 0.0), (coarse.ScannedCount, coarse.Switch.LastTime, coarse.Switch.LastLargestBin));

            // The layer has no decision yet: its drawing is raw points while the part draws the coarse density.
            Assert.False(coarse.Switch.Density);

            // Its first full frame decides from raw points by the conditions.
            hold.Release();
            while (coarse.Switch.LastTime == 0)
            {
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), "No full snapshot was scanned in 60 s.");
                coarseBackend.Images.Sent();
                coarseEngine.RenderFrame();
            }

            Assert.True(coarse.Switch.LastTime >= 14.1);
            Assert.True(coarse.Switch.Density && coarse.DrewDensity);
        }
        finally
        {
            hold.Release();
        }
    }

    /// <summary>
    /// The markers' area is a disc of the diameter in physical pixels, Size times the scale, whatever their shape (design
    /// §5.5.2 "予測時間").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 300,000 seeded points of a normal blob of σ 12; Auto at 8 DIPs; a plan of 640 × 360 at the scale 1.5 that waits.
    ///
    /// Steps:
    /// - Render the whole view with circles, squares, and crosses.
    ///
    /// Expected:
    /// - The same count, largest bin, and time for the three shapes; the time is the formula's at 12 px, not at 8.
    /// </remarks>
    [Fact]
    public void MarkerArea_IsADiscOfSizeTimesTheScale_WhateverTheShape()
    {
        const int Count = 300_000;
        using var blob = new Blob(Count, sigma: 12, seed: 606);
        var table = blob.Table;
        using var pool = new PlotWorkerPool(4);
        using var plan = new PlotPlan(pool, 640, 360, new TextEngine(new FakeTextBackend()), 1.5f);
        var times = new List<(long, double, double)>();
        foreach (var shape in new[] { MarkerShape.Circle, MarkerShape.Square, MarkerShape.Cross })
        {
            _ = plan.Render(
                GeomTesting.Spec(
                    table,
                    new PointLayerSpec
                    {
                        X = table.Col<double>("x"),
                        Y = table.Col<double>("y"),
                        Size = 8,
                        Shape = shape,
                    }
                )
            );
            var choice = ((IndexedPointPlan)plan.Layers[0]).Switch;
            times.Add((choice.LastCount, choice.LastLargestBin, choice.LastTime));
        }

        var (count, bin, time) = Assert.Single(times.Distinct());
        Assert.True(count > 0 && bin > 0);
        Assert.Equal(PredictedTime.Of(count, 12, bin), time);
        Assert.NotEqual(PredictedTime.Of(count, 8, bin), time);
    }

    /// <summary>
    /// A layer with no drawing to remember starts from raw points while its time lies in the band, and one that comes from a
    /// density keeps it there (design §5.5.2 "入れ替わりを抑える").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 300,000 seeded points of a normal blob of σ 12, Auto; an engine of 640 × 360 that waits for its indexes. The sizes
    ///   that predict 12.7 ms (in the band) and 16 ms at the whole view are set from a first frame.
    ///
    /// Steps:
    /// - Draw at the band's size with a new threshold (nothing remembered). Draw at 16 ms, then at the band's size again.
    /// - Set Points, then Auto again (nothing remembered), at the band's size.
    ///
    /// Expected:
    /// - Raw points at 12.7 ms; a density at 16 ms; still a density at 12.7 ms; raw points again after Draw changed.
    /// </remarks>
    [Fact]
    public void NothingRemembered_InTheBand_StartsFromRawPoints()
    {
        const int Count = 300_000;
        using var blob = new Blob(Count, sigma: 12, seed: 607);
        var table = blob.Table;
        PlotSpec Spec(float size, PointDraw draw, long threshold) =>
            GeomTesting.Spec(
                table,
                new PointLayerSpec
                {
                    X = table.Col<double>("x"),
                    Y = table.Col<double>("y"),
                    Size = size,
                    Draw = draw,
                    DensityThreshold = threshold,
                }
            );

        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, 640, 360) { WaitsForIndexes = true };
        engine.SetPlot(Spec(1, PointDraw.Auto, 10_000_000));
        engine.RenderFrame();
        var plan = (IndexedPointPlan)engine.Plan!.Layers[0];
        var (count, bin) = (plan.Switch.LastCount, plan.Switch.LastLargestBin);
        var (band, over) = (DiameterFor(12.7, count, bin), DiameterFor(16, count, bin));

        bool Draw(float size, PointDraw draw, long threshold)
        {
            engine.SetPlot(Spec(size, draw, threshold));
            backend.Images.Sent();
            engine.RenderFrame();
            plan = (IndexedPointPlan)engine.Plan!.Layers[0];
            return plan.DrewDensity;
        }

        Assert.False(Draw(band, PointDraw.Auto, 9_999_999));
        Assert.Equal(12.7, plan.Switch.LastTime, 1e-4);
        Assert.True(Draw(over, PointDraw.Auto, 9_999_999));
        Assert.True(Draw(band, PointDraw.Auto, 9_999_999));
        Assert.False(Draw(band, PointDraw.Points, 9_999_999));
        Assert.False(Draw(band, PointDraw.Auto, 9_999_999));
    }

    /// <summary>
    /// A frame in which no part of the layer scanned a full snapshot reads no condition and keeps the layer's drawing: a
    /// layer drawn as a density stays one, and comes back to the band's choice when a full snapshot is scanned again
    /// (design §5.5.2 "入れ替わりを抑える", "スナップショットが確定する前").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 300,000 seeded points of a normal blob of σ 12, colored by 3 names, Auto; an engine of 640 × 360 that waits for its
    ///   indexes. The sizes that predict 16 ms and 12.7 ms (in the band) at the whole view are set from a first frame.
    /// - A part keeps its full snapshot until it is released, so the engine makes no such frame after the first full one;
    ///   the test makes it by opening a frame of the plan's cache in which no part is scanned, and choosing then.
    ///
    /// Steps:
    /// - Draw at 16 ms. Open a frame without a scan and choose the layer's drawing three times in a row, at the size of
    ///   16 ms and then of 12.7 ms. Draw at 12.7 ms through the engine. Draw at 5 ms.
    ///
    /// Expected:
    /// - A density at 16 ms. In the frames without a scan: still a density, and no count and no time read (0 both). At
    ///   12.7 ms through the engine, still a density (the band holds it); at 5 ms, raw points.
    /// - Before the fix the frame without a scan went back to raw points (the first assertion of those frames guards it).
    /// </remarks>
    [Fact]
    public void FrameWithNoFullScan_KeepsTheDrawing()
    {
        const int Count = 300_000;
        using var blob = new Blob(Count, sigma: 12, seed: 610);
        var (table, categories) = (blob.Table, blob.Categories);
        PointLayerSpec Point(float size) =>
            new()
            {
                X = table.Col<double>("x"),
                Y = table.Col<double>("y"),
                ColorBy = categories,
                Size = size,
            };

        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, 640, 360) { WaitsForIndexes = true };
        engine.SetPlot(GeomTesting.Spec(table, Point(1)));
        engine.RenderFrame();
        IndexedPointPlan[] Parts() => [.. ((SplitPlan)engine.Plan!.Layers[0]).Parts.Cast<IndexedPointPlan>()];
        var choice = Parts()[0].Switch;
        var (count, bin) = (choice.LastCount, choice.LastLargestBin);
        var (over, band, under) = (DiameterFor(16, count, bin), DiameterFor(12.7, count, bin), DiameterFor(5, count, bin));

        bool Draw(float size)
        {
            engine.SetPlot(GeomTesting.Spec(table, Point(size)));
            backend.Images.Sent();
            engine.RenderFrame();
            Assert.Same(choice, Parts()[0].Switch);
            return Assert.Single(Parts().Select(p => p.DrewDensity).Distinct());
        }

        Assert.True(Draw(over));
        Assert.Equal(16, choice.LastTime, 1e-4);

        // Frames of the plan's cache in which no part is scanned: no part scanned a full snapshot in them.
        var context = new RecordContext(
            new PlotGuides(new TextEngine(new FakeTextBackend()), 1, 640, 360),
            new M4Batch(),
            new RenderList(),
            new GeomFrame()
        )
        {
            Images = engine.Plan!.Images,
        };
        foreach (var size in new[] { over, band, band })
        {
            context.Images.BeginFrame();
            Assert.True(choice.Choose(context, Point(size)));
            Assert.Equal((0L, 0.0), (choice.LastCount, choice.LastTime));
        }

        Assert.True(Draw(band));
        Assert.Equal(12.7, choice.LastTime, 1e-4);
        Assert.False(Draw(under));
    }

    /// <summary>
    /// A new spec whose layers over other columns change places makes every layer again, so no layer takes the drawing
    /// another remembered: both start from raw points in the band (design §5.5.2 "入れ替わりを抑える", "索引の保管庫").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two point layers of 300,000 seeded points each, Auto: A over (x, y), a normal blob of σ 12 around (50, 50), and B
    ///   over (u, v), another of σ 10. An engine of 640 × 360 that waits for its indexes. Each layer's sizes that predict
    ///   16 ms and 12.7 ms (in the band) at the whole view are set from a first frame.
    ///
    /// Steps:
    /// - Draw [A at 16 ms, B at 12.7 ms], then [A at 12.7 ms, B at 12.7 ms]. Then swap them: [B at 12.7 ms, A at 12.7 ms].
    ///
    /// Expected:
    /// - Before the swap the plans and their switches stay; A draws a density at 16 ms and holds it at 12.7 ms, and B draws
    ///   raw points at 12.7 ms.
    /// - After it both positions hold new plans with new switches, and both draw raw points at 12.7 ms; position 0 does not
    ///   take the density position 0 remembered.
    /// </remarks>
    [Fact]
    public void Reorder_OfLayersOverOtherColumns_ForgetsTheirDrawings()
    {
        const int Count = 300_000;
        var random = new Random(611);
        var (x, y, u, v) = (new double[Count], new double[Count], new double[Count], new double[Count]);
        for (var i = 0; i < Count; i++)
        {
            (x[i], y[i]) = Normal(random, 12);
            (u[i], v[i]) = Normal(random, 10);
        }

        using var xs = Column.Wrap(x);
        using var ys = Column.Wrap(y);
        using var us = Column.Wrap(u);
        using var vs = Column.Wrap(v);
        var table = DataTable.Create().Add("x", xs).Add("y", ys).Add("u", us).Add("v", vs).Build();
        PointLayerSpec A(float size) =>
            new()
            {
                X = table.Col<double>("x"),
                Y = table.Col<double>("y"),
                Size = size,
            };
        PointLayerSpec B(float size) =>
            new()
            {
                X = table.Col<double>("u"),
                Y = table.Col<double>("v"),
                Size = size,
            };

        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, 640, 360) { WaitsForIndexes = true };
        IndexedPointPlan Layer(int at) => (IndexedPointPlan)engine.Plan!.Layers[at];
        void Draw(PointLayerSpec first, PointLayerSpec second)
        {
            engine.SetPlot(GeomTesting.Spec(table, first, second));
            backend.Images.Sent();
            engine.RenderFrame();
        }

        Draw(A(1), B(1));
        float Size(int at, double time) => DiameterFor(time, Layer(at).Switch.LastCount, Layer(at).Switch.LastLargestBin);
        var (overA, bandA, bandB) = (Size(0, 16), Size(0, 12.7), Size(1, 12.7));
        var (planA, planB) = (Layer(0), Layer(1));

        Draw(A(overA), B(bandB));
        Assert.True(planA.DrewDensity);
        Draw(A(bandA), B(bandB));
        Assert.Same(planA, Layer(0));
        Assert.Same(planB, Layer(1));
        Assert.Equal(12.7, planA.Switch.LastTime, 1e-4);
        Assert.Equal(12.7, planB.Switch.LastTime, 1e-4);
        Assert.True(planA.DrewDensity);
        Assert.False(planB.DrewDensity);

        Draw(B(bandB), A(bandA));
        var (first, second) = (Layer(0), Layer(1));
        Assert.Same(us, ((PointLayerSpec)first.Spec).X.Column);
        Assert.Same(xs, ((PointLayerSpec)second.Spec).X.Column);
        Assert.NotSame(planB, first);
        Assert.NotSame(planA, second);
        Assert.NotSame(planA.Switch, first.Switch);
        Assert.NotSame(planB.Switch, first.Switch);
        Assert.NotSame(planA.Switch, second.Switch);
        Assert.NotSame(planB.Switch, second.Switch);
        Assert.Equal(12.7, first.Switch.LastTime, 1e-4);
        Assert.Equal(12.7, second.Switch.LastTime, 1e-4);
        Assert.False(first.DrewDensity);
        Assert.False(second.DrewDensity);
    }

    /// <summary>
    /// Frames of a script whose views switch an Auto layer by its predicted time, both ways, allocate nothing once warm
    /// (invariant 3, design §5.5.2 "確保").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 300,000 seeded points of a normal blob of σ 12, colored by 3 names; Auto with the default threshold, its markers
    ///   sized from a first frame to predict 20 ms at the whole view; an engine that waits for its indexes.
    /// - A script of square views around (50, 50) of sides 100, 50, 10, 4, 10, 50, and 100, run 6 times.
    ///
    /// Steps:
    /// - Run the script twice more as a warm-up; collect; run it twice again, counting the thread's allocations.
    ///
    /// Expected:
    /// - In the warm-up, the layer draws both a density and raw points with its count under the threshold; no frame is
    ///   provisional at the end.
    /// - 0 bytes.
    /// </remarks>
    [Fact]
    public void Frames_SwitchingByThePredictedTime_AllocateNothing()
    {
        const int Count = 300_000;
        using var blob = new Blob(Count, sigma: 12, seed: 608);
        var (table, categories) = (blob.Table, blob.Categories);
        PlotSpec Spec(float size) =>
            GeomTesting.Spec(
                table,
                new PointLayerSpec
                {
                    X = table.Col<double>("x"),
                    Y = table.Col<double>("y"),
                    ColorBy = categories,
                    Size = size,
                }
            );

        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, 640, 360) { WaitsForIndexes = true };
        engine.SetPlot(Spec(1));
        engine.RenderFrame();
        IndexedPointPlan First() => ((SplitPlan)engine.Plan!.Layers[0]).Parts.Cast<IndexedPointPlan>().First();
        var diameter = DiameterFor(20, First().Switch.LastCount, First().Switch.LastLargestBin);
        engine.SetPlot(Spec(diameter));
        engine.RenderFrame();
        double[] sides = [100, 50, 10, 4, 10, 50, 100];
        var views = sides
            .Select(s => new PanelView2D(ViewRange.FromValues(50 - (s / 2), 50 + (s / 2)), ViewRange.FromValues(50 - (s / 2), 50 + (s / 2))))
            .ToArray();
        var part = First();
        var drawn = new bool[views.Length];
        for (var round = 0; round < 6; round++)
        {
            for (var i = 0; i < views.Length; i++)
            {
                Draw(views[i]);
                Assert.True(part.Switch.LastCount <= 10_000_000);
                drawn[i] = part.DrewDensity;
            }
        }

        Assert.Contains(true, drawn);
        Assert.Contains(false, drawn);
        Assert.False(engine.IsProvisional);

        PerformanceAssert.DoesNotAllocate(() =>
        {
            for (var round = 0; round < 2; round++)
            {
                foreach (var view in views)
                {
                    Draw(view);
                }
            }
        });

        void Draw(PanelView2D view)
        {
            backend.Images.Sent();
            engine.Views.SetView(Panel, view);
            engine.RenderFrame();
        }
    }

    /// <summary>The tuning that makes density tiles from 2,048 points, so that a few hundred thousand points have deep tiles.</summary>
    private static SpatialIndexTuning SmallTiles => SpatialIndexTuning.Default with { DensitySplit = 2_048, TotalShare = 10 };

    /// <summary>Returns the markers' diameter, in pixels, that makes a count and a largest bin predict a time.</summary>
    private static float DiameterFor(double time, long count, double largestBin)
    {
        var n = count / PredictedTime.PointsUnit;
        var term = time - PredictedTime.Fixed - (PredictedTime.PerPoints * n);
        var area = term / (PredictedTime.Overlap * n * Math.Pow(largestBin, PredictedTime.Exponent));
        return (float)Math.Sqrt(area * 4 / Math.PI);
    }

    /// <summary>
    /// Returns the design's estimate of the largest bin of a full snapshot drawn over the view [0, side]² of a panel of a
    /// width and a height: the greatest count over the area on the screen of the resident cells that touch the view (every
    /// cell when <paramref name="everyCell"/>), a cell's area no less than a bin of the tile level.
    /// </summary>
    private static double Estimate(SpatialSnapshot snapshot, int level, double width, double height, double side = 100, bool everyCell = false)
    {
        var (perX, perY) = (width / side, height / side);
        var (_, _, rangeX, rangeY) = snapshot.CellBox(0, 0);
        var bin = rangeX / Math.Pow(2, level + 8) * perX * (rangeY / Math.Pow(2, level + 8) * perY);
        var (lowX, lowY) = (0.0 - snapshot.XRange.Origin, 0.0 - snapshot.YRange.Origin);
        var largest = 0.0;
        foreach (var cell in snapshot.ResidentCells)
        {
            if (
                everyCell
                || (
                    cell.CornerX <= lowX + side
                    && cell.CornerX + cell.ExtentX >= lowX
                    && cell.CornerY <= lowY + side
                    && cell.CornerY + cell.ExtentY >= lowY
                )
            )
            {
                largest = Math.Max(largest, cell.Count / Math.Max(cell.ExtentX * perX * cell.ExtentY * perY, bin));
            }
        }

        return largest;
    }

    private static (double X, double Y) Normal(Random random, double sigma)
    {
        var r = sigma * Math.Sqrt(-2 * Math.Log(1 - random.NextDouble()));
        var a = 2 * Math.PI * random.NextDouble();
        return (50 + (r * Math.Cos(a)), 50 + (r * Math.Sin(a)));
    }

    /// <summary>A table of a seeded normal blob around (50, 50), with a category of 3 names row by row.</summary>
    private sealed class Blob : IDisposable
    {
        private readonly Column<double> _x;
        private readonly Column<double> _y;

        public Blob(int count, double sigma, int seed)
        {
            var random = new Random(seed);
            var (x, y) = (new double[count], new double[count]);
            for (var i = 0; i < count; i++)
            {
                (x[i], y[i]) = Normal(random, sigma);
            }

            (_x, _y) = (Column.Wrap(x), Column.Wrap(y));
            Table = DataTable
                .Create()
                .Add("x", _x)
                .Add("y", _y)
                .Add("c", Column.Categorical(Enumerable.Range(0, count).Select(i => (string?)Three[i % 3]).ToArray()))
                .Build();
            Categories = Table.Cat("c").ToColumnRef();
        }

        public DataTable Table { get; }

        public ColumnRef Categories { get; }

        public void Dispose()
        {
            _x.Dispose();
            _y.Dispose();
        }
    }

    /// <summary>Stops a pool's index builds after a job until released, so that a snapshot stands at that stage.</summary>
    private sealed class BuildHold : IDisposable
    {
        private readonly ManualResetEventSlim _released = new();

        public BuildHold(SpatialJob job) =>
            OnJob = (ended, elapsed) =>
            {
                if (ended == job)
                {
                    _ = _released.Wait(TimeSpan.FromSeconds(60));
                }
            };

        /// <summary>Gets the hook a build calls after each job: it waits at the held job until the release, 60 s at most.</summary>
        public Action<SpatialJob, long> OnJob { get; }

        public void Release() => _released.Set();

        public void Dispose()
        {
            _released.Set();
            _released.Dispose();
        }
    }

    private enum Fit
    {
        In,
        Out,
        Split,
    }

    private readonly record struct DesignScene(string Name, long Points, double Diameter, double LargestBin, double Time, Fit Fit, bool Density);
}
