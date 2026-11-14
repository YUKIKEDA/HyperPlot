using System.Numerics;
using HyperPlot.Interaction;

namespace HyperPlot;

/// <content>The hit test over a panel's layers (design §6.3, row I2a, Issue #337).</content>
internal sealed partial class PlotPlan
{
    // The buffers of the search over a spatial index (design §6.3 "探し方", row R3c): one a plot's plan, made at the first
    // hit test that reaches a layer on the index path, 851,968 bytes, and lent to each hit test after.
    private SpatialNearestScratch? _nearest;

    /// <summary>
    /// Answers a hit test at a pixel of a panel as last drawn (design §6.3): each layer that answers adds its series' hits, in
    /// the spec's order, on its own pair of scales. A layer that failed or draws nothing yet answers nothing; one that draws
    /// an older version answers provisionally. A density's bins come after the points of every layer (row R3c, Issue #471
    /// Decision 15). Returns the hits found, written or not. Allocates nothing once warm.
    /// </summary>
    internal int HitTest(int plot, int panel, Vector2 pixel, float radius, HitMode mode, Span<SeriesHit> results)
    {
        // A polar panel's pixels are not along its axes: row I2a answers on Cartesian panels only (Issue #341 Decision 1).
        if (_whole is not null || _spec is not { } spec || panel >= _panels.Length || spec.Coord is PolarCoordSpec)
        {
            return 0;
        }

        var target = _panels[panel];
        var frames = target.Frames;
        var (along, across) = _guides.Flipped ? (pixel.Y, pixel.X) : (pixel.X, pixel.Y);
        var scan = new HitScan(results, _store, plot, panel, mode, radius, along, across) { Scratch = _nearest };
        for (var i = 0; i < target.Layers.Length && i < spec.Layers.Count; i++)
        {
            var layer = target.Layers[i];
            if (Failed(i) || layer.Readiness == LayerReadiness.Pending)
            {
                continue;
            }

            scan.Layer = i;
            scan.X = spec.Layers[i].SecondaryX ? frames.X2 : frames.X;
            scan.Y = spec.Layers[i].SecondaryY ? frames.Y2 : frames.Y;
            scan.LayerProvisional = layer.Readiness == LayerReadiness.Previous;
            var from = scan.Found;
            layer.Hit(ref scan);

            // A facet's gathered panel answers in its own rows: taken back to the plot's table (Issue #337 Decision 3). A layer
            // without rows (an image's element, a user's geom) is drawn the same in every panel and keeps its answer.
            if (target.Data is { Map: not null } data && _facet is { } facet && !FacetData.Rowless(spec.Layers[i]))
            {
                scan.Remap(from, facet, data.Key);
            }
        }

        _nearest = scan.Scratch;
        BinsLast(results[..Math.Min(scan.Found, results.Length)]);
        return scan.Found;
    }

    /// <summary>
    /// Puts the hits of a density's bins (a row of −1) after the other hits, each kind in its order (row R3c, Issue #471
    /// Decision 15): a bin is at a distance of 0, and the first hit is what a click selects, so that a point under the
    /// cursor stays selectable over a density.
    /// </summary>
    private static void BinsLast(Span<SeriesHit> hits)
    {
        var firstBin = -1;
        for (var i = 0; i < hits.Length; i++)
        {
            if (hits[i].Row < 0)
            {
                firstBin = firstBin < 0 ? i : firstBin;
                continue;
            }

            if (firstBin >= 0)
            {
                // Move the bins between one place on, and the point before them: the copy within a span keeps the overlap.
                var hit = hits[i];
                hits[firstBin..i].CopyTo(hits[(firstBin + 1)..]);
                hits[firstBin++] = hit;
            }
        }
    }

    /// <summary>
    /// Answers the statistics of a panel's series over a range of the x scale's space (design §6.5 "計測", row I4b, Issue #390
    /// Decisions 2, 3, 4, 10, 15): each series layer's y, a split layer's by category, a stat's and a stack's from their
    /// derived tables, a band's two edges. A layer on the secondary x, a polar panel, and the layers that are not series
    /// answer none. Waits for no worker; allocates nothing.
    /// </summary>
    internal int Statistics(int plot, int panel, ScaleRange x, Span<SeriesStatistics> results)
    {
        if (_whole is not null || _spec is not { } spec || panel >= _panels.Length || spec.Coord is PolarCoordSpec)
        {
            return 0;
        }

        var target = _panels[panel];
        var frames = target.Frames;
        var scan = new HitScan(results, _store, plot, panel, x);
        for (var i = 0; i < target.Layers.Length && i < spec.Layers.Count; i++)
        {
            var layer = target.Layers[i];
            if (Failed(i) || layer.Readiness == LayerReadiness.Pending || spec.Layers[i].SecondaryX)
            {
                continue;
            }

            scan.Layer = i;
            scan.X = frames.X;
            scan.Y = spec.Layers[i].SecondaryY ? frames.Y2 : frames.Y;
            scan.LayerProvisional = layer.Readiness == LayerReadiness.Previous;
            layer.Hit(ref scan);
        }

        return scan.Found;
    }
}
