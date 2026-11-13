namespace HyperPlot.Tests;

/// <summary>
/// The density tiles of a spatial tile index against the brute force over every point of the copy: which cells have a
/// tile, the bins, the widths, the budget, the lookup, and the stage of the build that makes them (design §3.4
/// "密度タイル", row C3b).
/// </summary>
[Collection("Large data")]
public sealed class SpatialDensityTests
{
    /// <summary>
    /// Every bin of every tile is the number of points of the copy in it, and each tile holds its largest bin, for each shape
    /// of data.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Leaves of 16 points and units of 256 rows, 3 workers, and a density split of <paramref name="split"/> points.
    ///   The share of the table and the tiles is not limited: a tile is 65,536 B or more, and the source is 40,000 B
    ///   to 320,000 B.
    /// - "uniform": 5,000 float points spread evenly. "nested": 20,000 double points in clusters inside clusters, so
    ///   that a quadrant without a tile is read in runs of 256 points. "deep": 1,000 double points spread evenly and
    ///   1,000 in a square 2^-29 of the range wide, so that split cells below level 24 hold more points than the
    ///   split. "coincident": the uniform points and 3,000 points at one place. "capped": the nested points with a
    ///   table share of 0.01, so that leaves are capped. "skipped": the uniform points with NaN and infinities in 50
    ///   rows of x and 50 rows of y.
    ///
    /// Steps:
    /// - Build the index and read its full snapshot. For each row of the table, ask for the tile of its cell.
    /// - Read every point of the copy and make its code by the formula of the design, a bit at a time.
    ///
    /// Expected:
    /// - A cell has a tile exactly when it is split, holds more points than the split, and its level is 24 or less.
    ///   TileCount is the number of such cells, and at least one cell has a tile. In "deep", a split cell below level
    ///   24 holds more points than the split and has no tile.
    /// - The table has a capped leaf in "capped" and a coincident leaf in "coincident".
    /// - Bin (x, y) of the tile of a cell of level L is the number of points whose code lies in the cell and whose
    ///   cell at level L + 8 has x and y as its lower 8 bits on each axis.
    /// - The tile's Largest is the most points of a bin of the loop (row R3b1), and the width of a tile is the least of 1,
    ///   2, 4, and 8 bytes that holds it.
    /// - ByteCount is the bins of every tile and 32 bytes a tile, the size of a line of the directory.
    /// </remarks>
    [Theory]
    [InlineData("uniform", 64)]
    [InlineData("nested", 256)]
    [InlineData("deep", 64)]
    [InlineData("coincident", 64)]
    [InlineData("capped", 256)]
    [InlineData("skipped", 64)]
    public void Tiles_OfEachShapeOfData_HoldThePointsOfEachBin(string shape, int split)
    {
        var tuning = SpatialReference.Small with { DensitySplit = split, TotalShare = 1e6 };
        using var built = Build(shape, tuning);
        var snapshot = built.Snapshot;
        Assert.True(built.Index.IsComplete);
        Assert.Null(built.Index.Fault);

        var tiles = snapshot.Tiles;
        Assert.NotNull(tiles);
        var codes = Codes(snapshot);
        var expected = 0;
        var below = 0;
        long bytes = 0;
        foreach (var row in snapshot.Rows)
        {
            below += row.Kind == (byte)SpatialCellKind.Inner && row.Count > split && row.Level > 24 ? 1 : 0;
            var wanted = row.Kind == (byte)SpatialCellKind.Inner && row.Count > split && row.Level <= 24;
            var found = tiles.TryGetTile(row.Code, row.Level, out var tile);
            if (found != wanted)
            {
                Assert.Fail($"The cell {row.Code} of level {row.Level} with {row.Count} points has a tile: {found}; expected {wanted}.");
            }

            if (!found)
            {
                continue;
            }

            expected++;
            var bins = Brute(codes, row.Code, row.Level);
            var largest = bins.Max();
            Assert.Equal(largest, tile.Largest);
            Assert.Equal(DensityTiles.BytesPerBinFor(largest), tile.BytesPerBin);
            Assert.Equal(DensityTiles.BinCount * tile.BytesPerBin, tile.Bins.Length);
            bytes += tile.Bins.Length + 32;
            for (var bin = 0; bin < bins.Length; bin++)
            {
                var (x, y) = (bin % DensityTiles.Side, bin / DensityTiles.Side);
                if (tile[x, y] != bins[bin])
                {
                    Assert.Fail(
                        $"Bin ({x}, {y}) of the tile of the cell {row.Code} of level {row.Level} is {tile[x, y]}; the loop gives {bins[bin]}."
                    );
                }
            }
        }

        Assert.True(expected > 0, "No cell has a tile.");
        Assert.Equal(expected, tiles.TileCount);
        Assert.Equal(bytes, tiles.ByteCount);
        Assert.Equal(32, System.Runtime.CompilerServices.Unsafe.SizeOf<DensityTiles.Entry>());

        // The root has a tile, so a leaf of the kind the shape is named for lies under a cell with a tile.
        var kinds = snapshot.Rows.ToArray().Select(row => (SpatialCellKind)row.Kind).ToHashSet();
        if (shape is "capped" or "coincident")
        {
            Assert.Contains(shape == "capped" ? SpatialCellKind.CappedLeaf : SpatialCellKind.CoincidentLeaf, kinds);
        }

        if (shape == "deep")
        {
            Assert.True(below > 0, "No split cell below level 24 holds more points than the split.");
        }
    }

    /// <summary>A tile's quadrant over a child with a tile is the 2 × 2 sums of the child's tile.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 20,000 nested double points, leaves of 16 points, a density split of 256, and 3 workers.
    ///
    /// Steps:
    /// - For each cell with a tile whose child has a tile, compare each bin of the child's quadrant with the sum of the
    ///   four bins of the child's tile below it.
    ///
    /// Expected:
    /// - Some tile has a child with a tile, and every such bin is the sum.
    /// </remarks>
    [Fact]
    public void Tile_OverAChildWithATile_IsTheSumOfTwoByTwoBins()
    {
        using var built = Build("nested", SpatialReference.Small with { DensitySplit = 256, TotalShare = 1e6 });
        var tiles = built.Snapshot.Tiles!;
        var rows = built.Snapshot.Rows;
        var pairs = 0;
        foreach (var row in rows)
        {
            if (!tiles.TryGetTile(row.Code, row.Level, out var parent))
            {
                continue;
            }

            for (var quadrant = 0; quadrant < 4; quadrant++)
            {
                var child = rows[row.FirstChild + quadrant];
                if (!tiles.TryGetTile(child.Code, child.Level, out var below))
                {
                    continue;
                }

                pairs++;
                var (left, top) = ((quadrant & 1) * 128, (quadrant >> 1) * 128);
                for (var y = 0; y < 128; y++)
                {
                    for (var x = 0; x < 128; x++)
                    {
                        var sum = below[2 * x, 2 * y] + below[(2 * x) + 1, 2 * y] + below[2 * x, (2 * y) + 1] + below[(2 * x) + 1, (2 * y) + 1];
                        if (parent[left + x, top + y] != sum)
                        {
                            Assert.Fail(
                                $"Bin ({left + x}, {top + y}) of the cell {row.Code} of level {row.Level} is {parent[left + x, top + y]}; its child's four bins sum to {sum}."
                            );
                        }
                    }
                }
            }
        }

        Assert.True(pairs > 0, "No tile has a child with a tile.");
    }

    /// <summary>The width of a tile changes where its largest bin passes 255 and 65,535, and no bin is saturated.</summary>
    /// <remarks>
    /// Preconditions:
    /// - <paramref name="same"/> float points at one place and 300 points spread evenly, leaves of 16 points, a density
    ///   split of 64, and 3 workers. None of the 300 points lies in the bin of the place, so that bin holds exactly
    ///   <paramref name="same"/> points and is the largest of the root's tile.
    ///
    /// Steps:
    /// - Read the tile of the root and its largest bin.
    ///
    /// Expected:
    /// - The largest bin of the loop over the copy is exactly <paramref name="same"/>: 255, 256, 65,535, or 65,536.
    /// - The tile's bin holds that number, and so does its Largest; the tile's bins are <paramref name="width"/> bytes
    ///   wide: 1 byte at 255, 2 at 256 and at 65,535, and 4 at 65,536.
    /// - The function of the width gives 4 bytes at 4,294,967,295 and 8 at 4,294,967,296, a number of points that no
    ///   test holds.
    /// </remarks>
    [Theory]
    [InlineData(255, 1)]
    [InlineData(256, 2)]
    [InlineData(65_535, 2)]
    [InlineData(65_536, 4)]
    public void Tile_LargestBinAtTheEndsOfTheWidths_TakesTheNextWidthWithoutSaturating(int same, int width)
    {
        var (xs, ys) = SpatialData.Uniform(300 + same, seed: 81);
        for (var row = 300; row < xs.Length; row++)
        {
            (xs[row], ys[row]) = (12.5f, 250f);
        }

        using var built = SpatialBuilt.Of(xs, ys, tuning: SpatialReference.Small with { DensitySplit = 64, TotalShare = 1e6 });
        Assert.True(built.Snapshot.Tiles!.TryGetTile(0, 0, out var root));
        var bins = Brute(Codes(built.Snapshot), 0, 0);
        var largest = bins.Max();
        Assert.Equal((ulong)same, largest);
        Assert.Equal(width, root.BytesPerBin);
        var index = Array.IndexOf(bins, largest);
        Assert.Equal(largest, root[index % DensityTiles.Side, index / DensityTiles.Side]);
        Assert.Equal((ulong)same, root.Largest);

        Assert.Equal(4, DensityTiles.BytesPerBinFor(uint.MaxValue));
        Assert.Equal(8, DensityTiles.BytesPerBinFor((ulong)uint.MaxValue + 1));
    }

    /// <summary>The tiles stop at the level whose estimate would pass 10 % of the source with the table.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 200,000 double points spread evenly (3,200,000 B of source), leaves of 4,096 points, a density split of 1,000,
    ///   and 3 workers: the estimate of the levels 0 to 2 passes 320,000 B.
    ///
    /// Steps:
    /// - Read the tiles, and plan the same snapshot again to read the estimate and the level that was left out.
    ///
    /// Expected:
    /// - A level from 1 on was left out. Every cell above it that is split and holds more than 1,000 points has a
    ///   tile, and no cell at or below it has one, although some hold more than 1,000 points.
    /// - The estimate is, for each tile, 32 B and 65,536 bins at the width of the most points one bin can hold by the
    ///   table: the points of a cell 8 levels below the tile's, or of a leaf that ends above that level.
    /// - The bytes of the tiles are no more than the estimate, and the table and the tiles together are no more than
    ///   10 % of the source.
    /// </remarks>
    [Fact]
    public void Tiles_EstimateAboveTheBudget_StopAtTheLevelThatFits()
    {
        const int Points = 200_000;
        const int Split = 1_000;
        var random = new Random(82);
        var xs = new double[Points];
        var ys = new double[Points];
        for (var row = 0; row < Points; row++)
        {
            (xs[row], ys[row]) = (random.NextDouble() * 100, random.NextDouble() * 1000);
        }

        var tuning = SpatialIndexTuning.Default with { RowsPerUnit = 256, DensitySplit = Split };
        using var built = SpatialBuilt.Of(xs, ys, tuning: tuning);
        var snapshot = built.Snapshot;
        var tiles = snapshot.Tiles!;
        const long Source = Points * 2L * sizeof(double);
        using var plan = new DensityBuilder(snapshot, Split, 256, Source, 0.10);

        var stopped = plan.StoppedLevel;
        Assert.True(stopped >= 1, $"The level left out is {stopped}.");
        var deeper = 0;
        foreach (var row in snapshot.Rows)
        {
            var dense = row.Kind == (byte)SpatialCellKind.Inner && row.Count > Split;
            Assert.Equal(dense && row.Level < stopped, tiles.TryGetTile(row.Code, row.Level, out _));
            deeper += dense && row.Level >= stopped ? 1 : 0;
        }

        Assert.True(deeper > 0, "No cell at or below the level left out holds more points than the split.");
        Assert.Equal(plan.TileCount, tiles.TileCount);

        long estimate = 0;
        var rows = snapshot.Rows.ToArray();
        foreach (var cell in rows)
        {
            if (!tiles.TryGetTile(cell.Code, cell.Level, out _))
            {
                continue;
            }

            // Every row whose cell lies in the tile's cell, at the level of the bins or a leaf above it.
            long most = 0;
            foreach (var row in rows)
            {
                var inside = row.Level >= cell.Level && row.Code >> (2 * (row.Level - cell.Level)) == cell.Code;
                if (inside && (row.Level == cell.Level + 8 || (row.FirstChild < 0 && row.Level < cell.Level + 8)))
                {
                    most = Math.Max(most, row.Count);
                }
            }

            estimate += 32 + (65_536L * DensityTiles.BytesPerBinFor((ulong)most));
        }

        Assert.Equal(estimate, plan.EstimatedBytes);
        Assert.True(tiles.ByteCount <= plan.EstimatedBytes, $"The tiles take {tiles.ByteCount} B; the estimate is {plan.EstimatedBytes} B.");
        var total = tiles.ByteCount + (snapshot.Rows.Length * 32L);
        Assert.True(total <= Source / 10, $"The table and the tiles take {total} B of {Source} B.");
    }

    /// <summary>TryGetTile finds a cell's tile without allocating and throws for a level outside 0 to 32.</summary>
    /// <remarks>
    /// Preconditions:
    /// - The complete index of 5,000 seeded float points with leaves of 16 points and a density split of 64.
    ///
    /// Steps:
    /// - Ask for the tile of every row of the table 20 times, counting the bytes this thread allocates.
    /// - Ask for a cell that is not in the table, and for the levels −1 and 33.
    ///
    /// Expected:
    /// - The calls allocate 0 B.
    /// - A cell that is not in the table has no tile, and the tile it leaves in the out parameter is empty.
    /// - The levels −1 and 33 throw ArgumentOutOfRangeException.
    /// </remarks>
    [Fact]
    public void TryGetTile_OnEveryCell_AllocatesNothingAndChecksTheLevel()
    {
        using var built = Build("uniform", SpatialReference.Small with { DensitySplit = 64, TotalShare = 1e6 });
        var tiles = built.Snapshot.Tiles!;
        var rows = built.Snapshot.Rows.ToArray();
        var found = 0;
        _ = tiles.TryGetTile(0, 0, out _);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var round = 0; round < 20; round++)
        {
            foreach (var row in rows)
            {
                found += tiles.TryGetTile(row.Code, row.Level, out _) ? 1 : 0;
            }
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(20 * tiles.TileCount, found);

        Assert.False(tiles.TryGetTile(ulong.MaxValue, 3, out var none));
        Assert.True(none.Bins.IsEmpty);
        Assert.Throws<ArgumentOutOfRangeException>(() => tiles.TryGetTile(0, -1, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => tiles.TryGetTile(0, 33, out _));
    }

    /// <summary>The tiles are the same bytes whatever the number of workers.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 20,000 nested double points, leaves of 16 points, units of 256 rows, and a density split of 256.
    ///
    /// Steps:
    /// - Build the index with 1, 3, and 8 workers and read the tile of every row of the table.
    ///
    /// Expected:
    /// - The three builds have the same number of tiles, and each tile has the same width and the same bytes.
    /// </remarks>
    [Fact]
    public void Tiles_BuiltByOneThreeAndEightWorkers_AreTheSameBytes()
    {
        var tuning = SpatialReference.Small with { DensitySplit = 256, TotalShare = 1e6 };
        var (xs, ys) = SpatialData.Nested(20_000, seed: 83);
        using var one = SpatialBuilt.Of(xs, ys, workers: 1, tuning: tuning);
        using var three = SpatialBuilt.Of(xs, ys, workers: 3, tuning: tuning);
        using var eight = SpatialBuilt.Of(xs, ys, workers: 8, tuning: tuning);

        Assert.True(one.Snapshot.Tiles!.TileCount > 4);
        foreach (var other in (SpatialBuilt[])[three, eight])
        {
            Assert.Equal(one.Snapshot.Tiles.TileCount, other.Snapshot.Tiles!.TileCount);
            Assert.Equal(one.Snapshot.Tiles.ByteCount, other.Snapshot.Tiles.ByteCount);
            foreach (var row in one.Snapshot.Rows)
            {
                var has = one.Snapshot.Tiles.TryGetTile(row.Code, row.Level, out var tile);
                Assert.Equal(has, other.Snapshot.Tiles.TryGetTile(row.Code, row.Level, out var same));
                Assert.Equal(tile.BytesPerBin, same.BytesPerBin);
                Assert.True(tile.Bins.SequenceEqual(same.Bins), $"The tile of the cell {row.Code} of level {row.Level} differs.");
            }
        }
    }

    /// <summary>
    /// The build makes the tiles in the state Density, after the full snapshot, one level at a time from the deepest,
    /// and gives them to the snapshot once.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 5,000 seeded float points on a pool of 1 thread, leaves of 16 points, and a density split of 64. A probe notes
    ///   the state, the version, the current snapshot, and whether it has tiles at the end of each job.
    ///
    /// Steps:
    /// - Build the index and wait for Completion.
    ///
    /// Expected:
    /// - After PublishFull the jobs are PlanDensity, then for each level with a tile its Density units and one
    ///   EndDensityLevel, then PublishDensity. There are at least 2 levels.
    /// - The state is Density at the end of every job from PublishFull to the last EndDensityLevel and Complete at
    ///   PublishDensity. The version is 2 over the same jobs and 3 at PublishDensity.
    /// - The current snapshot is the full one from PublishFull on. It has no tiles until PublishDensity, where it has
    ///   them, and Completion has ended at the end of no job.
    /// - The number of EndDensityLevel jobs is the number of levels that have a tile in the table.
    /// </remarks>
    [Fact]
    public void Build_WithTiles_PassesTheStateDensityAndPublishesTheTilesOnce()
    {
        using var rig = new SpatialRig(seed: 84);
        rig.Watch();
        var index = rig.Build(tuning: SpatialReference.Small with { DensitySplit = 64, TotalShare = 1e6 });

        var steps = rig.Probe.Steps;
        var sights = rig.Sights;
        var full = Array.FindIndex(steps, step => step.Job == SpatialJob.PublishFull);
        var last = steps.Length - 1;
        Assert.Equal(SpatialJob.PlanDensity, steps[full + 1].Job);
        Assert.Equal(SpatialJob.PublishDensity, steps[last].Job);
        var between = steps[(full + 2)..last].Select(step => step.Job).ToArray();
        Assert.All(between, job => Assert.True(job is SpatialJob.Density or SpatialJob.EndDensityLevel, $"{job} ran among the tiles."));
        Assert.Equal(SpatialJob.Density, between[0]);
        Assert.Equal(SpatialJob.EndDensityLevel, between[^1]);

        for (var i = full; i <= last; i++)
        {
            var (step, sight) = (steps[i], sights[i]);
            var done = i == last;
            if (
                step.State != (done ? SpatialIndexState.Complete : SpatialIndexState.Density)
                || step.Version != (done ? 3 : 2)
                || sight.Snapshot != sights[full].Snapshot
                || sight.HasTiles != done
                || sight.Ended
            )
            {
                Assert.Fail($"Job {i} ({step.Job}) ended as {step} with {sight}.");
            }
        }

        var snapshot = index.Acquire();
        var levels = 0;
        for (var level = 0; level <= 24; level++)
        {
            var any = false;
            foreach (var row in snapshot.Rows)
            {
                any |= row.Level == level && snapshot.Tiles!.TryGetTile(row.Code, row.Level, out _);
            }

            levels += any ? 1 : 0;
        }

        snapshot.Release();
        Assert.True(levels >= 2, $"{levels} level has a tile.");
        Assert.Equal(levels, between.Count(job => job == SpatialJob.EndDensityLevel));
        Assert.True(index.IsComplete);
        Assert.Equal(3, index.Version);
    }

    /// <summary>
    /// A failure and a Dispose among the tiles leave the full snapshot without tiles, and a change of a column there
    /// lets the tiles end and builds again.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 5,000 seeded float points on a pool of 1 thread, leaves of 16 points, and a density split of 64.
    /// - At the end of the first Density unit, on the worker: the listener throws ("fails"), or the test calls Dispose
    ///   on another thread and the worker waits until the state is Disposed ("disposed"), or the test calls Invalidate
    ///   on x ("invalidated").
    ///
    /// Steps:
    /// - Build the index and wait for Completion, or for Dispose.
    ///
    /// Expected:
    /// - "fails": Completion is faulted with the listener's exception, which is the Fault; the state is Failed, the
    ///   version is 2, and Acquire returns the full snapshot, not provisional, without tiles.
    /// - "disposed": Dispose returns, the version is 2, PublishDensity never ran, and the full snapshot that was
    ///   borrowed before has no tiles and still reads its points.
    /// - "invalidated": the first build ends its tiles (the version is 3 at its PublishDensity, where the state is
    ///   Waiting), a second build follows, and the index is complete at the version 5 with tiles.
    /// </remarks>
    [Theory]
    [InlineData("fails")]
    [InlineData("disposed")]
    [InlineData("invalidated")]
    public void Build_InterruptedAmongTheTiles_KeepsTheFullSnapshot(string how)
    {
        using var rig = new SpatialRig(seed: 85);
        var thrown = new InvalidOperationException("The listener failed on purpose.");
        var done = 0;
        SpatialSnapshot? borrowed = null;
        Thread? disposer = null;
        rig.Probe.AfterJob = (job, index) =>
        {
            if (job != SpatialJob.Density || Interlocked.Exchange(ref done, 1) != 0)
            {
                return;
            }

            switch (how)
            {
                case "fails":
                    throw thrown;
                case "disposed":
                    borrowed = index.Acquire();
                    disposer = new Thread(index.Dispose);
                    disposer.Start();
                    SpinWait.SpinUntil(() => index.State == SpatialIndexState.Disposed, 30_000);
                    break;
                default:
                    rig.X.Invalidate();
                    break;
            }
        };
        var index = rig.Start(tuning: SpatialReference.Small with { DensitySplit = 64, TotalShare = 1e6 });

        if (how == "fails")
        {
            Assert.Same(thrown, Eventually.Faults(index.Completion));
            Assert.Same(thrown, index.Fault);
            Assert.Equal(SpatialIndexState.Failed, index.State);
            Assert.Equal(2, index.Version);
            var full = index.Acquire();
            Assert.Equal(SpatialStage.Full, full.Stage);
            Assert.False(full.IsProvisional);
            Assert.Null(full.Tiles);
            rig.Check(full);
            full.Release();
        }
        else if (how == "disposed")
        {
            Assert.IsType<ObjectDisposedException>(Eventually.Faults(index.Completion));
            Assert.True(disposer!.Join(30_000), "Dispose did not return within the watchdog.");
            Assert.Equal(2, index.Version);
            Assert.Equal(0, rig.Probe.CountOf(SpatialJob.PublishDensity));
            Assert.Equal(SpatialStage.Full, borrowed!.Stage);
            Assert.Null(borrowed.Tiles);
            rig.Check(borrowed);
            borrowed.Release();
        }
        else
        {
            Eventually.Completes(index.Completion);
            var ends = rig.Probe.Steps.Where(step => step.Job == SpatialJob.PublishDensity).ToArray();
            Assert.Equal(2, ends.Length);
            Assert.Equal((SpatialIndexState.Waiting, 3L), (ends[0].State, ends[0].Version));
            Assert.Equal((SpatialIndexState.Complete, 5L), (ends[1].State, ends[1].Version));
            Assert.True(index.IsComplete);
            var full = index.Acquire();
            Assert.True(full.Tiles!.TileCount > 0);
            rig.Check(full);
            full.Release();
        }
    }

    private static SpatialBuilt Build(string shape, SpatialIndexTuning tuning)
    {
        switch (shape)
        {
            case "nested":
            case "capped":
                var (nx, ny) = SpatialData.Nested(20_000, seed: 86);
                return SpatialBuilt.Of(nx, ny, tuning: shape == "capped" ? tuning with { TableShare = 0.01 } : tuning);
            case "deep":
                var random = new Random(88);
                var (dx, dy) = (new double[2_000], new double[2_000]);
                for (var row = 0; row < dx.Length; row++)
                {
                    // The second thousand lies in a square 2^-29 of the range wide.
                    var width = row < 1_000 ? 1.0 : 1.0 / (1 << 29);
                    (dx[row], dy[row]) = (0.3 + (random.NextDouble() * width * 0.7), 0.6 + (random.NextDouble() * width * 0.4));
                }

                (dx[0], dy[0]) = (0, 0);
                (dx[1], dy[1]) = (1, 1);
                return SpatialBuilt.Of(dx, dy, tuning: tuning);
            default:
                var (xs, ys) = SpatialData.Uniform(shape == "coincident" ? 8_000 : 5_000, seed: 87);
                for (var row = 5_000; row < xs.Length; row++)
                {
                    (xs[row], ys[row]) = (-20.25f, 640f);
                }

                if (shape == "skipped")
                {
                    for (var row = 0; row < 50; row++)
                    {
                        xs[row * 7] = row % 2 == 0 ? float.NaN : float.PositiveInfinity;
                        ys[(row * 11) + 3] = row % 2 == 0 ? float.NegativeInfinity : float.NaN;
                    }
                }

                return SpatialBuilt.Of(xs, ys, tuning: tuning);
        }
    }

    // The code of every point of the copy, by the formula of the design.
    private static ulong[] Codes(SpatialSnapshot snapshot)
    {
        var (x, y, _) = SpatialReference.ReadAll(snapshot);
        var (xr, yr) = (snapshot.XRange, snapshot.YRange);
        var codes = new ulong[x.Length];
        for (var i = 0; i < codes.Length; i++)
        {
            codes[i] = SpatialReference.Code(SpatialReference.Cell(x[i], xr.Lower, xr.Upper), SpatialReference.Cell(y[i], yr.Lower, yr.Upper));
        }

        return codes;
    }

    // The bins of the tile of a cell: each point of the cell goes to the bin of its cell 8 levels below, whose lower 16
    // bits hold the column in the even bits and the row in the odd ones.
    private static ulong[] Brute(ulong[] codes, ulong cell, int level)
    {
        var bins = new ulong[DensityTiles.BinCount];
        foreach (var code in codes)
        {
            if (SpatialReference.CellAt(code, level) != cell)
            {
                continue;
            }

            var below = SpatialReference.CellAt(code, level + 8);
            var (x, y) = (0, 0);
            for (var bit = 0; bit < 8; bit++)
            {
                x |= (int)((below >> (2 * bit)) & 1) << bit;
                y |= (int)((below >> ((2 * bit) + 1)) & 1) << bit;
            }

            bins[(y * DensityTiles.Side) + x]++;
        }

        return bins;
    }
}
