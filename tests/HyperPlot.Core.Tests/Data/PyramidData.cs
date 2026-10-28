using System.Numerics;

namespace HyperPlot.Tests;

/// <summary>Seeded series for the pyramid and M4 tests: values, NaN patterns, and X columns of every kind.</summary>
internal static class PyramidData
{
    /// <summary>Two chunks and a short third, so that ranges cross chunk seams and the last chunk's layout is short.</summary>
    public const int Length = (2 * Column.ChunkLength) + 70_001;

    public static readonly string[] Patterns = ["None", "Sparse", "Buckets", "Chunk", "All"];

    /// <summary>Returns values in [-1000, 1000] with plateaus (ties), and NaN in the named pattern.</summary>
    public static double[] Values(int length, string pattern, int seed, bool integral)
    {
        var random = new Random(seed);
        var values = new double[length];
        for (var i = 0; i < length; i++)
        {
            var v = (random.NextDouble() * 2000) - 1000;
            values[i] = integral ? Math.Round(v) : v;
        }

        // Plateaus of one value, so that the first occurrence of a minimum or a maximum matters.
        for (var p = 0; p < length / 50_000; p++)
        {
            var at = random.Next(length - 300);
            var value = random.Next(2) == 0 ? -1000 : 1000;
            for (var i = at; i < at + 300; i += 3)
            {
                values[i] = value;
            }
        }

        switch (pattern)
        {
            case "Sparse":
                for (var i = 0; i < length; i++)
                {
                    if (random.Next(100) == 0)
                    {
                        values[i] = double.NaN;
                    }
                }

                break;
            case "Buckets":
                // Whole 64-element buckets, whole 4096-element buckets, and partly NaN buckets.
                for (var b = 0; b < length / 64; b++)
                {
                    var roll = random.Next(10);
                    if (roll == 0)
                    {
                        values.AsSpan(b * 64, 64).Fill(double.NaN);
                    }
                    else if (roll == 1)
                    {
                        values[(b * 64) + random.Next(64)] = double.NaN;
                    }
                }

                for (var b = 0; b < length / 4096; b += 7)
                {
                    values.AsSpan(b * 4096, 4096).Fill(double.NaN);
                }

                break;
            case "Chunk":
                values.AsSpan(Column.ChunkLength, Math.Min(Column.ChunkLength, length - Column.ChunkLength)).Fill(double.NaN);
                for (var i = 0; i < length; i += 997)
                {
                    values[i] = double.NaN;
                }

                break;
            case "All":
                values.AsSpan().Fill(double.NaN);
                break;
        }

        return values;
    }

    public static T[] Convert<T>(double[] values)
        where T : unmanaged, INumber<T>
    {
        var result = new T[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            result[i] = T.CreateTruncating(values[i]);
        }

        return result;
    }

    /// <summary>Returns the values a column of <typeparamref name="T"/> holds, read back as double (bytes wrap to 0..255).</summary>
    public static double[] AsStored<T>(T[] values)
        where T : unmanaged, INumber<T>
    {
        var result = new double[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            result[i] = double.CreateTruncating(values[i]);
        }

        return result;
    }

    /// <summary>Non-decreasing X values with repeats (steps of 0), as the column of <paramref name="kind"/> stores them.</summary>
    public static (Column Column, double[] Values) X(string kind, int length, int seed)
    {
        var random = new Random(seed);
        switch (kind)
        {
            case "Uniform":
            {
                var uniform = Column.Uniform(1000.0, 0.1, length);
                var values = new double[length];
                for (var i = 0; i < length; i++)
                {
                    values[i] = uniform.GetValue(i);
                }

                return (uniform, values);
            }

            case "Double":
            {
                var stored = new double[length];
                var x = -5e3;
                for (var i = 0; i < length; i++)
                {
                    stored[i] = x;
                    x += random.Next(20) == 0 ? 0 : random.NextDouble() * 0.3;
                }

                return (Column.Wrap(stored), stored);
            }

            case "Long":
            {
                var stored = new long[length];
                var x = 1_000_000_000_000L;
                for (var i = 0; i < length; i++)
                {
                    stored[i] = x;
                    x += random.Next(6);
                }

                return (Column.Wrap(stored), AsStored(stored));
            }

            default:
            {
                var stored = new float[length];
                var x = -100f;
                for (var i = 0; i < length; i++)
                {
                    stored[i] = x;
                    x += random.Next(20) == 0 ? 0 : (float)(random.NextDouble() * 0.01);
                }

                return (Column.Wrap(stored), AsStored(stored));
            }
        }
    }

    /// <summary>Strictly increasing boundaries for a view: evenly spaced, or at element values, sometimes past both ends.</summary>
    public static double[]? Boundaries(double[] x, Random random)
    {
        int[] widths = [1, 2, 7, 64, 333, 1920];
        var w = widths[random.Next(widths.Length)];
        var span = x[^1] - x[0];
        if (random.Next(3) == 0)
        {
            var lo = x[random.Next(x.Length)] - (random.Next(4) == 0 ? span : 0);
            var hi = x[random.Next(x.Length)] + (random.Next(4) == 0 ? span : 0);
            if (!(lo < hi))
            {
                (lo, hi) = (x[0] - 1, x[^1] + 1);
            }

            var evenly = new double[w + 1];
            for (var k = 0; k <= w; k++)
            {
                evenly[k] = lo + ((hi - lo) * k / w);
            }

            return Increasing(evenly) ? evenly : null;
        }

        // At element values exactly, or one step of the double above them, where a division can land one element short.
        var above = random.Next(2) == 0;
        var picked = Enumerable
            .Range(0, w + 1)
            .Select(_ => x[random.Next(x.Length)])
            .Select(v => above ? Math.BitIncrement(v) : v)
            .Distinct()
            .Order()
            .ToArray();
        return picked.Length >= 2 ? picked : null;
    }

    private static bool Increasing(double[] values)
    {
        for (var i = 1; i < values.Length; i++)
        {
            if (!(values[i - 1] < values[i]))
            {
                return false;
            }
        }

        return true;
    }
}
