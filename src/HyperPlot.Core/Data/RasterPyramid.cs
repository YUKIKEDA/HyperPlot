using System.Numerics;
using System.Runtime.ExceptionServices;
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
    private readonly long[] _readGenerations;
    private readonly long[][] _stamps;
    private readonly Lock _gate = new();
    private long _nextStamp = 1;

    // The update running (row W1b3): a chain of the levels' averages on the workers, which the frames look at without
    // waiting, and the rows it rewrites, from level 0 up.
    private Task? _running;
    private (int Low, int High) _rows;
    private long _faultVersion;
    private bool _disposed;
    private bool _freed;

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
        _readGenerations = new long[raster.Values.ChunkCount];
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

    /// <summary>Gets the number of tiles written by the last update that ended, over all levels. Tests only.</summary>
    public int TilesWritten { get; private set; }

    /// <summary>Gets a value indicating whether the levels' memory has been freed. Tests only.</summary>
    public bool Freed => Volatile.Read(ref _freed);

    /// <summary>Gets the number of updates started. Tests only.</summary>
    public int UpdatesStarted { get; private set; }

    /// <summary>Gets a value indicating whether every level has been made once: the image can be drawn (row W1b3).</summary>
    public bool Built { get; private set; }

    /// <summary>
    /// Gets a value indicating whether an update runs (row W1b3): its levels are being written over, so that none of their
    /// tiles is sent (design §5.5.3, Issue #314 Decisions 2 and 3).
    /// </summary>
    public bool Updating => _running is not null;

    /// <summary>
    /// Gets the exception of the last update that failed, or null (Issue #314 Decision 4): its chunks are made again when the
    /// column's version moves.
    /// </summary>
    public Exception? Fault { get; private set; }

    /// <summary>Returns the number of tiles along a side of a number of texels.</summary>
    public static int TilesOf(int texels) => (texels + TileSide - 1) / TileSide;

    /// <summary>Returns the stamp of a tile's band of rows: it moves when the band is written again.</summary>
    public long Stamp(int level, int tileRow) => _stamps[level][tileRow];

    /// <summary>
    /// Brings the levels toward the column's current chunks, on the plan's thread (design §3.7, row W1b3): takes an update
    /// that ended, and starts one over the chunks whose generation moved, on the workers, unless the last one failed at the
    /// column's version. A render that waits waits for the update, and throws its failure.
    /// </summary>
    public void Update(PlotWorkerPool pool, bool wait)
    {
        Take();
        if (_running is null && !(Fault is not null && _faultVersion == Raster.Values.Version))
        {
            Start(pool);
        }

        if (wait && _running is { } running)
        {
            try
            {
                running.GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // Taken below, as the frames take it.
            }

            Take();
        }

        if (wait && Fault is { } fault)
        {
            ExceptionDispatchInfo.Throw(fault);
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

    /// <summary>
    /// Frees the levels; an update that runs keeps them until it ends, and nothing waits for it (Issue #314 Decision 5).
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_running is { IsCompleted: false })
            {
                return;
            }
        }

        Free();
    }

    /// <summary>Frees the levels' native memory, once.</summary>
    private protected abstract void FreeLevels();

    /// <summary>Returns the column's generation of a chunk, which moves with each Invalidate over it.</summary>
    private protected abstract long GenerationOf(int chunk);

    /// <summary>Averages rows <paramref name="first"/> to <paramref name="last"/> of a level from the level under it.</summary>
    private protected abstract void Average(int level, int first, int last);

    /// <summary>
    /// Starts an update over the chunks whose generation moved: reads their generations now, and records them only once
    /// every level is averaged (Issue #314 Decision 4).
    /// </summary>
    private void Start(PlotWorkerPool pool)
    {
        var column = Raster.Values;
        var rowElements = (long)Raster.Width * Raster.Channels;
        var (low, high) = (int.MaxValue, -1);
        for (var chunk = 0; chunk < _seenGenerations.Length; chunk++)
        {
            var generation = GenerationOf(chunk);
            _readGenerations[chunk] = generation;
            if (generation == _seenGenerations[chunk])
            {
                continue;
            }

            var start = (long)chunk << Column.ChunkShift;
            var end = Math.Min(column.Length, start + Column.ChunkLength);
            low = Math.Min(low, (int)(start / rowElements));
            high = Math.Max(high, (int)((end - 1) / rowElements));
        }

        if (high < 0)
        {
            Built = true;
            return;
        }

        (_rows, _faultVersion, Fault) = ((low, high), column.Version, null);
        UpdatesStarted++;
        _running = Run(pool, low, high);
    }

    /// <summary>Averages each level above 0 over the rows under the changed ones, a level after the one under it, on the workers.</summary>
    private async Task Run(PlotWorkerPool pool, int low, int high)
    {
        try
        {
            for (var level = 1; level < Sizes.Length; level++)
            {
                (low, high) = (low / 2, high / 2);
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }
                }

                var (from, to, target) = (low, high, level);
                var bands = ((to - from) / Band) + 1;
                await pool.RunChunks(bands, band => Average(target, from + (band * Band), Math.Min(to, from + (band * Band) + Band - 1)))
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            bool free;
            lock (_gate)
            {
                free = _disposed;
            }

            if (free)
            {
                Free();
            }
        }
    }

    /// <summary>
    /// Takes an update that ended (row W1b3): records the generations it read and moves the stamps of the rows it wrote, or
    /// keeps its failure for the column's version it started at.
    /// </summary>
    private void Take()
    {
        if (_running is not { IsCompleted: true } done)
        {
            return;
        }

        _running = null;
        if (done.Exception is { } failed)
        {
            Fault = failed.InnerException ?? failed;
            return;
        }

        Array.Copy(_readGenerations, _seenGenerations, _seenGenerations.Length);
        TilesWritten = 0;
        var (low, high) = _rows;

        // Level 0 is the raster: only its stamps move. Each level above was averaged over the rows under the changed ones.
        Touch(0, low, high);
        for (var level = 1; level < Sizes.Length; level++)
        {
            (low, high) = (low / 2, high / 2);
            Touch(level, low, high);
        }

        Built = true;
    }

    private void Free()
    {
        FreeLevels();
        Volatile.Write(ref _freed, true);
    }

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
