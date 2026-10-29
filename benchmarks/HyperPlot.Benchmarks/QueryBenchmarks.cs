using BenchmarkDotNet.Attributes;

namespace HyperPlot.Benchmarks;

/// <summary>The exact range query (design §3.3) over a built pyramid of 1e7 floats with sparse NaN.</summary>
[MemoryDiagnoser]
public class QueryBenchmarks : IDisposable
{
    private const int Ranges = 256;

    private readonly long[] _starts = new long[Ranges];
    private BuiltSeries? _series;

    /// <summary>Gets or sets the length of each queried range.</summary>
    [Params(64, 4096, 100_000, BuiltSeries.Length)]
    public int RangeLength { get; set; }

    /// <summary>Gets a sink that keeps the results observable.</summary>
    public long Sink { get; private set; }

    /// <summary>Builds the pyramid and picks the seeded range starts.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _series = new BuiltSeries();
        var state = KernelData.Seed;
        for (var i = 0; i < Ranges; i++)
        {
            _starts[i] = (long)(KernelData.Next(ref state) % (ulong)(BuiltSeries.Length - RangeLength + 1));
        }
    }

    /// <summary>One range query, averaged over 256 seeded starts.</summary>
    [Benchmark(OperationsPerInvoke = Ranges)]
    public void GetMinMax()
    {
        long sink = 0;
        foreach (var start in _starts)
        {
            sink += _series!.Pyramid.GetMinMax(start, RangeLength).Count;
        }

        Sink = sink;
    }

    /// <summary>Releases the series.</summary>
    [GlobalCleanup]
    public void Dispose()
    {
        _series?.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>M4 extraction (design §5.5.1) over the whole of a built pyramid of 1e7 floats with sparse NaN, at 1,920 columns.</summary>
[MemoryDiagnoser]
public class M4Benchmarks : IDisposable
{
    private const int Columns = 1920;

    private BuiltSeries? _series;
    private double[] _boundaries = [];
    private double[] _tickBoundaries = [];
    private M4Point[] _points = [];

    /// <summary>Builds the pyramid and the boundaries.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _series = new BuiltSeries();
        _boundaries = [.. Enumerable.Range(0, Columns + 1).Select(k => k * ((double)BuiltSeries.Length / Columns))];
        _tickBoundaries = [.. _boundaries.Select(b => b * 10)];
        _points = new M4Point[M4.MaxPointCount(Columns)];
    }

    /// <summary>X evenly spaced; NaN breaks the line.</summary>
    [Benchmark]
    public int UniformBreak() => M4.Extract(_series!.UniformX, _series.Pyramid, default, _boundaries, _points).Count;

    /// <summary>X evenly spaced; NaN skipped.</summary>
    [Benchmark]
    public int UniformConnect() => M4.Extract(_series!.UniformX, _series.Pyramid, default, _boundaries, _points, new M4Options(M4Gaps.Connect)).Count;

    /// <summary>A stored X column, whose boundaries are found by search; NaN breaks the line.</summary>
    [Benchmark]
    public int StoredBreak() => M4.Extract(_series!.StoredX, _series.Pyramid, default, _boundaries, _points).Count;

    /// <summary>A stored X column of longs from 0, in the origin 0: the cost of the integer difference (design §3.6).</summary>
    [Benchmark]
    public int StoredLongBreak() => M4.Extract(_series!.StoredLongX, _series.Pyramid, default, _boundaries, _points).Count;

    /// <summary>A stored X column of 2026's .NET ticks 10 apart, in an origin at its start (row D2).</summary>
    [Benchmark]
    public int TicksBreak() =>
        M4.Extract(_series!.TicksX, _series.Pyramid, new ScaleOrigin(BuiltSeries.Ticks2026, 0), _tickBoundaries, _points).Count;

    /// <summary>A stored X column; NaN skipped.</summary>
    [Benchmark]
    public int StoredConnect() => M4.Extract(_series!.StoredX, _series.Pyramid, default, _boundaries, _points, new M4Options(M4Gaps.Connect)).Count;

    /// <summary>Releases the series.</summary>
    [GlobalCleanup]
    public void Dispose()
    {
        _series?.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>1e7 seeded floats with sparse NaN, their built pyramid, and two X columns with computed statistics.</summary>
internal sealed class BuiltSeries : IDisposable
{
    internal const int Length = 10_000_000;
    internal const long Ticks2026 = 639_288_288_000_000_000;

    private readonly PlotWorkerPool _pool;
    private readonly Column<float> _y;

    internal BuiltSeries()
    {
        // The run is pinned to one logical processor, where ProcessorCount reads 1; the pool only builds here.
        _pool = new PlotWorkerPool(4);
        _y = Column.Wrap(KernelData.Walk<float>(Length, sparseNaN: true));
        Pyramid = _pool.BuildPyramid(_y);
        Pyramid.Completion.Wait();
        UniformX = Column.Uniform(0, 1, Length);
        StoredX = Column.Wrap(Enumerable.Range(0, Length).Select(i => (double)i).ToArray());
        _pool.PrecomputeStats(StoredX).Wait();
        StoredLongX = Column.Wrap(Enumerable.Range(0, Length).Select(i => (long)i).ToArray());
        _pool.PrecomputeStats(StoredLongX).Wait();
        TicksX = Column.Wrap(Enumerable.Range(0, Length).Select(i => Ticks2026 + (10L * i)).ToArray());
        _pool.PrecomputeStats(TicksX).Wait();
    }

    internal MinMaxPyramid<float> Pyramid { get; }

    internal UniformColumn UniformX { get; }

    internal Column<double> StoredX { get; }

    internal Column<long> StoredLongX { get; }

    internal Column<long> TicksX { get; }

    public void Dispose()
    {
        Pyramid.Dispose();
        _y.Dispose();
        StoredX.Dispose();
        StoredLongX.Dispose();
        TicksX.Dispose();
        _pool.Dispose();
    }
}
