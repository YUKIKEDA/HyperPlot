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
/// each and so pass the density split (262,144), and a sparse part of the same 16 points a square unit over
/// [128, 192] × [64, 128], with too few points for any tile.
/// </remarks>
[Collection("Large data")]
public sealed class DensityDrawingTests
{
    private const int Width = 512;
    private const int Height = 384;
    private const int DenseCount = (1024 * 1024) + 2;
    private const int SparseCount = (256 * 256) + 2;

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
    /// one fill of its points, a rectangle over the cell (design §5.5.2 "水準の選び方", "祖先のタイル").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The lattice: the dense part's root and its quadrants at (0, 0) and (128, 128) have tiles, its other two quadrants
    ///   and every deeper cell have none; the sparse part has no tile. Density over the Viridis map; a plan of 512 × 384.
    ///
    /// Steps:
    /// - Render 3 pixels a unit around (128, 128): the dense part's range spans 768 pixels (level 1), the sparse part's 192
    ///   (level 0).
    /// - Render 24 pixels a unit around (128, 120): 6,144 pixels (level 4) and 1,536 pixels (level 2).
    ///
    /// Expected:
    /// - Level 1: four pieces, one a quadrant; the quadrants at (0, 0) and (128, 128) read their own tiles, the other two
    ///   read the root's. The sparse part is one fill of its root cell: 65,538 points over 4^8 bins of its level, 1 + 2^-15,
    ///   recorded as one rectangle over [128, 192] × [64, 128]: (256, 192) to (448, 384) in pixels. Four image records.
    /// - Level 4: the dense pieces are cells of level 4; those in the quadrants with tiles read their quadrant's tile, the
    ///   others the root's, and both kinds are drawn. The sparse part's cell [128, 144) × [112, 128) is one fill of 4,096
    ///   points over 4^8 bins, 1/16, recorded as one rectangle from (256, 0) to (640, 384).
    /// </remarks>
    [Fact]
    public void Pieces_CellsWithoutATile_ReadTheirAncestorsTileOrAreOneFill()
    {
        using var data = Lattice();
        using var pool = new PlotWorkerPool(4);
        using var plan = TestPlans.Create(pool, Width, Height);
        var point = data.Point(DensityNorm.Log, opacity: 1);

        var coarse = plan.Render(View(data, point, perUnit: 3, centerX: 128, centerY: 128));
        var (dense, sparse) = Parts(plan);
        Assert.Equal((1, 0), (dense.TileLevel, sparse.TileLevel));
        Assert.Equal(
            [
                new DensityPiece(0, 1, 0, 1, 0),
                new DensityPiece(1, 1, 0, 0, 0),
                new DensityPiece(2, 1, 0, 0, 0),
                new DensityPiece(3, 1, 3, 1, 0),
            ],
            dense.Pieces.ToArray().OrderBy(p => p.CellCode)
        );
        Assert.Equal([new DensityPiece(0, 0, 0, -1, 1 + (1.0 / 32_768))], sparse.Pieces.ToArray());
        Assert.Equal(4, coarse.Images.Length);
        Assert.Contains(coarse.Rects.ToArray(), r => Near(r, 256, 192, 192, 192));

        var fine = plan.Render(View(data, point, perUnit: 24, centerX: 128, centerY: 120));
        (dense, sparse) = Parts(plan);
        Assert.Equal((4, 2), (dense.TileLevel, sparse.TileLevel));
        var pieces = dense.Pieces.ToArray();
        var (ownQuadrant, root) = (0, 0);
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
                root++;
            }
        }

        Assert.True(ownQuadrant > 0 && root > 0);
        Assert.Contains(pieces, p => Corner(dense, p.CellCode, p.CellLevel) == (112, 112));
        Assert.Contains(pieces, p => Corner(dense, p.CellCode, p.CellLevel) == (128, 112));
        Assert.Equal(pieces.Length, fine.Images.Length);
        var fill = Assert.Single(sparse.Pieces.ToArray());
        Assert.Equal((2, -1, 1.0 / 16), (fill.CellLevel, fill.TileLevel, fill.Count));
        Assert.Equal((128.0, 112.0), Corner(sparse, fill.CellCode, fill.CellLevel));
        Assert.Contains(fine.Rects.ToArray(), r => Near(r, 256, 0, 384, 384));
    }

    /// <summary>
    /// The same density of points gives the same color whether a bin of the chosen level's tile, of an ancestor's tile k
    /// levels up, or a fill draws it, for each norm (design §5.5.2 "広さで正規化する").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The lattice, 16 points a square unit wherever it has points. Density over the Viridis map at opacity 1 with the
    ///   norm of the case; a plan of 512 × 384.
    /// - The shader's t of a texel value v, as Image.hlsl writes it: s(v) of the transform (none, symlog log10(1 + v / C),
    ///   pow v^C), then (s − low) / (high − low), held to [0, 1].
    /// - The design's t of a count c under a top m: log(1 + c) / log(1 + m), c / m, or ∛c / ∛m.
    ///
    /// Steps:
    /// - Render 3 pixels a unit around (128, 128): the dense part draws level 1, whose bins of 1/4 square unit hold 4
    ///   points, from the quadrants' own tiles (bins of 4) and from the root's (bins of 16, k = 1). The top is 5, a bin of
    ///   a quadrant's tile that also holds a corner point.
    /// - Render 24 pixels a unit around (128, 120): level 4, whose bins of 1/256 square unit hold 1/16 of a point, drawn
    ///   from a quadrant's tile (bins of 4, k = 3), from the root's (bins of 16, k = 4), and as the sparse part's fill of
    ///   4,096 points over 16 × 16 units. The top is 5 / 4^3.
    ///
    /// Expected:
    /// - At level 1, every image record's t of its tile's bin (4 for k = 0, 16 for k = 1) is the design's t of 4 under 5,
    ///   to within 1e-6, and both k are drawn.
    /// - At level 4, every image record's t of its tile's bin (4 for k = 3, 16 for k = 4) is the design's t of 1/16 under
    ///   5/64, to within 1e-6, both k are drawn, and the fill's rectangle has Viridis's color at that t, to within 1 of
    ///   255 a channel.
    /// </remarks>
    [Theory]
    [InlineData(DensityNorm.Log)]
    [InlineData(DensityNorm.Linear)]
    [InlineData(DensityNorm.Cbrt)]
    public void SameDensity_FromTheChosenTileAnAncestorOrAFill_HasTheSameColor(DensityNorm norm)
    {
        using var data = Lattice();
        using var pool = new PlotWorkerPool(4);
        using var plan = TestPlans.Create(pool, Width, Height);
        var point = data.Point(norm, opacity: 1);

        var coarse = plan.Render(View(data, point, perUnit: 3, centerX: 128, centerY: 128)).Images.ToArray();
        var expected = Design(norm, 4, 5);
        Assert.All(coarse, image => Assert.Equal(expected, ShaderT(image, ShallowerOf(image) == 0 ? 4 : 16), 6));
        Assert.Equal([0, 1], coarse.Select(ShallowerOf).Distinct().Order());

        var fine = plan.Render(View(data, point, perUnit: 24, centerX: 128, centerY: 120));
        expected = Design(norm, 1.0 / 16, 5.0 / 64);
        var images = fine.Images.ToArray();
        Assert.All(images, image => Assert.Equal(expected, ShaderT(image, ShallowerOf(image) == 3 ? 4 : 16), 6));
        Assert.Equal([3, 4], images.Select(ShallowerOf).Distinct().Order());
        var color = ColorMaps.Viridis.Map(expected);
        var rect = Assert.Single(fine.Rects.ToArray(), r => Near(r, 256, 0, 384, 384));
        Assert.InRange((rect.Color.X * 255) - color.R, -1, 1);
        Assert.InRange((rect.Color.Y * 255) - color.G, -1, 1);
        Assert.InRange((rect.Color.Z * 255) - color.B, -1, 1);
    }

    /// <summary>
    /// The top of the scale is the largest normalized bin of the tiles and fills drawn this frame, and the parts of a split
    /// layer share it (design §5.5.2 "濃さの上端 m").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The lattice: the root tile's largest bin holds 17 points (16 and a corner point), each quadrant tile's 5 (4 and a
    ///   corner point); the sparse part's fills hold 4,096 points a 16 × 16 cell. Density with Linear over Viridis.
    ///
    /// Steps:
    /// - Render 3 pixels a unit around (128, 128) (level 1: the quadrants' tiles at k = 0, the root's at k = 1).
    /// - Render 24 pixels a unit around (128, 120) (level 4: a quadrant's tile at k = 3, the root's at k = 4, a fill).
    /// - Render 24 pixels a unit around (160, 100), where no quadrant tile is drawn (the root's at k = 4, and fills).
    ///
    /// Expected:
    /// - Tops of max(5, 17/4) = 5; max(5/64, 17/256, 1/16) = 5/64; and max(17/256, 1/16) = 17/256, exactly.
    /// - Both parts read the same switch. At level 4 the sparse part's fill of 1/16, whose own largest is 1/16, has the
    ///   color of t = (1/16) / (5/64) = 0.8, not 1: it is drawn under the layer's top.
    /// </remarks>
    [Fact]
    public void Top_IsTheLargestNormalizedBinDrawn_AndTheParts_ShareIt()
    {
        using var data = Lattice();
        using var pool = new PlotWorkerPool(4);
        using var plan = TestPlans.Create(pool, Width, Height);
        var point = data.Point(DensityNorm.Linear, opacity: 1);

        _ = plan.Render(View(data, point, perUnit: 3, centerX: 128, centerY: 128));
        var (dense, sparse) = Parts(plan);
        Assert.Same(dense.Switch, sparse.Switch);
        Assert.Equal(5.0, dense.Switch.Top);

        var fine = plan.Render(View(data, point, perUnit: 24, centerX: 128, centerY: 120));
        Assert.Equal(5.0 / 64, dense.Switch.Top);
        var color = ColorMaps.Viridis.Map(0.8);
        var rect = Assert.Single(fine.Rects.ToArray(), r => Near(r, 256, 0, 384, 384));
        Assert.InRange((rect.Color.X * 255) - color.R, -0.5, 0.5);
        Assert.InRange((rect.Color.Y * 255) - color.G, -0.5, 0.5);
        Assert.InRange((rect.Color.Z * 255) - color.B, -0.5, 0.5);

        _ = plan.Render(View(data, point, perUnit: 24, centerX: 160, centerY: 100));
        Assert.All(dense.Pieces.ToArray(), p => Assert.Equal(0, p.TileLevel));
        Assert.NotEmpty(sparse.Pieces.ToArray());
        Assert.Equal(17.0 / 256, dense.Switch.Top);
    }

    /// <summary>
    /// A density's image record holds the cell's face, the part of its own or its ancestor's tile it reads, the paint of
    /// its norm under the top, the color table, the opacity, and the zero-is-transparent flag (design §5.5.2 "描き方",
    /// "祖先のタイル", "広さで正規化する").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The lattice; Density over Viridis with the norm of the case at opacity 0.75; a plan of 512 × 384 (y up, so a
    ///   texel row runs toward lower pixels).
    ///
    /// Steps:
    /// - Render 3 pixels a unit around (128, 128) (top 5): read the record whose corner is the quadrant [0, 128)² (its own
    ///   tile, k = 0).
    /// - Render 24 pixels a unit around (128, 120) (top 5/64): read the records of the cells [112, 128)² (the quadrant's
    ///   tile, k = 3) and [128, 144) × [112, 128) (the root's tile, k = 4).
    ///
    /// Expected, for each record:
    /// - Origin at the cell's lower-left corner in pixels: (−128, 576), (−128, 384), and (256, 384); a texel of 1.5 pixels
    ///   along x and −1.5 along y; Range (0, 0, 256, 256).
    /// - SampleOrigin (0, 0), (224, 224), and (128, 112) in face texels; SampleScale 1, 1/8, and 1/16.
    /// - Flags: the transform of the norm (Linear 0, Log 2 for symlog, Cbrt 3 for pow) in bits 2 and 3, and bit 6 for
    ///   zero is transparent, nothing else. Constant, Low, and High as the design gives them for a top m and k: Linear 0,
    ///   0, m · 4^k; Log 4^k, 0, log10(1 + m); Cbrt 1/3, 0, ∛m · 4^(k/3).
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
        using var plan = TestPlans.Create(pool, Width, Height);
        var point = data.Point(norm, opacity: 0.75);

        var coarse = plan.Render(View(data, point, perUnit: 3, centerX: 128, centerY: 128)).Images.ToArray();
        Check(At(coarse, -128, 576), shallower: 0, top: 5, sampleOrigin: Vector2.Zero);

        var fine = plan.Render(View(data, point, perUnit: 24, centerX: 128, centerY: 120)).Images.ToArray();
        Check(At(fine, -128, 384), shallower: 3, top: 5.0 / 64, sampleOrigin: new Vector2(224, 224));
        Check(At(fine, 256, 384), shallower: 4, top: 5.0 / 64, sampleOrigin: new Vector2(128, 112));

        static ImageInstance At(ImageInstance[] images, float x, float y) =>
            Assert.Single(images, i => Math.Abs(i.Origin.X - x) < 1e-3 && Math.Abs(i.Origin.Y - y) < 1e-3);

        void Check(in ImageInstance image, int shallower, double top, Vector2 sampleOrigin)
        {
            var area = Math.Pow(4, shallower);
            var (kind, constant, high) = norm switch
            {
                DensityNorm.Linear => (0u, 0.0, top * area),
                DensityNorm.Log => (2u, area, Math.Log10(1 + top)),
                _ => (3u, 1.0 / 3, Math.Cbrt(top) * Math.Pow(area, 1.0 / 3)),
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

    /// <summary>Returns the design's t of a count under a top (design §5.5.2 "描き方").</summary>
    private static double Design(DensityNorm norm, double count, double top) =>
        norm switch
        {
            DensityNorm.Linear => count / top,
            DensityNorm.Cbrt => Math.Cbrt(count) / Math.Cbrt(top),
            _ => Math.Log(1 + count) / Math.Log(1 + top),
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

    /// <summary>Returns the lattice's two parts, told apart by their points.</summary>
    private static (IndexedPointPlan Dense, IndexedPointPlan Sparse) Parts(PlotPlan plan)
    {
        var parts = ((SplitPlan)plan.Layers[0]).Parts.Cast<IndexedPointPlan>().ToArray();
        Assert.Equal(2, parts.Length);
        return (parts.Single(p => PointsOf(p) == DenseCount), parts.Single(p => PointsOf(p) == SparseCount));
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
    /// Makes the lattice: part "d", 1,024 × 1,024 points at ((i + 0.5) / 4, (j + 0.5) / 4) and two at (0, 0) and (256, 256);
    /// part "s", 256 × 256 points at (128 + (i + 0.5) / 4, 64 + (j + 0.5) / 4) and two at (128, 64) and (192, 128).
    /// </summary>
    private static LatticeData Lattice()
    {
        var x = new double[DenseCount + SparseCount];
        var y = new double[DenseCount + SparseCount];
        var k = new string?[DenseCount + SparseCount];
        var n = 0;
        for (var j = 0; j < 1024; j++)
        {
            for (var i = 0; i < 1024; i++)
            {
                (x[n], y[n], k[n]) = ((i + 0.5) / 4, (j + 0.5) / 4, "d");
                n++;
            }
        }

        (x[n], y[n], k[n]) = (0, 0, "d");
        (x[n + 1], y[n + 1], k[n + 1]) = (256, 256, "d");
        n += 2;
        for (var j = 0; j < 256; j++)
        {
            for (var i = 0; i < 256; i++)
            {
                (x[n], y[n], k[n]) = (128 + ((i + 0.5) / 4), 64 + ((j + 0.5) / 4), "s");
                n++;
            }
        }

        (x[n], y[n], k[n]) = (128, 64, "s");
        (x[n + 1], y[n + 1], k[n + 1]) = (192, 128, "s");
        Assert.Equal(x.Length, n + 2);

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
