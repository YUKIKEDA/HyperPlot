namespace HyperPlot.SurfaceLod;

/// <summary>
/// SPIKE (row K0c, Issue #489): the index lists of a tile, one for each of the 16 shapes of its edges (Issue #482,
/// decision 4). An edge whose neighbor is one level coarser drops its odd vertices: each is moved onto the even vertex
/// before it, and a triangle that loses its area is left out. The edge then has the neighbor's vertices only, so the two
/// tiles meet without a crack, and no triangle is added (no skirt).
/// Edge bits: 1 the edge at j = 0 (−y), 2 at i = tile (+x), 4 at j = tile (+y), 8 at i = 0 (−x).
/// A cell (i, j) is cut into (00, 10, 01) and (11, 01, 10), as SurfaceData measures the error.
/// </summary>
internal static class Stitch
{
    public const int Shapes = 16;

    /// <summary>The indices of the 16 lists, one after another, and where each list starts and how long it is.</summary>
    public static uint[] Build(int tile, int[] starts, int[] counts)
    {
        var indices = new List<uint>();
        for (var mask = 0; mask < Shapes; mask++)
        {
            starts[mask] = indices.Count;
            for (var j = 0; j < tile; j++)
            {
                for (var i = 0; i < tile; i++)
                {
                    var a = Vertex(i, j, tile, mask);
                    var b = Vertex(i + 1, j, tile, mask);
                    var c = Vertex(i, j + 1, tile, mask);
                    var d = Vertex(i + 1, j + 1, tile, mask);
                    Add(indices, a, b, c);
                    Add(indices, d, c, b);
                }
            }

            counts[mask] = indices.Count - starts[mask];
        }

        return [.. indices];
    }

    /// <summary>
    /// Checks every list: each triangle has a positive area in the index plane (no fold), the areas add up to the tile's
    /// (so the triangles cover it without a gap or an overlap), and no odd vertex of a coarse edge is used. Returns null
    /// when all hold, or what failed.
    /// </summary>
    public static string? Check(int tile, uint[] indices, int[] starts, int[] counts)
    {
        var side = (uint)(tile + 1);
        for (var mask = 0; mask < Shapes; mask++)
        {
            long twiceArea = 0;
            for (var t = starts[mask]; t < starts[mask] + counts[mask]; t += 3)
            {
                var (ai, aj) = ((int)(indices[t] % side), (int)(indices[t] / side));
                var (bi, bj) = ((int)(indices[t + 1] % side), (int)(indices[t + 1] / side));
                var (ci, cj) = ((int)(indices[t + 2] % side), (int)(indices[t + 2] / side));
                var cross = ((bi - ai) * (cj - aj)) - ((bj - aj) * (ci - ai));
                if (cross <= 0)
                {
                    return $"shape {mask}: triangle {(t - starts[mask]) / 3} has the area {cross / 2.0}";
                }

                twiceArea += cross;
                for (var k = 0; k < 3; k++)
                {
                    var (i, j) = ((int)(indices[t + k] % side), (int)(indices[t + k] / side));
                    var odd =
                        ((mask & 1) != 0 && j == 0 && (i & 1) == 1)
                        || ((mask & 4) != 0 && j == tile && (i & 1) == 1)
                        || ((mask & 8) != 0 && i == 0 && (j & 1) == 1)
                        || ((mask & 2) != 0 && i == tile && (j & 1) == 1);
                    if (odd)
                    {
                        return $"shape {mask}: the odd vertex ({i}, {j}) of a coarse edge is used";
                    }
                }
            }

            if (twiceArea != 2L * tile * tile)
            {
                return $"shape {mask}: the triangles' area is {twiceArea / 2.0}, not {tile * tile}";
            }
        }

        return null;
    }

    // The vertex (i, j), moved onto the even vertex before it when it is an odd vertex of a coarse edge. A corner is even
    // on both of its edges, so no vertex is moved twice.
    private static uint Vertex(int i, int j, int tile, int mask)
    {
        if ((i & 1) == 1 && (((mask & 1) != 0 && j == 0) || ((mask & 4) != 0 && j == tile)))
        {
            i--;
        }

        if ((j & 1) == 1 && (((mask & 8) != 0 && i == 0) || ((mask & 2) != 0 && i == tile)))
        {
            j--;
        }

        return (uint)((j * (tile + 1)) + i);
    }

    private static void Add(List<uint> indices, uint a, uint b, uint c)
    {
        if (a != b && b != c && a != c)
        {
            indices.Add(a);
            indices.Add(b);
            indices.Add(c);
        }
    }
}
