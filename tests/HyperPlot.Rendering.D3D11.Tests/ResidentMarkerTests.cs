using System.Numerics;
using TerraFX.Interop.DirectX;

namespace HyperPlot.Rendering.D3D11.Tests;

/// <summary>
/// The resident markers on WARP (design §5.1 "常駐の点", §5.5.2, §10 R3a row): pages of a spatial snapshot's points, placed by
/// the cells' records, against the same points recorded one by one. Windows only.
/// </summary>
[Collection("Large data")]
public sealed class ResidentMarkerTests
{
    private const int Size = 96;
    private const int Points = 100_000;
    private static readonly Color Background = new(250, 245, 235, 255);
    private static readonly MarkerStyle Style = new(new Color(20, 60, 170, 160), 3, MarkerShape.Square);

    /// <summary>
    /// Runs over the pages of a snapshot draw each point where its value maps, as the markers recorded from the same values
    /// do, also when x runs down the target.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The full snapshot of 100,000 seeded points over [0, 1]², whose 2 pages are requested with no frame budget.
    /// - A 96 × 96 WARP target that shows [0.4, 0.5]² of the values; squares of 3 pixels at alpha 160.
    ///
    /// Steps:
    /// - Draw a run over each whole page with a record for each cell: base 0, the corner's pixel, the pixels of a unit.
    /// - Draw the same points with DrawMarkers at the pixels of their values, in the copy's order. Do both again swapped.
    ///
    /// Expected:
    /// - No channel of any pixel differs by more than 6 between the two, swapped or not; the picture is not only background.
    /// </remarks>
    [Fact]
    public void Runs_DrawThePointsWhereTheirValuesMap()
    {
        using var built = new Built();
        using var backend = D3D11Backend.Create(useWarp: true);
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(Size, Size));
        foreach (var swap in new[] { false, true })
        {
            var resident = Read(backend, target, Resident(backend, built.Snapshot, swap));
            var recorded = Read(backend, target, Recorded(built.Snapshot, swap));
            Assert.Contains(resident.Chunk(4), p => p[2] != Background.B);
            AssertClose(recorded, resident, 6);
        }
    }

    /// <summary>
    /// After a device loss the cache forgets its pages and the pipeline its buffers; the next frame reads the pages again
    /// and draws the same pixels (design §5.2, invariant 10).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The snapshot and the target of the test above, drawn once: 2 pages with a buffer each.
    ///
    /// Steps:
    /// - Lose the device through Map; request the pages again and draw.
    ///
    /// Expected:
    /// - The loss throws GpuDeviceLostException and leaves no page in use and no buffer; the next draw makes 2 buffers and
    ///   reads the same pixels.
    /// </remarks>
    [Fact]
    public void DeviceLost_ReadsThePagesAgain()
    {
        using var built = new Built();
        using var backend = D3D11Backend.Create(useWarp: true);
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(Size, Size));
        var first = Read(backend, target, Resident(backend, built.Snapshot, swap: false));
        Assert.Equal((2, 2), (backend.Images.Points.PagesInUse, backend.ResidentMarkers.PageCount));

        backend.CheckResult = (call, result) => call == "Map" ? DXGI.DXGI_ERROR_DEVICE_REMOVED : result;
        _ = Assert.Throws<GpuDeviceLostException>(() => target.ReadPixels(new byte[Size * Size * 4]));
        backend.CheckResult = null;
        Assert.Equal((0, 0), (backend.Images.Points.PagesInUse, backend.ResidentMarkers.PageCount));

        Assert.Equal(first, Read(backend, target, Resident(backend, built.Snapshot, swap: false)));
        Assert.Equal(2, backend.ResidentMarkers.PageCount);
    }

    /// <summary>
    /// A page the cache lets go of loses its buffer at the next frame, and a run that names a slot without a buffer draws
    /// nothing (design §5.1 "生の点のページ").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The snapshot above, drawn once: 2 buffers.
    ///
    /// Steps:
    /// - Forget the snapshot's pages; draw a list that still holds the runs.
    ///
    /// Expected:
    /// - No buffer is left, and every pixel is the background.
    /// </remarks>
    [Fact]
    public void ForgottenPages_LoseTheirBuffers()
    {
        using var built = new Built();
        using var backend = D3D11Backend.Create(useWarp: true);
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(Size, Size));
        var list = Resident(backend, built.Snapshot, swap: false);
        _ = Read(backend, target, list);
        backend.Images.Points.Forget(built.Snapshot);

        var pixels = Read(backend, target, list);
        Assert.Equal(0, backend.ResidentMarkers.PageCount);
        Assert.All(pixels.Chunk(4), p => Assert.Equal(Background.R, p[0]));
    }

    /// <summary>The full snapshot of 100,000 seeded points over [0, 1]², with what it was built from.</summary>
    private sealed class Built : IDisposable
    {
        private readonly PlotWorkerPool _pool = new(2);
        private readonly Column<double> _x;
        private readonly Column<double> _y;
        private readonly SpatialIndex _index;

        public Built()
        {
            var random = new Random(31);
            _x = Column.Wrap(Enumerable.Range(0, Points).Select(_ => random.NextDouble()).ToArray());
            _y = Column.Wrap(Enumerable.Range(0, Points).Select(_ => random.NextDouble()).ToArray());
            _index = _pool.BuildSpatialIndex(_x, _y, new SpatialIndexInput { Rows = RowRange.All });
            _index.WhenComplete().GetAwaiter().GetResult();
            Snapshot = _index.Acquire();
        }

        public SpatialSnapshot Snapshot { get; }

        public void Dispose()
        {
            Snapshot.Release();
            _index.Dispose();
            _x.Dispose();
            _y.Dispose();
            _pool.Dispose();
        }
    }

    // The pixel of a value: [0.4, 0.5] over the target's 96 pixels.
    private static double Pixel(double value) => (value - 0.4) * 10 * Size;

    private static RenderList Resident(D3D11Backend backend, SpatialSnapshot snapshot, bool swap)
    {
        var list = new RenderList();
        var cells = snapshot.ResidentCells;
        var offset = list.AddResidentCells(cells.Length, out var records);
        for (var c = 0; c < cells.Length; c++)
        {
            var cell = cells[c];
            records[c] = new ResidentCellRecord(
                0,
                0,
                (float)Pixel(cell.CornerX),
                (float)Pixel(cell.CornerY),
                (float)(cell.ExtentX * 10 * Size / SpatialSnapshot.CellUnits),
                (float)(cell.ExtentY * 10 * Size / SpatialSnapshot.CellUnits)
            );
        }

        backend.Images.Unbounded = true;
        backend.Images.BeginFrame();
        var pages = (int)((snapshot.PointCount + PointPages.PagePoints - 1) / PointPages.PagePoints);
        for (var page = 0; page < pages; page++)
        {
            var slot = backend.Images.Points.Request(snapshot, page);
            Assert.True(slot >= 0);
            list.DrawResidentMarkers(
                new ResidentMarkerRun(
                    slot,
                    0,
                    PointPages.PointsOf(snapshot, page),
                    offset,
                    Style.Size,
                    (uint)Style.Shape | (16u << 8),
                    RenderList.Premultiply(Style.Color),
                    swap,
                    new Vector2(float.MinValue),
                    new Vector2(float.MaxValue),
                    default
                )
            );
        }

        return list;
    }

    private static RenderList Recorded(SpatialSnapshot snapshot, bool swap)
    {
        var (xs, ys) = (new double[snapshot.PointCount], new double[snapshot.PointCount]);
        snapshot.ReadPoints(0, xs, ys, new long[xs.Length]);
        var centers = new Vector2[xs.Length];
        for (var i = 0; i < centers.Length; i++)
        {
            var (x, y) = ((float)Pixel(xs[i]), (float)Pixel(ys[i]));
            centers[i] = swap ? new Vector2(y, x) : new Vector2(x, y);
        }

        var list = new RenderList();
        list.DrawMarkers(centers, Style);
        return list;
    }

    private static byte[] Read(D3D11Backend backend, IRenderTarget target, RenderList list)
    {
        target.Clear(Background);
        backend.Draw(target, list);
        var pixels = new byte[Size * Size * 4];
        target.ReadPixels(pixels);
        return pixels;
    }

    private static void AssertClose(byte[] expected, byte[] actual, int tolerance)
    {
        var worst = 0;
        var at = -1;
        for (var i = 0; i < expected.Length; i++)
        {
            var difference = Math.Abs(expected[i] - actual[i]);
            if (difference > worst)
            {
                (worst, at) = (difference, i);
            }
        }

        Assert.True(worst <= tolerance, $"Channel {at % 4} of pixel ({(at / 4) % Size}, {(at / 4) / Size}) differs by {worst}.");
    }
}
