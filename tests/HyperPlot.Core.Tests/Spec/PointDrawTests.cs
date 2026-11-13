using static HyperPlot.Tests.GeomTesting;

namespace HyperPlot.Tests;

/// <summary>
/// The density members of a point layer (design §4.1, §4.8 "検査（行 R3b1）" and "差分（行 R3b1）", §10 R3b1 row): the
/// builder, the checks of the compile, and the diff, which takes a change of them as a change of looks.
/// </summary>
public sealed class PointDrawTests
{
    /// <summary>
    /// The builder's Draw and Density take every named value and a threshold of 1 or more, refuse the rest, and the spec they
    /// add holds them; a layer that sets neither holds the defaults (design §4.9).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A table of "x" and "y" of 3 doubles.
    ///
    /// Steps:
    /// - Build a point layer with Draw of each value of Enum.GetValues of PointDraw, and with Density of each value of
    ///   Enum.GetValues of DensityNorm.
    /// - In the configure, call Draw with −1 and with the value past the largest; Density with the norm −1 and past the
    ///   largest, and with the thresholds 0, −1, and long.MinValue.
    /// - Build a layer with Draw(Density) and Density(Cbrt, viridis, 1), one with Density(threshold: long.MaxValue), one with
    ///   Density() alone, and one with neither.
    ///
    /// Expected:
    /// - Each named value is held. ArgumentOutOfRangeException naming draw, norm, or threshold for each refused value.
    /// - The set layer holds Density, Cbrt, the same viridis map, and 1; the second long.MaxValue; Density() holds Log, no
    ///   map, and 10,000,000, as does the layer that sets neither, with Auto.
    /// </remarks>
    [Fact]
    public void Builder_DrawAndDensity_CheckAndHold()
    {
        using var xs = Column.Wrap(new double[] { 1, 2, 3 });
        using var ys = Column.Wrap(new double[] { 4, 5, 6 });
        var table = DataTable.Create().Add("x", xs).Add("y", ys).Build();
        var (x, y) = (table.Col<double>("x"), table.Col<double>("y"));
        var draws = Enum.GetValues<PointDraw>();
        var norms = Enum.GetValues<DensityNorm>();
        foreach (var draw in draws)
        {
            Assert.Equal(draw, Built(p => p.Draw(draw)).Draw);
        }

        foreach (var norm in norms)
        {
            Assert.Equal(norm, Built(p => p.Density(norm)).DensityNorm);
        }

        foreach (var draw in new[] { (PointDraw)(-1), (PointDraw)(draws.Max(d => (int)d) + 1) })
        {
            Assert.Equal("draw", Assert.Throws<ArgumentOutOfRangeException>(() => Built(p => p.Draw(draw))).ParamName);
        }

        foreach (var norm in new[] { (DensityNorm)(-1), (DensityNorm)(norms.Max(n => (int)n) + 1) })
        {
            Assert.Equal("norm", Assert.Throws<ArgumentOutOfRangeException>(() => Built(p => p.Density(norm))).ParamName);
        }

        foreach (var threshold in new[] { 0, -1, long.MinValue })
        {
            Assert.Equal("threshold", Assert.Throws<ArgumentOutOfRangeException>(() => Built(p => p.Density(threshold: threshold))).ParamName);
        }

        var set = Built(p => p.Draw(PointDraw.Density).Density(DensityNorm.Cbrt, ColorMaps.Viridis, 1));
        Assert.Equal((PointDraw.Density, DensityNorm.Cbrt, 1L), (set.Draw, set.DensityNorm, set.DensityThreshold));
        Assert.Same(ColorMaps.Viridis, set.DensityMap);
        Assert.Equal(long.MaxValue, Built(p => p.Density(threshold: long.MaxValue)).DensityThreshold);

        var density = Built(p => p.Density());
        Assert.Equal(
            (PointDraw.Auto, DensityNorm.Log, 10_000_000L, null),
            (density.Draw, density.DensityNorm, density.DensityThreshold, density.DensityMap)
        );
        var plain = Built(_ => { });
        Assert.Equal((PointDraw.Auto, DensityNorm.Log, 10_000_000L, null), (plain.Draw, plain.DensityNorm, plain.DensityThreshold, plain.DensityMap));

        PointLayerSpec Built(Action<PointBuilder> configure) =>
            Assert.IsType<PointLayerSpec>(Plot.Create(table).Point(x, y, configure).Build().Layers[0]);
    }

    /// <summary>
    /// The compile takes every named Draw and DensityNorm and a DensityThreshold of 1 or more, and refuses the rest with an
    /// ArgumentOutOfRangeException that names the layer, also for values a spec puts in with <c>with</c> (design §4.8
    /// "検査（行 R3b1）"). The checks compare the names one by one, so a name left out fails here.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Three points over [0, 100]²; a spec of two point layers, the second the one changed.
    ///
    /// Steps:
    /// - Render the second layer with each value of Enum.GetValues of PointDraw and of DensityNorm, with the thresholds 1
    ///   and long.MaxValue, and with the viridis map.
    /// - Render it with Draw and DensityNorm of −1 and past the largest value, and with the thresholds 0, −1, and
    ///   long.MinValue.
    ///
    /// Expected:
    /// - Each accepted spec records the six markers of the two layers.
    /// - Each refused spec throws ArgumentOutOfRangeException whose ParamName is "Layers[1].Draw", "Layers[1].DensityNorm",
    ///   or "Layers[1].DensityThreshold".
    /// </remarks>
    [Fact]
    public void Validate_DensityMembers_TakeEveryNamedValueAndNoOther()
    {
        using var pool = new PlotWorkerPool(1);
        using var plan = TestPlans.Create(pool, Size, Size);
        using var xs = Column.Wrap([10.0, 50, 90]);
        using var ys = Column.Wrap([10.0, 50, 90]);
        var table = DataTable.Create().Add("x", xs).Add("y", ys).Build();
        var point = new PointLayerSpec { X = table.Col<double>("x"), Y = table.Col<double>("y") };
        var draws = Enum.GetValues<PointDraw>();
        var norms = Enum.GetValues<DensityNorm>();

        var accepted = new List<PointLayerSpec>();
        accepted.Add(point with { DensityThreshold = 1 });
        accepted.Add(point with { DensityThreshold = long.MaxValue });
        accepted.AddRange(draws.Select(draw => point with { Draw = draw }));
        accepted.AddRange(norms.Select(norm => point with { DensityNorm = norm }));
        accepted.Add(point with { DensityMap = ColorMaps.Viridis });
        foreach (var layer in accepted)
        {
            Assert.Equal(6, plan.Render(Spec(table, point, layer)).Markers.Length);
        }

        Refused(point with { Draw = (PointDraw)(-1) }, "Layers[1].Draw");
        Refused(point with { Draw = (PointDraw)(draws.Max(d => (int)d) + 1) }, "Layers[1].Draw");
        Refused(point with { DensityNorm = (DensityNorm)(-1) }, "Layers[1].DensityNorm");
        Refused(point with { DensityNorm = (DensityNorm)(norms.Max(n => (int)n) + 1) }, "Layers[1].DensityNorm");
        Refused(point with { DensityThreshold = 0 }, "Layers[1].DensityThreshold");
        Refused(point with { DensityThreshold = -1 }, "Layers[1].DensityThreshold");
        Refused(point with { DensityThreshold = long.MinValue }, "Layers[1].DensityThreshold");

        void Refused(PointLayerSpec layer, string name) =>
            Assert.Equal(name, Assert.Throws<ArgumentOutOfRangeException>(() => plan.Render(Spec(table, point, layer))).ParamName);
    }

    /// <summary>
    /// A new node that changes only Draw, DensityThreshold, DensityNorm, or DensityMap is a change of looks (design §4.8
    /// "差分（行 R3b1）"): the layer's plan stays, and every plan that draws the layer takes the new values, in a plain
    /// layer, in the parts of a split layer, and in the panels of a facet that gathers its rows.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Five points over [0, 100]² and a category of "a" and "b" that takes turns row by row.
    /// - Three arrangements: the plain layer; the layer split by the category; the plain layer wrapped by the category,
    ///   whose two panels gather their rows.
    ///
    /// Steps:
    /// - For each arrangement on a new plan: render the layer; then for each of the four members alone, render the layer
    ///   with it changed (Density, 5, Cbrt, viridis), then the layer as it was.
    ///
    /// Expected:
    /// - The facet has two panels, which gather their rows; the split layer has two parts.
    /// - After each render, the plan of the layer is the object of the first render, and each part's or panel's node holds
    ///   the four members of the node rendered.
    /// </remarks>
    [Fact]
    public void Diff_DensityMembersAlone_KeepTheLayersPlan()
    {
        using var pool = new PlotWorkerPool(1);
        using var xs = Column.Wrap([10.0, 30, 50, 70, 90]);
        using var ys = Column.Wrap([90.0, 70, 50, 30, 10]);
        var table = DataTable.Create().Add("x", xs).Add("y", ys).Add("c", Column.Categorical(["a", "b", "a", "b", "a"])).Build();
        var category = table.Cat("c").ToColumnRef();
        var (ordinal, facet) = (Scale.Ordinal(), new FacetWrapSpec { By = category });
        var plain = new PointLayerSpec { X = table.Col<double>("x"), Y = table.Col<double>("y") };
        var draw = plain with { Draw = PointDraw.Density };
        var threshold = plain with { DensityThreshold = 5 };
        var norm = plain with { DensityNorm = DensityNorm.Cbrt };
        var map = plain with { DensityMap = ColorMaps.Viridis };
        foreach (var name in (string[])["plain", "split", "facet"])
        {
            using var plan = TestPlans.Create(pool, Size, Size);
            _ = plan.Render(Make(name, plain));
            var kept = plan.Layers[0];
            if (name == "facet")
            {
                Assert.Equal(2, plan.Panels.Count);
                Assert.All(plan.Panels, panel => Assert.NotNull(panel.Data?.Map));
            }

            if (name == "split")
            {
                Assert.Equal(2, ((SplitPlan)kept).SeriesCount);
            }

            foreach (var change in (PointLayerSpec[])[draw, threshold, norm, map])
            {
                foreach (var node in (PointLayerSpec[])[change, plain])
                {
                    _ = plan.Render(Make(name, node));
                    Assert.Same(kept, plan.Layers[0]);
                    foreach (var drawn in Nodes(plan))
                    {
                        Assert.Equal(
                            (node.Draw, node.DensityThreshold, node.DensityNorm, node.DensityMap),
                            (drawn.Draw, drawn.DensityThreshold, drawn.DensityNorm, drawn.DensityMap)
                        );
                    }
                }
            }
        }

        PlotSpec Make(string name, PointLayerSpec layer) =>
            name switch
            {
                "plain" => Spec(table, layer),
                "split" => Spec(table, layer with { ColorBy = category }) with { ScaleColor = ordinal },
                _ => Spec(table, layer) with { Facet = facet },
            };

        // The nodes the plans of every panel and part draw.
        static List<PointLayerSpec> Nodes(PlotPlan plan)
        {
            var nodes = new List<PointLayerSpec>();
            foreach (var panel in plan.Panels)
            {
                var layer = panel.Layers[0];
                LayerPlan[] plans = layer is SplitPlan split ? [.. split.Parts] : [layer];
                nodes.AddRange(plans.Select(drawn => Assert.IsType<PointLayerSpec>(drawn.Spec)));
            }

            return nodes;
        }
    }
}
