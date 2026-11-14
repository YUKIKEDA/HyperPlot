using System.Numerics;
using System.Runtime.InteropServices;

namespace HyperPlot.Points3D;

/// <summary>A point as the GPU reads it: the position in the box's coordinates and an RGBA8 color, 16 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Point3
{
    public Vector3 Position;
    public uint Color;
}

/// <summary>Three columns of positions in the box's coordinates, one value a row.</summary>
internal sealed class PointColumns
{
    public required float[] X { get; init; }

    public required float[] Y { get; init; }

    public required float[] Z { get; init; }
}

/// <summary>The seeded points of the scene (SPIKE, row K0a).</summary>
internal static class PointData
{
    private const int ColumnBlock = 1 << 20;

    // matplotlib's tab10, RGBA8 with R in the low byte: neighbors of different index differ in color, so that a tie broken
    // differently by A and B shows in the pixels.
    private static readonly uint[] _palette =
    [
        0xFFB4771F,
        0xFF0E7FFF,
        0xFF2CA02C,
        0xFF2827D6,
        0xFFBD6794,
        0xFF4B568C,
        0xFFC277E3,
        0xFF7F7F7F,
        0xFF22BDBC,
        0xFFCFBE17,
    ];

    /// <summary>
    /// Makes count points from a seed: uniform in the box [-1, 1]³, or a normal blob of a tenth of the side (σ = 0.2 on each
    /// axis) around the middle, cut to the box. The order is the generator's, a random order, as the shuffled input of
    /// decision 7 gives within a node; "cells" sorts them by the Morton code of a 16³ grid, a stand-in for the octree's
    /// order of nodes with each node's points in random order.
    /// </summary>
    public static Point3[] Make(int count, bool cluster, bool cells, int seed)
    {
        var random = new Random(seed);
        var points = new Point3[count];
        for (var i = 0; i < count; i++)
        {
            var position = cluster
                ? Vector3.Clamp(new Vector3(Normal(random), Normal(random), Normal(random)) * 0.2f, -Vector3.One, Vector3.One)
                : new Vector3(Uniform(random), Uniform(random), Uniform(random));

            // The color comes from a hash of the index, so that the same point keeps its color in every order.
            points[i] = new Point3 { Position = position, Color = ColorOf(i) };
        }

        if (cells)
        {
            var keys = new int[count];
            for (var i = 0; i < count; i++)
            {
                keys[i] = Morton(points[i].Position);
            }

            Array.Sort(keys, points);
        }

        return points;
    }

    /// <summary>The color of a row: a hash of its index into the palette.</summary>
    public static uint ColorOf(int row) => _palette[(int)(((uint)row * 2654435761u) >> 16) % _palette.Length];

    /// <summary>
    /// Makes count points from a seed as three columns, the octree's source (SPIKE, row K0a, hypotheses 2 to 4): uniform in
    /// the box [-1, 1]³, or the normal blob of <see cref="Make"/>. Blocks of 2^20 rows each take a generator seeded from the
    /// seed and the block, so the points do not depend on the workers; they are not the points <see cref="Make"/> makes.
    /// </summary>
    public static PointColumns MakeColumns(int count, bool cluster, int seed, int workers)
    {
        var x = new float[count];
        var y = new float[count];
        var z = new float[count];
        var blocks = (int)(((long)count + ColumnBlock - 1) / ColumnBlock);
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = workers };
        Parallel.For(0, blocks, parallel, block => FillBlock(block, cluster, seed, x, y, z));
        return new PointColumns { X = x, Y = y, Z = z };
    }

    private static void FillBlock(int block, bool cluster, int seed, float[] x, float[] y, float[] z)
    {
        var random = new Random(unchecked((seed * 1_000_003) + block));
        var from = block * ColumnBlock;
        var to = Math.Min(x.Length, from + ColumnBlock);
        for (var i = from; i < to; i++)
        {
            if (cluster)
            {
                x[i] = Math.Clamp(Normal(random) * 0.2f, -1f, 1f);
                y[i] = Math.Clamp(Normal(random) * 0.2f, -1f, 1f);
                z[i] = Math.Clamp(Normal(random) * 0.2f, -1f, 1f);
            }
            else
            {
                x[i] = Uniform(random);
                y[i] = Uniform(random);
                z[i] = Uniform(random);
            }
        }
    }

    private static float Uniform(Random random) => (float)((random.NextDouble() * 2) - 1);

    private static float Normal(Random random) =>
        (float)(Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble()));

    private static int Morton(Vector3 position)
    {
        var cell = Vector3.Clamp((position + Vector3.One) * 8, Vector3.Zero, new Vector3(15));
        var (x, y, z) = ((int)cell.X, (int)cell.Y, (int)cell.Z);
        var key = 0;
        for (var bit = 0; bit < 4; bit++)
        {
            key |= (((x >> bit) & 1) << (3 * bit)) | (((y >> bit) & 1) << ((3 * bit) + 1)) | (((z >> bit) & 1) << ((3 * bit) + 2));
        }

        return key;
    }
}
