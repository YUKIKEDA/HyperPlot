using System.Diagnostics;
using System.Numerics;

namespace HyperPlot.SurfaceLod;

/// <summary>The heights of a scene.</summary>
internal enum Shape
{
    /// <summary>A few low waves: the levels' errors fall fast.</summary>
    Smooth,

    /// <summary>The waves and white noise of a set amplitude at every sample: the errors stay near the amplitude.</summary>
    Noisy,

    /// <summary>The waves and isolated single-sample spikes: the tiles that hold a spike need level 0.</summary>
    Spikes,
}

/// <summary>
/// SPIKE (row K0c, Issue #489): a grid of 10,241 × 10,241 heights (10,240 × 10,240 cells, the 1e8 cells of requirement
/// K-4 rounded up to 5 × 2,048, so that every tile size divides it), its coordinates, the levels of every-other-sample
/// decimation (Issue #482, decision 2), and the quadtree of tiles: per tile and level, the greatest |z| difference between
/// an original sample and the level's triangles above it, in z's scale space (linear here: the data's units), and the
/// tile's z bounds. The roots are a forest of 5 × 5 tiles of 2,048 cells; a tile at level L spans tile·2^L cells with
/// (tile + 1)² vertices, every 2^L-th sample.
/// </summary>
internal sealed class SurfaceData
{
    public const int RootsPerAxis = 5;
    public const int RootCells = 2048;
    public const int Cells = RootsPerAxis * RootCells;
    public const int Samples = Cells + 1;

    public SurfaceData(Shape shape, bool uniform, int tile, float noise, int spikes, int seed)
    {
        if (tile is not (64 or 128 or 256))
        {
            throw new ArgumentException($"Tile {tile}: 64, 128, or 256 cells.");
        }

        Tile = tile;
        Uniform = uniform;
        TopLevel = BitOperations.Log2((uint)(RootCells / tile));
        var started = Stopwatch.GetTimestamp();
        X = Coordinates(uniform, seed, 0x51);
        Y = Coordinates(uniform, seed, 0xA7);
        var heights = new float[Samples * Samples];
        Fill(heights, shape, noise, spikes, seed);
        (ZMin, ZMax) = MinMax(heights);
        GenerateSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;

        started = Stopwatch.GetTimestamp();
        Levels = new float[TopLevel + 1][];
        Levels[0] = heights;
        for (var k = 1; k <= TopLevel; k++)
        {
            Levels[k] = Subsample(heights, k);
        }

        LevelSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;

        LevelSide = new int[TopLevel + 1];
        LevelOffset = new int[TopLevel + 1];
        for (var level = 0; level <= TopLevel; level++)
        {
            LevelSide[level] = Cells / (tile << level);
            LevelOffset[level] = NodeCount;
            NodeCount += LevelSide[level] * LevelSide[level];
        }

        NodeLevel = new byte[NodeCount];
        NodeX = new short[NodeCount];
        NodeY = new short[NodeCount];
        for (var level = 0; level <= TopLevel; level++)
        {
            var side = LevelSide[level];
            for (var n = 0; n < side * side; n++)
            {
                var id = LevelOffset[level] + n;
                NodeLevel[id] = (byte)level;
                NodeX[id] = (short)(n % side);
                NodeY[id] = (short)(n / side);
            }
        }

        started = Stopwatch.GetTimestamp();
        Error = new float[NodeCount];
        NodeZMin = new float[NodeCount];
        NodeZMax = new float[NodeCount];
        Bounds();
        Errors();
        ErrorSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
    }

    /// <summary>The cells along a side of a tile.</summary>
    public int Tile { get; }

    public bool Uniform { get; }

    /// <summary>The level of the roots: log2(2,048 / tile).</summary>
    public int TopLevel { get; }

    /// <summary>The x of each sample column in the box's coordinates ([-1, 1]), increasing.</summary>
    public float[] X { get; }

    /// <summary>The y of each sample row in the box's coordinates ([-1, 1]), increasing.</summary>
    public float[] Y { get; }

    public float ZMin { get; }

    public float ZMax { get; }

    /// <summary>Level k's heights, (Cells / 2^k + 1)² of them, row-major; level 0 is the original grid.</summary>
    public float[][] Levels { get; }

    /// <summary>The tiles along an axis at each level.</summary>
    public int[] LevelSide { get; }

    /// <summary>The id of each level's first tile; a tile's id is the offset plus y times the side plus x.</summary>
    public int[] LevelOffset { get; }

    public int NodeCount { get; }

    public byte[] NodeLevel { get; }

    public short[] NodeX { get; }

    public short[] NodeY { get; }

    /// <summary>
    /// The greatest |z| difference of a tile's original samples from its level's triangles, in z's units, made monotone:
    /// a tile's error is at least each child's (Ulrich 2002's condition for a screen error that falls as one descends).
    /// </summary>
    public float[] Error { get; }

    public float[] NodeZMin { get; }

    public float[] NodeZMax { get; }

    public double GenerateSeconds { get; }

    public double LevelSeconds { get; }

    public double ErrorSeconds { get; }

    /// <summary>Level k's samples along an axis.</summary>
    public static int LevelWidth(int k) => (Cells >> k) + 1;

    public int Node(int level, int x, int y) => LevelOffset[level] + (y * LevelSide[level]) + x;

    // SplitMix64 of the pair and the seed, in [0, 1).
    private static float Hash01(int i, int j, int seed)
    {
        var z = ((ulong)(uint)i | ((ulong)(uint)j << 32)) ^ ((ulong)(uint)seed * 0x9E3779B97F4A7C15UL);
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (z >> 40) * (1f / (1 << 24));
    }

    // About [-1, 1]: waves of 1.25 and 0.75 periods across the box, and a slant.
    private static float Smooth(float x, float y) =>
        (0.5f * MathF.Sin(2.5f * MathF.PI * x) * MathF.Cos(1.5f * MathF.PI * y)) + (0.3f * MathF.Sin((4 * x) + (3 * y))) + (0.2f * x * y);

    // Uniform: -1 + 2i / Cells. Non-uniform: steps that vary smoothly over 3 periods (e^±1.5) times a seeded factor in
    // [0.5, 1.5), so that neighboring steps differ up to 3 times and the steps of the whole axis about 60 times.
    private static float[] Coordinates(bool uniform, int seed, int salt)
    {
        var values = new float[Samples];
        if (uniform)
        {
            for (var i = 0; i < Samples; i++)
            {
                values[i] = (float)(-1 + (2.0 * i / Cells));
            }

            return values;
        }

        var sums = new double[Samples];
        for (var i = 0; i < Cells; i++)
        {
            var step = Math.Exp(1.5 * Math.Sin((2 * Math.PI * 3 * i / Cells) + salt)) * (0.5 + Hash01(i, salt, seed));
            sums[i + 1] = sums[i] + step;
        }

        for (var i = 0; i < Samples; i++)
        {
            values[i] = (float)(-1 + (2 * sums[i] / sums[Cells]));
        }

        values[Cells] = 1;
        return values;
    }

    private void Fill(float[] heights, Shape shape, float noise, int spikes, int seed)
    {
        var (xs, ys) = (X, Y);
        _ = Parallel.For(
            0,
            Samples,
            j =>
            {
                var row = heights.AsSpan(j * Samples, Samples);
                var y = ys[j];
                for (var i = 0; i < Samples; i++)
                {
                    var z = Smooth(xs[i], y);
                    if (shape == Shape.Noisy)
                    {
                        z += noise * ((2 * Hash01(i, j, seed)) - 1);
                    }

                    row[i] = z;
                }
            }
        );

        if (shape == Shape.Spikes)
        {
            // Single samples raised by 1 (half the box's height), away from each other: seeded, so the same each run.
            var random = new Random(seed);
            for (var s = 0; s < spikes; s++)
            {
                heights[(random.Next(1, Cells) * Samples) + random.Next(1, Cells)] += 1;
            }
        }
    }

    private static (float Min, float Max) MinMax(float[] heights)
    {
        var gate = new Lock();
        var (min, max) = (float.MaxValue, float.MinValue);
        _ = Parallel.For(
            0,
            Samples,
            () => (Min: float.MaxValue, Max: float.MinValue),
            (j, _, local) =>
            {
                foreach (var z in heights.AsSpan(j * Samples, Samples))
                {
                    local = (Math.Min(local.Min, z), Math.Max(local.Max, z));
                }

                return local;
            },
            local =>
            {
                lock (gate)
                {
                    (min, max) = (Math.Min(min, local.Min), Math.Max(max, local.Max));
                }
            }
        );
        return (min, max);
    }

    private static float[] Subsample(float[] heights, int k)
    {
        var width = LevelWidth(k);
        var level = new float[width * width];
        _ = Parallel.For(
            0,
            width,
            r =>
            {
                var source = heights.AsSpan((r << k) * Samples, Samples);
                var target = level.AsSpan(r * width, width);
                for (var c = 0; c < width; c++)
                {
                    target[c] = source[c << k];
                }
            }
        );
        return level;
    }

    // The z bounds of each level-0 tile from its samples, then of each parent from its children.
    private void Bounds()
    {
        var heights = Levels[0];
        var tile = Tile;
        var side = LevelSide[0];
        _ = Parallel.For(
            0,
            side * side,
            n =>
            {
                var (x0, y0) = ((n % side) * tile, (n / side) * tile);
                var (min, max) = (float.MaxValue, float.MinValue);
                for (var gy = y0; gy <= y0 + tile; gy++)
                {
                    foreach (var z in heights.AsSpan((gy * Samples) + x0, tile + 1))
                    {
                        (min, max) = (Math.Min(min, z), Math.Max(max, z));
                    }
                }

                NodeZMin[n] = min;
                NodeZMax[n] = max;
            }
        );

        for (var level = 1; level <= TopLevel; level++)
        {
            var levelSide = LevelSide[level];
            for (var n = 0; n < levelSide * levelSide; n++)
            {
                var id = LevelOffset[level] + n;
                var (min, max) = (float.MaxValue, float.MinValue);
                for (var c = 0; c < 4; c++)
                {
                    var child = Node(level - 1, (2 * (n % levelSide)) + (c & 1), (2 * (n / levelSide)) + (c >> 1));
                    (min, max) = (Math.Min(min, NodeZMin[child]), Math.Max(max, NodeZMax[child]));
                }

                NodeZMin[id] = min;
                NodeZMax[id] = max;
            }
        }
    }

    // Each tile's error against the original samples (level 0's is 0), then each parent raised to its children's.
    private void Errors()
    {
        for (var level = 1; level <= TopLevel; level++)
        {
            var side = LevelSide[level];
            var offset = LevelOffset[level];
            var step = 1 << level;
            var span = Tile << level;
            _ = Parallel.For(0, side * side, n => Error[offset + n] = TileError((n % side) * span, (n / side) * span, span, step));
        }

        for (var level = 1; level <= TopLevel; level++)
        {
            var side = LevelSide[level];
            for (var n = 0; n < side * side; n++)
            {
                var id = LevelOffset[level] + n;
                for (var c = 0; c < 4; c++)
                {
                    Error[id] = Math.Max(Error[id], Error[Node(level - 1, (2 * (n % side)) + (c & 1), (2 * (n / side)) + (c >> 1))]);
                }
            }
        }
    }

    // The coarse cell of a sample is the one it falls in (the last for the far edge); its triangles are (00, 10, 01) where
    // u + v ≤ 1 and (11, 01, 10) beyond, as the index buffers cut a cell (Stitch). u and v are taken in the coordinates,
    // so that a non-uniform grid is measured on the triangles the GPU draws.
    private float TileError(int x0, int y0, int span, int step)
    {
        var (h, xs, ys, tile) = (Levels[0], X, Y, Tile);
        var worst = 0f;
        for (var gy = y0; gy <= y0 + span; gy++)
        {
            var ya = y0 + (Math.Min((gy - y0) / step, tile - 1) * step);
            var yb = ya + step;
            var v = (ys[gy] - ys[ya]) / (ys[yb] - ys[ya]);
            var (rowA, rowB, row) = (ya * Samples, yb * Samples, gy * Samples);
            for (var gx = x0; gx <= x0 + span; gx++)
            {
                var xa = x0 + (Math.Min((gx - x0) / step, tile - 1) * step);
                var xb = xa + step;
                var u = (xs[gx] - xs[xa]) / (xs[xb] - xs[xa]);
                var (z00, z10, z01, z11) = (h[rowA + xa], h[rowA + xb], h[rowB + xa], h[rowB + xb]);
                var surface = u + v <= 1 ? z00 + (u * (z10 - z00)) + (v * (z01 - z00)) : z11 + ((1 - u) * (z01 - z11)) + ((1 - v) * (z10 - z11));
                worst = Math.Max(worst, MathF.Abs(h[row + gx] - surface));
            }
        }

        return worst;
    }
}
