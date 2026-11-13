using System.Numerics;

namespace HyperPlot.Lines3D;

/// <summary>
/// A seeded 3D line, each axis normalized to [0, 1]: the space of a linear scale over the data's range, where the errors
/// of the index are measured (SPIKE, row K0b).
/// </summary>
internal sealed class LineData
{
    private LineData(float[] x, float[] y, float[] z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public float[] X { get; }

    public float[] Y { get; }

    public float[] Z { get; }

    public int Count => X.Length;

    public Vector3 this[int i] => new(X[i], Y[i], Z[i]);

    /// <summary>
    /// Makes the line. walk: a random walk with N(0, 1) steps on each axis. helix: <paramref name="turns"/> turns of radius 1
    /// rising from z = -1 to 1, with N(0, noise²) added on each axis. Both are then normalized to [0, 1] on each axis.
    /// </summary>
    public static LineData Make(string kind, int count, ulong seed, double noise, double turns)
    {
        var x = new float[count];
        var y = new float[count];
        var z = new float[count];
        var random = new Rng(seed);
        if (kind == "walk")
        {
            double px = 0;
            double py = 0;
            double pz = 0;
            for (var i = 0; i < count; i++)
            {
                x[i] = (float)px;
                y[i] = (float)py;
                z[i] = (float)pz;
                px += random.NextNormal();
                py += random.NextNormal();
                pz += random.NextNormal();
            }
        }
        else
        {
            var step = 2 * Math.PI * turns / count;
            for (var i = 0; i < count; i++)
            {
                var angle = step * i;
                x[i] = (float)(Math.Cos(angle) + (noise * random.NextNormal()));
                y[i] = (float)(Math.Sin(angle) + (noise * random.NextNormal()));
                z[i] = (float)((2.0 * i / count) - 1 + (noise * random.NextNormal()));
            }
        }

        Normalize(x);
        Normalize(y);
        Normalize(z);
        return new LineData(x, y, z);
    }

    private static void Normalize(float[] values)
    {
        var min = double.MaxValue;
        var max = double.MinValue;
        foreach (var v in values)
        {
            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }

        var scale = max > min ? 1 / (max - min) : 0;
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = (float)((values[i] - min) * scale);
        }
    }

    /// <summary>xoshiro256** seeded through SplitMix64, with Box–Muller normals: the same line from the same seed.</summary>
    private struct Rng
    {
        private ulong _s0;
        private ulong _s1;
        private ulong _s2;
        private ulong _s3;
        private double _spare;
        private bool _hasSpare;

        public Rng(ulong seed)
        {
            var state = seed;
            _s0 = SplitMix(ref state);
            _s1 = SplitMix(ref state);
            _s2 = SplitMix(ref state);
            _s3 = SplitMix(ref state);
        }

        public double NextNormal()
        {
            if (_hasSpare)
            {
                _hasSpare = false;
                return _spare;
            }

            var radius = Math.Sqrt(-2 * Math.Log(1 - NextDouble()));
            var angle = 2 * Math.PI * NextDouble();
            _spare = radius * Math.Sin(angle);
            _hasSpare = true;
            return radius * Math.Cos(angle);
        }

        private double NextDouble() => (Next() >> 11) * (1.0 / (1UL << 53));

        private ulong Next()
        {
            var result = BitOperations.RotateLeft(_s1 * 5, 7) * 9;
            var t = _s1 << 17;
            _s2 ^= _s0;
            _s3 ^= _s1;
            _s1 ^= _s2;
            _s0 ^= _s3;
            _s2 ^= t;
            _s3 = BitOperations.RotateLeft(_s3, 45);
            return result;
        }

        private static ulong SplitMix(ref ulong state)
        {
            state += 0x9E3779B97F4A7C15UL;
            var z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
