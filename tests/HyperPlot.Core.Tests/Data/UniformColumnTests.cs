namespace HyperPlot.Tests;

public sealed class UniformColumnTests
{
    /// <summary>
    /// Element i of a uniform column is start + i * step, rounded once, also past what a float or an int can index.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - start 1e9 (a large origin, like a timestamp) and step 1e-6, over 1e10 elements.
    ///
    /// Steps:
    /// - Read elements 0, 1, 2^31 and the last, and Last.
    ///
    /// Expected:
    /// - Each equals Math.FusedMultiplyAdd(i, step, start); Last equals the last element; ChunkCount covers the length.
    /// </remarks>
    [Fact]
    public void GetValue_LargeOriginAndLength_IsStartPlusIndexTimesStep()
    {
        const long length = 10_000_000_000;
        var column = Column.Uniform(start: 1e9, step: 1e-6, length);

        foreach (var index in new[] { 0L, 1L, 1L << 31, length - 1 })
        {
            Assert.Equal(Math.FusedMultiplyAdd(index, 1e-6, 1e9), column.GetValue(index));
        }

        Assert.Equal(column.GetValue(length - 1), column.Last);
        Assert.Equal((int)((length + Column.ChunkLength - 1) / Column.ChunkLength), column.ChunkCount);
    }

    /// <summary>
    /// A uniform column needs a finite start and a finite step above 0, since a time series' X increases (design §3.3).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - None.
    ///
    /// Steps:
    /// - Make columns with a NaN start, an infinite start, a step of 0, a negative step, a NaN step, and a negative length.
    ///
    /// Expected:
    /// - ArgumentOutOfRangeException for each.
    /// </remarks>
    [Fact]
    public void Uniform_BadArguments_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Column.Uniform(double.NaN, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Column.Uniform(double.PositiveInfinity, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Column.Uniform(0, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Column.Uniform(0, -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Column.Uniform(0, double.NaN, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Column.Uniform(0, 1, -1));
    }

    /// <summary>
    /// Reads outside a uniform column throw.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A uniform column of 3 elements.
    ///
    /// Steps:
    /// - Read elements -1 and 3.
    ///
    /// Expected:
    /// - ArgumentOutOfRangeException for each.
    /// </remarks>
    [Fact]
    public void GetValue_OutOfRange_Throws()
    {
        var column = Column.Uniform(0, 1, 3);

        Assert.Throws<ArgumentOutOfRangeException>(() => column.GetValue(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => column.GetValue(3));
    }
}
