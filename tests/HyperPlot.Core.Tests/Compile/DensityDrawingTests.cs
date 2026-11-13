using System.Numerics;
using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>
/// What a density draws (design §5.5.2 "水準の選び方", "広さで正規化する", "描き方", "祖先のタイル", "濃さの上端 m", §10 R3b1 row):
/// the level of its tiles, its pieces from a cell's own tile, an ancestor's, or one fill, their colors, the top of the
/// scale, and the image records, all through a plan's render.
/// </summary>
/// <remarks>
/// The lattice of these tests (<see cref="Lattice"/>) is laid out so that every bin count is known without the code:
/// a dense part of 16 points a square unit over [0, 256]², whose quadrants at (0, 0) and (128, 128) hold one more point
/// each and so pass the density split (262,144), and two parts of the same 16 points a square unit with too few points
/// for any tile: a sparse part over [128, 192] × [64, 128] and a third over [112, 208]², whose ranges are 64 and 96 units
/// wide against the dense part's 256. Densities and tops are in points a square physical pixel.
/// </remarks>
[Collection("Large data")]
public sealed class DensityDrawingTests
{
    private const int Width = 512;
    private const int Height = 384;
    private const int DenseCount = (1024 * 1024) + 2;
    private const int SparseCount = (256 * 256) + 2;
    private const int ThirdCount = (384 * 384) + 2;

    /// <summary>
    /// The tile level is the deepest whose bins are at least a physical pixel, on the narrower of x and y, from 0 to 24
    /// (design §5.5.2 "水準の選び方"), at each zoom step and at exactly 1 pixel.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 140,000 seeded points over (0, 256)² and two at (0, 0) and (256, 256), so that the range is 256 on both axes;
    ///   Density; a plan of 512 × 384 physical pixels whose panel is the whole target.
    ///
    /// Steps:
    /// - For each level L from 0 to 24, render x domains [0, d] such that the range spans 2^(L + 8) pixels (bins of exactly
    ///   1 pixel) or 2^(L + 8) × (1 − 2^-30) pixels (bins just under 1 pixel), with y spanning 2^(L + 11) pixels; then the
    ///   same with x and y swapped.
    /// - Render 2^32 and 2^40 pixels on both axes.
    ///
    /// Expected:
    /// - Exactly 1 pixel on the narrower axis gives L; just under 1 pixel gives L − 1 (0 for L = 0), whichever axis is the
    ///   narrower. 2^32 pixels gives 24, and 2^40 pixels stays at 24.
    /// - No piece's cell is deeper than the chosen level.
    /// </remarks>
    [Fact]
    public void TileLevel_AtEachZoomStep_IsTheDeepestWithBinsOfAPixelOnTheNarrowerAxis()
    {
        const int Count = 140_000;
        const double Range = 256;
        var random = new Random(51);
        var x = new double[Count + 2];
        var y = new double[Count + 2];
        for (var i = 0; i < Count; i++)
        {
            (x[i], y[i]) = (random.NextDouble() * Range, random.NextDouble() * Range);
        }

        (x[Count], y[Count], x[Count + 1], y[Count + 1]) = (0, 0, Range, Range);
        using var xs = Column.Wrap(x);
        using var ys = Column.Wrap(y);
        var table = DataTable.Create().Add("x", xs).Add("y", ys).Build();
        using var pool = new PlotWorkerPool(2);
        using var plan = TestPlans.Create(pool, Width, Height);
        var point = new PointLayerSpec
        {
            X = table.Col<double>("x"),
            Y = table.Col<double>("y"),
            Draw = PointDraw.Density,
        };

        int Level(double pixelsX, double pixelsY)
        {
            // The range spans Range × Width / domain pixels: the domain is Range × Width / pixels, exact for a power of two.
            _ = plan.Render(
                GeomTesting.Spec(table, point) with
                {
                    ScaleX = Scale.Linear().WithDomain(0, Range * Width / pixelsX),
                    ScaleY = Scale.Linear().WithDomain(0, Range * Height / pixelsY),
                }
            );
            var guide = plan.Guides.PanelAt(0);
            Assert.Equal((0, 0, Width, Height), (guide.Left, guide.Top, guide.Width, guide.Height));
            var layer = (IndexedPointPlan)plan.Layers[0];
            Assert.All(layer.Pieces.ToArray(), piece => Assert.True(piece.CellLevel <= layer.TileLevel));
            return layer.TileLevel;
        }

        const double Under = 1 - (1.0 / (1L << 30));
        for (var level = 0; level <= 24; level++)
        {
            var exact = Math.Pow(2, level + 8);
            var deeper = Math.Pow(2, level + 11);
            var below = Math.Max(level - 1, 0);
            Assert.Equal(level, Level(exact, deeper));
            Assert.Equal(level, Level(deeper, exact));
            Assert.Equal(below, Level(exact * Under, deeper));
            Assert.Equal(below, Level(deeper, exact * Under));
        }

        Assert.Equal(24, Level(Math.Pow(2, 32), Math.Pow(2, 32)));
        Assert.Equal(24, Level(Math.Pow(2, 40), Math.Pow(2, 40)));
    }

    /// <summary>
    /// A cell of the view with no tile of its own reads its deepest ancestor's tile, and a cell with no ancestor's tile is
    /// one fill of its points over its area on the screen, a rectangle over the cell (design §5.5.2 "水準の選び方", "祖先の
    /// タイル", "広さで正規化する").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The lattice: the dense part's root and its quadrants at (0, 0) and (128, 128) have tiles, its other two quadrants
    ///   and every deeper cell have none; the sparse and the third part have no tile. Density over the Viridis map; a panel
    ///   of 512 × 384 beside the legend.
    ///
    /// Steps:
    /// - Render 3 pixels a unit around (128, 128): the dense part's range spans 768 pixels (level 1), the sparse part's 192
    ///   (level 0).
    /// - Render 24 pixels a unit around (128, 120): the dense part's 6,144 pixels (level 4), the sparse part's 1,536 (level
    ///   2), the third part's 2,304 (level 3).
    ///
    /// Expected:
    /// - Level 1: four pieces, one a quadrant; the quadrants at (0, 0) and (128, 128) read their own tiles, the other two
    ///   read the root's. The sparse part is one fill of its root cell: 65,538 points over 192 × 192 pixels, recorded as
    ///   one rectangle from (256, 192) to (448, 384). Four image records.
    /// - Level 4: the dense pieces are cells of level 4; those in the quadrants with tiles read their quadrant's tile, the
    ///   others the root's, and both kinds are drawn. The sparse part's cell [128, 144) × [112, 128) is one fill of 4,096
    ///   points over 384 × 384 pixels, 1/36, recorded as one rectangle from (256, 0) to (640, 384). The third part's cells
    ///   are fills of level 3, its cell [124, 136) × [112, 124) of 2,304 points over 288 × 288 pixels, 1/36 too.
    /// </remarks>
    [Fact]
    public void Pieces_CellsWithoutATile_ReadTheirAncestorsTileOrAreOneFill()
    {
        using var data = Lattice();
        using var pool = new PlotWorkerPool(4);
        using var plan = LatticePlan(pool);
        var point = data.Point(DensityNorm.Log, opacity: 1);

        var coarse = plan.Render(View(data, point, perUnit: 3, centerX: 128, centerY: 128));
        var (dense, sparse, _) = Parts(plan);
        Assert.Equal((1, 0), (dense.TileLevel, sparse.TileLevel));
        Assert.Equal(
            [new DensityPiece(0, 1, 0, 1, 0), new DensityPiece(1, 1, 0, 0, 0), new DensityPiece(2, 1, 0, 0, 0), new DensityPiece(3, 1, 3, 1, 0)],
            dense.Pieces.ToArray().OrderBy(p => p.CellCode)
        );
        var root = Assert.Single(sparse.Pieces.ToArray());
        Assert.Equal((0UL, 0, 0UL, -1), (root.CellCode, root.CellLevel, root.TileCode, root.TileLevel));
        Near(65_538.0 / (192 * 192), root.Count);
        Assert.Equal(4, coarse.Images.Length);
        Assert.Contains(coarse.Rects.ToArray(), r => Near(r, 256, 192, 192, 192));

        var fine = plan.Render(View(data, point, perUnit: 24, centerX: 128, centerY: 120));
        (dense, sparse, var third) = Parts(plan);
        Assert.Equal((4, 2, 3), (dense.TileLevel, sparse.TileLevel, third.TileLevel));
        var pieces = dense.Pieces.ToArray();
        var (ownQuadrant, rootTile) = (0, 0);
        foreach (var piece in pieces)
        {
            Assert.Equal(4, piece.CellLevel);
            var (cornerX, cornerY) = Corner(dense, piece.CellCode, piece.CellLevel);
            if ((cornerX < 128) == (cornerY < 128))
            {
                // The quadrant's own tile: the cell's code at level 1 is the upper two bits of its code at level 4.
                Assert.Equal((piece.CellCode >> 6, 1), (piece.TileCode, piece.TileLevel));
                ownQuadrant++;
            }
            else
            {
                Assert.Equal((0UL, 0), (piece.TileCode, piece.TileLevel));
                rootTile++;
            }
        }

        Assert.True(ownQuadrant > 0 && rootTile > 0);
        Assert.Contains(pieces, p => Corner(dense, p.CellCode, p.CellLevel) == (112, 112));
        Assert.Contains(pieces, p => Corner(dense, p.CellCode, p.CellLevel) == (128, 112));
        Assert.Equal(pieces.Length, fine.Images.Length);
        var fill = Assert.Single(sparse.Pieces.ToArray());
        Assert.Equal((2, -1), (fill.CellLevel, fill.TileLevel));
        Near(1.0 / 36, fill.Count);
        Assert.Equal((128.0, 112.0), Corner(sparse, fill.CellCode, fill.CellLevel));
        Assert.Contains(fine.Rects.ToArray(), r => Near(r, 256, 0, 384, 384));
        var thirds = third.Pieces.ToArray();
        Assert.All(thirds, p => Assert.Equal((3, -1), (p.CellLevel, p.TileLevel)));
        var cell = Assert.Single(thirds, p => Corner(third, p.CellCode, p.CellLevel) == (124, 112));
        Near(1.0 / 36, cell.Count);
        Assert.Contains(fine.Rects.ToArray(), r => Near(r, 160, 96, 288, 288));
    }

    /// <summary>
    /// The same density of points gives the same color whether a bin of the chosen level's tile, of an ancestor's tile, or
    /// a fill draws it, and whatever the range of the part that draws it, for each norm (design §5.5.2 "広さで正規化する").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The lattice, 16 points a square unit wherever it has points, in three parts whose ranges are 256, 64, and 96 units
    ///   wide (ratios of 4, 8/3, and 3/2). Density over the Viridis map at opacity 1 with the norm of the case; a panel of
    ///   512 × 384 beside the legend.
    /// - The shader's t of a texel value v, as Image.hlsl writes it: s(v) of the transform (none, symlog log10(1 + v / C),
    ///   pow v^C), then (s − low) / (high − low), held to [0, 1].
    /// - The design's t of a density d under a top m: log(1 + d) / log(1 + m), d / m, or ∛d / ∛m, with both in points a
    ///   square physical pixel.
    ///
    /// Steps:
    /// - Render 3 pixels a unit around (128, 128): 16/9 points a square pixel. The dense part draws its quadrants' own
    ///   tiles (bins of 4 points over 1.5 × 1.5 pixels) and the root's (bins of 16 over 3 × 3). The top is 5 / 2.25, a
    ///   bin of a quadrant's tile that also holds a corner point.
    /// - Render 24 pixels a unit around (128, 120): 1/36 points a square pixel, drawn from a quadrant's tile (bins of 4 over
    ///   12 × 12 pixels), from the root's (bins of 16 over 24 × 24), as the sparse part's fill (4,096 points over 384 × 384)
    ///   and as the third part's fill (2,304 points over 288 × 288). The top is 5 / 144.
    ///
    /// Expected:
    /// - At 3 pixels a unit, every image record's t of its tile's bin (4 for its own tile, 16 for the root's) is the design's
    ///   t of 16/9 under 20/9, to within 1e-6, and both kinds are drawn.
    /// - At 24 pixels a unit, every image record's t of its tile's bin is the design's t of 1/36 under 5/144, to within
    ///   1e-6, both tiles are drawn, and the sparse and the third part's fills have Viridis's color at that t, to within 1
    ///   of 255 a channel.
    /// </remarks>
    [Theory]
    [InlineData(DensityNorm.Log)]
    [InlineData(DensityNorm.Linear)]
    [InlineData(DensityNorm.Cbrt)]
    public void SameDensity_FromAnyTileOrFillOfAnyPart_HasTheSameColor(DensityNorm norm)
    {
        using var data = Lattice();
        using var pool = new PlotWorkerPool(4);
        using var plan = LatticePlan(pool);
        var point = data.Point(norm, opacity: 1);

        var coarse = plan.Render(View(data, point, perUnit: 3, centerX: 128, centerY: 128)).Images.ToArray();
        var expected = Design(norm, 16.0 / 9, 20.0 / 9);
        Assert.All(coarse, image => Assert.Equal(expected, ShaderT(image, ShallowerOf(image) == 0 ? 4 : 16), 6));
        Assert.Equal([0, 1], coarse.Select(ShallowerOf).Distinct().Order());

        var fine = plan.Render(View(data, point, perUnit: 24, centerX: 128, centerY: 120));
        expected = Design(norm, 1.0 / 36, 5.0 / 144);
        var images = fine.Images.ToArray();
        Assert.All(images, image => Assert.Equal(expected, ShaderT(image, ShallowerOf(image) == 3 ? 4 : 16), 6));
        Assert.Equal([3, 4], images.Select(ShallowerOf).Distinct().Order());
        var color = ColorMaps.Viridis.Map(expected);
        foreach (var (x, y, side) in new[] { (256f, 0f, 384f), (160f, 96f, 288f) })
        {
            var rect = Assert.Single(fine.Rects.ToArray(), r => Near(r, x, y, side, side));
            Assert.InRange((rect.Color.X * 255) - color.R, -1, 1);
            Assert.InRange((rect.Color.Y * 255) - color.G, -1, 1);
            Assert.InRange((rect.Color.Z * 255) - color.B, -1, 1);
        }
    }

    /// <summary>
    /// The top of the scale is the largest bin or fill drawn this frame in points a square physical pixel, and the parts of
    /// a split layer share it (design §5.5.2 "濃さの上端 m").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The lattice: the root tile's largest bin holds 17 points (16 and a corner point), each quadrant tile's 5 (4 and a
    ///   corner point); the sparse part's fills hold 4,096 points a 16 × 16 cell. Density with Linear over Viridis.
    ///
    /// Steps:
    /// - Render 3 pixels a unit around (128, 128): quadrant bins of 1.5 × 1.5 pixels, root bins of 3 × 3.
    /// - Render 24 pixels a unit around (128, 120): quadrant bins of 12 × 12 pixels, root bins of 24 × 24, fills of
    ///   384 × 384.
    /// - Render 24 pixels a unit around (160, 100), where no quadrant tile is drawn: the root's, and fills.
    ///
    /// Expected:
    /// - Tops of max(5 / 2.25, 17 / 9) = 20/9; max(5 / 144, 17 / 576, 1 / 36) = 5/144; and max(17 / 576, 1 / 36) = 17/576,
    ///   to within 1e-9 of each.
    /// - The three parts read the same switch. At 24 pixels a unit the sparse part's fill of 1/36, its own largest, has the
    ///   color of t = (1/36) / (5/144) = 0.8, not 1: it is drawn under the layer's top.
    /// </remarks>
    [Fact]
    public void Top_IsTheLargestNormalizedBinDrawn_AndTheParts_ShareIt()
    {
        using var data = Lattice();
        using var pool = new PlotWorkerPool(4);
        using var plan = LatticePlan(pool);
        var point = data.Point(DensityNorm.Linear, opacity: 1);

        _ = plan.Render(View(data, point, perUnit: 3, centerX: 128, centerY: 128));
        var (dense, sparse, third) = Parts(plan);
        Assert.Same(dense.Switch, sparse.Switch);
        Assert.Same(dense.Switch, third.Switch);
        Near(20.0 / 9, dense.Switch.Top);

        var fine = plan.Render(View(data, point, perUnit: 24, centerX: 128, centerY: 120));
        Near(5.0 / 144, dense.Switch.Top);
        var color = ColorMaps.Viridis.Map(0.8);
        var rect = Assert.Single(fine.Rects.ToArray(), r => Near(r, 256, 0, 384, 384));
        Assert.InRange((rect.Color.X * 255) - color.R, -0.5, 0.5);
        Assert.InRange((rect.Color.Y * 255) - color.G, -0.5, 0.5);
        Assert.InRange((rect.Color.Z * 255) - color.B, -0.5, 0.5);

        _ = plan.Render(View(data, point, perUnit: 24, centerX: 160, centerY: 100));
        Assert.All(dense.Pieces.ToArray(), p => Assert.Equal(0, p.TileLevel));
        Assert.NotEmpty(sparse.Pieces.ToArray());
        Near(17.0 / 576, dense.Switch.Top);
    }

    /// <summary>
    /// A density's image record holds the cell's face, the part of its own or its ancestor's tile it reads, the paint of
    /// its norm under the top for the tile's bin area on the screen, the color table, the opacity, and the zero-is-
    /// transparent flag (design §5.5.2 "描き方", "祖先のタイル", "広さで正規化する").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The lattice; Density over Viridis with the norm of the case at opacity 0.75; a panel of 512 × 384 beside the legend
    ///   (y up, so a texel row runs toward lower pixels).
    ///
    /// Steps:
    /// - Render 3 pixels a unit around (128, 128) (top 20/9): read the record whose corner is the quadrant [0, 128)² (its
    ///   own tile, bins of 1.5 × 1.5 pixels).
    /// - Render 24 pixels a unit around (128, 120) (top 5/144): read the records of the cells [112, 128)² (the quadrant's
    ///   tile, bins of 12 × 12 pixels) and [128, 144) × [112, 128) (the root's tile, bins of 24 × 24 pixels).
    ///
    /// Expected, for each record:
    /// - Origin at the cell's lower-left corner in pixels: (−128, 576), (−128, 384), and (256, 384); a texel of 1.5 pixels
    ///   along x and −1.5 along y; Range (0, 0, 256, 256).
    /// - SampleOrigin (0, 0), (224, 224), and (128, 112) in face texels; SampleScale 1, 1/8, and 1/16.
    /// - Flags: the transform of the norm (Linear 0, Log 2 for symlog, Cbrt 3 for pow) in bits 2 and 3, and bit 6 for
    ///   zero is transparent, nothing else. Constant, Low, and High as the design gives them for a top m and a bin area a:
    ///   Linear 0, 0, m · a; Log a, 0, log10(1 + m); Cbrt 1/3, 0, ∛m · ∛a.
    /// - Table is Viridis's row; Opacity 0.75; Slice within a page of 64.
    /// </remarks>
    [Theory]
    [InlineData(DensityNorm.Log)]
    [InlineData(DensityNorm.Linear)]
    [InlineData(DensityNorm.Cbrt)]
    public void ImageRecord_HoldsTheFaceTheTilesPartAndThePaint(DensityNorm norm)
    {
        using var data = Lattice();
        using var pool = new PlotWorkerPool(4);
        using var plan = LatticePlan(pool);
        var point = data.Point(norm, opacity: 0.75);

        var coarse = plan.Render(View(data, point, perUnit: 3, centerX: 128, centerY: 128)).Images.ToArray();
        Check(At(coarse, -128, 576), shallower: 0, area: 2.25, top: 20.0 / 9, sampleOrigin: Vector2.Zero);

        var fine = plan.Render(View(data, point, perUnit: 24, centerX: 128, centerY: 120)).Images.ToArray();
        Check(At(fine, -128, 384), shallower: 3, area: 144, top: 5.0 / 144, sampleOrigin: new Vector2(224, 224));
        Check(At(fine, 256, 384), shallower: 4, area: 576, top: 5.0 / 144, sampleOrigin: new Vector2(128, 112));

        static ImageInstance At(ImageInstance[] images, float x, float y) =>
            Assert.Single(images, i => Math.Abs(i.Origin.X - x) < 1e-3 && Math.Abs(i.Origin.Y - y) < 1e-3);

        void Check(in ImageInstance image, int shallower, double area, double top, Vector2 sampleOrigin)
        {
            var (kind, constant, high) = norm switch
            {
                DensityNorm.Linear => (0u, 0.0, top * area),
                DensityNorm.Log => (2u, area, Math.Log10(1 + top)),
                _ => (3u, 1.0 / 3, Math.Cbrt(top) * Math.Cbrt(area)),
            };
            Assert.Equal((kind << 2) | ImageInstance.ZeroTransparent, image.Flags);
            Assert.Equal(constant, image.Constant, 1e-6 * Math.Max(1, constant));
            Assert.Equal(0f, image.Low);
            Assert.Equal(high, image.High, 1e-6 * high);
            Assert.Equal(new Vector4(0, 0, 256, 256), image.Range);
            Assert.Equal(sampleOrigin, image.SampleOrigin);
            Assert.Equal(Math.Pow(2, -shallower), image.SampleScale, 9);
            Assert.Equal(1.5, image.AlongU.X, 4);
            Assert.Equal(0, image.AlongU.Y, 6);
            Assert.Equal(0, image.AlongV.X, 6);
            Assert.Equal(-1.5, image.AlongV.Y, 4);
            Assert.Equal((uint)plan.Images.Table(ColorMaps.Viridis), image.Table);
            Assert.Equal(0.75f, image.Opacity);
            Assert.InRange(image.Slice, 0u, (uint)ResidencyCache.PageSlots - 1);
        }
    }

    /// <summary>Asserts a value to within 1e-9 of its size: the pixels a unit are a division of the panel by the domain.</summary>
    private static void Near(double expected, double actual) => Assert.Equal(expected, actual, 1e-9 * Math.Abs(expected));

    /// <summary>Returns the shader's t of a texel value v of an image record: s(v), then (s − low) / (high − low) in [0, 1].</summary>
    private static double ShaderT(in ImageInstance image, double value)
    {
        var s = ((image.Flags >> 2) & 3) switch
        {
            2 => Math.Log10(1 + (value / image.Constant)),
            3 => Math.Pow(value, image.Constant),
            _ => value,
        };
        return Math.Clamp((s - image.Low) / (image.High - image.Low), 0, 1);
    }

    /// <summary>Returns the design's t of a density under a top (design §5.5.2 "描き方").</summary>
    private static double Design(DensityNorm norm, double density, double top) =>
        norm switch
        {
            DensityNorm.Linear => density / top,
            DensityNorm.Cbrt => Math.Cbrt(density) / Math.Cbrt(top),
            _ => Math.Log(1 + density) / Math.Log(1 + top),
        };

    /// <summary>Returns k, the levels between the chosen level and the tile a record reads: its SampleScale is 2^-k.</summary>
    private static int ShallowerOf(ImageInstance image) => (int)Math.Round(-Math.Log2(image.SampleScale));

    private static bool Near(RectInstance rect, float x, float y, float width, float height) =>
        Math.Abs(rect.X - x) < 1e-3 && Math.Abs(rect.Y - y) < 1e-3 && Math.Abs(rect.Width - width) < 1e-3 && Math.Abs(rect.Height - height) < 1e-3;

    /// <summary>Returns a cell's lower-left corner in data units, from its snapshot's box and origins.</summary>
    private static (double X, double Y) Corner(IndexedPointPlan part, ulong code, int level)
    {
        var snapshot = part.SpatialIndex!.Acquire();
        try
        {
            var (x, y, _, _) = snapshot.CellBox(code, level);
            return (x + snapshot.XRange.Origin, y + snapshot.YRange.Origin);
        }
        finally
        {
            snapshot.Release();
        }
    }

    private static long PointsOf(IndexedPointPlan part)
    {
        var snapshot = part.SpatialIndex!.Acquire();
        try
        {
            return snapshot.PointCount;
        }
        finally
        {
            snapshot.Release();
        }
    }

    /// <summary>Returns the lattice's three parts, told apart by their points.</summary>
    private static (IndexedPointPlan Dense, IndexedPointPlan Sparse, IndexedPointPlan Third) Parts(PlotPlan plan)
    {
        var guide = plan.Guides.PanelAt(0);
        Assert.Equal((0, 0, Width, Height), (guide.Left, guide.Top, guide.Width, guide.Height));
        var parts = ((SplitPlan)plan.Layers[0]).Parts.Cast<IndexedPointPlan>().ToArray();
        Assert.Equal(3, parts.Length);
        return (
            parts.Single(p => PointsOf(p) == DenseCount),
            parts.Single(p => PointsOf(p) == SparseCount),
            parts.Single(p => PointsOf(p) == ThirdCount)
        );
    }

    /// <summary>
    /// Returns a plan whose panel is <see cref="Width"/> × <see cref="Height"/> beside the legend of the lattice's three
    /// parts: the target is wider by the legend's width, measured on a plan of three rows of the same names.
    /// </summary>
    private static PlotPlan LatticePlan(PlotWorkerPool pool)
    {
        using var xs = Column.Wrap(new double[] { 0, 1, 2 });
        var table = DataTable.Create().Add("x", xs).Add("k", Column.Categorical(["d", "s", "t"])).Build();
        var point = new PointLayerSpec
        {
            X = table.Col<double>("x"),
            Y = table.Col<double>("x"),
            ColorBy = table.Cat("k").ToColumnRef(),
        };
        using var probe = TestPlans.Create(pool, Width, Height);
        _ = probe.Render(GeomTesting.Spec(table, point) with { ScaleColor = Scale.Ordinal() });
        return TestPlans.Create(pool, Width + Width - probe.Guides.PanelAt(0).Width, Height);
    }

    /// <summary>Returns a spec of the lattice drawn at some pixels a unit on both axes, around a point.</summary>
    private static PlotSpec View(LatticeData data, PointLayerSpec point, double perUnit, double centerX, double centerY)
    {
        var (halfX, halfY) = (Width / perUnit / 2, Height / perUnit / 2);
        return GeomTesting.Spec(data.Table, point) with
        {
            ScaleX = Scale.Linear().WithDomain(centerX - halfX, centerX + halfX),
            ScaleY = Scale.Linear().WithDomain(centerY - halfY, centerY + halfY),
            ScaleColor = Scale.Ordinal(),
        };
    }

    /// <summary>
    /// Makes the lattice, 16 points a square unit in each part: part "d", 1,024 × 1,024 points at ((i + 0.5) / 4,
    /// (j + 0.5) / 4) and two at (0, 0) and (256, 256); part "s", 256 × 256 points at (128 + (i + 0.5) / 4,
    /// 64 + (j + 0.5) / 4) and two at (128, 64) and (192, 128); part "t", 384 × 384 points at (112 + (i + 0.5) / 4,
    /// 112 + (j + 0.5) / 4) and two at (112, 112) and (208, 208).
    /// </summary>
    private static LatticeData Lattice()
    {
        var length = DenseCount + SparseCount + ThirdCount;
        var x = new double[length];
        var y = new double[length];
        var k = new string?[length];
        var n = 0;
        void Part(int side, double left, double bottom, string name)
        {
            for (var j = 0; j < side; j++)
            {
                for (var i = 0; i < side; i++)
                {
                    (x[n], y[n], k[n]) = (left + ((i + 0.5) / 4), bottom + ((j + 0.5) / 4), name);
                    n++;
                }
            }

            (x[n], y[n], k[n]) = (left, bottom, name);
            (x[n + 1], y[n + 1], k[n + 1]) = (left + (side / 4.0), bottom + (side / 4.0), name);
            n += 2;
        }

        Part(1024, 0, 0, "d");
        Part(256, 128, 64, "s");
        Part(384, 112, 112, "t");
        Assert.Equal(length, n);

        // The quadrants of the dense part: two hold 262,145 points, past the density split, and two 262,144, at it.
        Assert.Equal(DensityTiles.BinCount * 4L, (long)SpatialIndexTuning.Default.DensitySplit);
        Assert.Equal(262_145, Enumerable.Range(0, DenseCount).Count(i => x[i] < 128 && y[i] < 128));
        Assert.Equal(262_144, Enumerable.Range(0, DenseCount).Count(i => x[i] >= 128 && y[i] < 128));
        return new LatticeData(x, y, k);
    }

    /// <summary>The lattice's columns and table.</summary>
    private sealed class LatticeData : IDisposable
    {
        private readonly Column<double> _x;
        private readonly Column<double> _y;

        public LatticeData(double[] x, double[] y, string?[] k)
        {
            (_x, _y) = (Column.Wrap(x), Column.Wrap(y));
            Table = DataTable.Create().Add("x", _x).Add("y", _y).Add("k", Column.Categorical(k)).Build();
        }

        public DataTable Table { get; }

        /// <summary>Returns the lattice's point layer: split by part, drawn as a density over Viridis.</summary>
        public PointLayerSpec Point(DensityNorm norm, double opacity) =>
            new()
            {
                X = Table.Col<double>("x"),
                Y = Table.Col<double>("y"),
                ColorBy = Table.Cat("k").ToColumnRef(),
                Draw = PointDraw.Density,
                DensityNorm = norm,
                DensityMap = ColorMaps.Viridis,
                Opacity = opacity,
            };

        public void Dispose()
        {
            _x.Dispose();
            _y.Dispose();
        }
    }
}
