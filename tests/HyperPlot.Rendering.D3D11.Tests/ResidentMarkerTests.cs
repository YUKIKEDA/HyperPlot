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
    /// A run draws each of the six shapes as the recorded markers of the same shape (design §5.1 "行 R3f の形"): the
    /// resident points' pixel shader takes its look from the run.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The snapshot and the target of the test above; markers of 7 pixels at alpha 160 with a stroke of 1.
    ///
    /// Steps:
    /// - For every value of MarkerShape: draw a run over each whole page, and the same points with DrawMarkers.
    ///
    /// Expected:
    /// - No channel of any pixel differs by more than 6 between the two, for each shape; no two shapes draw the same picture.
    /// </remarks>
    [Fact]
    public void Runs_DrawEveryShapeAsTheRecordedMarkers()
    {
        using var built = new Built();
        using var backend = D3D11Backend.Create(useWarp: true);
        using var target = backend.CreateRenderTarget(new RenderTargetDescription(Size, Size));
        var pictures = new List<byte[]>();
        foreach (var shape in Enum.GetValues<MarkerShape>())
        {
            var style = Style with { Size = 7, Shape = shape };
            var resident = Read(backend, target, Resident(backend, built.Snapshot, swap: false, style));
            AssertClose(Read(backend, target, Recorded(built.Snapshot, swap: false, style)), resident, 6);
            Assert.DoesNotContain(pictures, p => p.AsSpan().SequenceEqual(resident));
            pictures.Add(resident);
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

    /// <summary>
    /// A zoomed view drawn from the blocks in view alone has the same pixels as the view drawn from every block of the same
    /// pages (design §5.5.2 "見えるブロックだけを描く", §10 R3b2 row): the blocks left out hold no point that reaches the plot
    /// area.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - <paramref name="count"/> seeded points over [0, 100]² in <paramref name="parts"/> parts, colored by a category
    ///   when there are 8; discs of 3 device-independent pixels at an opacity of 0.6; domains [5, 20] on both axes, inside
    ///   the quadtree's first cell of every level down to 3, whose edges at 0 and 25 are more than 60 physical pixels from
    ///   the view's.
    /// - A 320 × 240 WARP target and two renderers that wait for their indexes, on one backend.
    ///
    /// Steps:
    /// - Render the plot with the first renderer and read its pixels. Set the cache to draw whole pages, render the plot
    ///   with the second renderer, and read its pixels.
    /// - For each part's snapshot, add up the points of the blocks that hold a point of a resident cell whose box reaches
    ///   into the view, and the points of the pages that hold such a block.
    ///
    /// Expected:
    /// - The two pictures are the same, byte for byte, and hold pixels that differ from the first pixel's color.
    /// - The blocks in view hold fewer points than their pages: the two renders drew different runs.
    /// </remarks>
    /// <param name="parts">The parts of the layer.</param>
    /// <param name="count">The points of the layer.</param>
    [Theory]
    [InlineData(1, 400_000)]
    [InlineData(8, 800_000)]
    public void BlocksInView_DrawTheSamePixelsAsWholePages(int parts, int count)
    {
        const int Width = 320;
        const int Height = 240;
        var random = new Random(71);
        using var pool = new PlotWorkerPool(2);
        using var xs = Column.Wrap(Enumerable.Range(0, count).Select(_ => random.NextDouble() * 100).ToArray());
        using var ys = Column.Wrap(Enumerable.Range(0, count).Select(_ => random.NextDouble() * 100).ToArray());
        var names = Enumerable.Range(0, parts).Select(i => $"k{i}").ToArray();
        var table = DataTable
            .Create()
            .Add("x", xs)
            .Add("y", ys)
            .Add("c", Column.Categorical(Enumerable.Range(0, count).Select(i => (string?)names[i % parts]).ToArray()))
            .Build();
        var spec = Plot.Create(table)
            .Point(
                table.Col<double>("x"),
                table.Col<double>("y"),
                p =>
                {
                    _ = p.Size(3).Opacity(0.6);
                    _ = parts > 1 ? p.ColorBy(table.Cat("c")) : p;
                }
            )
            .ScaleX(Scale.Linear().WithDomain(5, 20))
            .ScaleY(Scale.Linear().WithDomain(5, 20))
            .Build();
        using var backend = D3D11Backend.Create(useWarp: true);
        var (inView, whole) = (new byte[Width * Height * 4], new byte[Width * Height * 4]);
        long blockPoints = 0;
        long pagePoints = 0;
        using (var renderer = new HeadlessRenderer(backend, Width, Height, pool))
        {
            renderer.Render(spec);
            renderer.ReadPixels(inView);
            var layer = renderer.Plan!.Layers[0];
            var plans = layer is SplitPlan split ? split.Parts.Cast<IndexedPointPlan>().ToArray() : [(IndexedPointPlan)layer];
            Assert.Equal(parts, plans.Length);
            var block = PointPages.BlockPoints(parts);
            foreach (var plan in plans)
            {
                var snapshot = plan.SpatialIndex!.Acquire();
                var blocks = new HashSet<long>();
                foreach (var cell in snapshot.ResidentCells)
                {
                    var (x0, y0) = (5 - snapshot.XRange.Origin, 5 - snapshot.YRange.Origin);
                    if (cell.CornerX <= x0 + 15 && cell.CornerX + cell.ExtentX >= x0 && cell.CornerY <= y0 + 15 && cell.CornerY + cell.ExtentY >= y0)
                    {
                        for (var b = cell.First / block; b * block < cell.First + cell.Count; b++)
                        {
                            _ = blocks.Add(b);
                        }
                    }
                }

                blockPoints += blocks.Sum(b => Math.Min(block, snapshot.PointCount - (b * block)));
                pagePoints += blocks
                    .Select(b => (int)(b * block / PointPages.PagePoints))
                    .Distinct()
                    .Sum(page => PointPages.PointsOf(snapshot, page));
                snapshot.Release();
            }
        }

        backend.Images.Points.DrawsWholePages = true;
        using (var renderer = new HeadlessRenderer(backend, Width, Height, pool))
        {
            renderer.Render(spec);
            renderer.ReadPixels(whole);
        }

        Assert.True(blockPoints > 0 && blockPoints < pagePoints, $"{blockPoints} points in the blocks in view, {pagePoints} in their pages.");
        Assert.Contains(inView.Chunk(4), p => !p.AsSpan().SequenceEqual(inView.AsSpan(0, 4)));
        Assert.True(inView.AsSpan().SequenceEqual(whole), "The picture of the blocks in view differs from the picture of whole pages.");
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

    private static RenderList Resident(D3D11Backend backend, SpatialSnapshot snapshot, bool swap, MarkerStyle? style = null)
    {
        var look = style ?? Style;
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
            var slot = backend.Images.Points.Request(snapshot, page, PointPages.MaxBlockPoints);
            Assert.True(slot >= 0);
            list.DrawResidentMarkers(
                new ResidentMarkerRun(
                    slot,
                    0,
                    PointPages.PointsOf(snapshot, page),
                    offset,
                    look.Size,
                    (uint)look.Shape | (16u << 8),
                    RenderList.Premultiply(look.Color),
                    swap,
                    new Vector2(float.MinValue),
                    new Vector2(float.MaxValue),
                    default
                )
            );
        }

        return list;
    }

    private static RenderList Recorded(SpatialSnapshot snapshot, bool swap, MarkerStyle? style = null)
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
        list.DrawMarkers(centers, style ?? Style);
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
