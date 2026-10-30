using System.Numerics;
using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>
/// What the geom tests of row G4 share: a 100 × 100 target with the theme without guides and domains of [0, 100] on both
/// axes, so that the value x is the pixel x and the value y is the pixel 100 − y; and the comparison of whole lists.
/// </summary>
internal static class GeomTesting
{
    public const int Size = 100;

    /// <summary>Returns a spec of the layers over a table, with domains of [0, 100] given on both axes.</summary>
    public static PlotSpec Spec(DataTable table, params LayerSpec[] layers) =>
        new()
        {
            Theme = TestPlans.Bare,
            Data = table,
            Layers = layers,
            ScaleX = Scale.Linear().WithDomain(0, Size),
            ScaleY = Scale.Linear().WithDomain(0, Size),
        };

    /// <summary>Returns a list that holds what the plan records before the layers: the panel's fill and its clip.</summary>
    public static RenderList Expected()
    {
        var list = new RenderList();
        list.DrawRect(0, 0, Size, Size, TestPlans.Bare.PanelBackground);
        list.SetClip(0, 0, Size, Size);
        return list;
    }

    /// <summary>Asserts that two lists hold the same commands and records, entry for entry.</summary>
    public static void AssertSameList(RenderList expected, RenderList actual)
    {
        Assert.Equal(expected.Commands.ToArray(), actual.Commands.ToArray());
        Assert.Equal(expected.Polylines.ToArray(), actual.Polylines.ToArray());
        Assert.Equal(expected.Segments.ToArray(), actual.Segments.ToArray());
        Assert.Equal(expected.Rects.ToArray(), actual.Rects.ToArray());
        Assert.Equal(expected.RectClips.ToArray(), actual.RectClips.ToArray());
        Assert.Equal(expected.Columns.ToArray(), actual.Columns.ToArray());
        Assert.Equal(expected.ColumnClips.ToArray(), actual.ColumnClips.ToArray());
    }

    /// <summary>Returns the trapezoids a list holds, without their colors.</summary>
    public static ColumnTrapezoid[] Trapezoids(RenderList list) =>
        list.Columns.ToArray().Select(c => new ColumnTrapezoid(c.Left, c.Right, c.TopLeft, c.TopRight, c.BottomLeft, c.BottomRight)).ToArray();

    /// <summary>Returns the rectangles a list holds after the panel's fill, as (x, y, width, height).</summary>
    public static (float X, float Y, float Width, float Height)[] DataRects(RenderList list) =>
        list.Rects[1..].ToArray().Select(r => (r.X, r.Y, r.Width, r.Height)).ToArray();

    /// <summary>Returns the segments of a list's polyline as pairs of ends.</summary>
    public static (Vector2 Start, Vector2 End)[] SegmentsOf(RenderList list, int polyline)
    {
        var record = list.Polylines[polyline];
        return list.Segments.Slice(record.SegmentStart, record.SegmentCount).ToArray().Select(s => (s.Start, s.End)).ToArray();
    }

    /// <summary>Asserts that a recorded float is within 1e-3 of a value computed in double.</summary>
    public static void Close(double expected, double actual) => Assert.InRange(actual, expected - 1e-3, expected + 1e-3);

    /// <summary>Returns a domain's ends as doubles.</summary>
    public static (double Min, double Max) Ends(ScaleRange domain) => (domain.Origin + domain.Lower, domain.Origin + domain.Upper);
}
