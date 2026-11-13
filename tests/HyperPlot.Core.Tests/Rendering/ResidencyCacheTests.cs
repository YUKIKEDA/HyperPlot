using System.Runtime.InteropServices;
using HyperPlot.Rendering;

namespace HyperPlot.Tests;

/// <summary>
/// The density faces of the residency cache (design §5.1 "密度タイル", §10 R3b1 row): a spatial snapshot's density tiles and
/// coarse density in slots of the float pool, beside the raster tiles.
/// </summary>
[Collection("Large data")]
public sealed class ResidencyCacheTests : IDisposable
{
    private readonly PlotWorkerPool _pool = new(1);

    public void Dispose() => _pool.Dispose();

    /// <summary>A density tile's face holds each bin's count as a float at the bin's texel past the border, and 0 in the border.</summary>
    /// <remarks>
    /// Preconditions:
    /// - A tile of bins <paramref name="width"/> bytes wide: bin (x, y) holds ((31x + 17y) mod 256) in its top byte and,
    ///   for the widths above 1, 1 in its bottom byte, so that the 8-byte bins pass 2^24 and are rounded as floats.
    /// - A residency cache with no frame budget and a full snapshot of 2,000 points as the key.
    ///
    /// Steps:
    /// - Request the tile at level 3 and read the staged face.
    ///
    /// Expected:
    /// - One upload of the float format at the slot returned.
    /// - Face texel (x + 1, y + 1) is the count of bin (x, y) converted to float, for every bin; every texel of the border
    ///   (row 0, row 257, column 0, column 257) is 0.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void RequestDensity_FillsTheBinsAsFloatsAndAZeroBorder(int width)
    {
        var (xs, ys) = SpatialData.Uniform(2_000, seed: 41);
        using var built = SpatialBuilt.Of(xs, ys);
        var counts = Counts(width);
        var cache = new ResidencyCache { Unbounded = true };
        cache.BeginFrame();
        var slot = cache.RequestDensity(built.Snapshot, 3, 5, new DensityTile(Bins(counts, width), width, 0));
        var upload = Assert.Single(cache.Uploads.ToArray());
        Assert.Equal((ResidencyCache.Format.Float, slot), (upload.Format, upload.Slot));
        var face = MemoryMarshal.Cast<uint, float>(cache.Face(upload)).ToArray();
        for (var y = 0; y < ResidencyCache.Side; y++)
        {
            for (var x = 0; x < ResidencyCache.Side; x++)
            {
                var inside = x is > 0 and < ResidencyCache.Side - 1 && y is > 0 and < ResidencyCache.Side - 1;
                var expected = inside ? (float)counts[((y - 1) * DensityTiles.Side) + x - 1] : 0f;
                Assert.Equal(expected, face[(y * ResidencyCache.Side) + x]);
            }
        }
    }

    /// <summary>The cache keeps a density tile by its snapshot, its level, and its code, and by nothing else.</summary>
    /// <remarks>
    /// Preconditions:
    /// - Two full snapshots of 2,000 points; a tile of 1-byte bins; a residency cache with no frame budget.
    ///
    /// Steps:
    /// - Request (snapshot A, level 4, code 9) twice; then change the snapshot alone, the level alone, and the code alone.
    ///
    /// Expected:
    /// - The second request returns the same slot and stages nothing.
    /// - Each change alone returns a slot of its own and stages a face: 4 slots and 4 uploads in all.
    /// </remarks>
    [Fact]
    public void RequestDensity_KeysBySnapshotLevelAndCode()
    {
        var (ax, ay) = SpatialData.Uniform(2_000, seed: 42);
        var (bx, by) = SpatialData.Uniform(2_000, seed: 43);
        using var a = SpatialBuilt.Of(ax, ay);
        using var b = SpatialBuilt.Of(bx, by);
        var bins = Bins(Counts(1), 1);
        var cache = new ResidencyCache { Unbounded = true };
        cache.BeginFrame();
        var first = cache.RequestDensity(a.Snapshot, 4, 9, new DensityTile(bins, 1, 0));
        Assert.Equal(first, cache.RequestDensity(a.Snapshot, 4, 9, new DensityTile(bins, 1, 0)));
        _ = Assert.Single(cache.Uploads.ToArray());

        int[] slots =
        [
            first,
            cache.RequestDensity(b.Snapshot, 4, 9, new DensityTile(bins, 1, 0)),
            cache.RequestDensity(a.Snapshot, 5, 9, new DensityTile(bins, 1, 0)),
            cache.RequestDensity(a.Snapshot, 4, 10, new DensityTile(bins, 1, 0)),
        ];
        Assert.Equal(4, slots.Distinct().Count());
        Assert.Equal(4, cache.Uploads.Length);
        Assert.Equal(4, cache.SlotsInUse);
    }

    /// <summary>
    /// Density faces and raster tiles share the frame's upload of 4 MB: together they stage 15 faces a frame, and the 16th
    /// of either kind waits for the next frame, where the faces already sent are found without being staged again.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A residency cache with the frame's budget; a mipmap of a raster 300 × 300; a full snapshot of 2,000 points and a
    ///   tile of 2-byte bins.
    ///
    /// Steps:
    /// - Request 10 raster tiles (other stamps of one tile), then density tiles of codes 0, 1, 2, … until one returns −1,
    ///   then one more raster tile.
    /// - Say the uploads were sent, begin the next frame, and request the 5 density tiles and the 10 raster tiles again.
    ///
    /// Expected:
    /// - 5 density tiles get a slot and the 6th returns −1, as does the raster tile after it; 15 uploads of the float
    ///   format to 15 different slots; BytesThisFrame is 15 faces of 266,256 bytes; 15 slots in use.
    /// - In the next frame every request returns its slot again and nothing is staged.
    /// </remarks>
    [Fact]
    public void RequestDensity_SharesTheFrameUploadWithRasterTiles()
    {
        using var column = Column.Wrap(new float[300 * 300]);
        using var mipmap = Raster.Of(column, 300).MakePyramid();
        mipmap.Update(_pool, wait: true);
        var (xs, ys) = SpatialData.Uniform(2_000, seed: 44);
        using var built = SpatialBuilt.Of(xs, ys);
        var bins = Bins(Counts(2), 2);
        var cache = new ResidencyCache();
        cache.BeginFrame();
        var raster = new int[10];
        for (var i = 0; i < raster.Length; i++)
        {
            raster[i] = cache.Request(Key(mipmap, i), mipmap);
            Assert.True(raster[i] >= 0);
        }

        var density = new List<int>();
        int slot;
        while ((slot = cache.RequestDensity(built.Snapshot, 2, (ulong)density.Count, new DensityTile(bins, 2, 0))) >= 0)
        {
            density.Add(slot);
        }

        Assert.Equal(5, density.Count);
        Assert.Equal(-1, cache.Request(Key(mipmap, 10), mipmap));
        var uploads = cache.Uploads.ToArray();
        Assert.Equal(15, uploads.Length);
        Assert.All(uploads, u => Assert.Equal(ResidencyCache.Format.Float, u.Format));
        Assert.Equal(15, uploads.Select(u => u.Slot).Distinct().Count());
        Assert.Equal(15L * 266_256, cache.BytesThisFrame);
        Assert.Equal(15, cache.SlotsInUse);

        cache.Sent();
        cache.BeginFrame();
        for (var code = 0; code < density.Count; code++)
        {
            Assert.Equal(density[code], cache.RequestDensity(built.Snapshot, 2, (ulong)code, new DensityTile(bins, 2, 0)));
        }

        for (var i = 0; i < raster.Length; i++)
        {
            Assert.Equal(raster[i], cache.Request(Key(mipmap, i), mipmap));
        }

        Assert.Equal(0, cache.Uploads.Length);
        Assert.Equal(0, cache.BytesThisFrame);
    }

    /// <summary>
    /// Density faces and raster tiles share the float pool's slots and its least-recently-used order: a raster tile lets
    /// go of the density tile used longest ago, and a density tile of the raster tile used longest ago, never one of this
    /// frame.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A residency cache with no frame budget, sent after each request as a backend sends; a mipmap of a raster 300 × 300;
    ///   a full snapshot of 2,000 points and a tile of 1-byte bins. The budget holds 15 pages of 64 slots: 960.
    ///
    /// Steps:
    /// - Frame 1: request 958 raster tiles (stamps 1 to 958). Frame 2: request density tile D. Frame 3: request raster tile
    ///   R0 (stamp 0): every slot is in use.
    /// - Frame 4: request the 958 raster tiles of frame 1 again; a new raster tile; D again; then another new raster tile.
    ///
    /// Expected:
    /// - Frames 1 to 3 fill the 960 slots; frame 4's requests of frame 1's tiles find their slots and stage nothing.
    /// - The new raster tile takes D's slot (D was used in frame 2, R0 in frame 3, the rest in frame 4); D is staged again
    ///   into R0's slot; the other new raster tile returns −1, since every slot is frame 4's.
    /// </remarks>
    [Fact]
    public void RequestDensity_SharesTheLeastRecentlyUsedOrderWithRasterTiles()
    {
        const int Slots = ResidencyCache.SlotCapacity / ResidencyCache.PageSlots * ResidencyCache.PageSlots;
        Assert.Equal(960, Slots);
        using var column = Column.Wrap(new float[300 * 300]);
        using var mipmap = Raster.Of(column, 300).MakePyramid();
        mipmap.Update(_pool, wait: true);
        var (xs, ys) = SpatialData.Uniform(2_000, seed: 45);
        using var built = SpatialBuilt.Of(xs, ys);
        var bins = Bins(Counts(1), 1);
        var cache = new ResidencyCache { Unbounded = true };

        cache.BeginFrame();
        var first = new int[Slots - 2];
        for (var i = 0; i < first.Length; i++)
        {
            first[i] = RequestRaster(i + 1);
            Assert.True(first[i] >= 0);
        }

        cache.BeginFrame();
        var d = RequestD();
        cache.BeginFrame();
        var r0 = RequestRaster(0);
        Assert.Equal(Slots, cache.SlotsInUse);
        Assert.Equal(Slots, first.Append(d).Append(r0).Distinct().Count());

        cache.BeginFrame();
        for (var i = 0; i < first.Length; i++)
        {
            Assert.Equal(first[i], cache.Request(Key(mipmap, i + 1), mipmap));
        }

        Assert.Equal(0, cache.Uploads.Length);
        Assert.Equal(d, RequestRaster(1_000));
        Assert.Equal(r0, cache.RequestDensity(built.Snapshot, 0, 0, new DensityTile(bins, 1, 0)));
        Assert.Equal(r0, Assert.Single(cache.Uploads.ToArray()).Slot);
        cache.Sent();
        Assert.Equal(-1, RequestRaster(1_001));

        int RequestRaster(long stamp)
        {
            var slot = cache.Request(Key(mipmap, stamp), mipmap);
            cache.Sent();
            return slot;
        }

        int RequestD()
        {
            var slot = cache.RequestDensity(built.Snapshot, 0, 0, new DensityTile(bins, 1, 0));
            cache.Sent();
            return slot;
        }
    }

    /// <summary>
    /// ForgetDensity lets go of the density tiles and the coarse density of one snapshot only, and the slots it frees are
    /// taken again; a reset after a device loss forgets every face, and the next request stages the same face again.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two full snapshots A and B of 2,000 points; a tile of 4-byte bins; a raster tile; a residency cache with no frame
    ///   budget.
    ///
    /// Steps:
    /// - Request A's tiles of codes 0 and 1, A's coarse density, B's tile of code 0, and the raster tile; say they were sent.
    /// - Forget A's density. Request B's tile and the raster tile, then A's tile of code 1.
    /// - Reset. Request A's tile of code 1 again.
    ///
    /// Expected:
    /// - Forgetting A leaves 2 slots in use; B's tile and the raster tile keep their slots and stage nothing; A's tile is
    ///   staged again into the lowest of A's freed slots.
    /// - After the reset no slot is in use and nothing is staged; the request stages a face equal to the one staged before.
    /// </remarks>
    [Fact]
    public void ForgetDensityAndReset_LetGoOfTheFaces()
    {
        using var column = Column.Wrap(new float[300 * 300]);
        using var mipmap = Raster.Of(column, 300).MakePyramid();
        mipmap.Update(_pool, wait: true);
        var (ax, ay) = SpatialData.Uniform(2_000, seed: 46);
        var (bx, by) = SpatialData.Uniform(2_000, seed: 47);
        using var a = SpatialBuilt.Of(ax, ay);
        using var b = SpatialBuilt.Of(bx, by);
        var bins = Bins(Counts(4), 4);
        var cache = new ResidencyCache { Unbounded = true };
        cache.BeginFrame();
        int[] ofA =
        [
            cache.RequestDensity(a.Snapshot, 1, 0, new DensityTile(bins, 4, 0)),
            cache.RequestDensity(a.Snapshot, 1, 1, new DensityTile(bins, 4, 0)),
            cache.RequestCoarseDensity(a.Snapshot),
        ];
        var ofB = cache.RequestDensity(b.Snapshot, 1, 0, new DensityTile(bins, 4, 0));
        var raster = cache.Request(Key(mipmap, 1), mipmap);
        var face = cache.Face(cache.Uploads[1]).ToArray();
        cache.Sent();
        Assert.Equal(5, cache.SlotsInUse);

        cache.BeginFrame();
        cache.ForgetDensity(a.Snapshot);
        Assert.Equal(2, cache.SlotsInUse);
        Assert.Equal(ofB, cache.RequestDensity(b.Snapshot, 1, 0, new DensityTile(bins, 4, 0)));
        Assert.Equal(raster, cache.Request(Key(mipmap, 1), mipmap));
        Assert.Equal(0, cache.Uploads.Length);
        Assert.Equal(ofA.Min(), cache.RequestDensity(a.Snapshot, 1, 1, new DensityTile(bins, 4, 0)));
        _ = Assert.Single(cache.Uploads.ToArray());
        cache.Sent();

        cache.Reset();
        Assert.Equal(0, cache.SlotsInUse);
        Assert.Equal(0, cache.Uploads.Length);
        cache.BeginFrame();
        _ = cache.RequestDensity(a.Snapshot, 1, 1, new DensityTile(bins, 4, 0));
        Assert.Equal(face, cache.Face(Assert.Single(cache.Uploads.ToArray())).ToArray());
    }

    /// <summary>
    /// The coarse density is one face of the snapshot's 256 × 256 counts of level 8, keyed apart from the snapshot's tiles,
    /// and a snapshot that holds nothing has none.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The full snapshot of 20,000 points in nested clusters; the root tile (level 0, code 0) made of 1-byte bins that
    ///   are all 0; a residency cache with no frame budget.
    /// - The empty snapshot of an index whose only worker is kept in another job.
    ///
    /// Steps:
    /// - Request the coarse density twice and the root tile; read the coarse density's face.
    /// - Request the coarse density of the empty snapshot.
    ///
    /// Expected:
    /// - The same slot twice, one upload for it; the root tile takes another slot.
    /// - Face texel (x + 1, y + 1) is CoarseDensity[256y + x] as a float, and the border is 0.
    /// - The empty snapshot throws ArgumentException.
    /// </remarks>
    [Fact]
    public void RequestCoarseDensity_IsOneFaceOfTheLevel8Counts()
    {
        var (xs, ys) = SpatialData.Nested(20_000, seed: 48);
        using var built = SpatialBuilt.Of(xs, ys);
        var snapshot = built.Snapshot;
        var cache = new ResidencyCache { Unbounded = true };
        cache.BeginFrame();
        var slot = cache.RequestCoarseDensity(snapshot);
        Assert.Equal(slot, cache.RequestCoarseDensity(snapshot));
        var upload = Assert.Single(cache.Uploads.ToArray());
        Assert.Equal(slot, upload.Slot);
        Assert.NotEqual(slot, cache.RequestDensity(snapshot, 0, 0, new DensityTile(new byte[DensityTiles.BinCount], 1, 0)));

        var counts = snapshot.CoarseDensity.ToArray();
        Assert.True(counts.Max() > 1);
        var face = MemoryMarshal.Cast<uint, float>(cache.Face(upload)).ToArray();
        for (var y = 0; y < ResidencyCache.Side; y++)
        {
            for (var x = 0; x < ResidencyCache.Side; x++)
            {
                var inside = x is > 0 and < ResidencyCache.Side - 1 && y is > 0 and < ResidencyCache.Side - 1;
                var expected = inside ? (float)counts[((y - 1) * DensityTiles.Side) + x - 1] : 0f;
                Assert.Equal(expected, face[(y * ResidencyCache.Side) + x]);
            }
        }

        using var rig = new SpatialRig();
        SpatialIndex index;
        using (rig.BlockWorker())
        {
            index = rig.Start();
            var empty = index.Acquire();
            Assert.Equal(SpatialStage.None, empty.Stage);
            var thrown = Assert.Throws<ArgumentException>(() => cache.RequestCoarseDensity(empty));
            Assert.Equal("snapshot", thrown.ParamName);
            empty.Release();
        }

        Eventually.Completes(index.Completion);
    }

    private static ResidencyCache.TileKey Key(RasterPyramid mipmap, long stamp) => new(mipmap, ResidencyCache.Format.Float, 0, 0, 0, stamp, 0);

    /// <summary>Returns the counts of 256 × 256 bins of a width: (31x + 17y) mod 256 in the top byte, and 1 in the bottom byte above 1 byte.</summary>
    private static ulong[] Counts(int width)
    {
        var counts = new ulong[DensityTiles.BinCount];
        for (var y = 0; y < DensityTiles.Side; y++)
        {
            for (var x = 0; x < DensityTiles.Side; x++)
            {
                var top = (ulong)(((31 * x) + (17 * y)) % 256) << (8 * (width - 1));
                counts[(y * DensityTiles.Side) + x] = width == 1 ? top : top | 1;
            }
        }

        return counts;
    }

    /// <summary>Writes counts as bins of a width in little-endian order, as a density tile holds them.</summary>
    private static byte[] Bins(ulong[] counts, int width)
    {
        var bins = new byte[counts.Length * width];
        for (var i = 0; i < counts.Length; i++)
        {
            for (var k = 0; k < width; k++)
            {
                bins[(i * width) + k] = (byte)(counts[i] >> (8 * k));
            }
        }

        return bins;
    }
}
