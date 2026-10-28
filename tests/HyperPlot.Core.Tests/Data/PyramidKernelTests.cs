using System.Numerics;

namespace HyperPlot.Tests;

public sealed class PyramidKernelTests
{
    private static readonly int[] Lengths = [.. Enumerable.Range(1, 70), 127, 128, 129, 4097, 100_003];

    /// <summary>
    /// The one pass of a build (design §3.3) gives the same statistics (the sum bit for bit; a zero minimum or maximum may differ in sign) and the same first level as the
    /// statistics kernel and the first-level kernel run apart.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Lengths 1 to 70 (every tail after the vector part, with any vector width up to 512 bits), 127 to 129 (a short last
    ///   bucket), 4,097, and 100,003.
    /// - float, double, and int values in these patterns: seeded random with about one NaN in eight; random without NaN;
    ///   sorted with ties; sorted with one decrease at 1, 7, 8, 15, 16, 63, 64, the second to last, and the last position;
    ///   all NaN; NaN only first; NaN only last; zeros of both signs with infinities.
    ///
    /// Steps:
    /// - Run StatsAndLevel1, and ChunkStatsKernel.Compute with Level1, on each input.
    ///
    /// Expected:
    /// - Min, Max, First, Last, Sum, Count, NaNCount, and IsNonDecreasing are equal (Sum compared bit for bit; Min and Max by value, so that -0 equals +0); the bucket minima,
    ///   maxima, and counts are equal.
    /// </remarks>
    [Fact]
    public void StatsAndLevel1_Patterns_EqualTheTwoKernels()
    {
        foreach (var length in Lengths)
        {
            foreach (var values in Patterns(length))
            {
                Check(values);
                Check(Array.ConvertAll(values, v => (float)v));
                Check(Array.ConvertAll(values, v => double.IsFinite(v) ? (int)Math.Clamp(v, -1e9, 1e9) : 0));
            }
        }
    }

    /// <summary>
    /// The passes over a chunk allocate nothing, so that a figure the kernel benchmarks report per operation is the
    /// harness's, not the kernels' (docs/benchmarks/b1-data-layer.md).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - One chunk of floats and one of doubles with sparse NaN; bucket arrays made before; one warm-up run of each call.
    ///
    /// Steps:
    /// - Run StatsAndLevel1, Level1, and ChunkStatsKernel.Compute 10 times each on each chunk.
    ///
    /// Expected:
    /// - 0 bytes on this thread.
    /// </remarks>
    [Fact]
    public void Passes_SteadyState_AllocateNothing()
    {
        var floats = PyramidData.Convert<float>(PyramidData.Values(Column.ChunkLength, "Sparse", 3, integral: false));
        var doubles = PyramidData.Convert<double>(PyramidData.Values(Column.ChunkLength, "Sparse", 4, integral: false));
        var buckets = Column.ChunkLength >> PyramidLayout.FirstShift;
        var (floatMins, floatMaxs) = (new float[buckets], new float[buckets]);
        var (doubleMins, doubleMaxs) = (new double[buckets], new double[buckets]);
        var numbers = new byte[buckets];
        long sink = 0;
        Run();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10; i++)
        {
            Run();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.NotEqual(0, sink);

        void Run()
        {
            sink += PyramidKernel.StatsAndLevel1<float>(floats, floatMins, floatMaxs, numbers).Count;
            sink += PyramidKernel.StatsAndLevel1<double>(doubles, doubleMins, doubleMaxs, numbers).Count;
            PyramidKernel.Level1<float>(floats, floatMins, floatMaxs, numbers);
            PyramidKernel.Level1<double>(doubles, doubleMins, doubleMaxs, numbers);
            sink += ChunkStatsKernel.Compute<float>(floats).Count + ChunkStatsKernel.Compute<double>(doubles).Count;
        }
    }

    private static IEnumerable<double[]> Patterns(int length)
    {
        var random = new Random(length);
        yield return Make(length, i => random.Next(8) == 0 ? double.NaN : (random.NextDouble() * 200) - 100);
        yield return Make(length, i => (random.NextDouble() * 2e6) - 1e6);
        var sorted = Make(length, i => i / 3);
        yield return sorted;
        foreach (var at in new[] { 1, 7, 8, 15, 16, 63, 64, length - 2, length - 1 })
        {
            if (at > 0 && at < length)
            {
                var dip = (double[])sorted.Clone();
                dip[at] = dip[at - 1] - 1;
                yield return dip;
            }
        }

        yield return Make(length, _ => double.NaN);
        var nanFirst = Make(length, i => i);
        nanFirst[0] = double.NaN;
        yield return nanFirst;
        var nanLast = Make(length, i => i);
        nanLast[^1] = double.NaN;
        yield return nanLast;
        double[] specials = [0.0, -0.0, double.PositiveInfinity, double.NegativeInfinity, 1.5];
        yield return Make(length, i => specials[random.Next(specials.Length)]);
    }

    private static double[] Make(int length, Func<int, double> value) => [.. Enumerable.Range(0, length).Select(value)];

    private static void Check<T>(T[] values)
        where T : unmanaged, INumber<T>
    {
        var buckets = (values.Length + 63) >> 6;
        var (mins, maxs, numbers) = (new T[buckets], new T[buckets], new byte[buckets]);
        var (expectedMins, expectedMaxs, expectedNumbers) = (new T[buckets], new T[buckets], new byte[buckets]);

        var actual = PyramidKernel.StatsAndLevel1<T>(values, mins, maxs, numbers);
        var expected = ChunkStatsKernel.Compute<T>(values);
        PyramidKernel.Level1<T>(values, expectedMins, expectedMaxs, expectedNumbers);

        Assert.Equal(
            (expected.Min, expected.Max, expected.Count, expected.NaNCount, expected.IsNonDecreasing, expected.Sum),
            (actual.Min, actual.Max, actual.Count, actual.NaNCount, actual.IsNonDecreasing, actual.Sum)
        );
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.Sum), BitConverter.DoubleToInt64Bits(actual.Sum));
        Assert.Equal(double.CreateTruncating(expected.First), double.CreateTruncating(actual.First));
        Assert.Equal(double.CreateTruncating(expected.Last), double.CreateTruncating(actual.Last));
        Assert.Equal(expectedMins, mins);
        Assert.Equal(expectedMaxs, maxs);
        Assert.Equal(expectedNumbers, numbers);
    }
}
