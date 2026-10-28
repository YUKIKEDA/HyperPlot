using System.Numerics;
using BenchmarkDotNet.Attributes;
using HyperPlot.Rendering;

namespace HyperPlot.Benchmarks;

/// <summary>
/// Recording M4 polylines into a <see cref="RenderList"/> (design §5.1): 1,920 pixel columns of four points each, the
/// output of M4 over a full-HD plot.
/// </summary>
[MemoryDiagnoser]
public class RenderListBenchmarks
{
    private const int Columns = 1920;

    private readonly RenderList _list = new();
    private Vector2[] _points = [];

    /// <summary>Gets or sets the number of series.</summary>
    [Params(1, 16)]
    public int Series { get; set; }

    /// <summary>Makes a seeded random walk of 7,680 points and records once, so that the buffers have grown.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var state = KernelData.Seed;
        var y = 540.0;
        _points = new Vector2[Columns * 4];
        for (var i = 0; i < _points.Length; i++)
        {
            var step = (((KernelData.Next(ref state) >> 11) * (1.0 / (1UL << 53))) - 0.5) * 20;
            y = Math.Clamp(y + step, 0, 1079);
            _points[i] = new Vector2((i / 4) + ((i % 4) / 4f), (float)y);
        }

        Record();
    }

    /// <summary>Clears the list and records the series.</summary>
    [Benchmark]
    public int Record()
    {
        _list.Clear();
        _list.SetClip(0, 0, 1920, 1080);
        for (var series = 0; series < Series; series++)
        {
            _list.DrawPolyline(_points, new LineStyle(new Color(0, 0, 0, 255), 1.5f));
        }

        return _list.Polylines.Length;
    }
}
