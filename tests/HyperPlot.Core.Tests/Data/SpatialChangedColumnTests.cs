namespace HyperPlot.Tests;

/// <summary>
/// A column whose values change between the passes of a build of a spatial tile index: the table is cut at the end of
/// the copy, a point without a place is dropped, a place without a point takes the point before it, and the snapshot
/// of such a build stays provisional until the column's version moves (design §3.4 "状態と公開", the changed columns;
/// row C3a).
/// </summary>
/// <remarks>
/// Every test builds over the same 1,000 float rows with both ranges passed as 0 to 256, so that the cell of a point at
/// level 8 is the whole part of its x and of its y, and with 256 rows per unit on a pool of 1 thread, so that the rows
/// 0 to 255, 256 to 511, 512 to 767, and 768 to 999 are the four units. The rows 0 to 699 lie one per cell of level 8
/// (x 20 to 194, y 20 to 23). The rows 700 to 999 lie in the cell (200, 200), which the build splits: 300 places of a
/// lattice of 20 by 20 that leaves the quadrant of the high x and the high y empty. That cell is the last in the order
/// of the codes, and its empty quadrant is the last in it. A test changes values at the end of a job of a rebuild, on
/// the worker, so that the next stage reads other values than the stage before it.
/// </remarks>
[Collection("Large data")]
public sealed class SpatialChangedColumnTests
{
    private const int Length = 1_000;
    private const int Sparse = 700;
    private static readonly ScaleRange Range = new(0, 0, 256);

    /// <summary>
    /// Leaves that counted more points than the copy has room for are cut at the end of the copy, every point still
    /// gets its place, and the snapshot is provisional.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The complete index of the 1,000 rows, whose snapshot agrees with the brute force.
    ///
    /// Steps:
    /// - Call Invalidate on x. In the rebuild, at the end of PublishCoarse (after the first count), move the rows 0 to
    ///   49 into the empty quadrant of the cell (200, 200); at the end of Table, move them back.
    /// - Wait for WhenComplete and acquire the snapshot.
    /// - Call Invalidate on x again, wait, and acquire.
    ///
    /// Expected:
    /// - The first count saw 1,000 points and the pass saw 350 in the split cell: the leaves ask for 1,050 places.
    ///   The snapshot holds 1,000 points, each row once, each in the cell of its leaf, and excludes none.
    /// - The quadrant that was empty has rows below level 9 in the table, all of them with no point: they were cut.
    /// - IsComplete is true and the snapshot is provisional. Another Acquire returns the same snapshot and queues
    ///   nothing: the state is Complete, and Allocate ran twice.
    /// - After the second Invalidate the snapshot is not provisional, agrees with the brute force, and the quadrant is
    ///   one row again.
    /// </remarks>
    [Fact]
    public void Build_PassCountsMoreThanTheCopyHolds_CutsTheTableAtTheEnd()
    {
        using var rig = new SpatialRig(Points());
        var index = BuildClean(rig);
        Assert.Equal((0, 0L), LastQuadrant(index));

        var moved = Take(rig, 0, 50);
        var snapshot = Rebuild(
            rig,
            afterCoarse: () =>
            {
                for (var row = 0; row < 50; row++)
                {
                    rig.Xs[row] = 200.5f + (((row % 10) + 0.3f) / 20);
                    rig.Ys[row] = 200.5f + (((row / 10) + 0.3f) / 20);
                }
            },
            afterTable: () => Put(rig, 0, moved)
        );

        Assert.Equal(Length, snapshot.PointCount);
        Assert.Equal(0, snapshot.ExcludedCount);
        var rows = AssertEveryPlaceHoldsAPoint(rig, snapshot, inItsCell: true);
        Assert.Equal(Enumerable.Range(0, Length).Select(row => (long)row), rows.Order());
        var (deeper, points) = LastQuadrant(index);
        Assert.True(deeper > 0, "No leaf of the quadrant that the pass counted points in was made.");
        Assert.Equal(0, points);
        AssertStaysProvisional(rig, snapshot);

        var clean = RebuildClean(rig);
        Assert.Equal((0, 0L), LastQuadrant(index));
        clean.Release();
    }

    /// <summary>
    /// A point that lies, at the writing, in a leaf with no place left for its group or with no point at all is
    /// dropped, the other points keep their places, and the snapshot is provisional.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The rows 990 to 999 have NaN as x. The complete index of the 1,000 rows, whose snapshot agrees with the brute
    ///   force: 990 points and 10 excluded rows.
    ///
    /// Steps:
    /// - Call Invalidate on x. In the rebuild, at the end of Table, give the rows 990 to 994 the place of the row 0 (a
    ///   leaf that holds points of other units only) and the rows 995 to 999 the place (100.5, 100.5) (a leaf without a
    ///   point).
    /// - Wait for WhenComplete and acquire the snapshot.
    /// - Call Invalidate on x again, wait, and acquire.
    ///
    /// Expected:
    /// - The snapshot holds 990 points: the rows 0 to 989, each once, each in the cell of its leaf. It excludes 10 rows,
    ///   which is what the first count saw.
    /// - IsComplete is true and the snapshot is provisional. Another Acquire returns the same snapshot and queues
    ///   nothing.
    /// - After the second Invalidate the snapshot holds the 1,000 points, is not provisional, and agrees with the brute
    ///   force.
    /// </remarks>
    [Fact]
    public void Build_PointsAppearAfterTheTable_DropsThem()
    {
        var points = Points();
        Array.Fill(points.X, float.NaN, 990, 10);
        using var rig = new SpatialRig(points);
        BuildClean(rig);

        var snapshot = Rebuild(
            rig,
            afterTable: () =>
            {
                for (var row = 990; row < Length; row++)
                {
                    rig.Xs[row] = row < 995 ? rig.Xs[0] : 100.5f;
                    rig.Ys[row] = row < 995 ? rig.Ys[0] : 100.5f;
                }
            }
        );

        Assert.Equal(990, snapshot.PointCount);
        Assert.Equal(10, snapshot.ExcludedCount);
        var rows = AssertEveryPlaceHoldsAPoint(rig, snapshot, inItsCell: true);
        Assert.Equal(Enumerable.Range(0, 990).Select(row => (long)row), rows.Order());
        AssertStaysProvisional(rig, snapshot);

        var clean = RebuildClean(rig);
        Assert.Equal(Length, clean.PointCount);
        clean.Release();
    }

    /// <summary>
    /// A place whose point is gone at the writing takes the point before it, the places before the first written point
    /// take that point, and the snapshot is provisional.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The complete index of the 1,000 rows. Its copy starts with the leaf of the rows 0 to 3 and 175 to 178 (the
    ///   first unit), 350 to 353 (the second), and 525 to 528 (the third), in that order; the rows 299 to 302 and 474 to
    ///   477 follow one another in one leaf; and the last place holds a row that the test reads from the copy.
    ///
    /// Steps:
    /// - Call Invalidate on x. In the rebuild, at the end of Table, write NaN over x of the rows 0 to 3 and 175 to 178
    ///   (every point of the first unit in the first leaf), of the row 300, and of the row at the last place.
    /// - Wait for WhenComplete, acquire the snapshot, and read every place of the copy.
    /// - Call Invalidate on x again, wait, and acquire.
    ///
    /// Expected:
    /// - The snapshot still holds 1,000 points and excludes none. Every place holds a row whose values are the
    ///   column's: none of the 10 rows is in the copy, and the 990 others are.
    /// - The places 0 to 8 hold the row 350, the first written point, and the places 9 to 11 the rows 351 to 353.
    /// - Where the rows 299 to 302 and 474 to 477 were, the places hold 299, 301, 302, 474 to 477, and 477 again.
    /// - The last two places hold the same row, which stood at the place before the last.
    /// - IsComplete is true and the snapshot is provisional. Another Acquire returns the same snapshot and queues
    ///   nothing.
    /// - After the second Invalidate the snapshot holds 990 points, excludes 10 rows, is not provisional, and agrees
    ///   with the brute force.
    /// </remarks>
    [Fact]
    public void Build_PointsVanishAfterTheTable_FillsTheirPlaces()
    {
        using var rig = new SpatialRig(Points());
        var index = BuildClean(rig);
        var first = index.Acquire();
        var before = SpatialReference.ReadAll(first).Rows;
        first.Release();
        Assert.Equal((long[])[0, 1, 2, 3, 175, 176, 177, 178, 350, 351, 352, 353, 525, 526, 527, 528], before[..16]);
        var middle = Array.IndexOf(before, 299L);
        Assert.Equal((long[])[299, 300, 301, 302, 474, 475, 476, 477], before[middle..(middle + 8)]);
        var last = before[^1];
        var beforeLast = before[^2];
        long[] gone = [0, 1, 2, 3, 175, 176, 177, 178, 300, last];

        var snapshot = Rebuild(
            rig,
            afterTable: () =>
            {
                foreach (var row in gone)
                {
                    rig.Xs[row] = float.NaN;
                }
            }
        );

        Assert.Equal(Length, snapshot.PointCount);
        Assert.Equal(0, snapshot.ExcludedCount);
        var rows = AssertEveryPlaceHoldsAPoint(rig, snapshot, inItsCell: false);
        Assert.Equal(Enumerable.Range(0, Length).Select(row => (long)row).Except(gone).Order(), rows.Distinct().Order());
        Assert.Equal((long[])[350, 350, 350, 350, 350, 350, 350, 350, 350, 351, 352, 353], rows[..12]);
        Assert.Equal((long[])[299, 301, 302, 474, 475, 476, 477, 477], rows[middle..(middle + 8)]);
        Assert.Equal((long[])[beforeLast, beforeLast], rows[^2..]);
        AssertStaysProvisional(rig, snapshot);

        var clean = RebuildClean(rig);
        Assert.Equal(990, clean.PointCount);
        Assert.Equal(10, clean.ExcludedCount);
        clean.Release();
    }

    /// <summary>
    /// A build that wrote no point at all publishes the table of no points, and that snapshot is provisional too.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - The complete index of the 1,000 rows.
    ///
    /// Steps:
    /// - Call Invalidate on x. In the rebuild, at the end of Table, write NaN over every x.
    /// - Wait for WhenComplete, acquire the snapshot, and ask it for the count and the cells of everything.
    /// - Call Invalidate on x again, wait, and acquire.
    ///
    /// Expected:
    /// - The snapshot is full and holds no point: Count answers 0 inside and 0 touching at every level, and GetCells
    ///   returns no cell.
    /// - IsComplete is true and the snapshot is provisional. Another Acquire returns the same snapshot and queues
    ///   nothing.
    /// - After the second Invalidate the snapshot holds no point, excludes 1,000 rows, is not provisional, and agrees
    ///   with the brute force.
    /// </remarks>
    [Fact]
    public void Build_EveryPointVanishesAfterTheTable_PublishesTheTableOfNoPoints()
    {
        using var rig = new SpatialRig(Points());
        BuildClean(rig);

        var snapshot = Rebuild(rig, afterTable: () => Array.Fill(rig.Xs, float.NaN));

        Assert.Equal(SpatialStage.Full, snapshot.Stage);
        Assert.Equal(0, snapshot.PointCount);
        foreach (var level in (int[])[0, 8, 32])
        {
            Assert.Equal(new SpatialCount(0, 0), snapshot.Count(SpatialReference.Everything, level));
            Assert.Empty(SpatialReference.Cells(snapshot, SpatialReference.Everything, level));
        }

        AssertStaysProvisional(rig, snapshot);

        var clean = RebuildClean(rig);
        Assert.Equal(0, clean.PointCount);
        Assert.Equal(Length, clean.ExcludedCount);
        clean.Release();
    }

    private static (float[] X, float[] Y) Points()
    {
        var xs = new float[Length];
        var ys = new float[Length];
        for (var row = 0; row < Sparse; row++)
        {
            xs[row] = 20.5f + (row % 175);
            ys[row] = 20.5f + (row / 175);
        }

        var next = Sparse;
        for (var v = 0; v < 20; v++)
        {
            for (var u = 0; u < 20; u++)
            {
                if (u >= 10 && v >= 10)
                {
                    continue;
                }

                xs[next] = 200 + ((u + 0.3f) / 20);
                ys[next] = 200 + ((v + 0.3f) / 20);
                next++;
            }
        }

        Assert.Equal(Length, next);
        return (xs, ys);
    }

    private static SpatialIndexInput Input() =>
        new()
        {
            Rows = RowRange.All,
            XRange = Range,
            YRange = Range,
        };

    // The first build, over values that do not change: its snapshot agrees with the brute force.
    private static SpatialIndex BuildClean(SpatialRig rig)
    {
        var index = rig.Build(Input());
        var snapshot = index.Acquire();
        Assert.False(snapshot.IsProvisional);
        rig.Check(snapshot, Range, Range);
        snapshot.Release();
        return index;
    }

    // Builds again, with a change of the values at the end of the first count and at the end of the table, and returns
    // the snapshot of that build. The changes run once, on the worker, between two jobs.
    private static SpatialSnapshot Rebuild(SpatialRig rig, Action? afterCoarse = null, Action? afterTable = null)
    {
        var index = rig.Index;
        var builds = rig.Probe.CountOf(SpatialJob.Allocate);
        var version = index.Version;
        rig.Probe.AfterJob = (job, _) =>
        {
            if (job == SpatialJob.PublishCoarse)
            {
                afterCoarse?.Invoke();
            }
            else if (job == SpatialJob.Table)
            {
                afterTable?.Invoke();
            }
        };
        rig.X.Invalidate();
        Eventually.Completes(index.WhenComplete());
        rig.Probe.AfterJob = null;

        Assert.Equal(builds + 1, rig.Probe.CountOf(SpatialJob.Allocate));
        Assert.Equal(version + 1, index.Version);
        return index.Acquire();
    }

    // The build after the changes ended: the values stand still, so its snapshot is exact.
    private static SpatialSnapshot RebuildClean(SpatialRig rig)
    {
        var snapshot = Rebuild(rig);
        Assert.False(snapshot.IsProvisional);
        rig.Check(snapshot, Range, Range);
        return snapshot;
    }

    // The snapshot of a build that the column changed under: provisional although the index is complete, and no call
    // builds again while the version stands. Gives the snapshot back.
    private static void AssertStaysProvisional(SpatialRig rig, SpatialSnapshot snapshot)
    {
        var index = rig.Index;
        var builds = rig.Probe.CountOf(SpatialJob.Allocate);
        Assert.True(index.IsComplete);
        Assert.True(snapshot.IsProvisional);
        Assert.True(index.WhenComplete().IsCompletedSuccessfully);

        var again = index.Acquire();
        Assert.Same(snapshot, again);
        again.Release();
        Assert.Equal(SpatialIndexState.Complete, index.State);
        Assert.Equal(builds, rig.Probe.CountOf(SpatialJob.Allocate));
        Assert.Equal(builds, rig.Allocator.Requests.Length);
        Assert.Null(index.Fault);
        snapshot.Release();
    }

    // Every place of the copy holds a row of the column with the values the column has now, the leaves follow one
    // another from 0 to the number of points, and the counts of every level agree with that number.
    private static long[] AssertEveryPlaceHoldsAPoint(SpatialRig rig, SpatialSnapshot snapshot, bool inItsCell)
    {
        Assert.Equal(SpatialStage.Full, snapshot.Stage);
        var (x, y, rows) = SpatialReference.ReadAll(snapshot);
        for (var place = 0; place < rows.Length; place++)
        {
            var row = rows[place];
            if (row < 0 || row >= Length || x[place] != rig.Xs[row] || y[place] != rig.Ys[row])
            {
                Assert.Fail($"The place {place} holds the row {row} at ({x[place]}, {y[place]}), which is not a point of the columns.");
            }
        }

        long next = 0;
        foreach (var cell in SpatialReference.Cells(snapshot, SpatialReference.Everything, 32))
        {
            if (cell.First != next || cell.Count <= 0)
            {
                Assert.Fail($"The leaf {cell} does not follow the place {next}.");
            }

            for (var place = cell.First; inItsCell && place < cell.First + cell.Count; place++)
            {
                var code = SpatialReference.Code(SpatialReference.Cell(x[place], 0, 256), SpatialReference.Cell(y[place], 0, 256));
                if (SpatialReference.CellAt(code, cell.Level) != cell.Code)
                {
                    Assert.Fail($"The place {place} holds the row {rows[place]}, which lies outside its leaf {cell}.");
                }
            }

            next += cell.Count;
        }

        Assert.Equal(snapshot.PointCount, next);
        foreach (var level in (int[])[0, 4, 8, 9, 12, 32])
        {
            Assert.Equal(new SpatialCount(snapshot.PointCount, 0), snapshot.Count(SpatialReference.Everything, level));
        }

        return rows;
    }

    // The rows of the table below level 9 inside the quadrant of the high x and the high y of the cell (200, 200), and
    // the points of every row of that quadrant.
    private static (int Deeper, long Points) LastQuadrant(SpatialIndex index)
    {
        var quadrant = (SpatialReference.CellAt(SpatialReference.Code(200u << 24, 200u << 24), 8) << 2) | 3;
        var snapshot = index.Acquire();
        var deeper = 0;
        long points = 0;
        foreach (var row in snapshot.Rows)
        {
            if (row.Level < 9 || row.Code >> (2 * (row.Level - 9)) != quadrant)
            {
                continue;
            }

            deeper += row.Level > 9 ? 1 : 0;
            points += row.Count;
        }

        snapshot.Release();
        return (deeper, points);
    }

    private static (float[] X, float[] Y) Take(SpatialRig rig, int start, int count) =>
        (rig.Xs[start..(start + count)], rig.Ys[start..(start + count)]);

    private static void Put(SpatialRig rig, int start, (float[] X, float[] Y) values)
    {
        values.X.CopyTo(rig.Xs, start);
        values.Y.CopyTo(rig.Ys, start);
    }
}
