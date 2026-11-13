namespace HyperPlot.Testing.Tests;

/// <summary>The helper of the Zero-Alloc tests (design §10, <c>.cursor/rules/bench.mdc</c>).</summary>
public sealed class PerformanceAssertTests
{
    private static byte[]? _sink;

    /// <summary>
    /// An array made in the counted run throws, and the message gives its bytes.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A measured action that makes a <c>byte[1000]</c> each run and keeps it in a static field.
    ///
    /// Steps:
    /// - Call <c>DoesNotAllocate</c> with one warm-up run.
    ///
    /// Expected:
    /// - <see cref="InvalidOperationException"/>, whose message names the scenario and 1,024 bytes (the array's 1,000 bytes and
    ///   its 24-byte header on a 64-bit runtime).
    /// </remarks>
    [Fact]
    public void DoesNotAllocate_AnArrayInTheCountedRun_ThrowsWithItsBytes()
    {
        var thrown = Assert.Throws<InvalidOperationException>(() =>
            PerformanceAssert.DoesNotAllocate(() => _sink = new byte[1000], scenario: "array")
        );

        Assert.StartsWith("array: ", thrown.Message, StringComparison.Ordinal);
        Assert.Contains(" 1024 bytes ", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The warm-up runs the measured code as many times as asked before the count, so an allocation of its first run only and
    /// the code's own compilation are not counted, and the helper counts nothing of its own.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A measured action that makes an array only when its captured field is empty, and counts its runs. The action has
    ///   not run before.
    ///
    /// Steps:
    /// - Call <c>DoesNotAllocate</c> with 1 and with 3 warm-up runs, each on a new action.
    ///
    /// Expected:
    /// - Neither throws; the actions ran 2 and 4 times.
    /// </remarks>
    [Fact]
    public void DoesNotAllocate_AnAllocationOfTheFirstRunOnly_Passes()
    {
        Assert.Equal(2, Runs(1));
        Assert.Equal(4, Runs(3));

        static int Runs(int warmUps)
        {
            byte[]? cache = null;
            var runs = 0;
            PerformanceAssert.DoesNotAllocate(
                () =>
                {
                    cache ??= new byte[1000];
                    runs++;
                },
                warmUps
            );
            return runs;
        }
    }

    /// <summary>
    /// A full collection comes between the last warm-up run and the counted run.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A measured action that writes <c>GC.CollectionCount(2)</c> into a slot for each run, in an array made before.
    ///
    /// Steps:
    /// - Call <c>DoesNotAllocate</c> with 2 warm-up runs.
    ///
    /// Expected:
    /// - The counted run sees more collections of generation 2 than the last warm-up run.
    /// </remarks>
    [Fact]
    public void DoesNotAllocate_CollectsBeforeTheCount()
    {
        var counts = new int[3];
        var run = 0;
        PerformanceAssert.DoesNotAllocate(() => counts[run++] = GC.CollectionCount(2), warmUps: 2);

        Assert.Equal(3, run);
        Assert.True(counts[2] > counts[1], $"Collections of generation 2 seen by the runs: {string.Join(", ", counts)}.");
    }

    /// <summary>
    /// A null action and a warm-up of fewer than 1 run throw before anything runs.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - An action that counts its runs.
    ///
    /// Steps:
    /// - Call <c>DoesNotAllocate</c> with <see langword="null"/>, and with 0 and −1 warm-up runs.
    ///
    /// Expected:
    /// - <see cref="ArgumentNullException"/> for <c>measured</c>, <see cref="ArgumentOutOfRangeException"/> for <c>warmUps</c>,
    ///   and the action never ran.
    /// </remarks>
    [Fact]
    public void DoesNotAllocate_InvalidArguments_Throw()
    {
        var runs = 0;
        void Count() => runs++;

        Assert.Equal("measured", Assert.Throws<ArgumentNullException>(() => PerformanceAssert.DoesNotAllocate(null!)).ParamName);
        Assert.Equal("warmUps", Assert.Throws<ArgumentOutOfRangeException>(() => PerformanceAssert.DoesNotAllocate(Count, 0)).ParamName);
        Assert.Equal("warmUps", Assert.Throws<ArgumentOutOfRangeException>(() => PerformanceAssert.DoesNotAllocate(Count, -1)).ParamName);
        Assert.Equal(0, runs);
    }
}
