using System.Numerics;
using System.Runtime.InteropServices;

namespace HyperPlot.Depth3D;

/// <summary>A vertex as the GPU reads it: the position in the box's coordinates and an RGBA8 color, 16 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Vertex3
{
    public Vector3 Position;
    public uint Color;
}

/// <summary>One polyline of a scene: its run of segments, its color, and the box of its vertices.</summary>
internal struct Polyline
{
    public int SegmentStart;
    public int SegmentCount;
    public uint Color;
    public Vector3 Min;
    public Vector3 Max;
}

/// <summary>
/// The seeded shapes of a scene (SPIKE, row K1, Issue #509): polylines, each a run of segments between consecutive
/// vertices, or markers, one vertex each.
/// </summary>
internal sealed class SceneData
{
    // matplotlib's tab20, RGBA8 with R in the low byte: one color a polyline.
    private static readonly uint[] _lineColors =
    [
        0xFFB4771F,
        0xFFE8C7AE,
        0xFF0E7FFF,
        0xFF78BBFF,
        0xFF2CA02C,
        0xFF8ADF98,
        0xFF2827D6,
        0xFF9698FF,
        0xFFBD6794,
        0xFFD5B0C5,
        0xFF4B568C,
        0xFF949CC4,
        0xFFC277E3,
        0xFFD2B6F7,
        0xFF7F7F7F,
        0xFFC7C7C7,
        0xFF22BDBC,
        0xFF8DDBDB,
        0xFFCFBE17,
        0xFFE5DA9E,
    ];

    // matplotlib's tab10: neighbors of different index differ in color, so that the order two markers are drawn in shows.
    private static readonly uint[] _markerColors =
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

    private SceneData(string name, Vertex3[] vertices, uint[] segments, Polyline[] polylines)
    {
        (Name, Vertices, Segments, Polylines) = (name, vertices, segments, polylines);
    }

    public string Name { get; }

    /// <summary>The polylines' vertices one polyline after another, or the markers' centers.</summary>
    public Vertex3[] Vertices { get; }

    /// <summary>The first vertex of each segment, polyline after polyline; empty for markers.</summary>
    public uint[] Segments { get; }

    public Polyline[] Polylines { get; }

    /// <summary>
    /// Polylines of segments in all from a seed.
    /// knot: Lissajous knots x = 0.9 sin(37 s + a), y = 0.9 sin(41 s + b), z = 0.9 sin(43 s + c), s over one period, closed,
    /// with phases a, b, c drawn for each polyline (8 by default): the strands cross and pass behind each other everywhere,
    /// and each knot passes behind itself. A segment is about half a pixel at 1,920 × 1,080 (about 70,000 pixels of curve
    /// a knot, reckoned from its length).
    /// walk: random walks (16 by default) from points drawn in [-0.5, 0.5]³, normal steps of 0.003 on each axis (about
    /// 1.3 pixels a step), reflected at the box's faces.
    /// </summary>
    public static SceneData Lines(string name, int segments, int polylines, int seed)
    {
        return name switch
        {
            "knot" => Knot(segments, polylines > 0 ? polylines : 8, seed),
            "walk" => Walk(segments, polylines > 0 ? polylines : 16, seed),
            _ => throw new ArgumentException($"Unknown line scene {name}: knot or walk."),
        };
    }

    /// <summary>
    /// Markers from a seed, each colored by a hash of its index. uniform: count points in [-1, 1]³. knot and walk: the
    /// vertices of that line scene of count segments (count plus the number of polylines markers), dense along strands.
    /// </summary>
    public static SceneData Markers(string name, int count, int seed)
    {
        Vertex3[] vertices;
        if (name == "uniform")
        {
            var random = new Random(seed);
            vertices = new Vertex3[count];
            for (var i = 0; i < count; i++)
            {
                vertices[i].Position = new Vector3(Uniform(random), Uniform(random), Uniform(random));
            }
        }
        else
        {
            vertices = Lines(name, count, 0, seed).Vertices;
        }

        for (var i = 0; i < vertices.Length; i++)
        {
            vertices[i].Color = _markerColors[(int)((((uint)i * 2654435761u) >> 16) % (uint)_markerColors.Length)];
        }

        return new SceneData(name, vertices, [], []);
    }

    private static SceneData Knot(int segments, int polylines, int seed)
    {
        var random = new Random(seed);
        var vertices = new Vertex3[segments + polylines];
        var starts = new uint[segments];
        var lines = new Polyline[polylines];
        var (v, s) = (0, 0);
        for (var k = 0; k < polylines; k++)
        {
            var count = (segments / polylines) + (k < segments % polylines ? 1 : 0);
            var (a, b, c) = (Angle(random), Angle(random), Angle(random));
            var color = _lineColors[k % _lineColors.Length];
            var (min, max) = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
            for (var i = 0; i <= count; i++)
            {
                var along = 2 * Math.PI * i / count;
                var position = new Vector3(
                    (float)(0.9 * Math.Sin((37 * along) + a)),
                    (float)(0.9 * Math.Sin((41 * along) + b)),
                    (float)(0.9 * Math.Sin((43 * along) + c))
                );
                vertices[v + i] = new Vertex3 { Position = position, Color = color };
                (min, max) = (Vector3.Min(min, position), Vector3.Max(max, position));
                if (i < count)
                {
                    starts[s + i] = (uint)(v + i);
                }
            }

            lines[k] = new Polyline
            {
                SegmentStart = s,
                SegmentCount = count,
                Color = color,
                Min = min,
                Max = max,
            };
            v += count + 1;
            s += count;
        }

        return new SceneData("knot", vertices, starts, lines);
    }

    private static SceneData Walk(int segments, int polylines, int seed)
    {
        const double Step = 0.003;
        var random = new Random(seed);
        var vertices = new Vertex3[segments + polylines];
        var starts = new uint[segments];
        var lines = new Polyline[polylines];
        var (v, s) = (0, 0);
        for (var k = 0; k < polylines; k++)
        {
            var count = (segments / polylines) + (k < segments % polylines ? 1 : 0);
            var color = _lineColors[k % _lineColors.Length];
            var (x, y, z) = (random.NextDouble() - 0.5, random.NextDouble() - 0.5, random.NextDouble() - 0.5);
            var (min, max) = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
            for (var i = 0; i <= count; i++)
            {
                if (i > 0)
                {
                    x = Reflect(x + (Step * Normal(random)));
                    y = Reflect(y + (Step * Normal(random)));
                    z = Reflect(z + (Step * Normal(random)));
                }

                var position = new Vector3((float)x, (float)y, (float)z);
                vertices[v + i] = new Vertex3 { Position = position, Color = color };
                (min, max) = (Vector3.Min(min, position), Vector3.Max(max, position));
                if (i < count)
                {
                    starts[s + i] = (uint)(v + i);
                }
            }

            lines[k] = new Polyline
            {
                SegmentStart = s,
                SegmentCount = count,
                Color = color,
                Min = min,
                Max = max,
            };
            v += count + 1;
            s += count;
        }

        return new SceneData("walk", vertices, starts, lines);
    }

    private static double Reflect(double value) =>
        value > 1 ? 2 - value
        : value < -1 ? -2 - value
        : value;

    private static double Angle(Random random) => 2 * Math.PI * random.NextDouble();

    private static float Uniform(Random random) => (float)((random.NextDouble() * 2) - 1);

    private static double Normal(Random random) => Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
}
