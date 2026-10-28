using System.Numerics;

namespace HyperPlot.Benchmarks;

/// <summary>The seeded inputs of the kernel benchmarks (.cursor/rules/bench.mdc): the same seed gives the same values.</summary>
public static class KernelData
{
    /// <summary>The seed of every benchmark input.</summary>
    public const ulong Seed = 0x48_79_70_65_72_50_6C_6FUL;

    /// <summary>
    /// Returns a random walk of <paramref name="length"/> values. With <paramref name="sparseNaN"/>, one value in 64 on
    /// average is NaN, so that most 64-element buckets hold a NaN and the kernels take their slow path.
    /// </summary>
    public static T[] Walk<T>(long length, bool sparseNaN)
        where T : unmanaged, INumber<T>
    {
        var values = new T[length];
        var state = Seed;
        var y = 0.0;
        for (long i = 0; i < length; i++)
        {
            var bits = Next(ref state);
            y += ((bits >> 11) * (1.0 / (1UL << 53))) - 0.5;
            values[i] = sparseNaN && (bits & 63) == 0 ? T.CreateTruncating(double.NaN) : T.CreateTruncating(y);
        }

        return values;
    }

    /// <summary>xorshift64*: fixed here, so that the inputs do not change with the runtime's <see cref="Random"/>.</summary>
    public static ulong Next(ref ulong state)
    {
        state ^= state >> 12;
        state ^= state << 25;
        state ^= state >> 27;
        return state * 0x2545F4914F6CDD1DUL;
    }
}
