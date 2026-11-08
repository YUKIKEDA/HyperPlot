using System.Numerics;
using HyperPlot.Interaction;

namespace HyperPlot;

/// <content>The hit test over a panel's layers (design §6.3, row I2a, Issue #337).</content>
internal sealed partial class PlotPlan
{
    /// <summary>
    /// Answers a hit test at a pixel of a panel as last drawn (design §6.3): each layer that answers adds its series' hits, in
    /// the spec's order, on its own pair of scales. A layer that failed or draws nothing yet answers nothing; one that draws
    /// an older version answers provisionally. Returns the hits found, written or not. Allocates nothing.
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
        var scan = new HitScan(results, _store, plot, panel, mode, radius, along, across);
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

        return scan.Found;
    }
}
