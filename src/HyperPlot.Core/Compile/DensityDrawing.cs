using System.Numerics;
using System.Runtime.InteropServices;
using HyperPlot.Rendering;

namespace HyperPlot;

/// <summary>
/// What the parts of one point layer on the index path share in a panel (design §5.5.2 "行 R3b1 の密度と切り替え"): the
/// drawing the layer takes this frame, raw points or density, chosen once a frame from the upper counts of all its parts,
/// and the top of the density's scale, the largest normalized bin of every part's drawn tiles.
/// </summary>
/// <remarks>One thread at a time: the frame's. After the parts' lists have grown, a frame allocates nothing.</remarks>
internal sealed class DensitySwitch
{
    /// <summary>The share of the threshold under which a layer drawn as a density goes back to raw points.</summary>
    internal const double Back = 0.8;

    private readonly List<IndexedPointPlan> _parts = [];
    private long _frame = -1;
    private PointDraw _draw;
    private long _threshold = -1;
    private bool _density;

    /// <summary>Gets a value indicating whether the layer draws a density this frame. Valid after <see cref="Prepare"/>.</summary>
    public bool Density => _density;

    /// <summary>Gets the top of the density's scale this frame, in points a bin of the chosen level; 0 when nothing is drawn.</summary>
    public double Top { get; private set; }

    /// <summary>Gets the upper count of the points in the view the last choice read, summed over the parts. Tests only.</summary>
    internal long LastCount { get; private set; }

    /// <summary>Adds a part of the layer.</summary>
    public void Add(IndexedPointPlan part) => _parts.Add(part);

    /// <summary>
    /// Chooses the drawing of the layer for a frame, once a frame: for <see cref="PointDraw.Auto"/>, density when the upper
    /// count of the points in the view (<c>Inside + Touching</c>, summed over the parts) passes the threshold, raw points when
    /// it falls under 0.8 times it, and the last frame's drawing between. Then plans every part's density and takes the top.
    /// </summary>
    /// <returns>Whether the layer draws a density this frame.</returns>
    public bool Prepare(RecordContext context, PointLayerSpec point)
    {
        var frame = context.Images.Frame;
        if (frame == _frame)
        {
            return _density;
        }

        _frame = frame;

        // A new drawing or threshold forgets the last frame's choice: the layer is counted again from raw points.
        if (point.Draw != _draw || point.DensityThreshold != _threshold)
        {
            (_draw, _threshold, _density) = (point.Draw, point.DensityThreshold, false);
        }

        if (point.Draw == PointDraw.Auto)
        {
            long count = 0;
            foreach (var part in CollectionsMarshal.AsSpan(_parts))
            {
                count += part.UpperCount(context);
            }

            LastCount = count;
            _density = _density ? count >= point.DensityThreshold * Back : count > point.DensityThreshold;
        }
        else
        {
            _density = point.Draw == PointDraw.Density;
        }

        // Every part plans its pieces, a part still on its coarse snapshot too, and the top is the largest of them.
        var top = 0.0;
        foreach (var part in CollectionsMarshal.AsSpan(_parts))
        {
            top = Math.Max(top, part.PlanDensity(context, _density));
        }

        Top = top;
        return _density;
    }
}

/// <summary>
/// One piece of a density a part draws (design §5.5.2 "水準の選び方"): a cell of the snapshot's table, drawn from the tile
/// of the cell itself or of its deepest ancestor that has one, or as one fill of its count when no ancestor has a tile.
/// </summary>
/// <param name="CellCode">The cell's code at its level.</param>
/// <param name="CellLevel">The cell's level.</param>
/// <param name="TileCode">The code of the cell whose tile is read, at <paramref name="TileLevel"/>; or the coarse grid's 0.</param>
/// <param name="TileLevel">The level of the tile read: the cell's, an ancestor's, or −1 for a fill.</param>
/// <param name="Count">A fill's points, normalized to a bin of the chosen level.</param>
internal readonly record struct DensityPiece(ulong CellCode, int CellLevel, ulong TileCode, int TileLevel, double Count);

/// <summary>The density's arithmetic of design §5.5.2, apart from the plan so that tests read it.</summary>
internal static class DensityMath
{
    /// <summary>The deepest level a density tile is drawn at.</summary>
    internal const int DeepestLevel = DensityTiles.DeepestTileLevel;

    /// <summary>
    /// Returns the level of the tiles to draw (design §5.5.2 "水準の選び方"): the deepest whose bins, cells of 8 levels
    /// deeper, are at least a physical pixel wide on both axes, from 0 to 24.
    /// </summary>
    /// <param name="pixelsX">The pixels the whole range of x spans: the range's width times the pixels of one unit.</param>
    /// <param name="pixelsY">The pixels the whole range of y spans.</param>
    public static int TileLevel(double pixelsX, double pixelsY)
    {
        // A bin of level L is the range over 2^(L + 8): at least a pixel while 2^(L + 8) is at most the range's pixels.
        var pixels = Math.Min(Math.Abs(pixelsX), Math.Abs(pixelsY));
        if (!(pixels >= 1 << DensityTiles.BinLevels))
        {
            return 0;
        }

        return Math.Clamp((int)Math.Floor(Math.Log2(pixels)) - DensityTiles.BinLevels, 0, DeepestLevel);
    }

    /// <summary>Returns the level whose cells are about a physical pixel wide on both axes, for counting the view (design §5.5.2).</summary>
    public static int PixelLevel(double pixelsX, double pixelsY)
    {
        var pixels = Math.Max(Math.Abs(pixelsX), Math.Abs(pixelsY));
        return !(pixels > 1) ? 0 : Math.Clamp((int)Math.Ceiling(Math.Log2(pixels)), 0, 32);
    }

    /// <summary>Returns a count normalized to a bin of the chosen level from a bin k levels shallower: c / 4^k.</summary>
    public static double Normalized(double count, int shallower) => count / Math.Pow(4, shallower);

    /// <summary>
    /// Returns where a count lies from 0 to 1 under the top (design §5.5.2 "描き方"): log(1 + c) / log(1 + m), c / m, or
    /// ∛c / ∛m; 0 for a count of 0 or a top of 0.
    /// </summary>
    public static double Position(DensityNorm norm, double count, double top)
    {
        if (!(count > 0) || !(top > 0))
        {
            return 0;
        }

        var t = norm switch
        {
            DensityNorm.Linear => count / top,
            DensityNorm.Cbrt => Math.Cbrt(count) / Math.Cbrt(top),
            _ => Math.Log(1 + count) / Math.Log(1 + top),
        };
        return Math.Clamp(t, 0, 1);
    }

    /// <summary>
    /// Returns how the image command paints a tile read k levels shallower than the chosen level (design §5.5.2 "広さで
    /// 正規化する"): the transform's kind as the image's flags read it, its constant, and the domain's high end, so that
    /// the shader's value of c equals the value of c / 4^k under the top.
    /// </summary>
    public static (uint Kind, float Constant, float High) Paint(DensityNorm norm, double top, int shallower)
    {
        var area = Math.Pow(4, shallower);
        return norm switch
        {
            // Linear: c / (m · 4^k).
            DensityNorm.Linear => (0u, 0f, (float)(top * area)),

            // Pow 1/3: ∛c / (∛m · 4^(k/3)).
            DensityNorm.Cbrt => (3u, 1f / 3, (float)(Math.Cbrt(top) * Math.Cbrt(area))),

            // Symlog of constant 4^k: log10(1 + c / 4^k) over log10(1 + m).
            _ => (2u, (float)area, (float)Math.Log10(1 + top)),
        };
    }

    /// <summary>Returns the density's color table of a layer drawn in its own color: from transparent to the color.</summary>
    public static ColorMap Ramp(Color color) => new(color with { A = 0 }, color);

    /// <summary>Returns a vector's components in screen order: swapped when the plot is flipped.</summary>
    public static Vector2 Screen(double along, double across, bool flipped) =>
        flipped ? new Vector2((float)across, (float)along) : new Vector2((float)along, (float)across);
}
