using System.Numerics;
using System.Runtime.InteropServices;
using HyperPlot.Rendering;

namespace HyperPlot;

/// <summary>
/// The drawing of one point layer on the index path (design §5.5.2 "`Auto` の切り替え"): raw points or density, chosen once
/// a frame for all its parts, those of every facet panel included, from what each part's scan before the recording found
/// (<see cref="IndexedPointPlan.Scan"/>): the upper counts of the points in the view, summed, and the largest bin, the
/// greatest of the parts'.
/// </summary>
/// <remarks>One thread at a time: the frame's. After the parts' list has grown, a frame allocates nothing.</remarks>
internal sealed class DensitySwitch
{
    /// <summary>
    /// The share of each threshold under which a layer drawn as a density goes back to raw points (design §5.5.2
    /// "入れ替わりを抑える"): of <see cref="PointLayerSpec.DensityThreshold"/>, and of <see cref="PredictedTime.Threshold"/>.
    /// </summary>
    internal const double Back = 0.8;

    private readonly List<IndexedPointPlan> _parts = [];
    private long _frame = -1;
    private PointDraw _draw;
    private long _threshold = -1;
    private bool _density;

    /// <summary>Gets a value indicating whether the layer draws a density this frame. Valid after <see cref="Choose"/>.</summary>
    public bool Density => _density;

    /// <summary>Gets the upper count of the points in the view the last choice read, summed over the parts. Tests only.</summary>
    internal long LastCount { get; private set; }

    /// <summary>Gets the largest bin the last choice read, the greatest of the parts', in points a physical pixel. Tests only.</summary>
    internal double LastLargestBin { get; private set; }

    /// <summary>Gets the predicted time of drawing the layer as raw points the last choice read, in ms; 0 when it read none. Tests only.</summary>
    internal double LastTime { get; private set; }

    /// <summary>Gets the predicted time the last choice read with four times the largest bin, in ms. Tests only.</summary>
    internal double LastTimeAtFourBins { get; private set; }

    /// <summary>Adds a part of the layer, of any panel.</summary>
    public void Add(IndexedPointPlan part) => _parts.Add(part);

    /// <summary>
    /// Chooses the drawing of the layer for a frame, once a frame (design §5.5.2 "`Auto` の切り替え", "入れ替わりを抑える"):
    /// for <see cref="PointDraw.Auto"/>, the drawing <see cref="Next"/> gives from the last one, the summed upper count, and
    /// the predicted times of the parts the frame scanned on a full snapshot; <see cref="PointDraw.Points"/> and
    /// <see cref="PointDraw.Density"/> read nothing. A frame in which no part scanned a full snapshot predicts no time and
    /// keeps the last drawing: raw points for a layer with no decision yet.
    /// </summary>
    /// <returns>Whether the layer draws a density this frame.</returns>
    public bool Choose(RecordContext context, PointLayerSpec point)
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

        if (point.Draw != PointDraw.Auto)
        {
            _density = point.Draw == PointDraw.Density;
            return _density;
        }

        var (count, largest, full) = (0L, 0.0, false);
        foreach (var part in CollectionsMarshal.AsSpan(_parts))
        {
            if (part.ScannedFull(frame))
            {
                (count, largest, full) = (count + part.ScannedCount, Math.Max(largest, part.ScannedLargestBin), true);
            }
        }

        // A frame in which no part scanned a full snapshot reads no condition and keeps the drawing: a layer with no
        // decision yet stays on raw points, and one that drew a density stays one (design §5.5.2 "入れ替わりを抑える").
        if (!full)
        {
            (LastCount, LastLargestBin, LastTime, LastTimeAtFourBins) = (0, 0, 0, 0);
            return _density;
        }

        // The markers' diameter in physical pixels, as the recording scales it.
        var diameter = point.Size * context.Scale;
        var (time, timeAtFour) = (PredictedTime.Of(count, diameter, largest), PredictedTime.Of(count, diameter, PredictedTime.LevelJump * largest));
        (LastCount, LastLargestBin, LastTime, LastTimeAtFourBins) = (count, largest, time, timeAtFour);
        _density = Next(_density, count, point.DensityThreshold, time, timeAtFour);
        return _density;
    }

    /// <summary>
    /// Returns the drawing of an <see cref="PointDraw.Auto"/> layer after a frame (design §5.5.2 "`Auto` の切り替え",
    /// "入れ替わりを抑える", "水準が替わるところ"): from raw points, a density when the upper count passes the threshold or the
    /// predicted time reaches <see cref="PredictedTime.Threshold"/>; from a density, raw points only when the count is under
    /// 0.8 times the threshold, the time under 0.8 times its threshold, and the time with four times the largest bin under
    /// its threshold. A layer with no decision yet comes from raw points.
    /// </summary>
    /// <param name="density">Whether the layer drew a density in the last frame.</param>
    /// <param name="count">The upper count of the points in the view, summed over the parts.</param>
    /// <param name="threshold">The layer's <see cref="PointLayerSpec.DensityThreshold"/>.</param>
    /// <param name="time">The predicted time, in ms (<see cref="PredictedTime.Of"/>).</param>
    /// <param name="timeAtFourBins">The predicted time with four times the largest bin, in ms.</param>
    internal static bool Next(bool density, long count, long threshold, double time, double timeAtFourBins) =>
        density
            ? !(count < threshold * Back && time < PredictedTime.Threshold * Back && timeAtFourBins < PredictedTime.Threshold)
            : count > threshold || time >= PredictedTime.Threshold;
}

/// <summary>
/// What the parts of one point layer on the index path share in a panel (design §5.5.2 "ブロック", "濃さの上端 m"): their
/// number, which sets the points of a block of their pages, and the top of the density's scale, the largest normalized bin
/// of every part's drawn pieces. Another panel's parts are not among them.
/// </summary>
/// <remarks>One thread at a time: the frame's. After the parts' list has grown, a frame allocates nothing.</remarks>
internal sealed class DensityPanel
{
    private readonly List<IndexedPointPlan> _parts = [];
    private long _frame = -1;

    /// <summary>Gets the top of the density's scale this frame, in points a square physical pixel; 0 when nothing is drawn.</summary>
    public double Top { get; private set; }

    /// <summary>
    /// Gets the number of the layer's parts in the panel, a part of no point too (design §5.5.2 "ブロック"): the parts that
    /// lie over one another, which set the points of a block of their pages.
    /// </summary>
    public int PartCount => _parts.Count;

    /// <summary>Adds a part of the layer in the panel.</summary>
    public void Add(IndexedPointPlan part) => _parts.Add(part);

    /// <summary>
    /// Sets each part's drawing for the frame, once a frame, and takes the top: the largest of the parts' that draw a
    /// density, a part still on its coarse snapshot too.
    /// </summary>
    public void Prepare(RecordContext context, bool density)
    {
        var frame = context.Images.Frame;
        if (frame == _frame)
        {
            return;
        }

        _frame = frame;
        var top = 0.0;
        foreach (var part in CollectionsMarshal.AsSpan(_parts))
        {
            top = Math.Max(top, part.PlanDensity(density));
        }

        Top = top;
    }
}

/// <summary>
/// The predicted GPU time of drawing a point layer as raw points (design §5.5.2 "予測時間"): T̂ = 1.95 + 5.46 n + 0.0612 n
/// A ρ^0.40 ms, with n the points in the view over 1e7, A the area of a disc of the markers' diameter in square physical
/// pixels, and ρ the largest bin in points a physical pixel. The coefficients and the threshold are the reference machine's
/// (an RTX 3060 Ti, a JIT build); another machine is not promised to fit 16.7 ms under the threshold.
/// </summary>
internal static class PredictedTime
{
    /// <summary>The fixed term, in ms.</summary>
    internal const double Fixed = 1.95;

    /// <summary>The term of the points, in ms a unit of <see cref="PointsUnit"/> points.</summary>
    internal const double PerPoints = 5.46;

    /// <summary>The coefficient of the overlap term: n · A · ρ^<see cref="Exponent"/>, A in square physical pixels, ρ in points a physical pixel.</summary>
    internal const double Overlap = 0.0612;

    /// <summary>The exponent of the largest bin in the overlap term.</summary>
    internal const double Exponent = 0.40;

    /// <summary>The points of one unit of n.</summary>
    internal const double PointsUnit = 1e7;

    /// <summary>The predicted time, in ms, from which an <see cref="PointDraw.Auto"/> layer draws a density (design §5.5.2 "境目").</summary>
    internal const double Threshold = 14.1;

    /// <summary>
    /// The most the largest bin grows when the chosen tile level gets one deeper (design §5.5.2 "水準が替わるところ"): a bin's
    /// area on the screen is a quarter, so its points a pixel are 1 to 4 times the shallower level's.
    /// </summary>
    internal const double LevelJump = 4;

    /// <summary>Returns the predicted time, in ms, in doubles, not rounded. Allocates nothing.</summary>
    /// <param name="points">The upper count of the points in the view, summed over the layer's parts.</param>
    /// <param name="diameter">The markers' diameter in physical pixels, whatever their shape.</param>
    /// <param name="largestBin">The largest bin, in points a physical pixel.</param>
    public static double Of(long points, double diameter, double largestBin)
    {
        var n = points / PointsUnit;
        var area = Math.PI * diameter * diameter / 4;
        var overlap = n > 0 && largestBin > 0 ? Overlap * n * area * Math.Pow(largestBin, Exponent) : 0;
        return Fixed + (PerPoints * n) + overlap;
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
/// <param name="Count">A fill's points a square physical pixel: its count over the cell's area on the screen.</param>
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

    /// <summary>
    /// Returns the area on the screen, in square physical pixels, of a bin of a cell's tile: the cell's extent over 256 on
    /// each axis times the physical pixels of a unit (design §5.5.2 "広さで正規化する").
    /// </summary>
    /// <param name="extentX">The cell's extent along x, in units of the snapshot's scale space.</param>
    /// <param name="extentY">The cell's extent along y.</param>
    /// <param name="perUnitX">The physical pixels of a unit along x, signed.</param>
    /// <param name="perUnitY">The physical pixels of a unit along y, signed.</param>
    public static double BinArea(double extentX, double extentY, double perUnitX, double perUnitY) =>
        Math.Abs(extentX * perUnitX * (extentY * perUnitY)) / ((double)DensityTiles.Side * DensityTiles.Side);

    /// <summary>
    /// Returns a resident cell's estimate of the largest bin (design §5.5.2 "密度タイルが付く前"): its count over its area on
    /// the screen, or over a bin's area when the cell is smaller than a bin; 0 when neither area is positive, so that the
    /// estimate is never NaN or infinite.
    /// </summary>
    /// <param name="count">The cell's points.</param>
    /// <param name="extentX">The cell's extent along x, in units of the snapshot's scale space.</param>
    /// <param name="extentY">The cell's extent along y.</param>
    /// <param name="perUnitX">The physical pixels of a unit along x, signed.</param>
    /// <param name="perUnitY">The physical pixels of a unit along y, signed.</param>
    /// <param name="binArea">The area of a bin of the chosen level on the screen (<see cref="BinArea"/>).</param>
    public static double CellEstimate(long count, double extentX, double extentY, double perUnitX, double perUnitY, double binArea) =>
        Normalized(count, Math.Max(Math.Abs(extentX * perUnitX * (extentY * perUnitY)), binArea));

    /// <summary>
    /// Returns a count normalized to the points of a square physical pixel: the count over the area of its bin or cell on
    /// the screen (design §5.5.2 "広さで正規化する"), so that the same density has the same value whatever the bin's level
    /// and whatever the part's range.
    /// </summary>
    public static double Normalized(double count, double area) => area > 0 ? count / area : 0;

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
    /// Returns how the image command paints a tile whose bins cover an area on the screen (design §5.5.2 "広さで正規化する"):
    /// the transform's kind as the image's flags read it, its constant, and the domain's high end, so that the shader's
    /// value of a bin's count c equals the value of c / area under the top.
    /// </summary>
    /// <param name="norm">The layer's norm.</param>
    /// <param name="top">The top of the scale, in points a square physical pixel.</param>
    /// <param name="area">The area of a bin of the tile on the screen, in square physical pixels (<see cref="BinArea"/>).</param>
    public static (uint Kind, float Constant, float High) Paint(DensityNorm norm, double top, double area) =>
        norm switch
        {
            // Linear: c / (m · area).
            DensityNorm.Linear => (0u, 0f, (float)(top * area)),

            // Pow 1/3: ∛c / (∛m · ∛area).
            DensityNorm.Cbrt => (3u, 1f / 3, (float)(Math.Cbrt(top) * Math.Cbrt(area))),

            // Symlog of constant area: log10(1 + c / area) over log10(1 + m).
            _ => (2u, (float)area, (float)Math.Log10(1 + top)),
        };

    /// <summary>Returns the density's color table of a layer drawn in its own color: from transparent to the color.</summary>
    public static ColorMap Ramp(Color color) => new(color with { A = 0 }, color);

    /// <summary>Returns a vector's components in screen order: swapped when the plot is flipped.</summary>
    public static Vector2 Screen(double along, double across, bool flipped) =>
        flipped ? new Vector2((float)across, (float)along) : new Vector2((float)along, (float)across);
}
