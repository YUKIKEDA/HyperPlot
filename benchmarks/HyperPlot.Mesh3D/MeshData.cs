using System.Numerics;
using System.Runtime.InteropServices;

namespace HyperPlot.Mesh3D;

/// <summary>
/// A vertex as the GPU reads it, 20 bytes: the position in the mesh's coordinates, the unit normal in the box's coordinates
/// in octahedral encoding (x in the low 16 bits, y in the high, each an SNORM16), and an RGBA8 color (R in the low byte).
/// </summary>
/// <remarks>
/// 20 bytes, not 28 with a float3 normal: 5e6 vertices of 28 bytes are 140 MB, over the 128 MB that D3D11 guarantees a
/// resource (D3D11_REQ_RESOURCE_SIZE_IN_MEGABYTES_EXPRESSION_A_TERM; a device with little dedicated memory, WARP, may stop
/// there). The product's vertex format is not decided here.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct MeshVertex
{
    public Vector3 Position;
    public uint Normal;
    public uint Color;
}

/// <summary>An indexed triangle list: three indices a triangle, counter-clockwise seen from the side its normal points to.</summary>
internal sealed class Mesh(MeshVertex[] vertices, uint[] indices)
{
    public MeshVertex[] Vertices { get; } = vertices;

    public uint[] Indices { get; } = indices;

    public int Triangles => Indices.Length / 3;
}

/// <summary>The seeded meshes of the scene (SPIKE, row K0d, Issue #490).</summary>
internal static class MeshData
{
    // A ramp of five colors (viridis's ends and middle), RGB, for t in [0, 1].
    private static readonly Vector3[] _ramp =
    [
        new(0.267f, 0.005f, 0.329f),
        new(0.231f, 0.322f, 0.545f),
        new(0.129f, 0.569f, 0.549f),
        new(0.369f, 0.788f, 0.384f),
        new(0.993f, 0.906f, 0.144f),
    ];

    /// <summary>
    /// Makes the mesh of a kind with about the given number of triangles from a seed, in [-1, 1]³ of the mesh's
    /// coordinates. Normals are made as design §13.5 makes them for an input without normals (area-weighted vertex normals),
    /// then turned into the box's coordinates by the inverse transpose of the per-axis scale and normalized on the CPU
    /// (Issue #483, decision 3).
    /// </summary>
    /// <remarks>
    /// "blob": a closed torus (major radius 0.6, minor 0.25) whose minor radius is displaced by a seeded sum of 8 waves that
    /// wrap around both angles, so that the surface is lumpy and seen through in up to 4 layers or more. Colored by a
    /// ramp of the waves and the minor angle, so that the inside and the outside of the ring differ.
    /// "surface": an open height field z = f(x, y) on a square grid, f a seeded sum of 8 waves (amplitude about 0.5) plus a
    /// uniform noise of ±0.003 a vertex (the grid's step is about 0.0009), colored by height. Seen from 30° above the horizon,
    /// its ridges hide the slopes behind them, and the noise puts many folds in a pixel.
    /// The triangles are in the grid's order, row by row, as an input mesh would come.
    /// </remarks>
    public static Mesh Make(string kind, int triangles, Vector3 scale, int seed) =>
        kind switch
        {
            "blob" => Blob(triangles, scale, seed),
            "surface" => Surface(triangles, scale, seed),
            _ => throw new ArgumentException($"Unknown mesh {kind}: blob or surface."),
        };

    private static Mesh Blob(int triangles, Vector3 scale, int seed)
    {
        // U × V vertices on the torus, 2·U·V triangles, U = 2V (the major circle is the longer).
        var v = Math.Max(3, (int)Math.Round(Math.Sqrt(triangles / 4.0)));
        var u = 2 * v;
        var random = new Random(seed);
        var waves = new (int M, int N, double Amplitude, double Phase)[8];
        for (var k = 0; k < waves.Length; k++)
        {
            waves[k] = (random.Next(1, 9), random.Next(1, 6), 0.012 + (0.03 * random.NextDouble()), 2 * Math.PI * random.NextDouble());
        }

        var vertices = new MeshVertex[u * v];
        for (var i = 0; i < u; i++)
        {
            var a = 2 * Math.PI * i / u;
            for (var j = 0; j < v; j++)
            {
                var b = 2 * Math.PI * j / v;
                var wave = 0.0;
                foreach (var (m, n, amplitude, phase) in waves)
                {
                    wave += amplitude * Math.Sin((m * a) + (n * b) + phase);
                }

                var minor = 0.25 + wave;
                var ring = 0.6 + (minor * Math.Cos(b));
                var position = new Vector3((float)(ring * Math.Cos(a)), (float)(ring * Math.Sin(a)), (float)(minor * Math.Sin(b)));
                var t = 0.5 + (0.5 * Math.Sin(3 * a) * Math.Cos(b)) + (4 * wave);
                vertices[(i * v) + j] = new MeshVertex { Position = position, Color = Ramp(t) };
            }
        }

        // Each quad (i, j) to (i + 1, j + 1), wrapping both ways, as two triangles counter-clockwise seen from outside.
        var indices = new uint[u * v * 6];
        var at = 0;
        for (var i = 0; i < u; i++)
        {
            var i1 = (i + 1) % u;
            for (var j = 0; j < v; j++)
            {
                var j1 = (j + 1) % v;
                var (p00, p10, p01, p11) = ((uint)((i * v) + j), (uint)((i1 * v) + j), (uint)((i * v) + j1), (uint)((i1 * v) + j1));
                (indices[at], indices[at + 1], indices[at + 2]) = (p00, p10, p11);
                (indices[at + 3], indices[at + 4], indices[at + 5]) = (p00, p11, p01);
                at += 6;
            }
        }

        return Finish(vertices, indices, scale);
    }

    private static Mesh Surface(int triangles, Vector3 scale, int seed)
    {
        // n × n vertices, 2·(n - 1)² triangles.
        var n = Math.Max(2, (int)Math.Round(Math.Sqrt(triangles / 2.0)) + 1);
        var random = new Random(seed);
        var waves = new (double Kx, double Ky, double Amplitude, double Phase)[8];
        for (var k = 0; k < waves.Length; k++)
        {
            var frequency = 1 + (7 * random.NextDouble());
            var direction = 2 * Math.PI * random.NextDouble();
            waves[k] = (
                frequency * Math.Cos(direction),
                frequency * Math.Sin(direction),
                0.04 + (0.1 * random.NextDouble()),
                2 * Math.PI * random.NextDouble()
            );
        }

        var vertices = new MeshVertex[n * n];
        for (var row = 0; row < n; row++)
        {
            var y = -1 + (2.0 * row / (n - 1));
            for (var column = 0; column < n; column++)
            {
                var x = -1 + (2.0 * column / (n - 1));
                var z = 0.0;
                foreach (var (kx, ky, amplitude, phase) in waves)
                {
                    z += amplitude * Math.Sin((kx * x) + (ky * y) + phase);
                }

                z = Math.Clamp(z + (0.003 * ((2 * random.NextDouble()) - 1)), -1, 1);
                vertices[(row * n) + column] = new MeshVertex { Position = new Vector3((float)x, (float)y, (float)z), Color = Ramp(0.5 + (0.8 * z)) };
            }
        }

        // Each cell as two triangles counter-clockwise seen from above (+z).
        var indices = new uint[(n - 1) * (n - 1) * 6];
        var at = 0;
        for (var row = 0; row < n - 1; row++)
        {
            for (var column = 0; column < n - 1; column++)
            {
                var p00 = (uint)((row * n) + column);
                var (p10, p01, p11) = (p00 + 1, p00 + (uint)n, p00 + (uint)n + 1);
                (indices[at], indices[at + 1], indices[at + 2]) = (p00, p10, p11);
                (indices[at + 3], indices[at + 4], indices[at + 5]) = (p00, p11, p01);
                at += 6;
            }
        }

        return Finish(vertices, indices, scale);
    }

    // Area-weighted vertex normals ((b - a) × (c - a) is twice the area along the normal), then the inverse transpose of the
    // scale diag(s), which is diag(1 / s), and normalization. A vertex with no area gets +z.
    private static Mesh Finish(MeshVertex[] vertices, uint[] indices, Vector3 scale)
    {
        var sums = new Vector3[vertices.Length];
        for (var t = 0; t < indices.Length; t += 3)
        {
            var (a, b, c) = (indices[t], indices[t + 1], indices[t + 2]);
            var normal = Vector3.Cross(vertices[b].Position - vertices[a].Position, vertices[c].Position - vertices[a].Position);
            sums[a] += normal;
            sums[b] += normal;
            sums[c] += normal;
        }

        var inverse = Vector3.One / scale;
        for (var i = 0; i < vertices.Length; i++)
        {
            var normal = sums[i] * inverse;
            var length = normal.Length();
            vertices[i].Normal = Octahedral(length > 0 ? normal / length : Vector3.UnitZ);
        }

        return new Mesh(vertices, indices);
    }

    // The octahedral encoding of a unit vector (Cigolle et al. 2014, JCGT 3(2)), each of the two components an SNORM16.
    private static uint Octahedral(Vector3 normal)
    {
        var n = normal / (MathF.Abs(normal.X) + MathF.Abs(normal.Y) + MathF.Abs(normal.Z));
        var (x, y) = (n.X, n.Y);
        if (n.Z < 0)
        {
            (x, y) = ((1 - MathF.Abs(n.Y)) * (n.X >= 0 ? 1 : -1), (1 - MathF.Abs(n.X)) * (n.Y >= 0 ? 1 : -1));
        }

        var low = (ushort)(short)MathF.Round(Math.Clamp(x, -1, 1) * 32767);
        var high = (ushort)(short)MathF.Round(Math.Clamp(y, -1, 1) * 32767);
        return low | ((uint)high << 16);
    }

    private static uint Ramp(double t)
    {
        var at = Math.Clamp(t, 0, 1) * (_ramp.Length - 1);
        var low = Math.Min((int)at, _ramp.Length - 2);
        var color = Vector3.Lerp(_ramp[low], _ramp[low + 1], (float)(at - low));
        var (r, g, b) = ((uint)MathF.Round(color.X * 255), (uint)MathF.Round(color.Y * 255), (uint)MathF.Round(color.Z * 255));
        return r | (g << 8) | (b << 16) | (0xFFu << 24);
    }
}
