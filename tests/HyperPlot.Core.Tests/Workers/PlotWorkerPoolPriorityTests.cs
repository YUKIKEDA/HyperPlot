using System.Runtime.InteropServices;

namespace HyperPlot.Tests;

/// <summary>
/// The priority of a worker pool's threads: lower during the computation of background work, normal for everything
/// else, and raised by what waits for a worker (design §8.1 "優先度", requirement N-12, row S4).
/// </summary>
[Collection("Large data")]
public sealed partial class PlotWorkerPoolPriorityTests
{
    /// <summary>A pool takes Lowest, BelowNormal, or Normal, and the engine ships with BelowNormal.</summary>
    /// <remarks>
    /// Preconditions:
    /// - The test assembly set the default priority to Normal before any test, and kept the value it found.
    ///
    /// Steps:
    /// - Read the value the assembly found. Make a pool without a priority, and pools with AboveNormal and Highest.
    ///
    /// Expected:
    /// - The value found is BelowNormal: what a pool made without a priority, the shared one included, has outside the tests.
    /// - The pool made without a priority has the default the assembly set.
    /// - AboveNormal and Highest throw ArgumentOutOfRangeException that names the parameter.
    /// </remarks>
    [Fact]
    public void Constructor_PriorityAboveNormal_ThrowsAndTheShippedDefaultIsBelowNormal()
    {
        Assert.Equal(ThreadPriority.BelowNormal, TestAssemblySetup.ShippedPriority);
        using (var pool = new PlotWorkerPool(1))
        {
            Assert.Equal(PlotWorkerPool.DefaultPriority, pool.Priority);
        }

        foreach (var priority in (ThreadPriority[])[ThreadPriority.AboveNormal, ThreadPriority.Highest])
        {
            var thrown = Assert.Throws<ArgumentOutOfRangeException>(() => new PlotWorkerPool(1, priority));
            Assert.Equal("priority", thrown.ParamName);
        }
    }

    /// <summary>
    /// A worker computes a background unit at the pool's priority and runs the rest of its work at the normal one; on
    /// Linux it keeps the nice value of the pool's priority.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A pool of 1 thread with <paramref name="priority"/>.
    /// - A spatial tile index of 5,000 seeded float points whose transform of x notes the calling thread's priority and
    ///   nice value: the transform is called inside the units of the range stage and of the counting.
    /// - A probe that notes the same at the end of each job, and a pyramid of 3 chunks that notes the same just before
    ///   it writes a block and just after.
    ///
    /// Steps:
    /// - Build the index and the pyramid, and wait for both.
    ///
    /// Expected:
    /// - On Windows: inside the transform and just before a block is written the priority is the pool's; at the end of
    ///   every job of the index, units and single jobs, and just after a block is written, it is Normal.
    /// - On Linux: every note on the worker has the nice value 19 for Lowest, 10 for BelowNormal, and 0 for Normal, and
    ///   the thread of the test has 0.
    /// - The index is complete, and the pyramid is complete.
    /// </remarks>
    [Theory]
    [InlineData(ThreadPriority.Lowest)]
    [InlineData(ThreadPriority.BelowNormal)]
    [InlineData(ThreadPriority.Normal)]
    public void BackgroundUnit_OnAWorker_ComputesAtThePoolsPriority(ThreadPriority priority)
    {
        using var pool = new PlotWorkerPool(1, priority);
        var (xs, ys) = SpatialData.Uniform(5_000, seed: 91);
        using var x = Column.Wrap(xs);
        using var y = Column.Wrap(ys);
        var scale = new Noting();
        var probe = new SpatialProbe();
        var atJobEnds = new List<Note>();
        probe.AfterJob = (_, _) =>
        {
            lock (atJobEnds)
            {
                atJobEnds.Add(Note.Read());
            }
        };
        using var index = SpatialReference.Build(pool, x, y, new SpatialIndexInput { Rows = RowRange.All, XTransform = scale }, probe: probe);

        using var column = TestStorage<float>.MakeColumn(new float[PyramidData.Length], out var storage);
        storage.Hold(0);
        var pyramid = pool.BuildPyramid(column);
        var inBlock = new List<Note>();
        var afterBlock = new List<Note>();
        pyramid.TestInBlock = () => inBlock.Add(Note.Read());
        pyramid.TestBlockWritten = () => afterBlock.Add(Note.Read());
        storage.WaitForWorker(0);
        storage.Release(0);
        Eventually.Completes(pyramid.Completion);
        pyramid.Dispose();

        Assert.True(index.IsComplete);
        Assert.NotEmpty(scale.Notes);
        Assert.NotEmpty(atJobEnds);
        Assert.Equal(3, inBlock.Count);
        Assert.Equal(3, afterBlock.Count);
        if (OperatingSystem.IsWindows())
        {
            Assert.All(scale.Notes, note => Assert.Equal(priority, note.Priority));
            Assert.All(inBlock, note => Assert.Equal(priority, note.Priority));
            Assert.All(atJobEnds, note => Assert.Equal(ThreadPriority.Normal, note.Priority));
            Assert.All(afterBlock, note => Assert.Equal(ThreadPriority.Normal, note.Priority));
        }
        else if (OperatingSystem.IsLinux())
        {
            var nice = priority switch
            {
                ThreadPriority.Lowest => 19,
                ThreadPriority.BelowNormal => 10,
                _ => 0,
            };
            Assert.All(
                (List<Note>[])[scale.Notes, inBlock, atJobEnds, afterBlock],
                notes => Assert.All(notes, note => Assert.Equal(nice, note.Nice))
            );
            Assert.Equal(0, Note.Read().Nice);
        }
    }

    /// <summary>What waits for a worker's unit sets the worker back to the normal priority before it waits.</summary>
    /// <remarks>
    /// Preconditions:
    /// - Windows. A pool of 1 thread with BelowNormal.
    /// - "index" and "pool": a spatial tile index of 5,000 seeded float points whose transform of x, in its first call,
    ///   which is inside a unit, keeps the worker until its priority is Normal, for 5 seconds at the most.
    ///   "pyramid": a pyramid of 3 chunks that does the same just before it writes its first block.
    ///
    /// Steps:
    /// - Wait until the worker is kept. Dispose the index, the pool, or the pyramid, and wait for the call to return.
    ///
    /// Expected:
    /// - The worker was at BelowNormal when it was kept, and its priority became Normal while it was kept: the disposal
    ///   raised it, since the worker itself raises it only after the unit.
    /// - The disposal returned.
    /// </remarks>
    [Theory]
    [InlineData("index")]
    [InlineData("pool")]
    [InlineData("pyramid")]
    public void Dispose_WhileAUnitComputes_RaisesTheWorkerBeforeItWaits(string disposed)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows changes the priority of a worker for a unit.");
        var pool = new PlotWorkerPool(1, ThreadPriority.BelowNormal);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ThreadPriority? before = null;
        bool? raised = null;
        void Keep()
        {
            if (before is not null)
            {
                return;
            }

            before = Thread.CurrentThread.Priority;
            reached.TrySetResult();
            raised = SpinWait.SpinUntil(() => Thread.CurrentThread.Priority == ThreadPriority.Normal, 5_000);
        }

        var (xs, ys) = SpatialData.Uniform(5_000, seed: 92);
        using var x = Column.Wrap(xs);
        using var y = Column.Wrap(ys);
        using var column = TestStorage<float>.MakeColumn(new float[PyramidData.Length], out var storage);
        Action dispose;
        if (disposed == "pyramid")
        {
            storage.Hold(0);
            var pyramid = pool.BuildPyramid(column);
            pyramid.TestInBlock = Keep;
            storage.WaitForWorker(0);
            storage.Release(0);
            dispose = pyramid.Dispose;
        }
        else
        {
            var index = SpatialReference.Start(
                pool,
                x,
                y,
                new SpatialIndexInput
                {
                    Rows = RowRange.All,
                    XTransform = new Noting { OnForward = Keep },
                }
            );
            dispose = disposed == "index" ? index.Dispose : pool.Dispose;
        }

        Eventually.Completes(reached.Task);
        var disposer = new Thread(() => dispose());
        disposer.Start();
        Assert.True(disposer.Join(30_000), "The disposal did not return within the watchdog.");
        pool.Dispose();

        Assert.Equal(ThreadPriority.BelowNormal, before);
        Assert.True(raised, "The worker stayed below the normal priority while the disposal waited for it.");
    }

    /// <summary>A pool at BelowNormal builds the same index, tiles, and pyramid as a pool at Normal.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 20,000 nested double points with leaves of 16 points and a density split of 256, built by a pool of 3 threads
    ///   at Normal and by one at BelowNormal. A float column of 3 chunks with a pyramid from each pool.
    ///
    /// Steps:
    /// - Compare the rows of the two tables, every point of the two copies, and the tile of every row. Ask both
    ///   pyramids for the minimum and the maximum of 4 ranges.
    ///
    /// Expected:
    /// - The tables are the same bytes, the copies hold the same points at the same places, and the tiles have the same
    ///   widths and bytes.
    /// - The pyramids give the same answers, none provisional.
    /// </remarks>
    [Fact]
    public void BackgroundWork_AtBelowNormal_GivesTheSameResultsAsAtNormal()
    {
        var tuning = SpatialReference.Small with { DensitySplit = 256, TotalShare = 1e6 };
        var (xs, ys) = SpatialData.Nested(20_000, seed: 93);
        var values = new float[PyramidData.Length];
        var random = new Random(94);
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = (float)random.NextDouble();
        }

        using var normal = new PlotWorkerPool(3, ThreadPriority.Normal);
        using var low = new PlotWorkerPool(3, ThreadPriority.BelowNormal);
        using var x = Column.Wrap(xs);
        using var y = Column.Wrap(ys);
        var input = new SpatialIndexInput { Rows = RowRange.All };
        using var first = SpatialReference.Build(normal, x, y, input, tuning);
        using var second = SpatialReference.Build(low, x, y, input, tuning);
        var (a, b) = (first.Acquire(), second.Acquire());
        Assert.True(MemoryMarshal.AsBytes(a.Rows).SequenceEqual(MemoryMarshal.AsBytes(b.Rows)), "The tables differ.");
        SpatialReference.AssertSamePoints(SpatialReference.ReadAll(a), SpatialReference.ReadAll(b));
        Assert.True(a.Tiles!.TileCount > 4);
        Assert.Equal(a.Tiles.TileCount, b.Tiles!.TileCount);
        foreach (var row in a.Rows)
        {
            var has = a.Tiles.TryGetTile(row.Code, row.Level, out var tile);
            Assert.Equal(has, b.Tiles.TryGetTile(row.Code, row.Level, out var same));
            Assert.Equal(tile.BytesPerBin, same.BytesPerBin);
            Assert.True(tile.Bins.SequenceEqual(same.Bins), $"The tile of the cell {row.Code} of level {row.Level} differs.");
        }

        a.Release();
        b.Release();

        using var column = Column.Wrap(values);
        using var one = normal.BuildPyramid(column);
        using var two = low.BuildPyramid(column);
        Eventually.Completes(one.Completion);
        Eventually.Completes(two.Completion);
        foreach (var (start, length) in ((long, long)[])[(0, values.Length), (5, 100_000), (1_048_000, 2_000), (2_000_000, 97_000)])
        {
            var expected = one.GetMinMax(start, length);
            Assert.False(expected.IsProvisional);
            Assert.Equal(expected, two.GetMinMax(start, length));
        }
    }

    [LibraryImport("libc", EntryPoint = "getpriority", SetLastError = true)]
    private static partial int GetPriority(int which, uint who);

    // The priority and, on Linux, the nice value of the calling thread.
    private readonly record struct Note(ThreadPriority Priority, int Nice)
    {
        public static Note Read() => new(Thread.CurrentThread.Priority, OperatingSystem.IsLinux() ? GetPriority(0, 0) : 0);
    }

    // A transform that copies its values and notes the thread that calls it.
    private sealed class Noting : IScale
    {
        public List<Note> Notes { get; } = [];

        public Action? OnForward { get; init; }

        public void Forward(ReadOnlySpan<double> values, Span<double> destination)
        {
            values.CopyTo(destination);
            lock (Notes)
            {
                Notes.Add(Note.Read());
            }

            OnForward?.Invoke();
        }

        public void Inverse(ReadOnlySpan<double> values, Span<double> destination) => values.CopyTo(destination);
    }
}
