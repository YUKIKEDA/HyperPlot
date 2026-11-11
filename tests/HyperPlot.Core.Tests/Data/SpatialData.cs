namespace HyperPlot.Tests;

/// <summary>Seeded points for the tests of the spatial tile index (design §10).</summary>
internal static class SpatialData
{
    /// <summary>One chunk and a short second, and not a multiple of the tests' unit, so that units and groups cross both seams.</summary>
    public const int Length = Column.ChunkLength + 4_321;

    /// <summary>Returns points spread evenly over [-50, 50) × [0, 1000).</summary>
    public static (float[] X, float[] Y) Uniform(int length, int seed)
    {
        var random = new Random(seed);
        var x = new float[length];
        var y = new float[length];
        for (var i = 0; i < length; i++)
        {
            x[i] = (float)((random.NextDouble() * 100) - 50);
            y[i] = (float)(random.NextDouble() * 1000);
        }

        return (x, y);
    }

    /// <summary>
    /// Returns clusters inside clusters: each point picks one of 5 centers at each of <paramref name="depth"/> steps, and
    /// each step is 1/64 as wide as the one before, so the table goes deep where the points are.
    /// </summary>
    public static (double[] X, double[] Y) Nested(int length, int seed, int depth = 5)
    {
        var random = new Random(seed);
        var x = new double[length];
        var y = new double[length];
        for (var i = 0; i < length; i++)
        {
            double px = 0;
            double py = 0;
            var width = 1.0;
            for (var d = 0; d < depth; d++)
            {
                var center = random.Next(5);
                px += width * (0.1 + (0.2 * center));
                py += width * (0.9 - (0.17 * center));
                width /= 64;
            }

            x[i] = px + (random.NextDouble() * width);
            y[i] = py + (random.NextDouble() * width);
        }

        return (x, y);
    }
}
