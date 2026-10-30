using System.Runtime.InteropServices;
using HyperPlot.Testing;
using HyperPlot.Tests;

namespace HyperPlot.Rendering.D3D11.Tests;

/// <summary>Plots with guides drawn on WARP (row G3, design §4.6, §10, §10.1), with the fake text backend for their pixels.</summary>
public sealed class GuideRenderTests
{
    private const int Width = 640;
    private const int Height = 360;
    private const int Points = 100_000;

    /// <summary>
    /// The light theme's guides (§10.1 scene 6): a time x in ns with a title, a nice linear y with a title, a legend of two
    /// labels with a dashed layer, and a plot title.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 1e5 points a layer, 1 ms apart from 2026-01-01 in Unix ns; seeded sines; the fake text backend; the scale 1.
    ///
    /// Steps:
    /// - Render at 640 × 360 with Theme.Light; compare with <c>Golden/guides-light.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void Light_MatchesBaseline()
    {
        var start = (new DateTime(2026, 1, 1) - DateTime.UnixEpoch).Ticks * 100;
        var x = Enumerable.Range(0, Points).Select(i => start + (1_000_000L * i)).ToArray();
        var random = new Random(3);
        var a = Enumerable.Range(0, Points).Select(i => (float)((Math.Sin(i * 1e-4) * 2.3) + (random.NextDouble() * 0.4))).ToArray();
        var b = Enumerable.Range(0, Points).Select(i => (float)(Math.Cos(i * 4e-5) + 0.7)).ToArray();
        using var pool = new PlotWorkerPool(2);
        using var xColumn = Column.Wrap(x);
        using var aColumn = Column.Wrap(a);
        using var bColumn = Column.Wrap(b);
        var table = DataTable.Create().Add("t", xColumn).Add("a", aColumn).Add("b", bColumn).Build();
        var spec = Plot.Create(table)
            .Line(table.Col<long>("t"), table.Col<float>("a"), l => l.Color(new Color(31, 119, 180, 255)).Label("signal"))
            .Line(
                table.Col<long>("t"),
                table.Col<float>("b"),
                l => l.Color(new Color(255, 127, 14, 255)).Width(2).Dash(new LineDash(6, 3)).Label("trend")
            )
            .ScaleX(Scale.Time(TimeUnit.Nanoseconds).WithTitle("Time"))
            .ScaleY(Scale.Linear().WithTitle("Value") with { Nice = true })
            .Title("Guides")
            .Build();

        using var backend = D3D11Backend.CreateForTest(useWarp: true, createDevice: null, new TextEngine(new FakeTextBackend()));
        using var renderer = new HeadlessRenderer(backend, Width, Height, pool);
        renderer.Render(spec);

        GoldenImage.AssertMatches(renderer, GoldenImageTests.Baseline("guides-light.png"), GoldenImageTests.Tolerance, GoldenImageTests.Output);
    }

    /// <summary>The dark theme's guides at the scale 1.5 (§10.1 scene 7): a nice log y and a sqrt x.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 1e5 points: x from 0 to 1e4, y from about 0.3 to 3,000; the fake text backend.
    ///
    /// Steps:
    /// - Render at 640 × 360 and the scale 1.5 with Theme.Dark; compare with <c>Golden/guides-dark.png</c>.
    ///
    /// Expected:
    /// - The image matches the baseline.
    /// </remarks>
    [Fact]
    public void Dark_MatchesBaseline()
    {
        var x = Enumerable.Range(0, Points).Select(i => i * 0.1).ToArray();
        var y = Enumerable.Range(0, Points).Select(i => (float)Math.Pow(10, (Math.Sin(i * 6e-5) * 2) + 1.5)).ToArray();
        using var pool = new PlotWorkerPool(2);
        using var xColumn = Column.Wrap(x);
        using var yColumn = Column.Wrap(y);
        var table = DataTable.Create().Add("x", xColumn).Add("y", yColumn).Build();
        var spec = Plot.Create(table)
            .Line(table.Col<double>("x"), table.Col<float>("y"), l => l.Color(new Color(120, 200, 255, 255)).Label("power"))
            .ScaleX(Scale.Sqrt().WithTitle("sqrt x"))
            .ScaleY(Scale.Log().WithTitle("log y") with { Nice = true })
            .Theme(Theme.Dark)
            .Title("Dark")
            .Build();

        using var backend = D3D11Backend.CreateForTest(useWarp: true, createDevice: null, new TextEngine(new FakeTextBackend()));
        using var renderer = new HeadlessRenderer(backend, Width, Height, pool, scale: 1.5f);
        renderer.Render(spec);

        GoldenImage.AssertMatches(renderer, GoldenImageTests.Baseline("guides-dark.png"), GoldenImageTests.Tolerance, GoldenImageTests.Output);
    }

    /// <summary>
    /// The axis lines of widths 1 and 2 lie just outside the panel and cover none of its pixels (design §4.6).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A red panel, no grid, the data drawn far outside the y domain; axis widths 1 and 2.
    ///
    /// Steps:
    /// - Render; read the panel's last row and first column, and the row below and the column left of it.
    ///
    /// Expected:
    /// - The panel's edge pixels are red; the pixels just outside are the axis color.
    /// </remarks>
    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    public void AxisLines_StayOffThePanel(float width)
    {
        using var pool = new PlotWorkerPool(1);
        using var y = Column.Wrap(new float[] { 100, 200, 300 });
        var table = DataTable.Create().Add("t", Column.Uniform(0, 1, 3)).Add("y", y).Build();
        var red = new Color(255, 0, 0, 255);
        var spec = Plot.Create(table)
            .Line(table.Col<double>("t"), table.Col<float>("y"))
            .ScaleY(Scale.Linear().WithDomain(0, 1))
            .Theme(Theme.Light with { PanelBackground = red, ShowGrid = false, AxisWidth = width, AxisColor = new Color(0, 0, 255, 255) })
            .Build();
        using var backend = D3D11Backend.CreateForTest(useWarp: true, createDevice: null, new TextEngine(new FakeTextBackend()));
        using var renderer = new HeadlessRenderer(backend, Width, Height, pool);
        renderer.Render(spec);
        var pixels = new byte[Width * Height * 4];
        renderer.ReadPixels(pixels);

        // The panel: find the red block's edges from the middle of the image.
        var (row, column) = (Height / 2, Width / 2);
        var left = column;
        while (IsRed(Pixel(left - 1, row)))
        {
            left--;
        }

        var bottom = row;
        while (IsRed(Pixel(column, bottom + 1)))
        {
            bottom++;
        }

        Assert.Equal((0, 0, 255), Pixel(left - 1, row));
        Assert.Equal((0, 0, 255), Pixel(column, bottom + 1));

        (byte, byte, byte) Pixel(int x, int y) =>
            (pixels[((y * Width) + x) * 4], pixels[(((y * Width) + x) * 4) + 1], pixels[(((y * Width) + x) * 4) + 2]);

        static bool IsRed((byte R, byte G, byte B) p) => p == (255, 0, 0);
    }

    /// <summary>The example of row G3 in design §4.9 runs as written: two mapped files, a time axis, light, then dark.</summary>
    /// <remarks>
    /// Preconditions:
    /// - Two files of 1e5 floats; a WARP backend through D3D11Backend.Create as the example has it, with DirectWrite.
    ///
    /// Steps:
    /// - Run the example's statements, reading the pixels after each Render.
    ///
    /// Expected:
    /// - The light image's corner is white, the dark image's corner the dark theme's background.
    /// </remarks>
    [Fact]
    public void DesignExample_RowG3_Runs()
    {
        var first = Path.Combine(Path.GetTempPath(), $"hyperplot-g3-{Guid.NewGuid():N}.f32");
        var second = Path.Combine(Path.GetTempPath(), $"hyperplot-g3-{Guid.NewGuid():N}.f32");
        File.WriteAllBytes(first, MemoryMarshal.AsBytes(Enumerable.Range(0, Points).Select(i => MathF.Sin(i * 1e-4f)).ToArray()).ToArray());
        File.WriteAllBytes(second, MemoryMarshal.AsBytes(Enumerable.Range(0, Points).Select(i => MathF.Cos(i * 3e-5f)).ToArray()).ToArray());
        try
        {
            var corners = new List<byte[]>();
            using (var ch1 = Column.MapFile<float>(first))
            using (var ch2 = Column.MapFile<float>(second))
            {
                var start = (new DateTime(2026, 1, 1) - DateTime.UnixEpoch).Ticks * 100;
                var table = DataTable
                    .Create()
                    .Add("t", Column.UniformInteger(start: start, step: 1_000_000, length: ch1.Length))
                    .Add("ch1", ch1)
                    .Add("ch2", ch2)
                    .Build();
                var t = table.Col<long>("t");
                var plot = Plot.Create(table)
                    .Line(x: t, y: table.Col<float>("ch1"), l => l.Color(new Color(31, 119, 180, 255)).Label("ch1"))
                    .Line(x: t, y: table.Col<float>("ch2"), l => l.Color(new Color(255, 127, 14, 255)).Dash(new LineDash(4, 2)).Label("ch2"))
                    .ScaleX(Scale.Time(TimeUnit.Nanoseconds).WithTitle("Time"))
                    .ScaleY(Scale.Linear().WithTitle("Value [V]") with { Nice = true })
                    .Title("Two channels")
                    .Build();

                using var backend = D3D11Backend.Create(useWarp: true);
                using var renderer = new HeadlessRenderer(backend, 1280, 720, scale: 1.5f);
                renderer.Render(plot);
                corners.Add(Corner(renderer));
                renderer.Render(plot with { Theme = Theme.Dark });
                corners.Add(Corner(renderer));
            }

            Assert.Equal(new byte[] { 255, 255, 255, 255 }, corners[0]);
            Assert.Equal(new byte[] { 24, 24, 27, 255 }, corners[1]);
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }

        static byte[] Corner(HeadlessRenderer renderer)
        {
            var pixels = new byte[renderer.Width * renderer.Height * 4];
            renderer.ReadPixels(pixels);
            return pixels[..4];
        }
    }
}
