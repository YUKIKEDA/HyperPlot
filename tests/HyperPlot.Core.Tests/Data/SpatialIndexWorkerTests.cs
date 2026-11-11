using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace HyperPlot.Tests;

/// <summary>The spatial tile index is the same whatever the number of workers that build it (design §3.4 "グループと単位", row C3a).</summary>
[Collection("Large data")]
public sealed class SpatialIndexWorkerTests
{
    /// <summary>Pools of 1, 3, and 8 threads build the same bytes of the table and the same copy.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 1,052,897 seeded points of double x and y in clusters inside clusters, so that leaves hold points of several
    ///   groups; every 97th row has NaN on one axis. Leaves of 16 points and units of 256 rows, so 4,113 units in 8 groups.
    ///
    /// Steps:
    /// - Build the index of every row with a pool of 1 thread, of 3 threads, and of 8 threads, and read each table and
    ///   every point of each copy.
    ///
    /// Expected:
    /// - The snapshot of 1 thread agrees with the brute force.
    /// - The three tables are the same bytes.
    /// - The three copies hold the same x, y, and row at every place, bit for bit, with the same counts, ranges, and
    ///   coarse density.
    /// </remarks>
    [Fact]
    public void Build_OneThreeAndEightWorkers_MakeTheSameBytes()
    {
        var (xs, ys) = SpatialData.Nested(SpatialData.Length, seed: 31);
        for (var row = 0; row < xs.Length; row += 97)
        {
            (row % 2 == 0 ? xs : ys)[row] = double.NaN;
        }

        using var one = SpatialBuilt.Of(xs, ys, workers: 1);
        SpatialReference.Check(one.Snapshot, SpatialAxisData.Of(xs), SpatialAxisData.Of(ys), new RowRange(0, xs.Length), SpatialReference.Small);
        var table = MemoryMarshal.AsBytes(one.Snapshot.Rows).ToArray();
        var (x1, y1, rows1) = SpatialReference.ReadAll(one.Snapshot);

        foreach (var workers in (int[])[3, 8])
        {
            using var built = SpatialBuilt.Of(xs, ys, workers: workers);

            Assert.True(built.Index.IsComplete);
            Assert.True(MemoryMarshal.AsBytes(built.Snapshot.Rows).SequenceEqual(table), $"The table of {workers} workers differs.");
            var (x, y, rows) = SpatialReference.ReadAll(built.Snapshot);
            Assert.True(rows.AsSpan().SequenceEqual(rows1), $"The rows of the copy of {workers} workers differ.");
            Assert.True(MemoryMarshal.AsBytes<double>(x).SequenceEqual(MemoryMarshal.AsBytes<double>(x1)), $"The x of {workers} workers differs.");
            Assert.True(MemoryMarshal.AsBytes<double>(y).SequenceEqual(MemoryMarshal.AsBytes<double>(y1)), $"The y of {workers} workers differs.");
            Assert.Equal(one.Snapshot.PointCount, built.Snapshot.PointCount);
            Assert.Equal(one.Snapshot.ExcludedCount, built.Snapshot.ExcludedCount);
            Assert.Equal(one.Snapshot.XRange, built.Snapshot.XRange);
            Assert.Equal(one.Snapshot.YRange, built.Snapshot.YRange);
            Assert.True(built.Snapshot.CoarseDensity.SequenceEqual(one.Snapshot.CoarseDensity), $"The coarse density of {workers} workers differs.");
        }
    }

    /// <summary>A user's transform is called from several workers at the same time, a unit at most a call, and the index is that of one worker.</summary>
    /// <remarks>
    /// Preconditions:
    /// - 70,001 seeded points of float x and double y. Both axes have one user's transform, asinh, which notes the
    ///   thread and the length of each call. Its first call does not return until a second thread is inside the
    ///   transform, or 30 seconds have passed.
    /// - Leaves of 16 points and units of 256 rows.
    ///
    /// Steps:
    /// - Build the index with a pool of 8 threads, and again with a pool of 1 thread and a transform that does not wait.
    ///
    /// Expected:
    /// - Two threads were inside the transform at the same time, and no call passed more than 256 values.
    /// - The snapshot of 8 threads agrees with the brute force over the transformed values: x rounded to float, y as double.
    /// - The table and the points of the copy are those of the build with 1 thread, bit for bit.
    /// </remarks>
    [Fact]
    public void Build_UsersTransform_IsCalledFromSeveralWorkersWithTheSameResult()
    {
        var (xs, floats) = SpatialData.Uniform(70_001, seed: 32);
        var ys = Array.ConvertAll(floats, value => (double)value);
        var shared = new Asinh(waitsForSecondThread: true);
        var alone = new Asinh(waitsForSecondThread: false);
        using var built = SpatialBuilt.Of(
            xs,
            ys,
            new SpatialIndexInput
            {
                Rows = RowRange.All,
                XTransform = shared,
                YTransform = shared,
            },
            workers: 8
        );
        using var single = SpatialBuilt.Of(
            xs,
            ys,
            new SpatialIndexInput
            {
                Rows = RowRange.All,
                XTransform = alone,
                YTransform = alone,
            },
            workers: 1
        );

        Assert.True(shared.WasEnteredByTwoThreads, "No second thread entered the transform while the first was inside.");
        Assert.True(shared.Threads.Count >= 2, $"{shared.Threads.Count} thread called the transform.");
        Assert.InRange(shared.LongestCall, 1, 256);
        Assert.Single(alone.Threads);
        Assert.True(built.Index.IsComplete);
        Assert.Null(built.Index.Fault);
        SpatialReference.Check(
            built.Snapshot,
            SpatialAxisData.Of(xs, Math.Asinh),
            SpatialAxisData.Of(ys, Math.Asinh),
            new RowRange(0, xs.Length),
            SpatialReference.Small
        );
        Assert.True(MemoryMarshal.AsBytes(built.Snapshot.Rows).SequenceEqual(MemoryMarshal.AsBytes(single.Snapshot.Rows)), "The tables differ.");
        var (x8, y8, rows8) = SpatialReference.ReadAll(built.Snapshot);
        var (x1, y1, rows1) = SpatialReference.ReadAll(single.Snapshot);
        Assert.True(rows8.AsSpan().SequenceEqual(rows1), "The rows of the copies differ.");
        Assert.True(MemoryMarshal.AsBytes<double>(x8).SequenceEqual(MemoryMarshal.AsBytes<double>(x1)), "The x of the copies differs.");
        Assert.True(MemoryMarshal.AsBytes<double>(y8).SequenceEqual(MemoryMarshal.AsBytes<double>(y1)), "The y of the copies differs.");
    }

    // asinh, which has no state of its own; the notes are for the test.
    private sealed class Asinh(bool waitsForSecondThread) : IScale
    {
        private readonly TaskCompletionSource _second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inside;
        private int _calls;
        private int _longest;

        public ConcurrentDictionary<int, bool> Threads { get; } = new();

        public bool WasEnteredByTwoThreads => _second.Task.IsCompleted;

        public int LongestCall => Volatile.Read(ref _longest);

        public void Forward(ReadOnlySpan<double> values, Span<double> destination)
        {
            Threads[Environment.CurrentManagedThreadId] = true;
            InterlockedMax(ref _longest, values.Length);
            if (Interlocked.Increment(ref _inside) >= 2)
            {
                _second.TrySetResult();
            }

            if (waitsForSecondThread && Interlocked.Increment(ref _calls) == 1)
            {
                _second.Task.Wait(30_000);
            }

            for (var i = 0; i < values.Length; i++)
            {
                destination[i] = Math.Asinh(values[i]);
            }

            Interlocked.Decrement(ref _inside);
        }

        public void Inverse(ReadOnlySpan<double> values, Span<double> destination)
        {
            for (var i = 0; i < values.Length; i++)
            {
                destination[i] = Math.Sinh(values[i]);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            var seen = Volatile.Read(ref target);
            while (value > seen)
            {
                var before = Interlocked.CompareExchange(ref target, value, seen);
                if (before == seen)
                {
                    return;
                }

                seen = before;
            }
        }
    }
}
