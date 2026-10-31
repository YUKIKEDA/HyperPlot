using System.Numerics;
using System.Runtime.InteropServices;

namespace HyperPlot;

/// <summary>
/// The mipmap of a raster, cut into tiles of 256 × 256 (design §3.7, row G9). Level 0 is the raster itself and is not
/// copied; level ℓ ≥ 1 is the 2 × 2 average of level ℓ − 1 (a side ⌈half⌉ long), held in native memory in the raster's own
/// type; the top level is the first whose sides are both at most 256. The workers build it; a change of the column's chunks
/// rebuilds the rows of those chunks and the rows above them only. Each band of 256 rows of a level has a stamp that moves
/// when the band is written, which the residency cache keys its tiles by.
/// </summary>
internal abstract class RasterPyramid : IDisposable
{
    /// <summary>The side of a tile, in texels.</summary>
    public const int TileSide = 256;

    /// <summary>The rows of a level that one worker's job averages.</summary>
    private const int Band = 64;

    private readonly long[] _seenGenerations;
    private readonly long[][] _stamps;
    private long _nextStamp = 1;

    private protected RasterPyramid(Raster raster)
    {
        Raster = raster;
        var sizes = new List<(int Width, int Height)> { (raster.Width, raster.Height) };
        while (sizes[^1].Width > TileSide || sizes[^1].Height > TileSide)
        {
            var (w, h) = sizes[^1];
            sizes.Add(((w + 1) / 2, (h + 1) / 2));
        }

        Sizes = [.. sizes];
        _seenGenerations = new long[raster.Values.ChunkCount];
        Array.Fill(_seenGenerations, -1);
        _stamps = new long[Sizes.Length][];
        for (var level = 0; level < Sizes.Length; level++)
        {
            _stamps[level] = new long[TilesOf(Sizes[level].Height)];
        }
    }

    /// <summary>Gets the raster.</summary>
    public Raster Raster { get; }

    /// <summary>Gets each level's width and height, level 0 first.</summary>
    public (int Width, int Height)[] Sizes { get; }

    /// <summary>Gets the number of levels.</summary>
    public int LevelCount => Sizes.Length;

    /// <summary>Gets the number of tiles written by the last <see cref="Update"/> over all levels. Tests only.</summary>
    public int TilesWritten { get; private set; }

    /// <summary>Returns the number of tiles along a side of a number of texels.</summary>
    public static int TilesOf(int texels) => (texels + TileSide - 1) / TileSide;

    /// <summary>Returns the stamp of a tile's band of rows: it moves when the band is written again.</summary>
    public long Stamp(int level, int tileRow) => _stamps[level][tileRow];

    /// <summary>
    /// Brings the levels to the column's current chunks: on the first call every level, then the rows of the chunks whose
    /// generation moved and the rows above them. Runs the averages on the workers and waits for them.
    /// </summary>
    public void Update(PlotWorkerPool pool)
    {
        TilesWritten = 0;
        var column = Raster.Values;
        var rowElements = (long)Raster.Width * Raster.Channels;
        var (low, high) = (int.MaxValue, -1);
        for (var chunk = 0; chunk < _seenGenerations.Length; chunk++)
        {
            var generation = GenerationOf(chunk);
            if (generation == _seenGenerations[chunk])
            {
                continue;
            }

            _seenGenerations[chunk] = generation;
            var start = (long)chunk << Column.ChunkShift;
            var end = Math.Min(column.Length, start + Column.ChunkLength);
            low = Math.Min(low, (int)(start / rowElements));
            high = Math.Max(high, (int)((end - 1) / rowElements));
        }

        if (high < 0)
        {
            return;
        }

        // Level 0 is the raster: only its stamps move. Each level above is averaged over the rows under the changed ones.
        Touch(0, low, high);
        for (var level = 1; level < Sizes.Length; level++)
        {
            (low, high) = (low / 2, high / 2);
            var (from, to) = (low, high);
            var target = level;
            var bands = ((to - from) / Band) + 1;
            pool.RunChunks(bands, band => Average(target, from + (band * Band), Math.Min(to, from + (band * Band) + Band - 1)))
                .GetAwaiter()
                .GetResult();
            Touch(level, low, high);
        }
    }

    /// <summary>
    /// Writes a tile of a level into a face of 258 × 258 floats: the tile's 256 × 256 texels, values less an origin, and
    /// a border of a texel copied from the neighbors (the raster's edge texels at its edges). Texels past the level's
    /// width or height repeat its last column or row. Numbers only.
    /// </summary>
    public abstract void CopyTile(int level, int tileColumn, int tileRow, double origin, Span<float> face);

    /// <summary>Writes a tile of an RGBA image into a face of 258 × 258 premultiplied colors (R, G, B, A in the low byte first).</summary>
    public abstract void CopyTile(int level, int tileColumn, int tileRow, Span<uint> face);

    public abstract void Dispose();

    /// <summary>Returns the column's generation of a chunk, which moves with each Invalidate over it.</summary>
    private protected abstract long GenerationOf(int chunk);

    /// <summary>Averages rows <paramref name="first"/> to <paramref name="last"/> of a level from the level under it.</summary>
    private protected abstract void Average(int level, int first, int last);

    private void Touch(int level, int low, int high)
    {
        var stamps = _stamps[level];
        for (var tileRow = low / TileSide; tileRow <= Math.Min(high / TileSide, stamps.Length - 1); tileRow++)
        {
            stamps[tileRow] = _nextStamp++;
            TilesWritten += TilesOf(Sizes[level].Width);
        }
    }
}
