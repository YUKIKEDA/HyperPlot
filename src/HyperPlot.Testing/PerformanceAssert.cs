using System.Globalization;

namespace HyperPlot.Testing;

/// <summary>
/// Checks that warm code allocates nothing on the calling thread (the Zero-Alloc tests, design §10,
/// <c>.cursor/rules/bench.mdc</c>). It depends on no test framework: an allocation throws
/// <see cref="InvalidOperationException"/>, which any framework reports as a failure.
/// </summary>
internal static class PerformanceAssert
{
    /// <summary>
    /// Runs <paramref name="measured"/> <paramref name="warmUps"/> times, collects garbage, then runs it once more and counts
    /// the bytes that run allocated on the calling thread (<see cref="GC.GetAllocatedBytesForCurrentThread"/>).
    /// </summary>
    /// <remarks>
    /// The warm-up runs the measured code before the count, so that the count does not take in its compilation. The collection
    /// comes after the warm-up, so that an allocation that follows a collection is counted in every run, not only when other
    /// code made garbage. Nothing else runs between the two reads of the counter.
    /// </remarks>
    /// <param name="measured">The code measured. It keeps its results in fields or captured locals, not in a list it grows.</param>
    /// <param name="warmUps">How many times <paramref name="measured"/> runs before the collection, 1 or more.</param>
    /// <param name="scenario">What the run measures, written at the head of the message, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="measured"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="warmUps"/> is less than 1.</exception>
    /// <exception cref="InvalidOperationException">The counted run allocated; the message gives the bytes.</exception>
    public static void DoesNotAllocate(Action measured, int warmUps = 1, string? scenario = null)
    {
        ArgumentNullException.ThrowIfNull(measured);
        ArgumentOutOfRangeException.ThrowIfLessThan(warmUps, 1);
        for (var i = 0; i < warmUps; i++)
        {
            measured();
        }

        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        measured();
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        if (bytes != 0)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{(scenario is null ? string.Empty : scenario + ": ")}The measured run allocated {bytes} bytes after {warmUps} warm-up runs and a collection."
                )
            );
        }
    }
}
