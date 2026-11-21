using HyperPlot.Testing;

namespace HyperPlot.Tests;

/// <summary>The sorts that allocate nothing, for the frame path's short spans and the engine's other sorts (Issues #570 and #585).</summary>
public sealed class SpanSortTests
{
    private static float _sink;

    /// <summary>
    /// <see cref="SpanSort"/> orders floats, doubles, and longs as the runtime's sort with no comparison does, by value:
    /// ascending, NaN first.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 200 seeded arrays of 0 to 40 values, each value one of: a random value in [−100, 100), a value repeated from the
    ///   array, 0, −0, NaN, +∞, −∞. The longs are the finite values times 10^15, and a random long in place of each other.
    ///
    /// Steps:
    /// - Sort a copy with <c>Array.Sort</c> and a copy with <see cref="SpanSort"/>, as floats, as doubles, and as longs.
    ///
    /// Expected:
    /// - The two copies are equal value for value (NaN equal to NaN, −0 equal to 0). Over the arrays, some hold NaN, some an
    ///   infinity, some a repeated value, and some more than 16 values.
    /// </remarks>
    [Fact]
    public void Ascending_OrdersAsTheRuntimesSort()
    {
        var random = new Random(570);
        var (nans, infinities, repeats, long16) = (0, 0, 0, 0);
        for (var round = 0; round < 200; round++)
        {
            var doubles = new double[round % 41];
            for (var i = 0; i < doubles.Length; i++)
            {
                doubles[i] = random.Next(10) switch
                {
                    0 => double.NaN,
                    1 => random.Next(2) == 0 ? double.PositiveInfinity : double.NegativeInfinity,
                    2 => random.Next(2) == 0 ? 0.0 : -0.0,
                    3 when i > 0 => doubles[random.Next(i)],
                    _ => Math.Round((random.NextDouble() * 200) - 100, 3),
                };
            }

            nans += doubles.Any(double.IsNaN) ? 1 : 0;
            infinities += doubles.Any(double.IsInfinity) ? 1 : 0;
            repeats += doubles.Distinct().Count() < doubles.Length ? 1 : 0;
            long16 += doubles.Length > 16 ? 1 : 0;
            var floats = doubles.Select(v => (float)v).ToArray();

            var (expected, actual) = ((double[])doubles.Clone(), (double[])doubles.Clone());
            Array.Sort(expected);
            SpanSort.Ascending(actual);
            Assert.Equal(expected, actual);

            var (expectedFloats, actualFloats) = ((float[])floats.Clone(), (float[])floats.Clone());
            Array.Sort(expectedFloats);
            SpanSort.Ascending(actualFloats);
            Assert.Equal(expectedFloats, actualFloats);

            var longs = doubles.Select(v => double.IsFinite(v) ? (long)(v * 1e15) : random.NextInt64()).ToArray();
            var (expectedLongs, actualLongs) = ((long[])longs.Clone(), (long[])longs.Clone());
            Array.Sort(expectedLongs);
            SpanSort.Ascending(actualLongs);
            Assert.Equal(expectedLongs, actualLongs);
        }

        Assert.True(
            nans > 0 && infinities > 0 && repeats > 0 && long16 > 0,
            $"NaN {nans}, infinities {infinities}, repeats {repeats}, long {long16}."
        );
    }

    /// <summary>
    /// A warm <see cref="SpanSort"/> allocates nothing under the JIT (design §11 invariant 3). What it allocates in a NativeAOT
    /// build, where a sort with no comparison allocated 48 bytes a call, is measured by the End-to-end scene
    /// <c>selection</c> (<c>docs/benchmarks/r3c-hittest.md</c>): the tests run on the JIT.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Spans of 2 floats, of 2 and 4 doubles, of 3 longs, and of 300 floats in descending order, on the stack.
    ///
    /// Steps:
    /// - Sort each, 100 times round, once to warm; count the thread's allocations over a second run.
    ///
    /// Expected:
    /// - 0 bytes; the last sorted span of 300 is ascending.
    /// </remarks>
    [Fact]
    public void Ascending_Warm_AllocatesNothing()
    {
        PerformanceAssert.DoesNotAllocate(static () =>
        {
            Span<float> many = stackalloc float[300];
            for (var k = 0; k < 100; k++)
            {
                Span<float> two = [2, 1];
                Span<double> cuts = [0, 2, 1, 3];
                Span<double> corners = [4, 2, 1, 3];
                Span<long> rows = [3, 1, 2];
                for (var i = 0; i < many.Length; i++)
                {
                    many[i] = many.Length - i;
                }

                SpanSort.Ascending(two);
                SpanSort.Ascending(cuts[1..3]);
                SpanSort.Ascending(corners);
                SpanSort.Ascending(rows);
                SpanSort.Ascending(many);
                _sink = two[0] + (float)cuts[1] + (float)corners[0] + rows[0] + many[0];
            }

            for (var i = 1; i < many.Length; i++)
            {
                if (many[i - 1] > many[i])
                {
                    throw new InvalidOperationException("The span of 300 is not ascending.");
                }
            }
        });

        // The least of each of the five sorted spans is 1.
        Assert.Equal(5, _sink);
    }
}
