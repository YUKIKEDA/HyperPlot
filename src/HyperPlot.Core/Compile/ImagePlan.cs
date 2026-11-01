using System.Numerics;
using HyperPlot.Rendering;

namespace HyperPlot;

/// <summary>
/// An image (design §5.5.3, row G9): the visible tiles of the level of its raster's mipmap that puts one texel at most two
/// a pixel. The top level's one tile is asked for first, then the drawn level's visible tiles from the middle out; a tile
/// not there yet is drawn from the nearest ancestor that is, whose part it reads. The levels between are not asked for. The frame's work is the visible tiles, not the raster's size.
/// </summary>
internal sealed class ImagePlan : LayerPlan
{
    private readonly RasterPyramid _mipmap;
    private Tile[] _tiles = new Tile[64];

    public ImagePlan(ImageLayerSpec spec, PlanBuild build)
        : base(spec)
    {
        _mipmap = build.Mipmap(spec.Raster);
        SetColumns(spec.Raster.Values);
    }

    /// <summary>Gets the mipmap. Tests only.</summary>
    public RasterPyramid Mipmap => _mipmap;

    /// <summary>Gets the level the last recording drew. Tests only.</summary>
    public int Level { get; private set; }

    /// <summary>Gets the number of visible tiles of the last recording. Tests only.</summary>
    public int VisibleTiles { get; private set; }

    /// <summary>Gets a value indicating whether the last recording drew a tile from an ancestor's part: a provisional image (invariant 4).</summary>
    public bool Provisional { get; private set; }

    public override void AddRanges(RangeSink sink, bool ofX, DataTable data)
    {
        var extent = ((ImageLayerSpec)Spec).ExtentOrDefault;
        var (a, b) = ofX ? (extent.Left, extent.Right) : (extent.Bottom, extent.Top);
        sink.Add(Math.Min(a, b), Math.Max(a, b), "an image's extent");
    }

    public override void Record(RecordContext context)
    {
        var image = (ImageLayerSpec)Spec;
        var raster = image.Raster;
        var extent = image.ExtentOrDefault;
        var (w, h) = (raster.Width, raster.Height);
        (VisibleTiles, Provisional) = (0, false);

        // The target point of base pixel corner (c, r) is P0 + c · Ec + r · Er: the axes are linear (design §5.5.3).
        double ColumnX(double c) => extent.Left + (c * (extent.Right - extent.Left) / w);
        double RowY(double r) =>
            image.Origin == RasterOrigin.Upper
                ? extent.Top + (r * (extent.Bottom - extent.Top) / h)
                : extent.Bottom + (r * (extent.Top - extent.Bottom) / h);
        var transform = context.Transform;
        var (alongAt0, alongAt1) = (transform.MapX(ColumnX(0) - context.XOrigin), transform.MapX(ColumnX(1) - context.XOrigin));
        var (acrossAt0, acrossAt1) = (transform.MapY(RowY(0) - context.YOrigin), transform.MapY(RowY(1) - context.YOrigin));
        var (ax, ay) = (alongAt1 - alongAt0, acrossAt1 - acrossAt0);
        if (!double.IsFinite(ax) || !double.IsFinite(ay) || ax == 0 || ay == 0)
        {
            return;
        }

        var flipped = context.Flipped;
        var p0 = flipped ? new Vector2D(acrossAt0, alongAt0) : new Vector2D(alongAt0, acrossAt0);
        var ec = flipped ? new Vector2D(0, ax) : new Vector2D(ax, 0);
        var er = flipped ? new Vector2D(ay, 0) : new Vector2D(0, ay);

        // The level: a texel of the finer direction at most 2 a pixel (design §5.5.3).
        var top = _mipmap.LevelCount - 1;
        var finest = Math.Max(1 / Math.Abs(ax), 1 / Math.Abs(ay));
        var level = finest < 2 ? 0 : Math.Min(top, (int)Math.Floor(Math.Log2(finest)));
        Level = level;
        var k = 1 << level;

        // The visible base columns and rows: the panel's rectangle through the inverse of the affine map.
        var (left, upper, width, height) = context.Panel;
        var (alongLow, alongHigh) = flipped ? (upper, upper + height) : (left, left + width);
        var (acrossLow, acrossHigh) = flipped ? (left, left + width) : (upper, upper + height);
        var (c0, c1) = Ordered((alongLow - alongAt0) / ax, (alongHigh - alongAt0) / ax);
        var (r0, r1) = Ordered((acrossLow - acrossAt0) / ay, (acrossHigh - acrossAt0) / ay);
        (c0, c1, r0, r1) = (Math.Max(c0, 0), Math.Min(c1, w), Math.Max(r0, 0), Math.Min(r1, h));
        if (c0 >= c1 || r0 >= r1)
        {
            return;
        }

        var span = (double)RasterPyramid.TileSide * k;
        var (levelWidth, levelHeight) = _mipmap.Sizes[level];
        var (tx0, tx1) = ((int)(c0 / span), Math.Min(RasterPyramid.TilesOf(levelWidth) - 1, (int)Math.Ceiling(c1 / span) - 1));
        var (ty0, ty1) = ((int)(r0 / span), Math.Min(RasterPyramid.TilesOf(levelHeight) - 1, (int)Math.Ceiling(r1 / span) - 1));
        var count = (tx1 - tx0 + 1) * (ty1 - ty0 + 1);
        if (count <= 0)
        {
            return;
        }

        if (_tiles.Length < count)
        {
            Array.Resize(ref _tiles, Math.Max(count, _tiles.Length * 2));
        }

        // Nearest the middle of the visible part first, so that the budget of a frame goes where the eye is.
        var (middleColumn, middleRow) = ((c0 + c1) / 2 / span, (r0 + r1) / 2 / span);
        var n = 0;
        for (var ty = ty0; ty <= ty1; ty++)
        {
            for (var tx = tx0; tx <= tx1; tx++)
            {
                var distance = ((tx + 0.5 - middleColumn) * (tx + 0.5 - middleColumn)) + ((ty + 0.5 - middleRow) * (ty + 0.5 - middleRow));
                _tiles[n++] = new Tile(tx, ty, distance);
            }
        }

        var tiles = _tiles.AsSpan(0, n);
        tiles.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
        VisibleTiles = n;

        var paint = Paint(image, context);
        var cache = context.Images;

        // The top level first: it is one tile, and every visible tile can fall back to it.
        _ = Slot(cache, top, 0, 0, paint.Origin, raster.IsRgba);
        foreach (ref readonly var tile in tiles)
        {
            // The tile, or the nearest ancestor that is there, read over the tile's part.
            var (from, slot, tileColumn, tileRow) = (level, -1, tile.Column, tile.Row);
            for (; from <= top; from++, tileColumn /= 2, tileRow /= 2)
            {
                slot = Slot(cache, from, tileColumn, tileRow, paint.Origin, raster.IsRgba);
                if (slot >= 0 || from == top)
                {
                    break;
                }
            }

            if (slot < 0)
            {
                continue;
            }

            Provisional |= from > level;
            var up = 1 << (from - level);
            var (u0, v0) = (tile.Column * RasterPyramid.TileSide, tile.Row * RasterPyramid.TileSide);
            var record = paint.Instance;
            var corner = p0 + (ec * ((double)u0 * k)) + (er * ((double)v0 * k));
            record.Origin = corner.Single;
            record.AlongU = (ec * k).Single;
            record.AlongV = (er * k).Single;

            // The tile's texels, the last ones cut at the image's edge: a lone pixel of an odd side is drawn its own width.
            record.Range = new Vector4(
                0,
                0,
                (float)Math.Min(RasterPyramid.TileSide, ((double)w / k) - u0),
                (float)Math.Min(RasterPyramid.TileSide, ((double)h / k) - v0)
            );
            record.SampleOrigin = new Vector2(
                (float)(((double)u0 / up) - (tileColumn * RasterPyramid.TileSide)),
                (float)(((double)v0 / up) - (tileRow * RasterPyramid.TileSide))
            );
            record.SampleScale = 1f / up;
            record.Slice = (uint)(slot % ResidencyCache.PageSlots);
            context.List.DrawImage(record, PageOf(raster.IsRgba, slot));
        }
    }

    /// <summary>Returns the page id a slot's tile is read from: the format and the page of the slot.</summary>
    internal static int PageOf(bool rgba, int slot) => ((rgba ? 1 : 0) * 1024) + (slot / ResidencyCache.PageSlots);

    private static (double Low, double High) Ordered(double a, double b) => a <= b ? (a, b) : (b, a);

    /// <summary>
    /// Returns how the tiles are painted (design §4.2, §5.3): for numbers, the transform, the color domain, the middle, the
    /// reverse, and the table, with the origin the tiles are sent less of: the domain's for a linear scale, 0 with a
    /// transform (design §3.6). For an RGBA image, premultiplied colors as they are.
    /// </summary>
    private (ImageInstance Instance, double Origin) Paint(ImageLayerSpec image, RecordContext context)
    {
        var instance = new ImageInstance
        {
            Opacity = (float)image.Opacity,
            Flags = (image.Interpolation == RasterInterpolation.Bilinear ? 1u : 0) | (image.Raster.IsRgba ? 2u : 0),
        };
        if (image.Raster.IsRgba || Colors is not { Inner: { } inner, Map: { } map } colors)
        {
            return (instance, 0);
        }

        var (kind, constant) = inner switch
        {
            LogScaleSpec => (1u, 0.0),
            SymlogScaleSpec symlog => (2u, symlog.Constant),
            PowScaleSpec pow => (3u, pow.Exponent),
            _ => (0u, 0.0),
        };
        var domain = colors.Domain;
        var origin = kind == 0 ? domain.Origin : 0;
        var shift = kind == 0 ? 0 : domain.Origin;
        instance.Flags |= (kind << 2) | (colors.Diverging ? 16u : 0) | (inner.Reverse ? 32u : 0);
        instance.Constant = (float)constant;
        instance.Low = (float)(domain.Lower + shift);
        instance.High = (float)(domain.Upper + shift);
        instance.Middle = (float)(colors.Middle - origin);
        var table = context.Images.Table(map);
        if (table < 0)
        {
            // Another map in a full table would paint with the wrong colors (PR #201's review).
            throw new InvalidOperationException(
                $"Layer {Index} paints a raster with a color map past the {ResidencyCache.MaxColorTables} the backend holds (design §5.2)."
            );
        }

        instance.Table = (uint)table;
        return (instance, origin);
    }

    private int Slot(ResidencyCache cache, int level, int column, int row, double origin, bool rgba) =>
        cache.Request(
            new ResidencyCache.TileKey(
                _mipmap,
                rgba ? ResidencyCache.Format.Rgba : ResidencyCache.Format.Float,
                level,
                column,
                row,
                _mipmap.Stamp(level, row),
                origin
            ),
            _mipmap
        );

    /// <summary>A visible tile: its column and row at the drawn level, and its squared distance from the middle in tiles.</summary>
    private readonly record struct Tile(int Column, int Row, double Distance);

    /// <summary>A point or a vector of the target in double, cut to single only in the record.</summary>
    private readonly record struct Vector2D(double X, double Y)
    {
        public Vector2 Single => new((float)X, (float)Y);

        public static Vector2D operator +(Vector2D a, Vector2D b) => new(a.X + b.X, a.Y + b.Y);

        public static Vector2D operator *(Vector2D a, double s) => new(a.X * s, a.Y * s);
    }
}
