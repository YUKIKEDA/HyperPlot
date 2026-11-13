using System.Numerics;

namespace HyperPlot.Volume;

/// <summary>
/// A transfer function: 256 entries of straight RGB and an opacity per 1/512 of the data's extent (so 512³ and 1024³ look the
/// same), read linearly at x = code / 65,534. Both functions leave x below 0.3 transparent, which empty-space skipping uses.
/// </summary>
internal sealed class TransferFunction
{
    public const int Entries = 256;

    /// <summary>The length of u the opacity is given for is 1 / ReferenceSteps.</summary>
    public const float ReferenceSteps = 512;

    private const float Floor = 0.3f;

    private readonly Vector4[] _table = new Vector4[Entries];

    private TransferFunction(string name, Func<float, float> opacity)
    {
        Name = name;
        for (var i = 0; i < Entries; i++)
        {
            var x = i / (float)(Entries - 1);
            _table[i] = new Vector4(Color(x), x < Floor ? 0 : opacity(x));
        }
    }

    public string Name { get; }

    public ReadOnlySpan<Vector4> Table => _table;

    /// <summary>Faint everywhere above the floor: rays cross the whole volume and early termination rarely stops them.</summary>
    public static TransferFunction Translucent() => new("translucent", x => 0.001f + (0.004f * (x - Floor) / (1 - Floor)));

    /// <summary>Close to opaque above the floor: rays stop soon after the first surface they meet.</summary>
    public static TransferFunction Opaque() => new("opaque", x => Math.Min(0.9f, 0.4f + (5 * (x - Floor))));

    public static TransferFunction Named(string name) =>
        name switch
        {
            "translucent" => Translucent(),
            "opaque" => Opaque(),
            _ => throw new ArgumentException($"Unknown transfer function {name}: translucent or opaque."),
        };

    /// <summary>
    /// Whether every value in [min, max] maps to opacity 0, reading the entries a linear lookup between them can blend. A
    /// range with min above max (a brick of NaN only) is empty.
    /// </summary>
    public bool IsEmpty(ushort min, ushort max)
    {
        if (min > max)
        {
            return true;
        }

        var first = (int)MathF.Floor(min / (float)NativeVolume.MaxCode * (Entries - 1));
        var last = Math.Min(Entries - 1, (int)MathF.Ceiling(max / (float)NativeVolume.MaxCode * (Entries - 1)));
        for (var i = first; i <= last; i++)
        {
            if (_table[i].W > 0)
            {
                return false;
            }
        }

        return true;
    }

    // Blue, cyan, yellow, red along x.
    private static Vector3 Color(float x) =>
        x < 0.5f
            ? Vector3.Lerp(new Vector3(0.1f, 0.2f, 0.9f), new Vector3(0.1f, 0.9f, 0.9f), x * 2)
            : Vector3.Lerp(new Vector3(0.95f, 0.9f, 0.1f), new Vector3(0.9f, 0.1f, 0.1f), (x - 0.5f) * 2);
}
