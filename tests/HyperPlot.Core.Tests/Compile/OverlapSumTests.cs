using HyperPlot.Interaction;
using HyperPlot.Rendering;
using HyperPlot.Testing;

namespace HyperPlot.Tests;

/// <summary>
/// The panels' sums of the Auto point layers that lie over one another (design §5.5.2 "重なる層の和", §10 R3b2 row, Issue
/// #563): the predicted times, the fixed term counted once, and the upper counts of the points in view, against the
/// thresholds, both ways (<see cref="PointBudget"/>).
/// </summary>
/// <remarks>
/// Each layer is seeded uniform points over [0, 100]², drawn whole on an engine of 640 × 360 without guides that waits for
/// its indexes, on the null backend. A layer's markers are sized from a first frame so that it predicts a time (its count
/// and largest bin do not depend on the size).
/// </remarks>
[Collection("Large data")]
public sealed class OverlapSumTests
{
    private static readonly PanelId Panel = new(0, 0);
    private static readonly string[] Six = ["a", "b", "c", "d", "e", "f"];

    /// <summary>
    /// Two Auto layers of one panel that each predict under 14.1 ms, whose sum with the fixed term once reaches it: the
    /// longer layer becomes a density and the other stays raw points, whichever layer is the longer.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two Auto layers of 300,000 points each, of their own seeds.
    ///
    /// Steps:
    /// - Draw layer 0 at 8.5 ms and layer 1 at 7.5 ms. Then, with a new threshold for both (which forgets the drawings),
    ///   layer 0 at 7.5 ms and layer 1 at 8.5 ms.
    ///
    /// Expected:
    /// - Each time is the one asked to 1e-4, and the sum 1.56 + the terms of both is 14.44 ms. First layer 0 draws a density
    ///   and layer 1 raw points; then layer 1 a density and layer 0 raw points.
    /// </remarks>
    [Fact]
    public void Time_SumPastTheThreshold_MakesTheLongerLayerADensity()
    {
        using var data = new Layers(300_000, 300_000);
        using var scene = new Scene(data);
        scene.Draw(scene.Auto(0, 1), scene.Auto(1, 1));
        var (a, b) = (scene.SizeFor(0, 8.5), scene.SizeFor(1, 7.5));
        var (a2, b2) = (scene.SizeFor(0, 7.5), scene.SizeFor(1, 8.5));

        scene.Draw(scene.Auto(0, a), scene.Auto(1, b));
        Assert.Equal((8.5, 7.5), (Math.Round(scene.Switch(0).LastTime, 4), Math.Round(scene.Switch(1).LastTime, 4)));
        Assert.Equal(14.44, PredictedTime.Fixed + scene.Switch(0).Terms + scene.Switch(1).Terms, 1e-3);
        Assert.Equal((true, false), (scene.Density(0), scene.Density(1)));

        scene.Draw(scene.Auto(0, a2) with { DensityThreshold = 9_999_999 }, scene.Auto(1, b2) with { DensityThreshold = 9_999_999 });
        Assert.Equal((7.5, 8.5), (Math.Round(scene.Switch(0).LastTime, 4), Math.Round(scene.Switch(1).LastTime, 4)));
        Assert.Equal((false, true), (scene.Density(0), scene.Density(1)));
    }

    /// <summary>
    /// Two layers of the same predicted time whose sum reaches 14.1 ms, or of the same count whose sum passes the
    /// threshold: the lower layer's number becomes the density.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two Auto layers over the same columns of 300,000 points, at the same size: the same count, largest bin, and time.
    ///
    /// Steps:
    /// - Draw both at 7.9 ms. Then draw both at 1 DIP with a threshold of 500,000 (which forgets the drawings).
    ///
    /// Expected:
    /// - The two times are equal bit for bit, and their sum is 14.24 ms. Layer 0 draws a density and layer 1 raw points.
    /// - At 1 DIP the counts' sum (600,000) passes 500,000, and again layer 0 draws a density and layer 1 raw points.
    /// </remarks>
    [Fact]
    public void EqualTimesOrCounts_MakeTheLowerLayerADensity()
    {
        using var data = new Layers(300_000);
        using var scene = new Scene(data);
        scene.Draw(scene.Auto(0, 1), scene.Auto(0, 1));
        var size = scene.SizeFor(0, 7.9);
        scene.Draw(scene.Auto(0, size), scene.Auto(0, size));
        Assert.Equal(scene.Switch(0).LastTime, scene.Switch(1).LastTime);
        Assert.Equal(14.24, PredictedTime.Fixed + scene.Switch(0).Terms + scene.Switch(1).Terms, 1e-3);
        Assert.Equal((true, false), (scene.Density(0), scene.Density(1)));

        scene.Draw(scene.Auto(0, 1) with { DensityThreshold = 500_000 }, scene.Auto(0, 1) with { DensityThreshold = 500_000 });
        Assert.Equal((300_000L, 300_000L), (scene.Switch(0).LastCount, scene.Switch(1).LastCount));
        Assert.Equal((true, false), (scene.Density(0), scene.Density(1)));
    }

    /// <summary>
    /// The fixed term is counted once: two layers whose times added reach 14.1 ms, and whose sum with the fixed term once
    /// does not, both stay raw points.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two Auto layers of 300,000 points each.
    ///
    /// Steps:
    /// - Draw both at 7.4 ms.
    ///
    /// Expected:
    /// - The two times added are 14.8 ms, at or over 14.1; the sum with the fixed term once is 13.24 ms, under it. Both draw
    ///   raw points.
    /// </remarks>
    [Fact]
    public void Time_FixedTermCountedOnce_LeavesBothRaw()
    {
        using var data = new Layers(300_000, 300_000);
        using var scene = new Scene(data);
        scene.Draw(scene.Auto(0, 1), scene.Auto(1, 1));
        scene.Draw(scene.Auto(0, scene.SizeFor(0, 7.4)), scene.Auto(1, scene.SizeFor(1, 7.4)));
        Assert.True(scene.Switch(0).LastTime + scene.Switch(1).LastTime >= PredictedTime.Threshold);
        Assert.Equal(13.24, PredictedTime.Fixed + scene.Switch(0).Terms + scene.Switch(1).Terms, 1e-3);
        Assert.Equal((false, false), (scene.Density(0), scene.Density(1)));
    }

    /// <summary>
    /// Two layers whose counts in view are each within their DensityThreshold and whose sum passes it: the layer of more
    /// points becomes a density, the other stays raw points.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Auto layers of 200,000 points (layer 0) and 300,000 points (layer 1), each of a DensityThreshold of 400,000, at 1 DIP
    ///   (times far under 14.1 ms).
    ///
    /// Steps:
    /// - Draw.
    ///
    /// Expected:
    /// - The counts are 200,000 and 300,000, each within 400,000. Layer 1 draws a density and layer 0 raw points.
    /// </remarks>
    [Fact]
    public void Count_SumPastTheThreshold_MakesTheLayerOfMorePointsADensity()
    {
        using var data = new Layers(200_000, 300_000);
        using var scene = new Scene(data);
        scene.Draw(scene.Auto(0, 1) with { DensityThreshold = 400_000 }, scene.Auto(1, 1) with { DensityThreshold = 400_000 });
        Assert.Equal((200_000L, 300_000L), (scene.Switch(0).LastCount, scene.Switch(1).LastCount));
        Assert.True(scene.Switch(1).LastTime < PredictedTime.Threshold * DensitySwitch.Back);
        Assert.Equal((false, true), (scene.Density(0), scene.Density(1)));
    }

    /// <summary>
    /// Layers of different DensityThresholds compare the sum with the least threshold of the layers left in it (design
    /// §5.5.2 "点の数の和").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Auto layers of 300,000, 200,000, and 100,000 points at 1 DIP.
    ///
    /// Steps:
    /// - Draw layers 0 and 1 with thresholds of 10,000,000 and 450,000. Then draw the three with thresholds of 250,000,
    ///   10,000,000, and 10,000,000.
    ///
    /// Expected:
    /// - Two layers: the sum 500,000 passes the lesser threshold (450,000), not the greater: layer 0, of more points, draws a
    ///   density, layer 1 raw points.
    /// - Three layers: layer 0 passes the least threshold and draws a density; the sum left, 300,000, is compared with the
    ///   least threshold of layers 1 and 2 (10,000,000), not with layer 0's, so both draw raw points.
    /// </remarks>
    [Fact]
    public void Count_DifferentThresholds_ComparesTheLeastOfTheLayersLeft()
    {
        using var data = new Layers(300_000, 200_000, 100_000);
        using var scene = new Scene(data);
        scene.Draw(scene.Auto(0, 1) with { DensityThreshold = 10_000_000 }, scene.Auto(1, 1) with { DensityThreshold = 450_000 });
        Assert.Equal((true, false), (scene.Density(0), scene.Density(1)));

        scene.Draw(
            scene.Auto(0, 1) with
            {
                DensityThreshold = 250_000,
            },
            scene.Auto(1, 1) with
            {
                DensityThreshold = 10_000_000,
            },
            scene.Auto(2, 1) with
            {
                DensityThreshold = 10_000_000,
            }
        );
        Assert.Equal((300_000L, 200_000L, 100_000L), (scene.Switch(0).LastCount, scene.Switch(1).LastCount, scene.Switch(2).LastCount));
        Assert.Equal((true, false, false), (scene.Density(0), scene.Density(1), scene.Density(2)));
    }

    /// <summary>
    /// The counts' sum comes first, and the times' sum reads only the layers left: a layer the counts made a density adds no
    /// term to the times' sum.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Auto layers of 300,000 points (layer 0, at 7 ms) and 200,000 points (layer 1, at 10 ms), each of a threshold of
    ///   400,000.
    ///
    /// Steps:
    /// - Draw.
    ///
    /// Expected:
    /// - The counts' sum (500,000) makes layer 0, of more points, a density. The times' sum of layer 1 alone (10 ms) stays
    ///   under 14.1 ms, so layer 1 draws raw points. With both layers' terms the times' sum would be 15.44 ms; read first,
    ///   it would make layer 1, the longer, the density and leave layer 0 raw.
    /// </remarks>
    [Fact]
    public void Count_BeforeTheTime_LeavesItsDensitiesOutOfTheTimesSum()
    {
        using var data = new Layers(300_000, 200_000);
        using var scene = new Scene(data);
        scene.Draw(scene.Auto(0, 1), scene.Auto(1, 1));
        var (a, b) = (scene.SizeFor(0, 7), scene.SizeFor(1, 10));
        scene.Draw(scene.Auto(0, a) with { DensityThreshold = 400_000 }, scene.Auto(1, b) with { DensityThreshold = 400_000 });
        Assert.Equal(15.44, PredictedTime.Fixed + scene.Switch(0).Terms + scene.Switch(1).Terms, 1e-3);
        Assert.Equal((true, false), (scene.Density(0), scene.Density(1)));
    }

    /// <summary>Points and Density layers over an Auto layer take no part in its panel's sums.</summary>
    /// <remarks>
    /// Preconditions:
    /// - Over the same 300,000 points: an Auto layer at 9 ms of a threshold of 400,000, a Points layer of the same size, and a
    ///   Density layer.
    ///
    /// Steps:
    /// - Draw.
    ///
    /// Expected:
    /// - The Auto layer draws raw points (its time and count alone are under the thresholds, and with the Points layer's
    ///   they would pass both: 600,000 points, 16.44 ms). The Points layer draws raw points and the Density layer a
    ///   density.
    /// </remarks>
    [Fact]
    public void PointsAndDensity_OverAnAutoLayer_StayOutOfTheSums()
    {
        using var data = new Layers(300_000);
        using var scene = new Scene(data);
        scene.Draw(scene.Auto(0, 1));
        var size = scene.SizeFor(0, 9);
        scene.Draw(
            scene.Auto(0, size) with
            {
                DensityThreshold = 400_000,
            },
            scene.Auto(0, size) with
            {
                Draw = PointDraw.Points,
            },
            scene.Auto(0, size) with
            {
                Draw = PointDraw.Density,
            }
        );
        Assert.Equal(9, scene.Switch(0).LastTime, 1e-4);
        Assert.Equal((false, false, true), (scene.Density(0), scene.Density(1), scene.Density(2)));
    }

    /// <summary>The layers of a figure's other plot lie in another panel and take no part in a plot's sums.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A figure of two plots side by side on a headless renderer of 1,280 × 360, each with an Auto layer over the same
    ///   300,000 points, sized from a first render so that each predicts 9 ms. Both layers are number 0 of their plot, so
    ///   only the plot's number tells their panels apart.
    ///
    /// Steps:
    /// - Render the figure.
    ///
    /// Expected:
    /// - The figure's choice read two panels. Each layer predicts 9 ms and draws raw points; one sum over both plots would
    ///   be 16.44 ms.
    /// </remarks>
    [Fact]
    public void FiguresOtherPlot_StaysOutOfTheSums()
    {
        using var data = new Layers(300_000);
        using var pool = new PlotWorkerPool(4);
        using var renderer = new HeadlessRenderer(new NullBackend(), 1280, 360, pool);
        FigureSpec Figure(float size) => HyperPlot.Figure.HConcat(data.Spec(data.Auto(0, size)), data.Spec(data.Auto(0, size)));
        IndexedPointPlan Layer(int plot) => (IndexedPointPlan)renderer.Figure!.Plans[plot].Panels[0].Layers[0];

        renderer.Render(Figure(1));
        var size = DiameterFor(9, Layer(0).Switch.LastCount, Layer(0).Switch.LastLargestBin);
        renderer.Render(Figure(size));
        Assert.Equal(2, renderer.Figure!.Plans[0].Budget.LastPanels);
        Assert.Equal((9.0, 9.0), (Math.Round(Layer(0).Switch.LastTime, 4), Math.Round(Layer(1).Switch.LastTime, 4)));
        Assert.Equal((false, false), (Layer(0).DrewDensity, Layer(1).DrewDensity));
    }

    /// <summary>
    /// A facet of two Auto layers over 6 panels: every panel's sum reads each layer's count and largest bin of all panels,
    /// and the layer the sums choose draws a density in every panel (design §5.5.2 "ファセットのパネル").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two Auto layers of 300,000 points each, faceted by 6 names row by row (50,000 points a layer a panel), on an engine
    ///   of 960 × 480. Layer 0 at 9 ms and layer 1 at 8 ms, from all panels' counts and bins.
    ///
    /// Steps:
    /// - Draw.
    ///
    /// Expected:
    /// - 6 panels, 6 sums read. The sum is 15.44 ms: every part of layer 0 draws a density, every part of layer 1 raw points.
    ///   Taken with one panel's counts (a sixth), the sum would be under 14.1 ms.
    /// </remarks>
    [Fact]
    public void Facet_TheLayerTheSumsChoose_IsADensityInEveryPanel()
    {
        using var data = new Layers(300_000, 300_000);
        using var scene = new Scene(data, 960, 480, facet: true);
        scene.Draw(scene.Auto(0, 1), scene.Auto(1, 1));
        var (a, b) = (scene.SizeFor(0, 9), scene.SizeFor(1, 8));
        scene.Draw(scene.Auto(0, a), scene.Auto(1, b));
        var panels = scene.Engine.Plan!.Panels;
        Assert.Equal(6, panels.Count);
        Assert.Equal(6, scene.Engine.Plan.Budget.LastPanels);
        var (first, second) = (scene.Switch(0), scene.Switch(1));
        Assert.Equal(15.44, PredictedTime.Fixed + first.Terms + second.Terms, 1e-3);
        var sixth =
            PredictedTime.Fixed
            + PredictedTime.Terms(first.LastCount / 6, a, first.LastLargestBin)
            + PredictedTime.Terms(second.LastCount / 6, b, second.LastLargestBin);
        Assert.True(sixth < PredictedTime.Threshold);
        Assert.All(panels, p => Assert.True(((IndexedPointPlan)p.Layers[0]).DrewDensity));
        Assert.All(panels, p => Assert.False(((IndexedPointPlan)p.Layers[1]).DrewDensity));
    }

    /// <summary>
    /// A density that would go back alone stays one while its panel's sum with it as raw points is at or over 11.28 ms, and
    /// goes back once the sum is under the band (design §5.5.2 "戻り").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two Auto layers of 300,000 points each.
    ///
    /// Steps:
    /// - Draw layer 0 at 16 ms and layer 1 at 7 ms. Draw layer 0 at 6 ms and layer 1 at 7 ms. Draw layer 1 at 3 ms.
    ///
    /// Expected:
    /// - First layer 0 draws a density and layer 1 raw points. At 6 ms layer 0 alone would go back (its time, its time of
    ///   four times the bin, and its count), but the sum with it is 11.44 ms, in the band, and the sum with four times both
    ///   bins is past 14.1 ms, and it stays a density. With layer 1 at 3 ms the sums are 7.44 and 11.1 ms and layer 0 goes
    ///   back.
    /// </remarks>
    [Fact]
    public void Back_SumWithTheLayerInTheBand_KeepsADensity()
    {
        using var data = new Layers(300_000, 300_000);
        using var scene = new Scene(data);
        scene.Draw(scene.Auto(0, 1), scene.Auto(1, 1));
        var (over, back, band, low) = (scene.SizeFor(0, 16), scene.SizeFor(0, 6), scene.SizeFor(1, 7), scene.SizeFor(1, 3));
        scene.Draw(scene.Auto(0, over), scene.Auto(1, band));
        Assert.Equal((true, false), (scene.Density(0), scene.Density(1)));

        scene.Draw(scene.Auto(0, back), scene.Auto(1, band));
        var alone = scene.Switch(0);
        Assert.False(DensitySwitch.Next(true, alone.LastCount, alone.Threshold, alone.LastTime, alone.LastTimeAtFourBins));
        Assert.Equal(11.44, PredictedTime.Fixed + scene.Switch(0).Terms + scene.Switch(1).Terms, 1e-3);
        Assert.Equal((true, false), (scene.Density(0), scene.Density(1)));

        scene.Draw(scene.Auto(0, back), scene.Auto(1, low));
        Assert.Equal((false, false), (scene.Density(0), scene.Density(1)));
    }

    /// <summary>
    /// Two densities whose panel's sums let both go back go back one a frame, the shorter predicted time first, though it
    /// is the higher layer's number.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two Auto layers of 300,000 points each.
    ///
    /// Steps:
    /// - Draw both at 16 ms. Draw layer 0 at 3 ms and layer 1 at 2 ms. Draw layer 0 at 2.9 ms and layer 1 at 2 ms.
    ///
    /// Expected:
    /// - At 16 ms both draw densities. In the next frame layer 1, the shorter, goes back and layer 0 stays a density, though
    ///   the sums with both as raw points (3.44 ms) let both. In the frame after, layer 0 goes back too.
    /// </remarks>
    [Fact]
    public void Back_TwoDensities_GoBackOneAFrameTheShorterFirst()
    {
        using var data = new Layers(300_000, 300_000);
        using var scene = new Scene(data);
        scene.Draw(scene.Auto(0, 1), scene.Auto(1, 1));
        var (over0, over1) = (scene.SizeFor(0, 16), scene.SizeFor(1, 16));
        var (back0, back1, next0) = (scene.SizeFor(0, 3), scene.SizeFor(1, 2), scene.SizeFor(0, 2.9));
        scene.Draw(scene.Auto(0, over0), scene.Auto(1, over1));
        Assert.Equal((true, true), (scene.Density(0), scene.Density(1)));

        scene.Draw(scene.Auto(0, back0), scene.Auto(1, back1));
        Assert.Equal(3.44, PredictedTime.Fixed + scene.Switch(0).Terms + scene.Switch(1).Terms, 1e-3);
        Assert.True(
            PredictedTime.Clears(
                PredictedTime.Fixed + scene.Switch(0).Terms + scene.Switch(1).Terms,
                PredictedTime.Fixed + scene.Switch(0).TermsAtFour + scene.Switch(1).TermsAtFour
            )
        );
        Assert.Equal((true, false), (scene.Density(0), scene.Density(1)));

        scene.Draw(scene.Auto(0, next0), scene.Auto(1, back1));
        Assert.Equal((false, false), (scene.Density(0), scene.Density(1)));
    }

    /// <summary>Two densities of the same predicted time that may both go back: the lower layer's number goes back first.</summary>
    /// <remarks>
    /// Preconditions:
    /// - Two Auto layers over the same columns of 300,000 points, at the same size.
    ///
    /// Steps:
    /// - Draw both at 16 ms. Draw both at 2 ms.
    ///
    /// Expected:
    /// - At 16 ms both draw densities. At 2 ms the times are equal bit for bit; layer 0 goes back and layer 1 stays a
    ///   density.
    /// </remarks>
    [Fact]
    public void Back_EqualTimes_TheLowerLayerFirst()
    {
        using var data = new Layers(300_000);
        using var scene = new Scene(data);
        scene.Draw(scene.Auto(0, 1), scene.Auto(0, 1));
        var (over, back) = (scene.SizeFor(0, 16), scene.SizeFor(0, 2));
        scene.Draw(scene.Auto(0, over), scene.Auto(0, over));
        Assert.Equal((true, true), (scene.Density(0), scene.Density(1)));

        scene.Draw(scene.Auto(0, back), scene.Auto(0, back));
        Assert.Equal(scene.Switch(0).LastTime, scene.Switch(1).LastTime);
        Assert.Equal((false, true), (scene.Density(0), scene.Density(1)));
    }

    /// <summary>
    /// The way back's time of four times the bin takes four times the largest bin of every layer in the sum, not only of
    /// the layer that would go back (design §5.5.2 "戻り").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two Auto layers of 300,000 points each.
    ///
    /// Steps:
    /// - Draw layer 0 at 16 ms and layer 1 at 9.14 ms. Draw layer 0 at 2.74 ms and layer 1 at 9.14 ms. Draw layer 1 at 5 ms.
    ///
    /// Expected:
    /// - First layer 0 draws a density. At 2.74 ms the sum with it as raw points is 10.32 ms, under 11.28; with four times
    ///   layer 0's bin alone the time would be 10.99 ms, under 14.1, but with four times both layers' bins it is 15.95 ms,
    ///   and layer 0 stays a density. With layer 1 at 5 ms, layer 0 goes back.
    /// </remarks>
    [Fact]
    public void Back_FourTimesTheBinOfEveryLayer_KeepsADensity()
    {
        using var data = new Layers(300_000, 300_000);
        using var scene = new Scene(data);
        scene.Draw(scene.Auto(0, 1), scene.Auto(1, 1));
        var (over, back, held, low) = (scene.SizeFor(0, 16), scene.SizeFor(0, 2.74), scene.SizeFor(1, 9.14), scene.SizeFor(1, 5));
        scene.Draw(scene.Auto(0, over), scene.Auto(1, held));
        Assert.Equal((true, false), (scene.Density(0), scene.Density(1)));

        scene.Draw(scene.Auto(0, back), scene.Auto(1, held));
        var (first, second) = (scene.Switch(0), scene.Switch(1));
        Assert.Equal(10.32, PredictedTime.Fixed + first.Terms + second.Terms, 1e-2);
        Assert.Equal(10.99, PredictedTime.Fixed + first.TermsAtFour + second.Terms, 1e-2);
        Assert.Equal(15.95, PredictedTime.Fixed + first.TermsAtFour + second.TermsAtFour, 1e-2);
        Assert.Equal((true, false), (scene.Density(0), scene.Density(1)));

        scene.Draw(scene.Auto(0, back), scene.Auto(1, low));
        Assert.Equal((false, false), (scene.Density(0), scene.Density(1)));
    }

    /// <summary>
    /// A density goes back only when its panel's counts' sum with it is under 0.8 times the least threshold: a layer whose
    /// count alone would let it stays a density while another layer's points are summed with it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Auto layers of 300,000 points (layer 0) and 200,000 points (layer 1), each of a threshold of 400,000, at 1 DIP.
    ///
    /// Steps:
    /// - Draw. Draw again at 1.01 DIP. Draw with layer 1 drawn as a density.
    ///
    /// Expected:
    /// - Layer 0 draws a density, by the counts' sum. Drawn again it stays one through every frame of the draw (the frame
    ///   before the last drew a density too): its count alone (300,000) is under 320,000, but the sum with layer 1's
    ///   (500,000) is not. With layer 1 a density, out of the sums, layer 0 goes back.
    /// </remarks>
    [Fact]
    public void Back_CountsSumWithTheLayer_KeepsADensity()
    {
        using var data = new Layers(300_000, 200_000);
        using var scene = new Scene(data);
        PointLayerSpec Layer(int layer, float size) => scene.Auto(layer, size) with { DensityThreshold = 400_000 };
        scene.Draw(Layer(0, 1), Layer(1, 1));
        Assert.Equal((true, false), (scene.Density(0), scene.Density(1)));
        var first = scene.Switch(0).LastTime;

        scene.Draw(Layer(0, 1.01f), Layer(1, 1.01f));
        Assert.NotEqual(first, scene.Switch(0).LastTime);
        Assert.True(scene.Switch(0).LastCount < 400_000 * DensitySwitch.Back);
        Assert.True(scene.Switch(0).WasDensity, "The frame before the last drew layer 0 as raw points: it went back and was made a density again.");
        Assert.Equal((true, false), (scene.Density(0), scene.Density(1)));

        scene.Draw(Layer(0, 1), Layer(1, 1) with { Draw = PointDraw.Density });
        Assert.Equal((false, true), (scene.Density(0), scene.Density(1)));
    }

    /// <summary>
    /// The way back's time is the panel's sum too, not the layer's alone: a density whose time alone and whose sum of four
    /// times the bins are under their bounds stays one while the sum of the times is at or over 11.28 ms.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A panel of two Auto layers whose sums are set as a full scan leaves them (<see cref="DensitySwitch.SetSums"/>): a
    ///   raw layer whose terms are its points' alone, 9 ms at the largest bin and at four times it (about 1.5e7 points in
    ///   view, past what a test's data reaches; the overlap's term 0), and a density drawn as one in the last frame of
    ///   terms of 1 ms at both. Counts of 1,000 each, thresholds of 1e7.
    /// - A second panel of the same layers but the raw one's terms 5 ms.
    ///
    /// Steps:
    /// - Read each panel's way back (<see cref="PointBudget.Qualify"/>).
    ///
    /// Expected:
    /// - First panel: the time alone is 2.56 ms and the sum of four times the bins 11.56 ms, under 14.1, but the sum of the
    ///   times is 11.56 ms, not under 11.28, so the density may not go back. Second panel: the sums are 7.56 ms and it may.
    /// </remarks>
    [Fact]
    public void Back_TimesSumOfTheOtherLayers_KeepsADensity()
    {
        var (raw, density) = (new DensitySwitch(0), new DensitySwitch(1));
        raw.SetSums(1_000, 10_000_000, 9, 9, density: false, wasDensity: false);
        density.SetSums(1_000, 10_000_000, 1, 1, density: true, wasDensity: true);
        PointBudget.Qualify([raw, density]);
        Assert.Equal(2.56, density.LastTime, 1e-12);
        Assert.False(density.MayReturn);

        raw.SetSums(1_000, 10_000_000, 5, 5, density: false, wasDensity: false);
        density.SetSums(1_000, 10_000_000, 1, 1, density: true, wasDensity: true);
        PointBudget.Qualify([raw, density]);
        Assert.True(density.MayReturn);
    }

    /// <summary>
    /// The sums at the bounds themselves (design §5.5.2 "時間の和", "戻り"): a times' sum of exactly 14.1 ms makes a density,
    /// and a density does not go back when its sums with it are exactly 11.28 ms, exactly 14.1 ms with four times the bins,
    /// or a count of exactly 0.8 times the threshold; just under each, it does.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two Auto layers' sums set as a full scan leaves them (<see cref="DensitySwitch.SetSums"/>), with terms chosen so
    ///   that 1.56 plus the terms, added in the order the panel adds them, is the bound to the last bit (checked).
    ///
    /// Steps:
    /// - Raise a panel of two raw layers whose times' sum is 14.1 ms, and one whose sum is a step under it.
    /// - Qualify a panel of a raw layer and a density at each bound of the way back, and 1e-12 ms under it (a step of a
    ///   term's last bit can round away in the sum).
    ///
    /// Expected:
    /// - At 14.1 ms the longer layer becomes a density and the other stays raw; a step under, both stay raw.
    /// - At each bound of the way back the density may not go back; a step under all three, it may.
    /// </remarks>
    [Fact]
    public void Sums_AtTheBounds_SwitchAndHold()
    {
        const double Bound = PredictedTime.Threshold;
        var back = PredictedTime.Threshold * DensitySwitch.Back;
        var (first, second) = (new DensitySwitch(0), new DensitySwitch(1));
        var rest = Bound - (PredictedTime.Fixed + 7);
        Assert.Equal(Bound, PredictedTime.Fixed + 7 + rest);
        Assert.True(rest < 7);
        first.SetSums(1_000, 10_000_000, 7, 7, density: false, wasDensity: false);
        second.SetSums(1_000, 10_000_000, rest, rest, density: false, wasDensity: false);
        PointBudget.Raise([first, second]);
        Assert.Equal((true, false), (first.Density, second.Density));

        first.SetSums(1_000, 10_000_000, 7, 7, density: false, wasDensity: false);
        second.SetSums(1_000, 10_000_000, Math.BitDecrement(rest), Math.BitDecrement(rest), density: false, wasDensity: false);
        Assert.True(PredictedTime.Fixed + 7 + Math.BitDecrement(rest) < Bound);
        PointBudget.Raise([first, second]);
        Assert.Equal((false, false), (first.Density, second.Density));

        // The way back: the raw layer's terms 5 ms, the density's the rest of each bound.
        bool MayReturn(long count, double terms, double termsAtFour)
        {
            var (raw, density) = (new DensitySwitch(0), new DensitySwitch(1));
            raw.SetSums(0, 1_000_000, 5, 5, density: false, wasDensity: false);
            density.SetSums(count, 1_000_000, terms, termsAtFour, density: true, wasDensity: true);
            PointBudget.Qualify([raw, density]);
            return density.MayReturn;
        }

        var (atBack, atBound) = (back - (PredictedTime.Fixed + 5), Bound - (PredictedTime.Fixed + 5));
        Assert.Equal(back, PredictedTime.Fixed + 5 + atBack);
        Assert.Equal(Bound, PredictedTime.Fixed + 5 + atBound);
        Assert.Equal(800_000, 1_000_000 * DensitySwitch.Back);
        var (under, underBound) = (atBack - 1e-12, atBound - 1e-12);
        Assert.True(PredictedTime.Fixed + 5 + under < back && PredictedTime.Fixed + 5 + underBound < Bound);
        Assert.False(MayReturn(1_000, atBack, underBound));
        Assert.False(MayReturn(1_000, under, atBound));
        Assert.False(MayReturn(800_000, under, underBound));
        Assert.True(MayReturn(799_999, under, underBound));
    }

    /// <summary>
    /// A layer in several panels goes back only when every one of its panels' sums lets it (design §5.5.2 "細部"), whichever
    /// panel is read first.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A density C drawn as one in the last frame, of terms 1 ms; panel A holds C and a raw layer of terms 9 ms (the
    ///   times' sum with C 11.56 ms), panel B holds C alone (2.56 ms). Sums set with <see cref="DensitySwitch.SetSums"/>.
    ///
    /// Steps:
    /// - Read panel A's way back, then B's. Set the sums again and read B's, then A's.
    ///
    /// Expected:
    /// - Panel B alone would let C back; after both panels, in either order, C may not go back.
    /// </remarks>
    [Fact]
    public void Back_EveryPanelOfTheLayer_MustLetIt()
    {
        var (raw, candidate) = (new DensitySwitch(0), new DensitySwitch(1));
        void Set()
        {
            raw.SetSums(1_000, 10_000_000, 9, 9, density: false, wasDensity: false);
            candidate.SetSums(1_000, 10_000_000, 1, 1, density: true, wasDensity: true);
        }

        Set();
        PointBudget.Qualify([candidate]);
        Assert.True(candidate.MayReturn);
        PointBudget.Qualify([raw, candidate]);
        Assert.False(candidate.MayReturn);

        Set();
        PointBudget.Qualify([raw, candidate]);
        PointBudget.Qualify([candidate]);
        Assert.False(candidate.MayReturn);
    }

    /// <summary>
    /// The machine's time scale multiplies a panel's whole sum, the fixed term too (design §5.5.2 "ほかの機械"): two layers
    /// whose sum is 7.14 ms, at or over half of 14.1 ms, and whose times alone at the scale are under 14.1 ms, switch one of
    /// them at a scale of 2 and none at 1.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two Auto layers of 300,000 points each, at 4.5 ms (layer 0) and 4.2 ms (layer 1) at the reference machine's speed.
    ///
    /// Steps:
    /// - Draw at the default scale of 1. Set the engine's <see cref="PlotEngine.GpuTimeScale"/> to 2 and draw.
    ///
    /// Expected:
    /// - At 1 the sum is 7.14 ms and both draw raw points. At 2 the layers' times read 9 ms and 8.4 ms, each under 14.1, and
    ///   the scaled sum 14.28 ms makes layer 0, the longer, a density; layer 1 stays raw points.
    /// </remarks>
    [Fact]
    public void TimeScale_MultipliesTheWholeSum()
    {
        using var data = new Layers(300_000, 300_000);
        using var scene = new Scene(data);
        scene.Draw(scene.Auto(0, 1), scene.Auto(1, 1));
        var (a, b) = (scene.SizeFor(0, 4.5), scene.SizeFor(1, 4.2));
        scene.Draw(scene.Auto(0, a), scene.Auto(1, b));
        Assert.Equal(7.14, PredictedTime.Fixed + scene.Switch(0).Terms + scene.Switch(1).Terms, 1e-3);
        Assert.Equal((false, false), (scene.Density(0), scene.Density(1)));

        scene.Engine.GpuTimeScale = 2;
        scene.Draw(scene.Auto(0, a), scene.Auto(1, b));
        Assert.Equal((9.0, 8.4), (Math.Round(scene.Switch(0).LastTime, 4), Math.Round(scene.Switch(1).LastTime, 4)));
        Assert.Equal((true, false), (scene.Density(0), scene.Density(1)));
    }

    /// <summary>
    /// A panel that let a density back lets no other back in the same frame, though another panel, whose layers differ while
    /// a part's index is still pending there, nominates another layer of it.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two Auto layers of 300,000 points each, faceted by 2 names row by row: panels 0 and 1, each with a part of both
    ///   layers. A pool of 4 whose fourth index build (layer 1's part in panel 1) stops after its first job until the test
    ///   lets it go, so that the part has no snapshot and is not scanned; an engine of 960 × 480 that does not wait.
    ///
    /// Steps:
    /// - Draw until the other three parts scan their full snapshots and their indexes are complete. Draw both layers at
    ///   16 ms. Draw layer 0 at 3 ms and layer 1 at 2 ms.
    ///
    /// Expected:
    /// - Panel 0 sums both layers, panel 1 layer 0 alone (two panels read). At 16 ms both draw densities. Then panel 0's
    ///   candidate is layer 1 (the shorter) and panel 1's layer 0; one goes back, the other stays a density: whichever goes
    ///   first was let back by panel 0, which holds both.
    /// </remarks>
    [Fact]
    public void Back_PanelThatLetOneBack_LetsNoOtherInTheFrame()
    {
        using var data = new Layers(300_000, 300_000);
        using var released = new ManualResetEventSlim();
        var allocations = 0;
        var tuning = SpatialIndexTuning.Default with
        {
            JobEnded = (job, elapsed) =>
            {
                if (job == SpatialJob.Allocate && Interlocked.Increment(ref allocations) == 4)
                {
                    _ = released.Wait(TimeSpan.FromSeconds(60));
                }
            },
        };
        using var pool = new PlotWorkerPool(4) { IndexTuning = tuning };
        var backend = new NullBackend();
        using var engine = new PlotEngine(backend, 960, 480, pool: pool);
        IndexedPointPlan Part(int panel, int layer) => (IndexedPointPlan)engine.Plan!.Panels[panel].Layers[layer];
        void Draw(float first, float second)
        {
            var spec = data.Spec(data.Auto(0, first), data.Auto(1, second));
            engine.SetPlot(spec with { Facet = new FacetWrapSpec { By = data.Table.Cat("g").ToColumnRef() } });
            backend.Images.Sent();
            engine.RenderFrame();
        }

        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            bool Settled() =>
                Volatile.Read(ref allocations) == 4
                && engine.Plan is { Panels.Count: 2 }
                && Part(0, 0).ScannedFull(backend.Images.Frame)
                && Part(0, 1).ScannedFull(backend.Images.Frame)
                && Part(1, 0).ScannedFull(backend.Images.Frame)
                && Part(0, 0).SpatialIndex!.IsComplete
                && Part(0, 1).SpatialIndex!.IsComplete
                && Part(1, 0).SpatialIndex!.IsComplete;
            Draw(1, 1);
            while (!Settled())
            {
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), "The three parts did not settle in 60 s.");
                Draw(1, 1);
            }

            Draw(1, 1);
            Assert.Equal(4, Volatile.Read(ref allocations));
            Assert.False(Part(1, 1).ScannedFull(backend.Images.Frame));
            Assert.Equal(2, engine.Plan!.Budget.LastPanels);
            var (first, second) = (Part(0, 0).Switch, Part(0, 1).Switch);
            float Size(DensitySwitch layer, double time) => DiameterFor(time, layer.LastCount, layer.LastLargestBin);
            var (over0, over1, back0, back1) = (Size(first, 16), Size(second, 16), Size(first, 3), Size(second, 2));

            Draw(over0, over1);
            Assert.Equal((true, true), (first.Density, second.Density));

            Draw(back0, back1);
            Assert.True(first.LastTime > second.LastTime);
            Assert.True(first.Density ^ second.Density, $"Layer 0 drew a density: {first.Density}; layer 1: {second.Density}.");
        }
        finally
        {
            released.Set();
        }
    }

    /// <summary>
    /// Frames whose panel's sums switch one of two Auto layers both ways allocate nothing once warm (invariant 3, design
    /// §5.5.2 "確保").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two Auto layers of 300,000 points each, layer 0 at 9 ms and layer 1 at 8 ms at the whole view. Views of sides 100
    ///   and 25 around (50, 50), with the same ticks (the plot has no guides).
    ///
    /// Steps:
    /// - Draw the two views in turn for 6 rounds; collect; count 2 rounds.
    ///
    /// Expected:
    /// - In the warm-up, layer 0 draws both a density (the whole view's sum, 15.44 ms) and raw points (the zoomed view's);
    ///   no frame is provisional at the end. 0 bytes.
    /// </remarks>
    [Fact]
    public void Frames_SwitchingByTheSums_AllocateNothing()
    {
        using var data = new Layers(300_000, 300_000);
        using var scene = new Scene(data);
        scene.Draw(scene.Auto(0, 1), scene.Auto(1, 1));
        scene.Draw(scene.Auto(0, scene.SizeFor(0, 9)), scene.Auto(1, scene.SizeFor(1, 8)));
        PanelView2D[] views = [View(100), View(25)];
        var drawn = new bool[views.Length];
        for (var round = 0; round < 6; round++)
        {
            for (var i = 0; i < views.Length; i++)
            {
                scene.Show(views[i]);
                drawn[i] = scene.Density(0);
                Assert.False(scene.Density(1));
            }
        }

        Assert.Equal((true, false), (drawn[0], drawn[1]));
        Assert.False(scene.Engine.IsProvisional, "The warm frames ended provisional: the counted frames would not take the final path.");
        PerformanceAssert.DoesNotAllocate(() =>
        {
            for (var round = 0; round < 2; round++)
            {
                foreach (var view in views)
                {
                    scene.Show(view);
                }
            }
        });

        static PanelView2D View(double side) =>
            new(ViewRange.FromValues(50 - (side / 2), 50 + (side / 2)), ViewRange.FromValues(50 - (side / 2), 50 + (side / 2)));
    }

    /// <summary>Returns the markers' diameter, in pixels, that makes a count and a largest bin predict a time.</summary>
    private static float DiameterFor(double time, long count, double largestBin)
    {
        var n = count / PredictedTime.PointsUnit;
        var term = time - PredictedTime.Fixed - (PredictedTime.PerPoints * n);
        var area = term / (PredictedTime.Overlap * n * Math.Pow(largestBin, PredictedTime.Exponent));
        return (float)Math.Sqrt(area * 4 / Math.PI);
    }

    /// <summary>
    /// A table of a pair of columns (x, y) a layer of data, each of seeded uniform points over [0, 100]² and NaN in the rows
    /// past them, with facet columns of 6 names (f) and 2 names (g) row by row.
    /// </summary>
    private sealed class Layers : IDisposable
    {
        private readonly List<IDisposable> _columns = [];

        public Layers(params int[] counts)
        {
            var builder = DataTable.Create();
            var rows = counts.Max();
            for (var layer = 0; layer < counts.Length; layer++)
            {
                var random = new Random(650 + layer);
                var (xs, ys) = (new double[rows], new double[rows]);
                for (var i = 0; i < rows; i++)
                {
                    (xs[i], ys[i]) = i < counts[layer] ? (random.NextDouble() * 100, random.NextDouble() * 100) : (double.NaN, double.NaN);
                }

                var (x, y) = (Column.Wrap(xs), Column.Wrap(ys));
                _columns.Add(x);
                _columns.Add(y);
                builder = builder.Add($"x{layer}", x).Add($"y{layer}", y);
            }

            Table = builder
                .Add("f", Column.Categorical(Enumerable.Range(0, rows).Select(i => (string?)Six[i % 6]).ToArray()))
                .Add("g", Column.Categorical(Enumerable.Range(0, rows).Select(i => (string?)Six[i % 2]).ToArray()))
                .Build();
        }

        public DataTable Table { get; }

        /// <summary>Returns an Auto layer over a pair of the table's columns.</summary>
        public PointLayerSpec Auto(int data, float size) =>
            new()
            {
                X = Table.Col<double>($"x{data}"),
                Y = Table.Col<double>($"y{data}"),
                Size = size,
            };

        public PlotSpec Spec(params LayerSpec[] layers) => GeomTesting.Spec(Table, layers);

        public void Dispose()
        {
            foreach (var column in _columns)
            {
                column.Dispose();
            }
        }
    }

    /// <summary>An engine that waits for its indexes over a table of layers, plain or faceted, on the null backend.</summary>
    private sealed class Scene : IDisposable
    {
        private readonly Layers _data;
        private readonly bool _facet;
        private readonly NullBackend _backend = new();
        private readonly PlotWorkerPool _pool = new(4);

        public Scene(Layers data, int width = 640, int height = 360, bool facet = false)
        {
            (_data, _facet) = (data, facet);
            Engine = new PlotEngine(_backend, width, height, pool: _pool) { WaitsForIndexes = true };
        }

        public PlotEngine Engine { get; }

        public PointLayerSpec Auto(int data, float size) => _data.Auto(data, size);

        /// <summary>Sets a spec of the layers and draws a frame.</summary>
        public void Draw(params LayerSpec[] layers)
        {
            var spec = _data.Spec(layers);
            Engine.SetPlot(_facet ? spec with { Facet = new FacetWrapSpec { By = _data.Table.Cat("f").ToColumnRef() } } : spec);
            _backend.Images.Sent();
            Engine.RenderFrame();
        }

        /// <summary>Sets the view of the plot's panel and draws a frame.</summary>
        public void Show(PanelView2D view)
        {
            _backend.Images.Sent();
            Engine.Views.SetView(Panel, view);
            Engine.RenderFrame();
        }

        /// <summary>Returns the switch of a layer, the same in every panel.</summary>
        public DensitySwitch Switch(int layer) => Part(layer).Switch;

        /// <summary>Returns whether a layer's first part drew a density in the last frame.</summary>
        public bool Density(int layer) => Part(layer).DrewDensity;

        /// <summary>Returns the markers' size that makes a layer predict a time, from the count and the bin of its last frame.</summary>
        public float SizeFor(int layer, double time) => DiameterFor(time, Switch(layer).LastCount, Switch(layer).LastLargestBin);

        public void Dispose()
        {
            Engine.Dispose();
            _pool.Dispose();
        }

        private IndexedPointPlan Part(int layer) => (IndexedPointPlan)Engine.Plan!.Panels[0].Layers[layer];
    }
}
