using System.Numerics;
using HyperPlot.Interaction;
using static HyperPlot.Tests.GeomTesting;

namespace HyperPlot.Tests;

/// <summary>
/// The parts of a split layer over runs of the layer's rows map only their runs (invariant 2, Issue #459): points and
/// rectangles on the CPU path, and their hit tests through the part's own buffers.
/// </summary>
public sealed class PartRowsTests
{
    private static readonly PanelId Panel = new(0, 0);
    private static readonly string[] Three = ["a", "b", "c"];

    /// <summary>
    /// Each part of a point layer colored by a category whose rows lie in runs maps its run alone, and a hit in the last part
    /// answers its own row and values (Issue #459).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Nine points (i + 1, 10 − i) in three categories, each on a run of three rows; domains [0, 10]; 640 × 360.
    ///
    /// Steps:
    /// - Draw the layer colored by the category. Hit the eighth point (row 7) in XY mode with a radius of 2 pixels.
    ///
    /// Expected:
    /// - Each part mapped 3 rows, not 9. The hit is row 7 at (8, 3).
    /// </remarks>
    [Fact]
    public void Points_MapTheirRunAndHitTheirRows()
    {
        using var xs = Column.Wrap(Enumerable.Range(0, 9).Select(i => i + 1.0).ToArray());
        using var ys = Column.Wrap(Enumerable.Range(0, 9).Select(i => 10.0 - i).ToArray());
        var table = DataTable
            .Create()
            .Add("x", xs)
            .Add("y", ys)
            .Add("c", Column.Categorical(Enumerable.Range(0, 9).Select(i => (string?)Three[i / 3]).ToArray()))
            .Build();
        using var engine = Drawn(
            Plot.Create(table)
                .Point(table.Col<double>("x"), table.Col<double>("y"), p => p.ColorBy(table.Cat("c")))
                .ScaleX(Scale.Linear().WithDomain(0, 10))
                .ScaleY(Scale.Linear().WithDomain(0, 10))
                .Build()
        );

        Assert.All(((SplitPlan)engine.Plan!.Layers[0]).Parts, part => Assert.Equal(3, ((PointPlan)part).MappedRows));
        var hits = new SeriesHit[1];
        var hit = Assert.Single(hits[..engine.HitTest(Pixel(engine, 8, 3), 2, HitMode.XY, hits)]);
        Assert.Equal((7L, 8.0, 3.0), (hit.Row, hit.X, hit.Y));
    }

    /// <summary>
    /// Each part of a rect layer filled by a category whose rows lie in runs maps its run alone, and a hit in the last part
    /// answers its own row (Issue #459).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Nine rectangles [i, i + 0.8] × [0, 1] in three categories, each on a run of three rows; domains [0, 10]; 640 × 360.
    ///
    /// Steps:
    /// - Draw the layer filled by the category. Hit the middle of the eighth rectangle (row 7) in XY mode with a radius of 1.
    ///
    /// Expected:
    /// - Each part mapped 3 rows, not 9. The hit is row 7, its x the rectangle's center 7.4.
    /// </remarks>
    [Fact]
    public void Rects_MapTheirRunAndHitTheirRows()
    {
        using var x0 = Column.Wrap(Enumerable.Range(0, 9).Select(i => (double)i).ToArray());
        using var x1 = Column.Wrap(Enumerable.Range(0, 9).Select(i => i + 0.8).ToArray());
        using var y0 = Column.Wrap(new double[9]);
        using var y1 = Column.Wrap(Enumerable.Repeat(1.0, 9).ToArray());
        var table = DataTable
            .Create()
            .Add("x0", x0)
            .Add("x1", x1)
            .Add("y0", y0)
            .Add("y1", y1)
            .Add("c", Column.Categorical(Enumerable.Range(0, 9).Select(i => (string?)Three[i / 3]).ToArray()))
            .Build();
        var rect = new RectLayerSpec
        {
            XMin = table.Col<double>("x0"),
            XMax = table.Col<double>("x1"),
            YMin = table.Col<double>("y0"),
            YMax = table.Col<double>("y1"),
            FillBy = table.Cat("c").ToColumnRef(),
        };
        using var engine = Drawn(
            Spec(table, rect) with
            {
                ScaleX = Scale.Linear().WithDomain(0, 10),
                ScaleY = Scale.Linear().WithDomain(0, 10),
                Theme = Theme.Light,
            }
        );

        Assert.All(((SplitPlan)engine.Plan!.Layers[0]).Parts, part => Assert.Equal(3, ((RectPlan)part).MappedRows));
        var hits = new SeriesHit[1];
        var hit = Assert.Single(hits[..engine.HitTest(Pixel(engine, 7.4, 0.5), 1, HitMode.XY, hits)]);
        Assert.Equal(7L, hit.Row);
        Assert.Equal(7.4, hit.X, 9);
    }

    /// <summary>Each part of a segment layer colored by a category whose rows lie in runs maps its run alone (Issue #459).</summary>
    /// <remarks>
    /// Preconditions:
    /// - Nine segments from (i, 0) to (i + 0.5, 1) in three categories, each on a run of three rows; domains [0, 10].
    ///
    /// Steps:
    /// - Draw the layer colored by the category.
    ///
    /// Expected:
    /// - Each part mapped 3 rows, not 9.
    /// </remarks>
    [Fact]
    public void Segments_MapTheirRun()
    {
        using var x0 = Column.Wrap(Enumerable.Range(0, 9).Select(i => (double)i).ToArray());
        using var x1 = Column.Wrap(Enumerable.Range(0, 9).Select(i => i + 0.5).ToArray());
        using var y0 = Column.Wrap(new double[9]);
        using var y1 = Column.Wrap(Enumerable.Repeat(1.0, 9).ToArray());
        var table = DataTable
            .Create()
            .Add("x0", x0)
            .Add("x1", x1)
            .Add("y0", y0)
            .Add("y1", y1)
            .Add("c", Column.Categorical(Enumerable.Range(0, 9).Select(i => (string?)Three[i / 3]).ToArray()))
            .Build();
        var segment = new SegmentLayerSpec
        {
            X = table.Col<double>("x0"),
            XEnd = table.Col<double>("x1"),
            Y = table.Col<double>("y0"),
            YEnd = table.Col<double>("y1"),
            ColorBy = table.Cat("c").ToColumnRef(),
        };
        using var engine = Drawn(
            Spec(table, segment) with
            {
                ScaleX = Scale.Linear().WithDomain(0, 10),
                ScaleY = Scale.Linear().WithDomain(0, 10),
                Theme = Theme.Light,
            }
        );

        Assert.All(((SplitPlan)engine.Plan!.Layers[0]).Parts, part => Assert.Equal(3, ((SegmentPlan)part).MappedRows));
    }

    /// <summary>Each part of a text layer colored by a category whose rows lie in runs maps its run alone (Issue #459).</summary>
    /// <remarks>
    /// Preconditions:
    /// - Nine labels at (i + 1, 5) in three categories, each on a run of three rows; domains [0, 10].
    ///
    /// Steps:
    /// - Draw the layer colored by the category.
    ///
    /// Expected:
    /// - Each part mapped 3 rows, not 9.
    /// </remarks>
    [Fact]
    public void Texts_MapTheirRun()
    {
        using var xs = Column.Wrap(Enumerable.Range(0, 9).Select(i => i + 1.0).ToArray());
        using var ys = Column.Wrap(Enumerable.Repeat(5.0, 9).ToArray());
        var table = DataTable
            .Create()
            .Add("x", xs)
            .Add("y", ys)
            .Add("c", Column.Categorical(Enumerable.Range(0, 9).Select(i => (string?)Three[i / 3]).ToArray()))
            .Build();
        using var engine = Drawn(
            Plot.Create(table)
                .Text(table.Col<double>("x"), table.Col<double>("y"), table.Col<double>("x"), t => t.ColorBy(table.Cat("c")))
                .ScaleX(Scale.Linear().WithDomain(0, 10))
                .ScaleY(Scale.Linear().WithDomain(0, 10))
                .Build()
        );

        Assert.All(((SplitPlan)engine.Plan!.Layers[0]).Parts, part => Assert.Equal(3, ((TextPlan)part).MappedRows));
    }

    private static PlotEngine Drawn(PlotSpec spec)
    {
        var engine = new PlotEngine(new NullBackend(), 640, 360) { WaitsForIndexes = true };
        engine.SetPlot(spec);
        engine.RenderFrame();
        return engine;
    }

    private static Vector2 Pixel(PlotEngine engine, double x, double y) =>
        engine.ViewToPixel(Panel, ViewRange.FromValues(x, x), ViewRange.FromValues(y, y));
}
