using BenchmarkDotNet.Attributes;

namespace HyperPlot.Benchmarks;

/// <summary>
/// Series of 1e7 floats at 1,920 pixel columns (design §5.5.1, §8.1): one after the other with <see cref="M4.Extract{TY}(UniformColumn, MinMaxPyramid{TY}, ReadOnlySpan{double}, Span{M4Point}, M4Options)"/>,
/// and across the shared pool with <see cref="PlotWorkerPool.Extract(M4Batch, double[])"/>. Not pinned: the workers run on
/// the cores the OS gives them (.cursor/rules/bench.mdc).
/// </summary>
[MemoryDiagnoser]
public class M4BatchBenchmarks : IDisposable
{
    private const int MaxSeries = 16;
    private const int Length = 10_000_000;
    private const int Columns = 1920;

    private readonly Column<float>[] _columns = new Column<float>[MaxSeries];
    private readonly MinMaxPyramid<float>[] _pyramids = new MinMaxPyramid<float>[MaxSeries];
    private readonly M4Point[][] _outputs = new M4Point[MaxSeries][];
    private readonly M4Batch _batch = new();
    private readonly UniformColumn _x = Column.Uniform(0, 1, Length);
    private double[] _boundaries = [];

    /// <summary>Gets or sets the number of series: 1, as many as the reference machine's physical cores (10), and 16.</summary>
    [Params(1, 10, 16)]
    public int Series { get; set; }

    /// <summary>Gets a sink that keeps the results observable.</summary>
    public int Sink { get; private set; }

    /// <summary>Makes the series and their pyramids, and the batch.</summary>
    [GlobalSetup]
    public void Setup()
    {
        for (var s = 0; s < Series; s++)
        {
            _columns[s] = Column.Wrap(KernelData.Walk<float>(Length, sparseNaN: true));
            _pyramids[s] = PlotWorkerPool.Shared.BuildPyramid(_columns[s]);
            _outputs[s] = new M4Point[M4.MaxPointCount(Columns)];
            _ = _batch.Add(_x, _pyramids[s], _outputs[s]);
        }

        for (var s = 0; s < Series; s++)
        {
            _pyramids[s].Completion.Wait();
        }

        _boundaries = [.. Enumerable.Range(0, Columns + 1).Select(k => k * ((double)Length / Columns))];
    }

    /// <summary>The series one after the other on this thread.</summary>
    [Benchmark(Baseline = true)]
    public void OneAfterTheOther()
    {
        var sink = 0;
        for (var s = 0; s < Series; s++)
        {
            sink += M4.Extract(_x, _pyramids[s], _boundaries, _outputs[s]).Count;
        }

        Sink = sink;
    }

    /// <summary>The series across the shared pool and this thread.</summary>
    [Benchmark]
    public void AcrossThePool()
    {
        PlotWorkerPool.Shared.Extract(_batch, _boundaries);
        Sink = _batch.GetResult(0).Count;
    }

    /// <summary>Releases the pyramids and the columns.</summary>
    [GlobalCleanup]
    public void Dispose()
    {
        foreach (var pyramid in _pyramids)
        {
            pyramid?.Dispose();
        }

        foreach (var column in _columns)
        {
            column?.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
