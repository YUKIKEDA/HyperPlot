using System.Numerics;
using BenchmarkDotNet.Attributes;

namespace HyperPlot.Benchmarks;

/// <summary>
/// The passes a worker can make over one chunk (design §3.2, §3.3): the statistics alone, the first level of the pyramid
/// alone (what a build does when the statistics are already computed), both one after the other, and both in one pass
/// (what a build does otherwise). Each operation reads 64 chunks (256 MiB of <see cref="float"/>), more than
/// the last-level cache, so the first pass over a chunk reads memory as a build does.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
[MemoryDiagnoser]
[GenericTypeArguments(typeof(float))]
[GenericTypeArguments(typeof(double))]
public class ChunkPassBenchmarks<T>
    where T : unmanaged, INumber<T>
{
    private const int Chunks = 64;

    private T[] _values = [];
    private T[] _mins = [];
    private T[] _maxs = [];
    private byte[] _numbers = [];

    /// <summary>Gets or sets a value indicating whether one value in 64 is NaN.</summary>
    [Params(false, true)]
    public bool SparseNaN { get; set; }

    /// <summary>Gets a sink that keeps the results observable.</summary>
    public long Sink { get; private set; }

    /// <summary>Makes the input and checks that the one pass equals the two kernels.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _values = KernelData.Walk<T>((long)Chunks * Column.ChunkLength, SparseNaN);
        var buckets = Column.ChunkLength >> PyramidLayout.FirstShift;
        _mins = new T[buckets];
        _maxs = new T[buckets];
        _numbers = new byte[buckets];
        var fusedMins = new T[buckets];
        var fusedMaxs = new T[buckets];
        var fusedNumbers = new byte[buckets];
        for (var chunk = 0; chunk < Chunks; chunk++)
        {
            var values = Chunk(chunk);
            var stats = ChunkStatsKernel.Compute(values);
            Level1(values);
            var fused = PyramidKernel.StatsAndLevel1<T>(values, fusedMins, fusedMaxs, fusedNumbers);
            var same =
                (stats.Min, stats.Max, stats.First, stats.Last, stats.Sum, stats.Count, stats.NaNCount, stats.IsNonDecreasing).Equals(
                    (fused.Min, fused.Max, fused.First, fused.Last, fused.Sum, fused.Count, fused.NaNCount, fused.IsNonDecreasing)
                )
                && _mins.AsSpan().SequenceEqual(fusedMins)
                && _maxs.AsSpan().SequenceEqual(fusedMaxs)
                && _numbers.AsSpan().SequenceEqual(fusedNumbers);
            if (!same)
            {
                throw new InvalidOperationException($"The one pass differs from the two kernels in chunk {chunk}.");
            }
        }
    }

    /// <summary>The statistics alone.</summary>
    [Benchmark(OperationsPerInvoke = Chunks)]
    public void Stats()
    {
        long sink = 0;
        for (var chunk = 0; chunk < Chunks; chunk++)
        {
            sink += ChunkStatsKernel.Compute(Chunk(chunk)).Count;
        }

        Sink = sink;
    }

    /// <summary>The first level alone.</summary>
    [Benchmark(OperationsPerInvoke = Chunks)]
    public void Level1()
    {
        for (var chunk = 0; chunk < Chunks; chunk++)
        {
            Level1(Chunk(chunk));
        }

        Sink = _numbers[0];
    }

    /// <summary>The statistics, then the first level over the same chunk: what a build did before row B1.</summary>
    [Benchmark(Baseline = true, OperationsPerInvoke = Chunks)]
    public void Separate()
    {
        long sink = 0;
        for (var chunk = 0; chunk < Chunks; chunk++)
        {
            var values = Chunk(chunk);
            sink += ChunkStatsKernel.Compute(values).Count;
            Level1(values);
        }

        Sink = sink;
    }

    /// <summary>The statistics and the first level in one pass.</summary>
    [Benchmark(OperationsPerInvoke = Chunks)]
    public void OnePass()
    {
        long sink = 0;
        for (var chunk = 0; chunk < Chunks; chunk++)
        {
            sink += PyramidKernel.StatsAndLevel1<T>(Chunk(chunk), _mins, _maxs, _numbers).Count;
        }

        Sink = sink;
    }

    private ReadOnlySpan<T> Chunk(int chunk) => _values.AsSpan(chunk * Column.ChunkLength, Column.ChunkLength);

    private void Level1(ReadOnlySpan<T> values) => PyramidKernel.Level1(values, _mins, _maxs, _numbers);
}
