using System.Numerics;

namespace HyperPlot.Tests;

public sealed class ChunkStatsTests
{
    /// <summary>
    /// The SIMD statistics equal a plain loop's, for every element type and for lengths around the vector width.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Seeded random values; for float and double, about one in eight is NaN.
    /// - Lengths 1 to 70 (every tail after the vector part, with any vector width up to 512 bits) and 100,003.
    ///
    /// Steps:
    /// - Compute the statistics of a wrapped column's only chunk, and the same with a scalar loop in the test.
    ///
    /// Expected:
    /// - Min, Max, First, Last, Count, NaNCount, and IsNonDecreasing are equal; Sum is equal within a relative 1e-12, since the
    ///   vector adds in another order (relative to the sum of magnitudes).
    /// </remarks>
    [Fact]
    public void GetStats_RandomValues_EqualAScalarLoop()
    {
        var random = new Random(20261028);
        var lengths = Enumerable.Range(1, 70).Append(100_003).ToArray();
        foreach (var length in lengths)
        {
            Check(Make(length, i => (float)((random.NextDouble() * 200) - 100), random, nan: float.NaN));
            Check(Make(length, i => (random.NextDouble() * 2e6) - 1e6, random, nan: double.NaN));
            Check(Make(length, i => random.Next(-1000, 1000), random, nan: null));
            Check(Make(length, i => random.NextInt64(long.MinValue / 4, long.MaxValue / 4), random, nan: null));
            Check(Make(length, i => (byte)random.Next(256), random, nan: null));
        }
    }

    /// <summary>
    /// A chunk of only NaN has no values: Count is 0, Min and Max are default, and it is not non-decreasing.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 37 NaN floats.
    ///
    /// Steps:
    /// - Read the statistics.
    ///
    /// Expected:
    /// - Count 0, NaNCount 37, Min and Max 0, Sum 0, First and Last NaN, IsNonDecreasing false.
    /// </remarks>
    [Fact]
    public void GetStats_AllNaN_HasNoValues()
    {
        using var column = Column.Wrap(Enumerable.Repeat(float.NaN, 37).ToArray());

        var stats = column.GetStats(0);

        Assert.Equal(0, stats.Count);
        Assert.Equal(37, stats.NaNCount);
        Assert.Equal(0, stats.Min);
        Assert.Equal(0, stats.Max);
        Assert.Equal(0, stats.Sum);
        Assert.True(float.IsNaN(stats.First));
        Assert.True(float.IsNaN(stats.Last));
        Assert.False(stats.IsNonDecreasing);
    }

    /// <summary>
    /// One decrease anywhere makes a chunk not non-decreasing: inside a vector, at the seam between two vectors, and in the tail.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - 0, 1, 2, ... over 1,003 doubles, with equal neighbors allowed.
    ///
    /// Steps:
    /// - Check the sorted chunk, then lower one element at positions 1, 8, 16, 63, 64, 999, and 1,002, one at a time.
    ///
    /// Expected:
    /// - The sorted chunk is non-decreasing; each lowered element makes it not.
    /// </remarks>
    [Fact]
    public void IsNonDecreasing_OneDecreaseAnywhere_IsFalse()
    {
        var values = new double[1003];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = i / 2;
        }

        using var column = Column.Wrap(values);
        Assert.True(column.GetStats(0).IsNonDecreasing);
        foreach (var position in new[] { 1, 8, 16, 63, 64, 999, 1002 })
        {
            var kept = values[position];
            values[position] = -1;
            column.Invalidate(position, 1);

            Assert.False(column.GetStats(0).IsNonDecreasing);

            values[position] = kept;
            column.Invalidate(position, 1);
        }

        Assert.True(column.GetStats(0).IsNonDecreasing);
    }

    /// <summary>
    /// The total statistics combine the chunks', and a decrease at the seam between two chunks makes the column not non-decreasing.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - Two chunks and 7 elements of increasing floats, so that each chunk alone is non-decreasing.
    ///
    /// Steps:
    /// - Read the total statistics; then lower the first element of the second chunk below the last of the first.
    ///
    /// Expected:
    /// - Before: Min is the first value, Max the last, Count the length, First and Last the ends, non-decreasing.
    /// - After: both chunks are still non-decreasing on their own, and the column is not.
    /// </remarks>
    [Fact]
    public void GetTotalStats_DecreaseAtTheChunkSeam_IsNotNonDecreasing()
    {
        const int length = (2 * Column.ChunkLength) + 7;
        var values = new float[length];
        for (var i = 0; i < length; i++)
        {
            values[i] = i;
        }

        using var column = Column.Wrap(values);
        var total = column.GetTotalStats();
        Assert.Equal(0, total.Min);
        Assert.Equal(length - 1, total.Max);
        Assert.Equal(0, total.First);
        Assert.Equal(length - 1, total.Last);
        Assert.Equal(length, total.Count);
        Assert.True(column.IsNonDecreasing());

        values[Column.ChunkLength] = -5;
        column.Invalidate(Column.ChunkLength, 1);

        Assert.True(column.GetStats(0).IsNonDecreasing);
        Assert.True(column.GetStats(1).IsNonDecreasing);
        Assert.False(column.IsNonDecreasing());
        Assert.Equal(-5, column.GetTotalStats().Min);
    }

    /// <summary>
    /// Invalidate drops the statistics of the chunks it touches, keeps the others, and raises Version.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A wrapped array of two chunks of zeros, both chunks' statistics read.
    ///
    /// Steps:
    /// - Write 9 into the second chunk without telling the column, and read its statistics.
    /// - Invalidate that element and read again; read Version before and after.
    ///
    /// Expected:
    /// - Before Invalidate the stored Max (0) comes back, as the contract of Wrap says; after it the new Max is 9.
    /// - The first chunk's statistics are unchanged; Version grows by 1.
    /// </remarks>
    [Fact]
    public void Invalidate_TouchedChunk_RecomputesOnlyIt()
    {
        var values = new int[2 * Column.ChunkLength];
        using var column = Column.Wrap(values);
        _ = column.GetStats(0);
        Assert.Equal(0, column.GetStats(1).Max);
        var version = column.Version;

        values[Column.ChunkLength + 3] = 9;
        Assert.Equal(0, column.GetStats(1).Max);

        column.Invalidate(Column.ChunkLength + 3, 1);

        Assert.Equal(9, column.GetStats(1).Max);
        Assert.Equal(0, column.GetStats(0).Max);
        Assert.Equal(version + 1, column.Version);
    }

    /// <summary>
    /// Invalidate checks its range, and an empty range changes nothing.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A column of 10 elements.
    ///
    /// Steps:
    /// - Invalidate (-1, 1), (0, -1), (5, 6), and (long.MaxValue, 1); then (10, 0).
    ///
    /// Expected:
    /// - ArgumentOutOfRangeException for the first four; the empty range leaves Version as it was.
    /// </remarks>
    [Fact]
    public void Invalidate_BadRange_Throws()
    {
        using var column = Column.Wrap(new float[10]);

        Assert.Throws<ArgumentOutOfRangeException>(() => column.Invalidate(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => column.Invalidate(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => column.Invalidate(5, 6));
        Assert.Throws<ArgumentOutOfRangeException>(() => column.Invalidate(long.MaxValue, 1));
        column.Invalidate(10, 0);
        Assert.Equal(0, column.Version);
    }

    private static T[] Make<T>(int length, Func<int, T> next, Random random, T? nan)
        where T : struct
    {
        var values = new T[length];
        for (var i = 0; i < length; i++)
        {
            values[i] = nan is { } missing && random.Next(8) == 0 ? missing : next(i);
        }

        return values;
    }

    private static void Check<T>(T[] values)
        where T : unmanaged, INumber<T>
    {
        using var column = Column.Wrap(values);
        var stats = column.GetStats(0);

        var min = T.Zero;
        var max = T.Zero;
        double sum = 0;
        double magnitude = 0;
        long count = 0;
        long nanCount = 0;
        var nonDecreasing = true;
        for (var i = 0; i < values.Length; i++)
        {
            var x = values[i];
            if (T.IsNaN(x))
            {
                nanCount++;
                nonDecreasing = false;
                continue;
            }

            min = count == 0 || x < min ? x : min;
            max = count == 0 || x > max ? x : max;
            sum += double.CreateTruncating(x);
            magnitude += Math.Abs(double.CreateTruncating(x));
            count++;
            if (i > 0 && !(values[i - 1] <= x))
            {
                nonDecreasing = false;
            }
        }

        var context = $"{typeof(T).Name}[{values.Length}]";
        Assert.True(min == stats.Min, $"{context} Min {stats.Min}, expected {min}");
        Assert.True(max == stats.Max, $"{context} Max {stats.Max}, expected {max}");
        Assert.True(values[0].Equals(stats.First), $"{context} First");
        Assert.True(values[^1].Equals(stats.Last), $"{context} Last");
        Assert.Equal(count, stats.Count);
        Assert.Equal(nanCount, stats.NaNCount);
        Assert.True(nonDecreasing == stats.IsNonDecreasing, $"{context} IsNonDecreasing {stats.IsNonDecreasing}");

        // Rounding errors of a sum in another order are bounded by the sum of magnitudes times n times epsilon; 1e-12 covers n = 1e5.
        Assert.True(Math.Abs(sum - stats.Sum) <= 1e-12 * magnitude, $"{context} Sum {stats.Sum}, expected {sum}");
    }
}
